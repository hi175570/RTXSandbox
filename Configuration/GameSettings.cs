using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RTXSandbox.Rendering;

namespace RTXSandbox.Configuration;

using RTXSandbox.Infrastructure;

public sealed class GameSettings
{
    public const int CurrentVersion = 5;

    public int Version { get; set; } = CurrentVersion;
    public int WindowWidth { get; set; } = 960;
    public int WindowHeight { get; set; } = 540;
    public bool VSync { get; set; } = true;
    public int FpsLimit { get; set; } = 120;
    public float RenderScale { get; set; } = 1.0f;
    public RenderBackendKind Renderer { get; set; } = RenderBackendKind.SoftwareRayTracing;
    public int PathBounces { get; set; } = 3;
    public int PathSamplesPerFrame { get; set; } = 1;
    public int PathMaxAccumulatedSamples { get; set; } = 4096;
    public int DeferredSpecularQuality { get; set; } = 2;
    public int DeferredReflectionQuality { get; set; } = 2;
    public int DeferredRendererQuality { get; set; } = 2;
    public int DeferredGBufferQuality { get; set; } = 2;
    public int DeferredLightingQuality { get; set; } = 2;
    public int DeferredShadowQuality { get; set; } = 2;
    public int DeferredAmbientOcclusionQuality { get; set; } = 2;
    public int DeferredWaterQuality { get; set; } = 2;
    public int DeferredTransparencyQuality { get; set; } = 2;
    public int DeferredFogQuality { get; set; } = 1;
    public int DeferredPostProcessingQuality { get; set; } = 2;
    public bool DeferredHdr { get; set; } = true;
    public bool DeferredBloom { get; set; } = true;
    public int DeferredToneMapping { get; set; } = 2;
    public DeferredDebugView DeferredDebugView { get; set; }
    public int PbrSpecularQuality { get; set; } = 2;
    public int PbrReflectionQuality { get; set; } = 2;
    public int ClassicLightingQuality { get; set; } = 1;
    public int HardwareRayTracingQuality { get; set; } = 2;
    public HardwareRayTracingRenderStyle HardwareRayTracingRenderStyle { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HardwareRayTracingWaxMode { get; set; }
    public bool HardwareRayTracingDenoiser { get; set; } = true;
    public float Exposure { get; set; } = 1.0f;
    public float FieldOfView { get; set; } = 72f;
    public float MouseSensitivity { get; set; } = 0.11f;
    public float SelectionDistance { get; set; } = 8f;
    public float TimeOfDay { get; set; } = 8.5f;
    public float DayLengthMinutes { get; set; } = 12f;
    public bool PauseTime { get; set; }
    public int WorldSeed { get; set; } = 1337;
    public string WorldName { get; set; } = "world1";
    public float AutosaveMinutes { get; set; } = 2f;
    public bool StartInFlightMode { get; set; }
    public bool StartWithNoClip { get; set; }
    public string SelectedBlockKey { get; set; } = "rtxsandbox:stone";
    public string EditorTool { get; set; } = "Place";
    public int BrushRadius { get; set; } = 2;
    public int ExplosionRadius { get; set; } = 4;
    public string[] CreativeHotbar { get; set; } = Array.Empty<string>();
    public int CreativeHotbarSlot { get; set; }
    public string[] FavoriteBlocks { get; set; } = Array.Empty<string>();
    public string[] RecentBlocks { get; set; } = Array.Empty<string>();
    public bool ShowCrosshair { get; set; } = true;
    public float CrosshairSize { get; set; } = 8f;
    public float CrosshairOpacity { get; set; } = 0.95f;
    public bool ContextualCrosshairColor { get; set; } = true;
    public int PrecisionGridDivisor { get; set; } = 16;

    [JsonIgnore]
    public string SourcePath { get; private set; } = Path.Combine(Environment.CurrentDirectory, "settings.json");
    [JsonIgnore]
    public bool RecoveredFromBackup { get; private set; }

    public static GameSettings Load(string? explicitPath = null)
    {
        string workingCopy = Path.Combine(Environment.CurrentDirectory, "settings.json");
        string outputCopy = Path.Combine(RuntimePaths.OutputRoot, "settings.json");
        string path = !string.IsNullOrWhiteSpace(explicitPath)
            ? Path.GetFullPath(explicitPath)
            : File.Exists(workingCopy) ? workingCopy : outputCopy;

        GameSettings settings = new();
        bool recovered = false;
        if (!TryRead(path, out settings, out string? primaryError))
        {
            string backupPath = path + ".bak";
            if (TryRead(backupPath, out GameSettings backup, out string? backupError))
            {
                settings = backup;
                recovered = true;
                Console.Error.WriteLine($"Could not read settings.json; recovered '{backupPath}': " +
                                        (primaryError ?? "primary file is missing"));
            }
            else if (File.Exists(path) || File.Exists(backupPath))
            {
                settings = new GameSettings();
                Console.Error.WriteLine($"Could not read settings.json; defaults will be used: {primaryError ?? "primary file is missing"}" +
                                        (File.Exists(backupPath) ? $" Backup also failed: {backupError}" : string.Empty));
            }
        }

        settings.SourcePath = !string.IsNullOrWhiteSpace(explicitPath)
            ? path
            : File.Exists(path) ? path : workingCopy;
        settings.RecoveredFromBackup = recovered;
        settings.Validate();
        return settings;
    }

    public void Save(string? explicitPath = null)
    {
        Validate();
        if (!string.IsNullOrWhiteSpace(explicitPath)) SourcePath = Path.GetFullPath(explicitPath);
        string temporaryPath = SourcePath + ".tmp";
        string backupPath = SourcePath + ".bak";
        try
        {
            string? directory = Path.GetDirectoryName(SourcePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(this, JsonOptions());
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096, leaveOpen: true);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(SourcePath))
            {
                File.Move(temporaryPath, SourcePath);
            }
            else if (RecoveredFromBackup)
            {
                // Keep the known-good backup instead of replacing it with the corrupt primary.
                File.Move(temporaryPath, SourcePath, overwrite: true);
            }
            else
            {
                try
                {
                    File.Replace(temporaryPath, SourcePath, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(SourcePath, backupPath, overwrite: true);
                    File.Move(temporaryPath, SourcePath, overwrite: true);
                }
            }
            RecoveredFromBackup = false;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not save settings to '{SourcePath}': {exception.Message}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
            }
        }
    }

    private void Validate()
    {
        Version = CurrentVersion;
        WindowWidth = Math.Clamp(WindowWidth, 640, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 360, 4320);
        FpsLimit = Math.Clamp(FpsLimit, 30, 360);
        RenderScale = Math.Clamp(RenderScale, 0.35f, 1f);
        if (!RenderBackendCatalog.IsImplemented(Renderer)) Renderer = RenderBackendKind.SoftwareRayTracing;
        PathBounces = Math.Clamp(PathBounces, 1, 12);
        PathSamplesPerFrame = Math.Clamp(PathSamplesPerFrame, 1, 8);
        PathMaxAccumulatedSamples = Math.Clamp(PathMaxAccumulatedSamples, 64, 16384);
        DeferredSpecularQuality = Math.Clamp(DeferredSpecularQuality, 0, 3);
        DeferredReflectionQuality = Math.Clamp(DeferredReflectionQuality, 0, 3);
        DeferredRendererQuality = Math.Clamp(DeferredRendererQuality, 0, 3);
        DeferredGBufferQuality = Math.Clamp(DeferredGBufferQuality, 0, 3);
        DeferredLightingQuality = Math.Clamp(DeferredLightingQuality, 0, 3);
        DeferredShadowQuality = Math.Clamp(DeferredShadowQuality, 0, 3);
        DeferredAmbientOcclusionQuality = Math.Clamp(DeferredAmbientOcclusionQuality, 0, 3);
        DeferredWaterQuality = Math.Clamp(DeferredWaterQuality, 0, 3);
        DeferredTransparencyQuality = Math.Clamp(DeferredTransparencyQuality, 0, 3);
        DeferredFogQuality = Math.Clamp(DeferredFogQuality, 0, 3);
        DeferredPostProcessingQuality = Math.Clamp(DeferredPostProcessingQuality, 0, 3);
        DeferredToneMapping = Math.Clamp(DeferredToneMapping, 0, 2);
        if (!Enum.IsDefined(DeferredDebugView)) DeferredDebugView = DeferredDebugView.FinalImage;
        PbrSpecularQuality = Math.Clamp(PbrSpecularQuality, 0, 3);
        PbrReflectionQuality = Math.Clamp(PbrReflectionQuality, 0, 3);
        ClassicLightingQuality = Math.Clamp(ClassicLightingQuality, 0, 2);
        HardwareRayTracingQuality = Math.Clamp(HardwareRayTracingQuality, 0, 3);
        if (HardwareRayTracingWaxMode && HardwareRayTracingRenderStyle == HardwareRayTracingRenderStyle.Default)
            HardwareRayTracingRenderStyle = HardwareRayTracingRenderStyle.WaxMode;
        HardwareRayTracingWaxMode = false;
        if (!Enum.IsDefined(HardwareRayTracingRenderStyle))
            HardwareRayTracingRenderStyle = HardwareRayTracingRenderStyle.Default;
        Exposure = Math.Clamp(Exposure, 0.35f, 3f);
        FieldOfView = Math.Clamp(FieldOfView, 45f, 105f);
        MouseSensitivity = Math.Clamp(MouseSensitivity, 0.02f, 0.5f);
        SelectionDistance = Math.Clamp(SelectionDistance, 3f, 16f);
        WorldName = string.IsNullOrWhiteSpace(WorldName) ? "world1" : WorldName.Trim();
        TimeOfDay = ((TimeOfDay % 24f) + 24f) % 24f;
        DayLengthMinutes = Math.Clamp(DayLengthMinutes, 1f, 120f);
        AutosaveMinutes = Math.Clamp(AutosaveMinutes, 0.5f, 30f);
        BrushRadius = Math.Clamp(BrushRadius, 1, 8);
        ExplosionRadius = Math.Clamp(ExplosionRadius, 1, 12);
        CreativeHotbar ??= Array.Empty<string>();
        CreativeHotbar = CreativeHotbar.Take(9).ToArray();
        CreativeHotbarSlot = Math.Clamp(CreativeHotbarSlot, 0, 8);
        FavoriteBlocks ??= Array.Empty<string>();
        FavoriteBlocks = FavoriteBlocks.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
        RecentBlocks ??= Array.Empty<string>();
        RecentBlocks = RecentBlocks.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
        CrosshairSize = Math.Clamp(CrosshairSize, 3f, 20f);
        CrosshairOpacity = Math.Clamp(CrosshairOpacity, 0.2f, 1f);
        if (!new[] { 2, 4, 6, 8, 16, 32, 64 }.Contains(PrecisionGridDivisor)) PrecisionGridDivisor = 16;
    }

    private static bool TryRead(string path, out GameSettings settings, out string? error)
    {
        settings = new GameSettings();
        error = null;
        if (!File.Exists(path)) return false;
        try
        {
            settings = JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path), JsonOptions())
                       ?? throw new InvalidDataException("Settings document is empty.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
