namespace RTXSandbox.World;

internal readonly record struct StoredVoxel(int LocalX, int Y, int LocalZ, ushort MaterialId);
internal readonly record struct StoredFluidLevel(int LocalX, int Y, int LocalZ, byte Level);
internal readonly record struct StoredMicroVoxel(int LocalX, int Y, int LocalZ, MicroVoxelSnapshot Snapshot);
internal sealed record VoxelChunkSnapshot(
    ChunkCoordinate Coordinate,
    IReadOnlyList<StoredVoxel> Voxels,
    IReadOnlyList<StoredFluidLevel> FluidLevels,
    IReadOnlyList<StoredMicroVoxel> MicroVoxels);
internal sealed record ChunkMeshingSnapshot(
    ChunkCoordinate Coordinate,
    int MinimumY,
    int MaximumYExclusive,
    ushort[] Blocks,
    byte[] FluidLevels,
    IReadOnlyList<StoredMicroVoxel> MicroVoxels)
{
    private const int Border = 1;
    private const int WidthWithBorder = ChunkCoordinate.Size + Border * 2;

    public ushort GetBlock(int worldX, int y, int worldZ)
    {
        int localX = worldX - Coordinate.MinimumX + Border;
        int localZ = worldZ - Coordinate.MinimumZ + Border;
        if ((uint)localX >= WidthWithBorder || (uint)localZ >= WidthWithBorder ||
            y < MinimumY || y >= MaximumYExclusive)
            return 0;
        int localY = y - MinimumY;
        return Blocks[localX + WidthWithBorder * (localY + (MaximumYExclusive - MinimumY) * localZ)];
    }

    public byte GetFluidLevel(int worldX, int y, int worldZ)
    {
        int localX = worldX - Coordinate.MinimumX + Border;
        int localZ = worldZ - Coordinate.MinimumZ + Border;
        if ((uint)localX >= WidthWithBorder || (uint)localZ >= WidthWithBorder ||
            y < MinimumY || y >= MaximumYExclusive)
            return 0;
        int localY = y - MinimumY;
        return FluidLevels[localX + WidthWithBorder * (localY + (MaximumYExclusive - MinimumY) * localZ)];
    }
}

/// <summary>
/// Owns regular and precision voxel data by horizontal chunk. X/Z are unbounded;
/// the vertical address is translated through the configured minimum Y.
/// </summary>
internal sealed class VoxelChunkStore
{
    private readonly Dictionary<ChunkCoordinate, VoxelChunk> _chunks = new();
    private readonly int _minimumY;
    private readonly int _height;

    public int ChunkCount => _chunks.Count;
    public int MicroVoxelContainerCount => _chunks.Values.Sum(chunk => chunk.MicroVoxelCount);

    public VoxelChunkStore(int minimumY, int maximumYExclusive)
    {
        int height = checked(maximumYExclusive - minimumY);
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        _minimumY = minimumY;
        _height = height;
    }

    public IReadOnlyList<ChunkCoordinate> SnapshotCoordinates() => _chunks.Keys
        .OrderBy(coordinate => coordinate.Z)
        .ThenBy(coordinate => coordinate.X)
        .ToArray();

    public bool Contains(ChunkCoordinate coordinate) => _chunks.ContainsKey(coordinate);

    public ushort GetBlock(int x, int y, int z)
    {
        ChunkCoordinate coordinate = ChunkCoordinate.FromVoxel(x, z);
        return _chunks.TryGetValue(coordinate, out VoxelChunk? chunk)
            ? chunk.GetBlock(x - coordinate.MinimumX, y, z - coordinate.MinimumZ)
            : (ushort)0;
    }

    public bool SetBlock(int x, int y, int z, ushort blockId, out ushort oldBlockId, out bool removedMicro)
    {
        ChunkCoordinate coordinate = ChunkCoordinate.FromVoxel(x, z);
        if (!_chunks.TryGetValue(coordinate, out VoxelChunk? chunk))
        {
            oldBlockId = 0;
            removedMicro = false;
            if (blockId == 0) return false;
            chunk = new VoxelChunk(_minimumY, _height);
            _chunks.Add(coordinate, chunk);
        }

        int localX = x - coordinate.MinimumX;
        int localZ = z - coordinate.MinimumZ;
        oldBlockId = chunk.GetBlock(localX, y, localZ);
        removedMicro = chunk.RemoveMicroVoxels(localX, y, localZ);
        bool blockChanged = chunk.SetBlock(localX, y, localZ, blockId);
        RemoveIfEmpty(coordinate, chunk);
        return blockChanged || removedMicro;
    }

    public bool TryGetMicroVoxels(int x, int y, int z, out MicroVoxelContainer? container)
    {
        ChunkCoordinate coordinate = ChunkCoordinate.FromVoxel(x, z);
        if (_chunks.TryGetValue(coordinate, out VoxelChunk? chunk))
            return chunk.TryGetMicroVoxels(x - coordinate.MinimumX, y, z - coordinate.MinimumZ, out container);
        container = null;
        return false;
    }

    public bool RemoveMicroVoxels(int x, int y, int z)
    {
        ChunkCoordinate coordinate = ChunkCoordinate.FromVoxel(x, z);
        if (!_chunks.TryGetValue(coordinate, out VoxelChunk? chunk) ||
            !chunk.RemoveMicroVoxels(x - coordinate.MinimumX, y, z - coordinate.MinimumZ)) return false;
        RemoveIfEmpty(coordinate, chunk);
        return true;
    }

    public void SetMicroVoxels(int x, int y, int z, MicroVoxelContainer container)
    {
        ChunkCoordinate coordinate = ChunkCoordinate.FromVoxel(x, z);
        if (!_chunks.TryGetValue(coordinate, out VoxelChunk? chunk))
        {
            chunk = new VoxelChunk(_minimumY, _height);
            _chunks.Add(coordinate, chunk);
        }
        chunk.SetMicroVoxels(x - coordinate.MinimumX, y, z - coordinate.MinimumZ, container);
    }

    public IEnumerable<(int X, int Y, int Z, MicroVoxelContainer Container)> EnumerateMicroVoxels()
    {
        foreach ((ChunkCoordinate coordinate, VoxelChunk chunk) in _chunks
                     .OrderBy(pair => pair.Key.Z)
                     .ThenBy(pair => pair.Key.X))
        foreach ((int localX, int y, int localZ, MicroVoxelContainer container) in chunk.EnumerateMicroVoxels())
            yield return (coordinate.MinimumX + localX, y, coordinate.MinimumZ + localZ, container);
    }

    public void Clear()
    {
        _chunks.Clear();
    }

    public void ClearMicroVoxels()
    {
        foreach ((ChunkCoordinate coordinate, VoxelChunk chunk) in _chunks.ToArray())
        {
            chunk.ClearMicroVoxels();
            RemoveIfEmpty(coordinate, chunk);
        }
    }

    public void LoadLinear(ReadOnlySpan<ushort> source, int width, int depth)
    {
        int expectedLength = checked(width * _height * depth);
        if (source.Length != expectedLength)
            throw new ArgumentException($"Expected {expectedLength} voxels, received {source.Length}.", nameof(source));
        Clear();
        for (int z = 0; z < depth; z++)
        for (int localY = 0; localY < _height; localY++)
        for (int x = 0; x < width; x++)
        {
            ushort blockId = source[LinearIndex(x, localY, z, width, _height)];
            if (blockId != 0) SetBlock(x, _minimumY + localY, z, blockId, out _, out _);
        }
    }

    public ushort[] CreateLinearSnapshot(int width, int depth)
        => CreateWindowSnapshot(0, 0, width, depth);

    public ushort[] CreateWindowSnapshot(int originX, int originZ, int width, int depth)
    {
        var destination = new ushort[checked(width * _height * depth)];
        foreach ((ChunkCoordinate coordinate, VoxelChunk chunk) in _chunks)
            chunk.CopyBlocksToLinear(destination, coordinate, originX, originZ, width, _height, depth);
        return destination;
    }

    public VoxelChunkSnapshot SnapshotChunk(ChunkCoordinate coordinate)
    {
        if (!_chunks.TryGetValue(coordinate, out VoxelChunk? chunk))
            return new VoxelChunkSnapshot(
                coordinate, Array.Empty<StoredVoxel>(), Array.Empty<StoredFluidLevel>(),
                Array.Empty<StoredMicroVoxel>());
        return new VoxelChunkSnapshot(
            coordinate,
            chunk.EnumerateBlocks().ToArray(),
            Array.Empty<StoredFluidLevel>(),
            chunk.EnumerateMicroVoxels()
                .Select(entry => new StoredMicroVoxel(entry.LocalX, entry.Y, entry.LocalZ, entry.Container.Snapshot()))
                .ToArray());
    }

    public void StoreChunk(VoxelChunkSnapshot snapshot)
    {
        if (_chunks.ContainsKey(snapshot.Coordinate))
            throw new InvalidOperationException($"Chunk {snapshot.Coordinate} is already loaded.");
        var chunk = new VoxelChunk(_minimumY, _height);
        foreach (StoredVoxel voxel in snapshot.Voxels)
            chunk.SetBlock(voxel.LocalX, voxel.Y, voxel.LocalZ, voxel.MaterialId);
        foreach (StoredMicroVoxel micro in snapshot.MicroVoxels)
            chunk.SetMicroVoxels(
                micro.LocalX,
                micro.Y,
                micro.LocalZ,
                new MicroVoxelContainer(micro.Snapshot));
        if (!chunk.IsEmpty) _chunks.Add(snapshot.Coordinate, chunk);
    }

    public OwnedChunk ExtractChunk(ChunkCoordinate coordinate)
    {
        _chunks.Remove(coordinate, out VoxelChunk? chunk);
        return new OwnedChunk(_minimumY, _height, chunk);
    }

    public void AdoptChunk(OwnedChunk source, ChunkCoordinate coordinate)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (source._minimumY != _minimumY || source._height != _height)
            throw new InvalidOperationException("Cannot adopt a Chunk with different vertical bounds.");
        if (_chunks.ContainsKey(coordinate))
            throw new InvalidOperationException($"Chunk {coordinate} is already loaded.");
        if (source.Take() is VoxelChunk chunk) _chunks.Add(coordinate, chunk);
    }

    internal sealed class OwnedChunk
    {
        internal readonly int _minimumY;
        internal readonly int _height;
        private VoxelChunk? _chunk;

        internal OwnedChunk(int minimumY, int height, VoxelChunk? chunk)
        {
            _minimumY = minimumY;
            _height = height;
            _chunk = chunk;
        }

        internal VoxelChunk? Take()
        {
            VoxelChunk? chunk = _chunk;
            _chunk = null;
            return chunk;
        }
    }

    public bool TryGetVerticalBounds(ChunkCoordinate coordinate, out int minimumY, out int maximumYExclusive)
    {
        if (_chunks.TryGetValue(coordinate, out VoxelChunk? chunk))
            return chunk.TryGetVerticalBounds(out minimumY, out maximumYExclusive);
        minimumY = maximumYExclusive = 0;
        return false;
    }

    public void LoadChunks(IEnumerable<VoxelChunkSnapshot> snapshots)
    {
        Clear();
        foreach (VoxelChunkSnapshot snapshot in snapshots)
        {
            foreach (StoredVoxel voxel in snapshot.Voxels)
            {
                SetBlock(
                    snapshot.Coordinate.MinimumX + voxel.LocalX,
                    voxel.Y,
                    snapshot.Coordinate.MinimumZ + voxel.LocalZ,
                    voxel.MaterialId,
                    out _, out _);
            }
            foreach (StoredMicroVoxel micro in snapshot.MicroVoxels)
            {
                SetMicroVoxels(
                    snapshot.Coordinate.MinimumX + micro.LocalX,
                    micro.Y,
                    snapshot.Coordinate.MinimumZ + micro.LocalZ,
                    new MicroVoxelContainer(micro.Snapshot));
            }
        }
    }

    private void RemoveIfEmpty(ChunkCoordinate coordinate, VoxelChunk chunk)
    {
        if (chunk.IsEmpty) _chunks.Remove(coordinate);
    }

    private static int LinearIndex(int x, int y, int z, int width, int height) =>
        x + width * (y + height * z);

    internal sealed class VoxelChunk
    {
        private readonly ushort[] _blocks;
        private readonly int[] _occupiedCellsByLayer;
        private readonly Dictionary<int, MicroVoxelContainer> _microVoxels = new();
        private readonly int _minimumY;
        private readonly int _height;
        private int _regularBlockCount;
        private int _minimumOccupiedY = int.MaxValue;
        private int _maximumOccupiedY = int.MinValue;

        public bool IsEmpty => _regularBlockCount == 0 && _microVoxels.Count == 0;
        public int MicroVoxelCount => _microVoxels.Count;

        public VoxelChunk(int minimumY, int height)
        {
            _minimumY = minimumY;
            _height = height;
            _blocks = new ushort[ChunkCoordinate.Size * height * ChunkCoordinate.Size];
            _occupiedCellsByLayer = new int[height];
        }

        public ushort GetBlock(int localX, int y, int localZ) => _blocks[Index(localX, y, localZ)];

        public bool SetBlock(int localX, int y, int localZ, ushort blockId)
        {
            int index = Index(localX, y, localZ);
            ushort oldBlockId = _blocks[index];
            if (oldBlockId == blockId) return false;
            if (oldBlockId == 0 && blockId != 0)
            {
                _regularBlockCount++;
                _occupiedCellsByLayer[y - _minimumY]++;
            }
            if (oldBlockId != 0 && blockId == 0)
            {
                _regularBlockCount--;
                _occupiedCellsByLayer[y - _minimumY]--;
            }
            _blocks[index] = blockId;
            if (blockId != 0) ExpandBounds(y);
            else if ((y == _minimumOccupiedY || y == _maximumOccupiedY) &&
                     _occupiedCellsByLayer[y - _minimumY] == 0)
                TrimEmptyBoundaryLayers();
            return true;
        }

        public bool TryGetMicroVoxels(int localX, int y, int localZ, out MicroVoxelContainer? container) =>
            _microVoxels.TryGetValue(Index(localX, y, localZ), out container);

        public bool RemoveMicroVoxels(int localX, int y, int localZ)
        {
            if (!_microVoxels.Remove(Index(localX, y, localZ))) return false;
            _occupiedCellsByLayer[y - _minimumY]--;
            if ((y == _minimumOccupiedY || y == _maximumOccupiedY) &&
                _occupiedCellsByLayer[y - _minimumY] == 0)
                TrimEmptyBoundaryLayers();
            return true;
        }

        public void SetMicroVoxels(int localX, int y, int localZ, MicroVoxelContainer container)
        {
            int index = Index(localX, y, localZ);
            bool alreadyOccupied = _blocks[index] != 0 || _microVoxels.ContainsKey(index);
            if (_blocks[index] != 0)
            {
                _blocks[index] = 0;
                _regularBlockCount--;
            }
            _microVoxels[index] = container;
            if (!alreadyOccupied) _occupiedCellsByLayer[y - _minimumY]++;
            ExpandBounds(y);
        }

        public IEnumerable<(int LocalX, int Y, int LocalZ, MicroVoxelContainer Container)> EnumerateMicroVoxels()
        {
            foreach ((int index, MicroVoxelContainer container) in _microVoxels.OrderBy(pair => pair.Key))
            {
                int localX = index % ChunkCoordinate.Size;
                int yz = index / ChunkCoordinate.Size;
                int y = _minimumY + yz % _height;
                int localZ = yz / _height;
                yield return (localX, y, localZ, container);
            }
        }

        public IEnumerable<StoredVoxel> EnumerateBlocks()
        {
            if (_regularBlockCount == 0) yield break;
            int minimumLocalY = _minimumOccupiedY - _minimumY;
            int maximumLocalY = _maximumOccupiedY - _minimumY;
            for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
            for (int localY = minimumLocalY; localY <= maximumLocalY; localY++)
            for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
            {
                ushort material = _blocks[localX + ChunkCoordinate.Size * (localY + _height * localZ)];
                if (material != 0)
                    yield return new StoredVoxel(localX, _minimumY + localY, localZ, material);
            }
        }

        public void ClearMicroVoxels()
        {
            foreach (int index in _microVoxels.Keys)
            {
                int yz = index / ChunkCoordinate.Size;
                int localY = yz % _height;
                _occupiedCellsByLayer[localY]--;
            }
            _microVoxels.Clear();
            TrimEmptyBoundaryLayers();
        }

        public bool TryGetVerticalBounds(out int minimumY, out int maximumYExclusive)
        {
            minimumY = _minimumOccupiedY;
            maximumYExclusive = _maximumOccupiedY + 1;
            return _minimumOccupiedY != int.MaxValue;
        }

        public void CopyBlocksToLinear(
            Span<ushort> destination,
            ChunkCoordinate coordinate,
            int originX,
            int originZ,
            int width,
            int height,
            int depth)
        {
            if (_regularBlockCount == 0) return;
            int startX = Math.Max(coordinate.MinimumX, originX);
            int startZ = Math.Max(coordinate.MinimumZ, originZ);
            int endX = Math.Min(coordinate.MinimumX + ChunkCoordinate.Size, originX + width);
            int endZ = Math.Min(coordinate.MinimumZ + ChunkCoordinate.Size, originZ + depth);
            int startLocalY = Math.Max(0, _minimumOccupiedY - _minimumY);
            int endLocalY = Math.Min(height, _maximumOccupiedY - _minimumY + 1);
            for (int z = startZ; z < endZ; z++)
            for (int localY = startLocalY; localY < endLocalY; localY++)
            for (int x = startX; x < endX; x++)
            {
                int y = _minimumY + localY;
                ushort blockId = GetBlock(x - coordinate.MinimumX, y, z - coordinate.MinimumZ);
                if (blockId != 0)
                    destination[LinearIndex(x - originX, localY, z - originZ, width, height)] = blockId;
            }
        }

        private int Index(int localX, int y, int localZ) =>
            localX + ChunkCoordinate.Size * (checked(y - _minimumY) + _height * localZ);

        private void ExpandBounds(int y)
        {
            _minimumOccupiedY = Math.Min(_minimumOccupiedY, y);
            _maximumOccupiedY = Math.Max(_maximumOccupiedY, y);
        }

        private void TrimEmptyBoundaryLayers()
        {
            if (_regularBlockCount == 0 && _microVoxels.Count == 0)
            {
                _minimumOccupiedY = int.MaxValue;
                _maximumOccupiedY = int.MinValue;
                return;
            }
            int minimumLayer = Math.Max(0, _minimumOccupiedY - _minimumY);
            while (minimumLayer < _height && _occupiedCellsByLayer[minimumLayer] == 0) minimumLayer++;
            int maximumLayer = Math.Min(_height - 1, _maximumOccupiedY - _minimumY);
            while (maximumLayer >= minimumLayer && _occupiedCellsByLayer[maximumLayer] == 0) maximumLayer--;
            _minimumOccupiedY = _minimumY + minimumLayer;
            _maximumOccupiedY = _minimumY + maximumLayer;
        }
    }
}
