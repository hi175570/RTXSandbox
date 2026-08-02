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
    private const int ChunkSize = 16;
    private readonly VoxelWorld _world;
    private readonly BufferPool _pool = new();
    private readonly ThreadDispatcher _dispatcher;
    private readonly Simulation _simulation;
    private readonly Dictionary<ulong, DynamicBody> _dynamicBodies = new();
    private readonly Dictionary<(int X, int Z), List<StaticBody>> _staticChunks = new();
    private readonly HashSet<(int X, int Z)> _dirtyChunks = new();
    private bool _disposed;

    public string Name => "BEPUphysics 2";
    public int DynamicBodyCount => _dynamicBodies.Count;
    public int StaticChunkBodyCount => _staticChunks.Values.Sum(list => list.Count);
    public int CollisionStepsLastFrame { get; private set; } = 1;

    public BepuPhysicsBackend(VoxelWorld world)
    {
        _world = world;
        int threads = Math.Max(1, Environment.ProcessorCount > 4 ? Environment.ProcessorCount - 2 : Environment.ProcessorCount - 1);
        _dispatcher = new ThreadDispatcher(threads);
        _simulation = Simulation.Create(
            _pool,
            new NarrowPhaseCallbacks(new SpringSettings(30f, 1f), 1.5f, 0.72f),
            new PoseIntegratorCallbacks(new Vector3(0f, -18f, 0f), 0.035f, 0.08f),
            new SolveDescription(8, 2));
        _world.DetailedVoxelChanged += OnVoxelChanged;
        _world.MicroVoxelsChanged += MarkAllStaticDirty;
        _world.WorldReloaded += MarkAllStaticDirty;
        MarkAllStaticDirty();
    }

    public void Step(float deltaSeconds)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BepuPhysicsBackend));
        RebuildDirtyChunks();
        ReconcileDynamicBodies();
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.05f);
        CollisionStepsLastFrame = Math.Clamp((int)MathF.Ceiling(deltaSeconds / (1f / 120f)), 1, 6);
        float step = deltaSeconds / CollisionStepsLastFrame;
        for (int i = 0; i < CollisionStepsLastFrame; i++) _simulation.Timestep(step, _dispatcher);
        SynchronizeGrids();
    }

    private void ReconcileDynamicBodies()
    {
        HashSet<ulong> live = _world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        foreach (ulong id in _dynamicBodies.Keys.Where(id => !live.Contains(id)).ToArray()) RemoveDynamic(id);
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            int signature = Signature(grid);
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
            float mass = MathF.Max(0.001f, element.Volume);
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
            grid.SetPhysicsState(gridPosition, orientation, body.Velocity.Linear, body.Velocity.Angular, !body.Awake);
            changed = true;
        }
        if (changed) _world.PhysicsGrids.NotifyChanged();
    }

    private void RebuildDirtyChunks()
    {
        foreach ((int x, int z) chunk in _dirtyChunks.ToArray())
        {
            _dirtyChunks.Remove(chunk);
            RemoveStaticChunk(chunk);
            CreateStaticChunk(chunk.x, chunk.z);
        }
    }

    private void CreateStaticChunk(int chunkX, int chunkZ)
    {
        int startX = chunkX * ChunkSize;
        int startZ = chunkZ * ChunkSize;
        var bodies = new List<StaticBody>();
        var boxes = new List<VoxelAabb>(8);
        for (int z = startZ; z < Math.Min(startZ + ChunkSize, VoxelWorld.Depth); z++)
        for (int y = 0; y < VoxelWorld.Height; y++)
        for (int x = startX; x < Math.Min(startX + ChunkSize, VoxelWorld.Width); x++)
        {
            boxes.Clear();
            _world.GetStaticCollisionBoxes(x, y, z, boxes);
            foreach (VoxelAabb voxel in boxes)
            {
                Vector3 size = voxel.Maximum - voxel.Minimum;
                Vector3 center = (voxel.Minimum + voxel.Maximum) * 0.5f;
                TypedIndex shape = _simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));
                StaticHandle handle = _simulation.Statics.Add(new StaticDescription(center, shape));
                bodies.Add(new StaticBody(handle, shape));
            }
        }
        if (bodies.Count > 0) _staticChunks[(chunkX, chunkZ)] = bodies;
    }

    private void RemoveDynamic(ulong id)
    {
        if (!_dynamicBodies.Remove(id, out DynamicBody entry)) return;
        _simulation.Bodies.Remove(entry.Handle);
        _simulation.Shapes.Remove(entry.Shape);
    }

    private void RemoveStaticChunk((int X, int Z) chunk)
    {
        if (!_staticChunks.Remove(chunk, out List<StaticBody>? bodies)) return;
        foreach (StaticBody body in bodies)
        {
            _simulation.Statics.Remove(body.Handle);
            _simulation.Shapes.Remove(body.Shape);
        }
    }

    private void OnVoxelChanged(VoxelChangedEvent change) => MarkChunkDirty(change.X, change.Z);
    private void MarkChunkDirty(int x, int z)
    {
        if (x < 0 || z < 0 || x >= VoxelWorld.Width || z >= VoxelWorld.Depth) return;
        _dirtyChunks.Add((x / ChunkSize, z / ChunkSize));
    }
    private void MarkAllStaticDirty()
    {
        for (int z = 0; z < VoxelWorld.Depth; z += ChunkSize)
        for (int x = 0; x < VoxelWorld.Width; x += ChunkSize)
            _dirtyChunks.Add((x / ChunkSize, z / ChunkSize));
    }
    private static int Signature(PhysicsGrid grid)
    {
        var hash = new HashCode();
        foreach (PhysicsGridElement element in grid.Elements)
        {
            hash.Add(element.CellX); hash.Add(element.CellY); hash.Add(element.CellZ);
            hash.Add(element.Minimum); hash.Add(element.Maximum); hash.Add(element.MaterialId);
        }
        return hash.ToHashCode();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.DetailedVoxelChanged -= OnVoxelChanged;
        _world.MicroVoxelsChanged -= MarkAllStaticDirty;
        _world.WorldReloaded -= MarkAllStaticDirty;
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
