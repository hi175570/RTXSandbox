using System.Numerics;
using RTXSandbox.Gameplay;
using RTXSandbox.ModAPI;
using RTXSandbox.Rendering.Backends;
using RTXSandbox.World;

namespace RTXSandbox.Rendering;

/// <summary>
/// Owns the active renderer and provides an atomic, runtime-safe backend switch.
/// The game and Mod API depend on this host instead of a concrete rendering implementation.
/// </summary>
public sealed class RenderBackendHost : IRenderBackend, IMaterialPreviewSource, IRenderBackendService
{
    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private int _width;
    private int _height;
    private float _renderScale;
    private bool _disposed;
    private IRenderBackend _active;
    private IRenderBackend? _cachedHardwareRayTracing;
    private readonly Func<RenderBackendKind, IRenderBackend> _backendFactory;

    public IRenderBackend ActiveBackend => _active;
    public RenderBackendKind Kind => _active.Kind;
    public RenderBackendCapabilities Capabilities => _active.Capabilities;
    public string Name => _active.Name;
    public int AccumulatedSamples => _active.AccumulatedSamples;
    public string Status { get; private set; } = string.Empty;
    public string ActiveBackendId => Kind.ToString();
    public string ActiveBackendName => Name;
    public IReadOnlyList<RenderBackendInfo> Backends { get; } = RenderBackendCatalog.All
        .Select(descriptor => new RenderBackendInfo(
            descriptor.Kind.ToString(),
            descriptor.DisplayName,
            descriptor.Implemented,
            descriptor.Description))
        .ToArray();

    public int MaterialAtlasTexture => PreviewSource.MaterialAtlasTexture;
    public int MaterialTextureResolution => PreviewSource.MaterialTextureResolution;

    private IMaterialPreviewSource PreviewSource =>
        _active as IMaterialPreviewSource ??
        throw new InvalidOperationException($"Renderer {_active.Name} does not expose material previews.");

    public RenderBackendHost(
        VoxelWorld world,
        BlockRegistry blocks,
        int width,
        int height,
        float renderScale,
        RenderBackendKind initialKind)
        : this(world, blocks, width, height, renderScale, initialKind, backendFactory: null)
    {
    }

    internal RenderBackendHost(
        VoxelWorld world,
        BlockRegistry blocks,
        int width,
        int height,
        float renderScale,
        RenderBackendKind initialKind,
        Func<RenderBackendKind, IRenderBackend>? backendFactory)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _renderScale = Math.Clamp(renderScale, 0.35f, 1f);
        _backendFactory = backendFactory ?? Create;
        string? fallbackReason = null;
        if (!RenderBackendCatalog.IsImplemented(initialKind))
        {
            fallbackReason = $"Renderer ID '{initialKind}' 不存在或目前不可用";
            initialKind = RenderBackendKind.Classic;
        }

        try
        {
            _active = _backendFactory(initialKind);
            Status = fallbackReason is null
                ? $"目前 Renderer：{_active.Name}"
                : $"{fallbackReason}；已安全回退至 {_active.Name}。";
        }
        catch (Exception exception) when (initialKind != RenderBackendKind.Classic)
        {
            _active = _backendFactory(RenderBackendKind.Classic);
            Status = $"{RenderBackendCatalog.Get(initialKind).ChineseDisplayName} 初始化失敗；" +
                     $"已安全回退至 {_active.Name}：{exception.Message}";
            Console.Error.WriteLine(Status);
        }
    }

    public bool Switch(RenderBackendKind kind)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RenderBackendHost));
        if (!RenderBackendCatalog.TryGet(kind, out RenderBackendDescriptor? descriptor))
        {
            Status = $"未知的 Renderer ID：{kind}。目前仍使用 {Name}。";
            return false;
        }
        if (!descriptor.Implemented)
        {
            Status = descriptor.Description;
            return false;
        }

        if (kind == Kind)
        {
            Status = $"目前 Renderer：{Name}";
            return true;
        }

        // Construct first. If shader compilation or GPU allocation fails, the previous
        // renderer remains usable and no half-switched state leaks into the game.
        IRenderBackend replacement;
        try
        {
            if (kind == RenderBackendKind.HardwareRayTracing && _cachedHardwareRayTracing is not null)
            {
                replacement = _cachedHardwareRayTracing;
                _cachedHardwareRayTracing = null;
                replacement.Resize(_width, _height, _renderScale);
            }
            else
            {
                replacement = _backendFactory(kind);
            }
        }
        catch (Exception exception)
        {
            Status = $"無法啟動 {descriptor.ChineseDisplayName}：{exception.Message}。目前仍使用 {Name}。";
            return false;
        }

        IRenderBackend previous = _active;
        _active = replacement;
        if (previous.Kind == RenderBackendKind.HardwareRayTracing)
            _cachedHardwareRayTracing = previous;
        else
            previous.Dispose();
        Status = $"目前 Renderer：{_active.Name}";
        return true;
    }

    public bool TrySwitch(string backendId) =>
        Enum.TryParse(backendId, ignoreCase: true, out RenderBackendKind kind) && Switch(kind);

    public void ResetHistory() => ResetAccumulation();

    public void Resize(int width, int height, float renderScale)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _renderScale = Math.Clamp(renderScale, 0.35f, 1f);
        _active.Resize(_width, _height, _renderScale);
    }

    public void ResetAccumulation() => _active.ResetAccumulation();

    public void Render(
        Vector3 cameraPosition,
        Camera camera,
        VoxelHit? selection,
        float timeOfDay,
        RenderQualitySettings quality,
        float exposure) =>
        _active.Render(cameraPosition, camera, selection, timeOfDay, quality, exposure);

    public MaterialTileUv GetMaterialTileUv(ushort materialId, MaterialFace face) =>
        PreviewSource.GetMaterialTileUv(materialId, face);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active.Dispose();
        _cachedHardwareRayTracing?.Dispose();
        _cachedHardwareRayTracing = null;
    }

    private IRenderBackend Create(RenderBackendKind kind) => kind switch
    {
        RenderBackendKind.HardwareRayTracing =>
            new DxrHardwareRenderer(_world, _blocks, _width, _height, _renderScale),
        RenderBackendKind.SoftwareRayTracing =>
            new RayTracingRenderer(_world, _blocks, _width, _height, _renderScale),
        RenderBackendKind.DeferredLighting =>
            new RasterVoxelRenderer(_world, _blocks, _width, _height, classic: false, deferred: true),
        RenderBackendKind.HybridPbrRaster =>
            new RasterVoxelRenderer(_world, _blocks, _width, _height, classic: false, hybrid: true),
        RenderBackendKind.PbrForward =>
            new RasterVoxelRenderer(_world, _blocks, _width, _height, classic: false),
        RenderBackendKind.Classic =>
            new RasterVoxelRenderer(_world, _blocks, _width, _height, classic: true),
        _ => throw new NotSupportedException(RenderBackendCatalog.Get(kind).Description),
    };
}
