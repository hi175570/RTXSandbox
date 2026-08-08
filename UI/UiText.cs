using RTXSandbox.Configuration;

namespace RTXSandbox.UI;

/// <summary>
/// Small, dependency-free localization table used by the shared navigation
/// surfaces. Existing feature panels can migrate into this table incrementally.
/// Missing keys deliberately fall back to Traditional Chinese instead of
/// rendering an identifier to the player.
/// </summary>
public static class UiText
{
    private static readonly IReadOnlyDictionary<string, (string Traditional, string Simplified, string English)> Text =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["continue"] = ("繼續遊戲", "继续游戏", "Continue"),
            ["settings"] = ("設定", "设置", "Settings"),
            ["achievements"] = ("成就", "成就", "Achievements"),
            ["statistics"] = ("統計", "统计", "Statistics"),
            ["mods"] = ("模組", "模组", "Mods"),
            ["language"] = ("語言", "语言", "Language"),
            ["controls"] = ("按鍵", "按键", "Controls"),
            ["leave_world"] = ("離開世界", "离开世界", "Leave World"),
            ["done"] = ("完成", "完成", "Done"),
            ["back"] = ("返回", "返回", "Back"),
            ["language_title"] = ("語言", "语言", "Language"),
            ["controls_title"] = ("按鍵設定", "按键设置", "Key Bindings"),
            ["achievements_title"] = ("成就列表", "成就列表", "Achievements"),
            ["achievement_name"] = ("成就名稱", "成就名称", "Achievement Name"),
            ["achievement_description"] = ("這是一個最多可容納三行文字的成就介紹範例。", "这是一个最多可容纳三行文字的成就介绍示例。", "An example achievement description that can occupy up to three lines."),
            ["achievement_locked"] = ("尚未完成", "尚未完成", "Locked"),
            ["move_forward"] = ("向前移動", "向前移动", "Move Forward"),
            ["move_backward"] = ("向後移動", "向后移动", "Move Backward"),
            ["move_left"] = ("向左移動", "向左移动", "Move Left"),
            ["move_right"] = ("向右移動", "向右移动", "Move Right"),
            ["jump"] = ("跳躍", "跳跃", "Jump"),
            ["crouch"] = ("蹲下", "蹲下", "Crouch"),
            ["sprint"] = ("衝刺", "冲刺", "Sprint"),
            ["inventory"] = ("物品欄", "物品栏", "Inventory"),
            ["build_tools"] = ("建造工具", "建造工具", "Build Tools"),
            ["precision"] = ("精密模式", "精密模式", "Precision Mode"),
            ["pause"] = ("暫停", "暂停", "Pause"),
            ["touch_editor"] = ("編輯觸控", "编辑触控", "Touch Editor"),
            ["renderer"] = ("渲染器", "渲染器", "Renderer"),
            ["save_world"] = ("儲存世界", "保存世界", "Save World"),
            ["traditional_chinese"] = ("繁體中文", "繁体中文", "Traditional Chinese"),
            ["simplified_chinese"] = ("簡體中文", "简体中文", "Simplified Chinese"),
            ["english"] = ("英文", "英文", "English"),
        };

    public static string Get(UiLanguage language, string key)
    {
        if (!Text.TryGetValue(key, out var value)) return key;
        return language switch
        {
            UiLanguage.SimplifiedChinese => value.Simplified,
            UiLanguage.English => value.English,
            _ => value.Traditional,
        };
    }

    public static string GlyphCorpus => string.Concat(Text.Values.SelectMany(value => new[]
    {
        value.Traditional,
        value.Simplified,
        value.English,
    }));
}
