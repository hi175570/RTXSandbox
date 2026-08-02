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
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _renderScale = Math.Clamp(renderScale, 0.35f, 1f);
        if (!RenderBackendCatalog.IsImplemented(initialKind))
        {
            initialKind = RenderBackendKind.SoftwareRayTracing;
        }

        _active = Create(initialKind);
        Status = $"Active renderer: {_active.Name}";
    }

    public bool Switch(RenderBackendKind kind)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RenderBackendHost));
        RenderBackendDescriptor descriptor = RenderBackendCatalog.Get(kind);
        if (!descriptor.Implemented)
        {
            Status = descriptor.Description;
            return false;
        }

        if (kind == Kind)
        {
            Status = $"Active renderer: {Name}";
            return true;
        }

        // Construct first. If shader compilation or GPU allocation fails, the previous
        // renderer remains usable and no half-switched state leaks into the game.
        IRenderBackend replacement;
        try
        {
            replacement = Create(kind);
        }
        catch (Exception exception)
        {
            Status = $"Could not start {descriptor.DisplayName}: {exception.Message}";
            return false;
        }

        IRenderBackend previous = _active;
        _active = replacement;
        previous.Dispose();
        Status = $"Active renderer: {_active.Name}";
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
        int pathBounces,
        float exposure) =>
        _active.Render(cameraPosition, camera, selection, timeOfDay, pathBounces, exposure);

    public MaterialTileUv GetMaterialTileUv(ushort materialId, MaterialFace face) =>
        PreviewSource.GetMaterialTileUv(materialId, face);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active.Dispose();
    }

    private IRenderBackend Create(RenderBackendKind kind) => kind switch
    {
        RenderBackendKind.SoftwareRayTracing =>
            new RayTracingRenderer(_world, _blocks, _width, _height, _renderScale),
        RenderBackendKind.PbrForward =>
            new RasterVoxelRenderer(_world, _blocks, _width, _height, classic: false),
        RenderBackendKind.Classic =>
            new RasterVoxelRenderer(_world, _blocks, _width, _height, classic: true),
        _ => throw new NotSupportedException(RenderBackendCatalog.Get(kind).Description),
    };
}
