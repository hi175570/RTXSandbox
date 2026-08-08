using System.Text.Json;
using System.Text.Json.Serialization;

namespace RTXSandbox.Android.TouchControls;

public enum TouchControlType
{
    Button,
    Joystick,
    DPad,
}

public enum VirtualBindingKind
{
    Keyboard,
    Mouse,
    GameAction,
}

public enum TouchButtonMode
{
    Normal,
    Toggle,
}

public sealed class TouchControlDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TouchControlType Type { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 0.10f;
    public float Height { get; set; } = 0.12f;
    public float Scale { get; set; } = 1f;
    public float Opacity { get; set; } = 0.55f;
    public string Label { get; set; } = "按鍵";
    public VirtualBindingKind BindingKind { get; set; }
    public string Binding { get; set; } = "Space";
    public TouchButtonMode Mode { get; set; }
    public bool Visible { get; set; } = true;
    public int ZIndex { get; set; }

    [JsonIgnore]
    public float EffectiveWidth => Math.Clamp(Width * Scale, 0.035f, 0.75f);

    [JsonIgnore]
    public float EffectiveHeight => Math.Clamp(Height * Scale, 0.045f, 0.75f);

    public TouchControlDefinition Clone()
    {
        TouchControlDefinition copy = (TouchControlDefinition)MemberwiseClone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.X = Math.Clamp(X + 0.025f, 0.02f, 0.98f);
        copy.Y = Math.Clamp(Y + 0.025f, 0.02f, 0.98f);
        return copy;
    }
}

public sealed class TouchControlProfile
{
    public const int CurrentFormatVersion = 2;
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "Default";
    public float HorizontalLookSensitivity { get; set; } = 0.18f;
    public float VerticalLookSensitivity { get; set; } = 0.18f;
    public bool InvertLookY { get; set; }
    public List<TouchControlDefinition> Controls { get; set; } = new();

    public static TouchControlProfile CreateDefault() => new()
    {
        Controls =
        {
            new TouchControlDefinition
            {
                Id = "movement", Type = TouchControlType.DPad, X = 0.145f, Y = 0.73f,
                Width = 0.23f, Height = 0.38f, Opacity = 0.42f, Label = "",
                BindingKind = VirtualBindingKind.GameAction, Binding = "Move", ZIndex = 10,
            },
            Button("jump", "SPC", 0.865f, 0.665f, "Space", VirtualBindingKind.Keyboard, 20),
            Button("mouse-left", "LMB", 0.755f, 0.825f, "Left", VirtualBindingKind.Mouse, 21),
            Button("mouse-right", "RMB", 0.885f, 0.835f, "Right", VirtualBindingKind.Mouse, 22),
            Button("inventory", "E", 0.905f, 0.115f, "E", VirtualBindingKind.Keyboard, 23, 0.09f, 0.10f),
            Button("key-v", "V", 0.765f, 0.115f, "V", VirtualBindingKind.Keyboard, 24, 0.065f, 0.09f),
            Button("key-b", "B", 0.835f, 0.115f, "B", VirtualBindingKind.Keyboard, 25, 0.065f, 0.09f),
            Button("back", "ESC", 0.055f, 0.085f, "Escape", VirtualBindingKind.Keyboard, 26, 0.075f, 0.085f),
        },
    };

    private static TouchControlDefinition Button(
        string id, string label, float x, float y, string binding, VirtualBindingKind kind,
        int zIndex, float width = 0.105f, float height = 0.14f) => new()
        {
            Id = id,
            Type = TouchControlType.Button,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Opacity = 0.52f,
            Label = label,
            BindingKind = kind,
            Binding = binding,
            ZIndex = zIndex,
        };
}

public sealed class TouchControlProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public TouchControlProfileStore(string filesDirectory) =>
        _path = System.IO.Path.Combine(filesDirectory, "touch-controls.json");

    public string Path => _path;

    public TouchControlProfile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                TouchControlProfile? profile = JsonSerializer.Deserialize<TouchControlProfile>(
                    File.ReadAllText(_path), Options);
                if (profile is not null && profile.FormatVersion is >= 1 and <= TouchControlProfile.CurrentFormatVersion &&
                    profile.Controls.Count > 0)
                {
                    bool migrated = MigrateBuiltInLabels(profile);
                    if (profile.FormatVersion < 2)
                    {
                        profile.Controls.Add(TouchControlProfile.CreateDefault().Controls.Single(control => control.Id == "back"));
                        profile.FormatVersion = 2;
                        migrated = true;
                    }
                    Normalize(profile);
                    if (migrated)
                    {
                        try { Save(profile); }
                        catch (IOException) { }
                    }
                    return profile;
                }
            }
        }
        catch (JsonException)
        {
            // A malformed user layout must never prevent the game from launching.
        }
        catch (IOException)
        {
        }
        return TouchControlProfile.CreateDefault();
    }

    public void Save(TouchControlProfile profile)
    {
        Normalize(profile);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profile, Options));
        File.Move(temporary, _path, overwrite: true);
    }

    private static void Normalize(TouchControlProfile profile)
    {
        profile.HorizontalLookSensitivity = Math.Clamp(profile.HorizontalLookSensitivity, 0.01f, 1.5f);
        profile.VerticalLookSensitivity = Math.Clamp(profile.VerticalLookSensitivity, 0.01f, 1.5f);
        foreach (TouchControlDefinition control in profile.Controls)
        {
            control.Id = string.IsNullOrWhiteSpace(control.Id) ? Guid.NewGuid().ToString("N") : control.Id;
            control.X = Math.Clamp(control.X, 0.01f, 0.99f);
            control.Y = Math.Clamp(control.Y, 0.01f, 0.99f);
            control.Width = Math.Clamp(control.Width, 0.025f, 0.70f);
            control.Height = Math.Clamp(control.Height, 0.025f, 0.70f);
            control.Scale = Math.Clamp(control.Scale, 0.25f, 3f);
            control.Opacity = Math.Clamp(control.Opacity, 0f, 1f);
            control.Label ??= string.Empty;
            if (control.Label.Length > 3) control.Label = control.Label[..3];
            control.Binding ??= string.Empty;
        }
    }

    private static bool MigrateBuiltInLabels(TouchControlProfile profile)
    {
        bool changed = false;
        var replacements = new Dictionary<string, (string[] OldLabels, string NewLabel)>
        {
            ["movement"] = (new[] { "移動", "WASD" }, ""),
            ["jump"] = (new[] { "跳", "Space" }, "SPC"),
            ["mouse-left"] = (new[] { "破壞", "Mouse Left" }, "LMB"),
            ["mouse-right"] = (new[] { "放置", "Mouse Right" }, "RMB"),
            ["inventory"] = (new[] { "背包" }, "E"),
        };
        foreach (TouchControlDefinition control in profile.Controls)
        {
            if (replacements.TryGetValue(control.Id, out (string[] OldLabels, string NewLabel) replacement) &&
                replacement.OldLabels.Contains(control.Label, StringComparer.Ordinal))
            {
                control.Label = replacement.NewLabel;
                changed = true;
            }
            if (control.Id == "movement" && control.Type == TouchControlType.Joystick &&
                control.Label.Length == 0)
            {
                control.Type = TouchControlType.DPad;
                changed = true;
            }
        }
        return changed;
    }
}
