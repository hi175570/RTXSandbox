using RTXSandbox;
using RTXSandbox.Modding;

string? smokeRenderer = args.Contains("--gpu-smoke-hardware", StringComparer.OrdinalIgnoreCase)
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
    if (args.Contains("--gpu-smoke-hardware", StringComparer.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_REQUIRE_HARDWARE_RT", "1");
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
