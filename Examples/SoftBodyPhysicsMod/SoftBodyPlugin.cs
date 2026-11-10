using System.Numerics;
using System.Reflection;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NET.Common;
using HarmonyLib;
using RTXSandbox.Gameplay;
using RTXSandbox.Engine;
using RTXSandbox.Persistence;
using RTXSandbox.World;

namespace RTXSandbox.SoftBodyPhysicsMod;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class SoftBodyPlugin : BasePlugin
{
    public const string PluginId = "rtxsandbox.soft-body-physics";
    public const string PluginName = "RTX Sandbox 软体物理";
    public const string PluginVersion = "1.3.0";

    private readonly Dictionary<ushort, SoftMaterial> _materials = new();
    private readonly List<SoftBody> _bodies = new();
    private SandboxGame? _game;
    private VoxelWorld? _world;
    private PlayerController? _player;
    private ConfigEntry<bool>? _enabled;
    private ConfigEntry<int>? _maximumBodies;
    private ConfigEntry<int>? _solverIterations;
    private PhysicsGridWorld? _subscribedGridWorld;
    private VoxelWorld? _subscribedVoxelWorld;
    private VoxelPhysicsSimulator? _physics;
    private readonly HashSet<(int X, int Y, int Z)> _pendingStaticSoftSeeds = new();
    private readonly HashSet<ulong> _capacityWarnings = new();
    private readonly Dictionary<ulong, int> _examinedGridTopology = new();
    private float _accumulator;
    private float _geometryAccumulator;
    private float _staticExtractionCountdown;
    private bool _extractingStaticStructure;
    private bool _smokeActive;
    private bool _smokeReported;
    private float _smokeElapsed;
    private float _smokeStartY;
    private Vector3 _smokeExpectedCenter;
    private float _smokeMaximumStrain;
    private float _smokeMaximumDeviation;
    private Vector3 _smokeSettledCenter;
    private bool _smokeSettlementCaptured;
    private bool _smokeAssemblyVerified;
    private bool _smokeBreakStarted;
    private float _smokeBreakElapsed;
    private ulong _smokeDetachedMetalId;
    private float _smokeDetachedMetalStartY;
    private int _smokeSupportX;
    private int _smokeSupportY;
    private int _smokeSupportZ;
    private bool _smokeSupportReleaseStarted;
    private ulong _smokeReleasedBodyId;
    private Vector3 _smokeReleasedBodyStart;
    private float _smokeReleasedBodyStartDeviation;
    private long _lastGameUpdateTimestamp;

    public override void Load()
    {
        _enabled = Config.Bind("Simulation", "Enabled", true, "Enable soft-body simulation.");
        _maximumBodies = Config.Bind("Simulation", "MaximumBodies", 32,
            new ConfigDescription("Maximum live soft bodies.", new AcceptableValueRange<int>(1, 128)));
        _solverIterations = Config.Bind("Simulation", "SolverIterations", 5,
            new ConfigDescription("Distance-constraint iterations per fixed step.", new AcceptableValueRange<int>(2, 12)));
        SoftBodyRuntime.InstallPatches(HarmonyInstance, Log, this);
        Log.LogInfo("软体物理 V1.3 已载入：直接通过 BepInEx/Harmony 接入游戏本体，不依赖 Mod API。");
    }

    private void AttachGame(SandboxGame game)
    {
        if (ReferenceEquals(_game, game)) return;
        _game = game;
        _world = game.World;
        _player = game.Player;
        _physics = game.Physics;
        RegisterMaterials(game.Blocks);
        OnWorldReady(game.World);
        Log.LogInfo("软体物理已直接接入 SandboxGame、VoxelWorld 与 PhysicsGrid。");
    }

    private void TickFromGame(SandboxGame game)
    {
        AttachGame(game);
        long now = Stopwatch.GetTimestamp();
        float deltaSeconds = _lastGameUpdateTimestamp == 0
            ? 1f / Math.Max(30, game.Settings.FpsLimit)
            : (float)Stopwatch.GetElapsedTime(_lastGameUpdateTimestamp, now).TotalSeconds;
        _lastGameUpdateTimestamp = now;
        OnUpdating(Math.Clamp(deltaSeconds, 0.0001f, 0.1f));
    }

    private void RegisterMaterials(BlockRegistry blocks)
    {
        Register(blocks, new SoftMaterial(
            "softphysics:gel", "透明凝胶", new Vector3(0.12f, 0.62f, 0.95f),
            BlockSurfacePattern.Grid, 0.72f, 0.965f, 0.020f, 0.55f, 0.72f, 1.05f, true));
        Register(blocks, new SoftMaterial(
            "softphysics:rubber", "弹性橡胶", new Vector3(0.88f, 0.06f, 0.08f),
            BlockSurfacePattern.Fabric, 0.62f, 0.993f, 0.12f, 0.30f, 0.82f, 1.35f, false));
        Register(blocks, new SoftMaterial(
            "softphysics:foam", "柔软泡棉", new Vector3(0.95f, 0.72f, 0.16f),
            BlockSurfacePattern.Panels, 0.22f, 0.965f, 0.025f, 0.62f, 0.68f, 0.35f, false));
        blocks.NotifyChanged();
    }

    private void Register(BlockRegistry blocks, SoftMaterial material)
    {
        ushort id = blocks.Register(new BlockDefinition(
            material.Key,
            material.DisplayName,
            material.Albedo,
            roughness: material.Transparent ? 0.16f : material.Key.EndsWith("rubber", StringComparison.Ordinal) ? 0.42f : 0.86f,
            metallic: 0f,
            isCollidable: true,
            isTransparent: material.Transparent,
            indexOfRefraction: material.Transparent ? 1.37f : 1.5f,
            transmission: material.Transparent ? 0.62f : 0f,
            surfacePattern: material.Pattern,
            hardnessSeconds: 0.3f,
            category: "软体物理",
            tags: new[] { "soft-body", "physics", "软体", "弹性" },
            soundSet: material.Key.EndsWith("foam", StringComparison.Ordinal) ? "wool" : "slime",
             particleEffect: "soft",
             density: material.Density));
        _materials[id] = material with { MaterialId = id };
    }

    private void OnWorldReady(VoxelWorld _)
    {
        if (_world is null) return;
        if (_subscribedGridWorld is not null)
            _subscribedGridWorld.Changed -= OnPhysicsGridsChanged;
        if (_subscribedVoxelWorld is not null)
            _subscribedVoxelWorld.DetailedVoxelChanged -= OnStaticVoxelChanged;
        _player = _game?.Player;
        _physics = _game?.Physics;
        _bodies.Clear();
        _pendingStaticSoftSeeds.Clear();
        _capacityWarnings.Clear();
        _examinedGridTopology.Clear();
        SoftBodyRuntime.Reset(_world);
        SoftBodyRuntime.SetCoreEditCompleted(ConvertPendingPhysicsStructures);
        _subscribedGridWorld = _world.PhysicsGrids;
        _subscribedGridWorld.Changed += OnPhysicsGridsChanged;
        _subscribedVoxelWorld = _world;
        _subscribedVoxelWorld.DetailedVoxelChanged += OnStaticVoxelChanged;
        _accumulator = 0f;
        _geometryAccumulator = 0f;
        _staticExtractionCountdown = 0f;
        _smokeElapsed = 0f;
        _smokeReported = false;
        _smokeMaximumStrain = 0f;
        _smokeMaximumDeviation = 0f;
        _smokeSettlementCaptured = false;
        _smokeAssemblyVerified = false;
        _smokeBreakStarted = false;
        _smokeBreakElapsed = 0f;
        _smokeDetachedMetalId = 0;
        _smokeDetachedMetalStartY = 0f;
        _smokeSupportReleaseStarted = false;
        _smokeReleasedBodyId = 0;
        _smokeReleasedBodyStart = Vector3.Zero;
        _smokeActive = Environment.GetEnvironmentVariable("RTX_SANDBOX_SOFT_BODY_SMOKE") == "1";
        Environment.SetEnvironmentVariable("RTX_SANDBOX_SOFT_BODY_SMOKE_DONE", _smokeActive ? "0" : null);
        if (_smokeActive &&
            _materials.Values.FirstOrDefault(material => material.Key == "softphysics:gel") is SoftMaterial gel &&
            _game?.Blocks.Definitions.FirstOrDefault(definition => definition.Key == "rtxsandbox:iron_block") is BlockDefinition iron)
        {
            int x = VoxelWorld.Width / 2 + 3;
            int z = VoxelWorld.Depth / 2 + 3;
            int terrainTop = Enumerable.Range(-1, 7)
                .SelectMany(offsetX => Enumerable.Range(-1, 3)
                    .Select(offsetZ => _world.FindTopVoxelY(x + offsetX, z + offsetZ)))
                .Max();
            int baseSurfaceY = terrainTop + 1;
            // Leave enough clearance for the weighted cantilever to sag visibly and
            // still let its detached metal tip perform an unambiguous free fall.
            int y = baseSurfaceY + 10;
            for (int sceneX = x - 1; sceneX <= x + 5; sceneX++)
            for (int sceneZ = z - 1; sceneZ <= z + 1; sceneZ++)
            {
                int columnTop = _world.FindTopVoxelY(sceneX, sceneZ);
                for (int fillY = columnTop + 1; fillY < baseSurfaceY; fillY++)
                    _world.SetBlockId(sceneX, fillY, sceneZ, iron.Id);
                for (int clearY = baseSurfaceY; clearY <= y + 2; clearY++)
                    _world.SetBlockId(sceneX, clearY, sceneZ, 0);
            }
            _smokeStartY = y + 0.5f;
            _smokeExpectedCenter = new Vector3(x + 2.5f, y + 0.5f, z + 0.5f);
            _smokeSupportX = x - 1;
            _smokeSupportY = y;
            _smokeSupportZ = z;
            for (int pedestalY = baseSurfaceY; pedestalY <= y; pedestalY++)
                _world.SetBlockId(x - 1, pedestalY, z, (ushort)VoxelMaterial.Bedrock);
            // Deliberately build this through ordinary static world voxels. The smoke
            // must exercise the same automatic activation path as player construction;
            // directly creating a PhysicsGrid would hide the supported-structure bug.
            for (int offset = 0; offset < 5; offset++)
                _world.SetBlockId(x + offset, y, z, offset == 4 ? iron.Id : gel.MaterialId);
        }
        ExtractAllLoadedStaticSoftStructures();
        ConvertPendingPhysicsStructures();
    }

    private void OnPhysicsGridsChanged() => ConvertPendingPhysicsStructures();

    private void OnStaticVoxelChanged(VoxelChangedEvent change)
    {
        if (_extractingStaticStructure || _world is null) return;
        if (_materials.ContainsKey(change.NewBlockId))
            _pendingStaticSoftSeeds.Add((change.X, change.Y, change.Z));

        ReadOnlySpan<(int X, int Y, int Z)> neighbors = stackalloc (int, int, int)[]
        {
            (change.X + 1, change.Y, change.Z), (change.X - 1, change.Y, change.Z),
            (change.X, change.Y + 1, change.Z), (change.X, change.Y - 1, change.Z),
            (change.X, change.Y, change.Z + 1), (change.X, change.Y, change.Z - 1),
        };
        foreach ((int x, int y, int z) in neighbors)
        {
            if (_materials.ContainsKey(_world.GetBlockId(x, y, z)))
                _pendingStaticSoftSeeds.Add((x, y, z));
        }
        if (_pendingStaticSoftSeeds.Count > 0) _staticExtractionCountdown = 0.75f;
    }

    private void ExtractAllLoadedStaticSoftStructures()
    {
        if (_physics is null || _materials.Count == 0) return;
        _extractingStaticStructure = true;
        try
        {
            int extracted = _physics.ExtractLoadedSoftStructures(_materials.Keys.ToArray());
            if (extracted > 0)
                Log.LogInfo($"已自动启用 {extracted} 个静态软体结构；支撑面保留为固定端。");
        }
        finally
        {
            _extractingStaticStructure = false;
            _pendingStaticSoftSeeds.Clear();
        }
    }

    private void ExtractPendingStaticSoftStructures(float deltaSeconds)
    {
        if (_physics is null || _world is null || _pendingStaticSoftSeeds.Count == 0) return;
        _staticExtractionCountdown -= Math.Clamp(deltaSeconds, 0f, 0.05f);
        if (_staticExtractionCountdown > 0f) return;

        (int X, int Y, int Z)[] seeds = _pendingStaticSoftSeeds.ToArray();
        _pendingStaticSoftSeeds.Clear();
        _extractingStaticStructure = true;
        try
        {
            foreach ((int x, int y, int z) in seeds)
                _physics.TryExtractSoftStructure(x, y, z, _materials.Keys.ToArray());
        }
        finally
        {
            _extractingStaticStructure = false;
        }
        ConvertPendingPhysicsStructures();
    }

    private void ConvertPendingPhysicsStructures()
    {
        if (_enabled?.Value != true || _world is null || _game is null ||
            SoftBodyRuntime.CoreEditInProgress) return;
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids.ToArray())
        {
            if (SoftBodyRuntime.IsOwned(grid.Id) || grid.Elements.Count == 0) continue;
            if (_examinedGridTopology.TryGetValue(grid.Id, out int examinedVersion) &&
                examinedVersion == grid.TopologyVersion) continue;
            if (!grid.Elements.Any(element => _materials.ContainsKey(element.MaterialId)))
            {
                _examinedGridTopology[grid.Id] = grid.TopologyVersion;
                continue;
            }
            if (grid.Elements.Count > SoftBody.MaximumSourceElements)
            {
                _examinedGridTopology[grid.Id] = grid.TopologyVersion;
                if (_capacityWarnings.Add(grid.Id))
                    Log.LogWarning(
                        $"Physics Grid #{grid.Id} 有 {grid.Elements.Count} 个元素，超过软体安全上限 " +
                        $"{SoftBody.MaximumSourceElements}；保留为普通刚体，避免求解器冻结游戏。");
                continue;
            }
            if (_bodies.Count >= (_maximumBodies?.Value ?? 32))
            {
                if (_capacityWarnings.Add(grid.Id))
                    Log.LogWarning($"软体数量已达到上限；Physics Grid #{grid.Id} 暂时保留为普通刚体。");
                continue;
            }

            var body = new SoftBody(grid, _materials, _game.World, _game.Blocks);
            grid.FractureOnImpact = false;
            body.Grid = grid;
            SoftBodyRuntime.Add(_world, body);
            _bodies.Add(body);
            SoftBodyRuntime.UpdateGeometry(
                _world, grid, body.Center, body.RenderOrientation,
                body.BuildCells(), body.BuildSmoothSpheres(),
                body.AverageVelocity, sleeping: false);
            body.MarkGeometryUpdated();
            Log.LogInfo(
                $"Physics Grid #{grid.Id} 已整体转换为软体：{body.SourceElementCount} 格，" +
                $"{body.ParticleCount} 质点，{body.PinnedParticleCount} 个固定点。" +
                "软体材质会主动启用求解；支撑只固定接触面，不再阻止形变。");
        }
    }

    private void OnUpdating(float deltaSeconds)
    {
        if (_enabled?.Value != true || _world is null || _game is null) return;
        ExtractPendingStaticSoftStructures(deltaSeconds);
        // Changed is raised while the core is still splitting a damaged grid.
        // Delay reconversion until the edit transaction is complete.
        ConvertPendingPhysicsStructures();
        _bodies.RemoveAll(body => body.Grid is null || !SoftBodyRuntime.IsOwned(body.Grid.Id));
        if (_bodies.Count == 0)
        {
            if (_smokeActive && _smokeBreakStarted) UpdateDetachedMetalSmoke(deltaSeconds);
            return;
        }
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.05f);
        _accumulator += deltaSeconds;
        _geometryAccumulator += deltaSeconds;
        const float fixedStep = 1f / 120f;
        int steps = 0;
        while (_accumulator >= fixedStep && steps++ < 6)
        {
            foreach (SoftBody body in _bodies)
                body.Step(fixedStep, _solverIterations?.Value ?? 5, _game.World, _game.Blocks, _player);
            _accumulator -= fixedStep;
        }
        if (steps >= 6) _accumulator = Math.Min(_accumulator, fixedStep);

        if (_geometryAccumulator < 1f / 60f) return;
        _geometryAccumulator = 0f;
        for (int index = _bodies.Count - 1; index >= 0; index--)
        {
            SoftBody body = _bodies[index];
            if (body.Grid is null || !SoftBodyRuntime.IsOwned(body.Grid.Id))
            {
                _bodies.RemoveAt(index);
                continue;
            }
            if (body.GeometryDirty)
            {
                SoftBodyRuntime.UpdateGeometry(
                    _world,
                    body.Grid,
                    body.Center,
                    body.RenderOrientation,
                    body.BuildCells(),
                    body.BuildSmoothSpheres(),
                    body.AverageVelocity,
                    body.IsSleeping);
                body.MarkGeometryUpdated();
            }
        }

        if (_smokeActive && !_smokeReported)
        {
            _smokeElapsed += deltaSeconds;
            if (_smokeBreakStarted)
            {
                UpdateDetachedMetalSmoke(deltaSeconds);
                return;
            }
            SoftBody? smokeBody = _bodies
                .Where(body => body.SourceElementCount == 5 && body.RigidParticleCount > 0)
                .OrderBy(body => Vector3.DistanceSquared(body.Center, _smokeExpectedCenter))
                .FirstOrDefault();
            if (smokeBody is not null)
            {
                _smokeMaximumStrain = MathF.Max(_smokeMaximumStrain, smokeBody.MaximumStrain);
                _smokeMaximumDeviation = MathF.Max(_smokeMaximumDeviation, smokeBody.MaximumShapeDeviation);
                if (!_smokeAssemblyVerified && _smokeElapsed >= 4f && smokeBody.Grid is PhysicsGrid assemblyGrid)
                {
                    VoxelPhysicsSimulator? assemblyPhysics = _game.Physics;
                    ushort gelId = _materials.Values.First(material =>
                        material.Key == "softphysics:gel").MaterialId;
                    var placementHit = new PhysicsGridRayHit(
                        assemblyGrid.Id,
                        4,
                        0,
                        0,
                        assemblyGrid.Elements.First(element => element.CellX == 4).MaterialId,
                        assemblyGrid.LocalToWorld(new Vector3(5f, 0.5f, 0.5f)),
                        assemblyGrid.LocalDirectionToWorld(Vector3.UnitX),
                        2f);
                    bool placed = assemblyPhysics?.TryPlaceOnGrid(placementHit, gelId) == true;
                    SoftBody? expanded = _bodies.FirstOrDefault(body =>
                        body.Grid?.Id == assemblyGrid.Id && body.SourceElementCount == 6);
                    (int X, int Y, int Z)? addedCell = expanded?.Grid?.Cells
                        .Where(cell => !(cell.X >= 0 && cell.X <= 4 && cell.Y == 0 && cell.Z == 0))
                        .Cast<(int X, int Y, int Z)?>()
                        .FirstOrDefault();
                    bool removed = placed && expanded?.Grid is not null && addedCell.HasValue &&
                                   assemblyPhysics?.TryRemoveGridCell(
                                       expanded.Grid.Id,
                                       addedCell.Value.X,
                                       addedCell.Value.Y,
                                       addedCell.Value.Z) == true;
                    if (!placed || expanded is null || !removed)
                    {
                        ReportSmokeFailure(
                            $"Active soft structure could not be extended and rebuilt " +
                            $"(placed={placed}, expanded={expanded is not null}, removed={removed}).");
                        return;
                    }
                    _smokeAssemblyVerified = true;
                    smokeBody = _bodies
                        .Where(body => body.SourceElementCount == 5 && body.RigidParticleCount > 0)
                        .OrderBy(body => Vector3.DistanceSquared(body.Center, _smokeExpectedCenter))
                        .FirstOrDefault();
                    if (smokeBody is null)
                    {
                        ReportSmokeFailure("Soft structure did not return to five cells after assembly verification.");
                        return;
                    }
                }
                if (!_smokeSettlementCaptured && _smokeElapsed >= 9.2f)
                {
                    _smokeSettledCenter = smokeBody.Center;
                    _smokeSettlementCaptured = true;
                }
            }
            if (_smokeElapsed >= 10f)
            {
                bool hardCellsKeepOriginalSize = smokeBody?.Grid?.Elements
                    .Where(element => !_materials.ContainsKey(element.MaterialId))
                    .All(element => Vector3.Distance(element.Maximum - element.Minimum, Vector3.One) < 0.002f)
                    == true;
                if (smokeBody?.Grid is null || smokeBody.SourceElementCount != 5 ||
                    smokeBody.Grid.SmoothSpheres.Count != 4 ||
                    smokeBody.Grid.Elements.Select(element => element.MaterialId).Distinct().Count() != 2 ||
                    smokeBody.RigidParticleCount == 0 || smokeBody.PinnedParticleCount == 0 ||
                    !_smokeAssemblyVerified ||
                    smokeBody.ParticleMassRatio < 2f ||
                    smokeBody.MaximumRigidShapeError > 0.04f ||
                    smokeBody.MaximumPinnedDrift > 0.001f ||
                    !hardCellsKeepOriginalSize ||
                    !float.IsFinite(smokeBody.Center.Y))
                {
                    ReportSmokeFailure(
                        "The connected four-soft-plus-heavy structure was not converted as one body " +
                        $"(bodies={_bodies.Count}, source={smokeBody?.SourceElementCount}, " +
                        $"skin={smokeBody?.Grid?.SmoothSpheres.Count}, cells={smokeBody?.Grid?.Elements.Count}, " +
                        $"materials={smokeBody?.Grid?.Elements.Select(element => element.MaterialId).Distinct().Count()}, " +
                        $"rigid={smokeBody?.RigidParticleCount}, pinned={smokeBody?.PinnedParticleCount}, " +
                        $"massRatio={smokeBody?.ParticleMassRatio:F2}, " +
                        $"rigidError={smokeBody?.MaximumRigidShapeError:F4}, " +
                        $"pinnedDrift={smokeBody?.MaximumPinnedDrift:F5}, " +
                        $"hardCellSize={hardCellsKeepOriginalSize}, " +
                        $"hardExtents={string.Join(';', smokeBody?.Grid?.Elements
                            .Where(element => !_materials.ContainsKey(element.MaterialId))
                            .Select(element => element.Maximum - element.Minimum) ?? [])}).");
                    return;
                }
                float horizontalDrift = _smokeSettlementCaptured
                    ? new Vector2(smokeBody.Center.X - _smokeSettledCenter.X, smokeBody.Center.Z - _smokeSettledCenter.Z).Length()
                    : float.PositiveInfinity;
                if (_smokeMaximumStrain < 0.020f)
                {
                    ReportSmokeFailure($"Body remained visually rigid (strain {_smokeMaximumStrain:F4}).");
                    return;
                }
                if (_smokeMaximumDeviation < 0.025f)
                {
                    ReportSmokeFailure(
                        $"Weighted cantilever did not visibly bend (deviation {_smokeMaximumDeviation:F4}).");
                    return;
                }
                if (!smokeBody.IsSleeping || horizontalDrift > 0.006f)
                {
                    ReportSmokeFailure(
                        $"Body did not settle without drift (sleeping={smokeBody.IsSleeping}, " +
                        $"drift={horizontalDrift:F5}, maxSpeed={smokeBody.MaximumParticleSpeed:F3}, " +
                        $"centerSpeed={smokeBody.AverageVelocity.Length():F3}).");
                    return;
                }
                bool saveCaptured = SoftBodyRuntime.VerifySaveCapture(_world);
                VoxelPhysicsSimulator? physics = _game.Physics;
                PhysicsGrid grid = smokeBody.Grid;
                bool broken = physics?.TryRemoveGridCell(grid.Id, 3, 0, 0) == true;
                ushort ironId = _game.Blocks.Definitions
                    .First(definition => definition.Key == "rtxsandbox:iron_block").Id;
                PhysicsGrid? detachedMetal = _world.PhysicsGrids.Grids.FirstOrDefault(candidate =>
                    !SoftBodyRuntime.IsOwned(candidate.Id) &&
                    candidate.Id > grid.Id &&
                    candidate.Elements.Count > 0 &&
                    candidate.Cells.Contains((4, 0, 0)) &&
                    candidate.Elements.All(element => element.MaterialId == ironId));
                if (!saveCaptured || !broken || detachedMetal is null)
                {
                    ReportSmokeFailure("Soft-body save capture or partial structure splitting did not run.");
                    return;
                }
                _smokeBreakStarted = true;
                _smokeBreakElapsed = 0f;
                _smokeDetachedMetalId = detachedMetal.Id;
                _smokeDetachedMetalStartY = detachedMetal.Position.Y;
            }
        }
    }

    private void UpdateDetachedMetalSmoke(float deltaSeconds)
    {
        if (_world is null || _smokeReported) return;
        _smokeBreakElapsed += deltaSeconds;
        if (_smokeBreakElapsed < 0.8f) return;
        PhysicsGrid? metal = _world.PhysicsGrids.Get(_smokeDetachedMetalId);
        if (metal is null)
        {
            ReportSmokeFailure("Detached metal disappeared during the support-release smoke test.");
            return;
        }
        if (!_smokeSupportReleaseStarted && metal.Position.Y >= _smokeDetachedMetalStartY - 0.12f)
        {
            string details = "missing";
            if (metal is not null)
            {
                (Vector3 minimum, Vector3 maximum) = metal.CalculateWorldBounds();
                int belowX = (int)MathF.Floor((minimum.X + maximum.X) * 0.5f);
                int belowY = (int)MathF.Floor(minimum.Y - 0.02f);
                int belowZ = (int)MathF.Floor((minimum.Z + maximum.Z) * 0.5f);
                details = $"boundsY={minimum.Y:F3}..{maximum.Y:F3}, velocityY={metal.LinearVelocity.Y:F3}, " +
                          $"sleeping={metal.IsSleeping}, below={_world.GetBlockId(belowX, belowY, belowZ)}";
            }
            ReportSmokeFailure(
                $"Detached metal remained floating (startY={_smokeDetachedMetalStartY:F3}, " +
                $"currentY={metal?.Position.Y:F3}, {details}).");
            return;
        }

        if (!_smokeSupportReleaseStarted)
        {
            SoftBody? releasedBody = _bodies
                .Where(body => body.SourceElementCount == 3 && body.Grid is not null)
                .OrderBy(body => Vector3.DistanceSquared(body.Center, _smokeExpectedCenter))
                .FirstOrDefault();
            if (releasedBody?.Grid is null)
            {
                ReportSmokeFailure("Remaining soft fragment was missing before support release.");
                return;
            }
            if (releasedBody.PinnedParticleCount == 0)
            {
                ReportSmokeFailure(
                    "Topology rebuild lost the surviving wall anchor before it was removed.");
                return;
            }
            _smokeSupportReleaseStarted = true;
            _smokeReleasedBodyId = releasedBody.Grid.Id;
            _smokeReleasedBodyStart = releasedBody.Center;
            _smokeReleasedBodyStartDeviation = releasedBody.MaximumShapeDeviation;
            _smokeBreakElapsed = 0f;
            _world.SetBlockId(_smokeSupportX, _smokeSupportY, _smokeSupportZ, 0);
            return;
        }

        SoftBody? fallingBody = _bodies.FirstOrDefault(body => body.Grid?.Id == _smokeReleasedBodyId);
        if (fallingBody is null)
        {
            ReportSmokeFailure("Soft fragment disappeared after its final support was removed.");
            return;
        }
        float supportDrop = _smokeReleasedBodyStart.Y - fallingBody.Center.Y;
        float horizontalReleaseDrift = new Vector2(
            fallingBody.Center.X - _smokeReleasedBodyStart.X,
            fallingBody.Center.Z - _smokeReleasedBodyStart.Z).Length();
        bool shapeRecovered = _smokeReleasedBodyStartDeviation >= 0.020f &&
                              fallingBody.MaximumShapeDeviation <=
                              _smokeReleasedBodyStartDeviation * 0.50f;
        if (fallingBody.PinnedParticleCount != 0 || supportDrop < 0.12f ||
            supportDrop > 12f || horizontalReleaseDrift > 0.6f ||
            fallingBody.AverageVelocity.Length() > 16f ||
            fallingBody.ConstraintRecovery < 0.55f || !shapeRecovered)
        {
            ReportSmokeFailure(
                $"Released support produced an anchored or explosive body " +
                $"(pinned={fallingBody.PinnedParticleCount}, drop={supportDrop:F3}, " +
                $"horizontal={horizontalReleaseDrift:F3}, speed={fallingBody.AverageVelocity.Length():F3}, " +
                $"recovery={fallingBody.ConstraintRecovery:F3}, " +
                $"shape={_smokeReleasedBodyStartDeviation:F3}->{fallingBody.MaximumShapeDeviation:F3}).");
            return;
        }
        _smokeReported = true;
        Environment.SetEnvironmentVariable("RTX_SANDBOX_SOFT_BODY_SMOKE_DONE", "PASS");
        Console.WriteLine(
            $"SOFT_BODY_SMOKE PASS bend={_smokeMaximumDeviation:F3} strain={_smokeMaximumStrain:F3} " +
            $"rigidError={_bodies.Select(body => body.MaximumRigidShapeError).DefaultIfEmpty().Max():F4} " +
            $"detachedMetalDrop={_smokeDetachedMetalStartY - metal.Position.Y:F3} " +
            $"supportDrop={supportDrop:F3} releaseDrift={horizontalReleaseDrift:F3} " +
            $"recovery={fallingBody.ConstraintRecovery:F3} " +
            $"shape={_smokeReleasedBodyStartDeviation:F3}->{fallingBody.MaximumShapeDeviation:F3} " +
            $"softFragments={_bodies.Count} metalGrid={metal.Id}");
    }

    private void ReportSmokeFailure(string message)
    {
        _smokeReported = true;
        string completeMessage = $"Soft-body smoke failed: {message}";
        Environment.SetEnvironmentVariable("RTX_SANDBOX_SOFT_BODY_SMOKE_DONE", $"FAIL:{message}");
        Log.LogError(completeMessage);
    }

    internal sealed class SoftBody
    {
        private const int MaximumParticles = 768;
        public const int MaximumSourceElements = 768;
        private readonly Particle[] _particles;
        private readonly bool[] _grounded;
        private readonly Spring[] _springs;
        private readonly int[][] _neighbors;
        private RigidGroup[] _rigidGroups;
        private readonly Dictionary<(int X, int Y, int Z, ushort Material), CellRestShape> _restCellShapes;
        private readonly float _totalMass;
        private readonly float _collisionRadius;
        private readonly float _minimumVisualHalfExtent;
        private readonly float _maximumVisualHalfExtent;
        private readonly Quaternion _sourceOrientation;
        private float _settleTimer;
        private float _constraintRecovery = 1f;
        private bool _sleeping;
        private bool _geometryDirty = true;

        public PhysicsGrid? Grid { get; set; }
        public int ParticleCount => _particles.Length;
        public int SourceElementCount { get; }
        public int PinnedParticleCount => _particles.Count(particle => particle.IsPinned);
        public int RigidParticleCount => _particles.Count(particle => !particle.IsSoft);
        public float ParticleMassRatio =>
            _particles.Max(particle => particle.Mass) / _particles.Min(particle => particle.Mass);
        public float MaximumParticleSpeed => _particles.Max(particle => particle.Velocity.Length());
        public bool IsSleeping => _sleeping;
        public bool GeometryDirty => _geometryDirty;
        public float ConstraintRecovery => _constraintRecovery;
        public Quaternion RenderOrientation
        {
            get
            {
                if (_rigidGroups.Length == 0) return Quaternion.Identity;
                RigidGroup primary = _rigidGroups
                    .OrderByDescending(group => group.Indices.Length)
                    .First();
                (_, Vector3 axisX, Vector3 axisY, Vector3 axisZ) = EstimateRigidGroupFrame(primary);
                var rotation = new Matrix4x4(
                    axisX.X, axisX.Y, axisX.Z, 0f,
                    axisY.X, axisY.Y, axisY.Z, 0f,
                    axisZ.X, axisZ.Y, axisZ.Z, 0f,
                    0f, 0f, 0f, 1f);
                return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
            }
        }
        public Vector3 Center
        {
            get
            {
                Vector3 sum = Vector3.Zero;
                for (int index = 0; index < _particles.Length; index++)
                    sum += _particles[index].Position * _particles[index].Mass;
                return sum / _totalMass;
            }
        }
        public Vector3 AverageVelocity
        {
            get
            {
                Vector3 sum = Vector3.Zero;
                for (int index = 0; index < _particles.Length; index++)
                    sum += _particles[index].Velocity * _particles[index].Mass;
                return sum / _totalMass;
            }
        }
        public float MaximumStrain
        {
            get
            {
                float maximum = 0f;
                foreach (Spring spring in _springs)
                {
                    float distance = Vector3.Distance(_particles[spring.A].Position, _particles[spring.B].Position);
                    maximum = MathF.Max(maximum, MathF.Abs(distance / spring.RestLength - 1f));
                }
                return maximum;
            }
        }
        public float MaximumShapeDeviation
        {
            get
            {
                Vector3 center = Center;
                (Vector3 axisX, Vector3 axisY, Vector3 axisZ) = EstimateFrame();
                float maximum = 0f;
                for (int index = 0; index < _particles.Length; index++)
                {
                    Vector3 rest = _particles[index].RestOffset;
                    Vector3 rigidTarget = center + axisX * rest.X + axisY * rest.Y + axisZ * rest.Z;
                    maximum = MathF.Max(maximum, Vector3.Distance(_particles[index].Position, rigidTarget));
                }
                return maximum;
            }
        }
        public float MaximumRigidShapeError
        {
            get
            {
                float maximum = 0f;
                foreach (RigidGroup group in _rigidGroups)
                    maximum = MathF.Max(maximum, MeasureRigidGroupError(group));
                return maximum;
            }
        }
        public float MaximumPinnedDrift => _particles
            .Where(particle => particle.IsPinned)
            .Select(particle => Vector3.Distance(particle.Position, particle.SourcePosition))
            .DefaultIfEmpty()
            .Max();

        public SoftBody(
            PhysicsGrid source,
            IReadOnlyDictionary<ushort, SoftMaterial> materials,
            IVoxelWorld world,
            IBlockRegistry blocks)
        {
            PhysicsGridElement[] elements = source.Elements.ToArray();
            _sourceOrientation = source.Orientation;
            SourceElementCount = elements.Length;
            int subdivisions = elements.Length <= 2 ? 4 : elements.Length <= 12 ? 3 : elements.Length <= 96 ? 2 : 1;
            while (subdivisions > 1 && (long)elements.Length * subdivisions * subdivisions * subdivisions > MaximumParticles)
                subdivisions--;

            var particles = new List<Particle>(Math.Min(
                MaximumParticles,
                elements.Length * subdivisions * subdivisions * subdivisions));
            _restCellShapes = new Dictionary<(int X, int Y, int Z, ushort Material), CellRestShape>();
            float spacingSum = 0f;
            int spacingSamples = 0;
            foreach (PhysicsGridElement element in elements)
            {
                _restCellShapes[(element.CellX, element.CellY, element.CellZ, element.MaterialId)] =
                    new CellRestShape(
                        source.LocalToWorld(element.Center),
                        source.Orientation,
                        element.HalfExtents);
                Vector3 size = element.Maximum - element.Minimum;
                Vector3 step = size / subdivisions;
                PhysicalProfile profile = CreatePhysicalProfile(element.MaterialId, materials, blocks);
                float subcellMass = MathF.Max(0.001f, step.X * step.Y * step.Z * profile.Density);
                spacingSum += MathF.Max(0.02f, MathF.Min(step.X, MathF.Min(step.Y, step.Z)));
                spacingSamples++;
                for (int z = 0; z < subdivisions; z++)
                for (int y = 0; y < subdivisions; y++)
                for (int x = 0; x < subdivisions; x++)
                {
                    Vector3 local = element.Minimum + new Vector3(
                        (x + 0.5f) * step.X,
                        (y + 0.5f) * step.Y,
                        (z + 0.5f) * step.Z);
                    Vector3 worldPosition = source.LocalToWorld(local);
                    particles.Add(new Particle(
                        worldPosition,
                        local,
                        Vector3.Zero,
                        source.VelocityAtPoint(worldPosition),
                        element.CellX,
                        element.CellY,
                        element.CellZ,
                        element.MaterialId,
                        subcellMass,
                        element.HalfExtents,
                        profile));
                }
            }
            if (particles.Count == 0) throw new InvalidOperationException("Cannot create an empty soft-body structure.");

            _totalMass = particles.Sum(particle => particle.Mass);
            Vector3 center = Vector3.Zero;
            foreach (Particle particle in particles) center += particle.Position * particle.Mass;
            center /= _totalMass;
            for (int index = 0; index < particles.Count; index++)
            {
                Particle particle = particles[index];
                particle.RestOffset = particle.Position - center;
                particles[index] = particle;
            }
            _particles = particles.ToArray();
            _rigidGroups = BuildRigidGroups(_particles);
            _grounded = new bool[_particles.Length];

            float spacing = spacingSamples == 0 ? 0.25f : spacingSum / spacingSamples;
            _collisionRadius = Math.Clamp(spacing * 0.48f, 0.035f, 0.16f);
            _minimumVisualHalfExtent = Math.Clamp(spacing * 0.43f, 0.032f, 0.15f);
            _maximumVisualHalfExtent = Math.Clamp(spacing * 0.82f, 0.055f, 0.28f);

            for (int index = 0; index < _particles.Length; index++)
            {
                Particle particle = _particles[index];
                if (particle.IsSoft)
                    TryPinToStaticSupport(ref particle, spacing * 0.58f + 0.025f, world, blocks);
                _particles[index] = particle;
            }

            var springs = new List<Spring>();
            var neighborLists = Enumerable.Range(0, _particles.Length).Select(_ => new List<int>(20)).ToArray();
            float maximumSpringLengthSquared = spacing * spacing * 3.20f;
            for (int a = 0; a < _particles.Length; a++)
            for (int b = a + 1; b < _particles.Length; b++)
            {
                Vector3 delta = _particles[b].RestOffset - _particles[a].RestOffset;
                float lengthSquared = delta.LengthSquared();
                if (lengthSquared > 0.000001f && lengthSquared <= maximumSpringLengthSquared)
                {
                    float stiffness = _particles[a].IsSoft || _particles[b].IsSoft
                        ? MathF.Min(_particles[a].Stiffness, _particles[b].Stiffness)
                        : 0.97f;
                    springs.Add(new Spring(a, b, delta.Length(), stiffness));
                    neighborLists[a].Add(b);
                    neighborLists[b].Add(a);
                }
            }
            _springs = springs.ToArray();
            _neighbors = neighborLists.Select(list => list.ToArray()).ToArray();
            if (_springs.Length == 0)
                throw new InvalidOperationException("Soft-body structure did not contain adjacent particles.");
        }

        public void Step(
            float deltaSeconds,
            int iterations,
            IVoxelWorld world,
            IBlockRegistry blocks,
            PlayerController? player)
        {
            bool releasedPinnedSupport = false;
            for (int index = 0; index < _particles.Length; index++)
            {
                Particle particle = _particles[index];
                if (!particle.IsPinned || IsSupportPresent(particle, world, blocks)) continue;
                particle.IsPinned = false;
                particle.PreviousPosition = particle.Position;
                particle.Velocity = Vector3.Zero;
                _particles[index] = particle;
                releasedPinnedSupport = true;
            }
            if (releasedPinnedSupport)
            {
                if (!HasPinnedSupport()) BeginFreeBodyRelease();
                Wake();
            }

            // Re-enable the original rest shape gradually after the final support
            // disappears. This avoids an impulsive catapult while still allowing gel,
            // rubber and foam to recover instead of permanently adopting the bent pose.
            _constraintRecovery = MathF.Min(1f, _constraintRecovery + deltaSeconds / 1.20f);

            if (_sleeping)
            {
                bool unpinnedShapeStillRecovering = !HasPinnedSupport() &&
                                                    (MaximumShapeDeviation > 0.055f ||
                                                     MaximumStrain > 0.075f);
                if (!unpinnedShapeStillRecovering &&
                    (HasSupport(world, blocks) || HasPinnedSupport()) &&
                    !WakeForPlayer(player)) return;
                Wake();
            }

            Vector3 oldCenter = Center;
            Array.Clear(_grounded);
            for (int index = 0; index < _particles.Length; index++)
            {
                Particle particle = _particles[index];
                particle.PreviousPosition = particle.Position;
                if (particle.IsPinned)
                {
                    particle.Velocity = Vector3.Zero;
                    _particles[index] = particle;
                    continue;
                }
                particle.Velocity += new Vector3(0f, -18f, 0f) * deltaSeconds;
                particle.Position += particle.Velocity * deltaSeconds;
                _particles[index] = particle;
            }

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                foreach (Spring spring in _springs)
                {
                    Particle a = _particles[spring.A];
                    Particle b = _particles[spring.B];
                    Vector3 delta = b.Position - a.Position;
                    float length = delta.Length();
                    if (length < 0.00001f) continue;
                    float weight = a.InverseMass + b.InverseMass;
                    if (weight <= 0f) continue;
                    Vector3 correction = delta / length *
                                         ((length - spring.RestLength) * spring.Stiffness * _constraintRecovery);
                    a.Position += correction * (a.InverseMass / weight);
                    b.Position -= correction * (b.InverseMass / weight);
                    _particles[spring.A] = a;
                    _particles[spring.B] = b;
                }

                Vector3 center = Center;
                (Vector3 axisX, Vector3 axisY, Vector3 axisZ) = EstimateFrame();
                for (int index = 0; index < _particles.Length; index++)
                {
                    Particle particle = _particles[index];
                    if (particle.IsPinned) continue;
                    Vector3 rest = particle.RestOffset;
                    Vector3 target = center + axisX * rest.X + axisY * rest.Y + axisZ * rest.Z;
                    particle.Position = Vector3.Lerp(
                        particle.Position,
                        target,
                        particle.ShapeRecovery * _constraintRecovery / iterations);
                    _particles[index] = particle;
                }

                // Hard blocks may share the same moving assembly with gel, but they
                // are not themselves soft. Shape matching keeps every connected hard
                // branch and its individual cube spacing rigid while springs still
                // transfer weight and motion through the soft/hard attachment.
                foreach (RigidGroup group in _rigidGroups)
                    ApplyRigidGroupConstraint(group, 0.94f);

                for (int index = 0; index < _particles.Length; index++)
                {
                    Particle particle = _particles[index];
                    if (particle.IsPinned) continue;
                    _grounded[index] |= ResolveWorldCollision(
                        ref particle.Position, _collisionRadius, world, blocks) == CollisionContact.Ground;
                    ResolvePlayerCollision(ref particle.Position, _collisionRadius, player);
                    _particles[index] = particle;
                }
            }

            Vector3 centerVelocity = (Center - oldCenter) / deltaSeconds;
            float maximumSpeed = 0f;
            int groundedCount = 0;
            for (int index = 0; index < _particles.Length; index++)
            {
                Particle particle = _particles[index];
                if (particle.IsPinned)
                {
                    particle.PreviousPosition = particle.Position;
                    particle.Velocity = Vector3.Zero;
                    _particles[index] = particle;
                    continue;
                }
                Vector3 velocity = (particle.Position - particle.PreviousPosition) / deltaSeconds;
                particle.Velocity = Vector3.Lerp(centerVelocity, velocity, particle.InternalMotion) * particle.Damping;
                if (_grounded[index])
                {
                    groundedCount++;
                    if (particle.Velocity.Y < 0f) particle.Velocity.Y = 0f;
                    particle.Velocity.X *= particle.GroundFriction;
                    particle.Velocity.Z *= particle.GroundFriction;
                }
                if (particle.Velocity.LengthSquared() < 0.0001f) particle.Velocity = Vector3.Zero;
                maximumSpeed = MathF.Max(maximumSpeed, particle.Velocity.Length());
                _particles[index] = particle;
            }

            int requiredGroundContacts = Math.Clamp(_particles.Length / 12, 1, 24);
            bool hasPinnedSupport = HasPinnedSupport();
            bool restingOnWorld = groundedCount >= requiredGroundContacts || hasPinnedSupport;
            bool recoveredEnoughToSleep = hasPinnedSupport ||
                                          (MaximumShapeDeviation <= 0.055f && MaximumStrain <= 0.075f);
            float centerSleepSpeed = hasPinnedSupport ? 0.24f : 0.16f;
            if (restingOnWorld && recoveredEnoughToSleep &&
                maximumSpeed < 0.32f &&
                centerVelocity.LengthSquared() < centerSleepSpeed * centerSleepSpeed)
            {
                _settleTimer += deltaSeconds;
                if (_settleTimer >= 0.28f)
                {
                    for (int index = 0; index < _particles.Length; index++)
                    {
                        Particle particle = _particles[index];
                        particle.PreviousPosition = particle.Position;
                        particle.Velocity = Vector3.Zero;
                        _particles[index] = particle;
                    }
                    _sleeping = true;
                }
            }
            else
            {
                _settleTimer = 0f;
            }
            _geometryDirty = true;
        }

        public void MarkGeometryUpdated() => _geometryDirty = false;

        public PhysicsGridElement[] BuildCells()
        {
            Vector3 center = Center;
            Quaternion inverseOrientation = Quaternion.Inverse(RenderOrientation);
            var cells = new List<PhysicsGridElement>(SourceElementCount);
            foreach (IGrouping<(int CellX, int CellY, int CellZ, ushort MaterialId), int> group in
                     Enumerable.Range(0, _particles.Length).GroupBy(index => (
                         _particles[index].CellX,
                         _particles[index].CellY,
                         _particles[index].CellZ,
                         _particles[index].MaterialId)))
            {
                Particle representative = _particles[group.First()];
                if (!representative.IsSoft)
                {
                    Vector3 cellCenter = Vector3.Zero;
                    int count = 0;
                    foreach (int index in group)
                    {
                        Particle particle = _particles[index];
                        cellCenter += particle.Position;
                        count++;
                    }
                    cellCenter = Vector3.Transform(
                        cellCenter / Math.Max(1, count) - center,
                        inverseOrientation);
                    Vector3 originalHalfExtents = representative.SourceCellHalfExtents;
                    cells.Add(new PhysicsGridElement(
                        group.Key.CellX, group.Key.CellY, group.Key.CellZ,
                        cellCenter - originalHalfExtents,
                        cellCenter + originalHalfExtents,
                        group.Key.MaterialId));
                    continue;
                }

                Vector3 minimum = new(float.PositiveInfinity);
                Vector3 maximum = new(float.NegativeInfinity);
                foreach (int index in group)
                {
                    Particle particle = _particles[index];
                    Vector3 local = Vector3.Transform(
                        particle.Position - center,
                        inverseOrientation);
                    Vector3 halfExtents = CalculateVisualHalfExtents(index);
                    minimum = Vector3.Min(minimum, local - halfExtents);
                    maximum = Vector3.Max(maximum, local + halfExtents);
                }
                cells.Add(new PhysicsGridElement(
                    group.Key.CellX, group.Key.CellY, group.Key.CellZ,
                    minimum, maximum, group.Key.MaterialId));
            }
            return cells
                .OrderBy(cell => cell.CellX)
                .ThenBy(cell => cell.CellY)
                .ThenBy(cell => cell.CellZ)
                .ToArray();
        }

        public PhysicsGridSmoothSphere[] BuildSmoothSpheres()
        {
            Vector3 bodyCenter = Center;
            Quaternion inverseOrientation = Quaternion.Inverse(RenderOrientation);
            return _particles
                .Where(particle => particle.IsSoft)
                .GroupBy(particle => (particle.CellX, particle.CellY, particle.CellZ, particle.MaterialId))
                .Select(group =>
                {
                    Particle[] cellParticles = group.ToArray();
                    Vector3 center = Vector3.Zero;
                    int count = 0;
                    foreach (Particle particle in cellParticles)
                    {
                        center += particle.Position;
                        count++;
                    }
                    center /= Math.Max(1, count);
                    float radius = 0f;
                    foreach (Particle particle in cellParticles)
                        radius = MathF.Max(radius, Vector3.Distance(center, particle.Position));
                    radius = Math.Clamp(radius + _minimumVisualHalfExtent * 0.62f, 0.48f, 0.72f);
                    Vector3[]? corners = null;
                    if (_restCellShapes.TryGetValue(
                            (group.Key.CellX, group.Key.CellY, group.Key.CellZ, group.Key.MaterialId),
                            out CellRestShape restShape))
                    {
                        corners = new Vector3[8];
                        for (int z = 0; z <= 1; z++)
                        for (int y = 0; y <= 1; y++)
                        for (int x = 0; x <= 1; x++)
                        {
                            Vector3 restCorner = restShape.WorldCenter + Vector3.Transform(
                                new Vector3(
                                    x == 0 ? -restShape.HalfExtents.X : restShape.HalfExtents.X,
                                    y == 0 ? -restShape.HalfExtents.Y : restShape.HalfExtents.Y,
                                    z == 0 ? -restShape.HalfExtents.Z : restShape.HalfExtents.Z),
                                restShape.Orientation);
                            Particle nearest = cellParticles
                                .OrderBy(particle => Vector3.DistanceSquared(particle.SourcePosition, restCorner))
                                .First();
                            Vector3 worldCorner = restCorner + nearest.Position - nearest.SourcePosition;
                            corners[x + y * 2 + z * 4] = Vector3.Transform(
                                worldCorner - bodyCenter,
                                inverseOrientation);
                        }
                    }
                    return new PhysicsGridSmoothSphere(
                        Vector3.Transform(center - bodyCenter, inverseOrientation),
                        radius,
                        group.Key.MaterialId,
                        group.Key.CellX,
                        group.Key.CellY,
                        group.Key.CellZ)
                    {
                        ExplicitCorners = corners,
                    };
                })
                .OrderBy(sphere => sphere.CellX)
                .ThenBy(sphere => sphere.CellY)
                .ThenBy(sphere => sphere.CellZ)
                .ToArray();
        }

        public (PhysicsGridElement[] Cells, Vector3 Center, Quaternion Orientation, Vector3 Velocity)
            BuildLatticeForTopologyEdit()
        {
            PhysicsGridElement[] cells = _particles
                .GroupBy(particle => (particle.CellX, particle.CellY, particle.CellZ))
                .Select(group => new PhysicsGridElement(
                    group.Key.CellX,
                    group.Key.CellY,
                    group.Key.CellZ,
                    new Vector3(group.Key.CellX, group.Key.CellY, group.Key.CellZ),
                    new Vector3(group.Key.CellX + 1f, group.Key.CellY + 1f, group.Key.CellZ + 1f),
                    group.GroupBy(particle => particle.MaterialId)
                        .OrderByDescending(material => material.Count())
                        .First().Key))
                .OrderBy(cell => cell.CellX)
                .ThenBy(cell => cell.CellY)
                .ThenBy(cell => cell.CellZ)
                .ToArray();
            Quaternion orientation;
            if (HasPinnedSupport())
            {
                orientation = _sourceOrientation;
            }
            else
            {
                (Vector3 axisX, Vector3 axisY, Vector3 axisZ) = EstimateFrame();
                var rotation = new Matrix4x4(
                    axisX.X, axisX.Y, axisX.Z, 0f,
                    axisY.X, axisY.Y, axisY.Z, 0f,
                    axisZ.X, axisZ.Y, axisZ.Z, 0f,
                    0f, 0f, 0f, 1f);
                orientation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
            }

            Vector3 center = Center;
            Particle[] pinned = _particles.Where(particle => particle.IsPinned).ToArray();
            if (pinned.Length > 0)
            {
                Vector3 localCenter = Vector3.Zero;
                foreach (Particle particle in _particles)
                    localCenter += particle.SourceLocalPosition * particle.Mass;
                localCenter /= _totalMass;

                center = Vector3.Zero;
                float pinnedMass = 0f;
                foreach (Particle particle in pinned)
                {
                    center += (particle.Position - Vector3.Transform(
                        particle.SourceLocalPosition - localCenter,
                        orientation)) * particle.Mass;
                    pinnedMass += particle.Mass;
                }
                center /= MathF.Max(pinnedMass, 0.0001f);
            }
            return (cells, center, orientation, AverageVelocity);
        }

        private Vector3 CalculateVisualHalfExtents(int index)
        {
            Vector3 position = _particles[index].Position;
            Vector3 half = new(_minimumVisualHalfExtent);
            foreach (int neighbor in _neighbors[index])
            {
                Vector3 delta = Vector3.Abs(_particles[neighbor].Position - position) * 0.54f;
                half = Vector3.Max(half, delta);
            }
            return Vector3.Clamp(
                half,
                new Vector3(_minimumVisualHalfExtent),
                new Vector3(_maximumVisualHalfExtent));
        }

        private (Vector3 X, Vector3 Y, Vector3 Z) EstimateFrame()
        {
            Vector3 center = Center;
            Vector3 axisX = Vector3.Zero;
            Vector3 axisY = Vector3.Zero;
            Vector3 expectedZ = Vector3.Zero;
            for (int index = 0; index < _particles.Length; index++)
            {
                Vector3 current = _particles[index].Position - center;
                Vector3 rest = _particles[index].RestOffset;
                float mass = _particles[index].Mass;
                axisX += current * (rest.X * mass);
                axisY += current * (rest.Y * mass);
                expectedZ += current * (rest.Z * mass);
            }

            axisX = NormalizeOr(axisX, Vector3.UnitX);
            axisY -= axisX * Vector3.Dot(axisY, axisX);
            axisY = NormalizeOr(axisY, Vector3.UnitY);
            Vector3 axisZ = NormalizeOr(Vector3.Cross(axisX, axisY), Vector3.UnitZ);
            if (Vector3.Dot(axisZ, expectedZ) < 0f) axisZ = -axisZ;
            axisY = NormalizeOr(Vector3.Cross(axisZ, axisX), Vector3.UnitY);
            return (axisX, axisY, axisZ);
        }

        private static RigidGroup[] BuildRigidGroups(IReadOnlyList<Particle> particles)
        {
            Dictionary<(int X, int Y, int Z), int[]> hardCells = Enumerable.Range(0, particles.Count)
                .Where(index => !particles[index].IsSoft)
                .GroupBy(index => (particles[index].CellX, particles[index].CellY, particles[index].CellZ))
                .ToDictionary(group => group.Key, group => group.ToArray());
            var visited = new HashSet<(int X, int Y, int Z)>();
            var groups = new List<RigidGroup>();
            ReadOnlySpan<(int X, int Y, int Z)> neighbors =
            [
                (1, 0, 0), (-1, 0, 0),
                (0, 1, 0), (0, -1, 0),
                (0, 0, 1), (0, 0, -1),
            ];
            foreach ((int X, int Y, int Z) start in hardCells.Keys)
            {
                if (!visited.Add(start)) continue;
                var queue = new Queue<(int X, int Y, int Z)>();
                var indices = new List<int>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    (int X, int Y, int Z) cell = queue.Dequeue();
                    indices.AddRange(hardCells[cell]);
                    foreach ((int dx, int dy, int dz) in neighbors)
                    {
                        (int X, int Y, int Z) next = (cell.X + dx, cell.Y + dy, cell.Z + dz);
                        if (hardCells.ContainsKey(next) && visited.Add(next)) queue.Enqueue(next);
                    }
                }

                float totalMass = indices.Sum(index => particles[index].Mass);
                Vector3 restCenter = Vector3.Zero;
                foreach (int index in indices)
                    restCenter += particles[index].RestOffset * particles[index].Mass;
                restCenter /= MathF.Max(totalMass, 0.0001f);
                groups.Add(new RigidGroup(
                    indices.ToArray(),
                    indices.Select(index => particles[index].RestOffset - restCenter).ToArray()));
            }
            return groups.ToArray();
        }

        private void BeginFreeBodyRelease()
        {
            Vector3 velocity = AverageVelocity;
            if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z))
                velocity = Vector3.Zero;
            const float maximumReleaseSpeed = 6f;
            if (velocity.LengthSquared() > maximumReleaseSpeed * maximumReleaseSpeed)
                velocity = Vector3.Normalize(velocity) * maximumReleaseSpeed;

            for (int index = 0; index < _particles.Length; index++)
            {
                Particle particle = _particles[index];
                particle.PreviousPosition = particle.Position;
                particle.Velocity = velocity;
                _particles[index] = particle;
            }
            _constraintRecovery = 0f;
            _settleTimer = 0f;
            _geometryDirty = true;
        }

        private void ApplyRigidGroupConstraint(RigidGroup group, float strength)
        {
            (Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ) = EstimateRigidGroupFrame(group);
            for (int localIndex = 0; localIndex < group.Indices.Length; localIndex++)
            {
                int particleIndex = group.Indices[localIndex];
                Particle particle = _particles[particleIndex];
                if (particle.IsPinned) continue;
                Vector3 rest = group.LocalRestOffsets[localIndex];
                Vector3 target = center + axisX * rest.X + axisY * rest.Y + axisZ * rest.Z;
                particle.Position = Vector3.Lerp(particle.Position, target, strength);
                _particles[particleIndex] = particle;
            }
        }

        private float MeasureRigidGroupError(RigidGroup group)
        {
            (Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ) = EstimateRigidGroupFrame(group);
            float maximum = 0f;
            for (int localIndex = 0; localIndex < group.Indices.Length; localIndex++)
            {
                Vector3 rest = group.LocalRestOffsets[localIndex];
                Vector3 target = center + axisX * rest.X + axisY * rest.Y + axisZ * rest.Z;
                maximum = MathF.Max(maximum,
                    Vector3.Distance(_particles[group.Indices[localIndex]].Position, target));
            }
            return maximum;
        }

        private (Vector3 Center, Vector3 X, Vector3 Y, Vector3 Z) EstimateRigidGroupFrame(RigidGroup group)
        {
            float totalMass = 0f;
            Vector3 center = Vector3.Zero;
            foreach (int index in group.Indices)
            {
                float mass = _particles[index].Mass;
                center += _particles[index].Position * mass;
                totalMass += mass;
            }
            center /= MathF.Max(totalMass, 0.0001f);

            Vector3 axisX = Vector3.Zero;
            Vector3 axisY = Vector3.Zero;
            Vector3 expectedZ = Vector3.Zero;
            for (int localIndex = 0; localIndex < group.Indices.Length; localIndex++)
            {
                int index = group.Indices[localIndex];
                Vector3 current = _particles[index].Position - center;
                Vector3 rest = group.LocalRestOffsets[localIndex];
                float mass = _particles[index].Mass;
                axisX += current * (rest.X * mass);
                axisY += current * (rest.Y * mass);
                expectedZ += current * (rest.Z * mass);
            }
            axisX = NormalizeOr(axisX, Vector3.UnitX);
            axisY -= axisX * Vector3.Dot(axisY, axisX);
            axisY = NormalizeOr(axisY, Vector3.UnitY);
            Vector3 axisZ = NormalizeOr(Vector3.Cross(axisX, axisY), Vector3.UnitZ);
            if (Vector3.Dot(axisZ, expectedZ) < 0f) axisZ = -axisZ;
            axisY = NormalizeOr(Vector3.Cross(axisZ, axisX), Vector3.UnitY);
            return (center, axisX, axisY, axisZ);
        }

        private static bool TryPinToStaticSupport(
            ref Particle particle,
            float probeDistance,
            IVoxelWorld world,
            IBlockRegistry blocks)
        {
            ReadOnlySpan<Vector3> directions = stackalloc Vector3[]
            {
                Vector3.UnitX, -Vector3.UnitX,
                Vector3.UnitY, -Vector3.UnitY,
                Vector3.UnitZ, -Vector3.UnitZ,
            };
            foreach (Vector3 direction in directions)
            {
                Vector3 sample = particle.Position + direction * probeDistance;
                int x = (int)MathF.Floor(sample.X);
                int y = (int)MathF.Floor(sample.Y);
                int z = (int)MathF.Floor(sample.Z);
                ushort blockId = world.GetBlock(x, y, z);
                if (blockId == 0 || !blocks.Get(blockId).IsCollidable) continue;
                particle.IsPinned = true;
                particle.SupportX = x;
                particle.SupportY = y;
                particle.SupportZ = z;
                particle.Velocity = Vector3.Zero;
                return true;
            }
            return false;
        }

        private static bool IsSupportPresent(Particle particle, IVoxelWorld world, IBlockRegistry blocks)
        {
            ushort blockId = world.GetBlock(particle.SupportX, particle.SupportY, particle.SupportZ);
            return blockId != 0 && blocks.Get(blockId).IsCollidable;
        }

        private bool HasPinnedSupport() => _particles.Any(particle => particle.IsPinned);

        private bool HasSupport(IVoxelWorld world, IBlockRegistry blocks)
        {
            int supported = 0;
            int required = Math.Clamp(_particles.Length / 12, 1, 24);
            for (int index = 0; index < _particles.Length; index++)
            {
                Vector3 particle = _particles[index].Position;
                int blockX = (int)MathF.Floor(particle.X);
                int blockY = (int)MathF.Floor(particle.Y - _collisionRadius - 0.025f);
                int blockZ = (int)MathF.Floor(particle.Z);
                ushort blockId = world.GetBlock(blockX, blockY, blockZ);
                if (blockId != 0 && blocks.Get(blockId).IsCollidable && ++supported >= required) return true;
            }
            return false;
        }

        private bool WakeForPlayer(PlayerController? player)
        {
            bool collided = false;
            for (int index = 0; index < _particles.Length; index++)
            {
                Particle particle = _particles[index];
                if (!ResolvePlayerCollision(ref particle.Position, _collisionRadius, player)) continue;
                particle.PreviousPosition = particle.Position;
                _particles[index] = particle;
                collided = true;
            }
            return collided;
        }

        private void Wake()
        {
            _sleeping = false;
            _settleTimer = 0f;
            _geometryDirty = true;
        }

        private static PhysicalProfile CreatePhysicalProfile(
            ushort materialId,
            IReadOnlyDictionary<ushort, SoftMaterial> materials,
            IBlockRegistry blocks)
        {
            if (materials.TryGetValue(materialId, out SoftMaterial soft))
            {
                return new PhysicalProfile(
                    soft.Stiffness,
                    soft.Damping,
                    soft.ShapeRecovery,
                    soft.InternalMotion,
                    soft.GroundFriction,
                    soft.Density,
                    IsSoft: true);
            }

            BlockDefinition block = blocks.Get(materialId);
            float density = block.Density;
            return new PhysicalProfile(
                Stiffness: 0.97f,
                Damping: 0.995f,
                ShapeRecovery: 0.015f,
                InternalMotion: 0.18f,
                GroundFriction: 0.76f,
                Density: density,
                IsSoft: false);
        }

        private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
            value.LengthSquared() > 0.000001f ? Vector3.Normalize(value) : fallback;

        private static CollisionContact ResolveWorldCollision(
            ref Vector3 position,
            float radius,
            IVoxelWorld world,
            IBlockRegistry blocks)
        {
            CollisionContact contact = CollisionContact.None;
            int minimumX = (int)MathF.Floor(position.X - radius);
            int maximumX = (int)MathF.Floor(position.X + radius);
            int minimumY = (int)MathF.Floor(position.Y - radius);
            int maximumY = (int)MathF.Floor(position.Y + radius);
            int minimumZ = (int)MathF.Floor(position.Z - radius);
            int maximumZ = (int)MathF.Floor(position.Z + radius);
            for (int z = minimumZ; z <= maximumZ; z++)
            for (int y = minimumY; y <= maximumY; y++)
            for (int x = minimumX; x <= maximumX; x++)
            {
                ushort blockId = world.GetBlock(x, y, z);
                if (blockId == 0 || !blocks.Get(blockId).IsCollidable) continue;
                Vector3 minimum = new(x - radius, y - radius, z - radius);
                Vector3 maximum = new(x + 1f + radius, y + 1f + radius, z + 1f + radius);
                if (position.X <= minimum.X || position.X >= maximum.X ||
                    position.Y <= minimum.Y || position.Y >= maximum.Y ||
                    position.Z <= minimum.Z || position.Z >= maximum.Z) continue;
                int face = 0;
                float closest = position.X - minimum.X;
                void Consider(int candidate, float distance)
                {
                    if (distance >= closest) return;
                    closest = distance;
                    face = candidate;
                }
                Consider(1, maximum.X - position.X);
                Consider(2, position.Y - minimum.Y);
                Consider(3, maximum.Y - position.Y);
                Consider(4, position.Z - minimum.Z);
                Consider(5, maximum.Z - position.Z);
                switch (face)
                {
                    case 0: position.X = minimum.X; if (contact == CollisionContact.None) contact = CollisionContact.Other; break;
                    case 1: position.X = maximum.X; if (contact == CollisionContact.None) contact = CollisionContact.Other; break;
                    case 2: position.Y = minimum.Y; if (contact == CollisionContact.None) contact = CollisionContact.Other; break;
                    case 3: position.Y = maximum.Y; contact = CollisionContact.Ground; break;
                    case 4: position.Z = minimum.Z; if (contact == CollisionContact.None) contact = CollisionContact.Other; break;
                    default: position.Z = maximum.Z; if (contact == CollisionContact.None) contact = CollisionContact.Other; break;
                }
            }
            return contact;
        }

        private static bool ResolvePlayerCollision(
            ref Vector3 position,
            float radius,
            PlayerController? player)
        {
            if (player is null || player.FlightMode || player.NoClip) return false;
            Vector3 minimum = new(
                player.Position.X - PlayerController.Radius - radius,
                player.Position.Y - radius,
                player.Position.Z - PlayerController.Radius - radius);
            Vector3 maximum = new(
                player.Position.X + PlayerController.Radius + radius,
                player.Position.Y + PlayerController.Height + radius,
                player.Position.Z + PlayerController.Radius + radius);
            if (position.X <= minimum.X || position.X >= maximum.X ||
                position.Y <= minimum.Y || position.Y >= maximum.Y ||
                position.Z <= minimum.Z || position.Z >= maximum.Z) return false;

            float left = position.X - minimum.X;
            float right = maximum.X - position.X;
            float back = position.Z - minimum.Z;
            float front = maximum.Z - position.Z;
            float nearest = MathF.Min(MathF.Min(left, right), MathF.Min(back, front));
            if (nearest == left) position.X = minimum.X;
            else if (nearest == right) position.X = maximum.X;
            else if (nearest == back) position.Z = minimum.Z;
            else position.Z = maximum.Z;
            return true;
        }

        private enum CollisionContact
        {
            None,
            Other,
            Ground
        }
    }

    private static class SoftBodyRuntime
    {
        private static readonly Dictionary<ulong, (VoxelWorld World, SoftBody Body)> Bodies = new();
        private static int _coreEditDepth;
        private static Action? _coreEditCompleted;
        public static bool CoreEditInProgress => _coreEditDepth > 0;
        private static readonly FieldInfo ElementsField = AccessTools.Field(typeof(PhysicsGrid), "_elements");
        private static readonly FieldInfo TopologyField = AccessTools.Field(typeof(PhysicsGrid), "<TopologyVersion>k__BackingField");
        private static readonly FieldInfo PositionField = AccessTools.Field(typeof(PhysicsGrid), "<Position>k__BackingField");
        private static readonly FieldInfo OrientationField = AccessTools.Field(typeof(PhysicsGrid), "<Orientation>k__BackingField");
        private static readonly FieldInfo SleepingField = AccessTools.Field(typeof(PhysicsGrid), "<IsSleeping>k__BackingField");
        private static readonly FieldInfo SleepTimerField = AccessTools.Field(typeof(PhysicsGrid), "<SleepTimer>k__BackingField");
        private static readonly MethodInfo RebuildIndex = AccessTools.Method(typeof(PhysicsGrid), "RebuildCellIndex");
        private static readonly MethodInfo RecalculateMass = AccessTools.Method(typeof(PhysicsGrid), "RecalculateMass");

        private static SoftBodyPlugin? _plugin;

        public static void InstallPatches(
            Harmony harmony,
            BepInEx.Logging.ManualLogSource log,
            SoftBodyPlugin plugin)
        {
            _plugin = plugin;
            MethodInfo gameLoaded = AccessTools.Method(typeof(SandboxGame), "OnLoad")
                ?? throw new MissingMethodException(nameof(SandboxGame), "OnLoad");
            MethodInfo gameUpdated = AccessTools.Method(typeof(SandboxGame), "OnUpdateFrame")
                ?? throw new MissingMethodException(nameof(SandboxGame), "OnUpdateFrame");
            harmony.Patch(gameLoaded, postfix: new HarmonyMethod(typeof(SoftBodyRuntime), nameof(GameLoaded)));
            harmony.Patch(gameUpdated, postfix: new HarmonyMethod(typeof(SoftBodyRuntime), nameof(GameUpdated)));
            Patch(harmony, AccessTools.Method(typeof(BepuPhysicsBackend), "CreateDynamic"), nameof(SkipBepuBody));
            PatchEdit(harmony, AccessTools.Method(typeof(VoxelPhysicsSimulator), nameof(VoxelPhysicsSimulator.TryRemoveGridElement)), nameof(RemoveElement));
            PatchEdit(harmony, AccessTools.Method(typeof(VoxelPhysicsSimulator), nameof(VoxelPhysicsSimulator.TryRemoveGridCell)), nameof(RemoveCell));
            PatchEdit(harmony, AccessTools.Method(typeof(VoxelPhysicsSimulator), nameof(VoxelPhysicsSimulator.TryPlaceOnGrid)), nameof(PrepareLatticeEdit));
            Patch(harmony, AccessTools.Method(typeof(VoxelPhysicsSimulator), nameof(VoxelPhysicsSimulator.TryPlaceMicroOnGrid)), nameof(RejectGridEdit));
            log.LogInfo("Harmony patches active: direct game lifecycle, BEPU isolation, per-cell breaking and edit rejection.");
        }

        private static void GameLoaded(SandboxGame __instance) => _plugin?.AttachGame(__instance);

        private static void GameUpdated(SandboxGame __instance) => _plugin?.TickFromGame(__instance);

        private static void Patch(Harmony harmony, MethodInfo? original, string prefix)
        {
            if (original is null) throw new MissingMethodException(prefix);
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(SoftBodyRuntime), prefix));
        }

        private static void PatchEdit(Harmony harmony, MethodInfo? original, string prefix)
        {
            if (original is null) throw new MissingMethodException(prefix);
            harmony.Patch(
                original,
                prefix: new HarmonyMethod(typeof(SoftBodyRuntime), prefix),
                postfix: new HarmonyMethod(typeof(SoftBodyRuntime), nameof(FinishCoreEdit)));
        }

        public static void Reset(VoxelWorld world)
        {
            foreach (ulong id in Bodies.Where(pair => ReferenceEquals(pair.Value.World, world)).Select(pair => pair.Key).ToArray())
                Bodies.Remove(id);
        }

        public static void SetCoreEditCompleted(Action callback) =>
            _coreEditCompleted = callback ?? throw new ArgumentNullException(nameof(callback));

        public static void Add(VoxelWorld world, SoftBody body)
        {
            if (body.Grid is null) throw new InvalidOperationException("The soft body has no render grid.");
            Bodies.Add(body.Grid.Id, (world, body));
        }

        public static bool IsOwned(ulong id) => Bodies.ContainsKey(id);

        public static bool VerifySaveCapture(VoxelWorld world)
        {
            MethodInfo capture = AccessTools.Method(typeof(WorldSaveService), "CapturePhysicsGrids")
                ?? throw new MissingMethodException(nameof(WorldSaveService), "CapturePhysicsGrids");
            object? result = capture.Invoke(null, new object[] { world.PhysicsGrids.Grids });
            int captured = result is System.Collections.IEnumerable values
                ? values.Cast<object>().Count()
                : -1;
            return captured == world.PhysicsGrids.Count;
        }

        public static void UpdateGeometry(
            VoxelWorld world,
            PhysicsGrid grid,
            Vector3 worldOrigin,
            Quaternion orientation,
            IReadOnlyList<PhysicsGridElement> cells,
            IReadOnlyList<PhysicsGridSmoothSphere> smoothSpheres,
            Vector3 velocity,
            bool sleeping)
        {
            if (!Bodies.ContainsKey(grid.Id) || world.PhysicsGrids.Get(grid.Id) != grid) return;
            var elements = (List<PhysicsGridElement>)ElementsField.GetValue(grid)!;
            bool shapeChanged = elements.Count != cells.Count;
            if (!shapeChanged)
            {
                const float visibleChangeSquared = 0.005f * 0.005f;
                for (int index = 0; index < cells.Count; index++)
                {
                    PhysicsGridElement oldCell = elements[index];
                    PhysicsGridElement newCell = cells[index];
                    if (oldCell.MaterialId == newCell.MaterialId &&
                        Vector3.DistanceSquared(oldCell.Minimum, newCell.Minimum) <= visibleChangeSquared &&
                        Vector3.DistanceSquared(oldCell.Maximum, newCell.Maximum) <= visibleChangeSquared) continue;
                    shapeChanged = true;
                    break;
                }
            }
            if (shapeChanged)
            {
                elements.Clear();
                elements.AddRange(cells);
                RebuildIndex.Invoke(grid, null);
                RecalculateMass.Invoke(grid, null);
                TopologyField.SetValue(grid, grid.TopologyVersion + 1);
            }
            grid.SetSmoothSpheres(smoothSpheres);
            orientation = Quaternion.Normalize(orientation);
            PositionField.SetValue(
                grid,
                worldOrigin + Vector3.Transform(grid.CenterOfMassLocal, orientation));
            OrientationField.SetValue(grid, orientation);
            grid.LinearVelocity = velocity;
            grid.AngularVelocity = Vector3.Zero;
            SleepingField.SetValue(grid, sleeping);
            SleepTimerField.SetValue(grid, sleeping ? 1f : 0f);
            world.PhysicsGrids.NotifyChanged();
        }

        private static bool Remove(ulong id)
        {
            if (!Bodies.Remove(id, out (VoxelWorld World, SoftBody Body) entry)) return false;
            return entry.Body.Grid is not null && entry.World.PhysicsGrids.Remove(entry.Body.Grid);
        }

        private static bool SkipBepuBody(PhysicsGrid grid) => !IsOwned(grid.Id);

        private static bool RemoveElement(PhysicsGridRayHit hit)
        {
            if (!IsOwned(hit.GridId)) return true;
            // Hand the grid back to the core before the edit. The original method can
            // then remove only the selected element, split disconnected components and
            // let gravity take over. Changed notifications reconvert any surviving
            // component that still contains a soft material.
            ReleaseOwnership(hit.GridId, restoreLattice: false);
            _coreEditDepth++;
            return true;
        }

        private static bool RemoveCell(ulong gridId)
        {
            if (!IsOwned(gridId)) return true;
            ReleaseOwnership(gridId, restoreLattice: true);
            _coreEditDepth++;
            return true;
        }

        private static bool PrepareLatticeEdit(PhysicsGridRayHit hit)
        {
            if (!IsOwned(hit.GridId)) return true;
            ReleaseOwnership(hit.GridId, restoreLattice: true);
            _coreEditDepth++;
            return true;
        }

        private static void ReleaseOwnership(ulong gridId, bool restoreLattice)
        {
            if (!Bodies.Remove(gridId, out (VoxelWorld World, SoftBody Body) entry)) return;
            PhysicsGrid? grid = entry.Body.Grid;
            if (grid is not null && restoreLattice)
            {
                (PhysicsGridElement[] cells, Vector3 center, Quaternion orientation, Vector3 velocity) =
                    entry.Body.BuildLatticeForTopologyEdit();
                var elements = (List<PhysicsGridElement>)ElementsField.GetValue(grid)!;
                elements.Clear();
                elements.AddRange(cells);
                RebuildIndex.Invoke(grid, null);
                RecalculateMass.Invoke(grid, null);
                grid.SetSmoothSpheres(Array.Empty<PhysicsGridSmoothSphere>());
                PositionField.SetValue(grid, center);
                OrientationField.SetValue(grid, orientation);
                grid.LinearVelocity = velocity;
                grid.AngularVelocity = Vector3.Zero;
                SleepingField.SetValue(grid, false);
                SleepTimerField.SetValue(grid, 0f);
                TopologyField.SetValue(grid, grid.TopologyVersion + 1);
            }
            entry.Body.Grid = null;
        }

        private static void FinishCoreEdit()
        {
            if (_coreEditDepth <= 0) return;
            _coreEditDepth--;
            if (_coreEditDepth == 0) _coreEditCompleted?.Invoke();
        }

        private static bool RejectGridEdit(PhysicsGridRayHit hit, ref bool __result)
        {
            if (!IsOwned(hit.GridId)) return true;
            __result = false;
            return false;
        }

    }

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 PreviousPosition;
        public Vector3 Velocity;
        public readonly Vector3 SourcePosition;
        public readonly Vector3 SourceLocalPosition;
        public Vector3 RestOffset;
        public readonly int CellX;
        public readonly int CellY;
        public readonly int CellZ;
        public readonly ushort MaterialId;
        public readonly float Mass;
        public readonly Vector3 SourceCellHalfExtents;
        public float InverseMass => IsPinned ? 0f : 1f / Mass;
        public readonly float Stiffness;
        public readonly float Damping;
        public readonly float ShapeRecovery;
        public readonly float InternalMotion;
        public readonly float GroundFriction;
        public readonly bool IsSoft;
        public bool IsPinned;
        public int SupportX;
        public int SupportY;
        public int SupportZ;

        public Particle(
            Vector3 position,
            Vector3 sourceLocalPosition,
            Vector3 restOffset,
            Vector3 velocity,
            int cellX,
            int cellY,
            int cellZ,
            ushort materialId,
            float mass,
            Vector3 sourceCellHalfExtents,
            PhysicalProfile profile)
        {
            Position = position;
            PreviousPosition = position;
            Velocity = velocity;
            SourcePosition = position;
            SourceLocalPosition = sourceLocalPosition;
            RestOffset = restOffset;
            CellX = cellX;
            CellY = cellY;
            CellZ = cellZ;
            MaterialId = materialId;
            Mass = mass;
            SourceCellHalfExtents = sourceCellHalfExtents;
            Stiffness = profile.Stiffness;
            Damping = profile.Damping;
            ShapeRecovery = profile.ShapeRecovery;
            InternalMotion = profile.InternalMotion;
            GroundFriction = profile.GroundFriction;
            IsSoft = profile.IsSoft;
            IsPinned = false;
            SupportX = 0;
            SupportY = 0;
            SupportZ = 0;
        }
    }

    private readonly record struct Spring(int A, int B, float RestLength, float Stiffness);

    private readonly record struct RigidGroup(int[] Indices, Vector3[] LocalRestOffsets);

    private readonly record struct CellRestShape(
        Vector3 WorldCenter,
        Quaternion Orientation,
        Vector3 HalfExtents);

    private readonly record struct PhysicalProfile(
        float Stiffness,
        float Damping,
        float ShapeRecovery,
        float InternalMotion,
        float GroundFriction,
        float Density,
        bool IsSoft);

    internal readonly record struct SoftMaterial(
        string Key,
        string DisplayName,
        Vector3 Albedo,
        BlockSurfacePattern Pattern,
        float Stiffness,
        float Damping,
        float ShapeRecovery,
        float InternalMotion,
        float GroundFriction,
        float Density,
        bool Transparent,
        ushort MaterialId = 0);
}
