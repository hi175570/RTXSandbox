using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using RTXSandbox.Infrastructure;

namespace RTXSandbox.Modding;

// Shared by the desktop preloader and the game's chainloader (including Android).
public static class BepInExEnvironment
{
    public static void Initialize(string executablePath, string pluginDirectory)
    {
        if (AppContext.GetData("RTXSandbox.BepInEx.EnvironmentReady") is true) return;
        InitializePaths(executablePath, pluginDirectory);
        InitializeConsoleDriver();
        AppContext.SetData("RTXSandbox.BepInEx.EnvironmentReady", true);
    }

    // BepInEx build 785 calls MonoMod.Utils.PlatformHelper from Paths and
    // ConsoleManager. Modern MonoMod (also required by the optional .NET 10 host) removed that type,
    // so initialize the documented Paths state directly and keep the game's
    // already-active console instead of invoking the obsolete helpers.
    private static void InitializePaths(string executablePath, string pluginDirectory)
    {
        string gameRoot = RuntimePaths.OutputRoot;
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
            [nameof(Paths.ConfigPath)] = Path.Combine(bepinRoot, "config"),
            [nameof(Paths.BepInExConfigPath)] = Path.Combine(bepinRoot, "config", "BepInEx.cfg"),
            [nameof(Paths.PluginPath)] = pluginDirectory,
            [nameof(Paths.PatcherPluginPath)] = Path.Combine(bepinRoot, "patchers"),
            [nameof(Paths.BepInExAssemblyDirectory)] = coreDirectory,
            [nameof(Paths.BepInExAssemblyPath)] = Path.Combine(coreDirectory, "BepInEx.Core.dll"),
            [nameof(Paths.CachePath)] = Path.Combine(bepinRoot, "cache"),
            [nameof(Paths.DllSearchPaths)] = new[] { RuntimePaths.ManagedDirectory, coreDirectory }
        };

        foreach ((string propertyName, object? value) in values)
        {
            PropertyInfo property = typeof(Paths).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMemberException(typeof(Paths).FullName, propertyName);
            property.SetValue(null, value);
        }

        // Cecil reads plugin metadata before the CLR loads the plugin. Its
        // trusted-platform map still contains the build-time Managed paths
        // recorded in the deps file, so register the real framework location
        // as a first-class search directory instead of duplicating core DLLs.
        string[] cecilDirectories = TypeLoader.CecilResolver.GetSearchDirectories();
        foreach (string directory in new[] { coreDirectory, RuntimePaths.ManagedDirectory })
        {
            if (!cecilDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase))
                TypeLoader.CecilResolver.AddSearchDirectory(directory);
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
}
