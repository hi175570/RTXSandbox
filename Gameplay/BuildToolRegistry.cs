using RTXSandbox.ModAPI;

namespace RTXSandbox.Gameplay;

public sealed class BuildToolRegistry : IBuildToolRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, BuildToolDefinition> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public event Action? DefinitionsChanged;

    public IReadOnlyList<BuildToolDefinition> Definitions
    {
        get
        {
            lock (_sync)
            {
                return _byKey.Values
                    .OrderBy(tool => tool.Category, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(tool => tool.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public BuildToolDefinition Register(BuildToolDefinition definition, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Key) || !definition.Key.Contains(':'))
        {
            throw new ArgumentException("Tool keys must use a namespaced form such as 'author:tool'.", nameof(definition));
        }

        lock (_sync)
        {
            if (_byKey.ContainsKey(definition.Key) && !force)
            {
                throw new InvalidOperationException($"A build tool with key '{definition.Key}' is already registered.");
            }
            _byKey[definition.Key] = definition;
        }

        DefinitionsChanged?.Invoke();
        return definition;
    }

    public bool Remove(string key)
    {
        bool removed;
        lock (_sync)
        {
            removed = _byKey.Remove(key);
        }
        if (removed)
        {
            DefinitionsChanged?.Invoke();
        }
        return removed;
    }

    public bool TryGet(string key, out BuildToolDefinition? definition)
    {
        lock (_sync)
        {
            return _byKey.TryGetValue(key, out definition);
        }
    }
}
