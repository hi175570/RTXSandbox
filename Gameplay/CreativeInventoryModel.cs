using RTXSandbox.Engine;

namespace RTXSandbox.Gameplay;

public readonly record struct CreativeStack(string? Key, int Count)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Key) || Count <= 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public CreativeStack Normalized => IsEmpty ? default : new(Key, 1);
}

/// <summary>Built-in creative inventory categories and whole-row scrolling, shared by all hosts.</summary>
public sealed class CreativeInventoryModel
{
    public string Category = "All";
    public string Search = string.Empty;
    public int FirstRow;
    public int TabPage;
    private float _wheelRemainder;
    public const int BackpackSize = 45;
    private readonly CreativeStack[] _backpack = new CreativeStack[BackpackSize];
    private CreativeStack[]? _beforeBackpack, _beforeHotbar;
    public CreativeStack Carried { get; private set; }
    // UI gesture state belongs to this inventory, never to a global ImGui singleton.
    public string? DragOrigin;
    public string? LastDragSlot;
    public bool ShiftDrag;
    public bool DragMoved;

    public CreativeStack GetBackpack(int slot) => _backpack[slot];
    public void RestoreBackpack(IEnumerable<CreativeStack>? saved)
    {
        Array.Clear(_backpack);
        if (saved is not null)
        {
            CreativeStack[] source = saved.Select(stack => stack.Normalized).Where(stack => !stack.IsEmpty).ToArray();
            // The first release briefly exposed 81 slots. Compact an existing layout into
            // the new 45-slot bag instead of silently losing stacks from rows six to nine.
            foreach (CreativeStack stack in source)
            {
                for (int i = 0; i < BackpackSize; i++)
                {
                    if (!_backpack[i].IsEmpty) continue;
                    _backpack[i] = stack;
                    break;
                }
            }
        }
    }
    public CreativeStack[] SnapshotBackpack() => (_beforeBackpack ?? _backpack).ToArray();
    public CreativeStack[] SnapshotHotbar(CreativeHotbar hotbar) =>
        _beforeHotbar?.ToArray() ?? Enumerable.Range(0, CreativeHotbar.SlotCount).Select(hotbar.GetStack).ToArray();

    private void Begin(CreativeHotbar hotbar)
    {
        if (_beforeBackpack is not null) return;
        _beforeBackpack = _backpack.ToArray();
        _beforeHotbar = SnapshotHotbar(hotbar);
    }
    private void CommitIfPlaced()
    {
        if (!Carried.IsEmpty) return;
        _beforeBackpack = _beforeHotbar = null;
    }
    public void CancelCarry(CreativeHotbar hotbar)
    {
        if (_beforeBackpack is not null) Array.Copy(_beforeBackpack, _backpack, BackpackSize);
        if (_beforeHotbar is not null)
            for (int i = 0; i < CreativeHotbar.SlotCount; i++) hotbar.SetStack(i, _beforeHotbar[i]);
        Carried = default;
        _beforeBackpack = _beforeHotbar = null;
        DragOrigin = LastDragSlot = null;
        ShiftDrag = false;
        DragMoved = false;
    }
    public void TakeCatalog(string key, bool fullStack, CreativeHotbar hotbar)
    {
        if (!Carried.IsEmpty && Carried.Key != key) return;
        Begin(hotbar);
        Carried = new(key, 1);
    }
    public bool DiscardCarried()
    {
        if (Carried.IsEmpty) return false;
        Carried = default;
        _beforeBackpack = _beforeHotbar = null;
        DragOrigin = LastDragSlot = null;
        ShiftDrag = DragMoved = false;
        return true;
    }
    public bool ClearInventory(CreativeHotbar hotbar)
    {
        bool changed = !Carried.IsEmpty || _backpack.Any(stack => !stack.IsEmpty) ||
                       Enumerable.Range(0, CreativeHotbar.SlotCount).Any(i => !hotbar.GetStack(i).IsEmpty);
        if (!changed) return false;
        Array.Clear(_backpack);
        for (int i = 0; i < CreativeHotbar.SlotCount; i++) hotbar.SetStack(i, default);
        Carried = default;
        _beforeBackpack = _beforeHotbar = null;
        DragOrigin = LastDragSlot = null;
        ShiftDrag = DragMoved = false;
        return true;
    }
    public CreativeStack Stack(bool quick, int slot, CreativeHotbar hotbar) => quick ? hotbar.GetStack(slot) : _backpack[slot];
    private void Set(bool quick, int slot, CreativeStack stack, CreativeHotbar hotbar)
    {
        if (quick) hotbar.SetStack(slot, stack); else _backpack[slot] = stack.Normalized;
    }
    public void ClickSlot(bool quick, int slot, CreativeHotbar hotbar, bool right = false)
    {
        CreativeStack target = Stack(quick, slot, hotbar);
        if (target.IsEmpty && Carried.IsEmpty) return;
        Begin(hotbar);
        if (Carried.IsEmpty)
        {
            Carried = target.Normalized;
            Set(quick, slot, default, hotbar);
        }
        else if (target.IsEmpty || target.Key == Carried.Key)
        {
            Set(quick, slot, Carried, hotbar);
            Carried = default;
        }
        else if (!right)
        {
            Set(quick, slot, Carried, hotbar);
            Carried = target;
        }
        CommitIfPlaced();
    }
    public void Collect(bool quick, int slot, CreativeHotbar hotbar)
    {
        var target = Stack(quick, slot, hotbar);
        if (Carried.IsEmpty || target.IsEmpty || target.Key != Carried.Key) return;
        Set(quick, slot, default, hotbar);
    }
    public void Transfer(bool quick, int slot, CreativeHotbar hotbar, int limit = 64)
    {
        if (!Carried.IsEmpty) return;
        var source = Stack(quick, slot, hotbar);
        if (source.IsEmpty) return;
        bool moved = false;
        int length = quick ? BackpackSize : CreativeHotbar.SlotCount;
        for (int i = 0; i < length; i++)
        {
            var target = Stack(!quick, i, hotbar);
            if (!target.IsEmpty) continue;
            Set(!quick, i, source, hotbar);
            moved = true;
            break;
        }
        if (moved) Set(quick, slot, default, hotbar);
    }
    public void PullOne(bool quick, int slot, CreativeHotbar hotbar)
    {
        if (!Carried.IsEmpty) return;
        var target = Stack(quick, slot, hotbar);
        // Creative references have no stack count; scrolling upward has nothing to add.
    }

    public static string CategoryOf(BlockDefinition block)
    {
        if (block.Tags.Any(t => t.Equals("glass", StringComparison.OrdinalIgnoreCase)) ||
            block.Key[(block.Key.IndexOf(':') + 1)..].Contains("glass", StringComparison.OrdinalIgnoreCase))
            return "Glass";
        if (block.Tags.Any(t => t.Equals("energy-projector", StringComparison.OrdinalIgnoreCase) ||
                                t.Equals("tool", StringComparison.OrdinalIgnoreCase)))
            return "Tools";
        string category = block.Category.Split('/')[0].Trim();
        return string.IsNullOrEmpty(category) ? "General" : category;
    }

    public static string[] Categories(IEnumerable<BlockDefinition> blocks)
    {
        string[] order = ["Nature", "Wood", "Building", "Glass", "Metal", "Optical", "Lighting", "Technology", "Tools", "Spheres", "Special", "General"];
        return new[] { "All" }.Concat(blocks.Select(CategoryOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => Array.FindIndex(order, x => x.Equals(c, StringComparison.OrdinalIgnoreCase)) is int i && i >= 0 ? i : order.Length)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase))
            .Append("Favorites").ToArray();
    }

    public BlockDefinition[] Filter(IEnumerable<BlockDefinition> blocks, IReadOnlySet<string> favorites,
        Func<BlockDefinition, string> name)
    {
        string[] terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return CreativePaletteOrdering.Order(blocks.Where(b =>
            (Category == "All" || (Category == "Favorites" ? favorites.Contains(b.Key) :
                CategoryOf(b).Equals(Category, StringComparison.OrdinalIgnoreCase))) &&
            terms.All(t => string.Join(' ', new[] { name(b), b.DisplayName, b.Key, b.Category }.Concat(b.Tags))
                .Contains(t, StringComparison.OrdinalIgnoreCase))), useSimpleGroups: true);
    }

    public void ResetScroll() { FirstRow = 0; _wheelRemainder = 0; }
    public static int MaximumFirstRow(int count, int columns, int rows) =>
        Math.Max(0, (count + Math.Max(1, columns) - 1) / Math.Max(1, columns) - Math.Max(1, rows));
    public void Scroll(float wheel, int maximum)
    {
        if (!float.IsFinite(wheel)) return;
        _wheelRemainder += wheel;
        int steps = (int)_wheelRemainder;
        _wheelRemainder -= steps;
        FirstRow = Math.Clamp(FirstRow - steps, 0, maximum);
        if (FirstRow == 0 && _wheelRemainder > 0 || FirstRow == maximum && _wheelRemainder < 0)
            _wheelRemainder = 0;
    }
}
