using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using RTXSandbox.Infrastructure;
using RTXSandbox.Launcher;

// This assembly must never reference RTXSandbox or its ModAPI. The game can
// only be loaded after Cecil has applied the preloader patches.
FrameworkAssemblyResolver.Install();
string gamePath = Path.Combine(AppContext.BaseDirectory, "RTXSandbox.dll");
// Use the launcher's own deps file so RTXSandbox is not in CoreCLR's trusted
// platform assembly list (TPA). TPA would silently prefer the on-disk original
// over LoadFromStream's patched bytes. Resolve game dependencies separately.
var dependencies = new AssemblyDependencyResolver(gamePath);
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string? path = dependencies.ResolveAssemblyToPath(name);
    if (path is null && name.Name is { } simple && Path.GetFileName(simple) == simple)
    {
        string candidate = Path.Combine(AppContext.BaseDirectory, simple + ".dll");
        if (File.Exists(candidate)) path = candidate;
    }
    return path is not null && File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
{
    string? path = dependencies.ResolveUnmanagedDllToPath(name);
    return path is not null ? NativeLibrary.Load(path) : IntPtr.Zero;
};
try
{
    bool skip = Environment.GetEnvironmentVariable("RTX_SANDBOX_DISABLE_BEPINEX") == "1" ||
                args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase) ||
                                arg.Equals("--physics-probe", StringComparison.OrdinalIgnoreCase) ||
                                arg.Equals("--world-stream-benchmark", StringComparison.OrdinalIgnoreCase) ||
                                arg.Equals("--adversarial-performance-suite", StringComparison.OrdinalIgnoreCase) ||
                                (arg.StartsWith("--gpu-smoke", StringComparison.OrdinalIgnoreCase) &&
                                 !args.Contains("--gpu-smoke-mod", StringComparer.OrdinalIgnoreCase)));
    if (!skip) DesktopPreloader.Run(gamePath);
    else AppContext.SetData("RTXSandbox.Preloader.Status", "Disabled");

    Assembly game = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == "RTXSandbox")
                    ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(gamePath);
    MethodInfo entry = game.EntryPoint ?? throw new MissingMethodException("RTXSandbox entry point is missing.");
    object? result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { args });
    return result is int exitCode ? exitCode : 0;
}
catch (Exception exception)
{
    Exception cause = exception is TargetInvocationException { InnerException: not null } invocation
        ? invocation.InnerException! : exception;
    Console.Error.WriteLine($"RTX Sandbox startup failed: {cause}");
    try
    {
        Directory.CreateDirectory(RuntimePaths.OutputRoot);
        File.WriteAllText(Path.Combine(RuntimePaths.OutputRoot, "bepinex_preloader_error.log"), cause.ToString());
    }
    catch (Exception logError) { Console.Error.WriteLine($"Could not write startup log: {logError.Message}"); }
    return 1;
}
