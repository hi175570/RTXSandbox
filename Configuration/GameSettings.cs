using System.Text.Json;
using System.Text.Json.Serialization;
using RTXSandbox.Rendering;

namespace RTXSandbox.Configuration;

public enum GameMode
{
    Survival,
    Creative,
}

public enum GameDifficulty
{
    Peaceful,
    Normal,
    Hard,
}

public enum PhysicsMode
{
    Off,
    Simple,
    Full,
}

public sealed class GameSettings
{
    public int WindowWidth { get; set; } = 960;
    public int WindowHeight { get; set; } = 540;
    public bool VSync { get; set; } = true;
    public float RenderScale { get; set; } = 1.0f;
    public RenderBackendKind Renderer { get; set; } = RenderBackendKind.SoftwareRayTracing;
    public int PathBounces { get; set; } = 3;
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
    public GameMode GameMode { get; set; } = GameMode.Creative;
    public GameDifficulty Difficulty { get; set; } = GameDifficulty.Normal;
    public bool EnableItemDrops { get; set; }
    public PhysicsMode PhysicsMode { get; set; } = PhysicsMode.Off;
    public bool ImpactFracture { get; set; }
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
    public bool ContextualCrosshairColor { get; set; }
    public int PrecisionGridDivisor { get; set; } = 16;

    [JsonIgnore]
    public string SourcePath { get; private set; } = Path.Combine(Environment.CurrentDirectory, "settings.json");

    public static GameSettings Load()
    {
        string workingCopy = Path.Combine(Environment.CurrentDirectory, "settings.json");
        string outputCopy = Path.Combine(AppContext.BaseDirectory, "settings.json");
        string path = File.Exists(workingCopy) ? workingCopy : outputCopy;

        GameSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path), JsonOptions()) ?? new GameSettings()
                : new GameSettings();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not read settings.json; defaults will be used: {exception.Message}");
            settings = new GameSettings();
        }

        settings.SourcePath = File.Exists(path) ? path : workingCopy;
        settings.Validate();
        return settings;
    }

    public void Save()
    {
        Validate();
        try
        {
            string? directory = Path.GetDirectoryName(SourcePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(SourcePath, JsonSerializer.Serialize(this, JsonOptions()));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not save settings to '{SourcePath}': {exception.Message}");
        }
    }

    private void Validate()
    {
        WindowWidth = Math.Clamp(WindowWidth, 640, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 360, 4320);
        RenderScale = Math.Clamp(RenderScale, 0.35f, 1f);
        if (!RenderBackendCatalog.IsImplemented(Renderer)) Renderer = RenderBackendKind.SoftwareRayTracing;
        PathBounces = Math.Clamp(PathBounces, 1, 12);
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
        GameMode = GameMode.Creative;
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
