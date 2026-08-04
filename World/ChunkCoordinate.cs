namespace RTXSandbox.World;

/// <summary>
/// Horizontal world-chunk address shared by rendering and physics. Conversion uses floor
/// division so the same rules remain valid when the bounded prototype gains negative space.
/// </summary>
public readonly record struct ChunkCoordinate(int X, int Z)
{
    public const int Size = 16;

    public int MinimumX => checked(X * Size);
    public int MinimumZ => checked(Z * Size);

    public static ChunkCoordinate FromVoxel(int x, int z) =>
        new(FloorDivide(x, Size), FloorDivide(z, Size));

    public static IEnumerable<ChunkCoordinate> CoveringArea(int width, int depth)
    {
        if (width <= 0 || depth <= 0) yield break;
        ChunkCoordinate maximum = FromVoxel(width - 1, depth - 1);
        for (int z = 0; z <= maximum.Z; z++)
        for (int x = 0; x <= maximum.X; x++)
            yield return new ChunkCoordinate(x, z);
    }

    public static IEnumerable<ChunkCoordinate> Around(ChunkCoordinate center, int radius)
    {
        radius = Math.Max(radius, 0);
        for (int z = center.Z - radius; z <= center.Z + radius; z++)
        for (int x = center.X - radius; x <= center.X + radius; x++)
            yield return new ChunkCoordinate(x, z);
    }

    /// <summary>Chunks whose exposed faces can change when one voxel is edited.</summary>
    public static IEnumerable<ChunkCoordinate> MeshDependenciesForVoxel(
        int x,
        int z)
    {
        ChunkCoordinate owner = FromVoxel(x, z);
        yield return owner;
        int localX = x - owner.MinimumX;
        int localZ = z - owner.MinimumZ;
        if (localX == 0) yield return owner with { X = owner.X - 1 };
        if (localX == Size - 1) yield return owner with { X = owner.X + 1 };
        if (localZ == 0) yield return owner with { Z = owner.Z - 1 };
        if (localZ == Size - 1) yield return owner with { Z = owner.Z + 1 };
    }

    public static IEnumerable<ChunkCoordinate> MeshDependenciesForVoxel(
        int x,
        int z,
        int width,
        int depth)
    {
        if (x < 0 || z < 0 || x >= width || z >= depth) yield break;
        ChunkCoordinate owner = FromVoxel(x, z);
        yield return owner;
        int localX = x - owner.MinimumX;
        int localZ = z - owner.MinimumZ;
        if (localX == 0 && x > 0) yield return owner with { X = owner.X - 1 };
        if (localX == Size - 1 && x + 1 < width) yield return owner with { X = owner.X + 1 };
        if (localZ == 0 && z > 0) yield return owner with { Z = owner.Z - 1 };
        if (localZ == Size - 1 && z + 1 < depth) yield return owner with { Z = owner.Z + 1 };
    }

    internal static int FloorDivide(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
