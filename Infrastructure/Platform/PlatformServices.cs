using System.Runtime.InteropServices;

namespace RTXSandbox.Infrastructure.Platform;

public enum RuntimePlatform
{
    Windows,
    Linux,
    MacOS,
    Android,
    Unknown,
}

public sealed record PlatformCapabilities(
    RuntimePlatform Platform,
    Architecture ProcessArchitecture,
    bool HasPhysicalKeyboard,
    bool HasMouse,
    bool HasTouch,
    bool SupportsDesktopOpenGl,
    bool SupportsDirect3D12,
    bool SupportsDxr)
{
    public static PlatformCapabilities DetectDesktop() => new(
        DetectPlatform(),
        RuntimeInformation.ProcessArchitecture,
        HasPhysicalKeyboard: true,
        HasMouse: true,
        HasTouch: false,
        SupportsDesktopOpenGl: true,
        SupportsDirect3D12: OperatingSystem.IsWindows(),
        SupportsDxr: OperatingSystem.IsWindows());

    private static RuntimePlatform DetectPlatform()
    {
        if (OperatingSystem.IsAndroid()) return RuntimePlatform.Android;
        if (OperatingSystem.IsWindows()) return RuntimePlatform.Windows;
        if (OperatingSystem.IsLinux()) return RuntimePlatform.Linux;
        if (OperatingSystem.IsMacOS()) return RuntimePlatform.MacOS;
        return RuntimePlatform.Unknown;
    }
}

public interface IPlatformStorage
{
    string DataRoot { get; }
    string SavesRoot { get; }
    string SettingsPath { get; }
}

public sealed class DirectoryPlatformStorage : IPlatformStorage
{
    public string DataRoot { get; }
    public string SavesRoot => Path.Combine(DataRoot, "Saves");
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");

    public DirectoryPlatformStorage(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("A platform data root is required.", nameof(dataRoot));
        DataRoot = Path.GetFullPath(dataRoot);
    }
}
