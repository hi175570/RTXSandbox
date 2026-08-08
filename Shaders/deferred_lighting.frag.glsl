#version 430 core

uniform sampler2D uPosition;
uniform sampler2D uAlbedo;
uniform sampler2D uNormal;
uniform sampler2D uEmission;
uniform sampler2D uMotion;
uniform sampler2D uDirectionalShadowMap;
uniform sampler2D uDynamicDirectionalShadowMap;
uniform vec3 uCameraPosition;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uLightView;
uniform mat4 uLightProjection;
uniform vec3 uPrimaryLightDirection;
uniform int uHasDirectionalShadowMap;
uniform int uHasDynamicDirectionalShadowMap;
uniform float uTimeOfDay;
uniform int uSpecularQuality;
uniform int uReflectionQuality;
uniform int uSelectionInReflections;
uniform int uRendererQuality;
uniform int uLightingQuality;
uniform int uShadowQuality;
uniform float uShadowBias;
uniform float uShadowNormalBias;
uniform float uShadowSlopeBias;
uniform float uShadowSoftness;
uniform int uAoQuality;
uniform int uDebugView;
uniform int uPointLightCount;
uniform vec4 uPointPositionRadius[16];
uniform vec4 uPointColorIntensity[16];
uniform int uSpotLightCount;
uniform vec4 uSpotPositionRadius[4];
uniform vec4 uSpotDirectionCone[4];
uniform vec4 uSpotColorIntensity[4];

in vec2 vUv;
layout(location = 0) out vec4 outColor;

const float PI = 3.14159265359;

bool finiteFloat(float value)
{
    return !isnan(value) && !isinf(value);
}

bool finiteVector(vec3 value)
{
    return all(not(isnan(value))) && all(not(isinf(value)));
}

vec3 safeNormalize(vec3 value, vec3 fallback)
{
    float lengthSquared = dot(value, value);
    if (!(lengthSquared > 1e-10) || !finiteFloat(lengthSquared)) return fallback;
    return value * inversesqrt(lengthSquared);
}

vec3 decodeOctahedral(vec2 encoded)
{
    vec2 f = encoded * 2.0 - 1.0;
    vec3 normal = vec3(f, 1.0 - abs(f.x) - abs(f.y));
    float fold = clamp(-normal.z, 0.0, 1.0);
    normal.xy += vec2(normal.x >= 0.0 ? -fold : fold,
                      normal.y >= 0.0 ? -fold : fold);
    return safeNormalize(normal, vec3(0.0, 1.0, 0.0));
}

vec3 skyColor(vec3 direction, float daylight)
{
    float horizon = smoothstep(-0.2, 0.72, direction.y);
    vec3 day = mix(vec3(0.58, 0.67, 0.82), vec3(0.055, 0.25, 0.68), horizon);
    vec3 night = mix(vec3(0.025, 0.032, 0.07), vec3(0.003, 0.008, 0.025), horizon);
    return mix(night, day, daylight);
}

bool projectWorld(vec3 world, out vec2 uv)
{
    vec4 clip = uProjection * uView * vec4(world, 1.0);
    if (!(clip.w > 0.001) || !finiteFloat(clip.w)) return false;
    uv = clip.xy / clip.w * 0.5 + 0.5;
    return all(greaterThanEqual(uv, vec2(0.001))) && all(lessThanEqual(uv, vec2(0.999)));
}

float screenShadow(vec3 origin, vec3 direction, float maximumDistance, int quality)
{
    if (quality <= 0 || !(maximumDistance > 0.001)) return 1.0;
    int steps = quality == 1 ? 8 : quality == 2 ? 14 : 22;
    float stepLength = maximumDistance / float(steps);
    for (int index = 1; index <= 22; index++)
    {
        if (index > steps) break;
        vec3 sampleWorld = origin + direction * (stepLength * float(index));
        vec2 uv;
        if (!projectWorld(sampleWorld, uv)) break;
        vec4 scene = texture(uPosition, uv);
        if (scene.a < 0.5 || !finiteVector(scene.xyz)) continue;
        float sceneDistance = length(scene.xyz - uCameraPosition);
        float rayDistance = length(sampleWorld - uCameraPosition);
        float proximity = distance(scene.xyz, sampleWorld);
        if (sceneDistance + 0.08 < rayDistance && proximity < max(0.30, stepLength * 1.45))
            return quality >= 3 ? 0.12 : 0.22;
    }
    return 1.0;
}

float directionalShadow(vec3 position, vec3 geometricNormal, vec3 lightDirection, int quality)
{
    if (quality <= 0 || uHasDirectionalShadowMap == 0) return 1.0;
    float nDotL = clamp(dot(geometricNormal, lightDirection), 0.0, 1.0);
    vec4 clip = uLightProjection * uLightView *
        vec4(position + geometricNormal * max(uShadowNormalBias, 0.0), 1.0);
    if (!(clip.w > 0.0) || !finiteFloat(clip.w)) return 1.0;
    vec3 projected = clip.xyz / clip.w * 0.5 + 0.5;
    if (!finiteVector(projected) || projected.z <= 0.0 || projected.z >= 1.0 ||
        any(lessThan(projected.xy, vec2(0.0))) || any(greaterThan(projected.xy, vec2(1.0))))
        return 1.0;

    float receiverBias = max(uShadowBias, 0.0) + max(uShadowSlopeBias, 0.0) * (1.0 - nDotL);
    vec2 texel = 1.0 / vec2(textureSize(uDirectionalShadowMap, 0));
    int radius = quality == 1 ? 0 : quality == 2 ? 1 : 2;
    float visible = 0.0;
    float samples = 0.0;
    for (int y = -2; y <= 2; y++)
    {
        if (abs(y) > radius) continue;
        for (int x = -2; x <= 2; x++)
        {
            if (abs(x) > radius) continue;
            vec2 shadowUv = clamp(projected.xy + vec2(x, y) * texel * max(uShadowSoftness, 0.5),
                vec2(0.0), vec2(1.0));
            float closestDepth = texture(uDirectionalShadowMap, shadowUv).r;
            if (uHasDynamicDirectionalShadowMap != 0)
                closestDepth = min(closestDepth, texture(uDynamicDirectionalShadowMap, shadowUv).r);
            visible += projected.z - receiverBias <= closestDepth ? 1.0 : 0.0;
            samples += 1.0;
        }
    }
    return clamp(visible / max(samples, 1.0), 0.0, 1.0);
}

float screenAo(vec3 position, vec3 geometricNormal, int quality)
{
    if (quality <= 0) return 1.0;
    vec2 texel = 1.0 / vec2(textureSize(uPosition, 0));
    int samples = quality == 1 ? 6 : quality == 2 ? 10 : 16;
    float radiusPixels = quality == 1 ? 7.0 : quality == 2 ? 13.0 : 20.0;
    float radiusWorld = quality == 1 ? 1.35 : quality == 2 ? 2.15 : 3.2;
    // A fixed golden-angle kernel is deliberately used instead of per-pixel noise.
    // With no temporal AO history, screen-space noise would crawl during camera motion.
    float rotation = 0.5;
    float occlusion = 0.0;
    for (int index = 0; index < 16; index++)
    {
        if (index >= samples) break;
        float progress = (float(index) + 0.5) / float(samples);
        float angle = rotation + float(index) * 2.399963;
        vec2 offset = vec2(cos(angle), sin(angle)) * texel * radiusPixels * sqrt(progress);
        vec2 sampleUv = clamp(vUv + offset, vec2(0.001), vec2(0.999));
        vec4 neighbor = texture(uPosition, sampleUv);
        if (neighbor.a < 0.5 || !finiteVector(neighbor.xyz)) continue;
        vec3 delta = neighbor.xyz - position;
        float distanceToNeighbor = length(delta);
        if (!(distanceToNeighbor > 0.025) || distanceToNeighbor >= radiusWorld) continue;

        // Use the unperturbed geometric normal. On one flat voxel face the signed
        // separation is zero, so procedural normal-map grain cannot become AO speckles.
        float signedSeparation = dot(geometricNormal, delta);
        if (signedSeparation <= 0.035) continue;
        vec3 direction = delta / max(distanceToNeighbor, 0.001);
        float hemisphere = smoothstep(0.04, 0.28, dot(geometricNormal, direction));
        vec3 neighborNormal = decodeOctahedral(texture(uMotion, sampleUv).zw);
        float normalWeight = 1.0 - max(dot(geometricNormal, neighborNormal), 0.0) * 0.35;
        float rangeWeight = 1.0 - smoothstep(0.08, radiusWorld, distanceToNeighbor);
        occlusion += hemisphere * normalWeight * rangeWeight;
    }
    return clamp(1.0 - occlusion / float(samples) * 2.15, 0.34, 1.0);
}

bool screenReflection(vec3 position, vec3 direction, float roughness, int quality,
    out vec3 reflected, out float confidence)
{
    reflected = vec3(0.0);
    confidence = 0.0;
    int steps = quality == 1 ? 12 : quality == 2 ? 24 : 38;
    float stride = mix(0.24, 0.48, roughness);
    vec3 previousSample = position + direction * 0.12;
    for (int index = 1; index <= 38; index++)
    {
        if (index > steps) break;
        float rayDistance = stride * float(index);
        vec3 sampleWorld = position + direction * rayDistance;
        vec2 uv;
        if (!projectWorld(sampleWorld, uv)) break;
        vec4 scene = texture(uPosition, uv);
        if (scene.a < 0.5 || !finiteVector(scene.xyz))
        {
            previousSample = sampleWorld;
            continue;
        }
        if (dot(scene.xyz - position, direction) <= 0.08)
        {
            previousSample = sampleWorld;
            continue;
        }
        float thickness = mix(0.16, 0.42, roughness) + stride * 0.7;
        if (distance(scene.xyz, sampleWorld) < thickness)
        {
            vec3 lower = previousSample;
            vec3 upper = sampleWorld;
            vec2 refinedUv = uv;
            for (int refinement = 0; refinement < 4; refinement++)
            {
                vec3 middle = (lower + upper) * 0.5;
                vec2 middleUv;
                if (!projectWorld(middle, middleUv)) break;
                vec4 middleScene = texture(uPosition, middleUv);
                if (middleScene.a > 0.5 && distance(middleScene.xyz, middle) < thickness)
                {
                    upper = middle;
                    refinedUv = middleUv;
                }
                else lower = middle;
            }
            vec3 hitAlbedo = texture(uAlbedo, refinedUv).rgb;
            vec4 hitEmissionSample = texture(uEmission, refinedUv);
            vec3 hitEmission = hitEmissionSample.rgb;
            if (uSelectionInReflections != 0)
                hitEmission += hitEmissionSample.a * vec3(0.8, 0.583, 0.06);
            reflected = hitAlbedo * 0.72 + hitEmission;
            float edge = min(min(refinedUv.x, refinedUv.y), min(1.0 - refinedUv.x, 1.0 - refinedUv.y));
            float edgeFade = smoothstep(0.015, 0.12, edge);
            float distanceFade = 1.0 - smoothstep(10.0, 42.0, rayDistance);
            confidence = edgeFade * distanceFade * (1.0 - roughness * 0.72);
            return confidence > 0.001;
        }
        previousSample = sampleWorld;
    }
    return false;
}

vec3 pbrLight(vec3 albedo, float roughness, float metallic, vec3 normal,
    vec3 viewDirection, vec3 lightDirection, vec3 radiance, float visibility)
{
    float nDotL = max(dot(normal, lightDirection), 0.0);
    float nDotV = max(dot(normal, viewDirection), 0.001);
    if (!(nDotL > 0.0) || !(visibility > 0.0)) return vec3(0.0);
    vec3 halfDirection = safeNormalize(viewDirection + lightDirection, normal);
    float nDotH = max(dot(normal, halfDirection), 0.0);
    float vDotH = clamp(dot(viewDirection, halfDirection), 0.0, 1.0);
    float safeRoughness = clamp(roughness, 0.045, 1.0);
    float alpha = max(safeRoughness * safeRoughness, 0.002025);
    float alpha2 = alpha * alpha;
    float denominator = nDotH * nDotH * (alpha2 - 1.0) + 1.0;
    float distribution = alpha2 / max(PI * denominator * denominator, 1e-5);
    float k = (safeRoughness + 1.0) * (safeRoughness + 1.0) / 8.0;
    float geometryV = nDotV / max(nDotV * (1.0 - k) + k, 1e-5);
    float geometryL = nDotL / max(nDotL * (1.0 - k) + k, 1e-5);
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(clamp(1.0 - vDotH, 0.0, 1.0), 5.0);
    vec3 specular = distribution * geometryV * geometryL * fresnel /
        max(4.0 * nDotV * nDotL, 1e-4);
    vec3 diffuse = (vec3(1.0) - fresnel) * (1.0 - metallic) * albedo / PI;
    vec3 result = (diffuse + specular * (float(uSpecularQuality) / 3.0)) *
        max(radiance, vec3(0.0)) * nDotL * clamp(visibility, 0.0, 1.0);
    return finiteVector(result) ? max(result, vec3(0.0)) : vec3(0.0);
}

vec3 materialIdColor(float materialId)
{
    return fract(vec3(0.1031, 0.11369, 0.13787) * (materialId + 1.0) * vec3(17.0, 29.0, 43.0));
}

void main()
{
    vec4 positionSample = texture(uPosition, vUv);
    float angle = (uTimeOfDay - 6.0) * (2.0 * PI / 24.0);
    vec3 sunDirection = safeNormalize(vec3(cos(angle), sin(angle), 0.28), vec3(0.0, 1.0, 0.0));
    float daylight = smoothstep(-0.12, 0.18, sunDirection.y);
    if (positionSample.a < 0.5)
    {
        if (uDebugView == 16) outColor = vec4(0.03, 0.28, 0.08, 1.0);
        else if (uDebugView != 0) outColor = vec4(0.0, 0.0, 0.0, 1.0);
        else
        {
            vec3 skyDirection = safeNormalize(vec3(vUv * 2.0 - 1.0, 0.75), vec3(0.0, 1.0, 0.0));
            outColor = vec4(skyColor(skyDirection, daylight), 1.0);
        }
        return;
    }

    vec4 albedoRoughness = texture(uAlbedo, vUv);
    vec4 normalMetallic = texture(uNormal, vUv);
    vec4 emissionSample = texture(uEmission, vUv);
    vec4 motionSample = texture(uMotion, vUv);
    vec3 position = positionSample.xyz;
    vec3 albedo = clamp(albedoRoughness.rgb, 0.0, 1.0);
    float roughness = clamp(albedoRoughness.a, 0.045, 1.0);
    float metallic = clamp(normalMetallic.a, 0.0, 1.0);
    vec3 normal = safeNormalize(normalMetallic.xyz * 2.0 - 1.0, vec3(0.0, 1.0, 0.0));
    vec3 geometricNormal = decodeOctahedral(motionSample.zw);
    vec3 viewDirection = safeNormalize(uCameraPosition - position, geometricNormal);
    vec3 emission = max(emissionSample.rgb, vec3(0.0)) + emissionSample.a * vec3(0.8, 0.583, 0.06);
    float ao = screenAo(position, geometricNormal, uAoQuality);
    bool invalidInput = !finiteVector(position) || !finiteVector(albedo) || !finiteVector(normal) ||
        !finiteVector(geometricNormal) || !finiteFloat(roughness) || !finiteFloat(metallic) || !finiteVector(emission);

    if (uDebugView == 1) { outColor = vec4(albedo, 1.0); return; }
    if (uDebugView == 2) { outColor = vec4(normal * 0.5 + 0.5, 1.0); return; }
    if (uDebugView == 3)
    {
        float depth = clamp(length(position - uCameraPosition) / 220.0, 0.0, 1.0);
        outColor = vec4(vec3(depth), 1.0); return;
    }
    if (uDebugView == 4) { outColor = vec4(vec3(metallic), 1.0); return; }
    if (uDebugView == 5) { outColor = vec4(vec3(roughness), 1.0); return; }
    if (uDebugView == 6) { outColor = vec4(vec3(ao), 1.0); return; }
    if (uDebugView == 7) { outColor = vec4(emission, 1.0); return; }
    if (uDebugView == 9) { outColor = vec4(abs(motionSample.xy) * 20.0, 0.0, 1.0); return; }
    if (uDebugView == 10) { outColor = vec4(fract(abs(position) * 0.05), 1.0); return; }
    if (uDebugView == 17) { outColor = vec4(materialIdColor(positionSample.a - 1.0), 1.0); return; }

    vec3 lightDirection = safeNormalize(uPrimaryLightDirection, sunDirection);
    float lightingScale = (0.55 + float(uLightingQuality) * 0.15) *
        (0.82 + float(uRendererQuality) * 0.06);
    vec3 sunRadiance = mix(vec3(0.16, 0.21, 0.35), vec3(4.2, 3.75, 3.15), daylight) * lightingScale;
    float sunVisibility = directionalShadow(position, geometricNormal, lightDirection, uShadowQuality);
    vec3 directLighting = pbrLight(albedo, roughness, metallic, normal, viewDirection,
        lightDirection, sunRadiance, sunVisibility);
    int activeLightCount = 1;

    for (int index = 0; index < 16; index++)
    {
        if (index >= uPointLightCount) break;
        vec3 toLight = uPointPositionRadius[index].xyz - position;
        float distanceToLight = length(toLight);
        float radius = max(uPointPositionRadius[index].w, 0.001);
        if (!(distanceToLight < radius)) continue;
        activeLightCount++;
        vec3 direction = safeNormalize(toLight, geometricNormal);
        float attenuation = pow(clamp(1.0 - distanceToLight / radius, 0.0, 1.0), 2.0) /
            (1.0 + distanceToLight * distanceToLight * 0.08);
        int shadowedPointLightLimit = uShadowQuality >= 3 ? 4 : uShadowQuality >= 2 ? 2 : 0;
        float visibility = index < shadowedPointLightLimit
            ? screenShadow(position + geometricNormal * 0.04, direction, distanceToLight, uShadowQuality) : 1.0;
        directLighting += pbrLight(albedo, roughness, metallic, normal, viewDirection, direction,
            uPointColorIntensity[index].rgb * uPointColorIntensity[index].a * attenuation * 8.0, visibility);
    }

    for (int index = 0; index < 4; index++)
    {
        if (index >= uSpotLightCount) break;
        vec3 toLight = uSpotPositionRadius[index].xyz - position;
        float distanceToLight = length(toLight);
        float radius = max(uSpotPositionRadius[index].w, 0.001);
        if (!(distanceToLight < radius)) continue;
        vec3 direction = safeNormalize(toLight, geometricNormal);
        vec3 spotDirection = safeNormalize(-uSpotDirectionCone[index].xyz, -direction);
        float cone = smoothstep(uSpotDirectionCone[index].w, min(0.995, uSpotDirectionCone[index].w + 0.07),
            dot(spotDirection, direction));
        if (!(cone > 0.001)) continue;
        activeLightCount++;
        float attenuation = cone * pow(clamp(1.0 - distanceToLight / radius, 0.0, 1.0), 2.0);
        float visibility = uShadowQuality >= 2
            ? screenShadow(position + geometricNormal * 0.04, direction, distanceToLight, uShadowQuality) : 1.0;
        directLighting += pbrLight(albedo, roughness, metallic, normal, viewDirection, direction,
            uSpotColorIntensity[index].rgb * uSpotColorIntensity[index].a * attenuation, visibility);
    }

    vec3 reflectedDirection = reflect(-viewDirection, normal);
    vec3 environment = skyColor(safeNormalize(reflectedDirection, geometricNormal), daylight);
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    float facing = clamp(dot(normal, viewDirection), 0.0, 1.0);
    vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(clamp(1.0 - facing, 0.0, 1.0), 5.0);
    // Direct sun shadowing does not remove all sky light, but leaving ambient
    // completely untouched made deep shadows visually disappear after HDR tonemapping.
    float ambientShadow = mix(0.68, 1.0, sunVisibility);
    vec3 ambient = albedo * mix(vec3(0.055, 0.065, 0.09), vec3(0.18, 0.22, 0.30), daylight) *
        (1.0 - metallic) * ao * ambientShadow;
    vec3 environmentReflection = environment * fresnel * (1.0 - roughness * 0.78) *
        (float(uReflectionQuality) / 3.0) * ao;
    vec3 indirectLighting = ambient + environmentReflection;
    vec3 ssrContribution = vec3(0.0);
    float reflectionConfidence = 0.0;
    if (uReflectionQuality > 0)
    {
        vec3 ssrColor;
        if (screenReflection(position + geometricNormal * 0.05, reflectedDirection, roughness,
                uReflectionQuality, ssrColor, reflectionConfidence))
        {
            ssrContribution = ssrColor * fresnel * reflectionConfidence;
            indirectLighting += ssrContribution;
        }
    }

    vec3 color = directLighting + indirectLighting + emission;
    bool invalidOutput = invalidInput || !finiteVector(directLighting) || !finiteVector(indirectLighting) ||
        !finiteVector(ssrContribution) || !finiteVector(color);
    if (uDebugView == 8) { outColor = vec4(max(color, vec3(0.0)), 1.0); return; }
    if (uDebugView == 11) { outColor = vec4(max(directLighting, vec3(0.0)), 1.0); return; }
    if (uDebugView == 12) { outColor = vec4(max(indirectLighting, vec3(0.0)), 1.0); return; }
    if (uDebugView == 13) { outColor = vec4(vec3(sunVisibility), 1.0); return; }
    if (uDebugView == 14) { outColor = vec4(max(ssrContribution, vec3(0.0)), 1.0); return; }
    if (uDebugView == 15) { outColor = vec4(vec3(reflectionConfidence), 1.0); return; }
    if (uDebugView == 16)
    {
        outColor = invalidOutput ? vec4(1.0, 0.0, 0.85, 1.0) : vec4(0.03, 0.28, 0.08, 1.0);
        return;
    }
    if (uDebugView == 18)
    {
        float heat = clamp(float(activeLightCount) / 8.0, 0.0, 1.0);
        outColor = vec4(heat, 1.0 - abs(heat * 2.0 - 1.0), 1.0 - heat, 1.0);
        return;
    }
    outColor = vec4(invalidOutput ? vec3(0.0) : max(color, vec3(0.0)), 1.0);
}
