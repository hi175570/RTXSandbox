using System.Reflection;
using BepInEx;
using BepInEx.NET.Common;
using RTXSandbox.Infrastructure;

namespace RTXSandbox.Modding;

public static class BepInExBootstrap
{
    private static NetChainloader? _chainloader;

    public static bool Started { get; private set; }
    public static string? FailureMessage { get; private set; }
    public static string PluginDirectory => Path.Combine(RuntimePaths.OutputRoot, "BepInEx", "plugins");
    public static string ConfigDirectory => Path.Combine(RuntimePaths.OutputRoot, "BepInEx", "config");
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
            string dataPath = Path.Combine(RuntimePaths.OutputRoot, "Data");
            Directory.CreateDirectory(dataPath);
            InitializePaths(executablePath);
            InitializeConsoleDriver();
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
            DependencyErrors = _chainloader.DependencyErrors
                .Concat(FindUnloadedPluginErrors(LoadedPlugins))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Started = true;
            Console.WriteLine($"BepInEx 6 CoreCLR mod loader active. Plugins: {PluginDirectory}");
            foreach (string error in DependencyErrors)
                Console.Error.WriteLine($"[Mod load error] {error}");
        }
        catch (Exception exception)
        {
            FailureMessage = exception.ToString();
            Console.Error.WriteLine($"BepInEx startup failed; the base game will continue without plugins:\n{exception}");
        }
    }

    // BepInEx build 785 calls MonoMod.Utils.PlatformHelper from Paths and
    // ConsoleManager. Modern MonoMod (also required by the optional .NET 10 host) removed that type,
    // so initialize the documented Paths state directly and keep the game's
    // already-active console instead of invoking the obsolete helpers.
    private static void InitializePaths(string executablePath)
    {
        string gameRoot = Path.GetDirectoryName(executablePath) ?? RuntimePaths.OutputRoot;
        string bepinRoot = Path.Combine(RuntimePaths.OutputRoot, "BepInEx");
        string coreDirectory = Path.Combine(bepinRoot, "core");
        var values = new Dictionary<string, object?>
        {
            [nameof(Paths.ExecutablePath)] = executablePath,
            [nameof(Paths.ProcessName)] = Path.GetFileNameWithoutExtension(executablePath),
            [nameof(Paths.GameRootPath)] = gameRoot,
            [nameof(Paths.GameDataPath)] = RuntimePaths.OutputRoot,
            [nameof(Paths.ManagedPath)] = RuntimePaths.ManagedDirectory,
            [nameof(Paths.BepInExRootPath)] = bepinRoot,
            [nameof(Paths.ConfigPath)] = ConfigDirectory,
            [nameof(Paths.BepInExConfigPath)] = Path.Combine(ConfigDirectory, "BepInEx.cfg"),
            [nameof(Paths.PluginPath)] = PluginDirectory,
            [nameof(Paths.PatcherPluginPath)] = Path.Combine(bepinRoot, "patchers"),
            [nameof(Paths.BepInExAssemblyDirectory)] = coreDirectory,
            [nameof(Paths.BepInExAssemblyPath)] = Path.Combine(coreDirectory, "BepInEx.Core.dll"),
            [nameof(Paths.CachePath)] = Path.Combine(bepinRoot, "cache"),
            [nameof(Paths.DllSearchPaths)] = new[] { RuntimePaths.ManagedDirectory }
        };

        foreach ((string propertyName, object? value) in values)
        {
            PropertyInfo property = typeof(Paths).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMemberException(typeof(Paths).FullName, propertyName);
            property.SetValue(null, value);
        }
    }

    private static void InitializeConsoleDriver()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The host already owns a valid stdout/stderr stream on Unix. The
            // bundled BepInEx Unix console shim is incompatible with modern
            // MonoMod, so leave the existing terminal untouched and prevent
            // the chainloader from trying to create a second native console.
            FieldInfo consoleEnabledField = typeof(ConsoleManager).GetField(
                "ConfigConsoleEnabled",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(ConsoleManager).FullName, "ConfigConsoleEnabled");
            object consoleEnabled = consoleEnabledField.GetValue(null)
                ?? throw new InvalidOperationException("BepInEx console configuration was not initialized.");
            PropertyInfo valueProperty = consoleEnabled.GetType().GetProperty("Value")
                ?? throw new MissingMemberException(consoleEnabled.GetType().FullName, "Value");
            valueProperty.SetValue(consoleEnabled, false);
            return;
        }

        Assembly coreAssembly = typeof(ConsoleManager).Assembly;
        const string driverName = "BepInEx.WindowsConsoleDriver";
        Type driverType = coreAssembly.GetType(driverName, throwOnError: true)!;
        object driver = Activator.CreateInstance(driverType, nonPublic: true)
            ?? throw new InvalidOperationException($"BepInEx console driver '{driverName}' could not be created.");
        MethodInfo initialize = driverType.GetMethod(
            "Initialize",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(bool), typeof(bool) },
            modifiers: null)
            ?? throw new MissingMethodException(driverType.FullName, "Initialize");
        initialize.Invoke(driver, new object[] { true, true });
        PropertyInfo driverProperty = typeof(ConsoleManager).GetProperty(
            "Driver",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(ConsoleManager).FullName, "Driver");
        driverProperty.SetValue(null, driver);
    }

    private static IEnumerable<string> FindUnloadedPluginErrors(IReadOnlyList<LoadedPluginInfo> loadedPlugins)
    {
        var loadedPaths = loadedPlugins
            .Select(plugin => Path.GetFullPath(plugin.Location))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string dllPath in Directory.EnumerateFiles(PluginDirectory, "*.dll", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(dllPath);
            if (loadedPaths.Contains(fullPath)) continue;

            string fileName = Path.GetFileName(fullPath);
            string metadata = string.Empty;
            string? assemblyReadError = null;
            try
            {
                _ = AssemblyName.GetAssemblyName(fullPath);
                metadata = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(fullPath));
            }
            catch (Exception exception)
            {
                assemblyReadError = exception.Message;
            }
            if (assemblyReadError is not null)
            {
                yield return $"{fileName} 無法讀取為 .NET 組件：{assemblyReadError}";
                continue;
            }

            string? targetFramework = FindTargetFramework(metadata);
            bool newerRuntime = targetFramework is not null &&
                Version.TryParse(targetFramework, out Version? targetVersion) &&
                targetVersion.Major > Environment.Version.Major;
            bool hasPluginEntryMetadata = metadata.Contains("BepInPlugin", StringComparison.Ordinal) &&
                                          metadata.Contains("BasePlugin", StringComparison.Ordinal);
            if (!newerRuntime && !hasPluginEntryMetadata) continue;

            var reasons = new List<string>();
            if (newerRuntime)
                reasons.Add($"目標是 .NET {targetFramework}，高於遊戲的 .NET {Environment.Version.Major}.0");
            if (!hasPluginEntryMetadata)
                reasons.Add("DLL 不含 BepInPlugin／BasePlugin 插件入口（通常是空專案或 Plugin.cs 未參與編譯）");
            else
                reasons.Add("BepInEx 找到插件中繼資料但沒有建立插件實例，請檢查缺少的相依 DLL");
            yield return $"{fileName} 未載入：{string.Join("；", reasons)}。請以 netstandard2.1 或不高於遊戲 Runtime 的 .NET 版本重新編譯，並確認插件類別有 [BepInPlugin] 且繼承 BasePlugin。";
        }
    }

    private static string? FindTargetFramework(string metadata)
    {
        const string marker = ".NETCoreApp,Version=v";
        int start = metadata.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        int end = start;
        while (end < metadata.Length && (char.IsDigit(metadata[end]) || metadata[end] == '.')) end++;
        return end > start ? metadata[start..end] : null;
    }
}

public sealed record LoadedPluginInfo(
    string Guid,
    string Name,
    string Version,
    string Location,
    IReadOnlyList<string> Dependencies,
    BasePlugin? Instance);
