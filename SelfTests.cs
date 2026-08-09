using System.Numerics;
using System.Reflection;
using System.Text.Json;
using RTXSandbox.Configuration;
using RTXSandbox.Gameplay;
using RTXSandbox.ModAPI;
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
        Run("analytic sphere uses rigid-body gravity and player pushing", TestAnalyticSpherePhysics, failures);
        Run("analytic spheres wait for and remain supported by floor colliders", TestAnalyticSphereFloorCollision, failures);
        Run("analytic sphere raycast selects nearest surface for ordinary destruction", TestAnalyticSphereRaycast, failures);
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
        Run("diagonal voxel contact does not transmit support", TestDiagonalFracture, failures);
        Run("crouching slows movement and prevents edge falls", TestCrouchMovement, failures);
        Run("flight movement is independent of view direction", TestFlightMovementAxes, failures);
        Run("transparent and gravity materials load from content", TestEditorMaterialMetadata, failures);
        Run("mod build tools participate in editor history", TestModBuildToolHistory, failures);
        Run("flat preset honors layers and options", TestFlatWorldPreset, failures);
        Run("natural preset contains no showcase structures", TestNaturalWorldPreset, failures);
        Run("natural v2 terrain is deterministic and geologically layered", TestNaturalV2Terrain, failures);
        Run("pure nature creates biome transitions", TestPureNaturePreset, failures);
        Run("void preset creates only a test platform", TestVoidWorldPreset, failures);
        Run("showcase preset builds RTX laboratories", TestShowcaseWorldPreset, failures);
        Run("categorized material library exceeds 150 blocks", TestMaterialLibrary, failures);
        Run("16-bit block ids persist above 255", TestHighBlockIdPersistence, failures);
        Run("legacy byte worlds migrate to 16-bit voxels", TestLegacyByteWorldMigration, failures);
        Run("procedural material atlas is deterministic and detailed", TestProceduralMaterialAtlas, failures);
        Run("creative palette groups glass and uses stable color order", TestCreativePaletteOrdering, failures);
        Run("creative hotbar stores namespaced block keys", TestCreativeHotbar, failures);
        Run("mixed precision grids stay sparse and exact", TestMicroVoxelGridPromotion, failures);
        Run("micro voxels raycast at exact surfaces", TestMicroVoxelRaycast, failures);
        Run("micro voxel edits publish precise and bulk invalidation", TestMicroVoxelChangeEvents, failures);
        Run("player collision follows micro voxel geometry", TestMicroVoxelCollision, failures);
        Run("precision edits undo and persist with key remapping", TestMicroVoxelEditingAndPersistence, failures);
        Run("primary destroys while secondary placement stays explicit", TestPrimaryDestroySemantics, failures);
        Run("renderer catalog exposes only working backends", TestRenderBackendCatalog, failures);
        Run("renderer failures preserve a safe usable backend", TestRendererFailureFallback, failures);
        Run("chunk coordinates and mesh invalidation cross boundaries", TestChunkCoordinates, failures);
        Run("voxel data is owned and released by chunks", TestChunkOwnedVoxelStorage, failures);
        Run("chunk storage preserves the legacy linear snapshot", TestChunkLinearSnapshot, failures);
        Run("chunk occupied bounds shrink without losing precision cells", TestChunkOccupiedBounds, failures);
        Run("normal destruction clears an entire precision cell", TestWholeCellMicroDestruction, failures);
        Run("physics grids persist with transforms and velocities", TestPhysicsGridPersistence, failures);
        Run("grid ownership transfers only aligned face-neighbor cells", TestGridOwnershipTransfer, failures);
        Run("physics grids prune enclosed collision cells", TestPhysicsGridCollisionShell, failures);
        Run("physics engine uses bounded collision substeps", TestPhysicsGridSubsteps, failures);
        Run("physics engine accumulates an exact fixed timestep", TestPhysicsFixedTimestep, failures);
        Run("physics backend survives world reload", TestPhysicsWorldReload, failures);
        Run("colliders rebuild only for shape changes and prioritize edits", TestUrgentPhysicsChunkRebuild, failures);
        Run("physics disposal is idempotent", TestPhysicsDisposal, failures);
        Run("sleeping physics grids do not invalidate rendering", TestSleepingGridInvalidation, failures);
        Run("physics grid center of mass uses volume and density", TestPhysicsGridCenterOfMass, failures);
        Run("physics grid culling radius covers its complete geometry", TestPhysicsGridBoundingRadius, failures);
        Run("removing an offset support gives structure angular velocity", TestOffsetSupportTorque, failures);
        Run("dynamic grid selection follows translation and rotation", TestDynamicGridSelectionTransform, failures);
        Run("dynamic grid placement preview matches the committed cell", TestDynamicGridPlacementPreview, failures);
        Run("precision placement on physics grids preserves micro size", TestPhysicsGridMicroPlacement, failures);
        Run("floor contact preserves rigid-body rotation", TestFloorContactRotation, failures);
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
        var facingUp = new PlayerController(world, new Vector3(2.5f, 4f, 2.5f))
        {
            FlightMode = true,
            NoClip = true,
        };
        for (int index = 0; index < 30; index++)
        {
            facingEast.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitX, false, false);
            facingUp.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitY, false, false);
        }
        Assert(Vector3.DistanceSquared(facingEast.Position, facingUp.Position) < 0.000001f,
            "flight movement changed with camera direction");
        Assert(facingEast.Position.Z < 0f && MathF.Abs(facingEast.Position.X - 2.5f) < 0.001f,
            "forward flight did not follow the fixed world axis");
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
        Assert(CountBlock(world, VoxelMaterial.Glass) > 10 && CountBlock(world, VoxelMaterial.Crystal) > 0,
            "showcase glass forest is missing");
        Assert(CountBlock(world, VoxelMaterial.RedLamp) > 0 && CountBlock(world, VoxelMaterial.BlueLamp) > 0,
            "showcase lighting lab is missing");
        Assert(CountBlock(world, VoxelMaterial.Mirror) > 0, "showcase material gallery is missing mirrors");
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
            "rtxsandbox:matte_glass",
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
        Assert(maximumFrostRoughness - minimumFrostRoughness > 0.1f,
            "frosted glass has no measurable roughness texture detail");

        blocks.TryGet("rtxsandbox:matte_glass", out BlockDefinition? matteGlass);
        int frostedRepeatedNeighbors = 0;
        int matteRepeatedNeighbors = 0;
        for (int y = 0; y < ProceduralMaterialAtlas.TileSize; y++)
        for (int x = 0; x + 1 < ProceduralMaterialAtlas.TileSize; x++)
        {
            if (Vector3.DistanceSquared(
                    first.ReadColor(frostedGlass!.Id, Rendering.MaterialFace.Side, x, y),
                    first.ReadColor(frostedGlass.Id, Rendering.MaterialFace.Side, x + 1, y)) < 0.000001f)
                frostedRepeatedNeighbors++;
            if (Vector3.DistanceSquared(
                    first.ReadColor(matteGlass!.Id, Rendering.MaterialFace.Side, x, y),
                    first.ReadColor(matteGlass.Id, Rendering.MaterialFace.Side, x + 1, y)) < 0.000001f)
                matteRepeatedNeighbors++;
        }
        Assert(frostedRepeatedNeighbors >= 96,
            $"frosted glass no longer has medium 2x2 grain ({frostedRepeatedNeighbors} repeated neighbors)");
        Assert(matteRepeatedNeighbors < frostedRepeatedNeighbors / 4,
            "matte glass grain is not visibly finer than frosted glass");

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
            "rtxsandbox:prismatic_glass", "rtxsandbox:frosted_glass", "rtxsandbox:matte_glass",
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
    }

    private static void TestRenderBackendCatalog()
    {
        RenderBackendKind[] implemented = RenderBackendCatalog.Implemented
            .Select(descriptor => descriptor.Kind)
            .ToArray();
        RenderBackendKind[] requiredRasterAndCompute =
        {
            RenderBackendKind.SoftwareRayTracing,
            RenderBackendKind.DeferredLighting,
            RenderBackendKind.HybridPbrRaster,
            RenderBackendKind.PbrForward,
            RenderBackendKind.Classic,
        };
        Assert(requiredRasterAndCompute.All(implemented.Contains),
            "implemented renderer list lost an existing raster or compute backend");
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

            var saves = new WorldSaveService(blocks, savePath);
            saves.Save(world, new Vector3(2f), 8f);
            var loaded = new VoxelWorld(generate: false, registry: blocks);
            Assert(saves.TryLoad(loaded, out _, out string? error), error ?? "grid world did not load");
            Assert(loaded.PhysicsGrids.Count == 1, "physics grid count was not restored");
            PhysicsGrid restored = loaded.PhysicsGrids.Grids[0];
            Assert(restored.Elements.Count == 2 && restored.Elements[1].MaterialId == (ushort)VoxelMaterial.Glass,
                "physics grid elements or materials were not restored");
            Assert(Vector3.Distance(restored.Position, grid.Position) < 0.001f &&
                   Vector3.Distance(restored.LinearVelocity, grid.LinearVelocity) < 0.001f &&
                   MathF.Abs(Quaternion.Dot(restored.Orientation, orientation)) > 0.999f,
                "physics grid transform or velocity was not restored");
        }
        finally
        {
            File.Delete(savePath);
            File.Delete(savePath + ".bak");
            File.Delete(savePath + ".tmp");
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
