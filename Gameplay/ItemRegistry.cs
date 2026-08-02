using System.Numerics;
using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

public sealed class ItemRegistry : IItemRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ItemDefinition> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ItemDefinition> _definitions = new();
    private readonly Dictionary<string, ItemDefinition> _generatedBlockItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockRegistry _blocks;

    public event Action? DefinitionsChanged;

    public IReadOnlyList<ItemDefinition> Definitions
    {
        get
        {
            lock (_sync)
            {
                return _definitions.ToArray();
            }
        }
    }

    public ItemRegistry(BlockRegistry blocks)
    {
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        SynchronizeBlockItems();
        blocks.DefinitionsChanged += SynchronizeBlockItems;
    }

    public ItemDefinition Register(ItemDefinition definition, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateKey(definition.Key);

        lock (_sync)
        {
            if (_byKey.TryGetValue(definition.Key, out ItemDefinition? existing))
            {
                if (!force)
                {
                    throw new InvalidOperationException($"An item with key '{definition.Key}' is already registered.");
                }
                _definitions.Remove(existing);
                _generatedBlockItems.Remove(definition.Key);
            }

            _byKey[definition.Key] = definition;
            _definitions.Add(definition);
            _definitions.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
        }

        DefinitionsChanged?.Invoke();
        return definition;
    }

    public bool Remove(string key)
    {
        bool removed;
        lock (_sync)
        {
            if (!_byKey.Remove(key, out ItemDefinition? definition))
            {
                return false;
            }
            _generatedBlockItems.Remove(key);
            removed = _definitions.Remove(definition);
        }

        if (removed)
        {
            DefinitionsChanged?.Invoke();
        }
        return removed;
    }

    public bool TryGet(string key, out ItemDefinition? definition)
    {
        lock (_sync)
        {
            return _byKey.TryGetValue(key, out definition);
        }
    }

    private void SynchronizeBlockItems()
    {
        IReadOnlyList<BlockDefinition> blocks = _blocks.Definitions;
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        lock (_sync)
        {
            foreach (BlockDefinition block in blocks.Where(block => block.IsPlaceable && block.Id != 0))
            {
                activeKeys.Add(block.Key);
                Vector3 tint = Vector3.Clamp(block.Albedo, Vector3.Zero, Vector3.One);
                if (_generatedBlockItems.TryGetValue(block.Key, out ItemDefinition? generated) &&
                    _byKey.TryGetValue(block.Key, out ItemDefinition? current) &&
                    ReferenceEquals(generated, current))
                {
                    generated.DisplayName = block.DisplayName;
                    generated.PlaceBlockKey = block.Key;
                    generated.IconTint = tint;
                    changed = true;
                    continue;
                }

                if (_byKey.ContainsKey(block.Key))
                {
                    continue;
                }

                var item = new ItemDefinition(
                    block.Key,
                    block.DisplayName,
                    maxStack: 64,
                    placeBlockKey: block.Key,
                    iconTint: tint);
                _byKey.Add(item.Key, item);
                _definitions.Add(item);
                _generatedBlockItems[item.Key] = item;
                changed = true;
            }

            foreach (string staleKey in _generatedBlockItems.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
            {
                ItemDefinition stale = _generatedBlockItems[staleKey];
                if (_byKey.TryGetValue(staleKey, out ItemDefinition? current) && ReferenceEquals(stale, current))
                {
                    _byKey.Remove(staleKey);
                    _definitions.Remove(stale);
                    changed = true;
                }
                _generatedBlockItems.Remove(staleKey);
            }

            _definitions.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));
        }

        if (changed)
        {
            DefinitionsChanged?.Invoke();
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.Contains(':'))
        {
            throw new ArgumentException("Item keys must use a namespaced form such as 'author:item'.", nameof(key));
        }
    }
}
