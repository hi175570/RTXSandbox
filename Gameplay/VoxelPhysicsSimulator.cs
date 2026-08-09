using System.Numerics;
using System.Diagnostics;
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
    private readonly HashSet<VoxelPoint> _queued = new();
    private bool _suppressWorldEvents;
    private float _analysisAccumulator;
    private const int MaximumAnalysisSeedsPerUpdate = 2;
    private const int MaximumAnalyzedCells = 65_536;
    private const double MaximumAnalysisMillisecondsPerFrame = 1.0;
    private Vector3? _lastRemovedSupportCenter;
    private BepuPhysicsBackend? _rigidBodyBackend;
    private Vector3 _simulationFocus;

    public int ChangesLastStep { get; private set; }
    public int SimulationSubstepsLastStep { get; private set; }
    public int ActiveGridCount => _world.PhysicsGrids.Count;
    public bool SimulationEnabled { get; set; } = true;
    public ulong SelectedGridId { get; private set; }
    public string Status { get; private set; } = "Grid physics ready";
    public string ModeName => "StructuralGrid";
    public string BackendName => _rigidBodyBackend?.Name ?? "BEPUphysics 2";
    public int PendingColliderBuildCount => _rigidBodyBackend?.PendingColliderBuildCount ?? 0;
    public int PendingStructuralEvaluationCount => _dirty.Count;
    public int ColliderBuildCount => _rigidBodyBackend?.ColliderBuildCount ?? 0;
    public double ColliderBuildTotalMilliseconds => _rigidBodyBackend?.ColliderBuildTotalMilliseconds ?? 0;
    public double MaximumColliderBuildMilliseconds => _rigidBodyBackend?.MaximumColliderBuildMilliseconds ?? 0;
    public double MaximumDynamicReconcileMilliseconds =>
        _rigidBodyBackend?.MaximumDynamicReconcileMilliseconds ?? 0;
    public double MaximumSimulationMilliseconds => _rigidBodyBackend?.MaximumSimulationMilliseconds ?? 0;
    public double MaximumSingleSimulationStepMilliseconds =>
        _rigidBodyBackend?.MaximumSingleSimulationStepMilliseconds ?? 0;
    public double MaximumStaticResidencyMilliseconds =>
        _rigidBodyBackend?.MaximumStaticResidencyMilliseconds ?? 0;

    public VoxelPhysicsSimulator(VoxelWorld world, BlockRegistry blocks)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        world.DetailedVoxelChanged += OnVoxelChanged;
        world.WorldReloaded += OnWorldReloaded;
        world.MicroVoxelCellChanged += OnMicroVoxelCellChanged;
    }

    public void RequestEvaluation()
    {
        // Evaluate exposed cells instead of blindly moving the complete generated terrain.
        foreach (ChunkCoordinate chunk in _world.SnapshotLoadedChunks())
        {
            VoxelChunkSnapshot snapshot = _world.SnapshotChunk(chunk);
            foreach (StoredVoxel voxel in snapshot.Voxels)
            {
                if (!IsStructural(voxel.MaterialId)) continue;
                int x = chunk.MinimumX + voxel.LocalX;
                int z = chunk.MinimumZ + voxel.LocalZ;
                if (!IsAnchor(x, voxel.Y, z) && HasEmptyFace(x, voxel.Y, z)) Enqueue(x, voxel.Y, z);
            }
            foreach (StoredMicroVoxel micro in snapshot.MicroVoxels)
            {
                if (!micro.Snapshot.Boxes.Any(box => IsStructural(box.MaterialId))) continue;
                int x = chunk.MinimumX + micro.LocalX;
                int z = chunk.MinimumZ + micro.LocalZ;
                if (!IsAnchor(x, micro.Y, z) && HasEmptyFace(x, micro.Y, z)) Enqueue(x, micro.Y, z);
            }
        }
    }

    public void Update(float deltaSeconds)
    {
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.05f);
        ChangesLastStep = 0;
        if (!SimulationEnabled)
        {
            SimulationSubstepsLastStep = 0;
            Status = $"Physics paused: {ActiveGridCount} grids retained";
            return;
        }
        _analysisAccumulator += deltaSeconds;
        if (_analysisAccumulator >= 0.05f)
        {
            _analysisAccumulator = 0f;
            AnalyzeDirtyComponents(maximumSeeds: MaximumAnalysisSeedsPerUpdate);
        }

        if (_world.PhysicsGrids.Count > 0 || _world.AnalyticSpheres.Count > 0 || _rigidBodyBackend is not null)
        {
            _rigidBodyBackend ??= new BepuPhysicsBackend(_world, _simulationFocus);
            _rigidBodyBackend.SetSimulationFocus(_simulationFocus);
            _rigidBodyBackend.Step(deltaSeconds);
            SimulationSubstepsLastStep = _rigidBodyBackend.CollisionStepsLastFrame;
            Status = $"BEPUphysics 2: {_rigidBodyBackend.DynamicBodyCount} bodies, " +
                     $"{_rigidBodyBackend.StaticChunkBodyCount} static chunks, {SimulationSubstepsLastStep} collision steps";
        }
        else
        {
            SimulationSubstepsLastStep = 0;
            Status = "BEPUphysics 2 ready";
        }
    }

    public int AnalyzeNow()
    {
        ChangesLastStep = 0;
        AnalyzeDirtyComponents(int.MaxValue, enforceTimeLimit: false);
        return ChangesLastStep;
    }

    public void SetSimulationFocus(Vector3 position)
    {
        _simulationFocus = position;
        _rigidBodyBackend?.SetSimulationFocus(position);
    }

    public void PushAnalyticSpheresFromPlayer(Vector3 playerPosition, Vector3 playerVelocity)
    {
        if (!SimulationEnabled || _world.AnalyticSpheres.Count == 0) return;
        _rigidBodyBackend ??= new BepuPhysicsBackend(_world, _simulationFocus);
        _rigidBodyBackend.QueuePlayerPush(playerPosition, playerVelocity);
    }

    public void ResetPerformanceCounters() => _rigidBodyBackend?.ResetPerformanceCounters();

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

    public bool TryRemoveGridElement(PhysicsGridRayHit hit)
    {
        PhysicsGrid? grid = _world.PhysicsGrids.Get(hit.GridId);
        if (grid is null || !grid.TryRemoveElementAtHit(hit)) return false;
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
        if (grid is null || !CanPlaceOnGrid(hit, materialId)) return false;
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

    public bool CanPlaceOnGrid(PhysicsGridRayHit hit, ushort materialId)
    {
        PhysicsGrid? grid = _world.PhysicsGrids.Get(hit.GridId);
        if (grid is null || !PlacementRules.IsFarEnough(hit.Distance) ||
            materialId == 0 || !_blocks.Get(materialId).IsPlaceable) return false;
        Vector3 localNormal = Vector3.Transform(hit.Normal, Quaternion.Inverse(grid.Orientation));
        VoxelPoint face = CardinalDirection(localNormal);
        return !grid.ContainsCell(hit.CellX + face.X, hit.CellY + face.Y, hit.CellZ + face.Z);
    }

    public bool TryPlaceMicroOnGrid(PhysicsGridRayHit hit, ushort materialId, int divisor)
    {
        PhysicsGrid? grid = _world.PhysicsGrids.Get(hit.GridId);
        if (grid is null || !PlacementRules.IsFarEnough(hit.Distance) ||
            divisor is < 2 or > MicroVoxelContainer.MaximumResolution ||
            materialId == 0 || !_blocks.Get(materialId).IsPlaceable) return false;

        Vector3 localNormal = Vector3.Normalize(Vector3.Transform(
            hit.Normal, Quaternion.Inverse(grid.Orientation)));
        Vector3 localPoint = grid.WorldToLocal(hit.Position) + localNormal * 0.0025f;
        float scale = 1f / divisor;
        Vector3 minimum = new(
            MathF.Floor(localPoint.X * divisor) * scale,
            MathF.Floor(localPoint.Y * divisor) * scale,
            MathF.Floor(localPoint.Z * divisor) * scale);
        int cellX = (int)MathF.Floor(minimum.X + 0.000001f);
        int cellY = (int)MathF.Floor(minimum.Y + 0.000001f);
        int cellZ = (int)MathF.Floor(minimum.Z + 0.000001f);
        bool added = grid.TryAddBox(
            cellX, cellY, cellZ,
            minimum,
            minimum + new Vector3(scale),
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
        bool oldStructural = IsStructural(change.OldBlockId);
        bool newStructural = IsStructural(change.NewBlockId);

        // Replacing one full structural voxel with another does not alter support
        // topology.  Enqueuing it used to launch a terrain-wide connectivity flood
        // for ordinary material painting, despite the occupied shape being identical.
        if (oldStructural && newStructural) return;
        if (!oldStructural && !newStructural) return;
        if (!oldStructural)
        {
            // A newly placed cell with direct support cannot detach anything that
            // already existed. Standalone/cantilever placement still enters the
            // structural queue and can become a Physics Grid.
            if (IsStructuralAt(change.X, change.Y - 1, change.Z)) return;
            Enqueue(change.X, change.Y, change.Z);
            return;
        }

        _lastRemovedSupportCenter = new Vector3(change.X + 0.5f, change.Y + 0.5f, change.Z + 0.5f);
        // Only cells above or beside a removed support can depend on it. A candidate
        // with its own direct support below is already stable and must not launch a
        // terrain-wide connectivity flood after an ordinary surface block break.
        ReadOnlySpan<VoxelPoint> candidates = stackalloc VoxelPoint[]
        {
            new(change.X, change.Y + 1, change.Z),
            new(change.X + 1, change.Y, change.Z),
            new(change.X - 1, change.Y, change.Z),
            new(change.X, change.Y, change.Z + 1),
            new(change.X, change.Y, change.Z - 1),
        };
        foreach (VoxelPoint candidate in candidates)
        {
            if (IsStructuralAt(candidate.X, candidate.Y, candidate.Z) &&
                !IsStructuralAt(candidate.X, candidate.Y - 1, candidate.Z))
                Enqueue(candidate.X, candidate.Y, candidate.Z);
        }
    }

    private void OnMicroVoxelCellChanged(int x, int y, int z)
    {
        Enqueue(x, y, z);
        foreach (VoxelPoint offset in FaceNeighbors)
            Enqueue(x + offset.X, y + offset.Y, z + offset.Z);
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
        var point = new VoxelPoint(x, y, z);
        if (_queued.Add(point)) _dirty.Enqueue(point);
    }

    private void AnalyzeDirtyComponents(int maximumSeeds, bool enforceTimeLimit = true)
    {
        long started = Stopwatch.GetTimestamp();
        var analyzed = new HashSet<VoxelPoint>();
        int seeds = 0;
        while (seeds < maximumSeeds && _dirty.TryDequeue(out VoxelPoint seed))
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (enforceTimeLimit && elapsedMs >= MaximumAnalysisMillisecondsPerFrame) break;
            _queued.Remove(seed);
            if (!IsStructuralAt(seed.X, seed.Y, seed.Z) || analyzed.Contains(seed)) continue;
            seeds++;
            List<VoxelPoint> component = FloodComponent(seed, analyzed, out bool anchored);
            if (!anchored && component.Count > 0) ExtractComponent(component);
        }
    }

    private List<VoxelPoint> FloodComponent(VoxelPoint seed, HashSet<VoxelPoint> analyzed, out bool anchored)
    {
        var component = new List<VoxelPoint>();
        var queue = new Queue<VoxelPoint>();
        queue.Enqueue(seed);
        analyzed.Add(seed);
        anchored = false;

        while (queue.TryDequeue(out VoxelPoint point))
        {
            component.Add(point);
            if (IsAnchor(point.X, point.Y, point.Z))
            {
                anchored = true;
                break;
            }
            if (component.Count >= MaximumAnalyzedCells)
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
                var neighbor = new VoxelPoint(x, y, z);
                if (analyzed.Contains(neighbor) || !CellsShareFace(point, neighbor, offset)) continue;
                analyzed.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }
        return component;
    }

    private bool IsAnchor(int x, int y, int z)
    {
        if (y == VoxelWorld.MinimumY) return true;
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
        ChangesLastStep += component.Count;
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

    private bool HasEmptyFace(int x, int y, int z) => FaceNeighbors.Any(offset =>
        !_world.IsInside(x + offset.X, y + offset.Y, z + offset.Z) ||
        !IsStructuralAt(x + offset.X, y + offset.Y, z + offset.Z));

    private bool IsStructuralAt(int x, int y, int z) =>
        IsStructural(_world.GetLoadedBlockId(x, y, z)) ||
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

    private readonly record struct FaceRect(int MinU, int MaxU, int MinV, int MaxV, int Resolution);

    public void Dispose()
    {
        _world.DetailedVoxelChanged -= OnVoxelChanged;
        _world.WorldReloaded -= OnWorldReloaded;
        _world.MicroVoxelCellChanged -= OnMicroVoxelCellChanged;
        _rigidBodyBackend?.Dispose();
        _rigidBodyBackend = null;
    }
}
