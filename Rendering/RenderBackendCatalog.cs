namespace RTXSandbox.Rendering;

using System.Diagnostics.CodeAnalysis;
using RTXSandbox.Rendering.Backends;

public enum RenderBackendKind
{
    HardwareRayTracing,
    SoftwareRayTracing,
    DeferredLighting,
    HybridPbrRaster,
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
    private static readonly (bool Supported, string Status) HardwareRayTracing = ProbeHardwareRayTracing();
    private static readonly bool DesktopComputeSupported = !OperatingSystem.IsMacOS();
    public static string HardwareRayTracingStatus => HardwareRayTracing.Status;

    public static IReadOnlyList<RenderBackendDescriptor> All { get; } = new[]
    {
        new RenderBackendDescriptor(
            RenderBackendKind.Classic,
            "Classic",
            "經典低負載",
            "Low-cost textured raster mode for compatibility and editor work.",
            true,
            RenderBackendCapabilities.RealTime | RenderBackendCapabilities.LowEnd |
            RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.PbrForward,
            "PBR Forward",
            "PBR 高品質前向光影",
            "High-quality forward PBR with dynamic shadows, emissive lights, HDR atmosphere, screen-space reflections and water.",
            true,
            RenderBackendCapabilities.RealTime | RenderBackendCapabilities.Reflections |
            RenderBackendCapabilities.Transmission | RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.DeferredLighting,
            "Deferred Lighting",
            "延遲光照",
            "Five-target GBuffer lighting with a forward transparency pass for water and glass.",
            true,
            RenderBackendCapabilities.RealTime | RenderBackendCapabilities.Reflections |
            RenderBackendCapabilities.Transmission | RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.HybridPbrRaster,
            "Hybrid PBR Raster",
            "混合式 PBR 光柵",
            "High-quality raster pipeline: deferred PBR opaque lighting, shadows, SSR, bloom, plus forward water and transparency.",
            true,
            RenderBackendCapabilities.RealTime | RenderBackendCapabilities.Reflections |
            RenderBackendCapabilities.Transmission | RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.SoftwareRayTracing,
            "Compute Path Tracing",
            "Compute 路徑追蹤",
            DesktopComputeSupported
                ? "OpenGL compute-shader path tracer. It does not require fixed-function RTX ray-tracing hardware."
                : "Unavailable: macOS exposes OpenGL 4.1, while this compute path requires OpenGL 4.3.",
            DesktopComputeSupported,
            RenderBackendCapabilities.ProgressiveAccumulation | RenderBackendCapabilities.GlobalIllumination |
            RenderBackendCapabilities.Reflections | RenderBackendCapabilities.Transmission |
            RenderBackendCapabilities.MicroVoxels),
        new RenderBackendDescriptor(
            RenderBackendKind.HardwareRayTracing,
            "Hardware Ray Tracing",
            "硬體光線追蹤",
            $"Direct3D 12 DXR V1. {HardwareRayTracingStatus}",
            HardwareRayTracing.Supported,
            RenderBackendCapabilities.RealTime | RenderBackendCapabilities.Reflections |
            RenderBackendCapabilities.Transmission),
    };

    public static IReadOnlyList<RenderBackendDescriptor> Implemented { get; } =
        All.Where(descriptor => descriptor.Implemented).ToArray();

    public static bool IsImplemented(RenderBackendKind kind) =>
        TryGet(kind, out RenderBackendDescriptor? descriptor) && descriptor.Implemented;

    public static bool TryGet(
        RenderBackendKind kind,
        [NotNullWhen(true)] out RenderBackendDescriptor? descriptor)
    {
        descriptor = All.FirstOrDefault(candidate => candidate.Kind == kind);
        return descriptor is not null;
    }

    public static RenderBackendDescriptor Get(RenderBackendKind kind) =>
        TryGet(kind, out RenderBackendDescriptor? descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的 Renderer ID。");

    private static (bool Supported, string Status) ProbeHardwareRayTracing()
    {
        bool supported = DxrHardwareRenderer.IsSupported(out string status);
        return (supported, status);
    }
}
