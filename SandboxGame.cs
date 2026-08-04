using System.Diagnostics;
using System.Text;
using BepInEx.Configuration;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using RTXSandbox.Configuration;
using RTXSandbox.Gameplay;
using RTXSandbox.ModAPI;
using RTXSandbox.Modding;
using RTXSandbox.Persistence;
using RTXSandbox.Rendering;
using RTXSandbox.Rendering.Backends;
using RTXSandbox.UI;
using RTXSandbox.World;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace RTXSandbox;

public sealed partial class SandboxGame : GameWindow
{
    private static readonly int[] QualityBounces = { 1, 2, 3, 4, 6, 8, 12 };
    private static readonly IReadOnlyDictionary<string, string> ChineseBlockWords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["absorber"] = "吸光體", ["air"] = "空氣", ["alloy"] = "合金", ["aluminum"] = "鋁",
            ["amber"] = "琥珀", ["andesite"] = "安山岩", ["animated"] = "動畫", ["aqua"] = "水藍",
            ["bamboo"] = "竹", ["bark"] = "樹皮", ["basalt"] = "玄武岩", ["bedrock"] = "基岩",
            ["birch"] = "白樺", ["black"] = "黑色", ["blackstone"] = "黑石", ["block"] = "方塊",
            ["blue"] = "藍色", ["board"] = "板", ["brass"] = "黃銅", ["brick"] = "磚塊",
            ["bricks"] = "石磚", ["bronze"] = "青銅", ["carbon"] = "碳纖維", ["casing"] = "外殼",
            ["charcoal"] = "炭灰色", ["checker"] = "棋盤", ["chiseled"] = "雕刻", ["chrome"] = "鉻",
            ["circuit"] = "電路", ["clay"] = "黏土", ["clear"] = "透明", ["cloud"] = "雲",
            ["coal"] = "煤", ["coarse"] = "粗糙", ["cobalt"] = "鈷", ["concrete"] = "混凝土",
            ["conduit"] = "導管", ["coolant"] = "冷卻", ["copper"] = "銅", ["core"] = "核心",
            ["crystal"] = "水晶", ["cut"] = "切割", ["cyan"] = "青色", ["darkwood"] = "深色木",
            ["decorative"] = "雕花", ["diamond"] = "鑽石", ["diorite"] = "閃長岩", ["dirt"] = "泥土",
            ["display"] = "顯示", ["dry"] = "乾燥", ["emerald"] = "翡翠", ["energy"] = "能量",
            ["fabric"] = "布料", ["floor"] = "地板", ["flower"] = "花叢", ["frosted"] = "磨砂",
            ["gem"] = "寶石", ["glass"] = "玻璃", ["glow"] = "發光石", ["gneiss"] = "片麻岩",
            ["gold"] = "金", ["granite"] = "花崗岩", ["grass"] = "草方塊", ["grate"] = "格柵",
            ["gravel"] = "礫石", ["gray"] = "灰色", ["green"] = "綠色", ["hazard"] = "警示",
            ["hemlock"] = "鐵杉", ["hologram"] = "全息", ["ice"] = "冰", ["industrial"] = "工業",
            ["insulated"] = "絕緣", ["iron"] = "鐵", ["lamp"] = "燈", ["lava"] = "岩漿",
            ["leaves"] = "樹葉", ["led"] = " LED", ["light"] = "光", ["limestone"] = "石灰岩",
            ["log"] = "原木", ["machine"] = "機械", ["magenta"] = "洋紅", ["magic"] = "魔法",
            ["maple"] = "楓木", ["marble"] = "大理石", ["metal"] = "金屬", ["mirror"] = "鏡面",
            ["moonlight"] = "月光", ["moss"] = "苔蘚", ["mossy"] = "覆苔", ["mud"] = "泥巴",
            ["neon"] = "霓虹", ["oak"] = "橡木", ["obsidian"] = "黑曜石", ["ore"] = "礦石",
            ["oxidized"] = "氧化", ["palm"] = "棕櫚木", ["panel"] = "面板", ["peat"] = "泥炭",
            ["perfect"] = "完美", ["pillar"] = "柱", ["pipe"] = "管", ["planks"] = "木板",
            ["plasma"] = "等離子", ["plaster"] = "灰泥", ["plate"] = "板", ["polished"] = "拋光",
            ["prismatic"] = "稜彩", ["purple"] = "紫色", ["quantum"] = "量子", ["quartz"] = "石英",
            ["rack"] = "機櫃", ["rainbow"] = "彩虹", ["reactor"] = "反應爐", ["red"] = "紅色",
            ["redwood"] = "紅杉木", ["rock"] = "岩石", ["rose"] = "玫瑰色", ["rusted"] = "鏽蝕",
            ["sand"] = "沙", ["sandstone"] = "砂岩", ["sandy"] = "砂質", ["screen"] = "螢幕",
            ["server"] = "伺服器", ["silver"] = "銀", ["slate"] = "板岩", ["smoked"] = "煙燻",
            ["smooth"] = "平滑", ["snow"] = "雪", ["soil"] = "土壤", ["solar"] = "太陽",
            ["spruce"] = "雲杉", ["stainless"] = "不鏽", ["steel"] = "鋼", ["stone"] = "石頭",
            ["stripes"] = "條紋", ["swamp"] = "沼澤", ["terracotta"] = "陶瓦", ["test"] = "測試",
            ["tiles"] = "磁磚", ["titanium"] = "鈦", ["travertine"] = "洞石", ["uranium"] = "鈾",
            ["vent"] = "通風口", ["volcanic"] = "火山", ["volumetric"] = "體積", ["wall"] = "牆",
            ["warm"] = "暖白", ["warning"] = "警告", ["water"] = "水", ["white"] = "白色",
            ["wood"] = "木材", ["workbench"] = "工作台", ["yellow"] = "黃色",
        };

    private readonly GameSettings _settings;
    private BlockRegistry _blocks = null!;
    private ItemRegistry _items = null!;
    private RecipeRegistry _recipes = null!;
    private BuildToolRegistry _toolRegistry = null!;
    private BuildToolSystem _buildTools = null!;
    private MicroBuildSystem _microBuild = null!;
    private VoxelPhysicsSimulator _physics = null!;
    private VoxelWorld _world = null!;
    private ModHost _modHost = null!;
    private WorldSaveService _worldSaves = null!;
    private PlayerController _player = null!;
    private PlayerInventory _inventory = null!;
    private CreativeHotbar _creativeHotbar = null!;
    private PlayerSurvival _survival = null!;
    private DroppedItemManager _droppedItems = null!;
    private CraftingService _crafting = null!;
    private ProgressionTracker _progression = null!;
    private Camera _camera = null!;
    private WorldClock _clock = null!;
    private RenderBackendHost _renderer = null!;
    private ImGuiController _ui = null!;
    private VoxelHit? _selection;
    private VoxelHit? _regularSelection;
    private MicroVoxelHit? _microSelection;
    private PhysicsGridRayHit? _gridSelection;
    private VoxelHit? _miningTarget;
    private float _miningProgress;
    private bool _loaded;
    private bool _mouseCaptured = true;
    private bool _settingsVisible;
    private bool _inventoryVisible;
    private bool _toolboxVisible;
    private bool _worldCreationVisible;
    private bool _mainMenuVisible;
    private bool _pauseMenuVisible;
    private bool _modsVisible;
    private bool _settingsOpenedFromPause;
    private string _worldNameDraft = "world1";
    private WorldGenerationSettings _worldDraft = null!;
    private bool _advancedWorldCreation;
    private string _worldCreationStatus = string.Empty;
    private string _mainMenuStatus = string.Empty;
    private IReadOnlyList<WorldSaveSummary> _knownWorlds = Array.Empty<WorldSaveSummary>();
    private string _selectedWorldStorage = string.Empty;
    private string _worldRenameDraft = string.Empty;
    private string? _deleteWorldConfirmation;
    private string? _rebuildWorldStorage;
    private float _lastAccumulatedTime;
    private string _saveStatus = "Not saved this session";
    private string _modStatus = "Configuration changes are saved by BepInEx.";
    private ModWorkspace? _modWorkspace;
    private IReadOnlyList<ModWorkspaceEntry> _modWorkspaceEntries = Array.Empty<ModWorkspaceEntry>();
    private bool _modWorkspaceLoaded;
    private string? _modSelectedPath;
    private Task<ModCompilationResult>? _modCompilationTask;
    private string? _modCompilationSource;
    private string _modCompilationLog = string.Empty;
    private bool _modEditorVisible;
    private string? _modEditorPath;
    private string _modEditorText = string.Empty;
    private string _modEditorSavedText = string.Empty;
    private string? _modMoveSource;
    private string _modMoveDestination = string.Empty;
    private bool _modMovePopupRequested;
    private string? _modDeleteSource;
    private bool _modDeletePopupRequested;
    private double _autosaveTimer;
    private string _paletteSearch = string.Empty;
    private string _paletteCategory = "All";
    private readonly HashSet<string> _favoriteBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recentBlocks = new();
    private bool _favoritesOnly;
    private bool _gpuSmoke;
    private bool _gpuSmokeUnderwater;
    private bool _gpuSmokeIme;
    private bool _gpuSmokePause;
    private string _gpuSmokeShortcut = string.Empty;
    private bool _gpuSmokeShortcutTriggered;
    private int _pauseMenuButtonCountThisFrame;
    private bool _hardwareSmokeEdited;
    private bool _hardwareSmokeValidated;
    private int _imeSmokeStage;
    private int _gpuSmokeFrames;
    private bool _debugVisible;
    private long _lastSpacePressTimestamp;
    private ChunkCoordinate? _streamedPlayerChunk;

    public GameSettings Settings => _settings;
    public BlockRegistry Blocks => _blocks;
    public ItemRegistry Items => _items;
    public RecipeRegistry Recipes => _recipes;
    public BuildToolRegistry ToolRegistry => _toolRegistry;
    public BuildToolSystem BuildTools => _buildTools;
    public VoxelPhysicsSimulator Physics => _physics;
    public VoxelWorld World => _world;
    public PlayerController Player => _player;
    public PlayerInventory Inventory => _inventory;
    public PlayerSurvival Survival => _survival;
    public DroppedItemManager DroppedItems => _droppedItems;
    public CraftingService Crafting => _crafting;
    public ProgressionTracker Progression => _progression;
    public Camera Camera => _camera;
    public WorldClock Clock => _clock;
    public RenderBackendHost Renderer => _renderer;
    public IRenderBackend ActiveRenderer => _renderer.ActiveBackend;
    public WorldSaveService WorldSaves => _worldSaves;

    public SandboxGame()
        : this(GameSettings.Load())
    {
    }

    private SandboxGame(GameSettings settings)
        : base(
            new GameWindowSettings { UpdateFrequency = settings.FpsLimit },
            CreateNativeWindowSettings(settings))
    {
        _settings = settings;
        VSync = settings.VSync ? VSyncMode.On : VSyncMode.Off;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        string rendererName = GL.GetString(StringName.Renderer) ?? "Unknown GPU";
        string version = GL.GetString(StringName.Version) ?? "Unknown OpenGL version";
        Console.WriteLine($"GPU: {rendererName}");
        Console.WriteLine($"OpenGL: {version}");
        _gpuSmoke = Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE") == "1";
        _gpuSmokeUnderwater = Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_UNDERWATER") == "1";
        _gpuSmokeIme = Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_IME") == "1";
        _gpuSmokePause = Environment.GetEnvironmentVariable("RTX_SANDBOX_OPEN_PAUSE_MENU") == "1";
        _gpuSmokeShortcut = Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_SHORTCUT") ?? string.Empty;
        if (_gpuSmoke && Enum.TryParse(
                Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_DEFERRED_DEBUG"),
                ignoreCase: true, out DeferredDebugView smokeDebugView))
            _settings.DeferredDebugView = smokeDebugView;
        if (_gpuSmoke && Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_DEFERRED_MAX_QUALITY") == "1")
        {
            _settings.DeferredRendererQuality = 3;
            _settings.DeferredGBufferQuality = 3;
            _settings.DeferredLightingQuality = 3;
            _settings.DeferredShadowQuality = 3;
            _settings.DeferredReflectionQuality = 3;
            _settings.DeferredAmbientOcclusionQuality = 3;
            _settings.DeferredWaterQuality = 3;
            _settings.DeferredTransparencyQuality = 3;
            _settings.DeferredFogQuality = 3;
            _settings.DeferredPostProcessingQuality = 3;
        }
        Console.WriteLine("操作：左鍵破壞 | 右鍵放置 | E 方塊庫 | B 建造工具 | V 精密模式 | F3 除錯資訊");

        GL.ClearColor(0.05f, 0.08f, 0.12f, 1f);
        _blocks = new BlockRegistry();
        _items = new ItemRegistry(_blocks);
        _recipes = new RecipeRegistry();
        _toolRegistry = new BuildToolRegistry();
        _world = new VoxelWorld(registry: _blocks, seed: _settings.WorldSeed);
        _modHost = new ModHost(this, _blocks, _items, _recipes, _toolRegistry, _world);
        _modHost.RegisterService(_settings);
        _modHost.AttachApi();
        if (Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_MOD") == "1")
        {
            if (!_blocks.TryGet("examplepack:cyan_lamp", out _))
                throw new InvalidOperationException("GPU mod smoke could not find the Example Content Pack block.");
            if (!_blocks.TryGet("examplemod:signal_block", out _))
                throw new InvalidOperationException("GPU mod smoke loaded no block from RTX Sandbox Example Mod.");
            Console.WriteLine("Content pack and external ModAPI registration verified.");
        }
        _worldSaves = new WorldSaveService(
            _blocks,
            preferredSeed: _settings.WorldSeed,
            worldName: _settings.WorldName);
        _modHost.RegisterService(_worldSaves);
        bool loadedSave = _worldSaves.TryLoad(_world, out SavedWorldState savedState, out string? loadError);
        if (!loadedSave && loadError is null)
        {
            int randomSeed = Random.Shared.Next(int.MinValue, int.MaxValue);
            _settings.WorldSeed = randomSeed;
            _world.Regenerate(WorldGenerationSettings.Natural(randomSeed));
        }
        _saveStatus = loadedSave
            ? $"Loaded {Path.GetFileName(_worldSaves.LastLoadSource)}{(_worldSaves.RecoveredLastLoad ? " (recovered)" : string.Empty)}"
            : loadError is null ? "New generated world" : $"Load failed: {loadError}";
        int spawnX = VoxelWorld.Width / 2;
        int spawnZ = VoxelWorld.Depth / 2 + 8;
        float spawnY = _world.FindTopVoxelY(spawnX, spawnZ) + 1.001f;
        NumericsVector3 spawnPosition = loadedSave
            ? savedState.PlayerPosition
            : new NumericsVector3(spawnX + 0.5f, spawnY, spawnZ + 0.5f);
        _player = new PlayerController(_world, spawnPosition);
        StreamWorldAroundPlayer();
        _player.FlightMode = _settings.StartInFlightMode;
        _player.NoClip = _settings.StartWithNoClip;
        _buildTools = new BuildToolSystem(_world, _blocks, _toolRegistry, _player.WouldIntersectBlock);
        if (_blocks.TryGet(_settings.SelectedBlockKey, out BlockDefinition? rememberedBlock) && rememberedBlock!.IsPlaceable)
            _buildTools.SelectedBlockId = rememberedBlock.Id;
        if (Enum.TryParse(_settings.EditorTool, ignoreCase: true, out EditorToolMode rememberedTool) &&
            rememberedTool != EditorToolMode.Custom)
            _buildTools.ToolMode = rememberedTool;
        _buildTools.BrushRadius = _settings.BrushRadius;
        _buildTools.ExplosionRadius = _settings.ExplosionRadius;
        _microBuild = new MicroBuildSystem(_world, _blocks, _player.WouldIntersectAabb);
        _microBuild.SetGridDivisor(_settings.PrecisionGridDivisor);
        _creativeHotbar = new CreativeHotbar(
            _blocks,
            _settings.CreativeHotbar,
            _settings.CreativeHotbarSlot,
            _settings.SelectedBlockKey);
        _buildTools.SelectedBlockId = _creativeHotbar.SelectedBlock.Id;
        foreach (string key in _settings.FavoriteBlocks) _favoriteBlocks.Add(key);
        foreach (string key in _settings.RecentBlocks)
        {
            if (_blocks.TryGet(key, out BlockDefinition? recent) && recent!.IsPlaceable) _recentBlocks.Add(recent.Key);
        }
        _physics = new VoxelPhysicsSimulator(_world, _blocks);
        _inventory = new PlayerInventory(_items);
        if (loadedSave && savedState.Inventory.Count > 0)
        {
            _inventory.Restore(savedState.Inventory, savedState.SelectedHotbarSlot);
        }
        else
        {
            SeedStartingInventory();
        }
        NumericsVector3 respawnPosition = new(spawnX + 0.5f, spawnY, spawnZ + 0.5f);
        _survival = new PlayerSurvival(
            _player,
            respawnPosition,
            damage => _modHost.Publish(damage),
            respawn => _modHost.Publish(respawn));
        if (loadedSave)
        {
            _survival.Restore(savedState.Health, savedState.Oxygen, savedState.Hunger);
        }
        _droppedItems = new DroppedItemManager(
            _world,
            _items,
            _inventory,
            pickup => _modHost.Publish(pickup));
        _crafting = new CraftingService(
            _recipes,
            _items,
            _inventory,
            crafting => _modHost.Publish(crafting),
            crafted => _modHost.Publish(crafted));
        _progression = new ProgressionTracker(
            _inventory,
            objective => _modHost.Publish(objective));
        if (loadedSave)
        {
            _progression.Restore(savedState.CompletedObjectives, savedState.SawNight);
        }
        _camera = new Camera { FieldOfViewDegrees = _settings.FieldOfView };
        if (_gpuSmokeUnderwater)
        {
            SetUpUnderwaterSmokeScene();
        }
        else if (_gpuSmoke)
        {
            // GPU smoke uses a deterministic showcase and never saves it. Persisted user
            // terrain/Physics Grids can otherwise make strict per-pixel evidence depend
            // on unrelated save history. Streaming is re-queued so the test still covers
            // asynchronous generation and renderer invalidation.
            _world.Regenerate(new WorldGenerationSettings
            {
                Preset = WorldPreset.Showcase,
                Seed = 1337,
                FlatSurfaceHeight = 4,
                SurfaceBlockKey = "rtxsandbox:white_concrete",
                UndergroundBlockKey = "rtxsandbox:stone",
                GenerateLakes = false,
                GenerateVegetation = false,
            });
            _player.Teleport(new NumericsVector3(VoxelWorld.Width / 2f + 0.5f, 6.001f,
                VoxelWorld.Depth / 2f + 8.5f));
            _streamedPlayerChunk = null;
            StreamWorldAroundPlayer();
            NumericsVector3 probe = _player.EyePosition + _camera.Forward * 3f;
            int probeX = (int)MathF.Floor(probe.X);
            int probeY = Math.Clamp((int)MathF.Floor(probe.Y), VoxelWorld.MinimumY, VoxelWorld.MaximumY - 1);
            int probeZ = (int)MathF.Floor(probe.Z);
            var smokeMicro = new MicroVoxelContainer(2);
            smokeMicro.Fill((ushort)VoxelMaterial.Stone);
            smokeMicro.PaintCell(2, 0, 0, 0, 0);
            _world.SetMicroVoxels(probeX, probeY, probeZ, smokeMicro);
            // Keep a second precision cell face-connected to terrain. The first cell is
            // intentionally allowed to detach into a Physics Grid; this one proves that
            // static micro geometry reaches the DXR static BLAS as well.
            const int staticMicroX = 20;
            const int staticMicroZ = 20;
            int staticMicroY = _world.FindSurfaceY(staticMicroX, staticMicroZ) + 1;
            _world.SetMicroVoxels(staticMicroX, staticMicroY, staticMicroZ, smokeMicro);
            _world.SetBlockId(2, 250, 2, (ushort)VoxelMaterial.Brick);
            _world.SetBlockId(3, 250, 2, (ushort)VoxelMaterial.Glass);
            int glowY = Math.Clamp(_world.FindTopVoxelY(4, 2), VoxelWorld.MinimumY, 255);
            _world.SetBlockId(4, glowY, 2, (ushort)VoxelMaterial.Glow);
            _physics.AnalyzeNow();
            if (_world.PhysicsGrids.Grids.LastOrDefault() is PhysicsGrid smokeGrid)
                smokeGrid.AngularVelocity = new NumericsVector3(0.25f, 0.45f, 0.12f);
        }
        _clock = new WorldClock(
            loadedSave ? savedState.TimeOfDay : _settings.TimeOfDay,
            _settings.DayLengthMinutes,
            _settings.PauseTime);
        _modHost.RegisterService(_player);
        _modHost.RegisterService(_buildTools);
        _modHost.RegisterService(_microBuild);
        _modHost.RegisterService(_creativeHotbar);
        _modHost.RegisterService(_physics);
        _modHost.RegisterService(_inventory);
        _modHost.RegisterService(_survival);
        _modHost.RegisterService(_droppedItems);
        _modHost.RegisterService(_crafting);
        _modHost.RegisterService(_progression);
        _modHost.RegisterService(_camera);
        _modHost.RegisterService(_clock);
        _lastAccumulatedTime = _clock.TimeOfDayHours;
        RenderBackendKind startupRenderer = _settings.Renderer;
        string? rendererOverride = Environment.GetEnvironmentVariable("RTX_SANDBOX_RENDERER");
        if (Enum.TryParse(rendererOverride, ignoreCase: true, out RenderBackendKind overriddenRenderer) &&
            RenderBackendCatalog.IsImplemented(overriddenRenderer))
        {
            startupRenderer = overriddenRenderer;
        }
        _renderer = new RenderBackendHost(
            _world,
            _blocks,
            ClientSize.X,
            ClientSize.Y,
            _settings.RenderScale,
            startupRenderer);
        if (_gpuSmoke && Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_REQUIRE_HARDWARE_RT") == "1" &&
            _renderer.Kind != RenderBackendKind.HardwareRayTracing)
            throw new InvalidOperationException($"Hardware RT smoke refused fallback: {_renderer.Status}");
        _ui = new ImGuiController(GetNativeWindowHandle());
        _modHost.RegisterService(_renderer);
        _modHost.RegisterService(_ui);
        _settingsVisible = Environment.GetEnvironmentVariable("RTX_SANDBOX_OPEN_SETTINGS") == "1";
        _inventoryVisible = (Environment.GetEnvironmentVariable("RTX_SANDBOX_OPEN_EDITOR") == "1" ||
                             Environment.GetEnvironmentVariable("RTX_SANDBOX_OPEN_INVENTORY") == "1") && !_settingsVisible;
        _worldCreationVisible = Environment.GetEnvironmentVariable("RTX_SANDBOX_OPEN_WORLD_CREATOR") == "1" && !_settingsVisible;
        _modsVisible = Environment.GetEnvironmentVariable("RTX_SANDBOX_OPEN_MODS") == "1" &&
                       !_settingsVisible && !_worldCreationVisible;
        _pauseMenuVisible = (_gpuSmokePause || _modsVisible) && !_settingsVisible && !_worldCreationVisible;
        if (_worldCreationVisible) _inventoryVisible = false;
        _worldDraft = _world.GenerationSettings.Clone();
        _worldNameDraft = _settings.WorldName;
        RefreshWorldList();
        bool forceMainMenu = Environment.GetEnvironmentVariable("RTX_SANDBOX_OPEN_MAIN_MENU") == "1";
        _mainMenuVisible = forceMainMenu ||
                           (!_gpuSmoke && !_settingsVisible && !_inventoryVisible && !_worldCreationVisible);
        SetMouseCaptured(!AnyInterfaceVisible());
        _loaded = true;
        _modHost.PublishWorldReady();
        UpdateSelection();
        UpdateTitle();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        if (!_loaded)
        {
            return;
        }

        HandleSystemAndRenderSettings();
        _world.ProcessPendingChunkGeneration(maximumChunks: 1);
        if (_ui.WantsKeyboardInput)
        {
            ResetMining();
            return;
        }
        if (!AnyInterfaceVisible())
        {
            HandleTimeControls();
            SelectMaterialFromInputs();
            HandleEditorShortcuts();
        }
        else
        {
            ResetMining();
            return;
        }

        _clock.Update((float)args.Time);
        _modHost.Update((float)args.Time);
        StreamWorldAroundPlayer();
        if (!_clock.Paused && _clock.CircularDistanceFrom(_lastAccumulatedTime) >= 0.012f)
        {
            _lastAccumulatedTime = _clock.TimeOfDayHours;
            _renderer.ResetAccumulation();
        }

        NumericsVector3 oldPosition = _player.Position;
        NumericsVector3 oldForward = _camera.Forward;

        if (_mouseCaptured && IsFocused)
        {
            _camera.Rotate(
                new NumericsVector2(MouseState.Delta.X, MouseState.Delta.Y),
                _settings.MouseSensitivity);
        }

        NumericsVector2 movement = NumericsVector2.Zero;
        if (_mouseCaptured && IsFocused)
        {
            movement.X = (KeyboardState.IsKeyDown(Keys.D) ? 1f : 0f) -
                         (KeyboardState.IsKeyDown(Keys.A) ? 1f : 0f);
            movement.Y = (KeyboardState.IsKeyDown(Keys.W) ? 1f : 0f) -
                         (KeyboardState.IsKeyDown(Keys.S) ? 1f : 0f);
        }

        bool sprint = KeyboardState.IsKeyDown(Keys.LeftShift) || KeyboardState.IsKeyDown(Keys.RightShift);
        bool jump = _mouseCaptured && KeyboardState.IsKeyDown(Keys.Space);
        bool descend = _mouseCaptured &&
                       (KeyboardState.IsKeyDown(Keys.LeftControl) || KeyboardState.IsKeyDown(Keys.RightControl));
        _player.Update((float)args.Time, movement, _camera.Forward, sprint, jump, descend);

        _physics.Update((float)args.Time);
        _player.ResolveExternalPenetration((float)args.Time);

        NumericsVector3 attemptedPosition = _player.Position;
        if (NumericsVector3.DistanceSquared(oldPosition, attemptedPosition) > 0.0000001f)
        {
            PlayerMoveEvent moveEvent = _modHost.Publish(new PlayerMoveEvent(oldPosition, attemptedPosition));
            if (moveEvent.IsCancelled)
            {
                _player.Teleport(oldPosition);
            }
            else if (NumericsVector3.DistanceSquared(moveEvent.NewPosition, attemptedPosition) > 0.0000001f)
            {
                _player.Teleport(moveEvent.NewPosition);
            }
        }

        _autosaveTimer += args.Time;
        if (_autosaveTimer >= _settings.AutosaveMinutes * 60.0)
        {
            SaveWorld();
        }

        bool viewChanged = NumericsVector3.DistanceSquared(oldPosition, _player.Position) > 0.0000001f ||
                           NumericsVector3.DistanceSquared(oldForward, _camera.Forward) > 0.0000001f;
        if (viewChanged)
        {
            _renderer.ResetAccumulation();
        }

        VoxelHit? previousSelection = RenderSelection();
        UpdateSelection();
        if (previousSelection != RenderSelection())
        {
            ResetMining();
            _renderer.ResetAccumulation();
        }

        if (_mouseCaptured && IsFocused)
        {
            if (_buildTools.ToolMode == EditorToolMode.GridAssign && _selection.HasValue)
            {
                if (MouseState.IsButtonPressed(MouseButton.Left))
                    _physics.SelectGrid(_selection.Value.GridId);
                if (MouseState.IsButtonPressed(MouseButton.Right) && _physics.TryTransferCell(_selection.Value))
                {
                    _renderer.ResetAccumulation();
                    UpdateSelection();
                }
                if (MouseState.IsButtonPressed(MouseButton.Middle)) AssignCreativeBlock(_selection.Value.MaterialId);
            }
            else if (_selection is { IsPhysicsGrid: true } && _gridSelection.HasValue)
            {
                PhysicsGridRayHit gridHit = _gridSelection.Value;
                if (MouseState.IsButtonPressed(MouseButton.Left) &&
                    _physics.TryRemoveGridCell(gridHit.GridId, gridHit.CellX, gridHit.CellY, gridHit.CellZ))
                {
                    _renderer.ResetAccumulation();
                    UpdateSelection();
                }
                if (MouseState.IsButtonPressed(MouseButton.Right) &&
                    _physics.TryPlaceOnGrid(gridHit, _buildTools.SelectedBlockId))
                {
                    _renderer.ResetAccumulation();
                    UpdateSelection();
                }
                if (MouseState.IsButtonPressed(MouseButton.Middle)) AssignCreativeBlock(gridHit.MaterialId);
            }
            else if (_microBuild.Enabled)
            {
                PrecisionHit? hit = CurrentPrecisionHit();
                if (MouseState.IsButtonPressed(MouseButton.Left) && hit.HasValue && _microBuild.Erase(hit.Value))
                {
                    _renderer.ResetAccumulation();
                    UpdateSelection();
                }
                if (MouseState.IsButtonPressed(MouseButton.Right) && hit.HasValue &&
                    _microBuild.Place(hit.Value, _buildTools.SelectedBlockId))
                {
                    _renderer.ResetAccumulation();
                    UpdateSelection();
                }
                if (MouseState.IsButtonPressed(MouseButton.Middle) && hit.HasValue)
                {
                    AssignCreativeBlock(hit.Value.MaterialId);
                }
            }
            else
            {
                if (MouseState.IsButtonPressed(MouseButton.Left) && _selection.HasValue)
                {
                    if (_buildTools.UsePrimary(_selection.Value))
                    {
                        _renderer.ResetAccumulation();
                        UpdateSelection();
                    }
                }
                if (MouseState.IsButtonPressed(MouseButton.Right))
                {
                    if (_selection.HasValue && _buildTools.QuickPlace(_selection.Value))
                    {
                        _renderer.ResetAccumulation();
                        UpdateSelection();
                    }
                }
                if (MouseState.IsButtonPressed(MouseButton.Middle) && _selection.HasValue)
                {
                    EditorToolMode previousTool = _buildTools.ToolMode;
                    _buildTools.ToolMode = EditorToolMode.Pick;
                    _buildTools.UsePrimary(_selection.Value);
                    _buildTools.ToolMode = previousTool;
                    AssignCreativeBlock(_buildTools.SelectedBlockId);
                }
            }
        }
        else
        {
            ResetMining();
        }
    }

    private void StreamWorldAroundPlayer()
    {
        ChunkCoordinate center = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Z));
        if (_streamedPlayerChunk == center) return;
        _world.QueueChunksAround(_player.Position, VoxelWorld.DefaultGenerationRadiusChunks);
        _streamedPlayerChunk = center;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        if (!_loaded)
        {
            return;
        }

        BeforeRenderEvent beforeRender = _modHost.Publish(new BeforeRenderEvent((float)args.Time));
        if (!beforeRender.IsCancelled)
        {
            _renderer.Render(
                _player.EyePosition,
                _camera,
                RenderSelection(),
                _clock.TimeOfDayHours,
                new RenderQualitySettings(
                    _settings.PathBounces,
                    _settings.PathSamplesPerFrame,
                    _settings.PathMaxAccumulatedSamples,
                    _settings.DeferredSpecularQuality,
                    _settings.DeferredReflectionQuality,
                    _settings.DeferredRendererQuality,
                    _settings.DeferredGBufferQuality,
                    _settings.DeferredLightingQuality,
                    _settings.DeferredShadowQuality,
                    _settings.DeferredAmbientOcclusionQuality,
                    _settings.DeferredWaterQuality,
                    _settings.DeferredTransparencyQuality,
                    _settings.DeferredFogQuality,
                    _settings.DeferredPostProcessingQuality,
                    _settings.DeferredHdr,
                    _settings.DeferredBloom,
                    _settings.DeferredToneMapping,
                    _settings.DeferredDebugView,
                    _settings.PbrSpecularQuality,
                    _settings.PbrReflectionQuality,
                    _settings.ClassicLightingQuality,
                    _settings.HardwareRayTracingQuality,
                    _settings.HardwareRayTracingRenderStyle),
                _settings.Exposure);
        }
        if (_gpuSmoke && Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_REQUIRE_HARDWARE_RT") == "1")
        {
            if (!_hardwareSmokeEdited &&
                (_world.PendingChunkGenerationCount == 0 || _gpuSmokeFrames >= 35))
            {
                _hardwareSmokeEdited = true;
                _settings.HardwareRayTracingRenderStyle = HardwareRayTracingRenderStyle.WaxMode;
                int editX = VoxelWorld.Width / 2;
                int editZ = VoxelWorld.Depth / 2;
                int editY = _world.FindSurfaceY(editX, editZ);
                ushort previous = _world.GetBlockId(editX, editY, editZ);
                ushort replacement = previous == (ushort)VoxelMaterial.Brick
                    ? (ushort)VoxelMaterial.Stone
                    : (ushort)VoxelMaterial.Brick;
                if (!_world.SetBlockId(editX, editY, editZ, replacement))
                    throw new InvalidOperationException("DXR smoke could not apply its deterministic world edit.");
                // Keep the smoke Grid in the center of view without overlapping the
                // player collider, which would push the camera away before the trace.
                NumericsVector3 gridCenter = _player.EyePosition + _camera.Forward * 2f;
                NumericsVector3 gridOrigin = gridCenter - new NumericsVector3(0.5f);
                _world.PhysicsGrids.Create(new[]
                {
                    // Put water on the optical axis so the strict smoke test proves
                    // that primary DXR rays execute the transmission/refraction path.
                    new PhysicsGridElement(0, 0, 0, NumericsVector3.Zero, NumericsVector3.One,
                        (ushort)VoxelMaterial.Water),
                    new PhysicsGridElement(-1, 0, 0, new NumericsVector3(-0.78f, 0f, 0f),
                        new NumericsVector3(-0.10f, 1f, 1f), (ushort)VoxelMaterial.Stone),
                    new PhysicsGridElement(1, 0, 0, new NumericsVector3(1.10f, 0f, 0f),
                        new NumericsVector3(1.78f, 1f, 1f), (ushort)VoxelMaterial.Glow),
                }, gridOrigin);
            }
            if (_gpuSmokeFrames + 1 >= 3)
            {
                if (_renderer.ActiveBackend is not DxrHardwareRenderer dxr)
                    throw new InvalidOperationException("DXR smoke lost the Hardware RT backend.");
                bool evidenceIncomplete = dxr.TracedTriangleCount <= 0 || dxr.LastHitPixelCount <= 0 ||
                    dxr.LastShadowRayPixelCount <= 0 || dxr.LastShadowOccludedPixelCount <= 0 ||
                    dxr.LastReflectionRayPixelCount <= 0 || dxr.LastWaxReflectionPixelCount <= 0 ||
                    dxr.LastTransparentHitPixelCount <= 0 || dxr.EmissiveLightCount <= 0 ||
                    dxr.VisibleDxrChunkCount <= 0 || dxr.ViewCullingExpansion < 1.6f ||
                    dxr.TracedPhysicsGridCount <= 0 || dxr.TracedPhysicsGridTriangleCount <= 0 ||
                    dxr.TracedMicroVoxelTriangleCount <= 0 || dxr.DynamicTopologyBuildCount <= 0 ||
                    dxr.LastPhysicsGridHitPixelCount <= 0 || dxr.LastTexturedHitPixelCount <= 0 ||
                    dxr.AccelerationStructureBuildCount < 3 ||
                    dxr.StaticAccelerationStructureBuildCount < 2 ||
                    dxr.DynamicAccelerationStructureBuildCount < 1 || dxr.TlasUpdateCount < 1 ||
                    dxr.LastHardwareQuality < 2 ||
                    !dxr.GlobalRayGeometryRetained;
                if (evidenceIncomplete && _gpuSmokeFrames + 1 >= 60)
                    throw new InvalidOperationException(
                        $"DXR smoke evidence incomplete: triangles={dxr.TracedTriangleCount}, hits={dxr.LastHitPixelCount}, " +
                        $"shadow={dxr.LastShadowRayPixelCount}, occluded={dxr.LastShadowOccludedPixelCount}, reflection={dxr.LastReflectionRayPixelCount}, wax={dxr.LastWaxReflectionPixelCount}, transparent={dxr.LastTransparentHitPixelCount}, emissiveLights={dxr.EmissiveLightCount}, " +
                        $"grids={dxr.TracedPhysicsGridCount}, gridTriangles={dxr.TracedPhysicsGridTriangleCount}, " +
                        $"gridHits={dxr.LastPhysicsGridHitPixelCount}, texturedHits={dxr.LastTexturedHitPixelCount}, " +
                        $"asBuilds={dxr.AccelerationStructureBuildCount}, staticBuilds={dxr.StaticAccelerationStructureBuildCount}, " +
                        $"dynamicRefits={dxr.DynamicAccelerationStructureBuildCount}, tlasUpdates={dxr.TlasUpdateCount}, " +
                        $"microTriangles={dxr.TracedMicroVoxelTriangleCount}, topologyBuilds={dxr.DynamicTopologyBuildCount}, visibleChunks={dxr.VisibleDxrChunkCount}, " +
                        $"shadowCasterChunks={dxr.ShadowCasterDxrChunkCount}, quality={dxr.LastHardwareQuality}.");
                if (!evidenceIncomplete)
                {
                    Console.WriteLine($"DXR evidence: tier {dxr.RaytracingTier}, triangles {dxr.TracedTriangleCount:N0}, " +
                                  $"hit pixels {dxr.LastHitPixelCount:N0}, AS builds {dxr.AccelerationStructureBuildCount}, " +
                                  $"static builds {dxr.StaticAccelerationStructureBuildCount}, " +
                                   $"dynamic refits {dxr.DynamicAccelerationStructureBuildCount}, TLAS updates {dxr.TlasUpdateCount}, " +
                                  $"last static/refit {dxr.LastStaticAccelerationStructureBuildMilliseconds:0.00}/" +
                                  $"{dxr.LastDynamicAccelerationStructureBuildMilliseconds:0.00} ms, " +
                                   $"quality {dxr.LastHardwareQuality}, shadow rays {dxr.LastShadowRayPixelCount:N0}, " +
                                   $"culling expansion {dxr.ViewCullingExpansion:0.0}x, " +
                                  $"occluded {dxr.LastShadowOccludedPixelCount:N0}, reflection rays {dxr.LastReflectionRayPixelCount:N0}, " +
                                  $"wax reflections {dxr.LastWaxReflectionPixelCount:N0}, " +
                                  $"transparent hits {dxr.LastTransparentHitPixelCount:N0}, emissive lights {dxr.EmissiveLightCount}, " +
                                   $"visible chunks {dxr.VisibleDxrChunkCount}/{_world.LoadedChunkCount}, " +
                                   $"global ray geometry {dxr.GlobalRayGeometryRetained}, " +
                                  $"offscreen shadow casters {dxr.ShadowCasterDxrChunkCount}, " +
                                   $"grid triangles {dxr.TracedPhysicsGridTriangleCount:N0}, micro triangles {dxr.TracedMicroVoxelTriangleCount:N0}, " +
                                   $"topology builds {dxr.DynamicTopologyBuildCount}, grid hits {dxr.LastPhysicsGridHitPixelCount:N0}, " +
                                      $"textured hits {dxr.LastTexturedHitPixelCount:N0}.");
                    ValidateHardwareRtFramebuffer();
                    _hardwareSmokeValidated = true;
                }
            }
        }
        _modHost.Publish(new AfterRenderEvent((float)args.Time));

        _ui.BeginFrame(ClientSize.X, ClientSize.Y, (float)args.Time, MouseState, KeyboardState);
        DrawHud();
        if (_inventoryVisible)
        {
            DrawCreativeInventoryInterface();
        }
        if (_toolboxVisible)
        {
            DrawBuildToolboxInterface();
        }
        if (_settingsVisible)
        {
            DrawSettingsInterface();
        }
        if (_modsVisible)
        {
            DrawModsInterface();
        }
        if (_worldCreationVisible)
        {
            DrawCreateWorldInterface();
        }
        if (_mainMenuVisible && !_settingsVisible && !_worldCreationVisible)
        {
            DrawMainMenu();
        }
        if (_pauseMenuVisible && !_settingsVisible && !_modsVisible && !_mainMenuVisible)
        {
            DrawPauseMenu();
        }
        _ui.EndFrame(ClientSize.X, ClientSize.Y);
        if (_gpuSmokePause && _gpuSmokeFrames + 1 >= 3 &&
            (!_pauseMenuVisible || _pauseMenuButtonCountThisFrame != 8))
            throw new InvalidOperationException(
                $"Pause menu smoke expected 8 controls, rendered {_pauseMenuButtonCountThisFrame}.");
        if (_gpuSmokeFrames + 1 >= 3 && !string.IsNullOrEmpty(_gpuSmokeShortcut))
        {
            bool stillOpen = _gpuSmokeShortcut == "inventory" ? _inventoryVisible : _settingsVisible;
            if (!_gpuSmokeShortcutTriggered || stillOpen)
                throw new InvalidOperationException(
                    $"UI shortcut smoke did not close {_gpuSmokeShortcut}; triggered={_gpuSmokeShortcutTriggered}, open={stillOpen}.");
            Console.WriteLine($"UI shortcut verified: {_gpuSmokeShortcut} closed after ImGui captured keyboard focus.");
        }
        if (_gpuSmokeUnderwater && _gpuSmokeFrames + 1 >= 3)
        {
            ValidateUnderwaterSmokeFrame();
        }
        if (_gpuSmoke && _gpuSmokeFrames + 1 >= 3 &&
            _world.PendingChunkGenerationCount == 0 &&
            _renderer.ActiveBackend is RasterVoxelRenderer deferredReady &&
            deferredReady.PendingVisibleMeshRebuildCount == 0 &&
            (_renderer.Kind == RenderBackendKind.DeferredLighting ||
             _renderer.Kind == RenderBackendKind.HybridPbrRaster))
        {
            ValidateDeferredSmokeFrame();
        }
        if (_gpuSmoke && _gpuSmokeFrames + 1 >= 3 &&
            _world.PendingChunkGenerationCount == 0 &&
            _renderer.ActiveBackend is RasterVoxelRenderer raster &&
            (_renderer.Kind == RenderBackendKind.Classic || _renderer.Kind == RenderBackendKind.PbrForward))
        {
            if (raster.VisibleChunkCount <= 0 || raster.CulledChunkCount <= 0)
                throw new InvalidOperationException(
                    $"Raster culling evidence incomplete: visible={raster.VisibleChunkCount}, culled={raster.CulledChunkCount}.");
            Console.WriteLine($"Raster culling evidence: {raster.VisibleChunkCount} visible chunks, " +
                              $"{raster.CulledChunkCount} culled chunks.");
        }
        SwapBuffers();
        if (_gpuSmoke && ++_gpuSmokeFrames >= 3 &&
            _world.PendingChunkGenerationCount == 0 &&
            (_renderer.ActiveBackend is not RasterVoxelRenderer closingRaster ||
             (closingRaster.Kind != RenderBackendKind.DeferredLighting &&
              closingRaster.Kind != RenderBackendKind.HybridPbrRaster) ||
             closingRaster.PendingVisibleMeshRebuildCount == 0) &&
            (Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_REQUIRE_HARDWARE_RT") != "1" ||
             _hardwareSmokeValidated))
        {
            if (_gpuSmokeIme && (_imeSmokeStage < 3 || !_ui.ImePositionUpdated))
                throw new InvalidOperationException("Unicode/IME smoke did not commit multilingual text or position the IME window.");
            Console.WriteLine($"GPU smoke test passed: rendered {_gpuSmokeFrames} frames with {_renderer.Name}.");
            Close();
        }

    }

    private void SetUpUnderwaterSmokeScene()
    {
        _world.LoadRawData(new ushort[VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth]);
        const int centerX = VoxelWorld.Width / 2;
        const int centerZ = VoxelWorld.Depth / 2;

        // A deterministic submerged camera facing a textured, emissive wall. The scene
        // makes a zero-distance water-ray regression produce a truly black readback.
        for (int x = centerX - 8; x <= centerX + 8; x++)
        for (int z = centerZ - 12; z <= centerZ + 6; z++)
            _world.SetBlockId(x, 0, z, (ushort)VoxelMaterial.Bedrock);

        for (int x = centerX - 7; x <= centerX + 7; x++)
        for (int y = 1; y <= 8; y++)
            _world.SetBlockId(
                x,
                y,
                centerZ - 11,
                (ushort)(((x + y) & 1) == 0 ? VoxelMaterial.Glow : VoxelMaterial.Marble));

        for (int x = centerX - 7; x <= centerX + 7; x++)
        for (int y = 1; y <= 7; y++)
        for (int z = centerZ - 10; z <= centerZ + 5; z++)
            _world.SetBlockId(x, y, z, (ushort)VoxelMaterial.Water);

        _player.Teleport(new NumericsVector3(centerX + 0.5f, 3f, centerZ + 0.5f));
        _player.FlightMode = true;
        _player.NoClip = false;
    }

    private void ValidateUnderwaterSmokeFrame()
    {
        const int sampleSize = 64;
        int width = Math.Min(sampleSize, Math.Max(1, ClientSize.X));
        int height = Math.Min(sampleSize, Math.Max(1, ClientSize.Y));
        // Sample the optical axis: every renderer points it at the submerged
        // checker wall, while the right side may contain a uniform water surface or UI.
        int x = Math.Clamp(ClientSize.X / 2 - width / 2, 0, Math.Max(0, ClientSize.X - width));
        int y = Math.Clamp(ClientSize.Y / 2 - height / 2, 0, Math.Max(0, ClientSize.Y - height));
        var pixels = new byte[width * height * 3];
        GL.Finish();
        GL.ReadPixels(x, y, width, height, PixelFormat.Rgb, PixelType.UnsignedByte, pixels);

        double luminance = 0.0;
        byte minimum = byte.MaxValue;
        byte maximum = byte.MinValue;
        for (int index = 0; index < pixels.Length; index += 3)
        {
            byte red = pixels[index];
            byte green = pixels[index + 1];
            byte blue = pixels[index + 2];
            byte value = (byte)Math.Clamp((int)Math.Round(red * 0.2126 + green * 0.7152 + blue * 0.0722), 0, 255);
            luminance += value;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        double average = luminance / Math.Max(1, width * height);
        if (average < 4.0 || maximum - minimum < 3)
            throw new InvalidOperationException(
                $"Underwater render is black or blank (average luminance {average:0.00}, range {minimum}-{maximum}).");
        Console.WriteLine($"Underwater framebuffer passed: average luminance {average:0.00}, range {minimum}-{maximum}.");
    }

    private void ValidateHardwareRtFramebuffer()
    {
        const int sampleSize = 64;
        int width = Math.Min(sampleSize, Math.Max(1, ClientSize.X));
        int height = Math.Min(sampleSize, Math.Max(1, ClientSize.Y));
        int x = Math.Max(0, ClientSize.X / 2 - width / 2);
        int y = Math.Max(0, ClientSize.Y / 2 - height / 2);
        var pixels = new byte[width * height * 3];
        GL.Finish();
        GL.ReadPixels(x, y, width, height, PixelFormat.Rgb, PixelType.UnsignedByte, pixels);
        byte minimum = byte.MaxValue;
        byte maximum = byte.MinValue;
        double total = 0;
        for (int index = 0; index < pixels.Length; index += 3)
        {
            byte value = (byte)Math.Clamp((int)Math.Round(
                pixels[index] * 0.2126 + pixels[index + 1] * 0.7152 + pixels[index + 2] * 0.0722), 0, 255);
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            total += value;
        }
        double average = total / Math.Max(1, width * height);
        if (average < 4 || maximum - minimum < 3)
            throw new InvalidOperationException(
                $"DXR framebuffer is black, blank, or constant (average {average:0.00}, range {minimum}-{maximum}).");
        Console.WriteLine($"DXR OpenGL framebuffer passed: average {average:0.00}, range {minimum}-{maximum}.");
    }

    private void ValidateDeferredSmokeFrame()
    {
        if (_renderer.ActiveBackend is not RasterVoxelRenderer deferred ||
            (deferred.Kind != RenderBackendKind.DeferredLighting &&
             deferred.Kind != RenderBackendKind.HybridPbrRaster))
            throw new InvalidOperationException("Deferred/Hybrid smoke lost its raster backend.");
        const int requiredFeatures = 1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 256 | 512;
        if ((deferred.DeferredFeatureMask & requiredFeatures) != requiredFeatures ||
            deferred.DeferredPointLightCount <= 0 || deferred.DeferredSpotLightCount <= 0 ||
            deferred.VisibleChunkCount <= 0 || deferred.DirectionalShadowDrawCount <= 0 ||
            deferred.DirectionalShadowMapUpdateCount <= 0 || deferred.ViewCullingExpansion < 1.6f)
            throw new InvalidOperationException(
                $"Deferred feature evidence incomplete: mask={deferred.DeferredFeatureMask}, " +
                $"pointLights={deferred.DeferredPointLightCount}, spotLights={deferred.DeferredSpotLightCount}, " +
                $"visibleChunks={deferred.VisibleChunkCount}, culledChunks={deferred.CulledChunkCount}, " +
                $"shadowCasterChunks={deferred.ShadowCasterChunkCount}, " +
                $"directionalShadowDraws={deferred.DirectionalShadowDrawCount}.");

        (float shadowMinimum, float shadowMaximum) = deferred.ReadDirectionalShadowDepthRange(dynamicLayer: false);
        if (shadowMinimum >= 0.9999f || shadowMaximum - shadowMinimum < 0.0001f)
            throw new InvalidOperationException(
                $"Deferred shadow texture is empty or constant: {shadowMinimum:0.000000}-{shadowMaximum:0.000000}.");
        if (_world.PhysicsGrids.Count > 0 &&
            (deferred.PhysicsGridMeshUploadCount <= 0 || deferred.DynamicShadowMapUpdateCount <= 0))
            throw new InvalidOperationException(
                $"Deferred Physics Grid evidence incomplete: uploads={deferred.PhysicsGridMeshUploadCount}, " +
                $"dynamicShadowUpdates={deferred.DynamicShadowMapUpdateCount}.");

        const int sampleSize = 64;
        int width = Math.Min(sampleSize, Math.Max(1, ClientSize.X));
        int height = Math.Min(sampleSize, Math.Max(1, ClientSize.Y));
        int x = Math.Max(0, ClientSize.X / 2 - width / 2);
        int y = Math.Max(0, ClientSize.Y / 2 - height / 2);
        var pixels = new byte[width * height * 3];
        GL.Finish();
        GL.ReadPixels(x, y, width, height, PixelFormat.Rgb, PixelType.UnsignedByte, pixels);
        double sum = 0.0;
        byte minimum = byte.MaxValue;
        byte maximum = byte.MinValue;
        foreach (byte value in pixels)
        {
            sum += value;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        double average = sum / Math.Max(1, pixels.Length);
        if (average < 4.0 || maximum - minimum < 3)
            throw new InvalidOperationException(
                $"Deferred framebuffer is black or blank (average {average:0.00}, range {minimum}-{maximum}).");
        Console.WriteLine($"Deferred evidence: feature mask {deferred.DeferredFeatureMask}, " +
                          $"point lights {deferred.DeferredPointLightCount}, spot lights {deferred.DeferredSpotLightCount}, " +
                           $"shadow quality {_settings.DeferredShadowQuality}, " +
                           $"culling expansion {deferred.ViewCullingExpansion:0.0}x, " +
                           $"directional shadow draws {deferred.DirectionalShadowDrawCount}, " +
                           $"shadow depth {shadowMinimum:0.000000}-{shadowMaximum:0.000000}, " +
                           $"static/dynamic shadow updates {deferred.StaticShadowMapUpdateCount}/" +
                           $"{deferred.DynamicShadowMapUpdateCount}, Grid mesh uploads {deferred.PhysicsGridMeshUploadCount}, " +
                           $"chunks {deferred.VisibleChunkCount} visible/{deferred.CulledChunkCount} culled, " +
                          $"offscreen shadow casters {deferred.ShadowCasterChunkCount}, " +
                          $"debug view {_settings.DeferredDebugView}, " +
                          $"framebuffer average {average:0.00}, range {minimum}-{maximum}.");
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        if (_loaded && e.Width > 0 && e.Height > 0)
        {
            _renderer.Resize(e.Width, e.Height, _settings.RenderScale);
        }
    }

    protected override void OnFocusedChanged(FocusedChangedEventArgs e)
    {
        base.OnFocusedChanged(e);
        if (!_loaded) return;
        _ui.HandleWindowFocusChanged(e.IsFocused, GetNativeWindowHandle());
        if (!e.IsFocused && !_gpuSmoke && !_mainMenuVisible)
        {
            _pauseMenuVisible = true;
            SetMouseCaptured(false);
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_loaded) _ui.AddInputCharacter((uint)e.Unicode);
    }

    protected override void OnUnload()
    {
        if (_loaded)
        {
            if (!_gpuSmoke)
            {
                SaveWorld();
                SaveCurrentSettings();
            }
            _ui.Dispose();
            _renderer.Dispose();
            _physics.Dispose();
            _loaded = false;
        }
        base.OnUnload();
    }

    private static NativeWindowSettings CreateNativeWindowSettings(GameSettings settings) => new()
    {
        ClientSize = new Vector2i(settings.WindowWidth, settings.WindowHeight),
        Title = "RTX Sandbox — loading…",
        API = ContextAPI.OpenGL,
        APIVersion = new Version(4, 3),
        Profile = ContextProfile.Core,
        Flags = ContextFlags.ForwardCompatible,
        NumberOfSamples = 0,
    };

    private unsafe IntPtr GetNativeWindowHandle() =>
        OperatingSystem.IsWindows() ? GLFW.GetWin32Window(WindowPtr) : IntPtr.Zero;

    private void HandleSystemAndRenderSettings()
    {
        // ImGui captures the keyboard whenever a window is focused. That must not
        // disable global close shortcuts; only an active IME composition owns Escape.
        bool imeComposing = _loaded && _ui.IsImeCompositionActive;
        bool enteringText = _loaded && _ui.WantsTextEntry;
        bool simulateShortcut = _gpuSmoke && !_gpuSmokeShortcutTriggered &&
                                _gpuSmokeFrames >= 1 && !string.IsNullOrEmpty(_gpuSmokeShortcut);
        bool escapePressed = KeyboardState.IsKeyPressed(Keys.Escape) ||
                             (simulateShortcut && _gpuSmokeShortcut == "settings");
        if (!imeComposing && escapePressed)
        {
            if (simulateShortcut) _gpuSmokeShortcutTriggered = true;
            if (_worldCreationVisible)
            {
                _worldCreationVisible = false;
                _advancedWorldCreation = false;
            }
            else if (_inventoryVisible)
            {
                _inventoryVisible = false;
            }
            else if (_toolboxVisible)
            {
                _toolboxVisible = false;
            }
            else if (_settingsVisible)
            {
                _settingsVisible = false;
                if (_settingsOpenedFromPause) _pauseMenuVisible = true;
                _settingsOpenedFromPause = false;
            }
            else if (_modsVisible)
            {
                _modsVisible = false;
            }
            else if (_pauseMenuVisible)
            {
                if (SaveWorld())
                {
                    _pauseMenuVisible = false;
                }
            }
            else if (_mainMenuVisible)
            {
                // World selection is already the top-level screen.
            }
            else
            {
                _pauseMenuVisible = true;
            }
            SetMouseCaptured(!AnyInterfaceVisible());
        }

        bool inventoryTogglePressed = KeyboardState.IsKeyPressed(Keys.Tab) ||
                                      KeyboardState.IsKeyPressed(Keys.I) ||
                                      KeyboardState.IsKeyPressed(Keys.E) ||
                                      (simulateShortcut && _gpuSmokeShortcut == "inventory");
        if (!enteringText && !_mainMenuVisible && !_settingsVisible && !_worldCreationVisible &&
            inventoryTogglePressed)
        {
            if (simulateShortcut) _gpuSmokeShortcutTriggered = true;
            _inventoryVisible = !_inventoryVisible;
            if (_inventoryVisible) _toolboxVisible = false;
            SetMouseCaptured(!AnyInterfaceVisible());
        }

        if (!enteringText && !_mainMenuVisible && !_settingsVisible && !_worldCreationVisible && KeyboardState.IsKeyPressed(Keys.B))
        {
            _toolboxVisible = !_toolboxVisible;
            if (_toolboxVisible) _inventoryVisible = false;
            SetMouseCaptured(!AnyInterfaceVisible());
        }

        if (AnyInterfaceVisible())
        {
            return;
        }

        if (KeyboardState.IsKeyPressed(Keys.F1))
        {
            VSync = VSync == VSyncMode.On ? VSyncMode.Off : VSyncMode.On;
            _settings.VSync = VSync == VSyncMode.On;
        }

        if (KeyboardState.IsKeyPressed(Keys.F2))
        {
            int current = Array.FindIndex(QualityBounces, value => value == _settings.PathBounces);
            _settings.PathBounces = QualityBounces[(current + 1 + QualityBounces.Length) % QualityBounces.Length];
            _renderer.ResetAccumulation();
        }

        if (KeyboardState.IsKeyPressed(Keys.F3))
        {
            _debugVisible = !_debugVisible;
        }

        if (KeyboardState.IsKeyPressed(Keys.F5))
        {
            SaveWorld();
        }

        if (KeyboardState.IsKeyPressed(Keys.PageUp))
        {
            _settings.Exposure = Math.Clamp(_settings.Exposure + 0.1f, 0.35f, 3f);
            _renderer.ResetAccumulation();
        }
        if (KeyboardState.IsKeyPressed(Keys.PageDown))
        {
            _settings.Exposure = Math.Clamp(_settings.Exposure - 0.1f, 0.35f, 3f);
            _renderer.ResetAccumulation();
        }

        if (KeyboardState.IsKeyPressed(Keys.Home))
        {
            _camera.FieldOfViewDegrees = Math.Clamp(_camera.FieldOfViewDegrees - 5f, 45f, 105f);
            _renderer.ResetAccumulation();
        }
        if (KeyboardState.IsKeyPressed(Keys.End))
        {
            _camera.FieldOfViewDegrees = Math.Clamp(_camera.FieldOfViewDegrees + 5f, 45f, 105f);
            _renderer.ResetAccumulation();
        }
    }

    private void HandleTimeControls()
    {
        bool changed = false;
        if (KeyboardState.IsKeyPressed(Keys.T))
        {
            _clock.Paused = !_clock.Paused;
            changed = true;
        }
        if (KeyboardState.IsKeyPressed(Keys.Comma))
        {
            _clock.AdjustHours(-1f);
            changed = true;
        }
        if (KeyboardState.IsKeyPressed(Keys.Period))
        {
            _clock.AdjustHours(1f);
            changed = true;
        }
        if (KeyboardState.IsKeyPressed(Keys.R))
        {
            _clock.SetTime(12f);
            changed = true;
        }
        if (KeyboardState.IsKeyPressed(Keys.Minus))
        {
            _clock.MultiplySpeed(0.5f);
            changed = true;
        }
        if (KeyboardState.IsKeyPressed(Keys.Equal))
        {
            _clock.MultiplySpeed(2f);
            changed = true;
        }

        if (changed)
        {
            _lastAccumulatedTime = _clock.TimeOfDayHours;
            _renderer.ResetAccumulation();
        }
    }

    private void HandleEditorShortcuts()
    {
        if (KeyboardState.IsKeyPressed(Keys.Space) && !_player.NoClip)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = _lastSpacePressTimestamp == 0
                ? double.MaxValue
                : (now - _lastSpacePressTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed <= 0.32)
            {
                _player.FlightMode = !_player.FlightMode;
                _lastSpacePressTimestamp = 0;
            }
            else
            {
                _lastSpacePressTimestamp = now;
            }
        }

        if (KeyboardState.IsKeyPressed(Keys.N))
        {
            _player.NoClip = !_player.NoClip;
            if (_player.NoClip) _player.FlightMode = true;
        }

        bool control = KeyboardState.IsKeyDown(Keys.LeftControl) || KeyboardState.IsKeyDown(Keys.RightControl);
        bool changed = false;
        if (!control && KeyboardState.IsKeyPressed(Keys.V))
        {
            _microBuild.Enabled = !_microBuild.Enabled;
            ResetMining();
            UpdateSelection();
        }
        if (control && KeyboardState.IsKeyPressed(Keys.C)) _buildTools.CopySelection();
        if (control && KeyboardState.IsKeyPressed(Keys.V) && _selection.HasValue)
        {
            (int x, int y, int z) = _selection.Value.Adjacent;
            changed = _buildTools.Paste(new VoxelPoint(x, y, z));
        }
        if (control && KeyboardState.IsKeyPressed(Keys.F)) changed = _buildTools.FillSelection();
        if (control && KeyboardState.IsKeyPressed(Keys.Delete)) changed = _buildTools.FillSelection(0);
        if (KeyboardState.IsKeyPressed(Keys.X)) _buildTools.RotateClipboardY();
        if (KeyboardState.IsKeyPressed(Keys.M)) _buildTools.MirrorClipboardX();
        if (KeyboardState.IsKeyPressed(Keys.LeftBracket)) _buildTools.BrushRadius = Math.Max(1, _buildTools.BrushRadius - 1);
        if (KeyboardState.IsKeyPressed(Keys.RightBracket)) _buildTools.BrushRadius = Math.Min(8, _buildTools.BrushRadius + 1);
        if (changed)
        {
            _renderer.ResetAccumulation();
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        _regularSelection = VoxelRaycast.Cast(
            _world,
            _player.EyePosition,
            _camera.Forward,
            _settings.SelectionDistance);
        _microSelection = MicroVoxelRaycast.Cast(
            _world,
            _player.EyePosition,
            _camera.Forward,
            _settings.SelectionDistance);
        _gridSelection = _world.PhysicsGrids.Raycast(
            _player.EyePosition,
            _camera.Forward,
            _settings.SelectionDistance);

        _selection = _regularSelection;
        if (_microSelection.HasValue &&
            (!_regularSelection.HasValue || _microSelection.Value.Distance < _regularSelection.Value.Distance))
        {
            MicroVoxelHit micro = _microSelection.Value;
            _selection = new VoxelHit(
                micro.X,
                micro.Y,
                micro.Z,
                micro.Normal,
                micro.Distance,
                (VoxelMaterial)micro.MaterialId);
        }
        if (_gridSelection.HasValue &&
            (!_selection.HasValue || _gridSelection.Value.Distance < _selection.Value.Distance))
        {
            PhysicsGridRayHit grid = _gridSelection.Value;
            _selection = new VoxelHit(
                grid.CellX,
                grid.CellY,
                grid.CellZ,
                grid.Normal,
                grid.Distance,
                (VoxelMaterial)grid.MaterialId,
                grid.GridId);
        }
    }

    private PrecisionHit? CurrentPrecisionHit()
    {
        PrecisionHit? regular = _regularSelection.HasValue
            ? new PrecisionHit(
                _regularSelection.Value.X,
                _regularSelection.Value.Y,
                _regularSelection.Value.Z,
                _player.EyePosition + NumericsVector3.Normalize(_camera.Forward) * _regularSelection.Value.Distance,
                _regularSelection.Value.Normal,
                _regularSelection.Value.Distance,
                _regularSelection.Value.MaterialId,
                false)
            : null;
        if (!_microSelection.HasValue) return regular;
        if (regular.HasValue && regular.Value.Distance <= _microSelection.Value.Distance) return regular;
        MicroVoxelHit hit = _microSelection.Value;
        return new PrecisionHit(hit.X, hit.Y, hit.Z, hit.Position, hit.Normal, hit.Distance, hit.MaterialId, true);
    }

    private VoxelHit? RenderSelection()
    {
        if (_selection is { IsPhysicsGrid: true }) return _selection;
        if (!_microBuild.Enabled) return _selection;
        PrecisionHit? hit = CurrentPrecisionHit();
        return hit.HasValue
            ? new VoxelHit(hit.Value.X, hit.Value.Y, hit.Value.Z, hit.Value.Normal, hit.Value.Distance, (VoxelMaterial)hit.Value.MaterialId)
            : null;
    }

    private void UpdateMining(float deltaSeconds)
    {
        if (!MouseState.IsButtonDown(MouseButton.Left) || !_selection.HasValue)
        {
            ResetMining();
            return;
        }

        VoxelHit hit = _selection.Value;
        BlockDefinition definition = _blocks.Get(hit.MaterialId);
        if (definition.IsUnbreakable || definition.Id == 0)
        {
            ResetMining();
            return;
        }

        if (!_miningTarget.HasValue ||
            _miningTarget.Value.X != hit.X ||
            _miningTarget.Value.Y != hit.Y ||
            _miningTarget.Value.Z != hit.Z ||
            _miningTarget.Value.MaterialId != hit.MaterialId)
        {
            _miningTarget = hit;
            _miningProgress = 0f;
        }

        const float speed = 8f;
        _miningProgress += deltaSeconds * speed / Math.Max(0.05f, definition.HardnessSeconds);
        if (_miningProgress < 1f)
        {
            return;
        }

        BlockBreakEvent breakEvent = _modHost.Publish(new BlockBreakEvent(
            _world,
            hit.X,
            hit.Y,
            hit.Z,
            hit.MaterialId));
        if (breakEvent.IsCancelled || !_world.IsInside(breakEvent.X, breakEvent.Y, breakEvent.Z))
        {
            ResetMining();
            return;
        }

        ushort brokenId = _world.GetBlockId(breakEvent.X, breakEvent.Y, breakEvent.Z);
        BlockDefinition brokenDefinition = _blocks.Get(brokenId);
        if (brokenDefinition.IsUnbreakable || brokenId == 0)
        {
            ResetMining();
            return;
        }

        if (_world.SetBlockId(breakEvent.X, breakEvent.Y, breakEvent.Z, 0))
        {
            _modHost.Publish(new BlockBrokenEvent(
                _world,
                breakEvent.X,
                breakEvent.Y,
                breakEvent.Z,
                brokenId,
                brokenDefinition.DropItemKey));
        }
        ResetMining();
        UpdateSelection();
    }

    private void PlaceSelectedVoxel()
    {
        if (!_selection.HasValue)
        {
            return;
        }

        if (!TryGetSelectedBlock(out ItemStack heldStack, out BlockDefinition selectedBlock) || heldStack.IsEmpty)
        {
            return;
        }

        VoxelHit hit = _selection.Value;
        if (!PlacementRules.IsFarEnough(hit.Distance)) return;
        BlockDefinition hitDefinition = _blocks.Get(hit.MaterialId);
        (int x, int y, int z) = hitDefinition.IsReplaceable && hit.MaterialId != selectedBlock.Id
            ? (hit.X, hit.Y, hit.Z)
            : hit.Adjacent;
        ushort existing = _world.GetBlockId(x, y, z);
        BlockPlaceEvent placeEvent = _modHost.Publish(new BlockPlaceEvent(
            _world,
            x,
            y,
            z,
            existing,
            selectedBlock.Id));
        if (placeEvent.IsCancelled || !_world.IsInside(placeEvent.X, placeEvent.Y, placeEvent.Z))
        {
            return;
        }

        x = placeEvent.X;
        y = placeEvent.Y;
        z = placeEvent.Z;
        existing = _world.GetBlockId(x, y, z);
        if ((existing != 0 && !_blocks.Get(existing).IsReplaceable) ||
            _player.WouldIntersectBlock(x, y, z))
        {
            return;
        }

        if (_world.SetBlockId(x, y, z, placeEvent.NewBlockId))
        {
            _progression.NotifyBlockPlaced(_blocks.Get(placeEvent.NewBlockId));
            UpdateSelection();
        }
    }

    private void SelectMaterialFromInputs()
    {
        ushort previous = _buildTools.SelectedBlockId;
        if (KeyboardState.IsKeyPressed(Keys.D1)) SelectQuickbarSlot(0);
        if (KeyboardState.IsKeyPressed(Keys.D2)) SelectQuickbarSlot(1);
        if (KeyboardState.IsKeyPressed(Keys.D3)) SelectQuickbarSlot(2);
        if (KeyboardState.IsKeyPressed(Keys.D4)) SelectQuickbarSlot(3);
        if (KeyboardState.IsKeyPressed(Keys.D5)) SelectQuickbarSlot(4);
        if (KeyboardState.IsKeyPressed(Keys.D6)) SelectQuickbarSlot(5);
        if (KeyboardState.IsKeyPressed(Keys.D7)) SelectQuickbarSlot(6);
        if (KeyboardState.IsKeyPressed(Keys.D8)) SelectQuickbarSlot(7);
        if (KeyboardState.IsKeyPressed(Keys.D9)) SelectQuickbarSlot(8);
        if (KeyboardState.IsKeyPressed(Keys.Q)) CycleQuickbar(-1);

        float scroll = MouseState.ScrollDelta.Y;
        if (_mouseCaptured && MathF.Abs(scroll) > 0.01f)
        {
            bool alt = KeyboardState.IsKeyDown(Keys.LeftAlt) || KeyboardState.IsKeyDown(Keys.RightAlt);
            if (alt)
            {
                _microBuild.CycleGrid(scroll > 0f ? -1 : 1);
                _settings.PrecisionGridDivisor = _microBuild.GridDivisor;
            }
            else
            {
                CycleQuickbar(scroll > 0f ? -1 : 1);
            }
        }

        if (previous != _buildTools.SelectedBlockId)
        {
            _renderer.ResetAccumulation();
        }
    }

    private void CycleQuickbar(int direction)
    {
        _creativeHotbar.Cycle(direction);
        _buildTools.SelectedBlockId = _creativeHotbar.SelectedBlock.Id;
    }

    private void SelectQuickbarSlot(int index)
    {
        _creativeHotbar.Select(index);
        _buildTools.SelectedBlockId = _creativeHotbar.SelectedBlock.Id;
        RememberRecent(_creativeHotbar.SelectedBlock.Key);
    }

    private void AssignCreativeBlock(ushort blockId)
    {
        BlockDefinition block = _blocks.Get(blockId);
        if (!block.IsPlaceable || block.Id == BlockRegistry.UnknownBlockId) return;
        _creativeHotbar.Assign(_creativeHotbar.SelectedSlot, block.Id);
        _buildTools.SelectedBlockId = block.Id;
        RememberRecent(block.Key);
        _renderer.ResetAccumulation();
    }

    private void RememberRecent(string key)
    {
        _recentBlocks.RemoveAll(existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase));
        _recentBlocks.Insert(0, key);
        if (_recentBlocks.Count > 24) _recentBlocks.RemoveRange(24, _recentBlocks.Count - 24);
    }

    private bool TryGetSelectedBlock(out ItemStack stack, out BlockDefinition block)
    {
        stack = _inventory.GetSlot(_inventory.SelectedHotbarSlot);
        if (stack.IsEmpty ||
            !_items.TryGet(stack.ItemKey!, out ItemDefinition? item) ||
            string.IsNullOrWhiteSpace(item!.PlaceBlockKey) ||
            !_blocks.TryGet(item.PlaceBlockKey, out BlockDefinition? definition))
        {
            block = _blocks.Get(BlockRegistry.UnknownBlockId);
            return false;
        }
        block = definition!;
        return true;
    }

    private float MiningSpeedFor(BlockDefinition block)
    {
        ItemStack stack = _inventory.GetSlot(_inventory.SelectedHotbarSlot);
        if (stack.IsEmpty || !_items.TryGet(stack.ItemKey!, out ItemDefinition? item) ||
            string.IsNullOrWhiteSpace(item!.ToolType))
        {
            return block.RequiredToolTier > 0 ? 0.35f : 1f;
        }

        bool preferred = string.IsNullOrWhiteSpace(block.PreferredToolType) ||
                         string.Equals(block.PreferredToolType, item.ToolType, StringComparison.OrdinalIgnoreCase);
        float speed = preferred ? item.MiningSpeedMultiplier : 1f;
        if (item.ToolTier < block.RequiredToolTier)
        {
            speed *= 0.25f;
        }
        return Math.Max(0.1f, speed);
    }

    private bool CanHarvest(BlockDefinition block)
    {
        if (block.RequiredToolTier <= 0)
        {
            return true;
        }
        ItemStack stack = _inventory.GetSlot(_inventory.SelectedHotbarSlot);
        return !stack.IsEmpty &&
               _items.TryGet(stack.ItemKey!, out ItemDefinition? item) &&
               item!.ToolTier >= block.RequiredToolTier &&
               (string.IsNullOrWhiteSpace(block.PreferredToolType) ||
                string.Equals(block.PreferredToolType, item.ToolType, StringComparison.OrdinalIgnoreCase));
    }

    private void ResetMining()
    {
        _miningTarget = null;
        _miningProgress = 0f;
    }

    private void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;
        CursorState = captured
            ? OpenTK.Windowing.Common.CursorState.Grabbed
            : OpenTK.Windowing.Common.CursorState.Normal;
    }

    private bool AnyInterfaceVisible() =>
        _mainMenuVisible || _pauseMenuVisible || _settingsVisible || _modsVisible || _inventoryVisible || _toolboxVisible || _worldCreationVisible;

    private void DrawHud()
    {
        if (_mainMenuVisible) return;
        if (_debugVisible)
        {
            ImGui.SetNextWindowPos(new NumericsVector2(12f, 12f), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.58f);
            ImGui.Begin(
                "##debug-hud",
                ImGuiWindowFlags.NoDecoration |
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoInputs |
                ImGuiWindowFlags.NoSavedSettings);
            ImGui.Text($"RTX Sandbox | {ImGui.GetIO().Framerate:0} FPS");
            ImGui.Text($"XYZ {_player.Position.X:0.0} / {_player.Position.Y:0.0} / {_player.Position.Z:0.0}");
            ImGui.Text($"渲染器 {_renderer.Name} | {_renderer.AccumulatedSamples} samples");
            ImGui.Text($"鏡頭 {(_player.NoClip ? "自由鏡頭" : _player.FlightMode ? "飛行（碰撞）" : "行走（碰撞）")}");
            ImGui.Text($"{_physics.BackendName} | Grid {_physics.ActiveGridCount} | " +
                       $"碰撞步 {_physics.SimulationSubstepsLastStep} | 時間 {_clock.DisplayTime}");
            ImGui.Text($"世界 {_world.GenerationSettings.Preset} | 種子 {_world.Seed} | Chunk {_world.LoadedChunkCount}");
            if (_microBuild.Enabled)
                ImGui.Text($"精密網格 1/{_microBuild.GridDivisor} | {_world.MicroVoxelContainerCount:N0} cells");
            ImGui.End();
        }

        ImGui.SetNextWindowPos(
            new NumericsVector2(ClientSize.X * 0.5f, ClientSize.Y - 14f),
            ImGuiCond.Always,
            new NumericsVector2(0.5f, 1f));
        ImGui.SetNextWindowBgAlpha(0.58f);
        ImGui.Begin(
            "##hotbar",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoSavedSettings);
        for (int slot = 0; slot < CreativeHotbar.SlotCount; slot++)
        {
            if (slot > 0) ImGui.SameLine();
            BlockDefinition block = _creativeHotbar.GetBlock(slot);
            DrawBlockCard(block, new NumericsVector2(58f, 61f), $"hud-slot-{slot}",
                selected: slot == _creativeHotbar.SelectedSlot,
                label: (slot + 1).ToString(),
                interactive: false,
                compact: true);
        }
        ImGui.End();

        DrawCrosshair();
    }

    private bool DrawBlockCard(
        BlockDefinition block,
        NumericsVector2 size,
        string id,
        bool selected,
        string? label = null,
        bool interactive = true,
        bool compact = false)
    {
        NumericsVector2 position = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##{id}", size);
        bool hovered = interactive && ImGui.IsItemHovered();
        bool clicked = interactive && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        System.Numerics.Vector4 background = selected
            ? new System.Numerics.Vector4(0.62f, 0.62f, 0.62f, 1f)
            : hovered
                ? new System.Numerics.Vector4(0.34f, 0.34f, 0.34f, 1f)
                : new System.Numerics.Vector4(0.20f, 0.20f, 0.20f, 1f);
        uint backgroundColor = ImGui.ColorConvertFloat4ToU32(background);
        uint borderColor = ImGui.ColorConvertFloat4ToU32(selected
            ? new System.Numerics.Vector4(0.96f, 0.96f, 0.96f, 1f)
            : new System.Numerics.Vector4(0.06f, 0.06f, 0.06f, 1f));
        drawList.AddRectFilled(position, position + size, backgroundColor, 1f);
        drawList.AddRect(position, position + size, borderColor, 1f, ImDrawFlags.None, selected ? 3f : 2f);

        float labelHeight = compact ? 13f : 23f;
        DrawMaterialCube(drawList, position + new NumericsVector2(4f, 3f),
            new NumericsVector2(size.X - 8f, size.Y - labelHeight - 4f), block);

        string text = label ?? ShortName(block.DisplayName, compact ? 8 : 14);
        NumericsVector2 textSize = ImGui.CalcTextSize(text);
        NumericsVector2 textPosition = new(
            position.X + Math.Max(3f, (size.X - textSize.X) * 0.5f),
            position.Y + size.Y - textSize.Y - (compact ? 2f : 5f));
        uint textColor = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.94f, 0.97f, 1f, 1f));
        drawList.AddText(textPosition, textColor, text);

        if (_favoriteBlocks.Contains(block.Key) && !compact)
        {
            uint star = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 0.78f, 0.18f, 1f));
            drawList.AddText(position + new NumericsVector2(size.X - 17f, 4f), star, "*");
        }

        if (interactive && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            if (!_favoriteBlocks.Add(block.Key)) _favoriteBlocks.Remove(block.Key);
        }

        if (hovered)
        {
            DrawBlockTooltip(block);
        }
        return clicked;
    }

    private void DrawMaterialCube(
        ImDrawListPtr drawList,
        NumericsVector2 position,
        NumericsVector2 size,
        BlockDefinition block)
    {
        float halfWidth = MathF.Min(size.X * 0.42f, size.Y * 0.46f);
        float topHeight = halfWidth * 0.56f;
        float sideHeight = MathF.Min(size.Y - topHeight - 1f, halfWidth * 0.98f);
        float centerX = position.X + size.X * 0.5f;
        float topY = position.Y + Math.Max(1f, (size.Y - topHeight - sideHeight) * 0.42f);
        NumericsVector2 top = new(centerX, topY);
        NumericsVector2 right = new(centerX + halfWidth, topY + topHeight);
        NumericsVector2 middle = new(centerX, topY + topHeight * 2f);
        NumericsVector2 left = new(centerX - halfWidth, topY + topHeight);
        NumericsVector2 rightBottom = right + new NumericsVector2(0f, sideHeight);
        NumericsVector2 middleBottom = middle + new NumericsVector2(0f, sideHeight);
        NumericsVector2 leftBottom = left + new NumericsVector2(0f, sideHeight);

        if (block.Emission.LengthSquared() > 0.04f)
        {
            System.Numerics.Vector3 glowVector = System.Numerics.Vector3.Clamp(block.Emission, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);
            uint glow = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(glowVector, 0.22f));
            drawList.AddCircleFilled(new NumericsVector2(centerX, topY + topHeight + sideHeight * 0.45f), halfWidth * 1.1f, glow, 24);
        }

        MaterialTileUv topUv = _renderer.GetMaterialTileUv(block.Id, Rendering.MaterialFace.Top);
        MaterialTileUv sideUv = _renderer.GetMaterialTileUv(block.Id, Rendering.MaterialFace.Side);
        nint texture = (nint)_renderer.MaterialAtlasTexture;
        float alpha = block.Transmission > 0f ? 0.78f : 1f;
        uint leftTint = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.72f, 0.76f, 0.82f, alpha));
        uint rightTint = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.55f, 0.6f, 0.69f, alpha));
        uint topTint = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 1f, 1f, alpha));

        AddTexturedQuad(drawList, texture, left, middle, middleBottom, leftBottom, sideUv, leftTint, flipX: true);
        AddTexturedQuad(drawList, texture, middle, right, rightBottom, middleBottom, sideUv, rightTint);
        AddTexturedQuad(drawList, texture, top, right, middle, left, topUv, topTint);

        uint outline = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.86f, 0.9f, 0.96f, 0.72f));
        drawList.AddLine(top, right, outline, 1f);
        drawList.AddLine(right, rightBottom, outline, 1f);
        drawList.AddLine(rightBottom, middleBottom, outline, 1f);
        drawList.AddLine(middleBottom, leftBottom, outline, 1f);
        drawList.AddLine(leftBottom, left, outline, 1f);
        drawList.AddLine(left, top, outline, 1f);
        drawList.AddLine(left, middle, outline, 1f);
        drawList.AddLine(middle, right, outline, 1f);
        drawList.AddLine(middle, middleBottom, outline, 1f);
    }

    private static void AddTexturedQuad(
        ImDrawListPtr drawList,
        nint texture,
        NumericsVector2 a,
        NumericsVector2 b,
        NumericsVector2 c,
        NumericsVector2 d,
        MaterialTileUv uv,
        uint tint,
        bool flipX = false)
    {
        NumericsVector2 uvA = flipX ? new NumericsVector2(uv.Maximum.X, uv.Minimum.Y) : uv.Minimum;
        NumericsVector2 uvB = flipX ? uv.Minimum : new NumericsVector2(uv.Maximum.X, uv.Minimum.Y);
        NumericsVector2 uvC = flipX ? new NumericsVector2(uv.Minimum.X, uv.Maximum.Y) : uv.Maximum;
        NumericsVector2 uvD = flipX ? uv.Maximum : new NumericsVector2(uv.Minimum.X, uv.Maximum.Y);
        drawList.AddImageQuad(texture, a, b, c, d, uvA, uvB, uvC, uvD, tint);
    }

    private void DrawBlockTooltip(BlockDefinition block)
    {
        ImGui.BeginTooltip();
        ImGui.Text(BlockDisplayName(block));
        if (!string.Equals(BlockDisplayName(block), block.DisplayName, StringComparison.Ordinal))
            ImGui.TextDisabled(block.DisplayName);
        ImGui.TextDisabled(block.Key);
        ImGui.Text($"{CategoryDisplayName(TopLevelCategory(block.Category))} | ID {block.Id}");
        ImGui.Text($"16×16 {block.TextureGenerator} | 變化量 {(block.TextureVariation < 0f ? "自動" : block.TextureVariation.ToString("0.00"))}");
        ImGui.Text($"金屬度 {block.Metallic:0.00} | 粗糙度 {block.Roughness:0.00}");
        if (block.Transmission > 0f)
            ImGui.Text($"透射 {block.Transmission:0.00} | 折射率 {block.IndexOfRefraction:0.00}");
        if (block.Emission.LengthSquared() > 0f)
            ImGui.Text($"自發光 {block.Emission.X:0.0}, {block.Emission.Y:0.0}, {block.Emission.Z:0.0}");
        if (block.Tags.Count > 0) ImGui.TextWrapped($"標籤：{string.Join(", ", block.Tags)}");
        ImGui.TextDisabled("左鍵：放入目前快捷格　右鍵：加入／移除收藏");
        ImGui.EndTooltip();
    }

    private void DrawCrosshair()
    {
        if (!_settings.ShowCrosshair || AnyInterfaceVisible()) return;

        NumericsVector2 center = new(ClientSize.X * 0.5f, ClientSize.Y * 0.5f);
        float size = _settings.CrosshairSize;
        float gap = MathF.Max(2f, size * 0.28f);
        System.Numerics.Vector4 color = CrosshairColor();
        color.W = _settings.CrosshairOpacity;

        ImDrawListPtr foreground = ImGui.GetForegroundDrawList();
        uint outline = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0f, 0f, 0f, _settings.CrosshairOpacity * 0.88f));
        uint foregroundColor = ImGui.ColorConvertFloat4ToU32(color);
        NumericsVector2[] starts =
        {
            center - new NumericsVector2(size, 0f),
            center + new NumericsVector2(gap, 0f),
            center - new NumericsVector2(0f, size),
            center + new NumericsVector2(0f, gap),
        };
        NumericsVector2[] ends =
        {
            center - new NumericsVector2(gap, 0f),
            center + new NumericsVector2(size, 0f),
            center - new NumericsVector2(0f, gap),
            center + new NumericsVector2(0f, size),
        };
        for (int index = 0; index < starts.Length; index++)
        {
            foreground.AddLine(starts[index], ends[index], outline, 3.5f);
            foreground.AddLine(starts[index], ends[index], foregroundColor, 1.6f);
        }
        foreground.AddCircleFilled(center, 1.65f, outline, 12);
        foreground.AddCircleFilled(center, 0.85f, foregroundColor, 12);
    }

    private System.Numerics.Vector4 CrosshairColor()
    {
        if (!_settings.ContextualCrosshairColor)
            return new System.Numerics.Vector4(0.96f, 0.98f, 1f, 1f);
        if (!_selection.HasValue)
            return new System.Numerics.Vector4(0.62f, 0.67f, 0.72f, 1f);
        if (MouseState.IsButtonDown(MouseButton.Right))
            return (_gridSelection.HasValue
                    ? _physics.CanPlaceOnGrid(_gridSelection.Value, _buildTools.SelectedBlockId)
                    : CanQuickPlaceAtSelection())
                ? new System.Numerics.Vector4(0.34f, 0.93f, 0.56f, 1f)
                : new System.Numerics.Vector4(1f, 0.28f, 0.24f, 1f);
        return _buildTools.ToolMode switch
        {
            EditorToolMode.SelectA or EditorToolMode.SelectB =>
                new System.Numerics.Vector4(0.30f, 0.82f, 1f, 1f),
            EditorToolMode.Pick => new System.Numerics.Vector4(0.72f, 0.48f, 1f, 1f),
            EditorToolMode.BrushAdd => new System.Numerics.Vector4(0.34f, 0.93f, 0.56f, 1f),
            EditorToolMode.GridAssign => new System.Numerics.Vector4(0.35f, 0.66f, 1f, 1f),
            EditorToolMode.Erase or EditorToolMode.BrushErase or EditorToolMode.Explode =>
                new System.Numerics.Vector4(1f, 0.36f, 0.24f, 1f),
            _ => _blocks.Get(_selection.Value.MaterialId).IsUnbreakable
                ? new System.Numerics.Vector4(1f, 0.28f, 0.24f, 1f)
                : new System.Numerics.Vector4(1f, 0.72f, 0.24f, 1f),
        };
    }

    private bool CanQuickPlaceAtSelection()
    {
        if (_microBuild.Enabled)
        {
            PrecisionHit? precision = CurrentPrecisionHit();
            return precision.HasValue && PlacementRules.IsFarEnough(precision.Value.Distance);
        }
        if (!_selection.HasValue) return false;
        VoxelHit hit = _selection.Value;
        if (!PlacementRules.IsFarEnough(hit.Distance)) return false;
        BlockDefinition hitDefinition = _blocks.Get(hit.MaterialId);
        (int x, int y, int z) = hitDefinition.IsReplaceable && hit.MaterialId != _buildTools.SelectedBlockId
            ? (hit.X, hit.Y, hit.Z)
            : hit.Adjacent;
        if (!_world.IsInside(x, y, z) || _player.WouldIntersectBlock(x, y, z)) return false;
        ushort existing = _world.GetBlockId(x, y, z);
        return existing == 0 || _blocks.Get(existing).IsReplaceable;
    }

    private void DrawDroppedItemMarkers()
    {
        if (_droppedItems.Items.Count == 0)
        {
            return;
        }

        NumericsVector3 forward = _camera.Forward;
        NumericsVector3 right = NumericsVector3.Normalize(NumericsVector3.Cross(forward, NumericsVector3.UnitY));
        NumericsVector3 up = NumericsVector3.Normalize(NumericsVector3.Cross(right, forward));
        float tanHalfFov = MathF.Tan(_camera.FieldOfViewDegrees * MathF.PI / 360f);
        float aspect = Math.Max(ClientSize.X, 1) / (float)Math.Max(ClientSize.Y, 1);
        ImDrawListPtr drawList = ImGui.GetForegroundDrawList();

        foreach (DroppedItem drop in _droppedItems.Items.Take(48))
        {
            NumericsVector3 relative = drop.Position - _player.EyePosition;
            float depth = NumericsVector3.Dot(relative, forward);
            if (depth <= 0.1f || depth > 32f)
            {
                continue;
            }

            float ndcX = NumericsVector3.Dot(relative, right) / (depth * tanHalfFov * aspect);
            float ndcY = NumericsVector3.Dot(relative, up) / (depth * tanHalfFov);
            if (MathF.Abs(ndcX) > 1.05f || MathF.Abs(ndcY) > 1.05f)
            {
                continue;
            }

            var screen = new NumericsVector2(
                (ndcX * 0.5f + 0.5f) * ClientSize.X,
                (0.5f - ndcY * 0.5f) * ClientSize.Y);
            float radius = Math.Clamp(15f / MathF.Sqrt(depth), 4f, 10f);
            System.Numerics.Vector4 tint = _items.TryGet(drop.ItemKey, out ItemDefinition? item)
                ? new System.Numerics.Vector4(item!.IconTint, 0.92f)
                : new System.Numerics.Vector4(1f, 0.2f, 1f, 0.92f);
            uint fill = ImGui.ColorConvertFloat4ToU32(tint);
            uint outline = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 1f, 1f, 0.9f));
            drawList.AddRectFilled(
                screen - new NumericsVector2(radius),
                screen + new NumericsVector2(radius),
                fill,
                2f);
            drawList.AddRect(
                screen - new NumericsVector2(radius),
                screen + new NumericsVector2(radius),
                outline,
                2f,
                ImDrawFlags.None,
                1.5f);
            if (drop.Count > 1)
            {
                drawList.AddText(
                    screen + new NumericsVector2(radius + 2f, -radius),
                    outline,
                    $"x{drop.Count}");
            }
        }
    }

    private void DrawCreativeInventoryInterface()
    {
        float width = Math.Clamp(ClientSize.X - 44f, 610f, 1080f);
        float height = Math.Clamp(ClientSize.Y - 54f, 430f, 720f);
        ImGui.SetNextWindowPos(
            new NumericsVector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f),
            ImGuiCond.Always,
            new NumericsVector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new NumericsVector2(width, height), ImGuiCond.Always);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NumericsVector2(14f, 14f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new NumericsVector2(8f, 8f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0.36f, 0.36f, 0.36f, 0.99f));
        ImGui.PushStyleColor(ImGuiCol.Border, new System.Numerics.Vector4(0.08f, 0.08f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new System.Numerics.Vector4(0.24f, 0.24f, 0.24f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new System.Numerics.Vector4(0.17f, 0.17f, 0.17f, 1f));
        bool open = _inventoryVisible;
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                 ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;
        if (ImGui.Begin("##creative-inventory", ref open, flags))
        {
            const string inventoryTitle = "方塊與物品";
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - ImGui.CalcTextSize(inventoryTitle).X) * 0.5f);
            ImGui.Text(inventoryTitle);
            ImGui.Separator();

            IReadOnlyList<BlockDefinition> palette = _blocks.PlaceableDefinitions();
            for (int slot = 0; slot < CreativeHotbar.SlotCount; slot++)
            {
                if (slot > 0) ImGui.SameLine();
                BlockDefinition quickBlock = _creativeHotbar.GetBlock(slot);
                if (DrawBlockCard(
                        quickBlock,
                        new NumericsVector2(76f, 76f),
                        $"standalone-quickbar-{slot}",
                        selected: slot == _creativeHotbar.SelectedSlot,
                        label: (slot + 1).ToString(),
                        compact: true))
                {
                    SelectQuickbarSlot(slot);
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.SetNextItemWidth(Math.Min(330f, width * 0.32f));
            ImGui.InputText("搜尋", ref _paletteSearch, 128);

            string[] categories = new[] { "All" }
                .Concat(palette.Select(block => TopLevelCategory(block.Category)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category == "All" ? string.Empty : category, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int categoryIndex = Math.Max(0, Array.FindIndex(categories,
                category => string.Equals(category, _paletteCategory, StringComparison.OrdinalIgnoreCase)));
            string[] categoryLabels = categories.Select(CategoryDisplayName).ToArray();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Min(210f, width * 0.22f));
            if (ImGui.Combo("分類", ref categoryIndex, categoryLabels, categoryLabels.Length))
                _paletteCategory = categories[categoryIndex];
            ImGui.SameLine();
            ImGui.Checkbox("只看收藏", ref _favoritesOnly);
            ImGui.SameLine();
            if (ImGui.Button("建造工具  B"))
            {
                _toolboxVisible = true;
                open = false;
            }

            BlockDefinition[] visibleBlocks = palette
                .Where(BlockMatchesPaletteFilter)
                .Where(block => !_favoritesOnly || _favoriteBlocks.Contains(block.Key))
                .ToArray();
            ImGui.BeginChild("StandalonePalette", new NumericsVector2(0f, -6f), ImGuiChildFlags.Borders);
            int columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / 112f));
            for (int index = 0; index < visibleBlocks.Length; index++)
            {
                BlockDefinition block = visibleBlocks[index];
                ImGui.PushID($"standalone:{block.Key}");
                if (DrawBlockCard(
                        block,
                        new NumericsVector2(104f, 102f),
                        $"standalone-material-{block.Key}",
                        block.Id == _buildTools.SelectedBlockId,
                        ShortName(BlockDisplayName(block), 14)))
                {
                    AssignCreativeBlock(block.Id);
                }
                ImGui.PopID();
                if ((index + 1) % columns != 0) ImGui.SameLine();
            }
            ImGui.EndChild();
        }
        ImGui.End();
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(4);

        if (!open)
        {
            _inventoryVisible = false;
            SetMouseCaptured(!AnyInterfaceVisible());
        }
    }

    private void DrawBuildToolboxInterface()
    {
        ImGui.SetNextWindowPos(new NumericsVector2(ClientSize.X - 22f, 22f), ImGuiCond.Always, new NumericsVector2(1f, 0f));
        ImGui.SetNextWindowSize(new NumericsVector2(Math.Min(430f, ClientSize.X - 44f), Math.Min(650f, ClientSize.Y - 44f)), ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0.025f, 0.045f, 0.068f, 0.98f));
        bool open = _toolboxVisible;
        if (ImGui.Begin("建造工具##standalone-tools", ref open,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.35f, 0.84f, 1f, 1f), "基本操作");
            ImGui.TextWrapped("左鍵破壞 · 右鍵放置 · 中鍵吸取材質。進階工具啟用時，左鍵改為使用該工具。");
            if (ImGui.Button("開啟方塊庫  E", new NumericsVector2(180f, 34f)))
            {
                _inventoryVisible = true;
                open = false;
            }

            ImGui.Separator();
            bool precision = _microBuild.Enabled;
            if (ImGui.Checkbox("精密微方塊模式  V", ref precision))
            {
                _microBuild.Enabled = precision;
                UpdateSelection();
            }
            int gridIndex = _microBuild.SupportedDivisors.ToList().IndexOf(_microBuild.GridDivisor);
            string[] gridLabels = _microBuild.SupportedDivisors.Select(value => $"1/{value}").ToArray();
            if (ImGui.Combo("微方塊精度", ref gridIndex, gridLabels, gridLabels.Length) && gridIndex >= 0)
            {
                _microBuild.SetGridDivisor(_microBuild.SupportedDivisors[gridIndex]);
                _settings.PrecisionGridDivisor = _microBuild.GridDivisor;
            }
            ImGui.TextDisabled("Alt + 滾輪也能快速切換精度");

            ImGui.Separator();
            ImGui.TextColored(new System.Numerics.Vector4(0.35f, 0.84f, 1f, 1f), "進階左鍵工具");
            EditorToolMode[] tools =
            {
                EditorToolMode.Place,
                EditorToolMode.Replace,
                EditorToolMode.Pick,
                EditorToolMode.BrushAdd,
                EditorToolMode.BrushErase,
                EditorToolMode.Flatten,
                EditorToolMode.Explode,
                EditorToolMode.GridAssign,
                EditorToolMode.SelectA,
                EditorToolMode.SelectB,
            };
            for (int index = 0; index < tools.Length; index++)
            {
                EditorToolMode mode = tools[index];
                bool selected = _buildTools.ToolMode == mode;
                if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.08f, 0.58f, 0.80f, 1f));
                if (ImGui.Button(ToolLabel(mode), new NumericsVector2(124f, 39f))) _buildTools.ToolMode = mode;
                if (selected) ImGui.PopStyleColor();
                if ((index + 1) % 3 != 0) ImGui.SameLine();
            }

            int brushRadius = _buildTools.BrushRadius;
            if (ImGui.SliderInt("筆刷半徑", ref brushRadius, 1, 8)) _buildTools.BrushRadius = brushRadius;
            int explosionRadius = _buildTools.ExplosionRadius;
            if (ImGui.SliderInt("爆炸半徑", ref explosionRadius, 1, 12)) _buildTools.ExplosionRadius = explosionRadius;

            ImGui.Separator();
            ImGui.Text($"選區 A：{_buildTools.SelectionA?.ToString() ?? "未設定"}");
            ImGui.Text($"選區 B：{_buildTools.SelectionB?.ToString() ?? "未設定"}");
            ImGui.TextDisabled($"體積 {_buildTools.SelectionVolume:N0} · 剪貼簿 {_buildTools.ClipboardDescription}");
            bool hasRegion = _buildTools.SelectionVolume > 0;
            if (!hasRegion) ImGui.BeginDisabled();
            if (ImGui.Button("填滿材質")) ApplyEditorChange(() => _buildTools.FillSelection());
            ImGui.SameLine();
            if (ImGui.Button("清空選區")) ApplyEditorChange(() => _buildTools.FillSelection(0));
            ImGui.SameLine();
            if (ImGui.Button("複製")) _buildTools.CopySelection();
            if (!hasRegion) ImGui.EndDisabled();

            ImGui.TextDisabled(_microBuild.Enabled ? _microBuild.Status : _buildTools.Status);

            ImGui.Separator();
            if (ImGui.Button("建立／重新生成世界", new NumericsVector2(205f, 34f)))
            {
                _worldDraft = _world.GenerationSettings.Clone();
                _worldNameDraft = NextWorldName(_knownWorlds);
                _worldCreationStatus = string.Empty;
                _advancedWorldCreation = false;
                _worldCreationVisible = true;
                open = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("立即保存")) SaveWorld();
        }
        ImGui.End();
        ImGui.PopStyleColor();

        if (!open)
        {
            _toolboxVisible = false;
            SetMouseCaptured(!AnyInterfaceVisible());
        }
    }

    private void DrawInventoryInterface()
    {
        ImGui.SetNextWindowPos(
            new NumericsVector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f),
            ImGuiCond.FirstUseEver,
            new NumericsVector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new NumericsVector2(900f, 560f), ImGuiCond.FirstUseEver);

        bool open = _inventoryVisible;
        if (ImGui.Begin("RTX Sandbox Editor", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled($"{_blocks.PlaceableDefinitions().Count} materials | {_toolRegistry.Definitions.Count} mod tools | Tab or I closes editor");
            if (ImGui.BeginTabBar("EditorTabs"))
            {
                if (ImGui.BeginTabItem("Blocks"))
                {
                    ImGui.TextWrapped("Creative Library: every material is unlimited. Click a cube to put it in the active quickbar slot; right click toggles a favorite.");
                    ImGui.TextDisabled("Quickbar: number keys select | mouse wheel cycles | middle click samples | E, I or Tab opens this library");
                    ImGui.Separator();
                    IReadOnlyList<BlockDefinition> palette = _blocks.PlaceableDefinitions();

                    ImGui.Text("Quickbar");
                    for (int slot = 0; slot < CreativeHotbar.SlotCount; slot++)
                    {
                        if (slot > 0) ImGui.SameLine();
                        BlockDefinition quickBlock = _creativeHotbar.GetBlock(slot);
                        if (DrawBlockCard(
                                quickBlock,
                                new NumericsVector2(76f, 76f),
                                $"library-quickbar-{slot}",
                                selected: slot == _creativeHotbar.SelectedSlot,
                                label: (slot + 1).ToString(),
                                compact: true))
                        {
                            SelectQuickbarSlot(slot);
                        }
                    }

                    if (_recentBlocks.Count > 0)
                    {
                        ImGui.Text("Recent");
                        int recentIndex = 0;
                        foreach (string key in _recentBlocks.Take(9).ToArray())
                        {
                            if (!_blocks.TryGet(key, out BlockDefinition? recentBlock) || !recentBlock!.IsPlaceable) continue;
                            if (recentIndex++ > 0) ImGui.SameLine();
                            if (DrawBlockCard(
                                    recentBlock,
                                    new NumericsVector2(68f, 68f),
                                    $"recent-{recentBlock.Key}",
                                    selected: recentBlock.Id == _buildTools.SelectedBlockId,
                                    label: string.Empty,
                                    compact: true))
                            {
                                AssignCreativeBlock(recentBlock.Id);
                            }
                        }
                    }

                    ImGui.Separator();
                    ImGui.SetNextItemWidth(330f);
                    ImGui.InputText("Search", ref _paletteSearch, 128);
                    string[] categories = new[] { "All" }
                        .Concat(palette.Select(block => TopLevelCategory(block.Category)))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(category => category == "All" ? string.Empty : category, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    int categoryIndex = Math.Max(0, Array.FindIndex(categories,
                        category => string.Equals(category, _paletteCategory, StringComparison.OrdinalIgnoreCase)));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(220f);
                    if (ImGui.Combo("Category", ref categoryIndex, categories, categories.Length))
                        _paletteCategory = categories[categoryIndex];
                    ImGui.SameLine();
                    ImGui.Checkbox("Favorites", ref _favoritesOnly);

                    BlockDefinition[] visibleBlocks = palette
                        .Where(BlockMatchesPaletteFilter)
                        .Where(block => !_favoritesOnly || _favoriteBlocks.Contains(block.Key))
                        .ToArray();
                    ImGui.TextDisabled($"Showing {visibleBlocks.Length} of {palette.Count} materials | procedural {_renderer.MaterialTextureResolution} x {_renderer.MaterialTextureResolution} PBR textures");
                    ImGui.BeginChild("BlockPaletteScroll", new NumericsVector2(0f, -4f), ImGuiChildFlags.Borders);
                    int columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / 112f));
                    for (int index = 0; index < visibleBlocks.Length; index++)
                    {
                        BlockDefinition block = visibleBlocks[index];
                        ImGui.PushID($"block:{block.Key}");
                        bool selected = block.Id == _buildTools.SelectedBlockId;
                        if (DrawBlockCard(
                                block,
                                new NumericsVector2(104f, 102f),
                                $"material-{block.Key}",
                                selected,
                                ShortName(block.DisplayName, 14)))
                        {
                            AssignCreativeBlock(block.Id);
                        }
                        ImGui.PopID();
                        if ((index + 1) % columns != 0)
                        {
                            ImGui.SameLine();
                        }
                    }
                    ImGui.EndChild();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Tools"))
                {
                    ImGui.TextWrapped("Left click uses the active tool. Right click places the selected material. Middle click samples a block.");
                    bool precisionEnabled = _microBuild.Enabled;
                    if (ImGui.Checkbox("Precision micro-voxel mode (V)", ref precisionEnabled))
                    {
                        _microBuild.Enabled = precisionEnabled;
                        UpdateSelection();
                    }
                    int gridIndex = _microBuild.SupportedDivisors.ToList().IndexOf(_microBuild.GridDivisor);
                    string[] gridLabels = _microBuild.SupportedDivisors.Select(divisor => $"1/{divisor}").ToArray();
                    if (ImGui.Combo("Precision grid", ref gridIndex, gridLabels, gridLabels.Length) && gridIndex >= 0)
                    {
                        _microBuild.SetGridDivisor(_microBuild.SupportedDivisors[gridIndex]);
                        _settings.PrecisionGridDivisor = _microBuild.GridDivisor;
                    }
                    ImGui.TextDisabled("Precision: left removes one grid cell, right adds one. Alt+wheel changes resolution.");
                    ImGui.Separator();
                    EditorToolMode[] builtInTools = Enum.GetValues<EditorToolMode>()
                        .Where(mode => mode != EditorToolMode.Custom)
                        .ToArray();
                    for (int index = 0; index < builtInTools.Length; index++)
                    {
                        EditorToolMode mode = builtInTools[index];
                        bool selected = _buildTools.ToolMode == mode;
                        if (selected)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.22f, 0.62f, 0.92f, 0.95f));
                        }
                        if (ImGui.Button(ToolLabel(mode), new NumericsVector2(132f, 46f))) _buildTools.ToolMode = mode;
                        if (selected) ImGui.PopStyleColor();
                        if ((index + 1) % 5 != 0) ImGui.SameLine();
                    }

                    ImGui.Separator();
                    int brushRadius = _buildTools.BrushRadius;
                    if (ImGui.SliderInt("Brush radius", ref brushRadius, 1, 8)) _buildTools.BrushRadius = brushRadius;
                    int explosionRadius = _buildTools.ExplosionRadius;
                    if (ImGui.SliderInt("Explosion radius", ref explosionRadius, 1, 12)) _buildTools.ExplosionRadius = explosionRadius;
                    ImGui.TextDisabled("Flatten uses the clicked height. Explode removes a sphere but always protects bedrock.");
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Region"))
                {
                    ImGui.Text($"A: {_buildTools.SelectionA?.ToString() ?? "not set"}");
                    ImGui.Text($"B: {_buildTools.SelectionB?.ToString() ?? "not set"}");
                    ImGui.Text($"Volume: {_buildTools.SelectionVolume:N0} voxels | Clipboard: {_buildTools.ClipboardDescription}");
                    if (!_selection.HasValue) ImGui.BeginDisabled();
                    if (ImGui.Button("Set A from target") && _selection.HasValue)
                        _buildTools.SetSelectionA(_selection.Value.X, _selection.Value.Y, _selection.Value.Z);
                    ImGui.SameLine();
                    if (ImGui.Button("Set B from target") && _selection.HasValue)
                        _buildTools.SetSelectionB(_selection.Value.X, _selection.Value.Y, _selection.Value.Z);
                    if (!_selection.HasValue) ImGui.EndDisabled();
                    ImGui.SameLine();
                    if (ImGui.Button("Clear markers")) _buildTools.ClearSelection();

                    ImGui.Separator();
                    bool hasRegion = _buildTools.SelectionVolume > 0;
                    if (!hasRegion) ImGui.BeginDisabled();
                    if (ImGui.Button("Fill selected material")) ApplyEditorChange(() => _buildTools.FillSelection());
                    ImGui.SameLine();
                    if (ImGui.Button("Clear region")) ApplyEditorChange(() => _buildTools.FillSelection(0));
                    ImGui.SameLine();
                    if (ImGui.Button("Copy")) _buildTools.CopySelection();
                    if (_selection.HasValue)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Replace target material"))
                            ApplyEditorChange(() => _buildTools.ReplaceSelection(_selection.Value.MaterialId));
                    }
                    if (!hasRegion) ImGui.EndDisabled();

                    ImGui.Separator();
                    if (!_buildTools.HasClipboard) ImGui.BeginDisabled();
                    if (ImGui.Button("Rotate Y 90 deg")) _buildTools.RotateClipboardY();
                    ImGui.SameLine();
                    if (ImGui.Button("Mirror X")) _buildTools.MirrorClipboardX();
                    ImGui.SameLine();
                    if (ImGui.Button("Paste at target") && _selection.HasValue)
                    {
                        (int x, int y, int z) = _selection.Value.Adjacent;
                        ApplyEditorChange(() => _buildTools.Paste(new VoxelPoint(x, y, z)));
                    }
                    if (!_buildTools.HasClipboard) ImGui.EndDisabled();

                    ImGui.TextDisabled(_buildTools.Status);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Camera & Physics"))
                {
                    bool flight = _player.FlightMode;
                    if (ImGui.Checkbox("Flight mode", ref flight))
                    {
                        _player.FlightMode = flight;
                        if (!flight) _player.NoClip = false;
                    }
                    bool noClip = _player.NoClip;
                    if (ImGui.Checkbox("No collision / pass through walls", ref noClip))
                    {
                        _player.NoClip = noClip;
                        if (noClip) _player.FlightMode = true;
                    }
                    ImGui.TextDisabled("WASD moves, Space rises, Ctrl descends, Shift accelerates. G/N toggle during play.");

                    ImGui.Separator();
                    ImGui.TextWrapped("Structural Grid physics is part of the world: unsupported six-face components become continuous moving and rotating local grids.");
                    ImGui.TextDisabled(_physics.Status);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("World"))
                {
                    WorldGenerationSettings generation = _world.GenerationSettings;
                    ImGui.Text($"Preset: {generation.Preset}");
                    ImGui.Text($"Biome: {generation.Biome} | Seed: {generation.Seed}");
                    ImGui.Text($"World: infinite X/Z, Y {VoxelWorld.MinimumY}..{VoxelWorld.MaximumY - 1}");
                    ImGui.Text($"Loaded chunks: {_world.LoadedChunkCount:N0}");
                    ImGui.TextWrapped(WorldPresetDescription(generation.Preset));
                    ImGui.Separator();
                    ImGui.TextWrapped("Creating a world saves the current one, then opens a separate named save directory.");
                    if (ImGui.Button("Create / regenerate world", new NumericsVector2(230f, 0f)))
                    {
                        _worldDraft = generation.Clone();
                        _worldNameDraft = NextWorldName(_knownWorlds);
                        _worldCreationStatus = string.Empty;
                        _advancedWorldCreation = false;
                        _worldCreationVisible = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Random seed"))
                    {
                        _worldDraft = generation.Clone();
                        _worldDraft.Seed = Random.Shared.Next(int.MinValue, int.MaxValue);
                        _worldNameDraft = NextWorldName(_knownWorlds);
                        _worldCreationStatus = string.Empty;
                        _advancedWorldCreation = false;
                        _worldCreationVisible = true;
                    }
                    ImGui.Separator();
                    ImGui.TextWrapped($"Save location: {_worldSaves.SaveDirectory}");
                    ImGui.TextDisabled(_saveStatus);
                    if (ImGui.Button("Save world now")) SaveWorld();
                    ImGui.SameLine();
                    if (ImGui.Button("Reload saved world")) LoadWorld();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Mod Tools"))
                {
                    ImGui.TextWrapped("BepInEx plugins can register tools through context.Tools.Register(..., force: true). Direct voxel writes made by a tool are captured as one undoable command.");
                    if (_toolRegistry.Definitions.Count == 0)
                    {
                        ImGui.TextDisabled("No custom build tools are registered.");
                    }
                    foreach (BuildToolDefinition tool in _toolRegistry.Definitions)
                    {
                        bool selected = _buildTools.ToolMode == EditorToolMode.Custom &&
                                        string.Equals(_buildTools.SelectedCustomToolKey, tool.Key, StringComparison.OrdinalIgnoreCase);
                        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.22f, 0.62f, 0.92f, 0.95f));
                        if (ImGui.Button($"{tool.DisplayName}##{tool.Key}", new NumericsVector2(180f, 0f)))
                        {
                            _buildTools.SelectedCustomToolKey = tool.Key;
                            _buildTools.ToolMode = EditorToolMode.Custom;
                        }
                        if (selected) ImGui.PopStyleColor();
                        ImGui.SameLine();
                        ImGui.TextWrapped(string.IsNullOrWhiteSpace(tool.Description) ? tool.Key : tool.Description);
                    }
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }

            ImGui.Separator();
            if (ImGui.Button("Close editor"))
            {
                open = false;
            }
        }
        ImGui.End();

        if (!open)
        {
            _inventoryVisible = false;
            SetMouseCaptured(!_settingsVisible);
        }
    }

    private void ApplyEditorChange(Func<bool> action)
    {
        if (!action()) return;
        _renderer.ResetAccumulation();
        UpdateSelection();
    }

    private static string ToolLabel(EditorToolMode mode) => mode switch
    {
        EditorToolMode.Place => "單格破壞",
        EditorToolMode.Erase => "清除",
        EditorToolMode.Replace => "替換材質",
        EditorToolMode.Pick => "吸取材質",
        EditorToolMode.SelectA => "設定選區 A",
        EditorToolMode.SelectB => "設定選區 B",
        EditorToolMode.BrushAdd => "球形增加",
        EditorToolMode.BrushErase => "球形清除",
        EditorToolMode.Flatten => "地形平整",
        EditorToolMode.Explode => "爆炸破壞",
        EditorToolMode.GridAssign => "網格歸屬",
        _ => mode.ToString(),
    };

    private void DrawMainMenu()
    {
        ImGui.SetNextWindowPos(
            new NumericsVector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f),
            ImGuiCond.Always,
            new NumericsVector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.82f);
        float menuWidth = Math.Clamp(ClientSize.X - 70f, 390f, 560f);
        float menuHeight = Math.Clamp(ClientSize.Y - 70f, 390f, 570f);
        ImGui.SetNextWindowSize(new NumericsVector2(menuWidth, menuHeight), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        if (ImGui.Begin("##main-menu", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            ImGui.Dummy(new NumericsVector2(0f, 10f));
            float titleWidth = ImGui.CalcTextSize("RTX SANDBOX").X;
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - titleWidth) * 0.5f);
            ImGui.Text("RTX SANDBOX");
            string subtitle = "光線追蹤體素建造與物理實驗平台";
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - ImGui.CalcTextSize(subtitle).X) * 0.5f);
            ImGui.TextDisabled(subtitle);
            ImGui.Dummy(new NumericsVector2(0f, 12f));

            float contentWidth = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button($"繼續：{_worldSaves.DisplayName}", new NumericsVector2(contentWidth, 42f)))
            {
                _mainMenuVisible = false;
                _mainMenuStatus = string.Empty;
                SetMouseCaptured(true);
            }

            ImGui.SeparatorText("世界");
            IReadOnlyList<WorldSaveSummary> worlds = _knownWorlds;
            float listHeight = Math.Max(92f, menuHeight - 315f);
            if (ImGui.BeginChild("SavedWorlds", new NumericsVector2(0f, listHeight), ImGuiChildFlags.Borders))
            {
                if (worlds.Count == 0)
                {
                    ImGui.TextDisabled("尚未有保存的世界");
                }
                foreach (WorldSaveSummary world in worlds)
                {
                    bool current = string.Equals(world.StorageName, _worldSaves.WorldName, StringComparison.OrdinalIgnoreCase);
                    bool selected = string.Equals(world.StorageName, _selectedWorldStorage, StringComparison.OrdinalIgnoreCase);
                    string label = current
                        ? $"● {world.DisplayName}##world-{world.StorageName}"
                        : $"{world.DisplayName}##world-{world.StorageName}";
                    if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, new NumericsVector2(0f, 34f)))
                    {
                        _selectedWorldStorage = world.StorageName;
                        _worldRenameDraft = world.DisplayName;
                        _deleteWorldConfirmation = null;
                    }
                    ImGui.SameLine(Math.Max(205f, contentWidth * 0.48f));
                    ImGui.TextDisabled(
                        $"{WorldPresetDisplayName(world.Preset)} · Seed {world.Seed}" +
                        (world.HasBackup ? " · 有備份" : string.Empty));
                }
            }
            ImGui.EndChild();

            WorldSaveSummary? selectedWorld = worlds.FirstOrDefault(world =>
                string.Equals(world.StorageName, _selectedWorldStorage, StringComparison.OrdinalIgnoreCase));
            if (selectedWorld is not null)
            {
                bool selectedCurrent = string.Equals(selectedWorld.StorageName, _worldSaves.WorldName,
                    StringComparison.OrdinalIgnoreCase);
                float quarter = (contentWidth - ImGui.GetStyle().ItemSpacing.X * 3f) * 0.25f;
                if (selectedCurrent) ImGui.BeginDisabled();
                if (ImGui.Button("開啟", new NumericsVector2(quarter, 34f))) OpenWorldFromMenu(selectedWorld.StorageName);
                if (selectedCurrent) ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("重新生成", new NumericsVector2(quarter, 34f))) BeginRebuildWorld(selectedWorld);
                ImGui.SameLine();
                if (ImGui.Button("重新命名", new NumericsVector2(quarter, 34f)))
                {
                    if (_worldSaves.TryRenameWorld(selectedWorld.StorageName, _worldRenameDraft, out string? renameError))
                    {
                        _mainMenuStatus = "世界名稱已更新；Seed 與世界內容沒有變更。";
                        RefreshWorldList();
                        if (selectedCurrent) UpdateTitle();
                    }
                    else _mainMenuStatus = $"重新命名失敗：{renameError}";
                }
                ImGui.SameLine();
                if (selectedCurrent) ImGui.BeginDisabled();
                string deleteLabel = _deleteWorldConfirmation == selectedWorld.StorageName ? "確認刪除" : "刪除";
                if (ImGui.Button(deleteLabel, new NumericsVector2(quarter, 34f)))
                {
                    if (_deleteWorldConfirmation != selectedWorld.StorageName)
                    {
                        _deleteWorldConfirmation = selectedWorld.StorageName;
                        _mainMenuStatus = "再次按下「確認刪除」才會永久刪除此世界。";
                    }
                    else if (_worldSaves.TryDeleteWorld(selectedWorld.StorageName, out string? deleteError))
                    {
                        _mainMenuStatus = $"已刪除世界「{selectedWorld.DisplayName}」。";
                        _deleteWorldConfirmation = null;
                        RefreshWorldList();
                    }
                    else _mainMenuStatus = $"刪除失敗：{deleteError}";
                }
                if (selectedCurrent) ImGui.EndDisabled();
                ImGui.SetNextItemWidth(contentWidth);
                ImGui.InputText("##world-rename", ref _worldRenameDraft, 64);
            }

            if (!string.IsNullOrWhiteSpace(_mainMenuStatus))
                ImGui.TextWrapped(_mainMenuStatus);
            if (ImGui.Button("建立新世界", new NumericsVector2(contentWidth, 38f)))
            {
                _worldDraft = WorldGenerationSettings.Natural(Random.Shared.Next(int.MinValue, int.MaxValue));
                _worldNameDraft = NextWorldName(worlds);
                _worldCreationStatus = string.Empty;
                _advancedWorldCreation = false;
                _worldCreationVisible = true;
            }
            float halfButton = (contentWidth - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            if (ImGui.Button("設定", new NumericsVector2(halfButton, 38f))) _settingsVisible = true;
            ImGui.SameLine();
            if (ImGui.Button("離開遊戲", new NumericsVector2(halfButton, 38f))) Close();
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void DrawPauseMenu()
    {
        _pauseMenuButtonCountThisFrame = 0;
        ImGui.SetNextWindowPos(new NumericsVector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f),
            ImGuiCond.Always, new NumericsVector2(0.5f));
        ImGui.SetNextWindowSize(new NumericsVector2(520f, 500f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new NumericsVector2(12f, 12f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0.08f, 0.09f, 0.10f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.28f, 0.29f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.42f, 0.44f, 0.46f, 1f));
        if (ImGui.Begin("##pause-menu", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            float width = ImGui.GetContentRegionAvail().X;
            ImGui.Dummy(new NumericsVector2(0f, 10f));
            if (PauseMenuButton("繼續遊戲", new NumericsVector2(width, 58f)))
            {
                _pauseMenuVisible = false;
                SetMouseCaptured(true);
            }
            ImGui.Separator();
            float half = (width - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            if (PauseMenuButton("設定", new NumericsVector2(half, 54f)))
            {
                _settingsOpenedFromPause = true;
                _settingsVisible = true;
            }
            ImGui.SameLine();
            PauseMenuButton("成就", new NumericsVector2(half, 54f));
            PauseMenuButton("統計", new NumericsVector2(half, 54f));
            ImGui.SameLine();
            if (PauseMenuButton("模組", new NumericsVector2(half, 54f)))
                _modsVisible = true;
            PauseMenuButton("語言（之後）", new NumericsVector2(half, 54f));
            ImGui.SameLine();
            PauseMenuButton("按鍵（之後）", new NumericsVector2(half, 54f));
            ImGui.Separator();
            if (PauseMenuButton("離開世界", new NumericsVector2(width, 58f)))
            {
                if (SaveWorld())
                {
                    _pauseMenuVisible = false;
                    _settingsOpenedFromPause = false;
                    _mainMenuVisible = true;
                    RefreshWorldList();
                }
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(3);
    }

    private bool PauseMenuButton(string label, NumericsVector2 size)
    {
        _pauseMenuButtonCountThisFrame++;
        return ImGui.Button(label, size);
    }

    private void DrawCreateWorldInterface()
    {
        ImGui.SetNextWindowPos(
            new NumericsVector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f),
            ImGuiCond.Always,
            new NumericsVector2(0.5f, 0.5f));
        float height = _advancedWorldCreation
            ? Math.Clamp(ClientSize.Y - 50f, 440f, 650f)
            : 285f;
        ImGui.SetNextWindowSize(new NumericsVector2(540f, height), ImGuiCond.Always);
        bool open = _worldCreationVisible;
        if (ImGui.Begin(_rebuildWorldStorage is null ? "建立世界" : "重新生成世界", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
        {
            if (_gpuSmokeIme && _imeSmokeStage == 0)
            {
                ImGui.SetKeyboardFocusHere();
                _imeSmokeStage = 1;
            }
            ImGui.InputText("世界名稱", ref _worldNameDraft, 64);
            if (_gpuSmokeIme && _imeSmokeStage == 1 && ImGui.IsItemActive())
            {
                if (!_ui.WantsKeyboardInput)
                    throw new InvalidOperationException("Focused text input did not take keyboard ownership from gameplay.");
                foreach (System.Text.Rune rune in "中文_日本語_한국어_ไทย_Русский".EnumerateRunes())
                    _ui.AddInputCharacter((uint)rune.Value);
                _imeSmokeStage = 2;
            }
            if (_gpuSmokeIme && _imeSmokeStage == 2 &&
                _worldNameDraft.Contains("中文_日本語_한국어_ไทย_Русский", StringComparison.Ordinal))
            {
                _imeSmokeStage = 3;
                Console.WriteLine("Unicode input, IME candidate positioning, and UI keyboard ownership verified.");
            }
            int seed = _worldDraft.Seed;
            if (ImGui.InputInt("種子", ref seed)) _worldDraft.Seed = seed;
            ImGui.SameLine();
            if (ImGui.Button("隨機")) _worldDraft.Seed = Random.Shared.Next(int.MinValue, int.MaxValue);

            ImGui.Checkbox("更多世界選項", ref _advancedWorldCreation);
            if (_advancedWorldCreation)
            {
                ImGui.Separator();
                int preset = (int)_worldDraft.Preset;
                string[] presets = Enum.GetValues<WorldPreset>().Select(WorldPresetDisplayName).ToArray();
                if (ImGui.Combo("世界模板", ref preset, presets, presets.Length))
                {
                    _worldDraft.Preset = (WorldPreset)preset;
                    ApplyPresetDefaults(_worldDraft);
                }
                ImGui.TextWrapped(WorldPresetDescription(_worldDraft.Preset));

                bool naturalOptions = _worldDraft.Preset is WorldPreset.Natural or WorldPreset.PureNature;
                bool flatOptions = _worldDraft.Preset == WorldPreset.Flat;
                if (!naturalOptions) ImGui.BeginDisabled();
                int biome = (int)_worldDraft.Biome;
                string[] biomes = Enum.GetValues<BiomePreset>().Select(BiomeDisplayName).ToArray();
                if (ImGui.Combo("生態域", ref biome, biomes, biomes.Length))
                    _worldDraft.Biome = (BiomePreset)biome;
                if (!naturalOptions) ImGui.EndDisabled();

                if (!flatOptions) ImGui.BeginDisabled();
                int surfaceHeight = _worldDraft.FlatSurfaceHeight;
                if (ImGui.SliderInt("平坦地表高度", ref surfaceHeight, VoxelWorld.MinimumY + 1, 256))
                    _worldDraft.FlatSurfaceHeight = surfaceHeight;
                string surfaceBlockKey = _worldDraft.SurfaceBlockKey;
                DrawGenerationBlockPicker("地表材質", ref surfaceBlockKey);
                _worldDraft.SurfaceBlockKey = surfaceBlockKey;
                string undergroundBlockKey = _worldDraft.UndergroundBlockKey;
                DrawGenerationBlockPicker("地下材質", ref undergroundBlockKey);
                _worldDraft.UndergroundBlockKey = undergroundBlockKey;
                if (!flatOptions) ImGui.EndDisabled();

                bool supportsNatureOptions = flatOptions || naturalOptions;
                if (!supportsNatureOptions) ImGui.BeginDisabled();
                bool lakes = _worldDraft.GenerateLakes;
                if (ImGui.Checkbox("生成湖泊／水系", ref lakes)) _worldDraft.GenerateLakes = lakes;
                bool vegetation = _worldDraft.GenerateVegetation;
                if (ImGui.Checkbox("生成樹木與植物", ref vegetation))
                    _worldDraft.GenerateVegetation = vegetation;
                if (!supportsNatureOptions) ImGui.EndDisabled();
            }
            else
            {
                ImGui.TextDisabled("Natural · 無生物 · 無人工建築 · 建立後直接開始建造");
            }

            ImGui.Dummy(new NumericsVector2(0f, 8f));
            if (string.IsNullOrWhiteSpace(_worldNameDraft)) ImGui.BeginDisabled();
            if (ImGui.Button(_rebuildWorldStorage is null ? "建立" : "覆蓋並重新生成", new NumericsVector2(180f, 40f)))
                CreateWorldFromDraft();
            if (string.IsNullOrWhiteSpace(_worldNameDraft)) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("取消", new NumericsVector2(150f, 40f))) open = false;
            if (!string.IsNullOrWhiteSpace(_worldCreationStatus)) ImGui.TextWrapped(_worldCreationStatus);
        }
        ImGui.End();
        if (!open)
        {
            _worldCreationVisible = false;
            _advancedWorldCreation = false;
            _rebuildWorldStorage = null;
            SetMouseCaptured(!AnyInterfaceVisible());
        }
    }

    private void DrawGenerationBlockPicker(string label, ref string blockKey)
    {
        IReadOnlyList<BlockDefinition> palette = _blocks.PlaceableDefinitions();
        string currentKey = blockKey;
        int selected = palette.ToList().FindIndex(block =>
            string.Equals(block.Key, currentKey, StringComparison.OrdinalIgnoreCase));
        if (selected < 0) selected = 0;
        string[] names = palette.Select(BlockDisplayName).ToArray();
        if (ImGui.Combo(label, ref selected, names, names.Length) && selected >= 0 && selected < palette.Count)
            blockKey = palette[selected].Key;
    }

    private void CreateWorldFromDraft()
    {
        try
        {
            string requestedName = _worldNameDraft.Trim();
            if (string.IsNullOrWhiteSpace(requestedName))
                throw new InvalidOperationException("請輸入世界名稱。");
            _worldDraft.Validate();
            string storageName = _rebuildWorldStorage ?? requestedName;
            var newWorldSaves = new WorldSaveService(
                _blocks,
                preferredSeed: _worldDraft.Seed,
                worldName: storageName,
                saveRoot: _worldSaves.WorldsRoot);
            if (_rebuildWorldStorage is null && Directory.Exists(newWorldSaves.SaveDirectory))
                throw new InvalidOperationException($"世界「{newWorldSaves.WorldName}」已存在，請使用另一個名稱。");
            if (!SaveWorld())
                throw new InvalidOperationException("目前世界無法保存，因此沒有切換到新世界。");

            _settings.WorldName = newWorldSaves.WorldName;
            _settings.WorldSeed = _worldDraft.Seed;
            _worldSaves = newWorldSaves;
            _modHost.RegisterService(_worldSaves);
            _world.Regenerate(_worldDraft);
            _buildTools.ResetForNewWorld();
            _microBuild.ResetForNewWorld();
            _droppedItems.Clear();
            SeedStartingInventory();
            _progression.Restore(Array.Empty<string>(), sawNight: false);
            _survival.Restore(
                PlayerSurvival.MaximumHealth,
                PlayerSurvival.MaximumOxygen,
                PlayerSurvival.MaximumHunger);
            _physics.RequestEvaluation();
            NumericsVector3 spawn = FindWorldSpawn();
            _player.Teleport(spawn);
            _streamedPlayerChunk = null;
            StreamWorldAroundPlayer();
            _survival.SetSpawn(spawn);
            _clock.SetTime(_worldDraft.Preset == WorldPreset.Showcase ? 14f : 9f);
            _lastAccumulatedTime = _clock.TimeOfDayHours;
            _renderer.ResetAccumulation();
            UpdateSelection();
            _modHost.PublishWorldReady();
            if (!SaveWorld())
                throw new IOException("新世界已建立，但第一次保存失敗；請檢查磁碟與權限後按 F5 重試。");
            if (!newWorldSaves.TryRenameWorld(newWorldSaves.WorldName, requestedName, out string? renameError))
                throw new IOException($"世界已生成，但顯示名稱更新失敗：{renameError}");
            _worldCreationStatus = $"Created {_worldDraft.Preset} / {_worldDraft.Biome} with seed {_worldDraft.Seed}.";
            _rebuildWorldStorage = null;
            _worldCreationVisible = false;
            _mainMenuVisible = false;
            _advancedWorldCreation = false;
            UpdateTitle();
            SetMouseCaptured(true);
        }
        catch (Exception exception)
        {
            _worldCreationStatus = $"World creation failed: {exception.Message}";
            Console.Error.WriteLine(_worldCreationStatus);
        }
    }

    private void BeginRebuildWorld(WorldSaveSummary world)
    {
        if (!_worldSaves.TryGetWorldGeneration(world.StorageName, out WorldGenerationSettings generation, out string? error))
        {
            _mainMenuStatus = $"無法讀取世界設定：{error}";
            return;
        }
        _rebuildWorldStorage = world.StorageName;
        _worldDraft = generation;
        _worldDraft.Seed = world.Seed;
        _worldNameDraft = world.DisplayName;
        _advancedWorldCreation = true;
        _worldCreationStatus = "重新生成會保留名稱與 Seed，但清除全部玩家修改並覆蓋世界。";
        _worldCreationVisible = true;
    }

    private void OpenWorldFromMenu(string storageName)
    {
        if (string.Equals(storageName, _worldSaves.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            _mainMenuVisible = false;
            SetMouseCaptured(true);
            return;
        }
        if (!SaveWorld())
        {
            _mainMenuStatus = "目前世界保存失敗，已取消切換以保護資料。";
            return;
        }

        var selectedSaves = new WorldSaveService(
            _blocks,
            preferredSeed: _settings.WorldSeed,
            worldName: storageName,
            saveRoot: _worldSaves.WorldsRoot);
        if (!selectedSaves.TryLoad(_world, out SavedWorldState state, out string? error))
        {
            _mainMenuStatus = error is null
                ? $"找不到世界「{storageName}」的有效存檔。"
                : $"世界載入失敗：{error}";
            return;
        }

        _worldSaves = selectedSaves;
        _modHost.RegisterService(_worldSaves);
        _settings.WorldName = selectedSaves.WorldName;
        _settings.WorldSeed = state.Seed;
        ApplyLoadedWorldState(state);
        _saveStatus = $"Loaded {_worldSaves.WorldName} at {DateTime.Now:HH:mm:ss}";
            _mainMenuStatus = string.Empty;
            _mainMenuVisible = false;
            RefreshWorldList();
            SetMouseCaptured(true);
    }

    private static string NextWorldName(IReadOnlyList<WorldSaveSummary> worlds)
    {
        var occupied = worlds.Select(world => world.StorageName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        const string baseName = "新的世界";
        if (!occupied.Contains(baseName)) return baseName;
        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!occupied.Contains(candidate)) return candidate;
        }
        return $"{baseName} {DateTime.Now:yyyyMMdd-HHmmss}";
    }

    private void RefreshWorldList()
    {
        _knownWorlds = _worldSaves.DiscoverWorlds();
        if (_knownWorlds.Count == 0)
        {
            _selectedWorldStorage = string.Empty;
            return;
        }
        if (_knownWorlds.All(world => !string.Equals(world.StorageName, _selectedWorldStorage,
                StringComparison.OrdinalIgnoreCase)))
            _selectedWorldStorage = _knownWorlds.FirstOrDefault(world =>
                string.Equals(world.StorageName, _worldSaves.WorldName, StringComparison.OrdinalIgnoreCase))?.StorageName
                ?? _knownWorlds[0].StorageName;
        WorldSaveSummary selected = _knownWorlds.First(world =>
            string.Equals(world.StorageName, _selectedWorldStorage, StringComparison.OrdinalIgnoreCase));
        _worldRenameDraft = selected.DisplayName;
    }

    private NumericsVector3 FindWorldSpawn()
    {
        int centerX = VoxelWorld.Width / 2;
        int centerZ = VoxelWorld.Depth / 2;
        for (int radius = 0; radius < VoxelWorld.Width / 2; radius++)
        for (int z = Math.Max(1, centerZ - radius); z <= Math.Min(VoxelWorld.Depth - 2, centerZ + radius); z++)
        for (int x = Math.Max(1, centerX - radius); x <= Math.Min(VoxelWorld.Width - 2, centerX + radius); x++)
        {
            if (radius > 0 && Math.Abs(x - centerX) != radius && Math.Abs(z - centerZ) != radius) continue;
            int surface = _world.FindSurfaceY(x, z);
            if (surface <= VoxelWorld.MinimumY || surface + 2 >= VoxelWorld.MaximumY || _world.IsFluid(x, surface + 1, z)) continue;
            return new NumericsVector3(x + 0.5f, surface + 1.001f, z + 0.5f);
        }
        return new NumericsVector3(centerX + 0.5f, 260f, centerZ + 0.5f);
    }

    private static void ApplyPresetDefaults(WorldGenerationSettings settings)
    {
        switch (settings.Preset)
        {
            case WorldPreset.Flat:
                settings.Biome = BiomePreset.Plains;
                settings.GenerateLakes = false;
                settings.GenerateVegetation = false;
                break;
            case WorldPreset.Natural:
            case WorldPreset.PureNature:
                settings.Biome = BiomePreset.Auto;
                settings.GenerateLakes = true;
                settings.GenerateVegetation = true;
                break;
            case WorldPreset.Void:
            case WorldPreset.Showcase:
                settings.Biome = BiomePreset.Auto;
                settings.GenerateLakes = false;
                settings.GenerateVegetation = false;
                break;
        }
    }

    private static string WorldPresetDescription(WorldPreset preset) => preset switch
    {
        WorldPreset.Flat => "平坦世界：可預測的地層，適合建築、效能測試和模組測試。",
        WorldPreset.Natural => "自然世界：地形、生態、植物和水系，不生成任何人工建築。",
        WorldPreset.PureNature => "純自然：連續的大型生態過渡、河流、山脈與洞穴。",
        WorldPreset.Void => "虛空測試：只有一座受光的 9×9 平台與天空。",
        WorldPreset.Showcase => "RTX 展示：水池、玻璃森林、材質展廳與 RGB 光照實驗室。",
        _ => preset.ToString(),
    };

    private static string WorldPresetDisplayName(WorldPreset preset) => preset switch
    {
        WorldPreset.Flat => "平坦",
        WorldPreset.Natural => "自然",
        WorldPreset.PureNature => "純自然",
        WorldPreset.Void => "虛空測試",
        WorldPreset.Showcase => "RTX 展示",
        _ => preset.ToString(),
    };

    private static string BiomeDisplayName(BiomePreset biome) => biome switch
    {
        BiomePreset.Auto => "自動",
        BiomePreset.Plains => "草原",
        BiomePreset.Forest => "森林",
        BiomePreset.BambooForest => "竹林",
        BiomePreset.Desert => "沙漠",
        BiomePreset.Snow => "雪地",
        BiomePreset.Mountains => "山地",
        BiomePreset.Swamp => "沼澤",
        _ => biome.ToString(),
    };

    private string HotbarLabel(int slot)
    {
        ItemStack stack = _inventory.GetSlot(slot);
        if (stack.IsEmpty)
        {
            return $"{slot + 1}\nEmpty";
        }
        string name = _items.TryGet(stack.ItemKey!, out ItemDefinition? item)
            ? ShortName(item!.DisplayName, 9)
            : ShortName(stack.ItemKey!, 9);
        string amount = item is not null && item.MaxDurability > 0
            ? $"D{Math.Max(0, item.MaxDurability - stack.Damage)}"
            : $"x{stack.Count}";
        return $"{slot + 1} {name}\n{amount}";
    }

    private string InventoryLabel(int slot)
    {
        ItemStack stack = _inventory.GetSlot(slot);
        if (stack.IsEmpty)
        {
            return $"{slot + 1}\nEmpty";
        }
        string name = _items.TryGet(stack.ItemKey!, out ItemDefinition? item)
            ? ShortName(item!.DisplayName, 11)
            : ShortName(stack.ItemKey!, 11);
        string amount = item is not null && item.MaxDurability > 0
            ? $"Durability {Math.Max(0, item.MaxDurability - stack.Damage)}"
            : $"x{stack.Count}";
        return $"{slot + 1}\n{name}\n{amount}";
    }

    private static string ShortName(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 3)] + "...";

    private bool BlockMatchesPaletteFilter(BlockDefinition block)
    {
        if (_paletteCategory != "All" &&
            !string.Equals(TopLevelCategory(block.Category), _paletteCategory, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(_paletteSearch)) return true;
        string searchable = string.Join(' ', new[] { BlockDisplayName(block), block.DisplayName, block.Key, block.Category }
            .Concat(block.Tags));
        return _paletteSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string TopLevelCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "General";
        int separator = category.IndexOf('/');
        return (separator < 0 ? category : category[..separator]).Trim();
    }

    private static string CategoryDisplayName(string category) => category switch
    {
        "All" => "全部",
        "Nature" => "自然",
        "Wood" => "木材",
        "Building" => "建築",
        "Metal" => "金屬",
        "Technology" => "科技",
        "Optical" => "玻璃與光學",
        "Lighting" => "燈光與發光",
        "Special" => "特殊材質",
        "General" => "一般",
        _ => category,
    };

    private static string BlockDisplayName(BlockDefinition block)
    {
        string tail = block.Key[(block.Key.IndexOf(':') + 1)..];
        string? phrase = tail switch
        {
            "light_gray_concrete" => "淺灰色混凝土",
            "stone_bricks" => "石磚",
            "slate_bricks" => "板岩磚",
            "blackstone_bricks" => "黑石磚",
            "marble_bricks" => "大理石磚",
            "granite_bricks" => "花崗岩磚",
            "limestone_bricks" => "石灰岩磚",
            "red_bricks" => "紅磚",
            "sandstone_bricks" => "砂岩磚",
            "stainless_steel" => "不鏽鋼",
            "circuit_board" => "電路板",
            "display_screen" => "顯示螢幕",
            "hazard_stripes" => "警示條紋",
            "perfect_mirror" => "完美鏡面",
            "light_absorber" => "完全吸光材質",
            "volumetric_light" => "體積光",
            "animated_test" => "動畫測試方塊",
            _ => null,
        };
        if (phrase is not null) return phrase;

        string[] tokens = tail.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var translated = new string[tokens.Length];
        for (int index = 0; index < tokens.Length; index++)
        {
            if (!ChineseBlockWords.TryGetValue(tokens[index], out string? word)) return block.DisplayName;
            translated[index] = word;
        }
        return string.Concat(translated);
    }

    private void DrawSettingsInterface()
    {
        System.Numerics.Vector2 center = new(ClientSize.X * 0.5f, ClientSize.Y * 0.5f);
        ImGui.SetNextWindowPos(center, ImGuiCond.FirstUseEver, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(680f, 640f), ImGuiCond.FirstUseEver);

        bool open = _settingsVisible;
        if (ImGui.Begin("RTX Sandbox 設定", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled("變更會立即生效，正常關閉遊戲時會自動保存。");
            ImGui.Spacing();

            if (ImGui.BeginTabBar("SettingsTabs"))
            {
                if (ImGui.BeginTabItem("畫面"))
                {
                    DrawRendererSelector();
                    ImGui.TextDisabled("所有材質都有 Fresnel 反射；粗糙度控制反射的清晰程度。");
                    ImGui.Separator();
                    DrawActivePipelineAdvancedSettings();
                    DrawCommonPipelineSettings();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("時間與光照"))
                {
                    float time = _clock.TimeOfDayHours;
                    if (ImGui.SliderFloat("遊戲時間", ref time, 0f, 23.99f, "%.2f 時"))
                    {
                        _clock.SetTime(time);
                        _lastAccumulatedTime = time;
                        _renderer.ResetAccumulation();
                    }

                    bool paused = _clock.Paused;
                    if (ImGui.Checkbox("暫停自動時間循環", ref paused))
                    {
                        _clock.Paused = paused;
                        _renderer.ResetAccumulation();
                    }

                    float dayLength = _clock.DayLengthMinutes;
                    if (ImGui.SliderFloat("一日所需現實分鐘", ref dayLength, 1f, 120f, "%.1f 分"))
                    {
                        _clock.SetDayLength(dayLength);
                    }

                    if (ImGui.Button("日出")) SetUiTime(6f);
                    ImGui.SameLine();
                    if (ImGui.Button("正午")) SetUiTime(12f);
                    ImGui.SameLine();
                    if (ImGui.Button("日落")) SetUiTime(18f);
                    ImGui.SameLine();
                    if (ImGui.Button("午夜")) SetUiTime(0f);

                    ImGui.Spacing();
                    ImGui.Text($"目前遊戲時間：{_clock.DisplayTime}");
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("控制與介面"))
                {
                    float sensitivity = _settings.MouseSensitivity;
                    if (ImGui.SliderFloat("滑鼠靈敏度", ref sensitivity, 0.02f, 0.5f, "%.2f"))
                    {
                        _settings.MouseSensitivity = sensitivity;
                    }

                    float reach = _settings.SelectionDistance;
                    if (ImGui.SliderFloat("建造距離", ref reach, 3f, 16f, "%.1f 格"))
                    {
                        _settings.SelectionDistance = reach;
                        UpdateSelection();
                    }

                    bool flight = _player.FlightMode;
                    if (ImGui.Checkbox("飛行模式", ref flight))
                    {
                        _player.FlightMode = flight;
                        if (!flight) _player.NoClip = false;
                    }

                    bool noClip = _player.NoClip;
                    if (ImGui.Checkbox("穿牆／無碰撞", ref noClip))
                    {
                        _player.NoClip = noClip;
                        if (noClip) _player.FlightMode = true;
                    }

                    ImGui.SeparatorText("準星");
                    bool showCrosshair = _settings.ShowCrosshair;
                    if (ImGui.Checkbox("顯示準星", ref showCrosshair)) _settings.ShowCrosshair = showCrosshair;
                    float crosshairSize = _settings.CrosshairSize;
                    if (ImGui.SliderFloat("準星大小", ref crosshairSize, 3f, 20f, "%.0f px"))
                        _settings.CrosshairSize = crosshairSize;
                    float crosshairOpacity = _settings.CrosshairOpacity;
                    if (ImGui.SliderFloat("準星透明度", ref crosshairOpacity, 0.2f, 1f, "%.2f"))
                        _settings.CrosshairOpacity = crosshairOpacity;
                    bool contextualCrosshair = _settings.ContextualCrosshairColor;
                    if (ImGui.Checkbox("依操作顯示情境顏色", ref contextualCrosshair))
                        _settings.ContextualCrosshairColor = contextualCrosshair;

                    ImGui.SeparatorText("結構物理");
                    ImGui.TextWrapped("六面失去支撐的結構會自動成為可移動、旋轉的局部 Grid。斜邊與斜角不傳遞支撐。此核心規則沒有關閉開關。");
                    float autosave = _settings.AutosaveMinutes;
                    if (ImGui.SliderFloat("自動保存間隔", ref autosave, 0.5f, 30f, "%.1f 分"))
                    {
                        _settings.AutosaveMinutes = autosave;
                        _autosaveTimer = 0.0;
                    }

                    ImGui.Separator();
                    ImGui.TextWrapped("RTX Sandbox 是創意建造、光追展示與物理實驗平台，沒有生存壓力、敵對生物或科技解鎖線。");
                    ImGui.TextWrapped("E／I／Tab 開啟獨立方塊庫；B 開啟建造工具。左鍵破壞，右鍵放置。");
                    ImGui.Separator();
                    ImGui.TextWrapped($"世界存檔：{_worldSaves.SavePath}");
                    ImGui.TextDisabled(_saveStatus);
                    if (ImGui.Button("立即保存世界"))
                    {
                        SaveWorld();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("重新載入世界"))
                    {
                        LoadWorld();
                    }
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.Separator();
            if (ImGui.Button("保存設定"))
            {
                SaveCurrentSettings();
            }
            ImGui.SameLine();
            if (ImGui.Button("完成"))
            {
                open = false;
            }
        }
        ImGui.End();

        if (!open)
        {
            _settingsVisible = false;
            _settingsOpenedFromPause = false;
            SetMouseCaptured(!AnyInterfaceVisible());
        }
    }

    private void DrawModsInterface()
    {
        ImGui.SetNextWindowPos(new NumericsVector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f),
            ImGuiCond.Always, new NumericsVector2(0.5f));
        ImGui.SetNextWindowSize(new NumericsVector2(
            MathF.Min(820f, MathF.Max(620f, ClientSize.X - 40f)),
            MathF.Min(720f, MathF.Max(560f, ClientSize.Y - 40f))), ImGuiCond.Always);
        bool open = _modsVisible;
        if (ImGui.Begin("模組", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
        {
            DrawModsPanel();
            ImGui.Separator();
            if (ImGui.Button("完成", new NumericsVector2(150f, 38f))) open = false;
        }
        ImGui.End();
        DrawModTextEditor();
        if (!open) _modsVisible = false;
    }

    private void DrawModsPanel()
    {
        ImGui.Text(BepInExBootstrap.Started
            ? $"BepInEx 6 CoreCLR：已啟動（載入 {BepInExBootstrap.LoadedPlugins.Count} 個插件）"
            : "BepInEx 6 CoreCLR：無法使用");
        ImGui.TextDisabled("Loader 內建 HarmonyX 修補、相依解析、日誌與設定保存；這些不會列成 Mod。");
        if (!string.IsNullOrWhiteSpace(BepInExBootstrap.FailureMessage))
        {
            ImGui.TextWrapped(BepInExBootstrap.FailureMessage);
        }

        if (ImGui.Button("開啟插件資料夾"))
        {
            OpenFolder(BepInExBootstrap.PluginDirectory);
        }
        ImGui.SameLine();
        if (ImGui.Button("開啟設定資料夾"))
        {
            OpenFolder(BepInExBootstrap.ConfigDirectory);
        }

        DrawModWorkspace();

        foreach (string error in BepInExBootstrap.DependencyErrors)
        {
            ImGui.TextWrapped($"相依錯誤：{error}");
        }

        ImGui.SeparatorText("已載入插件");
        if (BepInExBootstrap.LoadedPlugins.Count == 0)
        {
            ImGui.TextDisabled("目前沒有任何 Mod；本體方塊與功能不會出現在這裡。");
        }
        foreach (LoadedPluginInfo plugin in BepInExBootstrap.LoadedPlugins)
        {
            if (!ImGui.TreeNode($"{plugin.Name} {plugin.Version}##{plugin.Guid}"))
            {
                continue;
            }

            ImGui.TextWrapped(plugin.Guid);
            ImGui.TextDisabled(plugin.Location);
            if (plugin.Dependencies.Count > 0)
            {
                ImGui.TextWrapped($"相依項目：{string.Join(", ", plugin.Dependencies)}");
            }

            ConfigFile? config = plugin.Instance?.Config;
            if (config is null || config.Count == 0)
            {
                ImGui.TextDisabled("這個插件沒有公開設定項目。");
            }
            else
            {
                foreach (ConfigEntryBase entry in config.Values
                             .OrderBy(entry => entry.Definition.Section, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(entry => entry.Definition.Key, StringComparer.OrdinalIgnoreCase))
                {
                    DrawConfigEntry(plugin, entry);
                }

                ImGui.PushID(plugin.Guid);
                if (ImGui.SmallButton("保存設定"))
                {
                    try
                    {
                        config.Save();
                        _modStatus = $"已保存 {plugin.Name} 的設定。";
                    }
                    catch (Exception exception)
                    {
                        _modStatus = $"設定保存失敗：{exception.Message}";
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("重新載入設定"))
                {
                    try
                    {
                        config.Reload();
                        _modStatus = $"已從磁碟重新載入 {plugin.Name} 的設定。";
                    }
                    catch (Exception exception)
                    {
                        _modStatus = $"設定重新載入失敗：{exception.Message}";
                    }
                }
                ImGui.PopID();
            }
            ImGui.TreePop();
        }

        ImGui.Separator();
        ImGui.TextDisabled(_modStatus);
        ImGui.TextDisabled("新增或移除插件 DLL 後請重新啟動遊戲；強制註冊以最後載入者為準。");
    }

    private void DrawModWorkspace()
    {
        CompleteModCompilationIfReady();
        if (!_modWorkspaceLoaded) RefreshModWorkspace();

        ImGui.SeparatorText("模組工作區（右鍵操作）");
        ImGui.TextDisabled("不會自動編譯或刪除；文字內容可使用任意副檔名，編譯目標固定為 net6.0。");
        if (ImGui.SmallButton("重新整理")) RefreshModWorkspace();
        ImGui.SameLine();
        bool compilationActive = _modCompilationTask is not null;
        if (compilationActive)
        {
            ImGui.TextDisabled("正在編譯；遊戲仍可繼續操作……");
        }
        else
        {
            ImGui.TextDisabled($"{_modWorkspaceEntries.Count} 個檔案；右鍵文字檔可編輯或編譯。");
        }

        if (ImGui.BeginChild("mod-workspace-files", new NumericsVector2(0f, 178f), ImGuiChildFlags.Borders))
        {
            if (_modWorkspaceEntries.Count == 0)
            {
                ImGui.TextDisabled("插件資料夾是空的。可先放入 .cs、.txt 或其他文字檔。");
            }
            foreach (ModWorkspaceEntry entry in _modWorkspaceEntries)
            {
                ImGui.PushID(entry.RelativePath);
                string type = entry.IsText ? "文字" : "檔案";
                string label = $"{entry.RelativePath}  [{type}，{FormatFileSize(entry.Length)}]";
                if (ImGui.Selectable(label, string.Equals(_modSelectedPath, entry.FullPath,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    _modSelectedPath = entry.FullPath;
                }

                if (ImGui.BeginPopupContextItem("mod-file-context"))
                {
                    ImGui.TextDisabled(entry.RelativePath);
                    if (!entry.IsText) ImGui.BeginDisabled();
                    if (ImGui.MenuItem("編輯文字")) OpenModTextEditor(entry.FullPath);
                    if (!entry.IsText) ImGui.EndDisabled();

                    if (!entry.IsText || compilationActive) ImGui.BeginDisabled();
                    if (ImGui.MenuItem("編譯")) StartModCompilation(entry.FullPath);
                    if (!entry.IsText || compilationActive) ImGui.EndDisabled();

                    if (compilationActive) ImGui.BeginDisabled();
                    if (ImGui.MenuItem("移動……"))
                    {
                        _modMoveSource = entry.FullPath;
                        string? directory = Path.GetDirectoryName(entry.RelativePath);
                        _modMoveDestination = directory is null or "." ? string.Empty : directory;
                        _modMovePopupRequested = true;
                    }
                    if (ImGui.MenuItem("刪除……"))
                    {
                        _modDeleteSource = entry.FullPath;
                        _modDeletePopupRequested = true;
                    }
                    if (compilationActive) ImGui.EndDisabled();
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        if (_modMovePopupRequested)
        {
            ImGui.OpenPopup("移動模組檔案");
            _modMovePopupRequested = false;
        }
        if (ImGui.BeginPopupModal("移動模組檔案", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(_modMoveSource is null
                ? "沒有選取檔案。"
                : Path.GetRelativePath(BepInExBootstrap.PluginDirectory, _modMoveSource));
            ImGui.InputText("目標子資料夾", ref _modMoveDestination, 512);
            ImGui.TextDisabled("請輸入 plugins 內的相對資料夾；不會覆寫同名檔案。");
            if (ImGui.Button("移動", new NumericsVector2(110f, 0f)))
            {
                MoveModFile();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("取消", new NumericsVector2(110f, 0f))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (_modDeletePopupRequested)
        {
            ImGui.OpenPopup("確認刪除模組檔案");
            _modDeletePopupRequested = false;
        }
        if (ImGui.BeginPopupModal("確認刪除模組檔案", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(_modDeleteSource is null
                ? "沒有選取檔案。"
                : $"確定刪除 {Path.GetRelativePath(BepInExBootstrap.PluginDirectory, _modDeleteSource)}？");
            ImGui.TextDisabled("確認後會移出 plugins 並保存到 BepInEx/trash，不會無提示永久清除。");
            if (ImGui.Button("確認刪除", new NumericsVector2(120f, 0f)))
            {
                DeleteModFile();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("取消", new NumericsVector2(120f, 0f))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (!string.IsNullOrWhiteSpace(_modCompilationLog) && ImGui.TreeNode("最近一次編譯輸出"))
        {
            ImGui.TextWrapped(_modCompilationLog);
            ImGui.TreePop();
        }
    }

    private void DrawModTextEditor()
    {
        if (!_modEditorVisible || _modEditorPath is null) return;
        ImGui.SetNextWindowSize(new NumericsVector2(760f, 560f), ImGuiCond.FirstUseEver);
        bool editorOpen = _modEditorVisible;
        string title = $"文字編輯器 — {Path.GetFileName(_modEditorPath)}###mod-text-editor";
        if (ImGui.Begin(title, ref editorOpen, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled(Path.GetRelativePath(BepInExBootstrap.PluginDirectory, _modEditorPath));
            bool changed = !string.Equals(_modEditorText, _modEditorSavedText, StringComparison.Ordinal);
            if (changed) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.78f, 0.2f, 1f), "尚未保存");
            else ImGui.TextDisabled("已保存");

            NumericsVector2 available = ImGui.GetContentRegionAvail();
            float editorHeight = MathF.Max(220f, available.Y - 92f);
            ImGui.InputTextMultiline(
                "##mod-editor-text",
                ref _modEditorText,
                4u * 1024u * 1024u,
                new NumericsVector2(-1f, editorHeight),
                ImGuiInputTextFlags.AllowTabInput);

            bool compilationActive = _modCompilationTask is not null;
            if (compilationActive) ImGui.BeginDisabled();
            if (ImGui.Button("保存", new NumericsVector2(105f, 0f))) SaveModEditor();
            ImGui.SameLine();
            if (!changed || compilationActive) ImGui.BeginDisabled();
            if (ImGui.Button("撤銷未保存", new NumericsVector2(125f, 0f))) _modEditorText = _modEditorSavedText;
            if (!changed || compilationActive) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("回滾上次保存", new NumericsVector2(135f, 0f)))
                ImGui.OpenPopup("確認回滾文字");
            if (compilationActive) ImGui.EndDisabled();
            ImGui.SameLine();
            if (compilationActive) ImGui.BeginDisabled();
            if (ImGui.Button("編譯已保存版本", new NumericsVector2(155f, 0f)))
                StartModCompilation(_modEditorPath);
            if (compilationActive) ImGui.EndDisabled();

            if (ImGui.BeginPopupModal("確認回滾文字", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped("將磁碟內容回滾到這個檔案上次保存前的備份？目前未保存文字也會被捨棄。");
                if (ImGui.Button("確認回滾", new NumericsVector2(115f, 0f)))
                {
                    RollbackModEditor();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("取消", new NumericsVector2(115f, 0f))) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }
        ImGui.End();
        _modEditorVisible = editorOpen;
    }

    private ModWorkspace GetModWorkspace() => _modWorkspace ??= new ModWorkspace(
        BepInExBootstrap.PluginDirectory,
        Infrastructure.RuntimePaths.ManagedDirectory);

    private void RefreshModWorkspace()
    {
        try
        {
            _modWorkspaceEntries = GetModWorkspace().EnumerateFiles();
            _modWorkspaceLoaded = true;
        }
        catch (Exception exception)
        {
            _modWorkspaceEntries = Array.Empty<ModWorkspaceEntry>();
            _modWorkspaceLoaded = true;
            _modStatus = $"讀取模組工作區失敗：{exception.Message}";
        }
    }

    private void OpenModTextEditor(string path)
    {
        try
        {
            string text = GetModWorkspace().ReadText(path);
            _modEditorPath = path;
            _modEditorText = text;
            _modEditorSavedText = text;
            _modEditorVisible = true;
            _modSelectedPath = path;
        }
        catch (Exception exception)
        {
            _modStatus = $"開啟文字失敗：{exception.Message}";
        }
    }

    private void SaveModEditor()
    {
        if (_modEditorPath is null) return;
        try
        {
            string backup = GetModWorkspace().SaveText(_modEditorPath, _modEditorText);
            _modEditorSavedText = _modEditorText;
            _modStatus = $"已保存文字；回滾備份：{backup}";
            RefreshModWorkspace();
        }
        catch (Exception exception)
        {
            _modStatus = $"保存文字失敗：{exception.Message}";
        }
    }

    private void RollbackModEditor()
    {
        if (_modEditorPath is null) return;
        try
        {
            _modEditorText = GetModWorkspace().RollbackText(_modEditorPath);
            _modEditorSavedText = _modEditorText;
            _modStatus = "已回滾到上次保存前的版本。";
            RefreshModWorkspace();
        }
        catch (Exception exception)
        {
            _modStatus = $"回滾失敗：{exception.Message}";
        }
    }

    private void StartModCompilation(string path)
    {
        if (_modCompilationTask is not null) return;
        _modCompilationSource = path;
        _modCompilationLog = string.Empty;
        _modStatus = $"正在手動編譯 {Path.GetFileName(path)}……";
        _modCompilationTask = Task.Run(() => GetModWorkspace().Compile(path));
    }

    private void CompleteModCompilationIfReady()
    {
        if (_modCompilationTask is null || !_modCompilationTask.IsCompleted) return;
        try
        {
            ModCompilationResult result = _modCompilationTask.GetAwaiter().GetResult();
            _modCompilationLog = result.Log;
            if (result.OrganizedLooseFiles)
            {
                if (string.Equals(_modEditorPath, _modCompilationSource, StringComparison.OrdinalIgnoreCase))
                    _modEditorPath = result.SourcePath;
                _modSelectedPath = result.SourcePath;
            }
            _modStatus = result.Success
                ? $"編譯完成：{result.AssemblyPath}。重新啟動遊戲後載入。"
                : $"編譯失敗，原始碼仍保留：{result.Log}";
        }
        catch (Exception exception)
        {
            _modCompilationLog = exception.ToString();
            _modStatus = $"編譯失敗，原始碼仍保留：{exception.Message}";
        }
        finally
        {
            _modCompilationTask = null;
            _modCompilationSource = null;
            RefreshModWorkspace();
        }
    }

    private void MoveModFile()
    {
        if (_modMoveSource is null) return;
        try
        {
            string oldPath = _modMoveSource;
            string movedPath = GetModWorkspace().MoveToFolder(oldPath, _modMoveDestination);
            if (string.Equals(_modEditorPath, oldPath, StringComparison.OrdinalIgnoreCase))
                _modEditorPath = movedPath;
            _modSelectedPath = movedPath;
            _modStatus = $"已移動到 {Path.GetRelativePath(BepInExBootstrap.PluginDirectory, movedPath)}";
            RefreshModWorkspace();
        }
        catch (Exception exception)
        {
            _modStatus = $"移動失敗：{exception.Message}";
        }
        finally
        {
            _modMoveSource = null;
        }
    }

    private void DeleteModFile()
    {
        if (_modDeleteSource is null) return;
        try
        {
            string oldPath = _modDeleteSource;
            string trashPath = GetModWorkspace().MoveToTrash(oldPath);
            if (string.Equals(_modEditorPath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                _modEditorVisible = false;
                _modEditorPath = null;
            }
            if (string.Equals(_modSelectedPath, oldPath, StringComparison.OrdinalIgnoreCase))
                _modSelectedPath = null;
            _modStatus = $"已從 plugins 刪除；可復原檔案位於 {trashPath}";
            RefreshModWorkspace();
        }
        catch (Exception exception)
        {
            _modStatus = $"刪除失敗：{exception.Message}";
        }
        finally
        {
            _modDeleteSource = null;
        }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1024L * 1024L => $"{bytes / (1024f * 1024f):0.0} MiB",
        >= 1024L => $"{bytes / 1024f:0.0} KiB",
        _ => $"{bytes} B",
    };

    private void DrawConfigEntry(LoadedPluginInfo plugin, ConfigEntryBase entry)
    {
        string label = $"{entry.Definition.Section} / {entry.Definition.Key}";
        ImGui.PushID($"{plugin.Guid}:{entry.Definition.Section}:{entry.Definition.Key}");
        try
        {
            object value = entry.BoxedValue;
            bool changed = false;
            if (value is bool boolean)
            {
                changed = ImGui.Checkbox(label, ref boolean);
                if (changed) entry.BoxedValue = boolean;
            }
            else if (value is int integer)
            {
                changed = ImGui.InputInt(label, ref integer);
                if (changed) entry.BoxedValue = integer;
            }
            else if (value is float single)
            {
                changed = ImGui.InputFloat(label, ref single, 0.1f, 1f, "%.3f");
                if (changed) entry.BoxedValue = single;
            }
            else if (value is string text)
            {
                changed = ImGui.InputText(label, ref text, 512);
                if (changed) entry.BoxedValue = text;
            }
            else if (entry.SettingType.IsEnum)
            {
                string[] names = Enum.GetNames(entry.SettingType);
                int selected = Array.IndexOf(names, value.ToString());
                selected = Math.Max(0, selected);
                changed = ImGui.Combo(label, ref selected, names, names.Length);
                if (changed) entry.BoxedValue = Enum.Parse(entry.SettingType, names[selected]);
            }
            else
            {
                ImGui.TextWrapped($"{label}: {value}");
                ImGui.SameLine();
                ImGui.TextDisabled($"({entry.SettingType.Name})");
            }

            if (changed)
            {
                _modStatus = $"Changed {plugin.Name}: {label}.";
            }
            if (!string.IsNullOrWhiteSpace(entry.Description.Description) && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(entry.Description.Description);
            }
        }
        catch (Exception exception)
        {
            _modStatus = $"Config change rejected: {exception.Message}";
        }
        ImGui.PopID();
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            _modStatus = $"Opened {path}";
        }
        catch (Exception exception)
        {
            _modStatus = $"Could not open folder: {exception.Message}";
        }
    }

    private void SeedStartingInventory()
    {
        _inventory.Restore(Array.Empty<ItemStack>());
        FillCreativeInventory();
    }

    private void FillCreativeInventory()
    {
        foreach (BlockDefinition block in _blocks.PlaceableDefinitions())
        {
            if (!_items.TryGet(block.Key, out ItemDefinition? item))
            {
                continue;
            }
            int missing = Math.Max(0, item!.MaxStack - _inventory.Count(item.Key));
            if (missing > 0)
            {
                _inventory.Add(item.Key, missing);
            }
        }
    }

    private void AddStarter(string itemKey, int count)
    {
        if (_items.TryGet(itemKey, out _))
        {
            _inventory.Add(itemKey, count);
        }
    }

    private void OnItemPickedUp(string itemKey, int count)
    {
        string name = _items.TryGet(itemKey, out ItemDefinition? item) ? item!.DisplayName : itemKey;
    }

    private void OnItemCrafted(RecipeDefinition recipe, ItemStack output)
    {
        _progression.NotifyCrafted(recipe, output);
        _survival.AddExhaustion(0.025f);
    }

    private void OnObjectiveCompleted(GameObjective objective)
    {
    }

    private void UseSelectedItem()
    {
        ItemStack stack = _inventory.GetSlot(_inventory.SelectedHotbarSlot);
        if (stack.IsEmpty || !_items.TryGet(stack.ItemKey!, out ItemDefinition? item) || item!.FoodValue <= 0f)
        {
            return;
        }
        if (_survival.Eat(item.FoodValue, item.HealValue))
        {
            // Legacy item hooks may still call this method, but creative inventory is unlimited.
        }
    }

    private bool HasNearbyWorkbench()
    {
        if (!_blocks.TryGet("rtxsandbox:workbench", out BlockDefinition? workbench))
        {
            return false;
        }
        int centerX = (int)MathF.Floor(_player.Position.X);
        int centerY = (int)MathF.Floor(_player.Position.Y);
        int centerZ = (int)MathF.Floor(_player.Position.Z);
        for (int y = centerY - 3; y <= centerY + 3; y++)
        for (int z = centerZ - 4; z <= centerZ + 4; z++)
        for (int x = centerX - 4; x <= centerX + 4; x++)
        {
            if (_world.GetBlockId(x, y, z) == workbench!.Id)
            {
                return true;
            }
        }
        return false;
    }

    private string DisplayItemName(string? itemKey) =>
        !string.IsNullOrWhiteSpace(itemKey) && _items.TryGet(itemKey, out ItemDefinition? item)
            ? item!.DisplayName
            : itemKey ?? "Unknown";

    private void SetUiTime(float hour)
    {
        _clock.SetTime(hour);
        _lastAccumulatedTime = hour;
        _renderer.ResetAccumulation();
    }

    private void SaveCurrentSettings()
    {
        _settings.WindowWidth = ClientSize.X;
        _settings.WindowHeight = ClientSize.Y;
        _settings.VSync = VSync == VSyncMode.On;
        _settings.Renderer = _renderer.Kind;
        _settings.FieldOfView = _camera.FieldOfViewDegrees;
        _settings.TimeOfDay = _clock.TimeOfDayHours;
        _settings.DayLengthMinutes = _clock.DayLengthMinutes;
        _settings.PauseTime = _clock.Paused;
        _settings.StartInFlightMode = _player.FlightMode;
        _settings.StartWithNoClip = _player.NoClip;
        _settings.SelectedBlockKey = _blocks.Get(_buildTools.SelectedBlockId).Key;
        _settings.CreativeHotbar = _creativeHotbar.Snapshot();
        _settings.CreativeHotbarSlot = _creativeHotbar.SelectedSlot;
        _settings.FavoriteBlocks = _favoriteBlocks.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        _settings.RecentBlocks = _recentBlocks.ToArray();
        _settings.EditorTool = _buildTools.ToolMode == EditorToolMode.Custom ? "Place" : _buildTools.ToolMode.ToString();
        _settings.BrushRadius = _buildTools.BrushRadius;
        _settings.ExplosionRadius = _buildTools.ExplosionRadius;
        _settings.PrecisionGridDivisor = _microBuild.GridDivisor;
        _settings.Save();
    }

    public bool SaveWorld()
    {
        try
        {
            _worldSaves.Save(
                _world,
                _player.Position,
                _clock.TimeOfDayHours,
                _inventory,
                _survival.Health,
                _survival.Oxygen,
                _survival.Hunger,
                _progression.SnapshotCompletedIds(),
                _progression.SawNight);
            _saveStatus = $"Saved at {DateTime.Now:HH:mm:ss}";
            _autosaveTimer = 0.0;
            RefreshWorldList();
            return true;
        }
        catch (Exception exception)
        {
            _saveStatus = $"Save failed: {exception.Message}";
            _autosaveTimer = 0.0;
            Console.Error.WriteLine(_saveStatus);
            return false;
        }
    }

    public void LoadWorld()
    {
        if (_worldSaves.TryLoad(_world, out SavedWorldState state, out string? error))
        {
            ApplyLoadedWorldState(state);
            _saveStatus = $"Reloaded at {DateTime.Now:HH:mm:ss}";
        }
        else
        {
            _saveStatus = error is null ? "No saved world exists yet." : $"Load failed: {error}";
        }
    }

    private void ApplyLoadedWorldState(SavedWorldState state)
    {
        _player.Teleport(state.PlayerPosition);
        _streamedPlayerChunk = null;
        StreamWorldAroundPlayer();
        _inventory.Restore(state.Inventory, state.SelectedHotbarSlot);
        if (state.Inventory.Count == 0) SeedStartingInventory();
        _survival.Restore(state.Health, state.Oxygen, state.Hunger);
        _survival.SetSpawn(FindWorldSpawn());
        _progression.Restore(state.CompletedObjectives, state.SawNight);
        _buildTools.ResetForNewWorld();
        _microBuild.ResetForNewWorld();
        _droppedItems.Clear();
        _clock.SetTime(state.TimeOfDay);
        _lastAccumulatedTime = state.TimeOfDay;
        _renderer.ResetAccumulation();
        _physics.RequestEvaluation();
        _worldDraft = _world.GenerationSettings.Clone();
        _worldNameDraft = _worldSaves.DisplayName;
        _autosaveTimer = 0.0;
        UpdateTitle();
        UpdateSelection();
        _modHost.PublishWorldReady();
    }

    private void UpdateTitle()
    {
        Title = $"RTX Sandbox — {_worldSaves.DisplayName}";
    }
}
