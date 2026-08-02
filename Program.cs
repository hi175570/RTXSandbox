using RTXSandbox;
using RTXSandbox.Modding;

string? smokeRenderer = args.Contains("--gpu-smoke-pbr", StringComparer.OrdinalIgnoreCase)
    ? "PbrForward"
    : args.Contains("--gpu-smoke-classic", StringComparer.OrdinalIgnoreCase)
        ? "Classic"
        : args.Contains("--gpu-smoke", StringComparer.OrdinalIgnoreCase) ||
          args.Contains("--gpu-smoke-menu", StringComparer.OrdinalIgnoreCase)
            ? "SoftwareRayTracing"
            : null;
if (smokeRenderer is not null)
{
    Environment.SetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE", "1");
    Environment.SetEnvironmentVariable("RTX_SANDBOX_DISABLE_BEPINEX", "1");
    Environment.SetEnvironmentVariable("RTX_SANDBOX_RENDERER", smokeRenderer);
    if (args.Contains("--gpu-smoke-menu", StringComparer.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable("RTX_SANDBOX_OPEN_MAIN_MENU", "1");
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
    if (!string.IsNullOrWhiteSpace(BepInExBootstrap.FailureMessage))
    {
        Console.Error.WriteLine(BepInExBootstrap.FailureMessage);
    }
    return BepInExBootstrap.Started ? 0 : 1;
}
using var game = new SandboxGame();
game.Run();
return 0;
