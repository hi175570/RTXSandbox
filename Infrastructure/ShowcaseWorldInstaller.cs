using System.Numerics;
using RTXSandbox.Gameplay;
using RTXSandbox.Persistence;
using RTXSandbox.World;

namespace RTXSandbox.Infrastructure;

/// <summary>Builds and verifies the deterministic RTX campus without launching a window.</summary>
public static class ShowcaseWorldInstaller
{
    public static int Run(string[] args)
    {
        try
        {
            bool verifyOnly = Value(args, "--verify-showcase-world=") is not null;
            string worldName = Value(args, verifyOnly ? "--verify-showcase-world=" : "--install-showcase-world=") ?? "world1";
            string sourceRoot = Path.GetFullPath(Value(args, "--showcase-source-root=") ??
                                                  Path.Combine(RuntimePaths.OutputRoot, "Saves"));
            string outputRoot = Path.GetFullPath(Value(args, "--showcase-output-root=") ?? sourceRoot);
            var blocks = new BlockRegistry();
            var sourceWorld = new VoxelWorld(generate: false, registry: blocks);
            var source = new WorldSaveService(blocks, worldName: worldName, saveRoot: sourceRoot);
            if (!source.TryLoad(sourceWorld, out SavedWorldState previous, out string? error))
                throw new InvalidOperationException($"Cannot load {worldName}: {error ?? "save not found"}");

            if (verifyOnly)
            {
                Verify(sourceWorld, previous, blocks);
                Console.WriteLine(
                    $"SHOWCASE_VERIFY PASS world={worldName} seed={previous.Seed} " +
                    $"chunks={sourceWorld.LoadedChunkCount} grids={sourceWorld.PhysicsGrids.Count} " +
                    $"spheres={sourceWorld.AnalyticSpheres.Count} source={source.SaveDirectory}");
                return 0;
            }

            var settings = new WorldGenerationSettings
            {
                Preset = WorldPreset.Showcase,
                Seed = previous.Seed,
                FlatSurfaceHeight = 4,
                SurfaceBlockKey = "rtxsandbox:white_concrete",
                UndergroundBlockKey = "rtxsandbox:stone",
                GenerateLakes = false,
                GenerateVegetation = false,
            };
            sourceWorld.Regenerate(settings);
            Vector3 spawn = new(33.5f, 6.001f, 32.5f);
            var items = new ItemRegistry(blocks);
            var inventory = new PlayerInventory(items,
                slotCount: Math.Max(45, previous.Inventory.Count), hotbarSize: 9);
            if (previous.Inventory.Count > 0)
                inventory.Restore(previous.Inventory, previous.SelectedHotbarSlot);

            var destination = new WorldSaveService(
                blocks, preferredSeed: previous.Seed, worldName: worldName, saveRoot: outputRoot);
            destination.Save(sourceWorld, spawn, 14f, inventory,
                previous.Health, previous.Oxygen, previous.Hunger,
                previous.CompletedObjectives, previous.SawNight);

            var verifiedWorld = new VoxelWorld(generate: false, registry: blocks);
            if (!destination.TryLoad(verifiedWorld, out SavedWorldState verified, out string? verifyError))
                throw new InvalidOperationException($"Showcase verification load failed: {verifyError}");
            Verify(verifiedWorld, verified, blocks);
            Console.WriteLine(
                $"SHOWCASE_INSTALL PASS world={worldName} seed={verified.Seed} " +
                $"chunks={verifiedWorld.LoadedChunkCount} grids={verifiedWorld.PhysicsGrids.Count} " +
                $"spheres={verifiedWorld.AnalyticSpheres.Count} output={destination.SaveDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SHOWCASE_INSTALL FAIL {exception}");
            return 1;
        }
    }

    private static void Verify(VoxelWorld world, SavedWorldState state, BlockRegistry blocks)
    {
        if (state.GenerationSettings.Preset != WorldPreset.Showcase)
            throw new InvalidDataException("Installed world is not the Showcase preset.");
        if (world.PhysicsGrids.Count < 8)
            throw new InvalidDataException($"Showcase has only {world.PhysicsGrids.Count} Physics Grids.");
        if (world.AnalyticSpheres.Count < 7)
            throw new InvalidDataException($"Showcase has only {world.AnalyticSpheres.Count} analytic spheres.");
        if (world.GetBlockId(21, 10, 41) != 0)
            throw new InvalidDataException("Camera-obscura aperture is blocked.");
        if (!blocks.Get(world.GetBlockId(47, 11, 60)).Tags.Any(tag =>
                tag.Equals("soft-body", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Showcase has no wall-attached soft-body beam.");
        if (world.PhysicsGrids.Grids.Any(grid => grid.Elements.Any(element =>
                blocks.Get(element.MaterialId).Tags.Any(tag =>
                    tag.Equals("soft-body", StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidDataException("Showcase serialized a supported soft body as a detached Physics Grid.");
        if (!world.RawData.Any(id => id != 0 && blocks.Get(id).Tags.Any(tag =>
                tag.Equals("animation-controller", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Showcase has no animation controller block.");
        foreach (int z in new[] { 52, 54, 56 })
        {
            if (world.GetBlockId(46, 9, z) == 0)
                throw new InvalidDataException($"Showcase soft-body strip at z={z} has no fixture wall.");
        }

        if (world.PhysicsGrids.Grids.Any(grid => grid.Elements.Any(element =>
                blocks.Get(element.MaterialId).Tags.Any(tag =>
                    tag.Equals("mechanical", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("mechanical-source", StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidDataException("Showcase still contains retired mechanical assemblies.");
    }

    private static string? Value(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?
            .Substring(prefix.Length);
}
