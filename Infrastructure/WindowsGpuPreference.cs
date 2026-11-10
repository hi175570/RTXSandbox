using Microsoft.Win32;

namespace RTXSandbox.Infrastructure;

/// <summary>
/// Requests the Windows high-performance adapter for the exact game executable.
/// This matters on Optimus laptops: DXR explicitly selects the discrete adapter,
/// while a plain OpenGL context otherwise commonly lands on the integrated GPU.
/// </summary>
internal static class WindowsGpuPreference
{
    private const string RegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static bool EnsureHighPerformanceForCurrentExecutable()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.ProcessPath))
            return false;

        string executable = Path.GetFullPath(Environment.ProcessPath);
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            string existing = key.GetValue(executable) as string ?? string.Empty;
            if (existing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(part => part.Equals("GpuPreference=2", StringComparison.OrdinalIgnoreCase)))
                return false;

            string[] preserved = existing
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => !part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string requested = string.Join(';', preserved.Append("GpuPreference=2")) + ";";
            key.SetValue(executable, requested, RegistryValueKind.String);
            Console.WriteLine($"Windows high-performance GPU preference registered for {executable}.");
            return true;
        }
        catch (Exception error)
        {
            // GPU routing is an optimisation. A locked registry or nonstandard
            // Windows session must not prevent the game from starting.
            Console.Error.WriteLine($"Could not register high-performance GPU preference: {error.Message}");
            return false;
        }
    }
}
