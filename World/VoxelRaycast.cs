using System.Numerics;

namespace RTXSandbox.World;

public readonly record struct VoxelHit(
    int X,
    int Y,
    int Z,
    Vector3 Normal,
    float Distance,
    VoxelMaterial Material,
    ulong GridId = 0)
{
    public ushort MaterialId => (ushort)Material;
    public bool IsPhysicsGrid => GridId != 0;

    public (int X, int Y, int Z) Adjacent =>
        (X + (int)Normal.X, Y + (int)Normal.Y, Z + (int)Normal.Z);
}

public static class VoxelRaycast
{
    public static VoxelHit? Cast(VoxelWorld world, Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (direction.LengthSquared() < 0.000001f)
        {
            return null;
        }

        direction = Vector3.Normalize(direction);
        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);
        int stepX = Math.Sign(direction.X);
        int stepY = Math.Sign(direction.Y);
        int stepZ = Math.Sign(direction.Z);
        float deltaX = SafeInverse(direction.X);
        float deltaY = SafeInverse(direction.Y);
        float deltaZ = SafeInverse(direction.Z);
        float maxX = InitialBoundaryDistance(origin.X, direction.X, x, stepX);
        float maxY = InitialBoundaryDistance(origin.Y, direction.Y, y, stepY);
        float maxZ = InitialBoundaryDistance(origin.Z, direction.Z, z, stepZ);
        Vector3 normal = Vector3.Zero;
        float distance = 0f;

        for (int iteration = 0; iteration < 512 && distance <= maxDistance; iteration++)
        {
            VoxelMaterial material = world.Get(x, y, z);
            // Water is a visual medium, never an editable selection target.
            if (material != VoxelMaterial.Air && !world.IsFluid(x, y, z))
            {
                return new VoxelHit(x, y, z, normal, distance, material);
            }

            if (maxX <= maxY && maxX <= maxZ)
            {
                x += stepX;
                distance = maxX;
                maxX += deltaX;
                normal = new Vector3(-stepX, 0f, 0f);
            }
            else if (maxY <= maxZ)
            {
                y += stepY;
                distance = maxY;
                maxY += deltaY;
                normal = new Vector3(0f, -stepY, 0f);
            }
            else
            {
                z += stepZ;
                distance = maxZ;
                maxZ += deltaZ;
                normal = new Vector3(0f, 0f, -stepZ);
            }
        }

        return null;
    }

    private static float SafeInverse(float value) =>
        MathF.Abs(value) < 0.000001f ? float.PositiveInfinity : MathF.Abs(1f / value);

    private static float InitialBoundaryDistance(float origin, float direction, int cell, int step)
    {
        if (step == 0)
        {
            return float.PositiveInfinity;
        }

        float boundary = step > 0 ? cell + 1f : cell;
        return (boundary - origin) / direction;
    }
}
