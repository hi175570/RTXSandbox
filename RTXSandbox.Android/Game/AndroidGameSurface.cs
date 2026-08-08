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
using RTXSandbox.Persistence;
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
    private const int FloatsPerVertex = 12;
    private const int ComputeWindowSize = 64;
    private const int ComputeWindowHeight = 256;
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
    private int _gbufferDepthTexture;
    private int _gbufferWidth;
    private int _gbufferHeight;
    private bool _supportsEs3;
    private bool _supportsCompute;
    private int _computeProgram;
    private int _computeDisplayProgram;
    private int _computeOutputTexture;
    private int _computeVoxelTexture;
    private int _computeMaterialTexture;
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
    private string _paletteSearch = string.Empty;
    private readonly HashSet<string> _favoriteBlocks = new(StringComparer.OrdinalIgnoreCase);
    private string _paletteCategory = "All";
    private bool _favoritesOnly;
    private IReadOnlyList<WorldSaveSummary> _knownWorlds = Array.Empty<WorldSaveSummary>();
    private string _selectedWorldStorage = "world1";
    private string _worldRenameDraft = "world1";
    private string _worldNameDraft = "New World";
    private int _worldSeedDraft = 1337;
    private string _worldMenuStatus = string.Empty;
    private double _maximumUiMilliseconds;
    private int _saveRequested;
    private int _saveWorkerRunning;

    public AndroidGameRenderer(
        VirtualInputState input,
        string baseContentPath,
        string contentPacksPath,
        IPlatformStorage storage,
        Action<string> status)
    {
        _input = input;
        _status = status;
        string? storedLanguage = global::Android.App.Application.Context
            .GetSharedPreferences("rtxsandbox-ui", FileCreationMode.Private)
            ?.GetString("language", UiLanguage.TraditionalChinese.ToString());
        if (Enum.TryParse(storedLanguage, out UiLanguage language)) _language = language;
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
        int spawnX = VoxelWorld.Width / 2;
        int spawnZ = VoxelWorld.Depth / 2 + 8;
        float spawnY = _world.FindTopVoxelY(spawnX, spawnZ) + 1.001f;
        Vector3 spawn = loaded ? state.PlayerPosition : new Vector3(spawnX + 0.5f, spawnY, spawnZ + 0.5f);
        _player = new PlayerController(_world, spawn);
        _microBuild = new MicroBuildSystem(_world, _blocks, _player.WouldIntersectAabb);
        _waterFlow = new WaterFlowSimulation(_world);
        _physics = new VoxelPhysicsSimulator(_world, _blocks);
        _hotbar = new CreativeHotbar(_blocks);
        _items = new ItemRegistry(_blocks);
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
            ? $"撌脰??亙?其???{Path.GetFileName(_worldSaves.LastLoadSource)}"
            : error is null ? "撌脣遣蝡?冽撘??? : $"??瑼??亙仃??撌脣??典遣蝡???{error}");
        RefreshWorldList();
    }

    public event Action<AndroidGameUiState>? UiStateChanged;

    public IReadOnlyList<AndroidBlockChoice> BlockChoices { get; }

    public IReadOnlyList<AndroidRendererChoice> RendererChoices =>
    [
        new("Classic", "蝬?", true, "OpenGL ES raster"),
        new("PbrForward", "PBR ???", true, "OpenGL ES forward material lighting"),
        new("Deferred", "撱園皜脫?", _supportsEs3,
            _supportsEs3 ? "OpenGL ES 3 MRT G-buffer" : "?閬?OpenGL ES 3.0 MRT"),
        new("Hybrid", "瘛瑕?撘?PBR", _supportsEs3,
            _supportsEs3 ? "Deferred geometry + screen-space environment pass" : "?閬?OpenGL ES 3.0 MRT"),
        new("ComputePathTracing", "Compute 頝臬?餈質馱", _supportsCompute,
            _supportsCompute ? "OpenGL ES 3.1 compute voxel ray tracing" : "?閬?OpenGL ES 3.1 compute shader"),
        new("HardwareRayTracing", "蝖祇???餈質馱", false, "DXR ???Windows Direct3D 12"),
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
                8.5f,
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
        _status($"Classic 繚 ?梁銝?嚗?瑼?繚 {_glVersion}");
        try
        {
            _imgui = new AndroidImGuiController(
                global::Android.App.Application.Context.Resources?.DisplayMetrics?.Density ?? 1f,
                AndroidUiGlyphText);
        }
        catch (Exception exception)
        {
            _status($"Android ImGui ???仃??{exception.Message}");
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
                    _camera.Rotate(new Vector2(lookX, lookY), 1f);

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
                float targetFov = sprint ? 7f : 0f;
                float blend = 1f - MathF.Exp(-10f * deltaTime);
                _sprintFovOffset += (targetFov - _sprintFovOffset) * blend;
                _camera.FieldOfViewDegrees = 72f + _sprintFovOffset;
                HandleBlockActions();
                _world.QueueChunksAround(_player.Position, radius: 2);
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

            if (_meshDirty && _clock.Elapsed.TotalSeconds - _lastMeshUpload >= 0.08)
            {
                UploadWorldMesh();
                _lastMeshUpload = _clock.Elapsed.TotalSeconds;
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
        GLES20.GlBindFramebuffer(GLES20.GlFramebuffer, 0);
        GLES20.GlViewport(0, 0, _width, _height);
        GLES20.GlClearColor(0.32f, 0.58f, 0.86f, 1f);
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
        GLES31.GlUniform3i(GLES31.GlGetUniformLocation(_computeProgram, "uVoxelOrigin"),
            _computeOriginX, _computeOriginY, _computeOriginZ);
        GLES31.GlActiveTexture(GLES31.GlTexture0);
        GLES31.GlBindTexture(GLES30.GlTexture3d, _computeVoxelTexture);
        GLES31.GlUniform1i(GLES31.GlGetUniformLocation(_computeProgram, "uVoxels"), 0);
        GLES31.GlActiveTexture(GLES31.GlTexture1);
        GLES31.GlBindTexture(GLES31.GlTexture2d, _computeMaterialTexture);
        GLES31.GlUniform1i(GLES31.GlGetUniformLocation(_computeProgram, "uMaterials"), 1);
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

    private void RenderDeferred(Matrix4x4 matrix, Vector3 eye, bool hybrid)
    {
        EnsureGBuffer();
        if (_gbufferFramebuffer == 0)
        {
            RenderForward(matrix, eye);
            return;
        }

        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _gbufferFramebuffer);
        GLES30.GlViewport(0, 0, _width, _height);
        GLES30.GlDrawBuffers(2, new[] { GLES30.GlColorAttachment0, GLES30.GlColorAttachment1 }, 0);
        GLES30.GlClearColor(0f, 0f, 0f, 0f);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
        DrawScenePass(_program, matrix, eye, includeTransparent: false);

        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
        GLES30.GlViewport(0, 0, _width, _height);
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlClearColor(0.32f, 0.58f, 0.86f, 1f);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
        GLES30.GlUseProgram(_lightingProgram);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _gbufferAlbedoTexture);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_lightingProgram, "uAlbedoMetal"), 0);
        GLES30.GlActiveTexture(GLES30.GlTexture1);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _gbufferNormalTexture);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_lightingProgram, "uNormalRough"), 1);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_lightingProgram, "uHybrid"), hybrid ? 1 : 0);
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
        for (int index = 0; index <= 5; index++) GLES20.GlDisableVertexAttribArray(index);
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
        }
        _imgui.EndFrame(_width, _height);
        if (tapPhase == 1) Volatile.Write(ref _uiTapPhase, 2);
        else if (tapPhase == 2) Volatile.Write(ref _uiTapPhase, 0);
    }

    private void DrawGameplayHudImGui()
    {
        ImGui.SetNextWindowPos(new Vector2(_width * 0.5f, _height - 82f),
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

        VoxelHit? target = VoxelRaycast.Cast(_world, _player.EyePosition, _camera.Forward, 10f);
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
            ImGui.Text($"?惇摨?{block.Metallic:0.00}  |  蝎?摨?{block.Roughness:0.00}");
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
            new Vector2(_width, _height), T, modsAvailable: false, showTouchControls: true,
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
            ImGui.SeparatorText(_language == UiLanguage.English ? "Worlds" : _language == UiLanguage.SimplifiedChinese ? "銝?" : "銝?");
            if (ImGui.BeginChild("SavedWorlds", new Vector2(0f, Math.Max(120f, menuHeight - 580f)), ImGuiChildFlags.Borders))
            {
                foreach (WorldSaveSummary world in _knownWorlds)
                {
                    bool current = string.Equals(world.StorageName, _worldSaves.WorldName, StringComparison.OrdinalIgnoreCase);
                    bool selected = string.Equals(world.StorageName, _selectedWorldStorage, StringComparison.OrdinalIgnoreCase);
                    string currentPrefix = current
                        ? (_language == UiLanguage.English ? "[Current] " : "[?桀?] ")
                        : string.Empty;
                    if (ImGui.Selectable($"{currentPrefix}{world.DisplayName}##world-{world.StorageName}",
                            selected, ImGuiSelectableFlags.None, new Vector2(0f, 34f)))
                    {
                        _selectedWorldStorage = world.StorageName;
                        _worldRenameDraft = world.DisplayName;
                        _worldMenuStatus = string.Empty;
                    }
                    ImGui.SameLine(Math.Max(205f, width * 0.48f));
                    ImGui.TextDisabled($"{world.Preset} 繚 Seed {world.Seed}");
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
                if (ImGui.Button("頛", new Vector2(quarter, 34f))) OpenWorld(selectedWorld.StorageName);
                if (current) ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("???", new Vector2(quarter, 34f))) RebuildWorld(selectedWorld.StorageName);
                ImGui.SameLine();
                if (ImGui.Button("??賢?", new Vector2(quarter, 34f))) RenameWorld(selectedWorld.StorageName);
                ImGui.SameLine();
                if (current) ImGui.BeginDisabled();
                if (ImGui.Button("?芷", new Vector2(quarter, 34f))) DeleteWorld(selectedWorld.StorageName);
                if (current) ImGui.EndDisabled();
                ImGui.SetNextItemWidth(width);
                ImGui.InputText("##world-rename", ref _worldRenameDraft, 64);
            }
            if (!string.IsNullOrWhiteSpace(_worldMenuStatus)) ImGui.TextWrapped(_worldMenuStatus);
            if (ImGui.Button("撱箇?銝?", new Vector2(width, 38f)))
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
        if (ImGui.Begin("撱箇?銝?", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.InputText("銝??迂", ref _worldNameDraft, 64);
            ImGui.InputInt("Seed", ref _worldSeedDraft);
            ImGui.TextDisabled("Natural 繚 Auto Biome 繚 Lakes 繚 Vegetation");
            ImGui.Separator();
            bool valid = !string.IsNullOrWhiteSpace(_worldNameDraft);
            if (!valid) ImGui.BeginDisabled();
            if (ImGui.Button("撱箇?", new Vector2(180f, 42f)))
            {
                CreateWorld(_worldNameDraft, _worldSeedDraft);
                open = false;
            }
            if (!valid) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("??", new Vector2(180f, 42f))) open = false;
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
            MathF.Min(_width - 24f, 900f), MathF.Min(_height - 24f, 560f)), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("RTX Sandbox Editor", ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled($"{BlockChoices.Count} materials | Tab or I closes editor");
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
                        bool selectedSlot = slot == _hotbar.SelectedSlot;
                        if (selectedSlot) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.62f, 0.92f, 0.95f));
                        if (ImGui.Button($"{slot + 1}\n{ShortBlockName(quickBlock.DisplayName)}##hotbar-{slot}", new Vector2(76f, 66f)))
                            _hotbar.SelectedSlot = slot;
                        if (selectedSlot) ImGui.PopStyleColor();
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
                            Vector3 tint = Vector3.Clamp(block.Albedo, new Vector3(0.08f), Vector3.One);
                            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(tint, 0.92f));
                            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Vector3.Min(Vector3.One, tint * 1.25f), 1f));
                            if (ImGui.Button($"{ShortBlockName(block.DisplayName)}##material-{block.Id}", new Vector2(104f, 92f)))
                            {
                                _hotbar.SelectAndAssign(block.Id);
                                PublishUiState();
                            }
                            ImGui.PopStyleColor(2);
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
        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "??;
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
        drawList.AddText(textPosition, textColor, text);

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

    private static void DrawMaterialCube(ImDrawListPtr drawList, Vector2 position, Vector2 size, BlockDefinition block)
    {
        Vector2 rightTop = position + new Vector2(size.X * 0.64f, 0f);
        Vector2 bottomRight = position + size;
        drawList.AddRectFilled(
            position,
            bottomRight,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.18f, 0.18f, 1f)),
            0.0f);
        drawList.AddRectFilled(
            position + new Vector2(3f, 3f),
            rightTop + new Vector2(2f, 2f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(block.Albedo * 0.78f + new Vector3(0.05f), 1f)),
            0.0f);
        drawList.AddRectFilled(
            rightTop,
            bottomRight - new Vector2(3f, 3f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(block.Albedo * 0.58f + new Vector3(0.05f), 1f)),
            0.0f);
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
        if (ImGui.Begin("RTX Sandbox 閮剖?", ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled("霈???喟???甇?虜??????芸?靽???);
            ImGui.Spacing();
            if (ImGui.BeginTabBar("SettingsTabs"))
            {
                if (ImGui.BeginTabItem("?恍"))
                {
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
                    ImGui.SeparatorText("憿舐內");
                    ImGui.Text($"閫??摨佗?{_width} ? {_height}");
                    ImGui.TextDisabled("Android ?圾?漲??VSync ?梁頂蝯?Surface 蝞∠???舀??Renderer 銝??質???具?);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("??????))
                {
                    ImGui.TextDisabled("銵???蝙?刻?銝?銝?渡??亙?鞈?嚗脤???賊??撠? Renderer ?亙??");
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("?批????))
                {
                    ImGui.TextWrapped("憭?萇雿輻 PC ?嚗ndroid ?血????舐楊頛舐?閫豢閬?撅扎?);
                    if (ImGui.Button(T("controls"), new Vector2(210f, 42f)))
                    {
                        _uiReturnMode = AndroidGameUiMode.Settings;
                        SetUiMode(AndroidGameUiMode.Controls);
                    }
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("閫豢"))
                {
                    ImGui.Dummy(new Vector2(0f, 12f));
                    if (ImGui.Button("??閫豢蝺刻摩??, new Vector2(240f, 48f)))
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
            ImGui.TextWrapped(_language == UiLanguage.English
                ? "External keyboard controls share the PC defaults. Select a key, then press its replacement. Touch controls remain an Android-only input layer."
                : _language == UiLanguage.SimplifiedChinese
                    ? "憭?桃?雿輻銝?PC ?詨???霈斗??柴圻?扳??桀??Android 銝颲撅?
                    : "憭?萇雿輻??PC ?詨???閮剜??萸孛?扳??萄??Android 撠頛詨撅扎?);
            if (_input.HardwareRebindTarget is not null)
                ImGui.TextColored(new Vector4(0.25f, 0.86f, 1f, 1f),
                    _language == UiLanguage.English ? "Press a key..." : "隢?銝???菊?);
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
            if (ImGui.Button(_language == UiLanguage.English ? "Reset defaults" : "?身?身",
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
        if (ImGui.Button($"{label}##binding-{kind}-{binding}", new Vector2(230f, 0f)))
            _input.BeginHardwareRebind(kind, binding);
    }

    private string T(string key) => UiText.Get(_language, key);

    private static readonly string[] AndroidUiGlyphText =
    {
        UiText.GlyphCorpus,
        "蝬????撱園皜脫?瘛瑕?撘楝敺蕭頩斤′擃?蝺?閬??Ｗ??舀撠?亙",
        "霈???喟??迤撣賊????脫??芸?靽??恍?????扳?嗡??Ｚ圾?漲蝟餌絞蝞∠?",
        "銵???蝙?其????湔???脤??賊?撠?憭?萇?身?閫豢撠頛詨撅?,
        "?拙?甈遣?極?瑟憛?鞈芣?撠翰?瑟?蝎曉?敺格憛芋撘蝺????身摰?撠梁絞閮芋蝯?閮?ａ?",
        "蝜?銝剜?蝪⊿?銝剜??望?蝜潛??蝺刻摩閫豢?停?迂隞晶撠閫??",
        "撱箇?銝?銝??迂??頛?????賢??芷憭望???撌脣??刻?嗥黎蝟餅?瘜?鋡恍?撅砍漲蝎?摨?,
    };

    private void HandleBlockActions()
    {
        bool destroy = _input.ConsumePressed(VirtualBindingKind.Mouse, "Left");
        bool place = _input.ConsumePressed(VirtualBindingKind.Mouse, "Right");
        if (!destroy && !place) return;

        PhysicsGridRayHit? gridHit = _world.PhysicsGrids.Raycast(
            _player.EyePosition, _camera.Forward, 10f);
        PrecisionHit? precision = _precisionMode ? CurrentPrecisionHit() : null;
        VoxelHit? hit = _precisionMode
            ? null
            : VoxelRaycast.Cast(_world, _player.EyePosition, _camera.Forward, 10f);
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
            if (gridChanged) _meshDirty = true;
            return;
        }

        if (_precisionMode)
        {
            bool precisionChanged = precision.HasValue &&
                                    (destroy
                                        ? _microBuild.Erase(precision.Value)
                                        : _microBuild.Place(precision.Value, _hotbar.SelectedBlock.Id));
            if (precisionChanged) _meshDirty = true;
            return;
        }

        bool changed = false;
        if (destroy && hit is { } remove && !_blocks.Get(remove.MaterialId).IsUnbreakable)
            changed = _world.SetBlockId(remove.X, remove.Y, remove.Z, 0);
        if (place && hit is { } target && PlacementRules.IsFarEnough(target.Distance))
        {
            (int x, int y, int z) = target.Adjacent;
            if (!_player.WouldIntersectBlock(x, y, z))
                changed = _world.SetBlockId(x, y, z, _hotbar.SelectedBlock.Id) || changed;
        }
        if (changed) _meshDirty = true;
    }

    private PrecisionHit? CurrentPrecisionHit()
    {
        VoxelHit? regularHit = VoxelRaycast.Cast(_world, _player.EyePosition, _camera.Forward, 10f);
        MicroVoxelHit? microHit = MicroVoxelRaycast.Cast(_world, _player.EyePosition, _camera.Forward, 10f);
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
                    AndroidGameUiMode.Language or AndroidGameUiMode.Achievements => _uiReturnMode,
                _ => AndroidGameUiMode.Pause,
            });
            return;
        }
        if (_uiMode != AndroidGameUiMode.Gameplay) return;
        if (_input.ConsumePressed(VirtualBindingKind.Keyboard, "E"))
            SetUiMode(AndroidGameUiMode.Inventory);
        else if (_input.ConsumePressed(VirtualBindingKind.Keyboard, "B"))
            SetUiMode(AndroidGameUiMode.BuildTools);
        else if (_input.ConsumePressed(VirtualBindingKind.Keyboard, "V"))
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

    private void EnsureGBuffer()
    {
        if (!_supportsEs3 || _width <= 1 || _height <= 1) return;
        if (_gbufferFramebuffer != 0 && _gbufferWidth == _width && _gbufferHeight == _height) return;
        DeleteGBuffer();
        int[] textures = new int[3];
        GLES30.GlGenTextures(textures.Length, textures, 0);
        _gbufferAlbedoTexture = textures[0];
        _gbufferNormalTexture = textures[1];
        _gbufferDepthTexture = textures[2];
        ConfigureColorTarget(_gbufferAlbedoTexture);
        ConfigureColorTarget(_gbufferNormalTexture);
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
        GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlDepthAttachment,
            GLES30.GlTexture2d, _gbufferDepthTexture, 0);
        GLES30.GlDrawBuffers(2, new[] { GLES30.GlColorAttachment0, GLES30.GlColorAttachment1 }, 0);
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
            int[] textures = new int[3];
            GLES30.GlGenTextures(textures.Length, textures, 0);
            _computeOutputTexture = textures[0];
            _computeVoxelTexture = textures[1];
            _computeMaterialTexture = textures[2];

            GLES30.GlBindTexture(GLES30.GlTexture3d, _computeVoxelTexture);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture3d, GLES30.GlTextureWrapR, GLES30.GlClampToEdge);
            GLES30.GlTexImage3D(GLES30.GlTexture3d, 0, GLES30.GlR16ui,
                ComputeWindowSize, ComputeWindowHeight, ComputeWindowSize, 0,
                GLES30.GlRedInteger, GLES30.GlUnsignedShort, null);
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
        ushort[] source = _world.CreateRenderWindowSnapshot(_computeOriginX, _computeOriginZ);
        short[] window = new short[ComputeWindowSize * ComputeWindowHeight * ComputeWindowSize];
        int sourceY = _computeOriginY - VoxelWorld.MinimumY;
        int rowBytes = ComputeWindowSize * sizeof(ushort);
        for (int z = 0; z < ComputeWindowSize; z++)
        for (int y = 0; y < ComputeWindowHeight; y++)
        {
            int sourceIndex = ComputeWindowSize * (sourceY + y + VoxelWorld.Height * z);
            int destinationIndex = ComputeWindowSize * (y + ComputeWindowHeight * z);
            System.Buffer.BlockCopy(source, sourceIndex * sizeof(ushort), window,
                destinationIndex * sizeof(short), rowBytes);
        }

        using ByteBuffer bytes = ByteBuffer.AllocateDirect(window.Length * sizeof(short))!;
        bytes.Order(ByteOrder.NativeOrder()!);
        ShortBuffer shorts = bytes.AsShortBuffer()!;
        shorts.Put(window);
        shorts.Position(0);
        GLES30.GlBindTexture(GLES30.GlTexture3d, _computeVoxelTexture);
        GLES30.GlTexSubImage3D(GLES30.GlTexture3d, 0, 0, 0, 0,
            ComputeWindowSize, ComputeWindowHeight, ComputeWindowSize,
            GLES30.GlRedInteger, GLES30.GlUnsignedShort, shorts);
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
        int[] textures = { _gbufferAlbedoTexture, _gbufferNormalTexture, _gbufferDepthTexture };
        if (textures.Any(texture => texture != 0)) GLES30.GlDeleteTextures(textures.Length, textures, 0);
        _gbufferFramebuffer = 0;
        _gbufferAlbedoTexture = 0;
        _gbufferNormalTexture = 0;
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
        }

        if (_chunkMeshes.Count == 0)
        {
            foreach (ChunkCoordinate coordinate in desired.OrderBy(chunk => chunk.Z).ThenBy(chunk => chunk.X))
            {
                MeshBuildResult result = BuildChunkMesh(_world.CreateChunkMeshingSnapshot(coordinate));
                _chunkMeshes[coordinate] = UploadMesh(result, null);
                _dirtyMeshChunks.Remove(coordinate);
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
            Vector3 local = element.Minimum + face.Vertices[index] * size;
            Vector3 vertex = grid.LocalToWorld(local);
            Vector3 normal = Vector3.Normalize(Vector3.Transform(
                new Vector3(face.X, face.Y, face.Z), grid.Orientation));
            AddVertex(data, vertex, normal, material);
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
            Vector3 vertex = minimum + face.Vertices[index] * size;
            AddVertex(data, vertex, new Vector3(face.X, face.Y, face.Z), material);
        }
    }

    private static void AddVertex(
        List<float> data,
        Vector3 vertex,
        Vector3 normal,
        BlockDefinition material)
    {
        data.Add(vertex.X); data.Add(vertex.Y); data.Add(vertex.Z);
        data.Add(normal.X); data.Add(normal.Y); data.Add(normal.Z);
        data.Add(material.Albedo.X); data.Add(material.Albedo.Y); data.Add(material.Albedo.Z);
        data.Add(material.Metallic); data.Add(material.Roughness); data.Add(Alpha(material));
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
        string precision = _precisionMode ? " 繚 V 蝎曉?" : string.Empty;
        _status($"{renderer} 繚 {fps} FPS 繚 {_vertexCount:N0} vertices 繚 UI max {_maximumUiMilliseconds:0.0} ms{precision}");
        if (_physics.ActiveGridCount > 0)
            _status($"Physics Grid 繚 {_physics.ActiveGridCount} active 繚 {_physics.Status}");
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
        attribute vec3 aPosition;
        attribute vec3 aNormal;
        attribute vec3 aColor;
        attribute float aMetallic;
        attribute float aRoughness;
        attribute float aAlpha;
        varying vec3 vPosition;
        varying vec3 vNormal;
        varying vec3 vAlbedo;
        varying float vMetallic;
        varying float vRoughness;
        varying float vAlpha;
        void main() {
            vPosition = aPosition;
            vNormal = aNormal;
            vAlbedo = aColor;
            vMetallic = aMetallic;
            vRoughness = aRoughness;
            vAlpha = aAlpha;
            gl_Position = uMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string ClassicFragmentShader = """
        precision mediump float;
        varying vec3 vNormal;
        varying vec3 vAlbedo;
        varying float vAlpha;
        void main() {
            vec3 lightDirection = normalize(vec3(0.45, 0.82, 0.32));
            float light = 0.30 + max(dot(normalize(vNormal), lightDirection), 0.0) * 0.70;
            gl_FragColor = vec4(vAlbedo * light, vAlpha);
        }
        """;

    private const string PbrFragmentShader = """
        precision highp float;
        const float PI = 3.14159265359;
        uniform vec3 uCamera;
        varying vec3 vPosition;
        varying vec3 vNormal;
        varying vec3 vAlbedo;
        varying float vMetallic;
        varying float vRoughness;
        varying float vAlpha;

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
            vec3 n = normalize(vNormal);
            vec3 v = normalize(uCamera - vPosition);
            vec3 l = normalize(vec3(0.45, 0.82, 0.32));
            vec3 h = normalize(v + l);
            float roughness = clamp(vRoughness, 0.05, 1.0);
            float metallic = clamp(vMetallic, 0.0, 1.0);
            float nl = max(dot(n, l), 0.0);
            float nv = max(dot(n, v), 0.001);
            vec3 f0 = mix(vec3(0.04), vAlbedo, metallic);
            vec3 f = fresnelSchlick(max(dot(h, v), 0.0), f0);
            float d = distributionGgx(n, h, roughness);
            float g = geometrySchlick(nv, roughness) * geometrySchlick(max(dot(n, l), 0.001), roughness);
            vec3 specular = d * g * f / max(4.0 * nv * max(nl, 0.001), 0.001);
            vec3 diffuse = (1.0 - f) * (1.0 - metallic) * vAlbedo / PI;
            vec3 color = (diffuse + specular) * nl * 2.2 + vAlbedo * 0.16;
            color = color / (color + vec3(1.0));
            color = pow(color, vec3(1.0 / 2.2));
            gl_FragColor = vec4(color, vAlpha);
        }
        """;

    private const string DeferredVertexShader = """
        #version 300 es
        uniform mat4 uMvp;
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aColor;
        layout(location = 3) in float aMetallic;
        layout(location = 4) in float aRoughness;
        layout(location = 5) in float aAlpha;
        out vec3 vNormal;
        out vec3 vAlbedo;
        out float vMetallic;
        out float vRoughness;
        void main() {
            vNormal = aNormal;
            vAlbedo = aColor;
            vMetallic = aMetallic;
            vRoughness = aRoughness;
            gl_Position = uMvp * vec4(aPosition, 1.0);
        }
        """;

    private const string DeferredGeometryFragmentShader = """
        #version 300 es
        precision highp float;
        in vec3 vNormal;
        in vec3 vAlbedo;
        in float vMetallic;
        in float vRoughness;
        layout(location = 0) out vec4 gAlbedoMetal;
        layout(location = 1) out vec4 gNormalRough;
        void main() {
            gAlbedoMetal = vec4(vAlbedo, clamp(vMetallic, 0.0, 1.0));
            gNormalRough = vec4(normalize(vNormal) * 0.5 + 0.5, clamp(vRoughness, 0.04, 1.0));
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
        uniform int uHybrid;
        in vec2 vUv;
        out vec4 outColor;
        void main() {
            vec4 albedoMetal = texture(uAlbedoMetal, vUv);
            vec4 normalRough = texture(uNormalRough, vUv);
            if (dot(normalRough.rgb, normalRough.rgb) < 0.001) {
                outColor = vec4(mix(vec3(0.12, 0.25, 0.42), vec3(0.36, 0.62, 0.90), vUv.y), 1.0);
                return;
            }
            vec3 n = normalize(normalRough.rgb * 2.0 - 1.0);
            vec3 albedo = albedoMetal.rgb;
            float metallic = albedoMetal.a;
            float roughness = normalRough.a;
            vec3 lightDirection = normalize(vec3(0.45, 0.82, 0.32));
            float diffuseLight = max(dot(n, lightDirection), 0.0);
            vec3 diffuse = albedo * (0.16 + diffuseLight * 0.84) * (1.0 - metallic * 0.35);
            vec3 reflectedSky = mix(vec3(0.10, 0.16, 0.23), vec3(0.38, 0.63, 0.88), n.y * 0.5 + 0.5);
            float facing = clamp(1.0 - abs(n.z), 0.0, 1.0);
            float fresnel = 0.04 + 0.96 * pow(facing, 5.0);
            float reflectionWeight = mix(fresnel, 1.0, metallic) * (1.0 - roughness * 0.72);
            vec3 color = diffuse;
            if (uHybrid != 0) color = mix(color, reflectedSky * mix(vec3(1.0), albedo, metallic), reflectionWeight);
            else color += reflectedSky * reflectionWeight * 0.18;
            color = color / (color + vec3(1.0));
            color = pow(color, vec3(1.0 / 2.2));
            outColor = vec4(color, 1.0);
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
        uniform sampler2D uMaterials;
        uniform vec2 uResolution;
        uniform vec3 uCamera;
        uniform vec3 uForward;
        uniform vec3 uRight;
        uniform vec3 uUp;
        uniform float uTanHalfFov;
        uniform ivec3 uVoxelOrigin;
        const ivec3 WindowSize = ivec3(64, 256, 64);

        bool insideWindow(ivec3 cell) {
            ivec3 localCell = cell - uVoxelOrigin;
            return all(greaterThanEqual(localCell, ivec3(0))) && all(lessThan(localCell, WindowSize));
        }

        uint voxelAt(ivec3 cell) {
            if (!insideWindow(cell)) return 0u;
            return texelFetch(uVoxels, cell - uVoxelOrigin, 0).r;
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
                    hitPosition = origin + ray * distanceAlongRay;
                    return material;
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
            return mix(vec3(0.10, 0.18, 0.30), vec3(0.36, 0.64, 0.92), clamp(direction.y * 0.5 + 0.5, 0.0, 1.0));
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
                color = material.rgb * (0.10 + diffuse * 0.90);

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
