using System.Numerics;

namespace RTXSandbox.World;

public readonly record struct PhysicsGridElement(
    int CellX,
    int CellY,
    int CellZ,
    Vector3 Minimum,
    Vector3 Maximum,
    ushort MaterialId)
{
    public Vector3 Center => (Minimum + Maximum) * 0.5f;
    public Vector3 HalfExtents => (Maximum - Minimum) * 0.5f;
    public float Volume
    {
        get
        {
            Vector3 size = Maximum - Minimum;
            return size.X * size.Y * size.Z;
        }
    }
}

public readonly record struct PhysicsGridRayHit(
    ulong GridId,
    int CellX,
    int CellY,
    int CellZ,
    ushort MaterialId,
    Vector3 Position,
    Vector3 Normal,
    float Distance);

/// <summary>
/// A voxel structure whose cells remain on an integer local lattice while the lattice itself
/// moves continuously in world space.
/// </summary>
public sealed class PhysicsGrid
{
    private readonly List<PhysicsGridElement> _elements;
    private readonly List<PhysicsGridElement> _collisionElements = new();
    private readonly Dictionary<(int X, int Y, int Z), List<int>> _cellElements = new();
    private readonly Func<ushort, float> _density;

    public ulong Id { get; }
    public IReadOnlyList<PhysicsGridElement> Elements => _elements;
    public IReadOnlyList<PhysicsGridElement> CollisionElements => _collisionElements;
    public int TopologyVersion { get; private set; } = 1;
    public Vector3 Position { get; internal set; }
    public Quaternion Orientation { get; internal set; } = Quaternion.Identity;
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
    public Vector3 CenterOfMassLocal { get; private set; }
    public float Mass { get; private set; }
    public Vector3 LocalInertia { get; private set; }
    public float BoundingRadius { get; private set; }
    public bool IsSleeping { get; internal set; }
    public float SleepTimer { get; internal set; }
    public bool FractureOnImpact { get; set; }
    public float ImpactCooldown { get; internal set; }

    public PhysicsGrid(
        ulong id,
        IEnumerable<PhysicsGridElement> elements,
        Vector3 worldOrigin,
        Func<ushort, float>? density = null)
    {
        Id = id;
        _elements = elements?.ToList() ?? throw new ArgumentNullException(nameof(elements));
        if (_elements.Count == 0) throw new ArgumentException("A physics grid needs geometry.", nameof(elements));
        _density = density ?? (_ => 1f);
        RebuildCellIndex();
        RecalculateMass();
        Position = worldOrigin + CenterOfMassLocal;
    }

    public Vector3 LocalToWorld(Vector3 local) =>
        Position + Vector3.Transform(local - CenterOfMassLocal, Orientation);

    public Vector3 WorldToLocal(Vector3 world) =>
        CenterOfMassLocal + Vector3.Transform(world - Position, Quaternion.Inverse(Orientation));

    public Vector3 LocalDirectionToWorld(Vector3 localDirection) =>
        Vector3.Transform(localDirection, Orientation);

    public bool ContainsCell(int x, int y, int z) => _cellElements.ContainsKey((x, y, z));

    public bool TryGetFullCellMaterial(int x, int y, int z, out ushort materialId)
    {
        materialId = 0;
        if (!_cellElements.TryGetValue((x, y, z), out List<int>? indices) || indices.Count != 1) return false;
        PhysicsGridElement element = _elements[indices[0]];
        Vector3 expectedMinimum = new(x, y, z);
        if (Vector3.DistanceSquared(element.Minimum, expectedMinimum) > 0.000001f ||
            Vector3.DistanceSquared(element.Maximum, expectedMinimum + Vector3.One) > 0.000001f) return false;
        materialId = element.MaterialId;
        return true;
    }

    public IReadOnlyCollection<(int X, int Y, int Z)> Cells => _cellElements.Keys;

    public bool TryRemoveCell(int x, int y, int z)
    {
        if (!_cellElements.ContainsKey((x, y, z))) return false;
        Vector3 oldCenter = CenterOfMassLocal;
        _elements.RemoveAll(element => element.CellX == x && element.CellY == y && element.CellZ == z);
        TopologyVersion++;
        if (_elements.Count == 0)
        {
            _cellElements.Clear();
            return true;
        }
        RebuildCellIndex();
        RecalculateMass();
        Position += Vector3.Transform(CenterOfMassLocal - oldCenter, Orientation);
        Wake();
        return true;
    }

    public bool TryAddFullCell(int x, int y, int z, ushort materialId)
    {
        if (ContainsCell(x, y, z) || materialId == 0) return false;
        Vector3 oldCenter = CenterOfMassLocal;
        _elements.Add(new PhysicsGridElement(
            x, y, z,
            new Vector3(x, y, z),
            new Vector3(x + 1f, y + 1f, z + 1f),
            materialId));
        TopologyVersion++;
        RebuildCellIndex();
        RecalculateMass();
        Position += Vector3.Transform(CenterOfMassLocal - oldCenter, Orientation);
        Wake();
        return true;
    }

    public void Wake()
    {
        IsSleeping = false;
        SleepTimer = 0f;
    }

    internal float DensityOf(ushort materialId) => _density(materialId);

    public Vector3 VelocityAtPoint(Vector3 worldPoint) =>
        LinearVelocity + Vector3.Cross(AngularVelocity, worldPoint - Position);

    public Vector3 ApplyInverseInertiaWorld(Vector3 worldTorque)
    {
        Quaternion inverse = Quaternion.Inverse(Orientation);
        Vector3 local = Vector3.Transform(worldTorque, inverse);
        local = new Vector3(
            local.X / MathF.Max(LocalInertia.X, 0.0001f),
            local.Y / MathF.Max(LocalInertia.Y, 0.0001f),
            local.Z / MathF.Max(LocalInertia.Z, 0.0001f));
        return Vector3.Transform(local, Orientation);
    }

    public void ApplyImpulseAtPoint(Vector3 impulse, Vector3 worldPoint)
    {
        LinearVelocity += impulse / Mass;
        AngularVelocity += ApplyInverseInertiaWorld(Vector3.Cross(worldPoint - Position, impulse));
        Wake();
    }

    internal void SetTransformAndVelocity(
        Vector3 position,
        Quaternion orientation,
        Vector3 linearVelocity,
        Vector3 angularVelocity)
    {
        Position = position;
        Orientation = Quaternion.Normalize(orientation);
        LinearVelocity = linearVelocity;
        AngularVelocity = angularVelocity;
        Wake();
    }

    internal bool SetPhysicsState(
        Vector3 position,
        Quaternion orientation,
        Vector3 linearVelocity,
        Vector3 angularVelocity,
        bool sleeping)
    {
        orientation = Quaternion.Normalize(orientation);
        bool transformChanged = Vector3.DistanceSquared(Position, position) > 0.0000000001f ||
                                1f - MathF.Abs(Quaternion.Dot(Orientation, orientation)) > 0.0000001f;
        Position = position;
        Orientation = orientation;
        LinearVelocity = linearVelocity;
        AngularVelocity = angularVelocity;
        IsSleeping = sleeping;
        if (!sleeping) SleepTimer = 0f;
        return transformChanged;
    }

    public PhysicsGridRayHit? Raycast(Vector3 origin, Vector3 direction, float maximumDistance)
    {
        if (direction.LengthSquared() < 0.000001f) return null;
        direction = Vector3.Normalize(direction);
        Quaternion inverse = Quaternion.Inverse(Orientation);
        Vector3 localOrigin = WorldToLocal(origin);
        Vector3 localDirection = Vector3.Transform(direction, inverse);
        float closest = maximumDistance;
        PhysicsGridRayHit? result = null;
        foreach (PhysicsGridElement element in _elements)
        {
            if (!IntersectAabb(localOrigin, localDirection, element.Minimum, element.Maximum, closest,
                    out float distance, out Vector3 localNormal)) continue;
            closest = distance;
            result = new PhysicsGridRayHit(
                Id,
                element.CellX,
                element.CellY,
                element.CellZ,
                element.MaterialId,
                origin + direction * distance,
                Vector3.Normalize(LocalDirectionToWorld(localNormal)),
                distance);
        }
        return result;
    }

    public (Vector3 Minimum, Vector3 Maximum) CalculateWorldBounds()
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (PhysicsGridElement element in _elements)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new(
                    (corner & 1) == 0 ? element.Minimum.X : element.Maximum.X,
                    (corner & 2) == 0 ? element.Minimum.Y : element.Maximum.Y,
                    (corner & 4) == 0 ? element.Minimum.Z : element.Maximum.Z);
                Vector3 world = LocalToWorld(local);
                minimum = Vector3.Min(minimum, world);
                maximum = Vector3.Max(maximum, world);
            }
        }
        return (minimum, maximum);
    }

    public (Vector3 Minimum, Vector3 Maximum) CalculateElementWorldBounds(PhysicsGridElement element)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new(
                (corner & 1) == 0 ? element.Minimum.X : element.Maximum.X,
                (corner & 2) == 0 ? element.Minimum.Y : element.Maximum.Y,
                (corner & 4) == 0 ? element.Minimum.Z : element.Maximum.Z);
            Vector3 world = LocalToWorld(local);
            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
        }
        return (minimum, maximum);
    }

    public bool TryCalculatePenetration(
        Vector3 axisAlignedMinimum,
        Vector3 axisAlignedMaximum,
        out Vector3 correction)
    {
        Vector3 playerCenter = (axisAlignedMinimum + axisAlignedMaximum) * 0.5f;
        Vector3 playerHalfExtents = (axisAlignedMaximum - axisAlignedMinimum) * 0.5f;
        correction = Vector3.Zero;
        float largestRequiredCorrection = 0f;

        foreach (PhysicsGridElement element in _collisionElements)
        {
            Vector3 boxCenter = LocalToWorld(element.Center);
            Vector3 boxHalfExtents = element.HalfExtents;
            if (!TryAabbObbMinimumTranslation(
                    playerCenter,
                    playerHalfExtents,
                    boxCenter,
                    boxHalfExtents,
                    Orientation,
                    out Vector3 elementCorrection)) continue;
            if (elementCorrection.LengthSquared() <= largestRequiredCorrection * largestRequiredCorrection) continue;
            correction = elementCorrection;
            largestRequiredCorrection = elementCorrection.Length();
        }
        return largestRequiredCorrection > 0f;
    }

    private static bool TryAabbObbMinimumTranslation(
        Vector3 centerA,
        Vector3 extentsA,
        Vector3 centerB,
        Vector3 extentsB,
        Quaternion orientationB,
        out Vector3 correction)
    {
        Vector3[] axesA = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        Vector3[] axesB =
        {
            Vector3.Normalize(Vector3.Transform(Vector3.UnitX, orientationB)),
            Vector3.Normalize(Vector3.Transform(Vector3.UnitY, orientationB)),
            Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, orientationB)),
        };
        Vector3 centerDelta = centerA - centerB;
        float minimumOverlap = float.PositiveInfinity;
        Vector3 minimumAxis = Vector3.Zero;

        bool TestAxis(Vector3 rawAxis)
        {
            float lengthSquared = rawAxis.LengthSquared();
            if (lengthSquared < 0.0000001f) return true;
            Vector3 axis = rawAxis / MathF.Sqrt(lengthSquared);
            float radiusA = extentsA.X * MathF.Abs(axis.X) +
                            extentsA.Y * MathF.Abs(axis.Y) +
                            extentsA.Z * MathF.Abs(axis.Z);
            float radiusB = extentsB.X * MathF.Abs(Vector3.Dot(axis, axesB[0])) +
                            extentsB.Y * MathF.Abs(Vector3.Dot(axis, axesB[1])) +
                            extentsB.Z * MathF.Abs(Vector3.Dot(axis, axesB[2]));
            float signedDistance = Vector3.Dot(centerDelta, axis);
            float overlap = radiusA + radiusB - MathF.Abs(signedDistance);
            if (overlap <= 0f) return false;
            if (overlap < minimumOverlap)
            {
                minimumOverlap = overlap;
                minimumAxis = signedDistance < 0f ? -axis : axis;
            }
            return true;
        }

        foreach (Vector3 axis in axesA)
            if (!TestAxis(axis)) { correction = Vector3.Zero; return false; }
        foreach (Vector3 axis in axesB)
            if (!TestAxis(axis)) { correction = Vector3.Zero; return false; }
        foreach (Vector3 axisA in axesA)
        foreach (Vector3 axisB in axesB)
            if (!TestAxis(Vector3.Cross(axisA, axisB))) { correction = Vector3.Zero; return false; }

        correction = minimumAxis * (minimumOverlap + 0.0005f);
        return minimumOverlap < float.PositiveInfinity;
    }

    private static bool IntersectAabb(
        Vector3 origin,
        Vector3 direction,
        Vector3 minimum,
        Vector3 maximum,
        float maximumDistance,
        out float distance,
        out Vector3 normal)
    {
        float near = 0f;
        float far = maximumDistance;
        normal = Vector3.Zero;
        for (int axis = 0; axis < 3; axis++)
        {
            float originAxis = Component(origin, axis);
            float directionAxis = Component(direction, axis);
            if (MathF.Abs(directionAxis) < 0.000001f)
            {
                if (originAxis < Component(minimum, axis) || originAxis > Component(maximum, axis))
                {
                    distance = 0f;
                    return false;
                }
                continue;
            }
            float first = (Component(minimum, axis) - originAxis) / directionAxis;
            float second = (Component(maximum, axis) - originAxis) / directionAxis;
            float sign = -MathF.Sign(directionAxis);
            if (first > second) (first, second) = (second, first);
            if (first > near)
            {
                near = first;
                normal = axis switch
                {
                    0 => new Vector3(sign, 0f, 0f),
                    1 => new Vector3(0f, sign, 0f),
                    _ => new Vector3(0f, 0f, sign),
                };
            }
            far = MathF.Min(far, second);
            if (near > far)
            {
                distance = 0f;
                return false;
            }
        }
        distance = near;
        return distance <= maximumDistance;
    }

    private static float Component(Vector3 value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };

    private void RebuildCellIndex()
    {
        _cellElements.Clear();
        for (int index = 0; index < _elements.Count; index++)
        {
            PhysicsGridElement element = _elements[index];
            if (!_cellElements.TryGetValue((element.CellX, element.CellY, element.CellZ), out List<int>? indices))
            {
                indices = new List<int>();
                _cellElements.Add((element.CellX, element.CellY, element.CellZ), indices);
            }
            indices.Add(index);
        }
        RebuildCollisionElements();
    }

    private void RebuildCollisionElements()
    {
        _collisionElements.Clear();
        foreach (PhysicsGridElement element in _elements)
        {
            // Precision geometry stays exact. Full cells fully surrounded by six other
            // cells are interior mass and can never be the first static-world contact.
            bool full = Vector3.DistanceSquared(element.Minimum, new Vector3(element.CellX, element.CellY, element.CellZ)) < 0.000001f &&
                        Vector3.DistanceSquared(element.Maximum, new Vector3(element.CellX + 1, element.CellY + 1, element.CellZ + 1)) < 0.000001f;
            if (!full ||
                !ContainsCell(element.CellX - 1, element.CellY, element.CellZ) ||
                !ContainsCell(element.CellX + 1, element.CellY, element.CellZ) ||
                !ContainsCell(element.CellX, element.CellY - 1, element.CellZ) ||
                !ContainsCell(element.CellX, element.CellY + 1, element.CellZ) ||
                !ContainsCell(element.CellX, element.CellY, element.CellZ - 1) ||
                !ContainsCell(element.CellX, element.CellY, element.CellZ + 1))
                _collisionElements.Add(element);
        }
    }

    private void RecalculateMass()
    {
        float mass = 0f;
        Vector3 weightedCenter = Vector3.Zero;
        foreach (PhysicsGridElement element in _elements)
        {
            float elementMass = MathF.Max(0.001f, element.Volume * MathF.Max(0.05f, _density(element.MaterialId)));
            mass += elementMass;
            weightedCenter += element.Center * elementMass;
        }
        Mass = MathF.Max(0.001f, mass);
        CenterOfMassLocal = weightedCenter / Mass;
        Vector3 inertia = Vector3.Zero;
        foreach (PhysicsGridElement element in _elements)
        {
            float elementMass = MathF.Max(0.001f, element.Volume * MathF.Max(0.05f, _density(element.MaterialId)));
            Vector3 size = element.Maximum - element.Minimum;
            Vector3 offset = element.Center - CenterOfMassLocal;
            inertia.X += elementMass * ((size.Y * size.Y + size.Z * size.Z) / 12f + offset.Y * offset.Y + offset.Z * offset.Z);
            inertia.Y += elementMass * ((size.X * size.X + size.Z * size.Z) / 12f + offset.X * offset.X + offset.Z * offset.Z);
            inertia.Z += elementMass * ((size.X * size.X + size.Y * size.Y) / 12f + offset.X * offset.X + offset.Y * offset.Y);
        }
        LocalInertia = Vector3.Max(inertia, new Vector3(0.01f));
        float radiusSquared = 0f;
        foreach (PhysicsGridElement element in _elements)
        {
            Vector3 extent = Vector3.Max(
                Vector3.Abs(element.Minimum - CenterOfMassLocal),
                Vector3.Abs(element.Maximum - CenterOfMassLocal));
            radiusSquared = MathF.Max(radiusSquared, extent.LengthSquared());
        }
        BoundingRadius = MathF.Sqrt(radiusSquared);
    }
}

public sealed class PhysicsGridWorld
{
    private readonly List<PhysicsGrid> _grids = new();
    private ulong _nextId = 1;

    public IReadOnlyList<PhysicsGrid> Grids => _grids;
    public int Count => _grids.Count;
    public event Action? Changed;

    public PhysicsGrid Create(
        IEnumerable<PhysicsGridElement> elements,
        Vector3 worldOrigin,
        Func<ushort, float>? density = null)
    {
        var grid = new PhysicsGrid(_nextId++, elements, worldOrigin, density);
        _grids.Add(grid);
        Changed?.Invoke();
        return grid;
    }

    public PhysicsGrid CreateFromGridElements(
        IEnumerable<PhysicsGridElement> elements,
        PhysicsGrid source)
    {
        var grid = new PhysicsGrid(_nextId++, elements, Vector3.Zero, source.DensityOf);
        Vector3 position = source.Position +
                           Vector3.Transform(grid.CenterOfMassLocal - source.CenterOfMassLocal, source.Orientation);
        grid.SetTransformAndVelocity(position, source.Orientation, source.LinearVelocity, source.AngularVelocity);
        grid.FractureOnImpact = source.FractureOnImpact;
        _grids.Add(grid);
        Changed?.Invoke();
        return grid;
    }

    public bool Remove(PhysicsGrid grid)
    {
        bool removed = _grids.Remove(grid);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Clear()
    {
        if (_grids.Count == 0) return;
        _grids.Clear();
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    public PhysicsGrid? Get(ulong id) => _grids.FirstOrDefault(grid => grid.Id == id);

    public bool TryCalculatePlayerPenetration(
        Vector3 minimum,
        Vector3 maximum,
        out Vector3 correction,
        out Vector3 surfaceVelocity)
    {
        correction = Vector3.Zero;
        surfaceVelocity = Vector3.Zero;
        float largest = 0f;
        Vector3 center = (minimum + maximum) * 0.5f;
        foreach (PhysicsGrid grid in _grids)
        {
            if (!grid.TryCalculatePenetration(minimum, maximum, out Vector3 candidate)) continue;
            float length = candidate.Length();
            if (length <= largest) continue;
            largest = length;
            correction = candidate;
            surfaceVelocity = grid.VelocityAtPoint(center);
        }
        return largest > 0f;
    }

    public PhysicsGridRayHit? Raycast(Vector3 origin, Vector3 direction, float maximumDistance)
    {
        PhysicsGridRayHit? closest = null;
        foreach (PhysicsGrid grid in _grids)
        {
            PhysicsGridRayHit? hit = grid.Raycast(origin, direction, maximumDistance);
            if (hit.HasValue && (!closest.HasValue || hit.Value.Distance < closest.Value.Distance)) closest = hit;
        }
        return closest;
    }

    public void AppendCollisionAabbs(int cellX, int cellY, int cellZ, List<VoxelAabb> destination)
    {
        Vector3 cellMinimum = new(cellX, cellY, cellZ);
        Vector3 cellMaximum = cellMinimum + Vector3.One;
        foreach (PhysicsGrid grid in _grids)
        foreach (PhysicsGridElement element in grid.Elements)
        {
            (Vector3 minimum, Vector3 maximum) = grid.CalculateElementWorldBounds(element);
            if (minimum.X >= cellMaximum.X || maximum.X <= cellMinimum.X ||
                minimum.Y >= cellMaximum.Y || maximum.Y <= cellMinimum.Y ||
                minimum.Z >= cellMaximum.Z || maximum.Z <= cellMinimum.Z) continue;
            destination.Add(new VoxelAabb(minimum, maximum, element.MaterialId));
        }
    }

    public bool TryGetPlatformVelocity(Vector3 feetPosition, out Vector3 velocity)
    {
        foreach (PhysicsGrid grid in _grids)
        foreach (PhysicsGridElement element in grid.Elements)
        {
            (Vector3 minimum, Vector3 maximum) = grid.CalculateElementWorldBounds(element);
            if (feetPosition.X < minimum.X - 0.32f || feetPosition.X > maximum.X + 0.32f ||
                feetPosition.Z < minimum.Z - 0.32f || feetPosition.Z > maximum.Z + 0.32f ||
                MathF.Abs(feetPosition.Y - maximum.Y) > 0.09f) continue;
            Vector3 radius = feetPosition - grid.Position;
            velocity = grid.LinearVelocity + Vector3.Cross(grid.AngularVelocity, radius);
            return true;
        }
        velocity = Vector3.Zero;
        return false;
    }
}
