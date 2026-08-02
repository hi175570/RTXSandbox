using RTXSandbox.ModAPI;

namespace RTXSandbox.Gameplay;

public sealed class RecipeRegistry : IRecipeRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RecipeDefinition> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecipeDefinition> _definitions = new();

    public event Action? DefinitionsChanged;

    public IReadOnlyList<RecipeDefinition> Definitions
    {
        get
        {
            lock (_sync)
            {
                return _definitions.ToArray();
            }
        }
    }

    public RecipeDefinition Register(RecipeDefinition definition, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateKey(definition.Key);
        if (definition.Output.IsEmpty || definition.Ingredients.Count == 0)
        {
            throw new ArgumentException("Recipes require at least one ingredient and a non-empty output.", nameof(definition));
        }

        lock (_sync)
        {
            if (_byKey.TryGetValue(definition.Key, out RecipeDefinition? existing))
            {
                if (!force)
                {
                    throw new InvalidOperationException($"A recipe with key '{definition.Key}' is already registered.");
                }
                _definitions.Remove(existing);
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
            if (!_byKey.Remove(key, out RecipeDefinition? recipe))
            {
                return false;
            }
            removed = _definitions.Remove(recipe);
        }
        if (removed) DefinitionsChanged?.Invoke();
        return removed;
    }

    public bool TryGet(string key, out RecipeDefinition? definition)
    {
        lock (_sync)
        {
            return _byKey.TryGetValue(key, out definition);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.Contains(':'))
        {
            throw new ArgumentException("Recipe keys must use a namespaced form such as 'author:recipe'.", nameof(key));
        }
    }
}
