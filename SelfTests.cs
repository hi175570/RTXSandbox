using System.Numerics;
using System.Reflection;
using System.Text.Json;
using RTXSandbox.Configuration;
using RTXSandbox.Gameplay;
using RTXSandbox.Engine;
using RTXSandbox.Infrastructure;
using RTXSandbox.Modding;
using RTXSandbox.Persistence;
using RTXSandbox.Rendering;
using RTXSandbox.Rendering.Backends;
using RTXSandbox.UI;
using RTXSandbox.World;

namespace RTXSandbox;

public static class SelfTests
{
    private static int _testsRun;

    public static int RunCreativeInventory()
    {
        _testsRun = 0;
        var failures = new List<string>();
        Run("creative inventory categories and whole-row scroll", CreativeInventoryTests.Model, failures);
        Run("creative inventory shared UI clicks and scroll", CreativeInventoryTests.Interface, failures);
        Run("creative backpack stacks transfers and persistence", CreativeInventoryTests.Transfers, failures);
        Run("creative hotbar stores namespaced block keys", TestCreativeHotbar, failures);
        Run("analytic spheres are direct creative inventory items", TestAnalyticSphereCreativePlacement, failures);
        Run("visible UI always releases the mouse", TestUiMouseCapturePolicy, failures);
        Run("IME is associated only with active text fields", TestImeOwnershipPolicy, failures);
        Run("empty hotbar secondary does not destroy blocks", TestPrimaryDestroySemantics, failures);
        foreach (string failure in failures) Console.Error.WriteLine(failure);
        return failures.Count == 0 ? 0 : 1;
    }

    public static int Run()
    {
        _testsRun = 0;
        var failures = new List<string>();
        Run("voxel set/get", TestVoxelSetGet, failures);
        Run("infinite world generates deterministic distant negative chunks", TestInfiniteWorldGeneration, failures);
        Run("queued chunk generation commits asynchronously", TestQueuedChunkGeneration, failures);
        Run("chunk streaming cancels obsolete generation work", TestChunkGenerationCancellation, failures);
        Run("raycast hit and normal", TestRaycast, failures);
        Run("player floor collision", TestFloorCollision, failures);
        Run("player cannot place inside body", TestPlacementOverlap, failures);
        Run("placement requires 1.1 blocks of eye distance", TestMinimumPlacementDistance, failures);
        Run("world clock cycle and pause", TestWorldClock, failures);
        Run("settings save atomically and recover from backup", TestSettingsRecovery, failures);
        Run("input defaults and navigation localization are stable", TestInputAndLocalizationDefaults, failures);
        Run("water raycast and collision", TestWaterBehavior, failures);
        Run("finite water falls, spreads, and conserves volume", TestWaterFlow, failures);
        Run("solid placement displaces water without deleting it", TestWaterDisplacement, failures);
        Run("connected water columns equalize their surfaces", TestCommunicatingWaterColumns, failures);
        Run("player can swim upward", TestSwimming, failures);
        Run("water jump impulse ignores fluid thickness", TestWaterJumpIgnoresFluidThickness, failures);
        Run("player continuously climbs out at a shoreline", TestSwimmingShoreExit, failures);
        Run("player does not auto-climb walls from water", TestSwimmingDoesNotAutoClimb, failures);
        Run("physics grids push players continuously without teleporting", TestContinuousGridPush, failures);
        Run("smooth soft-body cells remain walkable without launching the player", TestSmoothGridWalkability, failures);
        Run("soft-body render skin is one smooth closed surface", TestSoftBodySurfaceSkin, failures);
        Run("analytic sphere uses rigid-body gravity and player pushing", TestAnalyticSpherePhysics, failures);
        Run("analytic spheres wait for and remain supported by floor colliders", TestAnalyticSphereFloorCollision, failures);
        Run("analytic sphere raycast selects nearest surface for ordinary destruction", TestAnalyticSphereRaycast, failures);
        Run("deformed soft-body faces remain editable", TestDeformedGridPlacement, failures);
        Run("soft sides accept repeated soft and solid attachments", TestSoftPlacementJoinsDeformableGrid, failures);
        Run("long deformed soft chain accepts a quiet perpendicular turn", TestLongSoftChainPerpendicularTurn, failures);
        Run("creative physics gun grabs throws and pins structures", TestCreativePhysicsGun, failures);
        Run("modded world save remaps block keys", TestWorldSaveRemapping, failures);
        Run("directory worlds save and recover independent chunks", TestDirectoryChunkPersistence, failures);
        Run("world management renames rebuilds and deletes safely", TestWorldManagement, failures);
        Run("empty directory worlds do not emit empty chunk files", TestEmptyDirectoryChunkPersistence, failures);
        Run("legacy directory region migrates into chunk generations", TestLegacyDirectoryMigration, failures);
        Run("forced block override is last-writer-wins", TestForcedBlockOverride, failures);
        Run("example content pack loads a playable block", TestExampleContentPack, failures);
        Run("manual mod workspace compiles and preserves loose text sources", TestModWorkspaceCompilation, failures);
        Run("inventory stacks and removes items", TestInventoryStacking, failures);
        Run("dropped items are picked up", TestDroppedItemPickup, failures);
        Run("survival applies fall and drowning damage", TestSurvivalDamage, failures);
        Run("player inventory and vitals persist", TestPlayerStatePersistence, failures);
        Run("generated world has unbreakable bedrock", TestBedrockProtection, failures);
        Run("data-driven recipes craft atomically", TestCrafting, failures);
        Run("tools consume durability and break", TestToolDurability, failures);
        Run("hunger, food and starvation work", TestHunger, failures);
        Run("first-night objectives can complete", TestProgression, failures);
        Run("region edits undo and redo atomically", TestEditorHistory, failures);
        Run("clipboard rotates and pastes", TestEditorClipboard, failures);
        Run("explosions protect bedrock", TestEditorExplosion, failures);
        Run("unsupported components become continuous physics grids", TestVoxelPhysics, failures);
        Run("static deformation replacement does not duplicate adjacent structures", TestStaticDeformationReplacement, failures);
        Run("supported soft materials activate as one pinned mixed structure", TestSupportedSoftStructureExtraction, failures);
        Run("core soft bodies bend while supported and release with support", TestCoreSoftBodySystem, failures);
        Run("saved soft bodies rebuild shared lattice topology and rigid endpoints", TestSoftBodySavedTopologyRecovery, failures);
        Run("retired mechanics are replaced by one inert animation controller", TestAnimationControllerPlaceholder, failures);
        Run("glass fracture uses bounded non-uniform rigid shards", TestEnergyGlassFracture, failures);
        Run("physics water disturbance conserves fluid volume", TestPhysicsWaterRipple, failures);
        Run("diagonal voxel contact does not transmit support", TestDiagonalFracture, failures);
        Run("crouching slows movement and prevents edge falls", TestCrouchMovement, failures);
        Run("flight follows camera yaw while vertical controls ignore pitch", TestFlightMovementAxes, failures);
        Run("transparent and gravity materials load from content", TestEditorMaterialMetadata, failures);
        Run("mod build tools participate in editor history", TestModBuildToolHistory, failures);
        Run("flat preset honors layers and options", TestFlatWorldPreset, failures);
        Run("natural preset contains no showcase structures", TestNaturalWorldPreset, failures);
        Run("natural v2 terrain is deterministic and geologically layered", TestNaturalV2Terrain, failures);
        Run("pure nature creates biome transitions", TestPureNaturePreset, failures);
        Run("void preset creates only a test platform", TestVoidWorldPreset, failures);
        Run("showcase preset builds RTX laboratories", TestShowcaseWorldPreset, failures);
        Run("external bridge preset stays blank and accepts exact cells", TestExternalBridgeWorldPreset, failures);
        Run("Manifold bridge preserves camera and block handedness", TestManifoldBridgeCoordinateConversion, failures);
        Run("categorized material library exceeds 150 blocks", TestMaterialLibrary, failures);
        Run("16-bit block ids persist above 255", TestHighBlockIdPersistence, failures);
        Run("legacy byte worlds migrate to 16-bit voxels", TestLegacyByteWorldMigration, failures);
        Run("procedural material atlas is deterministic and detailed", TestProceduralMaterialAtlas, failures);
        Run("creative palette groups glass and uses stable color order", TestCreativePaletteOrdering, failures);
        Run("creative inventory categories and whole-row scroll", CreativeInventoryTests.Model, failures);
        Run("creative inventory shared UI clicks and scroll", CreativeInventoryTests.Interface, failures);
        Run("creative backpack stacks transfers and persistence", CreativeInventoryTests.Transfers, failures);
        Run("creative hotbar stores namespaced block keys", TestCreativeHotbar, failures);
        Run("analytic spheres are direct creative inventory items", TestAnalyticSphereCreativePlacement, failures);
        Run("visible UI always releases the mouse", TestUiMouseCapturePolicy, failures);
        Run("IME is associated only with active text fields", TestImeOwnershipPolicy, failures);
        Run("mixed precision grids stay sparse and exact", TestMicroVoxelGridPromotion, failures);
        Run("micro voxels raycast at exact surfaces", TestMicroVoxelRaycast, failures);
        Run("micro voxel edits publish precise and bulk invalidation", TestMicroVoxelChangeEvents, failures);
        Run("player collision follows micro voxel geometry", TestMicroVoxelCollision, failures);
        Run("precision edits undo and persist with key remapping", TestMicroVoxelEditingAndPersistence, failures);
        Run("primary destroys while secondary placement stays explicit", TestPrimaryDestroySemantics, failures);
        Run("renderer catalog exposes only working backends", TestRenderBackendCatalog, failures);
        Run("renderer failures preserve a safe usable backend", TestRendererFailureFallback, failures);
        Run("DXR primary coverage uses every display pixel", TestDxrFullPrimaryCoverage, failures);
        Run("chunk coordinates and mesh invalidation cross boundaries", TestChunkCoordinates, failures);
        Run("voxel data is owned and released by chunks", TestChunkOwnedVoxelStorage, failures);
        Run("chunk storage preserves the legacy linear snapshot", TestChunkLinearSnapshot, failures);
        Run("chunk occupied bounds shrink without losing precision cells", TestChunkOccupiedBounds, failures);
        Run("normal destruction clears an entire precision cell", TestWholeCellMicroDestruction, failures);
        Run("physics grids persist with transforms and velocities", TestPhysicsGridPersistence, failures);
        Run("loaded and unsupported sleeping bodies cannot float", TestSleepingBodiesRevalidateSupport, failures);
        Run("grid ownership transfers only aligned face-neighbor cells", TestGridOwnershipTransfer, failures);
        Run("physics grids prune enclosed collision cells", TestPhysicsGridCollisionShell, failures);
        Run("physics engine uses bounded collision substeps", TestPhysicsGridSubsteps, failures);
        Run("physics engine accumulates an exact fixed timestep", TestPhysicsFixedTimestep, failures);
        Run("flying structures force-load terrain ahead of their bounds", TestPhysicsStructureChunkResidency, failures);
        Run("sleeping rigid grids reconcile only after topology events", TestEventDrivenDynamicReconcile, failures);
        Run("100k settled debris is virtualized without disappearing", TestSettledDebrisVirtualization, failures);
        Run("large moving debris uses bounded collision LOD without deletion", TestMovingDebrisCollisionLod, failures);
        Run("active debris settles without edge-balance reawakening", TestActiveDebrisSettles, failures);
        Run("physics backend survives world reload", TestPhysicsWorldReload, failures);
        Run("colliders rebuild only for shape changes and prioritize edits", TestUrgentPhysicsChunkRebuild, failures);
        Run("removing static support wakes a sleeping rigid body", TestSupportRemovalWakesRigidBody, failures);
        Run("physics disposal is idempotent", TestPhysicsDisposal, failures);
        Run("sleeping physics grids do not invalidate rendering", TestSleepingGridInvalidation, failures);
        Run("physics grid center of mass uses volume and density", TestPhysicsGridCenterOfMass, failures);
        Run("physics grid culling radius covers its complete geometry", TestPhysicsGridBoundingRadius, failures);
        Run("removing an offset support gives structure angular velocity", TestOffsetSupportTorque, failures);
        Run("dynamic grid selection follows translation and rotation", TestDynamicGridSelectionTransform, failures);
        Run("dynamic grid placement preview matches the committed cell", TestDynamicGridPlacementPreview, failures);
        Run("precision placement on physics grids preserves micro size", TestPhysicsGridMicroPlacement, failures);
        Run("floor contact preserves rigid-body rotation", TestFloorContactRotation, failures);
        Run("precarious voxel edge balance receives a tipping perturbation", TestPrecariousGridRest, failures);
        Run("resting grid does not enter a perpetual bounce cycle", TestGridSettlesWithoutBounce, failures);

        if (failures.Count == 0)
        {
            Console.WriteLine($"All {_testsRun} RTX Sandbox self-tests passed.");
            return 0;
        }

        Console.Error.WriteLine($"{failures.Count} self-test(s) failed:");
        foreach (string failure in failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }
        return 1;
    }

    private static void TestManifoldBridgeCoordinateConversion()
    {
        Quaternion unityYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);
        Vector3 unityForward = Vector3.Transform(Vector3.UnitZ, unityYaw);
        Vector3 convertedForward = ManifoldGardenCoordinates.CameraForward(unityYaw);
        Assert(Vector3.Distance(
                   convertedForward,
                   ManifoldGardenCoordinates.Position(unityForward)) < 0.0001f,
            "converted Manifold camera forward does not preserve Unity yaw");

        Quaternion unityRotation = Quaternion.Normalize(
            Quaternion.CreateFromYawPitchRoll(0.63f, -0.41f, 0.27f));
        Vector3 unityLocal = Vector3.Normalize(new Vector3(0.31f, 0.72f, -0.44f));
        Vector3 expected = ManifoldGardenCoordinates.Position(Vector3.Transform(unityLocal, unityRotation));
        Vector3 actual = Vector3.Transform(
            ManifoldGardenCoordinates.Position(unityLocal),
            ManifoldGardenCoordinates.Rotation(unityRotation));
        Assert(Vector3.Distance(expected, actual) < 0.0001f,
            "converted Manifold block rotation does not match the reflected Unity vector");

        Vector3 camera = ManifoldGardenCoordinates.Position(new Vector3(2f, 3f, 4f));
        Vector3 point = ManifoldGardenCoordinates.Position(new Vector3(3f, 3f, 9f));
        Vector3 right = Vector3.Normalize(Vector3.Cross(
            ManifoldGardenCoordinates.CameraForward(Quaternion.Identity),
            ManifoldGardenCoordinates.CameraUp(Quaternion.Identity)));
        Assert(Vector3.Dot(point - camera, right) > 0f,
            "converted Manifold screen-right direction was mirrored");
    }

    private static void TestVoxelSetGet()
    {
        var world = new VoxelWorld(generate: false);
        Assert(world.Set(2, 3, 4, VoxelMaterial.Metal), "first write should change the world");
        Assert(world.Get(2, 3, 4) == VoxelMaterial.Metal, "material mismatch");
        Assert(world.Set(-1, 3, -4, VoxelMaterial.Stone) &&
               world.Get(-1, 3, -4) == VoxelMaterial.Stone,
            "negative horizontal coordinates were rejected");
        Assert(!world.Set(0, VoxelWorld.MinimumY - 1, 0, VoxelMaterial.Stone) &&
               !world.Set(0, VoxelWorld.MaximumY, 0, VoxelMaterial.Stone),
            "vertical out-of-bounds write succeeded");
    }

    private static void TestAnalyticSpherePhysics()
    {
        string saveRoot = Path.Combine(Path.GetTempPath(), $"rtxsandbox-sphere-{Guid.NewGuid():N}");
        try
        {
            var blocks = new BlockRegistry();
            var world = new VoxelWorld(generate: false, registry: blocks);
            AnalyticSphere sphere = world.AnalyticSpheres.Create(
                new Vector3(0.8f, 10f, 0f), 0.6f, (ushort)VoxelMaterial.Metal);
            using var physics = new VoxelPhysicsSimulator(world, blocks);
            float startingX = sphere.Center.X;
            float startingY = sphere.Center.Y;
            physics.PushAnalyticSpheresFromPlayer(new Vector3(0f, 9f, 0f), new Vector3(4f, 0f, 0f));
            for (int step = 0; step < 20; step++) physics.Update(1f / 120f);
            Assert(sphere.Center.X > startingX + 0.03f,
                "player contact did not transfer horizontal motion to the sphere body");
            Assert(sphere.Center.Y < startingY - 0.02f,
                "analytic sphere was not advanced by BEPU gravity");
            Assert(MathF.Abs(sphere.Radius - 0.6f) < 0.0001f,
                "sphere radius changed during rigid-body integration");

            var saves = new WorldSaveService(blocks, worldName: "sphere", saveRoot: saveRoot);
            saves.Save(world, Vector3.Zero, 12f);
            var restoredWorld = new VoxelWorld(generate: false, registry: blocks);
            Assert(saves.TryLoad(restoredWorld, out _, out string? error),
                error ?? "analytic sphere world did not load");
            Assert(restoredWorld.AnalyticSpheres.Count == 1,
                "analytic sphere count was not restored");
            AnalyticSphere restored = restoredWorld.AnalyticSpheres.Spheres[0];
            Assert(Vector3.Distance(restored.Center, sphere.Center) < 0.001f &&
                   Vector3.Distance(restored.LinearVelocity, sphere.LinearVelocity) < 0.001f &&
                   restored.MaterialId == sphere.MaterialId,
                "analytic sphere transform, velocity, or material was not restored");
        }
        finally
        {
            if (Directory.Exists(saveRoot)) Directory.Delete(saveRoot, recursive: true);
        }
    }

    private static void TestAnalyticSphereFloorCollision()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(64, 0, 64, VoxelMaterial.Bedrock);
        AnalyticSphere sphere = world.AnalyticSpheres.Create(
            new Vector3(64.5f, 1.525f, 64.5f), 0.5f, (ushort)VoxelMaterial.Metal);
        using var backend = new BepuPhysicsBackend(world, Vector3.Zero);

        // Advance simulation time faster than the asynchronous collider can be built.
        // A newly placed sphere must not begin falling through its known solid support.
        for (int step = 0; step < 120; step++) backend.Step(1f / 120f);
        Assert(sphere.Center.Y >= 1.45f,
            "sphere advanced through a solid floor while its Chunk collider was still pending");

        WaitForColliderJobs(backend);
        for (int step = 0; step < 240; step++) backend.Step(1f / 120f);
        Assert(sphere.Center.Y >= 1.45f,
            "sphere floor collider was absent after the player-centered collider residency range");
    }

    private static void TestAnalyticSphereRaycast()
    {
        var spheres = new AnalyticSphereWorld();
        AnalyticSphere far = spheres.Create(new Vector3(0f, 0f, 6f), 1f, (ushort)VoxelMaterial.Metal);
        AnalyticSphere near = spheres.Create(new Vector3(0f, 0f, 3f), 0.5f, (ushort)VoxelMaterial.Stone);

        AnalyticSphereHit? hit = spheres.Raycast(Vector3.Zero, Vector3.UnitZ, 8f);
        Assert(hit.HasValue && hit.Value.SphereId == near.Id,
            "sphere raycast did not return the nearest analytic surface");
        Assert(MathF.Abs(hit!.Value.Distance - 2.5f) < 0.0001f &&
               Vector3.Distance(hit.Value.Normal, -Vector3.UnitZ) < 0.0001f,
            "sphere raycast returned an incorrect distance or normal");
        Assert(spheres.Remove(hit.Value.SphereId) && spheres.Get(near.Id) is null && spheres.Get(far.Id) is not null,
            "selected sphere could not be removed without a special tool mode");
    }

    private static void TestDeformedGridPlacement()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = world.PhysicsGrids.Create(new[]
        {
            new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Crystal),
        }, new Vector3(4f, 4f, 4f), id => blocks.Get(id).Density);
        Vector3[] corners =
        [
            new(0.00f, 0.00f, 0.00f), new(1.00f, 0.08f, 0.00f),
            new(0.00f, 1.00f, 0.00f), new(1.00f, 1.08f, 0.00f),
            new(0.00f, 0.00f, 1.00f), new(1.00f, 0.08f, 1.00f),
            new(0.00f, 1.00f, 1.00f), new(1.00f, 1.08f, 1.00f),
        ];
        grid.SetSmoothSpheres(new[]
        {
            new PhysicsGridSmoothSphere(new Vector3(0.5f, 0.54f, 0.5f), 0.5f,
                (ushort)VoxelMaterial.Crystal, 0, 0, 0) { ExplicitCorners = corners },
        });
        grid.IsDeformable = true;
        Vector3 origin = grid.LocalToWorld(new Vector3(-2f, 0.5f, 0.5f));
        PhysicsGridRayHit? hit = grid.Raycast(origin, grid.LocalDirectionToWorld(Vector3.UnitX), 6f);
        Assert(hit.HasValue && hit.Value.CellX == 0 && hit.Value.Normal.X < -0.9f,
            "deformed gel side did not produce an editable surface hit");
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        Assert(physics.TryPlaceOnGrid(hit!.Value, (ushort)VoxelMaterial.Metal) &&
               world.GetBlockId(3, 4, 4) == 0 &&
               grid.TryGetFullCellMaterial(-1, 0, 0, out ushort endpoint) &&
               endpoint == (ushort)VoxelMaterial.Metal,
            "placing a solid against deformable gel did not attach a rigid endpoint to the lattice");
        Assert(grid.TryRemoveCell(0, 0, 0) && grid.SmoothSpheres.All(surface =>
                surface.CellX != 0 || surface.CellY != 0 || surface.CellZ != 0),
            "removing a deformed source block left its authored render skin behind");
        Assert(SandboxGame.ShouldPreferPhysicsGridHit(3.012f, 3.0f) &&
               !SandboxGame.ShouldPreferPhysicsGridHit(3.08f, 3.0f),
            "selection did not prefer a visible deformed face without stealing a genuinely nearer voxel");
    }

    private static void TestSoftPlacementJoinsDeformableGrid()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        Assert(blocks.TryGet("rtxsandbox:gel", out BlockDefinition? gel), "gel material is missing");
        world.SetBlockId(3, 4, 4, (ushort)VoxelMaterial.Bedrock);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            [new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, gel!.Id)],
            new Vector3(4f, 4f, 4f), id => blocks.Get(id).Density);
        grid.IsDeformable = true;
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        using var soft = new SoftBodyPhysicsSystem(world, blocks, physics);
        for (int extension = 1; extension <= 3; extension++)
        {
            for (int frame = 0; frame < 8; frame++) soft.Update(1f / 120f);
            PhysicsGridSmoothSphere outer = grid.SmoothSpheres.Single(cell => cell.CellX == extension - 1);
            int[] faceCorners = [1, 3, 5, 7];
            Vector3 faceCenter = faceCorners
                .Select(index => grid.LocalToWorld(outer.ExplicitCorners![index]))
                .Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / faceCorners.Length;
            Vector3 origin = faceCenter + grid.LocalDirectionToWorld(Vector3.UnitX) * 3f;
            PhysicsGridRayHit? hit = grid.Raycast(origin, -grid.LocalDirectionToWorld(Vector3.UnitX), 5f);
            Assert(hit.HasValue && hit.Value.CellX == extension - 1,
                $"gel extension {extension} could not select its newest side face");
            Vector3 localNormal = Vector3.Transform(hit!.Value.Normal, Quaternion.Inverse(grid.Orientation));
            bool canPlace = physics.CanPlaceOnGrid(hit.Value, gel.Id);
            PhysicsGrid? managedGrid = world.PhysicsGrids.Get(hit.Value.GridId);
            Assert(canPlace && physics.TryPlaceOnGrid(hit.Value, gel.Id),
                $"gel side extension {extension} was rejected (distance {hit.Value.Distance:F3}, " +
                $"normal {localNormal}, topology {grid.TopologyVersion}, " +
                $"managed {managedGrid is not null}, deformable {managedGrid?.IsDeformable}, " +
                $"placeable {blocks.Get(gel.Id).IsPlaceable}, contains-next {managedGrid?.ContainsCell(extension, 0, 0)}, " +
                $"cells {string.Join(';', grid.Cells.OrderBy(cell => cell.X).Select(cell => $"{cell.X},{cell.Y},{cell.Z}"))})");
            Assert(grid.TryGetFullCellMaterial(extension, 0, 0, out ushort joinedMaterial) &&
                   joinedMaterial == gel.Id,
                $"gel side extension {extension} was not joined to the deformable lattice");
        }
        Assert(world.GetBlockId(5, 4, 4) == 0 && world.PhysicsGrids.Count == 1,
            "placed gel became a separate static voxel/body instead of extending the soft structure");

        for (int frame = 0; frame < 8; frame++) soft.Update(1f / 120f);
        PhysicsGridSmoothSphere lastGel = grid.SmoothSpheres.Single(cell => cell.CellX == 3);
        int[] lastFaceCorners = [1, 3, 5, 7];
        Vector3 lastFaceCenter = lastFaceCorners
            .Select(index => grid.LocalToWorld(lastGel.ExplicitCorners![index]))
            .Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / lastFaceCorners.Length;
        PhysicsGridRayHit? solidHit = grid.Raycast(
            lastFaceCenter + grid.LocalDirectionToWorld(Vector3.UnitX) * 3f,
            -grid.LocalDirectionToWorld(Vector3.UnitX),
            5f);
        Assert(solidHit.HasValue && physics.CanPlaceOnGrid(solidHit.Value, (ushort)VoxelMaterial.Metal) &&
               physics.TryPlaceOnGrid(solidHit.Value, (ushort)VoxelMaterial.Metal),
            "a solid block was rejected after repeatedly extending the soft side");
        Assert(grid.TryGetFullCellMaterial(4, 0, 0, out ushort rigidMaterial) &&
               rigidMaterial == (ushort)VoxelMaterial.Metal && world.GetBlockId(8, 4, 4) == 0,
            "the solid endpoint was detached into the world instead of joining the mixed structure");

        for (int frame = 0; frame < 16; frame++) soft.Update(1f / 120f);
        PhysicsGridSmoothSphere rigidEndpoint = grid.SmoothSpheres.Single(cell => cell.CellX == 4);
        int[] edgeA = [0, 0, 0, 1, 1, 2, 2, 3, 4, 4, 5, 6];
        int[] edgeB = [1, 2, 4, 3, 5, 3, 6, 7, 5, 6, 7, 7];
        Assert(edgeA.Zip(edgeB).All(edge =>
                MathF.Abs(Vector3.Distance(
                    rigidEndpoint.ExplicitCorners![edge.First],
                    rigidEndpoint.ExplicitCorners![edge.Second]) - 1f) < 0.025f),
            "the newly attached solid endpoint stretched with the soft lattice");

        // Reproduce the gameplay-only failure: the structure has already sagged, then
        // a player turns ninety degrees from its current tip. Building the final L in
        // one call is not equivalent; every placement rebuild is part of the regression.
        for (int frame = 0; frame < 20; frame++) soft.Update(1f / 120f);
        Dictionary<(int X, int Y, int Z, int Corner), Vector3> beforeTurn = grid.SmoothSpheres
            .Where(surface => surface.HasExplicitCorners)
            .SelectMany(surface => Enumerable.Range(0, 8).Select(corner =>
                new KeyValuePair<(int, int, int, int), Vector3>(
                    (surface.CellX, surface.CellY, surface.CellZ, corner),
                    grid.LocalToWorld(surface.ExplicitCorners![corner]))))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        PhysicsGridSmoothSphere turnSource = grid.SmoothSpheres.Single(cell =>
            cell.CellX == 3 && cell.CellY == 0 && cell.CellZ == 0);
        int[] positiveZFace = [4, 5, 6, 7];
        Vector3 turnFaceCenter = positiveZFace
            .Select(index => grid.LocalToWorld(turnSource.ExplicitCorners![index]))
            .Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / positiveZFace.Length;
        PhysicsGridRayHit? turnHit = grid.Raycast(
            turnFaceCenter + grid.LocalDirectionToWorld(Vector3.UnitZ) * 3f,
            -grid.LocalDirectionToWorld(Vector3.UnitZ), 5f);
        Assert(turnHit.HasValue && turnHit.Value.CellX == 3 &&
               physics.TryPlaceOnGrid(turnHit.Value, gel.Id),
            "a ninety-degree gel turn could not be placed through the gameplay path");
        soft.Update(1f / 120f);
        float largestPlacementJump = grid.SmoothSpheres
            .Where(surface => surface.HasExplicitCorners)
            .SelectMany(surface => Enumerable.Range(0, 8).Select(corner =>
            {
                var key = (surface.CellX, surface.CellY, surface.CellZ, corner);
                return beforeTurn.TryGetValue(key, out Vector3 before)
                    ? Vector3.Distance(before, grid.LocalToWorld(surface.ExplicitCorners![corner]))
                    : 0f;
            }))
            .DefaultIfEmpty(0f)
            .Max();
        Assert(largestPlacementJump < 0.12f,
            $"placing the turn relocated the already deformed structure by {largestPlacementJump:0.000} m");
        Assert(grid.TryGetFullCellMaterial(3, 0, 1, out ushort turnedMaterial) && turnedMaterial == gel.Id,
            "the ninety-degree gel cell did not join the existing lattice");

        for (int frame = 0; frame < 3; frame++) soft.Update(1f / 120f);
        PhysicsGridSmoothSphere turnCell = grid.SmoothSpheres.Single(cell =>
            cell.CellX == 3 && cell.CellY == 0 && cell.CellZ == 1);
        Vector3 payloadFaceCenter = positiveZFace
            .Select(index => grid.LocalToWorld(turnCell.ExplicitCorners![index]))
            .Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / positiveZFace.Length;
        PhysicsGridRayHit? payloadHit = grid.Raycast(
            payloadFaceCenter + grid.LocalDirectionToWorld(Vector3.UnitZ) * 3f,
            -grid.LocalDirectionToWorld(Vector3.UnitZ), 5f);
        Assert(payloadHit.HasValue && physics.TryPlaceOnGrid(payloadHit.Value, (ushort)VoxelMaterial.Metal),
            "a solid payload could not be attached after the ninety-degree turn");

        // Average grid velocity can remain near zero while opposite corners whip in
        // opposite directions, so observe the actual lattice nodes after the turn.
        // This specifically guards the gameplay report where any length of straight
        // building is stable but the first perpendicular cell makes the body flail.
        Dictionary<(int X, int Y, int Z), Vector3> previousFrame = SnapshotSoftNodes(grid);
        float maximumTurnNodeSpeed = 0f;
        float heightBeforeTurnMotion = previousFrame.Values.Max(point => point.Y);
        float highestDuringTurnMotion = heightBeforeTurnMotion;
        for (int frame = 0; frame < 45; frame++)
        {
            soft.Update(1f / 120f);
            Dictionary<(int X, int Y, int Z), Vector3> currentFrame = SnapshotSoftNodes(grid);
            foreach (((int X, int Y, int Z) key, Vector3 current) in currentFrame)
            {
                if (!previousFrame.TryGetValue(key, out Vector3 previous)) continue;
                maximumTurnNodeSpeed = MathF.Max(
                    maximumTurnNodeSpeed,
                    Vector3.Distance(previous, current) * 120f);
            }
            highestDuringTurnMotion = MathF.Max(highestDuringTurnMotion,
                currentFrame.Values.Max(point => point.Y));
            previousFrame = currentFrame;
        }
        Assert(maximumTurnNodeSpeed < 8f && highestDuringTurnMotion < heightBeforeTurnMotion + 0.50f,
            $"ninety-degree topology edit injected energy (node speed {maximumTurnNodeSpeed:0.00}, " +
            $"rise {highestDuringTurnMotion - heightBeforeTurnMotion:0.00})");

        // Cut it while the topology is still moving. Both components may inherit their
        // current momentum, but neither may manufacture energy or fly upward by itself.
        Assert(physics.TryRemoveGridCell(grid.Id, 2, 0, 0),
            "moving gel could not be cut through the normal destruction path");
        Assert(world.PhysicsGrids.Count == 2,
            $"cutting the bent gel produced {world.PhysicsGrids.Count} components instead of two");
        float initialHighest = world.PhysicsGrids.Grids
            .SelectMany(body => body.SmoothSpheres.SelectMany(surface => surface.ExplicitCorners ?? []
                ).Select(corner => body.LocalToWorld(corner).Y))
            .DefaultIfEmpty(0f).Max();
        float observedHighest = initialHighest;
        float maximumSpeed = 0f;
        for (int frame = 0; frame < 180; frame++)
        {
            soft.Update(1f / 120f);
            foreach (PhysicsGrid body in world.PhysicsGrids.Grids)
            {
                Assert(float.IsFinite(body.Position.X) && float.IsFinite(body.Position.Y) &&
                       float.IsFinite(body.Position.Z), "cut soft component produced a non-finite transform");
                maximumSpeed = MathF.Max(maximumSpeed, body.LinearVelocity.Length());
                observedHighest = MathF.Max(observedHighest, body.SmoothSpheres
                    .SelectMany(surface => surface.ExplicitCorners ?? [])
                    .Select(corner => body.LocalToWorld(corner).Y)
                    .DefaultIfEmpty(body.Position.Y).Max());
            }
        }
        Assert(maximumSpeed < 12f && observedHighest < initialHighest + 0.75f,
            $"cut components self-accelerated (speed {maximumSpeed:0.00}, rise {observedHighest - initialHighest:0.00})");

        static Dictionary<(int X, int Y, int Z), Vector3> SnapshotSoftNodes(PhysicsGrid body)
        {
            var result = new Dictionary<(int X, int Y, int Z), Vector3>();
            foreach (PhysicsGridSmoothSphere surface in body.SmoothSpheres.Where(surface => surface.HasExplicitCorners))
            for (int z = 0; z < 2; z++)
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                result[(surface.CellX + x, surface.CellY + y, surface.CellZ + z)] =
                    body.LocalToWorld(surface.GetCorner(x, y, z));
            return result;
        }
    }

    private static void TestLongSoftChainPerpendicularTurn()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        Assert(blocks.TryGet("rtxsandbox:gel", out BlockDefinition? gel), "gel material is missing");
        Assert(blocks.TryGet("rtxsandbox:iron_block", out BlockDefinition? iron), "iron material is missing");
        world.SetBlockId(3, 8, 4, (ushort)VoxelMaterial.Bedrock);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            [new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, gel!.Id)],
            new Vector3(4f, 8f, 4f), id => blocks.Get(id).Density);
        grid.IsDeformable = true;
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        using var soft = new SoftBodyPhysicsSystem(world, blocks, physics);

        // Match the captured live failure: four soft cells, then a three-cell straight
        // rigid endpoint. The first rigid cell placed sideways turns that cluster into
        // an asymmetric L, which the old axis-wise fit could not rotate correctly.
        for (int extension = 1; extension <= 6; extension++)
        {
            for (int frame = 0; frame < 12; frame++) soft.Update(1f / 120f);
            PhysicsGridSmoothSphere source = grid.SmoothSpheres.Single(cell =>
                cell.CellX == extension - 1 && cell.CellY == 0 && cell.CellZ == 0);
            Vector3 center = new[] { 1, 3, 5, 7 }
                .Select(index => grid.LocalToWorld(source.ExplicitCorners![index]))
                .Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / 4f;
            PhysicsGridRayHit? hit = grid.Raycast(
                center + grid.LocalDirectionToWorld(Vector3.UnitX) * 3f,
                -grid.LocalDirectionToWorld(Vector3.UnitX), 5f);
            ushort material = extension <= 3 ? gel.Id : iron!.Id;
            Assert(hit.HasValue && hit.Value.CellX == extension - 1 &&
                   physics.TryPlaceOnGrid(hit.Value, material),
                $"mixed soft/iron chain rejected straight extension {extension}");
        }

        // Let the long cantilever acquire a real curved baseline before turning it.
        for (int frame = 0; frame < 180; frame++) soft.Update(1f / 120f);
        PhysicsGridSmoothSphere tip = grid.SmoothSpheres.Single(cell =>
            cell.CellX == 6 && cell.CellY == 0 && cell.CellZ == 0);
        Vector3 turnCenter = new[] { 4, 5, 6, 7 }
            .Select(index => grid.LocalToWorld(tip.ExplicitCorners![index]))
            .Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / 4f;
        PhysicsGridRayHit? turnHit = grid.Raycast(
            turnCenter + grid.LocalDirectionToWorld(Vector3.UnitZ) * 3f,
            -grid.LocalDirectionToWorld(Vector3.UnitZ), 5f);
        Assert(turnHit.HasValue && turnHit.Value.CellX == 6 &&
               physics.TryPlaceOnGrid(turnHit.Value, iron!.Id),
            "mixed soft/iron chain rejected its first perpendicular rigid endpoint cell");

        Dictionary<(int X, int Y, int Z), Vector3> previous = SnapshotNodes(grid);
        float initialHighest = previous.Values.Max(point => point.Y);
        float maximumNodeSpeed = 0f;
        float highest = initialHighest;
        for (int frame = 0; frame < 240; frame++)
        {
            soft.Update(1f / 120f);
            Dictionary<(int X, int Y, int Z), Vector3> current = SnapshotNodes(grid);
            foreach (((int X, int Y, int Z) key, Vector3 point) in current)
            {
                if (previous.TryGetValue(key, out Vector3 before))
                    maximumNodeSpeed = MathF.Max(maximumNodeSpeed, Vector3.Distance(before, point) * 120f);
                highest = MathF.Max(highest, point.Y);
            }
            previous = current;
        }
        Assert(maximumNodeSpeed < 8f && highest < initialHighest + 0.50f,
            $"long chain perpendicular turn flailed (node speed {maximumNodeSpeed:0.00}, " +
            $"rise {highest - initialHighest:0.00})");

        static Dictionary<(int X, int Y, int Z), Vector3> SnapshotNodes(PhysicsGrid body)
        {
            var result = new Dictionary<(int X, int Y, int Z), Vector3>();
            foreach (PhysicsGridSmoothSphere surface in body.SmoothSpheres.Where(surface => surface.HasExplicitCorners))
            for (int z = 0; z < 2; z++)
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                result[(surface.CellX + x, surface.CellY + y, surface.CellZ + z)] =
                    body.LocalToWorld(surface.GetCorner(x, y, z));
            return result;
        }
    }

    private static void TestCreativePhysicsGun()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            [new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Metal)],
            new Vector3(0f, 2f, 4f), id => blocks.Get(id).Density);
        var hit = new PhysicsGridRayHit(
            grid.Id, 0, 0, 0, (ushort)VoxelMaterial.Metal,
            grid.LocalToWorld(new Vector3(0.5f, 0.5f, 0f)), -Vector3.UnitZ, 4f);
        using var gun = new EnergyProjectileSystem(world, blocks);
        Assert(gun.Mode == PhysicsProjectorMode.Manipulate,
            "physics gun did not start in explicit manipulation mode");
        gun.SetMode(PhysicsProjectorMode.Impulse);
        Assert(!gun.TryBeginGrab(hit, Vector3.Zero) && !gun.IsHolding,
            "impulse mode still intercepted a structure as a grab");
        gun.SetMode(PhysicsProjectorMode.Manipulate);
        Assert(gun.TryBeginGrab(hit, Vector3.Zero) && gun.IsHolding &&
               grid.IsPhysicsGunHeld && grid.IsKinematicController,
            "physics gun did not take kinematic ownership of the selected structure");
        Vector3 before = grid.Position;
        Vector3 raisedAim = Vector3.Normalize(new Vector3(0f, 1f, 1f));
        Assert(gun.UpdateHeld(1f / 30f, Vector3.Zero, raisedAim) && grid.Position != before,
            "held structure did not follow the physics gun target");
        Vector3 heldPoint = grid.LocalToWorld(new Vector3(0.5f, 0.5f, 0f));
        Vector3 expectedHeldPoint = raisedAim * gun.HoldDistance;
        Assert(Vector3.Distance(heldPoint, expectedHeldPoint) < 0.0001f,
            $"grabbed point lagged behind the crosshair height: actual={heldPoint}, expected={expectedHeldPoint}");
        Assert(gun.ThrowHeld(Vector3.UnitZ) && !gun.IsHolding && !grid.IsKinematicController &&
               grid.LinearVelocity.Z > 5f,
            "physics gun did not release the structure with a throw velocity");

        PhysicsGridRayHit movedHit = hit with { Position = grid.LocalToWorld(new Vector3(0.5f, 0.5f, 0f)) };
        Assert(gun.TryBeginGrab(movedHit, Vector3.Zero) && gun.FreezeHeld() &&
               grid.IsPhysicsGunFrozen && grid.IsKinematicController && grid.IsSleeping,
            "physics gun pin did not persist as a frozen kinematic structure");
        Assert(gun.ToggleFrozen(grid.Id) && !grid.IsPhysicsGunFrozen &&
               !grid.IsKinematicController && !grid.IsSleeping,
            "physics gun could not release a pinned structure");
    }

    private static void TestInfiniteWorldGeneration()
    {
        var settings = WorldGenerationSettings.Natural(8675309);
        var first = new VoxelWorld(generationSettings: settings);
        var second = new VoxelWorld(generationSettings: settings);
        var distant = new ChunkCoordinate(-41, 73);
        Assert(first.EnsureChunkGenerated(distant) && second.EnsureChunkGenerated(distant),
            "distant negative/positive chunk coordinates were not generated");
        VoxelChunkSnapshot firstChunk = first.SnapshotChunk(distant);
        VoxelChunkSnapshot secondChunk = second.SnapshotChunk(distant);
        Assert(firstChunk.Voxels.SequenceEqual(secondChunk.Voxels),
            "same seed generated different distant chunk contents");
        Assert(firstChunk.Voxels.Any(voxel => voxel.Y == VoxelWorld.MinimumY &&
                                              voxel.MaterialId == (ushort)VoxelMaterial.Bedrock),
            "distant chunk did not include the -64 bedrock boundary");
        Assert(first.IsInside(int.MinValue, 0, int.MaxValue) &&
               !first.IsInside(0, VoxelWorld.MaximumY, 0),
            "world bounds are not unbounded X/Z with the configured vertical range");
    }

    private static void TestQueuedChunkGeneration()
    {
        var world = new VoxelWorld(generationSettings: WorldGenerationSettings.Natural(424242));
        int loadedBefore = world.LoadedChunkCount;
        var target = new ChunkCoordinate(-17, 21);
        Vector3 targetPosition = new(target.MinimumX + 0.5f, 8f, target.MinimumZ + 0.5f);
        Assert(world.QueueChunksAround(targetPosition, radius: 0) == 1,
            "missing target chunk was not queued");
        Assert(world.LoadedChunkCount == loadedBefore && world.PendingChunkGenerationCount == 1,
            "queue call synchronously generated or committed the chunk");

        bool committed = false;
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (world.ProcessPendingChunkGeneration() == 1)
            {
                committed = true;
                break;
            }
            Thread.Yield();
        }
        Assert(committed && world.PendingChunkGenerationCount == 0,
            "worker result was not committed within the test timeout");
        Assert(world.SnapshotGeneratedChunks().Contains(target) && world.SnapshotChunk(target).Voxels.Count > 0,
            "committed worker result did not populate the requested chunk");
    }

    private static void TestChunkGenerationCancellation()
    {
        var settings = new WorldGenerationSettings
        {
            Preset = WorldPreset.Flat,
            Seed = 4141,
            FlatSurfaceHeight = 4,
            GenerateLakes = false,
            GenerateVegetation = false,
        };
        var world = new VoxelWorld(generate: false, generationSettings: settings);
        world.QueueChunksAround(Vector3.Zero, radius: 1);
        Assert(world.PendingChunkGenerationCount == 9,
            $"first generation window queued {world.PendingChunkGenerationCount} chunks instead of nine");

        var distantPosition = new Vector3(1600.5f, 8f, -1600.5f);
        world.QueueChunksAround(distantPosition, radius: 1);
        Assert(world.PendingChunkGenerationCount == 9,
            "moving the generation window retained obsolete chunks around the old position");

        ChunkCoordinate direct = ChunkCoordinate.FromVoxel(1600, -1601);
        Assert(world.EnsureChunkGenerated(direct), "direct chunk generation did not run");
        Assert(world.PendingChunkGenerationCount == 8,
            "a synchronously generated queued chunk remained falsely pending");
    }

    private static void TestModWorkspaceCompilation()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), $"RTXSandbox-ModWorkspace-{Guid.NewGuid():N}");
        string pluginRoot = Path.Combine(testRoot, "plugins");
        string backupRoot = Path.Combine(testRoot, "backups");
        string trashRoot = Path.Combine(testRoot, "trash");
        Directory.CreateDirectory(pluginRoot);
        string prerequisiteSource = Path.Combine(pluginRoot, "Prerequisite.txt");
        string looseSource = Path.Combine(pluginRoot, "MysteryText.txt");
        const string prerequisite = "public static class PrerequisiteMarker { public const int Value = 7; }";
        const string source = """
            using BepInEx;
            using BepInEx.NET.Common;

            [BepInPlugin("rtxsandbox.tests.mystery", "Mystery Text Mod", "1.0.0")]
            public sealed class MysteryTextPlugin : BasePlugin
            {
                public int PrerequisiteValue => PrerequisiteMarker.Value;
                public override void Load() { }
            }
            """;
        try
        {
            File.WriteAllText(prerequisiteSource, prerequisite);
            var workspace = new ModWorkspace(pluginRoot, AppContext.BaseDirectory, backupRoot, trashRoot);
            ModCompilationResult prerequisiteResult = workspace.Compile(prerequisiteSource, TimeSpan.FromMinutes(2));
            Assert(prerequisiteResult.Success, $"prerequisite compile failed: {prerequisiteResult.Log}");

            File.WriteAllText(looseSource, source);
            File.WriteAllBytes(Path.Combine(pluginRoot, "MysteryText.dll"), new byte[] { 1, 2, 3, 4 });

            ModCompilationResult result = workspace.Compile(looseSource, TimeSpan.FromMinutes(2));
            Assert(result.Success, $"manual mod compile failed: {result.Log}");
            Assert(result.ReferenceCount > prerequisiteResult.ReferenceCount,
                "recursive plugin prerequisite DLL was not included in all-in references");
            Assert(result.OrganizedLooseFiles, "loose source was not organized on explicit compile");
            string organizedSource = Path.Combine(pluginRoot, "MysteryText", "MysteryText.txt");
            string compiledAssembly = Path.Combine(pluginRoot, "MysteryText", "MysteryText.dll");
            Assert(File.Exists(organizedSource), "organized source was lost");
            Assert(File.ReadAllText(organizedSource) == source, "compile changed the source text");
            Assert(!File.Exists(looseSource), "loose source was copied instead of moved into its folder");
            Assert(File.Exists(compiledAssembly), "compiled DLL was not placed beside the source");
            Assert(AssemblyName.GetAssemblyName(compiledAssembly).Name == "MysteryText",
                "compiled output is not a readable MysteryText assembly");

            string edited = source.Replace("1.0.0", "1.0.1", StringComparison.Ordinal);
            string backupPath = workspace.SaveText(organizedSource, edited);
            Assert(File.Exists(backupPath), "editor save did not retain a rollback copy");
            Assert(workspace.RollbackText(organizedSource) == source, "editor rollback did not restore the prior save");

            string movedSource = workspace.MoveToFolder(organizedSource, "Moved");
            Assert(File.Exists(movedSource), "explicit move did not move the source");
            string trashedSource = workspace.MoveToTrash(movedSource);
            Assert(File.Exists(trashedSource) && !File.Exists(movedSource),
                "confirmed delete did not move the source into recoverable trash");
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void TestRaycast()
    {
        var world = new VoxelWorld(generate: false);
        world.Set(3, 2, 2, VoxelMaterial.Stone);
        VoxelHit? hit = VoxelRaycast.Cast(world, new Vector3(1.5f, 2.5f, 2.5f), Vector3.UnitX, 8f);
        Assert(hit.HasValue, "expected a hit");
        Assert(hit!.Value.X == 3 && hit.Value.Normal == -Vector3.UnitX, "wrong voxel or face normal");
        Assert(MathF.Abs(hit.Value.Distance - 1.5f) < 0.001f, "wrong hit distance");
    }

    private static void TestFloorCollision()
    {
        var world = new VoxelWorld(generate: false);
        for (int z = 0; z < 8; z++)
        for (int x = 0; x < 8; x++)
        {
            world.Set(x, 0, z, VoxelMaterial.Stone);
        }

        var player = new PlayerController(world, new Vector3(3.5f, 3f, 3.5f));
        for (int i = 0; i < 240; i++)
        {
            player.Update(1f / 120f, Vector2.Zero, -Vector3.UnitZ, false, false);
        }
        Assert(MathF.Abs(player.Position.Y - 1f) < 0.001f, $"feet ended at {player.Position.Y}");
        Assert(player.IsGrounded, "player should be grounded");
    }

    private static void TestPlacementOverlap()
    {
        var world = new VoxelWorld(generate: false);
        var player = new PlayerController(world, new Vector3(3.5f, 1f, 3.5f));
        Assert(player.WouldIntersectBlock(3, 1, 3), "body overlap was not detected");
        Assert(!player.WouldIntersectBlock(4, 1, 3), "adjacent voxel incorrectly overlaps body");
    }

    private static void TestMinimumPlacementDistance()
    {
        var blocks = new BlockRegistry();

        var regularWorld = new VoxelWorld(generate: false, registry: blocks);
        regularWorld.Set(3, 2, 2, VoxelMaterial.Stone);
        var buildTools = new BuildToolSystem(regularWorld, blocks, new BuildToolRegistry())
        {
            SelectedBlockId = (ushort)VoxelMaterial.Glass,
        };
        var nearRegular = new VoxelHit(3, 2, 2, -Vector3.UnitX, 1.099f, VoxelMaterial.Stone);
        Assert(!buildTools.QuickPlace(nearRegular) && regularWorld.Get(2, 2, 2) == VoxelMaterial.Air,
            "regular placement accepted a target closer than 1.1 blocks");
        Assert(buildTools.QuickPlace(nearRegular with { Distance = 1.1f }) &&
               regularWorld.Get(2, 2, 2) == VoxelMaterial.Glass,
            "regular placement rejected the exact 1.1-block boundary");

        var microWorld = new VoxelWorld(generate: false, registry: blocks);
        microWorld.Set(3, 2, 2, VoxelMaterial.Stone);
        var micro = new MicroBuildSystem(microWorld, blocks);
        var nearPrecision = new PrecisionHit(
            3, 2, 2, new Vector3(3f, 2.5f, 2.5f), -Vector3.UnitX, 1.099f,
            (ushort)VoxelMaterial.Stone, false);
        Assert(!micro.Place(nearPrecision, (ushort)VoxelMaterial.Glass),
            "precision placement accepted a target closer than 1.1 blocks");
        Assert(micro.Place(nearPrecision with { Distance = 1.1f }, (ushort)VoxelMaterial.Glass),
            "precision placement rejected the exact 1.1-block boundary");

        var gridWorld = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = gridWorld.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            new Vector3(8f));
        using var physics = new VoxelPhysicsSimulator(gridWorld, blocks);
        var nearGrid = new PhysicsGridRayHit(
            grid.Id, 0, 0, 0, (ushort)VoxelMaterial.Stone,
            grid.LocalToWorld(new Vector3(1f, 0.5f, 0.5f)), Vector3.UnitX, 1.099f);
        Assert(!physics.CanPlaceOnGrid(nearGrid, (ushort)VoxelMaterial.Glass),
            "Physics Grid placement accepted a target closer than 1.1 blocks");
        Assert(physics.CanPlaceOnGrid(nearGrid with { Distance = 1.1f }, (ushort)VoxelMaterial.Glass),
            "Physics Grid placement rejected the exact 1.1-block boundary");
    }

    private static void TestWorldClock()
    {
        var clock = new WorldClock(23.5f, 1f);
        clock.Update(2.5f); // One real minute is one full day: 2.5 seconds advances one hour.
        Assert(MathF.Abs(clock.TimeOfDayHours - 0.5f) < 0.001f, "clock did not wrap at midnight");
        clock.Paused = true;
        clock.Update(10f);
        Assert(MathF.Abs(clock.TimeOfDayHours - 0.5f) < 0.001f, "paused clock advanced");
        clock.AdjustHours(-1f);
        Assert(MathF.Abs(clock.TimeOfDayHours - 23.5f) < 0.001f, "manual rewind did not wrap");
    }

    private static void TestSettingsRecovery()
    {
        var presetSettings = new GameSettings();
        presetSettings.ApplyDeferredQualityPreset(DeferredQualityPreset.Performance);
        Assert(presetSettings.DeferredShadowResolution == 1024 &&
               presetSettings.DeferredShadowDistance == 72f &&
               presetSettings.DeferredReflectionQuality == 1,
            "Deferred Performance preset did not apply its bounded quality profile");
        presetSettings.ApplyDeferredQualityPreset(DeferredQualityPreset.Quality);
        Assert(presetSettings.DeferredShadowResolution == 4096 &&
               presetSettings.DeferredShadowDistance == 140f &&
               presetSettings.DeferredAmbientOcclusionQuality == 3 &&
               presetSettings.DeferredPostProcessingQuality == 3,
            "Deferred Quality preset did not apply its complete quality profile");
        presetSettings.ApplyForwardQualityPreset(ForwardQualityPreset.Potato);
        Assert(presetSettings.ForwardShadowResolution == 1024 &&
               presetSettings.ForwardShadowDistance == 64f &&
               !presetSettings.ForwardBloom && !presetSettings.ForwardTemporalAntiAliasing,
            "Forward Potato preset did not disable its expensive optional stages");
        presetSettings.ApplyForwardQualityPreset(ForwardQualityPreset.Ultra);
        Assert(presetSettings.ForwardShadowResolution == 4096 &&
               presetSettings.ForwardShadowDistance == 144f &&
               presetSettings.ForwardAmbientOcclusionQuality == 3 &&
               presetSettings.ForwardTemporalAntiAliasing && presetSettings.ForwardApproximateGi,
            "Forward Ultra preset did not apply its complete shader-pack profile");

        string directory = Path.Combine(Path.GetTempPath(), $"rtxsandbox-settings-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new GameSettings
            {
                Exposure = 1.4f,
                PathBounces = 5,
                PathSamplesPerFrame = 2,
                PathMaxAccumulatedSamples = 2048,
                PathRayDistance = 176f,
                PathDirectLightingStrength = 1.25f,
                PathEnvironmentIntensity = 0.8f,
                PathEmissionIntensity = 1.4f,
                PathReflectionStrength = 1.6f,
                PathRussianRouletteStart = 4,
                DeferredSpecularQuality = 1,
                DeferredShadowQuality = 3,
                DeferredShadowResolution = 4096,
                DeferredShadowBias = 0.00072f,
                DeferredShadowNormalBias = 0.041f,
                DeferredShadowSlopeBias = 0.0021f,
                DeferredShadowSoftness = 1.7f,
                DeferredShadowDistance = 132f,
                DeferredAmbientOcclusionQuality = 1,
                DeferredDebugView = DeferredDebugView.Normal,
                DeferredQualityPreset = DeferredQualityPreset.Custom,
                HybridLightingQuality = 1,
                HybridSpecularQuality = 2,
                HybridShadowQuality = 3,
                HybridShadowResolution = 4096,
                HybridReflectionQuality = 1,
                HybridAmbientOcclusionQuality = 2,
                HybridWaterQuality = 3,
                HybridTransparencyQuality = 1,
                HybridFogQuality = 0,
                HybridBloom = false,
                PbrReflectionQuality = 3,
                ForwardQualityPreset = ForwardQualityPreset.Custom,
                ForwardShadowResolution = 4096,
                ForwardShadowQuality = 3,
                ForwardAmbientOcclusionQuality = 1,
                ForwardVolumetricQuality = 3,
                ForwardDebugView = ForwardDebugView.ScreenSpaceReflection,
                ForwardTemporalAntiAliasing = false,
                ClassicLightingQuality = 2,
                ClassicSunlightStrength = 1.3f,
                ClassicAmbientStrength = 0.7f,
                ClassicColorSaturation = 1.15f,
                ClassicWaterQuality = 3,
                ClassicWaterWaveStrength = 1.2f,
                ClassicTransparencyQuality = 2,
                ClassicFogDensity = 0.35f,
                HardwareRayTracingReflectionBounces = 5,
                HardwareRayTracingRayDistance = 184f,
                HardwareRayTracingShadowStrength = 0.75f,
                HardwareRayTracingEmissionStrength = 1.35f,
                HardwareRayTracingReflectionStrength = 1.6f,
                HardwareRayTracingRenderStyle = HardwareRayTracingRenderStyle.Reference,
                InfiniteMirrorQuality = InfiniteMirrorQuality.Extreme,
                SelectionOutlineReflections = false,
                Language = UiLanguage.English,
                MoveForwardKey = "Up",
                JumpKey = "J",
                CrouchKey = "C",
                SprintKey = "LeftAlt",
                FpsLimit = 144,
            };
            settings.Save(path);
            Assert(File.Exists(path) && !File.Exists(path + ".tmp"),
                "first settings save was not finalized atomically");
            string settingsJson = File.ReadAllText(path);
            Assert(!settingsJson.Contains("physicsMode", StringComparison.OrdinalIgnoreCase) &&
                   !settingsJson.Contains("impactFracture", StringComparison.OrdinalIgnoreCase) &&
                   !settingsJson.Contains("gameMode", StringComparison.OrdinalIgnoreCase) &&
                   !settingsJson.Contains("difficulty", StringComparison.OrdinalIgnoreCase) &&
                   !settingsJson.Contains("enableItemDrops", StringComparison.OrdinalIgnoreCase) &&
                   !settingsJson.Contains("hardwareRayTracingDenoiser", StringComparison.OrdinalIgnoreCase),
                "removed physics or survival switches still leaked into the settings schema");

            settings.Exposure = 2.2f;
            settings.PathBounces = 9;
            settings.FpsLimit = 165;
            settings.Save();
            Assert(File.Exists(path + ".bak"), "replacing settings did not retain a backup");
            GameSettings latest = GameSettings.Load(path);
            Assert(!latest.RecoveredFromBackup && MathF.Abs(latest.Exposure - 2.2f) < 0.001f &&
                   latest.PathBounces == 9 && latest.FpsLimit == 165 &&
                   latest.PathSamplesPerFrame == 2 && latest.PathMaxAccumulatedSamples == 2048 &&
                   MathF.Abs(latest.PathRayDistance - 176f) < 0.001f &&
                   MathF.Abs(latest.PathDirectLightingStrength - 1.25f) < 0.001f &&
                   MathF.Abs(latest.PathEnvironmentIntensity - 0.8f) < 0.001f &&
                   MathF.Abs(latest.PathEmissionIntensity - 1.4f) < 0.001f &&
                   MathF.Abs(latest.PathReflectionStrength - 1.6f) < 0.001f &&
                   latest.PathRussianRouletteStart == 4 &&
                   latest.DeferredSpecularQuality == 1 && latest.PbrReflectionQuality == 3 &&
                   latest.DeferredShadowQuality == 3 && latest.DeferredAmbientOcclusionQuality == 1 &&
                   latest.DeferredShadowResolution == 4096 &&
                   MathF.Abs(latest.DeferredShadowBias - 0.00072f) < 0.000001f &&
                   MathF.Abs(latest.DeferredShadowNormalBias - 0.041f) < 0.000001f &&
                   MathF.Abs(latest.DeferredShadowSlopeBias - 0.0021f) < 0.000001f &&
                   MathF.Abs(latest.DeferredShadowSoftness - 1.7f) < 0.0001f &&
                   MathF.Abs(latest.DeferredShadowDistance - 132f) < 0.001f &&
                   latest.DeferredDebugView == DeferredDebugView.Normal &&
                   latest.DeferredQualityPreset == DeferredQualityPreset.Custom &&
                   latest.HybridLightingQuality == 1 && latest.HybridSpecularQuality == 2 &&
                   latest.HybridShadowQuality == 3 && latest.HybridShadowResolution == 4096 &&
                   latest.HybridReflectionQuality == 1 && latest.HybridAmbientOcclusionQuality == 2 &&
                   latest.HybridWaterQuality == 3 && latest.HybridTransparencyQuality == 1 &&
                   latest.HybridFogQuality == 0 && !latest.HybridBloom &&
                   latest.ForwardQualityPreset == ForwardQualityPreset.Custom &&
                   latest.ForwardShadowResolution == 4096 && latest.ForwardShadowQuality == 3 &&
                   latest.ForwardAmbientOcclusionQuality == 1 && latest.ForwardVolumetricQuality == 3 &&
                   latest.ForwardDebugView == ForwardDebugView.ScreenSpaceReflection &&
                   !latest.ForwardTemporalAntiAliasing &&
                   latest.ClassicLightingQuality == 2 &&
                   MathF.Abs(latest.ClassicSunlightStrength - 1.3f) < 0.001f &&
                   MathF.Abs(latest.ClassicAmbientStrength - 0.7f) < 0.001f &&
                   MathF.Abs(latest.ClassicColorSaturation - 1.15f) < 0.001f &&
                   latest.ClassicWaterQuality == 3 &&
                   MathF.Abs(latest.ClassicWaterWaveStrength - 1.2f) < 0.001f &&
                   latest.ClassicTransparencyQuality == 2 &&
                   MathF.Abs(latest.ClassicFogDensity - 0.35f) < 0.001f &&
                   latest.HardwareRayTracingReflectionBounces == 5 &&
                   MathF.Abs(latest.HardwareRayTracingRayDistance - 184f) < 0.001f &&
                   MathF.Abs(latest.HardwareRayTracingShadowStrength - 0.75f) < 0.001f &&
                   MathF.Abs(latest.HardwareRayTracingEmissionStrength - 1.35f) < 0.001f &&
                   MathF.Abs(latest.HardwareRayTracingReflectionStrength - 1.6f) < 0.001f &&
                   latest.HardwareRayTracingRenderStyle == HardwareRayTracingRenderStyle.Reference &&
                   latest.InfiniteMirrorQuality == InfiniteMirrorQuality.Extreme &&
                   !latest.SelectionOutlineReflections &&
                   latest.Language == UiLanguage.English && latest.MoveForwardKey == "Up" &&
                   latest.JumpKey == "J" && latest.CrouchKey == "C" && latest.SprintKey == "LeftAlt" &&
                   latest.Version == GameSettings.CurrentVersion,
                "settings did not round-trip through the current schema");

            File.WriteAllText(path, "{ definitely-not-json");
            GameSettings recovered = GameSettings.Load(path);
            Assert(recovered.RecoveredFromBackup && MathF.Abs(recovered.Exposure - 1.4f) < 0.001f &&
                   recovered.PathBounces == 5 && recovered.FpsLimit == 144,
                "corrupt settings did not recover the last valid backup");
            recovered.Save();
            GameSettings repaired = GameSettings.Load(path);
            Assert(!repaired.RecoveredFromBackup && MathF.Abs(repaired.Exposure - 1.4f) < 0.001f,
                "saving recovered settings did not repair the primary file");

            File.Delete(path);
            GameSettings missingPrimary = GameSettings.Load(path);
            Assert(missingPrimary.RecoveredFromBackup && MathF.Abs(missingPrimary.Exposure - 1.4f) < 0.001f,
                "a missing primary settings file did not recover the valid backup");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestWaterBehavior()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(2, 2, 2, VoxelMaterial.Water);
        world.Set(3, 2, 2, VoxelMaterial.Water);
        world.Set(4, 2, 2, VoxelMaterial.Stone);
        Assert(!world.IsSolidForCollision(2, 2, 2), "water should not be a solid collider");
        Assert(world.FindTopVoxelY(2, 2) == 2, "water surface was not included in top-voxel query");
        VoxelHit? hit = VoxelRaycast.Cast(world, new Vector3(2.5f, 2.5f, 2.5f), Vector3.UnitX, 8f);
        Assert(hit.HasValue && hit.Value.Material == VoxelMaterial.Stone, "ray starting in water did not reach solid terrain");
        VoxelHit? outsideHit = VoxelRaycast.Cast(world, new Vector3(1.5f, 2.5f, 2.5f), Vector3.UnitX, 8f);
        Assert(outsideHit.HasValue && outsideHit.Value.Material == VoxelMaterial.Stone && outsideHit.Value.X == 4,
            "selection ray targeted water instead of passing through it");

        BlockDefinition water = blocks.Get((ushort)VoxelMaterial.Water);
        Assert(!water.IsUnbreakable && water.IsReplaceable && water.IsTransparent &&
               water.Transmission > 0.9f && MathF.Abs(water.IndexOfRefraction - 1.333f) < 0.01f,
            "water optical or editing properties were not loaded");
        var tools = new BuildToolSystem(world, blocks, new BuildToolRegistry())
        {
            SelectedBlockId = (ushort)VoxelMaterial.Glass,
        };
        var waterHit = new VoxelHit(2, 2, 2, Vector3.UnitX, 1.5f, VoxelMaterial.Water);
        Assert(tools.QuickPlace(waterHit) && world.Get(2, 2, 2) == VoxelMaterial.Glass,
            "placing a block did not replace the targeted water cell");
        world.Set(2, 2, 2, VoxelMaterial.Water);
        Assert(tools.UsePrimary(waterHit) && world.Get(2, 2, 2) == VoxelMaterial.Air,
            "primary destroy could not remove water");

        world.Set(2, 2, 2, VoxelMaterial.Water);
        var precision = new MicroBuildSystem(world, blocks);
        Assert(precision.PaintCell(2, 2, 2, 16, 0, 0, 0, 0),
            "precision editing still treated water as unbreakable");
    }

    private static void TestInputAndLocalizationDefaults()
    {
        var settings = new GameSettings();
        Assert(settings.CrouchKey == "LeftShift" && settings.SprintKey == "LeftControl" &&
               settings.MoveForwardKey == "W" && settings.JumpKey == "Space",
            "new input defaults do not match Shift crouch and Ctrl+W sprint");
        Assert(UiText.Get(UiLanguage.TraditionalChinese, "controls") == "按鍵" &&
               UiText.Get(UiLanguage.SimplifiedChinese, "controls") == "按键" &&
               UiText.Get(UiLanguage.English, "controls") == "Controls",
            "navigation localization did not distinguish Traditional, Simplified and English");
    }

    private static void TestWaterFlow()
    {
        var world = new VoxelWorld(generate: false);
        using var flow = new WaterFlowSimulation(world);
        for (int x = 0; x <= 10; x++)
        for (int z = 0; z <= 10; z++)
            world.Set(x, 0, z, VoxelMaterial.Stone);
        world.Set(5, 3, 5, VoxelMaterial.Water);

        flow.Update(0.05f);
        Assert(world.Get(5, 2, 5) == VoxelMaterial.Water,
            "water did not fall into the empty cell below");
        Assert(world.Get(5, 3, 5) == VoxelMaterial.Air,
            "finite water duplicated its source while falling");
        flow.Update(0.05f);
        Assert(world.Get(5, 1, 5) == VoxelMaterial.Water,
            "falling water did not continue to the floor");
        for (int step = 0; step < 12; step++) flow.Update(0.05f);
        (int X, int Z) horizontal = world.Get(6, 1, 5) == VoxelMaterial.Water ? (6, 5) :
            world.Get(4, 1, 5) == VoxelMaterial.Water ? (4, 5) :
            world.Get(5, 1, 6) == VoxelMaterial.Water ? (5, 6) : (5, 4);
        Assert(world.Get(horizontal.X, 1, horizontal.Z) == VoxelMaterial.Water,
            "supported water did not spread horizontally");
        Assert(world.GetFluidAmount(horizontal.X, 1, horizontal.Z) < VoxelWorld.FluidCapacity,
            "outward water did not become a partial-volume layer");
        Assert(SumWater(world, 0, 10, 0, 5, 0, 10) == VoxelWorld.FluidCapacity,
            "water flow created or destroyed finite volume");
        ChunkMeshingSnapshot waterSnapshot = world.CreateChunkMeshingSnapshot(
            ChunkCoordinate.FromVoxel(horizontal.X, horizontal.Z));
        Assert(waterSnapshot.GetFluidLevel(horizontal.X, 1, horizontal.Z) ==
               world.GetFluidLevel(horizontal.X, 1, horizontal.Z),
            "fluid level was not copied into the immutable renderer snapshot");

        var pourWorld = new VoxelWorld(generate: false);
        using var pourFlow = new WaterFlowSimulation(pourWorld);
        for (int x = 1; x <= 9; x++)
        for (int z = 1; z <= 9; z++)
            pourWorld.Set(x, 0, z, VoxelMaterial.Stone);
        pourWorld.Set(5, 1, 5, VoxelMaterial.Water);
        Assert(pourWorld.PourWater(5, 1, 5),
            "an existing water cell rejected a second bucket");
        for (int step = 0; step < 24; step++) pourFlow.Update(0.05f);
        Assert(SumWater(pourWorld, 0, 10, 0, 4, 0, 10) == VoxelWorld.FluidCapacity * 2,
            "pouring into existing water did not preserve the added finite volume");
        Assert(new[] { (4, 1, 5), (6, 1, 5), (5, 1, 4), (5, 1, 6) }
                .Any(cell => pourWorld.GetFluidAmount(cell.Item1, cell.Item2, cell.Item3) > 0),
            "a second bucket did not flow out of the targeted water cell");
    }

    private static void TestWaterDisplacement()
    {
        var world = new VoxelWorld(generate: false);
        using var flow = new WaterFlowSimulation(world);
        for (int x = 1; x <= 9; x++)
        for (int z = 1; z <= 9; z++)
            world.Set(x, 0, z, VoxelMaterial.Stone);
        world.Set(5, 1, 5, VoxelMaterial.Water);
        Assert(world.Set(5, 1, 5, VoxelMaterial.Stone), "solid did not replace the water cell");
        flow.Update(0.05f);
        Assert(world.Get(5, 1, 5) == VoxelMaterial.Stone,
            "water displacement removed the newly placed solid");
        Assert(SumWater(world, 0, 10, 0, 5, 0, 10) == VoxelWorld.FluidCapacity,
            "placing a solid deleted or duplicated displaced water");
        Assert(new[] { (4, 1, 5), (6, 1, 5), (5, 1, 4), (5, 1, 6) }
                .Any(cell => world.GetFluidAmount(cell.Item1, cell.Item2, cell.Item3) is > 0 and < VoxelWorld.FluidCapacity),
            "displaced water did not form a thin spill around the block");

        var submergedWorld = new VoxelWorld(generate: false);
        using var submergedFlow = new WaterFlowSimulation(submergedWorld);
        for (int x = 0; x < 18; x++)
        for (int z = 0; z < 18; z++)
        {
            submergedWorld.Set(x, 0, z, VoxelMaterial.Stone);
            submergedWorld.Set(x, 1, z, VoxelMaterial.Water);
            submergedWorld.Set(x, 2, z, VoxelMaterial.Water);
        }
        submergedWorld.Set(9, 1, 9, VoxelMaterial.Stone);
        Assert(submergedWorld.Set(9, 1, 9, VoxelMaterial.Air),
            "submerged temporary block could not be excavated");
        submergedFlow.Update(0.05f);
        Assert(submergedWorld.GetFluidAmount(9, 1, 9) > 0,
            "a newly excavated underwater cell waited behind the bulk water queue and stayed air");
    }

    private static void TestCommunicatingWaterColumns()
    {
        var world = new VoxelWorld(generate: false);
        using var flow = new WaterFlowSimulation(world);
        for (int x = 2; x <= 6; x++)
        {
            world.Set(x, 0, 5, VoxelMaterial.Stone);
            world.Set(x, 1, 5, VoxelMaterial.Water);
        }
        // Sealed U-shaped vessel: the only connection between both open shafts is
        // the full-water pipe at y=1, so balancing must move water upward by pressure.
        for (int y = 1; y <= 4; y++)
        {
            world.Set(1, y, 5, VoxelMaterial.Stone);
            world.Set(7, y, 5, VoxelMaterial.Stone);
            for (int x = 2; x <= 6; x++)
            {
                world.Set(x, y, 4, VoxelMaterial.Stone);
                world.Set(x, y, 6, VoxelMaterial.Stone);
            }
        }
        for (int x = 3; x <= 5; x++)
        for (int y = 2; y <= 4; y++)
            world.Set(x, y, 5, VoxelMaterial.Stone);
        world.Set(2, 2, 5, VoxelMaterial.Water);
        world.Set(2, 3, 5, VoxelMaterial.Water);
        int initialVolume = SumWater(world, 0, 8, 0, 6, 3, 7);
        for (int step = 0; step < 80; step++) flow.Update(0.05f);

        Assert(world.GetFluidAmount(6, 2, 5) == VoxelWorld.FluidCapacity,
            "the low vessel did not rise through its connected bottom pipe");
        Assert(world.GetFluidAmount(2, 3, 5) == 0,
            "the high vessel did not drain while equalizing");
        Assert(SumWater(world, 0, 8, 0, 6, 3, 7) == initialVolume,
            "communicating-vessel balancing did not conserve water");
    }

    private static int SumWater(
        VoxelWorld world, int minimumX, int maximumX, int minimumY, int maximumY, int minimumZ, int maximumZ)
    {
        int amount = 0;
        for (int z = minimumZ; z <= maximumZ; z++)
        for (int y = minimumY; y <= maximumY; y++)
        for (int x = minimumX; x <= maximumX; x++)
            amount += world.GetFluidAmount(x, y, z);
        return amount;
    }

    private static void TestSwimming()
    {
        var world = new VoxelWorld(generate: false);
        for (int y = 1; y <= 4; y++)
        {
            world.Set(3, y, 3, VoxelMaterial.Water);
        }
        var player = new PlayerController(world, new Vector3(3.5f, 1.1f, 3.5f));
        float initialY = player.Position.Y;
        for (int i = 0; i < 30; i++)
        {
            player.Update(1f / 60f, Vector2.Zero, -Vector3.UnitZ, false, true);
        }
        Assert(player.IsInWater, "player did not detect water");
        Assert(player.Position.Y > initialY + 0.2f, "swim-up input did not raise the player");
    }

    private static void TestWaterJumpIgnoresFluidThickness()
    {
        static PlayerController CreatePlayer(byte fluidLevel)
        {
            var world = new VoxelWorld(generate: false);
            world.Set(3, 0, 3, VoxelMaterial.Bedrock);
            world.Set(3, 1, 3, VoxelMaterial.Water);
            world.SetFluidLevel(3, 1, 3, fluidLevel);
            return new PlayerController(world, new Vector3(3.5f, 1f, 3.5f));
        }

        PlayerController fullWater = CreatePlayer(0);
        PlayerController thinnestWater = CreatePlayer(VoxelWorld.MaximumFluidLevel);
        fullWater.Update(1f / 60f, Vector2.Zero, -Vector3.UnitZ, false, true);
        thinnestWater.Update(1f / 60f, Vector2.Zero, -Vector3.UnitZ, false, true);

        Assert(fullWater.IsInWater && thinnestWater.IsInWater,
            "the thinnest water layer did not enable the jump-out action");
        Assert(MathF.Abs(fullWater.VerticalVelocity - thinnestWater.VerticalVelocity) < 0.0001f,
            $"water thickness changed jump speed ({fullWater.VerticalVelocity:0.000} vs {thinnestWater.VerticalVelocity:0.000})");
        Assert(fullWater.VerticalVelocity >= 6.19f,
            $"water jump still used gradual acceleration ({fullWater.VerticalVelocity:0.000})");
    }

    private static void TestSwimmingShoreExit()
    {
        var world = new VoxelWorld(generate: false);
        for (int x = 2; x <= 4; x++)
        for (int z = 3; z <= 5; z++)
        {
            world.Set(x, 0, z, VoxelMaterial.Bedrock);
            world.Set(x, 1, z, VoxelMaterial.Water);
        }
        for (int x = 2; x <= 4; x++)
        {
            world.Set(x, 0, 2, VoxelMaterial.Bedrock);
            world.Set(x, 1, 2, VoxelMaterial.Stone);
        }
        var player = new PlayerController(world, new Vector3(3.5f, 1.05f, 3.5f));
        bool reachedShore = false;
        for (int frame = 0; frame < 180; frame++)
        {
            player.Update(1f / 60f, new Vector2(0f, 1f), -Vector3.UnitZ, false, true);
            if (player.Position.Z < 3.0f && player.Position.Y >= 1.95f)
            {
                reachedShore = true;
                break;
            }
        }
        Assert(reachedShore,
            $"player remained trapped at shoreline ({player.Position.X:0.00}, {player.Position.Y:0.00}, {player.Position.Z:0.00})");
    }

    private static void TestSwimmingDoesNotAutoClimb()
    {
        var world = new VoxelWorld(generate: false);
        for (int x = 2; x <= 4; x++)
        for (int z = 3; z <= 5; z++)
        {
            world.Set(x, 0, z, VoxelMaterial.Bedrock);
            world.Set(x, 1, z, VoxelMaterial.Water);
        }
        for (int x = 2; x <= 4; x++)
        for (int y = 0; y <= 3; y++)
            world.Set(x, y, 2, y == 0 ? VoxelMaterial.Bedrock : VoxelMaterial.Stone);

        var player = new PlayerController(world, new Vector3(3.5f, 1.05f, 3.5f));
        float initialY = player.Position.Y;
        for (int frame = 0; frame < 180; frame++)
            player.Update(1f / 60f, new Vector2(0f, 1f), -Vector3.UnitZ, false, false);

        Assert(player.Position.Z >= 3.25f,
            $"player crossed a high wall without jump input (Z={player.Position.Z:0.00})");
        Assert(player.Position.Y < initialY + 0.2f,
            $"player auto-climbed while pressing forward in water (Y={player.Position.Y:0.00})");
    }

    private static void TestContinuousGridPush()
    {
        var world = new VoxelWorld(generate: false);
        world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            Vector3.Zero);
        var player = new PlayerController(world, new Vector3(1.2f, 0f, 0.5f));
        Vector3 initial = player.Position;
        Assert(player.ResolveExternalPenetration(1f / 60f), "overlapping Grid did not push the player");
        float firstStep = Vector3.Distance(initial, player.Position);
        Assert(firstStep > 0f && firstStep <= 0.041f,
            $"Grid squeeze teleported the player by {firstStep:0.000} m in one frame");
        Assert(MathF.Abs(player.Position.Y - initial.Y) < 0.001f,
            $"Grid squeeze pushed the player upward instead of sideways (Y={player.Position.Y:0.000})");
        for (int frame = 0; frame < 30; frame++) player.ResolveExternalPenetration(1f / 60f);
        Assert(!world.PhysicsGrids.TryCalculatePlayerPenetration(
                new Vector3(player.Position.X - PlayerController.Radius, player.Position.Y, player.Position.Z - PlayerController.Radius),
                new Vector3(player.Position.X + PlayerController.Radius, player.Position.Y + PlayerController.Height, player.Position.Z + PlayerController.Radius),
                out _, out _),
            "bounded continuous pushes never released the player from the Grid");

        var fastWorld = new VoxelWorld(generate: false);
        PhysicsGrid fastGrid = fastWorld.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            Vector3.Zero);
        fastGrid.LinearVelocity = Vector3.UnitX * 120f;
        var fastPlayer = new PlayerController(fastWorld, new Vector3(1.2f, 0f, 0.5f));
        Vector3 fastInitial = fastPlayer.Position;
        Assert(fastPlayer.ResolveExternalPenetration(1f / 60f),
            "fast overlapping Grid did not trigger anti-stuck recovery");
        float fastStep = Vector3.Distance(fastInitial, fastPlayer.Position);
        Assert(MathF.Abs(fastStep - firstStep) < 0.0001f,
            $"anti-stuck speed inherited Grid velocity: stationary={firstStep:0.000}, fast={fastStep:0.000}");

        var overheadWorld = new VoxelWorld(generate: false);
        overheadWorld.PhysicsGrids.Create(
            [new PhysicsGridElement(0, 0, 0, Vector3.Zero, new Vector3(7f, 1f, 7f),
                (ushort)VoxelMaterial.Stone)],
            new Vector3(-3.5f, 1.25f, -3.5f));
        var coveredPlayer = new PlayerController(overheadWorld, Vector3.Zero);
        for (int frame = 0; frame < 30; frame++)
            coveredPlayer.ResolveExternalPenetration(1f / 60f);
        Assert(coveredPlayer.Position.Y < 0.05f,
            $"falling overhead structure lifted the player onto its roof (Y={coveredPlayer.Position.Y:0.00})");
        Assert(new Vector2(coveredPlayer.Position.X, coveredPlayer.Position.Z).Length() > 0.25f,
            "wide overhead structure did not push the player toward a horizontal escape");
    }

    private static void TestSmoothGridWalkability()
    {
        var world = new VoxelWorld(generate: false);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            Enumerable.Range(0, 5).Select(x => new PhysicsGridElement(
                x, 0, 0,
                new Vector3(x, 0f, 0f),
                new Vector3(x + 1f, 1f, 1f),
                (ushort)VoxelMaterial.Stone)),
            Vector3.Zero);
        grid.SetSmoothSpheres(Enumerable.Range(0, 5).Select(x => new PhysicsGridSmoothSphere(
            new Vector3(x + 0.5f, 0.5f, 0.5f),
            0.62f,
            (ushort)VoxelMaterial.Stone,
            x, 0, 0)));

        var player = new PlayerController(world, new Vector3(0.75f, 2.2f, 0.5f));
        for (int frame = 0; frame < 90; frame++)
            player.Update(1f / 60f, Vector2.Zero, Vector3.UnitX, false, false);
        Assert(player.IsGrounded && MathF.Abs(player.Position.Y - 1f) < 0.01f,
            $"player did not settle on the smooth Grid proxy (Y={player.Position.Y:0.000})");

        float highest = player.Position.Y;
        for (int frame = 0; frame < 24; frame++)
        {
            player.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitX, false, false);
            highest = MathF.Max(highest, player.Position.Y);
        }
        Assert(player.Position.X > 2.2f && player.Position.Y >= 0.99f && highest <= 1.01f,
            $"walking on the smooth Grid launched or dropped the player " +
            $"(X={player.Position.X:0.00}, Y={player.Position.Y:0.000}, maxY={highest:0.000})");

        var rampWorld = new VoxelWorld(generate: false);
        PhysicsGrid ramp = rampWorld.PhysicsGrids.Create(
            Enumerable.Range(0, 8).Select(x => new PhysicsGridElement(
                x, 0, 0,
                new Vector3(x, x * 0.16f, 0f),
                new Vector3(x + 1f, 1f + (x + 1) * 0.16f, 1f),
                (ushort)VoxelMaterial.Stone)),
            Vector3.Zero);
        ramp.SetSmoothSpheres(Enumerable.Range(0, 8).Select(x =>
        {
            float leftTop = 1f + x * 0.16f;
            float rightTop = leftTop + 0.16f;
            Vector3[] corners =
            [
                new(x, leftTop - 1f, 0f), new(x + 1f, rightTop - 1f, 0f),
                new(x, leftTop, 0f), new(x + 1f, rightTop, 0f),
                new(x, leftTop - 1f, 1f), new(x + 1f, rightTop - 1f, 1f),
                new(x, leftTop, 1f), new(x + 1f, rightTop, 1f),
            ];
            return new PhysicsGridSmoothSphere(
                corners.Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / 8f,
                0.6f, (ushort)VoxelMaterial.Stone, x, 0, 0)
            {
                ExplicitCorners = corners,
            };
        }));
        var rampPlayer = new PlayerController(rampWorld, new Vector3(0.55f, 1.15f, 0.5f));
        for (int frame = 0; frame < 120; frame++)
            rampPlayer.Update(1f / 60f, Vector2.Zero, Vector3.UnitX, false, false);
        for (int frame = 0; frame < 85; frame++)
            rampPlayer.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitX, false, false);
        Assert(rampPlayer.Position.X > 6.5f && rampPlayer.Position.Y > 1.9f &&
               MathF.Abs(rampPlayer.Position.Z - 0.5f) < 0.05f,
            $"player was blocked or pushed sideways walking uphill " +
            $"(X={rampPlayer.Position.X:0.00}, Y={rampPlayer.Position.Y:0.00}, Z={rampPlayer.Position.Z:0.00})");
        for (int frame = 0; frame < 85; frame++)
            rampPlayer.Update(1f / 60f, new Vector2(0f, -1f), Vector3.UnitX, false, false);
        Assert(rampPlayer.Position.X < 1.5f && MathF.Abs(rampPlayer.Position.Z - 0.5f) < 0.05f,
            $"player was blocked or pushed sideways walking downhill " +
            $"(X={rampPlayer.Position.X:0.00}, Y={rampPlayer.Position.Y:0.00}, Z={rampPlayer.Position.Z:0.00})");
    }

    private static void TestSoftBodySurfaceSkin()
    {
        PhysicsGridSmoothSphere[] straightCells = Enumerable.Range(0, 4)
            .Select(x => new PhysicsGridSmoothSphere(
                new Vector3(x + 0.5f, 0.5f, 0.5f),
                0.5f,
                (ushort)VoxelMaterial.Glass,
                x, 0, 0))
            .ToArray();
        SoftBodySurfaceVertex[] straightSurface = SoftBodySurfaceBuilder.Build(straightCells);
        Vector3 straightMinimum = new(
            straightSurface.Min(vertex => vertex.Position.X),
            straightSurface.Min(vertex => vertex.Position.Y),
            straightSurface.Min(vertex => vertex.Position.Z));
        Vector3 straightMaximum = new(
            straightSurface.Max(vertex => vertex.Position.X),
            straightSurface.Max(vertex => vertex.Position.Y),
            straightSurface.Max(vertex => vertex.Position.Z));
        Assert(Vector3.Distance(straightMinimum, Vector3.Zero) < 0.0001f &&
               Vector3.Distance(straightMaximum, new Vector3(4f, 1f, 1f)) < 0.0001f,
            $"soft-body skin rounded or inflated a straight voxel beam " +
            $"(bounds={straightMinimum}..{straightMaximum})");
        Assert(straightSurface.All(vertex =>
                MathF.Max(MathF.Abs(vertex.Normal.X),
                    MathF.Max(MathF.Abs(vertex.Normal.Y), MathF.Abs(vertex.Normal.Z))) > 0.999f),
            "soft-body skin smoothed across a voxel crease instead of retaining flat square faces");
        Assert(straightSurface.All(vertex => vertex.FaceIndex is >= 0 and <= 2),
            "deformed geometry passed cube face indices into the three-face material atlas");

        var anchoredCell = new PhysicsGridSmoothSphere(
            new Vector3(0.5f, 0.35f, 0.5f),
            0.5f,
            (ushort)VoxelMaterial.Glass,
            0, 0, 0)
        {
            ExplicitCorners =
            [
                new Vector3(0f, 0f, 0f), new Vector3(1f, -0.30f, 0f),
                new Vector3(0f, 1f, 0f), new Vector3(1f, 0.70f, 0f),
                new Vector3(0f, 0f, 1f), new Vector3(1f, -0.30f, 1f),
                new Vector3(0f, 1f, 1f), new Vector3(1f, 0.70f, 1f),
            ],
        };
        SoftBodySurfaceVertex[] anchoredSurface = SoftBodySurfaceBuilder.Build([anchoredCell]);
        SoftBodySurfaceVertex[] supportedFace = anchoredSurface
            .Where(vertex => vertex.Normal.X < -0.999f)
            .ToArray();
        Assert(supportedFace.Length > 0 && supportedFace.All(vertex =>
                MathF.Abs(vertex.Position.X) < 0.0001f &&
                vertex.Position.Y >= -0.0001f && vertex.Position.Y <= 1.0001f),
            "explicit soft-body corners allowed the wall-supported face to drift");
        Assert(anchoredSurface.Any(vertex => vertex.Position.X > 0.99f && vertex.Position.Y < -0.20f),
            "explicit soft-body corners did not preserve deformation away from the fixed face");

        PhysicsGridSmoothSphere[] mirrorDent = Enumerable.Range(0, 36)
            .Select(index =>
            {
                int x = index % 6;
                int y = index / 6;
                Vector3 origin = new(x, y, 0f);
                return new PhysicsGridSmoothSphere(origin + new Vector3(0.5f), 1f / 12f,
                    (ushort)VoxelMaterial.Mirror, x, y, 0)
                {
                    ExplicitCorners =
                    [
                        origin, origin + Vector3.UnitX,
                        origin + Vector3.UnitY, origin + Vector3.UnitX + Vector3.UnitY,
                        origin + Vector3.UnitZ, origin + Vector3.UnitX + Vector3.UnitZ,
                        origin + Vector3.UnitY + Vector3.UnitZ,
                        origin + Vector3.One,
                    ],
                };
            })
            .ToArray();
        SoftBodySurfaceVertex[] mirrorDentSurface = SoftBodySurfaceBuilder.Build(mirrorDent);
        Assert(mirrorDentSurface.Length is > 500 and < 5_000,
            $"explicit 6x6 mirror dent produced excessive reflective geometry " +
            $"({mirrorDentSurface.Length / 3:N0} triangles)");

        PhysicsGridSmoothSphere[] cells = Enumerable.Range(0, 4)
            .Select(x => new PhysicsGridSmoothSphere(
                new Vector3(x + 0.5f, 0.5f - x * x * 0.035f, 0.5f),
                0.5f,
                (ushort)VoxelMaterial.Glass,
                x, 0, 0))
            .ToArray();
        SoftBodySurfaceVertex[] surface = SoftBodySurfaceBuilder.Build(cells);
        Assert(surface.Length > 1_000 && surface.Length < 10_000,
            $"continuous four-cell skin produced an implausible vertex count ({surface.Length})");
        Assert(surface.All(vertex => float.IsFinite(vertex.Position.X) &&
                                     float.IsFinite(vertex.Position.Y) &&
                                     float.IsFinite(vertex.Position.Z) &&
                                     MathF.Abs(vertex.Normal.Length() - 1f) < 0.001f),
            "soft-body subdivision produced an invalid position or non-unit normal");
        Assert(surface.Any(vertex => vertex.FaceIndex is 1 or 2 &&
            MathF.Abs(vertex.Position.Y - vertex.Uv.Y) > 0.02f),
            "soft-body material coordinates still follow deformed world positions instead of stretching with the lattice");

        SoftBodyBeamSegment[] segments = SoftBodySurfaceBuilder.BuildBeamSegments(cells);
        Assert(segments.Length == 3 && segments.All(segment => segment.Start != segment.End) &&
               segments.All(segment => MathF.Abs(segment.HalfWidth - 0.5f) < 0.0001f),
            "compute fallback did not preserve the connected square beam shape");

        var edgeUses = new Dictionary<(string A, string B), int>();
        for (int triangle = 0; triangle < surface.Length; triangle += 3)
        {
            CountEdge(surface[triangle].Position, surface[triangle + 1].Position);
            CountEdge(surface[triangle + 1].Position, surface[triangle + 2].Position);
            CountEdge(surface[triangle + 2].Position, surface[triangle].Position);
        }
        Assert(edgeUses.Count > 0 && edgeUses.Values.All(count => count == 2),
            "soft-body render skin is not a closed manifold; gaps can reappear between source cells");

        void CountEdge(Vector3 first, Vector3 second)
        {
            string a = Key(first);
            string b = Key(second);
            (string A, string B) edge = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
            edgeUses[edge] = edgeUses.GetValueOrDefault(edge) + 1;
        }

        static string Key(Vector3 value) =>
            $"{MathF.Round(value.X, 5):0.00000},{MathF.Round(value.Y, 5):0.00000},{MathF.Round(value.Z, 5):0.00000}";
    }

    private static void TestWorldSaveRemapping()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"rtxsandbox-test-{Guid.NewGuid():N}.rtxw");
        try
        {
            var originalBlocks = new BlockRegistry();
            ushort originalNeonId = originalBlocks.Register(new BlockDefinition(
                "test:neon", "Test Neon", new Vector3(0f, 1f, 1f)));
            var originalWorld = new VoxelWorld(generate: false, registry: originalBlocks);
            originalWorld.SetGenerationMetadata(new WorldGenerationSettings
            {
                Preset = WorldPreset.Flat,
                Biome = BiomePreset.Plains,
                Seed = 2468,
                FlatSurfaceHeight = 9,
                SurfaceBlockKey = "rtxsandbox:grass",
                UndergroundBlockKey = "rtxsandbox:stone",
                GenerateLakes = false,
                GenerateVegetation = false,
            });
            originalWorld.SetBlockId(4, 5, 6, originalNeonId);
            new WorldSaveService(originalBlocks, savePath).Save(originalWorld, new Vector3(2f, 3f, 4f), 17.5f);

            var reorderedBlocks = new BlockRegistry();
            reorderedBlocks.Register(new BlockDefinition("test:other", "Other", Vector3.One));
            ushort remappedNeonId = reorderedBlocks.Register(new BlockDefinition(
                "test:neon", "Test Neon", new Vector3(0f, 1f, 1f)));
            var loadedWorld = new VoxelWorld(generate: false, registry: reorderedBlocks);
            var saves = new WorldSaveService(reorderedBlocks, savePath);
            Assert(saves.TryLoad(loadedWorld, out SavedWorldState state, out string? error), error ?? "save did not load");
            Assert(originalNeonId != remappedNeonId, "test did not change the runtime block id");
            Assert(loadedWorld.GetBlockId(4, 5, 6) == remappedNeonId, "saved block key was not remapped");
            Assert(Vector3.DistanceSquared(state.PlayerPosition, new Vector3(2f, 3f, 4f)) < 0.0001f, "player position was not restored");
            Assert(MathF.Abs(state.TimeOfDay - 17.5f) < 0.001f, "saved time was not restored");
            Assert(state.GenerationSettings.Preset == WorldPreset.Flat && state.GenerationSettings.Seed == 2468 &&
                   loadedWorld.GenerationSettings.FlatSurfaceHeight == 9,
                "world generation preset metadata was not restored");
        }
        finally
        {
            File.Delete(savePath);
            File.Delete(savePath + ".tmp");
        }
    }

    private static void TestDirectoryChunkPersistence()
    {
        string saveRoot = Path.Combine(Path.GetTempPath(), $"rtxsandbox-chunks-{Guid.NewGuid():N}");
        try
        {
            var originalBlocks = new BlockRegistry();
            ushort originalMaterial = originalBlocks.Register(new BlockDefinition(
                "test:chunk_material", "Chunk Material", new Vector3(0.2f, 0.7f, 1f)));
            var originalWorld = new VoxelWorld(generate: false, registry: originalBlocks);
            originalWorld.SetGenerationMetadata(new WorldGenerationSettings
            {
                Preset = WorldPreset.Flat,
                Biome = BiomePreset.Plains,
                Seed = 314159,
                FlatSurfaceHeight = 4,
                GenerateLakes = false,
                GenerateVegetation = false,
            });
            originalWorld.SetBlockId(15, 3, 15, originalMaterial);
            originalWorld.SetBlockId(16, 4, 15, originalMaterial);
            originalWorld.SetBlockId(14, 6, 14, (ushort)VoxelMaterial.Water);
            originalWorld.SetFluidLevel(14, 6, 14, 3);
            var micro = new MicroVoxelContainer(4);
            micro.PaintCell(4, 0, 0, 0, originalMaterial);
            originalWorld.SetMicroVoxels(32, 5, 15, micro);
            PhysicsGrid originalGrid = originalWorld.PhysicsGrids.Create(
                new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, originalMaterial) },
                new Vector3(8f, 10f, 8f));
            originalGrid.AngularVelocity = new Vector3(0.2f, 0.4f, 0.1f);

            var originalSaves = new WorldSaveService(
                originalBlocks, preferredSeed: 314159, worldName: "chunk-test", saveRoot: saveRoot);
            originalSaves.Save(originalWorld, new Vector3(2f, 3f, 4f), 17.5f);
            Assert(File.Exists(originalSaves.SavePath), "directory world manifest was not created");
            WorldSaveSummary[] discovered = originalSaves.DiscoverWorlds().ToArray();
            Assert(discovered.Length == 1 && discovered[0].StorageName == "chunk-test" &&
                   discovered[0].Seed == 314159 && discovered[0].Preset == WorldPreset.Flat,
                "world browser metadata did not discover the saved world");
            string[] firstRegionFiles = Directory.GetFiles(
                Path.Combine(originalSaves.SaveDirectory, "regions"), "*.region", SearchOption.AllDirectories);
            Assert(firstRegionFiles.Length == 1,
                $"three chunks in one 32x32 area produced {firstRegionFiles.Length} region files");

            var reorderedBlocks = new BlockRegistry();
            reorderedBlocks.Register(new BlockDefinition("test:before_chunk", "Before Chunk", Vector3.One));
            ushort remappedMaterial = reorderedBlocks.Register(new BlockDefinition(
                "test:chunk_material", "Chunk Material", new Vector3(0.2f, 0.7f, 1f)));
            var loadedWorld = new VoxelWorld(generate: false, registry: reorderedBlocks);
            var loadedSaves = new WorldSaveService(
                reorderedBlocks, preferredSeed: 1, worldName: "chunk-test", saveRoot: saveRoot);
            Assert(loadedSaves.TryLoad(loadedWorld, out SavedWorldState loadedState, out string? loadError),
                loadError ?? "directory world did not load");
            Assert(remappedMaterial != originalMaterial &&
                   loadedWorld.GetBlockId(15, 3, 15) == remappedMaterial &&
                   loadedWorld.GetBlockId(16, 4, 15) == remappedMaterial,
                "independent chunk material keys were not remapped");
            Assert(loadedWorld.Get(14, 6, 14) == VoxelMaterial.Water &&
                   loadedWorld.GetFluidLevel(14, 6, 14) == 3,
                "flowing-water height was not restored from region v2");
            Assert(loadedWorld.TryGetMicroVoxels(32, 5, 15, out MicroVoxelContainer? loadedMicro) &&
                   loadedMicro!.Boxes.All(box => box.MaterialId == remappedMaterial),
                "precision geometry was not restored from its chunk");
            Assert(loadedWorld.PhysicsGrids.Count == 1 &&
                   loadedWorld.PhysicsGrids.Grids[0].Elements[0].MaterialId == remappedMaterial,
                "world-level physics state was not remapped and restored");
            Assert(Vector3.DistanceSquared(loadedState.PlayerPosition, new Vector3(2f, 3f, 4f)) < 0.0001f &&
                   MathF.Abs(loadedState.TimeOfDay - 17.5f) < 0.001f,
                "directory player or time state was not restored");

            loadedWorld.SetBlockId(15, 3, 15, (ushort)VoxelMaterial.Glass);
            loadedSaves.Save(loadedWorld, new Vector3(9f, 9f, 9f), 3f);
            string corruptRegionRelativePath;
            using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(loadedSaves.SavePath)))
            {
                corruptRegionRelativePath = manifest.RootElement.GetProperty("regions")
                    .EnumerateArray()
                    .Select(element => element.GetString() ?? string.Empty)
                    .First(path => path.EndsWith("/0_0.region", StringComparison.Ordinal));
            }
            string corruptRegionPath = Path.GetFullPath(Path.Combine(
                loadedSaves.SaveDirectory,
                corruptRegionRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            File.WriteAllBytes(corruptRegionPath, new byte[] { 1, 2, 3, 4 });

            var recoveredWorld = new VoxelWorld(generate: false, registry: reorderedBlocks);
            var recoverySaves = new WorldSaveService(
                reorderedBlocks, preferredSeed: 1, worldName: "chunk-test", saveRoot: saveRoot);
            Assert(recoverySaves.TryLoad(recoveredWorld, out SavedWorldState recoveredState, out string? recoveryError),
                recoveryError ?? "backup generation did not load");
            Assert(recoverySaves.RecoveredLastLoad &&
                   recoveredWorld.GetBlockId(15, 3, 15) == remappedMaterial &&
                   Vector3.DistanceSquared(recoveredState.PlayerPosition, new Vector3(2f, 3f, 4f)) < 0.0001f,
                "a corrupt active chunk did not recover the complete previous generation");

            recoverySaves.Save(recoveredWorld, recoveredState.PlayerPosition, recoveredState.TimeOfDay);
            var repairedWorld = new VoxelWorld(generate: false, registry: reorderedBlocks);
            var repairedSaves = new WorldSaveService(
                reorderedBlocks, preferredSeed: 1, worldName: "chunk-test", saveRoot: saveRoot);
            Assert(repairedSaves.TryLoad(repairedWorld, out _, out string? repairedError),
                repairedError ?? "repaired chunk generation did not load");
            Assert(!repairedSaves.RecoveredLastLoad && repairedWorld.GetBlockId(15, 3, 15) == remappedMaterial,
                "saving after recovery did not repair the primary manifest");
        }
        finally
        {
            if (Directory.Exists(saveRoot)) Directory.Delete(saveRoot, recursive: true);
        }
    }

    private static void TestLegacyDirectoryMigration()
    {
        string saveRoot = Path.Combine(Path.GetTempPath(), $"rtxsandbox-legacy-directory-{Guid.NewGuid():N}");
        try
        {
            var blocks = new BlockRegistry();
            string worldDirectory = Path.Combine(saveRoot, "legacy-world");
            string legacyChunkPath = Path.Combine(worldDirectory, "chunks", "0_0.chunk");
            var legacyWorld = new VoxelWorld(generate: false, registry: blocks);
            legacyWorld.Set(4, 5, 6, VoxelMaterial.Marble);
            new WorldSaveService(blocks, legacyChunkPath)
                .Save(legacyWorld, new Vector3(7f, 8f, 9f), 13.25f);
            string levelPath = Path.Combine(worldDirectory, "level.json");
            File.WriteAllText(levelPath, JsonSerializer.Serialize(new
            {
                formatVersion = 2,
                seed = 1337,
                chunks = new[] { "chunks/0_0.chunk" },
            }));

            var loadedWorld = new VoxelWorld(generate: false, registry: blocks);
            var directorySaves = new WorldSaveService(
                blocks, preferredSeed: 1, worldName: "legacy-world", saveRoot: saveRoot);
            Assert(directorySaves.TryLoad(loadedWorld, out SavedWorldState state, out string? error),
                error ?? "legacy directory region did not load");
            Assert(loadedWorld.Get(4, 5, 6) == VoxelMaterial.Marble &&
                   Vector3.DistanceSquared(state.PlayerPosition, new Vector3(7f, 8f, 9f)) < 0.0001f,
                "legacy directory region lost world or player state");

            directorySaves.Save(loadedWorld, state.PlayerPosition, state.TimeOfDay);
            using JsonDocument migratedManifest = JsonDocument.Parse(File.ReadAllText(levelPath));
            Assert(migratedManifest.RootElement.GetProperty("formatVersion").GetInt32() == 4 &&
                   migratedManifest.RootElement.GetProperty("regions").GetArrayLength() == 1 &&
                   File.Exists(levelPath + ".bak"),
                "saving a legacy directory did not create a generation manifest and preserve its old manifest");
        }
        finally
        {
            if (Directory.Exists(saveRoot)) Directory.Delete(saveRoot, recursive: true);
        }
    }

    private static void TestWorldManagement()
    {
        string saveRoot = Path.Combine(Path.GetTempPath(), $"rtxsandbox-world-management-{Guid.NewGuid():N}");
        try
        {
            var blocks = new BlockRegistry();
            var generation = new WorldGenerationSettings
            {
                Preset = WorldPreset.Void,
                Biome = BiomePreset.Auto,
                Seed = 24681357,
                GenerateLakes = false,
                GenerateVegetation = false,
            };
            generation.Validate();
            var world = new VoxelWorld(generate: false, registry: blocks);
            world.Regenerate(generation);
            world.SetBlockId(1, 10, 1, (ushort)VoxelMaterial.Glow);

            var saves = new WorldSaveService(
                blocks, preferredSeed: generation.Seed, worldName: "managed-world", saveRoot: saveRoot);
            saves.Save(world, new Vector3(4f, 5f, 6f), 9f);
            Assert(saves.TryRenameWorld("managed-world", "重新命名的世界", out string? renameError),
                renameError ?? "world rename failed");
            WorldSaveSummary renamed = saves.DiscoverWorlds().Single();
            Assert(renamed.StorageName == "managed-world" && renamed.DisplayName == "重新命名的世界" &&
                   renamed.Seed == generation.Seed,
                "rename changed storage identity or seed instead of display name only");
            saves.Save(world, new Vector3(4f, 5f, 6f), 9f);
            Assert(saves.DisplayName == "重新命名的世界" &&
                   saves.DiscoverWorlds().Single().DisplayName == "重新命名的世界",
                "a normal autosave reverted the renamed display name");
            Assert(saves.TryGetWorldGeneration("managed-world", out WorldGenerationSettings loadedGeneration,
                    out string? generationError), generationError ?? "could not read rebuild settings");
            Assert(loadedGeneration.Seed == generation.Seed && loadedGeneration.Preset == WorldPreset.Void,
                "rebuild settings did not preserve seed or preset");

            // Rebuild follows the same production flow: regenerate from stored settings,
            // overwrite the active generation, then restore the display name metadata.
            world.Regenerate(loadedGeneration);
            saves.Save(world, new Vector3(4f, 5f, 6f), 9f);
            Assert(saves.TryRenameWorld("managed-world", renamed.DisplayName, out renameError),
                renameError ?? "rebuild did not preserve display name");
            var rebuilt = new VoxelWorld(generate: false, registry: blocks);
            var loader = new WorldSaveService(
                blocks, preferredSeed: 1, worldName: "managed-world", saveRoot: saveRoot);
            Assert(loader.TryLoad(rebuilt, out SavedWorldState rebuiltState, out string? loadError),
                loadError ?? "rebuilt world did not load");
            Assert(rebuilt.GetBlockId(1, 10, 1) == 0 && rebuiltState.Seed == generation.Seed &&
                   rebuiltState.GenerationSettings.Preset == WorldPreset.Void,
                "rebuild retained player edits or changed seed/settings");
            Assert(!saves.TryDeleteWorld("..", out _),
                "world delete accepted a path outside the managed Saves root");
            Assert(saves.TryDeleteWorld("managed-world", out string? deleteError),
                deleteError ?? "world delete failed");
            Assert(!Directory.Exists(Path.Combine(saveRoot, "managed-world")) && saves.DiscoverWorlds().Count == 0,
                "deleted world remained on disk or in discovery");
        }
        finally
        {
            if (Directory.Exists(saveRoot)) Directory.Delete(saveRoot, recursive: true);
        }
    }

    private static void TestEmptyDirectoryChunkPersistence()
    {
        string saveRoot = Path.Combine(Path.GetTempPath(), $"rtxsandbox-empty-directory-{Guid.NewGuid():N}");
        try
        {
            var blocks = new BlockRegistry();
            var emptyWorld = new VoxelWorld(generate: false, registry: blocks);
            var saves = new WorldSaveService(
                blocks, preferredSeed: 2026, worldName: "empty-world", saveRoot: saveRoot);
            saves.Save(emptyWorld, new Vector3(1f, 2f, 3f), 6.5f);

            string regionsDirectory = Path.Combine(saves.SaveDirectory, "regions");
            Assert(Directory.Exists(regionsDirectory), "empty world generation directory was not created");
            Assert(Directory.GetFiles(regionsDirectory, "*.region", SearchOption.AllDirectories).Length == 0,
                "empty world emitted an unnecessary region payload");

            var loadedWorld = new VoxelWorld(generate: false, registry: blocks);
            var loader = new WorldSaveService(
                blocks, preferredSeed: 1, worldName: "empty-world", saveRoot: saveRoot);
            Assert(loader.TryLoad(loadedWorld, out SavedWorldState state, out string? error),
                error ?? "empty directory world did not load");
            Assert(loadedWorld.LoadedChunkCount == 0 && loadedWorld.PhysicsGrids.Count == 0,
                "loading an empty world allocated voxel chunks or physics grids");
            Assert(Vector3.DistanceSquared(state.PlayerPosition, new Vector3(1f, 2f, 3f)) < 0.0001f &&
                   MathF.Abs(state.TimeOfDay - 6.5f) < 0.001f,
                "empty world player state was not restored");
        }
        finally
        {
            if (Directory.Exists(saveRoot)) Directory.Delete(saveRoot, recursive: true);
        }
    }

    private static void TestExampleContentPack()
    {
        var blocks = new BlockRegistry();
        LoadedContentPack pack = blocks.LoadedContentPacks.Single(candidate =>
            candidate.Id == "rtxsandbox.example-content");
        Assert(pack.Enabled && pack.Error is null && pack.BlockCount == 1,
            $"example content pack did not load cleanly: {pack.Error ?? "no block was registered"}");
        Assert(blocks.TryGet("examplepack:cyan_lamp", out BlockDefinition? definition) &&
               definition!.Id == 500 && definition.IsPlaceable && definition.Emission.LengthSquared() > 0f,
            "example content pack block is missing or not playable");
    }

    private static void TestForcedBlockOverride()
    {
        var blocks = new BlockRegistry();
        int changeNotifications = 0;
        blocks.DefinitionsChanged += () => changeNotifications++;

        ushort originalStoneId = blocks.Get((ushort)VoxelMaterial.Stone).Id;
        var replacement = new BlockDefinition(
            "rtxsandbox:stone",
            "Replacement Stone",
            new Vector3(0.1f, 0.2f, 0.9f),
            roughness: 0.05f,
            metallic: 1f);
        ushort replacementId = blocks.Register(replacement, force: true);

        Assert(replacementId == originalStoneId, "forcing the same key did not preserve its block id");
        Assert(ReferenceEquals(blocks.Get(originalStoneId), replacement), "forced replacement was not installed by id");
        Assert(blocks.TryGet("rtxsandbox:stone", out BlockDefinition? byKey) && ReferenceEquals(byKey, replacement),
            "forced replacement was not installed by key");
        Assert(changeNotifications == 1, "forced replacement did not emit exactly one definition notification");

        replacement.Emission = new Vector3(4f, 0f, 0f);
        blocks.NotifyChanged();
        Assert(changeNotifications == 2, "direct definition edit could not notify render consumers");

        var idTakeover = new BlockDefinition(
            "test:id_takeover",
            "ID Takeover",
            Vector3.One,
            id: originalStoneId);
        blocks.Register(idTakeover, force: true);
        Assert(ReferenceEquals(blocks.Get(originalStoneId), idTakeover), "forced numeric id takeover did not win");
        Assert(!blocks.TryGet("rtxsandbox:stone", out _), "old key survived a forced numeric id takeover");
        Assert(changeNotifications == 3, "forced id takeover did not emit one definition notification");
    }

    private static void TestInventoryStacking()
    {
        var blocks = new BlockRegistry();
        var items = new ItemRegistry(blocks);
        var inventory = new PlayerInventory(items);
        Assert(inventory.Add("rtxsandbox:stone", 70) == 0, "inventory rejected a valid stack");
        Assert(inventory.GetSlot(0).Count == 64 && inventory.GetSlot(1).Count == 6,
            "items did not split at the maximum stack size");
        Assert(inventory.Remove("rtxsandbox:stone", 65), "inventory could not remove an available quantity");
        Assert(inventory.Count("rtxsandbox:stone") == 5, "inventory removal left the wrong quantity");
    }

    private static void TestDroppedItemPickup()
    {
        var blocks = new BlockRegistry();
        var items = new ItemRegistry(blocks);
        var inventory = new PlayerInventory(items);
        var world = new VoxelWorld(generate: false, registry: blocks);
        var drops = new DroppedItemManager(world, items, inventory, pickup => pickup);
        var playerPosition = new Vector3(3.5f, 1f, 3.5f);
        Assert(drops.Spawn("rtxsandbox:dirt", 3, playerPosition + Vector3.UnitY * 0.8f, Vector3.Zero),
            "valid drop did not spawn");
        for (int i = 0; i < 8; i++)
        {
            drops.Update(0.05f, playerPosition);
        }
        Assert(inventory.Count("rtxsandbox:dirt") == 3, "nearby drop was not collected");
        Assert(drops.Items.Count == 0, "fully collected drop remained in the world");
    }

    private static void TestSurvivalDamage()
    {
        var fallWorld = new VoxelWorld(generate: false);
        for (int z = 0; z < 8; z++)
        for (int x = 0; x < 8; x++)
        {
            fallWorld.Set(x, 0, z, VoxelMaterial.Stone);
        }
        var fallingPlayer = new PlayerController(fallWorld, new Vector3(3.5f, 9f, 3.5f));
        var fallSurvival = new PlayerSurvival(
            fallingPlayer,
            new Vector3(3.5f, 1f, 3.5f),
            damage => damage,
            respawn => respawn);
        for (int i = 0; i < 360; i++)
        {
            fallingPlayer.Update(1f / 120f, Vector2.Zero, -Vector3.UnitZ, false, false);
            fallSurvival.Update(1f / 120f);
        }
        Assert(fallSurvival.Health < PlayerSurvival.MaximumHealth && fallSurvival.Health > 0f,
            "survivable fall did not reduce health correctly");

        var waterWorld = new VoxelWorld(generate: false);
        for (int y = 0; y < 24; y++)
        {
            waterWorld.Set(3, y, 3, VoxelMaterial.Water);
        }
        var swimmingPlayer = new PlayerController(waterWorld, new Vector3(3.5f, 1f, 3.5f));
        var waterSurvival = new PlayerSurvival(
            swimmingPlayer,
            new Vector3(3.5f, 25f, 3.5f),
            damage => damage,
            respawn => respawn);
        for (int i = 0; i < 720; i++)
        {
            swimmingPlayer.Update(1f / 60f, Vector2.Zero, -Vector3.UnitZ, false, false);
            waterSurvival.Update(1f / 60f);
        }
        Assert(waterSurvival.Oxygen <= 0.01f, "underwater oxygen did not deplete");
        Assert(waterSurvival.Health < PlayerSurvival.MaximumHealth, "drowning did not cause damage");
    }

    private static void TestPlayerStatePersistence()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"rtxsandbox-player-{Guid.NewGuid():N}.rtxw");
        try
        {
            var blocks = new BlockRegistry();
            var items = new ItemRegistry(blocks);
            var inventory = new PlayerInventory(items);
            inventory.SetSlot(2, new ItemStack("rtxsandbox:glow", 7));
            inventory.SelectedHotbarSlot = 2;
            var world = new VoxelWorld(generate: false, registry: blocks);
            var saves = new WorldSaveService(blocks, savePath);
            saves.Save(
                world,
                new Vector3(4f, 5f, 6f),
                21.25f,
                inventory,
                13.5f,
                4.25f,
                6.75f,
                new[] { "gather_wood", "craft_planks" },
                sawNight: true);

            var loadedWorld = new VoxelWorld(generate: false, registry: blocks);
            Assert(saves.TryLoad(loadedWorld, out SavedWorldState state, out string? error), error ?? "save did not load");
            Assert(state.SelectedHotbarSlot == 2, "selected hotbar slot was not restored");
            Assert(state.Inventory.Count == inventory.SlotCount && state.Inventory[2].Count == 7,
                "inventory stack was not restored");
            Assert(MathF.Abs(state.Health - 13.5f) < 0.001f && MathF.Abs(state.Oxygen - 4.25f) < 0.001f,
                "player vitals were not restored");
            Assert(MathF.Abs(state.Hunger - 6.75f) < 0.001f, "hunger was not restored");
            Assert(state.SawNight && state.CompletedObjectives.SequenceEqual(new[] { "gather_wood", "craft_planks" }),
                "objective progress was not restored");
        }
        finally
        {
            File.Delete(savePath);
            File.Delete(savePath + ".bak");
            File.Delete(savePath + ".tmp");
        }
    }

    private static void TestBedrockProtection()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(registry: blocks);
        ushort bedrockId = world.GetBlockId(12, VoxelWorld.MinimumY, 12);
        Assert(bedrockId == (ushort)VoxelMaterial.Bedrock, "bottom layer is not bedrock");
        Assert(blocks.Get(bedrockId).IsUnbreakable, "bedrock definition is breakable");
    }

    private static void TestCrafting()
    {
        var blocks = new BlockRegistry();
        var items = new ItemRegistry(blocks);
        var recipes = new RecipeRegistry();
        ItemRecipeContentLoader.LoadInto(items, recipes);
        var inventory = new PlayerInventory(items);
        inventory.Add("rtxsandbox:wood", 2);
        var crafting = new CraftingService(recipes, items, inventory, before => before, after => after);

        Assert(crafting.Craft("rtxsandbox:planks_from_wood", false, 2) == 2, "wood did not craft into planks");
        Assert(inventory.Count("rtxsandbox:wood") == 0 && inventory.Count("rtxsandbox:planks") == 8,
            "crafting did not consume/output the correct quantities");
        Assert(crafting.Craft("rtxsandbox:sticks_from_planks", false) == 1, "planks did not craft into sticks");
        inventory.Add("rtxsandbox:stone", 3);
        Assert(crafting.Craft("rtxsandbox:stone_pickaxe", false) == 0,
            "advanced recipe ignored its workbench requirement");
        Assert(crafting.Craft("rtxsandbox:stone_pickaxe", true) == 1,
            "valid workbench recipe did not craft");
        Assert(inventory.Count("rtxsandbox:stone_pickaxe") == 1, "crafted tool was not inserted");
    }

    private static void TestToolDurability()
    {
        var blocks = new BlockRegistry();
        var items = new ItemRegistry(blocks);
        var recipes = new RecipeRegistry();
        ItemRecipeContentLoader.LoadInto(items, recipes);
        var inventory = new PlayerInventory(items);
        inventory.SetSlot(0, new ItemStack("rtxsandbox:wooden_pickaxe", 1));
        Assert(inventory.DamageSelectedTool(63, out string? broken) && broken is null,
            "tool did not accept durability damage");
        Assert(inventory.GetSlot(0).Damage == 63, "tool damage was not stored in the stack");
        Assert(inventory.DamageSelectedTool(1, out broken) && broken == "rtxsandbox:wooden_pickaxe",
            "tool did not break at maximum durability");
        Assert(inventory.GetSlot(0).IsEmpty, "broken tool remained in its slot");
    }

    private static void TestHunger()
    {
        var world = new VoxelWorld(generate: false);
        var player = new PlayerController(world, new Vector3(3.5f, 2f, 3.5f));
        var survival = new PlayerSurvival(player, player.Position, damage => damage, respawn => respawn);
        survival.AddExhaustion(6f);
        Assert(survival.Eat(5f, 1f), "food could not be eaten while hungry");
        Assert(survival.Hunger > 18.9f, "food restored the wrong hunger amount");
        survival.AddExhaustion(PlayerSurvival.MaximumHunger);
        for (int i = 0; i < 130; i++)
        {
            survival.Update(1f / 60f);
        }
        Assert(survival.Health < PlayerSurvival.MaximumHealth, "starvation did not cause damage");
    }

    private static void TestProgression()
    {
        var blocks = new BlockRegistry();
        var items = new ItemRegistry(blocks);
        var recipes = new RecipeRegistry();
        ItemRecipeContentLoader.LoadInto(items, recipes);
        var inventory = new PlayerInventory(items);
        var progression = new ProgressionTracker(inventory, objective => objective);

        inventory.Add("rtxsandbox:wood", 1);
        progression.Update(8f);
        Assert(recipes.TryGet("rtxsandbox:planks_from_wood", out RecipeDefinition? planksRecipe),
            "base planks recipe is missing");
        progression.NotifyCrafted(planksRecipe!, new ItemStack("rtxsandbox:planks", 4));
        progression.NotifyBlockPlaced(blocks.Get((byte)VoxelMaterial.Workbench));
        inventory.Add("rtxsandbox:stone_pickaxe", 1);
        progression.Update(12f);
        progression.NotifyBlockPlaced(blocks.Get((byte)VoxelMaterial.RedLamp));
        progression.Update(20f);
        progression.Update(7f);
        Assert(progression.AllCompleted, "first-night objective chain did not complete");
    }

    private static void TestEditorHistory()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        var tools = new BuildToolSystem(world, blocks, new BuildToolRegistry());
        tools.SetSelectionA(1, 1, 1);
        tools.SetSelectionB(2, 2, 2);
        Assert(tools.FillSelection((byte)VoxelMaterial.Stone), "selection fill did not edit the world");
        Assert(tools.SelectionVolume == 8, "selection volume was calculated incorrectly");
        Assert(world.Get(2, 2, 2) == VoxelMaterial.Stone, "selection fill missed a corner");
        Assert(tools.Undo(), "selection fill could not be undone");
        Assert(world.Get(1, 1, 1) == VoxelMaterial.Air && world.Get(2, 2, 2) == VoxelMaterial.Air,
            "region undo was not atomic");
        Assert(tools.Redo() && world.Get(2, 2, 2) == VoxelMaterial.Stone,
            "region redo did not restore the command");
    }

    private static void TestEditorClipboard()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        var tools = new BuildToolSystem(world, blocks, new BuildToolRegistry());
        world.Set(1, 1, 1, VoxelMaterial.Stone);
        world.Set(2, 1, 1, VoxelMaterial.Glow);
        tools.SetSelectionA(1, 1, 1);
        tools.SetSelectionB(2, 1, 1);
        Assert(tools.CopySelection(), "selection copy failed");
        Assert(tools.RotateClipboardY(), "clipboard rotation failed");
        Assert(tools.Paste(new VoxelPoint(5, 1, 5)), "clipboard paste failed");
        Assert(world.Get(5, 1, 5) == VoxelMaterial.Stone && world.Get(5, 1, 6) == VoxelMaterial.Glow,
            "rotated clipboard orientation was wrong");
    }

    private static void TestEditorExplosion()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        var tools = new BuildToolSystem(world, blocks, new BuildToolRegistry());
        world.Set(4, 4, 4, VoxelMaterial.Bedrock);
        world.Set(5, 4, 4, VoxelMaterial.Stone);
        Assert(tools.Explode(4, 4, 4, 2), "explosion changed no editable voxels");
        Assert(world.Get(4, 4, 4) == VoxelMaterial.Bedrock, "explosion removed protected bedrock");
        Assert(world.Get(5, 4, 4) == VoxelMaterial.Air, "explosion did not remove nearby stone");
        Assert(tools.Undo() && world.Get(5, 4, 4) == VoxelMaterial.Stone,
            "explosion was not undoable as one command");
    }

    private static void TestVoxelPhysics()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(3, VoxelWorld.MinimumY, 3, VoxelMaterial.Bedrock);
        world.Set(3, VoxelWorld.MinimumY + 1, 3, VoxelMaterial.Stone);
        world.Set(3, VoxelWorld.MinimumY + 2, 3, VoxelMaterial.Stone);
        world.Set(6, 4, 6, VoxelMaterial.Brick);
        world.Set(7, 4, 6, VoxelMaterial.Glass);
        var physics = new VoxelPhysicsSimulator(world, blocks);
        physics.RequestEvaluation();
        physics.AnalyzeNow();
        Assert(world.Get(3, VoxelWorld.MinimumY + 2, 3) == VoxelMaterial.Stone,
            "grounded six-face structure detached");
        Assert(world.Get(6, 4, 6) == VoxelMaterial.Air && world.Get(7, 4, 6) == VoxelMaterial.Air,
            "unsupported structure remained in the static world");
        Assert(world.PhysicsGrids.Count == 1 && world.PhysicsGrids.Grids[0].Elements.Count == 2,
            "connected unsupported voxels did not become one local grid");
        float startY = world.PhysicsGrids.Grids[0].Position.Y;
        physics.SimulationEnabled = false;
        for (int index = 0; index < 4; index++) physics.Update(1f / 60f);
        Assert(world.PhysicsGrids.Count == 1 &&
               MathF.Abs(world.PhysicsGrids.Grids[0].Position.Y - startY) < 0.00001f,
            "disabling physics deleted or moved an existing grid");
        physics.SimulationEnabled = true;
        for (int index = 0; index < 12; index++) physics.Update(1f / 60f);
        Assert(world.PhysicsGrids.Grids[0].Position.Y < startY,
            "physics grid did not move continuously under gravity");
    }

    private static void TestStaticDeformationReplacement()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        ushort metal = (ushort)VoxelMaterial.Metal;
        // The first cell is consumed by the deformation mesh.  Its neighbour is a
        // legitimate part of the authored scene, not a duplicate body to detach.
        world.SetBlockId(4, 8, 4, metal);
        world.SetBlockId(5, 8, 4, metal);
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        Assert(physics.TryConsumeForStaticDeformation(4, 8, 4, metal),
            "static deformation replacement did not consume its source voxel");
        physics.AnalyzeNow();
        Assert(world.GetBlockId(4, 8, 4) == 0 && world.GetBlockId(5, 8, 4) == metal &&
               world.PhysicsGrids.Count == 0,
            "static deformation removal detached a duplicate neighbouring structure");

        PhysicsGrid patch = world.PhysicsGrids.Create(
            Enumerable.Range(0, 3).Select(x => new PhysicsGridElement(
                x, 0, 0, new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1), metal)),
            new Vector3(10, 8, 4), id => blocks.Get(id).Density);
        patch.SetSmoothSpheres(Enumerable.Range(0, 3).Select(x =>
            new PhysicsGridSmoothSphere(new Vector3(x + 0.5f, 0.5f, 0.5f), 0.5f, metal, x, 0, 0)
            {
                ExplicitCorners =
                [
                    new(x, 0, 0), new(x + 1, 0, 0), new(x, 1, 0), new(x + 1, 1, 0),
                    new(x, 0, 1), new(x + 1, 0, 1), new(x, 1, 1), new(x + 1, 1, 1),
                ],
            }));
        patch.FreezeAsStaticDeformation();
        var precisionHit = new PhysicsGridRayHit(
            patch.Id, 1, 0, 0, metal,
            patch.LocalToWorld(new Vector3(1.5f, 0.5f, 0f)),
            patch.LocalDirectionToWorld(-Vector3.UnitZ), 2f);
        Assert(physics.TryRemoveGridSelection(precisionHit, precisionEditing: true) &&
               world.PhysicsGrids.Count == 2 &&
               world.PhysicsGrids.Grids.All(grid =>
                   grid.IsStaticDeformation && grid.Elements.Count == 1 &&
                   grid.SmoothSpheres is [{ HasExplicitCorners: true }]),
            "precision destruction did not remove one complete dented source block while preserving its neighbours");
    }

    private static void TestDiagonalFracture()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(3, 4, 3, VoxelMaterial.Stone);
        world.Set(4, 5, 3, VoxelMaterial.Stone); // Edge-only diagonal contact.
        var physics = new VoxelPhysicsSimulator(world, blocks);
        physics.RequestEvaluation();
        physics.AnalyzeNow();
        Assert(world.PhysicsGrids.Count == 2,
            "diagonally touching voxels were incorrectly merged into one supported component");
    }

    private static void TestCrouchMovement()
    {
        var world = new VoxelWorld(generate: false);
        for (int z = -2; z <= 2; z++)
        for (int x = -4; x <= 4; x++)
            world.Set(x, 0, z, VoxelMaterial.Stone);

        var standing = new PlayerController(world, new Vector3(0.5f, 1f, 0.5f));
        var crouching = new PlayerController(world, new Vector3(0.5f, 1f, -0.5f));
        standing.Update(1f / 60f, Vector2.Zero, Vector3.UnitX, false, false);
        crouching.Update(1f / 60f, Vector2.Zero, Vector3.UnitX, false, false, crouch: true);
        for (int index = 0; index < 30; index++)
        {
            standing.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitX, false, false);
            crouching.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitX, false, false, crouch: true);
        }
        Assert(crouching.IsCrouching && crouching.Position.X < standing.Position.X * 0.6f,
            "crouching did not lower movement speed");
        Assert(crouching.EyePosition.Y < crouching.Position.Y + PlayerController.EyeHeight,
            "crouching did not lower the player eye height");

        var edgeWorld = new VoxelWorld(generate: false);
        edgeWorld.Set(0, 0, 0, VoxelMaterial.Stone);
        edgeWorld.Set(1, 0, 0, VoxelMaterial.Stone);
        var edgePlayer = new PlayerController(edgeWorld, new Vector3(1.5f, 1f, 0.5f));
        edgePlayer.Update(1f / 60f, Vector2.Zero, Vector3.UnitX, false, false, crouch: true);
        for (int index = 0; index < 120; index++)
            edgePlayer.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitX, false, false, crouch: true);
        Assert(edgePlayer.Position.Y > 0.99f && edgePlayer.Position.X <= 2f + PlayerController.Radius,
            "crouching allowed the player to walk off a block edge");
    }

    private static void TestFlightMovementAxes()
    {
        var world = new VoxelWorld(generate: false);
        var facingEast = new PlayerController(world, new Vector3(2.5f, 4f, 2.5f))
        {
            FlightMode = true,
            NoClip = true,
        };
        var lookingUpEast = new PlayerController(world, new Vector3(2.5f, 4f, 2.5f))
        {
            FlightMode = true,
            NoClip = true,
        };
        for (int index = 0; index < 30; index++)
        {
            facingEast.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitX, false, false);
            lookingUpEast.Update(
                1f / 60f,
                new Vector2(0f, 1f),
                Vector3.Normalize(new Vector3(0.25f, 0.95f, 0f)),
                false,
                false);
        }
        Assert(facingEast.Position.X > 5f && MathF.Abs(facingEast.Position.Z - 2.5f) < 0.001f,
            "forward flight did not follow camera yaw");
        Assert(MathF.Abs(facingEast.Position.X - lookingUpEast.Position.X) < 0.001f &&
               MathF.Abs(lookingUpEast.Position.Y - 4f) < 0.001f,
            "camera pitch incorrectly changed horizontal flight or vertical altitude");

        var ascendingNorth = new PlayerController(world, new Vector3(2.5f, 4f, 2.5f))
        {
            FlightMode = true,
            NoClip = true,
        };
        var ascendingEast = new PlayerController(world, new Vector3(2.5f, 4f, 2.5f))
        {
            FlightMode = true,
            NoClip = true,
        };
        ascendingNorth.Update(0.1f, Vector2.Zero, -Vector3.UnitZ, false, true);
        ascendingEast.Update(0.1f, Vector2.Zero, Vector3.UnitX, false, true);
        Assert(MathF.Abs(ascendingNorth.Position.Y - ascendingEast.Position.Y) < 0.000001f &&
               ascendingNorth.Position.Y > 4f,
            "Space ascent changed with camera direction");
    }

    private static void TestEditorMaterialMetadata()
    {
        var blocks = new BlockRegistry();
        Assert(blocks.Definitions.Count >= 33, "expanded base material palette did not load");
        Assert(blocks.Get((byte)VoxelMaterial.Glass).IsTransparent &&
               blocks.Get((byte)VoxelMaterial.Glass).IndexOfRefraction > 1.4f,
            "glass transmission metadata is missing");
        Assert(blocks.Get((byte)VoxelMaterial.Gravel).AffectedByGravity,
            "gravel was not marked for simple physics");
        Assert(blocks.TryGet("rtxsandbox:iron_block", out BlockDefinition? iron) &&
               MathF.Abs(iron!.Density - 7.87f) < 0.001f,
            "iron physical density was not loaded from content");
    }

    private static void TestModBuildToolHistory()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(1, 1, 1, VoxelMaterial.Stone);
        var registry = new BuildToolRegistry();
        registry.Register(new BuildToolDefinition(
            "test:lamp_tool",
            "Lamp Tool",
            use => use.World.SetBlock(use.X + use.NormalX, use.Y + use.NormalY, use.Z + use.NormalZ,
                (byte)VoxelMaterial.Glow)));
        var tools = new BuildToolSystem(world, blocks, registry)
        {
            ToolMode = EditorToolMode.Custom,
            SelectedCustomToolKey = "test:lamp_tool",
        };
        var hit = new VoxelHit(1, 1, 1, Vector3.UnitX, 1f, VoxelMaterial.Stone);
        Assert(tools.UsePrimary(hit), "registered mod tool did not edit the world");
        Assert(world.Get(2, 1, 1) == VoxelMaterial.Glow, "mod tool wrote the wrong voxel");
        Assert(tools.Undo() && world.Get(2, 1, 1) == VoxelMaterial.Air,
            "mod tool changes were not captured by undo history");
    }

    private static void TestFlatWorldPreset()
    {
        var blocks = new BlockRegistry();
        var settings = new WorldGenerationSettings
        {
            Preset = WorldPreset.Flat,
            Seed = 42,
            FlatSurfaceHeight = 9,
            SurfaceBlockKey = "rtxsandbox:white_concrete",
            UndergroundBlockKey = "rtxsandbox:black_concrete",
            GenerateLakes = false,
            GenerateVegetation = false,
        };
        var world = new VoxelWorld(registry: blocks, generationSettings: settings);
        Assert(world.Get(10, VoxelWorld.MinimumY, 10) == VoxelMaterial.Bedrock, "flat world has no bedrock layer");
        Assert(world.Get(10, 8, 10) == VoxelMaterial.BlackConcrete, "flat underground material was ignored");
        Assert(world.Get(10, 9, 10) == VoxelMaterial.WhiteConcrete, "flat surface material was ignored");
        Assert(world.Get(10, 10, 10) == VoxelMaterial.Air, "flat world generated material above its surface");
        Assert(world.Seed == 42 && world.GenerationSettings.Preset == WorldPreset.Flat,
            "flat generation metadata was not retained");
    }

    private static void TestNaturalWorldPreset()
    {
        var blocks = new BlockRegistry();
        var settings = new WorldGenerationSettings
        {
            Preset = WorldPreset.Natural,
            Biome = BiomePreset.Desert,
            Seed = 777,
            GenerateLakes = true,
            GenerateVegetation = true,
        };
        var world = new VoxelWorld(registry: blocks, generationSettings: settings);
        Assert(CountBlock(world, VoxelMaterial.Sand) > 100, "locked desert biome did not generate sand");
        Assert(CountBlock(world, VoxelMaterial.Marble) == 0 && CountBlock(world, VoxelMaterial.Glow) == 0,
            "natural world incorrectly generated showcase structures");
    }

    private static void TestNaturalV2Terrain()
    {
        var blocks = new BlockRegistry();
        var settings = new WorldGenerationSettings
        {
            Preset = WorldPreset.PureNature,
            Biome = BiomePreset.Auto,
            Seed = 24681357,
            GenerateLakes = true,
            GenerateVegetation = true,
        };
        var first = new VoxelWorld(registry: blocks, generationSettings: settings);
        var second = new VoxelWorld(registry: blocks, generationSettings: settings);
        Assert(first.RawData.SequenceEqual(second.RawData), "same seed produced different natural terrain");

        string[] geologicalKeys =
        {
            "rtxsandbox:granite",
            "rtxsandbox:limestone",
            "rtxsandbox:slate",
            "rtxsandbox:volcanic_rock",
        };
        int geologicalVoxels = geologicalKeys
            .Select(key => blocks.TryGet(key, out BlockDefinition? definition) ? definition!.Id : (ushort)0)
            .Where(id => id != 0)
            .Sum(id => first.RawData.Count(voxel => voxel == id));
        Assert(geologicalVoxels > 200, $"natural terrain generated only {geologicalVoxels} layered geology voxels");
        Assert(first.RawData.Any(voxel => voxel == (ushort)VoxelMaterial.Water), "natural v2 generated no river/lake water");
    }

    private static void TestPureNaturePreset()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(registry: blocks, generationSettings: new WorldGenerationSettings
        {
            Preset = WorldPreset.PureNature,
            Biome = BiomePreset.Auto,
            Seed = 90125,
            GenerateLakes = true,
            GenerateVegetation = true,
        });
        foreach (int chunkX in new[] { -128, -96, -64, -32, 32, 64, 96, 128 })
            world.EnsureChunkGenerated(new ChunkCoordinate(chunkX, 0));
        HashSet<ushort> materials = world.SnapshotGeneratedChunks()
            .SelectMany(chunk => world.SnapshotChunk(chunk).Voxels)
            .Select(voxel => voxel.MaterialId)
            .ToHashSet();
        int climateSurfaceTypes = new[]
        {
            VoxelMaterial.Sand, VoxelMaterial.Grass, VoxelMaterial.Snow,
            VoxelMaterial.Mud, VoxelMaterial.Bamboo,
        }.Count(material => materials.Contains((ushort)material));
        Assert(climateSurfaceTypes >= 3,
            $"infinite pure-nature sampling exposed only {climateSurfaceTypes} climate surface types");
        Assert(materials.Contains((ushort)VoxelMaterial.Water), "pure nature has no connected water band");
    }

    private static void TestVoidWorldPreset()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(registry: blocks, generationSettings: new WorldGenerationSettings
        {
            Preset = WorldPreset.Void,
            Seed = 1,
        });
        int occupied = world.RawData.Count(block => block != 0);
        Assert(occupied == 82, $"void world should contain 81 platform blocks and one lamp, found {occupied}");
        Assert(CountBlock(world, VoxelMaterial.WhiteConcrete) == 81, "void platform has the wrong dimensions");
    }

    private static void TestShowcaseWorldPreset()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(registry: blocks, generationSettings: new WorldGenerationSettings
        {
            Preset = WorldPreset.Showcase,
            Seed = 1337,
        });
        Assert(CountBlock(world, VoxelMaterial.Water) > 50, "showcase water lab is missing");
        int glassCount = world.RawData.Count(id => id != 0 && blocks.Get(id).Tags.Any(tag =>
            tag.Contains("glass", StringComparison.OrdinalIgnoreCase)));
        int crystalCount = world.RawData.Count(id => id != 0 && blocks.Get(id).Tags.Any(tag =>
            tag.Contains("crystal", StringComparison.OrdinalIgnoreCase)));
        Assert(glassCount > 40 && crystalCount > 0,
            "showcase glass forest is missing");
        bool hasWarmEmitter = world.RawData.Any(id => id != 0 && blocks.Get(id).Emission.X > 1f);
        bool hasCoolEmitter = world.RawData.Any(id => id != 0 && blocks.Get(id).Emission.Z > 1f);
        Assert(hasWarmEmitter && hasCoolEmitter,
            "showcase lighting lab is missing");
        Assert(CountBlock(world, VoxelMaterial.Mirror) > 0, "showcase material gallery is missing mirrors");
        Assert(world.PhysicsGrids.Count >= 8, "showcase physics arena is missing structures");
        Assert(world.AnalyticSpheres.Count >= 7, "showcase physics arena is missing analytic spheres");
        Assert(world.GetBlockId(21, 10, 41) == 0, "showcase camera-obscura aperture is blocked");
        Assert(blocks.Get(world.GetBlockId(47, 11, 60)).Tags.Contains("soft-body"),
            "showcase core soft-body beam is not attached world geometry");
        Assert(world.GetBlockId(46, 11, 60) == (ushort)VoxelMaterial.Bedrock,
            "showcase core soft-body beam has no world fixture");
        Assert(!world.PhysicsGrids.Grids.Any(grid => grid.Elements.Any(element =>
                blocks.Get(element.MaterialId).Tags.Contains("soft-body"))),
            "showcase incorrectly serialized supported soft blocks as a detached physics grid");
        Assert(world.RawData.Any(id => id != 0 && blocks.Get(id).Tags.Contains("animation-controller")),
            "showcase animation controller block is missing");
    }

    private static void TestExternalBridgeWorldPreset()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks, generationSettings: new WorldGenerationSettings
        {
            Preset = WorldPreset.ExternalBridge,
            Seed = 1337,
            GenerateLakes = false,
            GenerateVegetation = false,
        });
        Assert(world.RawData.All(block => block == 0), "external bridge world generated terrain");
        Assert(world.QueueChunksAround(Vector3.Zero, 3) == 0,
            "external bridge world queued procedural terrain");
        Assert(world.SetBlockId(-3, 4, 7, (ushort)VoxelMaterial.Metal),
            "external bridge cell was rejected");
        Assert(world.GetBlockId(-3, 4, 7) == (ushort)VoxelMaterial.Metal,
            "external bridge cell did not preserve the exact coordinate and material");
        Assert(world.IntersectsSolid(new Vector3(-2.9f, 4.1f, 7.1f), new Vector3(-2.1f, 4.9f, 7.9f)),
            "external bridge block did not participate in collision");
    }

    private static void TestAnimationControllerPlaceholder()
    {
        var blocks = new BlockRegistry();
        Assert(blocks.TryGet("rtxsandbox:animation_controller", out BlockDefinition? controller) &&
               controller!.IsPlaceable && controller.Tags.Contains("animation-controller"),
            "animation controller placeholder block is missing");
        string[] retired =
        [
            "rtxsandbox:linear_controller", "rtxsandbox:rotary_controller",
            "rtxsandbox:electric_motor", "rtxsandbox:drive_shaft",
            "rtxsandbox:small_spur_gear", "rtxsandbox:large_spur_gear",
        ];
        Assert(retired.All(key => !blocks.TryGet(key, out _)),
            "retired mechanical prototype blocks are still registered");
        Assert(!MechanicalPartGeometry.IsMechanical(controller!),
            "animation controller still resolves to the retired mechanical renderer");
    }

    private static void TestCoreSoftBodySystem()
    {
        var blocks = new BlockRegistry();
        Assert(blocks.TryGet("rtxsandbox:iron_block", out BlockDefinition? iron), "iron endpoint material is missing");
        int[] edgeA = [0, 0, 0, 1, 1, 2, 2, 3, 4, 4, 5, 6];
        int[] edgeB = [1, 2, 4, 3, 5, 3, 6, 7, 5, 6, 7, 7];
        foreach (string key in new[] { "rtxsandbox:gel", "rtxsandbox:rubber", "rtxsandbox:foam" })
        {
            Assert(blocks.TryGet(key, out BlockDefinition? foundMaterial), $"core soft material {key} is missing");
            BlockDefinition material = foundMaterial!;
            var world = new VoxelWorld(generate: false, registry: blocks);
            for (int z = -8; z <= 8; z++)
            for (int x = -8; x <= 12; x++) world.SetBlockId(x, 0, z, (ushort)VoxelMaterial.Bedrock);
            for (int y = 0; y <= 6; y++) world.SetBlockId(0, y, 0, (ushort)VoxelMaterial.Bedrock);
            for (int x = 1; x <= 4; x++) world.SetBlockId(x, 6, 0, material.Id);
            world.SetBlockId(5, 6, 0, iron!.Id);
            using var physics = new VoxelPhysicsSimulator(world, blocks);
            using var soft = new SoftBodyPhysicsSystem(world, blocks, physics);
            PhysicsGrid grid = world.PhysicsGrids.Grids.Single();
            float initialTip = grid.LocalToWorld(grid.Elements.MaxBy(element => element.CellX).Center).Y;
            for (int frame = 0; frame < 720; frame++) soft.Update(1f / 120f);
            Assert(grid.IsDeformable, $"core solver did not own {key}");
            Assert(grid.SmoothSpheres.Count == 5, $"{key} did not publish every source cell");
            float bentTip = grid.SmoothSpheres.MaxBy(cell => cell.CellX).Center.Y +
                            (grid.Position - grid.CenterOfMassLocal).Y;
            Assert(bentTip < initialTip - 0.015f, $"{key} did not bend ({initialTip:F3}->{bentTip:F3})");
            Assert(bentTip > initialTip - 1.35f,
                $"{key} collapsed instead of remaining a supported soft solid ({initialTip:F3}->{bentTip:F3})");
            PhysicsGridSmoothSphere hardTip = grid.SmoothSpheres.Single(cell => cell.MaterialId == iron.Id);
            float maximumRigidEdgeError = Enumerable.Range(0, edgeA.Length)
                .Select(index => MathF.Abs(Vector3.Distance(
                    hardTip.ExplicitCorners![edgeA[index]],
                    hardTip.ExplicitCorners![edgeB[index]]) - 1f)).Max();
            Assert(maximumRigidEdgeError < 0.01f,
                $"{key} iron endpoint inherited soft deformation (edge error {maximumRigidEdgeError:F4})");
            float maximumSoftEdge = grid.SmoothSpheres
                .Where(cell => cell.MaterialId == material.Id)
                .SelectMany(cell => Enumerable.Range(0, edgeA.Length).Select(index =>
                    Vector3.Distance(cell.ExplicitCorners![edgeA[index]], cell.ExplicitCorners![edgeB[index]])))
                .Max();
            Assert(maximumSoftEdge < 1.16f,
                $"{key} formed an unbounded tether (edge {maximumSoftEdge:F3})");
            PhysicsGridSmoothSphere supportedRoot = grid.SmoothSpheres.MinBy(cell => cell.CellX);
            float supportedRootWorldY = supportedRoot.Center.Y + (grid.Position - grid.CenterOfMassLocal).Y;
            Assert(supportedRootWorldY > 6.15f,
                $"{key} forgot its still-present wall support (root Y {supportedRootWorldY:F3})");

            world.SetBlockId(0, 6, 0, 0);
            float beforeRelease = grid.Position.Y;
            for (int frame = 0; frame < 90; frame++) soft.Update(1f / 120f);
            Assert(grid.Position.Y < beforeRelease - 0.05f, $"removing {key} support did not release it");
            // Clear the remaining vertical fixture before the floor-settling check;
            // this isolates restitution instead of timing a body scraping down a wall.
            for (int y = 1; y < 6; y++) world.SetBlockId(0, y, 0, 0);
            for (int frame = 0; frame < 1200; frame++) soft.Update(1f / 120f);
            (Vector3 settledMinimum, Vector3 settledMaximum) = grid.CalculateWorldBounds();
            Assert(grid.IsSleeping && MathF.Abs(grid.LinearVelocity.Y) < 0.025f,
                $"released {key} never settled on the floor (sleep {grid.IsSleeping}, " +
                $"vy {grid.LinearVelocity.Y:F3}, Y {settledMinimum.Y:F2}..{settledMaximum.Y:F2})");
        }
    }

    private static void TestSoftBodySavedTopologyRecovery()
    {
        var blocks = new BlockRegistry();
        Assert(blocks.TryGet("rtxsandbox:gel", out BlockDefinition? gel), "core gel material is missing");
        var world = new VoxelWorld(generate: false, registry: blocks);
        for (int y = 0; y <= 6; y++) world.SetBlockId(0, y, 0, (ushort)VoxelMaterial.Bedrock);

        var elements = new List<PhysicsGridElement>();
        var corruptedSkin = new List<PhysicsGridSmoothSphere>();
        for (int x = 1; x <= 6; x++)
        {
            ushort material = x <= 4 ? gel!.Id : (ushort)VoxelMaterial.Metal;
            // This is the legacy failure mode captured in world1: logical neighbours
            // retain consecutive cell coordinates, while their persisted AABBs and
            // explicit corners have drifted into disconnected one-block islands.
            Vector3 minimum = new(x, 6f, (x - 1) * 3f);
            Vector3 maximum = minimum + Vector3.One;
            elements.Add(new PhysicsGridElement(x, 6, 0, minimum, maximum, material));
            var corners = new Vector3[8];
            for (int z = 0; z < 2; z++)
            for (int y = 0; y < 2; y++)
            for (int cornerX = 0; cornerX < 2; cornerX++)
                corners[cornerX + y * 2 + z * 4] = minimum + new Vector3(cornerX, y, z);
            corruptedSkin.Add(new PhysicsGridSmoothSphere(
                (minimum + maximum) * 0.5f, 0.5f, material, x, 6, 0)
            {
                ExplicitCorners = corners,
            });
        }

        PhysicsGrid corrupted = world.PhysicsGrids.Create(elements, Vector3.Zero, id => blocks.Get(id).Density);
        corrupted.SetSmoothSpheres(corruptedSkin);
        string savePath = Path.Combine(Path.GetTempPath(), $"rtxsandbox-soft-recovery-{Guid.NewGuid():N}.rtxw");
        try
        {
            new WorldSaveService(blocks, savePath).Save(world, new Vector3(2f), 12f);
            var loaded = new VoxelWorld(generate: false, registry: blocks);
            var saves = new WorldSaveService(blocks, savePath);
            Assert(saves.TryLoad(loaded, out _, out string? error), error ?? "corrupted soft save did not load");
            PhysicsGrid grid = loaded.PhysicsGrids.Grids.Single();
            using var physics = new VoxelPhysicsSimulator(loaded, blocks);
            using var soft = new SoftBodyPhysicsSystem(loaded, blocks, physics);
            for (int frame = 0; frame < 1200; frame++) soft.Update(1f / 120f);

            Assert(grid.IsDeformable && grid.SmoothSpheres.Count == 6,
                "reloaded soft structure was not reclaimed by the core solver");
            float worldOriginZ = grid.LocalToWorld(Vector3.Zero).Z;
            float spanZ = grid.SmoothSpheres.SelectMany(cell => cell.ExplicitCorners!)
                .Select(corner => corner.Z + worldOriginZ).Max() -
                          grid.SmoothSpheres.SelectMany(cell => cell.ExplicitCorners!)
                .Select(corner => corner.Z + worldOriginZ).Min();
            Assert(spanZ < 3.5f,
                $"legacy disconnected AABBs survived topology recovery (Z span {spanZ:F3})");

            PhysicsGridSmoothSphere[] ordered = grid.SmoothSpheres.OrderBy(cell => cell.CellX).ToArray();
            float maximumSharedCornerGap = 0f;
            for (int cell = 0; cell < ordered.Length - 1; cell++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
                maximumSharedCornerGap = MathF.Max(maximumSharedCornerGap, Vector3.Distance(
                    ordered[cell].GetCorner(1, y, z), ordered[cell + 1].GetCorner(0, y, z)));
            Assert(maximumSharedCornerGap < 0.001f,
                $"logical neighbour corners were still disconnected ({maximumSharedCornerGap:F4})");

            PhysicsGridSmoothSphere loadedRoot = ordered[0];
            float loadedRootMinimumY = loadedRoot.ExplicitCorners!
                .Select(corner => grid.LocalToWorld(corner).Y).Min();
            Assert(loadedRootMinimumY > 5.5f,
                $"reloaded soft structure forgot its wall glue and fell (root Y {loadedRootMinimumY:F3})");

            PhysicsGridSmoothSphere[] hard = ordered.Where(cell => cell.MaterialId == (ushort)VoxelMaterial.Metal).ToArray();
            Assert(hard.Length == 2 && MathF.Abs(Vector3.Distance(hard[0].Center, hard[1].Center) - 1f) < 0.015f,
                "solid endpoint was not projected as one rigid cluster");
        }
        finally
        {
            File.Delete(savePath);
            File.Delete(savePath + ".bak");
            File.Delete(savePath + ".tmp");
        }
    }

    private static void TestMechanicalControllers()
    {
        var blocks = new BlockRegistry();
        Assert(blocks.TryGet("rtxsandbox:linear_controller", out BlockDefinition? controller),
            "linear controller material is missing");
        Assert(blocks.TryGet("rtxsandbox:electric_motor", out BlockDefinition? motor),
            "mechanical power source is missing");
        Assert(blocks.TryGet("rtxsandbox:reduction_gearbox", out BlockDefinition? reduction),
            "mechanical gearbox is missing");
        Assert(blocks.TryGet("rtxsandbox:small_spur_gear", out BlockDefinition? smallGear),
            "small spur gear is missing");
        Assert(blocks.TryGet("rtxsandbox:large_spur_gear", out BlockDefinition? largeGear),
            "large spur gear is missing");
        Assert(blocks.TryGet("rtxsandbox:bevel_gear", out BlockDefinition? bevelGear),
            "bevel gear is missing");
        Assert(blocks.TryGet("rtxsandbox:gear_rack", out BlockDefinition? rack),
            "gear rack is missing");
        Assert(blocks.TryGet("rtxsandbox:universal_joint", out BlockDefinition? universalJoint),
            "universal joint is missing");
        Assert(MechanicalPartGeometry.Build(smallGear!, MechanicalAxis.X).Count >= 10 &&
               MechanicalPartGeometry.Build(largeGear!, MechanicalAxis.X).Count >
               MechanicalPartGeometry.Build(smallGear!, MechanicalAxis.X).Count &&
               MechanicalPartGeometry.Build(bevelGear!, MechanicalAxis.X).Count >= 18 &&
               MechanicalPartGeometry.Build(rack!, MechanicalAxis.X).Count >= 10 &&
               MechanicalPartGeometry.Build(universalJoint!, MechanicalAxis.X).Count >= 4,
            "new transmission parts still share one placeholder cube");
        IReadOnlyList<MechanicalPartMeshVertex> smallMesh =
            MechanicalPartGeometry.BuildVisualMesh(smallGear!, MechanicalAxis.X);
        IReadOnlyList<MechanicalPartMeshVertex> largeMesh =
            MechanicalPartGeometry.BuildVisualMesh(largeGear!, MechanicalAxis.X);
        Assert(smallMesh.Count > 600 && largeMesh.Count > smallMesh.Count &&
               smallMesh.Count % 3 == 0 && largeMesh.Count % 3 == 0 &&
               smallMesh.All(vertex => vertex.Position.X >= -0.001f && vertex.Position.X <= 1.001f &&
                                       vertex.Position.Y >= -0.001f && vertex.Position.Y <= 1.001f &&
                                       vertex.Position.Z >= -0.001f && vertex.Position.Z <= 1.001f &&
                                       MathF.Abs(vertex.Normal.Length() - 1f) < 0.001f),
            "high precision gear mesh is missing, degenerate, or outside its voxel");
        IReadOnlyList<MechanicalPartBox> motorParts = MechanicalPartGeometry.Build(motor!, MechanicalAxis.X);
        Assert(motorParts.Count >= 7 && motorParts.All(part =>
                part.Minimum.X >= 0f && part.Minimum.Y >= 0f && part.Minimum.Z >= 0f &&
                part.Maximum.X <= 1f && part.Maximum.Y <= 1f && part.Maximum.Z <= 1f) &&
               motorParts.Any(part => (part.Maximum - part.Minimum).Y < 0.45f),
            "mechanical blocks still resolve to one featureless full cube");
        var axisMaterials = new Dictionary<(int X, int Y, int Z), ushort>
        {
            [(0, 0, -1)] = motor!.Id,
            [(0, 0, 1)] = reduction!.Id,
        };
        MechanicalAxis connectedAxis = MechanicalPartGeometry.ResolveAxis(0, 0, 0,
            (x, y, z) => axisMaterials.GetValueOrDefault((x, y, z)), blocks.CreateLookupSnapshot());
        Assert(connectedAxis == MechanicalAxis.Z,
            "procedural shaft did not align to its face-connected neighbours");
        var wallMounted = new Dictionary<(int X, int Y, int Z), ushort>
        {
            [(0, 0, -1)] = (ushort)VoxelMaterial.Stone,
            [(1, 0, 0)] = largeGear!.Id,
        };
        Assert(MechanicalPartGeometry.ResolveAxis(0, 0, 0,
                   (x, y, z) => wallMounted.GetValueOrDefault((x, y, z)),
                   blocks.CreateLookupSnapshot()) == MechanicalAxis.Z,
            "a neighbouring gear overrode the shared wall mounting axis");
        var world = new VoxelWorld(generate: false, registry: blocks);

        PhysicsGrid unpowered = world.PhysicsGrids.Create(new[]
        {
            new PhysicsGridElement(0,0,0, Vector3.Zero, Vector3.One, controller!.Id),
            new PhysicsGridElement(1,0,0, Vector3.UnitX, Vector3.UnitX + Vector3.One, (ushort)VoxelMaterial.Metal),
        }, new Vector3(0f, 5f, 6f), id => blocks.Get(id).Density);
        var mechanics = new KinematicStructureSystem(world, blocks);
        Vector3 unpoweredStart = unpowered.Position;
        for (int frame = 0; frame < 30; frame++) mechanics.Update(1f / 60f);
        Assert(!unpowered.IsKinematicController && unpowered.Position == unpoweredStart,
            "an unpowered actuator still played a scripted animation");

        PhysicsGrid disconnected = world.PhysicsGrids.Create(new[]
        {
            new PhysicsGridElement(0,0,0, Vector3.Zero, Vector3.One, controller.Id),
            new PhysicsGridElement(1,0,0, Vector3.UnitX, Vector3.UnitX + Vector3.One,
                (ushort)VoxelMaterial.Metal),
            new PhysicsGridElement(2,0,0, new Vector3(2,0,0), new Vector3(3,1,1), motor!.Id),
        }, new Vector3(0f, 5f, 12f), id => blocks.Get(id).Density);
        for (int frame = 0; frame < 30; frame++) mechanics.Update(1f / 60f);
        Assert(!disconnected.IsKinematicController,
            "a motor powered a controller through a non-mechanical payload block");

        PhysicsGrid unsupportedPowered = world.PhysicsGrids.Create(new[]
        {
            new PhysicsGridElement(-1,0,0, new Vector3(-1,0,0), new Vector3(0,1,1), motor.Id),
            new PhysicsGridElement(0,0,0, Vector3.Zero, Vector3.One, controller.Id),
        }, new Vector3(20f, 16f, 20f), id => blocks.Get(id).Density);
        float unsupportedStartY = unsupportedPowered.Position.Y;
        using (var fallingPhysics = new VoxelPhysicsSimulator(world, blocks))
        {
            for (int frame = 0; frame < 60; frame++)
            {
                mechanics.Update(1f / 120f);
                fallingPhysics.Update(1f / 120f);
            }
        }
        Assert(!unsupportedPowered.IsKinematicController &&
               unsupportedPowered.Position.Y < unsupportedStartY - 0.15f,
            "unsupported powered machine hovered instead of falling as a rigid body");

        PhysicsGrid grid = world.PhysicsGrids.Create(new[]
        {
            new PhysicsGridElement(-2,0,0, new Vector3(-2,0,0), new Vector3(-1,1,1), motor!.Id),
            new PhysicsGridElement(-1,0,0, new Vector3(-1,0,0), new Vector3(0,1,1), reduction!.Id),
            new PhysicsGridElement(0,0,0, Vector3.Zero, Vector3.One, controller.Id),
            new PhysicsGridElement(1,0,0, Vector3.UnitX, Vector3.UnitX + Vector3.One, (ushort)VoxelMaterial.Metal),
        }, new Vector3(4f, 5f, 6f), id => blocks.Get(id).Density);
        for (int x = 2; x <= 4; x++) world.SetBlockId(x, 4, 6, (ushort)VoxelMaterial.Bedrock);
        PhysicsGrid payload = world.PhysicsGrids.Create(new[]
        {
            new PhysicsGridElement(0,0,0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Metal),
        }, new Vector3(5f, 5f, 6f), id => blocks.Get(id).Density);
        Vector3 start = grid.Position;
        Vector3 payloadStart = payload.Position;
        for (int frame = 0; frame < 120; frame++) mechanics.Update(1f / 60f);
        Assert(grid.IsKinematicController, "controller grid was not claimed by mechanics");
        Assert(Vector3.Distance(start, grid.Position) < 0.0001f,
            "powered machine moved its motor housing instead of only driving the output");
        Assert(payload.LinearVelocity.LengthSquared() > 0.001f ||
               Vector3.Distance(payloadStart, payload.Position) > 0.001f,
            "anchored actuator did not transfer force to its separate payload");
        Assert(mechanics.TryGetTelemetry(grid.Id, out KinematicStructureSystem.MechanicalTelemetry telemetry) &&
               telemetry.Powered && telemetry.Rpm > 0.5f && MathF.Abs(telemetry.GearRatio - 0.42f) < 0.001f &&
               telemetry.AvailableTorque > 20f,
            $"mechanical powertrain telemetry is invalid: {telemetry}");
        using (var projectiles = new EnergyProjectileSystem(world, blocks))
        {
            MethodInfo applyImpact = typeof(EnergyProjectileSystem).GetMethod(
                "ApplyImpact", BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("energy impact hook is missing");
            applyImpact.Invoke(projectiles,
            [
                new EnergyImpact(grid.Position, -Vector3.UnitX, Vector3.UnitX,
                    36f, 0.6f, motor.Id, 0, 0, 0, grid.Id),
            ]);
            mechanics.Update(1f / 30f);
            Assert(Vector3.Distance(start, grid.Position) < 0.0001f,
                "an impact displaced a machine through its fixed foundation");
        }
        for (int x = 2; x <= 4; x++) world.SetBlockId(x, 4, 6, 0);
        mechanics.Update(1f / 60f);
        Assert(!grid.IsKinematicController && mechanics.ActiveControllerCount == 0,
            "machine stayed kinematic after its final static support was removed");
        grid.ApplyImpulseAtPoint(Vector3.UnitX * 12f, grid.Position);
        Assert(grid.LinearVelocity.LengthSquared() > 0.001f,
            "released machine did not accept force as a normal rigid body");
    }

    private static void TestEnergyGlassFracture()
    {
        var blocks = new BlockRegistry();
        Assert(blocks.TryGet("rtxsandbox:glass", out BlockDefinition? baseGlass), "base glass is missing");
        Assert(blocks.TryGet("rtxsandbox:clear_glass", out BlockDefinition? glass), "clear glass is missing");
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.SetBlockId(4, 4, 4, baseGlass!.Id);
        using var projectiles = new EnergyProjectileSystem(world, blocks)
        {
            ForceLevel = EnergyForceLevel.High,
            ImpactRadius = 0.18f,
        };
        projectiles.Fire(new Vector3(1.5f, 4.5f, 4.5f), Vector3.UnitX);
        for (int frame = 0; frame < 30 && projectiles.ActiveProjectileCount > 0; frame++)
            projectiles.Update(1f / 120f);
        Assert(projectiles.ActiveProjectileCount == 0, "energy projectile did not resolve its hit");
        Assert(world.GetBlockId(4, 4, 4) == 0 &&
               !world.TryGetMicroVoxels(4, 4, 4, out _),
            "high-pressure ordinary glass remained floating in the world after shattering");
        PhysicsGrid[] shards = world.PhysicsGrids.Grids.ToArray();
        Assert(shards.Length == 8,
            $"glass fracture produced {shards.Length} shard bodies instead of the bounded radial set");
        Assert(shards.Sum(shard => shard.Elements.Count) == 8 &&
               shards.All(shard => shard.Elements.Count == 1 &&
                                    shard.IsDebrisFragment &&
                                    shard.SmoothSpheres.Count == 1 &&
                                    shard.SmoothSpheres[0].HasExplicitCorners &&
                                   shard.LinearVelocity.LengthSquared() > 0.01f),
            "glass fracture did not create one explicit irregular prism per rigid shard");
        Assert(shards.All(shard =>
                   SoftBodySurfaceBuilder.Build(shard.SmoothSpheres) is { Length: 24 } surface &&
                   surface.Chunk(3).All(triangle =>
                       Vector3.Cross(triangle[1].Position - triangle[0].Position,
                           triangle[2].Position - triangle[0].Position).LengthSquared() > 0.0000001f)),
            "glass shard renderer emitted subdivision, duplicate faces, or zero-area triangles");
        Assert(shards.All(shard => SoftBodySurfaceBuilder.BuildBeamSegments(shard.SmoothSpheres) is
                   [{ HalfWidth: < 0.5f }]),
            "compute path tracing fallback expanded an irregular shard into a full block beam");
        int distinctVisualCenters = shards.Select(shard => shard.SmoothSpheres[0].ExplicitCorners!
                .Aggregate(Vector3.Zero, (sum, corner) => sum + corner) / 8f)
            .Select(center => (MathF.Round(center.Y, 3), MathF.Round(center.Z, 3)))
            .Distinct().Count();
        Assert(distinctVisualCenters == 8,
            "seeded glass fracture returned duplicate boxes instead of radial procedural pieces");

        PhysicsGrid dynamicGlass = world.PhysicsGrids.Create(new[]
        {
            new PhysicsGridElement(0,0,0, Vector3.Zero, Vector3.One, glass!.Id),
        }, new Vector3(8f, 5f, 4f), id => blocks.Get(id).Density);
        MethodInfo applyImpact = typeof(EnergyProjectileSystem).GetMethod(
            "ApplyImpact", BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("energy impact hook is missing");
        int beforeDynamicShards = world.PhysicsGrids.Count;
        HashSet<ulong> idsBeforeDynamicHit = world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        applyImpact.Invoke(projectiles,
        [
            new EnergyImpact(dynamicGlass.Position, -Vector3.UnitX, Vector3.UnitX,
                12f, 1.25f, glass.Id, 0, 0, 0, dynamicGlass.Id),
        ]);
        Assert(ReferenceEquals(world.PhysicsGrids.Get(dynamicGlass.Id), dynamicGlass) &&
               world.PhysicsGrids.Count == beforeDynamicShards + 7,
            "first glass hit deleted/replaced its original body instead of retaining one child in place");
        PhysicsGrid[] dynamicShards = world.PhysicsGrids.Grids
            .Where(shard => shard.Id == dynamicGlass.Id || !idsBeforeDynamicHit.Contains(shard.Id)).ToArray();
        Assert(dynamicShards.Length == 8 &&
               dynamicShards.Sum(shard => shard.Elements.Count) == 8 &&
               dynamicShards.All(shard => shard.IsDebrisFragment &&
                                          shard.SmoothSpheres.Count == 1 &&
                                          shard.SmoothSpheres[0].HasExplicitCorners),
            "dynamic glass multiplied one block into excessive fragment geometry");
        float shardHeightBefore = dynamicShards.Average(shard => shard.Position.Y);
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        for (int frame = 0; frame < 45; frame++) physics.Update(1f / 120f);
        float shardHeightAfter = dynamicShards.Average(shard => shard.Position.Y);
        Assert(shardHeightAfter < shardHeightBefore - 0.02f,
            "small glass shard structures were created visually but did not receive rigid-body gravity");

        PhysicsGrid firstGeneration = dynamicShards[0];
        ulong firstGenerationId = firstGeneration.Id;
        int bodyCountBeforeSecondHit = world.PhysicsGrids.Count;
        HashSet<ulong> idsBeforeSecondHit = world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        applyImpact.Invoke(projectiles,
        [
            new EnergyImpact(firstGeneration.Position, -Vector3.UnitX, Vector3.UnitX,
                30f, 0.16f, glass.Id, 0, 0, 0, firstGenerationId),
        ]);
        Assert(ReferenceEquals(world.PhysicsGrids.Get(firstGenerationId), firstGeneration) &&
               world.PhysicsGrids.Count == bodyCountBeforeSecondHit + 2,
            "second hit deleted the struck shard instead of retaining it plus two child prisms");
        PhysicsGrid[] secondGeneration = world.PhysicsGrids.Grids
            .Where(shard => (shard.Id == firstGenerationId || !idsBeforeSecondHit.Contains(shard.Id)) &&
                            shard.FractureGeneration == 2)
            .ToArray();
        Assert(secondGeneration.Length == 3 && secondGeneration.All(shard =>
                   shard.FractureOnImpact && shard.SmoothSpheres is [{ HasExplicitCorners: true }]),
            "second-generation glass children lost their geometry or fracture depth");
        Assert(secondGeneration.All(shard => shard.LinearVelocity.Length() <= 18.001f &&
                                             shard.AngularVelocity.Length() <= 24.001f),
            "second hit launched a light shard at disappearance/floating speed");

        PhysicsGrid second = firstGeneration;
        int bodyCountBeforeThirdHit = world.PhysicsGrids.Count;
        HashSet<ulong> idsBeforeThirdHit = world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        applyImpact.Invoke(projectiles,
        [
            new EnergyImpact(second.Position, -Vector3.UnitX, Vector3.UnitX,
                30f, 0.16f, glass.Id, 0, 0, 0, second.Id),
        ]);
        Assert(ReferenceEquals(world.PhysicsGrids.Get(second.Id), second) &&
               world.PhysicsGrids.Count == bodyCountBeforeThirdHit + 2,
            "third hit deleted the struck shard instead of retaining it plus two final children");
        PhysicsGrid terminal = world.PhysicsGrids.Grids
            .Where(shard => (shard.Id == second.Id || !idsBeforeThirdHit.Contains(shard.Id)) &&
                            shard.FractureGeneration == 3)
            .First(shard => shard.Id == second.Id);
        Assert(!terminal.FractureOnImpact, "third-generation shard still requests recursive fracture");

        ulong terminalId = terminal.Id;
        int bodyCountBeforeTerminalHit = world.PhysicsGrids.Count;
        Vector3 velocityBeforeTerminalHit = terminal.LinearVelocity;
        applyImpact.Invoke(projectiles,
        [
            new EnergyImpact(terminal.Position, -Vector3.UnitZ, Vector3.UnitZ,
                30f, 0.16f, glass.Id, 0, 0, 0, terminalId),
        ]);
        physics.Update(1f / 60f);
        PhysicsGrid? terminalAfterHit = world.PhysicsGrids.Get(terminalId);
        Assert(terminalAfterHit is not null && world.PhysicsGrids.Count == bodyCountBeforeTerminalHit,
            "hitting a third-generation shard deleted the shard or its whole debris group");
        Assert(Vector3.Distance(terminalAfterHit!.LinearVelocity, velocityBeforeTerminalHit) > 0.05f,
            "terminal glass shard did not receive the impact impulse");
        Assert(terminalAfterHit.LinearVelocity.Length() <= 18.5f &&
               terminalAfterHit.AngularVelocity.Length() <= 24.1f,
            $"terminal hit launched the retained shard at an unbounded speed " +
            $"(linear={terminalAfterHit.LinearVelocity.Length():0.00}, " +
            $"angular={terminalAfterHit.AngularVelocity.Length():0.00})");
    }

    private static void TestPhysicsWaterRipple()
    {
        var world = new VoxelWorld(generate: false);
        for (int z = 0; z < 3; z++)
        for (int x = 0; x < 3; x++)
        {
            world.SetBlockId(x, 2, z, (ushort)VoxelMaterial.Water);
            if (x != 1 || z != 1)
                world.SetFluidAmount(x, 2, z, 10);
        }
        using var flow = new WaterFlowSimulation(world);
        int before = 0;
        for (int z = 0; z < 3; z++) for (int x = 0; x < 3; x++) before += world.GetFluidAmount(x, 2, z);
        int moved = flow.Disturb(new Vector3(1.5f, 2.8f, 1.5f), 5f);
        int after = 0;
        for (int z = 0; z < 3; z++) for (int x = 0; x < 3; x++) after += world.GetFluidAmount(x, 2, z);
        Assert(moved > 0, "water impact produced no local surface disturbance");
        Assert(before == after, $"water disturbance changed volume ({before}->{after})");
        Assert(world.GetFluidAmount(1, 2, 1) < VoxelWorld.FluidCapacity,
            "water disturbance did not lower the impact cell");
    }

    private static int CountBlock(VoxelWorld world, VoxelMaterial material) =>
        world.RawData.Count(block => block == (ushort)material);

    private static void TestMaterialLibrary()
    {
        var blocks = new BlockRegistry();
        Assert(blocks.Definitions.Count >= 196, $"expected at least 196 built-in definitions, found {blocks.Definitions.Count}");
        int categories = blocks.PlaceableDefinitions()
            .Select(block => block.Category.Split('/')[0].Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        Assert(categories >= 7, $"material library has only {categories} top-level categories");
        Assert(blocks.TryGet("rtxsandbox:diamond_glass", out BlockDefinition? diamondGlass) &&
               diamondGlass!.Transmission > 0.9f && diamondGlass.IndexOfRefraction > 2.4f,
            "optical material metadata did not load");
        Assert(blocks.TryGet("rtxsandbox:circuit_board", out BlockDefinition? circuit) &&
               circuit!.SurfacePattern == BlockSurfacePattern.Circuit && circuit.Tags.Contains("tech"),
            "technology category metadata did not load");
    }

    private static void TestProceduralMaterialAtlas()
    {
        var blocks = new BlockRegistry();
        var first = new ProceduralMaterialAtlas(blocks);
        var second = new ProceduralMaterialAtlas(blocks);
        Assert(ProceduralMaterialAtlas.TileSize == 16, "material tiles are not 16 x 16");
        Assert(blocks.TryGet("rtxsandbox:coarse_dirt", out BlockDefinition? dirt), "coarse dirt is missing");
        var dirtColors = new HashSet<string>();
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
        {
            Vector3 color = first.ReadColor(dirt!.Id, Rendering.MaterialFace.Top, x, y);
            dirtColors.Add($"{color.X:0.000}:{color.Y:0.000}:{color.Z:0.000}");
            Assert(Vector3.DistanceSquared(color, second.ReadColor(dirt.Id, Rendering.MaterialFace.Top, x, y)) < 0.000001f,
                "same block key baked a different texture");
        }
        Assert(dirtColors.Count >= 12, $"natural dirt texture has only {dirtColors.Count} distinct colors");

        Assert(blocks.TryGet("rtxsandbox:iron_block", out BlockDefinition? iron) &&
               string.Equals(iron!.TextureGenerator, "forged_iron_pixel", StringComparison.OrdinalIgnoreCase),
            "iron block is not wired to the hand-drawn voxel forged-iron texture");
        var ironColors = new HashSet<string>();
        float ironNeighborDelta = 0f;
        int ironNeighborCount = 0;
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
        {
            Vector3 color = first.ReadColor(iron!.Id, Rendering.MaterialFace.Side, x, y);
            ironColors.Add($"{color.X:0.000}:{color.Y:0.000}:{color.Z:0.000}");
            if (x + 1 < ProceduralMaterialAtlas.TileSize)
            {
                ironNeighborDelta += Vector3.Distance(color,
                    first.ReadColor(iron.Id, Rendering.MaterialFace.Side, x + 1, y));
                ironNeighborCount++;
            }
        }
        Assert(ironColors.Count is >= 5 and <= 7,
            $"iron texture is not a controlled voxel-art palette ({ironColors.Count} colors)");
        Assert(ironNeighborDelta / Math.Max(1, ironNeighborCount) < 0.16f,
            "iron texture still reads as decorrelated per-texel random noise");
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
            Assert(Vector3.DistanceSquared(
                    first.ReadColor(iron!.Id, Rendering.MaterialFace.Side, 0, y),
                    first.ReadColor(iron.Id, Rendering.MaterialFace.Side, ProceduralMaterialAtlas.TileSize - 1, y)) < 0.000001f,
                $"iron texture has a vertical tile seam on row {y}");
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
            Assert(Vector3.DistanceSquared(
                    first.ReadColor(iron!.Id, Rendering.MaterialFace.Side, x, 0),
                    first.ReadColor(iron.Id, Rendering.MaterialFace.Side, x, ProceduralMaterialAtlas.TileSize - 1)) < 0.000001f,
                $"iron texture has a horizontal tile seam on column {x}");

        Assert(blocks.TryGet("rtxsandbox:hazard_stripes", out BlockDefinition? hazard) &&
               string.Equals(hazard!.TextureGenerator, "hazard_stripes", StringComparison.OrdinalIgnoreCase),
            "hazard block is not wired to continuous diagonal stripes");
        Vector3 hazardYellow = first.ReadColor(hazard!.Id, Rendering.MaterialFace.Side, 1, 1);
        Vector3 hazardDark = first.ReadColor(hazard.Id, Rendering.MaterialFace.Side, 5, 1);
        Assert(hazardYellow.X > hazardDark.X * 4f && hazardYellow.Y > hazardDark.Y * 4f,
            "hazard texture does not contain distinct yellow and charcoal bands");
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < 8; x++)
            Assert(Vector3.DistanceSquared(
                    first.ReadColor(hazard.Id, Rendering.MaterialFace.Side, x, y),
                    first.ReadColor(hazard.Id, Rendering.MaterialFace.Side, x + 8, y)) < 0.000001f,
                "hazard stripes are not exactly periodic across voxel boundaries");

        Assert(blocks.TryGet("rtxsandbox:industrial_floor", out BlockDefinition? industrialFloor) &&
               string.Equals(industrialFloor!.TextureGenerator, "industrial_tread_plate", StringComparison.OrdinalIgnoreCase),
            "industrial floor is still using the generic tile/grid texture");
        var floorColors = new HashSet<string>();
        int raisedTreads = 0;
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
        {
            Vector3 floorColor = first.ReadColor(industrialFloor!.Id, Rendering.MaterialFace.Top, x, y);
            floorColors.Add($"{floorColor.X:0.000}:{floorColor.Y:0.000}:{floorColor.Z:0.000}");
            if (first.ReadRoughness(industrialFloor.Id, Rendering.MaterialFace.Top, x, y) < 0.28f)
                raisedTreads++;
            Assert(Vector3.DistanceSquared(floorColor,
                    first.ReadColor(industrialFloor.Id, Rendering.MaterialFace.Top, x & 7, y & 7)) < 0.000001f,
                "industrial tread plate is not exactly periodic across voxel boundaries");
        }
        Assert(floorColors.Count is >= 3 and <= 5 && raisedTreads == 16,
            $"industrial floor does not read as a controlled tread plate ({floorColors.Count} colors, {raisedTreads} treads)");

        Assert(blocks.TryGet("rtxsandbox:grass", out BlockDefinition? grass), "grass block is missing");
        Vector3 sideBottom = Vector3.Zero;
        Vector3 sideTop = Vector3.Zero;
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
        {
            Vector3 color = first.ReadColor(grass!.Id, Rendering.MaterialFace.Side, x, y);
            if (y < ProceduralMaterialAtlas.TileSize / 4) sideBottom += color;
            if (y >= ProceduralMaterialAtlas.TileSize * 3 / 4) sideTop += color;
        }
        Assert(sideTop.Y > sideBottom.Y + 4f,
            "grass side texture is vertically inverted: the green cap is not on the top edge");
        MaterialTileUv grassSideUv = first.GetTileUv(grass!.Id, Rendering.MaterialFace.Side);
        var previewUv = SandboxGame.ResolveMaterialPreviewUvs(grassSideUv, flipX: false, flipY: true);
        Assert(previewUv.A.Y > previewUv.C.Y && previewUv.B.Y > previewUv.D.Y,
            "inventory cube preview does not sample the top of side textures at the top of the icon");

        string[] surfaceGlassKeys =
        {
            "rtxsandbox:frosted_glass",
            "rtxsandbox:mosaic_glass",
            "rtxsandbox:acrylic_glass",
            "rtxsandbox:ribbed_glass",
            "rtxsandbox:etched_glass",
        };
        foreach (string key in surfaceGlassKeys)
            Assert(blocks.TryGet(key, out BlockDefinition? glass) && glass!.IsTransparent &&
                   glass.Transmission > 0f && !string.Equals(glass.TextureGenerator, "auto", StringComparison.OrdinalIgnoreCase),
                $"surface glass '{key}' is missing explicit optical material data");
        blocks.TryGet("rtxsandbox:mosaic_glass", out BlockDefinition? mosaicGlass);
        var mosaicColors = new HashSet<string>();
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
        {
            Vector3 color = first.ReadColor(mosaicGlass!.Id, Rendering.MaterialFace.Side, x, y);
            mosaicColors.Add($"{color.X:0.00}:{color.Y:0.00}:{color.Z:0.00}");
        }
        Assert(mosaicColors.Count >= 12, $"mosaic glass has only {mosaicColors.Count} visible texture colors");
        blocks.TryGet("rtxsandbox:frosted_glass", out BlockDefinition? frostedGlass);
        float minimumFrostRoughness = 1f;
        float maximumFrostRoughness = 0f;
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
        {
            float roughness = first.ReadRoughness(frostedGlass!.Id, Rendering.MaterialFace.Side, x, y);
            minimumFrostRoughness = Math.Min(minimumFrostRoughness, roughness);
            maximumFrostRoughness = Math.Max(maximumFrostRoughness, roughness);
        }
        Assert(minimumFrostRoughness >= 0.43f && maximumFrostRoughness <= 0.60f &&
               maximumFrostRoughness - minimumFrostRoughness > 0.035f,
            $"frosted glass roughness is unrealistic ({minimumFrostRoughness:F3}-{maximumFrostRoughness:F3})");

        Assert(!blocks.TryGet("rtxsandbox:matte_glass", out _),
            "removed matte glass is still registered");
        int frostedChangedNeighbors = 0;
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x + 1 < ProceduralMaterialAtlas.TileSize; x++)
        {
            if (Vector3.DistanceSquared(
                    first.ReadColor(frostedGlass!.Id, Rendering.MaterialFace.Side, x, y),
                    first.ReadColor(frostedGlass.Id, Rendering.MaterialFace.Side, x + 1, y)) > 0.00001f)
                frostedChangedNeighbors++;
        }
        Assert(frostedChangedNeighbors is >= 40 and <= 225,
            $"frosted glass variation is either flat or pixel-noisy ({frostedChangedNeighbors} changed neighbors)");

        blocks.TryGet("rtxsandbox:etched_glass", out BlockDefinition? etchedGlass);
        float clearEtchedTexels = 0f;
        float roughEtchedTexels = 0f;
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
        {
            float etchedRoughness = first.ReadRoughness(etchedGlass!.Id, Rendering.MaterialFace.Side, x, y);
            if (etchedRoughness < 0.18f) clearEtchedTexels++;
            if (etchedRoughness > 0.55f) roughEtchedTexels++;
        }
        Assert(clearEtchedTexels > 20 && roughEtchedTexels > 20,
            "etched glass does not contain both clear base glass and rough etched grooves");

        Assert(blocks.TryGet("rtxsandbox:white_concrete", out BlockDefinition? concrete), "white concrete is missing");
        Vector3 reference = first.ReadColor(concrete!.Id, Rendering.MaterialFace.Top, 0, 0);
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x < ProceduralMaterialAtlas.TileSize; x++)
        {
            Assert(Vector3.DistanceSquared(reference, first.ReadColor(concrete.Id, Rendering.MaterialFace.Top, x, y)) < 0.000001f,
                "pure concrete material received unwanted random color noise");
        }
    }

    private static void TestCreativeHotbar()
    {
        var blocks = new BlockRegistry();
        var hotbar = new CreativeHotbar(blocks, selectedSlot: 3, legacySelectedKey: "rtxsandbox:stone");
        Assert(hotbar.SelectedSlot == 3 && hotbar.SelectedBlock.Key == "rtxsandbox:stone", "legacy selected material was not migrated");
        Assert(blocks.TryGet("rtxsandbox:granite", out BlockDefinition? granite), "granite is missing");
        Assert(hotbar.Assign(3, granite!.Id), "creative slot assignment failed");
        string[] snapshot = hotbar.Snapshot();
        Assert(snapshot[3] == "rtxsandbox:granite", "hotbar did not persist a namespaced key");

        var restored = new CreativeHotbar(blocks, snapshot, selectedSlot: 3);
        Assert(restored.SelectedBlock.Key == "rtxsandbox:granite", "hotbar key did not resolve after restore");
        restored.Cycle(1);
        Assert(restored.SelectedSlot == 4, "hotbar cycling did not select the next slot");
    }

    private static void TestAnalyticSphereCreativePlacement()
    {
        var blocks = new BlockRegistry();
        Assert(blocks.TryGet("rtxsandbox:iron_sphere", out BlockDefinition? ironSphere),
            "iron sphere creative item is missing");
        Assert(blocks.TryGet("rtxsandbox:mirror_sphere", out BlockDefinition? mirrorSphere),
            "mirror sphere creative item is missing");
        Assert(blocks.TryGet("rtxsandbox:black_hole", out BlockDefinition? blackHole),
            "black hole creative item is missing");
        Assert(AnalyticSpherePlacement.IsSphereItem(ironSphere!) &&
               AnalyticSpherePlacement.IsSphereItem(mirrorSphere!) &&
               AnalyticSpherePlacement.IsSphereItem(blackHole!) &&
               AnalyticSpherePlacement.IsBlackHole(blackHole!),
            "sphere inventory items do not use analytic placement");
        Assert(CreativeInventoryModel.Categories(blocks.PlaceableDefinitions()).Contains("Spheres"),
            "creative inventory has no sphere category");
        Assert(blocks.TryGet("rtxsandbox:energy_projector", out BlockDefinition? projector) &&
               CreativeInventoryModel.CategoryOf(projector!) == "Tools" &&
               CreativeInventoryModel.Categories(blocks.PlaceableDefinitions()).Contains("Tools"),
            "energy projector is not discoverable in the creative Tools tab");

        var inventory = new CreativeInventoryModel { Category = "Spheres" };
        BlockDefinition[] sphereItems = inventory.Filter(
            blocks.PlaceableDefinitions(), new HashSet<string>(), block => block.DisplayName);
        Assert(sphereItems.Select(block => block.Key).SequenceEqual(
                new[] { "rtxsandbox:black_hole", "rtxsandbox:iron_sphere", "rtxsandbox:mirror_sphere" }),
            "sphere category does not contain the three direct-placement items");

        var world = new VoxelWorld(generate: false, registry: blocks);
        var target = new VoxelHit(0, 0, 2, -Vector3.UnitZ, 2f, VoxelMaterial.Stone);
        AnalyticSphere? placed = AnalyticSpherePlacement.TryPlace(
            world.AnalyticSpheres,
            ironSphere!,
            Vector3.Zero,
            Vector3.UnitZ,
            target,
            null);
        Assert(placed is not null && placed.MaterialId == ironSphere!.Id,
            "direct placement did not create an analytic sphere with the selected material");
        Assert(MathF.Abs(placed!.Radius - AnalyticSpherePlacement.Radius(ironSphere!)) < 0.0001f,
            "direct placement ignored the inventory item's sphere radius");
        Assert(world.GetBlockId(0, 0, 1) == 0,
            "direct sphere placement accidentally created a voxel block");

        AnalyticSphere? placedBlackHole = AnalyticSpherePlacement.TryPlace(
            world.AnalyticSpheres,
            blackHole!,
            new Vector3(4f, 0f, 0f),
            Vector3.UnitZ,
            target,
            null);
        Assert(placedBlackHole is not null && placedBlackHole.IsKinematic && placedBlackHole.IsSleeping &&
               placedBlackHole.LinearVelocity == Vector3.Zero && placedBlackHole.AngularVelocity == Vector3.Zero,
            "black hole placement did not create a stationary analytic lens");

        var tooClose = new VoxelHit(0, 0, 1, -Vector3.UnitZ, 1f, VoxelMaterial.Stone);
        Assert(AnalyticSpherePlacement.TryPlace(
                world.AnalyticSpheres, mirrorSphere!, Vector3.Zero, Vector3.UnitZ, tooClose, null) is null,
            "direct sphere placement bypassed the 1.1-block eye-distance rule");

        string saveRoot = Path.Combine(Path.GetTempPath(), $"rtxsandbox-black-hole-{Guid.NewGuid():N}");
        try
        {
            var saves = new WorldSaveService(blocks, worldName: "black-hole", saveRoot: saveRoot);
            saves.Save(world, Vector3.Zero, 14f);
            var restoredWorld = new VoxelWorld(generate: false, registry: blocks);
            Assert(saves.TryLoad(restoredWorld, out _, out string? error),
                error ?? "black-hole world did not load");
            AnalyticSphere restoredBlackHole = restoredWorld.AnalyticSpheres.Spheres.Single(
                sphere => sphere.MaterialId == blackHole!.Id);
            Assert(restoredBlackHole.IsKinematic && restoredBlackHole.IsSleeping &&
                   restoredBlackHole.LinearVelocity == Vector3.Zero &&
                   restoredBlackHole.AngularVelocity == Vector3.Zero,
                "saved black hole resumed as a falling or moving rigid body");
        }
        finally
        {
            if (Directory.Exists(saveRoot)) Directory.Delete(saveRoot, recursive: true);
        }
    }

    private static void TestCreativePaletteOrdering()
    {
        var blocks = new BlockRegistry();
        BlockDefinition[] ordered = CreativePaletteOrdering.Order(blocks.PlaceableDefinitions(), useSimpleGroups: true);
        int[] glassIndices = ordered
            .Select((block, index) => (block, index))
            .Where(entry => entry.block.Key.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                            entry.block.Tags.Any(tag => tag.Equals("glass", StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.index)
            .ToArray();
        Assert(glassIndices.Length >= 15, "creative palette did not find the complete glass family");
        Assert(glassIndices[^1] - glassIndices[0] + 1 == glassIndices.Length,
            "glass materials are split by unrelated inventory entries");

        string[] expectedGlassOrder =
        {
            "rtxsandbox:glass", "rtxsandbox:clear_glass", "rtxsandbox:red_glass",
            "rtxsandbox:yellow_glass", "rtxsandbox:green_glass", "rtxsandbox:blue_glass",
            "rtxsandbox:purple_glass", "rtxsandbox:smoked_glass", "rtxsandbox:diamond_glass",
            "rtxsandbox:prismatic_glass", "rtxsandbox:frosted_glass",
            "rtxsandbox:mosaic_glass", "rtxsandbox:acrylic_glass", "rtxsandbox:ribbed_glass",
            "rtxsandbox:etched_glass",
        };
        string[] actualGlassOrder = glassIndices.Select(index => ordered[index].Key).ToArray();
        Assert(expectedGlassOrder.SequenceEqual(actualGlassOrder),
            $"glass inventory order is unstable: {string.Join(", ", actualGlassOrder)}");

        BlockDefinition[] categoryOrder = CreativePaletteOrdering.Order(blocks.PlaceableDefinitions(), useSimpleGroups: false);
        Assert(categoryOrder.Select(block => block.Id).SequenceEqual(categoryOrder.Select(block => block.Id).OrderBy(id => id)),
            "selected-category ordering no longer preserves the registry order");
    }

    private static void TestHighBlockIdPersistence()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"rtxsandbox-16bit-{Guid.NewGuid():N}.rtxw");
        try
        {
            var blocks = new BlockRegistry();
            ushort highId = blocks.Register(new BlockDefinition(
                "test:high_id",
                "High ID",
                new Vector3(0.2f, 0.8f, 1f),
                id: 1024));
            Assert(highId == 1024, "registry truncated a 16-bit explicit id");
            var world = new VoxelWorld(generate: false, registry: blocks);
            Assert(world.SetBlockId(7, 8, 9, highId), "world rejected a block id above 255");
            var saves = new WorldSaveService(blocks, savePath);
            saves.Save(world, new Vector3(2f), 12f);

            var loaded = new VoxelWorld(generate: false, registry: blocks);
            Assert(saves.TryLoad(loaded, out _, out string? error), error ?? "16-bit world did not load");
            Assert(loaded.GetBlockId(7, 8, 9) == 1024, "16-bit block id was truncated during save/load");
        }
        finally
        {
            File.Delete(savePath);
            File.Delete(savePath + ".bak");
            File.Delete(savePath + ".tmp");
        }
    }

    private static void TestLegacyByteWorldMigration()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"rtxsandbox-v4-{Guid.NewGuid():N}.rtxw");
        try
        {
            using (FileStream stream = File.Create(savePath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x57585452);
                writer.Write(4);
                writer.Write(VoxelWorld.Width);
                const int legacyHeight = 32;
                writer.Write(legacyHeight);
                writer.Write(VoxelWorld.Depth);
                writer.Write(15f);
                writer.Write(3.5f);
                writer.Write(10f);
                writer.Write(3.5f);
                writer.Write(20f);
                writer.Write(10f);
                writer.Write(20f);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(false);
                writer.Write(WorldPreset.Natural.ToString());
                writer.Write(BiomePreset.Auto.ToString());
                writer.Write(4455);
                writer.Write(6);
                writer.Write("rtxsandbox:grass");
                writer.Write("rtxsandbox:stone");
                writer.Write(true);
                writer.Write(true);
                writer.Write(2);
                writer.Write((byte)0);
                writer.Write("rtxsandbox:air");
                writer.Write((byte)VoxelMaterial.Stone);
                writer.Write("rtxsandbox:stone");
                int voxelCount = VoxelWorld.Width * legacyHeight * VoxelWorld.Depth;
                writer.Write(voxelCount);
                var legacy = new byte[voxelCount];
                int index = 4 + VoxelWorld.Width * (5 + legacyHeight * 6);
                legacy[index] = (byte)VoxelMaterial.Stone;
                writer.Write(legacy);
            }

            var blocks = new BlockRegistry();
            var world = new VoxelWorld(generate: false, registry: blocks);
            var saves = new WorldSaveService(blocks, savePath);
            Assert(saves.TryLoad(world, out SavedWorldState state, out string? error), error ?? "legacy v4 world did not load");
            Assert(world.Get(4, 5, 6) == VoxelMaterial.Stone, "legacy byte voxel was not widened/remapped");
            Assert(state.GenerationSettings.Seed == 4455, "legacy generation metadata was lost");
        }
        finally
        {
            File.Delete(savePath);
            File.Delete(savePath + ".bak");
            File.Delete(savePath + ".tmp");
        }
    }

    private static void TestMicroVoxelGridPromotion()
    {
        var micro = new MicroVoxelContainer();
        micro.Fill((ushort)VoxelMaterial.Stone);
        micro.PaintCell(6, 0, 0, 0, (ushort)VoxelMaterial.Dirt);
        micro.PaintCell(16, 15, 15, 15, (ushort)VoxelMaterial.Glass);
        Assert(micro.Resolution == 48, $"mixed 1/6 and 1/16 grids promoted to {micro.Resolution}, not 1/48");
        Assert(micro.Boxes.Count < 32, $"sparse edit expanded into {micro.Boxes.Count} boxes");

        long totalVolume = micro.Boxes.Sum(box =>
            (long)(box.MaxX - box.MinX) * (box.MaxY - box.MinY) * (box.MaxZ - box.MinZ));
        long fullVolume = 48L * 48L * 48L;
        Assert(totalVolume == fullVolume, "painting materials changed the occupied micro volume");
    }

    private static void TestMicroVoxelRaycast()
    {
        var world = new VoxelWorld(generate: false);
        var micro = new MicroVoxelContainer(2);
        micro.Paint(0, 0, 0, 1, 1, 1, (ushort)VoxelMaterial.Stone);
        world.SetMicroVoxels(3, 2, 2, micro);

        MicroVoxelHit? hit = MicroVoxelRaycast.Cast(
            world,
            new Vector3(1.5f, 2.25f, 2.25f),
            Vector3.UnitX,
            8f);
        Assert(hit.HasValue, "expected a micro voxel hit");
        Assert(MathF.Abs(hit!.Value.Distance - 1.5f) < 0.001f, $"micro hit distance was {hit.Value.Distance}");
        Assert(hit.Value.Normal == -Vector3.UnitX && hit.Value.MaterialId == (ushort)VoxelMaterial.Stone,
            "micro hit returned the wrong face or material");
        MicroVoxelHit? reverseHit = MicroVoxelRaycast.Cast(
            world,
            new Vector3(4.5f, 2.25f, 2.25f),
            -Vector3.UnitX,
            8f);
        Assert(reverseHit.HasValue && reverseHit.Value.Normal == Vector3.UnitX,
            "reverse micro ray returned the wrong outward normal");
    }

    private static void TestMicroVoxelChangeEvents()
    {
        var world = new VoxelWorld(generate: false);
        var changedCells = new List<(int X, int Y, int Z)>();
        int reloads = 0;
        int aggregateChanges = 0;
        world.MicroVoxelCellChanged += (x, y, z) => changedCells.Add((x, y, z));
        world.MicroVoxelsReloaded += () => reloads++;
        world.MicroVoxelsChanged += () => aggregateChanges++;

        var micro = new MicroVoxelContainer(2);
        micro.PaintCell(2, 0, 0, 0, (ushort)VoxelMaterial.Stone);
        Assert(world.SetMicroVoxels(15, 2, 15, micro), "micro cell setup failed");
        Assert(changedCells.SequenceEqual(new[] { (15, 2, 15) }) && reloads == 0 && aggregateChanges == 1,
            "a local precision edit did not publish its exact cell");

        Assert(world.SetBlockId(15, 2, 15, (ushort)VoxelMaterial.Glass),
            "regular block replacement did not remove precision geometry");
        Assert(changedCells.Count == 2 && changedCells[1] == (15, 2, 15) && aggregateChanges == 2,
            "removing precision geometry through a full block missed its exact invalidation");

        world.LoadMicroVoxels(Array.Empty<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)>());
        Assert(reloads == 1 && changedCells.Count == 2 && aggregateChanges == 3,
            "bulk precision loading was confused with a local edit");
    }

    private static void TestMicroVoxelCollision()
    {
        var world = new VoxelWorld(generate: false);
        var slab = new MicroVoxelContainer(2);
        slab.Paint(0, 0, 0, 2, 1, 2, (ushort)VoxelMaterial.Stone);
        world.SetMicroVoxels(3, 0, 3, slab);
        var player = new PlayerController(world, new Vector3(3.5f, 3f, 3.5f));
        for (int index = 0; index < 240; index++)
            player.Update(1f / 120f, Vector2.Zero, -Vector3.UnitZ, false, false);
        Assert(MathF.Abs(player.Position.Y - 0.5f) < 0.001f, $"player feet stopped at {player.Position.Y}, not slab height 0.5");
        Assert(player.IsGrounded, "player did not ground on micro voxel geometry");
    }

    private static void TestMicroVoxelEditingAndPersistence()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"rtxsandbox-micro-{Guid.NewGuid():N}.rtxw");
        try
        {
            var originalBlocks = new BlockRegistry();
            ushort originalMaterial = originalBlocks.Register(new BlockDefinition(
                "test:micro_material", "Micro Material", new Vector3(0.3f, 0.8f, 0.9f)));
            var world = new VoxelWorld(generate: false, registry: originalBlocks);
            world.SetBlockId(3, 2, 2, originalMaterial);
            var tools = new MicroBuildSystem(world, originalBlocks);
            tools.SetGridDivisor(6);
            var hit = new PrecisionHit(
                3, 2, 2, new Vector3(3f, 2.5f, 2.5f), -Vector3.UnitX, 1.5f, originalMaterial, false);
            Assert(tools.Erase(hit), "precision erase failed");
            Assert(world.TryGetMicroVoxels(3, 2, 2, out MicroVoxelContainer? edited) && edited!.Resolution == 6,
                "full voxel was not fractured onto the selected grid");
            Assert(tools.Undo() && world.GetBlockId(3, 2, 2) == originalMaterial,
                "precision undo did not restore the full voxel");
            Assert(tools.Redo() && world.TryGetMicroVoxels(3, 2, 2, out _),
                "precision redo did not restore sparse geometry");

            new WorldSaveService(originalBlocks, savePath).Save(world, new Vector3(2f), 12f);
            var reorderedBlocks = new BlockRegistry();
            reorderedBlocks.Register(new BlockDefinition("test:before_micro", "Before", Vector3.One));
            ushort remappedMaterial = reorderedBlocks.Register(new BlockDefinition(
                "test:micro_material", "Micro Material", new Vector3(0.3f, 0.8f, 0.9f)));
            Assert(remappedMaterial != originalMaterial, "test registry did not reorder the material id");
            var loaded = new VoxelWorld(generate: false, registry: reorderedBlocks);
            var saves = new WorldSaveService(reorderedBlocks, savePath);
            Assert(saves.TryLoad(loaded, out _, out string? error), error ?? "micro voxel save did not load");
            Assert(loaded.TryGetMicroVoxels(3, 2, 2, out MicroVoxelContainer? restored),
                "micro voxel cell was not restored");
            Assert(restored!.Resolution == 6 && restored.Boxes.All(box => box.MaterialId == remappedMaterial),
                "micro voxel resolution or namespaced material remapping was lost");
        }
        finally
        {
            File.Delete(savePath);
            File.Delete(savePath + ".bak");
            File.Delete(savePath + ".tmp");
        }
    }


    private static void TestPrimaryDestroySemantics()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        var tools = new BuildToolSystem(world, blocks, new BuildToolRegistry())
        {
            ToolMode = EditorToolMode.Place,
            SelectedBlockId = (ushort)VoxelMaterial.Glass,
        };
        world.Set(3, 2, 2, VoxelMaterial.Stone);
        var hit = new VoxelHit(3, 2, 2, -Vector3.UnitX, 1.5f, VoxelMaterial.Stone);
        Assert(tools.UsePrimary(hit), "primary click made no change");
        Assert(world.Get(3, 2, 2) == VoxelMaterial.Air && world.Get(2, 2, 2) == VoxelMaterial.Air,
            "primary click placed a voxel instead of destroying the target");
        Assert(tools.QuickPlace(new VoxelHit(4, 2, 2, -Vector3.UnitX, 1.5f, VoxelMaterial.Stone)),
            "explicit secondary placement failed");
        Assert(world.Get(3, 2, 2) == VoxelMaterial.Glass,
            "secondary placement did not use the selected material");
        tools.SelectedBlockId = 0;
        Assert(!tools.QuickPlace(new VoxelHit(4, 2, 2, -Vector3.UnitX, 1.5f, VoxelMaterial.Stone)) &&
            world.Get(3, 2, 2) == VoxelMaterial.Glass, "empty hotbar erased the placement target");
    }

    private static void TestUiMouseCapturePolicy()
    {
        Assert(!SandboxGame.ShouldCaptureMouse(requested: true, interfaceVisible: true),
            "pause-to-inventory transition recaptured and hid the mouse");
        Assert(!SandboxGame.ShouldCaptureMouse(requested: false, interfaceVisible: false),
            "an explicit release was ignored");
        Assert(SandboxGame.ShouldCaptureMouse(requested: true, interfaceVisible: false),
            "bare gameplay could not restore mouse capture");
    }

    private static void TestImeOwnershipPolicy()
    {
        Assert(!ImGuiController.ShouldEnableIme(interfaceVisible: false, wantsTextEntry: false),
            "gameplay left the Windows IME associated");
        Assert(!ImGuiController.ShouldEnableIme(interfaceVisible: true, wantsTextEntry: false),
            "ordinary menu navigation allowed IME to steal action keys");
        Assert(ImGuiController.ShouldEnableIme(interfaceVisible: true, wantsTextEntry: true),
            "an active text field could not enable IME composition");
    }

    private static void TestRenderBackendCatalog()
    {
        RenderBackendKind[] implemented = RenderBackendCatalog.Implemented
            .Select(descriptor => descriptor.Kind)
            .ToArray();
        RenderBackendKind[] requiredRaster =
        {
            RenderBackendKind.DeferredLighting,
            RenderBackendKind.HybridPbrRaster,
            RenderBackendKind.PbrForward,
            RenderBackendKind.Classic,
        };
        Assert(requiredRaster.All(implemented.Contains),
            "implemented renderer list lost an existing raster backend");
        Assert(RenderBackendCatalog.IsImplemented(RenderBackendKind.SoftwareRayTracing) ==
               !OperatingSystem.IsMacOS(),
            "compute path tracing availability disagrees with the OpenGL 4.3 platform requirement");
        Assert(RenderBackendCatalog.IsImplemented(RenderBackendKind.HardwareRayTracing) ==
               DxrHardwareRenderer.IsSupported(out _),
            "hardware ray tracing catalog availability disagrees with the DXR capability probe");
        Assert(RenderBackendCatalog.IsImplemented(RenderBackendKind.DeferredLighting),
            "completed GBuffer deferred mode is not selectable");
        RenderBackendKind[] catalogOrder = RenderBackendCatalog.All.Select(descriptor => descriptor.Kind).ToArray();
        int deferredIndex = Array.IndexOf(catalogOrder, RenderBackendKind.DeferredLighting);
        Assert(deferredIndex >= 0 && deferredIndex + 2 < catalogOrder.Length &&
               catalogOrder[deferredIndex + 1] == RenderBackendKind.HybridPbrRaster &&
               catalogOrder[deferredIndex + 2] == RenderBackendKind.SoftwareRayTracing,
            "Hybrid PBR Raster is not positioned between Deferred and Compute PT");
        Assert(RenderBackendCatalog.Implemented.Where(descriptor =>
                    descriptor.Kind != RenderBackendKind.HardwareRayTracing).All(descriptor =>
                descriptor.Capabilities.HasFlag(RenderBackendCapabilities.MicroVoxels)),
            "a selectable renderer cannot display precision-built geometry");
    }

    private static void TestRendererFailureFallback()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        TestRenderBackend? classic = null;
        using var host = new RenderBackendHost(
            world,
            blocks,
            320,
            180,
            1f,
            RenderBackendKind.DeferredLighting,
            kind =>
            {
                if (kind is RenderBackendKind.DeferredLighting or RenderBackendKind.PbrForward)
                    throw new InvalidOperationException("simulated renderer initialization failure");
                var backend = new TestRenderBackend(kind);
                if (kind == RenderBackendKind.Classic) classic = backend;
                return backend;
            });

        Assert(host.Kind == RenderBackendKind.Classic && host.Status.Contains("安全回退", StringComparison.Ordinal),
            "a failed startup renderer did not fall back to Classic with a clear reason");
        Assert(!host.Switch((RenderBackendKind)999) && host.Kind == RenderBackendKind.Classic,
            "an unknown renderer ID threw or replaced the active renderer");
        Assert(!host.Switch(RenderBackendKind.PbrForward) && host.Kind == RenderBackendKind.Classic &&
               classic is { Disposed: false },
            "a failed runtime switch disposed or replaced the working renderer");
    }

    private static void TestChunkCoordinates()
    {
        Assert(ChunkCoordinate.FromVoxel(0, 0) == new ChunkCoordinate(0, 0) &&
               ChunkCoordinate.FromVoxel(15, 15) == new ChunkCoordinate(0, 0) &&
               ChunkCoordinate.FromVoxel(16, 16) == new ChunkCoordinate(1, 1),
            "positive chunk boundary conversion was incorrect");
        Assert(ChunkCoordinate.FromVoxel(-1, -1) == new ChunkCoordinate(-1, -1) &&
               ChunkCoordinate.FromVoxel(-16, -16) == new ChunkCoordinate(-1, -1) &&
               ChunkCoordinate.FromVoxel(-17, -17) == new ChunkCoordinate(-2, -2),
            "negative coordinates did not use floor division");
        Assert(RegionCoordinate.FromChunk(new ChunkCoordinate(31, 31)) == new RegionCoordinate(0, 0) &&
               RegionCoordinate.FromChunk(new ChunkCoordinate(32, 32)) == new RegionCoordinate(1, 1) &&
               RegionCoordinate.FromChunk(new ChunkCoordinate(-1, -1)) == new RegionCoordinate(-1, -1) &&
               RegionCoordinate.FromChunk(new ChunkCoordinate(-32, -32)) == new RegionCoordinate(-1, -1) &&
               RegionCoordinate.FromChunk(new ChunkCoordinate(-33, -33)) == new RegionCoordinate(-2, -2),
            "32x32 region grouping failed at a positive or negative boundary");

        ChunkCoordinate[] affected = ChunkCoordinate.MeshDependenciesForVoxel(
                15, 15, VoxelWorld.Width, VoxelWorld.Depth)
            .OrderBy(chunk => chunk.X)
            .ThenBy(chunk => chunk.Z)
            .ToArray();
        Assert(affected.SequenceEqual(new[]
        {
            new ChunkCoordinate(0, 0),
            new ChunkCoordinate(0, 1),
            new ChunkCoordinate(1, 0),
        }), "a boundary edit did not invalidate the owner and two face-neighbor chunks");

        ChunkCoordinate[] interior = ChunkCoordinate.MeshDependenciesForVoxel(
                8, 8, VoxelWorld.Width, VoxelWorld.Depth)
            .ToArray();
        Assert(interior.SequenceEqual(new[] { new ChunkCoordinate(0, 0) }),
            "an interior edit invalidated unrelated chunks");
    }

    private static void TestChunkOwnedVoxelStorage()
    {
        var world = new VoxelWorld(generate: false);
        Assert(world.LoadedChunkCount == 0, "an empty world eagerly allocated voxel chunks");

        world.Set(15, 2, 15, VoxelMaterial.Stone);
        world.Set(16, 2, 15, VoxelMaterial.Glass);
        Assert(world.LoadedChunkCount == 2,
            "voxels across a chunk boundary were not owned by separate chunks");

        world.Set(15, 2, 15, VoxelMaterial.Air);
        Assert(world.LoadedChunkCount == 1,
            "removing the final voxel did not release its empty chunk");

        var micro = new MicroVoxelContainer(4);
        micro.PaintCell(4, 0, 0, 0, (ushort)VoxelMaterial.Marble);
        world.SetMicroVoxels(32, 3, 15, micro);
        Assert(world.LoadedChunkCount == 2 && world.MicroVoxelContainerCount == 1,
            "precision geometry did not participate in chunk ownership");
        world.SetBlockId(32, 3, 15, 0);
        Assert(world.LoadedChunkCount == 1 && world.MicroVoxelContainerCount == 0,
            "removing the final precision volume did not release its empty chunk");

        world.Set(16, 2, 15, VoxelMaterial.Air);
        Assert(world.LoadedChunkCount == 0, "the world retained an empty final chunk");
    }

    private static void TestChunkLinearSnapshot()
    {
        var source = new ushort[VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth];
        static int Index(int x, int y, int z) =>
            x + VoxelWorld.Width * (y + VoxelWorld.Height * z);
        source[Index(0, 0, 0)] = (ushort)VoxelMaterial.Bedrock;
        source[Index(15, 7, 15)] = (ushort)VoxelMaterial.Stone;
        source[Index(16, 7, 15)] = (ushort)VoxelMaterial.Glass;
        source[Index(63, 31, 63)] = (ushort)VoxelMaterial.Glow;

        var world = new VoxelWorld(generate: false);
        world.LoadRawData(source);
        Assert(world.LoadedChunkCount == 3,
            $"linear load created {world.LoadedChunkCount} chunks instead of three occupied chunks");
        Assert(world.RawData.SequenceEqual(source),
            "chunk storage changed the established x/y/z linear save layout");

        world.LoadRawData(new ushort[source.Length]);
        Assert(world.LoadedChunkCount == 0 && world.RawData.All(block => block == 0),
            "loading an empty snapshot retained stale chunk data");
    }

    private static void TestChunkOccupiedBounds()
    {
        var world = new VoxelWorld(generate: false);
        var chunk = new ChunkCoordinate(0, 0);
        world.Set(2, -40, 2, VoxelMaterial.Stone);
        world.Set(2, 420, 2, VoxelMaterial.Glass);
        Assert(world.TryGetChunkVerticalBounds(chunk, out int minimum, out int maximum) &&
               minimum == -40 && maximum == 421,
            $"initial occupied bounds were {minimum}..{maximum}");

        world.Set(2, 420, 2, VoxelMaterial.Air);
        Assert(world.TryGetChunkVerticalBounds(chunk, out minimum, out maximum) &&
               minimum == -40 && maximum == -39,
            $"removing the top boundary left bounds at {minimum}..{maximum}");

        var micro = new MicroVoxelContainer(4);
        micro.PaintCell(4, 0, 0, 0, (ushort)VoxelMaterial.Marble);
        world.SetMicroVoxels(3, 300, 3, micro);
        world.Set(2, -40, 2, VoxelMaterial.Air);
        Assert(world.TryGetChunkVerticalBounds(chunk, out minimum, out maximum) &&
               minimum == 300 && maximum == 301,
            "regular-boundary removal discarded or hid the remaining precision cell");
    }

    private static void TestWholeCellMicroDestruction()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        var micro = new MicroVoxelContainer(16);
        micro.PaintCell(16, 2, 3, 4, (ushort)VoxelMaterial.Stone);
        micro.PaintCell(16, 10, 11, 12, (ushort)VoxelMaterial.Glass);
        world.SetMicroVoxels(3, 2, 2, micro);

        var tools = new BuildToolSystem(world, blocks, new BuildToolRegistry());
        var wholeCellHit = new VoxelHit(3, 2, 2, -Vector3.UnitX, 1.5f, VoxelMaterial.Stone);
        Assert(tools.UsePrimary(wholeCellHit), "normal primary destruction made no change");
        Assert(!world.TryGetMicroVoxels(3, 2, 2, out _) && world.GetBlockId(3, 2, 2) == 0,
            "normal mode left precision fragments behind");
    }

    private static void TestPhysicsGridPersistence()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"rtxsandbox-grid-{Guid.NewGuid():N}.rtxw");
        try
        {
            var blocks = new BlockRegistry();
            var world = new VoxelWorld(generate: false, registry: blocks);
            PhysicsGrid grid = world.PhysicsGrids.Create(
                new[]
                {
                    new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Brick),
                    new PhysicsGridElement(1, 0, 0, Vector3.UnitX, new Vector3(2f, 1f, 1f), (ushort)VoxelMaterial.Glass),
                },
                new Vector3(8f, 12f, 9f));
            Quaternion orientation = Quaternion.CreateFromYawPitchRoll(0.4f, 0.2f, -0.1f);
            grid.SetTransformAndVelocity(
                new Vector3(9f, 13f, 10f), orientation,
                new Vector3(1f, -2f, 0.5f), new Vector3(0.1f, 0.2f, 0.3f));
            Vector3[] shardCorners =
            [
                new(1f, 0f, 0f), new(2f, 0f, 0f), new(2f, 1f, 0f), new(2f, 1f, 0f),
                new(1f, 0f, 1f), new(2f, 0f, 1f), new(2f, 1f, 1f), new(2f, 1f, 1f),
            ];
            grid.SetSmoothSpheres(
            [
                new PhysicsGridSmoothSphere(new Vector3(1.75f, 0.5f, 0.5f), 0.7f,
                    (ushort)VoxelMaterial.Glass, 1, 0, 0) { ExplicitCorners = shardCorners },
            ]);
            PhysicsGrid staticDent = world.PhysicsGrids.Create(
                [new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Metal)],
                new Vector3(4f, 5f, 6f));
            staticDent.FreezeAsStaticDeformation();
            PhysicsGrid pinned = world.PhysicsGrids.Create(
                [new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Brick)],
                new Vector3(14f, 9f, 7f));
            pinned.IsPhysicsGunFrozen = true;
            pinned.IsKinematicController = true;
            pinned.IsSleeping = true;

            var saves = new WorldSaveService(blocks, savePath);
            saves.Save(world, new Vector3(2f), 8f);
            var loaded = new VoxelWorld(generate: false, registry: blocks);
            Assert(saves.TryLoad(loaded, out _, out string? error), error ?? "grid world did not load");
            Assert(loaded.PhysicsGrids.Count == 3, "physics grid count was not restored");
            PhysicsGrid restored = loaded.PhysicsGrids.Grids[0];
            Assert(restored.Elements.Count == 2 && restored.Elements[1].MaterialId == (ushort)VoxelMaterial.Glass,
                "physics grid elements or materials were not restored");
            Assert(Vector3.Distance(restored.Position, grid.Position) < 0.001f &&
                   Vector3.Distance(restored.LinearVelocity, grid.LinearVelocity) < 0.001f &&
                   MathF.Abs(Quaternion.Dot(restored.Orientation, orientation)) > 0.999f,
                "physics grid transform or velocity was not restored");
            Assert(restored.SmoothSpheres.Count == 1 && restored.SmoothSpheres[0].HasExplicitCorners &&
                   Vector3.Distance(restored.SmoothSpheres[0].ExplicitCorners![2], shardCorners[2]) < 0.0001f,
                "procedural shard/deformation surface was not restored");
            Assert(loaded.PhysicsGrids.Grids[1].IsStaticDeformation &&
                   loaded.PhysicsGrids.Grids[1].IsKinematicController,
                "permanent static deformation state was not restored");
            Assert(loaded.PhysicsGrids.Grids[2].IsPhysicsGunFrozen &&
                   loaded.PhysicsGrids.Grids[2].IsKinematicController &&
                   loaded.PhysicsGrids.Grids[2].IsSleeping,
                "physics gun pin state was not restored");
        }
        finally
        {
            File.Delete(savePath);
            File.Delete(savePath + ".bak");
            File.Delete(savePath + ".tmp");
        }
    }

    private static void TestSleepingBodiesRevalidateSupport()
    {
        string saveRoot = Path.Combine(Path.GetTempPath(), $"rtxsandbox-sleep-{Guid.NewGuid():N}");
        try
        {
            var blocks = new BlockRegistry();
            var world = new VoxelWorld(generate: false, registry: blocks);
            PhysicsGrid structure = world.PhysicsGrids.Create(
                [new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One,
                    (ushort)VoxelMaterial.Metal)],
                new Vector3(8f, 12f, 8f), id => blocks.Get(id).Density);
            structure.IsSleeping = true;
            PhysicsGrid shard = world.PhysicsGrids.Create(
                [new PhysicsGridElement(0, 0, 0, Vector3.Zero, new Vector3(0.2f),
                    (ushort)VoxelMaterial.Glass)],
                new Vector3(9f, 12f, 8f), id => blocks.Get(id).Density);
            shard.FractureGeneration = 1;
            shard.IsSleeping = true;
            AnalyticSphere sphere = world.AnalyticSpheres.Create(
                new Vector3(10f, 12f, 8f), 0.5f, (ushort)VoxelMaterial.Metal);
            sphere.IsSleeping = true;

            var saves = new WorldSaveService(blocks, worldName: "sleep", saveRoot: saveRoot);
            saves.Save(world, Vector3.Zero, 12f);
            var loaded = new VoxelWorld(generate: false, registry: blocks);
            Assert(saves.TryLoad(loaded, out _, out string? error), error ?? "sleep-state world did not load");
            PhysicsGrid restoredStructure = loaded.PhysicsGrids.Grids.Single(grid => !grid.IsDebrisFragment);
            PhysicsGrid restoredShard = loaded.PhysicsGrids.Grids.Single(grid => grid.IsDebrisFragment);
            AnalyticSphere restoredSphere = loaded.AnalyticSpheres.Spheres.Single();
            Assert(!restoredStructure.IsSleeping && restoredShard.IsSleeping && !restoredSphere.IsSleeping,
                "world loading trusted stale sleep for a complete body or woke settled debris");

            float structureY = restoredStructure.Position.Y;
            float sphereY = restoredSphere.Center.Y;
            using var backend = new BepuPhysicsBackend(loaded, restoredStructure.Position);
            for (int frame = 0; frame < 60; frame++) backend.Step(1f / 120f);
            Assert(restoredStructure.Position.Y < structureY - 0.1f &&
                   restoredSphere.Center.Y < sphereY - 0.1f,
                "unsupported bodies restored from sleep remained suspended in air");
            Assert(restoredShard.IsSleeping,
                "support revalidation disabled settled-debris virtualization");
        }
        finally
        {
            if (Directory.Exists(saveRoot)) Directory.Delete(saveRoot, recursive: true);
        }
    }

    private static void TestGridOwnershipTransfer()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid target = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            Vector3.Zero);
        PhysicsGrid source = world.PhysicsGrids.Create(
            new[]
            {
                new PhysicsGridElement(1, 0, 0, Vector3.UnitX, new Vector3(2f, 1f, 1f),
                    (ushort)VoxelMaterial.Glass),
            },
            Vector3.Zero);
        var physics = new VoxelPhysicsSimulator(world, blocks);
        Assert(physics.SelectGrid(target.Id), "target Grid could not be selected");
        var hit = new VoxelHit(1, 0, 0, -Vector3.UnitX, 1f, VoxelMaterial.Glass, source.Id);
        Assert(physics.TryTransferCell(hit), "aligned adjacent cell did not transfer");
        Assert(target.TryGetFullCellMaterial(1, 0, 0, out ushort material) &&
               material == (ushort)VoxelMaterial.Glass,
            "transferred material was not added to the target lattice");
        Assert(world.PhysicsGrids.Get(source.Id) is null, "empty source Grid was not removed");

        PhysicsGrid misaligned = world.PhysicsGrids.Create(
            new[]
            {
                new PhysicsGridElement(2, 0, 0, new Vector3(2f, 0f, 0f), new Vector3(3f, 1f, 1f),
                    (ushort)VoxelMaterial.Brick),
            },
            new Vector3(0.2f, 0f, 0f));
        var badHit = new VoxelHit(2, 0, 0, -Vector3.UnitX, 1f, VoxelMaterial.Brick, misaligned.Id);
        Assert(!physics.TryTransferCell(badHit), "misaligned lattice was incorrectly snapped during transfer");
    }

    private static void TestPhysicsGridCollisionShell()
    {
        var elements = new List<PhysicsGridElement>();
        for (int z = 0; z < 3; z++)
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
            elements.Add(new PhysicsGridElement(x, y, z, new Vector3(x, y, z),
                new Vector3(x + 1f, y + 1f, z + 1f), (ushort)VoxelMaterial.Stone));
        var grid = new PhysicsGrid(1, elements, Vector3.Zero);
        Assert(grid.Elements.Count == 27 && grid.CollisionElements.Count == 1 &&
               grid.CollisionElements[0].Minimum == Vector3.Zero &&
               grid.CollisionElements[0].Maximum == new Vector3(3f),
            "full-cell collision volume was not greedily merged without changing its bounds");
    }

    private static void TestDxrFullPrimaryCoverage()
    {
        Assert(DxrHardwareRenderer.PrimaryRayCount(1920, 1080) == 1920 * 1080,
            "DXR primary-ray budget still omits display pixels");
        Assert(!DxrHardwareRenderer.UsesSpatialPrimarySubsampling,
            "DXR primary coverage unexpectedly re-enabled guessed temporal pixels");
    }

    private static void TestPhysicsGridSubsteps()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            new Vector3(20f, 20f, 20f));
        grid.LinearVelocity = new Vector3(100f, 0f, 0f);
        var physics = new VoxelPhysicsSimulator(world, blocks);
        physics.Update(0.05f);
        Assert(physics.SimulationSubstepsLastStep == 2 && physics.Status.Contains("BEPUphysics 2"),
            "physics engine did not use the bounded collision-step budget");
    }

    private static void TestPhysicsFixedTimestep()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            new Vector3(20f, 20f, 20f));
        using var backend = new BepuPhysicsBackend(world);
        Vector3 initialPosition = grid.Position;

        backend.Step(1f / 240f);
        Assert(backend.CollisionStepsLastFrame == 0 && Vector3.Distance(grid.Position, initialPosition) < 0.000001f,
            "a partial frame advanced the physics simulation");

        backend.Step(1f / 240f);
        Assert(backend.CollisionStepsLastFrame == 1 && grid.Position.Y < initialPosition.Y,
            "two partial frames did not accumulate into one exact physics step");

        backend.Step(1f);
        Assert(backend.CollisionStepsLastFrame == 2,
            "a stalled frame exceeded or failed to fill the catch-up budget");
    }

    private static void TestPhysicsStructureChunkResidency()
    {
        var blocks = new BlockRegistry();
        var generation = new WorldGenerationSettings
        {
            Preset = WorldPreset.Flat,
            Seed = 2468,
            FlatSurfaceHeight = 4,
            SurfaceBlockKey = "rtxsandbox:stone",
            UndergroundBlockKey = "rtxsandbox:stone",
            GenerateLakes = false,
            GenerateVegetation = false,
        };
        var world = new VoxelWorld(generate: true, registry: blocks, generationSettings: generation);
        var targetChunk = new ChunkCoordinate(4, 1);
        Assert(!world.SnapshotLoadedChunks().Contains(targetChunk),
            "residency test target chunk was already loaded");
        PhysicsGrid structure = world.PhysicsGrids.Create(
            [new PhysicsGridElement(0, 0, 0, Vector3.Zero, new Vector3(2f, 2f, 2f),
                (ushort)VoxelMaterial.Stone)],
            new Vector3(62f, 12f, 24f), id => blocks.Get(id).Density);
        structure.LinearVelocity = new Vector3(24f, 0f, 0f);

        using var physics = new VoxelPhysicsSimulator(world, blocks);
        physics.Update(1f / 60f);
        Assert(world.SnapshotLoadedChunks().Contains(targetChunk) &&
               physics.ForcedPhysicsChunkLoadsLastStep > 0,
            "moving complete structure reached an unloaded chunk without forcing terrain residency");

        PhysicsGrid debris = world.PhysicsGrids.Create(
            [new PhysicsGridElement(0, 0, 0, Vector3.Zero, new Vector3(0.2f),
                (ushort)VoxelMaterial.Glass)],
            new Vector3(96f, 20f, 24f), id => blocks.Get(id).Density);
        debris.IsDebrisFragment = true;
        debris.LinearVelocity = new Vector3(24f, 0f, 0f);
        var debrisAhead = new ChunkCoordinate(6, 1);
        Assert(!world.SnapshotLoadedChunks().Contains(debrisAhead),
            "debris residency test target chunk was already loaded");
        structure.IsSleeping = true;
        physics.Update(1f / 60f);
        Assert(!world.SnapshotLoadedChunks().Contains(debrisAhead),
            "a tiny debris fragment retained an unbounded trail of generated chunks");
    }

    private static void TestEventDrivenDynamicReconcile()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            new Vector3(20f, 20f, 20f));
        using var backend = new BepuPhysicsBackend(world);

        backend.Step(0f);
        int reconcilesAfterRegistration = backend.DynamicReconcileCount;
        for (int frame = 0; frame < 120; frame++) backend.Step(0f);
        Assert(backend.DynamicReconcileCount == reconcilesAfterRegistration,
            "an unchanged rigid-grid collection was rescanned every frame");

        Assert(grid.TryAddFullCell(1, 0, 0, (ushort)VoxelMaterial.Metal),
            "test topology edit was rejected");
        backend.Step(0f);
        Assert(backend.DynamicReconcileCount == reconcilesAfterRegistration + 1,
            "a rigid-grid topology event did not schedule exactly one body reconciliation");
        backend.Step(0f);
        Assert(backend.DynamicReconcileCount == reconcilesAfterRegistration + 1,
            "one topology edit remained dirty after its body reconciliation");
    }

    private static void TestSettledDebrisVirtualization()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        const int shardCount = 100_000;
        for (int index = 0; index < shardCount; index++)
        {
            PhysicsGrid shard = world.PhysicsGrids.Create(
                new[]
                {
                    new PhysicsGridElement(0, 0, 0, Vector3.Zero,
                        new Vector3(0.18f, 0.08f, 0.12f), (ushort)VoxelMaterial.Glass),
                },
                new Vector3(index % 320, 4f, index / 320));
            shard.FractureGeneration = 1;
            shard.IsSleeping = true;
        }

        using var backend = new BepuPhysicsBackend(world);
        backend.Step(0f);
        Assert(world.PhysicsGrids.Count == shardCount,
            "settled-debris optimization deleted world entities");
        Assert(backend.DynamicBodyCount == 0 && backend.VirtualizedDebrisCount == shardCount,
            "settled debris still allocated one rigid body per shard");
        Assert(world.PhysicsGrids.CollisionGridCount == 0,
            "settled debris remained in the unbounded active collision scan");
        PhysicsGrid playerShard = world.PhysicsGrids.Grids[0];
        (Vector3 playerMinimum, Vector3 playerMaximum) = playerShard.CalculateWorldBounds();
        Assert(world.PhysicsGrids.TryCalculatePlayerPenetration(
                   playerMinimum + new Vector3(0.01f), playerMaximum - new Vector3(0.01f),
                   out Vector3 playerCorrection, out _) && playerCorrection.LengthSquared() > 0.000001f,
            "virtualized visible debris no longer collides with the player");

        PhysicsGrid struck = world.PhysicsGrids.Grids[shardCount / 2];
        world.PhysicsGrids.SetCollisionVirtualized(struck, virtualized: false);
        struck.ApplyImpulseAtPoint(new Vector3(2f, 0f, 0f), struck.Position);
        world.PhysicsGrids.NotifyTopologyChanged(struck);
        backend.Step(0f);
        Assert(world.PhysicsGrids.Count == shardCount && backend.DynamicBodyCount == 1,
            "reactivating one shard either removed the group or rebuilt every settled body");
    }

    private static void TestActiveDebrisSettles()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++) world.Set(x, 0, z, VoxelMaterial.Bedrock);

        const int shardCount = 64;
        for (int index = 0; index < shardCount; index++)
        {
            int x = index % 8;
            int z = index / 8;
            PhysicsGrid shard = world.PhysicsGrids.Create(
                new[]
                {
                    new PhysicsGridElement(0, 0, 0, Vector3.Zero,
                        new Vector3(0.24f, 0.08f, 0.18f), (ushort)VoxelMaterial.Glass),
                },
                new Vector3(3f + x * 0.5f, 1.001f, 3f + z * 0.5f));
            shard.FractureGeneration = 1;
            shard.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ,
                (index & 1) == 0 ? 0.11f : -0.11f);
        }

        using var backend = new BepuPhysicsBackend(world);
        WaitForColliderJobs(backend);
        for (int frame = 0; frame < 240 && backend.VirtualizedDebrisCount < shardCount; frame++)
            backend.Step(1f / 120f);

        Assert(world.PhysicsGrids.Count == shardCount,
            "active-debris settling deleted shard entities");
        Assert(backend.VirtualizedDebrisCount == shardCount && backend.DynamicBodyCount == 0,
            $"only {backend.VirtualizedDebrisCount}/{shardCount} quiet shards reached the virtualized state");
    }

    private static void TestMovingDebrisCollisionLod()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        const int shardCount = 144;
        for (int index = 0; index < shardCount; index++)
        {
            PhysicsGrid shard = world.PhysicsGrids.Create(
                new[]
                {
                    new PhysicsGridElement(0, 0, 0, Vector3.Zero,
                        new Vector3(0.18f, 0.06f, 0.14f), (ushort)VoxelMaterial.Glass),
                },
                new Vector3(index % 64 * 0.35f, 20f + index / 64 * 0.25f, 0f));
            shard.FractureGeneration = 1;
        }

        using var backend = new BepuPhysicsBackend(world, new Vector3(0f, 20f, 0f));
        backend.Step(0f);
        Assert(world.PhysicsGrids.Count == shardCount,
            "moving-debris LOD deleted shards during registration");
        Assert(backend.DynamicBodyCount == 8 && backend.LightweightDebrisCount == 136,
            $"moving-debris LOD allocated {backend.DynamicBodyCount} exact and " +
            $"{backend.LightweightDebrisCount} lightweight bodies");
        float before = world.PhysicsGrids.Grids.Average(grid => grid.Position.Y);
        backend.Step(1f / 30f);
        float after = world.PhysicsGrids.Grids.Average(grid => grid.Position.Y);
        Assert(world.PhysicsGrids.Count == shardCount && after < before,
            "lightweight moving debris either disappeared or ignored gravity");
    }

    private static void TestPhysicsWorldReload()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(4, 0, 4, VoxelMaterial.Bedrock);
        world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Brick) },
            new Vector3(4f, 4f, 4f));
        using var backend = new BepuPhysicsBackend(world);
        WaitForColliderJobs(backend);
        backend.Step(1f / 120f);
        Assert(backend.DynamicBodyCount == 1 && backend.StaticChunkBodyCount == 1,
            "initial physics bodies were not registered");

        world.LoadRawData(new ushort[VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth]);
        Assert(backend.DynamicBodyCount == 0 && backend.StaticChunkBodyCount == 0,
            "world reload retained old-world colliders until a later physics frame");
        backend.Step(0f);
        Assert(backend.DynamicBodyCount == 0 && backend.StaticChunkBodyCount == 0,
            "world reload left stale physics bodies behind");

        world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Glass) },
            new Vector3(8f, 8f, 8f));
        backend.Step(1f / 120f);
        Assert(backend.DynamicBodyCount == 1,
            "physics backend did not accept bodies after a world reload");
    }

    private static void TestUrgentPhysicsChunkRebuild()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(0, 0, 0, VoxelMaterial.Stone);
        world.Set(16, 0, 0, VoxelMaterial.Stone);
        world.Set(32, 0, 0, VoxelMaterial.Stone);
        using var backend = new BepuPhysicsBackend(world);

        WaitForColliderJobs(backend);
        int initialBuilds = backend.ColliderBuildCount;
        world.Set(32, 0, 0, VoxelMaterial.Glass);
        backend.Step(0f);
        Assert(backend.ColliderBuildCount == initialBuilds && backend.PendingColliderBuildCount == 0,
            "a material-only replacement rebuilt an identical static collider");

        world.Set(32, 0, 0, VoxelMaterial.Air);
        WaitForColliderJobs(backend);
        Assert(!backend.IsStaticChunkRegistered(new ChunkCoordinate(2, 0)),
            "the player-edited collision shape waited behind the streaming rebuild backlog");
    }

    private static void TestSupportRemovalWakesRigidBody()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(4, 0, 4, VoxelMaterial.Bedrock);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            [new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One,
                (ushort)VoxelMaterial.Metal)],
            new Vector3(4f, 1f, 4f), id => blocks.Get(id).Density);
        grid.IsSleeping = true;
        using var backend = new BepuPhysicsBackend(world, grid.Position);
        WaitForColliderJobs(backend);
        // Committing the asynchronously built floor collider is allowed to wake an
        // overlapping body. Let BEPU establish a real resting contact and sleep it;
        // the regression is about the later topology event, not an authored flag.
        for (int frame = 0; frame < 1_200 && !grid.IsSleeping; frame++)
            backend.Step(1f / 120f);
        Assert(grid.IsSleeping && backend.DynamicBodyCount == 1,
            "supported test body did not begin in the sleeping state");

        float startingY = grid.Position.Y;
        world.Set(4, 0, 4, VoxelMaterial.Air);
        Assert(!grid.IsSleeping,
            "removing the touching support did not wake its sleeping body");
        WaitForColliderJobs(backend);
        for (int frame = 0; frame < 60; frame++) backend.Step(1f / 120f);
        Assert(grid.Position.Y < startingY - 0.1f,
            "body remained suspended after its rebuilt static collider lost support");
    }

    private static void WaitForColliderJobs(BepuPhysicsBackend backend)
    {
        for (int attempt = 0; attempt < 2_000 && backend.PendingColliderBuildCount > 0; attempt++)
        {
            backend.Step(0f);
            if (backend.PendingColliderBuildCount > 0) Thread.Sleep(1);
        }
        Assert(backend.PendingColliderBuildCount == 0, "background collider jobs did not drain");
    }

    private static void TestPhysicsDisposal()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            new Vector3(8f, 8f, 8f));
        var physics = new VoxelPhysicsSimulator(world, blocks);
        physics.Update(1f / 120f);
        physics.Dispose();
        physics.Dispose();
    }

    private static void TestSleepingGridInvalidation()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            new Vector3(8f, 8f, 8f));
        grid.IsSleeping = true;
        int visualChanges = 0;
        world.PhysicsGrids.Changed += () => visualChanges++;
        using var backend = new BepuPhysicsBackend(world);
        backend.Step(1f / 120f);
        backend.Step(1f / 120f);
        Assert(visualChanges == 0,
            "an unchanged sleeping body repeatedly invalidated the dynamic render mesh");
    }

    private static void TestPhysicsGridCenterOfMass()
    {
        var grid = new PhysicsGrid(1,
            new[]
            {
                new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone),
                new PhysicsGridElement(2, 0, 0, new Vector3(2f, 0f, 0f), new Vector3(2.5f, 1f, 1f),
                    (ushort)VoxelMaterial.Metal),
            },
            Vector3.Zero,
            material => material == (ushort)VoxelMaterial.Metal ? 4f : 1f);
        // Centres 0.5 and 2.25; masses are 1.0 and (0.5 volume * 4 density) = 2.0.
        Assert(MathF.Abs(grid.CenterOfMassLocal.X - 1.6666667f) < 0.0001f,
            "center of mass was not volume-and-density weighted");
    }

    private static void TestPhysicsGridBoundingRadius()
    {
        var grid = new PhysicsGrid(1,
            new[]
            {
                new PhysicsGridElement(
                    0, 0, 0,
                    new Vector3(-24f, -2f, -1f),
                    new Vector3(24f, 2f, 1f),
                    (ushort)VoxelMaterial.Stone),
            },
            Vector3.Zero);
        Assert(grid.BoundingRadius >= 24f,
            $"large Grid reported a {grid.BoundingRadius:0.00}-block culling radius");
    }

    private static void TestOffsetSupportTorque()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(5, 0, 5, VoxelMaterial.Bedrock);
        world.Set(5, 1, 5, VoxelMaterial.Stone);
        world.Set(5, 2, 5, VoxelMaterial.Brick);
        world.Set(6, 2, 5, VoxelMaterial.Brick);
        world.Set(7, 2, 5, VoxelMaterial.Brick);
        var physics = new VoxelPhysicsSimulator(world, blocks);
        world.Set(5, 1, 5, VoxelMaterial.Air);
        physics.AnalyzeNow();
        Assert(world.PhysicsGrids.Count == 1, "unsupported beam did not detach");
        Assert(world.PhysicsGrids.Grids[0].AngularVelocity.LengthSquared() > 0.0001f,
            "offset support removal produced no angular velocity");
        Quaternion before = world.PhysicsGrids.Grids[0].Orientation;
        physics.Update(0.03f);
        Assert(MathF.Abs(Quaternion.Dot(before, world.PhysicsGrids.Grids[0].Orientation)) < 0.999999f,
            "angular velocity did not update the local Grid orientation");
    }

    private static void TestSupportedSoftStructureExtraction()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        const int y = 12;
        world.Set(3, y, 5, VoxelMaterial.Bedrock);
        for (int x = 4; x <= 7; x++) world.Set(x, y, 5, VoxelMaterial.Glass);
        world.Set(8, y, 5, VoxelMaterial.Metal);

        using var physics = new VoxelPhysicsSimulator(world, blocks);
        int extracted = physics.ExtractLoadedSoftStructures(new[] { (ushort)VoxelMaterial.Glass });
        Assert(extracted == 1 && world.PhysicsGrids.Count == 1,
            $"supported soft beam extraction produced {extracted} structures and {world.PhysicsGrids.Count} grids");
        PhysicsGrid grid = world.PhysicsGrids.Grids[0];
        Assert(grid.Elements.Count == 5 &&
               grid.Elements.Count(element => element.MaterialId == (ushort)VoxelMaterial.Glass) == 4 &&
               grid.Elements.Single(element => element.MaterialId == (ushort)VoxelMaterial.Metal).CellX == 4,
            "soft beam and finite hard tip were not extracted as one mixed structure");
        Assert(world.Get(3, y, 5) == VoxelMaterial.Bedrock,
            "static support was incorrectly absorbed into the soft structure");
        Assert(Enumerable.Range(4, 5).All(x => world.Get(x, y, 5) == VoxelMaterial.Air),
            "extracted soft structure remained duplicated in the static world");
    }

    private static void TestDynamicGridSelectionTransform()
    {
        var world = new VoxelWorld(generate: false);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Glass) },
            new Vector3(8f, 8f, 8f));
        grid.SetTransformAndVelocity(
            new Vector3(12f, 10f, 9f), Quaternion.CreateFromYawPitchRoll(0.7f, 0.35f, 0.2f),
            Vector3.Zero, Vector3.Zero);
        Vector3 faceNormal = Vector3.Normalize(grid.LocalDirectionToWorld(-Vector3.UnitZ));
        Vector3 faceCenter = grid.LocalToWorld(new Vector3(0.5f, 0.5f, 0f));
        PhysicsGridRayHit? first = world.PhysicsGrids.Raycast(faceCenter + faceNormal * 2f, -faceNormal, 4f);
        Assert(first.HasValue && first.Value.GridId == grid.Id && first.Value.CellX == 0,
            "rotated Grid could not be selected in transformed coordinates");
        int selectedCellX = first.GetValueOrDefault().CellX;

        grid.Position += new Vector3(3f, -1f, 2f);
        world.PhysicsGrids.NotifyChanged();
        faceCenter = grid.LocalToWorld(new Vector3(0.5f, 0.5f, 0f));
        PhysicsGridRayHit? moved = world.PhysicsGrids.Raycast(faceCenter + faceNormal * 2f, -faceNormal, 4f);
        Assert(moved.HasValue && moved.Value.GridId == grid.Id && moved.Value.CellX == selectedCellX,
            "selection ray did not follow the moving Grid transform");

        grid.SetTransformAndVelocity(
            grid.Position,
            grid.Orientation,
            new Vector3(0.8f, -1.2f, 0.35f),
            new Vector3(0.3f, 0.6f, 0.2f));
        Vector3 beforePhysicsPosition = grid.Position;
        Quaternion beforePhysicsOrientation = grid.Orientation;
        using var backend = new BepuPhysicsBackend(world);
        backend.Step(1f / 120f);
        Assert(Vector3.DistanceSquared(beforePhysicsPosition, grid.Position) > 0.000001f &&
               MathF.Abs(Quaternion.Dot(beforePhysicsOrientation, grid.Orientation)) < 0.9999999f,
            "BEPU movement was not synchronized back to the selectable Grid transform");
        faceNormal = Vector3.Normalize(grid.LocalDirectionToWorld(-Vector3.UnitZ));
        faceCenter = grid.LocalToWorld(new Vector3(0.5f, 0.5f, 0f));
        PhysicsGridRayHit? falling = world.PhysicsGrids.Raycast(faceCenter + faceNormal * 2f, -faceNormal, 4f);
        Assert(falling.HasValue && falling.Value.GridId == grid.Id && falling.Value.CellX == selectedCellX,
            "selection stopped following while BEPU was translating and rotating the Grid");
    }

    private static void TestDynamicGridPlacementPreview()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            new Vector3(8f, 8f, 8f));
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        var hit = new PhysicsGridRayHit(
            grid.Id,
            0,
            0,
            0,
            (ushort)VoxelMaterial.Stone,
            grid.LocalToWorld(new Vector3(1f, 0.5f, 0.5f)),
            Vector3.UnitX,
            2f);

        Assert(physics.CanPlaceOnGrid(hit, (ushort)VoxelMaterial.Glass),
            "grid placement preview rejected a free face-neighbor cell");
        Assert(physics.TryPlaceOnGrid(hit, (ushort)VoxelMaterial.Glass) && grid.ContainsCell(1, 0, 0),
            "grid placement did not commit the cell accepted by its preview");
        Assert(!physics.CanPlaceOnGrid(hit, (ushort)VoxelMaterial.Glass),
            "grid placement preview accepted an occupied destination");
    }

    private static void TestPhysicsGridMicroPlacement()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Stone) },
            new Vector3(8f, 8f, 8f));
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        var hit = new PhysicsGridRayHit(
            grid.Id, 0, 0, 0, (ushort)VoxelMaterial.Stone,
            grid.LocalToWorld(new Vector3(1f, 0.53f, 0.53f)),
            grid.LocalDirectionToWorld(Vector3.UnitX), 2f);

        Assert(physics.TryPlaceMicroOnGrid(hit, (ushort)VoxelMaterial.Glass, 16),
            "precision grid placement was rejected");
        PhysicsGridElement placed = grid.Elements.Single(element => element.MaterialId == (ushort)VoxelMaterial.Glass);
        Vector3 size = placed.Maximum - placed.Minimum;
        Assert(Vector3.DistanceSquared(size, new Vector3(1f / 16f)) < 0.0000001f,
            $"precision grid placement became a full cell: {size}");
        PhysicsGridRayHit? microHit = grid.Raycast(
            grid.LocalToWorld(placed.Center + Vector3.UnitX),
            grid.LocalDirectionToWorld(-Vector3.UnitX),
            2f);
        Assert(microHit.HasValue && physics.TryRemoveGridElement(microHit.Value),
            "precision grid element could not be removed independently");
        Assert(grid.Elements.Count == 1 && grid.Elements[0].MaterialId == (ushort)VoxelMaterial.Stone,
            "removing a precision grid element also removed its full-cell neighbor");
    }

    private static void TestFloorContactRotation()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        for (int x = 0; x < 16; x++)
        for (int z = 3; z < 9; z++) world.Set(x, 0, z, VoxelMaterial.Bedrock);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[]
            {
                new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Brick),
                new PhysicsGridElement(1, 0, 0, Vector3.UnitX, new Vector3(2f, 1f, 1f),
                    (ushort)VoxelMaterial.Brick),
            },
            new Vector3(5f, 1.02f, 5f));
        grid.SetTransformAndVelocity(grid.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.18f),
            new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, 1.1f));
        var physics = new VoxelPhysicsSimulator(world, blocks);
        for (int i = 0; i < 4; i++) physics.Update(1f / 120f);
        Quaternion duringContact = grid.Orientation;
        for (int i = 0; i < 8; i++) physics.Update(1f / 120f);
        Assert(MathF.Abs(Quaternion.Dot(duringContact, grid.Orientation)) < 0.999999f,
            "floor contact froze the complete Grid orientation");
    }

    private static void TestGridSettlesWithoutBounce()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++) world.Set(x, 0, z, VoxelMaterial.Bedrock);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[]
            {
                new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Brick),
                new PhysicsGridElement(1, 0, 0, Vector3.UnitX, new Vector3(2f, 1f, 1f),
                    (ushort)VoxelMaterial.Brick),
            },
            new Vector3(6f, 5f, 6f));
        grid.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.23f);
        var physics = new VoxelPhysicsSimulator(world, blocks);
        for (int i = 0; i < 360; i++) physics.Update(1f / 120f);
        float maximumLateUpwardSpeed = 0f;
        for (int i = 0; i < 360; i++)
        {
            physics.Update(1f / 120f);
            maximumLateUpwardSpeed = MathF.Max(maximumLateUpwardSpeed, grid.LinearVelocity.Y);
        }
        Assert(maximumLateUpwardSpeed < 0.12f,
            $"resting Grid kept bouncing at {maximumLateUpwardSpeed:0.00} m/s");
    }

    private static void TestPrecariousGridRest()
    {
        var blocks = new BlockRegistry();
        var world = new VoxelWorld(generate: false, registry: blocks);
        for (int x = 0; x < 12; x++)
        for (int z = 0; z < 12; z++) world.Set(x, 0, z, VoxelMaterial.Bedrock);
        PhysicsGrid grid = world.PhysicsGrids.Create(
            new[] { new PhysicsGridElement(
                0, 0, 0, Vector3.Zero, Vector3.One, (ushort)VoxelMaterial.Brick) },
            Vector3.Zero);
        Quaternion edgeBalance = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.25f);
        grid.SetTransformAndVelocity(
            new Vector3(5.5f, 1f + MathF.Sqrt(0.5f), 5.5f),
            edgeBalance,
            Vector3.Zero,
            Vector3.Zero);
        grid.IsSleeping = true;

        using var physics = new VoxelPhysicsSimulator(world, blocks);
        for (int index = 0; index < 4; index++) physics.Update(1f / 120f);
        Assert(!grid.IsSleeping && grid.AngularVelocity.Length() >= 0.10f,
            $"exact edge balance remained asleep (angular={grid.AngularVelocity.Length():0.000})");
    }

    private static void Run(string name, Action test, List<string> failures)
    {
        _testsRun++;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Console.Write($"TEST {_testsRun,2}  {name} ... ");
        Console.Out.Flush();
        try
        {
            test();
            stopwatch.Stop();
            Console.WriteLine($"PASS ({stopwatch.Elapsed.TotalMilliseconds:0} ms)");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Console.WriteLine($"FAIL ({stopwatch.Elapsed.TotalMilliseconds:0} ms)");
            failures.Add($"{name}: {exception.Message}");
            if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                string annotationTitle = EscapeGitHubCommandProperty($"Self-test failed: {name}");
                string annotationMessage = EscapeGitHubCommandData(exception.Message);
                Console.WriteLine($"::error title={annotationTitle}::{annotationMessage}");
            }
        }
    }

    private static string EscapeGitHubCommandProperty(string value) => value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal)
        .Replace(":", "%3A", StringComparison.Ordinal)
        .Replace(",", "%2C", StringComparison.Ordinal);

    private static string EscapeGitHubCommandData(string value) => value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TestRenderBackend : IRenderBackend
    {
        public TestRenderBackend(RenderBackendKind kind) => Kind = kind;

        public RenderBackendKind Kind { get; }
        public RenderBackendCapabilities Capabilities => RenderBackendCapabilities.RealTime;
        public string Name => $"Test {Kind}";
        public int AccumulatedSamples => 0;
        public bool Disposed { get; private set; }

        public void Resize(int width, int height, float renderScale) { }
        public void ResetAccumulation() { }
        public void Render(Vector3 cameraPosition, Camera camera, VoxelHit? selection, float timeOfDay,
            RenderQualitySettings quality, float exposure) { }
        public void Dispose() => Disposed = true;
    }
}
