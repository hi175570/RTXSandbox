using System.Numerics;
using System.Text.Json;
using RTXSandbox.ModAPI;
using RTXSandbox.Infrastructure;

namespace RTXSandbox.Gameplay;

public static class ItemRecipeContentLoader
{
    public static string BaseItemsPath => Path.Combine(RuntimePaths.OutputRoot, "Content", "Base", "base.items.json");
    public static string BaseRecipesPath => Path.Combine(RuntimePaths.OutputRoot, "Content", "Base", "base.recipes.json");

    public static void LoadInto(ItemRegistry items, RecipeRegistry recipes, string? contentPacksPath = null)
    {
        RegisterItems(items, Read<ItemDocument>(BaseItemsPath), force: false);
        RegisterRecipes(recipes, Read<RecipeDocument>(BaseRecipesPath), force: false);

        contentPacksPath ??= Path.Combine(RuntimePaths.OutputRoot, "ContentPacks");
        if (!Directory.Exists(contentPacksPath))
        {
            return;
        }

        foreach ((PackManifest manifest, string directory) in Directory.GetDirectories(contentPacksPath)
                     .Select(directory => (Manifest: ReadManifest(directory), Directory: directory))
                     .Where(candidate => candidate.Manifest is not null && candidate.Manifest.Enabled)
                     .Select(candidate => (candidate.Manifest!, candidate.Directory))
                     .OrderBy(candidate => candidate.Item1.Priority)
                     .ThenBy(candidate => candidate.Item1.Id, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (string file in EnumerateJson(directory, "items"))
                {
                    RegisterItems(items, Read<ItemDocument>(file), manifest.ForceOverrides);
                }
                foreach (string file in EnumerateJson(directory, "recipes"))
                {
                    RegisterRecipes(recipes, Read<RecipeDocument>(file), manifest.ForceOverrides);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Could not load item/recipe content from '{manifest.Id}': {exception.Message}");
            }
        }
    }

    private static void RegisterItems(ItemRegistry registry, ItemDocument document, bool force)
    {
        ValidateFormat(document.FormatVersion, "item");
        foreach (ItemContent item in document.Items)
        {
            Vector3 tint = item.IconTint.Length == 3
                ? new Vector3(item.IconTint[0], item.IconTint[1], item.IconTint[2])
                : Vector3.One;
            registry.Register(new ItemDefinition(
                item.Key,
                item.DisplayName,
                item.MaxStack,
                item.MaxDurability,
                item.PlaceBlockKey,
                tint,
                item.ToolType,
                item.ToolTier,
                item.MiningSpeedMultiplier,
                item.FoodValue,
                item.HealValue),
                force || item.Force);
        }
    }

    private static void RegisterRecipes(RecipeRegistry registry, RecipeDocument document, bool force)
    {
        ValidateFormat(document.FormatVersion, "recipe");
        foreach (RecipeContent recipe in document.Recipes)
        {
            registry.Register(new RecipeDefinition(
                recipe.Key,
                recipe.DisplayName,
                recipe.Ingredients.Select(ingredient => new RecipeIngredient(ingredient.ItemKey, ingredient.Count)),
                new ItemStack(recipe.Output.ItemKey, recipe.Output.Count, recipe.Output.Damage),
                recipe.RequiresWorkbench),
                force || recipe.Force);
        }
    }

    private static T Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required gameplay content was not found: {path}", path);
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions())
               ?? throw new InvalidDataException($"Gameplay content '{path}' is empty.");
    }

    private static PackManifest? ReadManifest(string directory)
    {
        string path = Path.Combine(directory, "pack.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path), JsonOptions());
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not read content-pack manifest '{path}': {exception.Message}");
            return null;
        }
    }

    private static IEnumerable<string> EnumerateJson(string root, string folder)
    {
        string directory = Path.Combine(root, folder);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();
    }

    private static void ValidateFormat(int version, string kind)
    {
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported {kind} content format {version}; expected 1.");
        }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class ItemDocument
    {
        public int FormatVersion { get; set; } = 1;
        public List<ItemContent> Items { get; set; } = new();
    }

    private sealed class ItemContent
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int MaxStack { get; set; } = 64;
        public int MaxDurability { get; set; }
        public string? PlaceBlockKey { get; set; }
        public float[] IconTint { get; set; } = new float[3];
        public string? ToolType { get; set; }
        public int ToolTier { get; set; }
        public float MiningSpeedMultiplier { get; set; } = 1f;
        public float FoodValue { get; set; }
        public float HealValue { get; set; }
        public bool Force { get; set; }
    }

    private sealed class RecipeDocument
    {
        public int FormatVersion { get; set; } = 1;
        public List<RecipeContent> Recipes { get; set; } = new();
    }

    private sealed class RecipeContent
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<IngredientContent> Ingredients { get; set; } = new();
        public OutputContent Output { get; set; } = new();
        public bool RequiresWorkbench { get; set; }
        public bool Force { get; set; }
    }

    private sealed class IngredientContent
    {
        public string ItemKey { get; set; } = string.Empty;
        public int Count { get; set; } = 1;
    }

    private sealed class OutputContent
    {
        public string ItemKey { get; set; } = string.Empty;
        public int Count { get; set; } = 1;
        public int Damage { get; set; }
    }

    private sealed class PackManifest
    {
        public string Id { get; set; } = "unnamed.pack";
        public int Priority { get; set; }
        public bool Enabled { get; set; } = true;
        public bool ForceOverrides { get; set; }
    }
}
