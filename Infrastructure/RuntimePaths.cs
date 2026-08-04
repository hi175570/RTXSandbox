namespace RTXSandbox.Infrastructure;

public static class RuntimePaths
{
    public static string ManagedDirectory { get; } =
        Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string OutputRoot { get; } = ResolveOutputRoot();

    private static string ResolveOutputRoot()
    {
        var managed = new DirectoryInfo(ManagedDirectory);
        return managed.Name.Equals("Managed", StringComparison.OrdinalIgnoreCase) && managed.Parent is not null
            ? managed.Parent.FullName
            : managed.FullName;
    }
}
