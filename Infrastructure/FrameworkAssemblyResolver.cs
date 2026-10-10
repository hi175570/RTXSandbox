using System.Reflection;
using System.Runtime.Loader;

namespace RTXSandbox.Infrastructure;

/// <summary>
/// Resolves the self-hosted BepInEx runtime from its conventional
/// BepInEx/core directory. The game and rendering dependencies remain in
/// Managed; framework assemblies do not need to be duplicated there.
/// </summary>
public static class FrameworkAssemblyResolver
{
    private static int _installed;
    private static string? _coreDirectory;

    public static string CoreDirectory => _coreDirectory ??
        Path.Combine(RuntimePaths.OutputRoot, "BepInEx", "core");

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        _coreDirectory = Path.GetFullPath(
            Path.Combine(RuntimePaths.OutputRoot, "BepInEx", "core"));
        AssemblyLoadContext.Default.Resolving += ResolveFromFrameworkDirectory;
    }

    private static Assembly? ResolveFromFrameworkDirectory(
        AssemblyLoadContext context,
        AssemblyName requestedAssembly)
    {
        string? simpleName = requestedAssembly.Name;
        if (string.IsNullOrWhiteSpace(simpleName) ||
            !string.Equals(simpleName, Path.GetFileName(simpleName), StringComparison.Ordinal))
        {
            return null;
        }

        string candidate = Path.Combine(CoreDirectory, $"{simpleName}.dll");
        if (!File.Exists(candidate)) return null;

        AssemblyName availableAssembly = AssemblyName.GetAssemblyName(candidate);
        return AssemblyName.ReferenceMatchesDefinition(requestedAssembly, availableAssembly)
            ? context.LoadFromAssemblyPath(candidate)
            : null;
    }
}
