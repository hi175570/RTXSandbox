using System.Numerics;

namespace RTXSandbox.World;

public readonly record struct MicroVoxelBox(
    int MinX,
    int MinY,
    int MinZ,
    int MaxX,
    int MaxY,
    int MaxZ,
    ushort MaterialId)
{
    public bool IsValid => MaterialId != 0 && MinX < MaxX && MinY < MaxY && MinZ < MaxZ;

    public bool Intersects(int minX, int minY, int minZ, int maxX, int maxY, int maxZ) =>
        MinX < maxX && MaxX > minX &&
        MinY < maxY && MaxY > minY &&
        MinZ < maxZ && MaxZ > minZ;
}

public sealed record MicroVoxelSnapshot(int Resolution, IReadOnlyList<MicroVoxelBox> Boxes);

public readonly record struct MicroVoxelCellSnapshot(
    int X,
    int Y,
    int Z,
    ushort RegularBlockId,
    MicroVoxelSnapshot? MicroVoxels);

public readonly record struct VoxelAabb(Vector3 Minimum, Vector3 Maximum, ushort MaterialId)
{
    public bool Intersects(Vector3 minimum, Vector3 maximum) =>
        Minimum.X < maximum.X && Maximum.X > minimum.X &&
        Minimum.Y < maximum.Y && Maximum.Y > minimum.Y &&
        Minimum.Z < maximum.Z && Maximum.Z > minimum.Z;
}

/// <summary>
/// Sparse exact micro geometry for one normal voxel cell. Coordinates are integers in a
/// per-container grid. Mixing divisors promotes the grid to their LCM, so 1/6 and 1/16
/// coexist exactly at resolution 48 without allocating a 48-cubed volume.
/// </summary>
public sealed class MicroVoxelContainer
{
    public const int MaximumResolution = 384;
    public const int MaximumBoxes = 2048;

    private readonly List<MicroVoxelBox> _boxes = new();

    public int Resolution { get; private set; }
    public IReadOnlyList<MicroVoxelBox> Boxes => _boxes;
    public bool IsEmpty => _boxes.Count == 0;

    public MicroVoxelContainer(int resolution = 1)
    {
        Resolution = ValidateResolution(resolution);
    }

    public MicroVoxelContainer(MicroVoxelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Resolution = ValidateResolution(snapshot.Resolution);
        foreach (MicroVoxelBox box in snapshot.Boxes)
        {
            ValidateBox(box);
            _boxes.Add(box);
        }
        Normalize();
    }

    public MicroVoxelSnapshot Snapshot() => new(Resolution, _boxes.ToArray());

    public void Fill(ushort materialId)
    {
        _boxes.Clear();
        if (materialId != 0)
        {
            _boxes.Add(new MicroVoxelBox(0, 0, 0, Resolution, Resolution, Resolution, materialId));
        }
    }

    public void PaintCell(int divisor, int cellX, int cellY, int cellZ, ushort materialId)
    {
        PromoteFor(divisor);
        int scale = Resolution / divisor;
        cellX = Math.Clamp(cellX, 0, divisor - 1);
        cellY = Math.Clamp(cellY, 0, divisor - 1);
        cellZ = Math.Clamp(cellZ, 0, divisor - 1);
        Paint(
            cellX * scale,
            cellY * scale,
            cellZ * scale,
            (cellX + 1) * scale,
            (cellY + 1) * scale,
            (cellZ + 1) * scale,
            materialId);
    }

    public void Paint(
        int minX,
        int minY,
        int minZ,
        int maxX,
        int maxY,
        int maxZ,
        ushort materialId)
    {
        ValidateBounds(minX, minY, minZ, maxX, maxY, maxZ);
        var replacement = new List<MicroVoxelBox>(_boxes.Count + 7);
        foreach (MicroVoxelBox box in _boxes)
        {
            Subtract(box, minX, minY, minZ, maxX, maxY, maxZ, replacement);
        }
        if (materialId != 0)
        {
            replacement.Add(new MicroVoxelBox(minX, minY, minZ, maxX, maxY, maxZ, materialId));
        }
        _boxes.Clear();
        _boxes.AddRange(replacement);
        Normalize();
        if (_boxes.Count > MaximumBoxes)
        {
            throw new InvalidOperationException($"A micro voxel cell cannot exceed {MaximumBoxes:N0} merged boxes.");
        }
    }

    public bool TryGetFullBlock(out ushort materialId)
    {
        if (_boxes.Count == 1)
        {
            MicroVoxelBox box = _boxes[0];
            if (box.MinX == 0 && box.MinY == 0 && box.MinZ == 0 &&
                box.MaxX == Resolution && box.MaxY == Resolution && box.MaxZ == Resolution)
            {
                materialId = box.MaterialId;
                return true;
            }
        }
        materialId = 0;
        return false;
    }

    public VoxelAabb ToWorldAabb(int worldX, int worldY, int worldZ, MicroVoxelBox box)
    {
        float inverse = 1f / Resolution;
        return new VoxelAabb(
            new Vector3(worldX + box.MinX * inverse, worldY + box.MinY * inverse, worldZ + box.MinZ * inverse),
            new Vector3(worldX + box.MaxX * inverse, worldY + box.MaxY * inverse, worldZ + box.MaxZ * inverse),
            box.MaterialId);
    }

    private void PromoteFor(int divisor)
    {
        divisor = ValidateResolution(divisor);
        int promoted = LeastCommonMultiple(Resolution, divisor);
        if (promoted > MaximumResolution)
        {
            throw new InvalidOperationException(
                $"Combining 1/{Resolution} with 1/{divisor} requires 1/{promoted}; the exact-grid limit is 1/{MaximumResolution}.");
        }
        if (promoted == Resolution) return;
        int scale = promoted / Resolution;
        for (int index = 0; index < _boxes.Count; index++)
        {
            MicroVoxelBox box = _boxes[index];
            _boxes[index] = box with
            {
                MinX = box.MinX * scale,
                MinY = box.MinY * scale,
                MinZ = box.MinZ * scale,
                MaxX = box.MaxX * scale,
                MaxY = box.MaxY * scale,
                MaxZ = box.MaxZ * scale,
            };
        }
        Resolution = promoted;
    }

    private void Normalize()
    {
        _boxes.RemoveAll(box => !box.IsValid);
        bool merged;
        do
        {
            merged = false;
            for (int leftIndex = 0; leftIndex < _boxes.Count && !merged; leftIndex++)
            for (int rightIndex = leftIndex + 1; rightIndex < _boxes.Count; rightIndex++)
            {
                if (TryMerge(_boxes[leftIndex], _boxes[rightIndex], out MicroVoxelBox combined))
                {
                    _boxes[leftIndex] = combined;
                    _boxes.RemoveAt(rightIndex);
                    merged = true;
                    break;
                }
            }
        } while (merged);

        _boxes.Sort(static (left, right) =>
        {
            int compare = left.MinY.CompareTo(right.MinY);
            if (compare != 0) return compare;
            compare = left.MinZ.CompareTo(right.MinZ);
            return compare != 0 ? compare : left.MinX.CompareTo(right.MinX);
        });
    }

    private static void Subtract(
        MicroVoxelBox source,
        int minX,
        int minY,
        int minZ,
        int maxX,
        int maxY,
        int maxZ,
        List<MicroVoxelBox> output)
    {
        if (!source.Intersects(minX, minY, minZ, maxX, maxY, maxZ))
        {
            output.Add(source);
            return;
        }

        int ix0 = Math.Max(source.MinX, minX);
        int iy0 = Math.Max(source.MinY, minY);
        int iz0 = Math.Max(source.MinZ, minZ);
        int ix1 = Math.Min(source.MaxX, maxX);
        int iy1 = Math.Min(source.MaxY, maxY);
        int iz1 = Math.Min(source.MaxZ, maxZ);
        ushort material = source.MaterialId;

        AddIfValid(output, new MicroVoxelBox(source.MinX, source.MinY, source.MinZ, ix0, source.MaxY, source.MaxZ, material));
        AddIfValid(output, new MicroVoxelBox(ix1, source.MinY, source.MinZ, source.MaxX, source.MaxY, source.MaxZ, material));
        AddIfValid(output, new MicroVoxelBox(ix0, source.MinY, source.MinZ, ix1, iy0, source.MaxZ, material));
        AddIfValid(output, new MicroVoxelBox(ix0, iy1, source.MinZ, ix1, source.MaxY, source.MaxZ, material));
        AddIfValid(output, new MicroVoxelBox(ix0, iy0, source.MinZ, ix1, iy1, iz0, material));
        AddIfValid(output, new MicroVoxelBox(ix0, iy0, iz1, ix1, iy1, source.MaxZ, material));
    }

    private static bool TryMerge(MicroVoxelBox left, MicroVoxelBox right, out MicroVoxelBox merged)
    {
        merged = default;
        if (left.MaterialId != right.MaterialId) return false;

        if (left.MinY == right.MinY && left.MaxY == right.MaxY &&
            left.MinZ == right.MinZ && left.MaxZ == right.MaxZ &&
            (left.MaxX == right.MinX || right.MaxX == left.MinX))
        {
            merged = left with { MinX = Math.Min(left.MinX, right.MinX), MaxX = Math.Max(left.MaxX, right.MaxX) };
            return true;
        }
        if (left.MinX == right.MinX && left.MaxX == right.MaxX &&
            left.MinZ == right.MinZ && left.MaxZ == right.MaxZ &&
            (left.MaxY == right.MinY || right.MaxY == left.MinY))
        {
            merged = left with { MinY = Math.Min(left.MinY, right.MinY), MaxY = Math.Max(left.MaxY, right.MaxY) };
            return true;
        }
        if (left.MinX == right.MinX && left.MaxX == right.MaxX &&
            left.MinY == right.MinY && left.MaxY == right.MaxY &&
            (left.MaxZ == right.MinZ || right.MaxZ == left.MinZ))
        {
            merged = left with { MinZ = Math.Min(left.MinZ, right.MinZ), MaxZ = Math.Max(left.MaxZ, right.MaxZ) };
            return true;
        }
        return false;
    }

    private void ValidateBounds(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        if (minX < 0 || minY < 0 || minZ < 0 ||
            maxX > Resolution || maxY > Resolution || maxZ > Resolution ||
            minX >= maxX || minY >= maxY || minZ >= maxZ)
        {
            throw new ArgumentOutOfRangeException(nameof(minX), "Micro voxel bounds must be a non-empty box inside the container resolution.");
        }
    }

    private void ValidateBox(MicroVoxelBox box)
    {
        ValidateBounds(box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ);
        if (box.MaterialId == 0) throw new InvalidDataException("Micro voxel boxes cannot store air as a material.");
    }

    private static int ValidateResolution(int resolution)
    {
        if (resolution < 1 || resolution > MaximumResolution)
            throw new ArgumentOutOfRangeException(nameof(resolution), $"Micro resolution must be between 1 and {MaximumResolution}.");
        return resolution;
    }

    private static void AddIfValid(List<MicroVoxelBox> output, MicroVoxelBox box)
    {
        if (box.IsValid) output.Add(box);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Abs(left);
    }

    private static int LeastCommonMultiple(int left, int right) =>
        checked(left / GreatestCommonDivisor(left, right) * right);
}

public readonly record struct MicroVoxelHit(
    int X,
    int Y,
    int Z,
    Vector3 Position,
    Vector3 Normal,
    float Distance,
    ushort MaterialId);

public static class MicroVoxelRaycast
{
    public static MicroVoxelHit? Cast(VoxelWorld world, Vector3 origin, Vector3 direction, float maxDistance)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (direction.LengthSquared() < 0.000001f) return null;
        direction = Vector3.Normalize(direction);
        MicroVoxelHit? nearest = null;

        foreach ((int x, int y, int z, MicroVoxelSnapshot snapshot) in world.SnapshotMicroVoxels())
        {
            float inverse = 1f / snapshot.Resolution;
            foreach (MicroVoxelBox box in snapshot.Boxes)
            {
                if (world.IsFluidMaterial(box.MaterialId)) continue;
                Vector3 minimum = new(x + box.MinX * inverse, y + box.MinY * inverse, z + box.MinZ * inverse);
                Vector3 maximum = new(x + box.MaxX * inverse, y + box.MaxY * inverse, z + box.MaxZ * inverse);
                if (!IntersectAabb(origin, direction, minimum, maximum, maxDistance, out float distance, out Vector3 normal)) continue;
                if (nearest.HasValue && nearest.Value.Distance <= distance) continue;
                nearest = new MicroVoxelHit(x, y, z, origin + direction * distance, normal, distance, box.MaterialId);
            }
        }
        return nearest;
    }

    private static bool IntersectAabb(
        Vector3 origin,
        Vector3 direction,
        Vector3 minimum,
        Vector3 maximum,
        float maxDistance,
        out float distance,
        out Vector3 normal)
    {
        float near = 0f;
        float far = maxDistance;
        normal = Vector3.Zero;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float min = axis == 0 ? minimum.X : axis == 1 ? minimum.Y : minimum.Z;
            float max = axis == 0 ? maximum.X : axis == 1 ? maximum.Y : maximum.Z;
            if (MathF.Abs(d) < 0.000001f)
            {
                if (o < min || o > max) { distance = 0f; return false; }
                continue;
            }
            float first = (min - o) / d;
            float second = (max - o) / d;
            float sign = -MathF.Sign(d);
            if (first > second)
            {
                (first, second) = (second, first);
            }
            if (first > near)
            {
                near = first;
                normal = axis == 0 ? new Vector3(sign, 0f, 0f) : axis == 1 ? new Vector3(0f, sign, 0f) : new Vector3(0f, 0f, sign);
            }
            far = MathF.Min(far, second);
            if (near > far) { distance = 0f; return false; }
        }
        distance = near;
        return near >= 0f && near <= maxDistance;
    }
}
