using System.Reflection;
using System.Runtime.Loader;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using RTXSandbox.Infrastructure;
using RTXSandbox.Modding;

namespace RTXSandbox.Launcher;

internal static class DesktopPreloader
{
    public static void Run(string gamePath)
    {
        string root = Path.Combine(RuntimePaths.OutputRoot, "BepInEx");
        foreach (string directory in new[] { "core", "plugins", "patchers", "config", "cache" })
            Directory.CreateDirectory(Path.Combine(root, directory));
        string plugins = Environment.GetEnvironmentVariable("RTX_SANDBOX_PLUGIN_ROOT") is { Length: > 0 } path
            ? Path.GetFullPath(path) : Path.Combine(root, "plugins");
        BepInExEnvironment.Initialize(gamePath, plugins);

        using var listener = new PreloaderLog();
        Logger.Listeners.Add(listener);
        try
        {
            EnsureGameNotLoaded();
            Console.WriteLine("BepInEx preloader started (before RTXSandbox assembly load).");
            using var patcher = new AssemblyPatcher((bytes, originalPath) =>
            {
                string name = AssemblyName.GetAssemblyName(originalPath).Name!;
                if (AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == name))
                    throw new InvalidOperationException($"Cannot apply startup patch: {name} was loaded too early.");
                using var stream = new MemoryStream(bytes, writable: false);
                return AssemblyLoadContext.Default.LoadFromStream(stream);
            });
            patcher.AddPatchersFromDirectory(Path.Combine(root, "patchers"));
            EnsureGameNotLoaded();
            int definitions = patcher.PatcherContext.PatchDefinitions.Count;
            // With no patchers, avoid reading every game DLL on each startup.
            // Initialize/Finalizer-only patchers still receive the assembly map.
            if (patcher.PatcherContext.PatcherPlugins.Count > 0)
            {
                patcher.LoadAssemblyDirectories(RuntimePaths.ManagedDirectory);
                // Bootstrap/runtime assemblies are already executing, and cannot
                // be rewritten. All other managed game assemblies stay available.
                var loaded = AssemblyLoadContext.Default.Assemblies.Select(a => a.GetName().Name).ToHashSet();
                foreach (var pair in patcher.PatcherContext.AvailableAssemblies.ToArray())
                {
                    if (!loaded.Contains(pair.Value.Name.Name)) continue;
                    pair.Value.Dispose();
                    patcher.PatcherContext.AvailableAssemblies.Remove(pair.Key);
                    patcher.PatcherContext.AvailableAssembliesPaths.Remove(pair.Key);
                }
                patcher.PatchAndLoad();
            }
            if (listener.Failed)
                throw new InvalidOperationException("A BepInEx preloader patcher failed. See BepInEx/preloader.log; fix/remove the patcher, or set RTX_SANDBOX_DISABLE_BEPINEX=1 to recover.");
            AppContext.SetData("RTXSandbox.Preloader.Status", "Completed");
            AppContext.SetData("RTXSandbox.Preloader.PatchDefinitions", definitions);
            AppContext.SetData("RTXSandbox.Preloader.PatchedAssemblies", patcher.PatcherContext.LoadedAssemblies.Count);
            Console.WriteLine($"BepInEx preloader completed: {definitions} patch definition(s), {patcher.PatcherContext.LoadedAssemblies.Count} patched assembly/assemblies.");
        }
        finally { Logger.Listeners.Remove(listener); }
    }

    private static void EnsureGameNotLoaded()
    {
        if (AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name is "RTXSandbox" or "RTXSandbox.ModAPI"))
            throw new InvalidOperationException("A patcher loaded the game/ModAPI before patching. Use Cecil metadata, not runtime game types, in startup patchers.");
    }

    private sealed class PreloaderLog : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;
        private readonly StreamWriter _writer = new(Path.Combine(RuntimePaths.OutputRoot, "BepInEx", "preloader.log"), append: false) { AutoFlush = true };
        public bool Failed { get; private set; }
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            lock (_writer)
            {
                string message = $"[{eventArgs.Level}:{eventArgs.Source.SourceName}] {eventArgs.Data}";
                _writer.WriteLine(message);
                if ((eventArgs.Level & (LogLevel.Error | LogLevel.Fatal)) != 0)
                {
                    Failed = true;
                    Console.Error.WriteLine(message);
                }
            }
        }
        public void Dispose() => _writer.Dispose();
    }
}
