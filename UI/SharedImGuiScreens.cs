using System.Numerics;
using ImGuiNET;
using RTXSandbox.Configuration;

namespace RTXSandbox.UI;

public enum SharedPauseAction
{
    None,
    Continue,
    Settings,
    Achievements,
    Statistics,
    Mods,
    Language,
    Controls,
    TouchControls,
    LeaveWorld,
}

/// <summary>
/// Platform-neutral ImGui screens shared directly by desktop and Android.
/// Platform hosts own actions, persistence and input, never a second layout.
/// </summary>
public static class SharedImGuiScreens
{
    public static SharedPauseAction DrawPauseMenu(
        Vector2 viewport,
        Func<string, string> text,
        bool modsAvailable,
        bool showTouchControls,
        bool allowWindowControls = false)
    {
        ImGui.SetNextWindowPos(viewport * 0.5f, allowWindowControls ? ImGuiCond.FirstUseEver : ImGuiCond.Always,
            new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(520f, showTouchControls ? 566f : 500f),
            allowWindowControls ? ImGuiCond.FirstUseEver : ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f);
        PushNavigationStyle();
        SharedPauseAction action = SharedPauseAction.None;
        bool open = true;
        ImGuiWindowFlags flags = allowWindowControls
            ? ImGuiWindowFlags.NoCollapse
            : ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        bool visible = allowWindowControls
            ? ImGui.Begin("RTX Sandbox##pause-menu", ref open, flags)
            : ImGui.Begin("##pause-menu", flags);
        if (visible)
        {
            float width = ImGui.GetContentRegionAvail().X;
            ImGui.Dummy(new Vector2(0f, 10f));
            if (ImGui.Button(text("continue"), new Vector2(width, 58f))) action = SharedPauseAction.Continue;
            ImGui.Separator();
            float half = (width - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            if (ImGui.Button(text("settings"), new Vector2(half, 54f))) action = SharedPauseAction.Settings;
            ImGui.SameLine();
            if (ImGui.Button(text("achievements"), new Vector2(half, 54f))) action = SharedPauseAction.Achievements;
            if (ImGui.Button(text("statistics"), new Vector2(half, 54f))) action = SharedPauseAction.Statistics;
            ImGui.SameLine();
            if (!modsAvailable) ImGui.BeginDisabled();
            if (ImGui.Button(text("mods"), new Vector2(half, 54f))) action = SharedPauseAction.Mods;
            if (!modsAvailable) ImGui.EndDisabled();
            if (ImGui.Button(text("language"), new Vector2(half, 54f))) action = SharedPauseAction.Language;
            ImGui.SameLine();
            if (ImGui.Button(text("controls"), new Vector2(half, 54f))) action = SharedPauseAction.Controls;
            if (showTouchControls)
            {
                ImGui.SeparatorText("Android");
                if (ImGui.Button(text("touch_editor"), new Vector2(width, 48f)))
                    action = SharedPauseAction.TouchControls;
            }
            ImGui.Separator();
            if (ImGui.Button(text("leave_world"), new Vector2(width, 58f))) action = SharedPauseAction.LeaveWorld;
        }
        ImGui.End();
        PopNavigationStyle();
        if (!open) action = SharedPauseAction.Continue;
        return action;
    }

    public static bool DrawAchievements(Vector2 viewport, Func<string, string> text)
    {
        ImGui.SetNextWindowPos(viewport * 0.5f, ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(
            MathF.Max(640f, viewport.X * 0.90f),
            MathF.Max(420f, viewport.Y * 0.88f)), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin(text("achievements_title"), ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
        {
            ImGui.TextDisabled(text("achievement_locked"));
            ImGui.Separator();
            if (ImGui.BeginChild("achievement-list", new Vector2(0f, -54f), ImGuiChildFlags.Borders))
            {
                for (int index = 0; index < 4; index++)
                {
                    ImGui.PushID(index);
                    ImGui.BeginChild("achievement-row", new Vector2(ImGui.GetContentRegionAvail().X, 104f),
                        ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
                    float nameWidth = ImGui.GetContentRegionAvail().X / 3f;
                    ImGui.BeginChild("name", new Vector2(nameWidth, 0f));
                    ImGui.Dummy(new Vector2(0f, 16f));
                    ImGui.TextWrapped(index == 0 ? text("achievement_name") : $"{text("achievement_name")} {index + 1}");
                    ImGui.TextDisabled(text("achievement_locked"));
                    ImGui.EndChild();
                    ImGui.SameLine();
                    ImGui.BeginChild("description", Vector2.Zero);
                    ImGui.Dummy(new Vector2(0f, 10f));
                    ImGui.TextWrapped(text("achievement_description"));
                    ImGui.EndChild();
                    ImGui.EndChild();
                    ImGui.Spacing();
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
            if (ImGui.Button(text("done"), new Vector2(180f, 40f))) open = false;
        }
        ImGui.End();
        return open;
    }

    public static bool DrawLanguage(
        Vector2 viewport,
        Func<string, string> text,
        ref UiLanguage language,
        out bool changed)
    {
        ImGui.SetNextWindowPos(viewport * 0.5f, ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(460f, 290f), ImGuiCond.FirstUseEver);
        bool open = true;
        changed = false;
        if (ImGui.Begin(text("language_title"), ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
        {
            changed |= LanguageChoice(UiLanguage.TraditionalChinese, text("traditional_chinese"), ref language);
            changed |= LanguageChoice(UiLanguage.SimplifiedChinese, text("simplified_chinese"), ref language);
            changed |= LanguageChoice(UiLanguage.English, text("english"), ref language);
            ImGui.Spacing();
            ImGui.TextWrapped(language switch
            {
                UiLanguage.English => "The interface language changes immediately.",
                UiLanguage.SimplifiedChinese => "界面语言会立即应用。",
                _ => "介面語言會立即套用。",
            });
            ImGui.Separator();
            if (ImGui.Button(text("done"), new Vector2(160f, 40f))) open = false;
        }
        ImGui.End();
        return open;
    }

    private static bool LanguageChoice(UiLanguage value, string label, ref UiLanguage language)
    {
        if (!ImGui.RadioButton(label, language == value)) return false;
        language = value;
        return true;
    }

    private static void PushNavigationStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12f, 12f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.09f, 0.10f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.29f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.42f, 0.44f, 0.46f, 1f));
    }

    private static void PopNavigationStyle()
    {
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(3);
    }
}
