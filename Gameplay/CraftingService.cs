using RTXSandbox.ModAPI;

namespace RTXSandbox.Gameplay;

public sealed class CraftingService
{
    private readonly RecipeRegistry _recipes;
    private readonly ItemRegistry _items;
    private readonly PlayerInventory _inventory;
    private readonly Func<CraftItemEvent, CraftItemEvent> _publishBefore;
    private readonly Func<ItemCraftedEvent, ItemCraftedEvent> _publishAfter;

    public event Action<RecipeDefinition, ItemStack>? Crafted;

    public CraftingService(
        RecipeRegistry recipes,
        ItemRegistry items,
        PlayerInventory inventory,
        Func<CraftItemEvent, CraftItemEvent> publishBefore,
        Func<ItemCraftedEvent, ItemCraftedEvent> publishAfter)
    {
        _recipes = recipes;
        _items = items;
        _inventory = inventory;
        _publishBefore = publishBefore;
        _publishAfter = publishAfter;
    }

    public bool CanCraft(string recipeKey, bool hasWorkbench) =>
        _recipes.TryGet(recipeKey, out RecipeDefinition? recipe) && MaxCraftable(recipe!, hasWorkbench) > 0;

    public int MaxCraftable(RecipeDefinition recipe, bool hasWorkbench)
    {
        if (recipe.RequiresWorkbench && !hasWorkbench)
        {
            return 0;
        }
        if (recipe.Output.IsEmpty || !_items.TryGet(recipe.Output.ItemKey!, out _))
        {
            return 0;
        }

        int byIngredients = int.MaxValue;
        foreach (IGrouping<string, RecipeIngredient> group in recipe.Ingredients
                     .GroupBy(ingredient => ingredient.ItemKey, StringComparer.OrdinalIgnoreCase))
        {
            int required = group.Sum(ingredient => ingredient.Count);
            byIngredients = Math.Min(byIngredients, _inventory.Count(group.Key) / required);
        }
        int byCapacity = _inventory.CapacityFor(recipe.Output.ItemKey!, recipe.Output.Damage) /
                         Math.Max(1, recipe.Output.Count);
        return Math.Max(0, Math.Min(byIngredients, byCapacity));
    }

    public int Craft(string recipeKey, bool hasWorkbench, int requestedCount = 1)
    {
        if (!_recipes.TryGet(recipeKey, out RecipeDefinition? recipe))
        {
            return 0;
        }
        CraftItemEvent crafting = _publishBefore(new CraftItemEvent(recipe!, requestedCount));
        if (crafting.IsCancelled)
        {
            return 0;
        }

        int count = Math.Min(Math.Max(1, crafting.Count), MaxCraftable(recipe!, hasWorkbench));
        if (count <= 0)
        {
            return 0;
        }

        foreach (IGrouping<string, RecipeIngredient> group in recipe!.Ingredients
                     .GroupBy(ingredient => ingredient.ItemKey, StringComparer.OrdinalIgnoreCase))
        {
            _inventory.Remove(group.Key, group.Sum(ingredient => ingredient.Count) * count);
        }

        int outputCount = recipe.Output.Count * count;
        int remaining = _inventory.Add(recipe.Output.ItemKey!, outputCount, recipe.Output.Damage);
        if (remaining != 0)
        {
            throw new InvalidOperationException("Inventory capacity changed during an atomic craft operation.");
        }

        var output = new ItemStack(recipe.Output.ItemKey, outputCount, recipe.Output.Damage);
        _publishAfter(new ItemCraftedEvent(recipe, output));
        Crafted?.Invoke(recipe, output);
        return count;
    }
}
