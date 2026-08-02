using System.Numerics;
using RTXSandbox.Gameplay;
using RTXSandbox.World;

namespace RTXSandbox.Rendering;

public interface IRenderBackend : IDisposable
{
    RenderBackendKind Kind { get; }
    RenderBackendCapabilities Capabilities { get; }
    string Name { get; }
    int AccumulatedSamples { get; }
    void Resize(int width, int height, float renderScale);
    void ResetAccumulation();
    void Render(
        Vector3 cameraPosition,
        Camera camera,
        VoxelHit? selection,
        float timeOfDay,
        int pathBounces,
        float exposure);
}

public interface IMaterialPreviewSource
{
    int MaterialAtlasTexture { get; }
    int MaterialTextureResolution { get; }
    MaterialTileUv GetMaterialTileUv(ushort materialId, MaterialFace face);
}
