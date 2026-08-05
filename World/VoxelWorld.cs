using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using RTXSandbox.ModAPI;

namespace RTXSandbox.World;

public sealed class VoxelWorld : IVoxelWorld
{
    // Width/Depth are the legacy GPU clip-window dimensions, not horizontal world bounds.
    public const int Width = 64;
    public const int Depth = 64;
    public const int MinimumY = -64;
    public const int MaximumY = 512;
    public const int Height = MaximumY - MinimumY;
    public const int DefaultGenerationRadiusChunks = 3;
    public const float FluidSurfaceHeight = 0.875f;

    private readonly VoxelChunkStore _storage = new(MinimumY, MaximumY);
    private readonly HashSet<ChunkCoordinate> _generatedChunks = new();
    private readonly ConcurrentQueue<ChunkGenerationRequest> _pendingChunkGeneration = new();
    private readonly ConcurrentQueue<ChunkGenerationResult> _completedChunkGeneration = new();
    private readonly ConcurrentDictionary<ChunkCoordinate, byte> _queuedChunkGeneration = new();
    private readonly SemaphoreSlim _chunkGenerationPermit = new(0, 1);
    private readonly BlockRegistry _blocks;
    private int _chunkGenerationWorkerRunning;
    private VoxelWorld? _chunkGenerationWorkerWorld;
    private int _chunkGenerationWorkerWorldVersion = int.MinValue;
    private int _chunkGenerationVersion;
    private bool _proceduralGenerationEnabled;
    private long _chunkGenerationTotalTicks;
    private long _chunkGenerationMaximumTicks;
    private long _chunkCommitTotalTicks;
    private long _chunkCommitMaximumTicks;
    private int _chunkGenerationCompletedCount;
    private int _chunkCommitCount;
    public PhysicsGridWorld PhysicsGrids { get; } = new();
    public int Seed { get; private set; }
    public WorldGenerationSettings GenerationSettings { get; private set; }

    public event Action<int, int, int, VoxelMaterial>? VoxelChanged;
    public event Action<VoxelChangedEvent>? DetailedVoxelChanged;
    public event Action? WorldReloaded;
    public event Action<int, int, int>? MicroVoxelCellChanged;
    public event Action? MicroVoxelsReloaded;
    // Legacy aggregate notification retained for the GPU packed micro-voxel buffer and Mods.
    public event Action? MicroVoxelsChanged;
    public event Action<ChunkCoordinate>? ChunkGenerated;

    public ushort[] RawData => _storage.CreateLinearSnapshot(Width, Depth);
    public ushort[] CreateRenderWindowSnapshot(int originX, int originZ) =>
        _storage.CreateWindowSnapshot(originX, originZ, Width, Depth);
    internal ushort[] CreateRenderChunkSnapshot(ChunkCoordinate chunk) =>
        _storage.CreateWindowSnapshot(
            chunk.MinimumX, chunk.MinimumZ, ChunkCoordinate.Size, ChunkCoordinate.Size);
    public int LoadedChunkCount => _storage.ChunkCount;
    public int PendingChunkGenerationCount => _queuedChunkGeneration.Count;
    public int CompletedChunkGenerationPendingCommitCount => _completedChunkGeneration.Count;
    public int CompletedChunkGenerationCount => Volatile.Read(ref _chunkGenerationCompletedCount);
    public int CommittedChunkGenerationCount => Volatile.Read(ref _chunkCommitCount);
    public double ChunkGenerationTotalMilliseconds => TicksToMilliseconds(Interlocked.Read(ref _chunkGenerationTotalTicks));
    public double MaximumChunkGenerationMilliseconds => TicksToMilliseconds(Interlocked.Read(ref _chunkGenerationMaximumTicks));
    public double ChunkCommitTotalMilliseconds => TicksToMilliseconds(Interlocked.Read(ref _chunkCommitTotalTicks));
    public double MaximumChunkCommitMilliseconds => TicksToMilliseconds(Interlocked.Read(ref _chunkCommitMaximumTicks));
    public int MicroVoxelContainerCount => _storage.MicroVoxelContainerCount;
    public IReadOnlyList<ChunkCoordinate> SnapshotLoadedChunks() => _storage.SnapshotCoordinates();
    public IReadOnlyCollection<ChunkCoordinate> SnapshotGeneratedChunks() => _generatedChunks.ToArray();
    internal VoxelChunkSnapshot SnapshotChunk(ChunkCoordinate coordinate) => _storage.SnapshotChunk(coordinate);
    internal ChunkMeshingSnapshot CreateChunkMeshingSnapshot(ChunkCoordinate coordinate)
    {
        int minimumY = int.MaxValue;
        int maximumY = int.MinValue;
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        {
            var neighbor = new ChunkCoordinate(coordinate.X + offsetX, coordinate.Z + offsetZ);
            if (!_storage.TryGetVerticalBounds(neighbor, out int neighborMinimum, out int neighborMaximum)) continue;
            minimumY = Math.Min(minimumY, neighborMinimum);
            maximumY = Math.Max(maximumY, neighborMaximum);
        }

        IReadOnlyList<StoredMicroVoxel> microVoxels = _storage.SnapshotChunk(coordinate).MicroVoxels;
        if (minimumY == int.MaxValue)
            return new ChunkMeshingSnapshot(coordinate, 0, 0, Array.Empty<ushort>(), microVoxels);

        minimumY = Math.Max(MinimumY, minimumY - 1);
        maximumY = Math.Min(MaximumY, maximumY + 1);
        const int widthWithBorder = ChunkCoordinate.Size + 2;
        int height = maximumY - minimumY;
        var blocks = new ushort[checked(widthWithBorder * height * widthWithBorder)];
        for (int localZ = 0; localZ < widthWithBorder; localZ++)
        for (int y = minimumY; y < maximumY; y++)
        for (int localX = 0; localX < widthWithBorder; localX++)
        {
            int worldX = coordinate.MinimumX + localX - 1;
            int worldZ = coordinate.MinimumZ + localZ - 1;
            blocks[localX + widthWithBorder * ((y - minimumY) + height * localZ)] =
                _storage.GetBlock(worldX, y, worldZ);
        }
        return new ChunkMeshingSnapshot(coordinate, minimumY, maximumY, blocks, microVoxels);
    }
    internal bool TryGetChunkVerticalBounds(
        ChunkCoordinate coordinate,
        out int minimumY,
        out int maximumYExclusive) =>
        _storage.TryGetVerticalBounds(coordinate, out minimumY, out maximumYExclusive);

    int IVoxelWorld.Width => Width;
    int IVoxelWorld.Height => Height;
    int IVoxelWorld.Depth => Depth;
    int IVoxelWorld.MinimumY => MinimumY;
    int IVoxelWorld.MaximumYExclusive => MaximumY;
    string IVoxelWorld.GenerationPreset => GenerationSettings.Preset.ToString();
    string IVoxelWorld.GenerationBiome => GenerationSettings.Biome.ToString();

    public VoxelWorld(
        bool generate = true,
        BlockRegistry? registry = null,
        int seed = 1337,
        WorldGenerationSettings? generationSettings = null)
    {
        _blocks = registry ?? new BlockRegistry();
        GenerationSettings = generationSettings?.Clone() ?? WorldGenerationSettings.Natural(seed);
        GenerationSettings.Seed = generationSettings?.Seed ?? seed;
        GenerationSettings.Validate();
        Seed = GenerationSettings.Seed;
        _proceduralGenerationEnabled = generate;
        if (generate)
        {
            GenerateWorld();
        }
    }

    public bool IsInside(int x, int y, int z) => y >= MinimumY && y < MaximumY;

    public VoxelMaterial Get(int x, int y, int z)
    {
        return IsInside(x, y, z) ? (VoxelMaterial)_storage.GetBlock(x, y, z) : VoxelMaterial.Air;
    }

    public ushort GetBlockId(int x, int y, int z) => (ushort)Get(x, y, z);
    internal ushort GetLoadedBlockId(int x, int y, int z) =>
        IsInside(x, y, z) ? _storage.GetBlock(x, y, z) : (ushort)0;

    ushort IVoxelWorld.GetBlock(int x, int y, int z) => GetBlockId(x, y, z);

    public bool IsSolid(int x, int y, int z) =>
        Get(x, y, z) != VoxelMaterial.Air ||
        (TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? micro) && !micro!.IsEmpty);

    public bool IsFluid(int x, int y, int z)
    {
        ushort blockId = GetBlockId(x, y, z);
        return blockId != 0 && _blocks.Get(blockId).IsFluid;
    }

    public bool IsFluidMaterial(ushort materialId) =>
        materialId != 0 && _blocks.Get(materialId).IsFluid;
    internal bool IsCollidableMaterial(ushort materialId) =>
        materialId != 0 && _blocks.Get(materialId).IsCollidable;
    internal BlockDefinition[] CreateBlockDefinitionLookupSnapshot() =>
        _blocks.CreateLookupSnapshot();
    internal event Action BlockDefinitionsChanged
    {
        add => _blocks.DefinitionsChanged += value;
        remove => _blocks.DefinitionsChanged -= value;
    }

    public bool IsSolidForCollision(int x, int y, int z)
    {
        if (y < MinimumY)
        {
            return true;
        }

        if (y >= MaximumY)
        {
            return false;
        }

        ushort materialId = GetBlockId(x, y, z);
        if (materialId != 0 && _blocks.Get(materialId).IsCollidable) return true;
        return TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? micro) &&
               micro!.Boxes.Any(box => _blocks.Get(box.MaterialId).IsCollidable);
    }

    public bool Set(int x, int y, int z, VoxelMaterial material)
        => SetBlockId(x, y, z, (ushort)material);

    public bool SetBlockId(int x, int y, int z, ushort blockId)
    {
        if (!IsInside(x, y, z))
        {
            return false;
        }
        ChunkCoordinate chunk = ChunkCoordinate.FromVoxel(x, z);
        if (_proceduralGenerationEnabled) EnsureChunkGenerated(chunk);
        else _generatedChunks.Add(chunk);

        if (!_storage.SetBlock(x, y, z, blockId, out ushort oldBlockId, out bool removedMicro)) return false;

        if (oldBlockId != blockId)
        {
            VoxelChanged?.Invoke(x, y, z, (VoxelMaterial)blockId);
            DetailedVoxelChanged?.Invoke(new VoxelChangedEvent(x, y, z, oldBlockId, blockId));
        }
        if (removedMicro) PublishMicroVoxelCellChanged(x, y, z);
        return true;
    }

    bool IVoxelWorld.SetBlock(int x, int y, int z, ushort blockId) => SetBlockId(x, y, z, blockId);

    public void LoadRawData(ReadOnlySpan<ushort> data)
    {
        ClearPendingChunkGeneration();
        int expectedLength = Width * Height * Depth;
        if (data.Length != expectedLength)
        {
            throw new ArgumentException($"Expected {expectedLength} voxels, received {data.Length}.", nameof(data));
        }
        _storage.LoadLinear(data, Width, Depth);
        _generatedChunks.Clear();
        foreach (ChunkCoordinate chunk in ChunkCoordinate.CoveringArea(Width, Depth))
            _generatedChunks.Add(chunk);
        PhysicsGrids.Clear();
        WorldReloaded?.Invoke();
        MicroVoxelsReloaded?.Invoke();
        MicroVoxelsChanged?.Invoke();
    }

    internal void LoadChunks(IReadOnlyList<VoxelChunkSnapshot> chunks)
    {
        ClearPendingChunkGeneration();
        _storage.LoadChunks(chunks);
        _generatedChunks.Clear();
        foreach (VoxelChunkSnapshot chunk in chunks) _generatedChunks.Add(chunk.Coordinate);
        PhysicsGrids.Clear();
        WorldReloaded?.Invoke();
        MicroVoxelsReloaded?.Invoke();
        MicroVoxelsChanged?.Invoke();
    }

    public void Regenerate(WorldGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ClearPendingChunkGeneration();
        GenerationSettings = settings.Clone();
        GenerationSettings.Validate();
        Seed = GenerationSettings.Seed;
        _proceduralGenerationEnabled = true;
        _storage.Clear();
        _generatedChunks.Clear();
        PhysicsGrids.Clear();
        GenerateWorld();
        WorldReloaded?.Invoke();
        MicroVoxelsReloaded?.Invoke();
        MicroVoxelsChanged?.Invoke();
    }

    public void SetGenerationMetadata(WorldGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ClearPendingChunkGeneration();
        GenerationSettings = settings.Clone();
        GenerationSettings.Validate();
        Seed = GenerationSettings.Seed;
    }

    public int FindSurfaceY(int x, int z)
    {
        ChunkCoordinate chunk = ChunkCoordinate.FromVoxel(x, z);
        if (!TryGetChunkVerticalBounds(chunk, out int minimumY, out int maximumY)) return MinimumY;
        for (int y = maximumY - 1; y >= minimumY; y--)
        {
            ushort materialId = GetBlockId(x, y, z);
            if (materialId != 0 && _blocks.Get(materialId).IsCollidable)
            {
                return y;
            }
        }

        return MinimumY;
    }

    public int FindTopVoxelY(int x, int z)
    {
        ChunkCoordinate chunk = ChunkCoordinate.FromVoxel(x, z);
        if (!TryGetChunkVerticalBounds(chunk, out int minimumY, out int maximumY)) return MinimumY;
        for (int y = maximumY - 1; y >= minimumY; y--)
        {
            if (IsSolid(x, y, z))
            {
                return y;
            }
        }

        return MinimumY;
    }

    public bool TryGetMicroVoxels(int x, int y, int z, out MicroVoxelContainer? container)
    {
        if (!IsInside(x, y, z))
        {
            container = null;
            return false;
        }
        return _storage.TryGetMicroVoxels(x, y, z, out container);
    }

    public MicroVoxelCellSnapshot CaptureCell(int x, int y, int z)
    {
        if (!IsInside(x, y, z)) throw new ArgumentOutOfRangeException(nameof(x));
        MicroVoxelSnapshot? micro = _storage.TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? container)
            ? container!.Snapshot()
            : null;
        return new MicroVoxelCellSnapshot(x, y, z, GetBlockId(x, y, z), micro);
    }

    public bool RestoreCell(MicroVoxelCellSnapshot snapshot)
    {
        if (!IsInside(snapshot.X, snapshot.Y, snapshot.Z)) return false;
        ushort oldBlock = _storage.GetBlock(snapshot.X, snapshot.Y, snapshot.Z);
        _storage.SetBlock(
            snapshot.X, snapshot.Y, snapshot.Z, snapshot.RegularBlockId, out _, out _);
        if (snapshot.MicroVoxels is not null && snapshot.MicroVoxels.Boxes.Count > 0)
        {
            _storage.SetMicroVoxels(
                snapshot.X, snapshot.Y, snapshot.Z, new MicroVoxelContainer(snapshot.MicroVoxels));
        }
        ushort newBlock = _storage.GetBlock(snapshot.X, snapshot.Y, snapshot.Z);
        if (oldBlock != newBlock)
        {
            VoxelChanged?.Invoke(snapshot.X, snapshot.Y, snapshot.Z, (VoxelMaterial)newBlock);
            DetailedVoxelChanged?.Invoke(new VoxelChangedEvent(snapshot.X, snapshot.Y, snapshot.Z, oldBlock, newBlock));
        }
        PublishMicroVoxelCellChanged(snapshot.X, snapshot.Y, snapshot.Z);
        return true;
    }

    public bool SetMicroVoxels(int x, int y, int z, MicroVoxelContainer? container)
    {
        if (!IsInside(x, y, z)) return false;
        ChunkCoordinate chunk = ChunkCoordinate.FromVoxel(x, z);
        if (_proceduralGenerationEnabled) EnsureChunkGenerated(chunk);
        else _generatedChunks.Add(chunk);
        ushort oldBlock = _storage.GetBlock(x, y, z);
        if (container is null || container.IsEmpty)
        {
            bool removed = _storage.RemoveMicroVoxels(x, y, z);
            if (removed) PublishMicroVoxelCellChanged(x, y, z);
            return removed;
        }

        if (container.TryGetFullBlock(out ushort fullMaterial))
        {
            return SetBlockId(x, y, z, fullMaterial);
        }

        _storage.SetMicroVoxels(x, y, z, new MicroVoxelContainer(container.Snapshot()));
        if (oldBlock != 0)
        {
            VoxelChanged?.Invoke(x, y, z, VoxelMaterial.Air);
            DetailedVoxelChanged?.Invoke(new VoxelChangedEvent(x, y, z, oldBlock, 0));
        }
        PublishMicroVoxelCellChanged(x, y, z);
        return true;
    }

    public IReadOnlyList<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)> SnapshotMicroVoxels()
    {
        var snapshots = new List<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)>(MicroVoxelContainerCount);
        foreach ((int x, int y, int z, MicroVoxelContainer container) in _storage.EnumerateMicroVoxels())
            snapshots.Add((x, y, z, container.Snapshot()));
        return snapshots;
    }

    public void LoadMicroVoxels(IEnumerable<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)> snapshots)
    {
        _storage.ClearMicroVoxels();
        foreach ((int x, int y, int z, MicroVoxelSnapshot snapshot) in snapshots)
        {
            if (!IsInside(x, y, z)) throw new InvalidDataException("Micro voxel cell is outside the world.");
            var container = new MicroVoxelContainer(snapshot);
            if (container.IsEmpty) continue;
            _storage.SetMicroVoxels(x, y, z, container);
        }
        MicroVoxelsReloaded?.Invoke();
        MicroVoxelsChanged?.Invoke();
    }

    private void PublishMicroVoxelCellChanged(int x, int y, int z)
    {
        MicroVoxelCellChanged?.Invoke(x, y, z);
        MicroVoxelsChanged?.Invoke();
    }

    public void GetCollisionBoxes(int x, int y, int z, List<VoxelAabb> destination)
    {
        GetStaticCollisionBoxes(x, y, z, destination);
        if (y >= MinimumY && y < MaximumY)
            PhysicsGrids.AppendCollisionAabbs(x, y, z, destination);
    }

    internal void GetStaticCollisionBoxes(int x, int y, int z, List<VoxelAabb> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (y < MinimumY)
        {
            destination.Add(new VoxelAabb(new Vector3(x, y, z), new Vector3(x + 1f, y + 1f, z + 1f), (ushort)VoxelMaterial.Bedrock));
            return;
        }
        if (y >= MaximumY) return;

        ushort regular = GetBlockId(x, y, z);
        if (regular != 0 && _blocks.Get(regular).IsCollidable)
        {
            destination.Add(new VoxelAabb(new Vector3(x, y, z), new Vector3(x + 1f, y + 1f, z + 1f), regular));
        }
        if (TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? micro))
        {
            foreach (MicroVoxelBox box in micro!.Boxes)
            {
                if (_blocks.Get(box.MaterialId).IsCollidable) destination.Add(micro.ToWorldAabb(x, y, z, box));
            }
        }
    }

    public bool IntersectsSolid(Vector3 minimum, Vector3 maximum)
        => IntersectsSolid(minimum, maximum, includeDynamicGrids: true);

    public bool IntersectsStaticSolid(Vector3 minimum, Vector3 maximum)
        => IntersectsSolid(minimum, maximum, includeDynamicGrids: false);

    private bool IntersectsSolid(Vector3 minimum, Vector3 maximum, bool includeDynamicGrids)
    {
        const float epsilon = 0.0001f;
        int minX = (int)MathF.Floor(minimum.X);
        int minY = (int)MathF.Floor(minimum.Y);
        int minZ = (int)MathF.Floor(minimum.Z);
        int maxX = (int)MathF.Floor(maximum.X - epsilon);
        int maxY = (int)MathF.Floor(maximum.Y - epsilon);
        int maxZ = (int)MathF.Floor(maximum.Z - epsilon);

        var boxes = new List<VoxelAabb>(16);
        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            boxes.Clear();
            if (includeDynamicGrids) GetCollisionBoxes(x, y, z, boxes);
            else GetStaticCollisionBoxes(x, y, z, boxes);
            if (boxes.Any(box => box.Intersects(minimum, maximum)))
            {
                return true;
            }
        }

        return false;
    }

    private void GenerateWorld()
    {
        _generatedChunks.Clear();
        foreach (ChunkCoordinate chunk in ChunkCoordinate.CoveringArea(Width, Depth))
            GenerateChunk(chunk, publishEvent: false);

        if (GenerationSettings.Preset == WorldPreset.Void)
        {
            GenerateVoid();
        }
        else if (GenerationSettings.Preset == WorldPreset.Showcase)
        {
            BuildShowcaseFeatures();
        }
    }

    public int EnsureChunksAround(Vector3 worldPosition, int radius = DefaultGenerationRadiusChunks)
    {
        ChunkCoordinate center = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(worldPosition.X), (int)MathF.Floor(worldPosition.Z));
        int generated = 0;
        foreach (ChunkCoordinate chunk in ChunkCoordinate.Around(center, radius)
                     .OrderBy(chunk => Math.Max(Math.Abs(chunk.X - center.X), Math.Abs(chunk.Z - center.Z))))
        {
            if (EnsureChunkGenerated(chunk)) generated++;
        }
        return generated;
    }

    /// <summary>
    /// Schedules missing chunks nearest-first. Generation happens in an isolated world on
    /// a worker thread, so terrain noise and cave construction never stall the game frame.
    /// Completed chunks are published only by <see cref="ProcessPendingChunkGeneration"/>
    /// on the owning thread.
    /// </summary>
    public int QueueChunksAround(Vector3 worldPosition, int radius = DefaultGenerationRadiusChunks)
    {
        ChunkCoordinate center = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(worldPosition.X), (int)MathF.Floor(worldPosition.Z));
        ChunkCoordinate[] wanted = ChunkCoordinate.Around(center, radius)
            .OrderBy(chunk => Math.Max(Math.Abs(chunk.X - center.X), Math.Abs(chunk.Z - center.Z)))
            .ToArray();
        var wantedSet = wanted.ToHashSet();
        // A teleport or fast flight must not leave the single terrain worker spending
        // seconds on chunks around an old player position. Queue entries are cheap to
        // skip; removing them from this live set also makes the pending count truthful.
        foreach (ChunkCoordinate stale in _queuedChunkGeneration.Keys.Where(chunk => !wantedSet.Contains(chunk)))
            _queuedChunkGeneration.TryRemove(stale, out _);

        int queued = 0;
        foreach (ChunkCoordinate chunk in wanted)
        {
            if (_generatedChunks.Contains(chunk) || !_queuedChunkGeneration.TryAdd(chunk, 0)) continue;
            _pendingChunkGeneration.Enqueue(new ChunkGenerationRequest(
                chunk,
                GenerationSettings.Clone(),
                _chunkGenerationVersion));
            queued++;
        }
        StartChunkGenerationWorker();
        return queued;
    }

    /// <summary>
    /// Queues an exact, caller-ordered set without expanding it to the normal streaming
    /// radius. This is used by the repeatable stress benchmark and keeps nearest-first
    /// order supplied by the caller.
    /// </summary>
    public int QueueChunkBurst(IEnumerable<ChunkCoordinate> chunks)
    {
        int queued = 0;
        foreach (ChunkCoordinate chunk in chunks.Distinct())
        {
            if (_generatedChunks.Contains(chunk) || !_queuedChunkGeneration.TryAdd(chunk, 0)) continue;
            _pendingChunkGeneration.Enqueue(new ChunkGenerationRequest(
                chunk,
                GenerationSettings.Clone(),
                _chunkGenerationVersion));
            queued++;
        }
        StartChunkGenerationWorker();
        return queued;
    }

    /// <summary>
    /// Pays the one-time Natural generator JIT/allocation setup while the world is on
    /// its loading screen, rather than on the first unexplored Chunk boundary. The
    /// generated probe is isolated and never enters the live world or save.
    /// </summary>
    public void WarmUpChunkGeneration()
    {
        if (!_proceduralGenerationEnabled || GenerationSettings.Preset == WorldPreset.Void) return;
        var probe = new VoxelWorld(
            generate: false,
            _blocks,
            seed: GenerationSettings.Seed,
            generationSettings: GenerationSettings);
        probe.EnsureChunkGenerated(new ChunkCoordinate(8191, -8191));
    }

    /// <summary>Commits at most <paramref name="maximumChunks"/> completed worker results.</summary>
    public int ProcessPendingChunkGeneration(int maximumChunks = 1)
    {
        maximumChunks = Math.Max(0, maximumChunks);
        int committed = 0;
        bool handledCompletedResult = false;
        while (committed < maximumChunks &&
               _completedChunkGeneration.TryDequeue(out ChunkGenerationResult? result))
        {
            if (result is null) continue;
            handledCompletedResult = true;
            ChunkCoordinate coordinate = result.Coordinate;
            if (result.Version != _chunkGenerationVersion) continue;
            if (!_queuedChunkGeneration.TryRemove(coordinate, out _)) continue;
            if (result.Error is not null)
            {
                Console.Error.WriteLine($"Chunk generation failed for {coordinate}: {result.Error.Message}");
                continue;
            }
            if (result.Storage is not null && _generatedChunks.Add(coordinate))
            {
                long commitStart = Stopwatch.GetTimestamp();
                // The isolated worker no longer owns this Chunk. Move its dense voxel
                // storage directly into the live world instead of enumerating tens of
                // thousands of sparse cells and allocating/copying a second dense array
                // on the update thread.
                _storage.AdoptChunk(result.Storage, coordinate);
                ChunkGenerated?.Invoke(coordinate);
                committed++;
                RecordTiming(
                    Stopwatch.GetTimestamp() - commitStart,
                    ref _chunkCommitTotalTicks,
                    ref _chunkCommitMaximumTicks,
                    ref _chunkCommitCount);
            }
        }
        // Grant exactly one more generation after a completed result was consumed.
        // This preserves the per-frame CPU pacing without creating a new OS Thread for
        // every Chunk in a burst.
        if (handledCompletedResult) SignalChunkGenerationWorker();
        return committed;
    }

    public bool EnsureChunkGenerated(ChunkCoordinate chunk)
    {
        if (_generatedChunks.Contains(chunk)) return false;
        GenerateChunk(chunk, publishEvent: true);
        // A direct edit may synchronously materialize a chunk that was waiting for the
        // worker. Its eventual queued result is now obsolete and must not keep the world
        // in a false "generation pending" state.
        _queuedChunkGeneration.TryRemove(chunk, out _);
        SignalChunkGenerationWorker();
        return true;
    }

    private void GenerateChunk(ChunkCoordinate chunk, bool publishEvent)
    {
        if (!_generatedChunks.Add(chunk)) return;
        switch (GenerationSettings.Preset)
        {
            case WorldPreset.Flat:
            case WorldPreset.Showcase:
                GenerateFlatChunk(chunk);
                break;
            case WorldPreset.Natural:
                GenerateNaturalChunk(chunk, pureNature: false);
                break;
            case WorldPreset.PureNature:
                GenerateNaturalChunk(chunk, pureNature: true);
                break;
            case WorldPreset.Void:
                break;
        }
        if (publishEvent) ChunkGenerated?.Invoke(chunk);
    }

    private void StartChunkGenerationWorker()
    {
        if (_pendingChunkGeneration.IsEmpty) return;
        if (Interlocked.CompareExchange(ref _chunkGenerationWorkerRunning, 1, 0) != 0)
        {
            ReleaseChunkGenerationPermit();
            return;
        }
        BlockRegistry registry = _blocks;
        var worker = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    if (!_chunkGenerationPermit.Wait(millisecondsTimeout: 2_000))
                    {
                        if (_pendingChunkGeneration.IsEmpty) break;
                        continue;
                    }
                    ChunkGenerationRequest? request = null;
                    while (_pendingChunkGeneration.TryDequeue(out ChunkGenerationRequest? candidate))
                    {
                        if (candidate is not null && candidate.Version == _chunkGenerationVersion &&
                            _queuedChunkGeneration.ContainsKey(candidate.Coordinate))
                        {
                            request = candidate;
                            break;
                        }
                    }
                    if (request is null) continue;
                    try
                    {
                        // The worker owns this temporary storage completely. The live
                        // world and renderer/physics events stay on the game thread.
                        if (_chunkGenerationWorkerWorld is null ||
                            _chunkGenerationWorkerWorldVersion != request.Version)
                        {
                            _chunkGenerationWorkerWorld = new VoxelWorld(
                                generate: false,
                                registry,
                                seed: request.Settings.Seed,
                                generationSettings: request.Settings);
                            _chunkGenerationWorkerWorldVersion = request.Version;
                        }
                        VoxelWorld generator = _chunkGenerationWorkerWorld;
                        long generationStart = Stopwatch.GetTimestamp();
                        generator.EnsureChunkGenerated(request.Coordinate);
                        RecordTiming(
                            Stopwatch.GetTimestamp() - generationStart,
                            ref _chunkGenerationTotalTicks,
                            ref _chunkGenerationMaximumTicks,
                            ref _chunkGenerationCompletedCount);
                        VoxelChunkStore.OwnedChunk generatedChunk =
                            generator._storage.ExtractChunk(request.Coordinate);
                        // Vegetation may briefly touch a neighbour. The old isolated
                        // worker discarded those side writes after every request; clear
                        // them here too so reuse changes allocation cost, not terrain.
                        generator._storage.Clear();
                        _completedChunkGeneration.Enqueue(new ChunkGenerationResult(
                            request.Coordinate,
                            generatedChunk,
                            request.Version,
                            null));
                    }
                    catch (Exception error)
                    {
                        _completedChunkGeneration.Enqueue(new ChunkGenerationResult(
                            request.Coordinate, null, request.Version, error));
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _chunkGenerationWorkerRunning, 0);
                // Close the idle-exit race: a request may have arrived immediately
                // before the running flag was cleared.
                if (!_pendingChunkGeneration.IsEmpty) StartChunkGenerationWorker();
            }
        })
        {
            IsBackground = true,
            Name = "RTX Sandbox Chunk Generator",
            Priority = ThreadPriority.BelowNormal,
        };
        worker.Start();
        ReleaseChunkGenerationPermit();
    }

    private void SignalChunkGenerationWorker()
    {
        if (_pendingChunkGeneration.IsEmpty) return;
        if (Volatile.Read(ref _chunkGenerationWorkerRunning) == 0)
            StartChunkGenerationWorker();
        else
            ReleaseChunkGenerationPermit();
    }

    private void ReleaseChunkGenerationPermit()
    {
        if (_chunkGenerationPermit.CurrentCount != 0) return;
        try { _chunkGenerationPermit.Release(); }
        catch (SemaphoreFullException) { }
    }

    public void ResetChunkPerformanceCounters()
    {
        Interlocked.Exchange(ref _chunkGenerationTotalTicks, 0);
        Interlocked.Exchange(ref _chunkGenerationMaximumTicks, 0);
        Interlocked.Exchange(ref _chunkCommitTotalTicks, 0);
        Interlocked.Exchange(ref _chunkCommitMaximumTicks, 0);
        Interlocked.Exchange(ref _chunkGenerationCompletedCount, 0);
        Interlocked.Exchange(ref _chunkCommitCount, 0);
    }

    private static void RecordTiming(long ticks, ref long total, ref long maximum, ref int count)
    {
        Interlocked.Add(ref total, ticks);
        Interlocked.Increment(ref count);
        long observed = Interlocked.Read(ref maximum);
        while (ticks > observed)
        {
            long previous = Interlocked.CompareExchange(ref maximum, ticks, observed);
            if (previous == observed) break;
            observed = previous;
        }
    }

    private static double TicksToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private void ClearPendingChunkGeneration()
    {
        Interlocked.Increment(ref _chunkGenerationVersion);
        while (_pendingChunkGeneration.TryDequeue(out _)) { }
        while (_completedChunkGeneration.TryDequeue(out _)) { }
        _queuedChunkGeneration.Clear();
        // An isolated in-flight request may finish naturally, but its versioned result
        // can no longer be committed into a reloaded/regenerated live world.
    }

    private void GenerateFlatChunk(ChunkCoordinate chunk)
    {
        int surfaceY = Math.Clamp(
            GenerationSettings.Preset == WorldPreset.Showcase ? 4 : GenerationSettings.FlatSurfaceHeight,
            MinimumY + 1, 256);
        ushort surface = ResolveBlock(GenerationSettings.SurfaceBlockKey, VoxelMaterial.Grass);
        ushort underground = ResolveBlock(GenerationSettings.UndergroundBlockKey, VoxelMaterial.Stone);
        for (int z = chunk.MinimumZ; z < chunk.MinimumZ + ChunkCoordinate.Size; z++)
        for (int x = chunk.MinimumX; x < chunk.MinimumX + ChunkCoordinate.Size; x++)
        {
            SetSilently(x, MinimumY, z, VoxelMaterial.Bedrock);
            for (int y = MinimumY + 1; y < surfaceY; y++) SetSilently(x, y, z, underground);
            SetSilently(x, surfaceY, z, surface);
        }
    }

    private void GenerateNaturalChunk(ChunkCoordinate chunk, bool pureNature)
    {
        const int waterLevel = 7;
        ushort grass = ResolveBlock("rtxsandbox:grass", VoxelMaterial.Grass);
        ushort dirt = ResolveBlock("rtxsandbox:dirt", VoxelMaterial.Dirt);
        ushort sand = ResolveBlock("rtxsandbox:sand", VoxelMaterial.Sand);
        ushort snow = ResolveBlock("rtxsandbox:snow", VoxelMaterial.Snow);
        ushort mud = ResolveBlock("rtxsandbox:swamp_mud", VoxelMaterial.Mud);
        ushort stone = ResolveBlock("rtxsandbox:stone", VoxelMaterial.Stone);
        ushort granite = ResolveBlock("rtxsandbox:granite", VoxelMaterial.Stone);
        ushort limestone = ResolveBlock("rtxsandbox:limestone", VoxelMaterial.Stone);
        ushort slate = ResolveBlock("rtxsandbox:slate", VoxelMaterial.Stone);
        ushort volcanic = ResolveBlock("rtxsandbox:volcanic_rock", VoxelMaterial.Stone);

        for (int z = chunk.MinimumZ; z < chunk.MinimumZ + ChunkCoordinate.Size; z++)
        for (int x = chunk.MinimumX; x < chunk.MinimumX + ChunkCoordinate.Size; x++)
        {
            TerrainSample sample = SampleTerrain(x, z, pureNature, waterLevel);
            if (GenerationSettings.Biome != BiomePreset.Auto)
            {
                sample = sample with { Biome = GenerationSettings.Biome };
                sample = sample with
                {
                    Surface = SurfaceForBiome(sample, GenerationSettings.Biome, waterLevel)
                };
            }
            int surface = Math.Clamp(sample.Surface, MinimumY + 4, 256);
            int slope = TerrainSlopeAt(x, z, pureNature, waterLevel, surface);
            ushort top = slope >= 4
                ? sample.Geology < 0.35f ? granite : slate
                : sample.Biome switch
                {
                    BiomePreset.Desert => sand,
                    BiomePreset.Snow => snow,
                    BiomePreset.Mountains when surface >= 18 => snow,
                    BiomePreset.Mountains => granite,
                    BiomePreset.Swamp => mud,
                    _ => grass,
                };
            ushort subsoil = sample.Biome is BiomePreset.Desert ? sand :
                sample.Biome is BiomePreset.Swamp ? mud : dirt;

            SetSilently(x, MinimumY, z, VoxelMaterial.Bedrock);
            for (int y = MinimumY + 1; y <= surface; y++)
            {
                int depth = surface - y;
                ushort material = y == surface ? top : depth <= 3 ? subsoil :
                    GeologicalLayer(x, y, z, surface, sample.Geology,
                        stone, granite, limestone, slate, volcanic);
                SetSilently(x, y, z, material);
            }
            for (int y = MinimumY + 4; y < surface - 3; y++)
            {
                float cave = FractalNoise3D(x * 0.105f, y * 0.13f, z * 0.105f, 1223);
                float tunnel = 1f - MathF.Abs(
                    FractalNoiseSigned(x * 0.038f + y * 0.012f, z * 0.038f - y * 0.014f, 1297));
                if (cave * 0.76f + tunnel * 0.24f > 0.82f)
                    SetSilently(x, y, z, VoxelMaterial.Air);
            }
            if (GenerationSettings.GenerateLakes)
                for (int y = surface + 1; y <= waterLevel; y++) SetSilently(x, y, z, VoxelMaterial.Water);
        }

        if (GenerationSettings.GenerateVegetation) GenerateChunkVegetation(chunk, pureNature, waterLevel);
    }

    private int TerrainSlopeAt(int x, int z, bool pureNature, int waterLevel, int center)
    {
        int maximum = 0;
        ReadOnlySpan<(int X, int Z)> offsets = stackalloc (int, int)[]
            { (-1, 0), (1, 0), (0, -1), (0, 1) };
        foreach ((int offsetX, int offsetZ) in offsets)
        {
            int neighbor = Math.Clamp(
                SampleTerrain(x + offsetX, z + offsetZ, pureNature, waterLevel).Surface,
                MinimumY + 4,
                256);
            maximum = Math.Max(maximum, Math.Abs(center - neighbor));
        }
        return maximum;
    }

    private void GenerateChunkVegetation(ChunkCoordinate chunk, bool pureNature, int waterLevel)
    {
        ushort oak = ResolveBlock("rtxsandbox:oak_log", VoxelMaterial.Wood);
        for (int z = chunk.MinimumZ + 2; z < chunk.MinimumZ + ChunkCoordinate.Size - 2; z++)
        for (int x = chunk.MinimumX + 2; x < chunk.MinimumX + ChunkCoordinate.Size - 2; x++)
        {
            uint hash = HashCoordinates(x, z);
            if (hash % 83u != 0u) continue;
            TerrainSample sample = SampleTerrain(x, z, pureNature, waterLevel);
            if (sample.Biome is BiomePreset.Desert or BiomePreset.Mountains) continue;
            int groundY = sample.Surface;
            if (_storage.GetBlock(x, groundY, z) == 0 || _storage.GetBlock(x, groundY + 1, z) != 0) continue;
            int trunkHeight = 3 + (int)((hash >> 8) % 3u);
            for (int y = groundY + 1; y <= groundY + trunkHeight; y++) SetSilently(x, y, z, oak);
            int crownY = groundY + trunkHeight;
            for (int oz = -2; oz <= 2; oz++)
            for (int oy = -1; oy <= 2; oy++)
            for (int ox = -2; ox <= 2; ox++)
            {
                if (Math.Abs(ox) + Math.Abs(oz) + Math.Max(oy, 0) > 4) continue;
                if (_storage.GetBlock(x + ox, crownY + oy, z + oz) == 0)
                    SetSilently(x + ox, crownY + oy, z + oz, VoxelMaterial.Leaves);
            }
        }
    }

    private void BuildShowcaseFeatures()
    {
        for (int z = 5; z <= 17; z++)
        for (int x = 5; x <= 17; x++)
        {
            SetSilently(x, 4, z, VoxelMaterial.Water);
            if (x is 5 or 17 || z is 5 or 17) SetSilently(x, 5, z, VoxelMaterial.Marble);
        }
        for (int z = 7; z <= 15; z += 4)
        for (int x = 25; x <= 41; x += 4)
        for (int y = 5; y <= 12; y++)
            SetSilently(x, y, z, (x + z) % 8 == 0 ? VoxelMaterial.Crystal : VoxelMaterial.Glass);
        BuildMaterialGallery();
        BuildLightingLab();
    }

    private void GenerateFlat()
    {
        int surfaceY = GenerationSettings.FlatSurfaceHeight;
        ushort surface = ResolveBlock(GenerationSettings.SurfaceBlockKey, VoxelMaterial.Grass);
        ushort underground = ResolveBlock(GenerationSettings.UndergroundBlockKey, VoxelMaterial.Stone);
        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            SetSilently(x, 0, z, VoxelMaterial.Bedrock);
            for (int y = 1; y < surfaceY; y++) SetSilently(x, y, z, underground);
            SetSilently(x, surfaceY, z, surface);
        }

        if (GenerationSettings.GenerateLakes)
        {
            CarveRoundLake(14, 15, 5, surfaceY);
            CarveRoundLake(49, 45, 4, surfaceY);
        }
        if (GenerationSettings.GenerateVegetation)
        {
            BiomePreset[,] biomes = FilledBiomeMap(BiomePreset.Plains);
            GenerateVegetation(biomes);
        }
    }

    private void GenerateNatural(bool pureNature)
    {
        const int waterLevel = 7;
        var biomes = new BiomePreset[Width, Depth];
        var surfaces = new int[Width, Depth];
        var terrain = new TerrainSample[Width, Depth];

        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            TerrainSample sample = SampleTerrain(x, z, pureNature, waterLevel);
            if (GenerationSettings.Biome != BiomePreset.Auto)
            {
                sample = sample with { Biome = GenerationSettings.Biome };
                sample = sample with { Surface = SurfaceForBiome(sample, GenerationSettings.Biome, waterLevel) };
            }
            terrain[x, z] = sample;
            biomes[x, z] = sample.Biome;
            surfaces[x, z] = sample.Surface;
        }

        ushort grass = ResolveBlock("rtxsandbox:grass", VoxelMaterial.Grass);
        ushort dirt = ResolveBlock("rtxsandbox:dirt", VoxelMaterial.Dirt);
        ushort sandySoil = ResolveBlock("rtxsandbox:sandy_soil", VoxelMaterial.Sand);
        ushort blackSoil = ResolveBlock("rtxsandbox:black_soil", VoxelMaterial.Dirt);
        ushort swampMud = ResolveBlock("rtxsandbox:swamp_mud", VoxelMaterial.Mud);
        ushort clay = ResolveBlock("rtxsandbox:clay", VoxelMaterial.Dirt);
        ushort sand = ResolveBlock("rtxsandbox:sand", VoxelMaterial.Sand);
        ushort snow = ResolveBlock("rtxsandbox:snow", VoxelMaterial.Snow);
        ushort stone = ResolveBlock("rtxsandbox:stone", VoxelMaterial.Stone);
        ushort granite = ResolveBlock("rtxsandbox:granite", VoxelMaterial.Stone);
        ushort limestone = ResolveBlock("rtxsandbox:limestone", VoxelMaterial.Stone);
        ushort slate = ResolveBlock("rtxsandbox:slate", VoxelMaterial.Stone);
        ushort volcanic = ResolveBlock("rtxsandbox:volcanic_rock", VoxelMaterial.Stone);

        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            TerrainSample sample = terrain[x, z];
            BiomePreset biome = sample.Biome;
            int surface = surfaces[x, z];
            int slope = TerrainSlope(surfaces, x, z);
            bool riverBank = sample.River > 0.08f && surface <= waterLevel + 2;

            ushort exposedRock = sample.Geology < 0.28f ? granite : sample.Geology > 0.72f ? slate : limestone;
            ushort top = slope >= 4
                ? exposedRock
                : riverBank
                    ? (sample.Moisture > 0.62f ? clay : sandySoil)
                    : biome switch
                    {
                        BiomePreset.Desert => sand,
                        BiomePreset.Snow => snow,
                        BiomePreset.Mountains when surface >= 14 => snow,
                        BiomePreset.Mountains => exposedRock,
                        BiomePreset.Swamp => swampMud,
                        _ => grass,
                    };
            ushort subsoil = biome switch
            {
                BiomePreset.Desert => sandySoil,
                BiomePreset.Swamp => swampMud,
                BiomePreset.Forest or BiomePreset.BambooForest when sample.Moisture > 0.62f => blackSoil,
                _ => dirt,
            };
            if (riverBank) subsoil = sample.Moisture > 0.66f ? clay : sandySoil;

            for (int y = 0; y <= surface; y++)
            {
                int depth = surface - y;
                ushort material = y == 0
                    ? (ushort)VoxelMaterial.Bedrock
                    : y == surface ? top
                    : depth <= 2 + (int)MathF.Round(sample.Erosion * 2f) ? subsoil
                    : GeologicalLayer(x, y, z, surface, sample.Geology, stone, granite, limestone, slate, volcanic);
                SetSilently(x, y, z, material);
            }

            if (surface > 8 && (pureNature || biome is BiomePreset.Mountains or BiomePreset.Snow || sample.Continental > 0.58f))
            {
                for (int y = 3; y < surface - 2; y++)
                {
                    float cave = FractalNoise3D(x * 0.105f, y * 0.13f, z * 0.105f, 1223);
                    float tunnel = 1f - MathF.Abs(FractalNoiseSigned(x * 0.038f + y * 0.012f, z * 0.038f - y * 0.014f, 1297));
                    float threshold = 0.79f + Math.Clamp((waterLevel + 1 - y) * 0.018f, 0f, 0.08f);
                    if (cave * 0.76f + tunnel * 0.24f > threshold) SetSilently(x, y, z, VoxelMaterial.Air);
                }
            }

            if (GenerationSettings.GenerateLakes)
            {
                for (int y = surface + 1; y <= waterLevel; y++) SetSilently(x, y, z, VoxelMaterial.Water);
            }
        }

        if (GenerationSettings.GenerateVegetation) GenerateVegetation(biomes);
    }

    private TerrainSample SampleTerrain(int x, int z, bool pureNature, int waterLevel)
    {
        float warpX = FractalNoiseSigned(x * 0.021f + 17.3f, z * 0.021f - 8.1f, 43) * 9f;
        float warpZ = FractalNoiseSigned(x * 0.021f - 12.7f, z * 0.021f + 21.6f, 71) * 9f;
        float sampleX = x + warpX;
        float sampleZ = z + warpZ;
        float continental = FractalNoise(sampleX * 0.021f, sampleZ * 0.021f, 101);
        float erosion = FractalNoise(sampleX * 0.036f + 13f, sampleZ * 0.036f - 9f, 307);
        float ridge = 1f - MathF.Abs(FractalNoiseSigned(sampleX * 0.031f, sampleZ * 0.031f, 701));
        float detail = FractalNoise(sampleX * 0.115f, sampleZ * 0.115f, 811);
        float moisture = FractalNoise(sampleX * 0.025f, sampleZ * 0.025f, 1709);
        float temperature = FractalNoise(sampleX * 0.022f + 19f, sampleZ * 0.022f - 11f, 2017);
        float mountain = FractalNoise(sampleX * 0.029f - 7f, sampleZ * 0.029f + 23f, 2371);
        float geology = FractalNoise(sampleX * 0.047f + 4f, sampleZ * 0.047f - 16f, 2719);
        float riverField = MathF.Abs(FractalNoiseSigned(sampleX * 0.024f + 8.2f, sampleZ * 0.024f - 3.7f, 911));
        float river = GenerationSettings.GenerateLakes ? 1f - SmoothStep(0.035f, 0.13f, riverField) : 0f;

        BiomePreset biome = DetermineBiome(x, z, pureNature, moisture, temperature, mountain);
        float baseHeight = 7.2f + (continental - 0.5f) * 7f + (detail - 0.5f) * 1.8f;
        float mountainMask = SmoothStep(0.54f, 0.82f, mountain);
        float ridgeHeight = ridge * ridge * (7f + mountainMask * 9f) * (1.15f - erosion * 0.58f);
        float height = baseHeight + ridgeHeight * mountainMask;

        var preliminary = new TerrainSample(
            biome,
            0,
            continental,
            erosion,
            ridge,
            moisture,
            temperature,
            geology,
            river,
            detail);
        int surface = SurfaceForBiome(preliminary, biome, waterLevel, height);
        return preliminary with { Surface = Math.Clamp(surface, MinimumY + 4, 256) };
    }

    private int SurfaceForBiome(TerrainSample sample, BiomePreset biome, int waterLevel, float? sourceHeight = null)
    {
        float height = sourceHeight ?? (7.2f + (sample.Continental - 0.5f) * 7f + (sample.Detail - 0.5f) * 1.8f);
        height = biome switch
        {
            BiomePreset.Mountains => height + sample.Ridge * sample.Ridge * 8f,
            BiomePreset.Desert => height * 0.76f + waterLevel * 0.25f + MathF.Sin((sample.Detail + sample.Geology) * MathF.PI * 5f) * 1.1f,
            BiomePreset.Swamp => Lerp(height, waterLevel - 1f + sample.Detail * 1.5f, 0.72f),
            BiomePreset.Snow => height + sample.Ridge * 2.8f,
            _ => height,
        };
        if (sample.River > 0f)
        {
            height = Lerp(height, waterLevel - 2f + sample.Detail * 0.7f, sample.River);
        }
        return Math.Clamp((int)MathF.Round(height), MinimumY + 4, 256);
    }

    private BiomePreset DetermineBiome(
        int x,
        int z,
        bool pureNature,
        float moisture,
        float temperature,
        float mountain)
    {

        if (pureNature)
        {
            // A broad, noise-warped climate cycle guarantees long-range biome variety
            // without tying the result to the old 64-block prototype boundary.
            float position = x / 2048f +
                             FractalNoiseSigned(x * 0.004f, z * 0.004f, 3907) * 0.10f +
                             (moisture - 0.5f) * 0.06f;
            position -= MathF.Floor(position);
            if (position < 0.16f) return BiomePreset.Desert;
            if (position < 0.34f) return BiomePreset.Plains;
            if (position < 0.56f) return BiomePreset.Forest;
            if (position < 0.72f) return temperature > 0.5f ? BiomePreset.BambooForest : BiomePreset.Swamp;
            if (position < 0.86f) return BiomePreset.Snow;
            return BiomePreset.Mountains;
        }

        if (mountain > 0.72f) return BiomePreset.Mountains;
        if (temperature < 0.27f) return BiomePreset.Snow;
        if (temperature > 0.68f && moisture < 0.42f) return BiomePreset.Desert;
        if (moisture > 0.76f) return BiomePreset.Swamp;
        if (moisture > 0.61f && temperature > 0.48f) return BiomePreset.BambooForest;
        if (moisture > 0.48f) return BiomePreset.Forest;
        return BiomePreset.Plains;
    }

    private int TerrainSlope(int[,] surfaces, int x, int z)
    {
        int center = surfaces[x, z];
        int maximum = 0;
        if (x > 0) maximum = Math.Max(maximum, Math.Abs(center - surfaces[x - 1, z]));
        if (x + 1 < Width) maximum = Math.Max(maximum, Math.Abs(center - surfaces[x + 1, z]));
        if (z > 0) maximum = Math.Max(maximum, Math.Abs(center - surfaces[x, z - 1]));
        if (z + 1 < Depth) maximum = Math.Max(maximum, Math.Abs(center - surfaces[x, z + 1]));
        return maximum;
    }

    private ushort GeologicalLayer(
        int x,
        int y,
        int z,
        int surface,
        float geology,
        ushort stone,
        ushort granite,
        ushort limestone,
        ushort slate,
        ushort volcanic)
    {
        int depth = surface - y;
        float pocket = FractalNoise3D(x * 0.075f, y * 0.085f, z * 0.075f, 2897);
        if (y <= 3 && pocket > 0.58f) return volcanic;
        if (depth > 8 && geology < 0.26f + (pocket - 0.5f) * 0.18f) return granite;
        if (geology > 0.74f - (pocket - 0.5f) * 0.16f) return slate;
        if (pocket > 0.64f && depth is > 3 and < 10) return limestone;
        return stone;
    }

    private void GenerateVegetation(BiomePreset[,] biomes)
    {
        ushort oak = ResolveBlock("rtxsandbox:oak_log", VoxelMaterial.Wood);
        ushort spruce = ResolveBlock("rtxsandbox:spruce_log", VoxelMaterial.Wood);
        ushort maple = ResolveBlock("rtxsandbox:maple_log", VoxelMaterial.Wood);
        ushort darkwood = ResolveBlock("rtxsandbox:darkwood_log", VoxelMaterial.Wood);
        for (int z = 3; z < Depth - 3; z++)
        for (int x = 3; x < Width - 3; x++)
        {
            int groundY = FindSurfaceY(x, z);
            if (groundY <= 0 || groundY >= Height - 7 || IsFluid(x, groundY, z) || IsFluid(x, groundY + 1, z)) continue;
            BiomePreset biome = biomes[x, z];
            uint hash = HashCoordinates(x, z);
            float grove = FractalNoise(x * 0.082f + 4f, z * 0.082f - 7f, 3413);
            uint baseFrequency = biome switch
            {
                BiomePreset.Forest => 15u,
                BiomePreset.BambooForest => 9u,
                BiomePreset.Swamp => 27u,
                BiomePreset.Snow => 61u,
                BiomePreset.Plains => 97u,
                _ => uint.MaxValue,
            };
            uint frequency = baseFrequency == uint.MaxValue
                ? uint.MaxValue
                : (uint)Math.Clamp((int)MathF.Round(baseFrequency * Lerp(1.5f, 0.62f, grove)), 7, 180);

            if (biome == BiomePreset.BambooForest && hash % frequency == 0)
            {
                int height = 4 + (int)((hash >> 8) % 5u);
                for (int y = groundY + 1; y <= Math.Min(groundY + height, Height - 1); y++)
                    SetSilently(x, y, z, VoxelMaterial.Bamboo);
            }
            else if (frequency != uint.MaxValue && hash % frequency == 0)
            {
                ushort trunk = biome switch
                {
                    BiomePreset.Snow => spruce,
                    BiomePreset.Swamp => darkwood,
                    BiomePreset.Forest when ((hash >> 10) & 3u) == 0u => maple,
                    BiomePreset.Forest when ((hash >> 10) & 3u) == 1u => spruce,
                    _ => oak,
                };
                bool conifer = biome == BiomePreset.Snow || trunk == spruce;
                BuildTree(x, groundY, z, 3 + (int)((hash >> 8) % (conifer ? 4u : 3u)), trunk, conifer);
            }
            else if (biome is BiomePreset.Plains or BiomePreset.Forest && hash % 43u == 0u)
            {
                SetSilently(x, groundY + 1, z, VoxelMaterial.Flower);
            }
        }
    }

    private void BuildTree(int x, int groundY, int z, int trunkHeight, ushort trunk, bool conifer)
    {
        for (int y = groundY + 1; y <= groundY + trunkHeight; y++) SetSilently(x, y, z, trunk);
        int crownY = groundY + trunkHeight;
        if (conifer)
        {
            for (int oy = -2; oy <= 2; oy++)
            {
                int radius = oy >= 2 ? 0 : oy == 1 ? 1 : 2;
                if (oy == -1) radius = 1;
                for (int oz = -radius; oz <= radius; oz++)
                for (int ox = -radius; ox <= radius; ox++)
                {
                    if (Math.Abs(ox) + Math.Abs(oz) > radius + 1) continue;
                    int leafX = x + ox;
                    int leafY = crownY + oy;
                    int leafZ = z + oz;
                    if (Get(leafX, leafY, leafZ) == VoxelMaterial.Air) SetSilently(leafX, leafY, leafZ, VoxelMaterial.Leaves);
                }
            }
            return;
        }

        for (int oz = -2; oz <= 2; oz++)
        for (int oy = -1; oy <= 2; oy++)
        for (int ox = -2; ox <= 2; ox++)
        {
            int distance = Math.Abs(ox) + Math.Abs(oz) + Math.Max(oy, 0);
            if (distance > 4 || (ox == 0 && oz == 0 && oy <= 0)) continue;
            if (distance >= 3 && ((HashCoordinates(x + ox, z + oz) ^ (uint)(oy * 265443576)) & 7u) == 0u) continue;
            int leafX = x + ox;
            int leafY = crownY + oy;
            int leafZ = z + oz;
            if (Get(leafX, leafY, leafZ) == VoxelMaterial.Air) SetSilently(leafX, leafY, leafZ, VoxelMaterial.Leaves);
        }
    }

    private void GenerateVoid()
    {
        int centerX = Width / 2;
        int centerZ = Depth / 2;
        for (int z = centerZ - 4; z <= centerZ + 4; z++)
        for (int x = centerX - 4; x <= centerX + 4; x++)
            SetSilently(x, 4, z, VoxelMaterial.WhiteConcrete);
        SetSilently(centerX, 3, centerZ, VoxelMaterial.Glow);
    }

    private void GenerateShowcase()
    {
        WorldGenerationSettings original = GenerationSettings;
        GenerationSettings = new WorldGenerationSettings
        {
            Preset = WorldPreset.Showcase,
            Seed = original.Seed,
            FlatSurfaceHeight = 4,
            SurfaceBlockKey = "rtxsandbox:white_concrete",
            UndergroundBlockKey = "rtxsandbox:stone",
            GenerateLakes = false,
            GenerateVegetation = false,
        };
        GenerateFlat();
        GenerationSettings = original;

        for (int z = 5; z <= 17; z++)
        for (int x = 5; x <= 17; x++)
        {
            SetSilently(x, 4, z, VoxelMaterial.Water);
            if (x is 5 or 17 || z is 5 or 17) SetSilently(x, 5, z, VoxelMaterial.Marble);
        }
        for (int z = 7; z <= 15; z += 4)
        for (int x = 25; x <= 41; x += 4)
        for (int y = 5; y <= 12; y++)
            SetSilently(x, y, z, (x + z) % 8 == 0 ? VoxelMaterial.Crystal : VoxelMaterial.Glass);

        BuildMaterialGallery();
        BuildLightingLab();
    }

    private void BuildLightingLab()
    {
        for (int z = 43; z <= 58; z++)
        for (int x = 6; x <= 22; x++)
        {
            SetSilently(x, 4, z, VoxelMaterial.BlackConcrete);
            if (x is 6 or 22 || z is 43 or 58)
                for (int y = 5; y <= 11; y++) SetSilently(x, y, z, VoxelMaterial.BlackConcrete);
        }
        SetSilently(10, 7, 49, VoxelMaterial.RedLamp);
        SetSilently(18, 7, 49, VoxelMaterial.BlueLamp);
        SetSilently(14, 7, 55, VoxelMaterial.PurpleLamp);
    }

    private void CarveRoundLake(int centerX, int centerZ, int radius, int surfaceY)
    {
        for (int z = centerZ - radius; z <= centerZ + radius; z++)
        for (int x = centerX - radius; x <= centerX + radius; x++)
        {
            int dx = x - centerX;
            int dz = z - centerZ;
            if (dx * dx + dz * dz > radius * radius) continue;
            SetSilently(x, surfaceY, z, VoxelMaterial.Water);
            if (dx * dx + dz * dz < (radius - 2) * (radius - 2))
            {
                SetSilently(x, surfaceY - 1, z, VoxelMaterial.Water);
            }
        }
    }

    private BiomePreset[,] FilledBiomeMap(BiomePreset biome)
    {
        var result = new BiomePreset[Width, Depth];
        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++) result[x, z] = biome;
        return result;
    }

    private ushort ResolveBlock(string key, VoxelMaterial fallback) =>
        _blocks.TryGet(key, out BlockDefinition? definition) && definition!.Id != 0
            ? definition.Id
            : (ushort)fallback;

    private float FractalNoise(float x, float z, int salt)
    {
        float sum = 0f;
        float amplitude = 0.57f;
        float total = 0f;
        for (int octave = 0; octave < 4; octave++)
        {
            sum += ValueNoise(x, z, salt + octave * 977) * amplitude;
            total += amplitude;
            x *= 2.03f;
            z *= 2.03f;
            amplitude *= 0.5f;
        }
        return sum / total;
    }

    private float FractalNoiseSigned(float x, float z, int salt) => FractalNoise(x, z, salt) * 2f - 1f;

    private float FractalNoise3D(float x, float y, float z, int salt)
    {
        float sum = 0f;
        float amplitude = 0.58f;
        float total = 0f;
        for (int octave = 0; octave < 3; octave++)
        {
            sum += ValueNoise3D(x, y, z, salt + octave * 977) * amplitude;
            total += amplitude;
            x *= 2.01f;
            y *= 2.01f;
            z *= 2.01f;
            amplitude *= 0.5f;
        }
        return sum / total;
    }

    private float ValueNoise(float x, float z, int salt)
    {
        int x0 = (int)MathF.Floor(x);
        int z0 = (int)MathF.Floor(z);
        float tx = Smooth(x - x0);
        float tz = Smooth(z - z0);
        float a = Hash01(x0, z0, salt);
        float b = Hash01(x0 + 1, z0, salt);
        float c = Hash01(x0, z0 + 1, salt);
        float d = Hash01(x0 + 1, z0 + 1, salt);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
    }

    private float ValueNoise3D(float x, float y, float z, int salt)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int z0 = (int)MathF.Floor(z);
        float tx = Smooth(x - x0);
        float ty = Smooth(y - y0);
        float tz = Smooth(z - z0);
        float c000 = Hash013(x0, y0, z0, salt);
        float c100 = Hash013(x0 + 1, y0, z0, salt);
        float c010 = Hash013(x0, y0 + 1, z0, salt);
        float c110 = Hash013(x0 + 1, y0 + 1, z0, salt);
        float c001 = Hash013(x0, y0, z0 + 1, salt);
        float c101 = Hash013(x0 + 1, y0, z0 + 1, salt);
        float c011 = Hash013(x0, y0 + 1, z0 + 1, salt);
        float c111 = Hash013(x0 + 1, y0 + 1, z0 + 1, salt);
        float lower = Lerp(Lerp(c000, c100, tx), Lerp(c010, c110, tx), ty);
        float upper = Lerp(Lerp(c001, c101, tx), Lerp(c011, c111, tx), ty);
        return Lerp(lower, upper, tz);
    }

    private float Hash01(int x, int z, int salt)
    {
        uint value = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(Seed * 83492791) ^ (uint)salt;
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        return (value & 0x00ffffffu) / 16777215f;
    }

    private float Hash013(int x, int y, int z, int salt)
    {
        uint value = unchecked((uint)(x * 73856093)) ^
                     unchecked((uint)(y * 83492791)) ^
                     unchecked((uint)(z * 19349663)) ^
                     unchecked((uint)(Seed * 265443576)) ^
                     unchecked((uint)salt);
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;
        return (value & 0x00ffffffu) / 16777215f;
    }

    private static float Smooth(float value) => value * value * (3f - 2f * value);
    private static float SmoothStep(float edge0, float edge1, float value) =>
        Smooth(Math.Clamp((value - edge0) / Math.Max(edge1 - edge0, 0.00001f), 0f, 1f));
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void BuildMaterialGallery()
    {
        IReadOnlyList<BlockDefinition> materials = _blocks.PlaceableDefinitions();
        const int columns = 16;
        for (int i = 0; i < materials.Count; i++)
        {
            int x = 25 + i % columns;
            int z = 20 + i / columns;
            if (z >= 42) break;
            BlockDefinition material = materials[i];
            int height = material.Transmission > 0f || material.Metallic > 0.8f || material.Emission.LengthSquared() > 0.1f
                ? 2
                : 1;
            for (int y = 5; y < 5 + height; y++) SetSilently(x, y, z, material.Id);
        }
    }

    private void BuildRuin()
    {
        int centerX = 38;
        int centerZ = 27;
        int groundY = FindSurfaceY(centerX, centerZ) + 1;

        for (int x = centerX - 4; x <= centerX + 4; x++)
        {
            SetSilently(x, groundY, centerZ, VoxelMaterial.Brick);
        }

        for (int y = groundY + 1; y <= groundY + 4; y++)
        {
            SetSilently(centerX - 4, y, centerZ, VoxelMaterial.Brick);
            SetSilently(centerX + 4, y, centerZ, VoxelMaterial.Brick);
        }

        for (int x = centerX - 4; x <= centerX + 4; x++)
        {
            SetSilently(x, groundY + 5, centerZ, VoxelMaterial.Marble);
        }

        SetSilently(centerX, groundY + 1, centerZ, VoxelMaterial.Glow);
    }

    private void SetSilently(int x, int y, int z, VoxelMaterial material)
        => SetSilently(x, y, z, (ushort)material);

    private void SetSilently(int x, int y, int z, ushort blockId)
    {
        if (IsInside(x, y, z))
        {
            _storage.SetBlock(x, y, z, blockId, out _, out _);
        }
    }

    private uint HashCoordinates(int x, int z)
    {
        uint value = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)Seed;
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        return value;
    }

    private readonly record struct TerrainSample(
        BiomePreset Biome,
        int Surface,
        float Continental,
        float Erosion,
        float Ridge,
        float Moisture,
        float Temperature,
        float Geology,
        float River,
        float Detail);

    private sealed record ChunkGenerationRequest(
        ChunkCoordinate Coordinate,
        WorldGenerationSettings Settings,
        int Version);

    private sealed record ChunkGenerationResult(
        ChunkCoordinate Coordinate,
        VoxelChunkStore.OwnedChunk? Storage,
        int Version,
        Exception? Error);
}
