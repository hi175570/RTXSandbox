using System.Numerics;
using RTXSandbox.ModAPI;

namespace RTXSandbox.World;

public sealed class BlockRegistry : IBlockRegistry
{
    public const int MaterialCapacity = 4096;
    public const ushort UnknownBlockId = MaterialCapacity - 1;
    private readonly object _sync = new();
    private readonly BlockDefinition?[] _byId = new BlockDefinition[MaterialCapacity];
    private readonly Dictionary<string, BlockDefinition> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BlockDefinition> _definitions = new();
    private readonly BlockDefinition _unknown = new(
        "rtxsandbox:unknown",
        "Unknown Block",
        new Vector3(1f, 0f, 1f),
        roughness: 0.5f,
        id: UnknownBlockId);

    public event Action? DefinitionsChanged;
    public IReadOnlyList<LoadedContentPack> LoadedContentPacks { get; }

    public IReadOnlyList<BlockDefinition> Definitions
    {
        get
        {
            lock (_sync)
            {
                return _definitions.ToArray();
            }
        }
    }

    public BlockRegistry(string? baseContentPath = null, string? contentPacksPath = null)
    {
        LoadedContentPacks = BlockContentLoader.LoadInto(this, baseContentPath, contentPacksPath);
    }

    public ushort Register(BlockDefinition definition, bool force = false)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }
        if (string.IsNullOrWhiteSpace(definition.Key) || !definition.Key.Contains(':'))
        {
            throw new ArgumentException("Block keys must use a namespaced form such as 'author:block'.", nameof(definition));
        }
        if (definition.DropItemKey is null && definition.IsPlaceable && !definition.IsFluid)
        {
            definition.DropItemKey = definition.Key;
        }

        ushort id;
        lock (_sync)
        {
            _byKey.TryGetValue(definition.Key, out BlockDefinition? existingByKey);
            if (existingByKey is not null && !force)
            {
                throw new InvalidOperationException($"A block with key '{definition.Key}' is already registered.");
            }

            id = definition.Id;
            if (id == 0 && !definition.Key.Equals("rtxsandbox:air", StringComparison.OrdinalIgnoreCase))
            {
                id = force && existingByKey is not null ? existingByKey.Id : FindFreeId();
                definition.Id = id;
            }

            BlockDefinition? existingById = _byId[id];
            if (existingById is not null && !ReferenceEquals(existingById, existingByKey) && !force)
            {
                throw new InvalidOperationException($"Block id {id} is already used by '{existingById.Key}'.");
            }

            if (force)
            {
                if (existingByKey is not null)
                {
                    RemoveInternal(existingByKey);
                }
                if (existingById is not null && !ReferenceEquals(existingById, existingByKey))
                {
                    RemoveInternal(existingById);
                }
            }

            _byId[id] = definition;
            _byKey[definition.Key] = definition;
            _definitions.Add(definition);
            _definitions.Sort((left, right) => left.Id.CompareTo(right.Id));
        }

        DefinitionsChanged?.Invoke();
        return id;
    }

    public bool Remove(string key)
    {
        bool removed;
        lock (_sync)
        {
            if (!_byKey.TryGetValue(key, out BlockDefinition? definition))
            {
                return false;
            }
            removed = RemoveInternal(definition);
        }

        if (removed)
        {
            DefinitionsChanged?.Invoke();
        }
        return removed;
    }

    public void NotifyChanged() => DefinitionsChanged?.Invoke();

    public BlockDefinition Get(ushort id)
    {
        lock (_sync)
        {
            return _byId[id] ?? _unknown;
        }
    }

    public bool TryGet(string key, out BlockDefinition? definition)
    {
        lock (_sync)
        {
            return _byKey.TryGetValue(key, out definition);
        }
    }

    public IReadOnlyList<BlockDefinition> PlaceableDefinitions()
    {
        lock (_sync)
        {
            return _definitions.Where(definition => definition.IsPlaceable && definition.Id != 0).ToArray();
        }
    }

    private ushort FindFreeId()
    {
        for (ushort id = 1; id < UnknownBlockId; id++)
        {
            if (_byId[id] is null)
            {
                return id;
            }
        }
        throw new InvalidOperationException($"The 16-bit material table reserved for the renderer is full (maximum {UnknownBlockId - 1} registered blocks).");
    }

    private bool RemoveInternal(BlockDefinition definition)
    {
        _byKey.Remove(definition.Key);
        if (ReferenceEquals(_byId[definition.Id], definition))
        {
            _byId[definition.Id] = null;
        }
        return _definitions.Remove(definition);
    }

}
