using RTXSandbox.ModAPI;

namespace RTXSandbox.Gameplay;

public sealed class PlayerInventory : IInventory
{
    private readonly ItemStack[] _slots;
    private readonly ItemRegistry _items;
    private int _selectedHotbarSlot;

    public event Action? Changed;

    public int SlotCount => _slots.Length;
    public int HotbarSize { get; }

    public int SelectedHotbarSlot
    {
        get => _selectedHotbarSlot;
        set
        {
            int selected = Math.Clamp(value, 0, HotbarSize - 1);
            if (_selectedHotbarSlot == selected)
            {
                return;
            }
            _selectedHotbarSlot = selected;
            Changed?.Invoke();
        }
    }

    public PlayerInventory(ItemRegistry items, int slotCount = 27, int hotbarSize = 9)
    {
        if (slotCount <= 0 || hotbarSize <= 0 || hotbarSize > slotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _slots = new ItemStack[slotCount];
        HotbarSize = hotbarSize;
    }

    public ItemStack GetSlot(int index)
    {
        ValidateSlot(index);
        return _slots[index];
    }

    public void SetSlot(int index, ItemStack stack)
    {
        ValidateSlot(index);
        if (stack.IsEmpty)
        {
            _slots[index] = ItemStack.Empty;
        }
        else
        {
            ItemDefinition item = RequireItem(stack.ItemKey!);
            _slots[index] = new ItemStack(item.Key, Math.Min(stack.Count, item.MaxStack), stack.Damage);
        }
        Changed?.Invoke();
    }

    public int Add(string itemKey, int count, int damage = 0)
    {
        if (count <= 0)
        {
            return 0;
        }
        ItemDefinition item = RequireItem(itemKey);
        int remaining = count;

        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            ItemStack stack = _slots[i];
            if (stack.IsEmpty || !string.Equals(stack.ItemKey, item.Key, StringComparison.OrdinalIgnoreCase) ||
                stack.Damage != damage || stack.Count >= item.MaxStack)
            {
                continue;
            }
            int added = Math.Min(item.MaxStack - stack.Count, remaining);
            _slots[i] = new ItemStack(item.Key, stack.Count + added, damage);
            remaining -= added;
        }

        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (!_slots[i].IsEmpty)
            {
                continue;
            }
            int added = Math.Min(item.MaxStack, remaining);
            _slots[i] = new ItemStack(item.Key, added, damage);
            remaining -= added;
        }

        if (remaining != count)
        {
            Changed?.Invoke();
        }
        return remaining;
    }

    public bool Remove(string itemKey, int count)
    {
        if (count <= 0)
        {
            return true;
        }
        if (Count(itemKey) < count)
        {
            return false;
        }

        int remaining = count;
        int selected = SelectedHotbarSlot;
        if (string.Equals(_slots[selected].ItemKey, itemKey, StringComparison.OrdinalIgnoreCase))
        {
            remaining -= TakeFromSlot(selected, remaining);
        }
        for (int i = 0; i < _slots.Length && remaining > 0; i++)
        {
            if (i != selected && string.Equals(_slots[i].ItemKey, itemKey, StringComparison.OrdinalIgnoreCase))
            {
                remaining -= TakeFromSlot(i, remaining);
            }
        }
        Changed?.Invoke();
        return true;
    }

    public int Count(string itemKey) => _slots
        .Where(stack => string.Equals(stack.ItemKey, itemKey, StringComparison.OrdinalIgnoreCase))
        .Sum(stack => stack.Count);

    public bool ConsumeSelected(int count = 1)
    {
        if (_slots[SelectedHotbarSlot].Count < count)
        {
            return false;
        }
        TakeFromSlot(SelectedHotbarSlot, count);
        Changed?.Invoke();
        return true;
    }

    public int CapacityFor(string itemKey, int damage = 0)
    {
        ItemDefinition item = RequireItem(itemKey);
        int capacity = 0;
        foreach (ItemStack stack in _slots)
        {
            if (stack.IsEmpty)
            {
                capacity += item.MaxStack;
            }
            else if (string.Equals(stack.ItemKey, item.Key, StringComparison.OrdinalIgnoreCase) && stack.Damage == damage)
            {
                capacity += Math.Max(0, item.MaxStack - stack.Count);
            }
        }
        return capacity;
    }

    public bool DamageSelectedTool(int amount, out string? brokenItemKey)
    {
        brokenItemKey = null;
        int slot = SelectedHotbarSlot;
        ItemStack stack = _slots[slot];
        if (stack.IsEmpty || !_items.TryGet(stack.ItemKey!, out ItemDefinition? item) || item!.MaxDurability <= 0)
        {
            return false;
        }

        int damage = stack.Damage + Math.Max(0, amount);
        if (damage >= item.MaxDurability)
        {
            brokenItemKey = item.Key;
            _slots[slot] = stack.Count > 1
                ? new ItemStack(item.Key, stack.Count - 1)
                : ItemStack.Empty;
        }
        else
        {
            _slots[slot] = new ItemStack(item.Key, stack.Count, damage);
        }
        Changed?.Invoke();
        return true;
    }

    public void Swap(int left, int right)
    {
        ValidateSlot(left);
        ValidateSlot(right);
        (_slots[left], _slots[right]) = (_slots[right], _slots[left]);
        Changed?.Invoke();
    }

    public ItemStack[] Snapshot() => _slots.ToArray();

    public void Restore(IEnumerable<ItemStack> stacks, int selectedHotbarSlot = 0)
    {
        Array.Fill(_slots, ItemStack.Empty);
        int index = 0;
        foreach (ItemStack stack in stacks.Take(_slots.Length))
        {
            if (!stack.IsEmpty && _items.TryGet(stack.ItemKey!, out ItemDefinition? definition))
            {
                _slots[index] = new ItemStack(
                    definition!.Key,
                    Math.Min(stack.Count, definition.MaxStack),
                    stack.Damage);
            }
            index++;
        }
        _selectedHotbarSlot = Math.Clamp(selectedHotbarSlot, 0, HotbarSize - 1);
        Changed?.Invoke();
    }

    private int TakeFromSlot(int index, int count)
    {
        ItemStack stack = _slots[index];
        int removed = Math.Min(stack.Count, count);
        int left = stack.Count - removed;
        _slots[index] = left > 0
            ? new ItemStack(stack.ItemKey, left, stack.Damage)
            : ItemStack.Empty;
        return removed;
    }

    private ItemDefinition RequireItem(string key) =>
        _items.TryGet(key, out ItemDefinition? item)
            ? item!
            : throw new KeyNotFoundException($"Unknown item '{key}'.");

    private void ValidateSlot(int index)
    {
        if ((uint)index >= (uint)_slots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
