using System.Diagnostics;
using System.Collections.Concurrent;
using System.Numerics;
using Android.Content;
using Android.Opengl;
using Android.Views;
using ImGuiNET;
using Java.Nio;
using Javax.Microedition.Khronos.Egl;
using Javax.Microedition.Khronos.Opengles;
using RTXSandbox.ModAPI;
using RTXSandbox.Android.TouchControls;
using RTXSandbox.Configuration;
using RTXSandbox.Gameplay;
using RTXSandbox.Infrastructure.Platform;
using RTXSandbox.Modding;
using RTXSandbox.Persistence;
using RTXSandbox.Rendering;
using RTXSandbox.World;
using RTXSandbox.UI;

namespace RTXSandbox.Android.Game;

public sealed class AndroidGameSurface : GLSurfaceView
{
    private readonly AndroidGameRenderer _renderer;

    public AndroidGameSurface(
        Context context,
        VirtualInputState input,
        string baseContentPath,
        string contentPacksPath,
        IPlatformStorage storage,
        Action<string> status) : base(context)
    {
        var activityManager = context.GetSystemService(Context.ActivityService) as global::Android.App.ActivityManager;
        int requestedGles = (activityManager?.DeviceConfigurationInfo?.ReqGlEsVersion ?? 0) >= 0x00030000 ? 3 : 2;
        SetEGLContextClientVersion(requestedGles);
        SetEGLConfigChooser(8, 8, 8, 8, 24, 0);
        PreserveEGLContextOnPause = true;
        _renderer = new AndroidGameRenderer(input, baseContentPath, contentPacksPath, storage, status);
        SetRenderer(_renderer);
        RenderMode = Rendermode.Continuously;
    }

    public event Action<AndroidGameUiState> UiStateChanged
    {
        add => _renderer.UiStateChanged += value;
        remove => _renderer.UiStateChanged -= value;
    }

    public event Action ModImportRequested
    {
        add => _renderer.ModImportRequested += value;
        remove => _renderer.ModImportRequested -= value;
    }

    public event Action ModStorageAccessRequested
    {
        add => _renderer.ModStorageAccessRequested += value;
        remove => _renderer.ModStorageAccessRequested -= value;
    }

    public IReadOnlyList<AndroidBlockChoice> BlockChoices => _renderer.BlockChoices;
    public IReadOnlyList<AndroidRendererChoice> RendererChoices => _renderer.RendererChoices;
    public void RequestUiMode(AndroidGameUiMode mode) => _renderer.RequestUiMode(mode);
    public void SelectBlock(ushort id) => _renderer.SelectBlock(id);
    public void SelectRenderer(AndroidRendererMode mode) => _renderer.SelectRenderer(mode);
    public void SaveWorld() => _renderer.SaveWorld();
    public void QueueWorldSave() => _renderer.QueueWorldSave();
    public void RequestPhysicsSmokeTest() => _renderer.RequestPhysicsSmokeTest();

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        bool down = e.ActionMasked is not MotionEventActions.Up and not MotionEventActions.Cancel;
        _renderer.SetUiPointer(e.GetX(), e.GetY(), down);
        return true;
    }
}

internal sealed class AndroidGameRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
    // Position, normal, fallback albedo, metallic, roughness, alpha,
    // tiled UV + material/face, and emission. Keep the last two groups packed
    // into two attributes so GLES 2 devices stay within the guaranteed eight
    // vertex-attribute slots.
    private const int FloatsPerVertex = 19;
    private const int ComputeWindowSize = 64;
    private const int ComputeWindowHeight = 256;
    private const int ShadowMapSize = 1024;
    private const double MeshUpdateIntervalSeconds = 1.0 / 60.0;
    private readonly VirtualInputState _input;
    private readonly Action<string> _status;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly BlockRegistry _blocks;
    private readonly VoxelWorld _world;
    private readonly PlayerController _player;
    private readonly Camera _camera = new();
    private WorldSaveService _worldSaves;
    private readonly string _saveRoot;
    private readonly CreativeHotbar _hotbar;
    private readonly ItemRegistry _items;
    private readonly RecipeRegistry _recipes;
    private readonly BuildToolRegistry _tools;
    private readonly ModHost _modHost;
    private readonly ISharedPreferences _uiPreferences;
    private readonly WorldClock _worldClock;
    private readonly PlayerInventory _inventory;
    private readonly MicroBuildSystem _microBuild;
    private readonly WaterFlowSimulation _waterFlow;
    private readonly VoxelPhysicsSimulator _physics;
    private readonly Dictionary<ChunkCoordinate, GpuMesh> _chunkMeshes = new();
    private readonly HashSet<ChunkCoordinate> _dirtyMeshChunks = new();
    private readonly Dictionary<ChunkCoordinate, int> _chunkMeshVersions = new();
    private readonly object _stateLock = new();
    private readonly ConcurrentQueue<Action> _requests = new();
    private AndroidGameUiMode _uiMode;
    private AndroidGameUiMode _uiReturnMode = AndroidGameUiMode.Pause;
    private AndroidRendererMode _rendererMode;
    private bool _precisionMode;
    private bool _meshDirty = true;
    private double _lastMeshUpload;
    private bool _interactiveMeshUpdateRequested;
    private string _glVersion = "OpenGL ES";
    private long _lastTicks;
    private double _fpsWindowStart;
    private int _fpsFrames;
    private int _width = 1;
    private int _height = 1;
    private int _program;
    private int _transparentProgram;
    private int _lightingProgram;
    private int _gbufferFramebuffer;
    private int _gbufferAlbedoTexture;
    private int _gbufferNormalTexture;
    private int _gbufferLightingTexture;
    private int _gbufferDepthTexture;
    private int _gbufferWidth;
    private int _gbufferHeight;
    private bool _supportsEs3;
    private bool _supportsCompute;
    private int _computeProgram;
    private int _computeDisplayProgram;
    private int _computeOutputTexture;
    private int _computeVoxelTexture;
    private int _computeFluidTexture;
    private int _computeMaterialTexture;
    private int _materialAtlasTexture;
    private int _materialDetailAtlasTexture;
    private int _materialAtlasWidth;
    private int _materialAtlasHeight;
    private const int MaterialAtlasTilesPerRow = 32;
    private int _shadowProgram;
    private int _shadowFramebuffer;
    private int _shadowColorTexture;
    private int _shadowDepthBuffer;
    private Matrix4x4 _shadowMatrix;
    private ChunkCoordinate? _shadowCenter;
    private bool _shadowDirty = true;
    private int _computeOriginX;
    private int _computeOriginY;
    private int _computeOriginZ;
    private bool _computeWorldDirty = true;
    private double _lastComputeWorldUpload;
    private int _computeTargetWidth;
    private int _computeTargetHeight;
    private GpuMesh? _gridMesh;
    private bool _gridMeshDirty = true;
    private int _vertexCount;
    private int _opaqueVertexCount;
    private int _transparentVertexCount;
    private ChunkCoordinate? _meshCenter;
    private Task<PendingChunkMesh>? _pendingChunkMeshBuild;
    private int _mvpLocation;
    private AndroidImGuiController? _imgui;
    private float _uiPointerX = -10000f;
    private float _uiPointerY = -10000f;
    private int _uiPointerDown;
    private int _uiTapPhase;
    private UiLanguage _language = UiLanguage.TraditionalChinese;
    private long _lastForwardPressTicks;
    private bool _doubleTapSprint;
    private float _sprintFovOffset;
    private float _baseFieldOfView;
    private float _lookSensitivity;
    private float _selectionDistance;
    private int _streamingRadius;
    private int _settingsTab = -1;
    private string _paletteSearch = string.Empty;
    private readonly HashSet<string> _favoriteBlocks = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WorldSaveSummary> _knownWorlds = Array.Empty<WorldSaveSummary>();
    private string _selectedWorldStorage = "world1";
    private string _worldRenameDraft = "world1";
    private string _worldNameDraft = "New World";
    private int _worldSeedDraft = 1337;
    private string _worldMenuStatus = string.Empty;
    private double _maximumUiMilliseconds;
    private int _saveRequested;
    private int _saveWorkerRunning;
    private double _nextHeldDestroyAt;

    public AndroidGameRenderer(
        VirtualInputState input,
        string baseContentPath,
        string contentPacksPath,
        IPlatformStorage storage,
        Action<string> status)
    {
        _input = input;
        _status = status;
        _uiPreferences = global::Android.App.Application.Context
            .GetSharedPreferences("rtxsandbox-ui", FileCreationMode.Private)!;
        string? storedLanguage = _uiPreferences.GetString("language", UiLanguage.TraditionalChinese.ToString());
        if (Enum.TryParse(storedLanguage, out UiLanguage language)) _language = language;
        _baseFieldOfView = Math.Clamp(_uiPreferences.GetFloat("field-of-view", 72f), 45f, 105f);
        _lookSensitivity = Math.Clamp(_uiPreferences.GetFloat("look-sensitivity", 1f), 0.2f, 2f);
        _selectionDistance = Math.Clamp(_uiPreferences.GetFloat("selection-distance", 10f), 3f, 16f);
        _streamingRadius = Math.Clamp(_uiPreferences.GetInt("streaming-radius", 2), 2, 8);
        _blocks = new BlockRegistry(baseContentPath, contentPacksPath);
        _world = new VoxelWorld(generate: false, registry: _blocks);
        _saveRoot = storage.SavesRoot;
        _worldSaves = new WorldSaveService(
            _blocks,
            preferredSeed: 1337,
            worldName: "world1",
            saveRoot: storage.SavesRoot);
        bool loaded = _worldSaves.TryLoad(_world, out SavedWorldState state, out string? error);
        if (!loaded)
            _world.Regenerate(WorldGenerationSettings.Natural(1337));
        _worldClock = new WorldClock(
            loaded ? state.TimeOfDay : 8.5f,
            Math.Clamp(_uiPreferences.GetFloat("day-length-minutes", 12f), 1f, 120f),
            _uiPreferences.GetBoolean("time-paused", false));
        int spawnX = VoxelWorld.Width / 2;
        int spawnZ = VoxelWorld.Depth / 2 + 8;
        float spawnY = _world.FindTopVoxelY(spawnX, spawnZ) + 1.001f;
        Vector3 spawn = loaded ? state.PlayerPosition : new Vector3(spawnX + 0.5f, spawnY, spawnZ + 0.5f);
        _player = new PlayerController(_world, spawn);
        _microBuild = new MicroBuildSystem(_world, _blocks, _player.WouldIntersectAabb);
        _waterFlow = new WaterFlowSimulation(_world);
        _physics = new VoxelPhysicsSimulator(_world, _blocks);
        _physics.SimulationEnabled = _uiPreferences.GetBoolean("physics-enabled", true);
        _items = new ItemRegistry(_blocks);
        _recipes = new RecipeRegistry();
        _tools = new BuildToolRegistry();
        _modHost = new ModHost(this, _blocks, _items, _recipes, _tools, _world);
        _modHost.AttachApi();
        _modHost.PublishWorldReady();
        _hotbar = new CreativeHotbar(_blocks);
        _inventory = new PlayerInventory(_items);
        if (loaded && state.Inventory.Count > 0)
        {
            _inventory.Restore(state.Inventory, state.SelectedHotbarSlot);
        }
        else
        {
            foreach (BlockDefinition block in _blocks.PlaceableDefinitions().Take(_inventory.SlotCount))
                _inventory.Add(block.Key, 64);
        }
        BlockChoices = _blocks.PlaceableDefinitions()
            .Select(block => new AndroidBlockChoice(block.Id, block.Key, block.DisplayName))
            .ToArray();
        _world.VoxelChanged += (x, _, z, _) =>
        {
            _computeWorldDirty = true;
            MarkChunkAndNeighborsDirty(x, z);
        };
        _world.ChunkGenerated += coordinate =>
        {
            _computeWorldDirty = true;
            MarkChunkAndNeighborsDirty(coordinate);
        };
        _world.MicroVoxelCellChanged += (x, _, z) => MarkChunkAndNeighborsDirty(x, z);
        _world.MicroVoxelsReloaded += MarkAllChunkMeshesDirty;
        _world.FluidLevelChanged += (x, _, z) =>
        {
            _computeWorldDirty = true;
            MarkChunkAndNeighborsDirty(x, z);
        };
        _world.PhysicsGrids.Changed += () =>
        {
            _gridMeshDirty = true;
            _meshDirty = true;
        };
        _status(loaded
            ? $"已載入世界：{Path.GetFileName(_worldSaves.LastLoadSource)}"
            : error is null
                ? "已建立新的世界。"
                : $"世界載入失敗，已建立新世界：{error}");
        RefreshWorldList();
    }

    public event Action<AndroidGameUiState>? UiStateChanged;
    public event Action? ModImportRequested;
    public event Action? ModStorageAccessRequested;

    public IReadOnlyList<AndroidBlockChoice> BlockChoices { get; }

    public IReadOnlyList<AndroidRendererChoice> RendererChoices =>
    [
        new("Classic", "經典光照", true, "OpenGL ES raster"),
        new("PbrForward", "PBR 前向光照", true, "OpenGL ES forward material lighting"),
        new("Deferred", "延遲渲染", _supportsEs3,
            _supportsEs3 ? "OpenGL ES 3 MRT G-buffer" : "需要 OpenGL ES 3.0 MRT"),
        new("Hybrid", "混合式 PBR", _supportsEs3,
            _supportsEs3 ? "Deferred geometry + screen-space environment pass" : "需要 OpenGL ES 3.0 MRT"),
        new("ComputePathTracing", "Compute 路徑追蹤", _supportsCompute,
            _supportsCompute ? "OpenGL ES 3.1 compute voxel ray tracing" : "需要 OpenGL ES 3.1 compute shader"),
        new("HardwareRayTracing", "硬體光線追蹤", false, "DXR 僅支援 Windows Direct3D 12"),
    ];

    public void RequestUiMode(AndroidGameUiMode mode) => _requests.Enqueue(() => SetUiMode(mode));
    public void SetUiPointer(float x, float y, bool down)
    {
        _uiPointerX = x;
        _uiPointerY = y;
        if (down && Volatile.Read(ref _uiPointerDown) == 0)
            Interlocked.CompareExchange(ref _uiTapPhase, 1, 0);
        Volatile.Write(ref _uiPointerDown, down ? 1 : 0);
    }
    public void SelectBlock(ushort id) => _requests.Enqueue(() =>
    {
        if (_hotbar.SelectAndAssign(id)) PublishUiState();
    });
    public void SelectRenderer(AndroidRendererMode mode) => _requests.Enqueue(() =>
    {
        if (_rendererMode == mode) return;
        AndroidRendererChoice? choice = RendererChoices.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, mode.ToString(), StringComparison.OrdinalIgnoreCase));
        if (choice is null || !choice.Available)
        {
            _status(choice?.Reason ?? $"Renderer {mode} is unavailable on this device");
            return;
        }
        _rendererMode = mode;
        RecreateProgram();
        PublishUiState();
    });

    public void SaveWorld()
    {
        lock (_stateLock)
            _worldSaves.Save(
                _world,
                _player.Position,
                _worldClock.TimeOfDayHours,
                _inventory,
                health: 20f,
                oxygen: 10f);
    }

    public void QueueWorldSave()
    {
        Interlocked.Exchange(ref _saveRequested, 1);
        if (Interlocked.CompareExchange(ref _saveWorkerRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _saveRequested, 0);
                    try
                    {
                        SaveWorld();
                    }
                    catch (Exception exception)
                    {
                        _status($"Android background save failed safely: {exception.Message}");
                    }
                }
                while (Interlocked.Exchange(ref _saveRequested, 0) != 0);
            }
            finally
            {
                Volatile.Write(ref _saveWorkerRunning, 0);
                if (Volatile.Read(ref _saveRequested) != 0) QueueWorldSave();
            }
        });
    }

    public void RequestPhysicsSmokeTest() => _requests.Enqueue(() =>
    {
        ushort materialId = _blocks.TryGet("rtxsandbox:metal", out BlockDefinition? metal)
            ? metal!.Id
            : (ushort)VoxelMaterial.Stone;
        Vector3 forward = Vector3.Normalize(new Vector3(_camera.Forward.X, 0f, _camera.Forward.Z));
        Vector3 location = _player.Position + forward * 4f;
        location.X = MathF.Floor(location.X) + 0.5f;
        location.Z = MathF.Floor(location.Z) + 0.5f;
        location.Y = _world.FindTopVoxelY((int)location.X, (int)location.Z) + 6f;
        _world.PhysicsGrids.Create(
            [new PhysicsGridElement(0, 0, 0, Vector3.Zero, Vector3.One, materialId)],
            location - new Vector3(0.5f));
        _status("Physics Grid smoke queued: one dynamic cube");
    });

    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        _glVersion = GLES20.GlGetString(GLES20.GlVersion) ?? "OpenGL ES";
        _supportsEs3 = _glVersion.StartsWith("OpenGL ES 3.", StringComparison.OrdinalIgnoreCase);
        _supportsCompute = _glVersion.StartsWith("OpenGL ES 3.1", StringComparison.OrdinalIgnoreCase) ||
                           _glVersion.StartsWith("OpenGL ES 3.2", StringComparison.OrdinalIgnoreCase);
        RecreateProgram();
        CreateMaterialAtlasTexture();
        _chunkMeshes.Clear();
        _pendingChunkMeshBuild = null;
        _gridMesh = null;
        _meshCenter = null;
        MarkAllChunkMeshesDirty();
        _gridMeshDirty = true;
        UploadWorldMesh();
        GLES20.GlEnable(GLES20.GlDepthTest);
        GLES20.GlEnable(0x0B44); // GL_CULL_FACE; binding name collides with GlCullFace().
        GLES20.GlCullFace(GLES20.GlBack);
        _lastTicks = _clock.ElapsedTicks;
        _fpsWindowStart = _clock.Elapsed.TotalSeconds;
        _status($"Classic 已載入 · {_glVersion}");
        try
        {
            _imgui = new AndroidImGuiController(
                global::Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 1f,
                AndroidUiGlyphText);
        }
        catch (Exception exception)
        {
            _status($"Android ImGui 初始化失敗：{exception.Message}");
        }
        PublishUiState();
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        GLES20.GlViewport(0, 0, _width, _height);
        if (_rendererMode is AndroidRendererMode.Deferred or AndroidRendererMode.Hybrid)
            EnsureGBuffer();
        if (_rendererMode == AndroidRendererMode.ComputePathTracing)
            EnsureComputeTargets();
    }

    public void OnDrawFrame(IGL10? gl)
    {
        while (_requests.TryDequeue(out Action? request)) request();
        long now = _clock.ElapsedTicks;
        float deltaTime = Math.Clamp((now - _lastTicks) / (float)Stopwatch.Frequency, 0f, 0.05f);
        _lastTicks = now;

        HandleUiActions();
        lock (_stateLock)
        {
            if (_uiMode == AndroidGameUiMode.Gameplay)
            {
                (float lookX, float lookY) = _input.ConsumeLookDelta();
                if (lookX != 0f || lookY != 0f)
                    _camera.Rotate(new Vector2(lookX, lookY), _lookSensitivity);

                (float moveX, float moveY) = _input.GetMovement();
                var movement = new Vector2(Math.Clamp(moveX, -1f, 1f), Math.Clamp(moveY, -1f, 1f));
                bool jump = _input.IsDown(VirtualBindingKind.Keyboard, "Space");
                if (_input.ConsumePressed(VirtualBindingKind.Keyboard, "W"))
                {
                    long pressedAt = _clock.ElapsedTicks;
                    double elapsed = _lastForwardPressTicks == 0
                        ? double.MaxValue
                        : (pressedAt - _lastForwardPressTicks) / (double)Stopwatch.Frequency;
                    _doubleTapSprint = elapsed <= 0.30;
                    _lastForwardPressTicks = pressedAt;
                }
                bool forwardDown = moveY > 0.01f;
                if (!forwardDown) _doubleTapSprint = false;
                bool crouch = _input.IsDown(VirtualBindingKind.GameAction, "Crouch") ||
                               _input.IsDown(VirtualBindingKind.Keyboard, "Shift");
                bool sprint = forwardDown && !crouch &&
                              (_input.IsDown(VirtualBindingKind.Keyboard, "Ctrl") || _doubleTapSprint);
                _player.Update(deltaTime, movement, _camera.Forward, sprint, jump, crouch: crouch);
                _waterFlow.Update(deltaTime);
                _physics.SetSimulationFocus(_player.Position);
                _physics.Update(deltaTime);
                _modHost.Update(deltaTime);
                _worldClock.Update(deltaTime);
                float targetFov = sprint ? 7f : 0f;
                float blend = 1f - MathF.Exp(-10f * deltaTime);
                _sprintFovOffset += (targetFov - _sprintFovOffset) * blend;
                _camera.FieldOfViewDegrees = _baseFieldOfView + _sprintFovOffset;
                HandleBlockActions();
                _world.QueueChunksAround(_player.Position, radius: _streamingRadius);
                if (_world.ProcessPendingChunkGeneration(maximumChunks: 1) > 0) _meshDirty = true;
                ChunkCoordinate currentCenter = ChunkCoordinate.FromVoxel(
                    (int)MathF.Floor(_player.Position.X),
                    (int)MathF.Floor(_player.Position.Z));
                if (_meshCenter != currentCenter) _meshDirty = true;
            }
            else
            {
                _input.ConsumeLookDelta();
            }

            double meshElapsed = _clock.Elapsed.TotalSeconds - _lastMeshUpload;
            if (_meshDirty && (_interactiveMeshUpdateRequested || meshElapsed >= MeshUpdateIntervalSeconds))
            {
                UploadWorldMesh();
                _lastMeshUpload = _clock.Elapsed.TotalSeconds;
                _interactiveMeshUpdateRequested = false;
            }
        }

        Vector3 eye = _player.EyePosition;
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye + _camera.Forward, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 180f * _camera.FieldOfViewDegrees,
            _width / (float)_height,
            0.05f,
            96f);
        // System.Numerics stores row-vector matrices in row-major order. GLES reads
        // this float array as column-major, which supplies the required transpose.
        Matrix4x4 matrix = view * projection;
        if (_rendererMode == AndroidRendererMode.ComputePathTracing && _supportsCompute)
            RenderComputePathTracing(eye);
        else if (_rendererMode is AndroidRendererMode.Deferred or AndroidRendererMode.Hybrid && _supportsEs3)
            RenderDeferred(matrix, eye, _rendererMode == AndroidRendererMode.Hybrid);
        else
            RenderForward(matrix, eye);
        long uiStart = Stopwatch.GetTimestamp();
        DrawImGui(deltaTime);
        _maximumUiMilliseconds = Math.Max(_maximumUiMilliseconds,
            Stopwatch.GetElapsedTime(uiStart).TotalMilliseconds);
        UpdateFps();
    }

    private void RenderForward(Matrix4x4 matrix, Vector3 eye)
    {
        RenderDirectionalShadow(eye);
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
        GLES20.GlViewport(0, 0, _width, _height);
        float daylight = DaylightStrength();
        GLES20.GlClearColor(0.32f * daylight, 0.58f * daylight, 0.86f * daylight, 1f);
        GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);
        DrawScenePass(_program, matrix, eye, includeTransparent: true);
    }

    private void RenderComputePathTracing(Vector3 eye)
    {
        EnsureComputeTargets();
        int desiredOriginX = (int)MathF.Floor(eye.X) - ComputeWindowSize / 2;
        int desiredOriginZ = (int)MathF.Floor(eye.Z) - ComputeWindowSize / 2;
        bool movedWindow = Math.Abs(desiredOriginX - _computeOriginX) >= ChunkCoordinate.Size ||
                           Math.Abs(desiredOriginZ - _computeOriginZ) >= ChunkCoordinate.Size;
        if ((_computeWorldDirty || movedWindow) && _clock.Elapsed.TotalSeconds - _lastComputeWorldUpload >= 0.25)
        {
            UploadComputeWorld(eye);
            _lastComputeWorldUpload = _clock.Elapsed.TotalSeconds;
        }

        Vector3 forward = Vector3.Normalize(_camera.Forward);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        GLES31.GlUseProgram(_computeProgram);
        SetVector3Uniform(_computeProgram, "uCamera", eye);
        SetVector3Uniform(_computeProgram, "uForward", forward);
        SetVector3Uniform(_computeProgram, "uRight", right);
        SetVector3Uniform(_computeProgram, "uUp", up);
        GLES31.GlUniform2f(GLES31.GlGetUniformLocation(_computeProgram, "uResolution"), _width, _height);
        GLES31.GlUniform1f(GLES31.GlGetUniformLocation(_computeProgram, "uTanHalfFov"),
            MathF.Tan(_camera.FieldOfViewDegrees * MathF.PI / 360f));
        GLES31.GlUniform1f(GLES31.GlGetUniformLocation(_computeProgram, "uDaylight"), DaylightStrength());
        GLES31.GlUniform3i(GLES31.GlGetUniformLocation(_computeProgram, "uVoxelOrigin"),
            _computeOriginX, _computeOriginY, _computeOriginZ);
        GLES31.GlActiveTexture(GLES31.GlTexture0);
        GLES31.GlBindTexture(GLES30.GlTexture3d, _computeVoxelTexture);
        GLES31.GlUniform1i(GLES31.GlGetUniformLocation(_computeProgram, "uVoxels"), 0);
        GLES31.GlActiveTexture(GLES31.GlTexture1);
        GLES31.GlBindTexture(GLES31.GlTexture2d, _computeMaterialTexture);
        GLES31.GlUniform1i(GLES31.GlGetUniformLocation(_computeProgram, "uMaterials"), 1);
        GLES31.GlActiveTexture(GLES31.GlTexture2);
        GLES31.GlBindTexture(GLES30.GlTexture3d, _computeFluidTexture);
        GLES31.GlUniform1i(GLES31.GlGetUniformLocation(_computeProgram, "uFluidLevels"), 2);
        GLES31.GlUniform1i(GLES31.GlGetUniformLocation(_computeProgram, "uWaterMaterial"),
            (int)VoxelMaterial.Water);
        GLES31.GlBindImageTexture(0, _computeOutputTexture, 0, false, 0, GLES31.GlWriteOnly, GLES31.GlRgba8);
        GLES31.GlDispatchCompute((_width + 7) / 8, (_height + 7) / 8, 1);
        GLES31.GlMemoryBarrier(GLES31.GlShaderImageAccessBarrierBit | GLES31.GlTextureFetchBarrierBit);

        GLES31.GlBindFramebuffer(GLES31.GlFramebuffer, 0);
        GLES31.GlViewport(0, 0, _width, _height);
        GLES31.GlDisable(GLES31.GlDepthTest);
        GLES31.GlUseProgram(_computeDisplayProgram);
        GLES31.GlActiveTexture(GLES31.GlTexture0);
        GLES31.GlBindTexture(GLES31.GlTexture2d, _computeOutputTexture);
        GLES31.GlUniform1i(GLES31.GlGetUniformLocation(_computeDisplayProgram, "uImage"), 0);
        GLES31.GlDrawArrays(GLES31.GlTriangles, 0, 3);
        GLES31.GlEnable(GLES31.GlDepthTest);
    }

    private static void SetVector3Uniform(int program, string name, Vector3 value)
    {
        int location = GLES20.GlGetUniformLocation(program, name);
        if (location >= 0) GLES20.GlUniform3f(location, value.X, value.Y, value.Z);
    }

    private float DaylightStrength()
    {
        float angle = (_worldClock.TimeOfDayHours - 6f) / 12f * MathF.PI;
        return Math.Clamp(MathF.Sin(angle), 0.035f, 1f);
    }

    private void BindShadowUniforms(int program)
    {
        int matrixLocation = GLES20.GlGetUniformLocation(program, "uLightMvp");
        if (matrixLocation < 0) return;
        GLES20.GlUniformMatrix4fv(matrixLocation, 1, false, ToArray(_shadowMatrix), 0);
        GLES20.GlActiveTexture(GLES20.GlTexture3);
        GLES20.GlBindTexture(GLES20.GlTexture2d, _shadowColorTexture);
        int sampler = GLES20.GlGetUniformLocation(program, "uShadowMap");
        if (sampler >= 0) GLES20.GlUniform1i(sampler, 3);
        int texel = GLES20.GlGetUniformLocation(program, "uShadowTexel");
        if (texel >= 0) GLES20.GlUniform2f(texel, 1f / ShadowMapSize, 1f / ShadowMapSize);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
    }

    private void BindMaterialUniforms(int program)
    {
        GLES20.GlActiveTexture(GLES20.GlTexture4);
        GLES20.GlBindTexture(GLES20.GlTexture2d, _materialAtlasTexture);
        int colorSampler = GLES20.GlGetUniformLocation(program, "uMaterialAtlas");
        if (colorSampler >= 0) GLES20.GlUniform1i(colorSampler, 4);

        GLES20.GlActiveTexture(GLES20.GlTexture5);
        GLES20.GlBindTexture(GLES20.GlTexture2d, _materialDetailAtlasTexture);
        int detailSampler = GLES20.GlGetUniformLocation(program, "uMaterialDetailAtlas");
        if (detailSampler >= 0) GLES20.GlUniform1i(detailSampler, 5);

        int atlasSize = GLES20.GlGetUniformLocation(program, "uMaterialAtlasSize");
        if (atlasSize >= 0)
            GLES20.GlUniform2f(atlasSize, _materialAtlasWidth, _materialAtlasHeight);
        int time = GLES20.GlGetUniformLocation(program, "uTime");
        if (time >= 0) GLES20.GlUniform1f(time, (float)_clock.Elapsed.TotalSeconds);
        int water = GLES20.GlGetUniformLocation(program, "uWaterMaterial");
        if (water >= 0) GLES20.GlUniform1f(water, (int)VoxelMaterial.Water);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
    }

    private void RenderDirectionalShadow(Vector3 eye)
    {
        EnsureShadowResources();
        if (_shadowFramebuffer == 0) return;
        ChunkCoordinate center = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Z));
        if (_shadowCenter != center)
        {
            _shadowCenter = center;
            _shadowDirty = true;
        }
        if (!_shadowDirty) return;

        Vector3 lightDirection = Vector3.Normalize(new Vector3(0.45f, 0.82f, 0.32f));
        Vector3 focus = new(
            center.MinimumX + ChunkCoordinate.Size * 0.5f,
            Math.Clamp(eye.Y - 8f, VoxelWorld.MinimumY + 24f, VoxelWorld.MaximumY - 24f),
            center.MinimumZ + ChunkCoordinate.Size * 0.5f);
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(focus + lightDirection * 72f, focus, Vector3.UnitY);
        Matrix4x4 lightProjection = Matrix4x4.CreateOrthographic(96f, 96f, 1f, 176f);
        _shadowMatrix = lightView * lightProjection;

        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _shadowFramebuffer);
        GLES20.GlViewport(0, 0, ShadowMapSize, ShadowMapSize);
        GLES20.GlClearColor(1f, 1f, 1f, 1f);
        GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);
        GLES20.GlEnable(GLES20.GlDepthTest);
        GLES20.GlDisable(GLES20.GlBlend);
        GLES20.GlCullFace(GLES20.GlFront);
        GLES20.GlUseProgram(_shadowProgram);
        int matrix = GLES20.GlGetUniformLocation(_shadowProgram, "uLightMvp");
        GLES20.GlUniformMatrix4fv(matrix, 1, false, ToArray(_shadowMatrix), 0);
        foreach (GpuMesh mesh in _chunkMeshes.Values) DrawMesh(mesh, transparent: false);
        if (_gridMesh is not null) DrawMesh(_gridMesh, transparent: false);
        DisableSceneAttributes();
        GLES20.GlCullFace(GLES20.GlBack);
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
        _shadowDirty = false;
    }

    private void EnsureShadowResources()
    {
        if (_shadowFramebuffer != 0) return;
        if (_shadowProgram == 0)
            _shadowProgram = CreateProgram(ShadowVertexShader, ShadowFragmentShader);

        int[] textures = new int[1];
        GLES20.GlGenTextures(1, textures, 0);
        _shadowColorTexture = textures[0];
        GLES20.GlBindTexture(GLES20.GlTexture2d, _shadowColorTexture);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlNearest);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlNearest);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba,
            ShadowMapSize, ShadowMapSize, 0, GLES20.GlRgba, GLES20.GlUnsignedByte, null);

        int[] renderbuffers = new int[1];
        GLES20.GlGenRenderbuffers(1, renderbuffers, 0);
        _shadowDepthBuffer = renderbuffers[0];
        GLES20.GlBindRenderbuffer(GLES20.GlRenderbuffer, _shadowDepthBuffer);
        GLES20.GlRenderbufferStorage(GLES20.GlRenderbuffer, GLES20.GlDepthComponent16,
            ShadowMapSize, ShadowMapSize);

        int[] framebuffers = new int[1];
        GLES20.GlGenFramebuffers(1, framebuffers, 0);
        _shadowFramebuffer = framebuffers[0];
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, _shadowFramebuffer);
        GLES20.GlFramebufferTexture2D(GLES20.GlFramebuffer, GLES20.GlColorAttachment0,
            GLES20.GlTexture2d, _shadowColorTexture, 0);
        GLES20.GlFramebufferRenderbuffer(GLES20.GlFramebuffer, GLES20.GlDepthAttachment,
            GLES20.GlRenderbuffer, _shadowDepthBuffer);
        int status = GLES20.GlCheckFramebufferStatus(GLES20.GlFramebuffer);
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
        if (status != GLES20.GlFramebufferComplete)
        {
            _status($"Android shadow map unavailable: FBO 0x{status:X}");
            _shadowFramebuffer = 0;
        }
    }

    private void RenderDeferred(Matrix4x4 matrix, Vector3 eye, bool hybrid)
    {
        RenderDirectionalShadow(eye);
        EnsureGBuffer();
        if (_gbufferFramebuffer == 0)
        {
            RenderForward(matrix, eye);
            return;
        }

        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _gbufferFramebuffer);
        GLES30.GlViewport(0, 0, _width, _height);
        GLES30.GlDrawBuffers(3,
            new[] { GLES30.GlColorAttachment0, GLES30.GlColorAttachment1, GLES30.GlColorAttachment2 }, 0);
        GLES30.GlClearColor(0f, 0f, 0f, 0f);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
        DrawScenePass(_program, matrix, eye, includeTransparent: false);

        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
        GLES30.GlViewport(0, 0, _width, _height);
        GLES30.GlDisable(GLES30.GlDepthTest);
        float daylight = DaylightStrength();
        GLES30.GlClearColor(0.32f * daylight, 0.58f * daylight, 0.86f * daylight, 1f);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
        GLES30.GlUseProgram(_lightingProgram);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _gbufferAlbedoTexture);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_lightingProgram, "uAlbedoMetal"), 0);
        GLES30.GlActiveTexture(GLES30.GlTexture1);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _gbufferNormalTexture);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_lightingProgram, "uNormalRough"), 1);
        GLES30.GlActiveTexture(GLES30.GlTexture2);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _gbufferLightingTexture);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_lightingProgram, "uLighting"), 2);
        GLES30.GlActiveTexture(GLES30.GlTexture3);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _gbufferDepthTexture);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_lightingProgram, "uDepth"), 3);
        GLES30.GlUniform2f(GLES30.GlGetUniformLocation(_lightingProgram, "uResolution"), _width, _height);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_lightingProgram, "uHybrid"), hybrid ? 1 : 0);
        GLES30.GlUniform1f(GLES30.GlGetUniformLocation(_lightingProgram, "uDaylight"), daylight);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 3);

        GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, _gbufferFramebuffer);
        GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, 0);
        GLES30.GlBlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
            GLES30.GlDepthBufferBit, GLES30.GlNearest);
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
        GLES30.GlEnable(GLES30.GlDepthTest);

        if (_transparentVertexCount > 0)
            DrawScenePass(_transparentProgram, matrix, eye, includeOpaque: false, includeTransparent: true);
    }

    private void DrawScenePass(
        int program,
        Matrix4x4 matrix,
        Vector3 eye,
        bool includeOpaque = true,
        bool includeTransparent = false)
    {
        GLES20.GlUseProgram(program);
        int mvp = GLES20.GlGetUniformLocation(program, "uMvp");
        if (mvp >= 0) GLES20.GlUniformMatrix4fv(mvp, 1, false, ToArray(matrix), 0);
        int camera = GLES20.GlGetUniformLocation(program, "uCamera");
        if (camera >= 0) GLES20.GlUniform3f(camera, eye.X, eye.Y, eye.Z);
        int daylight = GLES20.GlGetUniformLocation(program, "uDaylight");
        if (daylight >= 0) GLES20.GlUniform1f(daylight, DaylightStrength());
        BindShadowUniforms(program);
        BindMaterialUniforms(program);
        if (includeOpaque)
        {
            foreach ((ChunkCoordinate coordinate, GpuMesh mesh) in _chunkMeshes)
                if (IsChunkVisible(coordinate, eye)) DrawMesh(mesh, transparent: false);
            if (_gridMesh is not null) DrawMesh(_gridMesh, transparent: false);
        }
        if (includeTransparent && _transparentVertexCount > 0)
        {
            GLES20.GlEnable(GLES20.GlBlend);
            GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);
            GLES20.GlDepthMask(false);
            foreach ((ChunkCoordinate coordinate, GpuMesh mesh) in _chunkMeshes)
                if (IsChunkVisible(coordinate, eye)) DrawMesh(mesh, transparent: true);
            if (_gridMesh is not null) DrawMesh(_gridMesh, transparent: true);
            GLES20.GlDepthMask(true);
            GLES20.GlDisable(GLES20.GlBlend);
        }
        DisableSceneAttributes();
    }

    private static void DisableSceneAttributes()
    {
        for (int index = 0; index <= 7; index++) GLES20.GlDisableVertexAttribArray(index);
    }

    private bool IsChunkVisible(ChunkCoordinate coordinate, Vector3 eye)
    {
        ChunkCoordinate playerChunk = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Z));
        int chunkDistance = Math.Max(
            Math.Abs(coordinate.X - playerChunk.X),
            Math.Abs(coordinate.Z - playerChunk.Z));
        if (chunkDistance <= 1) return true;

        Vector2 toCenter = new(
            coordinate.MinimumX + ChunkCoordinate.Size * 0.5f - eye.X,
            coordinate.MinimumZ + ChunkCoordinate.Size * 0.5f - eye.Z);
        float lengthSquared = toCenter.LengthSquared();
        if (lengthSquared < 0.001f) return true;
        Vector2 forward = Vector2.Normalize(new Vector2(_camera.Forward.X, _camera.Forward.Z));
        float cosine = Vector2.Dot(forward, toCenter / MathF.Sqrt(lengthSquared));

        // The normal camera half-angle is expanded by 1.6x and includes an
        // additional chunk-radius margin. This deliberately overdraws near the
        // edge so visible terrain cannot pop out as the player turns.
        float expandedHalfAngle = MathF.Min(89f, _camera.FieldOfViewDegrees * 0.5f * 1.6f + 15f);
        return cosine >= MathF.Cos(expandedHalfAngle * MathF.PI / 180f);
    }

    private void DrawImGui(float deltaTime)
    {
        if (_imgui is null) return;
        int tapPhase = Volatile.Read(ref _uiTapPhase);
        bool pointerDown = tapPhase == 1 || Volatile.Read(ref _uiPointerDown) != 0;
        _imgui.BeginFrame(_width, _height, deltaTime, _uiPointerX, _uiPointerY, pointerDown);
        if (_uiMode == AndroidGameUiMode.Gameplay)
        {
            DrawGameplayHudImGui();
        }
        else switch (_uiMode)
        {
            case AndroidGameUiMode.MainMenu: DrawMainMenuImGui(); break;
            case AndroidGameUiMode.CreateWorld: DrawCreateWorldImGui(); break;
            case AndroidGameUiMode.Pause: DrawPauseImGui(); break;
            case AndroidGameUiMode.Inventory:
            case AndroidGameUiMode.BuildTools: DrawBlockLibraryImGui(); break;
            case AndroidGameUiMode.Settings:
            case AndroidGameUiMode.RendererSelection: DrawRendererImGui(); break;
            case AndroidGameUiMode.Achievements: DrawAchievementsImGui(); break;
            case AndroidGameUiMode.Language: DrawLanguageImGui(); break;
            case AndroidGameUiMode.Controls: DrawControlsImGui(); break;
            case AndroidGameUiMode.Mods: DrawModsImGui(); break;
        }
        _imgui.EndFrame(_width, _height);
        if (tapPhase == 1) Volatile.Write(ref _uiTapPhase, 2);
        else if (tapPhase == 2) Volatile.Write(ref _uiTapPhase, 0);
    }

    private void DrawGameplayHudImGui()
    {
        ImGui.SetNextWindowPos(new Vector2(_width * 0.5f, _height - 28f),
            ImGuiCond.Always, new Vector2(0.5f, 1f));
        ImGui.SetNextWindowBgAlpha(0.58f);
        ImGui.Begin("##hotbar",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings);
        for (int slot = 0; slot < CreativeHotbar.SlotCount; slot++)
        {
            if (slot > 0) ImGui.SameLine();
            BlockDefinition block = _hotbar.GetBlock(slot);
            DrawBlockCard(
                block,
                new Vector2(58f, 61f),
                $"hud-slot-{slot}",
                selected: slot == _hotbar.SelectedSlot,
                label: (slot + 1).ToString(),
                interactive: false,
                compact: true);
        }
        ImGui.End();

        VoxelHit? target = VoxelRaycast.Cast(_world, _player.EyePosition, _camera.Forward, _selectionDistance);
        if (target.HasValue)
        {
            BlockDefinition block = _blocks.Get(target.Value.MaterialId);
            ImGui.SetNextWindowPos(new Vector2(_width * 0.5f, 16f), ImGuiCond.Always, new Vector2(0.5f, 0f));
            ImGui.SetNextWindowSizeConstraints(new Vector2(330f, 0f), new Vector2(390f, float.MaxValue));
            ImGui.SetNextWindowBgAlpha(0.76f);
            ImGui.Begin("##target-block-information",
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings);
            ImGui.ColorButton("##target-color", new Vector4(Vector3.Clamp(block.Albedo, Vector3.Zero, Vector3.One), 1f),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(20f, 20f));
            ImGui.SameLine();
            ImGui.Text(block.DisplayName);
            ImGui.TextDisabled($"ID {block.Id}  |  {block.Key}");
            ImGui.Text($"金屬度 {block.Metallic:0.00}  |  粗糙度 {block.Roughness:0.00}");
            ImGui.End();
        }

        Vector2 center = new(_width * 0.5f, _height * 0.5f);
        ImDrawListPtr foreground = ImGui.GetForegroundDrawList();
        uint outline = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.88f));
        uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.96f, 0.98f, 1f, 1f));
        const float size = 8f;
        const float gap = 2.5f;
        Vector2[] starts =
        [
            center - new Vector2(size, 0f), center + new Vector2(gap, 0f),
            center - new Vector2(0f, size), center + new Vector2(0f, gap),
        ];
        Vector2[] ends =
        [
            center - new Vector2(gap, 0f), center + new Vector2(size, 0f),
            center - new Vector2(0f, gap), center + new Vector2(0f, size),
        ];
        for (int index = 0; index < starts.Length; index++)
        {
            foreground.AddLine(starts[index], ends[index], outline, 3.5f);
            foreground.AddLine(starts[index], ends[index], color, 1.6f);
        }
    }

    private bool BeginCenteredWindow(
        string title,
        ref bool open,
        float widthFraction = 0.72f,
        float heightFraction = 0.82f)
    {
        ImGui.SetNextWindowPos(new Vector2(_width * 0.5f, _height * 0.5f),
            ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(
            MathF.Min(_width - 24f, MathF.Max(440f, _width * widthFraction)),
            MathF.Min(_height - 24f, MathF.Max(300f, _height * heightFraction))), ImGuiCond.FirstUseEver);
        return ImGui.Begin(title, ref open, ImGuiWindowFlags.NoCollapse);
    }

    private void DrawPauseImGui()
    {
        SharedPauseAction action = SharedImGuiScreens.DrawPauseMenu(
            new Vector2(_width, _height), T, modsAvailable: true, showTouchControls: true,
            allowWindowControls: true);
        switch (action)
        {
            case SharedPauseAction.Continue:
                SetUiMode(AndroidGameUiMode.Gameplay);
                break;
            case SharedPauseAction.Settings:
                _uiReturnMode = AndroidGameUiMode.Pause;
                SetUiMode(AndroidGameUiMode.Settings);
                break;
            case SharedPauseAction.Achievements:
                _uiReturnMode = AndroidGameUiMode.Pause;
                SetUiMode(AndroidGameUiMode.Achievements);
                break;
            case SharedPauseAction.Language:
                _uiReturnMode = AndroidGameUiMode.Pause;
                SetUiMode(AndroidGameUiMode.Language);
                break;
            case SharedPauseAction.Controls:
                _uiReturnMode = AndroidGameUiMode.Pause;
                SetUiMode(AndroidGameUiMode.Controls);
                break;
            case SharedPauseAction.Mods:
                _uiReturnMode = AndroidGameUiMode.Pause;
                SetUiMode(AndroidGameUiMode.Mods);
                break;
            case SharedPauseAction.TouchControls:
                SetUiMode(AndroidGameUiMode.TouchEditor);
                break;
            case SharedPauseAction.LeaveWorld:
                SaveWorld();
                SetUiMode(AndroidGameUiMode.MainMenu);
                break;
        }
    }

    private void DrawMainMenuImGui()
    {
        ImGui.SetNextWindowPos(new Vector2(_width * 0.5f, _height * 0.5f),
            ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowBgAlpha(0.82f);
        float menuWidth = Math.Clamp(_width - 70f, 390f, 560f);
        float menuHeight = Math.Clamp(_height - 70f, 520f, 820f);
        ImGui.SetNextWindowSize(new Vector2(menuWidth, menuHeight), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        if (ImGui.Begin("##main-menu", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            ImGui.Dummy(new Vector2(0f, 10f));
            float titleWidth = ImGui.CalcTextSize("RTX SANDBOX").X;
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - titleWidth) * 0.5f);
            ImGui.Text("RTX SANDBOX");
            const string subtitle = "Renderer Sandbox + Mod Sandbox";
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - ImGui.CalcTextSize(subtitle).X) * 0.5f);
            ImGui.TextDisabled(subtitle);
            ImGui.Dummy(new Vector2(0f, 12f));
            float width = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button($"{T("continue")} - {_worldSaves.DisplayName}", new Vector2(width, 42f)))
                SetUiMode(AndroidGameUiMode.Gameplay);
            ImGui.SeparatorText(Localize("世界", "世界", "Worlds"));
            if (ImGui.BeginChild("SavedWorlds", new Vector2(0f, Math.Max(120f, menuHeight - 580f)), ImGuiChildFlags.Borders))
            {
                foreach (WorldSaveSummary world in _knownWorlds)
                {
                    bool current = string.Equals(world.StorageName, _worldSaves.WorldName, StringComparison.OrdinalIgnoreCase);
                    bool selected = string.Equals(world.StorageName, _selectedWorldStorage, StringComparison.OrdinalIgnoreCase);
                    string currentPrefix = current
                        ? Localize("[目前] ", "[当前] ", "[Current] ")
                        : string.Empty;
                    if (ImGui.Selectable($"{currentPrefix}{world.DisplayName}##world-{world.StorageName}",
                            selected, ImGuiSelectableFlags.None, new Vector2(0f, 34f)))
                    {
                        _selectedWorldStorage = world.StorageName;
                        _worldRenameDraft = world.DisplayName;
                        _worldMenuStatus = string.Empty;
                    }
                    ImGui.SameLine(Math.Max(205f, width * 0.48f));
                    ImGui.TextDisabled($"{world.Preset} · Seed {world.Seed}");
                }
            }
            ImGui.EndChild();
            WorldSaveSummary? selectedWorld = _knownWorlds.FirstOrDefault(world =>
                string.Equals(world.StorageName, _selectedWorldStorage, StringComparison.OrdinalIgnoreCase));
            if (selectedWorld is not null)
            {
                bool current = string.Equals(selectedWorld.StorageName, _worldSaves.WorldName, StringComparison.OrdinalIgnoreCase);
                float quarter = (width - ImGui.GetStyle().ItemSpacing.X * 3f) * 0.25f;
                if (current) ImGui.BeginDisabled();
                if (ImGui.Button(Localize("載入", "加载", "Open"), new Vector2(quarter, 34f))) OpenWorld(selectedWorld.StorageName);
                if (current) ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button(Localize("重新生成", "重新生成", "Rebuild"), new Vector2(quarter, 34f))) RebuildWorld(selectedWorld.StorageName);
                ImGui.SameLine();
                if (ImGui.Button(Localize("重新命名", "重命名", "Rename"), new Vector2(quarter, 34f))) RenameWorld(selectedWorld.StorageName);
                ImGui.SameLine();
                if (current) ImGui.BeginDisabled();
                if (ImGui.Button(Localize("刪除", "删除", "Delete"), new Vector2(quarter, 34f))) DeleteWorld(selectedWorld.StorageName);
                if (current) ImGui.EndDisabled();
                ImGui.SetNextItemWidth(width);
                ImGui.InputText("##world-rename", ref _worldRenameDraft, 64);
            }
            if (!string.IsNullOrWhiteSpace(_worldMenuStatus)) ImGui.TextWrapped(_worldMenuStatus);
            if (ImGui.Button(Localize("建立世界", "创建世界", "Create World"), new Vector2(width, 38f)))
            {
                _worldNameDraft = "New World";
                _worldSeedDraft = Random.Shared.Next(int.MinValue, int.MaxValue);
                SetUiMode(AndroidGameUiMode.CreateWorld);
            }
            float half = (width - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            if (ImGui.Button(T("settings"), new Vector2(half, 38f)))
            {
                _uiReturnMode = AndroidGameUiMode.MainMenu;
                SetUiMode(AndroidGameUiMode.Settings);
            }
            ImGui.SameLine();
            if (ImGui.Button(T("language"), new Vector2(half, 38f)))
            {
                _uiReturnMode = AndroidGameUiMode.MainMenu;
                SetUiMode(AndroidGameUiMode.Language);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void DrawCreateWorldImGui()
    {
        ImGui.SetNextWindowPos(new Vector2(_width * 0.5f, _height * 0.5f),
            ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(MathF.Min(_width - 24f, 620f), MathF.Min(_height - 24f, 460f)),
            ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin(Localize("建立世界", "创建世界", "Create World"), ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.InputText(Localize("世界名稱", "世界名称", "World Name"), ref _worldNameDraft, 64);
            ImGui.InputInt("Seed", ref _worldSeedDraft);
            ImGui.TextDisabled("Natural · Auto Biome · Lakes · Vegetation");
            ImGui.Separator();
            bool valid = !string.IsNullOrWhiteSpace(_worldNameDraft);
            if (!valid) ImGui.BeginDisabled();
            if (ImGui.Button(Localize("建立", "创建", "Create"), new Vector2(180f, 42f)))
            {
                CreateWorld(_worldNameDraft, _worldSeedDraft);
                open = false;
            }
            if (!valid) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button(Localize("取消", "取消", "Cancel"), new Vector2(180f, 42f))) open = false;
        }
        ImGui.End();
        if (!open && _uiMode == AndroidGameUiMode.CreateWorld)
            SetUiMode(AndroidGameUiMode.MainMenu);
    }

    private void RefreshWorldList()
    {
        _knownWorlds = _worldSaves.DiscoverWorlds();
        if (_knownWorlds.Count == 0)
        {
            _knownWorlds =
            [
                new WorldSaveSummary(_worldSaves.WorldName, _worldSaves.DisplayName, _world.Seed,
                    _world.GenerationSettings.Preset, DateTimeOffset.UtcNow, false),
            ];
        }
        if (!_knownWorlds.Any(world => string.Equals(
                world.StorageName, _selectedWorldStorage, StringComparison.OrdinalIgnoreCase)))
            _selectedWorldStorage = _worldSaves.WorldName;
        WorldSaveSummary? selected = _knownWorlds.FirstOrDefault(world => string.Equals(
            world.StorageName, _selectedWorldStorage, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) _worldRenameDraft = selected.DisplayName;
    }

    private void OpenWorld(string storageName)
    {
        SaveWorld();
        var service = new WorldSaveService(_blocks, preferredSeed: 1337, worldName: storageName, saveRoot: _saveRoot);
        if (!service.TryLoad(_world, out SavedWorldState state, out string? error))
        {
            _worldMenuStatus = $"Failed to load world: {error}";
            return;
        }
        _worldSaves = service;
        _player.Teleport(state.PlayerPosition);
        _worldClock.SetTime(state.TimeOfDay);
        if (state.Inventory.Count > 0) _inventory.Restore(state.Inventory, state.SelectedHotbarSlot);
        _selectedWorldStorage = service.WorldName;
        MarkAllChunkMeshesDirty();
        _gridMeshDirty = true;
        RefreshWorldList();
        _worldMenuStatus = $"Loaded: {service.DisplayName}";
        SetUiMode(AndroidGameUiMode.Gameplay);
    }

    private void CreateWorld(string name, int seed)
    {
        if (_knownWorlds.Any(world => string.Equals(world.StorageName, name, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(world.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
        {
            _worldMenuStatus = "A world with this name already exists.";
            return;
        }
        SaveWorld();
        var service = new WorldSaveService(_blocks, preferredSeed: seed, worldName: name, saveRoot: _saveRoot);
        _world.Regenerate(WorldGenerationSettings.Natural(seed));
        _worldClock.SetTime(8.5f);
        _worldSaves = service;
        TeleportToSpawn();
        SaveWorld();
        _selectedWorldStorage = service.WorldName;
        MarkAllChunkMeshesDirty();
        _gridMeshDirty = true;
        RefreshWorldList();
        _worldMenuStatus = $"Created: {service.DisplayName}";
        SetUiMode(AndroidGameUiMode.Gameplay);
    }

    private void RebuildWorld(string storageName)
    {
        SaveWorld();
        if (!_worldSaves.TryGetWorldGeneration(storageName, out WorldGenerationSettings settings, out string? error))
        {
            _worldMenuStatus = $"Rebuild failed: {error}";
            return;
        }
        _worldSaves = new WorldSaveService(_blocks, preferredSeed: settings.Seed,
            worldName: storageName, saveRoot: _saveRoot);
        _world.Regenerate(settings);
        _worldClock.SetTime(8.5f);
        TeleportToSpawn();
        SaveWorld();
        _selectedWorldStorage = storageName;
        MarkAllChunkMeshesDirty();
        _gridMeshDirty = true;
        RefreshWorldList();
        _worldMenuStatus = "World regenerated using original seed.";
    }

    private void RenameWorld(string storageName)
    {
        if (_worldSaves.TryRenameWorld(storageName, _worldRenameDraft, out string? error))
        {
            _worldMenuStatus = "World renamed.";
            RefreshWorldList();
        }
        else _worldMenuStatus = $"Failed to rename world: {error}";
    }

    private void DeleteWorld(string storageName)
    {
        if (_worldSaves.TryDeleteWorld(storageName, out string? error))
        {
            _worldMenuStatus = "World deleted.";
            RefreshWorldList();
        }
        else _worldMenuStatus = $"Failed to delete world: {error}";
    }

    private void TeleportToSpawn()
    {
        int x = VoxelWorld.Width / 2;
        int z = VoxelWorld.Depth / 2 + 8;
        float y = _world.FindTopVoxelY(x, z) + 1.001f;
        _player.Teleport(new Vector3(x + 0.5f, y, z + 0.5f));
    }

    private void DrawBlockLibraryImGui()
    {
        ImGui.SetNextWindowPos(new Vector2(_width * 0.5f, _height * 0.5f),
            ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(
            MathF.Min(_width - 24f, 900f), MathF.Min(_height - 24f, 850f)), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("RTX Sandbox Editor", ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled($"{BlockChoices.Count} materials | E closes editor");
            if (ImGui.BeginTabBar("EditorTabs"))
            {
                bool blocksTabOpen = true;
                if (ImGui.BeginTabItem("Blocks", ref blocksTabOpen, _uiMode == AndroidGameUiMode.Inventory
                        ? ImGuiTabItemFlags.SetSelected
                        : ImGuiTabItemFlags.None))
                {
                    ImGui.TextWrapped("Creative Library: every material is unlimited. Tap a material to put it in the active quickbar slot.");
                    ImGui.Text("Quickbar");
                    for (int slot = 0; slot < CreativeHotbar.SlotCount; slot++)
                    {
                        if (slot > 0) ImGui.SameLine();
                        BlockDefinition quickBlock = _hotbar.GetBlock(slot);
                        if (DrawBlockCard(
                                quickBlock,
                                new Vector2(76f, 76f),
                                $"library-quickbar-{slot}",
                                selected: slot == _hotbar.SelectedSlot,
                                label: (slot + 1).ToString(),
                                compact: true))
                            _hotbar.SelectedSlot = slot;
                    }
                    ImGui.Separator();
                    ImGui.SetNextItemWidth(330f);
                    ImGui.InputText("Search", ref _paletteSearch, 128);
                    BlockDefinition[] visible = CreativePaletteOrdering.Order(
                        _blocks.PlaceableDefinitions().Where(block =>
                            string.IsNullOrWhiteSpace(_paletteSearch) ||
                            block.DisplayName.Contains(_paletteSearch, StringComparison.OrdinalIgnoreCase) ||
                            block.Key.Contains(_paletteSearch, StringComparison.OrdinalIgnoreCase)),
                        useSimpleGroups: true);
                    ImGui.TextDisabled($"Showing {visible.Length} of {BlockChoices.Count} materials");
                    if (ImGui.BeginChild("BlockPaletteScroll", Vector2.Zero, ImGuiChildFlags.Borders))
                    {
                        int columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / 112f));
                        for (int index = 0; index < visible.Length; index++)
                        {
                            BlockDefinition block = visible[index];
                            ImGui.PushID($"block:{block.Key}");
                            if (DrawBlockCard(
                                    block,
                                    new Vector2(104f, 102f),
                                    $"material-{block.Key}",
                                    selected: block.Id == _hotbar.SelectedBlock.Id,
                                    label: ShortName(block.DisplayName, 14)))
                            {
                                _hotbar.SelectAndAssign(block.Id);
                                PublishUiState();
                            }
                            ImGui.PopID();
                            if ((index + 1) % columns != 0) ImGui.SameLine();
                        }
                    }
                    ImGui.EndChild();
                    ImGui.EndTabItem();
                }
                bool toolsTabOpen = true;
                if (ImGui.BeginTabItem("Tools", ref toolsTabOpen, _uiMode == AndroidGameUiMode.BuildTools
                        ? ImGuiTabItemFlags.SetSelected
                        : ImGuiTabItemFlags.None))
                {
                    ImGui.TextWrapped("Left click removes. Right click places the selected material.");
                    bool precision = _precisionMode;
                    if (ImGui.Checkbox("Precision micro-voxel mode (V)", ref precision))
                    {
                        _precisionMode = precision;
                        _microBuild.Enabled = precision;
                        PublishUiState();
                    }
                    int gridIndex = _microBuild.SupportedDivisors.ToList().IndexOf(_microBuild.GridDivisor);
                    string[] grids = _microBuild.SupportedDivisors.Select(value => $"1/{value}").ToArray();
                    if (ImGui.Combo("Precision grid", ref gridIndex, grids, grids.Length) && gridIndex >= 0)
                        _microBuild.SetGridDivisor(_microBuild.SupportedDivisors[gridIndex]);
                    ImGui.TextDisabled("Android touch bindings are edited from Pause > Touch Editor.");
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
        if (!open) SetUiMode(AndroidGameUiMode.Gameplay);
    }

    private static string ShortBlockName(string value) => ShortName(value, 12);
    private static string ShortName(string value, int maxLength)
    {
        if (maxLength <= 0) return string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
    }

    private bool DrawBlockCard(
        BlockDefinition block,
        Vector2 size,
        string id,
        bool selected,
        string? label = null,
        bool interactive = true,
        bool compact = false)
    {
        Vector2 position = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##{id}", size);
        bool hovered = interactive && ImGui.IsItemHovered();
        bool clicked = interactive && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        Vector4 background = selected
            ? new Vector4(0.62f, 0.62f, 0.62f, 1f)
            : hovered
                ? new Vector4(0.34f, 0.34f, 0.34f, 1f)
                : new Vector4(0.20f, 0.20f, 0.20f, 1f);
        uint backgroundColor = ImGui.ColorConvertFloat4ToU32(background);
        uint borderColor = ImGui.ColorConvertFloat4ToU32(selected
            ? new Vector4(0.96f, 0.96f, 0.96f, 1f)
            : new Vector4(0.06f, 0.06f, 0.06f, 1f));
        drawList.AddRectFilled(position, position + size, backgroundColor, 1f);
        drawList.AddRect(position, position + size, borderColor, 1f, ImDrawFlags.None, selected ? 3f : 2f);

        float labelHeight = compact ? 13f : 23f;
        DrawMaterialCube(
            drawList,
            position + new Vector2(4f, 3f),
            new Vector2(size.X - 8f, size.Y - labelHeight - 4f),
            block);

        string text = label ?? (compact ? ShortName(block.DisplayName, 8) : ShortBlockName(block.DisplayName));
        Vector2 textSize = ImGui.CalcTextSize(text);
        Vector2 textPosition = new(
            position.X + Math.Max(3f, (size.X - textSize.X) * 0.5f),
            position.Y + size.Y - textSize.Y - (compact ? 2f : 5f));
        uint textColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.94f, 0.97f, 1f, 1f));
        drawList.PushClipRect(position + Vector2.One, position + size - Vector2.One, true);
        drawList.AddText(textPosition, textColor, text);
        drawList.PopClipRect();

        if (_favoriteBlocks.Contains(block.Key) && !compact)
        {
            uint star = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.78f, 0.18f, 1f));
            drawList.AddText(position + new Vector2(size.X - 17f, 4f), star, "*");
        }

        if (interactive && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            if (!_favoriteBlocks.Add(block.Key)) _favoriteBlocks.Remove(block.Key);
        }

        if (hovered)
            DrawBlockTooltip(block);

        return clicked;
    }

    private void DrawMaterialCube(ImDrawListPtr drawList, Vector2 position, Vector2 size, BlockDefinition block)
    {
        float halfWidth = MathF.Min(size.X * 0.42f, size.Y * 0.46f);
        float topHeight = halfWidth * 0.56f;
        float sideHeight = MathF.Min(size.Y - topHeight - 1f, halfWidth * 0.98f);
        float centerX = position.X + size.X * 0.5f;
        float topY = position.Y + Math.Max(1f, (size.Y - topHeight - sideHeight) * 0.42f);
        Vector2 top = new(centerX, topY);
        Vector2 right = new(centerX + halfWidth, topY + topHeight);
        Vector2 middle = new(centerX, topY + topHeight * 2f);
        Vector2 left = new(centerX - halfWidth, topY + topHeight);
        Vector2 rightBottom = right + new Vector2(0f, sideHeight);
        Vector2 middleBottom = middle + new Vector2(0f, sideHeight);
        Vector2 leftBottom = left + new Vector2(0f, sideHeight);

        if (block.Emission.LengthSquared() > 0.04f)
        {
            Vector3 glowVector = Vector3.Clamp(block.Emission, Vector3.Zero, Vector3.One);
            uint glow = ImGui.ColorConvertFloat4ToU32(new Vector4(glowVector, 0.22f));
            drawList.AddCircleFilled(new Vector2(centerX, topY + topHeight + sideHeight * 0.45f),
                halfWidth * 1.1f, glow, 24);
        }

        MaterialTileUv topUv = MaterialTileUvFor(block.Id, MaterialFace.Top);
        MaterialTileUv sideUv = MaterialTileUvFor(block.Id, MaterialFace.Side);
        nint texture = (nint)_materialAtlasTexture;
        float alpha = block.Transmission > 0f ? 0.78f : 1f;
        uint leftTint = ImGui.ColorConvertFloat4ToU32(new Vector4(0.72f, 0.76f, 0.82f, alpha));
        uint rightTint = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.60f, 0.69f, alpha));
        uint topTint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, alpha));

        AddTexturedQuad(drawList, texture, left, middle, middleBottom, leftBottom, sideUv, leftTint,
            flipX: true, flipY: true);
        AddTexturedQuad(drawList, texture, middle, right, rightBottom, middleBottom, sideUv, rightTint,
            flipY: true);
        AddTexturedQuad(drawList, texture, top, right, middle, left, topUv, topTint);

        uint outline = ImGui.ColorConvertFloat4ToU32(new Vector4(0.86f, 0.90f, 0.96f, 0.72f));
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

    private MaterialTileUv MaterialTileUvFor(ushort materialId, MaterialFace face)
    {
        int tile = materialId * ProceduralMaterialAtlas.FacesPerMaterial + (int)face;
        int tileX = tile % MaterialAtlasTilesPerRow * ProceduralMaterialAtlas.TileSize;
        int tileY = tile / MaterialAtlasTilesPerRow * ProceduralMaterialAtlas.TileSize;
        return new MaterialTileUv(
            new Vector2(
                (tileX + 0.5f) / _materialAtlasWidth,
                (tileY + 0.5f) / _materialAtlasHeight),
            new Vector2(
                (tileX + ProceduralMaterialAtlas.TileSize - 0.5f) / _materialAtlasWidth,
                (tileY + ProceduralMaterialAtlas.TileSize - 0.5f) / _materialAtlasHeight));
    }

    private static void AddTexturedQuad(
        ImDrawListPtr drawList,
        nint texture,
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Vector2 d,
        MaterialTileUv uv,
        uint tint,
        bool flipX = false,
        bool flipY = false)
    {
        float left = flipX ? uv.Maximum.X : uv.Minimum.X;
        float right = flipX ? uv.Minimum.X : uv.Maximum.X;
        float top = flipY ? uv.Maximum.Y : uv.Minimum.Y;
        float bottom = flipY ? uv.Minimum.Y : uv.Maximum.Y;
        drawList.AddImageQuad(texture, a, b, c, d,
            new Vector2(left, top), new Vector2(right, top),
            new Vector2(right, bottom), new Vector2(left, bottom), tint);
    }

    private static void DrawBlockTooltip(BlockDefinition block)
    {
        ImGui.BeginTooltip();
        ImGui.Text(block.DisplayName);
        ImGui.TextDisabled($"ID {block.Id}  |  {block.Key}");
        ImGui.Text($"Metallic {block.Metallic:0.00}  |  Roughness {block.Roughness:0.00}");
        ImGui.EndTooltip();
    }
    private void DrawRendererImGui()
    {
        ImGui.SetNextWindowPos(new Vector2(_width * 0.5f, _height * 0.5f),
            ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(
            MathF.Min(_width - 24f, 680f), MathF.Min(_height - 24f, 640f)), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin(Localize("RTX Sandbox 設定", "RTX Sandbox 设置", "RTX Sandbox Settings"), ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled(Localize(
                "桌面與 Android 共用主要選單結構；此頁只顯示裝置支援的功能。",
                "桌面与 Android 共用主要菜单结构；此页只显示设备支持的功能。",
                "Desktop and Android share the main menu structure; this page only shows supported device features."));
            ImGui.Spacing();
            if (ImGui.BeginTabBar("SettingsTabs"))
            {
                if (ImGui.BeginTabItem(Localize("畫面", "画面", "Graphics")))
                {
                    ResetSettingsScrollForTab(0);
                    ImGui.SeparatorText(T("renderer"));
                    foreach (AndroidRendererChoice choice in RendererChoices)
                    {
                        if (!choice.Available) ImGui.BeginDisabled();
                        bool selected = string.Equals(choice.Name, _rendererMode.ToString(), StringComparison.OrdinalIgnoreCase);
                        if (ImGui.RadioButton(choice.DisplayName, selected) &&
                            Enum.TryParse(choice.Name, out AndroidRendererMode mode))
                        {
                            _rendererMode = mode;
                            RecreateProgram();
                            PublishUiState();
                        }
                        ImGui.SameLine();
                        ImGui.TextDisabled(choice.Reason);
                        if (!choice.Available) ImGui.EndDisabled();
                    }
                    ImGui.SeparatorText(Localize("顯示", "显示", "Display"));
                    ImGui.Text($"{Localize("目前解析度", "当前分辨率", "Current resolution")}：{_width} × {_height}");
                    int streamingRadius = _streamingRadius;
                    if (ImGui.SliderInt(Localize("區塊顯示距離", "区块显示距离", "Chunk view distance"),
                            ref streamingRadius, 2, 8))
                    {
                        _streamingRadius = streamingRadius;
                        _uiPreferences.Edit()?.PutInt("streaming-radius", streamingRadius)?.Apply();
                        _meshDirty = true;
                    }
                    float fieldOfView = _baseFieldOfView;
                    if (ImGui.SliderFloat(Localize("視野角度", "视野角度", "Field of view"),
                            ref fieldOfView, 45f, 105f, "%.0f°"))
                    {
                        _baseFieldOfView = fieldOfView;
                        _uiPreferences.Edit()?.PutFloat("field-of-view", fieldOfView)?.Apply();
                    }
                    ImGui.TextDisabled(Localize(
                        "Android 的 VSync 由系統 Surface 控制；只顯示目前真正生效的圖形選項。",
                        "Android 的 VSync 由系统 Surface 控制；只显示当前真正生效的图形选项。",
                        "Android VSync is controlled by the system Surface; only functional options are shown."));
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Localize("時間與光照", "时间与光照", "Time & Lighting")))
                {
                    ResetSettingsScrollForTab(1);
                    float time = _worldClock.TimeOfDayHours;
                    if (ImGui.SliderFloat(Localize("遊戲時間", "游戏时间", "Game time"),
                            ref time, 0f, 23.99f, "%.2f"))
                        _worldClock.SetTime(time);
                    bool timePaused = _worldClock.Paused;
                    if (ImGui.Checkbox(Localize("暫停自動時間循環", "暂停自动时间循环", "Pause time cycle"),
                            ref timePaused))
                    {
                        _worldClock.Paused = timePaused;
                        _uiPreferences.Edit()?.PutBoolean("time-paused", timePaused)?.Apply();
                    }
                    float dayLength = _worldClock.DayLengthMinutes;
                    if (ImGui.SliderFloat(Localize("一日所需現實分鐘", "一天所需现实分钟", "Real minutes per day"),
                            ref dayLength, 1f, 120f, "%.1f"))
                    {
                        _worldClock.SetDayLength(dayLength);
                        _uiPreferences.Edit()?.PutFloat("day-length-minutes", dayLength)?.Apply();
                    }
                    if (ImGui.Button(Localize("日出", "日出", "Sunrise"))) _worldClock.SetTime(6f);
                    ImGui.SameLine();
                    if (ImGui.Button(Localize("正午", "正午", "Noon"))) _worldClock.SetTime(12f);
                    ImGui.SameLine();
                    if (ImGui.Button(Localize("日落", "日落", "Sunset"))) _worldClock.SetTime(18f);
                    ImGui.SameLine();
                    if (ImGui.Button(Localize("午夜", "午夜", "Midnight"))) _worldClock.SetTime(0f);
                    ImGui.Text($"{Localize("目前遊戲時間", "当前游戏时间", "Current game time")}：{_worldClock.DisplayTime}");
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Localize("控制與介面", "控制与界面", "Controls & UI")))
                {
                    ResetSettingsScrollForTab(2);
                    float sensitivity = _lookSensitivity;
                    if (ImGui.SliderFloat(Localize("視角靈敏度", "视角灵敏度", "Look sensitivity"),
                            ref sensitivity, 0.2f, 2f, "%.2f"))
                    {
                        _lookSensitivity = sensitivity;
                        _uiPreferences.Edit()?.PutFloat("look-sensitivity", sensitivity)?.Apply();
                    }
                    float reach = _selectionDistance;
                    if (ImGui.SliderFloat(Localize("建造距離", "建造距离", "Build reach"),
                            ref reach, 3f, 16f, "%.1f"))
                    {
                        _selectionDistance = reach;
                        _uiPreferences.Edit()?.PutFloat("selection-distance", reach)?.Apply();
                    }
                    bool flight = _player.FlightMode;
                    if (ImGui.Checkbox(Localize("飛行模式", "飞行模式", "Flight mode"), ref flight))
                    {
                        _player.FlightMode = flight;
                        if (!flight) _player.NoClip = false;
                    }
                    bool noClip = _player.NoClip;
                    if (ImGui.Checkbox(Localize("穿牆／無碰撞", "穿墙／无碰撞", "No clip"), ref noClip))
                    {
                        _player.NoClip = noClip;
                        if (noClip) _player.FlightMode = true;
                    }
                    ImGui.SeparatorText(Localize("結構物理", "结构物理", "Structure physics"));
                    bool physicsEnabled = _physics.SimulationEnabled;
                    if (ImGui.Checkbox(Localize("啟用物理模擬", "启用物理模拟", "Enable physics simulation"),
                            ref physicsEnabled))
                    {
                        _physics.SimulationEnabled = physicsEnabled;
                        global::Android.App.Application.Context
                            .GetSharedPreferences("rtxsandbox-ui", FileCreationMode.Private)
                            ?.Edit()
                            ?.PutBoolean("physics-enabled", physicsEnabled)
                            ?.Apply();
                    }
                    ImGui.TextDisabled(Localize(
                        "關閉後保留既有物理結構的位置，只停止模擬。",
                        "关闭后保留已有物理结构的位置，只停止模拟。",
                        "When disabled, existing physics grids keep their positions and stop simulating."));
                    ImGui.Separator();
                    if (ImGui.Button(T("controls"), new Vector2(210f, 42f)))
                    {
                        _uiReturnMode = AndroidGameUiMode.Settings;
                        SetUiMode(AndroidGameUiMode.Controls);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button(Localize("立即保存世界", "立即保存世界", "Save world now"), new Vector2(210f, 42f)))
                        SaveWorld();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Localize("觸控", "触控", "Touch")))
                {
                    ResetSettingsScrollForTab(3);
                    ImGui.Dummy(new Vector2(0f, 12f));
                    if (ImGui.Button(T("touch_editor"), new Vector2(240f, 48f)))
                        SetUiMode(AndroidGameUiMode.TouchEditor);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            ImGui.Separator();
            if (ImGui.Button(T("done"), new Vector2(180f, 42f))) open = false;
        }
        ImGui.End();
        if (!open) SetUiMode(_uiReturnMode);
    }

    private void DrawAchievementsImGui()
    {
        if (!SharedImGuiScreens.DrawAchievements(new Vector2(_width, _height), T))
            SetUiMode(_uiReturnMode);
    }

    private void DrawLanguageImGui()
    {
        UiLanguage language = _language;
        bool open = SharedImGuiScreens.DrawLanguage(
            new Vector2(_width, _height), T, ref language, out bool changed);
        if (changed)
        {
            _language = language;
            global::Android.App.Application.Context
                .GetSharedPreferences("rtxsandbox-ui", FileCreationMode.Private)
                ?.Edit()
                ?.PutString("language", language.ToString())
                ?.Apply();
        }
        if (!open) SetUiMode(_uiReturnMode);
    }

    private void DrawControlsImGui()
    {
        bool open = true;
        if (BeginCenteredWindow(T("controls_title"), ref open, 0.72f, 0.82f))
        {
            ImGui.TextWrapped(Localize(
                "外接鍵盤沿用 PC 預設。選擇按鍵後，再按下替代鍵。觸控控制仍是 Android 專用輸入層。",
                "外接键盘沿用 PC 默认。选择按键后，再按下替代键。触控控制仍是 Android 专用输入层。",
                "External keyboard controls share the PC defaults. Select a key, then press its replacement. Touch controls remain an Android-only input layer."));
            if (_input.HardwareRebindTarget is not null)
                ImGui.TextColored(new Vector4(0.25f, 0.86f, 1f, 1f),
                    Localize("請按下任意鍵……", "请按下任意键……", "Press a key..."));
            ImGui.Separator();
            DrawEditableBinding("move_forward", VirtualBindingKind.Keyboard, "W", "W");
            DrawEditableBinding("move_backward", VirtualBindingKind.Keyboard, "S", "S");
            DrawEditableBinding("move_left", VirtualBindingKind.Keyboard, "A", "A");
            DrawEditableBinding("move_right", VirtualBindingKind.Keyboard, "D", "D");
            DrawEditableBinding("jump", VirtualBindingKind.Keyboard, "Space", "Space");
            DrawEditableBinding("crouch", VirtualBindingKind.GameAction, "Crouch", "Shift");
            DrawEditableBinding("sprint", VirtualBindingKind.Keyboard, "Ctrl", "Ctrl");
            DrawEditableBinding("inventory", VirtualBindingKind.Keyboard, "E", "E");
            DrawEditableBinding("build_tools", VirtualBindingKind.Keyboard, "B", "B");
            DrawEditableBinding("precision", VirtualBindingKind.Keyboard, "V", "V");
            ImGui.Separator();
            if (ImGui.Button(Localize("重設預設值", "重置默认值", "Reset defaults"),
                    new Vector2(180f, 42f)))
                _input.ResetHardwareBindings();
            ImGui.SameLine();
            if (ImGui.Button(T("done"), new Vector2(180f, 42f))) open = false;
        }
        ImGui.End();
        if (!open) SetUiMode(_uiReturnMode);
    }

    private void DrawEditableBinding(
        string key,
        VirtualBindingKind kind,
        string binding,
        string fallback)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(T(key));
        ImGui.SameLine(250f);
        string label = _input.HardwareKeyLabel(kind, binding, fallback);
        if (string.Equals(label, "Unbound", StringComparison.OrdinalIgnoreCase))
            label = Localize("未設定", "未设置", "Unbound");
        if (ImGui.Button($"{label}##binding-{kind}-{binding}", new Vector2(230f, 0f)))
            _input.BeginHardwareRebind(kind, binding);
    }

    private string T(string key) => UiText.Get(_language, key);

    private string Localize(string traditional, string simplified, string english) => _language switch
    {
        UiLanguage.SimplifiedChinese => simplified,
        UiLanguage.English => english,
        _ => traditional,
    };

    private static readonly string[] AndroidUiGlyphText =
    {
        UiText.GlyphCorpus,
        "經典光照前向延遲混合路徑追蹤硬體世界目前載入重新生成命名刪除建立取消",
        "图形游戏按键触控解析度渲染器品质外接键盘默认替代输入",
        "圖形遊戲按鍵觸控解析度渲染器品質外接鍵盤預設替代輸入",
        "金屬度粗糙度精密模式已載入初始化失敗新的世界",
        "桌面與安卓共用主要選單結構此頁只顯示裝置支援功能目前系統控制專屬設定規則核心管理真正可用另有編輯層選擇按下任意鍵重設值未設定",
        "桌面与安卓共用主要菜单结构此页只显示设备支持功能当前系统控制专属设置规则核心管理真正可用另有编辑层选择按下任意键重置值未设置",
        "啟用物理模擬關閉後保留既有結構的位置只停止",
        "启用物理模拟关闭后保留已有结构的位置只停止",
        "桌面與 Android 共用主要選單結構；此頁只顯示裝置支援的功能。世界與遊戲規則由共用核心管理；只在功能真正可用時提供設定。關閉後保留既有物理結構的位置，只停止模擬。",
        "桌面与 Android 共用主要菜单结构；此页只显示设备支持的功能。世界与游戏规则由共用核心管理；只在功能真正可用时提供设置。关闭后保留已有物理结构的位置，只停止模拟。",
        "將模組與相依檔直接放進此目錄重新啟動後載入也可使用下方按鈕選取匯入授予資料夾權限可選尚未安裝個；：",
        "将模组与依赖文件直接放进此目录重新启动后加载也可使用下方按钮选择导入授予文件夹权限可选尚未安装个；：",
        "畫面區塊顯示距離視野角度時間與光照遊戲暫停自動循環一日所需現實分鐘日出正午日落午夜控制與介面視角靈敏度建造飛行穿牆無碰撞結構物理立即保存世界",
        "画面区块显示距离视野角度时间与光照游戏暂停自动循环一天所需现实分钟日出正午日落午夜控制与界面视角灵敏度建造飞行穿墙无碰撞结构物理立即保存世界",
    };

    private void HandleBlockActions()
    {
        bool destroyHeld = _input.IsDown(VirtualBindingKind.Mouse, "Left");
        bool destroy = _input.ConsumePressed(VirtualBindingKind.Mouse, "Left");
        double now = _clock.Elapsed.TotalSeconds;
        if (destroyHeld && now >= _nextHeldDestroyAt)
        {
            destroy = true;
            _nextHeldDestroyAt = now + 0.18;
        }
        else if (!destroyHeld)
        {
            _nextHeldDestroyAt = 0;
        }
        bool place = _input.ConsumePressed(VirtualBindingKind.Mouse, "Right");
        if (!destroy && !place) return;

        PhysicsGridRayHit? gridHit = _world.PhysicsGrids.Raycast(
            _player.EyePosition, _camera.Forward, _selectionDistance);
        PrecisionHit? precision = _precisionMode ? CurrentPrecisionHit() : null;
        VoxelHit? hit = _precisionMode
            ? null
            : VoxelRaycast.Cast(_world, _player.EyePosition, _camera.Forward, _selectionDistance);
        float worldDistance = _precisionMode
            ? precision?.Distance ?? float.PositiveInfinity
            : hit?.Distance ?? float.PositiveInfinity;
        if (gridHit.HasValue && gridHit.Value.Distance < worldDistance)
        {
            PhysicsGridRayHit gridTarget = gridHit.Value;
            bool gridChanged = destroy
                ? (_precisionMode
                    ? _physics.TryRemoveGridElement(gridTarget)
                    : _physics.TryRemoveGridCell(
                        gridTarget.GridId, gridTarget.CellX, gridTarget.CellY, gridTarget.CellZ))
                : (_precisionMode
                    ? _physics.TryPlaceMicroOnGrid(
                        gridTarget, _hotbar.SelectedBlock.Id, _microBuild.GridDivisor)
                    : _physics.TryPlaceOnGrid(gridTarget, _hotbar.SelectedBlock.Id));
            if (gridChanged)
            {
                _meshDirty = true;
                _interactiveMeshUpdateRequested = true;
            }
            return;
        }

        if (_precisionMode)
        {
            bool precisionChanged = precision.HasValue &&
                                    (destroy
                                        ? _microBuild.Erase(precision.Value)
                                        : _microBuild.Place(precision.Value, _hotbar.SelectedBlock.Id));
            if (precisionChanged)
            {
                _meshDirty = true;
                _interactiveMeshUpdateRequested = true;
            }
            return;
        }

        bool changed = false;
        if (destroy && hit is { } remove && !_blocks.Get(remove.MaterialId).IsUnbreakable)
            changed = _world.SetBlockId(remove.X, remove.Y, remove.Z, 0);
        if (place && hit is { } target && PlacementRules.IsFarEnough(target.Distance))
        {
            BlockDefinition selected = _hotbar.SelectedBlock;
            if (selected.Id == (ushort)VoxelMaterial.Water && _world.IsFluid(target.X, target.Y, target.Z))
            {
                changed = _world.PourWater(target.X, target.Y, target.Z) || changed;
            }
            else
            {
                BlockDefinition targetDefinition = _blocks.Get(target.MaterialId);
                (int x, int y, int z) = targetDefinition.IsReplaceable && target.MaterialId != selected.Id
                    ? (target.X, target.Y, target.Z)
                    : target.Adjacent;
                if (!selected.IsCollidable || !_player.WouldIntersectBlock(x, y, z))
                    changed = _world.SetBlockId(x, y, z, selected.Id) || changed;
            }
        }
        if (changed)
        {
            _meshDirty = true;
            _interactiveMeshUpdateRequested = true;
        }
    }

    private PrecisionHit? CurrentPrecisionHit()
    {
        VoxelHit? regularHit = VoxelRaycast.Cast(_world, _player.EyePosition, _camera.Forward, _selectionDistance);
        MicroVoxelHit? microHit = MicroVoxelRaycast.Cast(_world, _player.EyePosition, _camera.Forward, _selectionDistance);
        PrecisionHit? regular = regularHit.HasValue
            ? new PrecisionHit(
                regularHit.Value.X,
                regularHit.Value.Y,
                regularHit.Value.Z,
                _player.EyePosition + Vector3.Normalize(_camera.Forward) * regularHit.Value.Distance,
                regularHit.Value.Normal,
                regularHit.Value.Distance,
                regularHit.Value.MaterialId,
                false)
            : null;
        if (!microHit.HasValue ||
            regular.HasValue && regular.Value.Distance <= microHit.Value.Distance) return regular;
        MicroVoxelHit hit = microHit.Value;
        return new PrecisionHit(hit.X, hit.Y, hit.Z, hit.Position, hit.Normal, hit.Distance, hit.MaterialId, true);
    }

    private void HandleUiActions()
    {
        if (_input.ConsumePressed(VirtualBindingKind.Keyboard, "Escape"))
        {
            if (_uiMode == AndroidGameUiMode.Pause) SaveWorld();
            SetUiMode(_uiMode switch
            {
                AndroidGameUiMode.Gameplay => AndroidGameUiMode.Pause,
                AndroidGameUiMode.Pause => AndroidGameUiMode.Gameplay,
                AndroidGameUiMode.MainMenu => AndroidGameUiMode.MainMenu,
                AndroidGameUiMode.Settings or AndroidGameUiMode.RendererSelection or
                    AndroidGameUiMode.Language or AndroidGameUiMode.Achievements or
                    AndroidGameUiMode.Controls or AndroidGameUiMode.Mods => _uiReturnMode,
                _ => AndroidGameUiMode.Pause,
            });
            return;
        }
        if (_input.ConsumePressed(VirtualBindingKind.Keyboard, "E"))
        {
            if (_uiMode is AndroidGameUiMode.Gameplay or AndroidGameUiMode.Inventory)
                SetUiMode(_uiMode == AndroidGameUiMode.Inventory
                    ? AndroidGameUiMode.Gameplay
                    : AndroidGameUiMode.Inventory);
            return;
        }
        if (_input.ConsumePressed(VirtualBindingKind.Keyboard, "B"))
        {
            if (_uiMode is AndroidGameUiMode.Gameplay or AndroidGameUiMode.BuildTools)
                SetUiMode(_uiMode == AndroidGameUiMode.BuildTools
                    ? AndroidGameUiMode.Gameplay
                    : AndroidGameUiMode.BuildTools);
            return;
        }
        if (_uiMode != AndroidGameUiMode.Gameplay) return;
        if (_input.ConsumePressed(VirtualBindingKind.Keyboard, "V"))
        {
            _precisionMode = !_precisionMode;
            _microBuild.Enabled = _precisionMode;
            PublishUiState();
        }
    }

    private void SetUiMode(AndroidGameUiMode mode)
    {
        _uiMode = mode;
        _input.ReleaseAll();
        PublishUiState();
    }

    private void PublishUiState() => UiStateChanged?.Invoke(new AndroidGameUiState(
        _uiMode,
        _hotbar.SelectedBlock.DisplayName,
        _precisionMode,
        _rendererMode));

    private void RecreateProgram()
    {
        if (_program != 0) GLES20.GlDeleteProgram(_program);
        if (_transparentProgram != 0) GLES20.GlDeleteProgram(_transparentProgram);
        if (_lightingProgram != 0) GLES20.GlDeleteProgram(_lightingProgram);
        _lightingProgram = 0;
        _transparentProgram = CreateProgram(VertexShader, PbrFragmentShader);
        if (_rendererMode is AndroidRendererMode.Deferred or AndroidRendererMode.Hybrid && _supportsEs3)
        {
            _program = CreateProgram(DeferredVertexShader, DeferredGeometryFragmentShader);
            _lightingProgram = CreateProgram(DeferredFullscreenVertexShader, DeferredLightingFragmentShader,
                bindSceneAttributes: false);
            EnsureGBuffer();
        }
        else if (_rendererMode == AndroidRendererMode.ComputePathTracing && _supportsCompute)
        {
            _program = CreateProgram(VertexShader, ClassicFragmentShader);
            if (_computeProgram != 0) GLES20.GlDeleteProgram(_computeProgram);
            if (_computeDisplayProgram != 0) GLES20.GlDeleteProgram(_computeDisplayProgram);
            _computeProgram = CreateComputeProgram(ComputePathTracingShader);
            _computeDisplayProgram = CreateProgram(
                DeferredFullscreenVertexShader, ComputeDisplayFragmentShader, bindSceneAttributes: false);
            EnsureComputeTargets();
            _computeWorldDirty = true;
        }
        else
        {
            _program = CreateProgram(VertexShader, _rendererMode == AndroidRendererMode.PbrForward
                ? PbrFragmentShader
                : ClassicFragmentShader);
        }
        _mvpLocation = GLES20.GlGetUniformLocation(_program, "uMvp");
    }

    private static int CreateComputeProgram(string source)
    {
        int shader = CompileShader(GLES31.GlComputeShader, source);
        int program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(program, shader);
        GLES20.GlLinkProgram(program);
        int[] linked = new int[1];
        GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, linked, 0);
        GLES20.GlDeleteShader(shader);
        if (linked[0] == 0) throw new InvalidOperationException(GLES20.GlGetProgramInfoLog(program));
        return program;
    }

    private void CreateMaterialAtlasTexture()
    {
        var atlas = new ProceduralMaterialAtlas(_blocks);
        int materialCount = _blocks.Definitions.Max(definition => (int)definition.Id) + 1;
        int tileCount = materialCount * ProceduralMaterialAtlas.FacesPerMaterial;
        int tileRows = (tileCount + MaterialAtlasTilesPerRow - 1) / MaterialAtlasTilesPerRow;
        _materialAtlasWidth = MaterialAtlasTilesPerRow * ProceduralMaterialAtlas.TileSize;
        _materialAtlasHeight = Math.Max(1, tileRows) * ProceduralMaterialAtlas.TileSize;
        byte[] compactPixels = new byte[_materialAtlasWidth * _materialAtlasHeight * 4];
        byte[] compactDetails = new byte[_materialAtlasWidth * _materialAtlasHeight * 4];
        foreach (BlockDefinition definition in _blocks.Definitions)
        foreach (MaterialFace face in Enum.GetValues<MaterialFace>())
        {
            int tile = definition.Id * ProceduralMaterialAtlas.FacesPerMaterial + (int)face;
            int sourceX = tile % ProceduralMaterialAtlas.TilesPerRow * ProceduralMaterialAtlas.TileSize;
            int sourceY = tile / ProceduralMaterialAtlas.TilesPerRow * ProceduralMaterialAtlas.TileSize;
            int destinationX = tile % MaterialAtlasTilesPerRow * ProceduralMaterialAtlas.TileSize;
            int destinationY = tile / MaterialAtlasTilesPerRow * ProceduralMaterialAtlas.TileSize;
            for (int row = 0; row < ProceduralMaterialAtlas.TileSize; row++)
            {
                int source = ((sourceY + row) * ProceduralMaterialAtlas.Width + sourceX) * 4;
                int destination = ((destinationY + row) * _materialAtlasWidth + destinationX) * 4;
                Array.Copy(atlas.ColorPixels, source, compactPixels, destination,
                    ProceduralMaterialAtlas.TileSize * 4);
                for (int column = 0; column < ProceduralMaterialAtlas.TileSize; column++)
                {
                    int sourceDetail = ((sourceY + row) * ProceduralMaterialAtlas.Width + sourceX + column) * 2;
                    int destinationDetail = destination + column * 4;
                    compactDetails[destinationDetail] = atlas.DetailPixels[sourceDetail];
                    compactDetails[destinationDetail + 1] = atlas.DetailPixels[sourceDetail + 1];
                    compactDetails[destinationDetail + 2] = 0;
                    compactDetails[destinationDetail + 3] = 255;
                }
            }
        }

        using ByteBuffer pixels = ByteBuffer.AllocateDirect(compactPixels.Length)!;
        pixels.Put(compactPixels);
        pixels.Position(0);
        using ByteBuffer details = ByteBuffer.AllocateDirect(compactDetails.Length)!;
        details.Put(compactDetails);
        details.Position(0);

        if (_materialAtlasTexture != 0 || _materialDetailAtlasTexture != 0)
            GLES20.GlDeleteTextures(2, new[] { _materialAtlasTexture, _materialDetailAtlasTexture }, 0);
        int[] textures = new int[2];
        GLES20.GlGenTextures(2, textures, 0);
        _materialAtlasTexture = textures[0];
        _materialDetailAtlasTexture = textures[1];
        GLES20.GlBindTexture(GLES20.GlTexture2d, _materialAtlasTexture);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlNearest);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlNearest);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba,
            _materialAtlasWidth, _materialAtlasHeight, 0,
            GLES20.GlRgba, GLES20.GlUnsignedByte, pixels);

        GLES20.GlBindTexture(GLES20.GlTexture2d, _materialDetailAtlasTexture);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMinFilter, GLES20.GlNearest);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureMagFilter, GLES20.GlNearest);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES20.GlTexture2d, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);
        GLES20.GlTexImage2D(GLES20.GlTexture2d, 0, GLES20.GlRgba,
            _materialAtlasWidth, _materialAtlasHeight, 0,
            GLES20.GlRgba, GLES20.GlUnsignedByte, details);
        int error = GLES20.GlGetError();
        if (error != GLES20.GlNoError)
            _status($"Android material atlas upload failed: GL 0x{error:X}");
    }

    private void EnsureGBuffer()
    {
        if (!_supportsEs3 || _width <= 1 || _height <= 1) return;
        if (_gbufferFramebuffer != 0 && _gbufferWidth == _width && _gbufferHeight == _height) return;
        DeleteGBuffer();
        int[] textures = new int[4];
        GLES30.GlGenTextures(textures.Length, textures, 0);
        _gbufferAlbedoTexture = textures[0];
        _gbufferNormalTexture = textures[1];
        _gbufferLightingTexture = textures[2];
        _gbufferDepthTexture = textures[3];
        ConfigureColorTarget(_gbufferAlbedoTexture);
        ConfigureColorTarget(_gbufferNormalTexture);
        ConfigureColorTarget(_gbufferLightingTexture);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _gbufferDepthTexture);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlDepthComponent24, _width, _height, 0,
            GLES30.GlDepthComponent, GLES30.GlUnsignedInt, null);

        int[] framebuffers = new int[1];
        GLES30.GlGenFramebuffers(1, framebuffers, 0);
        _gbufferFramebuffer = framebuffers[0];
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _gbufferFramebuffer);
        GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlColorAttachment0,
            GLES30.GlTexture2d, _gbufferAlbedoTexture, 0);
        GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlColorAttachment1,
            GLES30.GlTexture2d, _gbufferNormalTexture, 0);
        GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlColorAttachment2,
            GLES30.GlTexture2d, _gbufferLightingTexture, 0);
        GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlDepthAttachment,
            GLES30.GlTexture2d, _gbufferDepthTexture, 0);
        GLES30.GlDrawBuffers(3,
            new[] { GLES30.GlColorAttachment0, GLES30.GlColorAttachment1, GLES30.GlColorAttachment2 }, 0);
        int status = GLES30.GlCheckFramebufferStatus(GLES30.GlFramebuffer);
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
        if (status != GLES30.GlFramebufferComplete)
        {
            DeleteGBuffer();
            _status($"Android G-buffer incomplete: 0x{status:X}");
        }
        else
        {
            _gbufferWidth = _width;
            _gbufferHeight = _height;
        }
    }

    private void EnsureComputeTargets()
    {
        if (!_supportsCompute || _width <= 1 || _height <= 1) return;
        if (_computeOutputTexture == 0)
        {
            int[] textures = new int[4];
            GLES30.GlGenTextures(textures.Length, textures, 0);
            _computeOutputTexture = textures[0];
            _computeVoxelTexture = textures[1];
            _computeFluidTexture = textures[2];
            _computeMaterialTexture = textures[3];

            GLES30.GlBindTexture(GLES30.GlTexture3d, _computeVoxelTexture);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapR, GLES30.GlClampToEdge);
            GLES30.GlTexImage3D(GLES30.GlTexture3d, 0, GLES30.GlR16ui,
                ComputeWindowSize, ComputeWindowHeight, ComputeWindowSize, 0,
                GLES30.GlRedInteger, GLES30.GlUnsignedShort, null);

            GLES30.GlBindTexture(GLES30.GlTexture3d, _computeFluidTexture);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapR, GLES30.GlClampToEdge);
            GLES30.GlTexImage3D(GLES30.GlTexture3d, 0, GLES30.GlR8ui,
                ComputeWindowSize, ComputeWindowHeight, ComputeWindowSize, 0,
                GLES30.GlRedInteger, GLES30.GlUnsignedByte, null);
            UploadComputeMaterials();
        }

        GLES30.GlBindTexture(GLES30.GlTexture2d, _computeOutputTexture);
        if (_computeTargetWidth != _width || _computeTargetHeight != _height)
        {
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba8, _width, _height, 0,
                GLES30.GlRgba, GLES30.GlUnsignedByte, null);
            _computeTargetWidth = _width;
            _computeTargetHeight = _height;
        }
    }

    private void UploadComputeMaterials()
    {
        const int materialCount = 4096;
        byte[] colors = new byte[materialCount * 4];
        foreach (BlockDefinition material in _blocks.PlaceableDefinitions())
        {
            if (material.Id >= materialCount) continue;
            int offset = material.Id * 4;
            Vector3 color = Vector3.Clamp(material.Albedo, Vector3.Zero, Vector3.One);
            colors[offset] = (byte)MathF.Round(color.X * 255f);
            colors[offset + 1] = (byte)MathF.Round(color.Y * 255f);
            colors[offset + 2] = (byte)MathF.Round(color.Z * 255f);
            colors[offset + 3] = (byte)MathF.Round(Math.Clamp(material.Metallic, 0f, 1f) * 255f);
        }
        using ByteBuffer bytes = ByteBuffer.AllocateDirect(colors.Length)!;
        bytes.Put(colors);
        bytes.Position(0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _computeMaterialTexture);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba8, materialCount, 1, 0,
            GLES30.GlRgba, GLES30.GlUnsignedByte, bytes);
    }

    private void UploadComputeWorld(Vector3 eye)
    {
        _computeOriginX = (int)MathF.Floor(eye.X) - ComputeWindowSize / 2;
        _computeOriginY = Math.Clamp((int)MathF.Floor(eye.Y) - ComputeWindowHeight / 2,
            VoxelWorld.MinimumY, VoxelWorld.MaximumY - ComputeWindowHeight);
        _computeOriginZ = (int)MathF.Floor(eye.Z) - ComputeWindowSize / 2;
        ushort[] source = _world.CreateRenderVolumeSnapshot(
            _computeOriginX, _computeOriginY, _computeOriginZ,
            ComputeWindowSize, ComputeWindowHeight, ComputeWindowSize);
        short[] window = new short[source.Length];
        System.Buffer.BlockCopy(source, 0, window, 0, source.Length * sizeof(ushort));

        using ByteBuffer bytes = ByteBuffer.AllocateDirect(window.Length * sizeof(short))!;
        bytes.Order(ByteOrder.NativeOrder()!);
        ShortBuffer shorts = bytes.AsShortBuffer()!;
        shorts.Put(window);
        shorts.Position(0);
        GLES30.GlBindTexture(GLES30.GlTexture3d, _computeVoxelTexture);
        GLES30.GlTexSubImage3D(GLES30.GlTexture3d, 0, 0, 0, 0,
            ComputeWindowSize, ComputeWindowHeight, ComputeWindowSize,
            GLES30.GlRedInteger, GLES30.GlUnsignedShort, shorts);

        byte[] fluidLevels = _world.CreateFluidRenderVolumeSnapshot(
            _computeOriginX, _computeOriginY, _computeOriginZ,
            ComputeWindowSize, ComputeWindowHeight, ComputeWindowSize);
        using ByteBuffer fluidBytes = ByteBuffer.AllocateDirect(fluidLevels.Length)!;
        fluidBytes.Put(fluidLevels);
        fluidBytes.Position(0);
        GLES30.GlBindTexture(GLES30.GlTexture3d, _computeFluidTexture);
        GLES30.GlTexSubImage3D(GLES30.GlTexture3d, 0, 0, 0, 0,
            ComputeWindowSize, ComputeWindowHeight, ComputeWindowSize,
            GLES30.GlRedInteger, GLES30.GlUnsignedByte, fluidBytes);
        _computeWorldDirty = false;
    }

    private void ConfigureColorTarget(int texture)
    {
        GLES30.GlBindTexture(GLES30.GlTexture2d, texture);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba8, _width, _height, 0,
            GLES30.GlRgba, GLES30.GlUnsignedByte, null);
    }

    private void DeleteGBuffer()
    {
        if (_gbufferFramebuffer != 0)
            GLES30.GlDeleteFramebuffers(1, new[] { _gbufferFramebuffer }, 0);
        int[] textures =
            { _gbufferAlbedoTexture, _gbufferNormalTexture, _gbufferLightingTexture, _gbufferDepthTexture };
        if (textures.Any(texture => texture != 0)) GLES30.GlDeleteTextures(textures.Length, textures, 0);
        _gbufferFramebuffer = 0;
        _gbufferAlbedoTexture = 0;
        _gbufferNormalTexture = 0;
        _gbufferLightingTexture = 0;
        _gbufferDepthTexture = 0;
        _gbufferWidth = 0;
        _gbufferHeight = 0;
    }

    private void UploadWorldMesh()
    {
        ChunkCoordinate center = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(_player.Position.X),
            (int)MathF.Floor(_player.Position.Z));
        HashSet<ChunkCoordinate> desired = _world.SnapshotLoadedChunks()
            .Where(chunk => Math.Abs(chunk.X - center.X) <= 2 && Math.Abs(chunk.Z - center.Z) <= 2)
            .ToHashSet();
        _meshCenter = center;

        foreach (ChunkCoordinate stale in _chunkMeshes.Keys.Where(chunk => !desired.Contains(chunk)).ToArray())
        {
            DeleteMesh(_chunkMeshes[stale]);
            _chunkMeshes.Remove(stale);
            _shadowDirty = true;
        }

        if (_chunkMeshes.Count == 0)
        {
            ChunkCoordinate? nearest = desired
                .OrderBy(chunk => Math.Max(Math.Abs(chunk.X - center.X), Math.Abs(chunk.Z - center.Z)))
                .ThenBy(chunk => chunk.Z)
                .ThenBy(chunk => chunk.X)
                .Cast<ChunkCoordinate?>()
                .FirstOrDefault();
            if (nearest.HasValue)
            {
                ChunkCoordinate coordinate = nearest.Value;
                MeshBuildResult result = BuildChunkMesh(_world.CreateChunkMeshingSnapshot(coordinate));
                _chunkMeshes[coordinate] = UploadMesh(result, null);
                _dirtyMeshChunks.Remove(coordinate);
                _shadowDirty = true;
            }
        }
        else
        {
            CommitCompletedChunkMesh(desired);
            if (_pendingChunkMeshBuild is null)
            {
                ChunkCoordinate? next = desired
                    .Where(coordinate =>
                        !_chunkMeshes.ContainsKey(coordinate) || _dirtyMeshChunks.Contains(coordinate))
                    .OrderBy(coordinate => Math.Max(
                        Math.Abs(coordinate.X - center.X),
                        Math.Abs(coordinate.Z - center.Z)))
                    .ThenBy(coordinate => coordinate.Z)
                    .ThenBy(coordinate => coordinate.X)
                    .Cast<ChunkCoordinate?>()
                    .FirstOrDefault();
                if (next.HasValue)
                {
                    ChunkCoordinate coordinate = next.Value;
                    ChunkMeshingSnapshot snapshot = _world.CreateChunkMeshingSnapshot(coordinate);
                    int version = _chunkMeshVersions.GetValueOrDefault(coordinate);
                    _pendingChunkMeshBuild = Task.Run(() => new PendingChunkMesh(
                        coordinate,
                        version,
                        BuildChunkMesh(snapshot)));
                }
            }
        }

        if (_gridMeshDirty || _gridMesh is null)
        {
            _gridMesh = UploadMesh(BuildGridMesh(), _gridMesh);
            _gridMeshDirty = false;
            _shadowDirty = true;
        }

        _opaqueVertexCount = _chunkMeshes.Values.Sum(mesh => mesh.OpaqueVertexCount) +
                             (_gridMesh?.OpaqueVertexCount ?? 0);
        _transparentVertexCount = _chunkMeshes.Values.Sum(mesh => mesh.TransparentVertexCount) +
                                  (_gridMesh?.TransparentVertexCount ?? 0);
        _vertexCount = _opaqueVertexCount + _transparentVertexCount;
        _meshDirty = desired.Any(coordinate =>
            !_chunkMeshes.ContainsKey(coordinate) || _dirtyMeshChunks.Contains(coordinate)) ||
                     _pendingChunkMeshBuild is not null;
    }

    private void CommitCompletedChunkMesh(HashSet<ChunkCoordinate> desired)
    {
        if (_pendingChunkMeshBuild is null || !_pendingChunkMeshBuild.IsCompleted) return;
        try
        {
            PendingChunkMesh completed = _pendingChunkMeshBuild.GetAwaiter().GetResult();
            int currentVersion = _chunkMeshVersions.GetValueOrDefault(completed.Coordinate);
            if (desired.Contains(completed.Coordinate))
            {
                _chunkMeshes.TryGetValue(completed.Coordinate, out GpuMesh? previous);
                _chunkMeshes[completed.Coordinate] = UploadMesh(completed.Mesh, previous);
                _shadowDirty = true;
                if (currentVersion == completed.Version)
                    _dirtyMeshChunks.Remove(completed.Coordinate);
            }
        }
        catch (Exception exception)
        {
            _status($"Android chunk mesh failed safely: {exception.Message}");
        }
        finally
        {
            _pendingChunkMeshBuild = null;
        }
    }

    private MeshBuildResult BuildChunkMesh(ChunkMeshingSnapshot chunk)
    {
        var opaque = new List<float>(16_000);
        var transparent = new List<float>(4_000);
        AddGreedyOpaqueGeometry(opaque, chunk);
        ChunkCoordinate coordinate = chunk.Coordinate;
        for (int z = coordinate.MinimumZ; z < coordinate.MinimumZ + ChunkCoordinate.Size; z++)
        for (int y = chunk.MinimumY; y < chunk.MaximumYExclusive; y++)
        for (int x = coordinate.MinimumX; x < coordinate.MinimumX + ChunkCoordinate.Size; x++)
        {
            ushort materialId = chunk.GetBlock(x, y, z);
            if (materialId == 0) continue;
            BlockDefinition material = _blocks.Get(materialId);
            if (material.IsFluid)
            {
                AddFluidGeometry(transparent, chunk, x, y, z, material);
                continue;
            }
            if (!IsTransparent(material)) continue;
            List<float> target = IsTransparent(material) ? transparent : opaque;
            foreach (Face face in Faces)
            {
                ushort neighbor = chunk.GetBlock(x + face.X, y + face.Y, z + face.Z);
                if (!ShouldRenderFace(material, neighbor)) continue;
                AddFace(target, new Vector3(x, y, z), Vector3.One, face, material);
            }
        }

        foreach (StoredMicroVoxel micro in chunk.MicroVoxels)
        {
            float scale = 1f / micro.Snapshot.Resolution;
            Vector3 cellOrigin = new(
                coordinate.MinimumX + micro.LocalX,
                micro.Y,
                coordinate.MinimumZ + micro.LocalZ);
            foreach (MicroVoxelBox box in micro.Snapshot.Boxes)
            {
                BlockDefinition material = _blocks.Get(box.MaterialId);
                Vector3 minimum = cellOrigin + new Vector3(box.MinX, box.MinY, box.MinZ) * scale;
                Vector3 maximum = cellOrigin + new Vector3(box.MaxX, box.MaxY, box.MaxZ) * scale;
                AddBox(IsTransparent(material) ? transparent : opaque, minimum, maximum, material);
            }
        }

        return FinishMesh(opaque, transparent);
    }

    private void AddGreedyOpaqueGeometry(List<float> target, ChunkMeshingSnapshot chunk)
    {
        ChunkCoordinate coordinate = chunk.Coordinate;
        int height = chunk.MaximumYExclusive - chunk.MinimumY;
        int[] mask = new int[Math.Max(ChunkCoordinate.Size * ChunkCoordinate.Size,
            ChunkCoordinate.Size * height)];

        for (int faceIndex = 0; faceIndex < Faces.Length; faceIndex++)
        {
            Face face = Faces[faceIndex];
            int layers = face.X != 0 || face.Z != 0 ? ChunkCoordinate.Size : height;
            int width = face.X != 0 ? ChunkCoordinate.Size : ChunkCoordinate.Size;
            int rows = face.Y != 0 ? ChunkCoordinate.Size : height;
            if (face.Z != 0)
            {
                width = ChunkCoordinate.Size;
                rows = height;
            }
            else if (face.X != 0)
            {
                width = ChunkCoordinate.Size;
                rows = height;
            }

            for (int layer = 0; layer < layers; layer++)
            {
                Array.Clear(mask, 0, width * rows);
                for (int row = 0; row < rows; row++)
                for (int column = 0; column < width; column++)
                {
                    int x;
                    int y;
                    int z;
                    if (face.Y != 0)
                    {
                        x = coordinate.MinimumX + column;
                        y = chunk.MinimumY + layer;
                        z = coordinate.MinimumZ + row;
                    }
                    else if (face.X != 0)
                    {
                        x = coordinate.MinimumX + layer;
                        y = chunk.MinimumY + row;
                        z = coordinate.MinimumZ + column;
                    }
                    else
                    {
                        x = coordinate.MinimumX + column;
                        y = chunk.MinimumY + row;
                        z = coordinate.MinimumZ + layer;
                    }

                    ushort materialId = chunk.GetBlock(x, y, z);
                    if (materialId == 0) continue;
                    BlockDefinition material = _blocks.Get(materialId);
                    if (material.IsFluid || IsTransparent(material)) continue;
                    ushort neighbor = chunk.GetBlock(x + face.X, y + face.Y, z + face.Z);
                    if (ShouldRenderFace(material, neighbor)) mask[row * width + column] = materialId;
                }

                for (int row = 0; row < rows; row++)
                for (int column = 0; column < width;)
                {
                    int materialId = mask[row * width + column];
                    if (materialId == 0)
                    {
                        column++;
                        continue;
                    }

                    int rectangleWidth = 1;
                    while (column + rectangleWidth < width &&
                           mask[row * width + column + rectangleWidth] == materialId)
                        rectangleWidth++;
                    int rectangleHeight = 1;
                    bool canGrow = true;
                    while (row + rectangleHeight < rows && canGrow)
                    {
                        for (int offset = 0; offset < rectangleWidth; offset++)
                        {
                            if (mask[(row + rectangleHeight) * width + column + offset] == materialId) continue;
                            canGrow = false;
                            break;
                        }
                        if (canGrow) rectangleHeight++;
                    }

                    for (int clearRow = 0; clearRow < rectangleHeight; clearRow++)
                    for (int clearColumn = 0; clearColumn < rectangleWidth; clearColumn++)
                        mask[(row + clearRow) * width + column + clearColumn] = 0;

                    Vector3 minimum;
                    Vector3 size;
                    if (face.Y != 0)
                    {
                        minimum = new Vector3(
                            coordinate.MinimumX + column,
                            chunk.MinimumY + layer,
                            coordinate.MinimumZ + row);
                        size = new Vector3(rectangleWidth, 1f, rectangleHeight);
                    }
                    else if (face.X != 0)
                    {
                        minimum = new Vector3(
                            coordinate.MinimumX + layer,
                            chunk.MinimumY + row,
                            coordinate.MinimumZ + column);
                        size = new Vector3(1f, rectangleHeight, rectangleWidth);
                    }
                    else
                    {
                        minimum = new Vector3(
                            coordinate.MinimumX + column,
                            chunk.MinimumY + row,
                            coordinate.MinimumZ + layer);
                        size = new Vector3(rectangleWidth, rectangleHeight, 1f);
                    }
                    AddFace(target, minimum, size, face, _blocks.Get((ushort)materialId));
                }
            }
        }
    }

    private MeshBuildResult BuildGridMesh()
    {
        var opaque = new List<float>(2_000);
        var transparent = new List<float>(500);
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        foreach (PhysicsGridElement element in grid.Elements)
        {
            BlockDefinition material = _blocks.Get(element.MaterialId);
            AddGridBox(IsTransparent(material) ? transparent : opaque, grid, element, material);
        }

        return FinishMesh(opaque, transparent);
    }

    private static MeshBuildResult FinishMesh(List<float> opaque, List<float> transparent)
    {
        int opaqueVertexCount = opaque.Count / FloatsPerVertex;
        int transparentVertexCount = transparent.Count / FloatsPerVertex;
        opaque.AddRange(transparent);
        return new MeshBuildResult(opaque.ToArray(), opaqueVertexCount, transparentVertexCount);
    }

    private static GpuMesh UploadMesh(MeshBuildResult result, GpuMesh? previous)
    {
        int buffer = previous?.Buffer ?? 0;
        if (result.Vertices.Length == 0)
        {
            if (buffer != 0) GLES20.GlDeleteBuffers(1, new[] { buffer }, 0);
            return new GpuMesh(0, 0, 0);
        }
        if (buffer == 0)
        {
            int[] buffers = new int[1];
            GLES20.GlGenBuffers(1, buffers, 0);
            buffer = buffers[0];
        }
        ByteBuffer bytes = ByteBuffer.AllocateDirect(result.Vertices.Length * sizeof(float))!;
        bytes.Order(ByteOrder.NativeOrder()!);
        FloatBuffer floats = bytes.AsFloatBuffer()!;
        floats.Put(result.Vertices);
        floats.Position(0);
        GLES20.GlBindBuffer(GLES20.GlArrayBuffer, buffer);
        GLES20.GlBufferData(
            GLES20.GlArrayBuffer,
            result.Vertices.Length * sizeof(float),
            floats,
            GLES20.GlStaticDraw);
        return new GpuMesh(buffer, result.OpaqueVertexCount, result.TransparentVertexCount);
    }

    private static void DeleteMesh(GpuMesh mesh)
    {
        if (mesh.Buffer != 0) GLES20.GlDeleteBuffers(1, new[] { mesh.Buffer }, 0);
    }

    private static void DrawMesh(GpuMesh mesh, bool transparent)
    {
        int count = transparent ? mesh.TransparentVertexCount : mesh.OpaqueVertexCount;
        if (mesh.Buffer == 0 || count == 0) return;
        GLES20.GlBindBuffer(GLES20.GlArrayBuffer, mesh.Buffer);
        int stride = FloatsPerVertex * sizeof(float);
        EnableAttribute(0, 3, stride, 0);
        EnableAttribute(1, 3, stride, 3 * sizeof(float));
        EnableAttribute(2, 3, stride, 6 * sizeof(float));
        EnableAttribute(3, 1, stride, 9 * sizeof(float));
        EnableAttribute(4, 1, stride, 10 * sizeof(float));
        EnableAttribute(5, 1, stride, 11 * sizeof(float));
        EnableAttribute(6, 4, stride, 12 * sizeof(float));
        EnableAttribute(7, 3, stride, 16 * sizeof(float));
        GLES20.GlDrawArrays(
            GLES20.GlTriangles,
            transparent ? mesh.OpaqueVertexCount : 0,
            count);
    }

    private void MarkChunkAndNeighborsDirty(int worldX, int worldZ)
    {
        ChunkCoordinate coordinate = ChunkCoordinate.FromVoxel(worldX, worldZ);
        MarkChunkDirty(coordinate);
        int localX = worldX - coordinate.MinimumX;
        int localZ = worldZ - coordinate.MinimumZ;
        if (localX == 0) MarkChunkDirty(new ChunkCoordinate(coordinate.X - 1, coordinate.Z));
        if (localX == ChunkCoordinate.Size - 1)
            MarkChunkDirty(new ChunkCoordinate(coordinate.X + 1, coordinate.Z));
        if (localZ == 0) MarkChunkDirty(new ChunkCoordinate(coordinate.X, coordinate.Z - 1));
        if (localZ == ChunkCoordinate.Size - 1)
            MarkChunkDirty(new ChunkCoordinate(coordinate.X, coordinate.Z + 1));
        _meshDirty = true;
    }

    private void MarkChunkAndNeighborsDirty(ChunkCoordinate coordinate)
    {
        MarkChunkDirty(coordinate);
        MarkChunkDirty(new ChunkCoordinate(coordinate.X - 1, coordinate.Z));
        MarkChunkDirty(new ChunkCoordinate(coordinate.X + 1, coordinate.Z));
        MarkChunkDirty(new ChunkCoordinate(coordinate.X, coordinate.Z - 1));
        MarkChunkDirty(new ChunkCoordinate(coordinate.X, coordinate.Z + 1));
        _meshDirty = true;
    }

    private void MarkAllChunkMeshesDirty()
    {
        _computeWorldDirty = true;
        foreach (ChunkCoordinate coordinate in _world.SnapshotLoadedChunks())
            MarkChunkDirty(coordinate);
        _meshDirty = true;
    }

    private void MarkChunkDirty(ChunkCoordinate coordinate)
    {
        _dirtyMeshChunks.Add(coordinate);
        _chunkMeshVersions[coordinate] = _chunkMeshVersions.GetValueOrDefault(coordinate) + 1;
    }

    private bool ShouldRenderFace(BlockDefinition material, ushort neighborId)
    {
        if (neighborId == 0) return true;
        BlockDefinition neighbor = _blocks.Get(neighborId);
        if (material.Id == neighborId && IsTransparent(material)) return false;
        return IsTransparent(material) || IsTransparent(neighbor);
    }

    private static bool IsTransparent(BlockDefinition material) =>
        material.IsTransparent || material.IsFluid || material.Transmission > 0f;

    private static float Alpha(BlockDefinition material)
    {
        if (material.IsFluid) return 0.58f;
        if (!IsTransparent(material)) return 1f;
        return Math.Clamp(0.22f + (1f - material.Transmission) * 0.58f, 0.22f, 0.82f);
    }

    private void AddFluidGeometry(
        List<float> data,
        ChunkMeshingSnapshot chunk,
        int x,
        int y,
        int z,
        BlockDefinition material)
    {
        bool fluidAbove = _blocks.Get(chunk.GetBlock(x, y + 1, z)).IsFluid;
        float top = fluidAbove ? y + 1f : y + VoxelWorld.GetFluidSurfaceHeight(chunk.GetFluidLevel(x, y, z));
        Vector3 minimum = new(x, y, z);
        Vector3 maximum = new(x + 1f, top, z + 1f);
        if (!fluidAbove) AddFace(data, minimum, maximum - minimum, Faces[2], material);
        if (!_blocks.Get(chunk.GetBlock(x, y - 1, z)).IsFluid)
            AddFace(data, minimum, maximum - minimum, Faces[3], material);

        for (int faceIndex = 0; faceIndex < Faces.Length; faceIndex++)
        {
            Face face = Faces[faceIndex];
            if (face.Y != 0) continue;
            int neighborX = x + face.X;
            int neighborZ = z + face.Z;
            ushort neighborId = chunk.GetBlock(neighborX, y, neighborZ);
            float sideBottom = y;
            if (_blocks.Get(neighborId).IsFluid)
            {
                bool neighborAbove = _blocks.Get(chunk.GetBlock(neighborX, y + 1, neighborZ)).IsFluid;
                float neighborTop = neighborAbove
                    ? y + 1f
                    : y + VoxelWorld.GetFluidSurfaceHeight(chunk.GetFluidLevel(neighborX, y, neighborZ));
                if (neighborTop >= top - 0.0001f) continue;
                sideBottom = neighborTop;
            }
            Vector3 sideMinimum = new(x, sideBottom, z);
            Vector3 sideMaximum = new(x + 1f, top, z + 1f);
            AddFace(data, sideMinimum, sideMaximum - sideMinimum, face, material);
        }
    }

    private static void AddBox(
        List<float> data,
        Vector3 minimum,
        Vector3 maximum,
        BlockDefinition material)
    {
        Vector3 size = maximum - minimum;
        foreach (Face face in Faces) AddFace(data, minimum, size, face, material);
    }

    private static void AddGridBox(
        List<float> data,
        PhysicsGrid grid,
        PhysicsGridElement element,
        BlockDefinition material)
    {
        Vector3 size = element.Maximum - element.Minimum;
        int[] order = { 0, 1, 2, 0, 2, 3 };
        foreach (Face face in Faces)
        foreach (int index in order)
        {
            Vector3 faceLocal = face.Vertices[index] * size;
            Vector3 local = element.Minimum + faceLocal;
            Vector3 vertex = grid.LocalToWorld(local);
            Vector3 normal = Vector3.Normalize(Vector3.Transform(
                new Vector3(face.X, face.Y, face.Z), grid.Orientation));
            AddVertex(data, vertex, normal, FaceUv(face, faceLocal), material, MaterialFaceFor(face));
        }
    }

    private static void AddFace(
        List<float> data,
        Vector3 minimum,
        Vector3 size,
        Face face,
        BlockDefinition material)
    {
        int[] order = { 0, 1, 2, 0, 2, 3 };
        foreach (int index in order)
        {
            Vector3 faceLocal = face.Vertices[index] * size;
            Vector3 vertex = minimum + faceLocal;
            AddVertex(data, vertex, new Vector3(face.X, face.Y, face.Z),
                FaceUv(face, faceLocal), material, MaterialFaceFor(face));
        }
    }

    private void DrawModsImGui()
    {
        bool open = true;
        if (BeginCenteredWindow(Localize("模組", "模组", "Mods"), ref open, 0.78f, 0.78f))
        {
            ImGui.Text(BepInExBootstrap.Started
                ? Localize($"BepInEx 已啟動 · {BepInExBootstrap.LoadedPlugins.Count} 個模組",
                    $"BepInEx 已启动 · {BepInExBootstrap.LoadedPlugins.Count} 个模组",
                    $"BepInEx active · {BepInExBootstrap.LoadedPlugins.Count} mods")
                : string.IsNullOrWhiteSpace(BepInExBootstrap.FailureMessage)
                    ? Localize("BepInEx 尚未啟動 · 請先授予權限", "BepInEx 尚未启动 · 请先授予权限", "BepInEx not started · grant access")
                    : Localize("BepInEx 啟動失敗", "BepInEx 启动失败", "BepInEx failed to start"));
            ImGui.TextWrapped(Localize(
                "將模組與相依 DLL 直接放進此目錄，重新啟動後載入：",
                "将模组与依赖 DLL 直接放进此目录，重新启动后加载：",
                "Place mods and dependency DLLs directly in this folder, then restart:"));
            ImGui.TextWrapped(BepInExBootstrap.PluginDirectory);
            if (ImGui.Button(Localize("授予模組目錄權限", "授予模组目录权限", "Grant folder access"), new Vector2(220f, 40f)))
                ModStorageAccessRequested?.Invoke();
            ImGui.SameLine();
            if (ImGui.Button(Localize("選取 DLL 匯入（可選）", "选择 DLL 导入（可选）", "Import DLL (optional)"), new Vector2(220f, 40f)))
                ModImportRequested?.Invoke();
            ImGui.TextDisabled(Localize("也可直接放入上方目錄", "也可直接放入上方目录", "Direct placement is supported"));
            ImGui.Separator();

            if (!string.IsNullOrWhiteSpace(BepInExBootstrap.FailureMessage))
                ImGui.TextWrapped(BepInExBootstrap.FailureMessage);
            foreach (string error in BepInExBootstrap.DependencyErrors)
                ImGui.TextWrapped($"! {error}");
            foreach (LoadedPluginInfo plugin in BepInExBootstrap.LoadedPlugins)
            {
                ImGui.SeparatorText(plugin.Name);
                ImGui.Text($"{plugin.Guid} · {plugin.Version}");
                ImGui.TextDisabled(plugin.Location);
            }
            if (BepInExBootstrap.LoadedPlugins.Count == 0 &&
                string.IsNullOrWhiteSpace(BepInExBootstrap.FailureMessage))
                ImGui.TextDisabled(Localize("尚未安裝模組。", "尚未安装模组。", "No mods installed."));

            ImGui.Separator();
            if (ImGui.Button(T("done"), new Vector2(180f, 42f))) open = false;
        }
        ImGui.End();
        if (!open) SetUiMode(_uiReturnMode);
    }

    private void ResetSettingsScrollForTab(int tab)
    {
        if (_settingsTab == tab) return;
        _settingsTab = tab;
        ImGui.SetScrollY(0f);
    }

    private static Vector2 FaceUv(Face face, Vector3 local) => face.Y != 0
        ? new Vector2(local.X, local.Z)
        : face.X != 0
            ? new Vector2(local.Z, local.Y)
            : new Vector2(local.X, local.Y);

    private static MaterialFace MaterialFaceFor(Face face) => face.Y > 0
        ? MaterialFace.Top
        : face.Y < 0
            ? MaterialFace.Bottom
            : MaterialFace.Side;

    private static void AddVertex(
        List<float> data,
        Vector3 vertex,
        Vector3 normal,
        Vector2 uv,
        BlockDefinition material,
        MaterialFace face)
    {
        data.Add(vertex.X); data.Add(vertex.Y); data.Add(vertex.Z);
        data.Add(normal.X); data.Add(normal.Y); data.Add(normal.Z);
        data.Add(material.Albedo.X); data.Add(material.Albedo.Y); data.Add(material.Albedo.Z);
        data.Add(material.Metallic); data.Add(material.Roughness); data.Add(Alpha(material));
        data.Add(uv.X); data.Add(uv.Y); data.Add(material.Id); data.Add((float)face);
        data.Add(material.Emission.X); data.Add(material.Emission.Y); data.Add(material.Emission.Z);
    }

    private static void EnableAttribute(int index, int size, int stride, int offset)
    {
        GLES20.GlEnableVertexAttribArray(index);
        GLES20.GlVertexAttribPointer(index, size, GLES20.GlFloat, false, stride, offset);
    }

    private void UpdateFps()
    {
        _fpsFrames++;
        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = now - _fpsWindowStart;
        if (elapsed < 1.0) return;
        int fps = (int)Math.Round(_fpsFrames / elapsed);
        string renderer = _rendererMode switch
        {
            AndroidRendererMode.PbrForward => "PBR Forward",
            AndroidRendererMode.Deferred => "Deferred",
            AndroidRendererMode.Hybrid => "Hybrid PBR",
            AndroidRendererMode.ComputePathTracing => "Compute PT",
            AndroidRendererMode.HardwareRayTracing => "Hardware RT",
            _ => "Classic",
        };
        string precision = _precisionMode ? " · V 精密模式" : string.Empty;
        _status($"{renderer} · {fps} FPS · {_vertexCount:N0} vertices · UI max {_maximumUiMilliseconds:0.0} ms{precision}");
        if (_physics.ActiveGridCount > 0)
            _status($"Physics Grid · {_physics.ActiveGridCount} active · {_physics.Status}");
        _fpsFrames = 0;
        _fpsWindowStart = now;
        _maximumUiMilliseconds = 0;
    }

    private static int CreateProgram(
        string vertexSource,
        string fragmentSource,
        bool bindSceneAttributes = true)
    {
        int vertex = CompileShader(GLES20.GlVertexShader, vertexSource);
        int fragment = CompileShader(GLES20.GlFragmentShader, fragmentSource);
        int program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(program, vertex);
        GLES20.GlAttachShader(program, fragment);
        if (bindSceneAttributes)
        {
            GLES20.GlBindAttribLocation(program, 0, "aPosition");
            GLES20.GlBindAttribLocation(program, 1, "aNormal");
            GLES20.GlBindAttribLocation(program, 2, "aColor");
            GLES20.GlBindAttribLocation(program, 3, "aMetallic");
            GLES20.GlBindAttribLocation(program, 4, "aRoughness");
            GLES20.GlBindAttribLocation(program, 5, "aAlpha");
            GLES20.GlBindAttribLocation(program, 6, "aTexMaterial");
            GLES20.GlBindAttribLocation(program, 7, "aEmission");
        }
        GLES20.GlLinkProgram(program);
        int[] linked = new int[1];
        GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, linked, 0);
        if (linked[0] == 0) throw new InvalidOperationException(GLES20.GlGetProgramInfoLog(program));
        GLES20.GlDeleteShader(vertex);
        GLES20.GlDeleteShader(fragment);
        return program;
    }

    private static int CompileShader(int type, string source)
    {
        int shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);
        int[] compiled = new int[1];
        GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, compiled, 0);
        if (compiled[0] == 0) throw new InvalidOperationException(GLES20.GlGetShaderInfoLog(shader));
        return shader;
    }

    private static float[] ToArray(Matrix4x4 value) =>
    [
        value.M11, value.M12, value.M13, value.M14,
        value.M21, value.M22, value.M23, value.M24,
        value.M31, value.M32, value.M33, value.M34,
        value.M41, value.M42, value.M43, value.M44,
    ];

    private sealed record MeshBuildResult(
        float[] Vertices,
        int OpaqueVertexCount,
        int TransparentVertexCount);

    private sealed record GpuMesh(
        int Buffer,
        int OpaqueVertexCount,
        int TransparentVertexCount);

    private sealed record PendingChunkMesh(
        ChunkCoordinate Coordinate,
        int Version,
        MeshBuildResult Mesh);

    private sealed record Face(int X, int Y, int Z, Vector3[] Vertices);

    private static readonly Face[] Faces =
    [
        new( 1, 0, 0, [new(1,0,0), new(1,1,0), new(1,1,1), new(1,0,1)]),
        new(-1, 0, 0, [new(0,0,1), new(0,1,1), new(0,1,0), new(0,0,0)]),
        new(0,  1, 0, [new(0,1,0), new(0,1,1), new(1,1,1), new(1,1,0)]),
        new(0, -1, 0, [new(0,0,1), new(0,0,0), new(1,0,0), new(1,0,1)]),
        new(0, 0,  1, [new(1,0,1), new(1,1,1), new(0,1,1), new(0,0,1)]),
        new(0, 0, -1, [new(0,0,0), new(0,1,0), new(1,1,0), new(1,0,0)]),
    ];

    private const string VertexShader = """
        uniform mat4 uMvp;
        uniform mat4 uLightMvp;
        attribute vec3 aPosition;
        attribute vec3 aNormal;
        attribute vec3 aColor;
        attribute float aMetallic;
        attribute float aRoughness;
        attribute float aAlpha;
        attribute vec4 aTexMaterial;
        attribute vec3 aEmission;
        varying vec3 vPosition;
        varying vec3 vNormal;
        varying vec3 vAlbedo;
        varying float vMetallic;
        varying float vRoughness;
        varying float vAlpha;
        varying vec2 vTexCoord;
        varying float vMaterialId;
        varying float vMaterialFace;
        varying vec3 vEmission;
        varying vec4 vShadowPosition;
        void main() {
            vPosition = aPosition;
            vNormal = aNormal;
            vAlbedo = aColor;
            vMetallic = aMetallic;
            vRoughness = aRoughness;
            vAlpha = aAlpha;
            vTexCoord = aTexMaterial.xy;
            vMaterialId = aTexMaterial.z;
            vMaterialFace = aTexMaterial.w;
            vEmission = aEmission;
            vShadowPosition = uLightMvp * vec4(aPosition, 1.0);
            gl_Position = uMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string ClassicFragmentShader = """
        precision mediump float;
        uniform sampler2D uShadowMap;
        uniform sampler2D uMaterialAtlas;
        uniform vec2 uMaterialAtlasSize;
        uniform vec2 uShadowTexel;
        uniform float uDaylight;
        varying vec3 vNormal;
        varying vec3 vAlbedo;
        varying float vAlpha;
        varying vec2 vTexCoord;
        varying float vMaterialId;
        varying float vMaterialFace;
        varying vec3 vEmission;
        varying vec4 vShadowPosition;
        vec2 materialUv(vec2 tiledUv) {
            float tile = floor(vMaterialId + 0.5) * 3.0 + floor(vMaterialFace + 0.5);
            vec2 origin = vec2(mod(tile, 32.0), floor(tile / 32.0)) * 16.0;
            vec2 pixel = floor(fract(tiledUv + vec2(0.0001)) * 16.0);
            return (origin + pixel + vec2(0.5)) / uMaterialAtlasSize;
        }
        float shadowVisibility(vec3 normal, vec3 lightDirection) {
            vec3 projected = vShadowPosition.xyz / max(vShadowPosition.w, 0.0001) * 0.5 + 0.5;
            if (projected.x <= 0.0 || projected.x >= 1.0 || projected.y <= 0.0 || projected.y >= 1.0 || projected.z >= 1.0)
                return 1.0;
            float bias = max(0.0022 * (1.0 - dot(normal, lightDirection)), 0.0007);
            float visibility = 0.0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++) {
                float depth = texture2D(uShadowMap, projected.xy + vec2(float(x), float(y)) * uShadowTexel).r;
                visibility += projected.z - bias <= depth ? 1.0 : 0.24;
            }
            return visibility / 9.0;
        }
        void main() {
            vec3 lightDirection = normalize(vec3(0.45, 0.82, 0.32));
            vec3 normal = normalize(vNormal);
            vec3 albedo = mix(vAlbedo, texture2D(uMaterialAtlas, materialUv(vTexCoord)).rgb, 0.96);
            float dayLight = 0.27 + max(dot(normal, lightDirection), 0.0) * 0.73 * shadowVisibility(normal, lightDirection);
            float light = mix(0.035, dayLight, uDaylight);
            gl_FragColor = vec4(albedo * light + vEmission * 0.42, vAlpha);
        }
        """;

    private const string PbrFragmentShader = """
        precision highp float;
        const float PI = 3.14159265359;
        uniform vec3 uCamera;
        uniform sampler2D uShadowMap;
        uniform sampler2D uMaterialAtlas;
        uniform sampler2D uMaterialDetailAtlas;
        uniform vec2 uMaterialAtlasSize;
        uniform vec2 uShadowTexel;
        uniform float uTime;
        uniform float uWaterMaterial;
        uniform float uDaylight;
        varying vec3 vPosition;
        varying vec3 vNormal;
        varying vec3 vAlbedo;
        varying float vMetallic;
        varying float vRoughness;
        varying float vAlpha;
        varying vec2 vTexCoord;
        varying float vMaterialId;
        varying float vMaterialFace;
        varying vec3 vEmission;
        varying vec4 vShadowPosition;

        vec2 materialUv(vec2 tiledUv) {
            float tile = floor(vMaterialId + 0.5) * 3.0 + floor(vMaterialFace + 0.5);
            vec2 origin = vec2(mod(tile, 32.0), floor(tile / 32.0)) * 16.0;
            vec2 pixel = floor(fract(tiledUv + vec2(0.0001)) * 16.0);
            return (origin + pixel + vec2(0.5)) / uMaterialAtlasSize;
        }

        vec3 detailNormal(vec3 geometricNormal, vec2 tiledUv) {
            vec2 uv = materialUv(tiledUv);
            vec2 stepUv = vec2(1.0) / uMaterialAtlasSize;
            float center = texture2D(uMaterialDetailAtlas, uv).g;
            float right = texture2D(uMaterialDetailAtlas, uv + vec2(stepUv.x, 0.0)).g;
            float up = texture2D(uMaterialDetailAtlas, uv + vec2(0.0, stepUv.y)).g;
            vec2 gradient = vec2(right - center, up - center);
            vec3 n = normalize(geometricNormal);
            vec3 tangent = abs(n.y) > 0.65 ? vec3(1.0, 0.0, 0.0)
                : abs(n.x) > 0.65 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
            vec3 bitangent = normalize(cross(n, tangent));
            return normalize(n - tangent * gradient.x * 2.2 - bitangent * gradient.y * 2.2);
        }

        float shadowVisibility(vec3 normal, vec3 lightDirection) {
            vec3 projected = vShadowPosition.xyz / max(vShadowPosition.w, 0.0001) * 0.5 + 0.5;
            if (projected.x <= 0.0 || projected.x >= 1.0 || projected.y <= 0.0 || projected.y >= 1.0 || projected.z >= 1.0)
                return 1.0;
            float bias = max(0.0022 * (1.0 - dot(normal, lightDirection)), 0.0007);
            float visibility = 0.0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++) {
                float depth = texture2D(uShadowMap, projected.xy + vec2(float(x), float(y)) * uShadowTexel).r;
                visibility += projected.z - bias <= depth ? 1.0 : 0.24;
            }
            return visibility / 9.0;
        }

        float distributionGgx(vec3 n, vec3 h, float roughness) {
            float a = roughness * roughness;
            float a2 = a * a;
            float nh = max(dot(n, h), 0.0);
            float d = nh * nh * (a2 - 1.0) + 1.0;
            return a2 / max(PI * d * d, 0.0001);
        }

        float geometrySchlick(float nv, float roughness) {
            float r = roughness + 1.0;
            float k = r * r / 8.0;
            return nv / max(nv * (1.0 - k) + k, 0.0001);
        }

        vec3 fresnelSchlick(float hv, vec3 f0) {
            return f0 + (1.0 - f0) * pow(1.0 - hv, 5.0);
        }

        void main() {
            vec2 atlasUv = materialUv(vTexCoord);
            vec3 albedo = mix(vAlbedo, texture2D(uMaterialAtlas, atlasUv).rgb, 0.96);
            vec2 detail = texture2D(uMaterialDetailAtlas, atlasUv).rg;
            vec3 n = detailNormal(vNormal, vTexCoord);
            if (abs(vMaterialId - uWaterMaterial) < 0.5) {
                vec2 wave = vec2(
                    sin(vPosition.x * 2.3 + uTime * 1.35) + sin(vPosition.z * 3.1 - uTime * 0.92),
                    cos(vPosition.z * 2.0 + uTime * 1.08) + cos(vPosition.x * 2.7 - uTime * 0.74));
                n = normalize(n + vec3(wave.x * 0.055, 0.0, wave.y * 0.055));
            }
            vec3 v = normalize(uCamera - vPosition);
            vec3 l = normalize(vec3(0.45, 0.82, 0.32));
            vec3 h = normalize(v + l);
            float roughness = clamp(mix(vRoughness, detail.r, 0.82), 0.04, 1.0);
            float metallic = clamp(vMetallic, 0.0, 1.0);
            float nl = max(dot(n, l), 0.0);
            float nv = max(dot(n, v), 0.001);
            vec3 f0 = mix(vec3(0.04), albedo, metallic);
            vec3 f = fresnelSchlick(max(dot(h, v), 0.0), f0);
            float d = distributionGgx(n, h, roughness);
            float g = geometrySchlick(nv, roughness) * geometrySchlick(max(dot(n, l), 0.001), roughness);
            vec3 specular = d * g * f / max(4.0 * nv * max(nl, 0.001), 0.001);
            vec3 diffuse = (1.0 - f) * (1.0 - metallic) * albedo / PI;
            float visibility = shadowVisibility(n, l);
            vec3 reflectedSky = mix(vec3(0.08, 0.14, 0.24), vec3(0.38, 0.64, 0.92),
                clamp(reflect(-v, n).y * 0.5 + 0.5, 0.0, 1.0)) * mix(0.035, 1.0, uDaylight);
            vec3 color = (diffuse + specular) * nl * visibility * 2.2 * uDaylight
                + albedo * mix(0.025, 0.14, uDaylight) + vEmission;
            if (vAlpha < 0.99) {
                float fresnel = 0.035 + 0.965 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
                vec3 transmitted = albedo * mix(0.72, 0.46, roughness);
                color = mix(transmitted, reflectedSky * mix(vec3(1.0), albedo, 0.35), fresnel);
                color += vEmission * 0.55;
            } else {
                color += reflectedSky * f * (1.0 - roughness) * 0.24;
            }
            color = color / (color + vec3(1.0));
            color = pow(color, vec3(1.0 / 2.2));
            gl_FragColor = vec4(color, vAlpha);
        }
        """;

    private const string DeferredVertexShader = """
        #version 300 es
        uniform mat4 uMvp;
        uniform mat4 uLightMvp;
        uniform vec3 uCamera;
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aColor;
        layout(location = 3) in float aMetallic;
        layout(location = 4) in float aRoughness;
        layout(location = 5) in float aAlpha;
        layout(location = 6) in vec4 aTexMaterial;
        layout(location = 7) in vec3 aEmission;
        out vec3 vPosition;
        out vec3 vNormal;
        out vec3 vAlbedo;
        out float vMetallic;
        out float vRoughness;
        out vec2 vTexCoord;
        out float vMaterialId;
        out float vMaterialFace;
        out vec3 vEmission;
        out vec3 vViewDirection;
        out vec4 vShadowPosition;
        void main() {
            vPosition = aPosition;
            vNormal = aNormal;
            vAlbedo = aColor;
            vMetallic = aMetallic;
            vRoughness = aRoughness;
            vTexCoord = aTexMaterial.xy;
            vMaterialId = aTexMaterial.z;
            vMaterialFace = aTexMaterial.w;
            vEmission = aEmission;
            vViewDirection = normalize(uCamera - aPosition);
            vShadowPosition = uLightMvp * vec4(aPosition, 1.0);
            gl_Position = uMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string DeferredGeometryFragmentShader = """
        #version 300 es
        precision highp float;
        in vec3 vPosition;
        in vec3 vNormal;
        in vec3 vAlbedo;
        in float vMetallic;
        in float vRoughness;
        in vec2 vTexCoord;
        in float vMaterialId;
        in float vMaterialFace;
        in vec3 vEmission;
        in vec3 vViewDirection;
        in vec4 vShadowPosition;
        uniform sampler2D uShadowMap;
        uniform sampler2D uMaterialAtlas;
        uniform sampler2D uMaterialDetailAtlas;
        uniform vec2 uMaterialAtlasSize;
        uniform vec2 uShadowTexel;
        layout(location = 0) out vec4 gAlbedoMetal;
        layout(location = 1) out vec4 gNormalRough;
        layout(location = 2) out vec4 gLighting;
        vec2 materialUv(vec2 tiledUv) {
            float tile = floor(vMaterialId + 0.5) * 3.0 + floor(vMaterialFace + 0.5);
            vec2 origin = vec2(mod(tile, 32.0), floor(tile / 32.0)) * 16.0;
            vec2 pixel = floor(fract(tiledUv + vec2(0.0001)) * 16.0);
            return (origin + pixel + vec2(0.5)) / uMaterialAtlasSize;
        }
        vec3 detailNormal(vec3 geometricNormal, vec2 tiledUv) {
            vec2 uv = materialUv(tiledUv);
            vec2 stepUv = vec2(1.0) / uMaterialAtlasSize;
            float center = texture(uMaterialDetailAtlas, uv).g;
            float right = texture(uMaterialDetailAtlas, uv + vec2(stepUv.x, 0.0)).g;
            float up = texture(uMaterialDetailAtlas, uv + vec2(0.0, stepUv.y)).g;
            vec2 gradient = vec2(right - center, up - center);
            vec3 n = normalize(geometricNormal);
            vec3 tangent = abs(n.y) > 0.65 ? vec3(1.0, 0.0, 0.0)
                : abs(n.x) > 0.65 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
            vec3 bitangent = normalize(cross(n, tangent));
            return normalize(n - tangent * gradient.x * 2.2 - bitangent * gradient.y * 2.2);
        }
        float shadowVisibility(vec3 normal, vec3 lightDirection) {
            vec3 projected = vShadowPosition.xyz / max(vShadowPosition.w, 0.0001) * 0.5 + 0.5;
            if (projected.x <= 0.0 || projected.x >= 1.0 || projected.y <= 0.0 || projected.y >= 1.0 || projected.z >= 1.0)
                return 1.0;
            float bias = max(0.0022 * (1.0 - dot(normal, lightDirection)), 0.0007);
            float visibility = 0.0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++) {
                float depth = texture(uShadowMap, projected.xy + vec2(float(x), float(y)) * uShadowTexel).r;
                visibility += projected.z - bias <= depth ? 1.0 : 0.24;
            }
            return visibility / 9.0;
        }
        void main() {
            vec2 atlasUv = materialUv(vTexCoord);
            vec3 albedo = mix(vAlbedo, texture(uMaterialAtlas, atlasUv).rgb, 0.96);
            vec2 detail = texture(uMaterialDetailAtlas, atlasUv).rg;
            vec3 normal = detailNormal(vNormal, vTexCoord);
            vec3 lightDirection = normalize(vec3(0.45, 0.82, 0.32));
            // Emission is folded into the HDR-range albedo input on GLES so the
            // three-target G-buffer stays compatible with mobile hardware.
            albedo += min(vEmission, vec3(2.0)) * 0.35;
            gAlbedoMetal = vec4(albedo, clamp(vMetallic, 0.0, 1.0));
            gNormalRough = vec4(normal * 0.5 + 0.5, clamp(mix(vRoughness, detail.r, 0.82), 0.04, 1.0));
            gLighting = vec4(normalize(vViewDirection) * 0.5 + 0.5,
                shadowVisibility(normal, lightDirection));
        }
        """;

    private const string DeferredFullscreenVertexShader = """
        #version 300 es
        precision highp float;
        out vec2 vUv;
        void main() {
            vec2 position = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vUv = position;
            gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string DeferredLightingFragmentShader = """
        #version 300 es
        precision highp float;
        uniform sampler2D uAlbedoMetal;
        uniform sampler2D uNormalRough;
        uniform sampler2D uLighting;
        uniform sampler2D uDepth;
        uniform vec2 uResolution;
        uniform int uHybrid;
        uniform float uDaylight;
        in vec2 vUv;
        out vec4 outColor;
        const float PI = 3.14159265359;
        float distributionGgx(vec3 n, vec3 h, float roughness) {
            float a = roughness * roughness;
            float a2 = a * a;
            float nh = max(dot(n, h), 0.0);
            float d = nh * nh * (a2 - 1.0) + 1.0;
            return a2 / max(PI * d * d, 0.0001);
        }
        float geometrySchlick(float nv, float roughness) {
            float r = roughness + 1.0;
            float k = r * r / 8.0;
            return nv / max(nv * (1.0 - k) + k, 0.0001);
        }
        vec3 fresnelSchlick(float hv, vec3 f0) {
            return f0 + (1.0 - f0) * pow(1.0 - hv, 5.0);
        }
        float screenSpaceOcclusion(vec2 uv) {
            float center = texture(uDepth, uv).r;
            if (center >= 0.9999) return 1.0;
            vec2 texel = 1.0 / uResolution;
            float blocked = 0.0;
            blocked += smoothstep(0.00008, 0.007, center - texture(uDepth, uv + vec2( 2.0, 0.0) * texel).r);
            blocked += smoothstep(0.00008, 0.007, center - texture(uDepth, uv + vec2(-2.0, 0.0) * texel).r);
            blocked += smoothstep(0.00008, 0.007, center - texture(uDepth, uv + vec2(0.0,  2.0) * texel).r);
            blocked += smoothstep(0.00008, 0.007, center - texture(uDepth, uv + vec2(0.0, -2.0) * texel).r);
            return clamp(1.0 - blocked * 0.105, 0.58, 1.0);
        }
        void main() {
            vec4 albedoMetal = texture(uAlbedoMetal, vUv);
            vec4 normalRough = texture(uNormalRough, vUv);
            if (dot(normalRough.rgb, normalRough.rgb) < 0.001) {
                outColor = vec4(mix(vec3(0.12, 0.25, 0.42), vec3(0.36, 0.62, 0.90), vUv.y)
                    * mix(0.035, 1.0, uDaylight), 1.0);
                return;
            }
            vec3 n = normalize(normalRough.rgb * 2.0 - 1.0);
            vec3 albedo = albedoMetal.rgb;
            float metallic = albedoMetal.a;
            float roughness = clamp(normalRough.a, 0.04, 1.0);
            vec4 lighting = texture(uLighting, vUv);
            vec3 v = normalize(lighting.rgb * 2.0 - 1.0);
            float shadow = lighting.a;
            vec3 l = normalize(vec3(0.45, 0.82, 0.32));
            vec3 h = normalize(v + l);
            float nl = max(dot(n, l), 0.0);
            float nv = max(dot(n, v), 0.001);
            vec3 f0 = mix(vec3(0.04), albedo, metallic);
            vec3 f = fresnelSchlick(max(dot(h, v), 0.0), f0);
            float d = distributionGgx(n, h, roughness);
            float g = geometrySchlick(nv, roughness) * geometrySchlick(max(nl, 0.001), roughness);
            vec3 specular = d * g * f / max(4.0 * nv * max(nl, 0.001), 0.001);
            vec3 diffuse = (1.0 - f) * (1.0 - metallic) * albedo / PI;
            vec3 color = (diffuse + specular) * nl * shadow * 2.2 * uDaylight
                + albedo * mix(0.025, 0.14, uDaylight);
            color *= screenSpaceOcclusion(vUv);
            vec3 reflected = reflect(-v, n);
            vec3 reflectedSky = mix(vec3(0.08, 0.13, 0.21), vec3(0.40, 0.66, 0.94),
                clamp(reflected.y * 0.5 + 0.5, 0.0, 1.0)) * mix(0.035, 1.0, uDaylight);
            float environmentWeight = clamp(max(max(f.r, f.g), f.b) * (1.0 - roughness * 0.80), 0.0, 0.94);
            if (uHybrid != 0)
                color = mix(color, reflectedSky * mix(vec3(1.0), albedo, metallic), environmentWeight);
            else
                color += reflectedSky * f * (1.0 - roughness) * 0.16;
            color = color / (color + vec3(1.0));
            color = pow(color, vec3(1.0 / 2.2));
            outColor = vec4(color, 1.0);
        }
        """;

    private const string ShadowVertexShader = """
        uniform mat4 uLightMvp;
        attribute vec3 aPosition;
        void main() {
            gl_Position = uLightMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string ShadowFragmentShader = """
        precision highp float;
        void main() {
            float depth = gl_FragCoord.z;
            gl_FragColor = vec4(depth, depth, depth, 1.0);
        }
        """;

    private const string ComputePathTracingShader = """
        #version 310 es
        precision highp float;
        precision highp int;
        precision highp usampler3D;
        layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
        layout(rgba8, binding = 0) writeonly uniform highp image2D uOutput;
        uniform highp usampler3D uVoxels;
        uniform highp usampler3D uFluidLevels;
        uniform sampler2D uMaterials;
        uniform vec2 uResolution;
        uniform vec3 uCamera;
        uniform vec3 uForward;
        uniform vec3 uRight;
        uniform vec3 uUp;
        uniform float uTanHalfFov;
        uniform float uDaylight;
        uniform ivec3 uVoxelOrigin;
        uniform int uWaterMaterial;
        const ivec3 WindowSize = ivec3(64, 256, 64);

        bool insideWindow(ivec3 cell) {
            ivec3 localCell = cell - uVoxelOrigin;
            return all(greaterThanEqual(localCell, ivec3(0))) && all(lessThan(localCell, WindowSize));
        }

        uint voxelAt(ivec3 cell) {
            if (!insideWindow(cell)) return 0u;
            return texelFetch(uVoxels, cell - uVoxelOrigin, 0).r;
        }

        uint fluidLevelAt(ivec3 cell) {
            if (!insideWindow(cell)) return 0u;
            return texelFetch(uFluidLevels, cell - uVoxelOrigin, 0).r;
        }

        uint traceVoxel(vec3 origin, vec3 direction, out vec3 normal, out vec3 hitPosition) {
            vec3 ray = normalize(direction);
            ivec3 cell = ivec3(floor(origin));
            ivec3 stepDirection = ivec3(sign(ray));
            vec3 inverseDirection = 1.0 / max(abs(ray), vec3(0.00001));
            vec3 cellFraction = origin - floor(origin);
            vec3 sideDistance = mix(cellFraction, 1.0 - cellFraction, greaterThan(ray, vec3(0.0))) * inverseDirection;
            vec3 deltaDistance = inverseDirection;
            normal = vec3(0.0);
            float distanceAlongRay = 0.0;
            for (int iteration = 0; iteration < 192; iteration++) {
                uint material = voxelAt(cell);
                if (material != 0u) {
                    float cellExit = min(sideDistance.x, min(sideDistance.y, sideDistance.z));
                    if (material == uint(uWaterMaterial)) {
                        float level = float(min(fluidLevelAt(cell), 15u));
                        float surfaceY = float(cell.y) + 0.875 * (16.0 - level) / 16.0;
                        vec3 entryPosition = origin + ray * distanceAlongRay;
                        if (entryPosition.y <= surfaceY + 0.0005) {
                            hitPosition = entryPosition;
                            return material;
                        }
                        if (ray.y < -0.00001) {
                            float surfaceDistance = (surfaceY - origin.y) / ray.y;
                            if (surfaceDistance >= distanceAlongRay && surfaceDistance <= cellExit) {
                                normal = vec3(0.0, 1.0, 0.0);
                                hitPosition = origin + ray * surfaceDistance;
                                return material;
                            }
                        }
                    } else {
                        hitPosition = origin + ray * distanceAlongRay;
                        return material;
                    }
                }
                if (!insideWindow(cell) && distanceAlongRay > 96.0) break;
                if (sideDistance.x <= sideDistance.y && sideDistance.x <= sideDistance.z) {
                    distanceAlongRay = sideDistance.x;
                    sideDistance.x += deltaDistance.x;
                    cell.x += stepDirection.x;
                    normal = vec3(-float(stepDirection.x), 0.0, 0.0);
                } else if (sideDistance.y <= sideDistance.z) {
                    distanceAlongRay = sideDistance.y;
                    sideDistance.y += deltaDistance.y;
                    cell.y += stepDirection.y;
                    normal = vec3(0.0, -float(stepDirection.y), 0.0);
                } else {
                    distanceAlongRay = sideDistance.z;
                    sideDistance.z += deltaDistance.z;
                    cell.z += stepDirection.z;
                    normal = vec3(0.0, 0.0, -float(stepDirection.z));
                }
                if (distanceAlongRay > 96.0) break;
            }
            hitPosition = origin + ray * 96.0;
            return 0u;
        }

        vec4 materialData(uint id) {
            return texelFetch(uMaterials, ivec2(int(min(id, 4095u)), 0), 0);
        }

        float hash12(vec2 value) {
            vec3 p = fract(vec3(value.xyx) * 0.1031);
            p += dot(p, p.yzx + 33.33);
            return fract((p.x + p.y) * p.z);
        }

        vec3 skyColor(vec3 direction) {
            return mix(vec3(0.10, 0.18, 0.30), vec3(0.36, 0.64, 0.92),
                clamp(direction.y * 0.5 + 0.5, 0.0, 1.0)) * mix(0.035, 1.0, uDaylight);
        }

        void main() {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
            if (pixel.x >= int(uResolution.x) || pixel.y >= int(uResolution.y)) return;
            vec2 ndc = ((vec2(pixel) + 0.5) / uResolution) * 2.0 - 1.0;
            ndc.x *= uResolution.x / uResolution.y;
            vec3 ray = normalize(uForward + uRight * ndc.x * uTanHalfFov + uUp * ndc.y * uTanHalfFov);
            vec3 normal;
            vec3 hitPosition;
            uint materialId = traceVoxel(uCamera, ray, normal, hitPosition);
            vec3 color;
            if (materialId == 0u) {
                color = skyColor(ray);
            } else {
                vec4 material = materialData(materialId);
                vec3 sunDirection = normalize(vec3(0.45, 0.82, 0.32));
                vec3 shadowNormal;
                vec3 shadowHit;
                uint shadowBlock = traceVoxel(hitPosition + normal * 0.015, sunDirection, shadowNormal, shadowHit);
                float visibility = shadowBlock == 0u ? 1.0 : 0.16;
                float diffuse = max(dot(normal, sunDirection), 0.0) * visibility;
                color = material.rgb * (mix(0.025, 0.10, uDaylight) + diffuse * 0.90 * uDaylight);

                float metallic = material.a;
                if (metallic > 0.12) {
                    vec3 reflectedDirection = reflect(ray, normal);
                    vec3 reflectedNormal;
                    vec3 reflectedHit;
                    uint reflectedId = traceVoxel(hitPosition + normal * 0.02, reflectedDirection,
                        reflectedNormal, reflectedHit);
                    vec3 reflectedColor = reflectedId == 0u
                        ? skyColor(reflectedDirection)
                        : materialData(reflectedId).rgb * (0.25 + 0.75 * max(dot(reflectedNormal, sunDirection), 0.0));
                    color = mix(color, reflectedColor * mix(vec3(1.0), material.rgb, metallic),
                        clamp(0.18 + metallic * 0.72, 0.0, 0.92));
                } else {
                    float randomAngle = hash12(vec2(pixel)) * 6.2831853;
                    vec3 tangent = normalize(abs(normal.y) < 0.9 ? cross(normal, vec3(0.0, 1.0, 0.0)) : cross(normal, vec3(1.0, 0.0, 0.0)));
                    vec3 bitangent = cross(normal, tangent);
                    vec3 bounceDirection = normalize(normal + tangent * cos(randomAngle) * 0.55 + bitangent * sin(randomAngle) * 0.55);
                    vec3 bounceNormal;
                    vec3 bounceHit;
                    uint bounceId = traceVoxel(hitPosition + normal * 0.02, bounceDirection, bounceNormal, bounceHit);
                    vec3 bounceColor = bounceId == 0u ? skyColor(bounceDirection) : materialData(bounceId).rgb;
                    color += material.rgb * bounceColor * 0.16;
                }
            }
            color = color / (color + vec3(1.0));
            color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
            imageStore(uOutput, pixel, vec4(color, 1.0));
        }
        """;

    private const string ComputeDisplayFragmentShader = """
        #version 300 es
        precision highp float;
        uniform sampler2D uImage;
        in vec2 vUv;
        out vec4 outColor;
        void main() {
            outColor = texture(uImage, vUv);
        }
        """;
}
