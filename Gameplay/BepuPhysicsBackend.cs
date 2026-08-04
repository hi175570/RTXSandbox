using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

/// <summary>BEPUphysics 2 compound-body backend for detached voxel grids.</summary>
public sealed class BepuPhysicsBackend : IDisposable
{
    private readonly VoxelWorld _world;
    private readonly BufferPool _pool = new();
    private readonly ThreadDispatcher _dispatcher;
    private readonly Simulation _simulation;
    private readonly Dictionary<ulong, DynamicBody> _dynamicBodies = new();
    private readonly Dictionary<ChunkCoordinate, List<StaticBody>> _staticChunks = new();
    private readonly HashSet<ChunkCoordinate> _dirtyChunks = new();
    private readonly HashSet<ChunkCoordinate> _urgentDirtyChunks = new();
    private const float FixedStepSeconds = 1f / 120f;
    private const int MaximumCatchUpSteps = 6;
    private float _timeAccumulator;
    private bool _disposed;

    public string Name => "BEPUphysics 2";
    public int DynamicBodyCount => _dynamicBodies.Count;
    public int StaticChunkBodyCount => _staticChunks.Values.Sum(list => list.Count);
    public int CollisionStepsLastFrame { get; private set; }

    public BepuPhysicsBackend(VoxelWorld world)
    {
        _world = world;
        int threads = Math.Max(1, Environment.ProcessorCount > 4 ? Environment.ProcessorCount - 2 : Environment.ProcessorCount - 1);
        _dispatcher = new ThreadDispatcher(threads);
        _simulation = Simulation.Create(
            _pool,
            // Contacts recover overlap without restitution. Keeping the recovery speed
            // below visible launch velocity prevents a landed Grid from repeatedly
            // throwing itself back into the air, while moderate friction still lets an
            // off-balance structure continue rotating on the floor.
            new NarrowPhaseCallbacks(new SpringSettings(20f, 1f), 0.35f, 0.55f),
            new PoseIntegratorCallbacks(new Vector3(0f, -18f, 0f), 0.035f, 0.08f),
            new SolveDescription(8, 2));
        _world.DetailedVoxelChanged += OnVoxelChanged;
        _world.MicroVoxelCellChanged += OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded += MarkAllStaticDirty;
        _world.WorldReloaded += OnWorldReloaded;
        _world.ChunkGenerated += OnChunkGenerated;
        MarkAllStaticDirty();
    }

    public void Step(float deltaSeconds)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BepuPhysicsBackend));
        RebuildDirtyChunks(maximumChunks: 1);
        ReconcileDynamicBodies();
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.25f);
        _timeAccumulator = MathF.Min(
            _timeAccumulator + deltaSeconds,
            FixedStepSeconds * MaximumCatchUpSteps);
        CollisionStepsLastFrame = 0;
        while (_timeAccumulator + 0.0000001f >= FixedStepSeconds &&
               CollisionStepsLastFrame < MaximumCatchUpSteps)
        {
            _simulation.Timestep(FixedStepSeconds, _dispatcher);
            _timeAccumulator -= FixedStepSeconds;
            CollisionStepsLastFrame++;
        }
        SynchronizeGrids();
    }

    private void ReconcileDynamicBodies()
    {
        HashSet<ulong> live = _world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        foreach (ulong id in _dynamicBodies.Keys.Where(id => !live.Contains(id)).ToArray()) RemoveDynamic(id);
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            int signature = grid.TopologyVersion;
            if (_dynamicBodies.TryGetValue(grid.Id, out DynamicBody existing) && existing.Signature == signature) continue;
            if (_dynamicBodies.ContainsKey(grid.Id)) RemoveDynamic(grid.Id);
            CreateDynamic(grid, signature);
        }
    }

    private void CreateDynamic(PhysicsGrid grid, int signature)
    {
        using var builder = new CompoundBuilder(_pool, _simulation.Shapes, grid.Elements.Count);
        foreach (PhysicsGridElement element in grid.Elements)
        {
            Vector3 size = Vector3.Max(element.Maximum - element.Minimum, new Vector3(0.01f));
            // Use the same mass model as PhysicsGrid so BEPU's compound center and
            // the rendered/local Grid origin cannot disagree.
            float mass = MathF.Max(
                0.001f,
                element.Volume * MathF.Max(0.05f, grid.DensityOf(element.MaterialId)));
            builder.Add(new Box(size.X, size.Y, size.Z),
                new RigidPose(element.Center - grid.CenterOfMassLocal, Quaternion.Identity), mass);
        }
        builder.BuildDynamicCompound(out Buffer<CompoundChild> children, out BodyInertia inertia, out Vector3 center);
        TypedIndex shape = _simulation.Shapes.Add(new Compound(children));
        Vector3 bodyPosition = grid.Position + Vector3.Transform(center, grid.Orientation);
        BodyDescription description = BodyDescription.CreateDynamic(
            new RigidPose(bodyPosition, grid.Orientation), inertia,
            new CollidableDescription(shape, 0.1f, ContinuousDetection.Continuous()),
            new BodyActivityDescription(0.02f));
        description.Velocity = new BodyVelocity(grid.LinearVelocity, grid.AngularVelocity);
        BodyHandle handle = _simulation.Bodies.Add(description);
        if (grid.IsSleeping)
        {
            BodyReference sleepingBody = _simulation.Bodies[handle];
            sleepingBody.Awake = false;
        }
        _dynamicBodies[grid.Id] = new DynamicBody(handle, shape, center, signature);
    }

    private void SynchronizeGrids()
    {
        bool changed = false;
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (!_dynamicBodies.TryGetValue(grid.Id, out DynamicBody entry)) continue;
            BodyReference body = _simulation.Bodies[entry.Handle];
            Quaternion orientation = body.Pose.Orientation;
            Vector3 gridPosition = body.Pose.Position - Vector3.Transform(entry.CenterOffset, orientation);
            changed |= grid.SetPhysicsState(
                gridPosition, orientation, body.Velocity.Linear, body.Velocity.Angular, !body.Awake);
        }
        if (changed) _world.PhysicsGrids.NotifyChanged();
    }

    private void RebuildDirtyChunks(int maximumChunks)
    {
        int remaining = Math.Max(0, maximumChunks);
        ChunkCoordinate[] selected = _urgentDirtyChunks.Take(remaining)
            .Concat(_dirtyChunks.Where(chunk => !_urgentDirtyChunks.Contains(chunk)).Take(remaining))
            .Take(remaining)
            .ToArray();
        foreach (ChunkCoordinate chunk in selected)
        {
            _urgentDirtyChunks.Remove(chunk);
            _dirtyChunks.Remove(chunk);
            RemoveStaticChunk(chunk);
            CreateStaticChunk(chunk);
        }
    }

    private void CreateStaticChunk(ChunkCoordinate chunk)
    {
        var bodies = new List<StaticBody>();
        VoxelChunkSnapshot snapshot = _world.SnapshotChunk(chunk);
        foreach (IGrouping<(int X, int Z), StoredVoxel> column in snapshot.Voxels
                     .Where(voxel => _world.IsCollidableMaterial(voxel.MaterialId))
                     .GroupBy(voxel => (voxel.LocalX, voxel.LocalZ)))
        {
            int x = chunk.MinimumX + column.Key.X;
            int z = chunk.MinimumZ + column.Key.Z;
            int runStart = int.MinValue;
            int previousY = int.MinValue;
            foreach (StoredVoxel voxel in column.OrderBy(voxel => voxel.Y))
            {
                if (runStart == int.MinValue) runStart = voxel.Y;
                else if (voxel.Y != previousY + 1)
                {
                    AddStaticBox(new VoxelAabb(
                        new Vector3(x, runStart, z),
                        new Vector3(x + 1f, previousY + 1f, z + 1f), 0), bodies);
                    runStart = voxel.Y;
                }
                previousY = voxel.Y;
            }
            if (runStart != int.MinValue)
                AddStaticBox(new VoxelAabb(
                    new Vector3(x, runStart, z),
                    new Vector3(x + 1f, previousY + 1f, z + 1f), 0), bodies);
        }

        foreach (StoredMicroVoxel micro in snapshot.MicroVoxels)
        {
            float inverse = 1f / micro.Snapshot.Resolution;
            foreach (MicroVoxelBox box in micro.Snapshot.Boxes)
            {
                if (!_world.IsCollidableMaterial(box.MaterialId)) continue;
                AddStaticBox(new VoxelAabb(
                    new Vector3(
                        chunk.MinimumX + micro.LocalX + box.MinX * inverse,
                        micro.Y + box.MinY * inverse,
                        chunk.MinimumZ + micro.LocalZ + box.MinZ * inverse),
                    new Vector3(
                        chunk.MinimumX + micro.LocalX + box.MaxX * inverse,
                        micro.Y + box.MaxY * inverse,
                        chunk.MinimumZ + micro.LocalZ + box.MaxZ * inverse),
                    box.MaterialId), bodies);
            }
        }
        if (bodies.Count > 0) _staticChunks[chunk] = bodies;
    }

    private void AddStaticBox(VoxelAabb voxel, List<StaticBody> bodies)
    {
        Vector3 size = voxel.Maximum - voxel.Minimum;
        Vector3 center = (voxel.Minimum + voxel.Maximum) * 0.5f;
        TypedIndex shape = _simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));
        StaticHandle handle = _simulation.Statics.Add(new StaticDescription(center, shape));
        bodies.Add(new StaticBody(handle, shape));
    }

    private void RemoveDynamic(ulong id)
    {
        if (!_dynamicBodies.Remove(id, out DynamicBody entry)) return;
        _simulation.Bodies.Remove(entry.Handle);
        _simulation.Shapes.Remove(entry.Shape);
    }

    private void RemoveStaticChunk(ChunkCoordinate chunk)
    {
        if (!_staticChunks.Remove(chunk, out List<StaticBody>? bodies)) return;
        foreach (StaticBody body in bodies)
        {
            _simulation.Statics.Remove(body.Handle);
            _simulation.Shapes.Remove(body.Shape);
        }
    }

    private void OnVoxelChanged(VoxelChangedEvent change) => MarkChunkDirty(change.X, change.Z, urgent: true);
    private void OnMicroVoxelCellChanged(int x, int y, int z) => MarkChunkDirty(x, z, urgent: true);
    private void OnWorldReloaded()
    {
        // A fractional step belongs to the old world. Never carry it across a load or
        // regeneration boundary, where it could advance newly restored bodies early.
        _timeAccumulator = 0f;
        foreach (ulong id in _dynamicBodies.Keys.ToArray()) RemoveDynamic(id);
        foreach (ChunkCoordinate chunk in _staticChunks.Keys.ToArray()) RemoveStaticChunk(chunk);
        _dirtyChunks.Clear();
        _urgentDirtyChunks.Clear();
        MarkAllStaticDirty();
    }

    private void MarkChunkDirty(int x, int z, bool urgent = false)
    {
        ChunkCoordinate chunk = ChunkCoordinate.FromVoxel(x, z);
        _dirtyChunks.Add(chunk);
        if (urgent) _urgentDirtyChunks.Add(chunk);
    }
    private void OnChunkGenerated(ChunkCoordinate chunk) => _dirtyChunks.Add(chunk);
    private void MarkAllStaticDirty()
    {
        foreach (ChunkCoordinate chunk in _staticChunks.Keys)
            _dirtyChunks.Add(chunk);
        foreach (ChunkCoordinate chunk in _world.SnapshotLoadedChunks())
            _dirtyChunks.Add(chunk);
    }
    internal bool IsStaticChunkRegistered(ChunkCoordinate chunk) => _staticChunks.ContainsKey(chunk);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.DetailedVoxelChanged -= OnVoxelChanged;
        _world.MicroVoxelCellChanged -= OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded -= MarkAllStaticDirty;
        _world.WorldReloaded -= OnWorldReloaded;
        _world.ChunkGenerated -= OnChunkGenerated;
        _simulation.Dispose();
        _dispatcher.Dispose();
        _pool.Clear();
    }

    private readonly record struct DynamicBody(BodyHandle Handle, TypedIndex Shape, Vector3 CenterOffset, int Signature);
    private readonly record struct StaticBody(StaticHandle Handle, TypedIndex Shape);

    private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        private readonly Vector3 _gravity;
        private readonly float _linearDamping;
        private readonly float _angularDamping;
        private Vector3Wide _gravityDt;
        private Vector<float> _linearDt;
        private Vector<float> _angularDt;
        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.ConserveMomentum;
        public bool AllowSubstepsForUnconstrainedBodies => true;
        public bool IntegrateVelocityForKinematics => false;
        public PoseIntegratorCallbacks(Vector3 gravity, float linearDamping, float angularDamping)
        { _gravity = gravity; _linearDamping = linearDamping; _angularDamping = angularDamping; _gravityDt = default; _linearDt = default; _angularDt = default; }
        public void Initialize(Simulation simulation) { }
        public void PrepareForIntegration(float dt)
        {
            _gravityDt = Vector3Wide.Broadcast(_gravity * dt);
            _linearDt = new Vector<float>(MathF.Pow(Math.Clamp(1f - _linearDamping, 0f, 1f), dt));
            _angularDt = new Vector<float>(MathF.Pow(Math.Clamp(1f - _angularDamping, 0f, 1f), dt));
        }
        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
            BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
            ref BodyVelocityWide velocity)
        { velocity.Linear = (velocity.Linear + _gravityDt) * _linearDt; velocity.Angular *= _angularDt; }
    }

    private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        private SpringSettings _spring;
        private readonly float _maxRecovery;
        private readonly float _friction;
        public NarrowPhaseCallbacks(SpringSettings spring, float maxRecovery, float friction)
        { _spring = spring; _maxRecovery = maxRecovery; _friction = friction; }
        public void Initialize(Simulation simulation) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) =>
            a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
            out PairMaterialProperties material) where TManifold : unmanaged, IContactManifold<TManifold>
        { material.FrictionCoefficient = _friction; material.MaximumRecoveryVelocity = _maxRecovery; material.SpringSettings = _spring; return true; }
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
            ref ConvexContactManifold manifold) => true;
        public void Dispose() { }
    }
}
