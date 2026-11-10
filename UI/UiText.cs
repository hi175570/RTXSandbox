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
            ["palette_title"] = ("創造物品欄", "创造物品栏", "Creative Inventory"),
            ["palette_all"] = ("全部", "全部", "All"),
            ["palette_favorites"] = ("收藏", "收藏", "Favorites"),
            ["palette_backpack"] = ("背包", "背包", "Backpack"),
            ["palette_nature"] = ("自然", "自然", "Nature"),
            ["palette_wood"] = ("木材", "木材", "Wood"),
            ["palette_building"] = ("建築", "建筑", "Building"),
            ["palette_glass"] = ("玻璃", "玻璃", "Glass"),
            ["palette_metal"] = ("金屬", "金属", "Metal"),
            ["palette_optical"] = ("光學", "光学", "Optics"),
            ["palette_lighting"] = ("照明", "照明", "Lighting"),
            ["palette_technology"] = ("科技", "科技", "Technology"),
            ["palette_tools"] = ("工具", "工具", "Tools"),
            ["palette_spheres"] = ("球體", "球体", "Spheres"),
            ["palette_special"] = ("特殊", "特殊", "Special"),
            ["palette_general"] = ("其他", "其他", "Other"),
            ["palette_search"] = ("搜尋名稱、ID 或標籤…", "搜索名称、ID 或标签…", "Search name, key or tag..."),
            ["palette_clear"] = ("清除", "清除", "Clear"),
            ["palette_empty"] = ("沒有符合的物品", "没有符合的物品", "No matching items"),
            ["palette_hotbar"] = ("快捷欄", "快捷栏", "Hotbar"),
            ["palette_hint"] = ("點擊取用 · Shift 快速放入 · 右鍵收藏", "点击取用 · Shift 快速放入 · 右键收藏", "Click: take · Shift: quick move · RMB: favorite"),
            ["palette_bag_hint"] = ("點擊取放 · Shift 快速搬運 · 右下角銷毀", "点击取放 · Shift 快速搬运 · 右下角销毁", "Click: move · Shift: transfer · Trash: destroy"),
            ["palette_trash"] = ("銷毀游標上的物品；Shift + 點擊清空背包與快捷欄", "销毁光标上的物品；Shift + 点击清空背包与快捷栏", "Destroy carried item; Shift-click clears inventory and hotbar"),
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
