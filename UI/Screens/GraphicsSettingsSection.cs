using ImGuiNET;
using RTXSandbox.Rendering;

namespace RTXSandbox;

/// <summary>Graphics settings section kept outside the game loop's main source file.</summary>
public sealed partial class SandboxGame
{
    private void DrawRendererSelector()
    {
        RenderBackendDescriptor active = RenderBackendCatalog.Get(_renderer.Kind);
        ImGui.Text($"渲染器：{active.ChineseDisplayName}");

        if (ImGui.BeginCombo("圖形模式", active.ChineseDisplayName))
        {
            foreach (RenderBackendDescriptor descriptor in RenderBackendCatalog.All)
            {
                if (!descriptor.Implemented) ImGui.BeginDisabled();
                bool selected = descriptor.Kind == _renderer.Kind;
                string label = descriptor.Implemented
                    ? descriptor.ChineseDisplayName
                    : $"{descriptor.ChineseDisplayName}（規劃中）";
                if (ImGui.Selectable(label, selected) && descriptor.Implemented)
                {
                    if (_renderer.Switch(descriptor.Kind))
                    {
                        _settings.Renderer = descriptor.Kind;
                        _renderer.ResetAccumulation();
                    }
                }
                if (selected) ImGui.SetItemDefaultFocus();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(descriptor.Description);
                if (!descriptor.Implemented) ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }

        string execution = _renderer.Capabilities.HasFlag(RenderBackendCapabilities.ProgressiveAccumulation)
            ? "漸進式累積"
            : "即時繪製";
        ImGui.SameLine();
        ImGui.TextDisabled(execution);
        ImGui.TextWrapped(_renderer.Status);
    }

    private void DrawCommonPipelineSettings()
    {
        ImGui.SeparatorText("顯示");
        ImGui.Text($"解析度：{ClientSize.X} × {ClientSize.Y}");
        float scale = _settings.RenderScale;
        if (ImGui.SliderFloat("渲染比例", ref scale, 0.35f, 1f, "%.2f"))
        {
            _settings.RenderScale = scale;
            _renderer.Resize(ClientSize.X, ClientSize.Y, scale);
        }

        bool vsync = VSync == OpenTK.Windowing.Common.VSyncMode.On;
        if (ImGui.Checkbox("垂直同步（VSync）", ref vsync))
        {
            VSync = vsync ? OpenTK.Windowing.Common.VSyncMode.On : OpenTK.Windowing.Common.VSyncMode.Off;
            _settings.VSync = vsync;
        }

        int fpsLimit = _settings.FpsLimit;
        if (ImGui.SliderInt("幀率上限", ref fpsLimit, 30, 360))
        {
            _settings.FpsLimit = fpsLimit;
            UpdateFrequency = fpsLimit;
        }

        ImGui.SeparatorText("攝影機");
        float fieldOfView = _camera.FieldOfViewDegrees;
        if (ImGui.SliderFloat("視野角度", ref fieldOfView, 45f, 105f, "%.0f°"))
        {
            _camera.FieldOfViewDegrees = fieldOfView;
            _settings.FieldOfView = fieldOfView;
            _renderer.ResetAccumulation();
        }

        ImGui.SeparatorText("光照與後製");
        float exposure = _settings.Exposure;
        if (ImGui.SliderFloat("曝光", ref exposure, 0.35f, 3f, "%.2f"))
        {
            _settings.Exposure = exposure;
            _renderer.ResetAccumulation();
        }
    }

    private void DrawActivePipelineAdvancedSettings()
    {
        if (!ImGui.CollapsingHeader("進階品質設定", ImGuiTreeNodeFlags.DefaultOpen)) return;

        switch (_renderer.Kind)
        {
            case RenderBackendKind.SoftwareRayTracing:
                int bounces = _settings.PathBounces;
                if (ImGui.SliderInt("最大光線反彈次數", ref bounces, 1, 12))
                {
                    _settings.PathBounces = bounces;
                    _renderer.ResetAccumulation();
                }
                int samplesPerFrame = _settings.PathSamplesPerFrame;
                if (ImGui.SliderInt("每幀採樣數", ref samplesPerFrame, 1, 8))
                {
                    _settings.PathSamplesPerFrame = samplesPerFrame;
                    _renderer.ResetAccumulation();
                }
                int maximumSamples = _settings.PathMaxAccumulatedSamples;
                if (ImGui.SliderInt("最大累計採樣數", ref maximumSamples, 64, 16384))
                {
                    _settings.PathMaxAccumulatedSamples = maximumSamples;
                    _renderer.ResetAccumulation();
                }
                ImGui.TextDisabled("提高反彈與採樣數會增加畫質，也會提高 GPU 負擔。");
                break;

            case RenderBackendKind.HybridPbrRaster:
                ImGui.TextDisabled("高畫質光柵：延遲 PBR 不透明光照 + 前向水與透明材質；最低品質固定為 2。 ");
                goto case RenderBackendKind.DeferredLighting;

            case RenderBackendKind.DeferredLighting:
                int rendererQuality = _settings.DeferredRendererQuality;
                if (ImGui.SliderInt("Renderer Quality", ref rendererQuality, 0, 3))
                    _settings.DeferredRendererQuality = rendererQuality;
                int gBufferQuality = _settings.DeferredGBufferQuality;
                if (ImGui.SliderInt("G-Buffer Quality", ref gBufferQuality, 0, 3))
                    _settings.DeferredGBufferQuality = gBufferQuality;
                int lightingQuality = _settings.DeferredLightingQuality;
                if (ImGui.SliderInt("Lighting Quality", ref lightingQuality, 0, 3))
                    _settings.DeferredLightingQuality = lightingQuality;
                int shadowQuality = _settings.DeferredShadowQuality;
                if (ImGui.SliderInt("Shadow Quality", ref shadowQuality, 0, 3))
                    _settings.DeferredShadowQuality = shadowQuality;
                int deferredSpecular = _settings.DeferredSpecularQuality;
                if (ImGui.SliderInt("高光品質", ref deferredSpecular, 0, 3))
                    _settings.DeferredSpecularQuality = deferredSpecular;
                int deferredReflections = _settings.DeferredReflectionQuality;
                if (ImGui.SliderInt("反射品質", ref deferredReflections, 0, 3))
                    _settings.DeferredReflectionQuality = deferredReflections;
                int aoQuality = _settings.DeferredAmbientOcclusionQuality;
                if (ImGui.SliderInt("Ambient Occlusion Quality", ref aoQuality, 0, 3))
                    _settings.DeferredAmbientOcclusionQuality = aoQuality;
                int waterQuality = _settings.DeferredWaterQuality;
                if (ImGui.SliderInt("Water Quality", ref waterQuality, 0, 3))
                    _settings.DeferredWaterQuality = waterQuality;
                int transparencyQuality = _settings.DeferredTransparencyQuality;
                if (ImGui.SliderInt("Transparency Quality", ref transparencyQuality, 0, 3))
                    _settings.DeferredTransparencyQuality = transparencyQuality;
                int fogQuality = _settings.DeferredFogQuality;
                if (ImGui.SliderInt("Fog Quality", ref fogQuality, 0, 3))
                    _settings.DeferredFogQuality = fogQuality;
                int postQuality = _settings.DeferredPostProcessingQuality;
                if (ImGui.SliderInt("Post Processing Quality", ref postQuality, 0, 3))
                    _settings.DeferredPostProcessingQuality = postQuality;
                bool hdr = _settings.DeferredHdr;
                if (ImGui.Checkbox("HDR", ref hdr)) _settings.DeferredHdr = hdr;
                bool bloom = _settings.DeferredBloom;
                if (ImGui.Checkbox("Bloom", ref bloom)) _settings.DeferredBloom = bloom;
                string[] toneMappingNames = { "Reinhard", "Filmic", "ACES" };
                int toneMapping = _settings.DeferredToneMapping;
                if (ImGui.Combo("Tone Mapping", ref toneMapping, toneMappingNames, toneMappingNames.Length))
                    _settings.DeferredToneMapping = toneMapping;
                string[] debugNames = { "Final Image", "Albedo", "Normal", "Depth", "Metallic", "Roughness", "AO", "Emission", "Lighting", "Motion Vector" };
                int debugView = (int)_settings.DeferredDebugView;
                if (ImGui.Combo("Debug View", ref debugView, debugNames, debugNames.Length))
                    _settings.DeferredDebugView = (DeferredDebugView)debugView;
                break;

            case RenderBackendKind.PbrForward:
                int pbrSpecular = _settings.PbrSpecularQuality;
                if (ImGui.SliderInt("高光品質", ref pbrSpecular, 0, 3))
                    _settings.PbrSpecularQuality = pbrSpecular;
                int pbrReflections = _settings.PbrReflectionQuality;
                if (ImGui.SliderInt("反射品質", ref pbrReflections, 0, 3))
                    _settings.PbrReflectionQuality = pbrReflections;
                break;

            case RenderBackendKind.Classic:
                int classicLighting = _settings.ClassicLightingQuality;
                if (ImGui.SliderInt("光照品質", ref classicLighting, 0, 2))
                    _settings.ClassicLightingQuality = classicLighting;
                break;

            case RenderBackendKind.HardwareRayTracing:
                int hardwareQuality = _settings.HardwareRayTracingQuality;
                if (ImGui.SliderInt("硬體光追品質", ref hardwareQuality, 0, 3))
                    _settings.HardwareRayTracingQuality = hardwareQuality;
                ImGui.TextDisabled(hardwareQuality switch
                {
                    0 => "主射線與基本材質（最低負載）",
                    1 => "主射線 + RTX 陰影射線",
                    2 => "RTX 陰影 + 選擇性反射射線",
                    _ => "RTX 陰影 + 廣泛反射射線（最高負載）",
                });
                string[] renderStyles = { "Default", "Wax Mode（打蠟模式）", "Reference（物理正確）", "Debug" };
                int renderStyle = (int)_settings.HardwareRayTracingRenderStyle;
                if (ImGui.Combo("Render Style", ref renderStyle, renderStyles, renderStyles.Length))
                    _settings.HardwareRayTracingRenderStyle = (HardwareRayTracingRenderStyle)renderStyle;
                ImGui.TextDisabled(_settings.HardwareRayTracingRenderStyle == HardwareRayTracingRenderStyle.WaxMode
                    ? "提高所有實心材質反射率；非金屬仍保留底色，不會全部變成鏡子。"
                    : "Default、物理參考與材質 Debug 均不啟用打蠟增益。");
                ImGui.BeginDisabled();
                bool denoiser = _settings.HardwareRayTracingDenoiser;
                if (ImGui.Checkbox("硬體光追降噪", ref denoiser))
                    _settings.HardwareRayTracingDenoiser = denoiser;
                ImGui.EndDisabled();
                ImGui.TextDisabled("降噪尚未實作。若 GPU 負載過高，請降低品質或渲染比例。");
                break;
        }
    }
}
