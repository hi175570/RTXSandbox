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
    private const int DescriptorCount = 4;
    private const int CameraConstantCount = 24;
    private static readonly bool[] AllFaces = { true, true, true, true, true, true };
    private static readonly Lazy<(bool Supported, string Reason)> Support = new(ProbeSupport);

    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly ID3D12Device5 _device;
    private readonly ID3D12InfoQueue? _infoQueue;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList4 _commands;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly ID3D12DescriptorHeap _descriptorHeap;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12StateObject _pipeline;
    private readonly ID3D12StateObjectProperties _pipelineProperties;
    private readonly ID3D12Resource _shaderTable;
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
    private ID3D12Resource? _instanceBuffer;
    private ID3D12Resource? _staticBlas;
    private ID3D12Resource? _dynamicBlas;
    private ID3D12Resource? _tlas;
    private ID3D12Resource? _output;
    private ID3D12Resource? _readback;
    private ID3D12Resource? _atlasColor;
    private ID3D12Resource? _atlasDetail;
    private ID3D12Resource? _emissiveLightBuffer;
    private int _emissiveLightCapacity;
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
    private int _emissiveLightCount;
    private int _lastLoadedChunkCount;
    private readonly Dictionary<ulong, CachedGridGeometry> _gridGeometryCache = new();
    private readonly List<EmissiveLight> _staticEmissiveCandidates = new();

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
        _world.WorldReloaded += OnWorldReloaded;
        _world.MicroVoxelCellChanged += OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded += OnMicroVoxelsReloaded;
        _world.PhysicsGrids.Changed += OnPhysicsGridsChanged;
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

    public void ResetAccumulation() { }

    public void Render(Vector3 cameraPosition, Camera camera, VoxelHit? selection, float timeOfDay,
        RenderQualitySettings quality, float exposure)
    {
        UpdateVisibleChunkMask(cameraPosition, camera, timeOfDay, quality);
        long now = Environment.TickCount64;
        bool staticGeometryReady = _forceImmediateStaticRebuild || _tlas is null ||
            (now - _lastStaticGeometryChangeMs >= 160 &&
             (_world.PendingChunkGenerationCount == 0 || now - _staticGeometryDirtySinceMs >= 1500));
        if (_geometryDirty && staticGeometryReady)
            RebuildWorldAccelerationStructures();
        else if (_physicsGeometryDirty && now - _lastAccelerationStructureBuildMs >= 120)
            RebuildDynamicAccelerationStructures();
        Trace(cameraPosition, camera, timeOfDay, quality);

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
        Vector3 lightDirection = PrimaryLightDirection(timeOfDay);
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

    private static Vector3 PrimaryLightDirection(float timeOfDay)
    {
        float angle = (timeOfDay - 6f) * (2f * MathF.PI / 24f);
        Vector3 sun = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.28f));
        return sun.Y >= 0f ? sun : -sun;
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
                    new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, 2, 0, 2)), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All),
                new RootParameter1(new RootConstants(0, 0, CameraConstantCount), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All),
                new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All),
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
            new ExportDescription("ClosestHit")));
        var hitGroup = new StateSubObject(new HitGroupDescription(
            "HitGroup", HitGroupType.Triangles,
            anyHitShaderImport: "AnyHit", closestHitShaderImport: "ClosestHit"));
        var shaderConfig = new StateSubObject(new RaytracingShaderConfig(6 * sizeof(float), 2 * sizeof(float)));
        var shaderAssociation = new StateSubObject(new SubObjectToExportsAssociation(
            shaderConfig, "RayGen", "Miss", "ShadowMiss", "AnyHit", "ClosestHit"));
        var pipelineConfig = new StateSubObject(new RaytracingPipelineConfig(2));
        var globalRoot = new StateSubObject(new GlobalRootSignature(_rootSignature));
        ID3D12StateObject pipeline;
        try
        {
            pipeline = _device.CreateStateObject(new StateObjectDescription(
                StateObjectType.RaytracingPipeline,
                dxilLibrary, hitGroup, shaderConfig, shaderAssociation, pipelineConfig, globalRoot));
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
        uint sectionSize = Align(recordSize, RaytracingShaderTableByteAlignment);
        ID3D12Resource table = _device.CreateCommittedResource(HeapType.Upload,
            ResourceDescription.Buffer(sectionSize * 3UL), ResourceStates.GenericRead);
        byte* destination;
        table.Map(0, &destination).CheckError();
        CopyShaderIdentifier(destination + sectionSize * 0, "RayGen");
        CopyShaderIdentifier(destination + sectionSize * 1, "HitGroup");
        CopyShaderIdentifier(destination + sectionSize * 2, "Miss");
        CopyShaderIdentifier(destination + sectionSize * 2 + recordSize, "ShadowMiss");
        table.Unmap(0);
        return (table, recordSize, sectionSize);
    }

    private void CopyShaderIdentifier(byte* destination, string exportName) =>
        Unsafe.CopyBlockUnaligned(destination, (void*)_pipelineProperties.GetShaderIdentifier(exportName), ShaderIdentifierSizeInBytes);

    private void RebuildWorldAccelerationStructures()
    {
        WorldVertex[] staticVertices = BuildStaticWorldVertices();
        WorldVertex[] dynamicVertices = BuildPhysicsGridVertices();
        RebuildAccelerationStructures(staticVertices, dynamicVertices, preserveStatic: false);
    }

    private void RebuildDynamicAccelerationStructures()
    {
        WorldVertex[] dynamicVertices = BuildPhysicsGridVertices();
        if (_dynamicBlas is not null && _dynamicVertexBuffer is not null &&
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
        WaitIdle();
        if (preserveStatic) DisposeDynamicGeometryResources();
        else DisposeGeometryResources();

        int stride = Marshal.SizeOf<WorldVertex>();
        BuildRaytracingAccelerationStructureInputs? staticInputs = null;
        RaytracingAccelerationStructurePrebuildInfo? staticInfo = null;
        if (!preserveStatic)
        {
            _staticVertexCount = staticVertices.Length;
            if (staticVertices.Length == 0 && dynamicVertices.Length == 0)
                throw new InvalidOperationException("DXR world contains no traceable geometry.");
            _staticVertexBuffer = CreateVertexBuffer(
                staticVertices.Length > 0 ? staticVertices : new WorldVertex[1], stride);
            if (staticVertices.Length > 0)
            {
                staticInputs = CreateBottomLevelInputs(
                    _staticVertexBuffer, staticVertices.Length, stride, allowUpdate: false);
                staticInfo = _device.GetRaytracingAccelerationStructurePrebuildInfo(staticInputs);
                _staticBlas = CreateAccelerationStructure(staticInfo.Value.ResultDataMaxSizeInBytes);
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
            _dynamicBlas = CreateAccelerationStructure(dynamicInfo.Value.ResultDataMaxSizeInBytes);
        }

        var instances = new List<RaytracingInstanceDescription>(2);
        if (_staticBlas is not null)
            instances.Add(CreateInstance(_staticBlas, instanceId: 0));
        if (_dynamicBlas is not null)
            instances.Add(CreateInstance(_dynamicBlas, instanceId: 1));
        if (instances.Count == 0)
            throw new InvalidOperationException("DXR acceleration structure has no instances.");

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
        WaitIdle();
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
        using ID3D12Resource scratch = _device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Buffer(scratchSize, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.Common);

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
        LastDynamicAccelerationStructureBuildMilliseconds = elapsed;
        DynamicAccelerationStructureBuildCount++;
        TlasUpdateCount++;
        AccelerationStructureBuildCount++;
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
                : RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
            Layout = ElementsLayout.Array,
            DescriptorsCount = 1,
            GeometryDescriptions = new[] { geometry },
        };
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
    }

    private void Trace(Vector3 cameraPosition, Camera camera, float timeOfDay, RenderQualitySettings quality)
    {
        if (_output is null || _readback is null || _staticVertexBuffer is null || _tlas is null) return;
        Vector3 forward = camera.Forward;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        Vector3 primaryLightDirection = PrimaryLightDirection(timeOfDay);
        int cameraX = (int)MathF.Floor(cameraPosition.X);
        int cameraY = (int)MathF.Floor(cameraPosition.Y);
        int cameraZ = (int)MathF.Floor(cameraPosition.Z);
        bool cameraUnderwater = _world.IsFluid(cameraX, cameraY, cameraZ) &&
                                cameraPosition.Y - cameraY < VoxelWorld.FluidSurfaceHeight;
        LastHardwareQuality = Math.Clamp(quality.HardwareRayTracingQuality, 0, 3);
        var constants = new CameraConstants(
            new Vector4(cameraPosition, MathF.Tan(camera.FieldOfViewDegrees * MathF.PI / 360f)),
            new Vector4(forward, _width / (float)_height),
            new Vector4(right, 0), new Vector4(up, 0),
            new Vector4(primaryLightDirection, 0),
            new Vector4(LastHardwareQuality, (int)quality.HardwareRayTracingRenderStyle,
                _emissiveLightCount, cameraUnderwater ? 1f : 0f));

        BeginCommands();
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
                SizeInBytes = _shaderRecordSize,
                StrideInBytes = _shaderRecordSize,
            },
            MissShaderTable = new GpuVirtualAddressRangeAndStride
            {
                StartAddress = _shaderTable.GPUVirtualAddress + _shaderTableSectionSize * 2UL,
                SizeInBytes = _shaderRecordSize * 2UL,
                StrideInBytes = _shaderRecordSize,
            },
        });
        _commands.ResourceBarrierTransition(_output, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        _commands.CopyTextureRegion(new TextureCopyLocation(_readback, _readbackFootprint), 0, 0, 0,
            new TextureCopyLocation(_output, 0), null);
        ExecuteCommands();
        if (_collectDiagnostics)
            WriteDebugMessages(throwOnError: true);
        CopyReadbackPixels();
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
                    if ((diagnostics & 128) != 0) transparentHitPixels++;
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
            }
        }
        _readback.Unmap(0);
    }

    private WorldVertex[] BuildStaticWorldVertices()
    {
        var vertices = new List<WorldVertex>(100_000);
        _staticEmissiveCandidates.Clear();
        TracedMicroVoxelTriangleCount = 0;
        IReadOnlyCollection<ChunkCoordinate> chunks = _visibilityCullingReady
            ? _visibleChunks
            : _world.SnapshotLoadedChunks();
        foreach (ChunkCoordinate chunk in chunks)
        {
            VoxelChunkSnapshot snapshot = _world.SnapshotChunk(chunk);
            foreach (StoredVoxel voxel in snapshot.Voxels)
            {
                int x = chunk.MinimumX + voxel.LocalX;
                int z = chunk.MinimumZ + voxel.LocalZ;
                BlockDefinition material = _blocks.Get(voxel.MaterialId);
                AddVoxelFaces(vertices, x, voxel.Y, z, material);
                AddStaticEmissiveCandidate(new Vector3(x + 0.5f, voxel.Y + 0.5f, z + 0.5f), material);
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
                    BlockDefinition material = _blocks.Get(box.MaterialId);
                    AddBoxFaces(vertices, minimum, maximum, material, AllFaces, null);
                    TracedMicroVoxelTriangleCount += (vertices.Count - before) / 3;
                    AddStaticEmissiveCandidate((minimum + maximum) * 0.5f, material);
                }
            }
        }

        return vertices.ToArray();
    }

    private WorldVertex[] BuildPhysicsGridVertices()
    {
        var vertices = new List<WorldVertex>(16_000);
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
                cached = BuildLocalGridGeometry(grid);
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
                    GridMarker = 1u,
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

    private CachedGridGeometry BuildLocalGridGeometry(PhysicsGrid grid)
    {
        var vertices = new List<WorldVertex>(Math.Max(36, grid.Elements.Count * 18));
        var lights = new List<LocalEmissiveLight>();
        var includeFaces = new bool[6];
        foreach (PhysicsGridElement element in grid.Elements)
        {
            BlockDefinition material = _blocks.Get(element.MaterialId);
            for (int face = 0; face < includeFaces.Length; face++)
                includeFaces[face] = ShouldRenderGridFace(grid, element, material, face);
            AddBoxFaces(vertices, element.Minimum, element.Maximum, material, includeFaces, null);
            if (TryCreateEmissiveLight(
                    (element.Minimum + element.Maximum) * 0.5f, material, out EmissiveLight light))
                lights.Add(new LocalEmissiveLight(light.Position, light.Radius, light.Color, light.Intensity));
        }
        return new CachedGridGeometry(grid.TopologyVersion, vertices.ToArray(), lights.ToArray());
    }

    private void AddStaticEmissiveCandidate(Vector3 position, BlockDefinition material)
    {
        if (TryCreateEmissiveLight(position, material, out EmissiveLight light))
            _staticEmissiveCandidates.Add(light);
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
        light = new EmissiveLight(
            position,
            Math.Clamp(4f + energy * 2.5f, 5f, 18f),
            Vector3.Normalize(emission + new Vector3(0.001f)),
            Math.Clamp(energy, 0.25f, 8f));
        return true;
    }

    private bool ShouldRenderGridFace(
        PhysicsGrid grid,
        PhysicsGridElement element,
        BlockDefinition material,
        int face)
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
        BlockDefinition adjacent = _blocks.Get(adjacentId);
        return material.Transmission > 0f || material.IsTransparent || material.IsFluid ||
               adjacent.Transmission > 0f || adjacent.IsTransparent || adjacent.IsFluid;
    }

    private void AddVoxelFaces(List<WorldVertex> vertices, int x, int y, int z, BlockDefinition material)
    {
        Vector3 minimum = new(x, y, z);
        Vector3 maximum = minimum + new Vector3(1f,
            material.IsFluid && !_world.IsFluid(x, y + 1, z) ? VoxelWorld.FluidSurfaceHeight : 1f, 1f);
        bool[] includeFaces = new bool[6];
        if (material.IsFluid)
        {
            ushort above = _world.GetLoadedBlockId(x, y + 1, z);
            includeFaces[0] = above == 0 || !_blocks.Get(above).IsFluid;
            AddBoxFaces(vertices, minimum, maximum, material, includeFaces, null);
            return;
        }
        (int X, int Y, int Z)[] neighbors = { (0, 1, 0), (-1, 0, 0), (1, 0, 0), (0, 0, -1), (0, 0, 1), (0, -1, 0) };
        for (int face = 0; face < 6; face++)
        {
            var neighbor = neighbors[face];
            ushort neighborId = _world.GetLoadedBlockId(x + neighbor.X, y + neighbor.Y, z + neighbor.Z);
            if (neighborId == 0)
            {
                includeFaces[face] = true;
                continue;
            }
            if (neighborId == material.Id)
            {
                includeFaces[face] = false;
                continue;
            }
            BlockDefinition adjacent = _blocks.Get(neighborId);
            includeFaces[face] = material.IsFluid || material.IsTransparent || material.Transmission > 0f ||
                                 adjacent.IsFluid || adjacent.IsTransparent || adjacent.Transmission > 0f;
        }
        AddBoxFaces(vertices, minimum, maximum, material, includeFaces, null);
    }

    private static void AddBoxFaces(
        List<WorldVertex> vertices,
        Vector3 minimum,
        Vector3 maximum,
        BlockDefinition material,
        IReadOnlyList<bool> includeFaces,
        PhysicsGrid? grid)
    {
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
        for (int face = 0; face < 6; face++)
        {
            if (!includeFaces[face]) continue;
            uint atlasFace = face == 0 ? 0u : face == 5 ? 2u : 1u;
            foreach (int corner in triangles)
            {
                Vector3 position = faces[face][corner];
                Vector3 normal = normals[face];
                if (grid is not null)
                {
                    position = grid.LocalToWorld(position);
                    normal = Vector3.Normalize(grid.LocalDirectionToWorld(normal));
                }
                vertices.Add(new WorldVertex(position, normal, uvs[corner], material.Id,
                    atlasFace, material.Roughness, material.Metallic, grid is null ? 0u : 1u,
                    material.Transmission,
                    (material.IsFluid ? 1u : 0u) | (material.IsTransparent ? 2u : 0u),
                    material.Emission));
            }
        }
    }

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

    private void OnVoxelChanged(int x, int y, int z, VoxelMaterial material) => MarkStaticGeometryDirty();
    private void OnWorldReloaded()
    {
        _gridGeometryCache.Clear();
        _staticEmissiveCandidates.Clear();
        MarkStaticGeometryDirty(forceImmediate: true);
    }
    private void OnMicroVoxelCellChanged(int x, int y, int z) => MarkStaticGeometryDirty();
    private void OnMicroVoxelsReloaded() => MarkStaticGeometryDirty(forceImmediate: true);

    private void MarkStaticGeometryDirty(bool forceImmediate = false)
    {
        long now = Environment.TickCount64;
        if (!_geometryDirty) _staticGeometryDirtySinceMs = now;
        _geometryDirty = true;
        _lastStaticGeometryChangeMs = now;
        _forceImmediateStaticRebuild |= forceImmediate;
    }
    private void OnPhysicsGridsChanged()
    {
        // Static world geometry is independent from moving Physics Grids. Topology
        // and transforms only invalidate the small dynamic BLAS plus the TLAS.
        _physicsGeometryDirty = true;
    }

    private void DisposeGeometryResources()
    {
        _instanceBuffer?.Dispose(); _instanceBuffer = null;
        _tlas?.Dispose(); _tlas = null;
        _dynamicBlas?.Dispose(); _dynamicBlas = null;
        _staticBlas?.Dispose(); _staticBlas = null;
        _dynamicVertexBuffer?.Dispose(); _dynamicVertexBuffer = null;
        _staticVertexBuffer?.Dispose(); _staticVertexBuffer = null;
        _emissiveLightBuffer?.Dispose(); _emissiveLightBuffer = null;
        _emissiveLightCapacity = 0;
    }

    private void DisposeDynamicGeometryResources()
    {
        _instanceBuffer?.Dispose(); _instanceBuffer = null;
        _tlas?.Dispose(); _tlas = null;
        _dynamicBlas?.Dispose(); _dynamicBlas = null;
        _dynamicVertexBuffer?.Dispose(); _dynamicVertexBuffer = null;
        _emissiveLightBuffer?.Dispose(); _emissiveLightBuffer = null;
        _emissiveLightCapacity = 0;
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
        _world.WorldReloaded -= OnWorldReloaded;
        _world.MicroVoxelCellChanged -= OnMicroVoxelCellChanged;
        _world.MicroVoxelsReloaded -= OnMicroVoxelsReloaded;
        _world.PhysicsGrids.Changed -= OnPhysicsGridsChanged;
        WaitIdle();
        DisposeGeometryResources();
        _readback?.Dispose();
        _output?.Dispose();
        _atlasDetail?.Dispose();
        _atlasColor?.Dispose();
        _shaderTable.Dispose();
        _pipelineProperties.Dispose();
        _pipeline.Dispose();
        _rootSignature.Dispose();
        _descriptorHeap.Dispose();
        _commands.Dispose();
        _allocator.Dispose();
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
        Vector3 Emission);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EmissiveLight(Vector3 Position, float Radius, Vector3 Color, float Intensity);
    private readonly record struct LocalEmissiveLight(
        Vector3 LocalPosition, float Radius, Vector3 Color, float Intensity);
    private sealed record CachedGridGeometry(
        int TopologyVersion, WorldVertex[] Vertices, LocalEmissiveLight[] Lights);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CameraConstants(
        Vector4 CameraPositionAndTanHalfFov,
        Vector4 CameraForwardAndAspect,
        Vector4 CameraRight,
        Vector4 CameraUp,
        Vector4 SunDirection,
        Vector4 RenderOptions);
}
