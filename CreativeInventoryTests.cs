using System.Numerics;
using ImGuiNET;
using RTXSandbox.Configuration;
using RTXSandbox.Gameplay;
using RTXSandbox.Engine;
using RTXSandbox.UI;
using RTXSandbox.World;

namespace RTXSandbox;

internal static class CreativeInventoryTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Model()
    {
        var registry = new BlockRegistry();
        var palette = registry.PlaceableDefinitions();
        var state = new CreativeInventoryModel();
        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] categories = CreativeInventoryModel.Categories(palette);
        Check(categories[0] == "All" && categories[^1] == "Favorites" && categories.Contains("Glass") && !categories.Contains("Backpack"),
            "top tabs must start with All, end with Favorites, and exclude the side backpack");
        Check(state.Filter(palette, favorites, b => b.DisplayName).Length == palette.Count, "All lost items");
        state.Category = "Glass";
        var glass = state.Filter(palette, favorites, b => b.DisplayName);
        Check(glass.Length >= 15 && glass.All(b => CreativeInventoryModel.CategoryOf(b) == "Glass"), "glass family split across tabs");
        state.Category = "Favorites";
        Check(state.Filter(palette, favorites, b => b.DisplayName).Length == 0, "empty favorites not empty");
        favorites.Add(glass[0].Key);
        Check(state.Filter(palette, favorites, b => b.DisplayName).Single().Id == glass[0].Id, "favorite filtering failed");
        state.Category = "All";
        state.Search = "glass red";
        Check(state.Filter(palette, favorites, b => b.DisplayName).Any(b => b.Key.EndsWith("red_glass")), "multi-term search failed");
        state.Search = "不存在的物品123";
        Check(state.Filter(palette, favorites, b => b.DisplayName).Length == 0, "zero-result search failed");
        var custom = new BlockDefinition("test:tab", "New item", Vector3.One) { Category = "Custom / Material" };
        Check(CreativeInventoryModel.Categories(palette.Append(custom)).Contains("Custom"), "new categories require code changes");
        Check(CreativeInventoryModel.MaximumFirstRow(46, 9, 5) == 1, "partial last row was lost");
        Check(CreativeInventoryModel.MaximumFirstRow(45, 9, 5) == 0, "exact page has overflow");
        state.Scroll(-0.5f, 10); Check(state.FirstRow == 0, "fractional wheel moved half a row");
        state.Scroll(-0.5f, 10); Check(state.FirstRow == 1, "wheel accumulation failed");
        state.Scroll(-100, 10); Check(state.FirstRow == 10, "scroll past end");
        state.Scroll(100, 10); Check(state.FirstRow == 0, "scroll past start");
    }

    public static unsafe void Interface()
    {
        IntPtr previous = ImGui.GetCurrentContext();
        IntPtr context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(1280, 720);
            io.DeltaTime = 1f / 60;
            io.NativePtr->IniFilename = null;
            io.Fonts.AddFontDefault();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytes);
            var registry = new BlockRegistry();
            var hotbar = new CreativeHotbar(registry);
            var state = new CreativeInventoryModel();
            var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var slots = new List<(Vector2 P, Vector2 Size, BlockDefinition Block)>();
            var icons = new List<(Vector2 P, Vector2 Size, BlockDefinition Block)>();
            CreativeInventoryAction action = CreativeInventoryAction.None;
            void Frame()
            {
                slots.Clear(); icons.Clear();
                ImGui.NewFrame();
                action = CreativeInventoryPanel.Draw(io.DisplaySize, state, registry.PlaceableDefinitions(), hotbar,
                    favorites, k => UiText.Get(UiLanguage.English, k), b => b.DisplayName,
                    (draw, p, size, b) => { if (draw.NativePtr == ImGui.GetForegroundDrawList().NativePtr) return; if (size.X == size.Y) slots.Add((p, size, b)); else icons.Add((p, size, b)); },
                    () => { });
                ImGui.Render();
                Check(ImGui.GetDrawData().TotalVtxCount > 0, "UI did not render");
            }
            void Click(Vector2 p, int button = 0)
            {
                io.AddMousePosEvent(p.X, p.Y); Frame();
                io.AddMouseButtonEvent(button, true); Frame();
                io.AddMouseButtonEvent(button, false); Frame();
                Frame(); // A release can change pages after the earlier widgets were drawn.
            }
            Frame(); Frame();
            Check(slots.Count == 54, $"expected 9x5 items plus 9 hotbar slots, got {slots.Count}");
            var first = slots[0];
            var second = slots[1];
            var previousSelected = hotbar.SelectedBlock.Id;
            Click(second.P + new Vector2(10));
            Check(hotbar.SelectedBlock.Id == previousSelected && state.Carried.Key == second.Block.Key && state.Carried.Count == 1,
                "catalog click must carry one, not assign material");
            Vector4 carriedBounds = CreativeInventoryPanel.LastCarriedBounds;
            Vector2 carriedCenter = new((carriedBounds.X + carriedBounds.Z) * 0.5f,
                (carriedBounds.Y + carriedBounds.W) * 0.5f);
            Check(Vector2.Distance(carriedCenter, io.MousePos) < 0.01f,
                $"carried item is not centered on the pointer ({carriedCenter} vs {io.MousePos})");
            Click(first.P + new Vector2(10));
            Check(state.Carried.IsEmpty, "clicking the creative catalog did not return the carried item");
            Click(second.P + new Vector2(10));
            state.CancelCarry(hotbar); Frame();
            io.AddKeyEvent(ImGuiKey.ModShift, true); Frame();
            Click(second.P + new Vector2(10));
            Check(state.Carried.Count == 1, "creative catalog must always carry one material reference");
            io.AddKeyEvent(ImGuiKey.ModShift, false); Frame();
            var quick = slots[^1];
            hotbar.SetStack(8, default); Frame();
            Click(quick.P + new Vector2(10));
            Check(state.Carried.IsEmpty && hotbar.GetStack(8).Count == 1, "click-to-place failed");
            // Drag from the catalog into an empty hotbar slot without selecting that slot.
            hotbar.SetStack(8, default); Frame();
            io.AddMousePosEvent(second.P.X + 10, second.P.Y + 10); Frame();
            io.AddMouseButtonEvent(0, true); Frame();
            io.AddMousePosEvent(quick.P.X + 10, quick.P.Y + 10); Frame();
            io.AddMouseButtonEvent(0, false); Frame(); Frame();
            Check(state.Carried.IsEmpty && hotbar.GetStack(8).Key == second.Block.Key && hotbar.GetStack(8).Count == 1,
                "press-drag-release didn't place item");
            Click(first.P + new Vector2(10), 1);
            Check(favorites.Contains(first.Block.Key), "right-click didn't favorite");
            Vector4 favoritesBounds = CreativeInventoryPanel.LastFavoritesTabBounds;
            Click(new Vector2((favoritesBounds.X + favoritesBounds.Z) * 0.5f,
                (favoritesBounds.Y + favoritesBounds.W) * 0.5f));
            Check(state.Category == "Favorites" && slots.Count == 10 && slots[0].Block.Key == first.Block.Key,
                "fixed bottom Favorites tab did not open the saved favorite");
            state.Category = "All"; Frame();
            io.AddMousePosEvent(first.P.X + 10, first.P.Y + 10);
            io.AddMouseWheelEvent(0, -1); Frame(); Frame();
            Check(state.FirstRow == 1 && slots[0].P == first.P, "scroll is not one fixed row");
            Check(slots[0].Block.Id != first.Block.Id, "scroll didn't replace contents");
            Vector2 scrollBottom = new(slots[8].P.X + slots[8].Size.X + 20,
                slots[44].P.Y + slots[44].Size.Y);
            Click(scrollBottom);
            Check(state.FirstRow == CreativeInventoryModel.MaximumFirstRow(registry.PlaceableDefinitions().Count, 9, 5),
                "scrollbar didn't reach last complete row");
            Vector2 scrollTop = new(scrollBottom.X, first.P.Y);
            Click(scrollTop);
            Check(state.FirstRow == 0 && slots[0].P == first.P, "scrollbar didn't snap back to first row");
            var glassIcon = icons.Single(i => CreativeInventoryModel.CategoryOf(i.Block) == "Glass");
            Click(glassIcon.P + new Vector2(10));
            Check(state.Category == "Glass" && state.FirstRow == 0, "tab selection didn't reset row");
            Check(slots.Take(slots.Count - 9).All(s => CreativeInventoryModel.CategoryOf(s.Block) == "Glass"), "tab contains wrong items");
            // Backpack is a side utility tab and has the same 9x5 slot geometry as the catalog.
            Vector4 backpackBounds = CreativeInventoryPanel.LastBackpackTabBounds;
            Click(new Vector2((backpackBounds.X + backpackBounds.Z) * 0.5f,
                (backpackBounds.Y + backpackBounds.W) * 0.5f));
            Check(state.Category == "Backpack", "side backpack tab cannot be clicked");
            state.RestoreBackpack(Enumerable.Repeat(new CreativeStack(second.Block.Key, 1), CreativeInventoryModel.BackpackSize)); Frame();
            Check(slots.Count == 54, "backpack must have 45 + 9 hotbar slots");
            Check(slots[0].Size == first.Size, "backpack and catalog slots use different sizes");
            var bagFirst = slots[0];
            var bagSecond = slots[1];
            var bagThird = slots[2];
            Click(bagFirst.P + new Vector2(8), 1);
            Check(state.Carried.Count == 1 && state.GetBackpack(0).IsEmpty, "click didn't pick the material reference");
            Click(bagSecond.P + new Vector2(8), 1);
            Check(state.Carried.IsEmpty && state.GetBackpack(1).Count == 1, "same-material click did not return the cursor item");
            state.TakeCatalog(second.Block.Key, true, hotbar); Frame();
            Click(bagThird.P + new Vector2(8));
            Check(state.Carried.IsEmpty && state.GetBackpack(2).Count == 1, "returning a creative item created a stack count");
            state.CancelCarry(hotbar); Frame();
            // Empty-handed Shift drag transfers every traversed stack.
            hotbar.SetStack(7, default);
            hotbar.SetStack(8, default);
            Frame();
            io.AddKeyEvent(ImGuiKey.ModShift, true); Frame();
            io.AddMousePosEvent(bagFirst.P.X + 8, bagFirst.P.Y + 8); Frame();
            io.AddMouseButtonEvent(0, true); Frame();
            io.AddMousePosEvent(bagSecond.P.X + 8, bagSecond.P.Y + 8); Frame();
            io.AddMouseButtonEvent(0, false); Frame();
            io.AddKeyEvent(ImGuiKey.ModShift, false); Frame();
            Check(state.GetBackpack(0).IsEmpty && state.GetBackpack(1).IsEmpty, "Shift drag missed a stack");
            // LMB drag collects matching stacks instead of duplicating them.
            state.RestoreBackpack(Enumerable.Repeat(new CreativeStack(second.Block.Key, 1), CreativeInventoryModel.BackpackSize)); Frame();
            io.AddMousePosEvent(bagFirst.P.X + 8, bagFirst.P.Y + 8); Frame();
            io.AddMouseButtonEvent(0, true); Frame();
            io.AddMousePosEvent(bagSecond.P.X + 8, bagSecond.P.Y + 8); Frame();
            io.AddMouseButtonEvent(0, false); Frame();
            Check(state.Carried.Count == 1 && state.GetBackpack(0).IsEmpty && state.GetBackpack(1).IsEmpty, "LMB collect failed");
            // The independent bottom-right trash destroys only the cursor item.
            Vector4 trashBounds = CreativeInventoryPanel.LastTrashBounds;
            Vector2 trash = new((trashBounds.X + trashBounds.Z) * 0.5f,
                (trashBounds.Y + trashBounds.W) * 0.5f);
            Click(trash);
            Check(state.Carried.IsEmpty, "trash did not destroy the carried material");
            state.CancelCarry(hotbar); Frame();
            state.RestoreBackpack(Enumerable.Repeat(
                new CreativeStack(second.Block.Key, 1), CreativeInventoryModel.BackpackSize));
            for (int i = 0; i < CreativeHotbar.SlotCount; i++)
                hotbar.SetStack(i, new CreativeStack(first.Block.Key, 1));
            Frame();
            io.AddKeyEvent(ImGuiKey.LeftShift, true);
            io.NativePtr->KeyShift = 1;
            Frame();
            Click(trash);
            io.AddKeyEvent(ImGuiKey.LeftShift, false);
            io.NativePtr->KeyShift = 0;
            Frame();
            Check(Enumerable.Range(0, CreativeInventoryModel.BackpackSize).All(i => state.GetBackpack(i).IsEmpty) &&
                  Enumerable.Range(0, CreativeHotbar.SlotCount).All(i => hotbar.GetStack(i).IsEmpty),
                "Shift-clicking trash did not clear the backpack and hotbar");
            for (int i = 0; i < CreativeHotbar.SlotCount; i++)
                hotbar.SetStack(i, new CreativeStack(first.Block.Key, 1));
            Frame();
            // Browse the next tab page, then select a category there.
            Vector4 arrowBounds = CreativeInventoryPanel.LastNextPageBounds;
            Vector2 pageArrowHit = new(
                (arrowBounds.X + arrowBounds.Z) * 0.5f,
                (arrowBounds.Y + arrowBounds.W) * 0.5f);
            Click(pageArrowHit);
            Check(state.TabPage == 1,
                $"next category page is inaccessible (categories={CreativeInventoryModel.Categories(registry.PlaceableDefinitions()).Length}, hit={pageArrowHit})");
            var lastPageIcon = icons.Last();
            Click(lastPageIcon.P + new Vector2(10));
            Check(state.Category == CreativeInventoryModel.CategoryOf(lastPageIcon.Block),
                $"second-page category selection failed: selected={state.Category}, icon={lastPageIcon.Block.Key}, expected={CreativeInventoryModel.CategoryOf(lastPageIcon.Block)}, pos={lastPageIcon.P}");
            state.Search = "nonexistent-item-123"; Frame();
            Check(slots.Count == 9, "empty search must retain hotbar only");
            state.Search = ""; state.Category = "All";
            for (int i = 0; i < CreativeHotbar.SlotCount; i++)
                hotbar.SetStack(i, new CreativeStack(first.Block.Key, 1));
            state.RestoreBackpack(Enumerable.Repeat(
                new CreativeStack(second.Block.Key, 1), CreativeInventoryModel.BackpackSize));
            foreach (Vector2 viewport in new[] { new Vector2(800, 480), new Vector2(480, 800), new Vector2(1920, 1080) })
            {
                io.DisplaySize = viewport; Frame(); Frame();
                Check(slots.Count == 54, "resizing lost complete 9x5 grid");
                Check(slots.All(s => s.P.X >= 0 && s.P.Y >= 0 && s.P.X + s.Size.X <= viewport.X && s.P.Y + s.Size.Y <= viewport.Y), "slots outside viewport");
                state.Category = "Backpack"; Frame(); Frame();
                Check(slots.Count == 54, $"resizing lost backpack slots ({viewport}, count={slots.Count})");
                Check(slots.All(s => s.P.X >= 0 && s.P.Y >= 0 && s.P.X + s.Size.X <= viewport.X && s.P.Y + s.Size.Y <= viewport.Y), "backpack outside viewport");
                state.Category = "All";
            }
            Frame(); Frame();
            var beforeClose = state.SnapshotBackpack();
            Click(slots[0].P + new Vector2(8));
            Check(!state.Carried.IsEmpty, "close test has no cursor stack");
            CreativeInventoryAction clickedAction = CreativeInventoryAction.None;
            Vector4 closeBounds = CreativeInventoryPanel.LastCloseBounds;
            Vector2 close = new(
                (closeBounds.X + closeBounds.Z) * 0.5f,
                (closeBounds.Y + closeBounds.W) * 0.5f);
            io.AddMousePosEvent(close.X, close.Y); Frame();
            io.AddMouseButtonEvent(0, true); Frame();
            io.AddMouseButtonEvent(0, false); Frame(); clickedAction = action;
            Check(clickedAction == CreativeInventoryAction.Close && state.Carried.IsEmpty && beforeClose.SequenceEqual(state.SnapshotBackpack()),
                "X did not cancel unfinished carry safely");
        }
        finally { ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous); }
    }

    public static void Transfers()
    {
        var registry = new BlockRegistry();
        var hotbar = new CreativeHotbar(registry);
        var state = new CreativeInventoryModel();
        string first = hotbar.GetKey(0), second = hotbar.GetKey(1);
        state.TakeCatalog(first, false, hotbar);
        state.ClickSlot(false, 44, hotbar);
        Check(state.GetBackpack(44).Count == 1 && state.Carried.IsEmpty, "45th slot unavailable");
        state.TakeCatalog(first, true, hotbar);
        state.ClickSlot(false, 44, hotbar);
        Check(state.GetBackpack(44).Count == 1 && state.Carried.IsEmpty, "returning the same material did not clear cursor");
        state.ClickSlot(false, 44, hotbar);
        Check(state.Carried.Count == 1 && state.GetBackpack(44).IsEmpty, "material pickup failed");
        state.ClickSlot(true, 1, hotbar);
        Check(state.Carried.Key == second && hotbar.GetStack(1).Key == first, "swap failed");
        var savedDuringCarry = state.SnapshotBackpack();
        Check(savedDuringCarry[44].Count == 1 && state.SnapshotHotbar(hotbar)[1].Key == second, "save during carry loses items");
        state.CancelCarry(hotbar);
        Check(state.GetBackpack(44).Count == 1 && hotbar.GetStack(1).Key == second, "closing didn't roll back unfinished swap");
        for (int i = 0; i < 9; i++) hotbar.SetStack(i, new(first, 1));
        state.Transfer(false, 44, hotbar);
        Check(state.GetBackpack(44).Count == 1, "full destination swallowed a material");
        hotbar.SetStack(8, default);
        state.Transfer(false, 44, hotbar);
        Check(state.GetBackpack(44).IsEmpty && hotbar.GetStack(8).Count == 1, "creative transfer wrong");
        state.Transfer(true, 8, hotbar);
        Check(hotbar.GetBlock(8).Id == 0 && hotbar.GetStack(8).IsEmpty, "empty hotbar must be air");
        var restored = new CreativeHotbar(registry, hotbar.Snapshot(), savedCounts: hotbar.SnapshotCounts());
        Check(restored.GetBlock(8).Id == 0 && restored.GetStack(0).Count == 1, "hotbar material persistence failed");
        string json = System.Text.Json.JsonSerializer.Serialize(new GameSettings {
            CreativeBackpack = state.SnapshotBackpack(), CreativeHotbar = hotbar.Snapshot(), CreativeHotbarCounts = hotbar.SnapshotCounts() });
        var settings = System.Text.Json.JsonSerializer.Deserialize<GameSettings>(json)!;
        var loaded = new CreativeInventoryModel(); loaded.RestoreBackpack(settings.CreativeBackpack);
        Check(loaded.SnapshotBackpack().SequenceEqual(state.SnapshotBackpack()), "backpack JSON round trip failed");
        // Stress every storage operation without introducing catalog copies. Cancel can
        // restore a transaction snapshot, so compare with the initial upper bound instead
        // of the immediately preceding (possibly mid-transaction) value.
        var random = new Random(45);
        int Total() => Enumerable.Range(0, CreativeInventoryModel.BackpackSize).Sum(i => state.GetBackpack(i).Count) + Enumerable.Range(0, 9).Sum(i => hotbar.GetStack(i).Count) + state.Carried.Count;
        int maximum = Total();
        for (int n = 0; n < 2000; n++)
        {
            bool quick = random.Next(2) == 0; int slot = random.Next(quick ? 9 : CreativeInventoryModel.BackpackSize);
            switch (random.Next(5))
            {
                case 0: state.ClickSlot(quick, slot, hotbar); break;
                case 1: state.ClickSlot(quick, slot, hotbar, true); break;
                case 2: state.Transfer(quick, slot, hotbar, random.Next(1, 65)); break;
                case 3: state.Collect(quick, slot, hotbar); break;
                default: state.CancelCarry(hotbar); break;
            }
            Check(Total() <= maximum, $"creative references duplicated at operation {n}");
            Check(Enumerable.Range(0, CreativeInventoryModel.BackpackSize).All(i => state.GetBackpack(i).Count is 0 or 1), "invalid creative slot count");
        }
    }
}
