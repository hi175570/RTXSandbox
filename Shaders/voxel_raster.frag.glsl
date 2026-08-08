#version 430 core

uniform sampler2D uMaterialAtlas;
uniform sampler2D uMaterialDetailAtlas;
uniform sampler2D uSceneColor;
uniform sampler2D uDirectionalShadowMap;
uniform sampler2D uDynamicDirectionalShadowMap;
uniform vec3 uCameraPosition;
uniform float uTimeOfDay;
uniform float uExposure;
uniform int uClassic;
uniform int uSpecularQuality;
uniform int uReflectionQuality;
uniform int uClassicLightingQuality;
uniform float uClassicSunlightStrength;
uniform float uClassicAmbientStrength;
uniform float uClassicColorSaturation;
uniform float uClassicWaterWaveStrength;
uniform float uClassicFogDensity;
uniform int uTransparentOnly;
uniform int uDeferredTransparent;
uniform int uWaterQuality;
uniform int uTransparencyQuality;
uniform vec2 uScreenSize;
uniform int uHasSelection;
uniform ivec3 uSelectedCell;
uniform int uSelectedGrid;

// Enhanced Forward-only state. Deferred transparency and Classic leave these at zero.
uniform int uForwardHdr;
uniform int uForwardPbr;
uniform int uForwardDebugView;
uniform int uForwardApproximateGi;
uniform int uForwardWaterEffects;
uniform int uForwardShadowQuality;
uniform int uHasDirectionalShadowMap;
uniform int uHasDynamicDirectionalShadowMap;
uniform float uShadowBias;
uniform float uShadowNormalBias;
uniform float uShadowSlopeBias;
uniform float uShadowSoftness;
uniform mat4 uLightView;
uniform mat4 uLightProjection;
uniform vec3 uPrimaryLightDirection;
uniform int uPointLightCount;
uniform vec4 uPointPositionRadius[16];
uniform vec4 uPointColorIntensity[16];

in vec3 vWorldPosition;
in vec3 vNormal;
in vec2 vUv;
flat in int vMaterial;
flat in int vFace;
flat in float vRoughness;
flat in float vMetallic;
flat in float vTransmission;
flat in int vFluid;
flat in vec3 vEmission;
flat in ivec3 vCell;
flat in int vGrid;

layout(location = 0) out vec4 outColor;
// This auxiliary target does not defer lighting: the final HDR lighting is still
// calculated above. It only exposes geometric normal/roughness to Forward post effects.
layout(location = 1) out vec4 outForwardSurface;

const float PI = 3.14159265359;
const int TILE_SIZE = 16;
const int TILES_PER_ROW = 128;

vec3 safeNormalize(vec3 value, vec3 fallback)
{
    float lengthSquared = dot(value, value);
    return lengthSquared > 1e-10 ? value * inversesqrt(lengthSquared) : fallback;
}

vec3 sky(vec3 direction, float daylight)
{
    direction = safeNormalize(direction, vec3(0.0, 1.0, 0.0));
    float horizon = smoothstep(-0.28, 0.72, direction.y);
    vec3 day = mix(vec3(0.50, 0.64, 0.82), vec3(0.045, 0.22, 0.66), horizon);
    vec3 night = mix(vec3(0.018, 0.025, 0.060), vec3(0.002, 0.006, 0.020), horizon);
    return mix(night, day, daylight);
}

vec3 atlasColor()
{
    int tile = vMaterial * 3 + vFace;
    ivec2 tileOrigin = ivec2((tile % TILES_PER_ROW) * TILE_SIZE, (tile / TILES_PER_ROW) * TILE_SIZE);
    ivec2 pixel = clamp(ivec2(floor(fract(vUv) * float(TILE_SIZE))), ivec2(0), ivec2(TILE_SIZE - 1));
    return texelFetch(uMaterialAtlas, tileOrigin + pixel, 0).rgb;
}

vec2 atlasDetail(ivec2 offset)
{
    int tile = vMaterial * 3 + vFace;
    ivec2 tileOrigin = ivec2((tile % TILES_PER_ROW) * TILE_SIZE, (tile / TILES_PER_ROW) * TILE_SIZE);
    ivec2 pixel = clamp(ivec2(floor(fract(vUv) * float(TILE_SIZE))) + offset,
        ivec2(0), ivec2(TILE_SIZE - 1));
    return texelFetch(uMaterialDetailAtlas, tileOrigin + pixel, 0).rg;
}

float sampleShadowLayer(sampler2D shadowMap, vec3 projected, float receiverBias, int radius)
{
    vec2 texel = 1.0 / vec2(textureSize(shadowMap, 0));
    float visible = 0.0;
    float samples = 0.0;
    for (int y = -2; y <= 2; y++)
    for (int x = -2; x <= 2; x++)
    {
        if (abs(x) > radius || abs(y) > radius) continue;
        vec2 offset = vec2(x, y) * texel * max(uShadowSoftness, 0.5);
        float stored = texture(shadowMap, clamp(projected.xy + offset, vec2(0.001), vec2(0.999))).r;
        visible += projected.z - receiverBias <= stored ? 1.0 : 0.0;
        samples += 1.0;
    }
    return visible / max(samples, 1.0);
}

float directionalVisibility(vec3 geometricNormal, vec3 lightDirection)
{
    if (uForwardShadowQuality <= 0 || uHasDirectionalShadowMap == 0) return 1.0;
    vec3 offsetPosition = vWorldPosition + geometricNormal * uShadowNormalBias;
    vec4 clip = uLightProjection * uLightView * vec4(offsetPosition, 1.0);
    if (abs(clip.w) < 1e-7) return 1.0;
    vec3 projected = clip.xyz / clip.w * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z >= 1.0 ||
        any(lessThan(projected.xy, vec2(0.0))) || any(greaterThan(projected.xy, vec2(1.0)))) return 1.0;
    float receiverBias = uShadowBias + uShadowSlopeBias *
        (1.0 - clamp(dot(geometricNormal, lightDirection), 0.0, 1.0));
    int radius = uForwardShadowQuality >= 3 ? 2 : (uForwardShadowQuality >= 2 ? 1 : 0);
    float staticVisibility = sampleShadowLayer(uDirectionalShadowMap, projected, receiverBias, radius);
    float dynamicVisibility = uHasDynamicDirectionalShadowMap != 0
        ? sampleShadowLayer(uDynamicDirectionalShadowMap, projected, receiverBias, radius)
        : 1.0;
    return min(staticVisibility, dynamicVisibility);
}

float distributionGgx(vec3 normal, vec3 halfDirection, float roughness)
{
    float a = max(roughness * roughness, 0.0025);
    float a2 = a * a;
    float nDotH = max(dot(normal, halfDirection), 0.0);
    float denominator = nDotH * nDotH * (a2 - 1.0) + 1.0;
    return a2 / max(PI * denominator * denominator, 1e-5);
}

float geometrySchlick(float nDotV, float roughness)
{
    float r = roughness + 1.0;
    float k = r * r * 0.125;
    return nDotV / max(nDotV * (1.0 - k) + k, 1e-5);
}

vec3 evaluatePbr(vec3 albedo, vec3 normal, vec3 viewDirection, vec3 lightDirection,
    vec3 radiance, float roughness, float metallic)
{
    float nDotL = max(dot(normal, lightDirection), 0.0);
    float nDotV = max(dot(normal, viewDirection), 0.0);
    if (nDotL <= 0.0 || nDotV <= 0.0) return vec3(0.0);
    vec3 halfDirection = safeNormalize(viewDirection + lightDirection, normal);
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    float hDotV = clamp(dot(halfDirection, viewDirection), 0.0, 1.0);
    vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - hDotV, 5.0);
    float distribution = distributionGgx(normal, halfDirection, roughness);
    float geometry = geometrySchlick(nDotV, roughness) *
        geometrySchlick(nDotL, roughness);
    vec3 specular = distribution * geometry * fresnel /
        max(4.0 * nDotV * nDotL, 1e-4);
    specular *= float(uSpecularQuality) / 3.0;
    vec3 diffuse = (vec3(1.0) - fresnel) * (1.0 - metallic) * albedo / PI;
    return (diffuse + specular) * radiance * nDotL;
}

void main()
{
    if (uTransparentOnly > 0 && vTransmission <= 0.0) discard;
    if (uTransparentOnly < 0 && vTransmission > 0.0) discard;

    vec3 albedo = vFluid != 0 ? vec3(0.025, 0.25, 0.46) : atlasColor();
    if (uClassic != 0)
    {
        float luminance = dot(albedo, vec3(0.2126, 0.7152, 0.0722));
        albedo = mix(vec3(luminance), albedo, uClassicColorSaturation);
    }
    vec3 geometricNormal = safeNormalize(vNormal, vec3(0.0, 1.0, 0.0));
    vec3 normal = geometricNormal;
    vec2 materialDetail = vFluid != 0 ? vec2(vRoughness, 0.5) : atlasDetail(ivec2(0));
    float roughness = clamp(mix(vRoughness, materialDetail.r, 0.72), 0.045, 1.0);
    float metallic = clamp(vMetallic, 0.0, 1.0);
    vec2 heightGradient = vec2(0.0);
    if (vFluid == 0)
        heightGradient = vec2(
            atlasDetail(ivec2(1, 0)).g - materialDetail.g,
            atlasDetail(ivec2(0, 1)).g - materialDetail.g);
    vec3 surfaceTangent = safeNormalize(abs(normal.y) < 0.95
        ? cross(vec3(0.0, 1.0, 0.0), normal)
        : cross(vec3(0.0, 0.0, 1.0), normal), vec3(1.0, 0.0, 0.0));
    vec3 surfaceBitangent = safeNormalize(cross(normal, surfaceTangent), vec3(0.0, 0.0, 1.0));
    if (vFluid == 0 && uClassic == 0)
        normal = safeNormalize(normal - surfaceTangent * heightGradient.x * 1.15 -
            surfaceBitangent * heightGradient.y * 1.15, geometricNormal);
    if (vFluid != 0)
    {
        float time = uTimeOfDay * 3.2;
        vec2 waveGradient = vec2(
            cos(vWorldPosition.x * 5.7 + vWorldPosition.z * 2.1 + time) * 0.045,
            sin(vWorldPosition.z * 6.3 - vWorldPosition.x * 1.7 - time * 0.83) * 0.045);
        if (uClassic == 0 || uWaterQuality > 1)
            waveGradient += vec2(
                cos(vWorldPosition.x * 1.9 - vWorldPosition.z * 4.3 - time * 0.7) * 0.025,
                sin(vWorldPosition.z * 2.2 + vWorldPosition.x * 3.7 + time * 0.54) * 0.025);
        if (uClassic != 0)
            waveGradient *= uClassicWaterWaveStrength;
        normal = safeNormalize(normal + surfaceTangent * waveGradient.x +
            surfaceBitangent * waveGradient.y, geometricNormal);
        roughness = 0.08;
    }
    vec3 viewDirection = safeNormalize(uCameraPosition - vWorldPosition, geometricNormal);
    float angle = (uTimeOfDay - 6.0) * (2.0 * PI / 24.0);
    vec3 sunDirection = safeNormalize(vec3(cos(angle), sin(angle), 0.28), vec3(0.0, 1.0, 0.0));
    float daylight = smoothstep(-0.12, 0.18, sunDirection.y);
    vec3 lightDirection = uForwardHdr != 0
        ? safeNormalize(uPrimaryLightDirection, vec3(0.0, 1.0, 0.0))
        : (daylight > 0.08 ? sunDirection : -sunDirection);
    vec3 color = albedo;
    float shadowVisibility = 1.0;
    vec3 directLighting = vec3(0.0);
    vec3 ambientLighting = vec3(0.0);
    vec3 approximateGi = vec3(0.0);

    if (uDeferredTransparent != 0 && vTransmission > 0.0)
    {
        vec2 screenUv = gl_FragCoord.xy / max(uScreenSize, vec2(1.0));
        float distortion = vFluid != 0 ? 0.012 : 0.0;
        distortion *= 0.35 + float(max(uWaterQuality, uTransparencyQuality)) * 0.22;
        vec2 refractedUv = clamp(screenUv + normal.xz * distortion, vec2(0.002), vec2(0.998));
        vec3 refracted = texture(uSceneColor, refractedUv).rgb;
        if (uDeferredTransparent == 2)
        {
            vec3 mapped = pow(clamp(refracted, vec3(0.0), vec3(0.9999)), vec3(2.2));
            refracted = -log(max(vec3(1.0) - mapped, vec3(0.0001))) / max(uExposure, 0.001);
        }
        float blurRadius = materialDetail.r * materialDetail.r *
            (0.00035 + float(max(uTransparencyQuality, 0)) * 0.0010);
        if (blurRadius > 0.0005)
        {
            vec3 blurred = refracted;
            blurred += texture(uSceneColor, clamp(refractedUv + vec2( blurRadius, 0.0), vec2(0.002), vec2(0.998))).rgb;
            blurred += texture(uSceneColor, clamp(refractedUv + vec2(-blurRadius, 0.0), vec2(0.002), vec2(0.998))).rgb;
            blurred += texture(uSceneColor, clamp(refractedUv + vec2(0.0,  blurRadius), vec2(0.002), vec2(0.998))).rgb;
            blurred += texture(uSceneColor, clamp(refractedUv + vec2(0.0, -blurRadius), vec2(0.002), vec2(0.998))).rgb;
            refracted = mix(refracted, blurred * 0.2, clamp(materialDetail.r, 0.0, 0.92));
        }
        vec3 reflected = sky(reflect(-viewDirection, normal), daylight);
        float fresnel = 0.035 + 0.965 * pow(1.0 - max(dot(normal, viewDirection), 0.0), 5.0);
        float sunSparkle = pow(max(dot(reflect(-lightDirection, normal), viewDirection), 0.0), 220.0) * daylight;
        if (vFluid != 0)
        {
            float waterDepthTint = 1.0 - exp(-length(uCameraPosition - vWorldPosition) * 0.012 * float(max(uWaterQuality, 1)));
            refracted = mix(refracted, vec3(0.008, 0.115, 0.22), clamp(waterDepthTint, 0.0, 0.70));
            color = mix(refracted, reflected, clamp(fresnel * 0.78, 0.04, 0.80));
            color += vec3(1.0, 0.78, 0.45) * sunSparkle * 2.4;
            if (uForwardHdr != 0 && uForwardWaterEffects == 0)
                color = mix(refracted, albedo, 0.38);
        }
        else
        {
            float chroma = max(albedo.r, max(albedo.g, albedo.b)) - min(albedo.r, min(albedo.g, albedo.b));
            float tintStrength = mix(0.08, 0.50, chroma) * clamp(vTransmission, 0.0, 1.0);
            color = mix(refracted * mix(vec3(1.0), albedo, tintStrength), reflected,
                clamp(fresnel, 0.04, 0.68));
        }
        color += vEmission;
    }
    else if (uClassic == 0)
    {
        shadowVisibility = directionalVisibility(geometricNormal, lightDirection);
        float sunset = (1.0 - smoothstep(0.05, 0.46, abs(sunDirection.y))) * daylight;
        vec3 sunColor = mix(vec3(1.00, 0.82, 0.62), vec3(1.0, 0.32, 0.09), sunset * 0.78);
        vec3 sunRadiance = sunColor * mix(0.16, 3.6, daylight) * shadowVisibility;
        if (uForwardPbr != 0)
            directLighting = evaluatePbr(albedo, normal, viewDirection, lightDirection,
                sunRadiance, roughness, metallic);
        else
            directLighting = albedo * max(dot(normal, lightDirection), 0.0) *
                sunRadiance * (1.0 - metallic * 0.65);

        vec3 skyAmbient = sky(normal, daylight);
        float skyWeight = mix(0.34, 0.88, clamp(geometricNormal.y * 0.5 + 0.5, 0.0, 1.0));
        ambientLighting = albedo * skyAmbient * skyWeight * mix(0.18, 0.42, daylight) *
            (1.0 - metallic * 0.72);
        if (uForwardApproximateGi != 0)
        {
            vec3 groundBounce = mix(vec3(0.025, 0.035, 0.045), albedo * vec3(0.22, 0.18, 0.13), daylight);
            approximateGi = groundBounce * (0.04 + 0.11 * (1.0 - abs(geometricNormal.y))) *
                (1.0 - metallic);
        }

        for (int index = 0; index < min(uPointLightCount, 16); index++)
        {
            vec3 toLight = uPointPositionRadius[index].xyz - vWorldPosition;
            float distanceSquared = max(dot(toLight, toLight), 1e-5);
            float radius = max(uPointPositionRadius[index].w, 0.01);
            float distanceToLight = sqrt(distanceSquared);
            if (distanceToLight >= radius) continue;
            vec3 localDirection = toLight / distanceToLight;
            float falloff = max(1.0 - distanceToLight / radius, 0.0);
            falloff = falloff * falloff / (1.0 + distanceSquared * 0.08);
            vec3 radiance = uPointColorIntensity[index].rgb *
                uPointColorIntensity[index].a * falloff * 8.0;
            directLighting += uForwardPbr != 0
                ? evaluatePbr(albedo, normal, viewDirection, localDirection, radiance, roughness, metallic)
                : albedo * radiance * max(dot(normal, localDirection), 0.0);
        }

        vec3 reflectedSky = sky(reflect(-viewDirection, normal), daylight);
        vec3 f0 = mix(vec3(0.04), albedo, metallic);
        float facing = clamp(dot(normal, viewDirection), 0.0, 1.0);
        vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - facing, 5.0);
        vec3 environment = reflectedSky * fresnel * (1.0 - roughness * 0.88) *
            (float(uReflectionQuality) / 3.0);
        color = directLighting + ambientLighting + approximateGi + environment + vEmission;

        if (uForwardDebugView == 1) color = albedo;
        else if (uForwardDebugView == 2) color = normal * 0.5 + 0.5;
        else if (uForwardDebugView == 4) color = vec3(roughness);
        else if (uForwardDebugView == 5) color = vec3(metallic);
        else if (uForwardDebugView == 6) color = vEmission;
        else if (uForwardDebugView == 8) color = vec3(shadowVisibility);
        else if (uForwardDebugView == 9) color = directLighting;
        else if (uForwardDebugView == 10) color = ambientLighting;
        else if (uForwardDebugView == 12) color = approximateGi;
    }
    else if (uClassic != 0)
    {
        float faceLight = uClassicAmbientStrength;
        if (uClassicLightingQuality == 1)
            faceLight = 0.62 * uClassicAmbientStrength +
                max(normal.y, 0.0) * 0.38 * uClassicSunlightStrength;
        else if (uClassicLightingQuality > 1)
            faceLight = 0.28 * uClassicAmbientStrength +
                max(dot(normal, lightDirection), 0.0) * 0.72 * uClassicSunlightStrength;
        color *= faceLight;
    }

    if (uClassic != 0 && uClassicFogDensity > 0.0001)
    {
        float viewDistance = length(uCameraPosition - vWorldPosition);
        float fogAmount = 1.0 - exp(-viewDistance * uClassicFogDensity * 0.012);
        color = mix(color, sky(viewDirection, daylight), clamp(fogAmount, 0.0, 0.88));
    }

    ivec3 fragmentCell = vGrid == 0
        ? ivec3(floor(vWorldPosition - geometricNormal * 0.002))
        : vCell;
    if (uHasSelection != 0 && vGrid == uSelectedGrid && all(equal(fragmentCell, uSelectedCell)))
    {
        vec2 edgeDistance = abs(vUv - vec2(0.5));
        if (max(edgeDistance.x, edgeDistance.y) > 0.455) color += vec3(0.667, 0.45, 0.027);
    }

    bool invalid = any(isnan(color)) || any(isinf(color)) || any(isnan(normal)) || any(isinf(normal));
    if (invalid) color = uForwardDebugView == 13 ? vec3(1.0, 0.0, 1.0) : vec3(0.0);
    outForwardSurface = vec4(geometricNormal * 0.5 + 0.5, roughness);
    if (uForwardHdr == 0)
    {
        color = vec3(1.0) - exp(-max(color, vec3(0.0)) * uExposure);
        color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
    }
    float alpha = vFluid != 0
        ? clamp(1.0 - vTransmission * 0.82, 0.12, 0.42)
        : (vTransmission > 0.0
            ? clamp(0.035 + (1.0 - vTransmission) * 0.42 + materialDetail.r * 0.10, 0.05, 0.42)
            : 1.0);
    outColor = vec4(max(color, vec3(0.0)), alpha);
}
