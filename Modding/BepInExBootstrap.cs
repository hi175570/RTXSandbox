using System.Reflection;
using BepInEx;
using BepInEx.NET.Common;

namespace RTXSandbox.Modding;

public static class BepInExBootstrap
{
    private static NetChainloader? _chainloader;

    public static bool Started { get; private set; }
    public static string? FailureMessage { get; private set; }
    public static string PluginDirectory => Path.Combine(AppContext.BaseDirectory, "BepInEx", "plugins");
    public static string ConfigDirectory => Path.Combine(AppContext.BaseDirectory, "BepInEx", "config");
    public static IReadOnlyList<LoadedPluginInfo> LoadedPlugins { get; private set; } = Array.Empty<LoadedPluginInfo>();
    public static IReadOnlyList<string> DependencyErrors { get; private set; } = Array.Empty<string>();

    public static void Start()
    {
        if (Started || Environment.GetEnvironmentVariable("RTX_SANDBOX_DISABLE_BEPINEX") == "1")
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(PluginDirectory);
            Directory.CreateDirectory(ConfigDirectory);
            string gameAssemblyPath = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("The game entry assembly path is unavailable.");
            string executablePath = Path.ChangeExtension(gameAssemblyPath, ".exe");
            if (!File.Exists(executablePath))
            {
                executablePath = Environment.ProcessPath ?? gameAssemblyPath;
            }
            string dataPath = Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(dataPath);
            Paths.SetExecutablePath(
                executablePath,
                Path.Combine(AppContext.BaseDirectory, "BepInEx"),
                AppContext.BaseDirectory);
            ConsoleManager.Initialize(alreadyActive: true, useManagedEncoder: true);
            _chainloader = new NetChainloader();
            _chainloader.Initialize();
            _chainloader.Execute();
            LoadedPlugins = _chainloader.Plugins.Values
                .OrderBy(plugin => plugin.Metadata.Name, StringComparer.OrdinalIgnoreCase)
                .Select(plugin => new LoadedPluginInfo(
                    plugin.Metadata.GUID,
                    plugin.Metadata.Name,
                    plugin.Metadata.Version.ToString(),
                    plugin.Location,
                    plugin.Dependencies.Select(dependency => dependency.DependencyGUID).ToArray(),
                    plugin.Instance as BasePlugin))
                .ToArray();
            DependencyErrors = _chainloader.DependencyErrors.ToArray();
            Started = true;
            Console.WriteLine($"BepInEx 6 CoreCLR mod loader active. Plugins: {PluginDirectory}");
        }
        catch (Exception exception)
        {
            FailureMessage = exception.ToString();
            Console.Error.WriteLine($"BepInEx startup failed; the base game will continue without plugins:\n{exception}");
        }
    }
}

public sealed record LoadedPluginInfo(
    string Guid,
    string Name,
    string Version,
    string Location,
    IReadOnlyList<string> Dependencies,
    BasePlugin? Instance);
