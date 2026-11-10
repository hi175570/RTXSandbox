using System.Numerics;
using ImGuiNET;
using RTXSandbox.Gameplay;
using RTXSandbox.Engine;

namespace RTXSandbox.UI;

public enum CreativeInventoryAction { None, Close, BuildTools }

/// <summary>One layout for desktop and Android. Hosts provide only texture drawing and game actions.</summary>
public static class CreativeInventoryPanel
{
    private static readonly Vector4 Ink = new(0.16f, 0.16f, 0.15f, 1f);
    private static readonly Vector4 Paper = new(0.78f, 0.78f, 0.75f, 1f);
    private static uint Color(float value) => ImGui.ColorConvertFloat4ToU32(new Vector4(value, value, value, 1));
    internal static Vector4 LastNextPageBounds { get; private set; }
    internal static Vector4 LastCloseBounds { get; private set; }
    internal static Vector4 LastBackpackTabBounds { get; private set; }
    internal static Vector4 LastFavoritesTabBounds { get; private set; }
    internal static Vector4 LastCarriedBounds { get; private set; }
    internal static Vector4 LastTrashBounds { get; private set; }

    public static string CategoryLabel(string category, Func<string, string> text)
    {
        string key = "palette_" + category.ToLowerInvariant();
        string translated = text(key);
        return translated == key ? category : translated;
    }

    public static CreativeInventoryAction Draw(Vector2 viewport, CreativeInventoryModel state,
        IReadOnlyList<BlockDefinition> palette, CreativeHotbar hotbar, HashSet<string> favorites,
        Func<string, string> text, Func<BlockDefinition, string> name,
        Action<ImDrawListPtr, Vector2, Vector2, BlockDefinition> cube,
        Action changed)
    {
        var action = CreativeInventoryAction.None;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { state.DragMoved = false; state.LastDragSlot = null; state.DragOrigin = null; }
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left)) state.DragMoved = true;
        const float panelWidth = 780f;
        const float panelHeight = 660f;
        const float utilityTabHeight = 58f;
        const float windowHeight = panelHeight + utilityTabHeight;
        float scale = Math.Clamp(Math.Min((viewport.X - 24) / panelWidth, (viewport.Y - 24) / windowHeight), 0.35f, 1f);
        Vector2 size = new Vector2(panelWidth, windowHeight) * scale;
        float gap = 4 * scale;
        ImGui.SetNextWindowPos(viewport * 0.5f, ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.Border, Ink);
        ImGui.PushStyleColor(ImGuiCol.Text, Ink);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.94f, 0.94f, 0.92f, 1));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.66f, 0.66f, 0.63f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.9f, 0.86f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.56f, 0.56f, 0.53f, 1));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(0.44f, 0.44f, 0.42f, 1));
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Ink);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Vector4(0.36f, 0.36f, 0.34f, 1));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Paper);
        bool visible = ImGui.Begin("##creative-inventory", ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollWithMouse);
        if (visible)
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            Vector2 origin = ImGui.GetWindowPos();
            Vector2 panelSize = new(panelWidth * scale, panelHeight * scale);
            Vector2 contentOrigin = origin + new Vector2(16 * scale);
            Vector2 contentSize = panelSize - new Vector2(32 * scale);
            draw.AddRectFilled(origin, origin + panelSize, ImGui.ColorConvertFloat4ToU32(Paper));
            draw.AddRect(origin + new Vector2(3), origin + panelSize - new Vector2(3), Color(0.96f), 0, ImDrawFlags.None, 2);
            const float utilityTabWidth = 68f;
            Vector2 backpackPosition = origin + new Vector2((panelWidth - utilityTabWidth) * scale,
                (panelHeight - 2) * scale);
            Vector2 backpackSize = new(utilityTabWidth * scale, (utilityTabHeight + 2) * scale);
            LastBackpackTabBounds = new Vector4(backpackPosition.X, backpackPosition.Y,
                backpackPosition.X + backpackSize.X, backpackPosition.Y + backpackSize.Y);
            ImGui.SetCursorScreenPos(backpackPosition);
            if (ImGui.InvisibleButton("bottom-backpack", backpackSize))
            {
                state.Category = "Backpack";
                state.ResetScroll();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(text("palette_backpack"));
            DrawBottomUtilityTab(draw, backpackPosition, backpackSize, scale,
                state.Category == "Backpack", backpack: true);
            Vector2 favoritesPosition = backpackPosition - new Vector2((utilityTabWidth + 5) * scale, 0);
            LastFavoritesTabBounds = new Vector4(favoritesPosition.X, favoritesPosition.Y,
                favoritesPosition.X + backpackSize.X, favoritesPosition.Y + backpackSize.Y);
            ImGui.SetCursorScreenPos(favoritesPosition);
            if (ImGui.InvisibleButton("bottom-favorites", backpackSize))
            {
                state.Category = "Favorites";
                state.ResetScroll();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(CategoryLabel("Favorites", text));
            DrawBottomUtilityTab(draw, favoritesPosition, backpackSize, scale,
                state.Category == "Favorites", backpack: false);
            ImGui.SetCursorScreenPos(contentOrigin);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
            ImGui.BeginChild("main-paper-content", contentSize,
                ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            float width = ImGui.GetContentRegionAvail().X;
            Vector2 titlePosition = ImGui.GetCursorScreenPos();
            ImGui.TextUnformatted(text("palette_title"));
            Vector2 afterTitle = ImGui.GetCursorScreenPos();
            Vector2 closeSize = new(24 * scale);
            Vector2 closePosition = titlePosition + new Vector2(width - closeSize.X - 4 * scale, 0);
            ImGui.SetCursorScreenPos(closePosition);
            if (ImGui.InvisibleButton("close-inventory", closeSize)) action = CreativeInventoryAction.Close;
            bool closeHovered = ImGui.IsItemHovered();
            bool closePressed = ImGui.IsItemActive();
            Bevel(draw, closePosition, closeSize, closeHovered ? 0.82f : 0.69f, closePressed);
            float crossInset = 7 * scale;
            uint closeInk = Color(closeHovered ? 0.12f : 0.25f);
            draw.AddLine(closePosition + new Vector2(crossInset),
                closePosition + closeSize - new Vector2(crossInset), closeInk, Math.Max(1, 2 * scale));
            draw.AddLine(new Vector2(closePosition.X + closeSize.X - crossInset, closePosition.Y + crossInset),
                new Vector2(closePosition.X + crossInset, closePosition.Y + closeSize.Y - crossInset),
                closeInk, Math.Max(1, 2 * scale));
            Vector2 closeMinimum = ImGui.GetItemRectMin();
            Vector2 closeMaximum = ImGui.GetItemRectMax();
            LastCloseBounds = new Vector4(closeMinimum.X, closeMinimum.Y, closeMaximum.X, closeMaximum.Y);
            ImGui.SetCursorScreenPos(afterTitle);

            string[] categories = CreativeInventoryModel.Categories(palette)
                .Where(category => !category.Equals("Favorites", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (state.Category is not ("Backpack" or "Favorites") &&
                !categories.Contains(state.Category, StringComparer.OrdinalIgnoreCase))
            { state.Category = "All"; state.ResetScroll(); }
            float arrowWidth = 30 * scale;
            float tabGap = 5 * scale;
            float tabHeight = 54 * scale;
            float tabAreaWidth = width - 2 * (arrowWidth + tabGap);
            int perPage = Math.Max(2, (int)(tabAreaWidth / (91 * scale)));
            int pages = (categories.Length + perPage - 1) / perPage;
            state.TabPage = Math.Clamp(state.TabPage, 0, pages - 1);
            Vector2 tabOrigin = ImGui.GetCursorScreenPos();
            Vector2 tabSize = new((tabAreaWidth - (perPage - 1) * tabGap) / perPage, tabHeight);
            Vector2 nextArrow = tabOrigin + new Vector2(width - arrowWidth, 0);
            LastNextPageBounds = new Vector4(
                nextArrow.X, nextArrow.Y, nextArrow.X + arrowWidth, nextArrow.Y + tabHeight);
            // Both arrows stay at the ends even when the last page has fewer tabs.
            if (PageArrow("previous-tabs", tabOrigin, new Vector2(arrowWidth, tabHeight), false, state.TabPage > 0))
                state.TabPage--;
            if (PageArrow("next-tabs", nextArrow, new Vector2(arrowWidth, tabHeight), true, state.TabPage < pages - 1))
                state.TabPage++;
            draw.AddLine(tabOrigin + new Vector2(arrowWidth + tabGap, tabHeight),
                nextArrow + new Vector2(-tabGap, tabHeight), Color(0.37f), 2);
            string pageLabel = $"{state.TabPage + 1} / {pages}";
            Vector2 pageTextSize = ImGui.CalcTextSize(pageLabel);
            draw.AddText(new Vector2(nextArrow.X - pageTextSize.X - 10 * scale, origin.Y + 18 * scale), Color(0.36f), pageLabel);
            for (int i = state.TabPage * perPage; i < Math.Min(categories.Length, (state.TabPage + 1) * perPage); i++)
            {
                string category = categories[i];
                string label = CategoryLabel(category, text);
                Vector2 p = tabOrigin + new Vector2(arrowWidth + tabGap + (i % perPage) * (tabSize.X + tabGap), 0);
                ImGui.SetCursorScreenPos(p);
                bool selected = category.Equals(state.Category, StringComparison.OrdinalIgnoreCase);
                if (ImGui.InvisibleButton("tab-" + category, tabSize)) { state.Category = category; state.ResetScroll(); selected = true; }
                bool hovered = ImGui.IsItemHovered();
                Vector2 top = p + new Vector2(0, selected ? 0 : 5 * scale);
                Vector2 bottom = p + tabSize;
                float shade = selected ? 0.84f : hovered ? 0.78f : 0.69f;
                draw.AddRectFilled(top + new Vector2(2, 2), bottom + new Vector2(2, 0), Color(0.43f), 3 * scale, ImDrawFlags.RoundCornersTop);
                draw.AddRectFilled(top, bottom, Color(shade), 3 * scale, ImDrawFlags.RoundCornersTop);
                draw.AddRect(top, bottom, Color(0.37f), 3 * scale, ImDrawFlags.RoundCornersTop, 1);
                draw.AddLine(top + new Vector2(3, 2), new Vector2(bottom.X - 3, top.Y + 2), Color(0.96f), 1);
                if (selected)
                {
                    // The active tab opens into the panel instead of retaining a button's bottom edge.
                    draw.AddRectFilled(new Vector2(p.X + 1, bottom.Y - 3), bottom + new Vector2(-1, 3),
                        ImGui.ColorConvertFloat4ToU32(Paper));
                    draw.AddRectFilled(top + new Vector2(7 * scale, 5 * scale),
                        new Vector2(bottom.X - 7 * scale, top.Y + 7 * scale), Color(0.39f));
                }
                BlockDefinition? icon = palette.FirstOrDefault(b => category == "All" ||
                    (category == "Favorites" ? favorites.Contains(b.Key) : CreativeInventoryModel.CategoryOf(b).Equals(category, StringComparison.OrdinalIgnoreCase)));
                if (category == "Favorites")
                    DrawStar(draw, p + new Vector2(tabSize.X * 0.5f, 28 * scale), 11 * scale, Color(0.38f));
                else if (icon is not null)
                {
                    float iconWidth = Math.Min(38 * scale, tabSize.X - 14 * scale);
                    float iconHeight = iconWidth * 0.82f;
                    cube(draw, p + new Vector2((tabSize.X - iconWidth) * 0.5f,
                        (tabSize.Y - iconHeight) * 0.5f), new Vector2(iconWidth, iconHeight), icon);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
            }
            ImGui.SetCursorScreenPos(tabOrigin + new Vector2(0, tabHeight + gap));
            ImGui.Spacing();
            bool backpack = state.Category == "Backpack";
            if (!backpack)
            {
                ImGui.SetNextItemWidth(Math.Max(100, width - 100 * scale));
                if (ImGui.InputTextWithHint("##search", text("palette_search"), ref state.Search, 128))
                    state.ResetScroll();
                ImGui.SameLine();
                if (ImGui.Button(text("palette_clear"), new Vector2(90 * scale, 0))) { state.Search = ""; state.ResetScroll(); }
            }
            else
            {
                float ruleY = ImGui.GetCursorScreenPos().Y + ImGui.GetTextLineHeight() * 0.5f;
                draw.AddLine(new Vector2(ImGui.GetCursorScreenPos().X, ruleY),
                    new Vector2(ImGui.GetCursorScreenPos().X + width, ruleY), Color(0.57f), scale);
                ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeight()));
            }
            BlockDefinition[] blocks = state.Filter(palette, favorites, name);
            ImGui.TextUnformatted($"{CategoryLabel(state.Category, text)}  ·  {(backpack ? CreativeInventoryModel.BackpackSize : blocks.Length)}");

            const int columns = 9;
            float slot = MathF.Floor(Math.Min((width - 22 * scale - gap * (columns - 1)) / columns,
                Math.Min((width - gap * 9) / 10,
                    (ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing() * 2 - 18 * scale - gap * 5) / 6)));
            slot = Math.Max(12, slot);
            float pitch = slot + gap;
            // Creative inventory is always a complete 9x5 page. The whole panel is
            // scaled to the viewport, so reducing the row count would silently hide
            // backpack cells and break the fixed-grid interaction model.
            const int rows = 5;
            int maxRow = backpack ? 0 : CreativeInventoryModel.MaximumFirstRow(blocks.Length, columns, rows);
            state.FirstRow = Math.Clamp(state.FirstRow, 0, maxRow);
            float gridWidth = columns * pitch - gap + (backpack ? 0 : 22 * scale);
            // Backpack includes the trash cell beside the nine-slot quickbar. Centre
            // that complete ten-cell footer, rather than pinning the 9x5 bag to the
            // left and leaving an unfinished-looking empty strip on the right.
            float layoutWidth = backpack ? 10 * pitch - gap : gridWidth;
            Vector2 gridOrigin = ImGui.GetCursorScreenPos() +
                new Vector2(Math.Max(0, (width - layoutWidth) * 0.5f), 0);
            float gridHeight = rows * pitch - gap;
            if (!backpack && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) &&
                ImGui.IsMouseHoveringRect(gridOrigin, gridOrigin + new Vector2(gridWidth, gridHeight)))
                state.Scroll(ImGui.GetIO().MouseWheel, maxRow);

            // Only complete rows exist in the draw list. No pixel-scroll child or clipped half slots.
            for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                int index = (state.FirstRow + row) * columns + col;
                Vector2 p = gridOrigin + new Vector2(col * pitch, row * pitch);
                ImGui.SetCursorScreenPos(p);
                if (backpack)
                    StorageSlot(false, row * columns + col, p, slot, state, palette, hotbar, favorites, name, cube, changed);
                else
                {
                    BlockDefinition? block = index < blocks.Length ? blocks[index] : null;
                    string id = "item-" + index;
                    bool hovered = Slot(id, p, slot, block, block?.Id == hotbar.SelectedBlock.Id, favorites, name, cube, state.Carried.IsEmpty, true);
                    bool clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
                    bool releasedFromAnotherSlot = ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
                        state.DragOrigin is not null && state.DragOrigin != id;
                    if (hovered && !state.Carried.IsEmpty && (clicked || releasedFromAnotherSlot))
                    {
                        if (state.DiscardCarried()) changed();
                    }
                    else if (hovered && block is not null && clicked)
                    {
                        state.TakeCatalog(block.Key, ImGui.GetIO().KeyShift, hotbar);
                        state.DragOrigin = id;
                    }
                }
            }
            if (!backpack && blocks.Length == 0)
                draw.AddText(gridOrigin + new Vector2(12, gridHeight * 0.5f), Color(0.95f), text("palette_empty"));
            if (!backpack)
            {
            ImGui.SetCursorScreenPos(gridOrigin + new Vector2(gridWidth - 16 * scale, 0));
            int inverted = maxRow - state.FirstRow;
            ImGui.BeginDisabled(maxRow == 0);
            if (ImGui.VSliderInt("##rows", new Vector2(16 * scale, gridHeight), ref inverted, 0, Math.Max(1, maxRow), ""))
                state.FirstRow = Math.Clamp(maxRow - inverted, 0, maxRow);
            ImGui.EndDisabled();
            }
            ImGui.SetCursorScreenPos(gridOrigin + new Vector2(0, gridHeight + 10 * scale));
            ImGui.TextUnformatted(text("palette_hotbar"));
            Vector2 hotbarOrigin = new(gridOrigin.X, ImGui.GetCursorScreenPos().Y);
            for (int i = 0; i < CreativeHotbar.SlotCount; i++)
            {
                Vector2 p = hotbarOrigin + new Vector2(i * pitch, 0);
                ImGui.SetCursorScreenPos(p);
                StorageSlot(true, i, p, slot, state, palette, hotbar, favorites, name, cube, changed);
                draw.AddText(p + new Vector2(4, 2), Color(0.97f), (i + 1).ToString());
            }
            if (backpack)
            {
                Vector2 trash = hotbarOrigin + new Vector2(CreativeHotbar.SlotCount * pitch, 0);
                ImGui.SetCursorScreenPos(trash);
                ImGui.InvisibleButton("backpack-trash", new Vector2(slot));
                Vector2 trashMinimum = ImGui.GetItemRectMin();
                Vector2 trashMaximum = ImGui.GetItemRectMax();
                LastTrashBounds = new Vector4(
                    trashMinimum.X, trashMinimum.Y, trashMaximum.X, trashMaximum.Y);
                bool trashHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                Bevel(draw, trash, new Vector2(slot), trashHovered ? 0.64f : 0.48f, true);
                DrawTrash(draw, trash, slot, Color(trashHovered ? 0.22f : 0.31f));
                bool trashShift = ImGui.GetIO().KeyShift || ImGui.IsKeyDown(ImGuiKey.ModShift) ||
                                  ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
                if (trashHovered && trashShift &&
                    (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseReleased(ImGuiMouseButton.Left)))
                {
                    if (state.ClearInventory(hotbar)) changed();
                }
                else if (trashHovered && !state.Carried.IsEmpty &&
                    (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseReleased(ImGuiMouseButton.Left)))
                {
                    if (state.DiscardCarried()) changed();
                }
                if (trashHovered) ImGui.SetTooltip(text("palette_trash"));
            }
            ImGui.SetCursorScreenPos(hotbarOrigin + new Vector2(0, slot + gap));
            ImGui.TextDisabled(text(backpack ? "palette_bag_hint" : "palette_hint"));
            ImGui.SameLine(width - 108 * scale);
            if (ImGui.SmallButton(text("build_tools"))) action = CreativeInventoryAction.BuildTools;
            if (!state.Carried.IsEmpty)
            {
                var held = palette.FirstOrDefault(b => b.Key == state.Carried.Key);
                float heldSize = Math.Clamp(slot - 10, 20, 48);
                Vector2 cursor = ImGui.GetIO().MousePos - new Vector2(heldSize * 0.5f);
                LastCarriedBounds = new Vector4(cursor.X, cursor.Y, cursor.X + heldSize, cursor.Y + heldSize);
                var foreground = ImGui.GetForegroundDrawList();
                if (held is not null) cube(foreground, cursor, new Vector2(heldSize), held);
                Count(foreground, cursor, heldSize, state.Carried.Count);
            }
            else LastCarriedBounds = default;
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) { state.DragOrigin = null; state.ShiftDrag = false; state.LastDragSlot = null; state.DragMoved = false; }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right)) state.LastDragSlot = null;
        if (action != CreativeInventoryAction.None) { state.CancelCarry(hotbar); changed(); }
        ImGui.End();
        ImGui.PopStyleColor(11);
        ImGui.PopStyleVar(5);
        return action;
    }

    private static void DrawBottomUtilityTab(ImDrawListPtr draw, Vector2 p, Vector2 tabSize,
        float scale, bool selected, bool backpack)
    {
        Vector2 top = p + new Vector2(0, selected ? 0 : 5 * scale);
        Vector2 bottom = p + tabSize;
        if (selected)
        {
            // The selected lower tab opens into the panel: no independent top
            // edge and exactly the same paper fill as the inventory body.
            draw.AddRectFilled(new Vector2(p.X, p.Y - 3 * scale), bottom,
                ImGui.ColorConvertFloat4ToU32(Paper), 3 * scale, ImDrawFlags.RoundCornersBottom);
            draw.AddLine(new Vector2(p.X, p.Y), new Vector2(p.X, bottom.Y - 3 * scale), Color(0.37f), scale);
            draw.AddLine(new Vector2(p.X + 3 * scale, bottom.Y),
                new Vector2(bottom.X - 3 * scale, bottom.Y), Color(0.37f), scale);
            draw.AddLine(new Vector2(bottom.X, bottom.Y - 3 * scale),
                new Vector2(bottom.X, p.Y), Color(0.37f), scale);
        }
        else
        {
            draw.AddRectFilled(top + new Vector2(2, 2), bottom + new Vector2(2, 2),
                Color(0.43f), 3 * scale, ImDrawFlags.RoundCornersBottom);
            draw.AddRectFilled(top, bottom, Color(0.69f), 3 * scale, ImDrawFlags.RoundCornersBottom);
            draw.AddRect(top, bottom, Color(0.37f), 3 * scale, ImDrawFlags.RoundCornersBottom, scale);
        }
        Vector2 center = p + tabSize * 0.5f + new Vector2(0, 2 * scale);
        if (backpack)
        {
            Vector2 bagSize = new(30, 32);
            Vector2 bag = center - bagSize * scale * 0.5f;
            draw.AddRect(bag + new Vector2(7, -5) * scale, bag + new Vector2(23, 9) * scale,
                Color(0.28f), 4 * scale, ImDrawFlags.None, 3 * scale);
            Bevel(draw, bag, bagSize * scale, 0.51f, false);
        }
        else DrawStar(draw, center, 15 * scale, Color(0.32f));
    }

    private static void DrawTrash(ImDrawListPtr draw, Vector2 p, float size, uint color)
    {
        float s = size / 48f;
        Vector2 a = p + new Vector2(14, 16) * s;
        Vector2 b = p + new Vector2(34, 38) * s;
        draw.AddRect(a, b, color, 2 * s, ImDrawFlags.None, Math.Max(1, 2 * s));
        draw.AddLine(p + new Vector2(11, 13) * s, p + new Vector2(37, 13) * s, color, Math.Max(1, 3 * s));
        draw.AddRect(p + new Vector2(19, 9) * s, p + new Vector2(29, 13) * s, color, 1 * s, ImDrawFlags.None, Math.Max(1, 2 * s));
        for (int i = 0; i < 3; i++)
        {
            float x = p.X + (19 + i * 5) * s;
            draw.AddLine(new Vector2(x, p.Y + 20 * s), new Vector2(x, p.Y + 34 * s), color, Math.Max(1, s));
        }
    }

    private static bool Slot(string id, Vector2 p, float size, BlockDefinition? block, bool selected,
        HashSet<string> favorites, Func<BlockDefinition, string> name,
        Action<ImDrawListPtr, Vector2, Vector2, BlockDefinition> cube, bool tooltip, bool catalog = false)
    {
        ImGui.InvisibleButton(id, new Vector2(size));
        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var draw = ImGui.GetWindowDrawList();
        Bevel(draw, p, new Vector2(size), hovered ? 0.64f : 0.48f, true);
        if (selected) draw.AddRect(p + new Vector2(2), p + new Vector2(size - 2), Color(0.96f), 0, ImDrawFlags.None, 2);
        if (block is not null)
        {
            float inset = Math.Clamp(size * 0.08f, 2f, 5f);
            cube(draw, p + new Vector2(inset), new Vector2(size - inset * 2), block);
            if (catalog && favorites.Contains(block.Key)) draw.AddText(p + new Vector2(size - 13, 2), 0xff66ddff, "*");
            if (catalog && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                if (!favorites.Add(block.Key)) favorites.Remove(block.Key);
            if (hovered && tooltip)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(name(block));
                ImGui.TextDisabled(block.Key);
                ImGui.EndTooltip();
            }
        }
        return hovered;
    }

    public static void Count(ImDrawListPtr draw, Vector2 p, float size, int count)
    {
        if (count <= 1) return;
        string label = count.ToString();
        float fontSize = Math.Clamp(size * 0.32f, 8f, ImGui.GetFontSize());
        Vector2 at = p + new Vector2(size) - ImGui.CalcTextSize(label) * (fontSize / ImGui.GetFontSize()) - new Vector2(2);
        draw.AddText(ImGui.GetFont(), fontSize, at + Vector2.One, 0xff202020, label);
        draw.AddText(ImGui.GetFont(), fontSize, at, 0xffffffff, label);
    }

    private static void StorageSlot(bool quick, int index, Vector2 p, float size, CreativeInventoryModel state,
        IReadOnlyList<BlockDefinition> palette, CreativeHotbar hotbar, HashSet<string> favorites,
        Func<BlockDefinition, string> name, Action<ImDrawListPtr, Vector2, Vector2, BlockDefinition> cube, Action changed)
    {
        string id = (quick ? "quick-" : "bag-") + index;
        var stack = state.Stack(quick, index, hotbar);
        var block = stack.IsEmpty ? null : palette.FirstOrDefault(b => b.Key == stack.Key);
        bool hovered = Slot(id, p, size, block, quick && index == hotbar.SelectedSlot, favorites, name, cube, state.Carried.IsEmpty);
        if (!stack.IsEmpty && block is null) ImGui.GetWindowDrawList().AddText(p + new Vector2(5), 0xffffffff, "?");
        Count(ImGui.GetWindowDrawList(), p, size, stack.Count);
        if (!hovered) return;
        bool shift = ImGui.GetIO().KeyShift || ImGui.IsKeyDown(ImGuiKey.ModShift) ||
                     ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            state.DragOrigin = id; state.LastDragSlot = id;
            state.ShiftDrag = shift && state.Carried.IsEmpty;
            if (state.ShiftDrag) state.Transfer(quick, index, hotbar);
            else state.ClickSlot(quick, index, hotbar);
            changed();
        }
        else if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && state.LastDragSlot != id)
        {
            if (state.ShiftDrag) { state.Transfer(quick, index, hotbar); state.LastDragSlot = id; changed(); }
            else if (!stack.IsEmpty && stack.Key == state.Carried.Key)
            { state.Collect(quick, index, hotbar); state.LastDragSlot = id; changed(); }
        }
        else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && state.DragMoved &&
                 !state.ShiftDrag && state.DragOrigin is not null && state.DragOrigin != id && state.LastDragSlot != id)
        { state.ClickSlot(quick, index, hotbar); changed(); }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) ||
            (ImGui.IsMouseDown(ImGuiMouseButton.Right) && !state.Carried.IsEmpty && state.LastDragSlot != id))
        { state.ClickSlot(quick, index, hotbar, right: true); state.LastDragSlot = id; changed(); }
        int wheel = (int)ImGui.GetIO().MouseWheel;
        for (int step = 0; step < Math.Min(64, Math.Abs(wheel)); step++)
        {
            if (wheel < 0) state.Transfer(quick, index, hotbar, 1); else state.PullOne(quick, index, hotbar);
            changed();
        }
    }

    private static bool PageArrow(string id, Vector2 p, Vector2 size, bool right, bool enabled)
    {
        ImGui.SetCursorScreenPos(p);
        ImGui.BeginDisabled(!enabled);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        bool pressed = ImGui.IsItemActive();
        ImGui.EndDisabled();
        var draw = ImGui.GetWindowDrawList();
        Vector2 face = new(p.X, p.Y + (size.Y - size.X) * 0.5f);
        Bevel(draw, face, new Vector2(size.X), enabled && hovered ? 0.83f : 0.72f, pressed);
        Vector2 center = face + new Vector2(size.X * 0.5f);
        float radius = size.X * 0.17f;
        float direction = right ? 1 : -1;
        uint ink = Color(enabled ? 0.23f : 0.57f);
        draw.AddLine(center + new Vector2(-direction * radius * 0.5f, -radius),
            center + new Vector2(direction * radius * 0.5f, 0), ink, 2);
        draw.AddLine(center + new Vector2(direction * radius * 0.5f, 0),
            center + new Vector2(-direction * radius * 0.5f, radius), ink, 2);
        return enabled && clicked;
    }

    private static void DrawStar(ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        Vector2 Point(int i)
        {
            float angle = -MathF.PI * 0.5f + i * MathF.PI / 5;
            float r = (i % 2 == 0 ? 1 : 0.46f) * radius;
            return center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
        }
        for (int i = 0; i < 10; i++) draw.AddTriangleFilled(center, Point(i), Point(i + 1), color);
    }

    private static void Bevel(ImDrawListPtr draw, Vector2 p, Vector2 size, float shade, bool inset)
    {
        draw.AddRectFilled(p, p + size, Color(shade));
        uint light = Color(inset ? 0.28f : 0.96f), dark = Color(inset ? 0.94f : 0.30f);
        draw.AddLine(p, p + new Vector2(size.X, 0), light, 2);
        draw.AddLine(p, p + new Vector2(0, size.Y), light, 2);
        draw.AddLine(p + new Vector2(0, size.Y), p + size, dark, 2);
        draw.AddLine(p + new Vector2(size.X, 0), p + size, dark, 2);
    }
}
