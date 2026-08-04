namespace RTXSandbox.Rendering;

public enum DeferredDebugView
{
    FinalImage,
    Albedo,
    Normal,
    Depth,
    Metallic,
    Roughness,
    AmbientOcclusion,
    Emission,
    Lighting,
    MotionVectors,
}

public enum HardwareRayTracingRenderStyle
{
    Default,
    WaxMode,
    Reference,
    Debug,
}

public readonly record struct RenderQualitySettings(
    int PathBounces,
    int PathSamplesPerFrame,
    int PathMaxAccumulatedSamples,
    int DeferredSpecularQuality,
    int DeferredReflectionQuality,
    int DeferredRendererQuality,
    int DeferredGBufferQuality,
    int DeferredLightingQuality,
    int DeferredShadowQuality,
    int DeferredAmbientOcclusionQuality,
    int DeferredWaterQuality,
    int DeferredTransparencyQuality,
    int DeferredFogQuality,
    int DeferredPostProcessingQuality,
    bool DeferredHdr,
    bool DeferredBloom,
    int DeferredToneMapping,
    DeferredDebugView DeferredDebugView,
    int PbrSpecularQuality,
    int PbrReflectionQuality,
    int ClassicLightingQuality,
    int HardwareRayTracingQuality,
    HardwareRayTracingRenderStyle HardwareRayTracingRenderStyle);
