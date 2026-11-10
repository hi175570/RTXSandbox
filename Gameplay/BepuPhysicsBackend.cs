using System.Numerics;
using System.Diagnostics;
using System.Buffers;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using RTXSandbox.Engine;
using RTXSandbox.Infrastructure;
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
    private readonly HashSet<ulong> _lightweightDebris = new();
    private readonly Dictionary<ulong, float> _debrisQuietSeconds = new();
    private readonly List<PhysicsGrid> _lightweightMoving = new();
    private readonly List<ulong> _lightweightStaleIds = new();
    private readonly Dictionary<(int X, int Y, int Z), List<LightweightEntry>> _lightweightBuckets = new();
    private readonly Stack<List<LightweightEntry>> _lightweightBucketPool = new();
    private int _lightweightPhase;
    private readonly Dictionary<ulong, AnalyticSphereBody> _analyticSphereBodies = new();
    private readonly Dictionary<ChunkCoordinate, List<StaticBody>> _staticChunks = new();
    private readonly HashSet<ChunkCoordinate> _dirtyChunks = new();
    private readonly HashSet<ChunkCoordinate> _urgentDirtyChunks = new();
    private readonly HashSet<ChunkCoordinate> _pendingStaticEvictions = new();
    private readonly HashSet<ChunkCoordinate> _partiallyEvictedStaticChunks = new();
    private readonly Dictionary<ChunkCoordinate, int> _colliderVersions = new();
    private readonly Dictionary<ChunkCoordinate, long> _colliderLastDirtyTimestamps = new();
    private readonly Dictionary<ChunkCoordinate, int> _colliderDirtyCounts = new();
    private ColliderBuildJob? _colliderBuildJob;
    private BlockDefinition[] _blockDefinitions;
    private int _colliderEpoch;
    private Vector3 _simulationFocus;
    private int _colliderResidencySignature = int.MinValue;
    // Player/world collision is solved directly by PlayerController. BEPU only
    // needs a small warm ring here; every dynamic grid and analytic sphere adds
    // its own collider residency around its complete bounds below.
    private const int PlayerColliderRadiusChunks = 1;
    private const int AnalyticSphereColliderRadiusChunks = 1;
    private const double ColliderEditCoalesceMilliseconds = 25.0;
    private const double RepeatedColliderEditCoalesceMilliseconds = 75.0;
    private const int MaximumStaticBodiesRemovedPerFrame = 8;
    private const float FixedStepSeconds = 1f / 120f;
    private const float MediumDebrisStepSeconds = 1f / 60f;
    private const float HeavyDebrisStepSeconds = 1f / 30f;
    private const int MaximumCatchUpSteps = 2;
    private const int MediumDynamicBodyThreshold = 256;
    private const int HeavyDynamicBodyThreshold = 1_024;
    // A 20k shard field cannot afford 20k compound bodies in a 30 Hz iterative
    // constraint solve.  Nearby fragments keep exact BEPU contacts; excess debris
    // retains motion and collision in the bounded spatial-hash solver below.  This is
    // a physics LOD, not deletion: every shard remains a PhysicsGrid and can return to
    // the exact tier when the population falls or it becomes relevant to the player.
    private const int MaximumExactDebrisBodies = 8;
    private const float LightweightGravity = -18f;
    private const float LightweightCollisionCellSize = 0.5f;
    private const float DebrisQuietLinearSpeed = 0.32f;
    private const float DebrisQuietAngularSpeed = 1.15f;
    private const float DebrisQuietSleepSeconds = 0.55f;
    private float _timeAccumulator;
    private Vector3 _pendingPlayerPosition;
    private Vector3 _pendingPlayerVelocity;
    private bool _hasPendingPlayerPush;
    private bool _dynamicTopologyDirty = true;
    private bool _disposed;

    public string Name => "BEPUphysics 2";
    public int DynamicBodyCount => _dynamicBodies.Count + _analyticSphereBodies.Count;
    public int LightweightDebrisCount => _lightweightDebris.Count;
    public int VirtualizedDebrisCount => _world.PhysicsGrids.Grids.Count(ShouldVirtualizeDebris);
    public int AnalyticSphereBodyCount => _analyticSphereBodies.Count;
    public int StaticChunkBodyCount => _staticChunks.Values.Sum(list => list.Count);
    public int CollisionStepsLastFrame { get; private set; }
    public int PendingColliderBuildCount => _dirtyChunks.Count;
    public int ColliderBuildCount { get; private set; }
    public double ColliderBuildTotalMilliseconds { get; private set; }
    public double MaximumColliderBuildMilliseconds { get; private set; }
    public double MaximumDynamicReconcileMilliseconds { get; private set; }
    public double MaximumSimulationMilliseconds { get; private set; }
    public double MaximumLightweightDebrisMilliseconds { get; private set; }
    public double MaximumSingleSimulationStepMilliseconds { get; private set; }
    public double MaximumStaticResidencyMilliseconds { get; private set; }
    public int DynamicReconcileCount { get; private set; }

    public BepuPhysicsBackend(VoxelWorld world, Vector3 simulationFocus = default)
    {
        _world = world;
        _simulationFocus = simulationFocus;
        _blockDefinitions = world.CreateBlockDefinitionLookupSnapshot();
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
        _world.BlockDefinitionsChanged += OnBlockDefinitionsChanged;
        _world.PhysicsGrids.TopologyChanged += OnPhysicsGridTopologyChanged;
        MarkAllStaticDirty();
    }

    public void Step(float deltaSeconds)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BepuPhysicsBackend));
        long residencyStarted = Stopwatch.GetTimestamp();
        RefreshStaticColliderResidency();
        EvictStaleStaticBodies();
        MaximumStaticResidencyMilliseconds = Math.Max(
            MaximumStaticResidencyMilliseconds, ElapsedMilliseconds(residencyStarted));
        RebuildDirtyChunks(FrameWorkBudget.Default.MaximumColliderCommitsPerFrame);
        long dynamicStarted = Stopwatch.GetTimestamp();
        if (_dynamicTopologyDirty)
        {
            _dynamicTopologyDirty = false;
            ReconcileDynamicBodies();
            DynamicReconcileCount++;
        }
        ReconcileAnalyticSphereBodies();
        ApplyExternalGridMotions();
        ApplyPendingPlayerPush();
        MaximumDynamicReconcileMilliseconds = Math.Max(
            MaximumDynamicReconcileMilliseconds, ElapsedMilliseconds(dynamicStarted));
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.25f);
        float stepSeconds = _dynamicBodies.Count >= HeavyDynamicBodyThreshold
            ? HeavyDebrisStepSeconds
            : _dynamicBodies.Count >= MediumDynamicBodyThreshold
                ? MediumDebrisStepSeconds
                : FixedStepSeconds;
        int maximumSteps = stepSeconds == FixedStepSeconds ? MaximumCatchUpSteps : 1;
        _timeAccumulator = MathF.Min(_timeAccumulator + deltaSeconds, stepSeconds * maximumSteps);
        CollisionStepsLastFrame = 0;
        long simulationStarted = Stopwatch.GetTimestamp();
        while (_timeAccumulator + 0.0000001f >= stepSeconds &&
               CollisionStepsLastFrame < maximumSteps)
        {
            long singleStepStarted = Stopwatch.GetTimestamp();
            // Dispatching a tiny active-body set across every logical core costs more
            // than the collision work itself (especially while another game owns CPU
            // time). Keep the parallel path for genuinely large simulations.
            if (DynamicBodyCount >= 64) _simulation.Timestep(stepSeconds, _dispatcher);
            else _simulation.Timestep(stepSeconds);
            MaximumSingleSimulationStepMilliseconds = Math.Max(
                MaximumSingleSimulationStepMilliseconds, ElapsedMilliseconds(singleStepStarted));
            _timeAccumulator -= stepSeconds;
            CollisionStepsLastFrame++;
        }
        MaximumSimulationMilliseconds = Math.Max(
            MaximumSimulationMilliseconds, ElapsedMilliseconds(simulationStarted));
        SynchronizeGrids(stepSeconds * CollisionStepsLastFrame);
        AdvanceLightweightDebris(deltaSeconds);
        SynchronizeAnalyticSpheres();
    }

    public void SetSimulationFocus(Vector3 position) => _simulationFocus = position;

    public void QueuePlayerPush(Vector3 playerPosition, Vector3 playerVelocity)
    {
        _pendingPlayerPosition = playerPosition;
        _pendingPlayerVelocity = playerVelocity;
        _hasPendingPlayerPush = true;
    }

    private void ReconcileDynamicBodies()
    {
        PhysicsGrid[] activeDebris = _world.PhysicsGrids.Grids
            .Where(grid => grid.IsDebrisFragment && !grid.IsDeformable &&
                           !grid.IsKinematicController && !ShouldVirtualizeDebris(grid))
            .OrderBy(grid => Vector3.DistanceSquared(grid.Position, _simulationFocus))
            .ThenBy(grid => grid.Id)
            .ToArray();
        var exactDebris = activeDebris.Take(MaximumExactDebrisBodies)
            .Select(grid => grid.Id)
            .ToHashSet();
        _lightweightDebris.Clear();
        foreach (PhysicsGrid debris in activeDebris)
            if (!exactDebris.Contains(debris.Id)) _lightweightDebris.Add(debris.Id);

        HashSet<ulong> live = _world.PhysicsGrids.Grids
            .Where(grid => !grid.IsDeformable && !grid.IsKinematicController &&
                           !ShouldVirtualizeDebris(grid) &&
                           (!grid.IsDebrisFragment || exactDebris.Contains(grid.Id)))
            .Select(grid => grid.Id)
            .ToHashSet();
        foreach (ulong id in _dynamicBodies.Keys.Where(id => !live.Contains(id)).ToArray()) RemoveDynamic(id);
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (ShouldVirtualizeDebris(grid))
            {
                // Loaded or freshly-created sleeping debris may never have owned a
                // BEPU body. Still remove it from expensive collision participation;
                // the complete shard remains selectable, renderable and saveable.
                _world.PhysicsGrids.SetCollisionVirtualized(grid, virtualized: true);
                continue;
            }
            if (grid.IsDeformable || grid.IsKinematicController) continue;
            _world.PhysicsGrids.SetCollisionVirtualized(grid, virtualized: false);
            if (_lightweightDebris.Contains(grid.Id)) continue;
            int signature = grid.TopologyVersion;
            if (_dynamicBodies.TryGetValue(grid.Id, out DynamicBody existing) && existing.Signature == signature) continue;
            if (_dynamicBodies.ContainsKey(grid.Id)) RemoveDynamic(grid.Id);
            if (grid.RequiresSupportRevalidation && !IsPhysicsGridSupportReady(grid))
            {
                // A newly loaded or newly fractured body may be reconciled before a
                // just-edited floor Chunk has completed its asynchronous collider
                // build. Retry next frame instead of letting it fall through the
                // visible floor during that short gap.
                _dynamicTopologyDirty = true;
                continue;
            }
            grid.RequiresSupportRevalidation = false;
            CreateDynamic(grid, signature);
        }
    }

    private void AdvanceLightweightDebris(float deltaSeconds)
    {
        if (_lightweightDebris.Count == 0 || deltaSeconds <= 0f) return;
        long started = Stopwatch.GetTimestamp();
        float step = Math.Clamp(deltaSeconds, 0f, 0.05f);
        _lightweightMoving.Clear();
        _lightweightStaleIds.Clear();
        foreach (ulong id in _lightweightDebris)
        {
            PhysicsGrid? grid = _world.PhysicsGrids.Get(id);
            if (grid is null || grid.IsSleeping || !grid.IsDebrisFragment)
            {
                _lightweightStaleIds.Add(id);
                continue;
            }
            _lightweightMoving.Add(grid);
        }
        foreach (ulong id in _lightweightStaleIds) _lightweightDebris.Remove(id);

        // Broadphase is rebuilt from every current centre in O(n), so a shard in this
        // frame's integration slice still collides with shards assigned to other
        // slices. At extreme counts, time-slice only the integration/contact solve;
        // each slice advances by the accumulated interval. This keeps all 20k bodies
        // moving and collidable without making a single frame pay for 20k terrain
        // probes, transforms and contact resolutions.
        foreach (List<LightweightEntry> previousBucket in _lightweightBuckets.Values)
        {
            previousBucket.Clear();
            _lightweightBucketPool.Push(previousBucket);
        }
        _lightweightBuckets.Clear();
        foreach (PhysicsGrid grid in _lightweightMoving)
        {
            (Vector3 minimum, Vector3 maximum) = CalculateLightweightBounds(grid);
            (int X, int Y, int Z) key = LightweightCell(grid.Position);
            if (!_lightweightBuckets.TryGetValue(key, out List<LightweightEntry>? bucket))
            {
                if (!_lightweightBucketPool.TryPop(out bucket))
                    bucket = new List<LightweightEntry>(2);
                _lightweightBuckets.Add(key, bucket);
            }
            bucket.Add(new LightweightEntry(grid, minimum, maximum));
        }

        int batchCount = _lightweightMoving.Count >= 15_000 ? 5 :
            _lightweightMoving.Count >= 7_500 ? 3 :
            _lightweightMoving.Count >= 3_000 ? 2 : 1;
        int phase = _lightweightPhase % batchCount;
        _lightweightPhase = (_lightweightPhase + 1) % batchCount;
        // Bound the accumulated approximation step so a badly delayed frame cannot
        // tunnel a thin shard through both the static swept landing test and its hash
        // neighbours. The bodies keep advancing; only the extreme-count LOD catches
        // up over subsequent slices after a long hitch.
        step = Math.Clamp(step * batchCount, 0f, 0.16f);
        bool changed = false;
        for (int movingIndex = phase; movingIndex < _lightweightMoving.Count; movingIndex += batchCount)
        {
            PhysicsGrid grid = _lightweightMoving[movingIndex];
            Vector3 linear = grid.LinearVelocity;
            linear.Y += LightweightGravity * step;
            linear *= MathF.Max(0f, 1f - 0.035f * step);
            Vector3 angular = grid.AngularVelocity * MathF.Max(0f, 1f - 0.08f * step);
            Vector3 position = grid.Position + linear * step;
            Quaternion orientation = IntegrateOrientation(grid.Orientation, angular, step);

            (Vector3 oldMinimum, _) = CalculateLightweightBounds(grid);
            Vector3 oldPosition = grid.Position;
            Quaternion oldOrientation = grid.Orientation;
            grid.Position = position;
            grid.Orientation = orientation;
            (Vector3 minimum, Vector3 maximum) = CalculateLightweightBounds(grid);

            bool hasStaticSupport = TryFindStaticLandingHeight(
                oldMinimum.Y, minimum, maximum, out float supportTop);
            if (hasStaticSupport)
            {
                float correctionY = supportTop - minimum.Y;
                grid.Position += new Vector3(0f, correctionY, 0f);
                minimum.Y += correctionY;
                maximum.Y += correctionY;
                if (linear.Y < 0f) linear.Y = -linear.Y * 0.10f;
                linear.X *= 0.68f;
                linear.Z *= 0.68f;
                angular *= 0.66f;
            }

            // Compare against the full prebuilt broadphase, including shards updated
            // in other phases. Contacts therefore do not disappear just because their
            // owners are integrated on different frames.
            (int X, int Y, int Z) key = LightweightCell(grid.Position);
            int resolved = 0;
            Vector3 cellPosition = grid.Position / LightweightCollisionCellSize;
            int adjacentX = cellPosition.X - MathF.Floor(cellPosition.X) < 0.5f ? -1 : 1;
            int adjacentY = cellPosition.Y - MathF.Floor(cellPosition.Y) < 0.5f ? -1 : 1;
            int adjacentZ = cellPosition.Z - MathF.Floor(cellPosition.Z) < 0.5f ? -1 : 1;
            for (int dz = 0; dz <= 1 && resolved < 4; dz++)
            for (int dy = 0; dy <= 1 && resolved < 4; dy++)
            for (int dx = 0; dx <= 1 && resolved < 4; dx++)
            {
                var neighbourKey = (
                    key.X + (dx == 0 ? 0 : adjacentX),
                    key.Y + (dy == 0 ? 0 : adjacentY),
                    key.Z + (dz == 0 ? 0 : adjacentZ));
                if (!_lightweightBuckets.TryGetValue(neighbourKey, out List<LightweightEntry>? neighbours)) continue;
                foreach (LightweightEntry neighbour in neighbours)
                {
                    if (resolved >= 4) break;
                    PhysicsGrid other = neighbour.Grid;
                    if (other.Id == grid.Id) continue;
                    if (!TryAabbSeparation(minimum, maximum, neighbour.Minimum, neighbour.Maximum,
                            out Vector3 correction)) continue;
                    grid.Position += correction;
                    Vector3 normal = Vector3.Normalize(correction);
                    float closing = Vector3.Dot(linear - other.LinearVelocity, normal);
                    if (closing < 0f)
                    {
                        Vector3 impulse = normal * (-closing * 0.38f);
                        linear += impulse;
                        other.LinearVelocity -= impulse;
                    }
                    angular *= 0.86f;
                    minimum += correction;
                    maximum += correction;
                    resolved++;
                }
            }

            float linearSpeedSquared = linear.LengthSquared();
            float angularSpeedSquared = angular.LengthSquared();
            bool sleeping = false;
            if (linearSpeedSquared <= DebrisQuietLinearSpeed * DebrisQuietLinearSpeed &&
                angularSpeedSquared <= DebrisQuietAngularSpeed * DebrisQuietAngularSpeed &&
                hasStaticSupport)
            {
                float quiet = _debrisQuietSeconds.GetValueOrDefault(grid.Id) + step;
                if (quiet >= DebrisQuietSleepSeconds)
                {
                    linear = Vector3.Zero;
                    angular = Vector3.Zero;
                    sleeping = true;
                    _debrisQuietSeconds.Remove(grid.Id);
                }
                else _debrisQuietSeconds[grid.Id] = quiet;
            }
            else _debrisQuietSeconds.Remove(grid.Id);

            Vector3 resolvedPosition = grid.Position;
            grid.Position = oldPosition;
            grid.Orientation = oldOrientation;
            changed |= grid.SetPhysicsState(
                resolvedPosition, orientation, linear, angular, sleeping);
            if (sleeping)
            {
                _world.PhysicsGrids.SetCollisionVirtualized(grid, virtualized: true);
                _lightweightDebris.Remove(grid.Id);
            }
        }
        if (changed) _world.PhysicsGrids.NotifyChanged();
        MaximumLightweightDebrisMilliseconds = Math.Max(
            MaximumLightweightDebrisMilliseconds, ElapsedMilliseconds(started));
    }

    private static Quaternion IntegrateOrientation(Quaternion orientation, Vector3 angularVelocity, float seconds)
    {
        float speed = angularVelocity.Length();
        if (speed <= 0.00001f) return orientation;
        Quaternion delta = Quaternion.CreateFromAxisAngle(angularVelocity / speed, speed * seconds);
        return Quaternion.Normalize(delta * orientation);
    }

    private bool TryFindStaticLandingHeight(
        float oldBottom, Vector3 minimum, Vector3 maximum, out float supportTop)
    {
        supportTop = float.NegativeInfinity;
        int firstY = (int)MathF.Floor(oldBottom - 0.001f);
        int lastY = (int)MathF.Floor(minimum.Y - 0.001f);
        Span<Vector2> samples = stackalloc Vector2[5]
        {
            new((minimum.X + maximum.X) * 0.5f, (minimum.Z + maximum.Z) * 0.5f),
            new(minimum.X + 0.001f, minimum.Z + 0.001f),
            new(maximum.X - 0.001f, minimum.Z + 0.001f),
            new(minimum.X + 0.001f, maximum.Z - 0.001f),
            new(maximum.X - 0.001f, maximum.Z - 0.001f),
        };
        // Lightweight fragments are normally far smaller than a voxel. Their centre
        // occupies the same support cell as all four corners, so five identical world
        // queries only multiply the 20k-piece cost. Retain corner sampling for an
        // unusually large approximate body that can genuinely bridge cell edges.
        int sampleCount = maximum.X - minimum.X <= 0.75f && maximum.Z - minimum.Z <= 0.75f
            ? 1
            : samples.Length;
        foreach (Vector2 sample in samples[..sampleCount])
        for (int y = firstY; y >= lastY; y--)
        {
            if (!_world.IsSolidForCollision((int)MathF.Floor(sample.X), y, (int)MathF.Floor(sample.Y)))
                continue;
            supportTop = MathF.Max(supportTop, y + 1f);
            break;
        }
        return float.IsFinite(supportTop) && minimum.Y < supportTop && oldBottom >= supportTop - 0.08f;
    }

    private static (int X, int Y, int Z) LightweightCell(Vector3 position) =>
        ((int)MathF.Floor(position.X / LightweightCollisionCellSize),
         (int)MathF.Floor(position.Y / LightweightCollisionCellSize),
         (int)MathF.Floor(position.Z / LightweightCollisionCellSize));

    private static (Vector3 Minimum, Vector3 Maximum) CalculateLightweightBounds(PhysicsGrid grid)
    {
        if (grid.CollisionElements.Count != 1) return grid.CalculateWorldBounds();
        PhysicsGridCollisionElement element = grid.CollisionElements[0];
        Vector3 center = grid.LocalToWorld(element.Center);
        Vector3 half = element.HalfExtents;
        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(grid.Orientation);
        Vector3 extent = new(
            MathF.Abs(rotation.M11) * half.X + MathF.Abs(rotation.M21) * half.Y +
            MathF.Abs(rotation.M31) * half.Z,
            MathF.Abs(rotation.M12) * half.X + MathF.Abs(rotation.M22) * half.Y +
            MathF.Abs(rotation.M32) * half.Z,
            MathF.Abs(rotation.M13) * half.X + MathF.Abs(rotation.M23) * half.Y +
            MathF.Abs(rotation.M33) * half.Z);
        return (center - extent, center + extent);
    }

    private static bool TryAabbSeparation(
        Vector3 minimum, Vector3 maximum, Vector3 otherMinimum, Vector3 otherMaximum,
        out Vector3 correction)
    {
        Vector3 overlap = Vector3.Min(maximum, otherMaximum) - Vector3.Max(minimum, otherMinimum);
        correction = Vector3.Zero;
        if (overlap.X <= 0f || overlap.Y <= 0f || overlap.Z <= 0f) return false;
        Vector3 center = (minimum + maximum) * 0.5f;
        Vector3 otherCenter = (otherMinimum + otherMaximum) * 0.5f;
        if (overlap.X <= overlap.Y && overlap.X <= overlap.Z)
            correction.X = center.X < otherCenter.X ? -overlap.X : overlap.X;
        else if (overlap.Y <= overlap.Z)
            correction.Y = center.Y < otherCenter.Y ? -overlap.Y : overlap.Y;
        else correction.Z = center.Z < otherCenter.Z ? -overlap.Z : overlap.Z;
        correction *= 0.52f;
        return true;
    }

    private readonly record struct LightweightEntry(
        PhysicsGrid Grid, Vector3 Minimum, Vector3 Maximum);

    private bool IsPhysicsGridSupportReady(PhysicsGrid grid)
    {
        (Vector3 minimum, Vector3 maximum) = grid.CalculateWorldBounds();
        int supportY = (int)MathF.Floor(minimum.Y - 0.08f);
        int minimumX = (int)MathF.Floor(minimum.X + 0.001f);
        int maximumX = (int)MathF.Floor(maximum.X - 0.001f);
        int minimumZ = (int)MathF.Floor(minimum.Z + 0.001f);
        int maximumZ = (int)MathF.Floor(maximum.Z - 0.001f);
        var supportChunks = new HashSet<ChunkCoordinate>();
        for (int z = minimumZ; z <= maximumZ; z++)
        for (int x = minimumX; x <= maximumX; x++)
        {
            if (!_world.IsSolidForCollision(x, supportY, z)) continue;
            supportChunks.Add(ChunkCoordinate.FromVoxel(x, z));
        }

        foreach (ChunkCoordinate chunk in supportChunks)
        {
            bool ready = _staticChunks.ContainsKey(chunk) &&
                         !_partiallyEvictedStaticChunks.Contains(chunk) &&
                         !_dirtyChunks.Contains(chunk);
            if (ready) continue;
            if (!_dirtyChunks.Contains(chunk)) MarkChunkDirty(chunk, urgent: true);
            else _urgentDirtyChunks.Add(chunk);
            return false;
        }
        return true;
    }

    private void OnPhysicsGridTopologyChanged(PhysicsGrid? grid)
    {
        // Deformable/kinematic systems own their bodies and can update render
        // topology every simulation step. Only rigid topology, collection changes,
        // or an explicit ownership transition requires a BEPU body-list rebuild.
        if (grid is null || (!grid.IsDeformable && !grid.IsKinematicController))
            _dynamicTopologyDirty = true;
    }

    private void ApplyExternalGridMotions()
    {
        // Gameplay impulses are authored on PhysicsGrid so soft bodies and rigid bodies
        // share one API. Push only new externally-authored motion into BEPU; ordinary
        // BEPU-to-grid synchronization leaves MotionVersion untouched and cannot form a
        // feedback loop.
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (!_dynamicBodies.TryGetValue(grid.Id, out DynamicBody entry) ||
                entry.MotionVersion == grid.MotionVersion) continue;
            BodyReference body = _simulation.Bodies[entry.Handle];
            body.Velocity = new BodyVelocity(grid.LinearVelocity, grid.AngularVelocity);
            body.Awake = true;
            _dynamicBodies[grid.Id] = entry with { MotionVersion = grid.MotionVersion };
        }
    }

    private void ReconcileAnalyticSphereBodies()
    {
        HashSet<ulong> live = _world.AnalyticSpheres.Spheres
            .Where(sphere => !sphere.IsKinematic)
            .Select(sphere => sphere.Id)
            .ToHashSet();
        foreach (ulong id in _analyticSphereBodies.Keys.Where(id => !live.Contains(id)).ToArray())
            RemoveAnalyticSphere(id);
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            if (sphere.IsKinematic) continue;
            if (_analyticSphereBodies.ContainsKey(sphere.Id)) continue;
            if (!IsAnalyticSphereSupportReady(sphere)) continue;
            CreateAnalyticSphere(sphere);
        }
    }

    private bool IsAnalyticSphereSupportReady(AnalyticSphere sphere)
    {
        // A sphere placed on a visible voxel can be reconciled before that Chunk's
        // asynchronous BEPU collider has been committed. Keep the world entity at its
        // authored position until any immediately supporting solid cells are ready;
        // otherwise gravity can move it completely through the pending floor.
        int minimumX = (int)MathF.Floor(sphere.Center.X - sphere.Radius + 0.001f);
        int maximumX = (int)MathF.Floor(sphere.Center.X + sphere.Radius - 0.001f);
        int minimumZ = (int)MathF.Floor(sphere.Center.Z - sphere.Radius + 0.001f);
        int maximumZ = (int)MathF.Floor(sphere.Center.Z + sphere.Radius - 0.001f);
        int supportY = (int)MathF.Floor(sphere.Center.Y - sphere.Radius - 0.125f);
        var supportChunks = new HashSet<ChunkCoordinate>();
        for (int z = minimumZ; z <= maximumZ; z++)
        for (int x = minimumX; x <= maximumX; x++)
        {
            if (!_world.IsSolidForCollision(x, supportY, z)) continue;
            supportChunks.Add(ChunkCoordinate.FromVoxel(x, z));
        }

        foreach (ChunkCoordinate chunk in supportChunks)
        {
            bool ready = _staticChunks.ContainsKey(chunk) &&
                         !_partiallyEvictedStaticChunks.Contains(chunk) &&
                         !_dirtyChunks.Contains(chunk);
            if (ready) continue;
            if (!_dirtyChunks.Contains(chunk)) MarkChunkDirty(chunk, urgent: true);
            else _urgentDirtyChunks.Add(chunk);
            return false;
        }
        return true;
    }

    private void CreateAnalyticSphere(AnalyticSphere sphere)
    {
        var shape = new BepuPhysics.Collidables.Sphere(sphere.Radius);
        TypedIndex shapeIndex = _simulation.Shapes.Add(shape);
        float volume = 4f / 3f * MathF.PI * sphere.Radius * sphere.Radius * sphere.Radius;
        float mass = MathF.Max(0.25f, volume * 3.4f);
        BodyDescription description = BodyDescription.CreateDynamic(
            new RigidPose(sphere.Center, Quaternion.Identity),
            shape.ComputeInertia(mass),
            // Analytic spheres may build significant fall speed and have a small
            // contact cross-section. Sweep them so a valid one-voxel floor cannot be
            // crossed between two fixed simulation steps.
            new CollidableDescription(shapeIndex, 0.08f, ContinuousDetection.Continuous(0.001f, 0.001f)),
            new BodyActivityDescription(0.015f));
        description.Velocity = new BodyVelocity(sphere.LinearVelocity, sphere.AngularVelocity);
        BodyHandle handle = _simulation.Bodies.Add(description);
        if (sphere.IsSleeping)
        {
            BodyReference sleepingBody = _simulation.Bodies[handle];
            sleepingBody.Awake = false;
        }
        _analyticSphereBodies[sphere.Id] = new AnalyticSphereBody(handle, shapeIndex);
    }

    private void ApplyPendingPlayerPush()
    {
        if (!_hasPendingPlayerPush) return;
        _hasPendingPlayerPush = false;
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            if (!_analyticSphereBodies.TryGetValue(sphere.Id, out AnalyticSphereBody entry)) continue;
            BodyReference body = _simulation.Bodies[entry.Handle];
            Vector3 center = body.Pose.Position;
            if (center.Y + sphere.Radius <= _pendingPlayerPosition.Y ||
                center.Y - sphere.Radius >= _pendingPlayerPosition.Y + PlayerController.Height)
                continue;

            Vector2 player = new(_pendingPlayerPosition.X, _pendingPlayerPosition.Z);
            Vector2 sphereCenter = new(center.X, center.Z);
            Vector2 offset = sphereCenter - player;
            float distance = offset.Length();
            float contactDistance = PlayerController.Radius + sphere.Radius;
            if (distance >= contactDistance) continue;

            Vector2 direction2 = distance > 0.0001f
                ? offset / distance
                : new Vector2(1f, 0f);
            Vector3 direction = new(direction2.X, 0f, direction2.Y);
            float intoSphere = MathF.Max(0f, Vector3.Dot(_pendingPlayerVelocity, direction));
            float penetration = contactDistance - distance;
            // The player is a kinematic controller. Move the sphere by only a small,
            // frame-bounded amount and transfer velocity; never teleport either body.
            body.Pose.Position += direction * MathF.Min(penetration, 0.045f);
            float targetSpeed = Math.Clamp(intoSphere + 0.55f, 0.55f, 5.5f);
            float currentSpeed = Vector3.Dot(body.Velocity.Linear, direction);
            if (currentSpeed < targetSpeed)
                body.Velocity.Linear += direction * MathF.Min(targetSpeed - currentSpeed, 1.4f);
            Vector3 rollingAxis = Vector3.Cross(Vector3.UnitY, direction);
            body.Velocity.Angular += rollingAxis * MathF.Min(targetSpeed / sphere.Radius, 5f) * 0.12f;
            body.Awake = true;
        }
    }

    private void CreateDynamic(PhysicsGrid grid, int signature)
    {
        // Interior cells contribute mass/inertia through PhysicsGrid, but they can
        // never participate in contacts. Building BEPU children from the cached shell
        // preserves the exact exterior while avoiding compound work for enclosed voxels.
        if (grid.CollisionElements.Count == 1)
        {
            PhysicsGridCollisionElement element = grid.CollisionElements[0];
            Vector3 size = Vector3.Max(element.Maximum - element.Minimum, new Vector3(0.01f));
            var box = new Box(size.X, size.Y, size.Z);
            TypedIndex boxShape = _simulation.Shapes.Add(box);
            Vector3 centerOffset = element.Center - grid.CenterOfMassLocal;
            Vector3 boxBodyPosition = grid.Position + Vector3.Transform(centerOffset, grid.Orientation);
            BodyDescription boxDescription = BodyDescription.CreateDynamic(
                new RigidPose(boxBodyPosition, grid.Orientation), box.ComputeInertia(element.Mass),
                new CollidableDescription(boxShape, 0.1f, CollisionDetectionFor(grid)),
                new BodyActivityDescription(0.02f));
            boxDescription.Velocity = new BodyVelocity(grid.LinearVelocity, grid.AngularVelocity);
            BodyHandle boxHandle = _simulation.Bodies.Add(boxDescription);
            if (grid.IsSleeping)
            {
                BodyReference sleepingBody = _simulation.Bodies[boxHandle];
                sleepingBody.Awake = false;
            }
            _dynamicBodies[grid.Id] = new DynamicBody(
                boxHandle, boxShape, centerOffset, signature, grid.MotionVersion);
            return;
        }

        using var builder = new CompoundBuilder(_pool, _simulation.Shapes, grid.CollisionElements.Count);
        foreach (PhysicsGridCollisionElement element in grid.CollisionElements)
        {
            Vector3 size = Vector3.Max(element.Maximum - element.Minimum, new Vector3(0.01f));
            builder.Add(new Box(size.X, size.Y, size.Z),
                new RigidPose(element.Center - grid.CenterOfMassLocal, Quaternion.Identity), element.Mass);
        }
        builder.BuildDynamicCompound(out Buffer<CompoundChild> children, out BodyInertia inertia, out Vector3 center);
        TypedIndex shape = _simulation.Shapes.Add(new Compound(children));
        Vector3 bodyPosition = grid.Position + Vector3.Transform(center, grid.Orientation);
        BodyDescription description = BodyDescription.CreateDynamic(
            new RigidPose(bodyPosition, grid.Orientation), inertia,
            new CollidableDescription(shape, 0.1f, CollisionDetectionFor(grid)),
            new BodyActivityDescription(0.02f));
        description.Velocity = new BodyVelocity(grid.LinearVelocity, grid.AngularVelocity);
        BodyHandle handle = _simulation.Bodies.Add(description);
        if (grid.IsSleeping)
        {
            BodyReference sleepingBody = _simulation.Bodies[handle];
            sleepingBody.Awake = false;
        }
        _dynamicBodies[grid.Id] = new DynamicBody(handle, shape, center, signature, grid.MotionVersion);
    }

    private static ContinuousDetection CollisionDetectionFor(PhysicsGrid grid)
    {
        float predictedMotion = (grid.LinearVelocity.Length() +
                                 grid.AngularVelocity.Length() * grid.BoundingRadius) * FixedStepSeconds;
        // At 120 Hz, ordinary falling/rotating grids are fully covered by BEPU's
        // velocity-expanded speculative contacts. Reserve costly sweep tests for a
        // body that could move through roughly half a voxel in one fixed step.
        return predictedMotion >= 0.45f
            ? ContinuousDetection.Continuous(0.001f, 0.001f)
            : ContinuousDetection.Passive;
    }

    private void SynchronizeGrids(float simulatedSeconds)
    {
        bool changed = false;
        List<ulong>? newlyVirtualized = null;
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (!_dynamicBodies.TryGetValue(grid.Id, out DynamicBody entry)) continue;
            BodyReference body = _simulation.Bodies[entry.Handle];
            bool wasSleeping = grid.IsSleeping;
            if (grid.IsDebrisFragment && body.Awake && simulatedSeconds > 0f)
            {
                float linearSpeedSquared = body.Velocity.Linear.LengthSquared();
                float angularSpeedSquared = body.Velocity.Angular.LengthSquared();
                if (linearSpeedSquared <= DebrisQuietLinearSpeed * DebrisQuietLinearSpeed &&
                    angularSpeedSquared <= DebrisQuietAngularSpeed * DebrisQuietAngularSpeed)
                {
                    float quietSeconds = _debrisQuietSeconds.GetValueOrDefault(grid.Id) + simulatedSeconds;
                    if (quietSeconds >= DebrisQuietSleepSeconds)
                    {
                        body.Velocity = default;
                        body.Awake = false;
                        _debrisQuietSeconds.Remove(grid.Id);
                    }
                    else
                    {
                        _debrisQuietSeconds[grid.Id] = quietSeconds;
                    }
                }
                else
                {
                    _debrisQuietSeconds.Remove(grid.Id);
                }
            }
            if (!grid.IsDebrisFragment && !body.Awake &&
                TryGetPrecariousRestTipAxis(grid, out Vector3 tipAxis))
            {
                // A mathematically exact edge or corner balance can make a voxel body
                // sleep forever even though the equilibrium is physically unstable.
                // Wake it with a very small deterministic angular perturbation. Face
                // supported bodies are excluded, so ordinary resting grids stay asleep.
                BodyVelocity velocity = body.Velocity;
                velocity.Angular += tipAxis * 0.16f;
                body.Velocity = velocity;
                body.Awake = true;
            }
            Quaternion orientation = body.Pose.Orientation;
            Vector3 gridPosition = body.Pose.Position - Vector3.Transform(entry.CenterOffset, orientation);
            changed |= grid.SetPhysicsState(
                gridPosition, orientation, body.Velocity.Linear, body.Velocity.Angular, !body.Awake);
            if (wasSleeping != grid.IsSleeping)
                _world.PhysicsGrids.NotifyGeometryMembershipChanged();
            if (ShouldVirtualizeDebris(grid))
                (newlyVirtualized ??= new List<ulong>()).Add(grid.Id);
        }
        // A sleeping shard remains a complete selectable/renderable world entity, but
        // it no longer consumes a BEPU compound body. It is re-created on demand when
        // an impact wakes it. This is the difference between virtualising a settled
        // 100k-piece pile and deleting/fading the pieces as an "optimization".
        if (newlyVirtualized is not null)
            foreach (ulong id in newlyVirtualized)
            {
                if (_world.PhysicsGrids.Get(id) is PhysicsGrid grid)
                    _world.PhysicsGrids.SetCollisionVirtualized(grid, virtualized: true);
                RemoveDynamic(id);
            }
        if (changed) _world.PhysicsGrids.NotifyChanged();
    }

    private static bool ShouldVirtualizeDebris(PhysicsGrid grid) =>
        grid.IsDebrisFragment && grid.IsSleeping;

    private bool TryGetPrecariousRestTipAxis(PhysicsGrid grid, out Vector3 axis)
    {
        axis = Vector3.Zero;
        Vector3 worldX = Vector3.Transform(Vector3.UnitX, grid.Orientation);
        Vector3 worldY = Vector3.Transform(Vector3.UnitY, grid.Orientation);
        Vector3 worldZ = Vector3.Transform(Vector3.UnitZ, grid.Orientation);
        float faceAlignment = MathF.Max(
            MathF.Abs(worldX.Y),
            MathF.Max(MathF.Abs(worldY.Y), MathF.Abs(worldZ.Y)));
        if (faceAlignment >= 0.985f) return false;

        int supportCount = 0;
        Vector2 supportSum = Vector2.Zero;
        Vector2 supportMinimum = new(float.PositiveInfinity);
        Vector2 supportMaximum = new(float.NegativeInfinity);
        foreach (PhysicsGridCollisionElement element in grid.CollisionElements)
        {
            for (int z = 0; z <= 1; z++)
            for (int y = 0; y <= 1; y++)
            for (int x = 0; x <= 1; x++)
            {
                Vector3 localCorner = new(
                    x == 0 ? element.Minimum.X : element.Maximum.X,
                    y == 0 ? element.Minimum.Y : element.Maximum.Y,
                    z == 0 ? element.Minimum.Z : element.Maximum.Z);
                Vector3 worldCorner = grid.LocalToWorld(localCorner);
                if (!HasStaticSupportImmediatelyBelow(worldCorner)) continue;
                Vector2 point = new(worldCorner.X, worldCorner.Z);
                supportSum += point;
                supportMinimum = Vector2.Min(supportMinimum, point);
                supportMaximum = Vector2.Max(supportMaximum, point);
                supportCount++;
            }
        }

        if (supportCount == 0) return false;
        Vector2 supportSpan = supportMaximum - supportMinimum;
        // A real face contact has area in both horizontal dimensions. A point or
        // line contact is deliberately left possible, but cannot remain asleep
        // indefinitely without an external brace.
        if (supportSpan.X >= 0.20f && supportSpan.Y >= 0.20f) return false;

        Vector2 supportCenter = supportSum / supportCount;
        Vector3 radial = new(
            grid.Position.X - supportCenter.X,
            0f,
            grid.Position.Z - supportCenter.Y);
        if (radial.LengthSquared() > 0.0004f)
        {
            axis = Vector3.Normalize(Vector3.Cross(radial, -Vector3.UnitY));
            return true;
        }

        float sign = (grid.Id & 1UL) == 0 ? 1f : -1f;
        axis = supportSpan.Y > supportSpan.X
            ? Vector3.UnitZ * sign
            : Vector3.UnitX * sign;
        return true;
    }

    private bool HasStaticSupportImmediatelyBelow(Vector3 point)
    {
        int y = (int)MathF.Floor(point.Y - 0.035f);
        for (int offsetX = -1; offsetX <= 1; offsetX++)
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            int x = (int)MathF.Floor(point.X + offsetX * 0.025f);
            int z = (int)MathF.Floor(point.Z + offsetZ * 0.025f);
            if (_world.IsCollidableMaterial(_world.GetBlockId(x, y, z))) return true;
        }
        return false;
    }

    private void SynchronizeAnalyticSpheres()
    {
        bool changed = false;
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            if (!_analyticSphereBodies.TryGetValue(sphere.Id, out AnalyticSphereBody entry)) continue;
            BodyReference body = _simulation.Bodies[entry.Handle];
            Vector3 position = body.Pose.Position;
            Vector3 linear = body.Velocity.Linear;
            Vector3 angular = body.Velocity.Angular;
            if (Vector3.DistanceSquared(sphere.Center, position) > 0.00000001f ||
                Vector3.DistanceSquared(sphere.LinearVelocity, linear) > 0.00000001f ||
                Vector3.DistanceSquared(sphere.AngularVelocity, angular) > 0.00000001f ||
                sphere.IsSleeping == body.Awake)
                changed = true;
            sphere.Center = position;
            sphere.LinearVelocity = linear;
            sphere.AngularVelocity = angular;
            sphere.IsSleeping = !body.Awake;
        }
        if (changed) _world.AnalyticSpheres.NotifyChanged();
    }

    private void RebuildDirtyChunks(int maximumChunks)
    {
        int remaining = Math.Max(0, maximumChunks);
        if (_colliderBuildJob is { Build.IsCompleted: true } completed && remaining > 0)
        {
            _colliderBuildJob = null;
            ColliderBuildData? data = null;
            try
            {
                data = completed.Build.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Chunk collider build failed for {completed.Chunk}: {error.Message}");
            }

            if (data is not null && completed.Epoch == _colliderEpoch &&
                IsColliderRequired(completed.Chunk) &&
                _colliderVersions.TryGetValue(completed.Chunk, out int currentVersion) &&
                currentVersion == completed.Version)
            {
                long commitStarted = Stopwatch.GetTimestamp();
                _urgentDirtyChunks.Remove(completed.Chunk);
                _dirtyChunks.Remove(completed.Chunk);
                _colliderLastDirtyTimestamps.Remove(completed.Chunk);
                _colliderDirtyCounts.Remove(completed.Chunk);
                _partiallyEvictedStaticChunks.Remove(completed.Chunk);
                RemoveStaticChunk(completed.Chunk);
                CreateStaticChunk(completed.Chunk, data.Boxes);
                double elapsed = data.BuildMilliseconds +
                                 (Stopwatch.GetTimestamp() - commitStarted) * 1000.0 / Stopwatch.Frequency;
                ColliderBuildCount++;
                ColliderBuildTotalMilliseconds += elapsed;
                MaximumColliderBuildMilliseconds = Math.Max(MaximumColliderBuildMilliseconds, elapsed);
                remaining--;
            }
        }

        if (_colliderBuildJob is not null || _dirtyChunks.Count == 0) return;
        ChunkCoordinate? eligible = _urgentDirtyChunks
            .Where(candidate => _dirtyChunks.Contains(candidate) && IsColliderEditQuiet(candidate))
            .OrderBy(ColliderPriority)
            .Cast<ChunkCoordinate?>()
            .FirstOrDefault();
        eligible ??= _dirtyChunks.Where(IsColliderEditQuiet)
            .OrderBy(ColliderPriority)
            .Cast<ChunkCoordinate?>()
            .FirstOrDefault();
        if (!eligible.HasValue) return;
        ChunkCoordinate chunk = eligible.Value;
        int version = _colliderVersions[chunk];
        int epoch = _colliderEpoch;
        ChunkMeshingSnapshot snapshot = _world.CreateChunkMeshingSnapshot(chunk);
        BlockDefinition[] definitions = _blockDefinitions;
        _colliderBuildJob = new ColliderBuildJob(
            chunk,
            version,
            epoch,
            Task.Run(() => BuildStaticChunk(snapshot, definitions)));
    }

    public void ResetPerformanceCounters()
    {
        ColliderBuildCount = 0;
        ColliderBuildTotalMilliseconds = 0;
        MaximumColliderBuildMilliseconds = 0;
        MaximumDynamicReconcileMilliseconds = 0;
        MaximumSimulationMilliseconds = 0;
        MaximumSingleSimulationStepMilliseconds = 0;
        MaximumStaticResidencyMilliseconds = 0;
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static ColliderBuildData BuildStaticChunk(
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        long started = Stopwatch.GetTimestamp();
        var boxes = new List<VoxelAabb>(ChunkCoordinate.Size * ChunkCoordinate.Size);
        var runsByHeight = new Dictionary<(int StartY, int EndY), byte[]>();
        ChunkCoordinate chunk = snapshot.Coordinate;
        try
        {
            // First collapse every vertical solid run, then greedily merge neighboring
            // columns with identical lower/upper faces. The occupied volume remains
            // exact while flat terrain commonly becomes one Box instead of 256.
            for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
            for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
            {
                int x = chunk.MinimumX + localX;
                int z = chunk.MinimumZ + localZ;
                int runStart = int.MinValue;
                int previousY = int.MinValue;
                for (int y = snapshot.MinimumY; y < snapshot.MaximumYExclusive; y++)
                {
                    ushort material = snapshot.GetBlock(x, y, z);
                    if (material == 0 || !definitions[material].IsCollidable)
                    {
                        if (runStart != int.MinValue)
                            AddColumnRun(localX, localZ, runStart, previousY + 1);
                        runStart = int.MinValue;
                        previousY = int.MinValue;
                        continue;
                    }
                    if (runStart == int.MinValue) runStart = y;
                    previousY = y;
                }
                if (runStart != int.MinValue)
                    AddColumnRun(localX, localZ, runStart, previousY + 1);
            }

            foreach (((int startY, int endY), byte[] mask) in runsByHeight)
            {
                for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
                for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
                {
                    int index = localX + ChunkCoordinate.Size * localZ;
                    if (mask[index] == 0) continue;
                    int width = 1;
                    while (localX + width < ChunkCoordinate.Size && mask[index + width] != 0) width++;
                    int depth = 1;
                    while (localZ + depth < ChunkCoordinate.Size)
                    {
                        bool completeRow = true;
                        int row = index + depth * ChunkCoordinate.Size;
                        for (int offsetX = 0; offsetX < width; offsetX++)
                            if (mask[row + offsetX] == 0) { completeRow = false; break; }
                        if (!completeRow) break;
                        depth++;
                    }
                    for (int offsetZ = 0; offsetZ < depth; offsetZ++)
                        Array.Clear(mask, index + offsetZ * ChunkCoordinate.Size, width);
                    boxes.Add(new VoxelAabb(
                        new Vector3(chunk.MinimumX + localX, startY, chunk.MinimumZ + localZ),
                        new Vector3(chunk.MinimumX + localX + width, endY, chunk.MinimumZ + localZ + depth),
                        0));
                }
            }

            foreach (StoredMicroVoxel micro in snapshot.MicroVoxels)
            {
                float inverse = 1f / micro.Snapshot.Resolution;
                foreach (MicroVoxelBox box in micro.Snapshot.Boxes)
                {
                    if (box.MaterialId == 0 || !definitions[box.MaterialId].IsCollidable) continue;
                    boxes.Add(new VoxelAabb(
                        new Vector3(
                            chunk.MinimumX + micro.LocalX + box.MinX * inverse,
                            micro.Y + box.MinY * inverse,
                            chunk.MinimumZ + micro.LocalZ + box.MinZ * inverse),
                        new Vector3(
                            chunk.MinimumX + micro.LocalX + box.MaxX * inverse,
                            micro.Y + box.MaxY * inverse,
                            chunk.MinimumZ + micro.LocalZ + box.MaxZ * inverse),
                        box.MaterialId));
                }
            }

            return new ColliderBuildData(
                boxes.ToArray(),
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);

            void AddColumnRun(int localX, int localZ, int startY, int endY)
            {
                var key = (startY, endY);
                if (!runsByHeight.TryGetValue(key, out byte[]? mask))
                {
                    mask = ArrayPool<byte>.Shared.Rent(ChunkCoordinate.Size * ChunkCoordinate.Size);
                    Array.Clear(mask, 0, ChunkCoordinate.Size * ChunkCoordinate.Size);
                    runsByHeight.Add(key, mask);
                }
                mask[localX + ChunkCoordinate.Size * localZ] = 1;
            }
        }
        finally
        {
            foreach (byte[] mask in runsByHeight.Values) ArrayPool<byte>.Shared.Return(mask);
        }
    }

    private void CreateStaticChunk(ChunkCoordinate chunk, IReadOnlyList<VoxelAabb> boxes)
    {
        var bodies = new List<StaticBody>(boxes.Count);
        foreach (VoxelAabb box in boxes) AddStaticBox(box, bodies);
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
        _debrisQuietSeconds.Remove(id);
        if (!_dynamicBodies.Remove(id, out DynamicBody entry)) return;
        _simulation.Bodies.Remove(entry.Handle);
        _simulation.Shapes.Remove(entry.Shape);
    }

    private void RemoveAnalyticSphere(ulong id)
    {
        if (!_analyticSphereBodies.Remove(id, out AnalyticSphereBody entry)) return;
        _simulation.Bodies.Remove(entry.Handle);
        _simulation.Shapes.Remove(entry.Shape);
    }

    private void RemoveStaticChunk(ChunkCoordinate chunk)
    {
        _pendingStaticEvictions.Remove(chunk);
        _partiallyEvictedStaticChunks.Remove(chunk);
        if (!_staticChunks.Remove(chunk, out List<StaticBody>? bodies)) return;
        foreach (StaticBody body in bodies)
        {
            _simulation.Statics.Remove(body.Handle);
            _simulation.Shapes.Remove(body.Shape);
        }
    }

    private void OnVoxelChanged(VoxelChangedEvent change)
    {
        // Static Chunk colliders contain occupancy boxes only; material IDs do not
        // affect their shape or contact properties. Avoid tearing down and recreating
        // hundreds of BEPU shapes when a full collidable block is merely repainted.
        bool oldCollidable = _world.IsCollidableMaterial(change.OldBlockId);
        bool newCollidable = _world.IsCollidableMaterial(change.NewBlockId);
        if (oldCollidable == newCollidable) return;
        WakeBodiesAffectedByStaticEdit(change.X, change.Y, change.Z);
        MarkChunkDirty(change.X, change.Z, urgent: true);
    }
    private void OnMicroVoxelCellChanged(int x, int y, int z)
    {
        WakeBodiesAffectedByStaticEdit(x, y, z);
        MarkChunkDirty(x, z, urgent: true);
    }

    /// <summary>
    /// BEPU can legitimately put a supported structure to sleep. Removing the voxel
    /// that supplied that support does not itself touch the dynamic body, so without
    /// an explicit wake the old sleeping state can survive after the static collider
    /// has gone and leave a real rigid body suspended in air. Limit the wake to bodies
    /// whose world bounds touch the edited cell; settled shard piles remain virtualized.
    /// </summary>
    private void WakeBodiesAffectedByStaticEdit(int x, int y, int z)
    {
        const float contactMargin = 0.12f;
        Vector3 editMinimum = new(x - contactMargin, y - contactMargin, z - contactMargin);
        Vector3 editMaximum = new(x + 1f + contactMargin, y + 1f + contactMargin, z + 1f + contactMargin);
        bool gridMembershipChanged = false;
        foreach ((ulong id, DynamicBody entry) in _dynamicBodies)
        {
            PhysicsGrid? grid = _world.PhysicsGrids.Get(id);
            if (grid is null || grid.IsDebrisFragment || !grid.IsSleeping) continue;
            (Vector3 minimum, Vector3 maximum) = grid.CalculateWorldBounds();
            if (!BoundsOverlap(minimum, maximum, editMinimum, editMaximum)) continue;
            BodyReference body = _simulation.Bodies[entry.Handle];
            body.Awake = true;
            grid.Wake();
            gridMembershipChanged = true;
        }
        if (gridMembershipChanged) _world.PhysicsGrids.NotifyGeometryMembershipChanged();

        bool sphereChanged = false;
        foreach ((ulong id, AnalyticSphereBody entry) in _analyticSphereBodies)
        {
            AnalyticSphere? sphere = _world.AnalyticSpheres.Get(id);
            if (sphere is null || !sphere.IsSleeping) continue;
            Vector3 radius = new(sphere.Radius);
            if (!BoundsOverlap(sphere.Center - radius, sphere.Center + radius, editMinimum, editMaximum))
                continue;
            BodyReference body = _simulation.Bodies[entry.Handle];
            body.Awake = true;
            sphere.IsSleeping = false;
            sphereChanged = true;
        }
        if (sphereChanged) _world.AnalyticSpheres.NotifyChanged();
    }

    private static bool BoundsOverlap(
        Vector3 minimum, Vector3 maximum, Vector3 otherMinimum, Vector3 otherMaximum) =>
        minimum.X <= otherMaximum.X && maximum.X >= otherMinimum.X &&
        minimum.Y <= otherMaximum.Y && maximum.Y >= otherMinimum.Y &&
        minimum.Z <= otherMaximum.Z && maximum.Z >= otherMinimum.Z;
    private void OnWorldReloaded()
    {
        // A fractional step belongs to the old world. Never carry it across a load or
        // regeneration boundary, where it could advance newly restored bodies early.
        _timeAccumulator = 0f;
        foreach (ulong id in _dynamicBodies.Keys.ToArray()) RemoveDynamic(id);
        foreach (ulong id in _analyticSphereBodies.Keys.ToArray()) RemoveAnalyticSphere(id);
        foreach (ChunkCoordinate chunk in _staticChunks.Keys.ToArray()) RemoveStaticChunk(chunk);
        _dirtyChunks.Clear();
        _urgentDirtyChunks.Clear();
        _pendingStaticEvictions.Clear();
        _partiallyEvictedStaticChunks.Clear();
        _colliderVersions.Clear();
        _colliderLastDirtyTimestamps.Clear();
        _colliderDirtyCounts.Clear();
        _colliderEpoch++;
        _colliderResidencySignature = int.MinValue;
        _dynamicTopologyDirty = true;
        MarkAllStaticDirty();
    }

    private void MarkChunkDirty(int x, int z, bool urgent = false)
    {
        ChunkCoordinate chunk = ChunkCoordinate.FromVoxel(x, z);
        MarkChunkDirty(chunk, urgent);
    }
    private void MarkChunkDirty(ChunkCoordinate chunk, bool urgent = false)
    {
        if (!IsColliderRequired(chunk)) return;
        bool alreadyDirty = !_dirtyChunks.Add(chunk);
        _colliderLastDirtyTimestamps[chunk] = Stopwatch.GetTimestamp();
        _colliderDirtyCounts[chunk] = alreadyDirty && _colliderDirtyCounts.TryGetValue(chunk, out int dirtyCount)
            ? dirtyCount + 1
            : 1;
        _colliderVersions[chunk] = _colliderVersions.TryGetValue(chunk, out int version)
            ? unchecked(version + 1)
            : 1;
        if (urgent) _urgentDirtyChunks.Add(chunk);
    }
    private void OnChunkGenerated(ChunkCoordinate chunk)
    {
        if (IsColliderRequired(chunk)) MarkChunkDirty(chunk);
    }
    private void MarkAllStaticDirty()
    {
        foreach (ChunkCoordinate chunk in _staticChunks.Keys)
            if (IsColliderRequired(chunk)) MarkChunkDirty(chunk);
        foreach (ChunkCoordinate chunk in _world.SnapshotLoadedChunks())
            if (IsColliderRequired(chunk)) MarkChunkDirty(chunk);
    }

    private void RefreshStaticColliderResidency()
    {
        int signature = CalculateColliderResidencySignature();
        if (signature == _colliderResidencySignature) return;
        _colliderResidencySignature = signature;

        HashSet<ChunkCoordinate> required = _world.SnapshotLoadedChunks()
            .Where(IsColliderRequired)
            .ToHashSet();
        foreach (ChunkCoordinate stale in _staticChunks.Keys.Where(chunk => !required.Contains(chunk)))
            _pendingStaticEvictions.Add(stale);
        foreach (ChunkCoordinate retained in required) _pendingStaticEvictions.Remove(retained);

        foreach (ChunkCoordinate dirty in _dirtyChunks.Where(chunk => !required.Contains(chunk)).ToArray())
        {
            _dirtyChunks.Remove(dirty);
            _urgentDirtyChunks.Remove(dirty);
            _colliderVersions.Remove(dirty);
            _colliderLastDirtyTimestamps.Remove(dirty);
            _colliderDirtyCounts.Remove(dirty);
        }
        foreach (ChunkCoordinate chunk in required)
        {
            if (_partiallyEvictedStaticChunks.Contains(chunk) && !_dirtyChunks.Contains(chunk))
                MarkChunkDirty(chunk, urgent: true);
            else if (!_staticChunks.ContainsKey(chunk) && !_dirtyChunks.Contains(chunk))
                MarkChunkDirty(chunk);
        }
    }

    private int CalculateColliderResidencySignature()
    {
        var hash = new HashCode();
        hash.Add(ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(_simulationFocus.X),
            (int)MathF.Floor(_simulationFocus.Z)));
        foreach (ulong gridId in _dynamicBodies.Keys)
        {
            PhysicsGrid? grid = _world.PhysicsGrids.Get(gridId);
            if (grid is null) continue;
            (Vector3 minimum, Vector3 maximum) = grid.CalculateWorldBounds();
            hash.Add(grid.Id);
            hash.Add(ChunkCoordinate.FromVoxel((int)MathF.Floor(minimum.X), (int)MathF.Floor(minimum.Z)));
            hash.Add(ChunkCoordinate.FromVoxel((int)MathF.Floor(maximum.X), (int)MathF.Floor(maximum.Z)));
        }
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            hash.Add(sphere.Id);
            hash.Add(ChunkCoordinate.FromVoxel(
                (int)MathF.Floor(sphere.Center.X),
                (int)MathF.Floor(sphere.Center.Z)));
        }
        return hash.ToHashCode();
    }

    private bool IsColliderRequired(ChunkCoordinate chunk)
    {
        ChunkCoordinate focus = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(_simulationFocus.X),
            (int)MathF.Floor(_simulationFocus.Z));
        if (Math.Max(Math.Abs(chunk.X - focus.X), Math.Abs(chunk.Z - focus.Z)) <=
            PlayerColliderRadiusChunks) return true;

        foreach (ulong gridId in _dynamicBodies.Keys)
        {
            PhysicsGrid? grid = _world.PhysicsGrids.Get(gridId);
            if (grid is null) continue;
            (Vector3 minimum, Vector3 maximum) = grid.CalculateWorldBounds();
            ChunkCoordinate first = ChunkCoordinate.FromVoxel(
                (int)MathF.Floor(minimum.X) - ChunkCoordinate.Size,
                (int)MathF.Floor(minimum.Z) - ChunkCoordinate.Size);
            ChunkCoordinate last = ChunkCoordinate.FromVoxel(
                (int)MathF.Floor(maximum.X) + ChunkCoordinate.Size,
                (int)MathF.Floor(maximum.Z) + ChunkCoordinate.Size);
            if (chunk.X >= first.X && chunk.X <= last.X && chunk.Z >= first.Z && chunk.Z <= last.Z)
                return true;
        }
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            ChunkCoordinate sphereChunk = ChunkCoordinate.FromVoxel(
                (int)MathF.Floor(sphere.Center.X),
                (int)MathF.Floor(sphere.Center.Z));
            if (Math.Max(Math.Abs(chunk.X - sphereChunk.X), Math.Abs(chunk.Z - sphereChunk.Z)) <=
                AnalyticSphereColliderRadiusChunks) return true;
        }
        return false;
    }

    private int ColliderPriority(ChunkCoordinate chunk)
    {
        ChunkCoordinate focus = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(_simulationFocus.X),
            (int)MathF.Floor(_simulationFocus.Z));
        int priority = Math.Max(Math.Abs(chunk.X - focus.X), Math.Abs(chunk.Z - focus.Z));
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            ChunkCoordinate sphereChunk = ChunkCoordinate.FromVoxel(
                (int)MathF.Floor(sphere.Center.X),
                (int)MathF.Floor(sphere.Center.Z));
            priority = Math.Min(priority,
                Math.Max(Math.Abs(chunk.X - sphereChunk.X), Math.Abs(chunk.Z - sphereChunk.Z)));
        }
        return priority;
    }

    private bool IsColliderEditQuiet(ChunkCoordinate chunk)
    {
        if (!_colliderLastDirtyTimestamps.TryGetValue(chunk, out long changedAt)) return true;
        double quietMilliseconds = _colliderDirtyCounts.TryGetValue(chunk, out int dirtyCount) && dirtyCount > 1
            ? RepeatedColliderEditCoalesceMilliseconds
            : ColliderEditCoalesceMilliseconds;
        return ElapsedMilliseconds(changedAt) >= quietMilliseconds;
    }

    private void EvictStaleStaticBodies()
    {
        ChunkCoordinate? stale = _pendingStaticEvictions
            .Where(chunk => !IsColliderRequired(chunk))
            .Cast<ChunkCoordinate?>()
            .FirstOrDefault();
        if (!stale.HasValue) return;
        if (!_staticChunks.TryGetValue(stale.Value, out List<StaticBody>? bodies))
        {
            _pendingStaticEvictions.Remove(stale.Value);
            _partiallyEvictedStaticChunks.Remove(stale.Value);
            return;
        }
        int removeCount = Math.Min(MaximumStaticBodiesRemovedPerFrame, bodies.Count);
        for (int index = 0; index < removeCount; index++)
        {
            int last = bodies.Count - 1;
            StaticBody body = bodies[last];
            bodies.RemoveAt(last);
            _simulation.Statics.Remove(body.Handle);
            _simulation.Shapes.Remove(body.Shape);
        }
        if (bodies.Count == 0)
        {
            _staticChunks.Remove(stale.Value);
            _pendingStaticEvictions.Remove(stale.Value);
            _partiallyEvictedStaticChunks.Remove(stale.Value);
        }
        else
        {
            _partiallyEvictedStaticChunks.Add(stale.Value);
        }
    }
    private void OnBlockDefinitionsChanged() =>
        _blockDefinitions = _world.CreateBlockDefinitionLookupSnapshot();
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
        _world.BlockDefinitionsChanged -= OnBlockDefinitionsChanged;
        _world.PhysicsGrids.TopologyChanged -= OnPhysicsGridTopologyChanged;
        _simulation.Dispose();
        _dispatcher.Dispose();
        _pool.Clear();
    }

    private readonly record struct DynamicBody(
        BodyHandle Handle,
        TypedIndex Shape,
        Vector3 CenterOffset,
        int Signature,
        int MotionVersion);
    private readonly record struct AnalyticSphereBody(BodyHandle Handle, TypedIndex Shape);
    private readonly record struct StaticBody(StaticHandle Handle, TypedIndex Shape);
    private sealed record ColliderBuildJob(
        ChunkCoordinate Chunk,
        int Version,
        int Epoch,
        Task<ColliderBuildData> Build);
    private sealed record ColliderBuildData(VoxelAabb[] Boxes, double BuildMilliseconds);

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
