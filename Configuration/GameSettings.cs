using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RTXSandbox.Rendering;

namespace RTXSandbox.Configuration;

using RTXSandbox.Infrastructure;

public sealed class GameSettings
{
    public const int CurrentVersion = 13;

    public int Version { get; set; } = CurrentVersion;
    public int WindowWidth { get; set; } = 960;
    public int WindowHeight { get; set; } = 540;
    public bool VSync { get; set; } = true;
    public int FpsLimit { get; set; } = 120;
    public float RenderScale { get; set; } = 1.0f;
    public UpscalingMode UpscalingMode { get; set; } = UpscalingMode.None;
    public RenderBackendKind Renderer { get; set; } = RenderBackendKind.SoftwareRayTracing;
    public int PathBounces { get; set; } = 3;
    public int PathSamplesPerFrame { get; set; } = 1;
    public int PathMaxAccumulatedSamples { get; set; } = 4096;
    public float PathRayDistance { get; set; } = 140f;
    public float PathDirectLightingStrength { get; set; } = 1f;
    public float PathEnvironmentIntensity { get; set; } = 1f;
    public float PathEmissionIntensity { get; set; } = 1f;
    public float PathReflectionStrength { get; set; } = 1f;
    public int PathRussianRouletteStart { get; set; } = 2;
    public int DeferredSpecularQuality { get; set; } = 2;
    public int DeferredReflectionQuality { get; set; } = 2;
    public int DeferredRendererQuality { get; set; } = 2;
    public int DeferredGBufferQuality { get; set; } = 2;
    public int DeferredLightingQuality { get; set; } = 2;
    public int DeferredShadowQuality { get; set; } = 2;
    public int DeferredShadowResolution { get; set; } = 2048;
    public float DeferredShadowBias { get; set; } = 0.00035f;
    public float DeferredShadowNormalBias { get; set; } = 0.025f;
    public float DeferredShadowSlopeBias { get; set; } = 0.00125f;
    public float DeferredShadowSoftness { get; set; } = 1f;
    public float DeferredShadowDistance { get; set; } = 100f;
    public int DeferredAmbientOcclusionQuality { get; set; } = 2;
    public int DeferredWaterQuality { get; set; } = 2;
    public int DeferredTransparencyQuality { get; set; } = 2;
    public int DeferredFogQuality { get; set; } = 1;
    public int DeferredPostProcessingQuality { get; set; } = 2;
    public bool DeferredHdr { get; set; } = true;
    public bool DeferredBloom { get; set; } = true;
    public int DeferredToneMapping { get; set; } = 2;
    public DeferredDebugView DeferredDebugView { get; set; }
    public DeferredQualityPreset DeferredQualityPreset { get; set; } = DeferredQualityPreset.Balanced;
    public int HybridLightingQuality { get; set; } = 3;
    public int HybridSpecularQuality { get; set; } = 3;
    public int HybridShadowQuality { get; set; } = 3;
    public int HybridShadowResolution { get; set; } = 4096;
    public int HybridReflectionQuality { get; set; } = 3;
    public int HybridAmbientOcclusionQuality { get; set; } = 3;
    public int HybridWaterQuality { get; set; } = 3;
    public int HybridTransparencyQuality { get; set; } = 3;
    public int HybridFogQuality { get; set; } = 2;
    public bool HybridBloom { get; set; } = true;
    public int PbrSpecularQuality { get; set; } = 2;
    public int PbrReflectionQuality { get; set; } = 2;
    public ForwardQualityPreset ForwardQualityPreset { get; set; } = ForwardQualityPreset.High;
    public int ForwardShadowQuality { get; set; } = 2;
    public int ForwardShadowResolution { get; set; } = 2048;
    public float ForwardShadowBias { get; set; } = 0.00035f;
    public float ForwardShadowNormalBias { get; set; } = 0.025f;
    public float ForwardShadowSlopeBias { get; set; } = 0.00125f;
    public float ForwardShadowSoftness { get; set; } = 1.1f;
    public float ForwardShadowDistance { get; set; } = 112f;
    public int ForwardAmbientOcclusionQuality { get; set; } = 2;
    public int ForwardWaterQuality { get; set; } = 2;
    public int ForwardVolumetricQuality { get; set; } = 2;
    public int ForwardPostProcessingQuality { get; set; } = 2;
    public int ForwardToneMapping { get; set; } = 2;
    public bool ForwardBloom { get; set; } = true;
    public bool ForwardPbr { get; set; } = true;
    public bool ForwardShadows { get; set; } = true;
    public bool ForwardAmbientOcclusion { get; set; } = true;
    public bool ForwardScreenSpaceReflections { get; set; } = true;
    public bool ForwardVolumetric { get; set; } = true;
    public bool ForwardWaterEffects { get; set; } = true;
    public bool ForwardApproximateGi { get; set; } = true;
    public bool ForwardTemporalAntiAliasing { get; set; } = true;
    public ForwardDebugView ForwardDebugView { get; set; }
    public int ClassicLightingQuality { get; set; } = 1;
    public float ClassicSunlightStrength { get; set; } = 1f;
    public float ClassicAmbientStrength { get; set; } = 1f;
    public float ClassicColorSaturation { get; set; } = 1f;
    public int ClassicWaterQuality { get; set; } = 1;
    public float ClassicWaterWaveStrength { get; set; } = 0.65f;
    public int ClassicTransparencyQuality { get; set; } = 1;
    public float ClassicFogDensity { get; set; } = 0.15f;
    public int HardwareRayTracingQuality { get; set; } = 2;
    public int HardwareRayTracingReflectionBounces { get; set; } = 3;
    public float HardwareRayTracingRayDistance { get; set; } = 220f;
    public float HardwareRayTracingShadowStrength { get; set; } = 1f;
    public float HardwareRayTracingEmissionStrength { get; set; } = 1f;
    public float HardwareRayTracingReflectionStrength { get; set; } = 1f;
    public HardwareRayTracingRenderStyle HardwareRayTracingRenderStyle { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HardwareRayTracingWaxMode { get; set; }
    public InfiniteMirrorQuality InfiniteMirrorQuality { get; set; }
    public float Exposure { get; set; } = 1.0f;
    public float FieldOfView { get; set; } = 72f;
    public float MouseSensitivity { get; set; } = 0.11f;
    public float SelectionDistance { get; set; } = 8f;
    public bool SelectionOutlineReflections { get; set; } = true;
    public float TimeOfDay { get; set; } = 8.5f;
    public float DayLengthMinutes { get; set; } = 12f;
    public bool PauseTime { get; set; }
    public int WorldSeed { get; set; } = 1337;
    public string WorldName { get; set; } = "world1";
    public float AutosaveMinutes { get; set; } = 2f;
    public bool StartInFlightMode { get; set; }
    public bool StartWithNoClip { get; set; }
    public bool PhysicsSimulationEnabled { get; set; } = true;
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
    public UiLanguage Language { get; set; } = UiLanguage.TraditionalChinese;
    public string MoveForwardKey { get; set; } = "W";
    public string MoveBackwardKey { get; set; } = "S";
    public string MoveLeftKey { get; set; } = "A";
    public string MoveRightKey { get; set; } = "D";
    public string JumpKey { get; set; } = "Space";
    public string CrouchKey { get; set; } = "LeftShift";
    public string SprintKey { get; set; } = "LeftControl";
    public string InventoryKey { get; set; } = "E";
    public string BuildToolsKey { get; set; } = "B";
    public string PrecisionModeKey { get; set; } = "V";
    public string PauseKey { get; set; } = "Escape";

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

    public void ApplyDeferredQualityPreset(DeferredQualityPreset preset)
    {
        if (preset == DeferredQualityPreset.Custom) return;
        int quality = preset switch
        {
            DeferredQualityPreset.Performance => 1,
            DeferredQualityPreset.Balanced => 2,
            _ => 3,
        };
        DeferredQualityPreset = preset;
        DeferredRendererQuality = quality;
        DeferredGBufferQuality = quality;
        DeferredLightingQuality = quality;
        DeferredShadowQuality = quality;
        DeferredShadowResolution = preset switch
        {
            DeferredQualityPreset.Performance => 1024,
            DeferredQualityPreset.Balanced => 2048,
            _ => 4096,
        };
        DeferredShadowDistance = preset switch
        {
            DeferredQualityPreset.Performance => 72f,
            DeferredQualityPreset.Balanced => 100f,
            _ => 140f,
        };
        DeferredShadowSoftness = preset == DeferredQualityPreset.Quality ? 1.35f : 1f;
        DeferredSpecularQuality = quality;
        DeferredReflectionQuality = quality;
        DeferredAmbientOcclusionQuality = quality;
        DeferredWaterQuality = quality;
        DeferredTransparencyQuality = quality;
        DeferredFogQuality = preset == DeferredQualityPreset.Performance ? 1 : quality;
        DeferredPostProcessingQuality = quality;
        DeferredHdr = true;
        DeferredBloom = true;
    }

    public void ApplyForwardQualityPreset(ForwardQualityPreset preset)
    {
        if (preset == ForwardQualityPreset.Custom) return;
        int quality = preset switch
        {
            ForwardQualityPreset.Potato => 0,
            ForwardQualityPreset.Medium => 1,
            ForwardQualityPreset.High => 2,
            _ => 3,
        };
        ForwardQualityPreset = preset;
        ForwardShadowQuality = Math.Max(1, quality);
        ForwardShadowResolution = preset switch
        {
            ForwardQualityPreset.Potato => 1024,
            ForwardQualityPreset.Medium => 1024,
            ForwardQualityPreset.High => 2048,
            _ => 4096,
        };
        ForwardShadowDistance = preset switch
        {
            ForwardQualityPreset.Potato => 64f,
            ForwardQualityPreset.Medium => 88f,
            ForwardQualityPreset.High => 112f,
            _ => 144f,
        };
        ForwardShadowSoftness = preset == ForwardQualityPreset.Ultra ? 1.45f : 1.05f;
        PbrSpecularQuality = Math.Max(1, quality);
        PbrReflectionQuality = quality;
        ForwardAmbientOcclusionQuality = quality;
        ForwardWaterQuality = Math.Max(1, quality);
        ForwardVolumetricQuality = preset == ForwardQualityPreset.Potato ? 0 : quality;
        ForwardPostProcessingQuality = Math.Max(1, quality);
        ForwardBloom = preset != ForwardQualityPreset.Potato;
        ForwardPbr = true;
        ForwardShadows = true;
        ForwardAmbientOcclusion = preset != ForwardQualityPreset.Potato;
        ForwardScreenSpaceReflections = quality >= 1;
        ForwardVolumetric = quality >= 1;
        ForwardWaterEffects = true;
        ForwardApproximateGi = quality >= 2;
        ForwardTemporalAntiAliasing = quality >= 2;
    }

    private void Validate()
    {
        Version = CurrentVersion;
        WindowWidth = Math.Clamp(WindowWidth, 640, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 360, 4320);
        FpsLimit = Math.Clamp(FpsLimit, 30, 360);
        RenderScale = Math.Clamp(RenderScale, 0.35f, 1f);
        if (!RenderBackendCatalog.IsImplemented(Renderer)) Renderer = RenderBackendKind.SoftwareRayTracing;
        if (!Enum.IsDefined(typeof(UpscalingMode), UpscalingMode))
            UpscalingMode = UpscalingMode.None;
        PathBounces = Math.Clamp(PathBounces, 1, 50);
        PathSamplesPerFrame = Math.Clamp(PathSamplesPerFrame, 1, 8);
        PathMaxAccumulatedSamples = Math.Clamp(PathMaxAccumulatedSamples, 64, 16384);
        PathRayDistance = Math.Clamp(PathRayDistance, 32f, 220f);
        PathDirectLightingStrength = Math.Clamp(PathDirectLightingStrength, 0f, 2f);
        PathEnvironmentIntensity = Math.Clamp(PathEnvironmentIntensity, 0f, 2f);
        PathEmissionIntensity = Math.Clamp(PathEmissionIntensity, 0f, 2f);
        PathReflectionStrength = Math.Clamp(PathReflectionStrength, 0f, 2f);
        PathRussianRouletteStart = Math.Clamp(PathRussianRouletteStart, 1, 12);
        DeferredSpecularQuality = Math.Clamp(DeferredSpecularQuality, 0, 3);
        DeferredReflectionQuality = Math.Clamp(DeferredReflectionQuality, 0, 3);
        DeferredRendererQuality = Math.Clamp(DeferredRendererQuality, 0, 3);
        DeferredGBufferQuality = Math.Clamp(DeferredGBufferQuality, 0, 3);
        DeferredLightingQuality = Math.Clamp(DeferredLightingQuality, 0, 3);
        DeferredShadowQuality = Math.Clamp(DeferredShadowQuality, 0, 3);
        DeferredShadowResolution = DeferredShadowResolution switch
        {
            <= 1536 => 1024,
            <= 3072 => 2048,
            _ => 4096,
        };
        DeferredShadowBias = Math.Clamp(DeferredShadowBias, 0.00005f, 0.003f);
        DeferredShadowNormalBias = Math.Clamp(DeferredShadowNormalBias, 0f, 0.12f);
        DeferredShadowSlopeBias = Math.Clamp(DeferredShadowSlopeBias, 0f, 0.006f);
        DeferredShadowSoftness = Math.Clamp(DeferredShadowSoftness, 0.5f, 3f);
        DeferredShadowDistance = Math.Clamp(DeferredShadowDistance, 48f, 160f);
        DeferredAmbientOcclusionQuality = Math.Clamp(DeferredAmbientOcclusionQuality, 0, 3);
        DeferredWaterQuality = Math.Clamp(DeferredWaterQuality, 0, 3);
        DeferredTransparencyQuality = Math.Clamp(DeferredTransparencyQuality, 0, 3);
        DeferredFogQuality = Math.Clamp(DeferredFogQuality, 0, 3);
        DeferredPostProcessingQuality = Math.Clamp(DeferredPostProcessingQuality, 0, 3);
        DeferredToneMapping = Math.Clamp(DeferredToneMapping, 0, 2);
        if (!Enum.IsDefined(DeferredDebugView)) DeferredDebugView = DeferredDebugView.FinalImage;
        if (!Enum.IsDefined(DeferredQualityPreset)) DeferredQualityPreset = DeferredQualityPreset.Custom;
        HybridLightingQuality = Math.Clamp(HybridLightingQuality, 0, 3);
        HybridSpecularQuality = Math.Clamp(HybridSpecularQuality, 0, 3);
        HybridShadowQuality = Math.Clamp(HybridShadowQuality, 0, 3);
        HybridShadowResolution = HybridShadowResolution switch
        {
            <= 1536 => 1024,
            <= 3072 => 2048,
            _ => 4096,
        };
        HybridReflectionQuality = Math.Clamp(HybridReflectionQuality, 0, 3);
        HybridAmbientOcclusionQuality = Math.Clamp(HybridAmbientOcclusionQuality, 0, 3);
        HybridWaterQuality = Math.Clamp(HybridWaterQuality, 0, 3);
        HybridTransparencyQuality = Math.Clamp(HybridTransparencyQuality, 0, 3);
        HybridFogQuality = Math.Clamp(HybridFogQuality, 0, 3);
        PbrSpecularQuality = Math.Clamp(PbrSpecularQuality, 0, 3);
        PbrReflectionQuality = Math.Clamp(PbrReflectionQuality, 0, 3);
        if (!Enum.IsDefined(ForwardQualityPreset)) ForwardQualityPreset = ForwardQualityPreset.Custom;
        ForwardShadowQuality = Math.Clamp(ForwardShadowQuality, 0, 3);
        ForwardShadowResolution = ForwardShadowResolution switch
        {
            <= 1536 => 1024,
            <= 3072 => 2048,
            _ => 4096,
        };
        ForwardShadowBias = Math.Clamp(ForwardShadowBias, 0.00005f, 0.003f);
        ForwardShadowNormalBias = Math.Clamp(ForwardShadowNormalBias, 0f, 0.12f);
        ForwardShadowSlopeBias = Math.Clamp(ForwardShadowSlopeBias, 0f, 0.006f);
        ForwardShadowSoftness = Math.Clamp(ForwardShadowSoftness, 0.5f, 3f);
        ForwardShadowDistance = Math.Clamp(ForwardShadowDistance, 48f, 160f);
        ForwardAmbientOcclusionQuality = Math.Clamp(ForwardAmbientOcclusionQuality, 0, 3);
        ForwardWaterQuality = Math.Clamp(ForwardWaterQuality, 0, 3);
        ForwardVolumetricQuality = Math.Clamp(ForwardVolumetricQuality, 0, 3);
        ForwardPostProcessingQuality = Math.Clamp(ForwardPostProcessingQuality, 0, 3);
        ForwardToneMapping = Math.Clamp(ForwardToneMapping, 0, 2);
        if (!Enum.IsDefined(ForwardDebugView)) ForwardDebugView = ForwardDebugView.FinalImage;
        ClassicLightingQuality = Math.Clamp(ClassicLightingQuality, 0, 2);
        ClassicSunlightStrength = Math.Clamp(ClassicSunlightStrength, 0f, 2f);
        ClassicAmbientStrength = Math.Clamp(ClassicAmbientStrength, 0f, 2f);
        ClassicColorSaturation = Math.Clamp(ClassicColorSaturation, 0f, 1.5f);
        ClassicWaterQuality = Math.Clamp(ClassicWaterQuality, 0, 3);
        ClassicWaterWaveStrength = Math.Clamp(ClassicWaterWaveStrength, 0f, 2f);
        ClassicTransparencyQuality = Math.Clamp(ClassicTransparencyQuality, 0, 3);
        ClassicFogDensity = Math.Clamp(ClassicFogDensity, 0f, 1f);
        HardwareRayTracingQuality = Math.Clamp(HardwareRayTracingQuality, 0, 3);
        HardwareRayTracingRayDistance = Math.Clamp(HardwareRayTracingRayDistance, 48f, 240f);
        HardwareRayTracingShadowStrength = Math.Clamp(HardwareRayTracingShadowStrength, 0f, 1f);
        HardwareRayTracingEmissionStrength = Math.Clamp(HardwareRayTracingEmissionStrength, 0f, 2f);
        HardwareRayTracingReflectionStrength = Math.Clamp(HardwareRayTracingReflectionStrength, 0f, 2f);
        // DXR TraceRay pipelines may declare at most 31 nested calls. Reserving two
        // levels for the primary ray and the deepest shadow ray leaves 29 real
        // reflection layers in this recursive implementation.
        HardwareRayTracingReflectionBounces = Math.Clamp(HardwareRayTracingReflectionBounces, 1, 29);
        if (HardwareRayTracingWaxMode && HardwareRayTracingRenderStyle == HardwareRayTracingRenderStyle.Default)
            HardwareRayTracingRenderStyle = HardwareRayTracingRenderStyle.WaxMode;
        HardwareRayTracingWaxMode = false;
        if (!Enum.IsDefined(HardwareRayTracingRenderStyle))
            HardwareRayTracingRenderStyle = HardwareRayTracingRenderStyle.Default;
        if (!Enum.IsDefined(InfiniteMirrorQuality)) InfiniteMirrorQuality = InfiniteMirrorQuality.Off;
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
        if (!Enum.IsDefined(Language)) Language = UiLanguage.TraditionalChinese;
        MoveForwardKey = NormalizeBinding(MoveForwardKey, "W");
        MoveBackwardKey = NormalizeBinding(MoveBackwardKey, "S");
        MoveLeftKey = NormalizeBinding(MoveLeftKey, "A");
        MoveRightKey = NormalizeBinding(MoveRightKey, "D");
        JumpKey = NormalizeBinding(JumpKey, "Space");
        CrouchKey = NormalizeBinding(CrouchKey, "LeftShift");
        SprintKey = NormalizeBinding(SprintKey, "LeftControl");
        InventoryKey = NormalizeBinding(InventoryKey, "E");
        BuildToolsKey = NormalizeBinding(BuildToolsKey, "B");
        PrecisionModeKey = NormalizeBinding(PrecisionModeKey, "V");
        PauseKey = NormalizeBinding(PauseKey, "Escape");
    }

    private static string NormalizeBinding(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

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
