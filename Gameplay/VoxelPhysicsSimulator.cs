using System.Numerics;
using RTXSandbox.Configuration;
using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

/// <summary>
/// Event-driven six-face structural fracture and continuous local-grid rigid-body simulation.
/// Static voxels never move one cell at a time: unsupported components are extracted into a
/// PhysicsGrid and the complete grid receives continuous translation and rotation.
/// </summary>
public sealed class VoxelPhysicsSimulator : IVoxelPhysicsService, IDisposable
{
    private static readonly VoxelPoint[] FaceNeighbors =
    {
        // Down first makes the common "is this connected to the ground?" query cheap.
        new(0, -1, 0), new(1, 0, 0), new(-1, 0, 0),
        new(0, 0, 1), new(0, 0, -1), new(0, 1, 0),
    };

    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly Queue<VoxelPoint> _dirty = new();
    private readonly HashSet<int> _queued = new();
    private bool _suppressWorldEvents;
    private float _analysisAccumulator;
    private Vector3? _lastRemovedSupportCenter;
    private BepuPhysicsBackend? _rigidBodyBackend;

    public bool Enabled { get; set; } = true;
    public bool ImpactFractureEnabled { get; set; }
    public float ImpactFractureSpeed { get; set; } = 9f;
    public int ChangesLastStep { get; private set; }
    public int BroadphasePairsLastStep { get; private set; }
    public int NarrowphasePairsLastStep { get; private set; }
    public int SimulationSubstepsLastStep { get; private set; } = 1;
    public int ActiveGridCount => _world.PhysicsGrids.Count;
    public ulong SelectedGridId { get; private set; }
    public string Status { get; private set; } = "Grid physics ready";
    public string ModeName => Enabled ? "StructuralGrid" : "Disabled";
    public string BackendName => _rigidBodyBackend?.Name ?? "BEPUphysics 2";

    // Source compatibility for older callers. The old Off/Simple/Full voxel-step modes no
    // longer control the simulation; structural grid physics is the only implementation.
    public PhysicsMode Mode
    {
        get => PhysicsMode.Full;
        set { }
    }

    public VoxelPhysicsSimulator(VoxelWorld world, BlockRegistry blocks, PhysicsMode ignoredMode = PhysicsMode.Off)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        world.DetailedVoxelChanged += OnVoxelChanged;
        world.WorldReloaded += OnWorldReloaded;
        world.MicroVoxelsChanged += OnMicroVoxelsChanged;
    }

    public void RequestEvaluation()
    {
        // Evaluate exposed cells instead of blindly moving the complete generated terrain.
        for (int z = 0; z < VoxelWorld.Depth; z++)
        for (int y = 1; y < VoxelWorld.Height; y++)
        for (int x = 0; x < VoxelWorld.Width; x++)
        {
            if (IsStructuralAt(x, y, z) && HasEmptyFace(x, y, z)) Enqueue(x, y, z);
        }
    }

    public bool TrySetMode(string modeName)
    {
        if (string.Equals(modeName, "Disabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modeName, "Off", StringComparison.OrdinalIgnoreCase))
        {
            Enabled = false;
            return true;
        }
        if (string.Equals(modeName, "StructuralGrid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modeName, "On", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modeName, "Full", StringComparison.OrdinalIgnoreCase))
        {
            Enabled = true;
            return true;
        }
        return false;
    }

    public void Update(float deltaSeconds)
    {
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.05f);
        ChangesLastStep = 0;
        if (!Enabled)
        {
            Status = "Grid physics disabled";
            return;
        }

        _analysisAccumulator += deltaSeconds;
        if (_analysisAccumulator >= 0.035f)
        {
            _analysisAccumulator = 0f;
            AnalyzeDirtyComponents(maximumSeeds: 6);
        }

        BroadphasePairsLastStep = 0;
        NarrowphasePairsLastStep = 0;
        if (_world.PhysicsGrids.Count > 0 || _rigidBodyBackend is not null)
        {
            _rigidBodyBackend ??= new BepuPhysicsBackend(_world);
            _rigidBodyBackend.Step(deltaSeconds);
            SimulationSubstepsLastStep = _rigidBodyBackend.CollisionStepsLastFrame;
            Status = $"BEPUphysics 2: {_rigidBodyBackend.DynamicBodyCount} bodies, " +
                     $"{_rigidBodyBackend.StaticChunkBodyCount} static chunks, {SimulationSubstepsLastStep} collision steps";
        }
        else
        {
            SimulationSubstepsLastStep = 1;
            Status = "BEPUphysics 2 ready";
        }
    }

    public int AnalyzeNow()
    {
        ChangesLastStep = 0;
        AnalyzeDirtyComponents(int.MaxValue);
        return ChangesLastStep;
    }

    public bool TryRemoveGridCell(ulong gridId, int localX, int localY, int localZ)
    {
        PhysicsGrid? grid = _world.PhysicsGrids.Get(gridId);
        if (grid is null || !grid.TryRemoveCell(localX, localY, localZ)) return false;
        if (grid.Elements.Count == 0)
        {
            _world.PhysicsGrids.Remove(grid);
            return true;
        }
        SplitDisconnectedGrid(grid);
        _world.PhysicsGrids.NotifyChanged();
        return true;
    }

    public bool TryPlaceOnGrid(PhysicsGridRayHit hit, ushort materialId)
    {
        PhysicsGrid? grid = _world.PhysicsGrids.Get(hit.GridId);
        if (grid is null || materialId == 0 || !_blocks.Get(materialId).IsPlaceable) return false;
        Vector3 localNormal = Vector3.Transform(hit.Normal, Quaternion.Inverse(grid.Orientation));
        VoxelPoint face = CardinalDirection(localNormal);
        bool added = grid.TryAddFullCell(
            hit.CellX + face.X,
            hit.CellY + face.Y,
            hit.CellZ + face.Z,
            materialId);
        if (added) _world.PhysicsGrids.NotifyChanged();
        return added;
    }

    public bool SelectGrid(ulong gridId)
    {
        if (gridId != 0 && _world.PhysicsGrids.Get(gridId) is null) return false;
        SelectedGridId = gridId;
        Status = gridId == 0 ? "Grid tool target: world" : $"Grid tool target: Grid {gridId}";
        return true;
    }

    /// <summary>
    /// Moves one full voxel to another lattice. This is deliberately strict: the target
    /// lattice must coincide with the cell centre and already touch the destination on one
    /// of its six faces. Rotated or fractional overlaps are rejected instead of snapping.
    /// </summary>
    public bool TryTransferCell(VoxelHit hit)
    {
        ulong sourceId = hit.GridId;
        if (sourceId == SelectedGridId) return false;

        PhysicsGrid? source = sourceId == 0 ? null : _world.PhysicsGrids.Get(sourceId);
        ushort material;
        Vector3 worldCenter;
        if (source is null)
        {
            if (sourceId != 0 || !_world.IsInside(hit.X, hit.Y, hit.Z)) return false;
            material = _world.GetBlockId(hit.X, hit.Y, hit.Z);
            if (!IsStructural(material) || _blocks.Get(material).IsUnbreakable) return false;
            worldCenter = new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f);
        }
        else
        {
            if (!source.TryGetFullCellMaterial(hit.X, hit.Y, hit.Z, out material)) return false;
            worldCenter = source.LocalToWorld(new Vector3(hit.X + 0.5f, hit.Y + 0.5f, hit.Z + 0.5f));
        }

        PhysicsGrid? target = SelectedGridId == 0 ? null : _world.PhysicsGrids.Get(SelectedGridId);
        if (SelectedGridId != 0 && target is null) return false;
        Vector3 targetLocal = target?.WorldToLocal(worldCenter) ?? worldCenter;
        int targetX = (int)MathF.Floor(targetLocal.X);
        int targetY = (int)MathF.Floor(targetLocal.Y);
        int targetZ = (int)MathF.Floor(targetLocal.Z);
        Vector3 snappedLocal = new(targetX + 0.5f, targetY + 0.5f, targetZ + 0.5f);
        Vector3 snappedWorld = target?.LocalToWorld(snappedLocal) ?? snappedLocal;
        if (Vector3.DistanceSquared(snappedWorld, worldCenter) > 0.0009f) return RejectTransfer("lattices are not aligned");

        bool destinationFree = target is null
            ? _world.IsInside(targetX, targetY, targetZ) && !IsStructuralAt(targetX, targetY, targetZ)
            : !target.ContainsCell(targetX, targetY, targetZ);
        if (!destinationFree) return RejectTransfer("destination is occupied");
        bool touchesTarget = FaceNeighbors.Any(offset => target is null
            ? IsStructuralAt(targetX + offset.X, targetY + offset.Y, targetZ + offset.Z)
            : target.ContainsCell(targetX + offset.X, targetY + offset.Y, targetZ + offset.Z));
        if (!touchesTarget) return RejectTransfer("destination must share a face with its target Grid");

        bool added;
        if (target is null)
        {
            _suppressWorldEvents = true;
            try { added = _world.SetBlockId(targetX, targetY, targetZ, material); }
            finally { _suppressWorldEvents = false; }
        }
        else added = target.TryAddFullCell(targetX, targetY, targetZ, material);
        if (!added) return false;

        bool removed;
        if (source is null)
        {
            _suppressWorldEvents = true;
            try { removed = _world.SetBlockId(hit.X, hit.Y, hit.Z, 0); }
            finally { _suppressWorldEvents = false; }
        }
        else removed = TryRemoveGridCell(source.Id, hit.X, hit.Y, hit.Z);

        if (!removed)
        {
            if (target is null) _world.SetBlockId(targetX, targetY, targetZ, 0);
            else target.TryRemoveCell(targetX, targetY, targetZ);
            return false;
        }
        _world.PhysicsGrids.NotifyChanged();
        Status = $"Moved cell from {(sourceId == 0 ? "world" : $"Grid {sourceId}")} to " +
                 (SelectedGridId == 0 ? "world" : $"Grid {SelectedGridId}");
        return true;
    }

    private bool RejectTransfer(string reason)
    {
        Status = $"Grid transfer rejected: {reason}";
        return false;
    }

    private void OnVoxelChanged(VoxelChangedEvent change)
    {
        if (_suppressWorldEvents) return;
        if (IsStructural(change.OldBlockId) && !IsStructural(change.NewBlockId))
            _lastRemovedSupportCenter = new Vector3(change.X + 0.5f, change.Y + 0.5f, change.Z + 0.5f);
        Enqueue(change.X, change.Y, change.Z);
        foreach (VoxelPoint offset in FaceNeighbors)
            Enqueue(change.X + offset.X, change.Y + offset.Y, change.Z + offset.Z);
    }

    private void OnMicroVoxelsChanged()
    {
        // Detailed cell coordinates are not part of the legacy event. Ordinary precision
        // edits also change the containing regular cell in the current editor path; explicit
        // callers can use RequestEvaluation after batch micro edits.
    }

    private void OnWorldReloaded()
    {
        _dirty.Clear();
        _queued.Clear();
        _world.PhysicsGrids.Clear();
    }

    private void Enqueue(int x, int y, int z)
    {
        if (!_world.IsInside(x, y, z) || !IsStructuralAt(x, y, z)) return;
        int index = Index(x, y, z);
        if (_queued.Add(index)) _dirty.Enqueue(new VoxelPoint(x, y, z));
    }

    private void AnalyzeDirtyComponents(int maximumSeeds)
    {
        var analyzed = new HashSet<int>();
        int seeds = 0;
        while (seeds < maximumSeeds && _dirty.TryDequeue(out VoxelPoint seed))
        {
            _queued.Remove(Index(seed.X, seed.Y, seed.Z));
            seeds++;
            if (!IsStructuralAt(seed.X, seed.Y, seed.Z) || analyzed.Contains(Index(seed.X, seed.Y, seed.Z))) continue;
            List<VoxelPoint> component = FloodComponent(seed, analyzed, out bool anchored);
            if (!anchored && component.Count > 0) ExtractComponent(component);
        }
    }

    private List<VoxelPoint> FloodComponent(VoxelPoint seed, HashSet<int> analyzed, out bool anchored)
    {
        const int maximumAnalyzedCells = VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth;
        var component = new List<VoxelPoint>();
        var queue = new Queue<VoxelPoint>();
        queue.Enqueue(seed);
        analyzed.Add(Index(seed.X, seed.Y, seed.Z));
        anchored = false;

        while (queue.TryDequeue(out VoxelPoint point))
        {
            component.Add(point);
            if (IsAnchor(point.X, point.Y, point.Z))
            {
                anchored = true;
                break;
            }
            if (component.Count >= maximumAnalyzedCells)
            {
                anchored = true;
                break;
            }

            foreach (VoxelPoint offset in FaceNeighbors)
            {
                int x = point.X + offset.X;
                int y = point.Y + offset.Y;
                int z = point.Z + offset.Z;
                if (!_world.IsInside(x, y, z) || !IsStructuralAt(x, y, z)) continue;
                int index = Index(x, y, z);
                if (analyzed.Contains(index) || !CellsShareFace(point, new VoxelPoint(x, y, z), offset)) continue;
                analyzed.Add(index);
                queue.Enqueue(new VoxelPoint(x, y, z));
            }
        }
        return component;
    }

    private bool IsAnchor(int x, int y, int z)
    {
        if (y == 0) return true;
        ushort regular = _world.GetBlockId(x, y, z);
        if (regular != 0 && _blocks.Get(regular).IsUnbreakable) return true;
        return _world.TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? micro) &&
               micro!.Boxes.Any(box => _blocks.Get(box.MaterialId).IsUnbreakable);
    }

    private bool CellsShareFace(VoxelPoint first, VoxelPoint second, VoxelPoint direction)
    {
        ushort firstRegular = _world.GetBlockId(first.X, first.Y, first.Z);
        ushort secondRegular = _world.GetBlockId(second.X, second.Y, second.Z);
        bool firstFull = IsStructural(firstRegular);
        bool secondFull = IsStructural(secondRegular);
        if (firstFull && secondFull) return true;

        List<FaceRect> firstFaces = GetFaceRects(first, direction);
        if (firstFaces.Count == 0) return false;
        List<FaceRect> secondFaces = GetFaceRects(second, new VoxelPoint(-direction.X, -direction.Y, -direction.Z));
        if (secondFaces.Count == 0) return false;
        if (firstFull || secondFull) return true;
        foreach (FaceRect a in firstFaces)
        foreach (FaceRect b in secondFaces)
        {
            if (IntervalsOverlap(a.MinU, a.MaxU, a.Resolution, b.MinU, b.MaxU, b.Resolution) &&
                IntervalsOverlap(a.MinV, a.MaxV, a.Resolution, b.MinV, b.MaxV, b.Resolution)) return true;
        }
        return false;
    }

    private List<FaceRect> GetFaceRects(VoxelPoint point, VoxelPoint direction)
    {
        ushort regular = _world.GetBlockId(point.X, point.Y, point.Z);
        if (IsStructural(regular)) return new List<FaceRect> { new(0, 1, 0, 1, 1) };
        var rectangles = new List<FaceRect>();
        if (!_world.TryGetMicroVoxels(point.X, point.Y, point.Z, out MicroVoxelContainer? micro)) return rectangles;
        int resolution = micro!.Resolution;
        foreach (MicroVoxelBox box in micro.Boxes)
        {
            if (!_blocks.Get(box.MaterialId).IsCollidable || _blocks.Get(box.MaterialId).IsFluid) continue;
            bool touches = direction switch
            {
                { X: 1 } => box.MaxX == resolution,
                { X: -1 } => box.MinX == 0,
                { Y: 1 } => box.MaxY == resolution,
                { Y: -1 } => box.MinY == 0,
                { Z: 1 } => box.MaxZ == resolution,
                _ => box.MinZ == 0,
            };
            if (!touches) continue;
            rectangles.Add(direction.X != 0
                ? new FaceRect(box.MinY, box.MaxY, box.MinZ, box.MaxZ, resolution)
                : direction.Y != 0
                    ? new FaceRect(box.MinX, box.MaxX, box.MinZ, box.MaxZ, resolution)
                    : new FaceRect(box.MinX, box.MaxX, box.MinY, box.MaxY, resolution));
        }
        return rectangles;
    }

    private void ExtractComponent(List<VoxelPoint> component)
    {
        int minX = component.Min(point => point.X);
        int minY = component.Min(point => point.Y);
        int minZ = component.Min(point => point.Z);
        var elements = new List<PhysicsGridElement>();
        foreach (VoxelPoint point in component)
        {
            int localX = point.X - minX;
            int localY = point.Y - minY;
            int localZ = point.Z - minZ;
            ushort regular = _world.GetBlockId(point.X, point.Y, point.Z);
            if (regular != 0)
            {
                elements.Add(new PhysicsGridElement(
                    localX, localY, localZ,
                    new Vector3(localX, localY, localZ),
                    new Vector3(localX + 1f, localY + 1f, localZ + 1f),
                    regular));
            }
            else if (_world.TryGetMicroVoxels(point.X, point.Y, point.Z, out MicroVoxelContainer? micro))
            {
                float inverse = 1f / micro!.Resolution;
                foreach (MicroVoxelBox box in micro.Boxes)
                {
                    elements.Add(new PhysicsGridElement(
                        localX, localY, localZ,
                        new Vector3(localX + box.MinX * inverse, localY + box.MinY * inverse, localZ + box.MinZ * inverse),
                        new Vector3(localX + box.MaxX * inverse, localY + box.MaxY * inverse, localZ + box.MaxZ * inverse),
                        box.MaterialId));
                }
            }
        }
        if (elements.Count == 0) return;

        _suppressWorldEvents = true;
        try
        {
            foreach (VoxelPoint point in component) _world.SetBlockId(point.X, point.Y, point.Z, 0);
        }
        finally
        {
            _suppressWorldEvents = false;
        }

        // Until block definitions expose explicit physical density, use volume density.
        // Rendering metallic/roughness values are not mass and previously displaced COM.
        PhysicsGrid grid = _world.PhysicsGrids.Create(elements, new Vector3(minX, minY, minZ));
        if (_lastRemovedSupportCenter.HasValue)
        {
            Vector3 lever = grid.Position - _lastRemovedSupportCenter.Value;
            Vector3 separationImpulse = new(0f, -grid.Mass * 0.32f, 0f);
            grid.ApplyImpulseAtPoint(separationImpulse, _lastRemovedSupportCenter.Value);
            // Perfectly symmetric structures have no physical torque; retain that result.
            if (lever.LengthSquared() > 0.01f)
                grid.AngularVelocity = Vector3.Clamp(grid.AngularVelocity, new Vector3(-3.5f), new Vector3(3.5f));
            _lastRemovedSupportCenter = null;
        }
        grid.FractureOnImpact = ImpactFractureEnabled;
        ChangesLastStep += component.Count;
    }

    private bool SimulateGrids(float deltaSeconds)
    {
        bool changed = false;
        var impactFractures = new List<(ulong GridId, int X, int Y, int Z, Vector3 Normal)>();
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            grid.ImpactCooldown = MathF.Max(0f, grid.ImpactCooldown - deltaSeconds);
            if (grid.IsSleeping) continue;
            changed = true;
            grid.LinearVelocity += new Vector3(0f, -18f, 0f) * deltaSeconds;
            grid.LinearVelocity *= MathF.Pow(0.996f, deltaSeconds * 60f);
            grid.AngularVelocity *= MathF.Pow(0.992f, deltaSeconds * 60f);
            grid.Position += grid.LinearVelocity * deltaSeconds;
            float angularSpeed = grid.AngularVelocity.Length();
            if (angularSpeed > 0.0001f)
            {
                Quaternion rotation = Quaternion.CreateFromAxisAngle(grid.AngularVelocity / angularSpeed, angularSpeed * deltaSeconds);
                grid.Orientation = Quaternion.Normalize(rotation * grid.Orientation);
            }

            if (FindStaticCollision(grid, out Vector3 contact, out Vector3 normal, out float penetration))
            {
                // Resolve only positional penetration. Reverting the complete pose locked
                // rotation whenever a corner touched the floor.
                // The broad world AABB overestimates penetration for rotated boxes. A
                // capped Baumgarte-style correction prevents that estimate from launching
                // the body back into the air while still removing overlap over several steps.
                const float contactSlop = 0.004f;
                float positionalCorrection = Math.Clamp((penetration - contactSlop) * 0.42f, 0f, 0.075f);
                grid.Position += normal * positionalCorrection;
                Vector3 contactVelocity = grid.VelocityAtPoint(contact);
                float normalVelocity = Vector3.Dot(contactVelocity, normal);
                float impactSpeed = MathF.Max(0f, -normalVelocity);
                if (normalVelocity < 0f)
                {
                    Vector3 radius = contact - grid.Position;
                    Vector3 rotational = Vector3.Cross(grid.ApplyInverseInertiaWorld(Vector3.Cross(radius, normal)), radius);
                    float denominator = 1f / grid.Mass + Vector3.Dot(normal, rotational);
                    // Resting contacts have no bounce. Only a genuinely hard impact keeps
                    // a tiny amount of restitution.
                    float restitution = impactSpeed >= 3.5f ? 0.035f : 0f;
                    float impulseMagnitude = -((1f + restitution) * normalVelocity) /
                                             MathF.Max(denominator, 0.0001f);
                    grid.ApplyImpulseAtPoint(normal * impulseMagnitude, contact);

                    Vector3 tangent = contactVelocity - normal * normalVelocity;
                    if (tangent.LengthSquared() > 0.00001f)
                    {
                        tangent = Vector3.Normalize(tangent);
                        Vector3 tangentRotational = Vector3.Cross(
                            grid.ApplyInverseInertiaWorld(Vector3.Cross(radius, tangent)), radius);
                        float tangentDenominator = 1f / grid.Mass + Vector3.Dot(tangent, tangentRotational);
                        float frictionImpulse = -Vector3.Dot(contactVelocity, tangent) /
                                                MathF.Max(tangentDenominator, 0.0001f);
                        frictionImpulse = Math.Clamp(frictionImpulse, -impulseMagnitude * 0.58f, impulseMagnitude * 0.58f);
                        grid.ApplyImpulseAtPoint(tangent * frictionImpulse, contact);
                    }
                }
                grid.LinearVelocity *= 0.93f;
                grid.AngularVelocity = Vector3.Clamp(grid.AngularVelocity, new Vector3(-8f), new Vector3(8f));

                if (ImpactFractureEnabled && grid.FractureOnImpact && grid.ImpactCooldown <= 0f &&
                    impactSpeed >= ImpactFractureSpeed && grid.Cells.Count >= 3)
                {
                    PhysicsGridElement closest = grid.Elements.MinBy(element =>
                        Vector3.DistanceSquared(grid.LocalToWorld(element.Center), contact));
                    impactFractures.Add((grid.Id, closest.CellX, closest.CellY, closest.CellZ, normal));
                    grid.ImpactCooldown = 0.65f;
                }

                if (grid.LinearVelocity.LengthSquared() < 0.025f && grid.AngularVelocity.LengthSquared() < 0.025f)
                {
                    grid.SleepTimer += deltaSeconds;
                    if (grid.SleepTimer >= 0.8f)
                    {
                        grid.IsSleeping = true;
                        grid.LinearVelocity = Vector3.Zero;
                        grid.AngularVelocity = Vector3.Zero;
                    }
                }
                else
                {
                    grid.SleepTimer = 0f;
                }
            }
            else
            {
                grid.SleepTimer = 0f;
            }
        }
        foreach ((ulong gridId, int x, int y, int z, Vector3 normal) in impactFractures)
            FractureGridAtCell(gridId, x, y, z, normal);
        if (ResolveGridCollisions()) changed = true;
        return changed;
    }

    private bool ResolveGridCollisions()
    {
        bool collided = false;
        var entries = _world.PhysicsGrids.Grids
            .Select(grid => (Grid: grid, Bounds: grid.CalculateWorldBounds()))
            .OrderBy(entry => entry.Bounds.Minimum.X)
            .ToArray();
        for (int firstIndex = 0; firstIndex < entries.Length; firstIndex++)
        for (int secondIndex = firstIndex + 1; secondIndex < entries.Length; secondIndex++)
        {
            (PhysicsGrid first, (Vector3 firstMin, Vector3 firstMax)) = entries[firstIndex];
            (PhysicsGrid second, (Vector3 secondMin, Vector3 secondMax)) = entries[secondIndex];
            if (secondMin.X >= firstMax.X) break;
            BroadphasePairsLastStep++;
            if (first.IsSleeping && second.IsSleeping) continue;
            Vector3 overlap = Vector3.Min(firstMax, secondMax) - Vector3.Max(firstMin, secondMin);
            if (overlap.X <= 0f || overlap.Y <= 0f || overlap.Z <= 0f) continue;
            NarrowphasePairsLastStep++;
            collided = true;
            Vector3 direction = second.Position - first.Position;
            Vector3 normal;
            float penetration;
            if (overlap.X <= overlap.Y && overlap.X <= overlap.Z)
            {
                normal = new Vector3(MathF.Sign(direction.X == 0f ? 1f : direction.X), 0f, 0f);
                penetration = overlap.X;
            }
            else if (overlap.Y <= overlap.Z)
            {
                normal = new Vector3(0f, MathF.Sign(direction.Y == 0f ? 1f : direction.Y), 0f);
                penetration = overlap.Y;
            }
            else
            {
                normal = new Vector3(0f, 0f, MathF.Sign(direction.Z == 0f ? 1f : direction.Z));
                penetration = overlap.Z;
            }
            float inverseFirst = 1f / first.Mass;
            float inverseSecond = 1f / second.Mass;
            float inverseTotal = inverseFirst + inverseSecond;
            first.Position -= normal * penetration * (inverseFirst / inverseTotal) * 0.52f;
            second.Position += normal * penetration * (inverseSecond / inverseTotal) * 0.52f;
            Vector3 relativeVelocity = second.LinearVelocity - first.LinearVelocity;
            float closingSpeed = Vector3.Dot(relativeVelocity, normal);
            if (closingSpeed < 0f)
            {
                float impulseMagnitude = -(1.08f * closingSpeed) / inverseTotal;
                Vector3 impulse = normal * impulseMagnitude;
                first.LinearVelocity -= impulse * inverseFirst;
                second.LinearVelocity += impulse * inverseSecond;
            }
            first.Wake();
            second.Wake();
        }
        return collided;
    }

    private bool FindStaticCollision(
        PhysicsGrid grid,
        out Vector3 contact,
        out Vector3 normal,
        out float penetration)
    {
        Vector3 axisX = Vector3.Normalize(grid.LocalDirectionToWorld(Vector3.UnitX));
        Vector3 axisY = Vector3.Normalize(grid.LocalDirectionToWorld(Vector3.UnitY));
        Vector3 axisZ = Vector3.Normalize(grid.LocalDirectionToWorld(Vector3.UnitZ));
        Vector3[] axes = { axisX, axisY, axisZ };
        foreach (PhysicsGridElement element in grid.CollisionElements)
        {
            Vector3 center = grid.LocalToWorld(element.Center);
            Vector3 half = element.HalfExtents;
            Vector3 worldRadius = new(
                MathF.Abs(axisX.X) * half.X + MathF.Abs(axisY.X) * half.Y + MathF.Abs(axisZ.X) * half.Z,
                MathF.Abs(axisX.Y) * half.X + MathF.Abs(axisY.Y) * half.Y + MathF.Abs(axisZ.Y) * half.Z,
                MathF.Abs(axisX.Z) * half.X + MathF.Abs(axisY.Z) * half.Y + MathF.Abs(axisZ.Z) * half.Z);
            Vector3 minimum = center - worldRadius;
            Vector3 maximum = center + worldRadius;
            int minX = (int)MathF.Floor(minimum.X);
            int minY = (int)MathF.Floor(minimum.Y);
            int minZ = (int)MathF.Floor(minimum.Z);
            int maxX = (int)MathF.Floor(maximum.X - 0.0001f);
            int maxY = (int)MathF.Floor(maximum.Y - 0.0001f);
            int maxZ = (int)MathF.Floor(maximum.Z - 0.0001f);
            for (int z = minZ; z <= maxZ; z++)
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (!_world.IsSolidForCollision(x, y, z)) continue;
                Vector3 staticCenter = new(x + 0.5f, y + 0.5f, z + 0.5f);
                if (!ObbIntersectsAabb(center, axes, half, staticCenter, new Vector3(0.5f))) continue;
                Vector3 staticMinimum = new(x, y, z);
                Vector3 staticMaximum = staticMinimum + Vector3.One;
                Vector3 overlap = Vector3.Min(maximum, staticMaximum) - Vector3.Max(minimum, staticMinimum);
                Vector3 separation = center - staticCenter;
                if (overlap.X <= overlap.Y && overlap.X <= overlap.Z)
                {
                    penetration = overlap.X;
                    normal = new Vector3(MathF.Sign(separation.X == 0f ? 1f : separation.X), 0f, 0f);
                }
                else if (overlap.Y <= overlap.Z)
                {
                    penetration = overlap.Y;
                    normal = new Vector3(0f, MathF.Sign(separation.Y == 0f ? 1f : separation.Y), 0f);
                }
                else
                {
                    penetration = overlap.Z;
                    normal = new Vector3(0f, 0f, MathF.Sign(separation.Z == 0f ? 1f : separation.Z));
                }
                // Closest point on the static cell is a substantially better torque arm
                // than the previous midpoint approximation.
                contact = Vector3.Clamp(center, staticMinimum, staticMaximum);
                return true;
            }
        }
        contact = Vector3.Zero;
        normal = Vector3.UnitY;
        penetration = 0f;
        return false;
    }

    private void FractureGridAtCell(ulong gridId, int x, int y, int z, Vector3 impactNormal)
    {
        PhysicsGrid? grid = _world.PhysicsGrids.Get(gridId);
        if (grid is null) return;
        PhysicsGridElement[] shardElements = grid.Elements
            .Where(element => element.CellX == x && element.CellY == y && element.CellZ == z)
            .ToArray();
        if (shardElements.Length == 0) return;
        PhysicsGrid shard = _world.PhysicsGrids.CreateFromGridElements(shardElements, grid);
        shard.LinearVelocity = grid.LinearVelocity + impactNormal * 1.4f;
        shard.AngularVelocity = grid.AngularVelocity + new Vector3(0.7f, -0.45f, 0.55f);
        shard.FractureOnImpact = false;
        if (!TryRemoveGridCell(gridId, x, y, z)) _world.PhysicsGrids.Remove(shard);
    }

    private void SplitDisconnectedGrid(PhysicsGrid grid)
    {
        var remaining = grid.Cells.ToHashSet();
        var components = new List<List<(int X, int Y, int Z)>>();
        while (remaining.Count > 0)
        {
            (int X, int Y, int Z) seed = remaining.First();
            remaining.Remove(seed);
            var component = new List<(int X, int Y, int Z)>();
            var queue = new Queue<(int X, int Y, int Z)>();
            queue.Enqueue(seed);
            while (queue.TryDequeue(out (int X, int Y, int Z) cell))
            {
                component.Add(cell);
                foreach (VoxelPoint offset in FaceNeighbors)
                {
                    var neighbor = (cell.X + offset.X, cell.Y + offset.Y, cell.Z + offset.Z);
                    if (!remaining.Contains(neighbor) || !GridCellsShareFace(grid, cell, neighbor, offset)) continue;
                    remaining.Remove(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
            components.Add(component);
        }
        if (components.Count <= 1) return;

        foreach (List<(int X, int Y, int Z)> component in components)
        {
            var cells = component.ToHashSet();
            PhysicsGridElement[] elements = grid.Elements
                .Where(element => cells.Contains((element.CellX, element.CellY, element.CellZ)))
                .ToArray();
            _world.PhysicsGrids.CreateFromGridElements(elements, grid);
        }
        _world.PhysicsGrids.Remove(grid);
    }

    private static bool GridCellsShareFace(
        PhysicsGrid grid,
        (int X, int Y, int Z) first,
        (int X, int Y, int Z) second,
        VoxelPoint direction)
    {
        foreach (PhysicsGridElement a in grid.Elements.Where(element =>
                     element.CellX == first.X && element.CellY == first.Y && element.CellZ == first.Z))
        foreach (PhysicsGridElement b in grid.Elements.Where(element =>
                     element.CellX == second.X && element.CellY == second.Y && element.CellZ == second.Z))
        {
            const float epsilon = 0.0001f;
            bool touches = direction switch
            {
                { X: 1 } => MathF.Abs(a.Maximum.X - b.Minimum.X) < epsilon &&
                            FloatOverlap(a.Minimum.Y, a.Maximum.Y, b.Minimum.Y, b.Maximum.Y) &&
                            FloatOverlap(a.Minimum.Z, a.Maximum.Z, b.Minimum.Z, b.Maximum.Z),
                { X: -1 } => MathF.Abs(a.Minimum.X - b.Maximum.X) < epsilon &&
                             FloatOverlap(a.Minimum.Y, a.Maximum.Y, b.Minimum.Y, b.Maximum.Y) &&
                             FloatOverlap(a.Minimum.Z, a.Maximum.Z, b.Minimum.Z, b.Maximum.Z),
                { Y: 1 } => MathF.Abs(a.Maximum.Y - b.Minimum.Y) < epsilon &&
                            FloatOverlap(a.Minimum.X, a.Maximum.X, b.Minimum.X, b.Maximum.X) &&
                            FloatOverlap(a.Minimum.Z, a.Maximum.Z, b.Minimum.Z, b.Maximum.Z),
                { Y: -1 } => MathF.Abs(a.Minimum.Y - b.Maximum.Y) < epsilon &&
                             FloatOverlap(a.Minimum.X, a.Maximum.X, b.Minimum.X, b.Maximum.X) &&
                             FloatOverlap(a.Minimum.Z, a.Maximum.Z, b.Minimum.Z, b.Maximum.Z),
                { Z: 1 } => MathF.Abs(a.Maximum.Z - b.Minimum.Z) < epsilon &&
                            FloatOverlap(a.Minimum.X, a.Maximum.X, b.Minimum.X, b.Maximum.X) &&
                            FloatOverlap(a.Minimum.Y, a.Maximum.Y, b.Minimum.Y, b.Maximum.Y),
                _ => MathF.Abs(a.Minimum.Z - b.Maximum.Z) < epsilon &&
                     FloatOverlap(a.Minimum.X, a.Maximum.X, b.Minimum.X, b.Maximum.X) &&
                     FloatOverlap(a.Minimum.Y, a.Maximum.Y, b.Minimum.Y, b.Maximum.Y),
            };
            if (touches) return true;
        }
        return false;
    }

    private static bool FloatOverlap(float minA, float maxA, float minB, float maxB) =>
        minA < maxB - 0.0001f && minB < maxA - 0.0001f;

    private static VoxelPoint CardinalDirection(Vector3 direction)
    {
        float x = MathF.Abs(direction.X);
        float y = MathF.Abs(direction.Y);
        float z = MathF.Abs(direction.Z);
        return x >= y && x >= z
            ? new VoxelPoint(Math.Sign(direction.X), 0, 0)
            : y >= z
                ? new VoxelPoint(0, Math.Sign(direction.Y), 0)
                : new VoxelPoint(0, 0, Math.Sign(direction.Z));
    }

    private static bool ObbIntersectsAabb(
        Vector3 centerA,
        Vector3[] axesA,
        Vector3 halfA,
        Vector3 centerB,
        Vector3 halfB)
    {
        const float epsilon = 0.00001f;
        float[,] rotation = new float[3, 3];
        float[,] absolute = new float[3, 3];
        Vector3[] worldAxes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            rotation[i, j] = Vector3.Dot(axesA[i], worldAxes[j]);
            absolute[i, j] = MathF.Abs(rotation[i, j]) + epsilon;
        }
        Vector3 difference = centerB - centerA;
        float[] translation = { Vector3.Dot(difference, axesA[0]), Vector3.Dot(difference, axesA[1]), Vector3.Dot(difference, axesA[2]) };
        float[] a = { halfA.X, halfA.Y, halfA.Z };
        float[] b = { halfB.X, halfB.Y, halfB.Z };
        for (int i = 0; i < 3; i++)
        {
            float radiusB = b[0] * absolute[i, 0] + b[1] * absolute[i, 1] + b[2] * absolute[i, 2];
            if (MathF.Abs(translation[i]) > a[i] + radiusB) return false;
        }
        for (int j = 0; j < 3; j++)
        {
            float projected = MathF.Abs(translation[0] * rotation[0, j] + translation[1] * rotation[1, j] + translation[2] * rotation[2, j]);
            float radiusA = a[0] * absolute[0, j] + a[1] * absolute[1, j] + a[2] * absolute[2, j];
            if (projected > radiusA + b[j]) return false;
        }
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            float projected = MathF.Abs(translation[(i + 2) % 3] * rotation[(i + 1) % 3, j] -
                                        translation[(i + 1) % 3] * rotation[(i + 2) % 3, j]);
            float radiusA = a[(i + 1) % 3] * absolute[(i + 2) % 3, j] +
                            a[(i + 2) % 3] * absolute[(i + 1) % 3, j];
            float radiusB = b[(j + 1) % 3] * absolute[i, (j + 2) % 3] +
                            b[(j + 2) % 3] * absolute[i, (j + 1) % 3];
            if (projected > radiusA + radiusB) return false;
        }
        return true;
    }

    private bool HasEmptyFace(int x, int y, int z) => FaceNeighbors.Any(offset =>
        !_world.IsInside(x + offset.X, y + offset.Y, z + offset.Z) ||
        !IsStructuralAt(x + offset.X, y + offset.Y, z + offset.Z));

    private bool IsStructuralAt(int x, int y, int z) =>
        IsStructural(_world.GetBlockId(x, y, z)) ||
        (_world.TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? micro) &&
         micro!.Boxes.Any(box => IsStructural(box.MaterialId)));

    private bool IsStructural(ushort block)
    {
        if (block == 0) return false;
        BlockDefinition definition = _blocks.Get(block);
        return definition.IsCollidable && !definition.IsFluid;
    }

    private static bool IntervalsOverlap(int minA, int maxA, int resolutionA, int minB, int maxB, int resolutionB) =>
        (long)minA * resolutionB < (long)maxB * resolutionA &&
        (long)minB * resolutionA < (long)maxA * resolutionB;

    private static int Index(int x, int y, int z) =>
        x + VoxelWorld.Width * (y + VoxelWorld.Height * z);

    private readonly record struct FaceRect(int MinU, int MaxU, int MinV, int MaxV, int Resolution);

    public void Dispose()
    {
        _world.DetailedVoxelChanged -= OnVoxelChanged;
        _world.WorldReloaded -= OnWorldReloaded;
        _world.MicroVoxelsChanged -= OnMicroVoxelsChanged;
        _rigidBodyBackend?.Dispose();
        _rigidBodyBackend = null;
    }
}
