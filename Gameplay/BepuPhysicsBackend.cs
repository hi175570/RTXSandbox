using System.Numerics;
using System.Diagnostics;
using System.Buffers;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using RTXSandbox.ModAPI;
using RTXSandbox.Infrastructure;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

/// <summary>BEPUphysics 2 compound-body backend for detached voxel grids.</summary>
public sealed class BepuPhysicsBackend : IDisposable
{
    private readonly VoxelWorld _world;
    private readonly BufferPool _pool = new();
    private readonly ThreadDispatcher _dispatcher;
    private readonly Simulation _simulation;
    private readonly Dictionary<ulong, DynamicBody> _dynamicBodies = new();
    private readonly Dictionary<ChunkCoordinate, List<StaticBody>> _staticChunks = new();
    private readonly HashSet<ChunkCoordinate> _dirtyChunks = new();
    private readonly HashSet<ChunkCoordinate> _urgentDirtyChunks = new();
    private readonly HashSet<ChunkCoordinate> _pendingStaticEvictions = new();
    private readonly HashSet<ChunkCoordinate> _partiallyEvictedStaticChunks = new();
    private readonly Dictionary<ChunkCoordinate, int> _colliderVersions = new();
    private readonly Dictionary<ChunkCoordinate, long> _colliderLastDirtyTimestamps = new();
    private readonly Dictionary<ChunkCoordinate, int> _colliderDirtyCounts = new();
    private ColliderBuildJob? _colliderBuildJob;
    private BlockDefinition[] _blockDefinitions;
    private int _colliderEpoch;
    private Vector3 _simulationFocus;
    private int _colliderResidencySignature = int.MinValue;
    private const int PlayerColliderRadiusChunks = 3;
    private const double ColliderEditCoalesceMilliseconds = 25.0;
    private const double RepeatedColliderEditCoalesceMilliseconds = 75.0;
    private const int MaximumStaticBodiesRemovedPerFrame = 8;
    private const float FixedStepSeconds = 1f / 120f;
    private const int MaximumCatchUpSteps = 2;
    private float _timeAccumulator;
    private bool _disposed;

    public string Name => "BEPUphysics 2";
    public int DynamicBodyCount => _dynamicBodies.Count;
    public int StaticChunkBodyCount => _staticChunks.Values.Sum(list => list.Count);
    public int CollisionStepsLastFrame { get; private set; }
    public int PendingColliderBuildCount => _dirtyChunks.Count;
    public int ColliderBuildCount { get; private set; }
    public double ColliderBuildTotalMilliseconds { get; private set; }
    public double MaximumColliderBuildMilliseconds { get; private set; }
    public double MaximumDynamicReconcileMilliseconds { get; private set; }
    public double MaximumSimulationMilliseconds { get; private set; }
    public double MaximumSingleSimulationStepMilliseconds { get; private set; }
    public double MaximumStaticResidencyMilliseconds { get; private set; }

    public BepuPhysicsBackend(VoxelWorld world, Vector3 simulationFocus = default)
    {
        _world = world;
        _simulationFocus = simulationFocus;
        _blockDefinitions = world.CreateBlockDefinitionLookupSnapshot();
        int threads = Math.Max(1, Environment.ProcessorCount > 4 ? Environment.ProcessorCount - 2 : Environment.ProcessorCount - 1);
        _dispatcher = new ThreadDispatcher(threads);
        _simulation = Simulation.Create(
            _pool,
            // Contacts recover overlap without restitution. Keeping the recovery speed
            // below visible launch velocity prevents a landed Grid from repeatedly
            // throwing itself back into the air, while moderate friction still lets an
            // off-balance structure continue rotating on the floor.
            new NarrowPhaseCallbacks(new SpringSettings(20f, 1f), 0.35f, 0.55f),
            new PoseIntegratorCallbacks(new Vector3(0f, -18f, 0f), 0.035f, 0.08f),
            new SolveDescription(8, 2));
        _world.DetailedVoxelChanged += OnVoxelChanged;
        _world.MicroVoxelCellChanged += OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded += MarkAllStaticDirty;
        _world.WorldReloaded += OnWorldReloaded;
        _world.ChunkGenerated += OnChunkGenerated;
        _world.BlockDefinitionsChanged += OnBlockDefinitionsChanged;
        MarkAllStaticDirty();
    }

    public void Step(float deltaSeconds)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BepuPhysicsBackend));
        long residencyStarted = Stopwatch.GetTimestamp();
        RefreshStaticColliderResidency();
        EvictStaleStaticBodies();
        MaximumStaticResidencyMilliseconds = Math.Max(
            MaximumStaticResidencyMilliseconds, ElapsedMilliseconds(residencyStarted));
        RebuildDirtyChunks(FrameWorkBudget.Default.MaximumColliderCommitsPerFrame);
        long dynamicStarted = Stopwatch.GetTimestamp();
        ReconcileDynamicBodies();
        MaximumDynamicReconcileMilliseconds = Math.Max(
            MaximumDynamicReconcileMilliseconds, ElapsedMilliseconds(dynamicStarted));
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.25f);
        _timeAccumulator = MathF.Min(
            _timeAccumulator + deltaSeconds,
            FixedStepSeconds * MaximumCatchUpSteps);
        CollisionStepsLastFrame = 0;
        long simulationStarted = Stopwatch.GetTimestamp();
        while (_timeAccumulator + 0.0000001f >= FixedStepSeconds &&
               CollisionStepsLastFrame < MaximumCatchUpSteps)
        {
            long singleStepStarted = Stopwatch.GetTimestamp();
            // Dispatching a tiny active-body set across every logical core costs more
            // than the collision work itself (especially while another game owns CPU
            // time). Keep the parallel path for genuinely large simulations.
            if (_dynamicBodies.Count >= 64) _simulation.Timestep(FixedStepSeconds, _dispatcher);
            else _simulation.Timestep(FixedStepSeconds);
            MaximumSingleSimulationStepMilliseconds = Math.Max(
                MaximumSingleSimulationStepMilliseconds, ElapsedMilliseconds(singleStepStarted));
            _timeAccumulator -= FixedStepSeconds;
            CollisionStepsLastFrame++;
        }
        MaximumSimulationMilliseconds = Math.Max(
            MaximumSimulationMilliseconds, ElapsedMilliseconds(simulationStarted));
        SynchronizeGrids();
    }

    public void SetSimulationFocus(Vector3 position) => _simulationFocus = position;

    private void ReconcileDynamicBodies()
    {
        HashSet<ulong> live = _world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        foreach (ulong id in _dynamicBodies.Keys.Where(id => !live.Contains(id)).ToArray()) RemoveDynamic(id);
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            int signature = grid.TopologyVersion;
            if (_dynamicBodies.TryGetValue(grid.Id, out DynamicBody existing) && existing.Signature == signature) continue;
            if (_dynamicBodies.ContainsKey(grid.Id)) RemoveDynamic(grid.Id);
            CreateDynamic(grid, signature);
        }
    }

    private void CreateDynamic(PhysicsGrid grid, int signature)
    {
        // Interior cells contribute mass/inertia through PhysicsGrid, but they can
        // never participate in contacts. Building BEPU children from the cached shell
        // preserves the exact exterior while avoiding compound work for enclosed voxels.
        if (grid.CollisionElements.Count == 1)
        {
            PhysicsGridCollisionElement element = grid.CollisionElements[0];
            Vector3 size = Vector3.Max(element.Maximum - element.Minimum, new Vector3(0.01f));
            var box = new Box(size.X, size.Y, size.Z);
            TypedIndex boxShape = _simulation.Shapes.Add(box);
            Vector3 centerOffset = element.Center - grid.CenterOfMassLocal;
            Vector3 boxBodyPosition = grid.Position + Vector3.Transform(centerOffset, grid.Orientation);
            BodyDescription boxDescription = BodyDescription.CreateDynamic(
                new RigidPose(boxBodyPosition, grid.Orientation), box.ComputeInertia(element.Mass),
                new CollidableDescription(boxShape, 0.1f, CollisionDetectionFor(grid)),
                new BodyActivityDescription(0.02f));
            boxDescription.Velocity = new BodyVelocity(grid.LinearVelocity, grid.AngularVelocity);
            BodyHandle boxHandle = _simulation.Bodies.Add(boxDescription);
            if (grid.IsSleeping)
            {
                BodyReference sleepingBody = _simulation.Bodies[boxHandle];
                sleepingBody.Awake = false;
            }
            _dynamicBodies[grid.Id] = new DynamicBody(boxHandle, boxShape, centerOffset, signature);
            return;
        }

        using var builder = new CompoundBuilder(_pool, _simulation.Shapes, grid.CollisionElements.Count);
        foreach (PhysicsGridCollisionElement element in grid.CollisionElements)
        {
            Vector3 size = Vector3.Max(element.Maximum - element.Minimum, new Vector3(0.01f));
            builder.Add(new Box(size.X, size.Y, size.Z),
                new RigidPose(element.Center - grid.CenterOfMassLocal, Quaternion.Identity), element.Mass);
        }
        builder.BuildDynamicCompound(out Buffer<CompoundChild> children, out BodyInertia inertia, out Vector3 center);
        TypedIndex shape = _simulation.Shapes.Add(new Compound(children));
        Vector3 bodyPosition = grid.Position + Vector3.Transform(center, grid.Orientation);
        BodyDescription description = BodyDescription.CreateDynamic(
            new RigidPose(bodyPosition, grid.Orientation), inertia,
            new CollidableDescription(shape, 0.1f, CollisionDetectionFor(grid)),
            new BodyActivityDescription(0.02f));
        description.Velocity = new BodyVelocity(grid.LinearVelocity, grid.AngularVelocity);
        BodyHandle handle = _simulation.Bodies.Add(description);
        if (grid.IsSleeping)
        {
            BodyReference sleepingBody = _simulation.Bodies[handle];
            sleepingBody.Awake = false;
        }
        _dynamicBodies[grid.Id] = new DynamicBody(handle, shape, center, signature);
    }

    private static ContinuousDetection CollisionDetectionFor(PhysicsGrid grid)
    {
        float predictedMotion = (grid.LinearVelocity.Length() +
                                 grid.AngularVelocity.Length() * grid.BoundingRadius) * FixedStepSeconds;
        // At 120 Hz, ordinary falling/rotating grids are fully covered by BEPU's
        // velocity-expanded speculative contacts. Reserve costly sweep tests for a
        // body that could move through roughly half a voxel in one fixed step.
        return predictedMotion >= 0.45f
            ? ContinuousDetection.Continuous(0.001f, 0.001f)
            : ContinuousDetection.Passive;
    }

    private void SynchronizeGrids()
    {
        bool changed = false;
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (!_dynamicBodies.TryGetValue(grid.Id, out DynamicBody entry)) continue;
            BodyReference body = _simulation.Bodies[entry.Handle];
            Quaternion orientation = body.Pose.Orientation;
            Vector3 gridPosition = body.Pose.Position - Vector3.Transform(entry.CenterOffset, orientation);
            changed |= grid.SetPhysicsState(
                gridPosition, orientation, body.Velocity.Linear, body.Velocity.Angular, !body.Awake);
        }
        if (changed) _world.PhysicsGrids.NotifyChanged();
    }

    private void RebuildDirtyChunks(int maximumChunks)
    {
        int remaining = Math.Max(0, maximumChunks);
        if (_colliderBuildJob is { Build.IsCompleted: true } completed && remaining > 0)
        {
            _colliderBuildJob = null;
            ColliderBuildData? data = null;
            try
            {
                data = completed.Build.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Chunk collider build failed for {completed.Chunk}: {error.Message}");
            }

            if (data is not null && completed.Epoch == _colliderEpoch &&
                IsColliderRequired(completed.Chunk) &&
                _colliderVersions.TryGetValue(completed.Chunk, out int currentVersion) &&
                currentVersion == completed.Version)
            {
                long commitStarted = Stopwatch.GetTimestamp();
                _urgentDirtyChunks.Remove(completed.Chunk);
                _dirtyChunks.Remove(completed.Chunk);
                _colliderLastDirtyTimestamps.Remove(completed.Chunk);
                _colliderDirtyCounts.Remove(completed.Chunk);
                _partiallyEvictedStaticChunks.Remove(completed.Chunk);
                RemoveStaticChunk(completed.Chunk);
                CreateStaticChunk(completed.Chunk, data.Boxes);
                double elapsed = data.BuildMilliseconds +
                                 (Stopwatch.GetTimestamp() - commitStarted) * 1000.0 / Stopwatch.Frequency;
                ColliderBuildCount++;
                ColliderBuildTotalMilliseconds += elapsed;
                MaximumColliderBuildMilliseconds = Math.Max(MaximumColliderBuildMilliseconds, elapsed);
                remaining--;
            }
        }

        if (_colliderBuildJob is not null || _dirtyChunks.Count == 0) return;
        ChunkCoordinate? eligible = _urgentDirtyChunks
            .Where(candidate => _dirtyChunks.Contains(candidate) && IsColliderEditQuiet(candidate))
            .Cast<ChunkCoordinate?>()
            .FirstOrDefault();
        eligible ??= _dirtyChunks.Where(IsColliderEditQuiet).Cast<ChunkCoordinate?>().FirstOrDefault();
        if (!eligible.HasValue) return;
        ChunkCoordinate chunk = eligible.Value;
        int version = _colliderVersions[chunk];
        int epoch = _colliderEpoch;
        ChunkMeshingSnapshot snapshot = _world.CreateChunkMeshingSnapshot(chunk);
        BlockDefinition[] definitions = _blockDefinitions;
        _colliderBuildJob = new ColliderBuildJob(
            chunk,
            version,
            epoch,
            Task.Run(() => BuildStaticChunk(snapshot, definitions)));
    }

    public void ResetPerformanceCounters()
    {
        ColliderBuildCount = 0;
        ColliderBuildTotalMilliseconds = 0;
        MaximumColliderBuildMilliseconds = 0;
        MaximumDynamicReconcileMilliseconds = 0;
        MaximumSimulationMilliseconds = 0;
        MaximumSingleSimulationStepMilliseconds = 0;
        MaximumStaticResidencyMilliseconds = 0;
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static ColliderBuildData BuildStaticChunk(
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        long started = Stopwatch.GetTimestamp();
        var boxes = new List<VoxelAabb>(ChunkCoordinate.Size * ChunkCoordinate.Size);
        var runsByHeight = new Dictionary<(int StartY, int EndY), byte[]>();
        ChunkCoordinate chunk = snapshot.Coordinate;
        try
        {
            // First collapse every vertical solid run, then greedily merge neighboring
            // columns with identical lower/upper faces. The occupied volume remains
            // exact while flat terrain commonly becomes one Box instead of 256.
            for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
            for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
            {
                int x = chunk.MinimumX + localX;
                int z = chunk.MinimumZ + localZ;
                int runStart = int.MinValue;
                int previousY = int.MinValue;
                for (int y = snapshot.MinimumY; y < snapshot.MaximumYExclusive; y++)
                {
                    ushort material = snapshot.GetBlock(x, y, z);
                    if (material == 0 || !definitions[material].IsCollidable)
                    {
                        if (runStart != int.MinValue)
                            AddColumnRun(localX, localZ, runStart, previousY + 1);
                        runStart = int.MinValue;
                        previousY = int.MinValue;
                        continue;
                    }
                    if (runStart == int.MinValue) runStart = y;
                    previousY = y;
                }
                if (runStart != int.MinValue)
                    AddColumnRun(localX, localZ, runStart, previousY + 1);
            }

            foreach (((int startY, int endY), byte[] mask) in runsByHeight)
            {
                for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
                for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
                {
                    int index = localX + ChunkCoordinate.Size * localZ;
                    if (mask[index] == 0) continue;
                    int width = 1;
                    while (localX + width < ChunkCoordinate.Size && mask[index + width] != 0) width++;
                    int depth = 1;
                    while (localZ + depth < ChunkCoordinate.Size)
                    {
                        bool completeRow = true;
                        int row = index + depth * ChunkCoordinate.Size;
                        for (int offsetX = 0; offsetX < width; offsetX++)
                            if (mask[row + offsetX] == 0) { completeRow = false; break; }
                        if (!completeRow) break;
                        depth++;
                    }
                    for (int offsetZ = 0; offsetZ < depth; offsetZ++)
                        Array.Clear(mask, index + offsetZ * ChunkCoordinate.Size, width);
                    boxes.Add(new VoxelAabb(
                        new Vector3(chunk.MinimumX + localX, startY, chunk.MinimumZ + localZ),
                        new Vector3(chunk.MinimumX + localX + width, endY, chunk.MinimumZ + localZ + depth),
                        0));
                }
            }

            foreach (StoredMicroVoxel micro in snapshot.MicroVoxels)
            {
                float inverse = 1f / micro.Snapshot.Resolution;
                foreach (MicroVoxelBox box in micro.Snapshot.Boxes)
                {
                    if (box.MaterialId == 0 || !definitions[box.MaterialId].IsCollidable) continue;
                    boxes.Add(new VoxelAabb(
                        new Vector3(
                            chunk.MinimumX + micro.LocalX + box.MinX * inverse,
                            micro.Y + box.MinY * inverse,
                            chunk.MinimumZ + micro.LocalZ + box.MinZ * inverse),
                        new Vector3(
                            chunk.MinimumX + micro.LocalX + box.MaxX * inverse,
                            micro.Y + box.MaxY * inverse,
                            chunk.MinimumZ + micro.LocalZ + box.MaxZ * inverse),
                        box.MaterialId));
                }
            }

            return new ColliderBuildData(
                boxes.ToArray(),
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);

            void AddColumnRun(int localX, int localZ, int startY, int endY)
            {
                var key = (startY, endY);
                if (!runsByHeight.TryGetValue(key, out byte[]? mask))
                {
                    mask = ArrayPool<byte>.Shared.Rent(ChunkCoordinate.Size * ChunkCoordinate.Size);
                    Array.Clear(mask, 0, ChunkCoordinate.Size * ChunkCoordinate.Size);
                    runsByHeight.Add(key, mask);
                }
                mask[localX + ChunkCoordinate.Size * localZ] = 1;
            }
        }
        finally
        {
            foreach (byte[] mask in runsByHeight.Values) ArrayPool<byte>.Shared.Return(mask);
        }
    }

    private void CreateStaticChunk(ChunkCoordinate chunk, IReadOnlyList<VoxelAabb> boxes)
    {
        var bodies = new List<StaticBody>(boxes.Count);
        foreach (VoxelAabb box in boxes) AddStaticBox(box, bodies);
        if (bodies.Count > 0) _staticChunks[chunk] = bodies;
    }

    private void AddStaticBox(VoxelAabb voxel, List<StaticBody> bodies)
    {
        Vector3 size = voxel.Maximum - voxel.Minimum;
        Vector3 center = (voxel.Minimum + voxel.Maximum) * 0.5f;
        TypedIndex shape = _simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));
        StaticHandle handle = _simulation.Statics.Add(new StaticDescription(center, shape));
        bodies.Add(new StaticBody(handle, shape));
    }

    private void RemoveDynamic(ulong id)
    {
        if (!_dynamicBodies.Remove(id, out DynamicBody entry)) return;
        _simulation.Bodies.Remove(entry.Handle);
        _simulation.Shapes.Remove(entry.Shape);
    }

    private void RemoveStaticChunk(ChunkCoordinate chunk)
    {
        _pendingStaticEvictions.Remove(chunk);
        _partiallyEvictedStaticChunks.Remove(chunk);
        if (!_staticChunks.Remove(chunk, out List<StaticBody>? bodies)) return;
        foreach (StaticBody body in bodies)
        {
            _simulation.Statics.Remove(body.Handle);
            _simulation.Shapes.Remove(body.Shape);
        }
    }

    private void OnVoxelChanged(VoxelChangedEvent change)
    {
        // Static Chunk colliders contain occupancy boxes only; material IDs do not
        // affect their shape or contact properties. Avoid tearing down and recreating
        // hundreds of BEPU shapes when a full collidable block is merely repainted.
        bool oldCollidable = _world.IsCollidableMaterial(change.OldBlockId);
        bool newCollidable = _world.IsCollidableMaterial(change.NewBlockId);
        if (oldCollidable == newCollidable) return;
        MarkChunkDirty(change.X, change.Z, urgent: true);
    }
    private void OnMicroVoxelCellChanged(int x, int y, int z) => MarkChunkDirty(x, z, urgent: true);
    private void OnWorldReloaded()
    {
        // A fractional step belongs to the old world. Never carry it across a load or
        // regeneration boundary, where it could advance newly restored bodies early.
        _timeAccumulator = 0f;
        foreach (ulong id in _dynamicBodies.Keys.ToArray()) RemoveDynamic(id);
        foreach (ChunkCoordinate chunk in _staticChunks.Keys.ToArray()) RemoveStaticChunk(chunk);
        _dirtyChunks.Clear();
        _urgentDirtyChunks.Clear();
        _pendingStaticEvictions.Clear();
        _partiallyEvictedStaticChunks.Clear();
        _colliderVersions.Clear();
        _colliderLastDirtyTimestamps.Clear();
        _colliderDirtyCounts.Clear();
        _colliderEpoch++;
        _colliderResidencySignature = int.MinValue;
        MarkAllStaticDirty();
    }

    private void MarkChunkDirty(int x, int z, bool urgent = false)
    {
        ChunkCoordinate chunk = ChunkCoordinate.FromVoxel(x, z);
        MarkChunkDirty(chunk, urgent);
    }
    private void MarkChunkDirty(ChunkCoordinate chunk, bool urgent = false)
    {
        if (!IsColliderRequired(chunk)) return;
        bool alreadyDirty = !_dirtyChunks.Add(chunk);
        _colliderLastDirtyTimestamps[chunk] = Stopwatch.GetTimestamp();
        _colliderDirtyCounts[chunk] = alreadyDirty && _colliderDirtyCounts.TryGetValue(chunk, out int dirtyCount)
            ? dirtyCount + 1
            : 1;
        _colliderVersions[chunk] = _colliderVersions.TryGetValue(chunk, out int version)
            ? unchecked(version + 1)
            : 1;
        if (urgent) _urgentDirtyChunks.Add(chunk);
    }
    private void OnChunkGenerated(ChunkCoordinate chunk)
    {
        if (IsColliderRequired(chunk)) MarkChunkDirty(chunk);
    }
    private void MarkAllStaticDirty()
    {
        foreach (ChunkCoordinate chunk in _staticChunks.Keys)
            if (IsColliderRequired(chunk)) MarkChunkDirty(chunk);
        foreach (ChunkCoordinate chunk in _world.SnapshotLoadedChunks())
            if (IsColliderRequired(chunk)) MarkChunkDirty(chunk);
    }

    private void RefreshStaticColliderResidency()
    {
        int signature = CalculateColliderResidencySignature();
        if (signature == _colliderResidencySignature) return;
        _colliderResidencySignature = signature;

        HashSet<ChunkCoordinate> required = _world.SnapshotLoadedChunks()
            .Where(IsColliderRequired)
            .ToHashSet();
        foreach (ChunkCoordinate stale in _staticChunks.Keys.Where(chunk => !required.Contains(chunk)))
            _pendingStaticEvictions.Add(stale);
        foreach (ChunkCoordinate retained in required) _pendingStaticEvictions.Remove(retained);

        foreach (ChunkCoordinate dirty in _dirtyChunks.Where(chunk => !required.Contains(chunk)).ToArray())
        {
            _dirtyChunks.Remove(dirty);
            _urgentDirtyChunks.Remove(dirty);
            _colliderVersions.Remove(dirty);
            _colliderLastDirtyTimestamps.Remove(dirty);
            _colliderDirtyCounts.Remove(dirty);
        }
        foreach (ChunkCoordinate chunk in required)
        {
            if (_partiallyEvictedStaticChunks.Contains(chunk) && !_dirtyChunks.Contains(chunk))
                MarkChunkDirty(chunk, urgent: true);
            else if (!_staticChunks.ContainsKey(chunk) && !_dirtyChunks.Contains(chunk))
                MarkChunkDirty(chunk);
        }
    }

    private int CalculateColliderResidencySignature()
    {
        var hash = new HashCode();
        hash.Add(ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(_simulationFocus.X),
            (int)MathF.Floor(_simulationFocus.Z)));
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            (Vector3 minimum, Vector3 maximum) = grid.CalculateWorldBounds();
            hash.Add(grid.Id);
            hash.Add(ChunkCoordinate.FromVoxel((int)MathF.Floor(minimum.X), (int)MathF.Floor(minimum.Z)));
            hash.Add(ChunkCoordinate.FromVoxel((int)MathF.Floor(maximum.X), (int)MathF.Floor(maximum.Z)));
        }
        return hash.ToHashCode();
    }

    private bool IsColliderRequired(ChunkCoordinate chunk)
    {
        ChunkCoordinate focus = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(_simulationFocus.X),
            (int)MathF.Floor(_simulationFocus.Z));
        if (Math.Max(Math.Abs(chunk.X - focus.X), Math.Abs(chunk.Z - focus.Z)) <=
            PlayerColliderRadiusChunks) return true;

        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            (Vector3 minimum, Vector3 maximum) = grid.CalculateWorldBounds();
            ChunkCoordinate first = ChunkCoordinate.FromVoxel(
                (int)MathF.Floor(minimum.X) - ChunkCoordinate.Size,
                (int)MathF.Floor(minimum.Z) - ChunkCoordinate.Size);
            ChunkCoordinate last = ChunkCoordinate.FromVoxel(
                (int)MathF.Floor(maximum.X) + ChunkCoordinate.Size,
                (int)MathF.Floor(maximum.Z) + ChunkCoordinate.Size);
            if (chunk.X >= first.X && chunk.X <= last.X && chunk.Z >= first.Z && chunk.Z <= last.Z)
                return true;
        }
        return false;
    }

    private bool IsColliderEditQuiet(ChunkCoordinate chunk)
    {
        if (!_colliderLastDirtyTimestamps.TryGetValue(chunk, out long changedAt)) return true;
        double quietMilliseconds = _colliderDirtyCounts.TryGetValue(chunk, out int dirtyCount) && dirtyCount > 1
            ? RepeatedColliderEditCoalesceMilliseconds
            : ColliderEditCoalesceMilliseconds;
        return ElapsedMilliseconds(changedAt) >= quietMilliseconds;
    }

    private void EvictStaleStaticBodies()
    {
        ChunkCoordinate? stale = _pendingStaticEvictions
            .Where(chunk => !IsColliderRequired(chunk))
            .Cast<ChunkCoordinate?>()
            .FirstOrDefault();
        if (!stale.HasValue) return;
        if (!_staticChunks.TryGetValue(stale.Value, out List<StaticBody>? bodies))
        {
            _pendingStaticEvictions.Remove(stale.Value);
            _partiallyEvictedStaticChunks.Remove(stale.Value);
            return;
        }
        int removeCount = Math.Min(MaximumStaticBodiesRemovedPerFrame, bodies.Count);
        for (int index = 0; index < removeCount; index++)
        {
            int last = bodies.Count - 1;
            StaticBody body = bodies[last];
            bodies.RemoveAt(last);
            _simulation.Statics.Remove(body.Handle);
            _simulation.Shapes.Remove(body.Shape);
        }
        if (bodies.Count == 0)
        {
            _staticChunks.Remove(stale.Value);
            _pendingStaticEvictions.Remove(stale.Value);
            _partiallyEvictedStaticChunks.Remove(stale.Value);
        }
        else
        {
            _partiallyEvictedStaticChunks.Add(stale.Value);
        }
    }
    private void OnBlockDefinitionsChanged() =>
        _blockDefinitions = _world.CreateBlockDefinitionLookupSnapshot();
    internal bool IsStaticChunkRegistered(ChunkCoordinate chunk) => _staticChunks.ContainsKey(chunk);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.DetailedVoxelChanged -= OnVoxelChanged;
        _world.MicroVoxelCellChanged -= OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded -= MarkAllStaticDirty;
        _world.WorldReloaded -= OnWorldReloaded;
        _world.ChunkGenerated -= OnChunkGenerated;
        _world.BlockDefinitionsChanged -= OnBlockDefinitionsChanged;
        _simulation.Dispose();
        _dispatcher.Dispose();
        _pool.Clear();
    }

    private readonly record struct DynamicBody(BodyHandle Handle, TypedIndex Shape, Vector3 CenterOffset, int Signature);
    private readonly record struct StaticBody(StaticHandle Handle, TypedIndex Shape);
    private sealed record ColliderBuildJob(
        ChunkCoordinate Chunk,
        int Version,
        int Epoch,
        Task<ColliderBuildData> Build);
    private sealed record ColliderBuildData(VoxelAabb[] Boxes, double BuildMilliseconds);

    private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        private readonly Vector3 _gravity;
        private readonly float _linearDamping;
        private readonly float _angularDamping;
        private Vector3Wide _gravityDt;
        private Vector<float> _linearDt;
        private Vector<float> _angularDt;
        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.ConserveMomentum;
        public bool AllowSubstepsForUnconstrainedBodies => true;
        public bool IntegrateVelocityForKinematics => false;
        public PoseIntegratorCallbacks(Vector3 gravity, float linearDamping, float angularDamping)
        { _gravity = gravity; _linearDamping = linearDamping; _angularDamping = angularDamping; _gravityDt = default; _linearDt = default; _angularDt = default; }
        public void Initialize(Simulation simulation) { }
        public void PrepareForIntegration(float dt)
        {
            _gravityDt = Vector3Wide.Broadcast(_gravity * dt);
            _linearDt = new Vector<float>(MathF.Pow(Math.Clamp(1f - _linearDamping, 0f, 1f), dt));
            _angularDt = new Vector<float>(MathF.Pow(Math.Clamp(1f - _angularDamping, 0f, 1f), dt));
        }
        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
            BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
            ref BodyVelocityWide velocity)
        { velocity.Linear = (velocity.Linear + _gravityDt) * _linearDt; velocity.Angular *= _angularDt; }
    }

    private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        private SpringSettings _spring;
        private readonly float _maxRecovery;
        private readonly float _friction;
        public NarrowPhaseCallbacks(SpringSettings spring, float maxRecovery, float friction)
        { _spring = spring; _maxRecovery = maxRecovery; _friction = friction; }
        public void Initialize(Simulation simulation) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) =>
            a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
            out PairMaterialProperties material) where TManifold : unmanaged, IContactManifold<TManifold>
        { material.FrictionCoefficient = _friction; material.MaximumRecoveryVelocity = _maxRecovery; material.SpringSettings = _spring; return true; }
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
            ref ConvexContactManifold manifold) => true;
        public void Dispose() { }
    }
}
