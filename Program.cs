using RTXSandbox;
using RTXSandbox.Gameplay;
using RTXSandbox.Infrastructure;
using RTXSandbox.Modding;
using RTXSandbox.ModAPI;
using RTXSandbox.World;
using System.Reflection;

// BepInEx is self-hosted by the game rather than injected through winhttp.dll.
// Install its conventional BepInEx/core resolver before the first reference to
// a framework type is JIT-compiled.
FrameworkAssemblyResolver.Install();

if (args.Contains("--adversarial-performance-suite", StringComparer.OrdinalIgnoreCase))
{
    return AdversarialPerformanceSuite.Run(args);
}

bool streamingBenchmark = args.Contains("--world-stream-benchmark", StringComparer.OrdinalIgnoreCase);
if (streamingBenchmark)
{
    string? rendererArgument = args.FirstOrDefault(argument =>
        argument.StartsWith("--benchmark-renderer=", StringComparison.OrdinalIgnoreCase));
    string renderer = rendererArgument?.Split('=', 2)[1] ?? "Classic";
    Environment.SetEnvironmentVariable("RTX_SANDBOX_STREAM_BENCHMARK", "1");
    Environment.SetEnvironmentVariable("RTX_SANDBOX_RENDERER", renderer);
    Environment.SetEnvironmentVariable("RTX_SANDBOX_DISABLE_BEPINEX", "1");
    CopyBenchmarkArgumentToEnvironment("--benchmark-profile=", "RTX_SANDBOX_BENCHMARK_PROFILE");
    CopyBenchmarkArgumentToEnvironment("--benchmark-style=", "RTX_SANDBOX_BENCHMARK_STYLE");
    CopyBenchmarkArgumentToEnvironment("--benchmark-run-id=", "RTX_SANDBOX_BENCHMARK_RUN_ID");
    CopyBenchmarkArgumentToEnvironment("--benchmark-label=", "RTX_SANDBOX_BENCHMARK_LABEL");
    CopyBenchmarkArgumentToEnvironment("--benchmark-infinite=", "RTX_SANDBOX_BENCHMARK_INFINITE_MIRROR");
}

bool infiniteMirrorSmoke = args.Contains("--gpu-smoke-hardware-infinite", StringComparer.OrdinalIgnoreCase);
bool glassOcclusionSmoke = args.Contains("--gpu-smoke-hardware-glass-occlusion", StringComparer.OrdinalIgnoreCase);
bool darkRoomSmoke = args.Contains("--gpu-smoke-hardware-dark-room", StringComparer.OrdinalIgnoreCase);
string? smokeRenderer = args.Contains("--gpu-smoke-hardware", StringComparer.OrdinalIgnoreCase) ||
                         infiniteMirrorSmoke || glassOcclusionSmoke || darkRoomSmoke
    ? "HardwareRayTracing"
    : args.Contains("--gpu-smoke-hybrid", StringComparer.OrdinalIgnoreCase)
    ? "HybridPbrRaster"
    : args.Contains("--gpu-smoke-pbr", StringComparer.OrdinalIgnoreCase)
    ? "PbrForward"
    : args.Contains("--gpu-smoke-deferred", StringComparer.OrdinalIgnoreCase)
        ? "DeferredLighting"
        : args.Contains("--gpu-smoke-classic", StringComparer.OrdinalIgnoreCase)
        ? "Classic"
        : args.Contains("--gpu-smoke", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-mod", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-ime", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-pause", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-inventory", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-shortcut-inventory", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-shortcut-settings", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-menu", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-world-creator", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-underwater", StringComparer.OrdinalIgnoreCase)
            ? "SoftwareRayTracing"
            : null;
if (smokeRenderer is not null)
{
    Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE", "1");
    bool smokeMod = args.Contains("--gpu-smoke-mod", StringComparer.OrdinalIgnoreCase);
    Environment.SetEnvironmentVariable("RTX_SANDBOX_DISABLE_BEPINEX", smokeMod ? null : "1");
    Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_MOD", smokeMod ? "1" : null);
    Environment.SetEnvironmentVariable("RTX_SANDBOX_RENDERER", smokeRenderer);
    CopyBenchmarkArgumentToEnvironment("--gpu-smoke-deferred-preset=", "RTX_SANDBOX_GPU_SMOKE_DEFERRED_PRESET");
    CopyBenchmarkArgumentToEnvironment("--gpu-smoke-forward-preset=", "RTX_SANDBOX_GPU_SMOKE_FORWARD_PRESET");
    CopyBenchmarkArgumentToEnvironment("--gpu-smoke-forward-debug=", "RTX_SANDBOX_GPU_SMOKE_FORWARD_DEBUG");
    if (args.Contains("--gpu-smoke-hardware", StringComparer.OrdinalIgnoreCase) ||
        infiniteMirrorSmoke || glassOcclusionSmoke || darkRoomSmoke)
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_REQUIRE_HARDWARE_RT", "1");
    if (infiniteMirrorSmoke)
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_INFINITE_MIRROR", "1");
    if (glassOcclusionSmoke)
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_GLASS_OCCLUSION", "1");
    if (darkRoomSmoke)
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_DARK_ROOM", "1");
    if (args.Contains("--gpu-smoke-menu", StringComparer.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable("RTX_SANDBOX_OPEN_MAIN_MENU", "1");
    if (args.Contains("--gpu-smoke-pause", StringComparer.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable("RTX_SANDBOX_OPEN_PAUSE_MENU", "1");
    if (args.Contains("--gpu-smoke-inventory", StringComparer.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable("RTX_SANDBOX_OPEN_INVENTORY", "1");
    if (args.Contains("--gpu-smoke-shortcut-inventory", StringComparer.OrdinalIgnoreCase))
    {
        Environment.SetEnvironmentVariable("RTX_SANDBOX_OPEN_INVENTORY", "1");
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_SHORTCUT", "inventory");
    }
    if (args.Contains("--gpu-smoke-shortcut-settings", StringComparer.OrdinalIgnoreCase))
    {
        Environment.SetEnvironmentVariable("RTX_SANDBOX_OPEN_SETTINGS", "1");
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_SHORTCUT", "settings");
    }
    if (args.Contains("--gpu-smoke-world-creator", StringComparer.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable("RTX_SANDBOX_OPEN_WORLD_CREATOR", "1");
    if (args.Contains("--gpu-smoke-ime", StringComparer.OrdinalIgnoreCase))
    {
        Environment.SetEnvironmentVariable("RTX_SANDBOX_OPEN_WORLD_CREATOR", "1");
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_IME", "1");
    }
    if (args.Contains("--gpu-smoke-underwater", StringComparer.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_UNDERWATER", "1");
}

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    return SelfTests.Run();
}

if (args.Contains("--physics-probe", StringComparer.OrdinalIgnoreCase))
{
    RunPhysicsProbe();
    return 0;
}

BepInExBootstrap.Start();
if (args.Contains("--mod-diagnostics", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"BepInEx started: {BepInExBootstrap.Started}");
    Console.WriteLine($"Plugin directory: {BepInExBootstrap.PluginDirectory}");
    foreach (LoadedPluginInfo plugin in BepInExBootstrap.LoadedPlugins)
        Console.WriteLine($"Loaded plugin: {plugin.Guid} {plugin.Version} ({plugin.Location})");
    foreach (string error in BepInExBootstrap.DependencyErrors)
        Console.Error.WriteLine($"Plugin error: {error}");
    if (!string.IsNullOrWhiteSpace(BepInExBootstrap.FailureMessage))
    {
        Console.Error.WriteLine(BepInExBootstrap.FailureMessage);
    }
    return BepInExBootstrap.Started ? 0 : 1;
}
using var game = new SandboxGame();
game.Run();
return 0;

void CopyBenchmarkArgumentToEnvironment(string prefix, string environmentName)
{
    string? argument = args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (argument is not null)
        Environment.SetEnvironmentVariable(environmentName, argument.Split('=', 2)[1]);
}

void RunPhysicsProbe()
{
    var blocks = new BlockRegistry();
    BlockDefinition stone = blocks.Get((ushort)VoxelMaterial.Stone);
    BlockDefinition brick = blocks.Get((ushort)VoxelMaterial.Brick);
    BlockDefinition glass = blocks.Get((ushort)VoxelMaterial.Glass);
    Console.WriteLine($"Stone unbreakable={stone.IsUnbreakable} collidable={stone.IsCollidable}");
    Console.WriteLine($"Brick unbreakable={brick.IsUnbreakable} collidable={brick.IsCollidable}");
    Console.WriteLine($"Glass unbreakable={glass.IsUnbreakable} collidable={glass.IsCollidable}");

    {
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(3, VoxelWorld.MinimumY, 3, VoxelMaterial.Bedrock);
        world.Set(3, VoxelWorld.MinimumY + 1, 3, VoxelMaterial.Stone);
        world.Set(3, VoxelWorld.MinimumY + 2, 3, VoxelMaterial.Stone);
        world.Set(6, 4, 6, VoxelMaterial.Brick);
        world.Set(7, 4, 6, VoxelMaterial.Glass);
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        var isAnchor = typeof(VoxelPhysicsSimulator).GetMethod("IsAnchor", BindingFlags.Instance | BindingFlags.NonPublic);
        var hasEmptyFace = typeof(VoxelPhysicsSimulator).GetMethod("HasEmptyFace", BindingFlags.Instance | BindingFlags.NonPublic);
        var isStructuralAt = typeof(VoxelPhysicsSimulator).GetMethod("IsStructuralAt", BindingFlags.Instance | BindingFlags.NonPublic);
        Console.WriteLine($"case1 A(6,4,6) structural={isStructuralAt!.Invoke(physics, new object[] { 6, 4, 6 })} anchor={isAnchor!.Invoke(physics, new object[] { 6, 4, 6 })} emptyFace={hasEmptyFace!.Invoke(physics, new object[] { 6, 4, 6 })}");
        Console.WriteLine($"case1 A(7,4,6) structural={isStructuralAt!.Invoke(physics, new object[] { 7, 4, 6 })} anchor={isAnchor!.Invoke(physics, new object[] { 7, 4, 6 })} emptyFace={hasEmptyFace!.Invoke(physics, new object[] { 7, 4, 6 })}");
        physics.RequestEvaluation();
        var dirtyField = typeof(VoxelPhysicsSimulator).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic);
        var queuedField = typeof(VoxelPhysicsSimulator).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic);
        int dirty = ((System.Collections.Generic.Queue<VoxelPoint>)dirtyField!.GetValue(physics)!).Count;
        int queued = ((System.Collections.Generic.HashSet<VoxelPoint>)queuedField!.GetValue(physics)!).Count;
        Console.WriteLine($"case1 dirty={dirty} queued={queued}");
        int changes = physics.AnalyzeNow();
        Console.WriteLine($"case1 changes={changes}");
        Console.WriteLine($"case1 grids={world.PhysicsGrids.Count}");
        Console.WriteLine($"  world(6,4,6)={world.Get(6, 4, 6)} world(7,4,6)={world.Get(7, 4, 6)}");
        foreach (var grid in world.PhysicsGrids.Grids)
        {
            string cells = string.Join("; ", grid.Elements.Select(element => $"({element.CellX},{element.CellY},{element.CellZ})"));
            Console.WriteLine($"  grid #{grid.Id} cells={grid.Elements.Count} position={grid.Position} cells={cells}");
        }
    }

    {
        var world = new VoxelWorld(generate: false, registry: blocks);
        world.Set(3, 4, 3, VoxelMaterial.Stone);
        world.Set(4, 5, 3, VoxelMaterial.Stone);
        using var physics = new VoxelPhysicsSimulator(world, blocks);
        var isAnchor2 = typeof(VoxelPhysicsSimulator).GetMethod("IsAnchor", BindingFlags.Instance | BindingFlags.NonPublic);
        var hasEmptyFace2 = typeof(VoxelPhysicsSimulator).GetMethod("HasEmptyFace", BindingFlags.Instance | BindingFlags.NonPublic);
        var isStructuralAt2 = typeof(VoxelPhysicsSimulator).GetMethod("IsStructuralAt", BindingFlags.Instance | BindingFlags.NonPublic);
        Console.WriteLine($"case2 A(3,4,3) structural={isStructuralAt2!.Invoke(physics, new object[] { 3, 4, 3 })} anchor={isAnchor2!.Invoke(physics, new object[] { 3, 4, 3 })} emptyFace={hasEmptyFace2!.Invoke(physics, new object[] { 3, 4, 3 })}");
        Console.WriteLine($"case2 A(4,5,3) structural={isStructuralAt2!.Invoke(physics, new object[] { 4, 5, 3 })} anchor={isAnchor2!.Invoke(physics, new object[] { 4, 5, 3 })} emptyFace={hasEmptyFace2!.Invoke(physics, new object[] { 4, 5, 3 })}");
        physics.RequestEvaluation();
        var dirtyField2 = typeof(VoxelPhysicsSimulator).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic);
        var queuedField2 = typeof(VoxelPhysicsSimulator).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic);
        int dirty2 = ((System.Collections.Generic.Queue<VoxelPoint>)dirtyField2!.GetValue(physics)!).Count;
        int queued2 = ((System.Collections.Generic.HashSet<VoxelPoint>)queuedField2!.GetValue(physics)!).Count;
        Console.WriteLine($"case2 dirty={dirty2} queued={queued2}");
        int changes = physics.AnalyzeNow();
        Console.WriteLine($"case2 changes={changes}");
        Console.WriteLine($"case2 grids={world.PhysicsGrids.Count}");
        Console.WriteLine($"  world(3,4,3)={world.Get(3, 4, 3)} world(4,5,3)={world.Get(4, 5, 3)}");
        foreach (var grid in world.PhysicsGrids.Grids)
        {
            string cells = string.Join("; ", grid.Elements.Select(element => $"({element.CellX},{element.CellY},{element.CellZ})"));
            Console.WriteLine($"  grid #{grid.Id} cells={grid.Elements.Count} position={grid.Position} cells={cells}");
        }
    }
}
