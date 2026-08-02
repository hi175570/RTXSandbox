namespace RTXSandbox.Rendering;

public enum RenderBackendKind
{
    HardwareRayTracing,
    SoftwareRayTracing,
    DeferredLighting,
    PbrForward,
    Classic,
}

[Flags]
public enum RenderBackendCapabilities
{
    None = 0,
    ProgressiveAccumulation = 1 << 0,
    GlobalIllumination = 1 << 1,
    Reflections = 1 << 2,
    Transmission = 1 << 3,
    MicroVoxels = 1 << 4,
    RealTime = 1 << 5,
    LowEnd = 1 << 6,
}

public sealed record RenderBackendDescriptor(
    RenderBackendKind Kind,
    string DisplayName,
    string ChineseDisplayName,
    string Description,
    bool Implemented,
    RenderBackendCapabilities Capabilities);

public static class RenderBackendCatalog
{
    public static IReadOnlyList<RenderBackendDescriptor> All { get; } = new[]
    {
        new RenderBackendDescriptor(
            RenderBackendKind.HardwareRayTracing,
            "Hardware Ray Tracing",
            "硬體光線追蹤",
            "Reserved for a future DXR or Vulkan RT backend; OpenGL cannot expose the required acceleration structures.",
            false,
            RenderBackendCapabilities.GlobalIllumination | RenderBackendCapabilities.Reflections |
            RenderBackendCapabilities.Transmission | RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.SoftwareRayTracing,
            "Compute Path Tracing",
            "Compute 路徑追蹤",
            "OpenGL compute-shader path tracer. It does not require fixed-function RTX ray-tracing hardware.",
            true,
            RenderBackendCapabilities.ProgressiveAccumulation | RenderBackendCapabilities.GlobalIllumination |
            RenderBackendCapabilities.Reflections | RenderBackendCapabilities.Transmission |
            RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.DeferredLighting,
            "Deferred Lighting",
            "延遲光照",
            "Planned GBuffer backend; unavailable until chunk meshes and the lighting pass are complete.",
            false,
            RenderBackendCapabilities.RealTime | RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.PbrForward,
            "PBR Forward",
            "PBR 即時渲染",
            "Real-time raster backend with procedural textures, sunlight, Fresnel sky reflections and transparency.",
            true,
            RenderBackendCapabilities.RealTime | RenderBackendCapabilities.Reflections |
            RenderBackendCapabilities.Transmission | RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.Classic,
            "Classic",
            "經典低負載",
            "Low-cost textured raster mode for compatibility and editor work.",
            true,
            RenderBackendCapabilities.RealTime | RenderBackendCapabilities.LowEnd |
            RenderBackendCapabilities.MicroVoxels),
    };

    public static IReadOnlyList<RenderBackendDescriptor> Implemented { get; } =
        All.Where(descriptor => descriptor.Implemented).ToArray();

    public static bool IsImplemented(RenderBackendKind kind) =>
        All.FirstOrDefault(descriptor => descriptor.Kind == kind)?.Implemented == true;

    public static RenderBackendDescriptor Get(RenderBackendKind kind) =>
        All.First(descriptor => descriptor.Kind == kind);
}
