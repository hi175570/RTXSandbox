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

public readonly record struct PhysicsGridCollisionElement(
    Vector3 Minimum,
    Vector3 Maximum,
    float Density)
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
    public float Mass => MathF.Max(0.001f, Volume * MathF.Max(0.05f, Density));
}

/// <summary>
/// Optional render skin for deformable grids. Simulation and selection remain grouped by
/// the source voxel coordinates, while renderers can draw a continuous deformable
/// voxel surface instead of exposing the individual solver particles.
/// </summary>
public readonly record struct PhysicsGridSmoothSphere(
    Vector3 Center,
    float Radius,
    ushort MaterialId,
    int CellX,
    int CellY,
    int CellZ)
{
    /// <summary>
    /// Optional deformed voxel corners in PhysicsGrid-local coordinates. The order is
    /// x + 2*y + 4*z. Soft-body renderers use these instead of inferring a surface
    /// from cell centres, which keeps a supported face pinned while the opposite face bends.
    /// </summary>
    public Vector3[]? ExplicitCorners { get; init; }

    public bool HasExplicitCorners => ExplicitCorners is { Length: 8 };

    public Vector3 GetCorner(int x, int y, int z)
    {
        if (!HasExplicitCorners)
            throw new InvalidOperationException("This deformable cell has no explicit corners.");
        return ExplicitCorners![x + y * 2 + z * 4];
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
    private readonly List<PhysicsGridCollisionElement> _collisionElements = new();
    private readonly List<PhysicsGridSmoothSphere> _smoothSpheres = new();
    private readonly Dictionary<(int X, int Y, int Z), PhysicsGridSmoothSphere> _smoothByCell = new();
    private readonly Dictionary<(int X, int Y, int Z), List<int>> _cellElements = new();
    private readonly Func<ushort, float> _density;

    internal event Action<PhysicsGrid>? TopologyChanged;

    public ulong Id { get; }
    public IReadOnlyList<PhysicsGridElement> Elements => _elements;
    public IReadOnlyList<PhysicsGridCollisionElement> CollisionElements => _collisionElements;
    public IReadOnlyList<PhysicsGridSmoothSphere> SmoothSpheres => _smoothSpheres;
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
    /// <summary>
    /// Transient load-time state. A persisted sleep flag was produced against contact
    /// manifolds that no longer exist, so the backend must wait for nearby static
    /// colliders and then revalidate the complete body under gravity once.
    /// </summary>
    internal bool RequiresSupportRevalidation { get; set; }
    public float SleepTimer { get; internal set; }
    public bool FractureOnImpact { get; set; }
    /// <summary>
    /// Zero is an intact structure. Glass shards use generations 1..3; generation 3
    /// remains physical and selectable but is no longer subdivided.  Keeping the depth
    /// on the body, rather than treating every shard as terminal, permits two deliberate
    /// follow-up fractures without an unbounded fragment explosion.
    /// </summary>
    public int FractureGeneration { get; set; }
    public bool IsDebrisFragment
    {
        get => FractureGeneration > 0;
        set
        {
            if (value && FractureGeneration == 0) FractureGeneration = 1;
            else if (!value) FractureGeneration = 0;
        }
    }
    internal int MotionVersion { get; private set; }
    /// <summary>
    /// True while the core soft-body solver owns this complete structure. BEPU must not
    /// create a second rigid body for it; selection, breaking and rendering still use
    /// this same PhysicsGrid and therefore remain block-granular.
    /// </summary>
    public bool IsDeformable { get; internal set; }
    public bool IsKinematicController { get; internal set; }
    /// <summary>Transient ownership while the creative physics gun is carrying this body.</summary>
    public bool IsPhysicsGunHeld { get; internal set; }
    /// <summary>Persistent player-authored pin. Unlike sleep, gravity cannot wake it.</summary>
    public bool IsPhysicsGunFrozen { get; internal set; }
    public bool IsStaticDeformation { get; private set; }
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
        RemoveSmoothCell(x, y, z);
        MarkTopologyChanged();
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

    public bool TryRemoveElementAtHit(PhysicsGridRayHit hit)
    {
        if (hit.GridId != Id) return false;
        Vector3 localNormal = Vector3.Normalize(Vector3.Transform(
            hit.Normal, Quaternion.Inverse(Orientation)));
        Vector3 localPoint = WorldToLocal(hit.Position) - localNormal * 0.0025f;
        int bestIndex = -1;
        float bestVolume = float.PositiveInfinity;
        for (int index = 0; index < _elements.Count; index++)
        {
            PhysicsGridElement element = _elements[index];
            if (element.CellX != hit.CellX || element.CellY != hit.CellY || element.CellZ != hit.CellZ ||
                localPoint.X < element.Minimum.X - 0.0001f || localPoint.X > element.Maximum.X + 0.0001f ||
                localPoint.Y < element.Minimum.Y - 0.0001f || localPoint.Y > element.Maximum.Y + 0.0001f ||
                localPoint.Z < element.Minimum.Z - 0.0001f || localPoint.Z > element.Maximum.Z + 0.0001f ||
                element.Volume >= bestVolume) continue;
            bestIndex = index;
            bestVolume = element.Volume;
        }
        if (bestIndex < 0) return false;

        Vector3 oldCenter = CenterOfMassLocal;
        PhysicsGridElement removed = _elements[bestIndex];
        _elements.RemoveAt(bestIndex);
        if (!_elements.Any(element => element.CellX == removed.CellX &&
                                     element.CellY == removed.CellY &&
                                     element.CellZ == removed.CellZ))
            RemoveSmoothCell(removed.CellX, removed.CellY, removed.CellZ);
        MarkTopologyChanged();
        if (_elements.Count == 0)
        {
            _cellElements.Clear();
            _collisionElements.Clear();
            return true;
        }
        RebuildCellIndex();
        RecalculateMass();
        Position += Vector3.Transform(CenterOfMassLocal - oldCenter, Orientation);
        Wake();
        return true;
    }

    /// <summary>
    /// Replaces all geometry owned by one source voxel without destroying the grid.
    /// Fracture uses this for the first child so a repeatedly struck shard keeps a
    /// continuous identity instead of disappearing for one frame while a new body is
    /// inserted. The local-to-world transform is preserved when the mass centre moves.
    /// </summary>
    public bool TryReplaceCellGeometry(
        int x,
        int y,
        int z,
        IReadOnlyList<PhysicsGridElement> replacement,
        PhysicsGridSmoothSphere? replacementSkin)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!_cellElements.ContainsKey((x, y, z)) || replacement.Count == 0 ||
            replacement.Any(element => element.CellX != x || element.CellY != y || element.CellZ != z ||
                                       element.MaterialId == 0 || !IsFinite(element.Minimum) ||
                                       !IsFinite(element.Maximum) || element.Volume <= 0.0000001f) ||
            replacementSkin is PhysicsGridSmoothSphere invalidSkin &&
            (invalidSkin.CellX != x || invalidSkin.CellY != y || invalidSkin.CellZ != z))
            return false;

        Vector3 oldCenter = CenterOfMassLocal;
        _elements.RemoveAll(element => element.CellX == x && element.CellY == y && element.CellZ == z);
        _elements.AddRange(replacement);
        RemoveSmoothCell(x, y, z);
        if (replacementSkin is PhysicsGridSmoothSphere skin)
        {
            _smoothSpheres.Add(skin);
            _smoothByCell[(x, y, z)] = skin;
        }
        RebuildCellIndex();
        RecalculateMass();
        Position += Vector3.Transform(CenterOfMassLocal - oldCenter, Orientation);
        MarkTopologyChanged();
        Wake();
        return true;
    }

    private void RemoveSmoothCell(int x, int y, int z)
    {
        _smoothSpheres.RemoveAll(sphere =>
            sphere.CellX == x && sphere.CellY == y && sphere.CellZ == z);
        _smoothByCell.Remove((x, y, z));
    }

    public bool TryAddFullCell(int x, int y, int z, ushort materialId)
    {
        if (ContainsCell(x, y, z) || materialId == 0) return false;
        // Deformation may push the visible AABB a few millimetres across its source
        // cell boundary. Building follows the persistent integer lattice, so that
        // harmless bulge must not make the second/third adjacent soft voxel overlap
        // its predecessor and get rejected.
        if (IsDeformable)
            return AppendBox(x, y, z, new Vector3(x, y, z), new Vector3(x + 1f, y + 1f, z + 1f), materialId);
        return TryAddBox(x, y, z, new Vector3(x, y, z), new Vector3(x + 1f, y + 1f, z + 1f), materialId);
    }

    /// <summary>
    /// Adds one source-lattice cell and its already continuous deformable skin as one
    /// topology transaction. Publishing the plain cube first would rebuild the solver
    /// against mismatched shared corners; a perpendicular extension could then snap the
    /// complete body back to its undeformed lattice and inject explosive velocities.
    /// </summary>
    public bool TryAddDeformableFullCell(
        int x,
        int y,
        int z,
        ushort materialId,
        IReadOnlyList<Vector3> corners)
    {
        if (!IsDeformable || ContainsCell(x, y, z) || materialId == 0 ||
            corners is null || corners.Count != 8 || corners.Any(corner => !IsFinite(corner))) return false;
        Vector3[] copiedCorners = corners.ToArray();
        Vector3 center = copiedCorners.Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / 8f;
        var skin = new PhysicsGridSmoothSphere(center, 0.5f, materialId, x, y, z)
        {
            ExplicitCorners = copiedCorners,
        };
        return AppendBox(
            x, y, z,
            new Vector3(x, y, z),
            new Vector3(x + 1f, y + 1f, z + 1f),
            materialId,
            skin);
    }

    public bool TryAddBox(
        int cellX,
        int cellY,
        int cellZ,
        Vector3 minimum,
        Vector3 maximum,
        ushort materialId)
    {
        if (materialId == 0 || !IsFinite(minimum) || !IsFinite(maximum) ||
            minimum.X < cellX - 0.00001f || minimum.Y < cellY - 0.00001f || minimum.Z < cellZ - 0.00001f ||
            maximum.X > cellX + 1.00001f || maximum.Y > cellY + 1.00001f || maximum.Z > cellZ + 1.00001f ||
            maximum.X - minimum.X <= 0.00001f || maximum.Y - minimum.Y <= 0.00001f ||
            maximum.Z - minimum.Z <= 0.00001f) return false;

        const float separationEpsilon = 0.00001f;
        foreach (PhysicsGridElement existing in _elements)
        {
            bool separated = maximum.X <= existing.Minimum.X + separationEpsilon ||
                             minimum.X >= existing.Maximum.X - separationEpsilon ||
                             maximum.Y <= existing.Minimum.Y + separationEpsilon ||
                             minimum.Y >= existing.Maximum.Y - separationEpsilon ||
                             maximum.Z <= existing.Minimum.Z + separationEpsilon ||
                             minimum.Z >= existing.Maximum.Z - separationEpsilon;
            if (!separated) return false;
        }

        return AppendBox(cellX, cellY, cellZ, minimum, maximum, materialId);
    }

    private bool AppendBox(
        int cellX,
        int cellY,
        int cellZ,
        Vector3 minimum,
        Vector3 maximum,
        ushort materialId,
        PhysicsGridSmoothSphere? smooth = null)
    {
        Vector3 oldCenter = CenterOfMassLocal;
        _elements.Add(new PhysicsGridElement(cellX, cellY, cellZ, minimum, maximum, materialId));
        RebuildCellIndex();
        RecalculateMass();
        Position += Vector3.Transform(CenterOfMassLocal - oldCenter, Orientation);
        if (smooth is PhysicsGridSmoothSphere surface)
        {
            _smoothSpheres.Add(surface);
            _smoothByCell[(cellX, cellY, cellZ)] = surface;
            foreach (Vector3 corner in surface.ExplicitCorners!)
                BoundingRadius = MathF.Max(BoundingRadius, Vector3.Distance(corner, CenterOfMassLocal));
        }
        Wake();
        // Notify only after the cell index and centre of mass agree with Elements.
        // Soft-body listeners rebuild synchronously from this event.
        MarkTopologyChanged();
        return true;
    }

    public void SetSmoothSpheres(IEnumerable<PhysicsGridSmoothSphere> spheres)
    {
        PhysicsGridSmoothSphere[] replacement = spheres?.ToArray()
            ?? throw new ArgumentNullException(nameof(spheres));
        bool changed = replacement.Length != _smoothSpheres.Count;
        if (!changed)
        {
            const float epsilonSquared = 0.0025f * 0.0025f;
            for (int index = 0; index < replacement.Length; index++)
            {
                PhysicsGridSmoothSphere oldValue = _smoothSpheres[index];
                PhysicsGridSmoothSphere newValue = replacement[index];
                if (oldValue.MaterialId == newValue.MaterialId &&
                    oldValue.CellX == newValue.CellX && oldValue.CellY == newValue.CellY &&
                    oldValue.CellZ == newValue.CellZ &&
                    MathF.Abs(oldValue.Radius - newValue.Radius) <= 0.0025f &&
                    Vector3.DistanceSquared(oldValue.Center, newValue.Center) <= epsilonSquared &&
                    CornersMatch(oldValue, newValue, epsilonSquared)) continue;
                changed = true;
                break;
            }
        }
        if (!changed) return;
        _smoothSpheres.Clear();
        _smoothSpheres.AddRange(replacement);
        _smoothByCell.Clear();
        foreach (PhysicsGridSmoothSphere sphere in replacement)
            _smoothByCell[(sphere.CellX, sphere.CellY, sphere.CellZ)] = sphere;
        MarkTopologyChanged();
        foreach (PhysicsGridSmoothSphere sphere in _smoothSpheres)
        {
            Vector3 extent = Vector3.Abs(sphere.Center - CenterOfMassLocal) + new Vector3(sphere.Radius);
            BoundingRadius = MathF.Max(BoundingRadius, extent.Length());
            if (!sphere.HasExplicitCorners) continue;
            for (int index = 0; index < 8; index++)
                BoundingRadius = MathF.Max(
                    BoundingRadius,
                    Vector3.Distance(sphere.ExplicitCorners![index], CenterOfMassLocal));
        }

        static bool CornersMatch(
            PhysicsGridSmoothSphere first,
            PhysicsGridSmoothSphere second,
            float epsilonSquared)
        {
            if (first.HasExplicitCorners != second.HasExplicitCorners) return false;
            if (!first.HasExplicitCorners) return true;
            for (int index = 0; index < 8; index++)
            {
                if (Vector3.DistanceSquared(
                        first.ExplicitCorners![index],
                        second.ExplicitCorners![index]) > epsilonSquared) return false;
            }
            return true;
        }
    }

    internal void ApplyDeformableGeometry(
        IReadOnlyList<PhysicsGridElement> elements,
        IReadOnlyList<PhysicsGridSmoothSphere> skin,
        Vector3 worldOrigin,
        Vector3 averageVelocity,
        bool sleeping)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(skin);
        if (elements.Count == 0) return;

        _elements.Clear();
        _elements.AddRange(elements);
        RebuildCellIndex();
        RecalculateMass();
        SetSmoothSpheres(skin);
        Position = worldOrigin + CenterOfMassLocal;
        Orientation = Quaternion.Identity;
        LinearVelocity = averageVelocity;
        AngularVelocity = Vector3.Zero;
        IsSleeping = sleeping;
        SleepTimer = sleeping ? 1f : 0f;
        MarkTopologyChanged();
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public void Wake()
    {
        IsSleeping = false;
        SleepTimer = 0f;
    }

    /// <summary>
    /// Keeps an impact-authored surface in the world without turning it into a falling
    /// rigid body. The grid remains raycastable, editable and collidable by the player;
    /// only continuous rigid-body integration is disabled.
    /// </summary>
    public void FreezeAsStaticDeformation()
    {
        IsStaticDeformation = true;
        IsKinematicController = true;
        LinearVelocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
        IsSleeping = true;
        SleepTimer = 1f;
    }

    internal float DensityOf(ushort materialId) => _density(materialId);

    private void MarkTopologyChanged()
    {
        TopologyVersion++;
        TopologyChanged?.Invoke(this);
    }

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
        Vector3 linearDelta = impulse / Mass;
        Vector3 angularDelta = ApplyInverseInertiaWorld(Vector3.Cross(worldPoint - Position, impulse));
        LinearVelocity += linearDelta;
        AngularVelocity += angularDelta;
        MotionVersion++;
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
        MotionVersion++;
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
            float distance;
            Vector3 localNormal;
            bool hit = _smoothByCell.TryGetValue((element.CellX, element.CellY, element.CellZ), out PhysicsGridSmoothSphere smooth) &&
                       smooth.HasExplicitCorners
                ? IntersectDeformedCell(localOrigin, localDirection, smooth, closest, out distance, out localNormal)
                : IntersectAabb(localOrigin, localDirection, element.Minimum, element.Maximum, closest,
                    out distance, out localNormal);
            if (!hit) continue;
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

    private static bool IntersectDeformedCell(
        Vector3 origin,
        Vector3 direction,
        PhysicsGridSmoothSphere cell,
        float maximumDistance,
        out float distance,
        out Vector3 normal)
    {
        ReadOnlySpan<(int A, int B, int C, int D, Vector3 Normal)> faces =
        [
            (2, 6, 7, 3, Vector3.UnitY),
            (0, 4, 6, 2, -Vector3.UnitX),
            (1, 3, 7, 5, Vector3.UnitX),
            (0, 2, 3, 1, -Vector3.UnitZ),
            (4, 5, 7, 6, Vector3.UnitZ),
            (0, 1, 5, 4, -Vector3.UnitY),
        ];
        distance = maximumDistance;
        normal = Vector3.Zero;
        bool found = false;
        foreach ((int a, int b, int c, int d, Vector3 expected) in faces)
        {
            Vector3 va = cell.GetCorner(a & 1, (a >> 1) & 1, (a >> 2) & 1);
            Vector3 vb = cell.GetCorner(b & 1, (b >> 1) & 1, (b >> 2) & 1);
            Vector3 vc = cell.GetCorner(c & 1, (c >> 1) & 1, (c >> 2) & 1);
            Vector3 vd = cell.GetCorner(d & 1, (d >> 1) & 1, (d >> 2) & 1);
            if (IntersectTriangle(origin, direction, va, vb, vc, distance, out float first) && first < distance)
            {
                Vector3 faceNormal = Vector3.Normalize(Vector3.Cross(vb - va, vc - va));
                normal = Vector3.Dot(faceNormal, expected) < 0f ? -faceNormal : faceNormal;
                distance = first;
                found = true;
            }
            if (IntersectTriangle(origin, direction, va, vc, vd, distance, out float second) && second < distance)
            {
                Vector3 faceNormal = Vector3.Normalize(Vector3.Cross(vc - va, vd - va));
                normal = Vector3.Dot(faceNormal, expected) < 0f ? -faceNormal : faceNormal;
                distance = second;
                found = true;
            }
        }
        return found;
    }

    private static bool IntersectTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        float maximumDistance,
        out float distance)
    {
        const float epsilon = 0.000001f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < epsilon) { distance = 0f; return false; }
        float inverse = 1f / determinant;
        Vector3 t = origin - a;
        float u = Vector3.Dot(t, p) * inverse;
        if (u < 0f || u > 1f) { distance = 0f; return false; }
        Vector3 q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f) { distance = 0f; return false; }
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0f && distance <= maximumDistance;
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

        // A deformable cell is not its broad-phase AABB. Treating a bent strip as a
        // stack of tall axis-aligned boxes makes the player hit an invisible vertical
        // riser while walking uphill and selects a lateral escape while walking down.
        // Prefer the authored top skin and return a purely vertical support correction;
        // the ordinary OBB path remains the fallback for walls and undersides.
        bool walkSurfaceHandled = false;
        foreach (PhysicsGridElement source in _elements)
        {
            if (!_smoothByCell.TryGetValue((source.CellX, source.CellY, source.CellZ),
                    out PhysicsGridSmoothSphere surface) || !surface.HasExplicitCorners) continue;
            if (!TryDeformedWalkSurfaceCorrection(
                    surface, axisAlignedMinimum, axisAlignedMaximum,
                    out Vector3 surfaceCorrection, out bool handledSurface))
            {
                walkSurfaceHandled |= handledSurface;
                continue;
            }
            walkSurfaceHandled = true;
            float required = surfaceCorrection.Length();
            if (required <= largestRequiredCorrection) continue;
            correction = surfaceCorrection;
            largestRequiredCorrection = required;
        }

        if (largestRequiredCorrection > 0f) return true;
        // The player is on or immediately above a valid authored upper face. Falling
        // back to the cell's broad AABB here would resurrect the invisible stair that
        // the exact surface test intentionally replaced.
        if (walkSurfaceHandled) return false;

        foreach (PhysicsGridCollisionElement element in _collisionElements)
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

    private bool TryDeformedWalkSurfaceCorrection(
        PhysicsGridSmoothSphere surface,
        Vector3 playerMinimum,
        Vector3 playerMaximum,
        out Vector3 correction,
        out bool handledSurface)
    {
        correction = Vector3.Zero;
        handledSurface = false;
        Span<Vector3> worldCorners = stackalloc Vector3[8];
        Vector3 cellMinimum = new(float.PositiveInfinity);
        Vector3 cellMaximum = new(float.NegativeInfinity);
        for (int index = 0; index < 8; index++)
        {
            Vector3 local = surface.ExplicitCorners![index];
            Vector3 world = LocalToWorld(local);
            worldCorners[index] = world;
            cellMinimum = Vector3.Min(cellMinimum, world);
            cellMaximum = Vector3.Max(cellMaximum, world);
        }
        if (playerMaximum.X <= cellMinimum.X || playerMinimum.X >= cellMaximum.X ||
            playerMaximum.Z <= cellMinimum.Z || playerMinimum.Z >= cellMaximum.Z ||
            playerMaximum.Y <= cellMinimum.Y || playerMinimum.Y >= cellMaximum.Y + 0.08f)
            return false;

        Vector3 center = (playerMinimum + playerMaximum) * 0.5f;
        float insetX = MathF.Max(0f, (playerMaximum.X - playerMinimum.X) * 0.34f);
        float insetZ = MathF.Max(0f, (playerMaximum.Z - playerMinimum.Z) * 0.34f);
        Span<Vector2> samples = stackalloc Vector2[9]
        {
            new(center.X, center.Z),
            new(center.X - insetX, center.Z), new(center.X + insetX, center.Z),
            new(center.X, center.Z - insetZ), new(center.X, center.Z + insetZ),
            new(center.X - insetX, center.Z - insetZ),
            new(center.X + insetX, center.Z - insetZ),
            new(center.X - insetX, center.Z + insetZ),
            new(center.X + insetX, center.Z + insetZ),
        };

        float highest = float.NegativeInfinity;
        foreach (Vector2 sample in samples)
        {
            if (TryTriangleHeightXZ(sample, worldCorners[2], worldCorners[6], worldCorners[7], out float first))
                highest = MathF.Max(highest, first);
            if (TryTriangleHeightXZ(sample, worldCorners[2], worldCorners[7], worldCorners[3], out float second))
                highest = MathF.Max(highest, second);
        }
        if (!float.IsFinite(highest)) return false;

        float lift = highest - playerMinimum.Y;
        // Only feet that are already close to the upper skin may step onto it. A player
        // below a falling strip is an underside collision and must be pushed sideways,
        // never teleported through the body onto its roof.
        if (lift <= 0.00001f)
        {
            handledSurface = playerMinimum.Y >= highest - 0.03f;
            return false;
        }
        if (lift > 0.46f) return false;
        handledSurface = true;
        correction = Vector3.UnitY * (lift + 0.0001f);
        return true;
    }

    private static bool TryTriangleHeightXZ(
        Vector2 point,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float height)
    {
        float denominator = (b.Z - c.Z) * (a.X - c.X) +
                            (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(denominator) < 0.000001f)
        {
            height = 0f;
            return false;
        }
        float u = ((b.Z - c.Z) * (point.X - c.X) +
                   (c.X - b.X) * (point.Y - c.Z)) / denominator;
        float v = ((c.Z - a.Z) * (point.X - c.X) +
                   (a.X - c.X) * (point.Y - c.Z)) / denominator;
        float w = 1f - u - v;
        const float epsilon = -0.0005f;
        if (u < epsilon || v < epsilon || w < epsilon)
        {
            height = 0f;
            return false;
        }
        height = a.Y * u + b.Y * v + c.Y * w;
        return true;
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
        var fullCells = new Dictionary<(int X, int Y, int Z), (PhysicsGridElement Element, float Density)>();
        foreach (PhysicsGridElement element in _elements)
        {
            bool full = Vector3.DistanceSquared(element.Minimum, new Vector3(element.CellX, element.CellY, element.CellZ)) < 0.000001f &&
                        Vector3.DistanceSquared(element.Maximum, new Vector3(element.CellX + 1, element.CellY + 1, element.CellZ + 1)) < 0.000001f;
            var cell = (element.CellX, element.CellY, element.CellZ);
            if (full && _cellElements.TryGetValue(cell, out List<int>? members) && members.Count == 1)
                fullCells[cell] = (element, MathF.Max(0.05f, _density(element.MaterialId)));
            else
                _collisionElements.Add(new PhysicsGridCollisionElement(
                    element.Minimum, element.Maximum, _density(element.MaterialId)));
        }

        // BEPU does not need one child shape per voxel. Greedily combine adjacent
        // full cells of the same physical density into exact cuboids; precision cells remain
        // untouched above. A solid 8x8x4 falling structure becomes one collision
        // child instead of a shell containing hundreds of children.
        var consumed = new HashSet<(int X, int Y, int Z)>();
        foreach (KeyValuePair<(int X, int Y, int Z), (PhysicsGridElement Element, float Density)> pair in fullCells
                     .OrderBy(entry => entry.Key.Y)
                     .ThenBy(entry => entry.Key.Z)
                     .ThenBy(entry => entry.Key.X))
        {
            (int startX, int startY, int startZ) = pair.Key;
            (int X, int Y, int Z) cell = pair.Key;
            float density = pair.Value.Density;
            if (consumed.Contains(cell)) continue;
            int width = 1;
            while (Matches(startX + width, startY, startZ, density)) width++;

            int depth = 1;
            while (true)
            {
                for (int x = 0; x < width; x++)
                    if (!Matches(startX + x, startY, startZ + depth, density)) goto DepthComplete;
                depth++;
            }
            DepthComplete:

            int height = 1;
            while (true)
            {
                for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                    if (!Matches(startX + x, startY + height, startZ + z, density)) goto HeightComplete;
                height++;
            }
            HeightComplete:

            for (int y = 0; y < height; y++)
            for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                consumed.Add((startX + x, startY + y, startZ + z));
            _collisionElements.Add(new PhysicsGridCollisionElement(
                new Vector3(startX, startY, startZ),
                new Vector3(startX + width, startY + height, startZ + depth),
                density));
        }

        bool Matches(int x, int y, int z, float density) =>
            !consumed.Contains((x, y, z)) &&
            fullCells.TryGetValue((x, y, z), out var candidate) &&
            candidate.Density == density;
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
    private readonly Dictionary<ulong, PhysicsGrid> _byId = new();
    private readonly HashSet<PhysicsGrid> _collisionGrids = new();
    private readonly Dictionary<ChunkCoordinate, HashSet<PhysicsGrid>> _virtualCollisionByChunk = new();
    private readonly Dictionary<ulong, ChunkCoordinate[]> _virtualCollisionMembership = new();
    private ulong _nextId = 1;
    private long _geometryMembershipRevision;

    public IReadOnlyList<PhysicsGrid> Grids => _grids;
    public int Count => _grids.Count;
    internal IReadOnlyCollection<PhysicsGrid> CollisionGrids => _collisionGrids;
    internal int CollisionGridCount => _collisionGrids.Count;
    internal long GeometryMembershipRevision => _geometryMembershipRevision;
    public event Action? Changed;
    public event Action<PhysicsGrid?>? TopologyChanged;

    public PhysicsGrid Create(
        IEnumerable<PhysicsGridElement> elements,
        Vector3 worldOrigin,
        Func<ushort, float>? density = null)
    {
        var grid = new PhysicsGrid(_nextId++, elements, worldOrigin, density);
        grid.TopologyChanged += OnGridTopologyChanged;
        _grids.Add(grid);
        _byId.Add(grid.Id, grid);
        _collisionGrids.Add(grid);
        _geometryMembershipRevision++;
        TopologyChanged?.Invoke(grid);
        Changed?.Invoke();
        return grid;
    }

    public PhysicsGrid CreateFromGridElements(
        IEnumerable<PhysicsGridElement> elements,
        PhysicsGrid source)
    {
        PhysicsGridElement[] elementArray = elements.ToArray();
        var grid = new PhysicsGrid(_nextId++, elementArray, Vector3.Zero, source.DensityOf);
        grid.TopologyChanged += OnGridTopologyChanged;
        Vector3 position = source.Position +
                           Vector3.Transform(grid.CenterOfMassLocal - source.CenterOfMassLocal, source.Orientation);
        grid.SetTransformAndVelocity(position, source.Orientation, source.LinearVelocity, source.AngularVelocity);
        grid.FractureOnImpact = source.FractureOnImpact;
        grid.FractureGeneration = source.FractureGeneration;
        if (source.SmoothSpheres.Count > 0)
        {
            HashSet<(int X, int Y, int Z)> cells = elementArray
                .Select(element => (element.CellX, element.CellY, element.CellZ))
                .ToHashSet();
            PhysicsGridSmoothSphere[] skin = source.SmoothSpheres
                .Where(sphere => cells.Contains((sphere.CellX, sphere.CellY, sphere.CellZ)))
                .ToArray();
            if (skin.Length > 0) grid.SetSmoothSpheres(skin);
        }
        if (source.IsStaticDeformation) grid.FreezeAsStaticDeformation();
        _grids.Add(grid);
        _byId.Add(grid.Id, grid);
        _collisionGrids.Add(grid);
        _geometryMembershipRevision++;
        TopologyChanged?.Invoke(grid);
        Changed?.Invoke();
        return grid;
    }

    public bool Remove(PhysicsGrid grid)
    {
        bool removed = _grids.Remove(grid);
        if (removed)
        {
            _byId.Remove(grid.Id);
            _collisionGrids.Remove(grid);
            RemoveVirtualCollision(grid);
            _geometryMembershipRevision++;
            grid.TopologyChanged -= OnGridTopologyChanged;
            TopologyChanged?.Invoke(grid);
            Changed?.Invoke();
        }
        return removed;
    }

    public void Clear()
    {
        if (_grids.Count == 0) return;
        foreach (PhysicsGrid grid in _grids)
            grid.TopologyChanged -= OnGridTopologyChanged;
        _grids.Clear();
        _byId.Clear();
        _collisionGrids.Clear();
        _virtualCollisionByChunk.Clear();
        _virtualCollisionMembership.Clear();
        _geometryMembershipRevision++;
        TopologyChanged?.Invoke(null);
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Signals that an unchanged grid moved between the active and sleeping render
    /// sets. This invalidates DXR membership without rebuilding its BEPU body.
    /// </summary>
    internal void NotifyGeometryMembershipChanged()
    {
        _geometryMembershipRevision++;
        Changed?.Invoke();
    }

    /// <summary>Signals a body-list or ownership change that requires physics reconciliation.</summary>
    public void NotifyTopologyChanged(PhysicsGrid? grid = null)
    {
        _geometryMembershipRevision++;
        TopologyChanged?.Invoke(grid);
        Changed?.Invoke();
    }

    private void OnGridTopologyChanged(PhysicsGrid grid)
    {
        if (_virtualCollisionMembership.ContainsKey(grid.Id))
            IndexVirtualCollision(grid);
        _geometryMembershipRevision++;
        TopologyChanged?.Invoke(grid);
        Changed?.Invoke();
    }

    public PhysicsGrid? Get(ulong id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Moves settled debris out of the full active-grid scan and into a spatial collision
    /// index. Rendering and BEPU rigid-body work can still be virtualized, but the player
    /// must continue colliding with the shard instead of walking through a visible pile.
    /// </summary>
    internal void SetCollisionVirtualized(PhysicsGrid grid, bool virtualized)
    {
        bool changed;
        if (virtualized)
        {
            changed = _collisionGrids.Remove(grid);
            if (_byId.ContainsKey(grid.Id) && !_virtualCollisionMembership.ContainsKey(grid.Id))
                changed |= IndexVirtualCollision(grid);
        }
        else
        {
            changed = RemoveVirtualCollision(grid);
            if (_byId.ContainsKey(grid.Id)) changed |= _collisionGrids.Add(grid);
        }
        if (changed) _geometryMembershipRevision++;
    }

    private bool IndexVirtualCollision(PhysicsGrid grid)
    {
        RemoveVirtualCollision(grid);
        (Vector3 minimum, Vector3 maximum) = grid.CalculateWorldBounds();
        ChunkCoordinate first = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(minimum.X), (int)MathF.Floor(minimum.Z));
        ChunkCoordinate last = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(maximum.X - 0.0001f), (int)MathF.Floor(maximum.Z - 0.0001f));
        var membership = new List<ChunkCoordinate>();
        for (int z = first.Z; z <= last.Z; z++)
        for (int x = first.X; x <= last.X; x++)
        {
            var chunk = new ChunkCoordinate(x, z);
            if (!_virtualCollisionByChunk.TryGetValue(chunk, out HashSet<PhysicsGrid>? grids))
            {
                grids = new HashSet<PhysicsGrid>();
                _virtualCollisionByChunk.Add(chunk, grids);
            }
            grids.Add(grid);
            membership.Add(chunk);
        }
        _virtualCollisionMembership[grid.Id] = membership.ToArray();
        return true;
    }

    private bool RemoveVirtualCollision(PhysicsGrid grid)
    {
        if (!_virtualCollisionMembership.Remove(grid.Id, out ChunkCoordinate[]? membership)) return false;
        foreach (ChunkCoordinate chunk in membership)
        {
            if (!_virtualCollisionByChunk.TryGetValue(chunk, out HashSet<PhysicsGrid>? grids)) continue;
            grids.Remove(grid);
            if (grids.Count == 0) _virtualCollisionByChunk.Remove(chunk);
        }
        return true;
    }

    private IEnumerable<PhysicsGrid> VirtualCollisionCandidates(Vector3 minimum, Vector3 maximum)
    {
        ChunkCoordinate first = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(minimum.X), (int)MathF.Floor(minimum.Z));
        ChunkCoordinate last = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(maximum.X - 0.0001f), (int)MathF.Floor(maximum.Z - 0.0001f));
        HashSet<ulong>? emitted = first == last ? null : new HashSet<ulong>();
        for (int z = first.Z; z <= last.Z; z++)
        for (int x = first.X; x <= last.X; x++)
        {
            if (!_virtualCollisionByChunk.TryGetValue(new ChunkCoordinate(x, z),
                    out HashSet<PhysicsGrid>? grids)) continue;
            foreach (PhysicsGrid grid in grids)
                if (emitted is null || emitted.Add(grid.Id)) yield return grid;
        }
    }

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
        foreach (PhysicsGrid grid in _collisionGrids)
        {
            if (!grid.TryCalculatePenetration(minimum, maximum, out Vector3 candidate)) continue;
            float length = candidate.Length();
            if (length <= largest) continue;
            largest = length;
            correction = candidate;
            surfaceVelocity = grid.VelocityAtPoint(center);
        }
        foreach (PhysicsGrid grid in VirtualCollisionCandidates(minimum, maximum))
        {
            if (!grid.TryCalculatePenetration(minimum, maximum, out Vector3 candidate)) continue;
            float length = candidate.Length();
            if (length <= largest) continue;
            largest = length;
            correction = candidate;
            surfaceVelocity = Vector3.Zero;
        }
        return largest > 0f;
    }

    public PhysicsGridRayHit? Raycast(Vector3 origin, Vector3 direction, float maximumDistance)
    {
        PhysicsGridRayHit? closest = null;
        // Selection/energy hits must still see virtualised sleeping shards so one
        // struck piece can be promoted back into real rigid-body simulation.
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
        foreach (PhysicsGrid grid in _collisionGrids)
        foreach (PhysicsGridElement element in grid.Elements)
        {
            (Vector3 minimum, Vector3 maximum) = grid.CalculateElementWorldBounds(element);
            if (minimum.X >= cellMaximum.X || maximum.X <= cellMinimum.X ||
                minimum.Y >= cellMaximum.Y || maximum.Y <= cellMinimum.Y ||
                minimum.Z >= cellMaximum.Z || maximum.Z <= cellMinimum.Z) continue;
            destination.Add(new VoxelAabb(minimum, maximum, element.MaterialId));
        }
        foreach (PhysicsGrid grid in VirtualCollisionCandidates(cellMinimum, cellMaximum))
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
        foreach (PhysicsGrid grid in _collisionGrids)
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
        Vector3 platformMinimum = feetPosition - new Vector3(0.32f, 0.10f, 0.32f);
        Vector3 platformMaximum = feetPosition + new Vector3(0.32f, 0.10f, 0.32f);
        foreach (PhysicsGrid grid in VirtualCollisionCandidates(platformMinimum, platformMaximum))
        foreach (PhysicsGridElement element in grid.Elements)
        {
            (Vector3 minimum, Vector3 maximum) = grid.CalculateElementWorldBounds(element);
            if (feetPosition.X < minimum.X - 0.32f || feetPosition.X > maximum.X + 0.32f ||
                feetPosition.Z < minimum.Z - 0.32f || feetPosition.Z > maximum.Z + 0.32f ||
                MathF.Abs(feetPosition.Y - maximum.Y) > 0.09f) continue;
            velocity = Vector3.Zero;
            return true;
        }
        velocity = Vector3.Zero;
        return false;
    }
}
