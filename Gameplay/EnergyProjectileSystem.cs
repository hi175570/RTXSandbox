using System.Numerics;
using RTXSandbox.Engine;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

public enum EnergyForceLevel { Low, Medium, High }

public enum PhysicsProjectorMode
{
    Manipulate,
    Impulse,
}

public readonly record struct EnergyImpact(
    Vector3 Position,
    Vector3 Normal,
    Vector3 Direction,
    float Force,
    float Radius,
    ushort MaterialId,
    int X,
    int Y,
    int Z,
    ulong GridId);

/// <summary>
/// Short-lived visible energy pulses. Force and affected radius are independent:
/// a narrow high-pressure pulse can fracture glass, while a broad pulse moves a group.
/// </summary>
public sealed class EnergyProjectileSystem : IDisposable
{
    internal const int MaximumGlassFractureGeneration = 3;
    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly List<Projectile> _projectiles = new();
    private ushort _proxyMaterial;
    private ulong _heldGridId;
    private Vector3 _heldLocalPoint;
    private Vector3 _previousHeldPosition;

    public PhysicsProjectorMode Mode { get; private set; } = PhysicsProjectorMode.Manipulate;
    public EnergyForceLevel ForceLevel { get; set; } = EnergyForceLevel.Medium;
    public float ImpactRadius { get; set; } = 1.25f;
    public int ActiveProjectileCount => _projectiles.Count;
    public bool IsHolding => _heldGridId != 0;
    public ulong HeldGridId => _heldGridId;
    public float HoldDistance { get; private set; } = 5f;
    public string PhysicsGunStatus { get; private set; } = "Ready";

    public void SetMode(PhysicsProjectorMode mode)
    {
        if (Mode == mode) return;
        if (IsHolding) ReleaseHeld();
        Mode = mode;
        PhysicsGunStatus = mode == PhysicsProjectorMode.Manipulate
            ? "Manipulation mode"
            : "Impulse mode";
    }

    public void ToggleMode() => SetMode(Mode == PhysicsProjectorMode.Manipulate
        ? PhysicsProjectorMode.Impulse
        : PhysicsProjectorMode.Manipulate);

    public bool CanGrab(ulong gridId)
    {
        PhysicsGrid? grid = _world.PhysicsGrids.Get(gridId);
        return grid is not null && !grid.IsDebrisFragment && !grid.IsDeformable &&
               !grid.IsStaticDeformation &&
               (!grid.IsKinematicController || grid.IsPhysicsGunFrozen);
    }

    /// <summary>
    /// Applies a deterministic, narrow energy impact to one intact glass grid. This is
    /// used by the hardware stress harness so it exercises the production fracture path
    /// without pretending that an ordinary fall shatters glass or scanning every body for
    /// thousands of synthetic projectiles.
    /// </summary>
    internal bool TriggerScriptedGlassFracture(ulong gridId, Vector3 direction, float force = 8f)
    {
        PhysicsGrid? source = _world.PhysicsGrids.Get(gridId);
        if (source is null || source.FractureGeneration != 0 || source.Elements.Count != 1)
            return false;
        PhysicsGridElement element = source.Elements[0];
        if (!IsGlass(_blocks.Get(element.MaterialId))) return false;

        Vector3 localNormal = Vector3.UnitZ;
        Vector3 worldNormal = Vector3.Normalize(source.LocalDirectionToWorld(localNormal));
        if (direction.LengthSquared() < 0.000001f) direction = -worldNormal;
        else direction = Vector3.Normalize(direction);
        Vector3 localImpact = new(
            (element.Minimum.X + element.Maximum.X) * 0.5f,
            (element.Minimum.Y + element.Maximum.Y) * 0.5f,
            element.Maximum.Z);
        var impact = new EnergyImpact(
            source.LocalToWorld(localImpact), worldNormal, direction,
            force, 0.25f, element.MaterialId,
            element.CellX, element.CellY, element.CellZ, source.Id);
        int before = _world.PhysicsGrids.Count;
        FractureGridGlass(impact, source);
        return source.FractureGeneration == 1 && _world.PhysicsGrids.Count > before;
    }

    public EnergyProjectileSystem(VoxelWorld world, BlockRegistry blocks)
    {
        _world = world;
        _blocks = blocks;
        _proxyMaterial = ResolveProxyMaterial();
    }

    public void Fire(Vector3 origin, Vector3 direction)
    {
        if (direction.LengthSquared() < 0.000001f) return;
        direction = Vector3.Normalize(direction);
        float force = ForceFor(ForceLevel);
        float speed = 32f + force * 0.35f;
        AnalyticSphere proxy = _world.AnalyticSpheres.Create(origin + direction * 0.72f, 0.20f, _proxyMaterial);
        proxy.IsKinematic = true;
        proxy.IsSleeping = false;
        _projectiles.Add(new Projectile(proxy, direction * speed, force, Math.Clamp(ImpactRadius, 0.15f, 4f), 2.5f));
    }

    public bool TryBeginGrab(PhysicsGridRayHit hit, Vector3 origin)
    {
        if (Mode != PhysicsProjectorMode.Manipulate)
        {
            PhysicsGunStatus = "Switch to manipulation mode to grab";
            return false;
        }
        PhysicsGrid? grid = _world.PhysicsGrids.Get(hit.GridId);
        if (!CanGrab(hit.GridId) || grid is null)
        {
            PhysicsGunStatus = "This body is owned by another controller";
            return false;
        }
        ReleaseHeld(Vector3.Zero);
        if (grid.IsPhysicsGunFrozen) grid.IsPhysicsGunFrozen = false;
        grid.IsPhysicsGunHeld = true;
        grid.IsKinematicController = true;
        grid.LinearVelocity = Vector3.Zero;
        grid.AngularVelocity = Vector3.Zero;
        grid.IsSleeping = false;
        _heldGridId = grid.Id;
        _heldLocalPoint = grid.WorldToLocal(hit.Position);
        _previousHeldPosition = grid.Position;
        HoldDistance = Math.Clamp(Vector3.Distance(origin, hit.Position), 1.25f, 24f);
        _world.PhysicsGrids.NotifyTopologyChanged(grid);
        PhysicsGunStatus = $"Holding structure #{grid.Id}";
        return true;
    }

    public bool UpdateHeld(float deltaSeconds, Vector3 origin, Vector3 direction)
    {
        if (_heldGridId == 0) return false;
        PhysicsGrid? grid = _world.PhysicsGrids.Get(_heldGridId);
        if (grid is null)
        {
            _heldGridId = 0;
            PhysicsGunStatus = "Held structure no longer exists";
            return false;
        }
        if (direction.LengthSquared() < 0.000001f) return false;
        direction = Vector3.Normalize(direction);
        float dt = Math.Clamp(deltaSeconds, 0.0001f, 0.05f);
        Vector3 desiredGrabPoint = origin + direction * HoldDistance;
        Vector3 grabOffset = Vector3.Transform(_heldLocalPoint - grid.CenterOfMassLocal, grid.Orientation);
        Vector3 desiredPosition = desiredGrabPoint - grabOffset;
        // This body is already under kinematic ownership, so an interpolated target only
        // adds visible aim lag: when the player looks upward the grabbed point remains
        // below the crosshair. Keep the exact picked point on the camera ray instead.
        Vector3 newPosition = desiredPosition;
        Vector3 carryVelocity = (newPosition - _previousHeldPosition) / dt;
        _previousHeldPosition = newPosition;
        grid.SetTransformAndVelocity(newPosition, grid.Orientation,
            Vector3.Clamp(carryVelocity, new Vector3(-35f), new Vector3(35f)), Vector3.Zero);
        grid.IsPhysicsGunHeld = true;
        grid.IsKinematicController = true;
        _world.PhysicsGrids.NotifyChanged();
        return true;
    }

    public void AdjustHoldDistance(float wheelDelta)
    {
        if (_heldGridId == 0 || MathF.Abs(wheelDelta) < 0.01f) return;
        HoldDistance = Math.Clamp(HoldDistance + wheelDelta * 0.75f, 1.25f, 24f);
        PhysicsGunStatus = $"Hold distance {HoldDistance:0.0} m";
    }

    public bool ReleaseHeld() => ReleaseHeld(Vector3.Zero);

    public bool ThrowHeld(Vector3 direction)
    {
        if (_heldGridId == 0) return false;
        if (direction.LengthSquared() < 0.000001f) direction = -Vector3.UnitZ;
        float launchSpeed = 8f + ForceFor(ForceLevel) * 0.55f;
        bool released = ReleaseHeld(Vector3.Normalize(direction) * launchSpeed);
        if (released) PhysicsGunStatus = $"Threw structure at {launchSpeed:0.#} m/s";
        return released;
    }

    public bool FreezeHeld()
    {
        if (_heldGridId == 0) return false;
        PhysicsGrid? grid = _world.PhysicsGrids.Get(_heldGridId);
        if (grid is null) { _heldGridId = 0; return false; }
        grid.IsPhysicsGunHeld = false;
        grid.IsPhysicsGunFrozen = true;
        grid.IsKinematicController = true;
        grid.LinearVelocity = Vector3.Zero;
        grid.AngularVelocity = Vector3.Zero;
        grid.IsSleeping = true;
        _heldGridId = 0;
        _world.PhysicsGrids.NotifyTopologyChanged(grid);
        PhysicsGunStatus = $"Pinned structure #{grid.Id}";
        return true;
    }

    public bool ToggleFrozen(ulong gridId)
    {
        PhysicsGrid? grid = _world.PhysicsGrids.Get(gridId);
        if (grid is null || grid.IsDeformable || grid.IsStaticDeformation || grid.IsDebrisFragment ||
            (grid.IsKinematicController && !grid.IsPhysicsGunFrozen)) return false;
        bool freeze = !grid.IsPhysicsGunFrozen;
        grid.IsPhysicsGunFrozen = freeze;
        grid.IsPhysicsGunHeld = false;
        grid.IsKinematicController = freeze;
        grid.LinearVelocity = Vector3.Zero;
        grid.AngularVelocity = Vector3.Zero;
        grid.IsSleeping = freeze;
        if (!freeze) grid.Wake();
        _world.PhysicsGrids.NotifyTopologyChanged(grid);
        PhysicsGunStatus = freeze ? $"Pinned structure #{grid.Id}" : $"Released pin #{grid.Id}";
        return true;
    }

    private bool ReleaseHeld(Vector3 launchVelocity)
    {
        if (_heldGridId == 0) return false;
        PhysicsGrid? grid = _world.PhysicsGrids.Get(_heldGridId);
        _heldGridId = 0;
        if (grid is null) return false;
        grid.IsPhysicsGunHeld = false;
        grid.IsPhysicsGunFrozen = false;
        grid.IsKinematicController = false;
        grid.LinearVelocity = launchVelocity == Vector3.Zero ? grid.LinearVelocity : launchVelocity;
        grid.AngularVelocity = Vector3.Zero;
        grid.Wake();
        _world.PhysicsGrids.NotifyTopologyChanged(grid);
        PhysicsGunStatus = launchVelocity == Vector3.Zero ? "Released structure" : PhysicsGunStatus;
        return true;
    }

    public void Update(float deltaSeconds)
    {
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.05f);
        bool moved = false;
        for (int index = _projectiles.Count - 1; index >= 0; index--)
        {
            Projectile projectile = _projectiles[index];
            float travel = projectile.Velocity.Length() * deltaSeconds;
            Vector3 direction = Vector3.Normalize(projectile.Velocity);
            if (TryFindHit(projectile.Proxy, projectile.Proxy.Center, direction, travel, out EnergyImpact impact))
            {
                ApplyImpact(impact with { Force = projectile.Force, Radius = projectile.Radius });
                _world.AnalyticSpheres.Remove(projectile.Proxy.Id);
                _projectiles.RemoveAt(index);
                moved = true;
                continue;
            }
            projectile.Proxy.Center += projectile.Velocity * deltaSeconds;
            projectile = projectile with { RemainingLife = projectile.RemainingLife - deltaSeconds };
            if (projectile.RemainingLife <= 0f)
            {
                _world.AnalyticSpheres.Remove(projectile.Proxy.Id);
                _projectiles.RemoveAt(index);
            }
            else _projectiles[index] = projectile;
            moved = true;
        }
        if (moved) _world.AnalyticSpheres.NotifyChanged();
    }

    private bool TryFindHit(
        AnalyticSphere proxy,
        Vector3 origin,
        Vector3 direction,
        float maximumDistance,
        out EnergyImpact impact)
    {
        impact = default;
        float closest = maximumDistance + 0.001f;
        VoxelHit? voxel = VoxelRaycast.Cast(_world, origin, direction, maximumDistance);
        if (voxel is { } worldHit && worldHit.Distance < closest)
        {
            closest = worldHit.Distance;
            impact = new EnergyImpact(
                origin + direction * worldHit.Distance, worldHit.Normal, direction,
                0f, 0f, worldHit.MaterialId, worldHit.X, worldHit.Y, worldHit.Z, 0);
        }
        PhysicsGridRayHit? gridHit = _world.PhysicsGrids.Raycast(origin, direction, maximumDistance);
        if (gridHit is { } dynamicHit && dynamicHit.Distance < closest)
        {
            closest = dynamicHit.Distance;
            impact = new EnergyImpact(dynamicHit.Position, dynamicHit.Normal, direction,
                0f, 0f, dynamicHit.MaterialId,
                dynamicHit.CellX, dynamicHit.CellY, dynamicHit.CellZ, dynamicHit.GridId);
        }
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            if (sphere.Id == proxy.Id || !IntersectSphere(origin, direction, sphere, maximumDistance, out float distance) ||
                distance >= closest) continue;
            closest = distance;
            Vector3 position = origin + direction * distance;
            impact = new EnergyImpact(position, Vector3.Normalize(position - sphere.Center), direction,
                0f, 0f, sphere.MaterialId, 0, 0, 0, ulong.MaxValue - sphere.Id);
        }
        return closest <= maximumDistance;
    }

    private void ApplyImpact(EnergyImpact impact)
    {
        if (impact.GridId != 0 && impact.GridId < ulong.MaxValue / 2 &&
            _world.PhysicsGrids.Get(impact.GridId) is PhysicsGrid directGrid)
        {
            bool reactivate = directGrid.IsDebrisFragment && directGrid.IsSleeping;
            if (reactivate) _world.PhysicsGrids.SetCollisionVirtualized(directGrid, virtualized: false);
            if (!directGrid.IsKinematicController)
                ApplyBoundedEnergyImpulse(directGrid, impact.Direction * impact.Force, impact.Position);
            if (reactivate) _world.PhysicsGrids.NotifyTopologyChanged(directGrid);
        }

        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (grid.Id == impact.GridId) continue;
            float distance = Vector3.Distance(grid.Position, impact.Position);
            if (distance > impact.Radius + grid.BoundingRadius) continue;
            float attenuation = 1f - Math.Clamp(distance / MathF.Max(impact.Radius + grid.BoundingRadius, 0.01f), 0f, 1f);
            bool reactivate = grid.IsDebrisFragment && grid.IsSleeping;
            if (reactivate) _world.PhysicsGrids.SetCollisionVirtualized(grid, virtualized: false);
            if (!grid.IsKinematicController)
                ApplyBoundedEnergyImpulse(grid, impact.Direction * impact.Force * attenuation, impact.Position);
            if (reactivate) _world.PhysicsGrids.NotifyTopologyChanged(grid);
        }
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            if (sphere.IsKinematic) continue;
            float distance = Vector3.Distance(sphere.Center, impact.Position);
            if (distance > impact.Radius + sphere.Radius) continue;
            float attenuation = 1f - Math.Clamp(distance / MathF.Max(impact.Radius + sphere.Radius, 0.01f), 0f, 1f);
            sphere.LinearVelocity += impact.Direction * impact.Force * attenuation / MathF.Max(0.5f, sphere.Radius * 3f);
            sphere.IsSleeping = false;
        }

        if (IsGlass(_blocks.Get(impact.MaterialId)))
        {
            if (impact.GridId == 0)
                FractureGlass(impact);
            else if (impact.GridId < ulong.MaxValue / 2 &&
                     _world.PhysicsGrids.Get(impact.GridId) is PhysicsGrid glassGrid &&
                     glassGrid.FractureGeneration < MaximumGlassFractureGeneration)
                FractureGridGlass(impact, glassGrid);
        }
        // PlasticDeformationMod deliberately patches this private method. This keeps
        // impact deformation a BepInEx/Harmony interception, not a new public Mod API.
    }

    private void FractureGlass(EnergyImpact impact)
    {
        float pressure = impact.Force / MathF.Max(0.08f, MathF.PI * impact.Radius * impact.Radius);
        if (pressure < 2.2f) return;
        const int resolution = 8;
        var micro = new MicroVoxelContainer(resolution);
        micro.Fill(impact.MaterialId);
        int seed = HashCode.Combine(_world.Seed, impact.X, impact.Y, impact.Z,
            (int)MathF.Round(impact.Position.X * 100f), (int)MathF.Round(impact.Position.Y * 100f));
        var random = new Random(seed);
        Vector3 local = impact.Position - new Vector3(impact.X, impact.Y, impact.Z);
        int normalAxis = DominantAxis(impact.Normal);
        (float u0, float v0) = Project(local, normalAxis);
        int rays = Math.Clamp(5 + (int)(pressure * 0.75f), 6, 14);
        float fractureSeverity = Math.Clamp(MathF.Max((pressure - 2.2f) / 12f,
            (impact.Force - 3f) / 24f), 0f, 1f);
        float shatterSeverity = Math.Clamp(MathF.Max((pressure - 3.2f) / 8f,
            (impact.Force - 6f) / 10f), 0f, 1f);
        // Ordinary unlaminated glass is brittle: the normal medium pulse must release
        // fragments instead of behaving like ballistic glass with a decorative crack.
        // Base building glass is ordinary annealed glass, not ballistic laminate.
        // Once an energy impact passes the fracture threshold it releases physical
        // shards immediately; force level changes their launch speed and spread.
        bool fullShatter = pressure >= 2.2f || impact.Force >= 3f;
        if (fullShatter)
        {
            // A previous implementation rasterized one block into 8x8x8 collision boxes.
            // Grouping those 512 boxes into six bodies limited the body count, but it did
            // not limit meshing, BLAS or compound-collider cost and visually produced dust.
            // Glass fracture is planar: partition only the struck face into a handful of
            // deterministic, non-uniform regions and extrude each through the pane depth.
            _world.SetBlockId(impact.X, impact.Y, impact.Z, 0);
            SpawnCoarseGlassShards(impact, seed, normalAxis);
            return;
        }
        float hole = 0.055f + fractureSeverity * 0.285f;
        var rayAngles = new float[rays];
        for (int ray = 0; ray < rays; ray++)
            rayAngles[ray] = ray * MathF.Tau / rays + (float)(random.NextDouble() - 0.5) * 0.24f;

        // Cracks alone left more than 80% of a high-pressure hit intact, which made
        // ordinary glass behave like ballistic laminate. Split the pane into coherent
        // radial fragments and release whole fragment regions once pressure is high.
        // The decision is shared through the full depth, so the result is jagged holes
        // and surviving shards instead of isolated salt-and-pepper micro voxels.
        const int fragmentRings = 4;
        int fragmentSectors = Math.Clamp(rays, 8, 14);
        var releasedFragments = new bool[fragmentSectors, fragmentRings];
        for (int sector = 0; sector < fragmentSectors; sector++)
        for (int ring = 0; ring < fragmentRings; ring++)
        {
            float edgeRetention = 1f - ring * 0.12f;
            float releaseChance = shatterSeverity * (0.82f * edgeRetention);
            releasedFragments[sector, ring] = random.NextDouble() < releaseChance;
        }

        for (int z = 0; z < resolution; z++)
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            Vector3 sample = new((x + 0.5f) / resolution, (y + 0.5f) / resolution, (z + 0.5f) / resolution);
            (float u, float v) = Project(sample, normalAxis);
            float du = u - u0, dv = v - v0;
            float radial = MathF.Sqrt(du * du + dv * dv);
            float angle = MathF.Atan2(dv, du);
            bool crack = radial < hole;
            for (int ray = 0; ray < rays && !crack; ray++)
            {
                float angularDistance = MathF.Abs(MathF.Atan2(
                    MathF.Sin(angle - rayAngles[ray]), MathF.Cos(angle - rayAngles[ray])));
                float jitter = 0.028f + fractureSeverity * 0.014f +
                               0.010f * MathF.Sin(radial * 31f + ray * 2.37f + seed * 0.001f);
                crack = angularDistance * MathF.Max(radial, 0.08f) < jitter;
            }
            if (!crack && fractureSeverity > 0.2f)
            {
                float firstRing = MathF.Abs(radial - 0.22f - 0.035f * MathF.Sin(angle * 3f + seed));
                float secondRing = MathF.Abs(radial - 0.40f - 0.025f * MathF.Sin(angle * 5f - seed));
                crack = firstRing < 0.018f + fractureSeverity * 0.010f ||
                        secondRing < 0.014f + fractureSeverity * 0.008f;
            }
            float normalizedAngle = (angle + MathF.PI) / MathF.Tau;
            int sectorIndex = Math.Clamp((int)(normalizedAngle * fragmentSectors), 0, fragmentSectors - 1);
            int ringIndex = Math.Clamp((int)(radial / 0.72f * fragmentRings), 0, fragmentRings - 1);
            bool released = shatterSeverity > 0f && releasedFragments[sectorIndex, ringIndex];
            if (crack || released) micro.PaintCell(resolution, x, y, z, 0);
        }
        _world.SetMicroVoxels(impact.X, impact.Y, impact.Z, micro.IsEmpty ? null : micro);
    }

    private void FractureGridGlass(EnergyImpact impact, PhysicsGrid source)
    {
        float pressure = impact.Force / MathF.Max(0.08f, MathF.PI * impact.Radius * impact.Radius);
        if (pressure < 2.2f && impact.Force < 7f) return;

        PhysicsGridElement[] hitElements = source.Elements.Where(element =>
            element.CellX == impact.X && element.CellY == impact.Y && element.CellZ == impact.Z &&
            element.MaterialId == impact.MaterialId).ToArray();
        if (hitElements.Length == 0) return;

        int seed = HashCode.Combine(_world.Seed, source.Id, impact.X, impact.Y, impact.Z);
        Vector3 localNormal = Vector3.TransformNormal(impact.Normal, Matrix4x4.CreateFromQuaternion(
            Quaternion.Inverse(source.Orientation)));
        int normalAxis = DominantAxis(localNormal);
        var groups = new List<List<PhysicsGridElement>>();
        var shardSkins = new List<PhysicsGridSmoothSphere?>();
        PhysicsGridSmoothSphere? authoredShard = source.SmoothSpheres.FirstOrDefault(sphere =>
            sphere.CellX == impact.X && sphere.CellY == impact.Y && sphere.CellZ == impact.Z &&
            sphere.MaterialId == impact.MaterialId && sphere.HasExplicitCorners);
        if (source.FractureGeneration > 0 && authoredShard is { HasExplicitCorners: true } parentSkin)
        {
            // Re-fracture the actual triangular prism.  Rebuilding from its axis-aligned
            // bounds duplicates volume and can make an entire pile appear to vanish when
            // the oversized replacements are culled. Three children preserve the exact
            // parent surface and keep the 8 -> 24 -> 72 worst-case growth bounded.
            foreach (Vector3[] corners in SubdivideTrianglePrism(parentSkin.ExplicitCorners!))
            {
                (Vector3 minimum, Vector3 maximum) = CreateCompactCollisionBounds(corners);
                groups.Add(
                [
                    new PhysicsGridElement(hitElements[0].CellX, hitElements[0].CellY, hitElements[0].CellZ,
                        minimum, maximum, hitElements[0].MaterialId),
                ]);
                shardSkins.Add(new PhysicsGridSmoothSphere(
                    corners.Aggregate(Vector3.Zero, (sum, point) => sum + point) / corners.Length,
                    parentSkin.Radius * 0.62f, parentSkin.MaterialId,
                    parentSkin.CellX, parentSkin.CellY, parentSkin.CellZ)
                {
                    ExplicitCorners = corners,
                });
            }
        }
        else if (hitElements.Length == 1)
        {
            PhysicsGridElement element = hitElements[0];
            (float hitU, float hitV) = Project(source.WorldToLocal(impact.Position), normalAxis);
            Vector3 boundsSize = element.Maximum - element.Minimum;
            (float boundsU0, float boundsV0) = Project(element.Minimum, normalAxis);
            (float boundsU1, float boundsV1) = Project(element.Maximum, normalAxis);
            var hitUv = new Vector2(
                Math.Clamp((hitU - boundsU0) / MathF.Max(boundsU1 - boundsU0, 0.0001f), 0.16f, 0.84f),
                Math.Clamp((hitV - boundsV0) / MathF.Max(boundsV1 - boundsV0, 0.0001f), 0.16f, 0.84f));
            foreach (FragmentTriangle triangle in CreateFragmentTriangles(seed, hitUv))
            {
                Vector3[] corners = CreateTrianglePrismCorners(
                    element.Minimum, element.Maximum, normalAxis, triangle);
                (Vector3 minimum, Vector3 maximum) = CreateCompactCollisionBounds(corners);
                groups.Add(
                [
                    new PhysicsGridElement(element.CellX, element.CellY, element.CellZ,
                        minimum, maximum, element.MaterialId),
                ]);
                shardSkins.Add(new PhysicsGridSmoothSphere(
                    corners.Aggregate(Vector3.Zero, (sum, point) => sum + point) / corners.Length,
                    MathF.Max(boundsSize.X, MathF.Max(boundsSize.Y, boundsSize.Z)) * 0.55f,
                    element.MaterialId, element.CellX, element.CellY, element.CellZ)
                {
                    ExplicitCorners = corners,
                });
            }
        }
        else
        {
            // Already-carved Micro Voxel glass must never be subdivided again. Assign its
            // existing elements to at most six coherent groups; this keeps the original
            // detail but prevents the old multiplicative N*64 fracture explosion.
            for (int index = 0; index < Math.Min(6, hitElements.Length); index++)
                groups.Add(new List<PhysicsGridElement>());
            foreach (PhysicsGridElement element in hitElements)
            {
                (float u, float v) = Project(element.Center, normalAxis);
                int groupHash = HashCode.Combine(seed,
                    (int)MathF.Floor(u * 17f), (int)MathF.Floor(v * 17f));
                int groupIndex = (groupHash & int.MaxValue) % groups.Count;
                groups[groupIndex].Add(element);
            }
            groups.RemoveAll(group => group.Count == 0);
            for (int index = 0; index < groups.Count; index++) shardSkins.Add(null);
        }

        int nextGeneration = Math.Min(
            MaximumGlassFractureGeneration, source.FractureGeneration + 1);
        bool retainedSource = source.Cells.Count == 1 &&
                              source.TryReplaceCellGeometry(
                                  impact.X, impact.Y, impact.Z,
                                  groups[0], shardSkins[0]);
        int firstNewShard = 0;
        if (retainedSource)
        {
            ConfigureFracturedShard(source, seed, 0, impact, nextGeneration);
            firstNewShard = 1;
        }
        else
        {
            source.TryRemoveCell(impact.X, impact.Y, impact.Z);
        }

        for (int shardIndex = firstNewShard; shardIndex < groups.Count; shardIndex++)
        {
            if (groups[shardIndex].Count == 0) continue;
            PhysicsGrid shard = _world.PhysicsGrids.CreateFromGridElements(groups[shardIndex], source);
            if (shardSkins[shardIndex] is PhysicsGridSmoothSphere skin)
                shard.SetSmoothSpheres([skin]);
            ConfigureFracturedShard(shard, seed, shardIndex, impact, nextGeneration);
        }
        if (!retainedSource && source.Elements.Count == 0) _world.PhysicsGrids.Remove(source);
        else _world.PhysicsGrids.NotifyChanged();
    }

    private static void ConfigureFracturedShard(
        PhysicsGrid shard,
        int seed,
        int shardIndex,
        EnergyImpact impact,
        int generation)
    {
        var random = new Random(HashCode.Combine(seed, shardIndex));
        Vector3 scatter = new((float)random.NextDouble() - 0.5f,
            0.12f + (float)random.NextDouble() * 0.45f,
            (float)random.NextDouble() - 0.5f);
        scatter = Vector3.Normalize(scatter);
        shard.LinearVelocity += impact.Direction * (0.9f + impact.Force * 0.04f) + scatter * 1.1f;
        shard.LinearVelocity = ClampMagnitude(shard.LinearVelocity, 18f);
        shard.AngularVelocity += new Vector3((float)random.NextDouble() - 0.5f,
            (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 6f;
        shard.AngularVelocity = ClampMagnitude(shard.AngularVelocity, 24f);
        shard.FractureGeneration = generation;
        shard.FractureOnImpact = generation < MaximumGlassFractureGeneration;
        shard.IsSleeping = false;
    }

    private static void ApplyBoundedEnergyImpulse(PhysicsGrid grid, Vector3 requestedImpulse, Vector3 worldPoint)
    {
        float magnitude = requestedImpulse.Length();
        if (magnitude <= 0.000001f) return;
        // Energy force is authored for one-metre blocks. Applying the same raw impulse
        // to a tiny triangular child divides it by a near-zero mass and launches it
        // hundreds of blocks per second. Bound per-hit velocity while preserving force
        // attenuation and mass ordering for ordinary structures.
        float maximumImpulse = MathF.Max(0.02f, grid.Mass * 10f);
        Vector3 impulse = magnitude > maximumImpulse
            ? requestedImpulse * (maximumImpulse / magnitude)
            : requestedImpulse;
        grid.ApplyImpulseAtPoint(impulse, worldPoint);
        grid.LinearVelocity = ClampMagnitude(grid.LinearVelocity, 18f);
        grid.AngularVelocity = ClampMagnitude(grid.AngularVelocity, 24f);
    }

    private static Vector3 ClampMagnitude(Vector3 value, float maximum)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > maximum * maximum
            ? value * (maximum / MathF.Sqrt(lengthSquared))
            : value;
    }

    private void SpawnCoarseGlassShards(EnergyImpact impact, int seed, int normalAxis)
    {
        Vector3 localImpact = impact.Position - new Vector3(impact.X, impact.Y, impact.Z);
        (float hitU, float hitV) = Project(localImpact, normalAxis);
        IReadOnlyList<FragmentTriangle> fragments = CreateFragmentTriangles(
            seed, new Vector2(Math.Clamp(hitU, 0.16f, 0.84f), Math.Clamp(hitV, 0.16f, 0.84f)));
        for (int shardIndex = 0; shardIndex < fragments.Count; shardIndex++)
        {
            FragmentTriangle fragment = fragments[shardIndex];
            Vector3[] corners = CreateTrianglePrismCorners(
                Vector3.Zero, Vector3.One, normalAxis, fragment);
            (Vector3 minimum, Vector3 maximum) = CreateCompactCollisionBounds(corners);
            var element = new PhysicsGridElement(0, 0, 0, minimum, maximum, impact.MaterialId);
            PhysicsGrid shard = _world.PhysicsGrids.Create(
                [element],
                new Vector3(impact.X, impact.Y, impact.Z),
                materialId => _blocks.Get(materialId).Density);
            shard.SetSmoothSpheres(
            [
                new PhysicsGridSmoothSphere(
                    corners.Aggregate(Vector3.Zero, (sum, point) => sum + point) / corners.Length,
                    0.72f, impact.MaterialId, 0, 0, 0)
                {
                    ExplicitCorners = corners,
                },
            ]);
            var random = new Random(HashCode.Combine(seed, shardIndex));
            Vector3 scatter = element.Center - localImpact;
            scatter = RemoveAxis(scatter, normalAxis);
            scatter += new Vector3(
                ((float)random.NextDouble() - 0.5f) * 0.18f,
                (float)random.NextDouble() * 0.22f,
                ((float)random.NextDouble() - 0.5f) * 0.18f);
            if (scatter.LengthSquared() > 0.0001f) scatter = Vector3.Normalize(scatter);
            shard.LinearVelocity = impact.Direction * (1.25f + impact.Force * 0.035f) + scatter * 1.35f;
            shard.AngularVelocity = new Vector3(
                (float)random.NextDouble() - 0.5f,
                (float)random.NextDouble() - 0.5f,
                (float)random.NextDouble() - 0.5f) * 5f;
            shard.FractureGeneration = 1;
            shard.FractureOnImpact = true;
            shard.IsSleeping = false;
        }
    }

    private static IReadOnlyList<FragmentTriangle> CreateFragmentTriangles(int seed, Vector2 impact)
    {
        var random = new Random(seed);
        impact += new Vector2(
            ((float)random.NextDouble() - 0.5f) * 0.08f,
            ((float)random.NextDouble() - 0.5f) * 0.08f);
        impact = Vector2.Clamp(impact, new Vector2(0.14f), new Vector2(0.86f));

        // Four corners guarantee complete coverage of the pane. One jittered point on
        // every edge breaks the square into eight deterministic radial shards without
        // rasterizing it into hundreds of cubes.
        var boundary = new List<Vector2>(8)
        {
            Vector2.Zero,
            new(0.30f + (float)random.NextDouble() * 0.40f, 0f),
            Vector2.UnitX,
            new(1f, 0.30f + (float)random.NextDouble() * 0.40f),
            Vector2.One,
            new(0.70f - (float)random.NextDouble() * 0.40f, 1f),
            Vector2.UnitY,
            new(0f, 0.70f - (float)random.NextDouble() * 0.40f),
        };
        var result = new List<FragmentTriangle>(boundary.Count);
        for (int index = 0; index < boundary.Count; index++)
        {
            Vector2 a = boundary[index];
            Vector2 b = boundary[(index + 1) % boundary.Count];
            result.Add(new FragmentTriangle(impact, a, b));
        }
        return result;
    }

    private static IReadOnlyList<Vector3[]> SubdivideTrianglePrism(Vector3[] parent)
    {
        if (parent.Length != 8) throw new ArgumentException("A shard prism requires eight authored corners.", nameof(parent));
        Vector3[] front = [parent[0], parent[1], parent[2]];
        Vector3[] back = [parent[4], parent[5], parent[6]];
        Vector3 frontCenter = (front[0] + front[1] + front[2]) / 3f;
        Vector3 backCenter = (back[0] + back[1] + back[2]) / 3f;
        var children = new List<Vector3[]>(3);
        for (int edge = 0; edge < 3; edge++)
        {
            int next = (edge + 1) % 3;
            // Same U,V,depth topology as CreateTrianglePrismCorners. The fourth
            // vertex duplicates the third because the shared surface builder consumes
            // an authored quad whose last edge is degenerate for triangular prisms.
            children.Add([
                frontCenter, front[edge], front[next], front[next],
                backCenter, back[edge], back[next], back[next],
            ]);
        }
        return children;
    }

    private static Vector3[] CreateTrianglePrismCorners(
        Vector3 boundsMinimum,
        Vector3 boundsMaximum,
        int normalAxis,
        FragmentTriangle triangle)
    {
        Vector3 size = boundsMaximum - boundsMinimum;
        Vector2[] quad = [triangle.Center, triangle.A, triangle.B, triangle.B];
        var corners = new Vector3[8];
        for (int depth = 0; depth <= 1; depth++)
        for (int vBit = 0; vBit <= 1; vBit++)
        for (int uBit = 0; uBit <= 1; uBit++)
        {
            Vector2 uv = quad[uBit + vBit * 2];
            Vector3 point = normalAxis switch
            {
                0 => new Vector3(boundsMinimum.X + size.X * depth,
                    boundsMinimum.Y + size.Y * uv.Y, boundsMinimum.Z + size.Z * uv.X),
                1 => new Vector3(boundsMinimum.X + size.X * uv.X,
                    boundsMinimum.Y + size.Y * depth, boundsMinimum.Z + size.Z * uv.Y),
                _ => new Vector3(boundsMinimum.X + size.X * uv.X,
                    boundsMinimum.Y + size.Y * uv.Y, boundsMinimum.Z + size.Z * depth),
            };
            // Corner topology is always authored as U,V,depth even when those axes map
            // to another world plane. This lets every renderer treat the shard as one
            // coherent triangular prism instead of reconnecting physical-axis indices.
            corners[uBit + vBit * 2 + depth * 4] = point;
        }
        return corners;
    }

    private static (Vector3 Minimum, Vector3 Maximum) CreateCompactCollisionBounds(Vector3[] corners)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        Vector3 center = Vector3.Zero;
        foreach (Vector3 corner in corners)
        {
            minimum = Vector3.Min(minimum, corner);
            maximum = Vector3.Max(maximum, corner);
            center += corner;
        }
        center /= corners.Length;
        Vector3 half = Vector3.Max((maximum - minimum) * 0.18f, new Vector3(0.035f));
        return (Vector3.Max(minimum, center - half), Vector3.Min(maximum, center + half));
    }

    private static Vector3 RemoveAxis(Vector3 value, int axis) => axis switch
    {
        0 => value with { X = 0f },
        1 => value with { Y = 0f },
        _ => value with { Z = 0f },
    };

    private readonly record struct FragmentTriangle(Vector2 Center, Vector2 A, Vector2 B);

    public void Dispose()
    {
        ReleaseHeld(Vector3.Zero);
        foreach (Projectile projectile in _projectiles) _world.AnalyticSpheres.Remove(projectile.Proxy.Id);
        _projectiles.Clear();
    }

    public static float ForceFor(EnergyForceLevel level) => level switch
    {
        EnergyForceLevel.Low => 3f,
        EnergyForceLevel.Medium => 12f,
        _ => 36f,
    };

    private ushort ResolveProxyMaterial()
    {
        foreach (string key in new[] { "rtxsandbox:energy_core", "rtxsandbox:plasma_conduit", "rtxsandbox:white_led" })
            if (_blocks.TryGet(key, out BlockDefinition? material) && material is not null) return material.Id;
        return (ushort)VoxelMaterial.Glow;
    }

    private static bool IsGlass(BlockDefinition block) =>
        block.IsTransparent &&
        (block.Tags.Any(tag => tag.Contains("glass", StringComparison.OrdinalIgnoreCase)) ||
         block.Key.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
         block.DisplayName.Contains("Glass", StringComparison.OrdinalIgnoreCase) ||
         block.DisplayName.Contains("玻璃", StringComparison.Ordinal));

    private static int DominantAxis(Vector3 normal)
    {
        Vector3 absolute = Vector3.Abs(normal);
        return absolute.X >= absolute.Y && absolute.X >= absolute.Z ? 0 : absolute.Y >= absolute.Z ? 1 : 2;
    }

    private static (float U, float V) Project(Vector3 value, int normalAxis) => normalAxis switch
    {
        0 => (value.Z, value.Y),
        1 => (value.X, value.Z),
        _ => (value.X, value.Y),
    };

    private static bool IntersectSphere(Vector3 origin, Vector3 direction, AnalyticSphere sphere,
        float maximumDistance, out float distance)
    {
        Vector3 offset = origin - sphere.Center;
        float projected = Vector3.Dot(offset, direction);
        float discriminant = projected * projected - (Vector3.Dot(offset, offset) - sphere.Radius * sphere.Radius);
        if (discriminant < 0f) { distance = 0f; return false; }
        float root = MathF.Sqrt(discriminant);
        distance = -projected - root;
        if (distance <= 0.001f) distance = -projected + root;
        return distance > 0.001f && distance <= maximumDistance;
    }

    private readonly record struct Projectile(
        AnalyticSphere Proxy,
        Vector3 Velocity,
        float Force,
        float Radius,
        float RemainingLife);
}
