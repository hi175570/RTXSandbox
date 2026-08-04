using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using RTXSandbox.Infrastructure;
using RTXSandbox.Gameplay;
using RTXSandbox.ModAPI;
using RTXSandbox.World;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using OpenTkVector3 = OpenTK.Mathematics.Vector3;

namespace RTXSandbox.Rendering.Backends;

/// <summary>
/// Shared real-time voxel raster backend. PBR Forward and Classic are separate modes,
/// but deliberately share chunk-mesh and atlas infrastructure.
/// </summary>
public sealed class RasterVoxelRenderer : IRenderBackend, IMaterialPreviewSource
{
    private const int FloatsPerVertex = 20;
    private const float CullingFrustumScale = 1.6f;
    private const int DirectionalShadowMapSize = 2048;
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
    private readonly ShaderProgram? _deferredGeometry;
    private readonly ShaderProgram? _deferredLighting;
    private readonly ShaderProgram? _deferredDepth;
    private readonly ShaderProgram? _deferredPost;
    private readonly Dictionary<ChunkCoordinate, MeshBuffer> _chunkMeshes = new();
    private readonly Dictionary<ulong, PhysicsGridMesh> _physicsGridMeshes = new();
    private readonly HashSet<ChunkCoordinate> _dirtyChunks = new();
    private readonly List<MeshBuffer> _visibleChunkMeshes = new();
    private readonly HashSet<ChunkCoordinate> _visibleChunkCoordinates = new();
    private readonly HashSet<ulong> _visiblePhysicsGridIds = new();
    private readonly int _materialAtlasTexture;
    private readonly int _materialDetailTexture;
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
    private int _directionalShadowBuffer;
    private int _directionalShadowDepth;
    private int _dynamicDirectionalShadowBuffer;
    private int _dynamicDirectionalShadowDepth;
    private int _fullscreenVertexArray;
    private Matrix4 _previousView = Matrix4.Identity;
    private Matrix4 _previousProjection = Matrix4.Identity;
    private bool _hasPreviousMatrices;
    private bool _directionalShadowDirty = true;
    private bool _dynamicDirectionalShadowDirty = true;
    private bool _hasDirectionalShadowMap;
    private bool _hasDynamicDirectionalShadowMap;
    private Vector3 _lastDirectionalShadowDirection;
    private Vector3 _lastDirectionalShadowCenter;
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
    public int VisibleChunkCount => _visibleChunkMeshes.Count;
    public int CulledChunkCount => _chunkMeshes.Count - _visibleChunkMeshes.Count;
    public int VisiblePhysicsGridCount => _visiblePhysicsGridIds.Count;
    public int ShadowCasterChunkCount { get; private set; }
    public int DirectionalShadowDrawCount { get; private set; }
    public int DirectionalShadowMapUpdateCount { get; private set; }
    public int StaticShadowMapUpdateCount { get; private set; }
    public int DynamicShadowMapUpdateCount { get; private set; }
    public int PhysicsGridMeshUploadCount { get; private set; }
    public int PendingVisibleMeshRebuildCount =>
        _dirtyChunks.Count(_visibleChunkCoordinates.Contains);
    public float ViewCullingExpansion => CullingFrustumScale;

    public (float Minimum, float Maximum) ReadDirectionalShadowDepthRange(bool dynamicLayer)
    {
        int texture = dynamicLayer ? _dynamicDirectionalShadowDepth : _directionalShadowDepth;
        if (texture == 0) return (1f, 1f);
        var depths = new float[DirectionalShadowMapSize * DirectionalShadowMapSize];
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
        string shaderRoot = Path.Combine(RuntimePaths.OutputRoot, "Shaders");
        _shader = new ShaderProgram(
            (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.vert.glsl"))),
            (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "voxel_raster.frag.glsl"))));
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

        foreach (ChunkCoordinate chunk in _world.SnapshotLoadedChunks())
        {
            _chunkMeshes.Add(chunk, CreateMeshBuffer());
            _dirtyChunks.Add(chunk);
            if (_deferred) _dirtyDeferredLightChunks.Add(chunk);
        }
        _materialAtlasTexture = GL.GenTexture();
        _materialDetailTexture = GL.GenTexture();
        UploadMaterialAtlas();
        Resize(width, height, 1f);
        _world.DetailedVoxelChanged += OnVoxelChanged;
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
    }

    public void ResetAccumulation()
    {
        // Real-time raster backends have no history buffer.
    }

    public void Render(
        Vector3 cameraPosition,
        Camera camera,
        VoxelHit? selection,
        float timeOfDay,
        RenderQualitySettings quality,
        float exposure)
    {
        UpdateVisibleGeometry(cameraPosition, camera, timeOfDay);
        RebuildDirtyMeshes();
        SynchronizePhysicsGridMeshes();

        if (_hybrid)
        {
            // Hybrid PBR Raster is the high-quality raster path: deferred opaque
            // lighting plus forward water/glass. It shares the proven GBuffer
            // infrastructure while maintaining its own selectable backend identity.
            quality = quality with
            {
                DeferredRendererQuality = Math.Max(2, quality.DeferredRendererQuality),
                DeferredGBufferQuality = Math.Max(2, quality.DeferredGBufferQuality),
                DeferredLightingQuality = Math.Max(2, quality.DeferredLightingQuality),
                DeferredShadowQuality = Math.Max(2, quality.DeferredShadowQuality),
                DeferredReflectionQuality = Math.Max(2, quality.DeferredReflectionQuality),
                DeferredWaterQuality = Math.Max(2, quality.DeferredWaterQuality),
                DeferredTransparencyQuality = Math.Max(2, quality.DeferredTransparencyQuality),
                DeferredPostProcessingQuality = Math.Max(2, quality.DeferredPostProcessingQuality),
                DeferredHdr = true,
                DeferredBloom = true,
            };
        }

        if (_deferred)
        {
            RenderDeferred(cameraPosition, camera, selection, timeOfDay, quality, exposure);
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
        GL.Uniform1(_shader.Uniform("uTransparentOnly"), -1);
        GL.Uniform1(_shader.Uniform("uDeferredTransparent"), 0);
        GL.Uniform1(_shader.Uniform("uWaterQuality"), 0);
        GL.Uniform1(_shader.Uniform("uTransparencyQuality"), 0);
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
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        GL.Uniform1(_shader.Uniform("uTransparentOnly"), 1);
        DrawVisibleGeometry(_shader, includePreviousModel: false, transparentSortOrigin: cameraPosition);
        GL.DepthMask(true);
        GL.BindVertexArray(0);
        CommitPhysicsGridTransforms();
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
        (Matrix4 lightView, Matrix4 lightProjection) = RenderDirectionalShadowMap(
            primaryLightDirection, quality.DeferredShadowQuality, cameraPosition);

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
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Lequal);

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
        GL.Uniform1(lighting.Uniform("uRendererQuality"), quality.DeferredRendererQuality);
        GL.Uniform1(lighting.Uniform("uLightingQuality"), quality.DeferredLightingQuality);
        GL.Uniform1(lighting.Uniform("uShadowQuality"), quality.DeferredShadowQuality);
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

        // HDR post pass: optional fog, bloom, color grading, sharpening and selected
        // tone mapper. Debug views bypass artistic post processing.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        ShaderProgram post = _deferredPost!;
        post.Use();
        BindGBufferTexture(post, "uHdrColor", _hdrColor, TextureUnit.Texture0, 0);
        BindGBufferTexture(post, "uPosition", _gPosition, TextureUnit.Texture1, 1);
        GL.Uniform2(post.Uniform("uTexelSize"), 1f / _width, 1f / _height);
        GL.Uniform3(post.Uniform("uCameraPosition"), cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        GL.Uniform1(post.Uniform("uExposure"), exposure);
        GL.Uniform1(post.Uniform("uHdrEnabled"), quality.DeferredHdr ? 1 : 0);
        GL.Uniform1(post.Uniform("uBloomEnabled"), quality.DeferredBloom ? 1 : 0);
        GL.Uniform1(post.Uniform("uPostQuality"), quality.DeferredPostProcessingQuality);
        GL.Uniform1(post.Uniform("uFogQuality"), quality.DeferredFogQuality);
        GL.Uniform1(post.Uniform("uToneMapping"), quality.DeferredToneMapping);
        GL.Uniform1(post.Uniform("uDebugView"), (int)quality.DeferredDebugView);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _gBuffer);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        GL.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
            ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

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
        GL.Uniform1(_shader.Uniform("uSpecularQuality"), quality.PbrSpecularQuality);
        GL.Uniform1(_shader.Uniform("uReflectionQuality"), quality.PbrReflectionQuality);
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
        GL.DepthMask(true);
        GL.BindVertexArray(0);

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

    private static void CreateDepthFramebuffer(out int framebuffer, out int depthTexture)
    {
        framebuffer = GL.GenFramebuffer();
        depthTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, depthTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24,
            DirectionalShadowMapSize, DirectionalShadowMapSize, 0,
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
            throw new InvalidOperationException("Deferred directional shadow framebuffer is incomplete.");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private (Matrix4 View, Matrix4 Projection) RenderDirectionalShadowMap(
        Vector3 lightDirection,
        int shadowQuality,
        Vector3 cameraPosition)
    {
        lightDirection = Vector3.Normalize(lightDirection);
        if (shadowQuality <= 0)
        {
            DirectionalShadowDrawCount = 0;
            return (_cachedLightView, _cachedLightProjection);
        }
        Vector3 requestedCenter = new(cameraPosition.X, 80f, cameraPosition.Z);
        bool lightChanged = !_hasDirectionalShadowMap ||
            Vector3.DistanceSquared(lightDirection, _lastDirectionalShadowDirection) >=
            ShadowDirectionUpdateThresholdSquared ||
            Vector3.DistanceSquared(requestedCenter, _lastDirectionalShadowCenter) >= 64f;
        if (lightChanged)
        {
            var direction = new OpenTkVector3(lightDirection.X, lightDirection.Y, lightDirection.Z);
            var center = new OpenTkVector3(requestedCenter.X, requestedCenter.Y, requestedCenter.Z);
            var eye = center + direction * 112f;
            OpenTkVector3 up = MathF.Abs(OpenTkVector3.Dot(direction, OpenTkVector3.UnitY)) > 0.96f
                ? OpenTkVector3.UnitZ
                : OpenTkVector3.UnitY;
            _cachedLightView = Matrix4.LookAt(eye, center, up);
            _cachedLightProjection = Matrix4.CreateOrthographic(100f, 100f, 1f, 224f);
            _lastDirectionalShadowDirection = lightDirection;
            _lastDirectionalShadowCenter = requestedCenter;
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
            RenderShadowLayer(_directionalShadowBuffer, drawDynamic: false);
            _directionalShadowDirty = false;
            _hasDirectionalShadowMap = true;
            StaticShadowMapUpdateCount++;
            DirectionalShadowMapUpdateCount++;
        }
        if (_dynamicDirectionalShadowDirty || !_hasDynamicDirectionalShadowMap)
        {
            RenderShadowLayer(_dynamicDirectionalShadowBuffer, drawDynamic: true);
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
        GL.Viewport(0, 0, DirectionalShadowMapSize, DirectionalShadowMapSize);
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
        _gMotion = CreateGBufferTexture(PixelInternalFormat.Rg16f, FramebufferAttachment.ColorAttachment4);
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
            bool shadowCaster = _deferred && !directlyVisible && IsShadowCasterNeeded(
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
                (_deferred && IsShadowCasterNeeded(grid.Position, gridRadius, lightDirection, cameraPosition,
                    camera.Forward, camera.FieldOfViewDegrees, aspect)))
                visibleGridIds.Add(grid.Id);
        }
        if (!_visiblePhysicsGridIds.SetEquals(visibleGridIds))
        {
            _visiblePhysicsGridIds.Clear();
            _visiblePhysicsGridIds.UnionWith(visibleGridIds);
        }
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
        ChunkCoordinate[] dirtyChunks = _dirtyChunks
            .Where(_chunkMeshes.ContainsKey)
            .Where(chunk => _deferred || _visibleChunkCoordinates.Contains(chunk))
            .OrderBy(chunk => _visibleChunkCoordinates.Contains(chunk) ? 0 : 1)
            .Take(1)
            .ToArray();
        foreach (ChunkCoordinate chunk in dirtyChunks)
        {
            RebuildChunkMesh(chunk);
            _dirtyChunks.Remove(chunk);
        }
    }

    private void RebuildChunkMesh(ChunkCoordinate chunk)
    {
        var vertices = new List<float>(32_000);
        VoxelChunkSnapshot chunkSnapshot = _world.SnapshotChunk(chunk);
        foreach (StoredVoxel voxel in chunkSnapshot.Voxels)
        {
            AddRegularVoxel(
                vertices,
                chunk.MinimumX + voxel.LocalX,
                voxel.Y,
                chunk.MinimumZ + voxel.LocalZ,
                voxel.MaterialId);
        }

        foreach (StoredMicroVoxel micro in chunkSnapshot.MicroVoxels)
        {
            int x = chunk.MinimumX + micro.LocalX;
            int y = micro.Y;
            int z = chunk.MinimumZ + micro.LocalZ;
            MicroVoxelSnapshot snapshot = micro.Snapshot;
            float inverse = 1f / snapshot.Resolution;
            foreach (MicroVoxelBox box in snapshot.Boxes)
            {
                AddBox(
                    vertices,
                    new Vector3(x + box.MinX * inverse, y + box.MinY * inverse, z + box.MinZ * inverse),
                    new Vector3(x + box.MaxX * inverse, y + box.MaxY * inverse, z + box.MaxZ * inverse),
                    box.MaterialId,
                    face => true,
                    x, y, z, 0);
            }
        }

        MeshBuffer mesh = _chunkMeshes[chunk];
        int minimumY = chunkSnapshot.Voxels.Count == 0
            ? int.MaxValue
            : chunkSnapshot.Voxels.Min(voxel => voxel.Y);
        int maximumY = chunkSnapshot.Voxels.Count == 0
            ? int.MinValue
            : chunkSnapshot.Voxels.Max(voxel => voxel.Y) + 1;
        foreach (StoredMicroVoxel micro in chunkSnapshot.MicroVoxels)
        {
            if (micro.Snapshot.Boxes.Count == 0) continue;
            minimumY = Math.Min(minimumY, micro.Y);
            maximumY = Math.Max(maximumY, micro.Y + 1);
        }
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
        UploadMesh(mesh, vertices, BufferUsageHint.StaticDraw);
        _directionalShadowDirty = true;
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
        var vertices = new List<float>(estimatedFloats);
        var includeFaces = new bool[6];
        Func<int, bool> includeFace = face => includeFaces[face];
        foreach (PhysicsGridElement element in grid.Elements)
        {
            // Geometry stays in Grid-local coordinates. Movement and rotation are
            // supplied as a model matrix, so physics no longer rebuilds/uploads every
            // vertex on every simulation step.
            for (int face = 0; face < includeFaces.Length; face++)
                includeFaces[face] = ShouldRenderGridFace(grid, element, face);
            AddBox(vertices, element.Minimum, element.Maximum, element.MaterialId, includeFace,
                element.CellX, element.CellY, element.CellZ, (int)grid.Id);
        }
        MeshBuffer buffer = CreateMeshBuffer();
        UploadMesh(buffer, vertices, BufferUsageHint.StaticDraw);
        PhysicsGridMeshUploadCount++;
        Matrix4 model = CreatePhysicsGridModel(grid);
        return new PhysicsGridMesh(buffer, signature, model);
    }

    private bool ShouldRenderGridFace(PhysicsGrid grid, PhysicsGridElement element, int face)
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
        BlockDefinition current = _blocks.Get(element.MaterialId);
        BlockDefinition adjacent = _blocks.Get(adjacentId);
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

    private static void UploadMesh(MeshBuffer mesh, List<float> vertices, BufferUsageHint usage)
    {
        float[] data = vertices.Count == 0 ? new float[FloatsPerVertex] : vertices.ToArray();
        GL.BindBuffer(BufferTarget.ArrayBuffer, mesh.VertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, usage);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        mesh.VertexCount = vertices.Count / FloatsPerVertex;
    }

    private static void DrawMesh(MeshBuffer mesh)
    {
        if (mesh.VertexCount == 0) return;
        GL.BindVertexArray(mesh.VertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, mesh.VertexCount);
    }

    private void AddRegularVoxel(List<float> vertices, int x, int y, int z, ushort material)
    {
        bool fluid = _blocks.Get(material).IsFluid;
        ushort above = _world.GetLoadedBlockId(x, y + 1, z);
        bool fluidAbove = above != 0 && _blocks.Get(above).IsFluid;
        float maximumY = fluid && !fluidAbove
            ? y + VoxelWorld.FluidSurfaceHeight
            : y + 1f;
        AddBox(
            vertices,
            new Vector3(x, y, z),
            new Vector3(x + 1f, maximumY, z + 1f),
            material,
            face => face switch
            {
                // A fluid volume is represented by one continuous, double-sided top
                // surface. Per-block side/bottom faces caused visible four-sided cubes
                // through alpha blending at oblique camera angles.
                0 => !fluidAbove && ShouldRenderFace(x, y + 1, z, material),
                _ when fluid => false,
                1 => ShouldRenderFace(x - 1, y, z, material),
                2 => ShouldRenderFace(x + 1, y, z, material),
                3 => ShouldRenderFace(x, y, z - 1, material),
                4 => ShouldRenderFace(x, y, z + 1, material),
                _ => ShouldRenderFace(x, y - 1, z, material),
            },
            x, y, z, 0);
    }

    private bool ShouldRenderFace(int x, int y, int z, ushort currentMaterial)
    {
        if (!_world.IsInside(x, y, z)) return true;
        ushort neighbor = _world.GetLoadedBlockId(x, y, z);
        if (neighbor == 0) return true;
        if (neighbor == currentMaterial) return false;
        BlockDefinition current = _blocks.Get(currentMaterial);
        BlockDefinition adjacent = _blocks.Get(neighbor);
        if (current.IsFluid && adjacent.IsFluid) return false;
        return current.Transmission > 0f || current.IsFluid || adjacent.Transmission > 0f || adjacent.IsFluid;
    }

    private void AddBox(
        List<float> vertices,
        Vector3 minimum,
        Vector3 maximum,
        ushort materialId,
        Func<int, bool> includeFace,
        int cellX,
        int cellY,
        int cellZ,
        int gridId,
        PhysicsGrid? grid = null)
    {
        // Face order is top, -X, +X, -Z, +Z, bottom. Atlas face is top/side/bottom.
        Vector3[][] faces =
        {
            new[] { new Vector3(minimum.X, maximum.Y, minimum.Z), new Vector3(maximum.X, maximum.Y, minimum.Z), new Vector3(maximum.X, maximum.Y, maximum.Z), new Vector3(minimum.X, maximum.Y, maximum.Z) },
            new[] { new Vector3(minimum.X, minimum.Y, maximum.Z), new Vector3(minimum.X, minimum.Y, minimum.Z), new Vector3(minimum.X, maximum.Y, minimum.Z), new Vector3(minimum.X, maximum.Y, maximum.Z) },
            new[] { new Vector3(maximum.X, minimum.Y, minimum.Z), new Vector3(maximum.X, minimum.Y, maximum.Z), new Vector3(maximum.X, maximum.Y, maximum.Z), new Vector3(maximum.X, maximum.Y, minimum.Z) },
            new[] { new Vector3(minimum.X, minimum.Y, minimum.Z), new Vector3(maximum.X, minimum.Y, minimum.Z), new Vector3(maximum.X, maximum.Y, minimum.Z), new Vector3(minimum.X, maximum.Y, minimum.Z) },
            new[] { new Vector3(maximum.X, minimum.Y, maximum.Z), new Vector3(minimum.X, minimum.Y, maximum.Z), new Vector3(minimum.X, maximum.Y, maximum.Z), new Vector3(maximum.X, maximum.Y, maximum.Z) },
            new[] { new Vector3(minimum.X, minimum.Y, maximum.Z), new Vector3(maximum.X, minimum.Y, maximum.Z), new Vector3(maximum.X, minimum.Y, minimum.Z), new Vector3(minimum.X, minimum.Y, minimum.Z) },
        };
        Vector3[] normals = { Vector3.UnitY, -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitY };
        Vector2[] uvs = { new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f) };
        int[] triangles = { 0, 1, 2, 0, 2, 3 };
        BlockDefinition definition = _blocks.Get(materialId);
        for (int face = 0; face < faces.Length; face++)
        {
            if (!includeFace(face)) continue;
            int atlasFace = face == 0 ? 0 : face == 5 ? 2 : 1;
            foreach (int corner in triangles)
            {
                Vector3 position = faces[face][corner];
                Vector3 normal = normals[face];
                if (grid is not null)
                {
                    position = grid.LocalToWorld(position);
                    normal = Vector3.Normalize(grid.LocalDirectionToWorld(normal));
                }
                Vector2 uv = uvs[corner];
                vertices.Add(position.X); vertices.Add(position.Y); vertices.Add(position.Z);
                vertices.Add(normal.X); vertices.Add(normal.Y); vertices.Add(normal.Z);
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

    private void OnVoxelChanged(VoxelChangedEvent change)
    {
        bool lightChanged = IsEmissive(change.OldBlockId) || IsEmissive(change.NewBlockId);
        if (_deferred && lightChanged)
        {
            _dirtyDeferredLightChunks.Add(ChunkCoordinate.FromVoxel(change.X, change.Z));
            _deferredLightsDirty = true;
        }
        foreach (ChunkCoordinate chunk in ChunkCoordinate.MeshDependenciesForVoxel(change.X, change.Z))
            _dirtyChunks.Add(chunk);
    }

    private bool IsEmissive(ushort materialId) =>
        materialId != 0 && _blocks.Get(materialId).Emission.LengthSquared() >= 0.0064f;

    private void OnWorldReloaded()
    {
        _deferredLightsDirty = true;
        _deferredLightsByChunk.Clear();
        _deferredLightsByGrid.Clear();
        _dirtyDeferredLightChunks.Clear();
        if (_deferred)
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
        if (!_chunkMeshes.ContainsKey(chunk)) _chunkMeshes.Add(chunk, CreateMeshBuffer());
        _dirtyChunks.Add(chunk);
        foreach (ChunkCoordinate neighbor in new[]
                 {
                     chunk with { X = chunk.X - 1 }, chunk with { X = chunk.X + 1 },
                     chunk with { Z = chunk.Z - 1 }, chunk with { Z = chunk.Z + 1 },
                 })
            if (_chunkMeshes.ContainsKey(neighbor)) _dirtyChunks.Add(neighbor);
        _deferredLightsDirty = true;
        if (_deferred) _dirtyDeferredLightChunks.Add(chunk);
    }

    private void ReconcileLoadedChunkMeshes()
    {
        HashSet<ChunkCoordinate> loaded = _world.SnapshotLoadedChunks().ToHashSet();
        foreach (ChunkCoordinate stale in _chunkMeshes.Keys.Where(chunk => !loaded.Contains(chunk)).ToArray())
        {
            _chunkMeshes[stale].Dispose();
            _chunkMeshes.Remove(stale);
            _dirtyChunks.Remove(stale);
            _deferredLightsByChunk.Remove(stale);
            _dirtyDeferredLightChunks.Remove(stale);
        }
        foreach (ChunkCoordinate chunk in loaded)
        {
            if (!_chunkMeshes.ContainsKey(chunk)) _chunkMeshes.Add(chunk, CreateMeshBuffer());
            _dirtyChunks.Add(chunk);
        }
    }

    private void OnMicroVoxelCellChanged(int x, int y, int z)
    {
        foreach (ChunkCoordinate chunk in ChunkCoordinate.MeshDependenciesForVoxel(x, z))
            _dirtyChunks.Add(chunk);
    }

    private void OnMicroVoxelsReloaded()
    {
        foreach (ChunkCoordinate chunk in _chunkMeshes.Keys) _dirtyChunks.Add(chunk);
    }

    private void OnPhysicsGridsChanged()
    {
        _physicsTopologyDirty = true;
        _dynamicDirectionalShadowDirty = true;
        if (_deferred) _deferredLightsDirty = true;
    }

    private void OnDefinitionsChanged()
    {
        UploadMaterialAtlas();
        OnWorldReloaded();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.DetailedVoxelChanged -= OnVoxelChanged;
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
        DeleteGBuffer();
        DeleteDirectionalShadowMaps();
        if (_fullscreenVertexArray != 0) GL.DeleteVertexArray(_fullscreenVertexArray);
        _deferredGeometry?.Dispose();
        _deferredLighting?.Dispose();
        _deferredDepth?.Dispose();
        _deferredPost?.Dispose();
        _shader.Dispose();
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
