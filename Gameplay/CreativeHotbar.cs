using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

/// <summary>
/// An unlimited creative quickbar. It stores namespaced keys instead of runtime IDs so
/// content packs and mods can be reordered without corrupting a player's layout.
/// </summary>
public sealed class CreativeHotbar
{
    public const int SlotCount = 9;

    private readonly BlockRegistry _blocks;
    private readonly string[] _keys = new string[SlotCount];
    private int _selectedSlot;

    public event Action? Changed;

    public int SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            int selected = Math.Clamp(value, 0, SlotCount - 1);
            if (_selectedSlot == selected) return;
            _selectedSlot = selected;
            Changed?.Invoke();
        }
    }

    public BlockDefinition SelectedBlock => GetBlock(_selectedSlot);

    public CreativeHotbar(
        BlockRegistry blocks,
        IEnumerable<string>? savedKeys = null,
        int selectedSlot = 0,
        string? legacySelectedKey = null)
    {
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        IReadOnlyList<BlockDefinition> palette = blocks.PlaceableDefinitions();
        string[] candidates = savedKeys?.Take(SlotCount).ToArray() ?? Array.Empty<string>();
        for (int index = 0; index < SlotCount; index++)
        {
            string? candidate = index < candidates.Length ? candidates[index] : null;
            _keys[index] = IsPlaceable(candidate)
                ? candidate!
                : palette.Count > 0 ? palette[index % palette.Count].Key : "rtxsandbox:unknown";
        }

        _selectedSlot = Math.Clamp(selectedSlot, 0, SlotCount - 1);
        if (IsPlaceable(legacySelectedKey) && (savedKeys is null || !savedKeys.Any()))
        {
            _keys[_selectedSlot] = legacySelectedKey!;
        }
    }

    public string GetKey(int slot)
    {
        ValidateSlot(slot);
        return _keys[slot];
    }

    public BlockDefinition GetBlock(int slot)
    {
        ValidateSlot(slot);
        return _blocks.TryGet(_keys[slot], out BlockDefinition? definition) && definition!.IsPlaceable
            ? definition
            : _blocks.Get(BlockRegistry.UnknownBlockId);
    }

    public bool Assign(int slot, ushort blockId)
    {
        ValidateSlot(slot);
        BlockDefinition definition = _blocks.Get(blockId);
        if (!definition.IsPlaceable || definition.Id == BlockRegistry.UnknownBlockId) return false;
        if (string.Equals(_keys[slot], definition.Key, StringComparison.OrdinalIgnoreCase)) return false;
        _keys[slot] = definition.Key;
        Changed?.Invoke();
        return true;
    }

    public bool Select(int slot)
    {
        ValidateSlot(slot);
        if (_selectedSlot == slot) return false;
        _selectedSlot = slot;
        Changed?.Invoke();
        return true;
    }

    public bool SelectAndAssign(ushort blockId)
    {
        bool changed = Assign(_selectedSlot, blockId);
        BlockDefinition definition = _blocks.Get(blockId);
        return changed || definition.Id == blockId;
    }

    public bool Cycle(int direction)
    {
        if (direction == 0) return false;
        int next = (_selectedSlot + Math.Sign(direction) + SlotCount) % SlotCount;
        return Select(next);
    }

    public string[] Snapshot() => _keys.ToArray();

    private bool IsPlaceable(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        _blocks.TryGet(key, out BlockDefinition? definition) &&
        definition!.IsPlaceable;

    private static void ValidateSlot(int slot)
    {
        if ((uint)slot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
    }
}
