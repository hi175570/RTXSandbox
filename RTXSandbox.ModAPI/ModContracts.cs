using System.ComponentModel;
using System.Numerics;

namespace RTXSandbox.ModAPI;

public enum BlockSurfacePattern : byte
{
    None,
    Grass,
    Stone,
    Sand,
    Wood,
    Leaves,
    Brick,
    CopperPatina,
    Obsidian,
    Marble,
    Ice,
    Tiles,
    Panels,
    Circuit,
    Grid,
    Rainbow,
    Hologram,
    Cloud,
    Lava,
    Checker,
    Fabric,
}

public sealed class BlockDefinition
{
    public ushort Id { get; set; }
    public string Key { get; }
    public string DisplayName { get; set; }
    public Vector3 Albedo { get; set; }
    public float Roughness { get; set; }
    public float Metallic { get; set; }
    public Vector3 Emission { get; set; }
    public bool IsCollidable { get; set; }
    public bool IsFluid { get; set; }
    public bool IsTransparent { get; set; }
    public float IndexOfRefraction { get; set; }
    public float Transmission { get; set; }
    public bool IsReplaceable { get; set; }
    public bool IsPlaceable { get; set; }
    public BlockSurfacePattern SurfacePattern { get; set; }
    public float HardnessSeconds { get; set; }
    public bool IsUnbreakable { get; set; }
    public string? DropItemKey { get; set; }
    public string? PreferredToolType { get; set; }
    public int RequiredToolTier { get; set; }
    public bool AffectedByGravity { get; set; }
    public string Category { get; set; }
    public IReadOnlyList<string> Tags { get; set; }
    public string SoundSet { get; set; }
    public string ParticleEffect { get; set; }
    public float AnimationSpeed { get; set; }
    public string TextureGenerator { get; set; }
    public float TextureVariation { get; set; }
    public int TextureSeed { get; set; }

    public BlockDefinition(
        string key,
        string displayName,
        Vector3 albedo,
        float roughness = 0.8f,
        float metallic = 0f,
        Vector3 emission = default,
        bool isCollidable = true,
        bool isFluid = false,
        bool isReplaceable = false,
        bool isPlaceable = true,
        ushort id = 0,
        BlockSurfacePattern surfacePattern = BlockSurfacePattern.None,
        float hardnessSeconds = 0.75f,
        bool isUnbreakable = false,
        string? dropItemKey = null,
        string? preferredToolType = null,
        int requiredToolTier = 0,
        bool affectedByGravity = false,
        bool isTransparent = false,
        float indexOfRefraction = 1.5f,
        float transmission = 0f,
        string category = "General",
        IEnumerable<string>? tags = null,
        string soundSet = "stone",
        string particleEffect = "dust",
        float animationSpeed = 0f,
        string textureGenerator = "auto",
        float textureVariation = -1f,
        int textureSeed = 0)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Albedo = Vector3.Clamp(albedo, Vector3.Zero, new Vector3(8f));
        Roughness = Math.Clamp(roughness, 0.001f, 1f);
        Metallic = Math.Clamp(metallic, 0f, 1f);
        Emission = Vector3.Max(emission, Vector3.Zero);
        IsCollidable = isCollidable;
        IsFluid = isFluid;
        IsTransparent = isTransparent;
        IndexOfRefraction = Math.Clamp(indexOfRefraction, 1f, 2.55f);
        Transmission = isTransparent ? Math.Clamp(transmission <= 0f ? 1f : transmission, 0f, 1f) : 0f;
        IsReplaceable = isReplaceable;
        IsPlaceable = isPlaceable;
        SurfacePattern = surfacePattern;
        HardnessSeconds = Math.Max(0.05f, hardnessSeconds);
        IsUnbreakable = isUnbreakable;
        DropItemKey = dropItemKey;
        PreferredToolType = preferredToolType;
        RequiredToolTier = Math.Max(0, requiredToolTier);
        AffectedByGravity = affectedByGravity;
        Category = string.IsNullOrWhiteSpace(category) ? "General" : category;
        Tags = tags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
               ?? Array.Empty<string>();
        SoundSet = string.IsNullOrWhiteSpace(soundSet) ? "stone" : soundSet;
        ParticleEffect = string.IsNullOrWhiteSpace(particleEffect) ? "dust" : particleEffect;
        AnimationSpeed = Math.Max(0f, animationSpeed);
        TextureGenerator = string.IsNullOrWhiteSpace(textureGenerator) ? "auto" : textureGenerator.Trim();
        TextureVariation = textureVariation < 0f ? -1f : Math.Clamp(textureVariation, 0f, 1f);
        TextureSeed = textureSeed;
        Id = id;
    }
}

public sealed class ItemDefinition
{
    public string Key { get; }
    public string DisplayName { get; set; }
    public int MaxStack { get; set; }
    public int MaxDurability { get; set; }
    public string? PlaceBlockKey { get; set; }
    public Vector3 IconTint { get; set; }
    public string? ToolType { get; set; }
    public int ToolTier { get; set; }
    public float MiningSpeedMultiplier { get; set; }
    public float FoodValue { get; set; }
    public float HealValue { get; set; }

    public ItemDefinition(
        string key,
        string displayName,
        int maxStack = 64,
        int maxDurability = 0,
        string? placeBlockKey = null,
        Vector3 iconTint = default,
        string? toolType = null,
        int toolTier = 0,
        float miningSpeedMultiplier = 1f,
        float foodValue = 0f,
        float healValue = 0f)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        MaxStack = Math.Clamp(maxStack, 1, 9999);
        MaxDurability = Math.Max(0, maxDurability);
        PlaceBlockKey = placeBlockKey;
        IconTint = Vector3.Clamp(iconTint, Vector3.Zero, Vector3.One);
        ToolType = toolType;
        ToolTier = Math.Max(0, toolTier);
        MiningSpeedMultiplier = Math.Max(0.1f, miningSpeedMultiplier);
        FoodValue = Math.Max(0f, foodValue);
        HealValue = Math.Max(0f, healValue);
    }
}

public readonly struct ItemStack
{
    public string? ItemKey { get; }
    public int Count { get; }
    public int Damage { get; }
    public bool IsEmpty => string.IsNullOrWhiteSpace(ItemKey) || Count <= 0;

    public ItemStack(string? itemKey, int count, int damage = 0)
    {
        ItemKey = itemKey;
        Count = Math.Max(0, count);
        Damage = Math.Max(0, damage);
    }

    public static ItemStack Empty => default;
}

public interface IItemRegistry
{
    IReadOnlyList<ItemDefinition> Definitions { get; }
    ItemDefinition Register(ItemDefinition definition, bool force = false);
    bool Remove(string key);
    bool TryGet(string key, out ItemDefinition? definition);
}

public sealed class RecipeIngredient
{
    public string ItemKey { get; }
    public int Count { get; }

    public RecipeIngredient(string itemKey, int count)
    {
        ItemKey = itemKey ?? throw new ArgumentNullException(nameof(itemKey));
        Count = Math.Max(1, count);
    }
}

public sealed class RecipeDefinition
{
    public string Key { get; }
    public string DisplayName { get; set; }
    public IReadOnlyList<RecipeIngredient> Ingredients { get; set; }
    public ItemStack Output { get; set; }
    public bool RequiresWorkbench { get; set; }

    public RecipeDefinition(
        string key,
        string displayName,
        IEnumerable<RecipeIngredient> ingredients,
        ItemStack output,
        bool requiresWorkbench = false)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Ingredients = ingredients?.ToArray() ?? throw new ArgumentNullException(nameof(ingredients));
        Output = output;
        RequiresWorkbench = requiresWorkbench;
    }
}

public interface IRecipeRegistry
{
    IReadOnlyList<RecipeDefinition> Definitions { get; }
    RecipeDefinition Register(RecipeDefinition definition, bool force = false);
    bool Remove(string key);
    bool TryGet(string key, out RecipeDefinition? definition);
}

public interface IInventory
{
    int SlotCount { get; }
    int HotbarSize { get; }
    int SelectedHotbarSlot { get; set; }
    ItemStack GetSlot(int index);
    void SetSlot(int index, ItemStack stack);
    int Add(string itemKey, int count, int damage = 0);
    bool Remove(string itemKey, int count);
    int Count(string itemKey);
}

public sealed class BuildToolUseContext
{
    public IVoxelWorld World { get; }
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public int NormalX { get; }
    public int NormalY { get; }
    public int NormalZ { get; }
    public ushort SelectedBlockId { get; }
    public int BrushRadius { get; }
    public bool Alternate { get; }

    public BuildToolUseContext(
        IVoxelWorld world,
        int x,
        int y,
        int z,
        int normalX,
        int normalY,
        int normalZ,
        ushort selectedBlockId,
        int brushRadius,
        bool alternate = false)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        X = x;
        Y = y;
        Z = z;
        NormalX = normalX;
        NormalY = normalY;
        NormalZ = normalZ;
        SelectedBlockId = selectedBlockId;
        BrushRadius = Math.Clamp(brushRadius, 1, 16);
        Alternate = alternate;
    }
}

public sealed class BuildToolDefinition
{
    public string Key { get; }
    public string DisplayName { get; set; }
    public string Category { get; set; }
    public string Description { get; set; }
    public Action<BuildToolUseContext> Use { get; set; }

    public BuildToolDefinition(
        string key,
        string displayName,
        Action<BuildToolUseContext> use,
        string category = "Mod Tools",
        string description = "")
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Use = use ?? throw new ArgumentNullException(nameof(use));
        Category = category;
        Description = description;
    }
}

public interface IBuildToolRegistry
{
    IReadOnlyList<BuildToolDefinition> Definitions { get; }
    BuildToolDefinition Register(BuildToolDefinition definition, bool force = false);
    bool Remove(string key);
    bool TryGet(string key, out BuildToolDefinition? definition);
}

public interface IGameEvents
{
    event Action<IVoxelWorld>? WorldReady;
    event Action<float>? Updating;
    event Action<VoxelChangedEvent>? VoxelChanged;
}

public interface IGameEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler, int priority = 0) where TEvent : class;
    TEvent Publish<TEvent>(TEvent gameEvent) where TEvent : class;
}

public abstract class CancellableGameEvent
{
    public bool IsCancelled { get; set; }
}

public sealed class WorldLoadedEvent
{
    public IVoxelWorld World { get; }

    public WorldLoadedEvent(IVoxelWorld world) => World = world;
}

public sealed class WorldTickEvent
{
    public IVoxelWorld World { get; }
    public float DeltaSeconds { get; }

    public WorldTickEvent(IVoxelWorld world, float deltaSeconds)
    {
        World = world;
        DeltaSeconds = deltaSeconds;
    }
}

public sealed class BlockBreakEvent : CancellableGameEvent
{
    public IVoxelWorld World { get; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public ushort BlockId { get; }

    public BlockBreakEvent(IVoxelWorld world, int x, int y, int z, ushort blockId)
    {
        World = world;
        X = x;
        Y = y;
        Z = z;
        BlockId = blockId;
    }
}

public sealed class BlockPlaceEvent : CancellableGameEvent
{
    public IVoxelWorld World { get; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public ushort ExistingBlockId { get; }
    public ushort NewBlockId { get; set; }

    public BlockPlaceEvent(IVoxelWorld world, int x, int y, int z, ushort existingBlockId, ushort newBlockId)
    {
        World = world;
        X = x;
        Y = y;
        Z = z;
        ExistingBlockId = existingBlockId;
        NewBlockId = newBlockId;
    }
}

public sealed class BlockBrokenEvent
{
    public IVoxelWorld World { get; }
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public ushort BlockId { get; }
    public string? DropItemKey { get; set; }
    public int DropCount { get; set; } = 1;

    public BlockBrokenEvent(IVoxelWorld world, int x, int y, int z, ushort blockId, string? dropItemKey)
    {
        World = world;
        X = x;
        Y = y;
        Z = z;
        BlockId = blockId;
        DropItemKey = dropItemKey;
    }
}

public enum PlayerDamageCause
{
    Generic,
    Fall,
    Drowning,
    Starvation,
    Void,
    Mod,
}

public sealed class PlayerDamageEvent : CancellableGameEvent
{
    public float Amount { get; set; }
    public PlayerDamageCause Cause { get; }

    public PlayerDamageEvent(float amount, PlayerDamageCause cause)
    {
        Amount = Math.Max(0f, amount);
        Cause = cause;
    }
}

public sealed class PlayerRespawnEvent
{
    public Vector3 Position { get; set; }
    public int DeathCount { get; }

    public PlayerRespawnEvent(Vector3 position, int deathCount)
    {
        Position = position;
        DeathCount = deathCount;
    }
}

public sealed class ItemPickupEvent : CancellableGameEvent
{
    public string ItemKey { get; }
    public int Count { get; set; }

    public ItemPickupEvent(string itemKey, int count)
    {
        ItemKey = itemKey;
        Count = Math.Max(0, count);
    }
}

public sealed class CraftItemEvent : CancellableGameEvent
{
    public RecipeDefinition Recipe { get; }
    public int Count { get; set; }

    public CraftItemEvent(RecipeDefinition recipe, int count = 1)
    {
        Recipe = recipe;
        Count = Math.Max(1, count);
    }
}

public sealed class ItemCraftedEvent
{
    public RecipeDefinition Recipe { get; }
    public ItemStack Output { get; }

    public ItemCraftedEvent(RecipeDefinition recipe, ItemStack output)
    {
        Recipe = recipe;
        Output = output;
    }
}

public sealed class ObjectiveCompletedEvent
{
    public string ObjectiveId { get; }
    public string Title { get; }

    public ObjectiveCompletedEvent(string objectiveId, string title)
    {
        ObjectiveId = objectiveId;
        Title = title;
    }
}

public sealed class PlayerMoveEvent : CancellableGameEvent
{
    public Vector3 OldPosition { get; }
    public Vector3 NewPosition { get; set; }

    public PlayerMoveEvent(Vector3 oldPosition, Vector3 newPosition)
    {
        OldPosition = oldPosition;
        NewPosition = newPosition;
    }
}

public readonly struct VoxelChangedEvent
{
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public ushort OldBlockId { get; }
    public ushort NewBlockId { get; }

    public VoxelChangedEvent(int x, int y, int z, ushort oldBlockId, ushort newBlockId)
    {
        X = x;
        Y = y;
        Z = z;
        OldBlockId = oldBlockId;
        NewBlockId = newBlockId;
    }
}

public interface IModLogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}

public interface IModContext
{
    IBlockRegistry Blocks { get; }
    IItemRegistry Items { get; }
    IRecipeRegistry Recipes { get; }
    IBuildToolRegistry Tools { get; }
    IGameEvents Events { get; }
    IGameEventBus EventBus { get; }
    IVoxelWorld World { get; }
    IModLogger Log { get; }
    object Game { get; }
    IServiceProvider Services { get; }
    string GameVersion { get; }

    T? GetService<T>() where T : class;
}

public static class SandboxApi
{
    private static readonly object Sync = new();
    private static readonly List<Action<IModContext>> Pending = new();
    private static IModContext? _current;

    public static IModContext? Current
    {
        get
        {
            lock (Sync)
            {
                return _current;
            }
        }
    }

    public static void WhenReady(Action<IModContext> initialize)
    {
        if (initialize is null)
        {
            throw new ArgumentNullException(nameof(initialize));
        }
        IModContext? context;
        lock (Sync)
        {
            context = _current;
            if (context is null)
            {
                Pending.Add(initialize);
                return;
            }
        }
        initialize(context);
    }

    public static T? GetService<T>() where T : class => Current?.GetService<T>();

    public static T RequireService<T>() where T : class =>
        GetService<T>() ?? throw new InvalidOperationException(
            $"The game service '{typeof(T).FullName}' is not available yet. Request it from a WorldReady handler.");

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void AttachHost(IModContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }
        Action<IModContext>[] initializers;
        lock (Sync)
        {
            if (_current is not null)
            {
                throw new InvalidOperationException("The RTX Sandbox mod host is already attached.");
            }
            _current = context;
            initializers = Pending.ToArray();
            Pending.Clear();
        }

        foreach (Action<IModContext> initialize in initializers)
        {
            try
            {
                initialize(context);
            }
            catch (Exception exception)
            {
                context.Log.Error("A queued mod initializer failed.", exception);
            }
        }
    }
}
