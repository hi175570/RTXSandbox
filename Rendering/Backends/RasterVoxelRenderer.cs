using System.Numerics;
using System.Diagnostics;
using System.Buffers;
using OpenTK.Graphics.OpenGL4;
using RTXSandbox.Infrastructure;
using RTXSandbox.Gameplay;
using RTXSandbox.ModAPI;
using RTXSandbox.World;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using OpenTkVector3 = OpenTK.Mathematics.Vector3;

namespace RTXSandbox.Rendering.Backends;

public readonly record struct DeferredBufferDiagnostics(
    int ActivePixelCount,
    int InvalidGBufferPixelCount,
    int InvalidHdrPixelCount,
    float MinimumNormalLength,
    float MaximumNormalLength,
    float MinimumRoughness,
    float MaximumRoughness);

public readonly record struct ForwardBufferDiagnostics(
    int ActivePixelCount,
    int InvalidSurfacePixelCount,
    int InvalidHdrPixelCount,
    float MinimumNormalLength,
    float MaximumNormalLength,
    float MinimumRoughness,
    float MaximumRoughness);

public readonly record struct DeferredShadowCoverageDiagnostics(
    int ActivePixelCount,
    int ShadowedPixelCount,
    int DeepShadowPixelCount,
    float MinimumVisibility,
    float MaximumVisibility);

/// <summary>
/// Shared real-time voxel raster backend. PBR Forward and Classic are separate modes,
/// but deliberately share chunk-mesh and atlas infrastructure.
/// </summary>
public sealed class RasterVoxelRenderer : IRenderBackend, IMaterialPreviewSource
{
    private const int MeshResidencyRadiusChunks = 16;
    private const double MeshEditCoalesceMilliseconds = 25.0;
    private const double RepeatedMeshEditCoalesceMilliseconds = 200.0;
    private const int FloatsPerVertex = 20;
    private const float CullingFrustumScale = 1.6f;
    private const int DefaultDirectionalShadowMapSize = 2048;
    // The retained light direction may move by less than one quarter of a shadow
    // texel before the maps are refreshed. This avoids redrawing a 2048² world
    // shadow map every frame without reducing its spatial resolution.
    private const float ShadowDirectionUpdateThresholdSquared = 1e-8f;

    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly bool _classic;
    private readonly bool _deferred;
    private readonly bool _hybrid;
    private readonly ShaderProgram _shader;
    private readonly ShaderProgram _analyticSphereShader;
    private readonly ShaderProgram? _deferredGeometry;
    private readonly ShaderProgram? _deferredLighting;
    private readonly ShaderProgram? _deferredDepth;
    private readonly ShaderProgram? _deferredPost;
    private readonly ShaderProgram? _forwardPost;
    private readonly Dictionary<ChunkCoordinate, MeshBuffer> _chunkMeshes = new();
    private readonly Dictionary<ulong, PhysicsGridMesh> _physicsGridMeshes = new();
    private readonly HashSet<ChunkCoordinate> _dirtyChunks = new();
    private readonly Dictionary<ChunkCoordinate, int> _chunkMeshVersions = new();
    private readonly Dictionary<ChunkCoordinate, long> _chunkMeshLastDirtyTimestamps = new();
    private readonly Dictionary<ChunkCoordinate, int> _chunkMeshDirtyCounts = new();
    private readonly Dictionary<ChunkCoordinate, ChunkMeshBuildJob> _chunkMeshBuildJobs = new();
    private readonly List<MeshBuffer> _visibleChunkMeshes = new();
    private readonly HashSet<ChunkCoordinate> _visibleChunkCoordinates = new();
    private readonly HashSet<ulong> _visiblePhysicsGridIds = new();
    private ChunkCoordinate _meshResidencyCenter;
    private bool _meshResidencyInitialized;
    private int _meshResidencyLoadedChunkCount = -1;
    private readonly int _materialAtlasTexture;
    private readonly int _materialDetailTexture;
    private BlockDefinition[] _blockDefinitions;
    private ProceduralMaterialAtlas _materialAtlas = null!;
    private int _width;
    private int _height;
    private bool _physicsTopologyDirty = true;
    private bool _forcePhysicsMeshRebuild = true;
    private bool _disposed;
    private int _gBuffer;
    private int _gPosition;
    private int _gAlbedo;
    private int _gNormal;
    private int _gEmission;
    private int _gMotion;
    private int _gDepth;
    private int _hdrBuffer;
    private int _hdrColor;
    private int _sceneColorCopy;
    private int _forwardBuffer;
    private int _forwardColor;
    private int _forwardSurface;
    private int _forwardDepth;
    private readonly int[] _forwardHistoryBuffers = new int[2];
    private readonly int[] _forwardHistoryTextures = new int[2];
    private int _forwardHistoryIndex;
    private bool _forwardHistoryValid;
    private readonly GpuPassTimer? _forwardShadowGpuTimer;
    private readonly GpuPassTimer? _forwardMainGpuTimer;
    private readonly GpuPassTimer? _forwardTransparencyGpuTimer;
    private readonly GpuPassTimer? _forwardPostGpuTimer;
    private int _directionalShadowBuffer;
    private int _directionalShadowDepth;
    private int _dynamicDirectionalShadowBuffer;
    private int _dynamicDirectionalShadowDepth;
    private int _directionalShadowMapSize = DefaultDirectionalShadowMapSize;
    private int _fullscreenVertexArray;
    private int _analyticSphereVertexArray;
    private Matrix4 _previousView = Matrix4.Identity;
    private Matrix4 _previousProjection = Matrix4.Identity;
    private bool _hasPreviousMatrices;
    private bool _directionalShadowDirty = true;
    private bool _dynamicDirectionalShadowDirty = true;
    private bool _hasDirectionalShadowMap;
    private bool _hasDynamicDirectionalShadowMap;
    private Vector3 _lastDirectionalShadowDirection;
    private Vector3 _lastDirectionalShadowCenter;
    private float _lastDirectionalShadowDistance = -1f;
    private Matrix4 _cachedLightView = Matrix4.Identity;
    private Matrix4 _cachedLightProjection = Matrix4.Identity;
    private bool _deferredLightsDirty = true;
    private readonly List<DeferredPointLight> _deferredPointLights = new();
    private readonly Dictionary<ChunkCoordinate, List<DeferredPointLight>> _deferredLightsByChunk = new();
    private readonly HashSet<ChunkCoordinate> _dirtyDeferredLightChunks = new();
    private readonly Dictionary<ulong, CachedGridLights> _deferredLightsByGrid = new();

    public RenderBackendKind Kind => _hybrid
        ? RenderBackendKind.HybridPbrRaster
        : _deferred ? RenderBackendKind.DeferredLighting
        : _classic ? RenderBackendKind.Classic : RenderBackendKind.PbrForward;
    public RenderBackendCapabilities Capabilities => _classic
        ? RenderBackendCapabilities.RealTime | RenderBackendCapabilities.LowEnd | RenderBackendCapabilities.MicroVoxels
        : RenderBackendCapabilities.RealTime | RenderBackendCapabilities.Reflections |
          RenderBackendCapabilities.Transmission | RenderBackendCapabilities.MicroVoxels;
    public string Name => _hybrid ? "Hybrid PBR Raster" : _deferred ? "Deferred GBuffer Lighting" : _classic ? "Classic Textured Raster" : "PBR Forward Raster";
    public int AccumulatedSamples => 1;
    public int MaterialAtlasTexture => _materialAtlasTexture;
    public int MaterialTextureResolution => ProceduralMaterialAtlas.TileSize;
    public int DeferredPointLightCount => _deferredPointLights.Count;
    public int DeferredSpotLightCount { get; private set; }
    public int DeferredFeatureMask { get; private set; }
    public int ForwardFeatureMask { get; private set; }
    public int VisibleChunkCount => _visibleChunkMeshes.Count;
    public int CulledChunkCount => _chunkMeshes.Count - _visibleChunkMeshes.Count;
    public int VisiblePhysicsGridCount => _visiblePhysicsGridIds.Count;
    public int ResidentChunkMeshCount => _chunkMeshes.Count;
    public int EvictedChunkMeshCount { get; private set; }
    public int ShadowCasterChunkCount { get; private set; }
    public int DirectionalShadowMapResolution => _directionalShadowMapSize;
    public int DirectionalShadowDrawCount { get; private set; }
    public int DirectionalShadowMapUpdateCount { get; private set; }
    public int StaticShadowMapUpdateCount { get; private set; }
    public int DynamicShadowMapUpdateCount { get; private set; }
    public int PhysicsGridMeshUploadCount { get; private set; }
    public int ChunkMeshBuildCount { get; private set; }
    public int ChunkGpuUploadCount { get; private set; }
    public long ChunkGpuUploadBytes { get; private set; }
    public int PendingChunkMeshJobCount => _chunkMeshBuildJobs.Count;
    public double ChunkMeshBuildTotalMilliseconds { get; private set; }
    public double MaximumChunkMeshBuildMilliseconds { get; private set; }
    public double ChunkGpuUploadTotalMilliseconds { get; private set; }
    public double MaximumChunkGpuUploadMilliseconds { get; private set; }
    public double DeferredShadowUpdateTotalMilliseconds { get; private set; }
    public double MaximumDeferredShadowUpdateMilliseconds { get; private set; }
    public double DeferredGeometryCpuMilliseconds { get; private set; }
    public double DeferredLightingCpuMilliseconds { get; private set; }
    public double DeferredPostCpuMilliseconds { get; private set; }
    public double DeferredTransparencyCpuMilliseconds { get; private set; }
    public double ForwardShadowCpuMilliseconds { get; private set; }
    public double ForwardMainCpuMilliseconds { get; private set; }
    public double ForwardTransparencyCpuMilliseconds { get; private set; }
    public double ForwardPostCpuMilliseconds { get; private set; }
    public double ForwardShadowGpuMilliseconds => _forwardShadowGpuTimer?.LastMilliseconds ?? 0.0;
    public double ForwardMainGpuMilliseconds => _forwardMainGpuTimer?.LastMilliseconds ?? 0.0;
    public double ForwardTransparencyGpuMilliseconds => _forwardTransparencyGpuTimer?.LastMilliseconds ?? 0.0;
    public double ForwardPostGpuMilliseconds => _forwardPostGpuTimer?.LastMilliseconds ?? 0.0;
    public double MaximumForwardShadowGpuMilliseconds => _forwardShadowGpuTimer?.MaximumMilliseconds ?? 0.0;
    public double MaximumForwardMainGpuMilliseconds => _forwardMainGpuTimer?.MaximumMilliseconds ?? 0.0;
    public double MaximumForwardTransparencyGpuMilliseconds => _forwardTransparencyGpuTimer?.MaximumMilliseconds ?? 0.0;
    public double MaximumForwardPostGpuMilliseconds => _forwardPostGpuTimer?.MaximumMilliseconds ?? 0.0;
    public int DeferredVisibleTriangleCount =>
        (_visibleChunkMeshes.Sum(mesh => mesh.VertexCount) +
         _visiblePhysicsGridIds.Sum(id => _physicsGridMeshes.TryGetValue(id, out PhysicsGridMesh? mesh)
             ? mesh.Buffer.VertexCount : 0)) / 3;
    public long DeferredGBufferBytes => (long)_width * _height * (8 + 4 + 8 + 8 + 8 + 4);
    public long DeferredHistoryBufferBytes => 0;
    public long ForwardBufferBytes => (long)_width * _height * (8 + 8 + 4 + 8 + 8);
    // CPU mesh lists are released after their versioned GPU upload; only pending
    // background results retain CPU vertices.
    public long ResidentCpuMeshBytes => _chunkMeshBuildJobs.Values
        .Where(job => job.Build.IsCompletedSuccessfully)
        .Sum(job => (long)job.Build.Result.Vertices.Count * sizeof(float));
    public long ResidentGpuMeshBytes =>
        _chunkMeshes.Values.Sum(mesh => (long)mesh.VertexCount * FloatsPerVertex * sizeof(float)) +
        _physicsGridMeshes.Values.Sum(mesh =>
            (long)mesh.Buffer.VertexCount * FloatsPerVertex * sizeof(float));
    public int PendingVisibleMeshRebuildCount =>
        _dirtyChunks.Count(_visibleChunkCoordinates.Contains);
    public float ViewCullingExpansion => CullingFrustumScale;
    private bool EnhancedForward => !_classic && !_deferred;

    public (float Minimum, float Maximum) ReadDirectionalShadowDepthRange(bool dynamicLayer)
    {
        int texture = dynamicLayer ? _dynamicDirectionalShadowDepth : _directionalShadowDepth;
        if (texture == 0) return (1f, 1f);
        var depths = new float[_directionalShadowMapSize * _directionalShadowMapSize];
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.DepthComponent, PixelType.Float, depths);
        float minimum = 1f;
        float maximum = 0f;
        foreach (float depth in depths)
        {
            minimum = MathF.Min(minimum, depth);
            maximum = MathF.Max(maximum, depth);
        }
        return (minimum, maximum);
    }

    public DeferredBufferDiagnostics ReadDeferredBufferDiagnostics()
    {
        if (!_deferred || _gPosition == 0 || _hdrColor == 0) return default;
        int pixelCount = checked(_width * _height);
        float[] positions = ReadRgbaTexture(_gPosition, pixelCount);
        float[] normals = ReadRgbaTexture(_gNormal, pixelCount);
        float[] albedoRoughness = ReadRgbaTexture(_gAlbedo, pixelCount);
        float[] motionGeometry = ReadRgbaTexture(_gMotion, pixelCount);
        float[] hdr = ReadRgbaTexture(_hdrColor, pixelCount);
        int active = 0;
        int invalidGBuffer = 0;
        int invalidHdr = 0;
        float minimumNormal = float.MaxValue;
        float maximumNormal = 0f;
        float minimumRoughness = 1f;
        float maximumRoughness = 0f;
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int offset = pixel * 4;
            bool hdrFinite = true;
            for (int component = 0; component < 3; component++)
                hdrFinite &= float.IsFinite(hdr[offset + component]);
            if (!hdrFinite) invalidHdr++;
            if (!(positions[offset + 3] > 0.5f)) continue;
            active++;
            bool finite = true;
            for (int component = 0; component < 4; component++)
            {
                finite &= float.IsFinite(positions[offset + component]);
                finite &= float.IsFinite(normals[offset + component]);
                finite &= float.IsFinite(albedoRoughness[offset + component]);
                finite &= float.IsFinite(motionGeometry[offset + component]);
            }
            if (!finite)
            {
                invalidGBuffer++;
                continue;
            }
            var decodedNormal = new Vector3(
                normals[offset] * 2f - 1f,
                normals[offset + 1] * 2f - 1f,
                normals[offset + 2] * 2f - 1f);
            float normalLength = decodedNormal.Length();
            minimumNormal = MathF.Min(minimumNormal, normalLength);
            maximumNormal = MathF.Max(maximumNormal, normalLength);
            minimumRoughness = MathF.Min(minimumRoughness, albedoRoughness[offset + 3]);
            maximumRoughness = MathF.Max(maximumRoughness, albedoRoughness[offset + 3]);
            if (normalLength is < 0.98f or > 1.02f ||
                motionGeometry[offset + 2] is < 0f or > 1f ||
                motionGeometry[offset + 3] is < 0f or > 1f)
                invalidGBuffer++;
        }
        if (active == 0) minimumNormal = minimumRoughness = 0f;
        return new DeferredBufferDiagnostics(active, invalidGBuffer, invalidHdr,
            minimumNormal, maximumNormal, minimumRoughness, maximumRoughness);
    }

    public ForwardBufferDiagnostics ReadForwardBufferDiagnostics()
    {
        if (!EnhancedForward || _forwardColor == 0 || _forwardSurface == 0 || _forwardDepth == 0)
            return default;
        int pixelCount = checked(_width * _height);
        float[] hdr = ReadRgbaTexture(_forwardColor, pixelCount);
        float[] surface = ReadRgbaTexture(_forwardSurface, pixelCount);
        var depth = new float[pixelCount];
        GL.BindTexture(TextureTarget.Texture2D, _forwardDepth);
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.DepthComponent, PixelType.Float, depth);
        int active = 0;
        int invalidSurface = 0;
        int invalidHdr = 0;
        float minimumNormal = float.MaxValue;
        float maximumNormal = 0f;
        float minimumRoughness = float.MaxValue;
        float maximumRoughness = 0f;
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int offset = pixel * 4;
            if (!float.IsFinite(hdr[offset]) || !float.IsFinite(hdr[offset + 1]) ||
                !float.IsFinite(hdr[offset + 2]) || hdr[offset] < 0f || hdr[offset + 1] < 0f || hdr[offset + 2] < 0f)
                invalidHdr++;
            if (depth[pixel] >= 0.99999f) continue;
            active++;
            var normal = new Vector3(
                surface[offset] * 2f - 1f,
                surface[offset + 1] * 2f - 1f,
                surface[offset + 2] * 2f - 1f);
            float normalLength = normal.Length();
            float roughness = surface[offset + 3];
            minimumNormal = MathF.Min(minimumNormal, normalLength);
            maximumNormal = MathF.Max(maximumNormal, normalLength);
            minimumRoughness = MathF.Min(minimumRoughness, roughness);
            maximumRoughness = MathF.Max(maximumRoughness, roughness);
            if (!float.IsFinite(normalLength) || normalLength is < 0.98f or > 1.02f ||
                !float.IsFinite(roughness) || roughness is < 0.044f or > 1.001f)
                invalidSurface++;
        }
        if (active == 0) minimumNormal = minimumRoughness = 0f;
        return new ForwardBufferDiagnostics(active, invalidSurface, invalidHdr,
            minimumNormal, maximumNormal, minimumRoughness, maximumRoughness);
    }

    public DeferredShadowCoverageDiagnostics ReadDeferredShadowCoverageDiagnostics()
    {
        if (!_deferred || _gPosition == 0 || _hdrColor == 0) return default;
        int pixelCount = checked(_width * _height);
        float[] position = ReadRgbaTexture(_gPosition, pixelCount);
        float[] lighting = ReadRgbaTexture(_hdrColor, pixelCount);
        int active = 0;
        int shadowed = 0;
        int deepShadow = 0;
        float minimum = 1f;
        float maximum = 0f;
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int offset = pixel * 4;
            if (position[offset + 3] < 0.5f) continue;
            active++;
            float visibility = Math.Clamp(lighting[offset], 0f, 1f);
            minimum = MathF.Min(minimum, visibility);
            maximum = MathF.Max(maximum, visibility);
            if (visibility < 0.98f) shadowed++;
            if (visibility < 0.5f) deepShadow++;
        }
        if (active == 0) minimum = maximum = 0f;
        return new DeferredShadowCoverageDiagnostics(active, shadowed, deepShadow, minimum, maximum);
    }

    private static float[] ReadRgbaTexture(int texture, int pixelCount)
    {
        var values = new float[checked(pixelCount * 4)];
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, values);
        return values;
    }

    public RasterVoxelRenderer(
        VoxelWorld world,
        BlockRegistry blocks,
        int width,
        int height,
        bool classic,
        bool deferred = false,
        bool hybrid = false)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _classic = classic;
        _hybrid = hybrid;
        _deferred = deferred || hybrid;
        _blockDefinitions = _blocks.CreateLookupSnapshot();
        string shaderRoot = Path.Combine(RuntimePaths.OutputRoot, "Shaders");
        _shader = new ShaderProgram(
            (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.vert.glsl"))),
            (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.frag.glsl"))));
        _analyticSphereShader = new ShaderProgram(
            (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "analytic_sphere.vert.glsl"))),
            (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "analytic_sphere.frag.glsl"))));
        _analyticSphereVertexArray = GL.GenVertexArray();
        if (_deferred)
        {
            _deferredGeometry = new ShaderProgram(
                (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.vert.glsl"))),
                (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "deferred_geometry.frag.glsl"))));
            _deferredLighting = new ShaderProgram(
                (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "deferred_lighting.vert.glsl"))),
                (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "deferred_lighting.frag.glsl"))));
            _deferredDepth = new ShaderProgram(
                (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.vert.glsl"))),
                (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "deferred_depth.frag.glsl"))));
            _deferredPost = new ShaderProgram(
                (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "deferred_lighting.vert.glsl"))),
                (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "deferred_post.frag.glsl"))));
            _fullscreenVertexArray = GL.GenVertexArray();
            CreateDirectionalShadowMap();
        }
        else if (EnhancedForward)
        {
            _deferredDepth = new ShaderProgram(
                (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.vert.glsl"))),
                (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "deferred_depth.frag.glsl"))));
            _forwardPost = new ShaderProgram(
                (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "deferred_lighting.vert.glsl"))),
                (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "forward_post.frag.glsl"))));
            _fullscreenVertexArray = GL.GenVertexArray();
            CreateDirectionalShadowMap();
            _forwardShadowGpuTimer = new GpuPassTimer();
            _forwardMainGpuTimer = new GpuPassTimer();
            _forwardTransparencyGpuTimer = new GpuPassTimer();
            _forwardPostGpuTimer = new GpuPassTimer();
        }

        foreach (ChunkCoordinate chunk in _world.SnapshotLoadedChunks())
        {
            _chunkMeshes.Add(chunk, CreateMeshBuffer());
            MarkChunkDirty(chunk);
            if (_deferred || EnhancedForward) _dirtyDeferredLightChunks.Add(chunk);
        }
        _materialAtlasTexture = GL.GenTexture();
        _materialDetailTexture = GL.GenTexture();
        UploadMaterialAtlas();
        Resize(width, height, 1f);
        _world.DetailedVoxelChanged += OnVoxelChanged;
        _world.FluidLevelChanged += OnFluidLevelChanged;
        _world.WorldReloaded += OnWorldReloaded;
        _world.MicroVoxelCellChanged += OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded += OnMicroVoxelsReloaded;
        _world.PhysicsGrids.Changed += OnPhysicsGridsChanged;
        _world.ChunkGenerated += OnChunkGenerated;
        _blocks.DefinitionsChanged += OnDefinitionsChanged;
    }

    public void Resize(int width, int height, float renderScale)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        if (_deferred) ResizeGBuffer();
        else if (EnhancedForward) ResizeForwardBuffers();
        ResizeSceneColorCopy();
    }

    public void ResetAccumulation()
    {
        _forwardHistoryValid = false;
    }

    public void Render(
        Vector3 cameraPosition,
        Camera camera,
        VoxelHit? selection,
        float timeOfDay,
        RenderQualitySettings quality,
        float exposure)
    {
        UpdateChunkMeshResidency(cameraPosition);
        UpdateVisibleGeometry(cameraPosition, camera, timeOfDay);
        RebuildDirtyMeshes();
        SynchronizePhysicsGridMeshes();

        if (_hybrid)
        {
            // Hybrid PBR Raster is the high-quality raster path: deferred opaque
            // lighting plus forward water/glass. It shares the proven GBuffer code,
            // but its user-facing quality values are intentionally independent from
            // Deferred so tuning one renderer never silently changes the other.
            quality = quality with
            {
                DeferredRendererQuality = Math.Max(1, quality.HybridLightingQuality),
                DeferredGBufferQuality = Math.Max(2, quality.HybridLightingQuality),
                DeferredLightingQuality = quality.HybridLightingQuality,
                DeferredSpecularQuality = quality.HybridSpecularQuality,
                DeferredShadowQuality = quality.HybridShadowQuality,
                DeferredShadowResolution = quality.HybridShadowResolution,
                DeferredShadowDistance = 64f + quality.HybridShadowQuality * 30f,
                DeferredShadowBias = 0.00035f,
                DeferredShadowNormalBias = 0.025f,
                DeferredShadowSlopeBias = 0.00125f,
                DeferredShadowSoftness = 0.8f + quality.HybridShadowQuality * 0.2f,
                DeferredReflectionQuality = quality.HybridReflectionQuality,
                DeferredAmbientOcclusionQuality = quality.HybridAmbientOcclusionQuality,
                DeferredWaterQuality = quality.HybridWaterQuality,
                DeferredTransparencyQuality = quality.HybridTransparencyQuality,
                DeferredFogQuality = quality.HybridFogQuality,
                DeferredPostProcessingQuality = Math.Max(2, quality.HybridLightingQuality),
                DeferredHdr = true,
                DeferredBloom = quality.HybridBloom,
                DeferredToneMapping = 2,
                DeferredDebugView = DeferredDebugView.FinalImage,
            };
        }

        if (_deferred)
        {
            RenderDeferred(cameraPosition, camera, selection, timeOfDay, quality, exposure);
            CommitPhysicsGridTransforms();
            return;
        }
        if (EnhancedForward)
        {
            RenderEnhancedForward(cameraPosition, camera, selection, timeOfDay, quality, exposure);
            CommitPhysicsGridTransforms();
            return;
        }

        float daylight = Math.Clamp(MathF.Sin((timeOfDay - 6f) * MathF.PI / 12f) * 0.72f + 0.28f, 0.04f, 1f);
        GL.ClearColor(
            0.018f + daylight * 0.10f,
            0.028f + daylight * 0.24f,
            0.06f + daylight * 0.48f,
            1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.Viewport(0, 0, _width, _height);

        var eye = new OpenTkVector3(cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        Vector3 forward = camera.Forward;
        var target = eye + new OpenTkVector3(forward.X, forward.Y, forward.Z);
        Matrix4 view = Matrix4.LookAt(eye, target, OpenTkVector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            camera.FieldOfViewDegrees * MathF.PI / 180f,
            _width / (float)Math.Max(_height, 1),
            0.03f,
            220f);

        _shader.Use();
        GL.UniformMatrix4(_shader.Uniform("uView"), false, ref view);
        GL.UniformMatrix4(_shader.Uniform("uProjection"), false, ref projection);
        GL.Uniform3(_shader.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.Uniform1(_shader.Uniform("uTimeOfDay"), timeOfDay);
        GL.Uniform1(_shader.Uniform("uExposure"), exposure);
        GL.Uniform1(_shader.Uniform("uClassic"), _classic ? 1 : 0);
        GL.Uniform1(_shader.Uniform("uSpecularQuality"), _classic ? 0 : quality.PbrSpecularQuality);
        GL.Uniform1(_shader.Uniform("uReflectionQuality"), _classic ? 0 : quality.PbrReflectionQuality);
        GL.Uniform1(_shader.Uniform("uClassicLightingQuality"), quality.ClassicLightingQuality);
        GL.Uniform1(_shader.Uniform("uClassicSunlightStrength"), quality.ClassicSunlightStrength);
        GL.Uniform1(_shader.Uniform("uClassicAmbientStrength"), quality.ClassicAmbientStrength);
        GL.Uniform1(_shader.Uniform("uClassicColorSaturation"), quality.ClassicColorSaturation);
        GL.Uniform1(_shader.Uniform("uClassicWaterWaveStrength"), quality.ClassicWaterWaveStrength);
        GL.Uniform1(_shader.Uniform("uClassicFogDensity"), quality.ClassicFogDensity);
        GL.Uniform1(_shader.Uniform("uTransparentOnly"), -1);
        GL.Uniform1(_shader.Uniform("uDeferredTransparent"), 0);
        GL.Uniform1(_shader.Uniform("uWaterQuality"), 0);
        GL.Uniform1(_shader.Uniform("uTransparencyQuality"), 0);
        GL.Uniform1(_shader.Uniform("uForwardHdr"), 0);
        GL.Uniform1(_shader.Uniform("uForwardPbr"), 0);
        GL.Uniform1(_shader.Uniform("uForwardDebugView"), 0);
        GL.Uniform1(_shader.Uniform("uForwardApproximateGi"), 0);
        GL.Uniform1(_shader.Uniform("uForwardWaterEffects"), 0);
        GL.Uniform1(_shader.Uniform("uForwardShadowQuality"), 0);
        GL.Uniform2(_shader.Uniform("uScreenSize"), (float)_width, (float)_height);
        if (selection.HasValue)
        {
            GL.Uniform1(_shader.Uniform("uHasSelection"), 1);
            GL.Uniform3(_shader.Uniform("uSelectedCell"), selection.Value.X, selection.Value.Y, selection.Value.Z);
            GL.Uniform1(_shader.Uniform("uSelectedGrid"), (int)selection.Value.GridId);
        }
        else
        {
            GL.Uniform1(_shader.Uniform("uHasSelection"), 0);
            GL.Uniform3(_shader.Uniform("uSelectedCell"), -1, -1, -1);
            GL.Uniform1(_shader.Uniform("uSelectedGrid"), -1);
        }
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _materialAtlasTexture);
        GL.Uniform1(_shader.Uniform("uMaterialAtlas"), 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _materialDetailTexture);
        GL.Uniform1(_shader.Uniform("uMaterialDetailAtlas"), 1);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Uniform1(_shader.Uniform("uSceneColor"), 2);
        DrawVisibleGeometry(_shader, includePreviousModel: false);

        // The quad is only a conservative raster proxy. Every covered fragment
        // solves the exact implicit sphere equation and writes the analytic depth.
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, _sceneColorCopy);
        GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, _width, _height);
        DrawAnalyticSpheres(cameraPosition, camera, timeOfDay, exposure, view, projection,
            _sceneColorCopy, hasSceneColor: true);

        // Forward and Classic render directly to the window. Preserve the opaque
        // result before drawing glass so refraction samples the real scene instead
        // of texture zero (which made distant glass appear gray/opaque).
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, _sceneColorCopy);
        GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, _width, _height);
        _shader.Use();
        GL.Uniform1(_shader.Uniform("uDeferredTransparent"), 2);
        GL.Uniform1(_shader.Uniform("uWaterQuality"), _classic ? quality.ClassicWaterQuality : 2);
        GL.Uniform1(_shader.Uniform("uTransparencyQuality"),
            _classic ? quality.ClassicTransparencyQuality : 2);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        GL.Uniform1(_shader.Uniform("uTransparentOnly"), 1);
        DrawVisibleGeometry(_shader, includePreviousModel: false, transparentSortOrigin: cameraPosition);
        GL.DepthMask(true);
        GL.BindVertexArray(0);
        CommitPhysicsGridTransforms();
    }

    private void RenderEnhancedForward(
        Vector3 cameraPosition,
        Camera camera,
        VoxelHit? selection,
        float timeOfDay,
        RenderQualitySettings quality,
        float exposure)
    {
        var eye = new OpenTkVector3(cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        Vector3 forward = camera.Forward;
        var target = eye + new OpenTkVector3(forward.X, forward.Y, forward.Z);
        Matrix4 view = Matrix4.LookAt(eye, target, OpenTkVector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            camera.FieldOfViewDegrees * MathF.PI / 180f,
            _width / (float)Math.Max(_height, 1), 0.03f, 220f);
        Matrix4 previousView = _hasPreviousMatrices ? _previousView : view;
        Matrix4 previousProjection = _hasPreviousMatrices ? _previousProjection : projection;
        Matrix4 viewProjection = view * projection;
        Matrix4 previousViewProjection = previousView * previousProjection;
        Matrix4 inverseViewProjection = Matrix4.Invert(viewProjection);

        if (_deferredLightsDirty) RebuildDeferredLights();
        Vector3 primaryLightDirection = PrimaryLightDirection(timeOfDay);
        int shadowQuality = quality.ForwardShadows ? quality.ForwardShadowQuality : 0;
        EnsureDirectionalShadowMapSize(quality.ForwardShadowResolution);
        long shadowStarted = Stopwatch.GetTimestamp();
        _forwardShadowGpuTimer!.Begin();
        (Matrix4 lightView, Matrix4 lightProjection) = RenderDirectionalShadowMap(
            primaryLightDirection, shadowQuality, quality.ForwardShadowDistance, cameraPosition);
        _forwardShadowGpuTimer.End();
        ForwardShadowCpuMilliseconds = ElapsedMilliseconds(shadowStarted);

        long mainStarted = Stopwatch.GetTimestamp();
        _forwardMainGpuTimer!.Begin();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _forwardBuffer);
        GL.DrawBuffers(2, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });
        GL.Viewport(0, 0, _width, _height);
        float daylight = Math.Clamp(MathF.Sin((timeOfDay - 6f) * MathF.PI / 12f) * 0.72f + 0.28f, 0.02f, 1f);
        GL.ClearColor(0.008f + daylight * 0.06f, 0.014f + daylight * 0.15f, 0.035f + daylight * 0.34f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        _shader.Use();
        GL.UniformMatrix4(_shader.Uniform("uView"), false, ref view);
        GL.UniformMatrix4(_shader.Uniform("uProjection"), false, ref projection);
        GL.Uniform3(_shader.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.Uniform1(_shader.Uniform("uTimeOfDay"), timeOfDay);
        GL.Uniform1(_shader.Uniform("uExposure"), exposure);
        GL.Uniform1(_shader.Uniform("uClassic"), 0);
        GL.Uniform1(_shader.Uniform("uSpecularQuality"), quality.PbrSpecularQuality);
        GL.Uniform1(_shader.Uniform("uReflectionQuality"), quality.PbrReflectionQuality);
        GL.Uniform1(_shader.Uniform("uClassicLightingQuality"), 0);
        GL.Uniform1(_shader.Uniform("uTransparentOnly"), -1);
        GL.Uniform1(_shader.Uniform("uDeferredTransparent"), 0);
        GL.Uniform1(_shader.Uniform("uWaterQuality"), quality.ForwardWaterQuality);
        GL.Uniform1(_shader.Uniform("uTransparencyQuality"), quality.ForwardWaterQuality);
        GL.Uniform2(_shader.Uniform("uScreenSize"), (float)_width, (float)_height);
        GL.Uniform1(_shader.Uniform("uForwardHdr"), 1);
        GL.Uniform1(_shader.Uniform("uForwardPbr"), quality.ForwardPbr ? 1 : 0);
        GL.Uniform1(_shader.Uniform("uForwardDebugView"), (int)quality.ForwardDebugView);
        GL.Uniform1(_shader.Uniform("uForwardApproximateGi"), quality.ForwardApproximateGi ? 1 : 0);
        GL.Uniform1(_shader.Uniform("uForwardWaterEffects"), quality.ForwardWaterEffects ? 1 : 0);
        GL.Uniform1(_shader.Uniform("uForwardShadowQuality"), shadowQuality);
        GL.Uniform1(_shader.Uniform("uHasDirectionalShadowMap"),
            shadowQuality > 0 && DirectionalShadowDrawCount > 0 ? 1 : 0);
        GL.Uniform1(_shader.Uniform("uHasDynamicDirectionalShadowMap"),
            shadowQuality > 0 && _physicsGridMeshes.Values.Any(mesh => mesh.Buffer.VertexCount > 0) ? 1 : 0);
        GL.Uniform1(_shader.Uniform("uShadowBias"), quality.ForwardShadowBias);
        GL.Uniform1(_shader.Uniform("uShadowNormalBias"), quality.ForwardShadowNormalBias);
        GL.Uniform1(_shader.Uniform("uShadowSlopeBias"), quality.ForwardShadowSlopeBias);
        GL.Uniform1(_shader.Uniform("uShadowSoftness"), quality.ForwardShadowSoftness);
        GL.UniformMatrix4(_shader.Uniform("uLightView"), false, ref lightView);
        GL.UniformMatrix4(_shader.Uniform("uLightProjection"), false, ref lightProjection);
        GL.Uniform3(_shader.Uniform("uPrimaryLightDirection"),
            primaryLightDirection.X, primaryLightDirection.Y, primaryLightDirection.Z);
        int pointCount = quality.ForwardPbr ? _deferredPointLights.Count : 0;
        GL.Uniform1(_shader.Uniform("uPointLightCount"), pointCount);
        for (int index = 0; index < pointCount; index++)
        {
            DeferredPointLight light = _deferredPointLights[index];
            GL.Uniform4(_shader.Uniform($"uPointPositionRadius[{index}]"),
                light.Position.X, light.Position.Y, light.Position.Z, light.Radius);
            GL.Uniform4(_shader.Uniform($"uPointColorIntensity[{index}]"),
                light.Color.X, light.Color.Y, light.Color.Z, light.Intensity);
        }
        SetSelectionUniforms(_shader, selection);
        BindGBufferTexture(_shader, "uMaterialAtlas", _materialAtlasTexture, TextureUnit.Texture0, 0);
        BindGBufferTexture(_shader, "uMaterialDetailAtlas", _materialDetailTexture, TextureUnit.Texture1, 1);
        BindGBufferTexture(_shader, "uSceneColor", _sceneColorCopy, TextureUnit.Texture2, 2);
        BindGBufferTexture(_shader, "uDirectionalShadowMap", _directionalShadowDepth, TextureUnit.Texture3, 3);
        BindGBufferTexture(_shader, "uDynamicDirectionalShadowMap",
            _dynamicDirectionalShadowDepth, TextureUnit.Texture4, 4);
        DrawVisibleGeometry(_shader, includePreviousModel: false);
        ForwardMainCpuMilliseconds = ElapsedMilliseconds(mainStarted);

        // The Forward HDR result remains the authoritative lit image. SceneColor is
        // only a read-only snapshot for analytic spheres and sorted water/glass.
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, _sceneColorCopy);
        GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, _width, _height);
        GL.DrawBuffers(2, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });
        DrawAnalyticSpheres(cameraPosition, camera, timeOfDay, exposure, view, projection,
            _sceneColorCopy, hasSceneColor: true);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, _sceneColorCopy);
        GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, _width, _height);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _forwardMainGpuTimer.End();

        long transparencyStarted = Stopwatch.GetTimestamp();
        _forwardTransparencyGpuTimer!.Begin();
        if (quality.ForwardDebugView == ForwardDebugView.FinalImage)
        {
            _shader.Use();
            GL.Uniform1(_shader.Uniform("uTransparentOnly"), 1);
            GL.Uniform1(_shader.Uniform("uDeferredTransparent"), 1);
            GL.Uniform1(_shader.Uniform("uWaterQuality"), quality.ForwardWaterEffects ? quality.ForwardWaterQuality : 0);
            GL.Uniform1(_shader.Uniform("uTransparencyQuality"), quality.ForwardWaterEffects ? quality.ForwardWaterQuality : 0);
            GL.Uniform1(_shader.Uniform("uForwardDebugView"), 0);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            DrawVisibleGeometry(_shader, includePreviousModel: false, transparentSortOrigin: cameraPosition);
            GL.DepthMask(true);
        }
        _forwardTransparencyGpuTimer.End();
        ForwardTransparencyCpuMilliseconds = ElapsedMilliseconds(transparencyStarted);

        long postStarted = Stopwatch.GetTimestamp();
        _forwardPostGpuTimer!.Begin();
        int historyWriteIndex = _forwardHistoryIndex;
        int historyReadIndex = 1 - historyWriteIndex;
        bool useFrameGeneration = quality.UpscalingMode != UpscalingMode.None;
        bool useTemporalReprojection = quality.UpscalingMode == UpscalingMode.Dlss ||
                                       (quality.ForwardTemporalAntiAliasing && quality.UpscalingMode != UpscalingMode.Fsr);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _forwardHistoryBuffers[historyWriteIndex]);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Viewport(0, 0, _width, _height);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        ShaderProgram post = _forwardPost!;
        post.Use();
        BindGBufferTexture(post, "uHdrColor", _forwardColor, TextureUnit.Texture0, 0);
        BindGBufferTexture(post, "uSurface", _forwardSurface, TextureUnit.Texture1, 1);
        BindGBufferTexture(post, "uDepth", _forwardDepth, TextureUnit.Texture2, 2);
        BindGBufferTexture(post, "uHistory", _forwardHistoryTextures[historyReadIndex], TextureUnit.Texture3, 3);
        GL.Uniform2(post.Uniform("uTexelSize"), 1f / _width, 1f / _height);
        GL.Uniform3(post.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.Uniform1(post.Uniform("uTimeOfDay"), timeOfDay);
        GL.Uniform1(post.Uniform("uExposure"), exposure);
        GL.UniformMatrix4(post.Uniform("uInverseViewProjection"), false, ref inverseViewProjection);
        GL.UniformMatrix4(post.Uniform("uViewProjection"), false, ref viewProjection);
        GL.UniformMatrix4(post.Uniform("uPreviousViewProjection"), false, ref previousViewProjection);
        GL.Uniform1(post.Uniform("uAoQuality"),
            quality.ForwardAmbientOcclusion ? quality.ForwardAmbientOcclusionQuality : 0);
        GL.Uniform1(post.Uniform("uReflectionQuality"),
            quality.ForwardScreenSpaceReflections ? quality.PbrReflectionQuality : 0);
        GL.Uniform1(post.Uniform("uVolumetricQuality"),
            quality.ForwardVolumetric ? quality.ForwardVolumetricQuality : 0);
        GL.Uniform1(post.Uniform("uPostQuality"), quality.ForwardPostProcessingQuality);
        GL.Uniform1(post.Uniform("uBloomEnabled"), quality.ForwardBloom ? 1 : 0);
        GL.Uniform1(post.Uniform("uToneMapping"), quality.ForwardToneMapping);
        GL.Uniform1(post.Uniform("uUpscalingMode"), (int)quality.UpscalingMode);
        GL.Uniform1(post.Uniform("uTaaEnabled"), useTemporalReprojection ? 1 : 0);
        GL.Uniform1(post.Uniform("uHasHistory"),
            _forwardHistoryValid && (quality.ForwardTemporalAntiAliasing || useFrameGeneration) ? 1 : 0);
        GL.Uniform1(post.Uniform("uDebugView"), (int)quality.ForwardDebugView);
        GL.BindVertexArray(_fullscreenVertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _forwardHistoryBuffers[historyWriteIndex]);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        GL.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _forwardPostGpuTimer.End();
        ForwardPostCpuMilliseconds = ElapsedMilliseconds(postStarted);
        _forwardHistoryIndex = historyReadIndex;
        _forwardHistoryValid = (quality.ForwardTemporalAntiAliasing || quality.UpscalingMode != UpscalingMode.None) &&
            quality.ForwardDebugView == ForwardDebugView.FinalImage;
        _previousView = view;
        _previousProjection = projection;
        _hasPreviousMatrices = true;

        ForwardFeatureMask =
            (quality.ForwardPbr ? 1 : 0) |
            (shadowQuality > 0 ? 2 : 0) |
            (pointCount > 0 ? 4 : 0) |
            8 |
            (quality.ForwardAmbientOcclusion && quality.ForwardAmbientOcclusionQuality > 0 ? 16 : 0) |
            (quality.ForwardScreenSpaceReflections && quality.PbrReflectionQuality > 0 ? 32 : 0) |
            (quality.ForwardWaterEffects && quality.ForwardWaterQuality > 0 ? 64 : 0) |
            (quality.ForwardVolumetric && quality.ForwardVolumetricQuality > 0 ? 128 : 0) |
            (quality.ForwardBloom ? 256 : 0) |
            (quality.ForwardTemporalAntiAliasing ? 512 : 0) |
            (quality.ForwardApproximateGi ? 1024 : 0) |
            (quality.ForwardDebugView != ForwardDebugView.FinalImage ? 2048 : 0);
    }

    private void RenderDeferred(
        Vector3 cameraPosition,
        Camera camera,
        VoxelHit? selection,
        float timeOfDay,
        RenderQualitySettings quality,
        float exposure)
    {
        var eye = new OpenTkVector3(cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        Vector3 forward = camera.Forward;
        var target = eye + new OpenTkVector3(forward.X, forward.Y, forward.Z);
        Matrix4 view = Matrix4.LookAt(eye, target, OpenTkVector3.UnitY);
        Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(
            camera.FieldOfViewDegrees * MathF.PI / 180f,
            _width / (float)Math.Max(_height, 1), 0.03f, 220f);

        if (_deferredLightsDirty) RebuildDeferredLights();
        Matrix4 previousView = _hasPreviousMatrices ? _previousView : view;
        Matrix4 previousProjection = _hasPreviousMatrices ? _previousProjection : projection;
        Vector3 primaryLightDirection = PrimaryLightDirection(timeOfDay);
        EnsureDirectionalShadowMapSize(quality.DeferredShadowResolution);
        (Matrix4 lightView, Matrix4 lightProjection) = RenderDirectionalShadowMap(
            primaryLightDirection, quality.DeferredShadowQuality, quality.DeferredShadowDistance, cameraPosition);

        long geometryStarted = Stopwatch.GetTimestamp();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _gBuffer);
        GL.Viewport(0, 0, _width, _height);
        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);

        // Real depth pre-pass: transparent materials are discarded and opaque depth is
        // populated before any G-Buffer color attachment is written.
        GL.ColorMask(false, false, false, false);
        ShaderProgram depth = _deferredDepth!;
        depth.Use();
        GL.UniformMatrix4(depth.Uniform("uView"), false, ref view);
        GL.UniformMatrix4(depth.Uniform("uProjection"), false, ref projection);
        DrawVisibleGeometry(depth, includePreviousModel: false);

        GL.ColorMask(true, true, true, true);
        GL.DepthMask(false);
        GL.DepthFunc(DepthFunction.Equal);
        ShaderProgram geometry = _deferredGeometry!;
        geometry.Use();
        GL.UniformMatrix4(geometry.Uniform("uView"), false, ref view);
        GL.UniformMatrix4(geometry.Uniform("uProjection"), false, ref projection);
        GL.UniformMatrix4(geometry.Uniform("uPreviousView"), false, ref previousView);
        GL.UniformMatrix4(geometry.Uniform("uPreviousProjection"), false, ref previousProjection);
        GL.Uniform1(geometry.Uniform("uGBufferQuality"), quality.DeferredGBufferQuality);
        SetSelectionUniforms(geometry, selection);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _materialAtlasTexture);
        GL.Uniform1(geometry.Uniform("uMaterialAtlas"), 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _materialDetailTexture);
        GL.Uniform1(geometry.Uniform("uMaterialDetailAtlas"), 1);
        DrawVisibleGeometry(geometry, includePreviousModel: true);
        DeferredGeometryCpuMilliseconds = ElapsedMilliseconds(geometryStarted);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Lequal);

        long lightingStarted = Stopwatch.GetTimestamp();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrBuffer);
        GL.Viewport(0, 0, _width, _height);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        ShaderProgram lighting = _deferredLighting!;
        lighting.Use();
        BindGBufferTexture(lighting, "uPosition", _gPosition, TextureUnit.Texture0, 0);
        BindGBufferTexture(lighting, "uAlbedo", _gAlbedo, TextureUnit.Texture1, 1);
        BindGBufferTexture(lighting, "uNormal", _gNormal, TextureUnit.Texture2, 2);
        BindGBufferTexture(lighting, "uEmission", _gEmission, TextureUnit.Texture3, 3);
        BindGBufferTexture(lighting, "uMotion", _gMotion, TextureUnit.Texture4, 4);
        BindGBufferTexture(lighting, "uDirectionalShadowMap", _directionalShadowDepth, TextureUnit.Texture5, 5);
        BindGBufferTexture(lighting, "uDynamicDirectionalShadowMap",
            _dynamicDirectionalShadowDepth, TextureUnit.Texture6, 6);
        GL.Uniform3(lighting.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.UniformMatrix4(lighting.Uniform("uView"), false, ref view);
        GL.UniformMatrix4(lighting.Uniform("uProjection"), false, ref projection);
        GL.UniformMatrix4(lighting.Uniform("uLightView"), false, ref lightView);
        GL.UniformMatrix4(lighting.Uniform("uLightProjection"), false, ref lightProjection);
        GL.Uniform3(lighting.Uniform("uPrimaryLightDirection"),
            primaryLightDirection.X, primaryLightDirection.Y, primaryLightDirection.Z);
        GL.Uniform1(lighting.Uniform("uHasDirectionalShadowMap"),
            quality.DeferredShadowQuality > 0 && DirectionalShadowDrawCount > 0 ? 1 : 0);
        GL.Uniform1(lighting.Uniform("uHasDynamicDirectionalShadowMap"),
            quality.DeferredShadowQuality > 0 &&
            _physicsGridMeshes.Values.Any(mesh => mesh.Buffer.VertexCount > 0) ? 1 : 0);
        GL.Uniform1(lighting.Uniform("uTimeOfDay"), timeOfDay);
        GL.Uniform1(lighting.Uniform("uSpecularQuality"), quality.DeferredSpecularQuality);
        GL.Uniform1(lighting.Uniform("uReflectionQuality"), quality.DeferredReflectionQuality);
        GL.Uniform1(lighting.Uniform("uSelectionInReflections"),
            quality.SelectionOutlineReflections ? 1 : 0);
        GL.Uniform1(lighting.Uniform("uRendererQuality"), quality.DeferredRendererQuality);
        GL.Uniform1(lighting.Uniform("uLightingQuality"), quality.DeferredLightingQuality);
        GL.Uniform1(lighting.Uniform("uShadowQuality"), quality.DeferredShadowQuality);
        GL.Uniform1(lighting.Uniform("uShadowBias"), quality.DeferredShadowBias);
        GL.Uniform1(lighting.Uniform("uShadowNormalBias"), quality.DeferredShadowNormalBias);
        GL.Uniform1(lighting.Uniform("uShadowSlopeBias"), quality.DeferredShadowSlopeBias);
        GL.Uniform1(lighting.Uniform("uShadowSoftness"), quality.DeferredShadowSoftness);
        GL.Uniform1(lighting.Uniform("uAoQuality"), quality.DeferredAmbientOcclusionQuality);
        GL.Uniform1(lighting.Uniform("uDebugView"), (int)quality.DeferredDebugView);
        int pointCount = quality.DeferredLightingQuality > 0 ? _deferredPointLights.Count : 0;
        GL.Uniform1(lighting.Uniform("uPointLightCount"), pointCount);
        for (int index = 0; index < pointCount; index++)
        {
            DeferredPointLight light = _deferredPointLights[index];
            GL.Uniform4(lighting.Uniform($"uPointPositionRadius[{index}]"),
                light.Position.X, light.Position.Y, light.Position.Z, light.Radius);
            GL.Uniform4(lighting.Uniform($"uPointColorIntensity[{index}]"),
                light.Color.X, light.Color.Y, light.Color.Z, light.Intensity);
        }
        DeferredSpotLightCount = quality.DeferredLightingQuality >= 2 ? 1 : 0;
        GL.Uniform1(lighting.Uniform("uSpotLightCount"), DeferredSpotLightCount);
        GL.Uniform4(lighting.Uniform("uSpotPositionRadius[0]"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z, 18f);
        GL.Uniform4(lighting.Uniform("uSpotDirectionCone[0]"), forward.X, forward.Y, forward.Z, 0.88f);
        GL.Uniform4(lighting.Uniform("uSpotColorIntensity[0]"), 0.78f, 0.88f, 1f, 0.42f);
        GL.BindVertexArray(_fullscreenVertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        DeferredLightingCpuMilliseconds = ElapsedMilliseconds(lightingStarted);

        // HDR post pass: optional fog, bloom, color grading, sharpening and selected
        // tone mapper. Debug views bypass artistic post processing.
        long postStarted = Stopwatch.GetTimestamp();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        ShaderProgram post = _deferredPost!;
        post.Use();
        BindGBufferTexture(post, "uHdrColor", _hdrColor, TextureUnit.Texture0, 0);
        BindGBufferTexture(post, "uPosition", _gPosition, TextureUnit.Texture1, 1);
        GL.Uniform2(post.Uniform("uTexelSize"), 1f / _width, 1f / _height);
        GL.Uniform3(post.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.Uniform1(post.Uniform("uExposure"), exposure);
        GL.Uniform1(post.Uniform("uTimeOfDay"), timeOfDay);
        GL.Uniform1(post.Uniform("uHdrEnabled"), quality.DeferredHdr ? 1 : 0);
        GL.Uniform1(post.Uniform("uBloomEnabled"), quality.DeferredBloom ? 1 : 0);
        GL.Uniform1(post.Uniform("uPostQuality"), quality.DeferredPostProcessingQuality);
        GL.Uniform1(post.Uniform("uFogQuality"), quality.DeferredFogQuality);
        GL.Uniform1(post.Uniform("uToneMapping"), quality.DeferredToneMapping);
        GL.Uniform1(post.Uniform("uDebugView"), (int)quality.DeferredDebugView);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        DeferredPostCpuMilliseconds = ElapsedMilliseconds(postStarted);

        if (quality.DeferredDebugView == DeferredDebugView.FinalImage)
        {
            long transparencyStarted = Stopwatch.GetTimestamp();
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _gBuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            GL.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
                ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture2D, _sceneColorCopy);
            GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, _width, _height);
            DrawAnalyticSpheres(cameraPosition, camera, timeOfDay, exposure, view, projection,
                _sceneColorCopy, hasSceneColor: true);

            // Transparent water/glass is intentionally a forward pass over the deferred
            // opaque lighting result; this is the standard hybrid solution for GBuffer renderers.
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            _shader.Use();
            GL.UniformMatrix4(_shader.Uniform("uView"), false, ref view);
            GL.UniformMatrix4(_shader.Uniform("uProjection"), false, ref projection);
            GL.Uniform3(_shader.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
            GL.Uniform1(_shader.Uniform("uTimeOfDay"), timeOfDay);
            GL.Uniform1(_shader.Uniform("uExposure"), exposure);
            GL.Uniform1(_shader.Uniform("uClassic"), 0);
            GL.Uniform1(_shader.Uniform("uSpecularQuality"), quality.DeferredSpecularQuality);
            GL.Uniform1(_shader.Uniform("uReflectionQuality"), quality.DeferredReflectionQuality);
            GL.Uniform1(_shader.Uniform("uClassicLightingQuality"), quality.ClassicLightingQuality);
            GL.Uniform1(_shader.Uniform("uTransparentOnly"), 1);
            GL.Uniform1(_shader.Uniform("uDeferredTransparent"), 1);
            GL.Uniform1(_shader.Uniform("uWaterQuality"), quality.DeferredWaterQuality);
            GL.Uniform1(_shader.Uniform("uTransparencyQuality"), quality.DeferredTransparencyQuality);
            GL.Uniform2(_shader.Uniform("uScreenSize"), (float)_width, (float)_height);
            SetSelectionUniforms(_shader, selection);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _materialAtlasTexture);
            GL.Uniform1(_shader.Uniform("uMaterialAtlas"), 0);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, _materialDetailTexture);
            GL.Uniform1(_shader.Uniform("uMaterialDetailAtlas"), 1);
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture2D, _hdrColor);
            GL.Uniform1(_shader.Uniform("uSceneColor"), 2);
            DrawVisibleGeometry(_shader, includePreviousModel: false, transparentSortOrigin: cameraPosition);
            DeferredTransparencyCpuMilliseconds = ElapsedMilliseconds(transparencyStarted);
            GL.DepthMask(true);
            GL.BindVertexArray(0);
        }
        else
        {
            DeferredTransparencyCpuMilliseconds = 0;
        }

        _previousView = view;
        _previousProjection = projection;
        _hasPreviousMatrices = true;
        DeferredFeatureMask = 1 | 2 | 4 |
            (quality.DeferredShadowQuality > 0 ? 8 : 0) |
            (quality.DeferredAmbientOcclusionQuality > 0 ? 16 : 0) |
            (quality.DeferredReflectionQuality > 0 ? 32 : 0) |
            (quality.DeferredHdr ? 64 : 0) |
            (quality.DeferredBloom ? 128 : 0) |
            (quality.DeferredWaterQuality > 0 && quality.DeferredTransparencyQuality > 0 ? 256 : 0) |
            (quality.DeferredFogQuality > 0 ? 512 : 0) |
            (quality.DeferredDebugView != DeferredDebugView.FinalImage ? 1024 : 0);
    }

    private void DrawAnalyticSpheres(
        Vector3 cameraPosition,
        Camera camera,
        float timeOfDay,
        float exposure,
        Matrix4 view,
        Matrix4 projection,
        int sceneColor,
        bool hasSceneColor)
    {
        if (_world.AnalyticSpheres.Count == 0) return;
        Vector3 forward = Vector3.Normalize(camera.Forward);
        Vector3 right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 0.000001f) right = Vector3.UnitX;
        else right = Vector3.Normalize(right);
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        _analyticSphereShader.Use();
        GL.UniformMatrix4(_analyticSphereShader.Uniform("uView"), false, ref view);
        GL.UniformMatrix4(_analyticSphereShader.Uniform("uProjection"), false, ref projection);
        GL.Uniform3(_analyticSphereShader.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.Uniform3(_analyticSphereShader.Uniform("uCameraRight"), right.X, right.Y, right.Z);
        GL.Uniform3(_analyticSphereShader.Uniform("uCameraUp"), up.X, up.Y, up.Z);
        GL.Uniform1(_analyticSphereShader.Uniform("uTimeOfDay"), timeOfDay);
        GL.Uniform1(_analyticSphereShader.Uniform("uExposure"), exposure);
        GL.Uniform1(_analyticSphereShader.Uniform("uClassic"), _classic ? 1 : 0);
        GL.Uniform1(_analyticSphereShader.Uniform("uForwardHdr"), EnhancedForward ? 1 : 0);
        GL.Uniform1(_analyticSphereShader.Uniform("uHasSceneColor"), hasSceneColor ? 1 : 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, sceneColor);
        GL.Uniform1(_analyticSphereShader.Uniform("uSceneColor"), 0);
        GL.BindVertexArray(_analyticSphereVertexArray);
        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            if (Vector3.DistanceSquared(sphere.Center, cameraPosition) > MathF.Pow(220f + sphere.Radius, 2f))
                continue;
            BlockDefinition material = _blocks.Get(sphere.MaterialId);
            GL.Uniform3(_analyticSphereShader.Uniform("uSphereCenter"), sphere.Center.X, sphere.Center.Y, sphere.Center.Z);
            GL.Uniform1(_analyticSphereShader.Uniform("uSphereRadius"), sphere.Radius);
            GL.Uniform3(_analyticSphereShader.Uniform("uAlbedo"), material.Albedo.X, material.Albedo.Y, material.Albedo.Z);
            GL.Uniform1(_analyticSphereShader.Uniform("uRoughness"), material.Roughness);
            GL.Uniform1(_analyticSphereShader.Uniform("uMetallic"), material.Metallic);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }
        GL.BindVertexArray(0);
    }

    private static void BindGBufferTexture(ShaderProgram shader, string uniform, int texture, TextureUnit unit, int index)
    {
        GL.ActiveTexture(unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.Uniform1(shader.Uniform(uniform), index);
    }

    private void CreateDirectionalShadowMap()
    {
        CreateDepthFramebuffer(out _directionalShadowBuffer, out _directionalShadowDepth);
        CreateDepthFramebuffer(out _dynamicDirectionalShadowBuffer, out _dynamicDirectionalShadowDepth);
    }

    private void EnsureDirectionalShadowMapSize(int requestedSize)
    {
        int size = requestedSize switch { <= 1536 => 1024, <= 3072 => 2048, _ => 4096 };
        if (size == _directionalShadowMapSize) return;
        if (_directionalShadowDepth != 0) GL.DeleteTexture(_directionalShadowDepth);
        if (_dynamicDirectionalShadowDepth != 0) GL.DeleteTexture(_dynamicDirectionalShadowDepth);
        if (_directionalShadowBuffer != 0) GL.DeleteFramebuffer(_directionalShadowBuffer);
        if (_dynamicDirectionalShadowBuffer != 0) GL.DeleteFramebuffer(_dynamicDirectionalShadowBuffer);
        _directionalShadowMapSize = size;
        _directionalShadowBuffer = _directionalShadowDepth = 0;
        _dynamicDirectionalShadowBuffer = _dynamicDirectionalShadowDepth = 0;
        CreateDirectionalShadowMap();
        _hasDirectionalShadowMap = false;
        _hasDynamicDirectionalShadowMap = false;
        _directionalShadowDirty = true;
        _dynamicDirectionalShadowDirty = true;
    }

    private void CreateDepthFramebuffer(out int framebuffer, out int depthTexture)
    {
        framebuffer = GL.GenFramebuffer();
        depthTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24,
            _directionalShadowMapSize, _directionalShadowMapSize, 0,
            PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, depthTexture, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("Directional shadow framebuffer is incomplete.");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private (Matrix4 View, Matrix4 Projection) RenderDirectionalShadowMap(
        Vector3 lightDirection,
        int shadowQuality,
        float shadowDistance,
        Vector3 cameraPosition)
    {
        lightDirection = Vector3.Normalize(lightDirection);
        shadowDistance = Math.Clamp(shadowDistance, 48f, 160f);
        if (shadowQuality <= 0)
        {
            DirectionalShadowDrawCount = 0;
            return (_cachedLightView, _cachedLightProjection);
        }
        Vector3 requestedCenter = new(cameraPosition.X, 80f, cameraPosition.Z);
        bool lightChanged = !_hasDirectionalShadowMap ||
            Vector3.DistanceSquared(lightDirection, _lastDirectionalShadowDirection) >=
            ShadowDirectionUpdateThresholdSquared ||
            Vector3.DistanceSquared(requestedCenter, _lastDirectionalShadowCenter) >= 64f ||
            MathF.Abs(shadowDistance - _lastDirectionalShadowDistance) >= 0.5f;
        if (lightChanged)
        {
            var direction = new OpenTkVector3(lightDirection.X, lightDirection.Y, lightDirection.Z);
            var center = new OpenTkVector3(requestedCenter.X, requestedCenter.Y, requestedCenter.Z);
            var eye = center + direction * 112f;
            OpenTkVector3 up = MathF.Abs(OpenTkVector3.Dot(direction, OpenTkVector3.UnitY)) > 0.96f
                ? OpenTkVector3.UnitZ
                : OpenTkVector3.UnitY;
            _cachedLightView = Matrix4.LookAt(eye, center, up);
            _cachedLightProjection = Matrix4.CreateOrthographic(shadowDistance, shadowDistance, 1f, 224f);
            _lastDirectionalShadowDirection = lightDirection;
            _lastDirectionalShadowCenter = requestedCenter;
            _lastDirectionalShadowDistance = shadowDistance;
            _directionalShadowDirty = true;
            _dynamicDirectionalShadowDirty = true;
        }

        DirectionalShadowDrawCount = _visibleChunkMeshes.Count(mesh => mesh.VertexCount > 0) +
                                     _visiblePhysicsGridIds.Count(id =>
                                         _physicsGridMeshes.TryGetValue(id, out PhysicsGridMesh? mesh) &&
                                         mesh.Buffer.VertexCount > 0);
        bool staticGeometryReady = !_dirtyChunks.Any(_visibleChunkCoordinates.Contains);
        if ((_directionalShadowDirty || !_hasDirectionalShadowMap) &&
            (staticGeometryReady || !_hasDirectionalShadowMap))
        {
            long shadowStart = Stopwatch.GetTimestamp();
            RenderShadowLayer(_directionalShadowBuffer, drawDynamic: false);
            RecordShadowUpdate(ElapsedMilliseconds(shadowStart));
            _directionalShadowDirty = false;
            _hasDirectionalShadowMap = true;
            StaticShadowMapUpdateCount++;
            DirectionalShadowMapUpdateCount++;
        }
        if (_dynamicDirectionalShadowDirty || !_hasDynamicDirectionalShadowMap)
        {
            long shadowStart = Stopwatch.GetTimestamp();
            RenderShadowLayer(_dynamicDirectionalShadowBuffer, drawDynamic: true);
            RecordShadowUpdate(ElapsedMilliseconds(shadowStart));
            _dynamicDirectionalShadowDirty = false;
            _hasDynamicDirectionalShadowMap = true;
            DynamicShadowMapUpdateCount++;
            DirectionalShadowMapUpdateCount++;
        }
        return (_cachedLightView, _cachedLightProjection);
    }

    private void RenderShadowLayer(int framebuffer, bool drawDynamic)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.Viewport(0, 0, _directionalShadowMapSize, _directionalShadowMapSize);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(2f, 4f);

        ShaderProgram depth = _deferredDepth!;
        depth.Use();
        Matrix4 lightView = _cachedLightView;
        Matrix4 lightProjection = _cachedLightProjection;
        GL.UniformMatrix4(depth.Uniform("uView"), false, ref lightView);
        GL.UniformMatrix4(depth.Uniform("uProjection"), false, ref lightProjection);
        if (drawDynamic) DrawDynamicShadowGeometry(depth);
        else DrawStaticShadowGeometry(depth);
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static void SetSelectionUniforms(ShaderProgram shader, VoxelHit? selection)
    {
        GL.Uniform1(shader.Uniform("uHasSelection"), selection.HasValue ? 1 : 0);
        GL.Uniform3(shader.Uniform("uSelectedCell"), selection?.X ?? -1, selection?.Y ?? -1, selection?.Z ?? -1);
        GL.Uniform1(shader.Uniform("uSelectedGrid"), selection.HasValue ? (int)selection.Value.GridId : -1);
    }

    private void ResizeGBuffer()
    {
        DeleteGBuffer();
        _gBuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _gBuffer);
        _gPosition = CreateGBufferTexture(PixelInternalFormat.Rgba16f, FramebufferAttachment.ColorAttachment0);
        _gAlbedo = CreateGBufferTexture(PixelInternalFormat.Rgba8, FramebufferAttachment.ColorAttachment1);
        _gNormal = CreateGBufferTexture(PixelInternalFormat.Rgba16f, FramebufferAttachment.ColorAttachment2);
        _gEmission = CreateGBufferTexture(PixelInternalFormat.Rgba16f, FramebufferAttachment.ColorAttachment3);
        // RG stores motion; BA stores the octahedral unperturbed geometric normal.
        // Keeping both normals prevents AO and shadow bias from treating normal-map
        // grain as actual occluding geometry.
        _gMotion = CreateGBufferTexture(PixelInternalFormat.Rgba16f, FramebufferAttachment.ColorAttachment4);
        _gDepth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _gDepth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, _width, _height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _gDepth);
        GL.DrawBuffers(5, new[]
        {
            DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1,
            DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3,
            DrawBuffersEnum.ColorAttachment4,
        });
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("Deferred GBuffer is incomplete.");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        _hdrBuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrBuffer);
        _hdrColor = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _hdrColor);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
            _width, _height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _hdrColor, 0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("Deferred HDR framebuffer is incomplete.");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void ResizeForwardBuffers()
    {
        DeleteForwardBuffers();
        _forwardBuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _forwardBuffer);
        _forwardColor = CreateForwardColorTexture(
            PixelInternalFormat.Rgba16f, FramebufferAttachment.ColorAttachment0, linear: true);
        _forwardSurface = CreateForwardColorTexture(
            PixelInternalFormat.Rgba16f, FramebufferAttachment.ColorAttachment1, linear: false);
        _forwardDepth = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _forwardDepth);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24,
            _width, _height, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _forwardDepth, 0);
        GL.DrawBuffers(2, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("Enhanced Forward HDR framebuffer is incomplete.");

        for (int index = 0; index < 2; index++)
        {
            _forwardHistoryBuffers[index] = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _forwardHistoryBuffers[index]);
            _forwardHistoryTextures[index] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _forwardHistoryTextures[index]);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
                _width, _height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _forwardHistoryTextures[index], 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("Enhanced Forward temporal framebuffer is incomplete.");
        }
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _forwardHistoryIndex = 0;
        _forwardHistoryValid = false;
    }

    private int CreateForwardColorTexture(
        PixelInternalFormat format,
        FramebufferAttachment attachment,
        bool linear)
    {
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, format,
            _width, _height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)(linear ? TextureMinFilter.Linear : TextureMinFilter.Nearest));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)(linear ? TextureMagFilter.Linear : TextureMagFilter.Nearest));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }

    private void ResizeSceneColorCopy()
    {
        if (_sceneColorCopy != 0) GL.DeleteTexture(_sceneColorCopy);
        _sceneColorCopy = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _sceneColorCopy);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
            _width, _height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    private int CreateGBufferTexture(PixelInternalFormat format, FramebufferAttachment attachment)
    {
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, format, _width, _height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }

    private void DeleteGBuffer()
    {
        if (_gPosition != 0) GL.DeleteTexture(_gPosition);
        if (_gAlbedo != 0) GL.DeleteTexture(_gAlbedo);
        if (_gNormal != 0) GL.DeleteTexture(_gNormal);
        if (_gEmission != 0) GL.DeleteTexture(_gEmission);
        if (_gMotion != 0) GL.DeleteTexture(_gMotion);
        if (_hdrColor != 0) GL.DeleteTexture(_hdrColor);
        if (_gDepth != 0) GL.DeleteRenderbuffer(_gDepth);
        if (_gBuffer != 0) GL.DeleteFramebuffer(_gBuffer);
        if (_hdrBuffer != 0) GL.DeleteFramebuffer(_hdrBuffer);
        _gPosition = _gAlbedo = _gNormal = _gEmission = _gMotion = _gDepth = _gBuffer = _hdrColor = _hdrBuffer = 0;
    }

    private void DeleteForwardBuffers()
    {
        if (_forwardColor != 0) GL.DeleteTexture(_forwardColor);
        if (_forwardSurface != 0) GL.DeleteTexture(_forwardSurface);
        if (_forwardDepth != 0) GL.DeleteTexture(_forwardDepth);
        if (_forwardBuffer != 0) GL.DeleteFramebuffer(_forwardBuffer);
        for (int index = 0; index < 2; index++)
        {
            if (_forwardHistoryTextures[index] != 0) GL.DeleteTexture(_forwardHistoryTextures[index]);
            if (_forwardHistoryBuffers[index] != 0) GL.DeleteFramebuffer(_forwardHistoryBuffers[index]);
            _forwardHistoryTextures[index] = 0;
            _forwardHistoryBuffers[index] = 0;
        }
        _forwardColor = _forwardSurface = _forwardDepth = _forwardBuffer = 0;
        _forwardHistoryValid = false;
    }

    public MaterialTileUv GetMaterialTileUv(ushort materialId, MaterialFace face) =>
        _materialAtlas.GetTileUv(materialId, face);

    private void UpdateVisibleGeometry(Vector3 cameraPosition, Camera camera, float timeOfDay)
    {
        HashSet<ChunkCoordinate> previousVisible = _visibleChunkCoordinates.ToHashSet();
        _visibleChunkMeshes.Clear();
        _visibleChunkCoordinates.Clear();
        ShadowCasterChunkCount = 0;
        float aspect = _width / (float)Math.Max(_height, 1);
        Vector3 lightDirection = PrimaryLightDirection(timeOfDay);
        foreach ((ChunkCoordinate coordinate, MeshBuffer mesh) in _chunkMeshes)
        {
            Vector3 center = new(
                coordinate.MinimumX + ChunkCoordinate.Size * 0.5f,
                mesh.BoundsCenterY,
                coordinate.MinimumZ + ChunkCoordinate.Size * 0.5f);
            float radius = MathF.Sqrt(
                ChunkCoordinate.Size * ChunkCoordinate.Size * 0.5f +
                mesh.BoundsHalfHeight * mesh.BoundsHalfHeight);
            bool directlyVisible = IsSphereInView(center, radius, cameraPosition, camera.Forward,
                camera.FieldOfViewDegrees, aspect, 12f);
            bool shadowCaster = (_deferred || EnhancedForward) && !directlyVisible && IsShadowCasterNeeded(
                center, radius, lightDirection, cameraPosition, camera.Forward,
                camera.FieldOfViewDegrees, aspect);
            if (!directlyVisible && !shadowCaster)
                continue;
            if (shadowCaster) ShadowCasterChunkCount++;
            _visibleChunkCoordinates.Add(coordinate);
            _visibleChunkMeshes.Add(mesh);
        }
        if (!previousVisible.SetEquals(_visibleChunkCoordinates)) _directionalShadowDirty = true;

        var visibleGridIds = new HashSet<ulong>();
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            float gridRadius = MathF.Max(1f, grid.BoundingRadius);
            if (IsSphereInView(grid.Position, gridRadius, cameraPosition, camera.Forward,
                    camera.FieldOfViewDegrees, aspect, 12f) ||
                ((_deferred || EnhancedForward) && IsShadowCasterNeeded(grid.Position, gridRadius, lightDirection, cameraPosition,
                    camera.Forward, camera.FieldOfViewDegrees, aspect)))
                visibleGridIds.Add(grid.Id);
        }
        if (!_visiblePhysicsGridIds.SetEquals(visibleGridIds))
        {
            _visiblePhysicsGridIds.Clear();
            _visiblePhysicsGridIds.UnionWith(visibleGridIds);
        }
    }

    private void UpdateChunkMeshResidency(Vector3 cameraPosition)
    {
        ChunkCoordinate center = ChunkCoordinate.FromVoxel(
            (int)MathF.Floor(cameraPosition.X),
            (int)MathF.Floor(cameraPosition.Z));
        int loadedCount = _world.LoadedChunkCount;
        if (_meshResidencyInitialized && center == _meshResidencyCenter &&
            loadedCount == _meshResidencyLoadedChunkCount) return;

        _meshResidencyInitialized = true;
        _meshResidencyCenter = center;
        _meshResidencyLoadedChunkCount = loadedCount;
        HashSet<ChunkCoordinate> desired = _world.SnapshotLoadedChunks()
            .Where(IsInsideMeshResidency)
            .ToHashSet();

        foreach (ChunkCoordinate stale in _chunkMeshes.Keys.Where(chunk => !desired.Contains(chunk)).ToArray())
        {
            _chunkMeshes[stale].Dispose();
            _chunkMeshes.Remove(stale);
            _dirtyChunks.Remove(stale);
            _chunkMeshVersions.Remove(stale);
            _chunkMeshLastDirtyTimestamps.Remove(stale);
            _chunkMeshDirtyCounts.Remove(stale);
            _chunkMeshBuildJobs.Remove(stale);
            _deferredLightsByChunk.Remove(stale);
            _dirtyDeferredLightChunks.Remove(stale);
            EvictedChunkMeshCount++;
        }
        foreach (ChunkCoordinate chunk in desired)
        {
            if (_chunkMeshes.ContainsKey(chunk)) continue;
            _chunkMeshes.Add(chunk, CreateMeshBuffer());
            MarkChunkDirty(chunk);
            if (_deferred || EnhancedForward) _dirtyDeferredLightChunks.Add(chunk);
        }
        if (_deferred || EnhancedForward) _deferredLightsDirty = true;
    }

    private bool IsInsideMeshResidency(ChunkCoordinate chunk) =>
        Math.Max(
            Math.Abs(chunk.X - _meshResidencyCenter.X),
            Math.Abs(chunk.Z - _meshResidencyCenter.Z)) <= MeshResidencyRadiusChunks;

    private bool IsMeshEditQuiet(ChunkCoordinate chunk)
    {
        if (!_chunkMeshLastDirtyTimestamps.TryGetValue(chunk, out long changedAt)) return true;
        double quietMilliseconds = _chunkMeshDirtyCounts.TryGetValue(chunk, out int dirtyCount) && dirtyCount > 1
            ? RepeatedMeshEditCoalesceMilliseconds
            : MeshEditCoalesceMilliseconds;
        return (Stopwatch.GetTimestamp() - changedAt) * 1000.0 / Stopwatch.Frequency >=
               quietMilliseconds;
    }

    private static bool IsSphereInView(
        Vector3 center,
        float radius,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        float verticalFieldOfViewDegrees,
        float aspect,
        float guardBandDegrees = 0f)
    {
        Vector3 offset = center - cameraPosition;
        float distance = offset.Length();
        if (distance <= radius + 0.01f) return true;
        Vector3 forward = Vector3.Normalize(cameraForward);
        if (Vector3.Dot(offset, forward) < -radius) return false;

        float verticalHalf = MathF.Min(verticalFieldOfViewDegrees * MathF.PI / 360f * CullingFrustumScale,
            MathF.PI * 0.495f);
        float diagonalHalf = MathF.Atan(MathF.Tan(verticalHalf) * MathF.Sqrt(1f + aspect * aspect));
        float sphereAllowance = MathF.Asin(Math.Clamp(radius / distance, 0f, 1f));
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(offset / distance, forward), -1f, 1f));
        return angle <= diagonalHalf + sphereAllowance + guardBandDegrees * MathF.PI / 180f;
    }

    private static bool IsShadowCasterNeeded(
        Vector3 casterCenter,
        float radius,
        Vector3 lightDirection,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        float fieldOfViewDegrees,
        float aspect)
    {
        // A caster outside the camera frustum can still shadow a visible receiver.
        // Project its bounds away from the sun/moon and retain it when that projected
        // shadow volume crosses the guarded camera frustum.
        ReadOnlySpan<float> distances = stackalloc float[] { 12f, 24f, 40f, 64f };
        foreach (float distance in distances)
        {
            Vector3 projectedReceiver = casterCenter - lightDirection * distance;
            if (IsSphereInView(projectedReceiver, radius + distance * 0.08f,
                    cameraPosition, cameraForward, fieldOfViewDegrees, aspect, 8f))
                return true;
        }
        return false;
    }

    private static Vector3 PrimaryLightDirection(float timeOfDay)
    {
        float angle = (timeOfDay - 6f) * (2f * MathF.PI / 24f);
        Vector3 sun = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.28f));
        return sun.Y >= 0f ? sun : -sun;
    }

    private void RebuildDirtyMeshes()
    {
        // CPU meshing never touches OpenGL and runs on one background job. Completed
        // data is version-checked and uploaded under the shared per-frame profile.
        int remainingUploads = FrameWorkBudget.Default.MaximumUploadsPerFrame;
        foreach ((ChunkCoordinate chunk, ChunkMeshBuildJob job) in _chunkMeshBuildJobs
                     .Where(pair => pair.Value.Build.IsCompleted)
                     .OrderBy(pair => _visibleChunkCoordinates.Contains(pair.Key) ? 0 : 1)
                     .ToArray())
        {
            _chunkMeshBuildJobs.Remove(chunk);
            ChunkMeshData data;
            try
            {
                data = job.Build.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Chunk mesh build failed for {chunk}: {error.Message}");
                continue;
            }
            using (data)
            {
                if (!_chunkMeshes.TryGetValue(chunk, out MeshBuffer? mesh) ||
                    !_chunkMeshVersions.TryGetValue(chunk, out int currentVersion) ||
                    currentVersion != job.Version)
                    continue;

                ChunkMeshBuildCount++;
                ChunkMeshBuildTotalMilliseconds += data.BuildMilliseconds;
                MaximumChunkMeshBuildMilliseconds = Math.Max(
                    MaximumChunkMeshBuildMilliseconds, data.BuildMilliseconds);
                long uploadStart = Stopwatch.GetTimestamp();
                ApplyChunkMesh(mesh, data);
                RecordGpuUpload(
                    ElapsedMilliseconds(uploadStart),
                    Math.Max(data.Vertices.Count, FloatsPerVertex) * sizeof(float));
                _dirtyChunks.Remove(chunk);
                _chunkMeshLastDirtyTimestamps.Remove(chunk);
                _chunkMeshDirtyCounts.Remove(chunk);
                _directionalShadowDirty = true;
                if (--remainingUploads <= 0) break;
            }
        }

        if (_chunkMeshBuildJobs.Count != 0) return;
        ChunkCoordinate? nextChunk = _dirtyChunks
            .Where(_chunkMeshes.ContainsKey)
            .Where(IsMeshEditQuiet)
            .Where(chunk => _deferred || _visibleChunkCoordinates.Contains(chunk))
            .OrderBy(chunk => _visibleChunkCoordinates.Contains(chunk) ? 0 : 1)
            .Cast<ChunkCoordinate?>()
            .FirstOrDefault();
        if (!nextChunk.HasValue) return;

        ChunkCoordinate coordinate = nextChunk.Value;
        int version = _chunkMeshVersions[coordinate];
        ChunkMeshingSnapshot snapshot = _world.CreateChunkMeshingSnapshot(coordinate);
        BlockDefinition[] definitions = _blockDefinitions;
        _chunkMeshBuildJobs[coordinate] = new ChunkMeshBuildJob(
            version,
            Task.Run(() => BuildChunkMesh(snapshot, definitions)));
    }

    private static ChunkMeshData BuildChunkMesh(
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        long buildStart = Stopwatch.GetTimestamp();
        var vertices = new PooledFloatBuffer(32_000);
        int minimumY = int.MaxValue;
        int maximumY = int.MinValue;
        ChunkCoordinate chunk = snapshot.Coordinate;
        try
        {
            for (int z = chunk.MinimumZ; z < chunk.MinimumZ + ChunkCoordinate.Size; z++)
            for (int y = snapshot.MinimumY; y < snapshot.MaximumYExclusive; y++)
            for (int x = chunk.MinimumX; x < chunk.MinimumX + ChunkCoordinate.Size; x++)
            {
                ushort material = snapshot.GetBlock(x, y, z);
                if (material == 0) continue;
                BlockDefinition definition = definitions[material];
                // Full opaque cubes and fluid tops are emitted by the greedy pass below.
                // Fluid boundary walls remain per-cell so falling water is visible,
                // while faces between adjacent water cells stay suppressed.
                if (definition.IsFluid)
                    AddFluidBoundarySides(vertices, x, y, z, material, snapshot, definitions);
                else if (!IsGreedyOpaque(definition))
                    AddRegularVoxel(vertices, x, y, z, material, snapshot, definitions);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y + 1);
            }

            AddGreedyChunkFaces(vertices, snapshot, definitions);

            foreach (StoredMicroVoxel micro in snapshot.MicroVoxels)
            {
                int x = chunk.MinimumX + micro.LocalX;
                int y = micro.Y;
                int z = chunk.MinimumZ + micro.LocalZ;
                MicroVoxelSnapshot microSnapshot = micro.Snapshot;
                float inverse = 1f / microSnapshot.Resolution;
                foreach (MicroVoxelBox box in microSnapshot.Boxes)
                {
                    AddBox(
                        vertices,
                        new Vector3(x + box.MinX * inverse, y + box.MinY * inverse, z + box.MinZ * inverse),
                        new Vector3(x + box.MaxX * inverse, y + box.MaxY * inverse, z + box.MaxZ * inverse),
                        box.MaterialId,
                        0b0011_1111,
                        x, y, z, 0,
                        definitions);
                }
                if (micro.Snapshot.Boxes.Count == 0) continue;
                minimumY = Math.Min(minimumY, micro.Y);
                maximumY = Math.Max(maximumY, micro.Y + 1);
            }

            return new ChunkMeshData(
                vertices,
                minimumY,
                maximumY,
                ElapsedMilliseconds(buildStart));
        }
        catch
        {
            vertices.Dispose();
            throw;
        }
    }

    private static void ApplyChunkMesh(MeshBuffer mesh, ChunkMeshData data)
    {
        int minimumY = data.MinimumY;
        int maximumY = data.MaximumYExclusive;
        if (minimumY == int.MaxValue)
        {
            mesh.BoundsCenterY = 0f;
            mesh.BoundsHalfHeight = 1f;
        }
        else
        {
            mesh.BoundsCenterY = (minimumY + maximumY) * 0.5f;
            mesh.BoundsHalfHeight = Math.Max(1f, (maximumY - minimumY) * 0.5f);
        }
        UploadMesh(mesh, data.Vertices, BufferUsageHint.StaticDraw);
    }

    private void SynchronizePhysicsGridMeshes()
    {
        if (_physicsTopologyDirty)
        {
            var active = _world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
            foreach (ulong stale in _physicsGridMeshes.Keys.Where(id => !active.Contains(id)).ToArray())
            {
                _physicsGridMeshes[stale].Dispose();
                _physicsGridMeshes.Remove(stale);
            }
            foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
            {
                int signature = grid.TopologyVersion;
                _physicsGridMeshes.TryGetValue(grid.Id, out PhysicsGridMesh? existing);
                if (!_forcePhysicsMeshRebuild &&
                    existing is not null &&
                    existing.TopologySignature == signature)
                    continue;
                existing?.Dispose();
                _physicsGridMeshes[grid.Id] = BuildPhysicsGridMesh(grid, signature);
            }
            _physicsTopologyDirty = false;
            _forcePhysicsMeshRebuild = false;
        }

        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (_physicsGridMeshes.TryGetValue(grid.Id, out PhysicsGridMesh? mesh))
                mesh.CurrentModel = CreatePhysicsGridModel(grid);
        }
    }

    private PhysicsGridMesh BuildPhysicsGridMesh(PhysicsGrid grid, int signature)
    {
        int estimatedFloats = Math.Clamp(grid.Elements.Count * 6 * FloatsPerVertex, 120, 2_000_000);
        using var vertices = new PooledFloatBuffer(estimatedFloats);
        BlockDefinition[] definitions = _blockDefinitions;
        foreach (PhysicsGridElement element in grid.Elements)
        {
            // Geometry stays in Grid-local coordinates. Movement and rotation are
            // supplied as a model matrix, so physics no longer rebuilds/uploads every
            // vertex on every simulation step.
            byte faceMask = 0;
            for (int face = 0; face < 6; face++)
                if (ShouldRenderGridFace(grid, element, face, definitions)) faceMask |= (byte)(1 << face);
            AddBox(vertices, element.Minimum, element.Maximum, element.MaterialId, faceMask,
                element.CellX, element.CellY, element.CellZ, (int)grid.Id, definitions);
        }
        MeshBuffer buffer = CreateMeshBuffer();
        long uploadStart = Stopwatch.GetTimestamp();
        UploadMesh(buffer, vertices, BufferUsageHint.StaticDraw);
        RecordGpuUpload(
            ElapsedMilliseconds(uploadStart),
            Math.Max(vertices.Count, FloatsPerVertex) * sizeof(float));
        PhysicsGridMeshUploadCount++;
        Matrix4 model = CreatePhysicsGridModel(grid);
        return new PhysicsGridMesh(buffer, signature, model);
    }

    private static bool ShouldRenderGridFace(
        PhysicsGrid grid,
        PhysicsGridElement element,
        int face,
        IReadOnlyList<BlockDefinition> definitions)
    {
        Vector3 expectedMinimum = new(element.CellX, element.CellY, element.CellZ);
        if (Vector3.DistanceSquared(element.Minimum, expectedMinimum) > 0.000001f ||
            Vector3.DistanceSquared(element.Maximum, expectedMinimum + Vector3.One) > 0.000001f)
            return true;
        (int X, int Y, int Z) offset = face switch
        {
            0 => (0, 1, 0),
            1 => (-1, 0, 0),
            2 => (1, 0, 0),
            3 => (0, 0, -1),
            4 => (0, 0, 1),
            _ => (0, -1, 0),
        };
        if (!grid.TryGetFullCellMaterial(
                element.CellX + offset.X,
                element.CellY + offset.Y,
                element.CellZ + offset.Z,
                out ushort adjacentId)) return true;
        if (adjacentId == element.MaterialId) return false;
        BlockDefinition current = definitions[element.MaterialId];
        BlockDefinition adjacent = definitions[adjacentId];
        return current.Transmission > 0f || current.IsTransparent || current.IsFluid ||
               adjacent.Transmission > 0f || adjacent.IsTransparent || adjacent.IsFluid;
    }

    private static Matrix4 CreatePhysicsGridModel(PhysicsGrid grid)
    {
        var center = new OpenTkVector3(
            grid.CenterOfMassLocal.X, grid.CenterOfMassLocal.Y, grid.CenterOfMassLocal.Z);
        var position = new OpenTkVector3(grid.Position.X, grid.Position.Y, grid.Position.Z);
        var orientation = new OpenTK.Mathematics.Quaternion(
            grid.Orientation.X, grid.Orientation.Y, grid.Orientation.Z, grid.Orientation.W);
        return Matrix4.CreateTranslation(-center) *
               Matrix4.CreateFromQuaternion(orientation) *
               Matrix4.CreateTranslation(position);
    }

    private void DrawVisibleGeometry(
        ShaderProgram shader,
        bool includePreviousModel,
        Vector3? transparentSortOrigin = null)
    {
        Matrix4 identity = Matrix4.Identity;
        SetModelUniforms(shader, identity, identity, includePreviousModel);
        IEnumerable<KeyValuePair<ChunkCoordinate, MeshBuffer>> chunkMeshes = _chunkMeshes
            .Where(pair => _visibleChunkCoordinates.Contains(pair.Key));
        if (transparentSortOrigin.HasValue)
        {
            Vector3 origin = transparentSortOrigin.Value;
            chunkMeshes = chunkMeshes.OrderByDescending(pair => Vector3.DistanceSquared(origin, new Vector3(
                pair.Key.MinimumX + ChunkCoordinate.Size * 0.5f,
                pair.Value.BoundsCenterY,
                pair.Key.MinimumZ + ChunkCoordinate.Size * 0.5f)));
        }
        foreach ((_, MeshBuffer mesh) in chunkMeshes) DrawMesh(mesh);

        IEnumerable<ulong> gridIds = _visiblePhysicsGridIds;
        if (transparentSortOrigin.HasValue)
        {
            Vector3 origin = transparentSortOrigin.Value;
            gridIds = gridIds.OrderByDescending(id =>
                _world.PhysicsGrids.Get(id) is PhysicsGrid grid
                    ? Vector3.DistanceSquared(origin, grid.Position)
                    : 0f);
        }
        foreach (ulong gridId in gridIds)
        {
            if (!_physicsGridMeshes.TryGetValue(gridId, out PhysicsGridMesh? gridMesh)) continue;
            Matrix4 previous = gridMesh.HasPreviousModel ? gridMesh.PreviousModel : gridMesh.CurrentModel;
            SetModelUniforms(shader, gridMesh.CurrentModel, previous, includePreviousModel);
            DrawMesh(gridMesh.Buffer);
        }
    }

    private void DrawStaticShadowGeometry(ShaderProgram shader)
    {
        Matrix4 identity = Matrix4.Identity;
        SetModelUniforms(shader, identity, identity, includePreviousModel: false);
        foreach (MeshBuffer mesh in _visibleChunkMeshes)
        {
            if (mesh.VertexCount == 0) continue;
            DrawMesh(mesh);
        }
    }

    private void DrawDynamicShadowGeometry(ShaderProgram shader)
    {
        foreach (ulong gridId in _visiblePhysicsGridIds)
        {
            if (!_physicsGridMeshes.TryGetValue(gridId, out PhysicsGridMesh? gridMesh)) continue;
            SetModelUniforms(shader, gridMesh.CurrentModel, gridMesh.CurrentModel, includePreviousModel: false);
            DrawMesh(gridMesh.Buffer);
        }
    }

    private static void SetModelUniforms(
        ShaderProgram shader,
        Matrix4 current,
        Matrix4 previous,
        bool includePreviousModel)
    {
        GL.UniformMatrix4(shader.Uniform("uModel"), false, ref current);
        if (includePreviousModel)
            GL.UniformMatrix4(shader.Uniform("uPreviousModel"), false, ref previous);
    }

    private void CommitPhysicsGridTransforms()
    {
        foreach (PhysicsGridMesh gridMesh in _physicsGridMeshes.Values)
        {
            gridMesh.PreviousModel = gridMesh.CurrentModel;
            gridMesh.HasPreviousModel = true;
        }
    }

    private static unsafe void UploadMesh(MeshBuffer mesh, PooledFloatBuffer vertices, BufferUsageHint usage)
    {
        GL.BindBuffer(BufferTarget.ArrayBuffer, mesh.VertexBuffer);
        if (vertices.Count == 0)
        {
            GL.BufferData(BufferTarget.ArrayBuffer, FloatsPerVertex * sizeof(float), IntPtr.Zero, usage);
        }
        else
        {
            fixed (float* data = vertices.AsSpan())
                GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float), (IntPtr)data, usage);
        }
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        mesh.VertexCount = vertices.Count / FloatsPerVertex;
    }

    private static void DrawMesh(MeshBuffer mesh)
    {
        if (mesh.VertexCount == 0) return;
        GL.BindVertexArray(mesh.VertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, mesh.VertexCount);
    }

    public void ResetPerformanceCounters()
    {
        ChunkMeshBuildCount = 0;
        ChunkGpuUploadCount = 0;
        ChunkGpuUploadBytes = 0;
        ChunkMeshBuildTotalMilliseconds = 0;
        MaximumChunkMeshBuildMilliseconds = 0;
        ChunkGpuUploadTotalMilliseconds = 0;
        MaximumChunkGpuUploadMilliseconds = 0;
        DeferredShadowUpdateTotalMilliseconds = 0;
        MaximumDeferredShadowUpdateMilliseconds = 0;
        DeferredGeometryCpuMilliseconds = 0;
        DeferredLightingCpuMilliseconds = 0;
        DeferredPostCpuMilliseconds = 0;
        DeferredTransparencyCpuMilliseconds = 0;
        DirectionalShadowMapUpdateCount = 0;
        StaticShadowMapUpdateCount = 0;
        DynamicShadowMapUpdateCount = 0;
        PhysicsGridMeshUploadCount = 0;
        _forwardShadowGpuTimer?.ResetMaximum();
        _forwardMainGpuTimer?.ResetMaximum();
        _forwardTransparencyGpuTimer?.ResetMaximum();
        _forwardPostGpuTimer?.ResetMaximum();
    }

    private void RecordGpuUpload(double milliseconds, long bytes)
    {
        ChunkGpuUploadCount++;
        ChunkGpuUploadBytes += Math.Max(0, bytes);
        ChunkGpuUploadTotalMilliseconds += milliseconds;
        MaximumChunkGpuUploadMilliseconds = Math.Max(MaximumChunkGpuUploadMilliseconds, milliseconds);
    }

    private void RecordShadowUpdate(double milliseconds)
    {
        DeferredShadowUpdateTotalMilliseconds += milliseconds;
        MaximumDeferredShadowUpdateMilliseconds = Math.Max(MaximumDeferredShadowUpdateMilliseconds, milliseconds);
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static void AddRegularVoxel(
        PooledFloatBuffer vertices,
        int x,
        int y,
        int z,
        ushort material,
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        bool fluid = definitions[material].IsFluid;
        ushort above = snapshot.GetBlock(x, y + 1, z);
        bool fluidAbove = above != 0 && definitions[above].IsFluid;
        float maximumY = fluid && !fluidAbove
            ? y + VoxelWorld.FluidSurfaceHeight
            : y + 1f;
        byte faceMask = 0;
        // A fluid volume is represented by one continuous, double-sided top surface.
        // Per-block side/bottom faces would expose every transparent fluid cell.
        if (!fluidAbove && ShouldRenderFace(x, y + 1, z, material, snapshot, definitions)) faceMask |= 1 << 0;
        if (!fluid)
        {
            if (ShouldRenderFace(x - 1, y, z, material, snapshot, definitions)) faceMask |= 1 << 1;
            if (ShouldRenderFace(x + 1, y, z, material, snapshot, definitions)) faceMask |= 1 << 2;
            if (ShouldRenderFace(x, y, z - 1, material, snapshot, definitions)) faceMask |= 1 << 3;
            if (ShouldRenderFace(x, y, z + 1, material, snapshot, definitions)) faceMask |= 1 << 4;
            if (ShouldRenderFace(x, y - 1, z, material, snapshot, definitions)) faceMask |= 1 << 5;
        }
        AddBox(vertices, new Vector3(x, y, z), new Vector3(x + 1f, maximumY, z + 1f),
            material, faceMask, x, y, z, 0, definitions);
    }

    private static void AddFluidBoundarySides(
        PooledFloatBuffer vertices,
        int x,
        int y,
        int z,
        ushort material,
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        ushort above = snapshot.GetBlock(x, y + 1, z);
        bool fluidAbove = above != 0 && definitions[above].IsFluid;
        byte level = snapshot.GetFluidLevel(x, y, z);
        float maximumY = fluidAbove ? y + 1f : y + VoxelWorld.GetFluidSurfaceHeight(level);

        // Generated oceans (level zero) retain their greedy top plane. Propagated
        // water gets an individual top so its decreasing flow level is visible.
        if (!fluidAbove && level > 0 && ShouldRenderFace(x, y + 1, z, material, snapshot, definitions))
            AddBox(vertices, new Vector3(x, y, z), new Vector3(x + 1f, maximumY, z + 1f),
                material, 1 << 0, x, y, z, 0, definitions);

        ReadOnlySpan<(int X, int Z, int Face)> neighbors = stackalloc (int, int, int)[]
        {
            (x - 1, z, 1), (x + 1, z, 2), (x, z - 1, 3), (x, z + 1, 4),
        };
        foreach ((int neighborX, int neighborZ, int face) in neighbors)
        {
            ushort neighbor = snapshot.GetBlock(neighborX, y, neighborZ);
            float minimumSideY = y;
            if (neighbor != 0 && definitions[neighbor].IsFluid)
            {
                ushort neighborAbove = snapshot.GetBlock(neighborX, y + 1, neighborZ);
                bool neighborHasFluidAbove = neighborAbove != 0 && definitions[neighborAbove].IsFluid;
                float neighborMaximumY = neighborHasFluidAbove
                    ? y + 1f
                    : y + VoxelWorld.GetFluidSurfaceHeight(snapshot.GetFluidLevel(neighborX, y, neighborZ));
                if (neighborMaximumY >= maximumY - 0.0001f) continue;
                minimumSideY = neighborMaximumY;
            }
            else if (!ShouldRenderFace(neighborX, y, neighborZ, material, snapshot, definitions))
            {
                continue;
            }
            AddBox(vertices, new Vector3(x, minimumSideY, z), new Vector3(x + 1f, maximumY, z + 1f),
                material, (byte)(1 << face), x, y, z, 0, definitions);
        }
    }

    private static bool ShouldRenderFluidBoundary(
        int neighborX,
        int neighborY,
        int neighborZ,
        ushort material,
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        ushort neighbor = snapshot.GetBlock(neighborX, neighborY, neighborZ);
        if (neighbor != 0 && definitions[neighbor].IsFluid) return false;
        return ShouldRenderFace(neighborX, neighborY, neighborZ, material, snapshot, definitions);
    }

    private static bool IsGreedyOpaque(BlockDefinition definition) =>
        !definition.IsFluid && !definition.IsTransparent && definition.Transmission <= 0f;

    private static void AddGreedyChunkFaces(
        PooledFloatBuffer vertices,
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        int height = snapshot.MaximumYExclusive - snapshot.MinimumY;
        if (height <= 0) return;
        ChunkCoordinate chunk = snapshot.Coordinate;
        int planarArea = ChunkCoordinate.Size * ChunkCoordinate.Size;
        ushort[] mask = ArrayPool<ushort>.Shared.Rent(Math.Max(planarArea, ChunkCoordinate.Size * height));
        try
        {

        // Horizontal opaque faces and continuous fluid top planes.
        for (int y = snapshot.MinimumY; y < snapshot.MaximumYExclusive; y++)
        {
            Array.Clear(mask, 0, planarArea);
            for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
            for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
            {
                int x = chunk.MinimumX + localX;
                int z = chunk.MinimumZ + localZ;
                ushort material = snapshot.GetBlock(x, y, z);
                if (material != 0 && IsGreedyOpaque(definitions[material]) &&
                    ShouldRenderFace(x, y + 1, z, material, snapshot, definitions))
                    mask[localX + ChunkCoordinate.Size * localZ] = material;
            }
            EmitGreedyMask(vertices, mask, ChunkCoordinate.Size, ChunkCoordinate.Size,
                face: 0, slice: y, chunk, snapshot.MinimumY, definitions, topHeight: 1f);

            Array.Clear(mask, 0, planarArea);
            for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
            for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
            {
                int x = chunk.MinimumX + localX;
                int z = chunk.MinimumZ + localZ;
                ushort material = snapshot.GetBlock(x, y, z);
                if (material != 0 && IsGreedyOpaque(definitions[material]) &&
                    ShouldRenderFace(x, y - 1, z, material, snapshot, definitions))
                    mask[localX + ChunkCoordinate.Size * localZ] = material;
            }
            EmitGreedyMask(vertices, mask, ChunkCoordinate.Size, ChunkCoordinate.Size,
                face: 5, slice: y, chunk, snapshot.MinimumY, definitions, topHeight: 1f);

            Array.Clear(mask, 0, planarArea);
            for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
            for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
            {
                int x = chunk.MinimumX + localX;
                int z = chunk.MinimumZ + localZ;
                ushort material = snapshot.GetBlock(x, y, z);
                if (material == 0 || !definitions[material].IsFluid) continue;
                if (snapshot.GetFluidLevel(x, y, z) > 0) continue;
                ushort above = snapshot.GetBlock(x, y + 1, z);
                bool fluidAbove = above != 0 && definitions[above].IsFluid;
                if (!fluidAbove && ShouldRenderFace(x, y + 1, z, material, snapshot, definitions))
                    mask[localX + ChunkCoordinate.Size * localZ] = material;
            }
            EmitGreedyMask(vertices, mask, ChunkCoordinate.Size, ChunkCoordinate.Size,
                face: 0, slice: y, chunk, snapshot.MinimumY, definitions,
                topHeight: VoxelWorld.FluidSurfaceHeight);
        }

        // X-facing opaque walls. U is Z and V is Y.
        for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
        for (int face = 1; face <= 2; face++)
        {
            int x = chunk.MinimumX + localX;
            Array.Clear(mask, 0, ChunkCoordinate.Size * height);
            for (int localY = 0; localY < height; localY++)
            for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
            {
                int y = snapshot.MinimumY + localY;
                int z = chunk.MinimumZ + localZ;
                ushort material = snapshot.GetBlock(x, y, z);
                int neighborX = face == 1 ? x - 1 : x + 1;
                if (material != 0 && IsGreedyOpaque(definitions[material]) &&
                    ShouldRenderFace(neighborX, y, z, material, snapshot, definitions))
                    mask[localZ + ChunkCoordinate.Size * localY] = material;
            }
            EmitGreedyMask(vertices, mask, ChunkCoordinate.Size, height,
                face, localX, chunk, snapshot.MinimumY, definitions, topHeight: 1f);
        }

        // Z-facing opaque walls. U is X and V is Y.
        for (int localZ = 0; localZ < ChunkCoordinate.Size; localZ++)
        for (int face = 3; face <= 4; face++)
        {
            int z = chunk.MinimumZ + localZ;
            Array.Clear(mask, 0, ChunkCoordinate.Size * height);
            for (int localY = 0; localY < height; localY++)
            for (int localX = 0; localX < ChunkCoordinate.Size; localX++)
            {
                int x = chunk.MinimumX + localX;
                int y = snapshot.MinimumY + localY;
                ushort material = snapshot.GetBlock(x, y, z);
                int neighborZ = face == 3 ? z - 1 : z + 1;
                if (material != 0 && IsGreedyOpaque(definitions[material]) &&
                    ShouldRenderFace(x, y, neighborZ, material, snapshot, definitions))
                    mask[localX + ChunkCoordinate.Size * localY] = material;
            }
            EmitGreedyMask(vertices, mask, ChunkCoordinate.Size, height,
                face, localZ, chunk, snapshot.MinimumY, definitions, topHeight: 1f);
        }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(mask);
        }
    }

    private static void EmitGreedyMask(
        PooledFloatBuffer vertices,
        ushort[] mask,
        int width,
        int height,
        int face,
        int slice,
        ChunkCoordinate chunk,
        int minimumY,
        IReadOnlyList<BlockDefinition> definitions,
        float topHeight)
    {
        for (int v = 0; v < height; v++)
        for (int u = 0; u < width; u++)
        {
            int index = u + width * v;
            ushort material = mask[index];
            if (material == 0) continue;
            int runWidth = 1;
            while (u + runWidth < width && mask[index + runWidth] == material) runWidth++;
            int runHeight = 1;
            bool canGrow = true;
            while (v + runHeight < height && canGrow)
            {
                int row = index + runHeight * width;
                for (int x = 0; x < runWidth; x++)
                    if (mask[row + x] != material) { canGrow = false; break; }
                if (canGrow) runHeight++;
            }
            for (int clearV = 0; clearV < runHeight; clearV++)
                Array.Clear(mask, index + clearV * width, runWidth);

            Vector3 minimum;
            Vector3 maximum;
            if (face is 0 or 5)
            {
                minimum = new Vector3(chunk.MinimumX + u, slice, chunk.MinimumZ + v);
                maximum = new Vector3(
                    chunk.MinimumX + u + runWidth,
                    slice + topHeight,
                    chunk.MinimumZ + v + runHeight);
            }
            else if (face is 1 or 2)
            {
                minimum = new Vector3(
                    chunk.MinimumX + slice,
                    minimumY + v,
                    chunk.MinimumZ + u);
                maximum = new Vector3(
                    chunk.MinimumX + slice + 1,
                    minimumY + v + runHeight,
                    chunk.MinimumZ + u + runWidth);
            }
            else
            {
                minimum = new Vector3(
                    chunk.MinimumX + u,
                    minimumY + v,
                    chunk.MinimumZ + slice);
                maximum = new Vector3(
                    chunk.MinimumX + u + runWidth,
                    minimumY + v + runHeight,
                    chunk.MinimumZ + slice + 1);
            }
            AddBox(vertices, minimum, maximum, material, (byte)(1 << face),
                (int)minimum.X, (int)minimum.Y, (int)minimum.Z, 0, definitions,
                tileUv: true);
        }
    }

    private static bool ShouldRenderFace(
        int x,
        int y,
        int z,
        ushort currentMaterial,
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        if (y < VoxelWorld.MinimumY || y >= VoxelWorld.MaximumY) return true;
        ushort neighbor = snapshot.GetBlock(x, y, z);
        if (neighbor == 0) return true;
        if (neighbor == currentMaterial) return false;
        BlockDefinition current = definitions[currentMaterial];
        BlockDefinition adjacent = definitions[neighbor];
        if (current.IsFluid && adjacent.IsFluid) return false;
        return current.Transmission > 0f || current.IsFluid || adjacent.Transmission > 0f || adjacent.IsFluid;
    }

    private static void AddBox(
        PooledFloatBuffer vertices,
        Vector3 minimum,
        Vector3 maximum,
        ushort materialId,
        byte faceMask,
        int cellX,
        int cellY,
        int cellZ,
        int gridId,
        IReadOnlyList<BlockDefinition> definitions,
        PhysicsGrid? grid = null,
        bool tileUv = false)
    {
        // Face order is top, -X, +X, -Z, +Z, bottom. Atlas face is top/side/bottom.
        BlockDefinition definition = definitions[materialId];
        for (int face = 0; face < 6; face++)
        {
            if ((faceMask & (1 << face)) == 0) continue;
            int atlasFace = face == 0 ? 0 : face == 5 ? 2 : 1;
            Vector3 normal = face switch
            {
                0 => Vector3.UnitY,
                1 => -Vector3.UnitX,
                2 => Vector3.UnitX,
                3 => -Vector3.UnitZ,
                4 => Vector3.UnitZ,
                _ => -Vector3.UnitY,
            };
            for (int vertex = 0; vertex < 6; vertex++)
            {
                int corner = vertex switch { 0 => 0, 1 => 1, 2 => 2, 3 => 0, 4 => 2, _ => 3 };
                Vector3 position = BoxFaceCorner(minimum, maximum, face, corner);
                Vector3 transformedNormal = normal;
                if (grid is not null)
                {
                    position = grid.LocalToWorld(position);
                    transformedNormal = Vector3.Normalize(grid.LocalDirectionToWorld(normal));
                }
                Vector2 uvScale = !tileUv ? Vector2.One : face switch
                {
                    0 or 5 => new Vector2(maximum.X - minimum.X, maximum.Z - minimum.Z),
                    1 or 2 => new Vector2(maximum.Z - minimum.Z, maximum.Y - minimum.Y),
                    _ => new Vector2(maximum.X - minimum.X, maximum.Y - minimum.Y),
                };
                Vector2 uv = corner switch
                {
                    0 => Vector2.Zero,
                    1 => new Vector2(uvScale.X, 0f),
                    2 => uvScale,
                    _ => new Vector2(0f, uvScale.Y),
                };
                vertices.Add(position.X); vertices.Add(position.Y); vertices.Add(position.Z);
                vertices.Add(transformedNormal.X); vertices.Add(transformedNormal.Y); vertices.Add(transformedNormal.Z);
                vertices.Add(uv.X); vertices.Add(uv.Y);
                vertices.Add(materialId); vertices.Add(atlasFace); vertices.Add(definition.Roughness); vertices.Add(definition.Metallic);
                // A negative packed transmission preserves the existing vertex layout
                // while carrying the fluid flag to the shader. The shader exposes the
                // absolute optical transmission to lighting.
                float packedTransmission = definition.IsFluid
                    ? -MathF.Max(definition.Transmission, 0.001f)
                    : definition.Transmission;
                vertices.Add(packedTransmission);
                vertices.Add(definition.Emission.X); vertices.Add(definition.Emission.Y); vertices.Add(definition.Emission.Z);
                vertices.Add(cellX); vertices.Add(cellY); vertices.Add(cellZ); vertices.Add(gridId);
            }
        }
    }

    private static Vector3 BoxFaceCorner(Vector3 minimum, Vector3 maximum, int face, int corner) =>
        (face, corner) switch
        {
            (0, 0) => new(minimum.X, maximum.Y, minimum.Z),
            (0, 1) => new(maximum.X, maximum.Y, minimum.Z),
            (0, 2) => new(maximum.X, maximum.Y, maximum.Z),
            (0, _) => new(minimum.X, maximum.Y, maximum.Z),
            (1, 0) => new(minimum.X, minimum.Y, maximum.Z),
            (1, 1) => new(minimum.X, minimum.Y, minimum.Z),
            (1, 2) => new(minimum.X, maximum.Y, minimum.Z),
            (1, _) => new(minimum.X, maximum.Y, maximum.Z),
            (2, 0) => new(maximum.X, minimum.Y, minimum.Z),
            (2, 1) => new(maximum.X, minimum.Y, maximum.Z),
            (2, 2) => new(maximum.X, maximum.Y, maximum.Z),
            (2, _) => new(maximum.X, maximum.Y, minimum.Z),
            (3, 0) => new(minimum.X, minimum.Y, minimum.Z),
            (3, 1) => new(maximum.X, minimum.Y, minimum.Z),
            (3, 2) => new(maximum.X, maximum.Y, minimum.Z),
            (3, _) => new(minimum.X, maximum.Y, minimum.Z),
            (4, 0) => new(maximum.X, minimum.Y, maximum.Z),
            (4, 1) => new(minimum.X, minimum.Y, maximum.Z),
            (4, 2) => new(minimum.X, maximum.Y, maximum.Z),
            (4, _) => new(maximum.X, maximum.Y, maximum.Z),
            (5, 0) => new(minimum.X, minimum.Y, maximum.Z),
            (5, 1) => new(maximum.X, minimum.Y, maximum.Z),
            (5, 2) => new(maximum.X, minimum.Y, minimum.Z),
            _ => new(minimum.X, minimum.Y, minimum.Z),
        };

    private void UploadMaterialAtlas()
    {
        _materialAtlas = new ProceduralMaterialAtlas(_blocks);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _materialAtlasTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba8,
            ProceduralMaterialAtlas.Width,
            ProceduralMaterialAtlas.Height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            _materialAtlas.ColorPixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _materialDetailTexture);
        GL.TexImage2D(
            TextureTarget.Texture2D, 0, PixelInternalFormat.Rg8,
            ProceduralMaterialAtlas.Width, ProceduralMaterialAtlas.Height, 0,
            PixelFormat.Rg, PixelType.UnsignedByte, _materialAtlas.DetailPixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    private void RebuildDeferredLights()
    {
        foreach (ChunkCoordinate chunk in _dirtyDeferredLightChunks.Take(4).ToArray())
        {
            var lights = new List<DeferredPointLight>();
            foreach (StoredVoxel voxel in _world.SnapshotChunk(chunk).Voxels)
            {
                ushort id = voxel.MaterialId;
                if (id == 0) continue;
                Vector3 emission = _blocks.Get(id).Emission;
                float energy = emission.Length();
                if (energy < 0.08f) continue;
                lights.Add(new DeferredPointLight(
                    new Vector3(
                        chunk.MinimumX + voxel.LocalX + 0.5f,
                        voxel.Y + 0.5f,
                        chunk.MinimumZ + voxel.LocalZ + 0.5f),
                    Vector3.Normalize(emission + new Vector3(0.001f)),
                    Math.Clamp(energy, 0.25f, 8f),
                    Math.Clamp(4f + energy * 2.5f, 5f, 18f)));
            }
            _deferredLightsByChunk[chunk] = lights;
            _dirtyDeferredLightChunks.Remove(chunk);
        }

        _deferredPointLights.Clear();
        var candidates = _deferredLightsByChunk.Values.SelectMany(lights => lights).ToList();
        var activeGridIds = _world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        foreach (ulong stale in _deferredLightsByGrid.Keys.Where(id => !activeGridIds.Contains(id)).ToArray())
            _deferredLightsByGrid.Remove(stale);
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (!_deferredLightsByGrid.TryGetValue(grid.Id, out CachedGridLights? cached) ||
                cached.TopologyVersion != grid.TopologyVersion)
            {
                LocalDeferredLight[] localLights = grid.Elements.Select(element =>
                    (Element: element, Emission: _blocks.Get(element.MaterialId).Emission))
                    .Where(entry => entry.Emission.LengthSquared() >= 0.0064f)
                    .Select(entry =>
                    {
                        float energy = entry.Emission.Length();
                        return new LocalDeferredLight(
                            (entry.Element.Minimum + entry.Element.Maximum) * 0.5f,
                            Vector3.Normalize(entry.Emission + new Vector3(0.001f)),
                            Math.Clamp(energy, 0.25f, 8f),
                            Math.Clamp(4f + energy * 2.5f, 5f, 18f));
                    })
                    .ToArray();
                cached = new CachedGridLights(grid.TopologyVersion, localLights);
                _deferredLightsByGrid[grid.Id] = cached;
            }
            foreach (LocalDeferredLight light in cached.Lights)
                candidates.Add(new DeferredPointLight(
                    grid.LocalToWorld(light.LocalPosition), light.Color, light.Intensity, light.Radius));
        }
        _deferredPointLights.AddRange(candidates
            .OrderByDescending(light => light.Intensity)
            .Take(16));
        _deferredLightsDirty = _dirtyDeferredLightChunks.Count > 0;
    }

    private static MeshBuffer CreateMeshBuffer()
    {
        int vertexArray = GL.GenVertexArray();
        int vertexBuffer = GL.GenBuffer();
        GL.BindVertexArray(vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
        int stride = FloatsPerVertex * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(4);
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, 12 * sizeof(float));
        GL.EnableVertexAttribArray(5);
        GL.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, stride, 16 * sizeof(float));
        GL.BindVertexArray(0);
        return new MeshBuffer(vertexArray, vertexBuffer);
    }

    private void MarkChunkDirty(ChunkCoordinate chunk)
    {
        bool alreadyDirty = !_dirtyChunks.Add(chunk);
        _chunkMeshLastDirtyTimestamps[chunk] = Stopwatch.GetTimestamp();
        _chunkMeshDirtyCounts[chunk] = alreadyDirty && _chunkMeshDirtyCounts.TryGetValue(chunk, out int dirtyCount)
            ? dirtyCount + 1
            : 1;
        _chunkMeshVersions[chunk] = _chunkMeshVersions.TryGetValue(chunk, out int version)
            ? unchecked(version + 1)
            : 1;
    }

    private void OnVoxelChanged(VoxelChangedEvent change)
    {
        bool lightChanged = IsEmissive(change.OldBlockId) || IsEmissive(change.NewBlockId);
        if ((_deferred || EnhancedForward) && lightChanged)
        {
            ChunkCoordinate lightChunk = ChunkCoordinate.FromVoxel(change.X, change.Z);
            if (_chunkMeshes.ContainsKey(lightChunk))
            {
                _dirtyDeferredLightChunks.Add(lightChunk);
                _deferredLightsDirty = true;
            }
        }
        if (!lightChanged && IsCompletelyOccludedOpaqueReplacement(change)) return;
        foreach (ChunkCoordinate chunk in ChunkCoordinate.MeshDependenciesForVoxel(change.X, change.Z))
            if (_chunkMeshes.ContainsKey(chunk)) MarkChunkDirty(chunk);
    }

    private void OnFluidLevelChanged(int x, int y, int z)
    {
        foreach (ChunkCoordinate chunk in ChunkCoordinate.MeshDependenciesForVoxel(x, z))
            if (_chunkMeshes.ContainsKey(chunk)) MarkChunkDirty(chunk);
    }

    private bool IsCompletelyOccludedOpaqueReplacement(VoxelChangedEvent change)
    {
        if (change.OldBlockId == 0 || change.NewBlockId == 0 ||
            !IsGreedyOpaque(_blocks.Get(change.OldBlockId)) ||
            !IsGreedyOpaque(_blocks.Get(change.NewBlockId))) return false;

        ReadOnlySpan<(int X, int Y, int Z)> neighbors = stackalloc (int, int, int)[]
        {
            (1, 0, 0), (-1, 0, 0), (0, 1, 0),
            (0, -1, 0), (0, 0, 1), (0, 0, -1),
        };
        foreach ((int offsetX, int offsetY, int offsetZ) in neighbors)
        {
            ushort neighbor = _world.GetLoadedBlockId(
                change.X + offsetX,
                change.Y + offsetY,
                change.Z + offsetZ);
            if (neighbor == 0 || !IsGreedyOpaque(_blocks.Get(neighbor))) return false;
        }
        return true;
    }

    private bool IsEmissive(ushort materialId) =>
        materialId != 0 && _blocks.Get(materialId).Emission.LengthSquared() >= 0.0064f;

    private void OnWorldReloaded()
    {
        _deferredLightsDirty = true;
        _deferredLightsByChunk.Clear();
        _deferredLightsByGrid.Clear();
        _dirtyDeferredLightChunks.Clear();
        if (_deferred || EnhancedForward)
            foreach (ChunkCoordinate chunk in _world.SnapshotLoadedChunks())
                _dirtyDeferredLightChunks.Add(chunk);
        _directionalShadowDirty = true;
        _hasDirectionalShadowMap = false;
        ReconcileLoadedChunkMeshes();
        _physicsTopologyDirty = true;
        _forcePhysicsMeshRebuild = true;
    }

    private void OnChunkGenerated(ChunkCoordinate chunk)
    {
        // ChunkGenerated already tells us exactly which entry changed.  Keeping the
        // observed count current avoids rebuilding a HashSet of every loaded chunk
        // once per streamed chunk; a camera-cell change still performs a complete
        // residency reconciliation below.
        _meshResidencyLoadedChunkCount = _world.LoadedChunkCount;
        if (_meshResidencyInitialized && !IsInsideMeshResidency(chunk)) return;
        if (!_chunkMeshes.ContainsKey(chunk)) _chunkMeshes.Add(chunk, CreateMeshBuffer());
        MarkChunkDirty(chunk);
        foreach (ChunkCoordinate neighbor in new[]
                 {
                     chunk with { X = chunk.X - 1 }, chunk with { X = chunk.X + 1 },
                     chunk with { Z = chunk.Z - 1 }, chunk with { Z = chunk.Z + 1 },
                 })
            if (_chunkMeshes.ContainsKey(neighbor)) MarkChunkDirty(neighbor);
        _deferredLightsDirty = true;
        if (_deferred || EnhancedForward) _dirtyDeferredLightChunks.Add(chunk);
    }

    private void ReconcileLoadedChunkMeshes()
    {
        HashSet<ChunkCoordinate> loaded = _world.SnapshotLoadedChunks()
            .Where(chunk => !_meshResidencyInitialized || IsInsideMeshResidency(chunk))
            .ToHashSet();
        foreach (ChunkCoordinate stale in _chunkMeshes.Keys.Where(chunk => !loaded.Contains(chunk)).ToArray())
        {
            _chunkMeshes[stale].Dispose();
            _chunkMeshes.Remove(stale);
            _dirtyChunks.Remove(stale);
            _chunkMeshVersions.Remove(stale);
            _chunkMeshLastDirtyTimestamps.Remove(stale);
            _chunkMeshDirtyCounts.Remove(stale);
            _chunkMeshBuildJobs.Remove(stale);
            _deferredLightsByChunk.Remove(stale);
            _dirtyDeferredLightChunks.Remove(stale);
        }
        foreach (ChunkCoordinate chunk in loaded)
        {
            if (!_chunkMeshes.ContainsKey(chunk)) _chunkMeshes.Add(chunk, CreateMeshBuffer());
            MarkChunkDirty(chunk);
        }
    }

    private void OnMicroVoxelCellChanged(int x, int y, int z)
    {
        foreach (ChunkCoordinate chunk in ChunkCoordinate.MeshDependenciesForVoxel(x, z))
            if (_chunkMeshes.ContainsKey(chunk)) MarkChunkDirty(chunk);
    }

    private void OnMicroVoxelsReloaded()
    {
        foreach (ChunkCoordinate chunk in _chunkMeshes.Keys) MarkChunkDirty(chunk);
    }

    private void OnPhysicsGridsChanged()
    {
        _physicsTopologyDirty = true;
        _dynamicDirectionalShadowDirty = true;
        if (_deferred || EnhancedForward) _deferredLightsDirty = true;
    }

    private void OnDefinitionsChanged()
    {
        _blockDefinitions = _blocks.CreateLookupSnapshot();
        UploadMaterialAtlas();
        OnWorldReloaded();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.DetailedVoxelChanged -= OnVoxelChanged;
        _world.FluidLevelChanged -= OnFluidLevelChanged;
        _world.WorldReloaded -= OnWorldReloaded;
        _world.MicroVoxelCellChanged -= OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded -= OnMicroVoxelsReloaded;
        _world.PhysicsGrids.Changed -= OnPhysicsGridsChanged;
        _world.ChunkGenerated -= OnChunkGenerated;
        _blocks.DefinitionsChanged -= OnDefinitionsChanged;
        foreach (MeshBuffer mesh in _chunkMeshes.Values) mesh.Dispose();
        foreach (PhysicsGridMesh mesh in _physicsGridMeshes.Values) mesh.Dispose();
        GL.DeleteTexture(_materialAtlasTexture);
        GL.DeleteTexture(_materialDetailTexture);
        if (_sceneColorCopy != 0) GL.DeleteTexture(_sceneColorCopy);
        DeleteGBuffer();
        DeleteForwardBuffers();
        DeleteDirectionalShadowMaps();
        if (_fullscreenVertexArray != 0) GL.DeleteVertexArray(_fullscreenVertexArray);
        if (_analyticSphereVertexArray != 0) GL.DeleteVertexArray(_analyticSphereVertexArray);
        _deferredGeometry?.Dispose();
        _deferredLighting?.Dispose();
        _deferredDepth?.Dispose();
        _deferredPost?.Dispose();
        _forwardPost?.Dispose();
        _forwardShadowGpuTimer?.Dispose();
        _forwardMainGpuTimer?.Dispose();
        _forwardTransparencyGpuTimer?.Dispose();
        _forwardPostGpuTimer?.Dispose();
        _shader.Dispose();
        _analyticSphereShader.Dispose();
    }

    private void DeleteDirectionalShadowMaps()
    {
        if (_directionalShadowDepth != 0) GL.DeleteTexture(_directionalShadowDepth);
        if (_dynamicDirectionalShadowDepth != 0) GL.DeleteTexture(_dynamicDirectionalShadowDepth);
        if (_directionalShadowBuffer != 0) GL.DeleteFramebuffer(_directionalShadowBuffer);
        if (_dynamicDirectionalShadowBuffer != 0) GL.DeleteFramebuffer(_dynamicDirectionalShadowBuffer);
        _directionalShadowDepth = _dynamicDirectionalShadowDepth = 0;
        _directionalShadowBuffer = _dynamicDirectionalShadowBuffer = 0;
    }

    private readonly record struct DeferredPointLight(Vector3 Position, Vector3 Color, float Intensity, float Radius);
    private readonly record struct LocalDeferredLight(
        Vector3 LocalPosition, Vector3 Color, float Intensity, float Radius);
    private sealed record CachedGridLights(int TopologyVersion, LocalDeferredLight[] Lights);
    private sealed record ChunkMeshData(
        PooledFloatBuffer Vertices,
        int MinimumY,
        int MaximumYExclusive,
        double BuildMilliseconds) : IDisposable
    {
        public void Dispose() => Vertices.Dispose();
    }
    private sealed record ChunkMeshBuildJob(int Version, Task<ChunkMeshData> Build);

    private sealed class GpuPassTimer : IDisposable
    {
        private readonly int[] _queries = { GL.GenQuery(), GL.GenQuery() };
        private readonly bool[] _issued = new bool[2];
        private int _writeIndex;

        public double LastMilliseconds { get; private set; }
        public double MaximumMilliseconds { get; private set; }

        public void Begin()
        {
            int readIndex = 1 - _writeIndex;
            if (_issued[readIndex])
            {
                GL.GetQueryObject(_queries[readIndex], GetQueryObjectParam.QueryResultAvailable, out int available);
                if (available != 0)
                {
                    GL.GetQueryObject(_queries[readIndex], GetQueryObjectParam.QueryResult, out long nanoseconds);
                    LastMilliseconds = nanoseconds / 1_000_000.0;
                    MaximumMilliseconds = Math.Max(MaximumMilliseconds, LastMilliseconds);
                    _issued[readIndex] = false;
                }
            }
            GL.BeginQuery(QueryTarget.TimeElapsed, _queries[_writeIndex]);
        }

        public void End()
        {
            GL.EndQuery(QueryTarget.TimeElapsed);
            _issued[_writeIndex] = true;
            _writeIndex = 1 - _writeIndex;
        }

        public void ResetMaximum() => MaximumMilliseconds = 0.0;

        public void Dispose()
        {
            foreach (int query in _queries) GL.DeleteQuery(query);
        }
    }

    private sealed class PooledFloatBuffer : IDisposable
    {
        private float[]? _buffer;

        public int Count { get; private set; }

        public PooledFloatBuffer(int initialCapacity)
        {
            _buffer = ArrayPool<float>.Shared.Rent(Math.Max(1, initialCapacity));
        }

        public void Add(float value)
        {
            float[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledFloatBuffer));
            if (Count == buffer.Length)
            {
                float[] expanded = ArrayPool<float>.Shared.Rent(checked(buffer.Length * 2));
                buffer.AsSpan(0, Count).CopyTo(expanded);
                ArrayPool<float>.Shared.Return(buffer);
                _buffer = buffer = expanded;
            }
            buffer[Count++] = value;
        }

        public Span<float> AsSpan() =>
            (_buffer ?? throw new ObjectDisposedException(nameof(PooledFloatBuffer))).AsSpan(0, Count);

        public void Dispose()
        {
            float[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<float>.Shared.Return(buffer);
            Count = 0;
        }
    }

    private sealed class PhysicsGridMesh : IDisposable
    {
        public MeshBuffer Buffer { get; }
        public int TopologySignature { get; }
        public Matrix4 CurrentModel { get; set; }
        public Matrix4 PreviousModel { get; set; }
        public bool HasPreviousModel { get; set; }

        public PhysicsGridMesh(MeshBuffer buffer, int topologySignature, Matrix4 model)
        {
            Buffer = buffer;
            TopologySignature = topologySignature;
            CurrentModel = model;
            PreviousModel = model;
        }

        public void Dispose() => Buffer.Dispose();
    }

    private sealed class MeshBuffer : IDisposable
    {
        public int VertexArray { get; }
        public int VertexBuffer { get; }
        public int VertexCount { get; set; }
        public float BoundsCenterY { get; set; } = 96f;
        public float BoundsHalfHeight { get; set; } = 160f;

        public MeshBuffer(int vertexArray, int vertexBuffer)
        {
            VertexArray = vertexArray;
            VertexBuffer = vertexBuffer;
        }

        public void Dispose()
        {
            GL.DeleteBuffer(VertexBuffer);
            GL.DeleteVertexArray(VertexArray);
        }
    }
}
