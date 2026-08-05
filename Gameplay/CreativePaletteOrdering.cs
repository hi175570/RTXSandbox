using RTXSandbox.ModAPI;

namespace RTXSandbox.Gameplay;

internal static class CreativePaletteOrdering
{
    private static readonly string[] ColorOrder =
    {
        "clear", "white", "light_gray", "gray", "black", "brown", "red", "orange",
        "yellow", "lime", "green", "cyan", "aqua", "light_blue", "blue", "purple",
        "magenta", "pink", "rose", "rainbow",
    };

    private static readonly string[] GlassOrder =
    {
        "glass", "clear_glass", "white_glass", "light_gray_glass", "gray_glass", "black_glass",
        "brown_glass", "red_glass", "orange_glass", "yellow_glass", "lime_glass", "green_glass",
        "cyan_glass", "aqua_glass", "light_blue_glass", "blue_glass", "purple_glass",
        "magenta_glass", "pink_glass", "rose_glass", "smoked_glass", "diamond_glass",
        "prismatic_glass", "frosted_glass", "matte_glass", "mosaic_glass", "acrylic_glass",
        "ribbed_glass", "etched_glass",
    };

    public static BlockDefinition[] Order(IEnumerable<BlockDefinition> blocks, bool useSimpleGroups)
    {
        if (!useSimpleGroups)
            return blocks.OrderBy(block => block.Id).ToArray();

        return blocks
            .OrderBy(GroupOrder)
            .ThenBy(SubgroupOrder)
            .ThenBy(ColorRank)
            .ThenBy(block => block.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(block => block.Id)
            .ToArray();
    }

    private static int GroupOrder(BlockDefinition block)
    {
        if (IsGlass(block)) return 4;
        return TopLevelCategory(block.Category).ToLowerInvariant() switch
        {
            "nature" => 0,
            "wood" => 1,
            "building" => 2,
            "metal" => 3,
            "optical" => 5,
            "technology" => 6,
            "lighting" => 7,
            "special" => 8,
            _ => 9,
        };
    }

    private static int SubgroupOrder(BlockDefinition block)
    {
        if (!IsGlass(block)) return 0;
        int index = Array.IndexOf(GlassOrder, KeyTail(block));
        return index >= 0 ? index : GlassOrder.Length;
    }

    private static int ColorRank(BlockDefinition block)
    {
        string searchable = $"{KeyTail(block)} {string.Join(' ', block.Tags)}";
        for (int index = 0; index < ColorOrder.Length; index++)
            if (searchable.Contains(ColorOrder[index], StringComparison.OrdinalIgnoreCase)) return index;
        return ColorOrder.Length;
    }

    private static bool IsGlass(BlockDefinition block) =>
        KeyTail(block).Contains("glass", StringComparison.OrdinalIgnoreCase) ||
        block.Tags.Any(tag => tag.Equals("glass", StringComparison.OrdinalIgnoreCase));

    private static string KeyTail(BlockDefinition block)
    {
        int separator = block.Key.IndexOf(':');
        return separator < 0 ? block.Key : block.Key[(separator + 1)..];
    }

    private static string TopLevelCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "General";
        int separator = category.IndexOf('/');
        return (separator < 0 ? category : category[..separator]).Trim();
    }
}
