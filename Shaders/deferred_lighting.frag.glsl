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
uniform int uRendererQuality;
uniform int uLightingQuality;
uniform int uShadowQuality;
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
    if (clip.w <= 0.001) return false;
    uv = clip.xy / clip.w * 0.5 + 0.5;
    return all(greaterThanEqual(uv, vec2(0.001))) && all(lessThanEqual(uv, vec2(0.999)));
}

float screenShadow(vec3 origin, vec3 direction, float maximumDistance, int quality)
{
    if (quality <= 0) return 1.0;
    int steps = quality == 1 ? 8 : quality == 2 ? 14 : 22;
    float stepLength = maximumDistance / float(steps);
    for (int index = 1; index <= 22; index++)
    {
        if (index > steps) break;
        vec3 sampleWorld = origin + direction * (stepLength * float(index));
        vec2 uv;
        if (!projectWorld(sampleWorld, uv)) break;
        vec4 scene = texture(uPosition, uv);
        if (scene.a < 0.5) continue;
        float sceneDistance = length(scene.xyz - uCameraPosition);
        float rayDistance = length(sampleWorld - uCameraPosition);
        float proximity = distance(scene.xyz, sampleWorld);
        if (sceneDistance + 0.08 < rayDistance && proximity < max(0.38, stepLength * 1.8))
            return quality >= 3 ? 0.08 : 0.18;
    }
    return 1.0;
}

float directionalShadow(vec3 position, vec3 normal, vec3 lightDirection, int quality)
{
    if (quality <= 0 || uHasDirectionalShadowMap == 0) return 1.0;
    vec4 clip = uLightProjection * uLightView * vec4(position + normal * 0.035, 1.0);
    if (clip.w <= 0.0) return 1.0;
    vec3 projected = clip.xyz / clip.w * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z >= 1.0 ||
        any(lessThan(projected.xy, vec2(0.0))) || any(greaterThan(projected.xy, vec2(1.0))))
        return 1.0;

    float normalBias = max(0.00045, 0.0022 * (1.0 - max(dot(normal, lightDirection), 0.0)));
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
            vec2 shadowUv = projected.xy + vec2(x, y) * texel;
            float closestDepth = texture(uDirectionalShadowMap, shadowUv).r;
            if (uHasDynamicDirectionalShadowMap != 0)
                closestDepth = min(closestDepth, texture(uDynamicDirectionalShadowMap, shadowUv).r);
            visible += projected.z - normalBias <= closestDepth ? 1.0 : 0.0;
            samples += 1.0;
        }
    }
    return visible / max(samples, 1.0);
}

float screenAo(vec3 position, vec3 normal, int quality)
{
    if (quality <= 0) return 1.0;
    vec2 texel = 1.0 / vec2(textureSize(uPosition, 0));
    int samples = quality == 1 ? 4 : quality == 2 ? 8 : 12;
    float occlusion = 0.0;
    for (int index = 0; index < 12; index++)
    {
        if (index >= samples) break;
        float angle = float(index) * 2.399963;
        float radius = (2.0 + float(index % 4) * 2.5) * float(quality);
        vec2 offset = vec2(cos(angle), sin(angle)) * texel * radius;
        vec4 neighbor = texture(uPosition, clamp(vUv + offset, vec2(0.001), vec2(0.999)));
        if (neighbor.a < 0.5) continue;
        vec3 delta = neighbor.xyz - position;
        float distanceToNeighbor = length(delta);
        float facing = max(dot(normal, normalize(delta + vec3(0.0001))), 0.0);
        occlusion += step(0.03, distanceToNeighbor) * (1.0 - smoothstep(0.15, 3.2, distanceToNeighbor)) * facing;
    }
    return clamp(1.0 - occlusion / float(samples) * 1.8, 0.24, 1.0);
}

bool screenReflection(vec3 position, vec3 direction, int quality, out vec3 reflected)
{
    int steps = quality == 1 ? 10 : quality == 2 ? 20 : 34;
    float stride = quality >= 3 ? 0.28 : 0.42;
    for (int index = 1; index <= 34; index++)
    {
        if (index > steps) break;
        vec3 sampleWorld = position + direction * (stride * float(index));
        vec2 uv;
        if (!projectWorld(sampleWorld, uv)) break;
        vec4 scene = texture(uPosition, uv);
        if (scene.a < 0.5) continue;
        if (distance(scene.xyz, sampleWorld) < stride * 1.8)
        {
            vec3 hitAlbedo = texture(uAlbedo, uv).rgb;
            vec3 hitEmission = texture(uEmission, uv).rgb;
            reflected = hitAlbedo * 0.72 + hitEmission;
            return true;
        }
    }
    return false;
}

vec3 pbrLight(vec3 albedo, float roughness, float metallic, vec3 normal,
    vec3 viewDirection, vec3 lightDirection, vec3 radiance, float visibility)
{
    vec3 halfDirection = normalize(viewDirection + lightDirection);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    float nDotV = max(dot(normal, viewDirection), 0.001);
    float nDotH = max(dot(normal, halfDirection), 0.0);
    float vDotH = max(dot(viewDirection, halfDirection), 0.0);
    float alpha = max(roughness * roughness, 0.025);
    float alpha2 = alpha * alpha;
    float denominator = nDotH * nDotH * (alpha2 - 1.0) + 1.0;
    float distribution = alpha2 / max(PI * denominator * denominator, 0.0001);
    float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float geometryV = nDotV / (nDotV * (1.0 - k) + k);
    float geometryL = nDotL / (nDotL * (1.0 - k) + k);
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - vDotH, 5.0);
    vec3 specular = distribution * geometryV * geometryL * fresnel / max(4.0 * nDotV * nDotL, 0.001);
    vec3 diffuse = (vec3(1.0) - fresnel) * (1.0 - metallic) * albedo / PI;
    return (diffuse + specular * (float(uSpecularQuality) / 3.0)) * radiance * nDotL * visibility;
}

void main()
{
    vec4 positionSample = texture(uPosition, vUv);
    float angle = (uTimeOfDay - 6.0) * (2.0 * PI / 24.0);
    vec3 sunDirection = normalize(vec3(cos(angle), sin(angle), 0.28));
    float daylight = smoothstep(-0.12, 0.18, sunDirection.y);
    if (positionSample.a < 0.5)
    {
        vec3 sky = skyColor(normalize(vec3(vUv * 2.0 - 1.0, 0.75)), daylight);
        outColor = vec4(sky, 1.0);
        return;
    }

    vec4 albedoRoughness = texture(uAlbedo, vUv);
    vec4 normalMetallic = texture(uNormal, vUv);
    vec3 emission = texture(uEmission, vUv).rgb;
    vec3 position = positionSample.xyz;
    vec3 albedo = albedoRoughness.rgb;
    float roughness = clamp(albedoRoughness.a, 0.025, 1.0);
    float metallic = clamp(normalMetallic.a, 0.0, 1.0);
    vec3 normal = normalize(normalMetallic.xyz * 2.0 - 1.0);
    vec3 viewDirection = normalize(uCameraPosition - position);
    float ao = screenAo(position, normal, uAoQuality);

    if (uDebugView == 1) { outColor = vec4(albedo, 1.0); return; }
    if (uDebugView == 2) { outColor = vec4(normal * 0.5 + 0.5, 1.0); return; }
    if (uDebugView == 3) { float d = clamp(length(position - uCameraPosition) / 100.0, 0.0, 1.0); outColor = vec4(vec3(d), 1.0); return; }
    if (uDebugView == 4) { outColor = vec4(vec3(metallic), 1.0); return; }
    if (uDebugView == 5) { outColor = vec4(vec3(roughness), 1.0); return; }
    if (uDebugView == 6) { outColor = vec4(vec3(ao), 1.0); return; }
    if (uDebugView == 7) { outColor = vec4(emission, 1.0); return; }
    if (uDebugView == 9) { vec2 motion = texture(uMotion, vUv).rg; outColor = vec4(abs(motion) * 20.0, 0.0, 1.0); return; }

    vec3 lightDirection = normalize(uPrimaryLightDirection);
    float lightingScale = (0.55 + float(uLightingQuality) * 0.15) *
        (0.82 + float(uRendererQuality) * 0.06);
    vec3 sunRadiance = mix(vec3(0.16, 0.21, 0.35), vec3(4.2, 3.75, 3.15), daylight) * lightingScale;
    float sunVisibility = directionalShadow(position, normal, lightDirection, uShadowQuality);
    vec3 color = pbrLight(albedo, roughness, metallic, normal, viewDirection,
        lightDirection, sunRadiance, sunVisibility);

    for (int index = 0; index < 16; index++)
    {
        if (index >= uPointLightCount) break;
        vec3 toLight = uPointPositionRadius[index].xyz - position;
        float distanceToLight = length(toLight);
        float radius = uPointPositionRadius[index].w;
        if (distanceToLight >= radius) continue;
        vec3 direction = toLight / max(distanceToLight, 0.001);
        float attenuation = pow(clamp(1.0 - distanceToLight / radius, 0.0, 1.0), 2.0) /
            (1.0 + distanceToLight * distanceToLight * 0.08);
        float visibility = (uShadowQuality >= 3 && index < 2)
            ? screenShadow(position + normal * 0.04, direction, distanceToLight, uShadowQuality) : 1.0;
        color += pbrLight(albedo, roughness, metallic, normal, viewDirection, direction,
            uPointColorIntensity[index].rgb * uPointColorIntensity[index].a * attenuation * 8.0, visibility);
    }

    for (int index = 0; index < 4; index++)
    {
        if (index >= uSpotLightCount) break;
        vec3 toLight = uSpotPositionRadius[index].xyz - position;
        float distanceToLight = length(toLight);
        if (distanceToLight >= uSpotPositionRadius[index].w) continue;
        vec3 direction = toLight / max(distanceToLight, 0.001);
        float cone = smoothstep(uSpotDirectionCone[index].w, min(0.995, uSpotDirectionCone[index].w + 0.07),
            dot(normalize(-uSpotDirectionCone[index].xyz), direction));
        float attenuation = cone * pow(1.0 - distanceToLight / uSpotPositionRadius[index].w, 2.0);
        float visibility = uShadowQuality >= 2
            ? screenShadow(position + normal * 0.04, direction, distanceToLight, uShadowQuality) : 1.0;
        color += pbrLight(albedo, roughness, metallic, normal, viewDirection, direction,
            uSpotColorIntensity[index].rgb * uSpotColorIntensity[index].a * attenuation, visibility);
    }

    if (uDebugView == 8) { outColor = vec4(max(color, vec3(0.0)), 1.0); return; }

    vec3 reflectedDirection = reflect(-viewDirection, normal);
    vec3 environment = skyColor(reflectedDirection, daylight);
    vec3 f0 = mix(vec3(0.04), albedo, metallic);
    float facing = max(dot(normal, viewDirection), 0.0);
    vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - facing, 5.0);
    vec3 ambient = albedo * mix(vec3(0.055, 0.065, 0.09), vec3(0.18, 0.22, 0.30), daylight) *
        (1.0 - metallic) * ao;
    color += ambient;
    color += environment * fresnel * (1.0 - roughness * 0.78) *
        (float(uReflectionQuality) / 3.0) * ao;
    if (uReflectionQuality > 0)
    {
        vec3 ssrColor;
        if (screenReflection(position + normal * 0.05, reflectedDirection, uReflectionQuality, ssrColor))
            color = mix(color, color + ssrColor * fresnel, (1.0 - roughness) * 0.62);
    }
    color += emission;
    outColor = vec4(max(color, vec3(0.0)), 1.0);
}
