using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RTXSandbox.Rendering;

namespace RTXSandbox.Infrastructure;

public static class AdversarialPerformanceSuite
{
    private static readonly RenderBackendKind[] Renderers =
    {
        RenderBackendKind.Classic,
        RenderBackendKind.PbrForward,
        RenderBackendKind.DeferredLighting,
        RenderBackendKind.HybridPbrRaster,
        RenderBackendKind.SoftwareRayTracing,
        RenderBackendKind.HardwareRayTracing,
    };

    public static int Run(string[] args)
    {
        string requestedProfile = ReadValue(args, "--suite-profile=")?.ToLowerInvariant() ?? "all";
        if (requestedProfile is not ("normal" or "optical" or "all"))
        {
            Console.Error.WriteLine("--suite-profile must be normal, optical, or all.");
            return 2;
        }

        string suiteId = $"{DateTime.UtcNow:yyyyMMddHHmm}-{Guid.NewGuid():N}"[..20];
        var cases = new List<SuiteCase>();
        if (requestedProfile is "normal" or "all")
            cases.AddRange(Renderers.Select(renderer => new SuiteCase(renderer, "normal", "default")));
        if (requestedProfile is "optical" or "all")
        {
            cases.AddRange(Renderers.Select(renderer => new SuiteCase(renderer, "optical", "default")));
            cases.Add(new SuiteCase(RenderBackendKind.HardwareRayTracing, "optical", "wax"));
        }

        Console.WriteLine($"RTX Sandbox adversarial suite {suiteId}: {cases.Count} isolated runs.");
        var results = new List<SuiteResult>(cases.Count);
        foreach (SuiteCase test in cases)
        {
            string runId = CreateRunId(suiteId, test);
            string label = $"adversarial-{test.Profile}-{test.Style}";
            Console.WriteLine($"\n=== {test.Renderer} / {test.Profile} / {test.Style} ===");
            int exitCode = RunChild(test, runId, label);
            string? reportPath = FindReport(label, test.Renderer);
            results.Add(ReadResult(test, exitCode, reportPath));
            CleanupChildStorage(test, runId);
        }

        WriteSuiteReport(suiteId, results);
        int failures = results.Count(result => result.ExitCode != 0 || !result.ReportFound);
        Console.WriteLine($"Suite complete: {results.Count - failures}/{results.Count} runs produced reports; " +
                          $"{results.Count(result => result.Minimum100Fps)} met 100 FPS, " +
                          $"{results.Count(result => result.Target150Fps)} met 150 FPS.");
        return failures == 0 ? 0 : 1;
    }

    private static int RunChild(SuiteCase test, string runId, string label)
    {
        string processPath = Environment.ProcessPath ??
                             throw new InvalidOperationException("Current executable path is unavailable.");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(processPath) ?? Environment.CurrentDirectory,
        };
        if (Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ??
                                   throw new InvalidOperationException("Entry assembly path is unavailable."));
        }
        start.ArgumentList.Add("--world-stream-benchmark");
        start.ArgumentList.Add($"--benchmark-renderer={test.Renderer}");
        start.ArgumentList.Add($"--benchmark-profile={test.Profile}");
        start.ArgumentList.Add($"--benchmark-style={test.Style}");
        start.ArgumentList.Add($"--benchmark-run-id={runId}");
        start.ArgumentList.Add($"--benchmark-label={label}");
        using Process child = Process.Start(start) ??
                              throw new InvalidOperationException("Could not start benchmark child process.");
        child.WaitForExit();
        return child.ExitCode;
    }

    private static string? FindReport(string label, RenderBackendKind renderer)
    {
        string path = Path.Combine(
            RuntimePaths.OutputRoot,
            "Benchmarks",
            "Reports",
            $"latest-{label}-{renderer}.json");
        return File.Exists(path) ? path : null;
    }

    private static SuiteResult ReadResult(SuiteCase test, int exitCode, string? reportPath)
    {
        if (reportPath is null)
            return new SuiteResult(test, exitCode, false, 0, 0, 0, false, false, false, null);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(reportPath));
        JsonElement root = document.RootElement;
        return new SuiteResult(
            test,
            exitCode,
            true,
            ReadDouble(root, "AverageFps"),
            ReadDouble(root, "OnePercentLowFps"),
            ReadDouble(root, "SlowestFrameMilliseconds"),
            ReadBool(root, "AcceptanceNoOneSecondFreeze"),
            ReadBool(root, "AcceptanceMinimum100Fps"),
            ReadBool(root, "Target150Fps"),
            reportPath);
    }

    private static void WriteSuiteReport(string suiteId, IReadOnlyList<SuiteResult> results)
    {
        string reportRoot = Path.Combine(RuntimePaths.OutputRoot, "Benchmarks", "Reports");
        Directory.CreateDirectory(reportRoot);
        string jsonPath = Path.Combine(reportRoot, $"adversarial-suite-{suiteId}.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        var markdown = new StringBuilder()
            .AppendLine("# RTX Sandbox Adversarial Performance Suite")
            .AppendLine()
            .AppendLine($"- Suite: `{suiteId}`")
            .AppendLine("- Every row used a new deterministic world; child and parent both clean its world directory.")
            .AppendLine("- Quality: render scale 100%, renderer quality maximum, Compute PT 50 bounces, DXR 29 bounces.")
            .AppendLine()
            .AppendLine("| Renderer | Profile | Style | Avg FPS | 1% Low | Slowest ms | <1s freeze | >=100 | >=150 | Exit |")
            .AppendLine("|---|---|---|---:|---:|---:|---|---|---|---:|");
        foreach (SuiteResult result in results)
            markdown.AppendLine($"| {result.Test.Renderer} | {result.Test.Profile} | {result.Test.Style} | " +
                                $"{result.AverageFps:0.00} | {result.OnePercentLowFps:0.00} | " +
                                $"{result.SlowestFrameMilliseconds:0.00} | {result.NoOneSecondFreeze} | " +
                                $"{result.Minimum100Fps} | {result.Target150Fps} | {result.ExitCode} |");
        File.WriteAllText(Path.ChangeExtension(jsonPath, ".md"), markdown.ToString());
        File.WriteAllText(Path.Combine(reportRoot, "latest-adversarial-suite.md"), markdown.ToString());
    }

    private static void CleanupChildStorage(SuiteCase test, string runId)
    {
        string worldName = $"adversarial-{test.Profile}-{test.Renderer}-{runId}";
        worldName = worldName[..Math.Min(64, worldName.Length)];
        DeleteInside(Path.Combine(RuntimePaths.OutputRoot, "Benchmarks", "Worlds"), worldName);
        string scratchName = $"scratch-{worldName}";
        scratchName = scratchName[..Math.Min(64, scratchName.Length)];
        DeleteInside(Path.Combine(RuntimePaths.OutputRoot, "Benchmarks", "IO"), scratchName);
    }

    private static void DeleteInside(string rootPath, string name)
    {
        string root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(root, name));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to delete outside benchmark root: {target}");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private static string CreateRunId(string suiteId, SuiteCase test)
    {
        string suffix = $"{suiteId}-{test.Profile[0]}-{(int)test.Renderer}-{test.Style[0]}";
        return suffix[..Math.Min(20, suffix.Length)];
    }

    private static string? ReadValue(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1];

    private static double ReadDouble(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) ? value.GetDouble() : 0;

    private static bool ReadBool(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.GetBoolean();

    public sealed record SuiteCase(RenderBackendKind Renderer, string Profile, string Style);
    public sealed record SuiteResult(
        SuiteCase Test,
        int ExitCode,
        bool ReportFound,
        double AverageFps,
        double OnePercentLowFps,
        double SlowestFrameMilliseconds,
        bool NoOneSecondFreeze,
        bool Minimum100Fps,
        bool Target150Fps,
        string? ReportPath);
}
