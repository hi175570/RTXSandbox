using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text;

namespace RTXSandbox.Modding;

public sealed record ModWorkspaceEntry(
    string FullPath,
    string RelativePath,
    long Length,
    bool IsText);

public sealed record ModCompilationResult(
    bool Success,
    string SourcePath,
    string? AssemblyPath,
    bool OrganizedLooseFiles,
    int ReferenceCount,
    string Log);

/// <summary>
/// Player-driven file operations for the in-game mod workspace. Nothing in this
/// class watches, compiles, moves, overwrites, or removes a player file unless
/// the corresponding method is explicitly called by the UI.
/// </summary>
public sealed class ModWorkspace
{
    private const long MaximumEditableBytes = 4L * 1024L * 1024L;
    private readonly string _pluginRoot;
    private readonly string _pluginRootPrefix;
    private readonly string _managedRoot;
    private readonly string _backupRoot;
    private readonly string _trashRoot;

    public ModWorkspace(
        string pluginRoot,
        string managedRoot,
        string? backupRoot = null,
        string? trashRoot = null)
    {
        _pluginRoot = NormalizeDirectory(pluginRoot);
        _pluginRootPrefix = _pluginRoot + Path.DirectorySeparatorChar;
        _managedRoot = NormalizeDirectory(managedRoot);
        string stateRoot = Path.GetDirectoryName(_pluginRoot) ?? _pluginRoot;
        _backupRoot = NormalizeDirectory(backupRoot ?? Path.Combine(stateRoot, "editor-backups"));
        _trashRoot = NormalizeDirectory(trashRoot ?? Path.Combine(stateRoot, "trash"));
    }

    public string PluginRoot => _pluginRoot;

    public IReadOnlyList<ModWorkspaceEntry> EnumerateFiles()
    {
        Directory.CreateDirectory(_pluginRoot);
        var entries = new List<ModWorkspaceEntry>();
        foreach (string path in Directory.EnumerateFiles(_pluginRoot, "*", SearchOption.AllDirectories))
        {
            string fullPath = ValidateExistingFile(path);
            var info = new FileInfo(fullPath);
            entries.Add(new ModWorkspaceEntry(
                fullPath,
                Path.GetRelativePath(_pluginRoot, fullPath),
                info.Length,
                IsTextFile(fullPath, info.Length)));
        }

        return entries
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string ReadText(string path)
    {
        string fullPath = ValidateExistingFile(path);
        var info = new FileInfo(fullPath);
        if (!IsTextFile(fullPath, info.Length))
            throw new InvalidOperationException("選取的檔案不是可安全編輯的文字檔，或大於 4 MiB。");
        return File.ReadAllText(fullPath, DetectTextEncoding(fullPath));
    }

    public string SaveText(string path, string contents)
    {
        string fullPath = ValidateExistingFile(path);
        _ = ReadText(fullPath);
        string backupPath = GetBackupPath(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(fullPath, backupPath, overwrite: true);

        string temporaryPath = fullPath + $".save-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return backupPath;
    }

    public string RollbackText(string path)
    {
        string fullPath = ValidateExistingFile(path);
        string backupPath = GetBackupPath(fullPath);
        if (!File.Exists(backupPath))
            throw new InvalidOperationException("這個檔案還沒有可回滾的保存版本。");
        File.Copy(backupPath, fullPath, overwrite: true);
        return ReadText(fullPath);
    }

    public string MoveToFolder(string path, string destinationRelativeFolder)
    {
        string fullPath = ValidateExistingFile(path);
        if (Path.IsPathRooted(destinationRelativeFolder))
            throw new InvalidOperationException("移動目標必須是插件目錄內的相對資料夾。");
        string destinationDirectory = ValidateInsideRoot(
            Path.Combine(_pluginRoot, string.IsNullOrWhiteSpace(destinationRelativeFolder)
                ? "."
                : destinationRelativeFolder.Trim()));
        Directory.CreateDirectory(destinationDirectory);
        string destinationPath = ValidateInsideRoot(Path.Combine(destinationDirectory, Path.GetFileName(fullPath)));
        if (File.Exists(destinationPath))
            throw new IOException($"目標已存在：{Path.GetRelativePath(_pluginRoot, destinationPath)}");
        File.Move(fullPath, destinationPath);
        return destinationPath;
    }

    public string MoveToTrash(string path)
    {
        string fullPath = ValidateExistingFile(path);
        string relative = Path.GetRelativePath(_pluginRoot, fullPath);
        string trashBatch = Path.Combine(_trashRoot, DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
        string destinationPath = Path.GetFullPath(Path.Combine(trashBatch, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Move(fullPath, destinationPath);
        return destinationPath;
    }

    public ModCompilationResult Compile(string sourcePath, TimeSpan? timeout = null)
    {
        string currentSource = ValidateExistingFile(sourcePath);
        _ = ReadText(currentSource);
        bool organized = false;
        try
        {
            (currentSource, organized) = OrganizeLooseSource(currentSource);
            string sourceName = Path.GetFileNameWithoutExtension(currentSource);
            string assemblyName = SanitizeAssemblyName(sourceName);
            if (Path.GetExtension(currentSource).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                assemblyName += ".Compiled";
            string outputDirectory = Path.GetDirectoryName(currentSource)!;
            string outputAssembly = Path.Combine(outputDirectory, assemblyName + ".dll");

            IReadOnlyList<ManagedReference> references = DiscoverReferences(outputAssembly, assemblyName);
            string temporaryDirectory = Path.Combine(
                Path.GetTempPath(), "rtxsandbox-mod-build", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            string projectPath = Path.Combine(temporaryDirectory, "ModBuild.csproj");
            try
            {
                File.WriteAllText(projectPath, CreateProject(currentSource, assemblyName, references),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                ProcessResult process = RunDotnetBuild(
                    projectPath,
                    outputDirectory,
                    timeout ?? TimeSpan.FromMinutes(2));
                bool success = process.ExitCode == 0 && File.Exists(outputAssembly);
                string log = process.Output.Trim();
                if (success)
                {
                    log = $"net6.0 編譯成功；引用 {references.Count} 個遊戲／插件組件。" +
                          (string.IsNullOrWhiteSpace(log) ? string.Empty : Environment.NewLine + log);
                }
                else if (string.IsNullOrWhiteSpace(log))
                {
                    log = "dotnet build 沒有產生 DLL。請確認已安裝可建置 net6.0 的 .NET SDK。";
                }

                return new ModCompilationResult(
                    success,
                    currentSource,
                    success ? outputAssembly : null,
                    organized,
                    references.Count,
                    log);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temporaryDirectory))
                        Directory.Delete(temporaryDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // A locked SDK scratch file must not turn a successful build into
                    // a reported failure. This directory never contains player source.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same best-effort cleanup rule as above.
                }
            }
        }
        catch (Exception exception)
        {
            return new ModCompilationResult(
                false,
                currentSource,
                null,
                organized,
                0,
                exception.Message);
        }
    }

    private (string SourcePath, bool Organized) OrganizeLooseSource(string sourcePath)
    {
        string? parent = Path.GetDirectoryName(sourcePath);
        if (!string.Equals(parent, _pluginRoot, StringComparison.OrdinalIgnoreCase))
            return (sourcePath, false);

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string folderName = SanitizeFolderName(stem);
        string destinationDirectory = ValidateInsideRoot(Path.Combine(_pluginRoot, folderName));
        string destinationSource = ValidateInsideRoot(Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)));
        if (File.Exists(destinationSource))
            throw new IOException($"無法整理散裝檔案，目標已存在：{Path.GetRelativePath(_pluginRoot, destinationSource)}");

        var companions = new List<(string Source, string Destination)>();
        if (!Path.GetExtension(sourcePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string suffix in new[] { ".dll", ".pdb", ".deps.json" })
            {
                string companion = Path.Combine(_pluginRoot, stem + suffix);
                if (!File.Exists(companion)) continue;
                string destination = Path.Combine(destinationDirectory, Path.GetFileName(companion));
                if (File.Exists(destination))
                    throw new IOException($"無法整理散裝檔案，目標已存在：{Path.GetRelativePath(_pluginRoot, destination)}");
                companions.Add((companion, destination));
            }
        }

        Directory.CreateDirectory(destinationDirectory);
        var moved = new List<(string Source, string Destination)>();
        try
        {
            File.Move(sourcePath, destinationSource);
            moved.Add((sourcePath, destinationSource));
            foreach ((string companion, string destination) in companions)
            {
                File.Move(companion, destination);
                moved.Add((companion, destination));
            }
        }
        catch
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                (string original, string destination) = moved[i];
                if (File.Exists(destination) && !File.Exists(original))
                    File.Move(destination, original);
            }
            throw;
        }
        return (destinationSource, true);
    }

    private IReadOnlyList<ManagedReference> DiscoverReferences(string outputAssembly, string targetAssemblyName)
    {
        var paths = new List<string>();
        if (Directory.Exists(_managedRoot))
            paths.AddRange(Directory.EnumerateFiles(_managedRoot, "*.dll", SearchOption.AllDirectories));
        if (Directory.Exists(_pluginRoot))
            paths.AddRange(Directory.EnumerateFiles(_pluginRoot, "*.dll", SearchOption.AllDirectories));

        var references = new Dictionary<string, ManagedReference>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, outputAssembly, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                AssemblyName assembly = AssemblyName.GetAssemblyName(fullPath);
                string? name = assembly.Name;
                if (string.IsNullOrWhiteSpace(name) ||
                    name.Equals(targetAssemblyName, StringComparison.OrdinalIgnoreCase) ||
                    IsNewerThanNet6(fullPath))
                {
                    continue;
                }
                references.TryAdd(name, new ManagedReference(name, fullPath));
            }
            catch (BadImageFormatException)
            {
                // Native DLLs in runtimes are deliberately not compiler references.
            }
            catch (FileLoadException)
            {
                // A malformed or unsupported assembly is not a usable prerequisite.
            }
        }
        return references.Values.OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsNewerThanNet6(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string metadata = Encoding.UTF8.GetString(bytes);
        const string marker = ".NETCoreApp,Version=v";
        int index = metadata.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return false;
        index += marker.Length;
        int end = index;
        while (end < metadata.Length && (char.IsDigit(metadata[end]) || metadata[end] == '.')) end++;
        return Version.TryParse(metadata[index..end], out Version? version) && version.Major > 6;
    }

    private static string CreateProject(
        string sourcePath,
        string assemblyName,
        IReadOnlyList<ManagedReference> references)
    {
        static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
        var project = new StringBuilder();
        project.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        project.AppendLine("  <PropertyGroup>");
        project.AppendLine("    <TargetFramework>net6.0</TargetFramework>");
        project.AppendLine("    <OutputType>Library</OutputType>");
        project.AppendLine($"    <AssemblyName>{Escape(assemblyName)}</AssemblyName>");
        project.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        project.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        project.AppendLine("    <Nullable>enable</Nullable>");
        project.AppendLine("    <LangVersion>latest</LangVersion>");
        project.AppendLine("    <CheckEolTargetFramework>false</CheckEolTargetFramework>");
        project.AppendLine("    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>");
        project.AppendLine("  </PropertyGroup>");
        project.AppendLine("  <ItemGroup>");
        project.AppendLine($"    <Compile Include=\"{Escape(sourcePath)}\" Link=\"{Escape(Path.GetFileNameWithoutExtension(sourcePath))}.cs\" />");
        project.AppendLine("  </ItemGroup>");
        if (references.Count > 0)
        {
            project.AppendLine("  <ItemGroup>");
            foreach (ManagedReference reference in references)
            {
                project.AppendLine($"    <Reference Include=\"{Escape(reference.Name)}\">");
                project.AppendLine($"      <HintPath>{Escape(reference.Path)}</HintPath>");
                project.AppendLine("      <Private>false</Private>");
                project.AppendLine("    </Reference>");
            }
            project.AppendLine("  </ItemGroup>");
        }
        project.AppendLine("</Project>");
        return project.ToString();
    }

    private static ProcessResult RunDotnetBuild(string projectPath, string outputDirectory, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 dotnet。請先安裝 .NET SDK（只有 Runtime 不足以編譯模組）。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        int timeoutMilliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"模組編譯超過 {timeout.TotalSeconds:0} 秒，已停止編譯程序；原始碼仍保留。");
        }
        Task.WaitAll(stdout, stderr);
        string output = string.Join(Environment.NewLine,
            new[] { stdout.Result.Trim(), stderr.Result.Trim() }.Where(value => value.Length > 0));
        return new ProcessResult(process.ExitCode, output);
    }

    private string ValidateExistingFile(string path)
    {
        string fullPath = ValidateInsideRoot(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("模組檔案不存在。", fullPath);
        return fullPath;
    }

    private string ValidateInsideRoot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_pluginRootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, _pluginRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("操作目標超出插件目錄，已拒絕執行。");
        }
        return fullPath;
    }

    private string GetBackupPath(string fullPath)
    {
        string relative = Path.GetRelativePath(_pluginRoot, fullPath);
        return Path.GetFullPath(Path.Combine(_backupRoot, relative + ".bak"));
    }

    private static bool IsTextFile(string path, long length)
    {
        if (length > MaximumEditableBytes) return false;
        try
        {
            Encoding encoding = DetectTextEncoding(path);
            using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[4096];
            int count = reader.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < count; i++)
            {
                char value = buffer[i];
                if (value == '\0' || (char.IsControl(value) && value is not '\r' and not '\n' and not '\t' and not '\f'))
                    return false;
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static Encoding DetectTextEncoding(string path)
    {
        Span<byte> bom = stackalloc byte[4];
        using (FileStream stream = File.OpenRead(path))
        {
            int count = stream.Read(bom);
            if (count >= 4 && bom[0] == 0xff && bom[1] == 0xfe && bom[2] == 0x00 && bom[3] == 0x00) return Encoding.UTF32;
            if (count >= 2 && bom[0] == 0xff && bom[1] == 0xfe) return Encoding.Unicode;
            if (count >= 2 && bom[0] == 0xfe && bom[1] == 0xff) return Encoding.BigEndianUnicode;
            if (count >= 3 && bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) return Encoding.UTF8;
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string SanitizeFolderName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Mod" : sanitized;
    }

    private static string SanitizeAssemblyName(string value)
    {
        string sanitized = new(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "CompiledMod" : sanitized;
    }

    private sealed record ManagedReference(string Name, string Path);
    private sealed record ProcessResult(int ExitCode, string Output);
}
