using System.Numerics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using RTXSandbox.Gameplay;
using RTXSandbox.Infrastructure;
using RTXSandbox.ModAPI;
using RTXSandbox.World;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.Dxc;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;
using DxgiFormat = Vortice.DXGI.Format;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace RTXSandbox.Rendering.Backends;

/// <summary>
/// Windows-only DXR V1 backend. DXR renders into a D3D12 UAV; a synchronized
/// readback is uploaded to an OpenGL texture so the existing window/UI remains intact.
/// </summary>
public sealed unsafe class DxrHardwareRenderer : IRenderBackend, IMaterialPreviewSource
{
    private const float CullingFrustumScale = 1.6f;
    private const int DescriptorCount = 6;
    private const int CameraConstantCount = 52;
    // D3D12 permits a maximum TraceRay recursion depth of 31. One level is the
    // primary ray and one is reserved for the deepest shadow ray.
    private const int MaximumReflectionBounces = 29;
    private const byte AllFaces = 0b0011_1111;
    private static readonly Lazy<(bool Supported, string Reason)> Support = new(ProbeSupport);

    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly ID3D12Device5 _device;
    private readonly ID3D12InfoQueue? _infoQueue;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList4 _commands;
    private readonly ID3D12Fence _fence;
    private readonly ID3D12QueryHeap _timestampQueryHeap;
    private readonly ID3D12Resource _timestampReadback;
    private readonly ulong _timestampFrequency;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly ID3D12DescriptorHeap _descriptorHeap;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12StateObject _pipeline;
    private readonly ID3D12StateObjectProperties _pipelineProperties;
    private readonly ID3D12Resource _shaderTable;
    private readonly ID3D12Resource _analyticSphereAabbBuffer;
    private readonly ID3D12Resource _analyticSphereBlas;
    private readonly ulong _analyticSphereBlasBytes;
    private readonly uint _shaderRecordSize;
    private readonly uint _shaderTableSectionSize;
    private readonly ShaderProgram _display;
    private readonly int _displayVertexArray;
    private readonly int _displayTexture;
    private readonly int _materialAtlasTexture;
    private readonly ProceduralMaterialAtlas _materialAtlas;
    private readonly bool _collectDiagnostics;

    private ID3D12Resource? _staticVertexBuffer;
    private ID3D12Resource? _dynamicVertexBuffer;
    private ID3D12Resource? _dynamicUpdateScratch;
    private ulong _dynamicUpdateScratchBytes;
    private ID3D12Resource? _instanceBuffer;
    private ID3D12Resource? _staticBlas;
    private ID3D12Resource? _dynamicBlas;
    private ID3D12Resource? _tlas;
    private ID3D12Resource? _output;
    private ID3D12Resource? _readback;
    private ID3D12Resource? _atlasColor;
    private ID3D12Resource? _atlasDetail;
    private readonly ID3D12Resource?[] _reflectionTailAtlases = new ID3D12Resource?[2];
    private readonly bool[] _reflectionTailAtlasReadable = new bool[2];
    private ID3D12Resource? _emissiveLightBuffer;
    private ID3D12Resource? _analyticSphereDataBuffer;
    private int _analyticSphereDataCapacity;
    private int _emissiveLightCapacity;
    private int _reflectionTailWriteIndex;
    private bool _reflectionTailHistoryValid;
    private bool _reflectionTailEnabled;
    private int _reflectionTailWidth;
    private int _reflectionTailHeight;
    private Vector3 _previousTailCameraPosition;
    private Vector3 _previousTailCameraForward = -Vector3.UnitZ;
    private Vector3 _previousTailCameraRight = Vector3.UnitX;
    private Vector3 _previousTailCameraUp = Vector3.UnitY;
    private float _previousTailTanHalfFov;
    private float _previousTailAspect = 1f;
    private PlacedSubresourceFootPrint _readbackFootprint;
    private byte[] _pixels = Array.Empty<byte>();
    private ulong _fenceValue;
    private int _width;
    private int _height;
    private int _viewportWidth;
    private int _viewportHeight;
    private float _renderScale;
    private bool _geometryDirty;
    private bool _physicsGeometryDirty;
    private long _lastAccelerationStructureBuildMs;
    private long _lastStaticGeometryChangeMs;
    private long _staticGeometryDirtySinceMs;
    private bool _forceImmediateStaticRebuild;
    private HashSet<ChunkCoordinate> _visibleChunks = new();
    private Vector3 _cullingCameraPosition;
    private Vector3 _cullingCameraForward = -Vector3.UnitZ;
    private Vector3 _cullingLightDirection = Vector3.UnitY;
    private float _cullingFieldOfView = 72f;
    private bool _retainGlobalRayGeometry;
    private bool _visibilityCullingReady;
    private bool _disposed;
    private int _staticVertexCount;
    private int _dynamicVertexCount;
    private ulong _staticBlasBytes;
    private ulong _dynamicBlasBytes;
    private ulong _tlasBytes;
    private int _emissiveLightCount;
    private int _lastLoadedChunkCount;
    private int _instanceCount;
    private int _lastAnalyticSphereCount;
    private bool _analyticSphereTopologyDirty;
    private readonly Dictionary<ulong, CachedGridGeometry> _gridGeometryCache = new();
    private readonly Dictionary<ChunkCoordinate, CachedStaticChunkGeometry> _staticChunkGeometryCache = new();
    private readonly HashSet<ChunkCoordinate> _dirtyStaticGeometryChunks = new();
    private readonly List<EmissiveLight> _staticEmissiveCandidates = new();
    private Task<StaticWorldGeometryBuild?>? _staticGeometryBuildTask;
    private CancellationTokenSource? _staticGeometryBuildCancellation;
    private int _staticGeometryVersion;

    public RenderBackendKind Kind => RenderBackendKind.HardwareRayTracing;
    public RenderBackendCapabilities Capabilities => RenderBackendCapabilities.RealTime |
        RenderBackendCapabilities.Reflections | RenderBackendCapabilities.Transmission;
    public string Name => "Direct3D 12 DXR V1";
    public int AccumulatedSamples => 1;
    public int MaterialAtlasTexture => _materialAtlasTexture;
    public int MaterialTextureResolution => ProceduralMaterialAtlas.TileSize;
    public string AdapterName { get; }
    public RaytracingTier RaytracingTier => _device.Options5.RaytracingTier;
    public int TracedTriangleCount => (_staticVertexCount + _dynamicVertexCount) / 3;
    public ulong AccelerationStructureMemoryBytes =>
        _staticBlasBytes + _dynamicBlasBytes + _analyticSphereBlasBytes + _tlasBytes;
    public int TracedAnalyticSphereCount => _world.AnalyticSpheres.Count;
    public int LastHitPixelCount { get; private set; }
    public int LastShadowRayPixelCount { get; private set; }
    public int LastShadowOccludedPixelCount { get; private set; }
    public int LastReflectionRayPixelCount { get; private set; }
    public int LastPhysicsGridHitPixelCount { get; private set; }
    public int LastTexturedHitPixelCount { get; private set; }
    public int LastWaxReflectionPixelCount { get; private set; }
    public float ViewCullingExpansion => CullingFrustumScale;
    public bool GlobalRayGeometryRetained => _retainGlobalRayGeometry;
    public int LastTransparentHitPixelCount { get; private set; }
    public int LastChromaticTransparentPixelCount { get; private set; }
    public int EmissiveLightCount => _emissiveLightCount;
    public int VisibleDxrChunkCount => _visibleChunks.Count;
    public int ShadowCasterDxrChunkCount { get; private set; }
    public int AccelerationStructureBuildCount { get; private set; }
    public int StaticAccelerationStructureBuildCount { get; private set; }
    public int DynamicAccelerationStructureBuildCount { get; private set; }
    public int TracedPhysicsGridCount { get; private set; }
    public int TracedPhysicsGridTriangleCount { get; private set; }
    public int TracedMicroVoxelTriangleCount { get; private set; }
    public int DynamicTopologyBuildCount { get; private set; }
    public int TlasUpdateCount { get; private set; }
    public int LastHardwareQuality { get; private set; }
    public double LastAccelerationStructureBuildMilliseconds { get; private set; }
    public double LastStaticAccelerationStructureBuildMilliseconds { get; private set; }
    public double LastDynamicAccelerationStructureBuildMilliseconds { get; private set; }
    public double MaximumAccelerationStructureBuildMilliseconds { get; private set; }
    public double AccelerationStructureBuildTotalMilliseconds { get; private set; }
    public double LastStaticGeometryBuildMilliseconds { get; private set; }
    public double MaximumStaticGeometryBuildMilliseconds { get; private set; }
    public int PendingStaticGeometryBuildCount => _staticGeometryBuildTask is null ? 0 : 1;
    public int ActualRealReflectionLayers { get; private set; }
    public int ApproximateReflectionLayers { get; private set; }
    public ulong ReflectionTailAtlasMemoryBytes { get; private set; }
    public double LastSynchronizedTraceMilliseconds { get; private set; }
    public double MaximumSynchronizedTraceMilliseconds { get; private set; }
    public double LastReflectionTailCpuMilliseconds { get; private set; }
    public double MaximumReflectionTailCpuMilliseconds { get; private set; }
    public double LastGpuTraceMilliseconds { get; private set; }
    public double MaximumGpuTraceMilliseconds { get; private set; }

    public static bool IsSupported(out string reason)
    {
        (bool supported, string result) = Support.Value;
        reason = result;
        return supported;
    }

    public DxrHardwareRenderer(VoxelWorld world, BlockRegistry blocks, int width, int height, float renderScale)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DXR V1 requires Windows.");
        if (!IsSupported(out string reason)) throw new NotSupportedException(reason);

        _world = world;
        _blocks = blocks;
        _collectDiagnostics = Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE") == "1";
        (_device, AdapterName) = CreateDevice();
        _infoQueue = _device.QueryInterfaceOrNull<ID3D12InfoQueue>();
        if (_device.Options5.RaytracingTier < RaytracingTier.Tier1_0)
            throw new NotSupportedException($"{AdapterName} does not expose D3D12 ray tracing tier 1.0.");

        _queue = _device.CreateCommandQueue(CommandListType.Direct);
        _allocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _commands = _device.CreateCommandList<ID3D12GraphicsCommandList4>(0, CommandListType.Direct, _allocator);
        _commands.Close();
        _fence = _device.CreateFence(0);
        _queue.GetTimestampFrequency(out _timestampFrequency).CheckError();
        _timestampQueryHeap = _device.CreateQueryHeap<ID3D12QueryHeap>(
            new QueryHeapDescription(QueryHeapType.Timestamp, 2, 0));
        _timestampReadback = _device.CreateCommittedResource(
            HeapType.Readback, ResourceDescription.Buffer(sizeof(ulong) * 2), ResourceStates.CopyDest);
        Console.WriteLine($"DXR device ready: {AdapterName}, tier {_device.Options5.RaytracingTier}; queue/list/fence created.");

        _descriptorHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            DescriptorCount,
            DescriptorHeapFlags.ShaderVisible));
        _rootSignature = CreateRootSignature();
        Console.WriteLine("DXR root signature created.");
        (_pipeline, _pipelineProperties) = CreatePipeline();
        Console.WriteLine("DXR ray generation/miss/closest-hit pipeline created.");
        (_shaderTable, _shaderRecordSize, _shaderTableSectionSize) = CreateShaderTable();
        Console.WriteLine("DXR shader table created.");
        (_analyticSphereAabbBuffer, _analyticSphereBlas, _analyticSphereBlasBytes) =
            CreateAnalyticSphereBottomLevel();
        Console.WriteLine("DXR analytic sphere procedural BLAS created.");

        string shaderRoot = Path.Combine(RuntimePaths.OutputRoot, "Shaders");
        _display = new ShaderProgram(
            (ShaderType.VertexShader, File.ReadAllText(Path.Combine(shaderRoot, "fullscreen.vert.glsl"))),
            (ShaderType.FragmentShader, File.ReadAllText(Path.Combine(shaderRoot, "tonemap.frag.glsl"))));
        _displayVertexArray = GL.GenVertexArray();
        _displayTexture = GL.GenTexture();

        _materialAtlas = new ProceduralMaterialAtlas(_blocks);
        _atlasColor = CreateAtlasTexture(
            ProceduralMaterialAtlas.Width, ProceduralMaterialAtlas.Height,
            DxgiFormat.R8G8B8A8_UNorm, 4, _materialAtlas.ColorPixels);
        _atlasDetail = CreateAtlasTexture(
            ProceduralMaterialAtlas.Width, ProceduralMaterialAtlas.Height,
            DxgiFormat.R8G8_UNorm, 2, _materialAtlas.DetailPixels);
        _materialAtlasTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _materialAtlasTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            ProceduralMaterialAtlas.Width, ProceduralMaterialAtlas.Height, 0,
            GlPixelFormat.Rgba, PixelType.UnsignedByte, _materialAtlas.ColorPixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        MarkStaticGeometryDirty(forceImmediate: true);
        Resize(width, height, renderScale);
        _world.VoxelChanged += OnVoxelChanged;
        _world.FluidLevelChanged += OnFluidLevelChanged;
        _world.WorldReloaded += OnWorldReloaded;
        _world.MicroVoxelCellChanged += OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded += OnMicroVoxelsReloaded;
        _world.ChunkGenerated += OnChunkGenerated;
        _world.PhysicsGrids.Changed += OnPhysicsGridsChanged;
        _world.AnalyticSpheres.Changed += OnAnalyticSpheresChanged;
        Console.WriteLine($"DXR initialized: {AdapterName}, tier {RaytracingTier}, BLAS triangles {TracedTriangleCount:N0}.");
    }

    public void Resize(int width, int height, float renderScale)
    {
        _viewportWidth = Math.Max(width, 1);
        _viewportHeight = Math.Max(height, 1);
        renderScale = Math.Clamp(renderScale, 0.35f, 1f);
        int traceWidth = Math.Max(1, (int)MathF.Round(_viewportWidth * renderScale));
        int traceHeight = Math.Max(1, (int)MathF.Round(_viewportHeight * renderScale));
        if (traceWidth == _width && traceHeight == _height && MathF.Abs(renderScale - _renderScale) < 0.001f) return;
        WaitIdle();
        _width = traceWidth;
        _height = traceHeight;
        _renderScale = renderScale;
        CreateOutputResources();
    }

    public void ResetAccumulation() => _reflectionTailHistoryValid = false;

    public void Render(Vector3 cameraPosition, Camera camera, VoxelHit? selection, float timeOfDay,
        RenderQualitySettings quality, float exposure)
    {
        long tailCpuStarted = Stopwatch.GetTimestamp();
        EnsureReflectionTailResources(quality.InfiniteMirrorQuality != InfiniteMirrorQuality.Off);
        LastReflectionTailCpuMilliseconds = quality.InfiniteMirrorQuality == InfiniteMirrorQuality.Off
            ? 0
            : ElapsedMilliseconds(tailCpuStarted);
        MaximumReflectionTailCpuMilliseconds = Math.Max(
            MaximumReflectionTailCpuMilliseconds, LastReflectionTailCpuMilliseconds);
        UpdateVisibleChunkMask(cameraPosition, camera, timeOfDay, quality);
        long now = Environment.TickCount64;
        bool staticGeometryReady = _forceImmediateStaticRebuild || _tlas is null ||
            (now - _lastStaticGeometryChangeMs >= 160 &&
             (_world.PendingChunkGenerationCount == 0 || now - _staticGeometryDirtySinceMs >= 1500));
        if (_geometryDirty && staticGeometryReady)
            ProcessStaticGeometryBuild();
        else if (_physicsGeometryDirty && now - _lastAccelerationStructureBuildMs >=
                 (_world.AnalyticSpheres.Count > 0 ? 16 : 120))
            RebuildDynamicAccelerationStructures();
        Trace(cameraPosition, camera, selection, timeOfDay, quality);

        GL.Disable(EnableCap.DepthTest);
        GL.Viewport(0, 0, _viewportWidth, _viewportHeight);
        GL.BindTexture(TextureTarget.Texture2D, _displayTexture);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, _width, _height,
            GlPixelFormat.Rgba, PixelType.UnsignedByte, _pixels);
        _display.Use();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _displayTexture);
        GL.Uniform1(_display.Uniform("uImage"), 0);
        GL.Uniform2(_display.Uniform("uViewport"), (float)_viewportWidth, (float)_viewportHeight);
        GL.Uniform1(_display.Uniform("uExposure"), exposure);
        GL.BindVertexArray(_displayVertexArray);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private void UpdateVisibleChunkMask(
        Vector3 cameraPosition,
        Camera camera,
        float timeOfDay,
        RenderQualitySettings quality)
    {
        IReadOnlyList<ChunkCoordinate> loadedChunks = _world.SnapshotLoadedChunks();
        Vector3 lightDirection = PrimaryLighting(timeOfDay).Direction;
        bool retainGlobalRayGeometry = quality.HardwareRayTracingQuality > 0;
        bool positionStable = Vector3.DistanceSquared(cameraPosition, _cullingCameraPosition) < 16f * 16f;
        bool viewStable = retainGlobalRayGeometry ||
            Vector3.Dot(Vector3.Normalize(camera.Forward), Vector3.Normalize(_cullingCameraForward)) >
                MathF.Cos(5f * MathF.PI / 180f);
        bool lightStable = retainGlobalRayGeometry ||
            Vector3.Dot(lightDirection, _cullingLightDirection) > MathF.Cos(2f * MathF.PI / 180f);
        if (_visibilityCullingReady && loadedChunks.Count == _lastLoadedChunkCount &&
            retainGlobalRayGeometry == _retainGlobalRayGeometry &&
            positionStable && viewStable && lightStable)
            return;

        var visibleChunks = new HashSet<ChunkCoordinate>();
        int shadowCasterCount = 0;
        foreach (ChunkCoordinate chunk in loadedChunks)
        {
            if (!_world.TryGetChunkVerticalBounds(chunk, out int minimumY, out int maximumY)) continue;
            float halfHeight = Math.Max(1f, (maximumY - minimumY) * 0.5f);
            Vector3 center = new(
                chunk.MinimumX + ChunkCoordinate.Size * 0.5f,
                (minimumY + maximumY) * 0.5f,
                chunk.MinimumZ + ChunkCoordinate.Size * 0.5f);
            float radius = MathF.Sqrt(ChunkCoordinate.Size * ChunkCoordinate.Size * 0.5f +
                                      halfHeight * halfHeight);
            bool directlyVisible = IsVisibleForHardwareRt(
                center, cameraPosition, camera.Forward, camera.FieldOfViewDegrees, radius);
            bool shadowCaster = !directlyVisible && IsShadowCasterNeededForHardwareRt(
                center, radius, lightDirection, cameraPosition, camera.Forward, camera.FieldOfViewDegrees);
            Vector2 horizontalOffset = new(center.X - cameraPosition.X, center.Z - cameraPosition.Z);
            // Quality modes retain every ray-reachable loaded chunk. This is independent
            // of camera direction, so merely rotating the view never rebuilds BLAS/TLAS.
            bool secondaryRayNeighborhood = retainGlobalRayGeometry && horizontalOffset.LengthSquared() <= 235f * 235f;
            if (directlyVisible || shadowCaster || secondaryRayNeighborhood) visibleChunks.Add(chunk);
            if (!directlyVisible && (shadowCaster || secondaryRayNeighborhood)) shadowCasterCount++;
        }
        ShadowCasterDxrChunkCount = shadowCasterCount;
        _cullingLightDirection = lightDirection;
        _cullingCameraPosition = cameraPosition;
        _cullingCameraForward = camera.Forward;
        _cullingFieldOfView = camera.FieldOfViewDegrees;
        _lastLoadedChunkCount = loadedChunks.Count;
        bool maskChanged = !_visibleChunks.SetEquals(visibleChunks);
        _visibleChunks = visibleChunks;
        _retainGlobalRayGeometry = retainGlobalRayGeometry;
        _visibilityCullingReady = true;
        if (maskChanged)
        {
            MarkStaticGeometryDirty();
        }
    }

    private static bool IsVisibleForHardwareRt(
        Vector3 center, Vector3 cameraPosition, Vector3 cameraForward, float fieldOfView, float radius)
    {
        Vector3 offset = center - cameraPosition;
        float distance = offset.Length();
        if (distance <= radius + 2f) return true;
        if (distance > 235f) return false;
        float halfAngle = Math.Clamp(fieldOfView * 0.5f * CullingFrustumScale + 34f +
            MathF.Atan(radius / distance) * 180f / MathF.PI,
            0f, 89f);
        return Vector3.Dot(offset / distance, cameraForward) >= MathF.Cos(halfAngle * MathF.PI / 180f);
    }

    private static bool IsShadowCasterNeededForHardwareRt(
        Vector3 casterCenter,
        float radius,
        Vector3 lightDirection,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        float fieldOfView)
    {
        ReadOnlySpan<float> distances = stackalloc float[] { 12f, 24f, 40f, 64f };
        foreach (float distance in distances)
        {
            Vector3 projectedReceiver = casterCenter - lightDirection * distance;
            if (IsVisibleForHardwareRt(projectedReceiver, cameraPosition, cameraForward,
                    fieldOfView + 8f, radius + distance * 0.08f))
                return true;
        }
        return false;
    }

    private static (Vector3 Direction, float Daylight) PrimaryLighting(float timeOfDay)
    {
        float angle = (timeOfDay - 6f) * (2f * MathF.PI / 24f);
        Vector3 sun = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.28f));
        float daylight = Math.Clamp((sun.Y + 0.12f) / 0.30f, 0f, 1f);
        daylight = daylight * daylight * (3f - 2f * daylight);
        return (daylight > 0.08f ? sun : -sun, daylight);
    }

    public MaterialTileUv GetMaterialTileUv(ushort materialId, MaterialFace face) =>
        _materialAtlas.GetTileUv(materialId, face);

    private static (bool Supported, string Reason) ProbeSupport()
    {
        if (!OperatingSystem.IsWindows()) return (false, "DXR V1 requires Windows.");
        try
        {
#if DEBUG
            if (D3D12GetDebugInterface(out ID3D12Debug? debug).Success)
            {
                debug!.EnableDebugLayer();
                debug.Dispose();
            }
#endif
            using ID3D12Device5 device = CreateDevice().Device;
            RaytracingTier tier = device.Options5.RaytracingTier;
            return tier >= RaytracingTier.Tier1_0
                ? (true, $"D3D12 DXR {tier} is supported.")
                : (false, $"D3D12 device exists, but ray tracing tier is {tier}.");
        }
        catch (Exception exception)
        {
            return (false, $"DXR device detection failed: {exception.Message}");
        }
    }

    private static (ID3D12Device5 Device, string AdapterName) CreateDevice()
    {
        using IDXGIFactory4 factory = CreateDXGIFactory2<IDXGIFactory4>(false);
        using IDXGIFactory6? factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        if (factory6 is not null)
        {
            for (int index = 0; factory6.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance,
                     out IDXGIAdapter1? adapter).Success; index++)
            {
                using (adapter)
                {
                    AdapterDescription1 description = adapter!.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0) continue;
                    if (D3D12CreateDevice(adapter, FeatureLevel.Level_12_0, out ID3D12Device5? device).Success)
                        return (device!, description.Description);
                }
            }
        }
        throw new NotSupportedException("No high-performance Direct3D 12 adapter could be created.");
    }

    private ID3D12RootSignature CreateRootSignature()
    {
        var description = new RootSignatureDescription1(RootSignatureFlags.None)
        {
            Parameters = new[]
            {
                new RootParameter1(new RootDescriptorTable1(
                    new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 0, 0, 0),
                    new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 1),
                    new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, 2, 0, 2),
                    new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 1, 0, 4,
                        DescriptorRangeFlags.DescriptorsVolatile | DescriptorRangeFlags.DataVolatile),
                    new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 6, 0, 5,
                        DescriptorRangeFlags.DescriptorsVolatile | DescriptorRangeFlags.DataVolatile)), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All),
                new RootParameter1(new RootConstants(0, 0, CameraConstantCount), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All),
            },
        };
        return _device.CreateRootSignature(description);
    }

    private (ID3D12StateObject Pipeline, ID3D12StateObjectProperties Properties) CreatePipeline()
    {
        string source = File.ReadAllText(Path.Combine(RuntimePaths.OutputRoot, "Shaders", "hardware_rt.hlsl"));
        using IDxcResult result = DxcCompiler.Compile(DxcShaderStage.Library, source, string.Empty,
            new DxcCompilerOptions { ShaderModel = DxcShaderModel.Model6_3 }, "hardware_rt.hlsl");
        if (result.GetStatus().Failure) throw new InvalidOperationException($"DXR shader compilation failed: {result.GetErrors()}");
        ReadOnlyMemory<byte> library = result.GetObjectBytecodeMemory();

        var dxilLibrary = new StateSubObject(new DxilLibraryDescription(library,
            new ExportDescription("RayGen"), new ExportDescription("Miss"),
            new ExportDescription("ShadowMiss"), new ExportDescription("AnyHit"),
            new ExportDescription("ClosestHit"), new ExportDescription("SphereIntersection"),
            new ExportDescription("SphereAnyHit"), new ExportDescription("SphereClosestHit")));
        var hitGroup = new StateSubObject(new HitGroupDescription(
            "HitGroup", HitGroupType.Triangles,
            anyHitShaderImport: "AnyHit", closestHitShaderImport: "ClosestHit"));
        var sphereHitGroup = new StateSubObject(new HitGroupDescription(
            "SphereHitGroup", HitGroupType.ProceduralPrimitive,
            anyHitShaderImport: "SphereAnyHit", closestHitShaderImport: "SphereClosestHit",
            intersectionShaderImport: "SphereIntersection"));
        var shaderConfig = new StateSubObject(new RaytracingShaderConfig(16 * sizeof(float), 3 * sizeof(float)));
        var shaderAssociation = new StateSubObject(new SubObjectToExportsAssociation(
            shaderConfig, "RayGen", "Miss", "ShadowMiss", "AnyHit", "ClosestHit",
            "SphereIntersection", "SphereAnyHit", "SphereClosestHit"));
        // Primary ray + 29 reflected rays + the deepest shadow ray. D3D12's hard
        // maximum declaration is 31, so this must never be raised without changing
        // to a non-recursive/wavefront DXR architecture.
        var pipelineConfig = new StateSubObject(new RaytracingPipelineConfig(MaximumReflectionBounces + 2));
        var globalRoot = new StateSubObject(new GlobalRootSignature(_rootSignature));
        ID3D12StateObject pipeline;
        try
        {
            pipeline = _device.CreateStateObject(new StateObjectDescription(
                StateObjectType.RaytracingPipeline,
                dxilLibrary, hitGroup, sphereHitGroup, shaderConfig, shaderAssociation, pipelineConfig, globalRoot));
        }
        catch
        {
            WriteDebugMessages();
            throw;
        }
        return (pipeline, pipeline.QueryInterface<ID3D12StateObjectProperties>());
    }

    private (ID3D12Resource Table, uint RecordSize, uint SectionSize) CreateShaderTable()
    {
        uint recordSize = Align(ShaderIdentifierSizeInBytes, RaytracingShaderRecordByteAlignment);
        uint sectionSize = Align(recordSize * 2, RaytracingShaderTableByteAlignment);
        ID3D12Resource table = _device.CreateCommittedResource(HeapType.Upload,
            ResourceDescription.Buffer(sectionSize * 3UL), ResourceStates.GenericRead);
        byte* destination;
        table.Map(0, &destination).CheckError();
        CopyShaderIdentifier(destination + sectionSize * 0, "RayGen");
        CopyShaderIdentifier(destination + sectionSize * 1, "HitGroup");
        CopyShaderIdentifier(destination + sectionSize * 1 + recordSize, "SphereHitGroup");
        CopyShaderIdentifier(destination + sectionSize * 2, "Miss");
        CopyShaderIdentifier(destination + sectionSize * 2 + recordSize, "ShadowMiss");
        table.Unmap(0);
        return (table, recordSize, sectionSize);
    }

    private void CopyShaderIdentifier(byte* destination, string exportName) =>
        Unsafe.CopyBlockUnaligned(destination, (void*)_pipelineProperties.GetShaderIdentifier(exportName), ShaderIdentifierSizeInBytes);

    private (ID3D12Resource AabbBuffer, ID3D12Resource Blas, ulong Bytes)
        CreateAnalyticSphereBottomLevel()
    {
        int stride = Marshal.SizeOf<AnalyticSphereAabb>();
        ID3D12Resource aabbBuffer = _device.CreateCommittedResource(
            HeapType.Upload, ResourceDescription.Buffer((ulong)stride), ResourceStates.GenericRead);
        aabbBuffer.Map<AnalyticSphereAabb>(0, 1)[0] = new AnalyticSphereAabb(
            -1f, -1f, -1f, 1f, 1f, 1f);
        aabbBuffer.Unmap(0);

        BuildRaytracingAccelerationStructureInputs inputs =
            CreateAnalyticSphereBottomLevelInputs(aabbBuffer, stride);
        RaytracingAccelerationStructurePrebuildInfo info =
            _device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
        ID3D12Resource blas = CreateAccelerationStructure(info.ResultDataMaxSizeInBytes);
        using ID3D12Resource scratch = _device.CreateCommittedResource(
            HeapType.Default,
            ResourceDescription.Buffer(info.ScratchDataSizeInBytes, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.Common);
        BeginCommands();
        _commands.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
        {
            Inputs = inputs,
            ScratchAccelerationStructureData = scratch.GPUVirtualAddress,
            DestinationAccelerationStructureData = blas.GPUVirtualAddress,
        });
        _commands.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(blas)));
        ExecuteCommands();
        return (aabbBuffer, blas, info.ResultDataMaxSizeInBytes);
    }

    private void ProcessStaticGeometryBuild()
    {
        if (_staticGeometryBuildTask is null)
        {
            ScheduleStaticGeometryBuild();
            return;
        }
        if (!_staticGeometryBuildTask.IsCompleted) return;

        StaticWorldGeometryBuild? result;
        try
        {
            result = _staticGeometryBuildTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _staticGeometryBuildTask = null;
            _staticGeometryBuildCancellation?.Dispose();
            _staticGeometryBuildCancellation = null;
        }
        // Cancellation is routine while streamed Chunks are arriving. The worker
        // returns no result instead of throwing so Visual Studio's first-chance
        // exception setting cannot interrupt normal gameplay.
        if (result is null) return;
        if (result.Version != _staticGeometryVersion) return;

        foreach ((ChunkCoordinate chunk, CachedStaticChunkGeometry geometry) in result.UpdatedChunks)
        {
            _staticChunkGeometryCache[chunk] = geometry;
            _dirtyStaticGeometryChunks.Remove(chunk);
        }
        _staticEmissiveCandidates.Clear();
        _staticEmissiveCandidates.AddRange(result.EmissiveLights);
        TracedMicroVoxelTriangleCount = result.MicroVoxelTriangleCount;
        LastStaticGeometryBuildMilliseconds = result.BuildMilliseconds;
        MaximumStaticGeometryBuildMilliseconds = Math.Max(
            MaximumStaticGeometryBuildMilliseconds,
            result.BuildMilliseconds);
        WorldVertex[] dynamicVertices = BuildPhysicsGridVertices();
        RebuildAccelerationStructures(result.Vertices, dynamicVertices, preserveStatic: false);
    }

    private void ScheduleStaticGeometryBuild()
    {
        BlockDefinition[] definitions = _blocks.CreateLookupSnapshot();
        IEnumerable<ChunkCoordinate> sourceChunks = _visibilityCullingReady
            ? _visibleChunks
            : _world.SnapshotLoadedChunks();
        ChunkCoordinate[] chunks = sourceChunks
            .OrderBy(chunk => chunk.Z)
            .ThenBy(chunk => chunk.X)
            .ToArray();
        var inputs = new List<StaticChunkBuildInput>(chunks.Length);
        foreach (ChunkCoordinate chunk in chunks)
        {
            if (!_staticChunkGeometryCache.TryGetValue(chunk, out CachedStaticChunkGeometry? cached) ||
                _dirtyStaticGeometryChunks.Contains(chunk))
            {
                inputs.Add(new StaticChunkBuildInput(
                    chunk,
                    null,
                    _world.CreateChunkMeshingSnapshot(chunk),
                    _world.SnapshotChunk(chunk)));
            }
            else
            {
                inputs.Add(new StaticChunkBuildInput(chunk, cached, null, null));
            }
        }
        int version = _staticGeometryVersion;
        _staticGeometryBuildCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _staticGeometryBuildCancellation.Token;
        _staticGeometryBuildTask = Task.Factory.StartNew(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                return BuildStaticWorldGeometry(version, inputs, definitions, cancellation);
            },
            // Do not associate the cooperative token with Task scheduling. If the
            // token is canceled before the delegate starts, TPL would manufacture a
            // Canceled task whose GetResult throws OperationCanceledException.
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RebuildDynamicAccelerationStructures()
    {
        WorldVertex[] dynamicVertices = BuildPhysicsGridVertices();
        if (!_analyticSphereTopologyDirty && _tlas is not null &&
            dynamicVertices.Length == _dynamicVertexCount &&
            dynamicVertices.Length == 0)
        {
            RefitTopLevelForAnalyticSpheres();
            return;
        }
        if (!_analyticSphereTopologyDirty && _dynamicBlas is not null && _dynamicVertexBuffer is not null &&
            dynamicVertices.Length > 0 && dynamicVertices.Length == _dynamicVertexCount)
        {
            RefitDynamicAccelerationStructures(dynamicVertices);
            return;
        }
        RebuildAccelerationStructures(Array.Empty<WorldVertex>(), dynamicVertices, preserveStatic: true);
    }

    private void RebuildAccelerationStructures(
        WorldVertex[] staticVertices,
        WorldVertex[] dynamicVertices,
        bool preserveStatic)
    {
        long buildStarted = Stopwatch.GetTimestamp();
        // Render/AS submissions are synchronously fenced by ExecuteCommands before
        // returning. A second empty signal/wait here only adds a scheduler round trip.
        if (preserveStatic) DisposeDynamicGeometryResources();
        else DisposeGeometryResources();

        int stride = Marshal.SizeOf<WorldVertex>();
        BuildRaytracingAccelerationStructureInputs? staticInputs = null;
        RaytracingAccelerationStructurePrebuildInfo? staticInfo = null;
        if (!preserveStatic)
        {
            _staticVertexCount = staticVertices.Length;
            if (staticVertices.Length == 0 && dynamicVertices.Length == 0 && _world.AnalyticSpheres.Count == 0)
                throw new InvalidOperationException("DXR world contains no traceable geometry.");
            _staticVertexBuffer = CreateVertexBuffer(
                staticVertices.Length > 0 ? staticVertices : new WorldVertex[1], stride);
            if (staticVertices.Length > 0)
            {
                staticInputs = CreateBottomLevelInputs(
                    _staticVertexBuffer, staticVertices.Length, stride, allowUpdate: false);
                staticInfo = _device.GetRaytracingAccelerationStructurePrebuildInfo(staticInputs);
                _staticBlasBytes = staticInfo.Value.ResultDataMaxSizeInBytes;
                _staticBlas = CreateAccelerationStructure(staticInfo.Value.ResultDataMaxSizeInBytes);
            }
            else
            {
                _staticBlasBytes = 0;
            }
        }
        if (_staticVertexBuffer is null)
            throw new InvalidOperationException("DXR static vertex storage was not initialized.");

        _dynamicVertexCount = dynamicVertices.Length;
        BuildRaytracingAccelerationStructureInputs? dynamicInputs = null;
        RaytracingAccelerationStructurePrebuildInfo? dynamicInfo = null;
        if (dynamicVertices.Length > 0)
        {
            _dynamicVertexBuffer = CreateVertexBuffer(dynamicVertices, stride);
            dynamicInputs = CreateBottomLevelInputs(
                _dynamicVertexBuffer, dynamicVertices.Length, stride, allowUpdate: true);
            dynamicInfo = _device.GetRaytracingAccelerationStructurePrebuildInfo(dynamicInputs);
            _dynamicBlasBytes = dynamicInfo.Value.ResultDataMaxSizeInBytes;
            _dynamicBlas = CreateAccelerationStructure(dynamicInfo.Value.ResultDataMaxSizeInBytes);
        }
        else
        {
            _dynamicBlasBytes = 0;
        }

        List<RaytracingInstanceDescription> instances = CreateSceneInstances();
        if (instances.Count == 0)
            throw new InvalidOperationException("DXR acceleration structure has no instances.");
        _instanceCount = instances.Count;
        _lastAnalyticSphereCount = _world.AnalyticSpheres.Count;
        _analyticSphereTopologyDirty = false;
        UpdateAnalyticSphereDataBuffer();

        var topInputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace |
                    RaytracingAccelerationStructureBuildFlags.AllowUpdate,
            Layout = ElementsLayout.Array,
            DescriptorsCount = instances.Count,
        };
        RaytracingAccelerationStructurePrebuildInfo topInfo =
            _device.GetRaytracingAccelerationStructurePrebuildInfo(topInputs);
        _tlasBytes = topInfo.ResultDataMaxSizeInBytes;
        _tlas = CreateAccelerationStructure(topInfo.ResultDataMaxSizeInBytes);

        _instanceBuffer = _device.CreateCommittedResource(HeapType.Upload,
            ResourceDescription.Buffer((ulong)(sizeof(RaytracingInstanceDescription) * instances.Count)),
            ResourceStates.GenericRead);
        RaytracingInstanceDescription* mappedInstances =
            _instanceBuffer.Map<RaytracingInstanceDescription>(0);
        for (int index = 0; index < instances.Count; index++) mappedInstances[index] = instances[index];
        _instanceBuffer.Unmap(0);
        topInputs.InstanceDescriptions = _instanceBuffer.GPUVirtualAddress;

        ulong scratchSize = topInfo.ScratchDataSizeInBytes;
        if (staticInfo.HasValue) scratchSize = Math.Max(scratchSize, staticInfo.Value.ScratchDataSizeInBytes);
        if (dynamicInfo.HasValue) scratchSize = Math.Max(scratchSize, dynamicInfo.Value.ScratchDataSizeInBytes);
        using ID3D12Resource scratch = _device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer(scratchSize, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);

        BeginCommands();
        if (staticInputs is not null && _staticBlas is not null)
        {
            _commands.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
            {
                Inputs = staticInputs,
                ScratchAccelerationStructureData = scratch.GPUVirtualAddress,
                DestinationAccelerationStructureData = _staticBlas!.GPUVirtualAddress,
            });
            _commands.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_staticBlas)));
        }
        if (dynamicInputs is not null && _dynamicBlas is not null)
        {
            _commands.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
            {
                Inputs = dynamicInputs,
                ScratchAccelerationStructureData = scratch.GPUVirtualAddress,
                DestinationAccelerationStructureData = _dynamicBlas!.GPUVirtualAddress,
            });
            _commands.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_dynamicBlas)));
        }
        _commands.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
        {
            Inputs = topInputs,
            ScratchAccelerationStructureData = scratch.GPUVirtualAddress,
            DestinationAccelerationStructureData = _tlas.GPUVirtualAddress,
        });
        _commands.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_tlas)));
        ExecuteCommands();

        CreateEmissiveLightBuffer();
        UpdateDescriptors();
        if (!preserveStatic)
        {
            _geometryDirty = false;
            _forceImmediateStaticRebuild = false;
            _staticGeometryDirtySinceMs = 0;
        }
        _physicsGeometryDirty = false;
        _lastAccelerationStructureBuildMs = Environment.TickCount64;
        double elapsed = (Stopwatch.GetTimestamp() - buildStarted) * 1000.0 / Stopwatch.Frequency;
        LastAccelerationStructureBuildMilliseconds = elapsed;
        RecordAccelerationStructureTiming(elapsed);
        if (preserveStatic)
        {
            LastDynamicAccelerationStructureBuildMilliseconds = elapsed;
            DynamicAccelerationStructureBuildCount++;
        }
        else
        {
            LastStaticAccelerationStructureBuildMilliseconds = elapsed;
            StaticAccelerationStructureBuildCount++;
        }
        AccelerationStructureBuildCount++;
    }

    private void RefitDynamicAccelerationStructures(WorldVertex[] dynamicVertices)
    {
        long buildStarted = Stopwatch.GetTimestamp();
        ID3D12Resource dynamicBlas = _dynamicBlas!;
        // The previous frame's trace was already fenced by ExecuteCommands.
        dynamicVertices.AsSpan().CopyTo(_dynamicVertexBuffer!.Map<WorldVertex>(0, dynamicVertices.Length));
        _dynamicVertexBuffer.Unmap(0);

        if (_instanceBuffer is null || _tlas is null)
            throw new InvalidOperationException("DXR TLAS update resources were not initialized.");
        int stride = Marshal.SizeOf<WorldVertex>();
        BuildRaytracingAccelerationStructureInputs dynamicInputs =
            CreateBottomLevelInputs(_dynamicVertexBuffer, dynamicVertices.Length, stride, allowUpdate: true);
        dynamicInputs.Flags |= RaytracingAccelerationStructureBuildFlags.PerformUpdate;
        RaytracingAccelerationStructurePrebuildInfo dynamicInfo =
            _device.GetRaytracingAccelerationStructurePrebuildInfo(dynamicInputs);

        int instanceCount = _staticBlas is null ? 1 : 2;
        List<RaytracingInstanceDescription> instances = CreateSceneInstances();
        if (instances.Count != _instanceCount)
            throw new InvalidOperationException("DXR instance topology changed during a TLAS refit.");
        RaytracingInstanceDescription* mappedInstances =
            _instanceBuffer.Map<RaytracingInstanceDescription>(0);
        for (int index = 0; index < instances.Count; index++) mappedInstances[index] = instances[index];
        _instanceBuffer.Unmap(0);
        UpdateAnalyticSphereDataBuffer();
        instanceCount = instances.Count;
        var topInputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace |
                    RaytracingAccelerationStructureBuildFlags.AllowUpdate |
                    RaytracingAccelerationStructureBuildFlags.PerformUpdate,
            Layout = ElementsLayout.Array,
            DescriptorsCount = instanceCount,
            InstanceDescriptions = _instanceBuffer.GPUVirtualAddress,
        };
        RaytracingAccelerationStructurePrebuildInfo topInfo =
            _device.GetRaytracingAccelerationStructurePrebuildInfo(topInputs);

        ulong scratchSize = Math.Max(topInfo.UpdateScratchDataSizeInBytes,
            dynamicInfo.UpdateScratchDataSizeInBytes);
        if (_dynamicUpdateScratch is null || _dynamicUpdateScratchBytes < scratchSize)
        {
            _dynamicUpdateScratch?.Dispose();
            _dynamicUpdateScratchBytes = scratchSize;
            _dynamicUpdateScratch = _device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Buffer(scratchSize, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.Common);
        }
        ID3D12Resource scratch = _dynamicUpdateScratch;

        BeginCommands();
        _commands.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
        {
            Inputs = dynamicInputs,
            ScratchAccelerationStructureData = scratch.GPUVirtualAddress,
            SourceAccelerationStructureData = dynamicBlas.GPUVirtualAddress,
            DestinationAccelerationStructureData = dynamicBlas.GPUVirtualAddress,
        });
        _commands.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(dynamicBlas)));
        _commands.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
        {
            Inputs = topInputs,
            ScratchAccelerationStructureData = scratch.GPUVirtualAddress,
            SourceAccelerationStructureData = _tlas.GPUVirtualAddress,
            DestinationAccelerationStructureData = _tlas.GPUVirtualAddress,
        });
        _commands.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_tlas)));
        ExecuteCommands();

        CreateEmissiveLightBuffer();
        UpdateDescriptors();
        _physicsGeometryDirty = false;
        _lastAccelerationStructureBuildMs = Environment.TickCount64;
        double elapsed = (Stopwatch.GetTimestamp() - buildStarted) * 1000.0 / Stopwatch.Frequency;
        LastAccelerationStructureBuildMilliseconds = elapsed;
        RecordAccelerationStructureTiming(elapsed);
        LastDynamicAccelerationStructureBuildMilliseconds = elapsed;
        DynamicAccelerationStructureBuildCount++;
        TlasUpdateCount++;
        AccelerationStructureBuildCount++;
    }

    private void RefitTopLevelForAnalyticSpheres()
    {
        if (_instanceBuffer is null || _tlas is null) return;
        long buildStarted = Stopwatch.GetTimestamp();
        List<RaytracingInstanceDescription> instances = CreateSceneInstances();
        if (instances.Count != _instanceCount)
        {
            _analyticSphereTopologyDirty = true;
            RebuildAccelerationStructures(
                Array.Empty<WorldVertex>(), Array.Empty<WorldVertex>(), preserveStatic: true);
            return;
        }
        RaytracingInstanceDescription* mappedInstances =
            _instanceBuffer.Map<RaytracingInstanceDescription>(0);
        for (int index = 0; index < instances.Count; index++) mappedInstances[index] = instances[index];
        _instanceBuffer.Unmap(0);
        UpdateAnalyticSphereDataBuffer();

        var topInputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace |
                    RaytracingAccelerationStructureBuildFlags.AllowUpdate |
                    RaytracingAccelerationStructureBuildFlags.PerformUpdate,
            Layout = ElementsLayout.Array,
            DescriptorsCount = instances.Count,
            InstanceDescriptions = _instanceBuffer.GPUVirtualAddress,
        };
        RaytracingAccelerationStructurePrebuildInfo topInfo =
            _device.GetRaytracingAccelerationStructurePrebuildInfo(topInputs);
        ulong scratchSize = topInfo.UpdateScratchDataSizeInBytes;
        if (_dynamicUpdateScratch is null || _dynamicUpdateScratchBytes < scratchSize)
        {
            _dynamicUpdateScratch?.Dispose();
            _dynamicUpdateScratchBytes = scratchSize;
            _dynamicUpdateScratch = _device.CreateCommittedResource(
                HeapType.Default,
                ResourceDescription.Buffer(scratchSize, ResourceFlags.AllowUnorderedAccess),
                ResourceStates.Common);
        }

        BeginCommands();
        _commands.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription
        {
            Inputs = topInputs,
            ScratchAccelerationStructureData = _dynamicUpdateScratch.GPUVirtualAddress,
            SourceAccelerationStructureData = _tlas.GPUVirtualAddress,
            DestinationAccelerationStructureData = _tlas.GPUVirtualAddress,
        });
        _commands.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(_tlas)));
        ExecuteCommands();
        _physicsGeometryDirty = false;
        _lastAccelerationStructureBuildMs = Environment.TickCount64;
        double elapsed = ElapsedMilliseconds(buildStarted);
        LastAccelerationStructureBuildMilliseconds = elapsed;
        LastDynamicAccelerationStructureBuildMilliseconds = elapsed;
        RecordAccelerationStructureTiming(elapsed);
        DynamicAccelerationStructureBuildCount++;
        TlasUpdateCount++;
        AccelerationStructureBuildCount++;
    }

    public void ResetPerformanceCounters()
    {
        AccelerationStructureBuildCount = 0;
        StaticAccelerationStructureBuildCount = 0;
        DynamicAccelerationStructureBuildCount = 0;
        TlasUpdateCount = 0;
        LastAccelerationStructureBuildMilliseconds = 0;
        LastStaticAccelerationStructureBuildMilliseconds = 0;
        LastDynamicAccelerationStructureBuildMilliseconds = 0;
        MaximumAccelerationStructureBuildMilliseconds = 0;
        AccelerationStructureBuildTotalMilliseconds = 0;
        LastStaticGeometryBuildMilliseconds = 0;
        MaximumStaticGeometryBuildMilliseconds = 0;
    }

    private void RecordAccelerationStructureTiming(double milliseconds)
    {
        AccelerationStructureBuildTotalMilliseconds += milliseconds;
        MaximumAccelerationStructureBuildMilliseconds = Math.Max(
            MaximumAccelerationStructureBuildMilliseconds, milliseconds);
    }

    private ID3D12Resource CreateVertexBuffer(WorldVertex[] vertices, int stride)
    {
        ID3D12Resource buffer = _device.CreateCommittedResource(HeapType.Upload,
            ResourceDescription.Buffer((ulong)(vertices.Length * stride)), ResourceStates.GenericRead);
        vertices.AsSpan().CopyTo(buffer.Map<WorldVertex>(0, vertices.Length));
        buffer.Unmap(0);
        return buffer;
    }

    private static BuildRaytracingAccelerationStructureInputs CreateBottomLevelInputs(
        ID3D12Resource vertexBuffer,
        int vertexCount,
        int stride,
        bool allowUpdate)
    {
        var geometry = new RaytracingGeometryDescription
        {
            Triangles = new RaytracingGeometryTrianglesDescription(
                new GpuVirtualAddressAndStride(vertexBuffer.GPUVirtualAddress, (ulong)stride),
                DxgiFormat.R32G32B32_Float, vertexCount),
            // Any-hit must remain active so transparent water/glass does not become
            // an opaque shadow caster.
            Flags = RaytracingGeometryFlags.None,
        };
        return new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel,
            Flags = allowUpdate
                ? RaytracingAccelerationStructureBuildFlags.PreferFastBuild |
                  RaytracingAccelerationStructureBuildFlags.AllowUpdate
                : RaytracingAccelerationStructureBuildFlags.PreferFastBuild,
            Layout = ElementsLayout.Array,
            DescriptorsCount = 1,
            GeometryDescriptions = new[] { geometry },
        };
    }

    private static BuildRaytracingAccelerationStructureInputs CreateAnalyticSphereBottomLevelInputs(
        ID3D12Resource aabbBuffer,
        int stride)
    {
        var geometry = new RaytracingGeometryDescription(
            new RaytracingGeometryAabbsDescription(
                1,
                new GpuVirtualAddressAndStride(aabbBuffer.GPUVirtualAddress, (ulong)stride)),
            RaytracingGeometryFlags.None);
        return new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
            Layout = ElementsLayout.Array,
            DescriptorsCount = 1,
            GeometryDescriptions = new[] { geometry },
        };
    }

    private List<RaytracingInstanceDescription> CreateSceneInstances()
    {
        var instances = new List<RaytracingInstanceDescription>(
            2 + _world.AnalyticSpheres.Count);
        if (_staticBlas is not null)
            instances.Add(CreateInstance(_staticBlas, instanceId: 0));
        if (_dynamicBlas is not null)
            instances.Add(CreateInstance(_dynamicBlas, instanceId: 1));
        for (int index = 0; index < _world.AnalyticSpheres.Spheres.Count; index++)
            instances.Add(CreateAnalyticSphereInstance(
                _analyticSphereBlas,
                _world.AnalyticSpheres.Spheres[index],
                (uint)index));
        return instances;
    }

    private static RaytracingInstanceDescription CreateAnalyticSphereInstance(
        ID3D12Resource blas,
        AnalyticSphere sphere,
        uint sphereIndex) => new()
    {
        Transform = new Matrix3x4(
            sphere.Radius, 0, 0, sphere.Center.X,
            0, sphere.Radius, 0, sphere.Center.Y,
            0, 0, sphere.Radius, sphere.Center.Z),
        InstanceMask = 0xff,
        InstanceID = (UInt24)sphereIndex,
        InstanceContributionToHitGroupIndex = (UInt24)1,
        Flags = RaytracingInstanceFlags.None,
        AccelerationStructure = blas.GPUVirtualAddress,
    };

    private void UpdateAnalyticSphereDataBuffer()
    {
        AnalyticSphereGpuData[] data = _world.AnalyticSpheres.Spheres
            .Select(sphere =>
            {
                BlockDefinition material = _blocks.Get(sphere.MaterialId);
                return new AnalyticSphereGpuData(
                    new Vector4(material.Albedo, material.Roughness),
                    new Vector4(material.Metallic, sphere.MaterialId, 0f, 0f),
                    new Vector4(sphere.Center, sphere.Radius));
            })
            .ToArray();
        if (data.Length == 0)
            data = new[]
            {
                new AnalyticSphereGpuData(
                    new Vector4(0.5f, 0.5f, 0.5f, 0.2f), Vector4.Zero, new Vector4(0f, 0f, 0f, 1f)),
            };
        if (_analyticSphereDataBuffer is null || _analyticSphereDataCapacity < data.Length)
        {
            _analyticSphereDataBuffer?.Dispose();
            _analyticSphereDataCapacity = 1;
            while (_analyticSphereDataCapacity < data.Length) _analyticSphereDataCapacity *= 2;
            _analyticSphereDataBuffer = _device.CreateCommittedResource(
                HeapType.Upload,
                ResourceDescription.Buffer((ulong)(_analyticSphereDataCapacity *
                    Marshal.SizeOf<AnalyticSphereGpuData>())),
                ResourceStates.GenericRead);
        }
        data.AsSpan().CopyTo(_analyticSphereDataBuffer.Map<AnalyticSphereGpuData>(0, data.Length));
        _analyticSphereDataBuffer.Unmap(0);
    }

    private static RaytracingInstanceDescription CreateInstance(ID3D12Resource blas, uint instanceId) => new()
    {
        Transform = new Matrix3x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0),
        InstanceMask = 0xff,
        InstanceID = (UInt24)instanceId,
        InstanceContributionToHitGroupIndex = (UInt24)0,
        Flags = RaytracingInstanceFlags.None,
        AccelerationStructure = blas.GPUVirtualAddress,
    };

    private ID3D12Resource CreateAccelerationStructure(ulong size) => _device.CreateCommittedResource(
        HeapType.Default, ResourceDescription.Buffer(size, ResourceFlags.AllowUnorderedAccess),
        ResourceStates.RaytracingAccelerationStructure);

    private void CreateEmissiveLightBuffer()
    {
        var candidates = new List<EmissiveLight>(_staticEmissiveCandidates);
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (!ShouldTraceGrid(grid) ||
                !_gridGeometryCache.TryGetValue(grid.Id, out CachedGridGeometry? cached))
                continue;
            foreach (LocalEmissiveLight light in cached.Lights)
                candidates.Add(new EmissiveLight(
                    grid.LocalToWorld(light.LocalPosition), light.Radius, light.Color, light.Intensity));
        }
        EmissiveLight[] lights = candidates.OrderByDescending(light => light.Intensity).Take(32).ToArray();
        _emissiveLightCount = lights.Length;
        if (lights.Length == 0) lights = new[] { new EmissiveLight(Vector3.Zero, 0f, Vector3.Zero, 0f) };
        int stride = Marshal.SizeOf<EmissiveLight>();
        if (_emissiveLightBuffer is null || _emissiveLightCapacity < lights.Length)
        {
            _emissiveLightBuffer?.Dispose();
            _emissiveLightCapacity = 1;
            while (_emissiveLightCapacity < lights.Length) _emissiveLightCapacity *= 2;
            _emissiveLightBuffer = _device.CreateCommittedResource(HeapType.Upload,
                ResourceDescription.Buffer((ulong)(_emissiveLightCapacity * stride)), ResourceStates.GenericRead);
        }
        lights.AsSpan().CopyTo(_emissiveLightBuffer.Map<EmissiveLight>(0, lights.Length));
        _emissiveLightBuffer.Unmap(0);
    }

    private ID3D12Resource CreateAtlasTexture(int width, int height, DxgiFormat format, int bytesPerPixel, byte[] pixels)
    {
        var description = new ResourceDescription
        {
            Dimension = ResourceDimension.Texture2D,
            Width = (ulong)width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Layout = Vortice.Direct3D12.TextureLayout.Unknown,
            Flags = ResourceFlags.None,
        };
        ID3D12Resource texture = _device.CreateCommittedResource(
            HeapType.Default, description, ResourceStates.CopyDest);
        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new int[1];
        var rowSizes = new ulong[1];
        _device.GetCopyableFootprints(description, 0, 1, 0, footprints, rows, rowSizes, out ulong totalBytes);
        using ID3D12Resource upload = _device.CreateCommittedResource(
            HeapType.Upload, ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);
        byte* destination;
        upload.Map(0, &destination).CheckError();
        int sourcePitch = width * bytesPerPixel;
        int destinationPitch = (int)footprints[0].Footprint.RowPitch;
        fixed (byte* source = pixels)
        {
            for (int y = 0; y < height; y++)
                System.Buffer.MemoryCopy(source + y * sourcePitch,
                    destination + y * destinationPitch, destinationPitch, sourcePitch);
        }
        upload.Unmap(0);

        BeginCommands();
        _commands.CopyTextureRegion(new TextureCopyLocation(texture, 0), 0, 0, 0,
            new TextureCopyLocation(upload, footprints[0]), null);
        _commands.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        ExecuteCommands();
        return texture;
    }

    private void CreateOutputResources()
    {
        bool recreateTail = _reflectionTailEnabled;
        DisposeReflectionTailResources();
        _output?.Dispose();
        _readback?.Dispose();
        var description = new ResourceDescription
        {
            Dimension = ResourceDimension.Texture2D,
            Width = (ulong)_width,
            Height = _height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = DxgiFormat.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Layout = Vortice.Direct3D12.TextureLayout.Unknown,
            Flags = ResourceFlags.AllowUnorderedAccess,
        };
        _output = _device.CreateCommittedResource(HeapType.Default, description, ResourceStates.CopySource);

        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new int[1];
        var rowSizes = new ulong[1];
        ulong totalBytes;
        _device.GetCopyableFootprints(description, 0, 1, 0, footprints, rows, rowSizes, out totalBytes);
        _readbackFootprint = footprints[0];
        _readback = _device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        _pixels = new byte[_width * _height * 4];

        GL.BindTexture(TextureTarget.Texture2D, _displayTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, _width, _height, 0,
            GlPixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        UpdateDescriptors();
        if (recreateTail) EnsureReflectionTailResources(enabled: true);
    }

    private void EnsureReflectionTailResources(bool enabled)
    {
        if (!enabled)
        {
            DisposeReflectionTailResources();
            return;
        }
        int wantedWidth = Math.Max(1, (_width + 3) / 4);
        int wantedHeight = Math.Max(1, (_height + 3) / 4);
        if (_reflectionTailAtlases[0] is not null &&
            _reflectionTailWidth == wantedWidth && _reflectionTailHeight == wantedHeight)
        {
            _reflectionTailEnabled = true;
            return;
        }

        DisposeReflectionTailResources();
        var description = new ResourceDescription
        {
            Dimension = ResourceDimension.Texture2D,
            Width = (ulong)wantedWidth,
            Height = wantedHeight,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = DxgiFormat.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Layout = Vortice.Direct3D12.TextureLayout.Unknown,
            Flags = ResourceFlags.AllowUnorderedAccess,
        };
        _reflectionTailAtlases[0] = _device.CreateCommittedResource(
            HeapType.Default, description, ResourceStates.UnorderedAccess);
        _reflectionTailAtlases[1] = _device.CreateCommittedResource(
            HeapType.Default, description, ResourceStates.UnorderedAccess);
        _reflectionTailWidth = wantedWidth;
        _reflectionTailHeight = wantedHeight;
        _reflectionTailWriteIndex = 0;
        _reflectionTailAtlasReadable[0] = false;
        _reflectionTailAtlasReadable[1] = false;
        _reflectionTailHistoryValid = false;
        _reflectionTailEnabled = true;
        ReflectionTailAtlasMemoryBytes = (ulong)wantedWidth * (ulong)wantedHeight * 8UL * 2UL;
        UpdateDescriptors();
    }

    private void DisposeReflectionTailResources()
    {
        if (_reflectionTailAtlases[0] is null && _reflectionTailAtlases[1] is null)
        {
            _reflectionTailEnabled = false;
            ReflectionTailAtlasMemoryBytes = 0;
            return;
        }
        WaitIdle();
        _reflectionTailAtlases[0]?.Dispose();
        _reflectionTailAtlases[1]?.Dispose();
        _reflectionTailAtlases[0] = null;
        _reflectionTailAtlases[1] = null;
        _reflectionTailEnabled = false;
        _reflectionTailHistoryValid = false;
        _reflectionTailWriteIndex = 0;
        _reflectionTailAtlasReadable[0] = false;
        _reflectionTailAtlasReadable[1] = false;
        _reflectionTailWidth = 0;
        _reflectionTailHeight = 0;
        ReflectionTailAtlasMemoryBytes = 0;
    }

    private void UpdateDescriptors()
    {
        if (_output is null || _tlas is null) return;
        int increment = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        CpuDescriptorHandle handle = _descriptorHeap.GetCPUDescriptorHandleForHeapStart();
        _device.CreateUnorderedAccessView(_output, null,
            new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Texture2D }, handle);
        _device.CreateShaderResourceView(null, new ShaderResourceViewDescription
        {
            Format = DxgiFormat.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            RaytracingAccelerationStructure = new RaytracingAccelerationStructureShaderResourceView
            {
                Location = _tlas.GPUVirtualAddress,
            },
        }, handle + (int)increment);
        if (_atlasColor is not null)
            _device.CreateShaderResourceView(_atlasColor, new ShaderResourceViewDescription
            {
                Format = DxgiFormat.R8G8B8A8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            }, handle + increment * 2);
        if (_atlasDetail is not null)
            _device.CreateShaderResourceView(_atlasDetail, new ShaderResourceViewDescription
            {
                Format = DxgiFormat.R8G8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            }, handle + increment * 3);
        if (_reflectionTailEnabled &&
            _reflectionTailAtlases[_reflectionTailWriteIndex] is ID3D12Resource tailWrite &&
            _reflectionTailAtlases[1 - _reflectionTailWriteIndex] is ID3D12Resource tailHistory)
        {
            _device.CreateUnorderedAccessView(tailWrite, null,
                new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Texture2D },
                handle + increment * 4);
            _device.CreateShaderResourceView(tailHistory, new ShaderResourceViewDescription
            {
                Format = DxgiFormat.R16G16B16A16_Float,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            }, handle + increment * 5);
        }
        else if (_output is not null)
        {
            // Keep the static root table fully initialized while the extension is Off.
            // No tail shader access occurs in this mode and no temporary atlas exists.
            _device.CreateUnorderedAccessView(_output, null,
                new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Texture2D },
                handle + increment * 4);
            _device.CreateShaderResourceView(_output, new ShaderResourceViewDescription
            {
                Format = DxgiFormat.R8G8B8A8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            }, handle + increment * 5);
        }
    }

    private void Trace(Vector3 cameraPosition, Camera camera, VoxelHit? selection, float timeOfDay,
        RenderQualitySettings quality)
    {
        if (_output is null || _readback is null || _staticVertexBuffer is null || _tlas is null) return;
        long traceStarted = Stopwatch.GetTimestamp();
        Vector3 forward = camera.Forward;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        if (_reflectionTailHistoryValid &&
            (Vector3.DistanceSquared(cameraPosition, _previousTailCameraPosition) > 24f * 24f ||
             Vector3.Dot(forward, _previousTailCameraForward) < 0.45f))
            _reflectionTailHistoryValid = false;
        (Vector3 primaryLightDirection, float daylight) = PrimaryLighting(timeOfDay);
        int cameraX = (int)MathF.Floor(cameraPosition.X);
        int cameraY = (int)MathF.Floor(cameraPosition.Y);
        int cameraZ = (int)MathF.Floor(cameraPosition.Z);
        bool cameraUnderwater = _world.IsFluid(cameraX, cameraY, cameraZ) &&
                                (cameraPosition.Y - cameraY < VoxelWorld.GetFluidSurfaceHeight(
                                     _world.GetFluidLevel(cameraX, cameraY, cameraZ)) ||
                                 _world.IsFluid(cameraX, cameraY + 1, cameraZ));
        LastHardwareQuality = Math.Clamp(quality.HardwareRayTracingQuality, 0, 3);
        int infiniteMirrorMode = (int)quality.InfiniteMirrorQuality;
        int targetMirrorLayers = quality.InfiniteMirrorQuality switch
        {
            InfiniteMirrorQuality.Balanced => 128,
            InfiniteMirrorQuality.Quality => 512,
            InfiniteMirrorQuality.Extreme => 1000,
            _ => 0,
        };
        int realReflectionLayers = infiniteMirrorMode > 0
            ? MaximumReflectionBounces
            : Math.Clamp(quality.HardwareRayTracingReflectionBounces, 1, MaximumReflectionBounces);
        ActualRealReflectionLayers = realReflectionLayers;
        ApproximateReflectionLayers = Math.Max(0, targetMirrorLayers - realReflectionLayers);
        if (!_reflectionTailHistoryValid)
        {
            _previousTailCameraPosition = cameraPosition;
            _previousTailCameraForward = forward;
            _previousTailCameraRight = right;
            _previousTailCameraUp = up;
            _previousTailTanHalfFov = MathF.Tan(camera.FieldOfViewDegrees * MathF.PI / 360f);
            _previousTailAspect = _width / (float)_height;
        }
        var constants = new CameraConstants(
            new Vector4(cameraPosition, MathF.Tan(camera.FieldOfViewDegrees * MathF.PI / 360f)),
            new Vector4(forward, _width / (float)_height),
            new Vector4(right, 0), new Vector4(up, 0),
            new Vector4(primaryLightDirection, daylight),
            new Vector4(LastHardwareQuality, (int)quality.HardwareRayTracingRenderStyle,
                _emissiveLightCount, cameraUnderwater ? 1f : 0f),
            new Vector4(realReflectionLayers, infiniteMirrorMode,
                selection.HasValue ? 1f : 0f, quality.SelectionOutlineReflections ? 1f : 0f),
            new Vector4(infiniteMirrorMode, targetMirrorLayers,
                _reflectionTailHistoryValid ? 1f : 0f, 4f),
            new Vector4(quality.HardwareRayTracingRayDistance,
                quality.HardwareRayTracingShadowStrength,
                quality.HardwareRayTracingEmissionStrength,
                quality.HardwareRayTracingReflectionStrength),
            new Vector4(_previousTailCameraPosition, _previousTailTanHalfFov),
            new Vector4(_previousTailCameraForward, _previousTailAspect),
            new Vector4(_previousTailCameraRight, 0),
            new Vector4(_previousTailCameraUp, 0),
            new Vector4(selection?.X ?? -1, selection?.Y ?? -1, selection?.Z ?? -1,
                selection.HasValue ? (float)Math.Min(selection.Value.GridId, 16_777_215UL) : 0f));

        if (_reflectionTailEnabled) UpdateDescriptors();

        BeginCommands();
        if (_reflectionTailEnabled && _reflectionTailAtlasReadable[_reflectionTailWriteIndex] &&
            _reflectionTailAtlases[_reflectionTailWriteIndex] is ID3D12Resource writableTail)
        {
            _commands.ResourceBarrierTransition(
                writableTail, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            _reflectionTailAtlasReadable[_reflectionTailWriteIndex] = false;
        }
        _commands.ResourceBarrierTransition(_output, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        _commands.SetDescriptorHeaps(1, new[] { _descriptorHeap });
        _commands.SetComputeRootSignature(_rootSignature);
        _commands.SetPipelineState1(_pipeline);
        _commands.SetComputeRootDescriptorTable(0, _descriptorHeap.GetGPUDescriptorHandleForHeapStart());
        _commands.SetComputeRootShaderResourceView(1, _staticVertexBuffer.GPUVirtualAddress);
        _commands.SetComputeRoot32BitConstants(2, CameraConstantCount, &constants, 0);
        if (_emissiveLightBuffer is not null)
            _commands.SetComputeRootShaderResourceView(3, _emissiveLightBuffer.GPUVirtualAddress);
        _commands.SetComputeRootShaderResourceView(4,
            (_dynamicVertexBuffer ?? _staticVertexBuffer).GPUVirtualAddress);
        if (_analyticSphereDataBuffer is not null)
            _commands.SetComputeRootShaderResourceView(5, _analyticSphereDataBuffer.GPUVirtualAddress);
        _commands.EndQuery(_timestampQueryHeap, QueryType.Timestamp, 0);
        _commands.DispatchRays(new DispatchRaysDescription
        {
            Width = _width,
            Height = _height,
            Depth = 1,
            RayGenerationShaderRecord = new GpuVirtualAddressRange
            {
                StartAddress = _shaderTable.GPUVirtualAddress,
                SizeInBytes = _shaderRecordSize,
            },
            HitGroupTable = new GpuVirtualAddressRangeAndStride
            {
                StartAddress = _shaderTable.GPUVirtualAddress + _shaderTableSectionSize,
                SizeInBytes = _shaderRecordSize * 2UL,
                StrideInBytes = _shaderRecordSize,
            },
            MissShaderTable = new GpuVirtualAddressRangeAndStride
            {
                StartAddress = _shaderTable.GPUVirtualAddress + _shaderTableSectionSize * 2UL,
                SizeInBytes = _shaderRecordSize * 2UL,
                StrideInBytes = _shaderRecordSize,
            },
        });
        _commands.EndQuery(_timestampQueryHeap, QueryType.Timestamp, 1);
        _commands.ResolveQueryData(
            _timestampQueryHeap, QueryType.Timestamp, 0, 2, _timestampReadback, 0);
        if (_reflectionTailEnabled &&
            _reflectionTailAtlases[_reflectionTailWriteIndex] is ID3D12Resource tailWrite)
        {
            _commands.ResourceBarrierTransition(
                tailWrite, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            _reflectionTailAtlasReadable[_reflectionTailWriteIndex] = true;
            if (_reflectionTailHistoryValid &&
                _reflectionTailAtlasReadable[1 - _reflectionTailWriteIndex] &&
                _reflectionTailAtlases[1 - _reflectionTailWriteIndex] is ID3D12Resource previousTail)
            {
                _commands.ResourceBarrierTransition(
                    previousTail, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
                _reflectionTailAtlasReadable[1 - _reflectionTailWriteIndex] = false;
            }
        }
        _commands.ResourceBarrierTransition(_output, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        _commands.CopyTextureRegion(new TextureCopyLocation(_readback, _readbackFootprint), 0, 0, 0,
            new TextureCopyLocation(_output, 0), null);
        ExecuteCommands();
        ReadGpuTraceTimestamp();
        if (_collectDiagnostics)
            WriteDebugMessages(throwOnError: true);
        CopyReadbackPixels();
        if (_reflectionTailEnabled)
        {
            _reflectionTailHistoryValid = true;
            _reflectionTailWriteIndex = 1 - _reflectionTailWriteIndex;
        }
        _previousTailCameraPosition = cameraPosition;
        _previousTailCameraForward = forward;
        _previousTailCameraRight = right;
        _previousTailCameraUp = up;
        _previousTailTanHalfFov = MathF.Tan(camera.FieldOfViewDegrees * MathF.PI / 360f);
        _previousTailAspect = _width / (float)_height;
        LastSynchronizedTraceMilliseconds = ElapsedMilliseconds(traceStarted);
        MaximumSynchronizedTraceMilliseconds = Math.Max(
            MaximumSynchronizedTraceMilliseconds, LastSynchronizedTraceMilliseconds);
    }

    private void ReadGpuTraceTimestamp()
    {
        ulong* timestamps;
        _timestampReadback.Map(0, (void**)&timestamps).CheckError();
        ulong start = timestamps[0];
        ulong end = timestamps[1];
        _timestampReadback.Unmap(0);
        LastGpuTraceMilliseconds = _timestampFrequency > 0 && end >= start
            ? (end - start) * 1000.0 / _timestampFrequency
            : 0;
        MaximumGpuTraceMilliseconds = Math.Max(MaximumGpuTraceMilliseconds, LastGpuTraceMilliseconds);
    }

    private void CopyReadbackPixels()
    {
        byte* source;
        _readback!.Map(0, &source).CheckError();
        int sourcePitch = (int)_readbackFootprint.Footprint.RowPitch;
        int destinationPitch = _width * 4;
        fixed (byte* destination = _pixels)
        {
            int hitPixels = 0;
            int shadowRayPixels = 0;
            int shadowOccludedPixels = 0;
            int reflectionRayPixels = 0;
            int physicsGridHitPixels = 0;
            int texturedHitPixels = 0;
            int waxReflectionPixels = 0;
            int transparentHitPixels = 0;
            int chromaticTransparentPixels = 0;
            for (int y = 0; y < _height; y++)
            {
                System.Buffer.MemoryCopy(source + y * sourcePitch,
                    destination + (_height - 1 - y) * destinationPitch,
                    destinationPitch, destinationPitch);
                if (!_collectDiagnostics) continue;
                for (int x = 0; x < _width; x++)
                {
                    byte diagnostics = source[y * sourcePitch + x * 4 + 3];
                    if ((diagnostics & 1) != 0) hitPixels++;
                    if ((diagnostics & 2) != 0) shadowRayPixels++;
                    if ((diagnostics & 4) != 0) shadowOccludedPixels++;
                    if ((diagnostics & 8) != 0) reflectionRayPixels++;
                    if ((diagnostics & 16) != 0) physicsGridHitPixels++;
                    if ((diagnostics & 32) != 0) texturedHitPixels++;
                    if ((diagnostics & 64) != 0) waxReflectionPixels++;
                    if ((diagnostics & 128) != 0)
                    {
                        transparentHitPixels++;
                        int red = source[y * sourcePitch + x * 4 + 0];
                        int green = source[y * sourcePitch + x * 4 + 1];
                        int blue = source[y * sourcePitch + x * 4 + 2];
                        if (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) >= 18)
                            chromaticTransparentPixels++;
                    }
                }
            }
            if (_collectDiagnostics)
            {
                LastHitPixelCount = hitPixels;
                LastShadowRayPixelCount = shadowRayPixels;
                LastShadowOccludedPixelCount = shadowOccludedPixels;
                LastReflectionRayPixelCount = reflectionRayPixels;
                LastPhysicsGridHitPixelCount = physicsGridHitPixels;
                LastTexturedHitPixelCount = texturedHitPixels;
                LastWaxReflectionPixelCount = waxReflectionPixels;
                LastTransparentHitPixelCount = transparentHitPixels;
                LastChromaticTransparentPixelCount = chromaticTransparentPixels;
            }
        }
        _readback.Unmap(0);
    }

    private static StaticWorldGeometryBuild? BuildStaticWorldGeometry(
        int version,
        IReadOnlyList<StaticChunkBuildInput> inputs,
        IReadOnlyList<BlockDefinition> definitions,
        CancellationToken cancellation)
    {
        long buildStarted = Stopwatch.GetTimestamp();
        var vertices = new List<WorldVertex>(100_000);
        var emissiveLights = new List<EmissiveLight>();
        var updated = new Dictionary<ChunkCoordinate, CachedStaticChunkGeometry>();
        int microVoxelTriangles = 0;
        foreach (StaticChunkBuildInput input in inputs)
        {
            if (cancellation.IsCancellationRequested) return null;
            CachedStaticChunkGeometry cached = input.Cached ?? BuildStaticChunkGeometry(
                input.Coordinate,
                input.MeshingSnapshot!,
                input.VoxelSnapshot!,
                definitions);
            if (input.Cached is null)
            {
                updated[input.Coordinate] = cached;
            }
            vertices.AddRange(cached.Vertices);
            emissiveLights.AddRange(cached.EmissiveLights);
            microVoxelTriangles += cached.MicroVoxelTriangleCount;
        }
        double milliseconds = (Stopwatch.GetTimestamp() - buildStarted) * 1000.0 / Stopwatch.Frequency;
        return new StaticWorldGeometryBuild(
            version,
            vertices.ToArray(),
            emissiveLights.ToArray(),
            microVoxelTriangles,
            updated,
            milliseconds);
    }

    private static CachedStaticChunkGeometry BuildStaticChunkGeometry(
        ChunkCoordinate chunk,
        ChunkMeshingSnapshot meshingSnapshot,
        VoxelChunkSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        var vertices = new List<WorldVertex>(8_000);
        var emissiveLights = new List<EmissiveLight>();
        int microVoxelTriangles = 0;
        AddGreedyChunkFaces(vertices, meshingSnapshot, definitions);
        foreach (StoredVoxel voxel in snapshot.Voxels)
        {
            int x = chunk.MinimumX + voxel.LocalX;
            int z = chunk.MinimumZ + voxel.LocalZ;
            BlockDefinition material = definitions[voxel.MaterialId];
            if (material.IsFluid)
                AddFluidFlowGeometry(vertices, meshingSnapshot, definitions, material, x, voxel.Y, z);
            if (TryCreateEmissiveLight(
                    new Vector3(x + 0.5f, voxel.Y + 0.5f, z + 0.5f), material, out EmissiveLight light))
                emissiveLights.Add(light);
        }
        foreach (StoredMicroVoxel micro in snapshot.MicroVoxels)
        {
            float inverse = 1f / micro.Snapshot.Resolution;
            foreach (MicroVoxelBox box in micro.Snapshot.Boxes)
            {
                int before = vertices.Count;
                Vector3 minimum = new(
                    chunk.MinimumX + micro.LocalX + box.MinX * inverse,
                    micro.Y + box.MinY * inverse,
                    chunk.MinimumZ + micro.LocalZ + box.MinZ * inverse);
                Vector3 maximum = new(
                    chunk.MinimumX + micro.LocalX + box.MaxX * inverse,
                    micro.Y + box.MaxY * inverse,
                    chunk.MinimumZ + micro.LocalZ + box.MaxZ * inverse);
                BlockDefinition material = definitions[box.MaterialId];
                AddBoxFaces(vertices, minimum, maximum, material, AllFaces, null);
                microVoxelTriangles += (vertices.Count - before) / 3;
                if (TryCreateEmissiveLight((minimum + maximum) * 0.5f, material, out EmissiveLight light))
                    emissiveLights.Add(light);
            }
        }
        return new CachedStaticChunkGeometry(
            vertices.ToArray(), emissiveLights.ToArray(), microVoxelTriangles);
    }

    private static void AddFluidFlowGeometry(
        List<WorldVertex> vertices,
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions,
        BlockDefinition material,
        int x,
        int y,
        int z)
    {
        ushort above = snapshot.GetBlock(x, y + 1, z);
        bool fluidAbove = above != 0 && definitions[above].IsFluid;
        byte level = snapshot.GetFluidLevel(x, y, z);
        float maximumY = fluidAbove ? y + 1f : y + VoxelWorld.GetFluidSurfaceHeight(level);
        if (!fluidAbove && level > 0)
            AddBoxFaces(vertices, new Vector3(x, y, z), new Vector3(x + 1f, maximumY, z + 1f),
                material, 1 << 0, null, cell: (x, y, z));

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
            else if (neighbor != 0 && !definitions[neighbor].IsTransparent &&
                     definitions[neighbor].Transmission <= 0f)
            {
                continue;
            }
            AddBoxFaces(vertices,
                new Vector3(x, minimumSideY, z), new Vector3(x + 1f, maximumY, z + 1f),
                material, (byte)(1 << face), null, cell: (x, y, z));
        }
    }

    private WorldVertex[] BuildPhysicsGridVertices()
    {
        var vertices = new List<WorldVertex>(16_000);
        BlockDefinition[] definitions = _blocks.CreateLookupSnapshot();
        TracedPhysicsGridCount = 0;
        TracedPhysicsGridTriangleCount = 0;
        var activeGridIds = _world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        foreach (ulong stale in _gridGeometryCache.Keys.Where(id => !activeGridIds.Contains(id)).ToArray())
            _gridGeometryCache.Remove(stale);
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (!ShouldTraceGrid(grid)) continue;
            if (!_gridGeometryCache.TryGetValue(grid.Id, out CachedGridGeometry? cached) ||
                cached.TopologyVersion != grid.TopologyVersion)
            {
                cached = BuildLocalGridGeometry(grid, definitions);
                _gridGeometryCache[grid.Id] = cached;
                DynamicTopologyBuildCount++;
            }
            int before = vertices.Count;
            foreach (WorldVertex vertex in cached.Vertices)
            {
                vertices.Add(vertex with
                {
                    Position = grid.LocalToWorld(vertex.Position),
                    Normal = Vector3.Normalize(grid.LocalDirectionToWorld(vertex.Normal)),
                });
            }
            int triangles = (vertices.Count - before) / 3;
            if (triangles <= 0) continue;
            TracedPhysicsGridCount++;
            TracedPhysicsGridTriangleCount += triangles;
        }
        return vertices.ToArray();
    }

    private bool ShouldTraceGrid(PhysicsGrid grid)
    {
        float radius = MathF.Max(1f, grid.BoundingRadius);
        return !_visibilityCullingReady || _retainGlobalRayGeometry ||
               IsVisibleForHardwareRt(grid.Position, _cullingCameraPosition, _cullingCameraForward,
                   _cullingFieldOfView, radius) ||
               IsShadowCasterNeededForHardwareRt(grid.Position, radius, _cullingLightDirection,
                   _cullingCameraPosition, _cullingCameraForward, _cullingFieldOfView);
    }

    private CachedGridGeometry BuildLocalGridGeometry(
        PhysicsGrid grid,
        IReadOnlyList<BlockDefinition> definitions)
    {
        var vertices = new List<WorldVertex>(Math.Max(36, grid.Elements.Count * 18));
        var lights = new List<LocalEmissiveLight>();
        foreach (PhysicsGridElement element in grid.Elements)
        {
            BlockDefinition material = definitions[element.MaterialId];
            byte faceMask = 0;
            for (int face = 0; face < 6; face++)
                if (ShouldRenderGridFace(grid, element, material, face, definitions)) faceMask |= (byte)(1 << face);
            uint gridId = grid.Id > 16_777_215UL ? 16_777_215u : (uint)grid.Id;
            AddBoxFaces(vertices, element.Minimum, element.Maximum, material, faceMask, null,
                cell: (element.CellX, element.CellY, element.CellZ), gridId: gridId);
            if (TryCreateEmissiveLight(
                    (element.Minimum + element.Maximum) * 0.5f, material, out EmissiveLight light))
                lights.Add(new LocalEmissiveLight(light.Position, light.Radius, light.Color, light.Intensity));
        }
        return new CachedGridGeometry(grid.TopologyVersion, vertices.ToArray(), lights.ToArray());
    }

    private static bool TryCreateEmissiveLight(
        Vector3 position,
        BlockDefinition material,
        out EmissiveLight light)
    {
        Vector3 emission = material.Emission;
        float energy = emission.Length();
        if (energy < 0.08f)
        {
            light = default;
            return false;
        }
        float peak = Math.Max(emission.X, Math.Max(emission.Y, emission.Z));
        Vector3 normalized = Vector3.Clamp(
            emission / Math.Max(peak, 0.001f), Vector3.Zero, Vector3.One);
        Vector3 pureColor = new(
            MathF.Pow(normalized.X, 1.65f),
            MathF.Pow(normalized.Y, 1.65f),
            MathF.Pow(normalized.Z, 1.65f));
        light = new EmissiveLight(
            position,
            Math.Clamp(3.2f + MathF.Sqrt(energy) * 1.45f, 4f, 12f),
            pureColor,
            Math.Clamp(MathF.Sqrt(energy) * 0.55f, 0.2f, 2.5f));
        return true;
    }

    private bool ShouldRenderGridFace(
        PhysicsGrid grid,
        PhysicsGridElement element,
        BlockDefinition material,
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
        if (adjacentId == material.Id) return false;
        BlockDefinition adjacent = definitions[adjacentId];
        return material.Transmission > 0f || material.IsTransparent || material.IsFluid ||
               adjacent.Transmission > 0f || adjacent.IsTransparent || adjacent.IsFluid;
    }

    private static void AddGreedyChunkFaces(
        List<WorldVertex> vertices,
        ChunkMeshingSnapshot snapshot,
        IReadOnlyList<BlockDefinition> definitions)
    {
        if (snapshot.MaximumYExclusive <= snapshot.MinimumY) return;
        int verticalCells = snapshot.MaximumYExclusive - snapshot.MinimumY;
        var mask = new ushort[Math.Max(ChunkCoordinate.Size * ChunkCoordinate.Size,
            ChunkCoordinate.Size * verticalCells)];

        for (int face = 0; face < 6; face++)
        {
            bool horizontal = face is 0 or 5;
            int sliceCount = horizontal ? verticalCells : ChunkCoordinate.Size;
            int maskWidth = ChunkCoordinate.Size;
            int maskHeight = horizontal ? ChunkCoordinate.Size : verticalCells;
            for (int slice = 0; slice < sliceCount; slice++)
            {
                Array.Clear(mask, 0, maskWidth * maskHeight);
                for (int v = 0; v < maskHeight; v++)
                for (int u = 0; u < maskWidth; u++)
                {
                    (int x, int y, int z) = FaceCell(snapshot, face, slice, u, v);
                    ushort materialId = snapshot.GetBlock(x, y, z);
                    if (materialId == 0) continue;
                    BlockDefinition material = definitions[materialId];
                    if (material.IsFluid && face != 0) continue;
                    if (material.IsFluid && snapshot.GetFluidLevel(x, y, z) > 0) continue;
                    (int offsetX, int offsetY, int offsetZ) = FaceNeighborOffset(face);
                    ushort neighborId = snapshot.GetBlock(x + offsetX, y + offsetY, z + offsetZ);
                    if (ShouldIncludeGreedyFace(face, materialId, neighborId, definitions))
                        mask[u + maskWidth * v] = materialId;
                }

                for (int v = 0; v < maskHeight; v++)
                for (int u = 0; u < maskWidth;)
                {
                    ushort materialId = mask[u + maskWidth * v];
                    if (materialId == 0)
                    {
                        u++;
                        continue;
                    }
                    int width = 1;
                    while (u + width < maskWidth && mask[u + width + maskWidth * v] == materialId) width++;
                    int height = 1;
                    while (v + height < maskHeight)
                    {
                        bool same = true;
                        for (int offset = 0; offset < width; offset++)
                            if (mask[u + offset + maskWidth * (v + height)] != materialId)
                            {
                                same = false;
                                break;
                            }
                        if (!same) break;
                        height++;
                    }

                    AddGreedyFace(vertices, snapshot, definitions[materialId], face,
                        slice, u, v, width, height);
                    for (int clearV = 0; clearV < height; clearV++)
                    for (int clearU = 0; clearU < width; clearU++)
                        mask[u + clearU + maskWidth * (v + clearV)] = 0;
                    u += width;
                }
            }
        }
    }

    private static bool ShouldIncludeGreedyFace(
        int face,
        ushort materialId,
        ushort neighborId,
        IReadOnlyList<BlockDefinition> definitions)
    {
        BlockDefinition material = definitions[materialId];
        if (material.IsFluid)
            return face == 0 && (neighborId == 0 || !definitions[neighborId].IsFluid);
        if (neighborId == 0) return true;
        if (neighborId == materialId) return false;
        BlockDefinition adjacent = definitions[neighborId];
        return material.IsTransparent || material.Transmission > 0f ||
               adjacent.IsFluid || adjacent.IsTransparent || adjacent.Transmission > 0f;
    }

    private static (int X, int Y, int Z) FaceCell(
        ChunkMeshingSnapshot snapshot,
        int face,
        int slice,
        int u,
        int v) => face switch
        {
            0 or 5 => (snapshot.Coordinate.MinimumX + u, snapshot.MinimumY + slice,
                snapshot.Coordinate.MinimumZ + v),
            1 or 2 => (snapshot.Coordinate.MinimumX + slice, snapshot.MinimumY + v,
                snapshot.Coordinate.MinimumZ + u),
            _ => (snapshot.Coordinate.MinimumX + u, snapshot.MinimumY + v,
                snapshot.Coordinate.MinimumZ + slice),
        };

    private static (int X, int Y, int Z) FaceNeighborOffset(int face) => face switch
    {
        0 => (0, 1, 0),
        1 => (-1, 0, 0),
        2 => (1, 0, 0),
        3 => (0, 0, -1),
        4 => (0, 0, 1),
        _ => (0, -1, 0),
    };

    private static void AddGreedyFace(
        List<WorldVertex> vertices,
        ChunkMeshingSnapshot snapshot,
        BlockDefinition material,
        int face,
        int slice,
        int u,
        int v,
        int width,
        int height)
    {
        (int x, int y, int z) = FaceCell(snapshot, face, slice, u, v);
        Vector3 minimum;
        Vector3 maximum;
        if (face is 0 or 5)
        {
            float maximumY = material.IsFluid ? y + VoxelWorld.FluidSurfaceHeight : y + 1f;
            minimum = new Vector3(x, y, z);
            maximum = new Vector3(x + width, maximumY, z + height);
        }
        else if (face is 1 or 2)
        {
            minimum = new Vector3(x, y, z);
            maximum = new Vector3(x + 1f, y + height, z + width);
        }
        else
        {
            minimum = new Vector3(x, y, z);
            maximum = new Vector3(x + width, y + height, z + 1f);
        }
        AddBoxFaces(vertices, minimum, maximum, material, (byte)(1 << face), null,
            new Vector2(width, height));
    }

    private static void AddBoxFaces(
        List<WorldVertex> vertices,
        Vector3 minimum,
        Vector3 maximum,
        BlockDefinition material,
        byte faceMask,
        PhysicsGrid? grid,
        Vector2 uvScale = default,
        (int X, int Y, int Z)? cell = null,
        uint gridId = 0)
    {
        if (uvScale.X <= 0f || uvScale.Y <= 0f) uvScale = Vector2.One;
        for (int face = 0; face < 6; face++)
        {
            if ((faceMask & (1 << face)) == 0) continue;
            uint atlasFace = face == 0 ? 0u : face == 5 ? 2u : 1u;
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
                Vector2 uv = corner switch
                {
                    0 => Vector2.Zero,
                    1 => Vector2.UnitX,
                    2 => Vector2.One,
                    _ => Vector2.UnitY,
                };
                uv *= uvScale;
                vertices.Add(new WorldVertex(position, transformedNormal, uv, material.Id,
                    atlasFace, material.Roughness, material.Metallic, gridId,
                    material.Transmission,
                    (material.IsFluid ? 1u : 0u) | (material.IsTransparent ? 2u : 0u) |
                    ((uint)Math.Clamp((int)MathF.Round(material.IndexOfRefraction * 100f), 100, 255) << 8),
                    cell?.X ?? 0, cell?.Y ?? 0, cell?.Z ?? 0,
                    material.Emission));
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

    private void BeginCommands()
    {
        _allocator.Reset();
        _commands.Reset(_allocator);
    }

    private void ExecuteCommands()
    {
        _commands.Close();
        _queue.ExecuteCommandList(_commands);
        WaitIdle();
    }

    private void WaitIdle()
    {
        if (_queue is null || _fence is null) return;
        ulong value = ++_fenceValue;
        _queue.Signal(_fence, value);
        _fence.SetEventOnCompletion(value, _fenceEvent);
        _fenceEvent.WaitOne();
    }

    private void OnVoxelChanged(int x, int y, int z, VoxelMaterial material)
    {
        foreach (ChunkCoordinate chunk in ChunkCoordinate.MeshDependenciesForVoxel(x, z))
            _dirtyStaticGeometryChunks.Add(chunk);
        MarkStaticGeometryDirty();
    }
    private void OnFluidLevelChanged(int x, int y, int z) =>
        OnVoxelChanged(x, y, z, VoxelMaterial.Water);
    private void OnWorldReloaded()
    {
        _gridGeometryCache.Clear();
        _staticChunkGeometryCache.Clear();
        _dirtyStaticGeometryChunks.Clear();
        _staticEmissiveCandidates.Clear();
        MarkStaticGeometryDirty(forceImmediate: true);
    }
    private void OnMicroVoxelCellChanged(int x, int y, int z)
    {
        foreach (ChunkCoordinate chunk in ChunkCoordinate.MeshDependenciesForVoxel(x, z))
            _dirtyStaticGeometryChunks.Add(chunk);
        MarkStaticGeometryDirty();
    }
    private void OnMicroVoxelsReloaded()
    {
        _staticChunkGeometryCache.Clear();
        _dirtyStaticGeometryChunks.Clear();
        MarkStaticGeometryDirty(forceImmediate: true);
    }
    private void OnChunkGenerated(ChunkCoordinate chunk)
    {
        _dirtyStaticGeometryChunks.Add(chunk);
        _dirtyStaticGeometryChunks.Add(chunk with { X = chunk.X - 1 });
        _dirtyStaticGeometryChunks.Add(chunk with { X = chunk.X + 1 });
        _dirtyStaticGeometryChunks.Add(chunk with { Z = chunk.Z - 1 });
        _dirtyStaticGeometryChunks.Add(chunk with { Z = chunk.Z + 1 });
        MarkStaticGeometryDirty();
    }

    private void MarkStaticGeometryDirty(bool forceImmediate = false)
    {
        long now = Environment.TickCount64;
        if (!_geometryDirty) _staticGeometryDirtySinceMs = now;
        _geometryDirty = true;
        _staticGeometryVersion++;
        _staticGeometryBuildCancellation?.Cancel();
        _lastStaticGeometryChangeMs = now;
        _forceImmediateStaticRebuild |= forceImmediate;
    }
    private void OnPhysicsGridsChanged()
    {
        // Static world geometry is independent from moving Physics Grids. Topology
        // and transforms only invalidate the small dynamic BLAS plus the TLAS.
        _physicsGeometryDirty = true;
    }
    private void OnAnalyticSpheresChanged()
    {
        if (_world.AnalyticSpheres.Count != _lastAnalyticSphereCount)
            _analyticSphereTopologyDirty = true;
        _physicsGeometryDirty = true;
    }

    private void DisposeGeometryResources()
    {
        _instanceBuffer?.Dispose(); _instanceBuffer = null;
        _tlas?.Dispose(); _tlas = null;
        _dynamicBlas?.Dispose(); _dynamicBlas = null;
        _staticBlas?.Dispose(); _staticBlas = null;
        _dynamicVertexBuffer?.Dispose(); _dynamicVertexBuffer = null;
        _dynamicUpdateScratch?.Dispose(); _dynamicUpdateScratch = null;
        _dynamicUpdateScratchBytes = 0;
        _staticVertexBuffer?.Dispose(); _staticVertexBuffer = null;
        _emissiveLightBuffer?.Dispose(); _emissiveLightBuffer = null;
        _analyticSphereDataBuffer?.Dispose(); _analyticSphereDataBuffer = null;
        _analyticSphereDataCapacity = 0;
        _emissiveLightCapacity = 0;
        _staticBlasBytes = 0;
        _dynamicBlasBytes = 0;
        _tlasBytes = 0;
    }

    private void DisposeDynamicGeometryResources()
    {
        _instanceBuffer?.Dispose(); _instanceBuffer = null;
        _tlas?.Dispose(); _tlas = null;
        _dynamicBlas?.Dispose(); _dynamicBlas = null;
        _dynamicVertexBuffer?.Dispose(); _dynamicVertexBuffer = null;
        _emissiveLightBuffer?.Dispose(); _emissiveLightBuffer = null;
        _analyticSphereDataBuffer?.Dispose(); _analyticSphereDataBuffer = null;
        _analyticSphereDataCapacity = 0;
        _emissiveLightCapacity = 0;
        _dynamicBlasBytes = 0;
        _tlasBytes = 0;
    }

    private void WriteDebugMessages(bool throwOnError = false)
    {
        if (_infoQueue is null) return;
        string? firstError = null;
        for (ulong index = 0; index < _infoQueue.NumStoredMessages; index++)
        {
            var message = _infoQueue.GetMessage(index);
            Console.Error.WriteLine($"DXR [{message.Severity}] {message.Id}: {message.Description}");
            if (message.Severity == MessageSeverity.Error && firstError is null)
                firstError = $"{message.Id}: {message.Description}";
        }
        _infoQueue.ClearStoredMessages();
        if (throwOnError && firstError is not null)
            throw new InvalidOperationException($"D3D12 Debug Layer rejected the DXR frame: {firstError}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _world.VoxelChanged -= OnVoxelChanged;
        _world.FluidLevelChanged -= OnFluidLevelChanged;
        _world.WorldReloaded -= OnWorldReloaded;
        _world.MicroVoxelCellChanged -= OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded -= OnMicroVoxelsReloaded;
        _world.ChunkGenerated -= OnChunkGenerated;
        _world.PhysicsGrids.Changed -= OnPhysicsGridsChanged;
        _world.AnalyticSpheres.Changed -= OnAnalyticSpheresChanged;
        _staticGeometryBuildCancellation?.Cancel();
        _staticGeometryBuildCancellation?.Dispose();
        _staticGeometryBuildCancellation = null;
        _staticGeometryBuildTask = null;
        WaitIdle();
        DisposeGeometryResources();
        _analyticSphereBlas.Dispose();
        _analyticSphereAabbBuffer.Dispose();
        _readback?.Dispose();
        _output?.Dispose();
        DisposeReflectionTailResources();
        _atlasDetail?.Dispose();
        _atlasColor?.Dispose();
        _shaderTable.Dispose();
        _pipelineProperties.Dispose();
        _pipeline.Dispose();
        _rootSignature.Dispose();
        _descriptorHeap.Dispose();
        _commands.Dispose();
        _allocator.Dispose();
        _timestampReadback.Dispose();
        _timestampQueryHeap.Dispose();
        _fence.Dispose();
        _queue.Dispose();
        _infoQueue?.Dispose();
        _device.Dispose();
        _fenceEvent.Dispose();
        _display.Dispose();
        GL.DeleteTexture(_displayTexture);
        GL.DeleteTexture(_materialAtlasTexture);
        GL.DeleteVertexArray(_displayVertexArray);
    }

    private static uint Align(uint value, uint alignment) => ((value + alignment - 1) / alignment) * alignment;

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WorldVertex(
        Vector3 Position,
        Vector3 Normal,
        Vector2 Uv,
        uint MaterialId,
        uint MaterialFace,
        float Roughness,
        float Metallic,
        uint GridMarker,
        float Transmission,
        uint MaterialFlags,
        int CellX,
        int CellY,
        int CellZ,
        Vector3 Emission);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EmissiveLight(Vector3 Position, float Radius, Vector3 Color, float Intensity);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AnalyticSphereGpuData(
        Vector4 AlbedoAndRoughness,
        Vector4 MetallicAndMaterial,
        Vector4 CenterAndRadius);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AnalyticSphereAabb(
        float MinimumX,
        float MinimumY,
        float MinimumZ,
        float MaximumX,
        float MaximumY,
        float MaximumZ);
    private readonly record struct LocalEmissiveLight(
        Vector3 LocalPosition, float Radius, Vector3 Color, float Intensity);
    private sealed record CachedGridGeometry(
        int TopologyVersion, WorldVertex[] Vertices, LocalEmissiveLight[] Lights);
    private sealed record CachedStaticChunkGeometry(
        WorldVertex[] Vertices, EmissiveLight[] EmissiveLights, int MicroVoxelTriangleCount);
    private sealed record StaticChunkBuildInput(
        ChunkCoordinate Coordinate,
        CachedStaticChunkGeometry? Cached,
        ChunkMeshingSnapshot? MeshingSnapshot,
        VoxelChunkSnapshot? VoxelSnapshot);
    private sealed record StaticWorldGeometryBuild(
        int Version,
        WorldVertex[] Vertices,
        EmissiveLight[] EmissiveLights,
        int MicroVoxelTriangleCount,
        IReadOnlyDictionary<ChunkCoordinate, CachedStaticChunkGeometry> UpdatedChunks,
        double BuildMilliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CameraConstants(
        Vector4 CameraPositionAndTanHalfFov,
        Vector4 CameraForwardAndAspect,
        Vector4 CameraRight,
        Vector4 CameraUp,
        Vector4 SunDirection,
        Vector4 RenderOptions,
        Vector4 TraceOptions,
        Vector4 TailOptions,
        Vector4 HardwareOptions,
        Vector4 PreviousCameraPositionAndTanHalfFov,
        Vector4 PreviousCameraForwardAndAspect,
        Vector4 PreviousCameraRight,
        Vector4 PreviousCameraUp,
        Vector4 SelectedCellAndGrid);
}
