using System.Numerics;
using RTXSandbox.Engine;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

/// <summary>
/// Core, always-available voxel soft bodies. A complete connected PhysicsGrid is one
/// body: soft cells bend, hard cells keep their edge/diagonal lengths, and the contact
/// face against static terrain is pinned until that support is actually removed.
/// </summary>
public sealed class SoftBodyPhysicsSystem : IDisposable
{
    private const int MaximumBodies = 32;
    private const int MaximumCellsPerBody = 512;
    private const float FixedStep = 1f / 120f;
    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly VoxelPhysicsSimulator _physics;
    private readonly HashSet<ushort> _softMaterials;
    private readonly Dictionary<ulong, Body> _bodies = new();
    private readonly HashSet<(int X, int Y, int Z)> _pendingSeeds = new();
    private bool _extracting;
    private bool _updatingGeometry;
    private float _extractCountdown;
    private float _accumulator;
    private float _geometryAccumulator;

    public int ActiveBodyCount => _bodies.Count;

    public SoftBodyPhysicsSystem(VoxelWorld world, BlockRegistry blocks, VoxelPhysicsSimulator physics)
    {
        _world = world;
        _blocks = blocks;
        _physics = physics;
        _softMaterials = blocks.Definitions
            .Where(IsSoft)
            .Select(block => block.Id)
            .ToHashSet();
        _world.DetailedVoxelChanged += OnVoxelChanged;
        _world.WorldReloaded += OnWorldReloaded;
        _world.PhysicsGrids.Changed += OnGridsChanged;
        ExtractLoaded();
        DiscoverBodies();
    }

    public void Update(float deltaSeconds)
    {
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.05f);
        ExtractPending(deltaSeconds);
        DiscoverBodies();
        RemoveMissingBodies();
        if (_bodies.Count == 0) return;

        _accumulator = MathF.Min(_accumulator + deltaSeconds, FixedStep * 5f);
        int steps = 0;
        while (_accumulator >= FixedStep && steps++ < 5)
        {
            foreach (Body body in _bodies.Values) body.Step(FixedStep, _world, _blocks);
            _accumulator -= FixedStep;
        }

        _geometryAccumulator += deltaSeconds;
        if (_geometryAccumulator < 1f / 60f) return;
        _geometryAccumulator = 0f;
        _updatingGeometry = true;
        try
        {
            bool changed = false;
            foreach (Body body in _bodies.Values)
            {
                changed |= body.UpdateGridGeometry();
            }
            if (changed) _world.PhysicsGrids.NotifyChanged();
        }
        finally
        {
            _updatingGeometry = false;
        }
    }

    private void ExtractLoaded()
    {
        if (_softMaterials.Count == 0) return;
        _extracting = true;
        try { _physics.ExtractLoadedSoftStructures(_softMaterials); }
        finally { _extracting = false; }
    }

    private void ExtractPending(float deltaSeconds)
    {
        if (_pendingSeeds.Count == 0) return;
        _extractCountdown -= deltaSeconds;
        if (_extractCountdown > 0f) return;
        (int X, int Y, int Z)[] seeds = _pendingSeeds.ToArray();
        _pendingSeeds.Clear();
        _extracting = true;
        try
        {
            foreach ((int x, int y, int z) in seeds)
                _physics.TryExtractSoftStructure(x, y, z, _softMaterials);
        }
        finally { _extracting = false; }
    }

    private void DiscoverBodies()
    {
        if (_updatingGeometry) return;
        bool ownershipChanged = false;
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids.ToArray())
        {
            Body? previousBody = null;
            if (_bodies.TryGetValue(grid.Id, out Body? existing))
            {
                if (existing.SourceTopology == grid.TopologyVersion) continue;
                previousBody = existing;
                grid.IsDeformable = false;
                _bodies.Remove(grid.Id);
                ownershipChanged = true;
            }
            if (_bodies.Count >= MaximumBodies || grid.Elements.Count == 0 ||
                grid.Elements.Count > MaximumCellsPerBody ||
                !grid.Elements.Any(element => _softMaterials.Contains(element.MaterialId))) continue;
            grid.IsDeformable = true;
            grid.FractureOnImpact = false;
            _bodies[grid.Id] = new Body(grid, _blocks, _world, previousBody);
            ownershipChanged = true;
        }
        if (ownershipChanged) _world.PhysicsGrids.NotifyTopologyChanged();
    }

    private void RemoveMissingBodies()
    {
        bool ownershipChanged = false;
        foreach ((ulong id, Body body) in _bodies.ToArray())
        {
            if (_world.PhysicsGrids.Get(id) == body.Grid) continue;
            body.Grid.IsDeformable = false;
            _bodies.Remove(id);
            ownershipChanged = true;
        }
        if (ownershipChanged) _world.PhysicsGrids.NotifyTopologyChanged();
    }

    private void OnVoxelChanged(VoxelChangedEvent change)
    {
        if (_extracting) return;
        if (_softMaterials.Contains(change.NewBlockId))
            _pendingSeeds.Add((change.X, change.Y, change.Z));
        foreach ((int dx, int dy, int dz) in FaceDirections)
            if (_softMaterials.Contains(_world.GetBlockId(change.X + dx, change.Y + dy, change.Z + dz)))
                _pendingSeeds.Add((change.X + dx, change.Y + dy, change.Z + dz));
        if (_pendingSeeds.Count > 0) _extractCountdown = 0.35f;
    }

    private void OnGridsChanged()
    {
        if (!_updatingGeometry) DiscoverBodies();
    }

    private void OnWorldReloaded()
    {
        foreach (Body body in _bodies.Values) body.Grid.IsDeformable = false;
        _bodies.Clear();
        _pendingSeeds.Clear();
        _accumulator = 0f;
        ExtractLoaded();
        DiscoverBodies();
    }

    public void Dispose()
    {
        _world.DetailedVoxelChanged -= OnVoxelChanged;
        _world.WorldReloaded -= OnWorldReloaded;
        _world.PhysicsGrids.Changed -= OnGridsChanged;
        foreach (Body body in _bodies.Values) body.Grid.IsDeformable = false;
        _bodies.Clear();
    }

    private static bool IsSoft(BlockDefinition block) =>
        block.Tags.Any(tag => string.Equals(tag, "soft-body", StringComparison.OrdinalIgnoreCase));

    private static readonly (int X, int Y, int Z)[] FaceDirections =
    {
        (1,0,0), (-1,0,0), (0,1,0), (0,-1,0), (0,0,1), (0,0,-1),
    };

    private sealed class Body
    {
        private const float NodeRadius = 0.08f;
        private const int SolverIterations = 12;
        private readonly Node[] _nodes;
        private readonly (int X, int Y, int Z)[] _nodeKeys;
        private readonly Constraint[] _constraints;
        private readonly Cell[] _cells;
        private readonly RigidCluster[] _rigidClusters;
        private readonly SupportBinding[][] _supportBindings;
        private readonly Vector3 _worldOrigin;
        private bool _sleeping;
        private float _sleepTimer;
        private float _groundContactTimer;

        public PhysicsGrid Grid { get; }
        public int SourceTopology { get; private set; }

        public Body(PhysicsGrid grid, BlockRegistry blocks, VoxelWorld world, Body? previous = null)
        {
            Grid = grid;
            _worldOrigin = grid.LocalToWorld(Vector3.Zero);
            var nodeMap = new Dictionary<(int X, int Y, int Z), int>();
            var nodeKeys = new List<(int X, int Y, int Z)>();
            var nodes = new List<Node>();
            var cells = new List<Cell>();
            var constraints = new Dictionary<(int A, int B), Constraint>();
            var currentSums = new List<Vector3>();
            var currentCounts = new List<int>();
            var densitySums = new List<float>();
            var densityCounts = new List<int>();
            var firstCurrent = new List<Vector3>();
            var smoothByCell = grid.SmoothSpheres
                .Where(surface => surface.HasExplicitCorners)
                .GroupBy(surface => (surface.CellX, surface.CellY, surface.CellZ))
                .ToDictionary(group => group.Key, group => group.First());
            Dictionary<(int X, int Y, int Z), NodeSnapshot>? previousNodes = previous?.SnapshotNodes();
            Dictionary<ConstraintRestKey, float>? previousConstraintRestLengths =
                previous?.SnapshotConstraintRestLengths();
            bool recoverRestShape = previous is null && HasInvalidSavedDeformation(grid, smoothByCell);
            bool adoptCurrentShapeAsRest = previous is null && smoothByCell.Count > 0 && !recoverRestShape;

            foreach (PhysicsGridElement element in grid.Elements)
            {
                BlockDefinition material = blocks.Get(element.MaterialId);
                bool soft = IsSoft(material);
                var cornerIndices = new int[8];
                for (int z = 0; z < 2; z++)
                for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                {
                    var key = (element.CellX + x, element.CellY + y, element.CellZ + z);
                    Vector3 restPosition = grid.LocalToWorld(new Vector3(key.Item1, key.Item2, key.Item3));
                    Vector3 currentPosition = restPosition;
                    if (!recoverRestShape && smoothByCell.TryGetValue(
                            (element.CellX, element.CellY, element.CellZ), out PhysicsGridSmoothSphere surface))
                        currentPosition = grid.LocalToWorld(surface.GetCorner(x, y, z));
                    if (previousNodes is not null && previousNodes.TryGetValue(key, out NodeSnapshot previousNode))
                        currentPosition = previousNode.Position;
                    if (!nodeMap.TryGetValue(key, out int nodeIndex))
                    {
                        nodeIndex = nodes.Count;
                        nodeMap.Add(key, nodeIndex);
                        nodeKeys.Add(key);
                        nodes.Add(new Node(restPosition, material.Density));
                        currentSums.Add(currentPosition);
                        currentCounts.Add(1);
                        densitySums.Add(material.Density);
                        densityCounts.Add(1);
                        firstCurrent.Add(currentPosition);
                    }
                    else
                    {
                        currentSums[nodeIndex] += currentPosition;
                        currentCounts[nodeIndex]++;
                        densitySums[nodeIndex] += material.Density;
                        densityCounts[nodeIndex]++;
                        if (previous is null &&
                            Vector3.DistanceSquared(firstCurrent[nodeIndex], currentPosition) > 0.20f * 0.20f)
                            recoverRestShape = true;
                    }
                    cornerIndices[x + y * 2 + z * 4] = nodeIndex;
                }
                Vector3[] restCorners = cornerIndices
                    .Select(index => nodes[index].Rest)
                    .ToArray();
                cells.Add(new Cell(element.CellX, element.CellY, element.CellZ,
                    element.MaterialId, cornerIndices, restCorners, soft));

                // All 28 corner pairs preserve volume. Soft material uses compliant
                // springs; hard inclusions retain a cuboid while the assembly bends.
                for (int a = 0; a < 8; a++)
                for (int b = a + 1; b < 8; b++)
                {
                    int ia = cornerIndices[a], ib = cornerIndices[b];
                    var pair = ia < ib ? (ia, ib) : (ib, ia);
                    float rest = Vector3.Distance(nodes[ia].Rest, nodes[ib].Rest);
                    float stiffness = soft ? MaterialStiffness(material) : 0.995f;
                    if (!constraints.TryGetValue(pair, out Constraint existing) || stiffness > existing.Stiffness)
                        constraints[pair] = new Constraint(pair.Item1, pair.Item2, rest, stiffness);
                }
            }
            for (int index = 0; index < nodes.Count; index++)
            {
                Node node = nodes[index];
                Vector3 initial = recoverRestShape ? node.Rest : currentSums[index] / currentCounts[index];
                float density = densitySums[index] / densityCounts[index];
                if (previousNodes is not null &&
                    previousNodes.TryGetValue(nodeKeys[index], out NodeSnapshot previousNode))
                    node.Reset(previousNode.Position, previousNode.Previous, previousNode.Velocity, density);
                else
                    node.Reset(initial, density);
                nodes[index] = node;
            }
            _nodes = nodes.ToArray();
            _nodeKeys = nodeKeys.ToArray();
            _cells = cells.ToArray();
            AddBendingConstraints(cells, nodes, constraints);
            if (previousConstraintRestLengths is not null || adoptCurrentShapeAsRest)
            {
                foreach (((int A, int B) pair, Constraint constraint) in constraints.ToArray())
                {
                    ConstraintRestKey key = CreateConstraintRestKey(
                        nodeKeys[constraint.A], nodeKeys[constraint.B]);
                    float restLength;
                    if (previousConstraintRestLengths is null ||
                        !previousConstraintRestLengths.TryGetValue(key, out restLength))
                    {
                        // A topology edit must be mechanically quiet. Existing rods keep
                        // their authored rest length, while rods introduced by the new
                        // cell start at the shape the player actually attached them to.
                        // Otherwise the first perpendicular extension adds pre-stressed
                        // two-cell bending rods and violently releases that error.
                        restLength = Vector3.Distance(
                            nodes[constraint.A].Position,
                            nodes[constraint.B].Position);
                        if (!float.IsFinite(restLength) || restLength < 0.0001f)
                            restLength = constraint.RestLength;
                    }
                    constraints[pair] = constraint with { RestLength = restLength };
                }
            }
            _constraints = constraints.Values.ToArray();
            _rigidClusters = BuildRigidClusters(
                cells,
                nodes,
                grid,
                previous is not null || adoptCurrentShapeAsRest);
            _supportBindings = CaptureSupportBindings(world, previousNodes);
            RefreshAnchors(world);
            SourceTopology = grid.TopologyVersion;
        }

        public void Step(float dt, VoxelWorld world, BlockRegistry blocks)
        {
            RefreshAnchors(world);
            if (_sleeping) return;

            for (int index = 0; index < _nodes.Length; index++)
            {
                ref Node node = ref _nodes[index];
                if (node.Pinned)
                {
                    node.Position = node.Anchor;
                    node.Previous = node.Anchor;
                    node.Velocity = Vector3.Zero;
                    continue;
                }
                node.Velocity += new Vector3(0f, -18f, 0f) * dt;
                // These blocks are viscoelastic solids, not trampolines. Constraint
                // correction already supplies the visible spring response; stronger
                // internal damping prevents a dropped gel/foam cluster from recycling
                // that energy for minutes after it has reached the floor.
                node.Velocity *= MathF.Pow(0.94f, dt * 120f);
                node.Previous = node.Position;
                node.Position += node.Velocity * dt;
            }

            bool contactedWorld = false;
            for (int iteration = 0; iteration < SolverIterations; iteration++)
            {
                foreach (Constraint constraint in _constraints)
                {
                    ref Node a = ref _nodes[constraint.A];
                    ref Node b = ref _nodes[constraint.B];
                    Vector3 delta = b.Position - a.Position;
                    float length = delta.Length();
                    if (length < 0.00001f) continue;
                    float strain = length / MathF.Max(constraint.RestLength, 0.0001f);
                    // Compliance is for bending and local squash, not infinitely long
                    // tethers. A hard strain ceiling stops a partly anchored gel strip
                    // from stretching across the room while keeping its ordinary sag.
                    float stiffness = strain > 1.22f
                        ? 1f
                        : strain > 1.12f ? MathF.Max(0.992f, constraint.Stiffness) : constraint.Stiffness;
                    Vector3 correction = delta * ((length - constraint.RestLength) / length * stiffness);
                    float inverseA = a.Pinned ? 0f : 1f / a.Mass;
                    float inverseB = b.Pinned ? 0f : 1f / b.Mass;
                    float inverseTotal = inverseA + inverseB;
                    if (inverseTotal <= 0f) continue;
                    if (!a.Pinned) a.Position += correction * (inverseA / inverseTotal);
                    if (!b.Pinned) b.Position -= correction * (inverseB / inverseTotal);
                }
                for (int index = 0; index < _nodes.Length; index++)
                    if (!_nodes[index].Pinned) contactedWorld |= ResolveWorldCollision(ref _nodes[index], world);
                // Collision works per node, but a solid endpoint must never inherit
                // those per-corner offsets. Make rigid projection the final operation
                // of every iteration so the published hard cell remains a cuboid.
                ProjectRigidClusters();
            }

            float maximumSpeed = 0f;
            Vector3 velocitySum = Vector3.Zero;
            int movingNodeCount = 0;
            foreach (ref Node node in _nodes.AsSpan())
            {
                if (node.Pinned) continue;
                node.Velocity = (node.Position - node.Previous) / dt;
                if (contactedWorld) node.Velocity *= 0.55f;
                if (!IsFinite(node.Velocity) || node.Velocity.LengthSquared() > 400f)
                    node.Velocity = Vector3.Clamp(node.Velocity, new Vector3(-20f), new Vector3(20f));
                maximumSpeed = MathF.Max(maximumSpeed, node.Velocity.Length());
                velocitySum += node.Velocity;
                movingNodeCount++;
            }
            float averageSpeed = movingNodeCount == 0 ? 0f : (velocitySum / movingNodeCount).Length();
            // Projection normally leaves a resting corner just outside the floor, so
            // the next step may report no penetration at all. Keep that legitimate
            // support contact alive with a tiny downward probe; otherwise the sleep
            // timer is reset every other frame and every elastic material hops forever.
            bool grounded = contactedWorld || _nodes.Any(node =>
                !node.Pinned && HasWorldSupportImmediatelyBelow(node, world));
            _groundContactTimer = grounded
                ? MathF.Min(1f, _groundContactTimer + dt)
                : MathF.Max(0f, _groundContactTimer - dt * 4f);
            bool resting = _groundContactTimer > 0f
                ? averageSpeed < 1.25f
                : maximumSpeed < 0.025f;
            if (resting) _sleepTimer += dt;
            else if (_groundContactTimer > 0f) _sleepTimer = MathF.Max(0f, _sleepTimer - dt * 0.35f);
            else _sleepTimer = 0f;
            _sleeping = _sleepTimer > 0.75f;
            if (_sleeping)
            {
                foreach (ref Node node in _nodes.AsSpan())
                {
                    if (node.Pinned) continue;
                    node.Previous = node.Position;
                    node.Velocity = Vector3.Zero;
                }
            }
        }

        public bool UpdateGridGeometry()
        {
            // Once a deformable body has genuinely settled, its last authored skin is
            // already on the grid. Republishing identical vertices at 60 Hz used to
            // rebuild BLAS/topology forever and reset path-tracing accumulation to one
            // sample even though nothing on screen moved.
            if (_sleeping && Grid.IsSleeping && SourceTopology == Grid.TopologyVersion)
                return false;
            var elements = new PhysicsGridElement[_cells.Length];
            var skin = new PhysicsGridSmoothSphere[_cells.Length];
            Vector3 averageVelocity = Vector3.Zero;
            foreach (Node node in _nodes) averageVelocity += node.Velocity;
            averageVelocity /= Math.Max(1, _nodes.Length);

            for (int cellIndex = 0; cellIndex < _cells.Length; cellIndex++)
            {
                Cell cell = _cells[cellIndex];
                var corners = new Vector3[8];
                Vector3 minimum = new(float.PositiveInfinity);
                Vector3 maximum = new(float.NegativeInfinity);
                Vector3 center = Vector3.Zero;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = _nodes[cell.Corners[corner]].Position - _worldOrigin;
                    corners[corner] = local;
                    minimum = Vector3.Min(minimum, local);
                    maximum = Vector3.Max(maximum, local);
                    center += local;
                }
                center /= 8f;
                elements[cellIndex] = new PhysicsGridElement(
                    cell.X, cell.Y, cell.Z, minimum, maximum, cell.MaterialId);
                skin[cellIndex] = new PhysicsGridSmoothSphere(
                    center, 0.5f, cell.MaterialId, cell.X, cell.Y, cell.Z)
                {
                    ExplicitCorners = corners,
                };
            }
            int previousTopology = Grid.TopologyVersion;
            Grid.ApplyDeformableGeometry(elements, skin, _worldOrigin, averageVelocity, _sleeping);
            SourceTopology = Grid.TopologyVersion;
            return previousTopology != Grid.TopologyVersion;
        }

        private Dictionary<(int X, int Y, int Z), NodeSnapshot> SnapshotNodes()
        {
            var snapshot = new Dictionary<(int X, int Y, int Z), NodeSnapshot>(_nodes.Length);
            for (int index = 0; index < _nodes.Length; index++)
            {
                Node node = _nodes[index];
                snapshot[_nodeKeys[index]] = new NodeSnapshot(
                    node.Position,
                    node.Previous,
                    node.Velocity,
                    _supportBindings[index].ToArray());
            }
            return snapshot;
        }

        private Dictionary<ConstraintRestKey, float> SnapshotConstraintRestLengths()
        {
            var snapshot = new Dictionary<ConstraintRestKey, float>(_constraints.Length);
            foreach (Constraint constraint in _constraints)
            {
                ConstraintRestKey key = CreateConstraintRestKey(
                    _nodeKeys[constraint.A], _nodeKeys[constraint.B]);
                snapshot[key] = constraint.RestLength;
            }
            return snapshot;
        }

        private static ConstraintRestKey CreateConstraintRestKey(
            (int X, int Y, int Z) first,
            (int X, int Y, int Z) second)
        {
            bool swap = first.X > second.X ||
                        (first.X == second.X && first.Y > second.Y) ||
                        (first.X == second.X && first.Y == second.Y && first.Z > second.Z);
            if (swap) (first, second) = (second, first);
            return new ConstraintRestKey(
                first.X, first.Y, first.Z,
                second.X, second.Y, second.Z);
        }

        private SupportBinding[][] CaptureSupportBindings(
            VoxelWorld world,
            IReadOnlyDictionary<(int X, int Y, int Z), NodeSnapshot>? previousNodes)
        {
            var bindings = Enumerable.Range(0, _nodes.Length)
                .Select(_ => new List<SupportBinding>())
                .ToArray();
            if (previousNodes is not null)
            {
                for (int index = 0; index < _nodeKeys.Length; index++)
                {
                    if (!previousNodes.TryGetValue(_nodeKeys[index], out NodeSnapshot previous)) continue;
                    foreach (SupportBinding binding in previous.Supports)
                    {
                        ushort material = world.GetBlockId(binding.X, binding.Y, binding.Z);
                        if (material != 0 && world.IsCollidableMaterial(material))
                            bindings[index].Add(binding);
                    }
                }
            }
            foreach (Cell cell in _cells)
            {
                // Saved soft bodies may already be visibly bent. Reconstruct glue from
                // the face that is touching terrain now, not from the undeformed source
                // lattice: the latter can be a full block away after a long cantilever
                // has sagged and made a correctly saved wall attachment fall on load.
                Vector3 center = cell.Corners
                    .Select(index => _nodes[index].Position)
                    .Aggregate(Vector3.Zero, (sum, point) => sum + point) / 8f;
                foreach ((int dx, int dy, int dz) in FaceDirections)
                {
                    int[] faceCorners = Enumerable.Range(0, 8)
                        .Where(corner =>
                        {
                            int cx = corner & 1;
                            int cy = (corner >> 1) & 1;
                            int cz = (corner >> 2) & 1;
                            return (dx > 0 && cx == 1) || (dx < 0 && cx == 0) ||
                                   (dy > 0 && cy == 1) || (dy < 0 && cy == 0) ||
                                   (dz > 0 && cz == 1) || (dz < 0 && cz == 0);
                        })
                        .ToArray();
                    Vector3 faceCenter = faceCorners
                        .Select(corner => _nodes[cell.Corners[corner]].Position)
                        .Aggregate(Vector3.Zero, (sum, point) => sum + point) / 4f;
                    Vector3 outward = faceCenter - center;
                    if (outward.LengthSquared() < 0.000001f)
                        outward = new Vector3(dx, dy, dz);
                    else
                        outward = Vector3.Normalize(outward);
                    Vector3 probe = faceCenter + outward * 0.18f;
                    int x = (int)MathF.Floor(probe.X);
                    int y = (int)MathF.Floor(probe.Y);
                    int z = (int)MathF.Floor(probe.Z);
                    ushort material = world.GetBlockId(x, y, z);
                    if (material == 0 || !world.IsCollidableMaterial(material)) continue;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        int cx = corner & 1;
                        int cy = (corner >> 1) & 1;
                        int cz = (corner >> 2) & 1;
                        if ((dx > 0 && cx == 1) || (dx < 0 && cx == 0) ||
                            (dy > 0 && cy == 1) || (dy < 0 && cy == 0) ||
                            (dz > 0 && cz == 1) || (dz < 0 && cz == 0))
                        {
                            int nodeIndex = cell.Corners[corner];
                            if (!bindings[nodeIndex].Any(binding =>
                                    binding.X == x && binding.Y == y && binding.Z == z))
                                bindings[nodeIndex].Add(new SupportBinding(
                                    x, y, z, _nodes[nodeIndex].Position));
                        }
                    }
                }
            }
            return bindings.Select(list => list.ToArray()).ToArray();
        }

        private void RefreshAnchors(VoxelWorld world)
        {
            for (int index = 0; index < _nodes.Length; index++)
            {
                ref Node node = ref _nodes[index];
                SupportBinding? active = null;
                foreach (SupportBinding binding in _supportBindings[index])
                {
                    ushort material = world.GetBlockId(binding.X, binding.Y, binding.Z);
                    if (material == 0 || !world.IsCollidableMaterial(material)) continue;
                    active = binding;
                    break;
                }
                if (active is SupportBinding support)
                {
                    node.Anchor = support.Anchor;
                    node.Pinned = true;
                    node.Position = node.Anchor;
                    node.Previous = node.Anchor;
                    node.Velocity = Vector3.Zero;
                }
                else if (node.Pinned)
                {
                    node.Pinned = false;
                    node.Previous = node.Position;
                    _sleeping = false;
                    _sleepTimer = 0f;
                    _groundContactTimer = 0f;
                }
            }
        }

        private void ProjectRigidClusters()
        {
            foreach (RigidCluster cluster in _rigidClusters)
            {
                Vector3 center = Vector3.Zero;
                foreach (int nodeIndex in cluster.Nodes) center += _nodes[nodeIndex].Position;
                center /= cluster.Nodes.Length;

                Vector3 currentX = Vector3.Zero;
                Vector3 currentY = Vector3.Zero;
                Vector3 currentZ = Vector3.Zero;
                for (int index = 0; index < cluster.Nodes.Length; index++)
                {
                    Vector3 currentOffset = _nodes[cluster.Nodes[index]].Position - center;
                    Vector3 restOffset = cluster.RestPositions[index] - cluster.RestCenter;
                    currentX += currentOffset * Vector3.Dot(restOffset, cluster.RestX);
                    currentY += currentOffset * Vector3.Dot(restOffset, cluster.RestY);
                    currentZ += currentOffset * Vector3.Dot(restOffset, cluster.RestZ);
                }
                if (currentX.LengthSquared() < 0.000001f ||
                    currentY.LengthSquared() < 0.000001f ||
                    currentZ.LengthSquared() < 0.000001f) continue;
                if (!TryExtractRigidRotation(ref currentX, ref currentY, ref currentZ)) continue;

                for (int index = 0; index < cluster.Nodes.Length; index++)
                {
                    ref Node node = ref _nodes[cluster.Nodes[index]];
                    if (node.Pinned) continue;
                    Vector3 restOffset = cluster.RestPositions[index] - cluster.RestCenter;
                    Vector3 desired = center +
                                      currentX * Vector3.Dot(restOffset, cluster.RestX) +
                                      currentY * Vector3.Dot(restOffset, cluster.RestY) +
                                      currentZ * Vector3.Dot(restOffset, cluster.RestZ);
                    Vector3 projection = desired - node.Position;
                    node.Position = desired;
                    // Shape matching removes internal distortion; it must not turn that
                    // correction into fresh kinetic energy on the next velocity rebuild.
                    // This was especially violent for an L-shaped rigid endpoint.
                    node.Previous += projection;
                }
            }
        }

        private static bool TryExtractRigidRotation(
            ref Vector3 columnX,
            ref Vector3 columnY,
            ref Vector3 columnZ)
        {
            // The covariance columns are not mutually orthogonal for an asymmetric
            // rigid cluster. Gram-Schmidt therefore mistakes an L-shaped cluster's
            // inertia cross-terms for rotation and injects a large false torque. The
            // orthogonal factor of the polar decomposition is the least-squares rigid
            // rotation for any connected shape, including long bars and L/T branches.
            float scale = MathF.Sqrt((columnX.LengthSquared() + columnY.LengthSquared() +
                                      columnZ.LengthSquared()) / 3f);
            if (!float.IsFinite(scale) || scale < 0.000001f) return false;
            Vector3 x = columnX / scale;
            Vector3 y = columnY / scale;
            Vector3 z = columnZ / scale;
            for (int iteration = 0; iteration < 12; iteration++)
            {
                float determinant = Vector3.Dot(x, Vector3.Cross(y, z));
                if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 0.0000001f)
                    return false;
                Vector3 inverseTransposeX = Vector3.Cross(y, z) / determinant;
                Vector3 inverseTransposeY = Vector3.Cross(z, x) / determinant;
                Vector3 inverseTransposeZ = Vector3.Cross(x, y) / determinant;
                Vector3 nextX = (x + inverseTransposeX) * 0.5f;
                Vector3 nextY = (y + inverseTransposeY) * 0.5f;
                Vector3 nextZ = (z + inverseTransposeZ) * 0.5f;
                float change = Vector3.DistanceSquared(x, nextX) +
                               Vector3.DistanceSquared(y, nextY) +
                               Vector3.DistanceSquared(z, nextZ);
                x = nextX;
                y = nextY;
                z = nextZ;
                if (change < 0.0000000001f) break;
            }

            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) ||
                x.LengthSquared() < 0.5f || y.LengthSquared() < 0.5f || z.LengthSquared() < 0.5f)
                return false;
            x = Vector3.Normalize(x);
            y -= x * Vector3.Dot(y, x);
            if (y.LengthSquared() < 0.000001f) return false;
            y = Vector3.Normalize(y);
            z = Vector3.Normalize(Vector3.Cross(x, y));
            columnX = x;
            columnY = y;
            columnZ = z;
            return true;
        }

        private static bool ResolveWorldCollision(ref Node node, VoxelWorld world)
        {
            int x = (int)MathF.Floor(node.Position.X);
            int y = (int)MathF.Floor(node.Position.Y);
            int z = (int)MathF.Floor(node.Position.Z);
            ushort material = world.GetBlockId(x, y, z);
            if (material == 0 || !world.IsCollidableMaterial(material)) return false;

            Vector3 beforeCorrection = node.Position;

            float left = node.Position.X - x;
            float right = x + 1f - node.Position.X;
            float bottom = node.Position.Y - y;
            float top = y + 1f - node.Position.Y;
            float back = node.Position.Z - z;
            float front = z + 1f - node.Position.Z;
            float nearest = MathF.Min(MathF.Min(MathF.Min(left, right), MathF.Min(bottom, top)), MathF.Min(back, front));
            // Prefer the face crossed by the trajectory. Lattice nodes commonly sit
            // exactly on a cell's X/Z boundary; choosing the numerically nearest face
            // there ejects a falling node sideways through the edge of a floor tile.
            if (node.Previous.Y >= y + 1f && node.Position.Y <= y + 1f)
            { node.Position.Y = y + 1f + NodeRadius; node.Velocity.Y = MathF.Max(0f, node.Velocity.Y); }
            else if (node.Previous.Y <= y && node.Position.Y >= y)
            { node.Position.Y = y - NodeRadius; node.Velocity.Y = MathF.Min(0f, node.Velocity.Y); }
            else if (node.Previous.X <= x && node.Position.X >= x)
            { node.Position.X = x - NodeRadius; node.Velocity.X = MathF.Min(0f, node.Velocity.X); }
            else if (node.Previous.X >= x + 1f && node.Position.X <= x + 1f)
            { node.Position.X = x + 1f + NodeRadius; node.Velocity.X = MathF.Max(0f, node.Velocity.X); }
            else if (node.Previous.Z <= z && node.Position.Z >= z)
            { node.Position.Z = z - NodeRadius; node.Velocity.Z = MathF.Min(0f, node.Velocity.Z); }
            else if (node.Previous.Z >= z + 1f && node.Position.Z <= z + 1f)
            { node.Position.Z = z + 1f + NodeRadius; node.Velocity.Z = MathF.Max(0f, node.Velocity.Z); }
            else if (nearest == top) { node.Position.Y = y + 1f + NodeRadius; node.Velocity.Y = MathF.Max(0f, node.Velocity.Y); }
            else if (nearest == bottom) { node.Position.Y = y - NodeRadius; node.Velocity.Y = MathF.Min(0f, node.Velocity.Y); }
            else if (nearest == left) { node.Position.X = x - NodeRadius; node.Velocity.X = MathF.Min(0f, node.Velocity.X); }
            else if (nearest == right) { node.Position.X = x + 1f + NodeRadius; node.Velocity.X = MathF.Max(0f, node.Velocity.X); }
            else if (nearest == back) { node.Position.Z = z - NodeRadius; node.Velocity.Z = MathF.Min(0f, node.Velocity.Z); }
            else { node.Position.Z = z + 1f + NodeRadius; node.Velocity.Z = MathF.Max(0f, node.Velocity.Z); }

            // Position-based collision projection is not an impulse. If Previous is
            // left inside the solid, the end-of-step velocity reconstruction treats
            // this depenetration distance as fresh outward speed and the soft cell
            // bounces forever. Move the history by the same projection so contact
            // removes only the penetrating normal motion and can actually settle.
            Vector3 correction = node.Position - beforeCorrection;
            if (MathF.Abs(correction.X) > 0.000001f) node.Previous.X = node.Position.X;
            if (MathF.Abs(correction.Y) > 0.000001f) node.Previous.Y = node.Position.Y;
            if (MathF.Abs(correction.Z) > 0.000001f) node.Previous.Z = node.Position.Z;
            return true;
        }

        private static bool HasWorldSupportImmediatelyBelow(Node node, VoxelWorld world)
        {
            Vector3 probe = node.Position - new Vector3(0f, NodeRadius + 0.04f, 0f);
            int x = (int)MathF.Floor(probe.X);
            int y = (int)MathF.Floor(probe.Y);
            int z = (int)MathF.Floor(probe.Z);
            ushort material = world.GetBlockId(x, y, z);
            return material != 0 && world.IsCollidableMaterial(material);
        }

        private static float MaterialStiffness(BlockDefinition material)
        {
            // These are soft solids, not cloth. They may squash locally, but a short
            // beam must carry a rigid payload without turning into a long rubber rope.
            if (material.Tags.Any(tag => tag.Equals("foam", StringComparison.OrdinalIgnoreCase))) return 0.80f;
            if (material.Tags.Any(tag => tag.Equals("gel", StringComparison.OrdinalIgnoreCase))) return 0.86f;
            return 0.88f;
        }

        private static bool HasInvalidSavedDeformation(
            PhysicsGrid grid,
            IReadOnlyDictionary<(int X, int Y, int Z), PhysicsGridSmoothSphere> smoothByCell)
        {
            // Version 1 of the core solver rebuilt its topology from each deformed
            // cell's AABB. After save/load, rotated neighbours no longer shared the
            // same corners, so every reload promoted the previous visual error into a
            // new rest shape. Reject only physically impossible per-voxel edges here;
            // ordinary bending and compression remain intact.
            int[] edgeA = [0, 0, 0, 1, 1, 2, 2, 3, 4, 4, 5, 6];
            int[] edgeB = [1, 2, 4, 3, 5, 3, 6, 7, 5, 6, 7, 7];
            foreach (PhysicsGridSmoothSphere surface in smoothByCell.Values)
            {
                for (int edge = 0; edge < edgeA.Length; edge++)
                {
                    Vector3 a = grid.LocalToWorld(surface.ExplicitCorners![edgeA[edge]]);
                    Vector3 b = grid.LocalToWorld(surface.ExplicitCorners![edgeB[edge]]);
                    float length = Vector3.Distance(a, b);
                    if (!float.IsFinite(length) || length is < 0.35f or > 1.28f)
                        return true;
                }
            }
            return false;
        }

        private static RigidCluster[] BuildRigidClusters(
            IReadOnlyList<Cell> cells,
            IReadOnlyList<Node> nodes,
            PhysicsGrid grid,
            bool adoptCurrentConfiguration)
        {
            var hardByCell = cells
                .Where(cell => !cell.Soft)
                .ToDictionary(cell => (cell.X, cell.Y, cell.Z));
            var remaining = hardByCell.Keys.ToHashSet();
            var clusters = new List<RigidCluster>();
            while (remaining.Count > 0)
            {
                (int X, int Y, int Z) seed = remaining.First();
                remaining.Remove(seed);
                var queue = new Queue<(int X, int Y, int Z)>();
                queue.Enqueue(seed);
                var clusterCells = new List<Cell>();
                while (queue.Count > 0)
                {
                    (int X, int Y, int Z) coordinate = queue.Dequeue();
                    clusterCells.Add(hardByCell[coordinate]);
                    foreach ((int dx, int dy, int dz) in FaceDirections)
                    {
                        var adjacent = (coordinate.X + dx, coordinate.Y + dy, coordinate.Z + dz);
                        if (!remaining.Remove(adjacent)) continue;
                        queue.Enqueue(adjacent);
                    }
                }

                int[] clusterNodes = clusterCells
                    .SelectMany(cell => cell.Corners)
                    .Distinct()
                    .ToArray();
                // Rebuilding after a topology edit must be mechanically quiet. In
                // particular, adding the first perpendicular hard cell must not make
                // an already bent endpoint solve back toward its original straight
                // lattice and release that projection as a violent impulse.
                Vector3[] restPositions = clusterNodes
                    .Select(index => adoptCurrentConfiguration
                        ? nodes[index].Position
                        : nodes[index].Rest)
                    .ToArray();
                Vector3 restCenter = restPositions.Aggregate(Vector3.Zero, (sum, point) => sum + point) /
                                     restPositions.Length;
                clusters.Add(new RigidCluster(
                    clusterNodes,
                    restPositions,
                    restCenter,
                    Vector3.Normalize(grid.LocalDirectionToWorld(Vector3.UnitX)),
                    Vector3.Normalize(grid.LocalDirectionToWorld(Vector3.UnitY)),
                    Vector3.Normalize(grid.LocalDirectionToWorld(Vector3.UnitZ))));
            }
            return clusters.ToArray();
        }

        private static void AddBendingConstraints(
            IReadOnlyList<Cell> cells,
            IReadOnlyList<Node> nodes,
            IDictionary<(int A, int B), Constraint> constraints)
        {
            var byCell = cells.ToDictionary(cell => (cell.X, cell.Y, cell.Z));
            foreach (Cell cell in cells)
            for (int axis = 0; axis < 3; axis++)
            {
                (int X, int Y, int Z) neighborKey = axis switch
                {
                    0 => (cell.X + 1, cell.Y, cell.Z),
                    1 => (cell.X, cell.Y + 1, cell.Z),
                    _ => (cell.X, cell.Y, cell.Z + 1),
                };
                if (!byCell.TryGetValue(neighborKey, out Cell neighbor)) continue;

                // A shared face alone behaves like a hinge. Four two-cell-span rods
                // resist that hinge without making an ordinary soft cell perfectly
                // rigid. If either side is solid, keep the payload/interface stiffer.
                float stiffness = cell.Soft && neighbor.Soft ? 0.58f :
                    cell.Soft || neighbor.Soft ? 0.88f : 0.998f;
                for (int bitA = 0; bitA < 2; bitA++)
                for (int bitB = 0; bitB < 2; bitB++)
                {
                    int first = axis switch
                    {
                        0 => Corner(0, bitA, bitB),
                        1 => Corner(bitA, 0, bitB),
                        _ => Corner(bitA, bitB, 0),
                    };
                    int second = axis switch
                    {
                        0 => Corner(1, bitA, bitB),
                        1 => Corner(bitA, 1, bitB),
                        _ => Corner(bitA, bitB, 1),
                    };
                    int nodeA = cell.Corners[first];
                    int nodeB = neighbor.Corners[second];
                    AddConstraint(nodeA, nodeB, stiffness);
                }
            }

            void AddConstraint(int first, int second, float stiffness)
            {
                var pair = first < second ? (first, second) : (second, first);
                float rest = Vector3.Distance(nodes[first].Rest, nodes[second].Rest);
                if (!constraints.TryGetValue(pair, out Constraint existing) || stiffness > existing.Stiffness)
                    constraints[pair] = new Constraint(pair.Item1, pair.Item2, rest, stiffness);
            }

            static int Corner(int x, int y, int z) => x + y * 2 + z * 4;
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private struct Node
        {
            public Vector3 Position;
            public Vector3 Previous;
            public Vector3 Velocity;
            public Vector3 Anchor;
            public readonly Vector3 Rest;
            public float Mass;
            public bool Pinned;
            public Node(Vector3 position, float density)
            {
                Position = Previous = Anchor = position;
                Rest = position;
                Velocity = Vector3.Zero;
                Mass = Math.Clamp(density / 8f, 0.025f, 4f);
                Pinned = false;
            }

            public void Reset(Vector3 position, float density)
            {
                Position = Previous = Anchor = position;
                Velocity = Vector3.Zero;
                Mass = Math.Clamp(density / 8f, 0.025f, 4f);
                Pinned = false;
            }

            public void Reset(Vector3 position, Vector3 previous, Vector3 velocity, float density)
            {
                Position = position;
                Previous = previous;
                Anchor = position;
                Velocity = velocity;
                Mass = Math.Clamp(density / 8f, 0.025f, 4f);
                Pinned = false;
            }
        }

        private readonly record struct Constraint(int A, int B, float RestLength, float Stiffness);
        private readonly record struct ConstraintRestKey(
            int AX, int AY, int AZ,
            int BX, int BY, int BZ);
        private readonly record struct SupportBinding(int X, int Y, int Z, Vector3 Anchor);
        private readonly record struct NodeSnapshot(
            Vector3 Position,
            Vector3 Previous,
            Vector3 Velocity,
            SupportBinding[] Supports);
        private readonly record struct RigidCluster(
            int[] Nodes,
            Vector3[] RestPositions,
            Vector3 RestCenter,
            Vector3 RestX,
            Vector3 RestY,
            Vector3 RestZ);
        private readonly record struct Cell(
            int X,
            int Y,
            int Z,
            ushort MaterialId,
            int[] Corners,
            Vector3[] RestCorners,
            bool Soft);
    }
}
