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
        // The live camera may include the temporary sprint animation. The slider
        // edits the stable base FOV so opening Settings while sprinting cannot
        // permanently save the widened value.
        float fieldOfView = _settings.FieldOfView;
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

    private void DrawSelectionReflectionSetting()
    {
        ImGui.SeparatorText("共用反射");
        bool enabled = _settings.SelectionOutlineReflections;
        if (ImGui.Checkbox("選取框參與反射", ref enabled))
        {
            _settings.SelectionOutlineReflections = enabled;
            _renderer.ResetAccumulation();
        }
        ImGui.TextDisabled("套用於會追蹤或重投影場景內容的渲染管線。");
    }

    private void DrawActivePipelineAdvancedSettings()
    {
        if (!ImGui.CollapsingHeader("光影設定", ImGuiTreeNodeFlags.DefaultOpen)) return;

        switch (_renderer.Kind)
        {
            case RenderBackendKind.SoftwareRayTracing:
                int bounces = _settings.PathBounces;
                if (ImGui.SliderInt("最大光線反彈次數", ref bounces, 1, 50))
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
                float pathRayDistance = _settings.PathRayDistance;
                if (ImGui.SliderFloat("射線追蹤距離", ref pathRayDistance, 32f, 220f, "%.0f 格"))
                {
                    _settings.PathRayDistance = pathRayDistance;
                    _renderer.ResetAccumulation();
                }
                float pathDirectLight = _settings.PathDirectLightingStrength;
                if (ImGui.SliderFloat("直接光照強度", ref pathDirectLight, 0f, 2f, "%.2f"))
                {
                    _settings.PathDirectLightingStrength = pathDirectLight;
                    _renderer.ResetAccumulation();
                }
                float pathEnvironment = _settings.PathEnvironmentIntensity;
                if (ImGui.SliderFloat("天空環境強度", ref pathEnvironment, 0f, 2f, "%.2f"))
                {
                    _settings.PathEnvironmentIntensity = pathEnvironment;
                    _renderer.ResetAccumulation();
                }
                float pathEmission = _settings.PathEmissionIntensity;
                if (ImGui.SliderFloat("自發光強度", ref pathEmission, 0f, 2f, "%.2f"))
                {
                    _settings.PathEmissionIntensity = pathEmission;
                    _renderer.ResetAccumulation();
                }
                float pathReflections = _settings.PathReflectionStrength;
                if (ImGui.SliderFloat("路徑反射取樣強度", ref pathReflections, 0f, 2f, "%.2f"))
                {
                    _settings.PathReflectionStrength = pathReflections;
                    _renderer.ResetAccumulation();
                }
                int rouletteStart = _settings.PathRussianRouletteStart;
                if (ImGui.SliderInt("俄羅斯輪盤起始反彈", ref rouletteStart, 1, 12))
                {
                    _settings.PathRussianRouletteStart = rouletteStart;
                    _renderer.ResetAccumulation();
                }
                ImGui.TextDisabled("提高反彈與採樣數會增加畫質，也會提高 GPU 負擔。");
                break;

            case RenderBackendKind.HybridPbrRaster:
            {
                ImGui.TextDisabled("混合光柵：Deferred PBR 不透明光照 + Forward 水與透明材質；設定不與 Deferred 共用。");
                int hybridLighting = _settings.HybridLightingQuality;
                if (ImGui.SliderInt("混合式光照品質", ref hybridLighting, 0, 3))
                    _settings.HybridLightingQuality = hybridLighting;
                int hybridSpecular = _settings.HybridSpecularQuality;
                if (ImGui.SliderInt("混合式高光品質", ref hybridSpecular, 0, 3))
                    _settings.HybridSpecularQuality = hybridSpecular;
                int hybridShadow = _settings.HybridShadowQuality;
                if (ImGui.SliderInt("混合式陰影品質", ref hybridShadow, 0, 3))
                    _settings.HybridShadowQuality = hybridShadow;
                string[] hybridShadowResolutions = { "1024 × 1024", "2048 × 2048", "4096 × 4096" };
                int hybridShadowResolution = _settings.HybridShadowResolution switch
                {
                    <= 1024 => 0,
                    <= 2048 => 1,
                    _ => 2,
                };
                if (ImGui.Combo("混合式陰影解析度", ref hybridShadowResolution,
                        hybridShadowResolutions, hybridShadowResolutions.Length))
                    _settings.HybridShadowResolution = new[] { 1024, 2048, 4096 }[hybridShadowResolution];
                int hybridReflection = _settings.HybridReflectionQuality;
                if (ImGui.SliderInt("混合式螢幕反射品質", ref hybridReflection, 0, 3))
                    _settings.HybridReflectionQuality = hybridReflection;
                int hybridAo = _settings.HybridAmbientOcclusionQuality;
                if (ImGui.SliderInt("混合式環境遮蔽品質", ref hybridAo, 0, 3))
                    _settings.HybridAmbientOcclusionQuality = hybridAo;
                int hybridWater = _settings.HybridWaterQuality;
                if (ImGui.SliderInt("混合式水面品質", ref hybridWater, 0, 3))
                    _settings.HybridWaterQuality = hybridWater;
                int hybridTransparency = _settings.HybridTransparencyQuality;
                if (ImGui.SliderInt("混合式透明材質品質", ref hybridTransparency, 0, 3))
                    _settings.HybridTransparencyQuality = hybridTransparency;
                int hybridFog = _settings.HybridFogQuality;
                if (ImGui.SliderInt("混合式霧品質", ref hybridFog, 0, 3))
                    _settings.HybridFogQuality = hybridFog;
                bool hybridBloom = _settings.HybridBloom;
                if (ImGui.Checkbox("混合式 HDR Bloom", ref hybridBloom))
                    _settings.HybridBloom = hybridBloom;
                break;
            }

            case RenderBackendKind.DeferredLighting:
                string[] presetNames = { "效能", "平衡", "品質", "自訂" };
                int preset = (int)_settings.DeferredQualityPreset;
                if (ImGui.Combo("Deferred 品質預設", ref preset, presetNames, presetNames.Length))
                {
                    _settings.DeferredQualityPreset = (DeferredQualityPreset)preset;
                    ApplyDeferredQualityPreset(_settings.DeferredQualityPreset);
                }
                int rendererQuality = _settings.DeferredRendererQuality;
                if (ImGui.SliderInt("整體渲染品質", ref rendererQuality, 0, 3))
                {
                    _settings.DeferredRendererQuality = rendererQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int gBufferQuality = _settings.DeferredGBufferQuality;
                if (ImGui.SliderInt("G-Buffer 品質", ref gBufferQuality, 0, 3))
                {
                    _settings.DeferredGBufferQuality = gBufferQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int lightingQuality = _settings.DeferredLightingQuality;
                if (ImGui.SliderInt("光照品質", ref lightingQuality, 0, 3))
                {
                    _settings.DeferredLightingQuality = lightingQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int shadowQuality = _settings.DeferredShadowQuality;
                if (ImGui.SliderInt("陰影品質", ref shadowQuality, 0, 3))
                {
                    _settings.DeferredShadowQuality = shadowQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                string[] shadowResolutionNames = { "1024²", "2048²", "4096²" };
                int shadowResolution = _settings.DeferredShadowResolution switch { 1024 => 0, 4096 => 2, _ => 1 };
                if (ImGui.Combo("陰影解析度", ref shadowResolution,
                        shadowResolutionNames, shadowResolutionNames.Length))
                {
                    _settings.DeferredShadowResolution = shadowResolution switch { 0 => 1024, 2 => 4096, _ => 2048 };
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                float shadowDistance = _settings.DeferredShadowDistance;
                if (ImGui.SliderFloat("陰影距離", ref shadowDistance, 48f, 160f, "%.0f 格"))
                {
                    _settings.DeferredShadowDistance = shadowDistance;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                float shadowBias = _settings.DeferredShadowBias;
                if (ImGui.SliderFloat("陰影固定 Bias", ref shadowBias, 0.00005f, 0.003f, "%.5f"))
                {
                    _settings.DeferredShadowBias = shadowBias;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                float shadowNormalBias = _settings.DeferredShadowNormalBias;
                if (ImGui.SliderFloat("陰影法線 Bias", ref shadowNormalBias, 0f, 0.12f, "%.3f"))
                {
                    _settings.DeferredShadowNormalBias = shadowNormalBias;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                float shadowSlopeBias = _settings.DeferredShadowSlopeBias;
                if (ImGui.SliderFloat("陰影斜率 Bias", ref shadowSlopeBias, 0f, 0.006f, "%.5f"))
                {
                    _settings.DeferredShadowSlopeBias = shadowSlopeBias;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                float shadowSoftness = _settings.DeferredShadowSoftness;
                if (ImGui.SliderFloat("陰影柔和度", ref shadowSoftness, 0.5f, 3f, "%.2f"))
                {
                    _settings.DeferredShadowSoftness = shadowSoftness;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int deferredSpecular = _settings.DeferredSpecularQuality;
                if (ImGui.SliderInt("高光品質", ref deferredSpecular, 0, 3))
                {
                    _settings.DeferredSpecularQuality = deferredSpecular;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int deferredReflections = _settings.DeferredReflectionQuality;
                if (ImGui.SliderInt("反射品質", ref deferredReflections, 0, 3))
                {
                    _settings.DeferredReflectionQuality = deferredReflections;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int aoQuality = _settings.DeferredAmbientOcclusionQuality;
                if (ImGui.SliderInt("環境遮蔽品質", ref aoQuality, 0, 3))
                {
                    _settings.DeferredAmbientOcclusionQuality = aoQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int waterQuality = _settings.DeferredWaterQuality;
                if (ImGui.SliderInt("水面品質", ref waterQuality, 0, 3))
                {
                    _settings.DeferredWaterQuality = waterQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int transparencyQuality = _settings.DeferredTransparencyQuality;
                if (ImGui.SliderInt("透明材質品質", ref transparencyQuality, 0, 3))
                {
                    _settings.DeferredTransparencyQuality = transparencyQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int fogQuality = _settings.DeferredFogQuality;
                if (ImGui.SliderInt("霧品質", ref fogQuality, 0, 3))
                {
                    _settings.DeferredFogQuality = fogQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                int postQuality = _settings.DeferredPostProcessingQuality;
                if (ImGui.SliderInt("後製品質", ref postQuality, 0, 3))
                {
                    _settings.DeferredPostProcessingQuality = postQuality;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                bool hdr = _settings.DeferredHdr;
                if (ImGui.Checkbox("HDR", ref hdr))
                {
                    _settings.DeferredHdr = hdr;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                bool bloom = _settings.DeferredBloom;
                if (ImGui.Checkbox("Bloom", ref bloom))
                {
                    _settings.DeferredBloom = bloom;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                string[] toneMappingNames = { "Reinhard", "Filmic", "ACES" };
                int toneMapping = _settings.DeferredToneMapping;
                if (ImGui.Combo("色調映射", ref toneMapping, toneMappingNames, toneMappingNames.Length))
                {
                    _settings.DeferredToneMapping = toneMapping;
                    _settings.DeferredQualityPreset = DeferredQualityPreset.Custom;
                }
                string[] debugNames =
                {
                    "最終畫面", "Albedo", "法線", "線性深度", "金屬度", "粗糙度", "環境遮蔽", "自發光",
                    "總光照", "Motion Vector", "世界座標", "直接光照", "間接光照", "方向陰影",
                    "螢幕空間反射", "反射可信度", "NaN／Infinity", "材質 ID", "像素光源數",
                };
                int debugView = (int)_settings.DeferredDebugView;
                if (ImGui.Combo("Deferred Debug View（F4）", ref debugView, debugNames, debugNames.Length))
                    _settings.DeferredDebugView = (DeferredDebugView)debugView;
                ImGui.TextDisabled("F4 可循環切換；非最終畫面時會在右上角顯示目前通道。");
                break;

            case RenderBackendKind.PbrForward:
            {
                ImGui.TextDisabled("Forward 核心：HDR 前向 PBR + 陰影 + 螢幕空間合成（不是 Deferred）。");
                string[] forwardPresetNames = { "Potato／低", "Medium／中", "High／高", "Ultra／極致", "自訂" };
                int forwardPreset = (int)_settings.ForwardQualityPreset;
                if (ImGui.Combo("Forward 畫質預設", ref forwardPreset, forwardPresetNames, forwardPresetNames.Length))
                    ApplyForwardQualityPreset((ForwardQualityPreset)forwardPreset);

                bool pbr = _settings.ForwardPbr;
                if (ImGui.Checkbox("PBR 材質", ref pbr))
                {
                    _settings.ForwardPbr = pbr;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                bool shadows = _settings.ForwardShadows;
                if (ImGui.Checkbox("動態太陽陰影", ref shadows))
                {
                    _settings.ForwardShadows = shadows;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                bool ao = _settings.ForwardAmbientOcclusion;
                if (ImGui.Checkbox("螢幕空間環境遮蔽", ref ao))
                {
                    _settings.ForwardAmbientOcclusion = ao;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                bool ssr = _settings.ForwardScreenSpaceReflections;
                if (ImGui.Checkbox("螢幕空間反射", ref ssr))
                {
                    _settings.ForwardScreenSpaceReflections = ssr;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                bool forwardBloom = _settings.ForwardBloom;
                if (ImGui.Checkbox("HDR Bloom", ref forwardBloom))
                {
                    _settings.ForwardBloom = forwardBloom;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                bool volumetric = _settings.ForwardVolumetric;
                if (ImGui.Checkbox("大氣霧與光柱", ref volumetric))
                {
                    _settings.ForwardVolumetric = volumetric;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                bool water = _settings.ForwardWaterEffects;
                if (ImGui.Checkbox("高品質水面", ref water))
                {
                    _settings.ForwardWaterEffects = water;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                bool gi = _settings.ForwardApproximateGi;
                if (ImGui.Checkbox("便宜 GI 近似", ref gi))
                {
                    _settings.ForwardApproximateGi = gi;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                bool taa = _settings.ForwardTemporalAntiAliasing;
                if (ImGui.Checkbox("TAA（重投影＋鄰域限制）", ref taa))
                {
                    _settings.ForwardTemporalAntiAliasing = taa;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                    _renderer.ResetAccumulation();
                }

                int pbrSpecular = _settings.PbrSpecularQuality;
                if (ImGui.SliderInt("高光品質", ref pbrSpecular, 0, 3))
                {
                    _settings.PbrSpecularQuality = pbrSpecular;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                int pbrReflections = _settings.PbrReflectionQuality;
                if (ImGui.SliderInt("反射品質", ref pbrReflections, 0, 3))
                {
                    _settings.PbrReflectionQuality = pbrReflections;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                int forwardShadowQuality = _settings.ForwardShadowQuality;
                if (ImGui.SliderInt("陰影濾波品質", ref forwardShadowQuality, 0, 3))
                {
                    _settings.ForwardShadowQuality = forwardShadowQuality;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                string[] shadowResolutions = { "1024 × 1024", "2048 × 2048", "4096 × 4096" };
                int shadowResolutionIndex = _settings.ForwardShadowResolution switch { <= 1024 => 0, <= 2048 => 1, _ => 2 };
                if (ImGui.Combo("陰影解析度", ref shadowResolutionIndex, shadowResolutions, shadowResolutions.Length))
                {
                    _settings.ForwardShadowResolution = new[] { 1024, 2048, 4096 }[shadowResolutionIndex];
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                float forwardShadowDistance = _settings.ForwardShadowDistance;
                if (ImGui.SliderFloat("陰影距離", ref forwardShadowDistance, 48f, 160f, "%.0f"))
                {
                    _settings.ForwardShadowDistance = forwardShadowDistance;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                float forwardShadowSoftness = _settings.ForwardShadowSoftness;
                if (ImGui.SliderFloat("陰影柔和度", ref forwardShadowSoftness, 0.5f, 3f, "%.2f"))
                {
                    _settings.ForwardShadowSoftness = forwardShadowSoftness;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                int forwardAoQuality = _settings.ForwardAmbientOcclusionQuality;
                if (ImGui.SliderInt("AO 品質", ref forwardAoQuality, 0, 3))
                {
                    _settings.ForwardAmbientOcclusionQuality = forwardAoQuality;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                int forwardWaterQuality = _settings.ForwardWaterQuality;
                if (ImGui.SliderInt("水面品質", ref forwardWaterQuality, 0, 3))
                {
                    _settings.ForwardWaterQuality = forwardWaterQuality;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                int volumetricQuality = _settings.ForwardVolumetricQuality;
                if (ImGui.SliderInt("大氣品質", ref volumetricQuality, 0, 3))
                {
                    _settings.ForwardVolumetricQuality = volumetricQuality;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                int forwardPostQuality = _settings.ForwardPostProcessingQuality;
                if (ImGui.SliderInt("後製品質", ref forwardPostQuality, 0, 3))
                {
                    _settings.ForwardPostProcessingQuality = forwardPostQuality;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                string[] forwardToneMappingNames = { "Reinhard", "Filmic", "ACES" };
                int forwardToneMapping = _settings.ForwardToneMapping;
                if (ImGui.Combo("色調映射", ref forwardToneMapping, forwardToneMappingNames, forwardToneMappingNames.Length))
                {
                    _settings.ForwardToneMapping = forwardToneMapping;
                    _settings.ForwardQualityPreset = ForwardQualityPreset.Custom;
                }
                string[] forwardDebugNames =
                {
                    "最終畫面", "Albedo", "法線", "線性深度", "粗糙度", "金屬度", "自發光",
                    "環境遮蔽", "方向陰影", "直接光照", "天空環境光", "螢幕空間反射", "GI 近似", "NaN／Infinity",
                };
                int forwardDebugView = (int)_settings.ForwardDebugView;
                if (ImGui.Combo("Forward Debug View（F4）", ref forwardDebugView, forwardDebugNames, forwardDebugNames.Length))
                {
                    _settings.ForwardDebugView = (ForwardDebugView)forwardDebugView;
                    _renderer.ResetAccumulation();
                }
                ImGui.TextDisabled("陰影 Bias 使用穩定預設；黑點診斷可切到 NaN／Infinity 通道。");
                break;
            }

            case RenderBackendKind.Classic:
            {
                int classicLighting = _settings.ClassicLightingQuality;
                if (ImGui.SliderInt("光照品質", ref classicLighting, 0, 2))
                    _settings.ClassicLightingQuality = classicLighting;
                float classicSunlight = _settings.ClassicSunlightStrength;
                if (ImGui.SliderFloat("日光強度", ref classicSunlight, 0f, 2f, "%.2f"))
                    _settings.ClassicSunlightStrength = classicSunlight;
                float classicAmbient = _settings.ClassicAmbientStrength;
                if (ImGui.SliderFloat("環境亮度", ref classicAmbient, 0f, 2f, "%.2f"))
                    _settings.ClassicAmbientStrength = classicAmbient;
                float classicSaturation = _settings.ClassicColorSaturation;
                if (ImGui.SliderFloat("材質色彩飽和度", ref classicSaturation, 0f, 1.5f, "%.2f"))
                    _settings.ClassicColorSaturation = classicSaturation;
                int classicWater = _settings.ClassicWaterQuality;
                if (ImGui.SliderInt("水面細節", ref classicWater, 0, 3))
                    _settings.ClassicWaterQuality = classicWater;
                float classicWaves = _settings.ClassicWaterWaveStrength;
                if (ImGui.SliderFloat("水波強度", ref classicWaves, 0f, 2f, "%.2f"))
                    _settings.ClassicWaterWaveStrength = classicWaves;
                int classicTransparency = _settings.ClassicTransparencyQuality;
                if (ImGui.SliderInt("透明材質品質", ref classicTransparency, 0, 3))
                    _settings.ClassicTransparencyQuality = classicTransparency;
                float classicFog = _settings.ClassicFogDensity;
                if (ImGui.SliderFloat("距離霧濃度", ref classicFog, 0f, 1f, "%.2f"))
                    _settings.ClassicFogDensity = classicFog;
                break;
            }

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
                int reflectionBounces = _settings.HardwareRayTracingReflectionBounces;
                if (ImGui.SliderInt("RTX 反射次數", ref reflectionBounces, 1, 29))
                    _settings.HardwareRayTracingReflectionBounces = reflectionBounces;
                ImGui.TextDisabled("鏡面命中後會繼續追蹤完整 DXR 世界；預設 3 次。DXR API 上限為 29 個完整反射層。");
                float hardwareRayDistance = _settings.HardwareRayTracingRayDistance;
                if (ImGui.SliderFloat("DXR 射線距離", ref hardwareRayDistance, 48f, 240f, "%.0f 格"))
                    _settings.HardwareRayTracingRayDistance = hardwareRayDistance;
                float hardwareShadowStrength = _settings.HardwareRayTracingShadowStrength;
                if (ImGui.SliderFloat("DXR 陰影強度", ref hardwareShadowStrength, 0f, 1f, "%.2f"))
                    _settings.HardwareRayTracingShadowStrength = hardwareShadowStrength;
                float hardwareEmissionStrength = _settings.HardwareRayTracingEmissionStrength;
                if (ImGui.SliderFloat("DXR 發光照明強度", ref hardwareEmissionStrength, 0f, 2f, "%.2f"))
                    _settings.HardwareRayTracingEmissionStrength = hardwareEmissionStrength;
                float hardwareReflectionStrength = _settings.HardwareRayTracingReflectionStrength;
                if (ImGui.SliderFloat("DXR 反射強度", ref hardwareReflectionStrength, 0f, 2f, "%.2f"))
                    _settings.HardwareRayTracingReflectionStrength = hardwareReflectionStrength;
                string[] renderStyles = { "Default", "Wax Mode（打蠟模式）", "Reference（物理正確）", "Debug" };
                int renderStyle = (int)_settings.HardwareRayTracingRenderStyle;
                if (ImGui.Combo("Render Style", ref renderStyle, renderStyles, renderStyles.Length))
                    _settings.HardwareRayTracingRenderStyle = (HardwareRayTracingRenderStyle)renderStyle;
                ImGui.TextDisabled(_settings.HardwareRayTracingRenderStyle == HardwareRayTracingRenderStyle.WaxMode
                    ? "所有實心材質的有效反射強度提高至原效果 1.7 倍；仍保留非金屬底色。"
                    : "Default、物理參考與材質 Debug 均不啟用打蠟增益。");
                string[] infiniteMirrorModes =
                    { "關閉", "平衡（最多 128 層）", "品質（最多 512 層）", "極致（最多 1000 層）" };
                int infiniteMirrorQuality = (int)_settings.InfiniteMirrorQuality;
                if (ImGui.Combo("無限鏡面延伸", ref infiniteMirrorQuality,
                        infiniteMirrorModes, infiniteMirrorModes.Length))
                    _settings.InfiniteMirrorQuality = (InfiniteMirrorQuality)infiniteMirrorQuality;
                if (_settings.InfiniteMirrorQuality != InfiniteMirrorQuality.Off)
                {
                    ImGui.Text("Real DXR Reflections: 29");
                    ImGui.Text("Reprojected Reflections: 30–128");
                    ImGui.Text(_settings.InfiniteMirrorQuality == InfiniteMirrorQuality.Extreme
                        ? "Analytic Reflection Tail: 129–1000+"
                        : _settings.InfiniteMirrorQuality == InfiniteMirrorQuality.Quality
                            ? "Analytic Reflection Tail: 129–512"
                            : "Analytic Reflection Tail: 129（能量收斂）");
                    ImGui.TextDisabled("僅平面低粗糙度鏡面；30 層後為近似，不是 DXR bounce。");
                }
                ImGui.TextDisabled("降噪尚未實作，因此不顯示無效開關；渲染比例仍由上方共用設定控制。");
                break;
        }
    }

    private void ApplyDeferredQualityPreset(DeferredQualityPreset preset)
    {
        _settings.ApplyDeferredQualityPreset(preset);
        _renderer.ResetAccumulation();
    }

    private void ApplyForwardQualityPreset(ForwardQualityPreset preset)
    {
        _settings.ApplyForwardQualityPreset(preset);
        _renderer.ResetAccumulation();
    }

    private static string DeferredDebugViewDisplayName(DeferredDebugView view) => view switch
    {
        DeferredDebugView.FinalImage => "最終畫面",
        DeferredDebugView.Albedo => "Albedo",
        DeferredDebugView.Normal => "法線",
        DeferredDebugView.LinearDepth => "線性深度",
        DeferredDebugView.Metallic => "金屬度",
        DeferredDebugView.Roughness => "粗糙度",
        DeferredDebugView.AmbientOcclusion => "環境遮蔽",
        DeferredDebugView.Emission => "自發光",
        DeferredDebugView.Lighting => "總光照",
        DeferredDebugView.MotionVectors => "Motion Vector",
        DeferredDebugView.WorldPosition => "世界座標",
        DeferredDebugView.DirectLighting => "直接光照",
        DeferredDebugView.IndirectLighting => "間接光照",
        DeferredDebugView.Shadow => "方向陰影",
        DeferredDebugView.ScreenSpaceReflection => "螢幕空間反射",
        DeferredDebugView.ReflectionConfidence => "反射可信度",
        DeferredDebugView.NanInfinity => "NaN／Infinity",
        DeferredDebugView.MaterialId => "材質 ID",
        DeferredDebugView.LightCount => "像素光源數",
        _ => view.ToString(),
    };

    private static string ForwardDebugViewDisplayName(ForwardDebugView view) => view switch
    {
        ForwardDebugView.FinalImage => "最終畫面",
        ForwardDebugView.Albedo => "Albedo",
        ForwardDebugView.Normal => "法線",
        ForwardDebugView.LinearDepth => "線性深度",
        ForwardDebugView.Roughness => "粗糙度",
        ForwardDebugView.Metallic => "金屬度",
        ForwardDebugView.Emissive => "自發光",
        ForwardDebugView.AmbientOcclusion => "環境遮蔽",
        ForwardDebugView.Shadow => "方向陰影",
        ForwardDebugView.DirectLighting => "直接光照",
        ForwardDebugView.AmbientLighting => "天空環境光",
        ForwardDebugView.ScreenSpaceReflection => "螢幕空間反射",
        ForwardDebugView.ApproximateGi => "GI 近似",
        ForwardDebugView.NanInfinity => "NaN／Infinity",
        _ => view.ToString(),
    };
}
