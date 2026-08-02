using System.Numerics;
using RTXSandbox.Configuration;
using RTXSandbox.Gameplay;
using RTXSandbox.ModAPI;
using RTXSandbox.Persistence;
using RTXSandbox.Rendering;
using RTXSandbox.World;

namespace RTXSandbox;

public static class SelfTests
{
    public static int Run()
    {
        var failures = new List<string>();
        Run("voxel set/get", TestVoxelSetGet, failures);
        Run("raycast hit and normal", TestRaycast, failures);
        Run("player floor collision", TestFloorCollision, failures);
        Run("player cannot place inside body", TestPlacementOverlap, failures);
        Run("world clock cycle and pause", TestWorldClock, failures);
        Run("water raycast and collision", TestWaterBehavior, failures);
        Run("player can swim upward", TestSwimming, failures);
        Run("modded world save remaps block keys", TestWorldSaveRemapping, failures);
        Run("forced block override is last-writer-wins", TestForcedBlockOverride, failures);
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
        Run("flight noclip passes through structures", TestFlightNoClip, failures);
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
        Run("creative hotbar stores namespaced block keys", TestCreativeHotbar, failures);
        Run("mixed precision grids stay sparse and exact", TestMicroVoxelGridPromotion, failures);
        Run("micro voxels raycast at exact surfaces", TestMicroVoxelRaycast, failures);
        Run("player collision follows micro voxel geometry", TestMicroVoxelCollision, failures);
        Run("precision edits undo and persist with key remapping", TestMicroVoxelEditingAndPersistence, failures);
        Run("primary destroys while secondary placement stays explicit", TestPrimaryDestroySemantics, failures);
        Run("renderer catalog exposes only working backends", TestRenderBackendCatalog, failures);
        Run("normal destruction clears an entire precision cell", TestWholeCellMicroDestruction, failures);
        Run("physics grids persist with transforms and velocities", TestPhysicsGridPersistence, failures);
        Run("grid ownership transfers only aligned face-neighbor cells", TestGridOwnershipTransfer, failures);
        Run("physics grids prune enclosed collision cells", TestPhysicsGridCollisionShell, failures);
        Run("physics engine uses bounded collision substeps", TestPhysicsGridSubsteps, failures);
        Run("physics grid center of mass uses element volume", TestPhysicsGridCenterOfMass, failures);
        Run("removing an offset support gives structure angular velocity", TestOffsetSupportTorque, failures);
        Run("dynamic grid selection follows translation and rotation", TestDynamicGridSelectionTransform, failures);
        Run("floor contact preserves rigid-body rotation", TestFloorContactRotation, failures);
        Run("resting grid does not enter a perpetual bounce cycle", TestGridSettlesWithoutBounce, failures);

        if (failures.Count == 0)
        {
            Console.WriteLine("All 53 RTX Sandbox self-tests passed.");
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
        Assert(!world.Set(-1, 3, 4, VoxelMaterial.Stone), "out-of-bounds write succeeded");
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

    private static void TestWaterBehavior()
    {
        var world = new VoxelWorld(generate: false);
        world.Set(2, 2, 2, VoxelMaterial.Water);
        world.Set(3, 2, 2, VoxelMaterial.Water);
        world.Set(4, 2, 2, VoxelMaterial.Stone);
        Assert(!world.IsSolidForCollision(2, 2, 2), "water should not be a solid collider");
        Assert(world.FindTopVoxelY(2, 2) == 2, "water surface was not included in top-voxel query");
        VoxelHit? hit = VoxelRaycast.Cast(world, new Vector3(2.5f, 2.5f, 2.5f), Vector3.UnitX, 8f);
        Assert(hit.HasValue && hit.Value.Material == VoxelMaterial.Stone, "ray starting in water did not reach solid terrain");
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
        ushort bedrockId = world.GetBlockId(12, 0, 12);
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
        world.Set(3, 0, 3, VoxelMaterial.Bedrock);
        world.Set(3, 1, 3, VoxelMaterial.Stone);
        world.Set(3, 2, 3, VoxelMaterial.Stone);
        world.Set(6, 4, 6, VoxelMaterial.Brick);
        world.Set(7, 4, 6, VoxelMaterial.Glass);
        var physics = new VoxelPhysicsSimulator(world, blocks);
        physics.RequestEvaluation();
        physics.AnalyzeNow();
        Assert(world.Get(3, 2, 3) == VoxelMaterial.Stone, "grounded six-face structure detached");
        Assert(world.Get(6, 4, 6) == VoxelMaterial.Air && world.Get(7, 4, 6) == VoxelMaterial.Air,
            "unsupported structure remained in the static world");
        Assert(world.PhysicsGrids.Count == 1 && world.PhysicsGrids.Grids[0].Elements.Count == 2,
            "connected unsupported voxels did not become one local grid");
        float startY = world.PhysicsGrids.Grids[0].Position.Y;
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

    private static void TestFlightNoClip()
    {
        var world = new VoxelWorld(generate: false);
        for (int y = 0; y < 4; y++) world.Set(3, y, 2, VoxelMaterial.Stone);
        var player = new PlayerController(world, new Vector3(2.5f, 1f, 2.5f))
        {
            FlightMode = true,
            NoClip = true,
        };
        for (int i = 0; i < 30; i++)
        {
            player.Update(1f / 60f, new Vector2(0f, 1f), Vector3.UnitX, false, false);
        }
        Assert(player.Position.X > 4f, "noclip flight was blocked by a voxel wall");
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
        Assert(world.Get(10, 0, 10) == VoxelMaterial.Bedrock, "flat world has no bedrock layer");
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
        Assert(CountBlock(world, VoxelMaterial.Sand) > 0, "pure nature has no desert band");
        Assert(CountBlock(world, VoxelMaterial.Grass) > 0, "pure nature has no plains/forest band");
        Assert(CountBlock(world, VoxelMaterial.Snow) > 0, "pure nature has no snow/mountain band");
        Assert(CountBlock(world, VoxelMaterial.Water) > 0, "pure nature has no connected water band");
        Assert(CountBlock(world, VoxelMaterial.Bamboo) > 0 || CountBlock(world, VoxelMaterial.Mud) > 0,
            "pure nature has no wet biome transition");
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
                writer.Write(VoxelWorld.Height);
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
                int voxelCount = VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth;
                writer.Write(voxelCount);
                var legacy = new byte[voxelCount];
                int index = 4 + VoxelWorld.Width * (5 + VoxelWorld.Height * 6);
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
        Assert(tools.QuickPlace(new VoxelHit(4, 2, 2, -Vector3.UnitX, 1f, VoxelMaterial.Stone)),
            "explicit secondary placement failed");
        Assert(world.Get(3, 2, 2) == VoxelMaterial.Glass,
            "secondary placement did not use the selected material");
    }

    private static void TestRenderBackendCatalog()
    {
        RenderBackendKind[] implemented = RenderBackendCatalog.Implemented
            .Select(descriptor => descriptor.Kind)
            .ToArray();
        Assert(implemented.SequenceEqual(new[]
        {
            RenderBackendKind.SoftwareRayTracing,
            RenderBackendKind.PbrForward,
            RenderBackendKind.Classic,
        }), "implemented renderer list is incomplete or exposes a placeholder");
        Assert(!RenderBackendCatalog.IsImplemented(RenderBackendKind.HardwareRayTracing),
            "hardware RT must remain unavailable until a DXR/Vulkan RT backend exists");
        Assert(!RenderBackendCatalog.IsImplemented(RenderBackendKind.DeferredLighting),
            "deferred mode must remain unavailable until its GBuffer path exists");
        Assert(RenderBackendCatalog.Implemented.All(descriptor =>
                descriptor.Capabilities.HasFlag(RenderBackendCapabilities.MicroVoxels)),
            "a selectable renderer cannot display precision-built geometry");
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
        Assert(grid.Elements.Count == 27 && grid.CollisionElements.Count == 26,
            "fully enclosed cell was not removed from the collision shell");
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
        Assert(physics.SimulationSubstepsLastStep == 6 && physics.Status.Contains("BEPUphysics 2"),
            "physics engine did not use the bounded collision-step budget");
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
            Vector3.Zero);
        // Centres 0.5 and 2.25, volumes 1 and 0.5 respectively.
        Assert(MathF.Abs(grid.CenterOfMassLocal.X - 1.0833334f) < 0.0001f,
            "center of mass was not volume weighted");
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
        Assert(maximumLateUpwardSpeed < 0.35f,
            $"resting Grid kept bouncing at {maximumLateUpwardSpeed:0.00} m/s");
    }

    private static void Run(string name, Action test, List<string> failures)
    {
        try
        {
            test();
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
