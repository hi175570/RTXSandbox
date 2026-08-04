namespace RTXSandbox.World;

/// <summary>A save-region address. One region groups 32×32 horizontal chunks.</summary>
public readonly record struct RegionCoordinate(int X, int Z)
{
    public const int SizeInChunks = 32;
    public const int SizeInBlocks = SizeInChunks * ChunkCoordinate.Size;

    public static RegionCoordinate FromChunk(ChunkCoordinate chunk) => new(
        ChunkCoordinate.FloorDivide(chunk.X, SizeInChunks),
        ChunkCoordinate.FloorDivide(chunk.Z, SizeInChunks));

    public bool Contains(ChunkCoordinate chunk) =>
        FromChunk(chunk) == this;
}
