#version 430 core

uniform sampler2D uHdrColor;
uniform sampler2D uSurface;
uniform sampler2D uDepth;
uniform sampler2D uHistory;
uniform vec2 uTexelSize;
uniform vec3 uCameraPosition;
uniform float uTimeOfDay;
uniform float uExposure;
uniform mat4 uInverseViewProjection;
uniform mat4 uViewProjection;
uniform mat4 uPreviousViewProjection;
uniform int uAoQuality;
uniform int uReflectionQuality;
uniform int uVolumetricQuality;
uniform int uPostQuality;
uniform int uBloomEnabled;
uniform int uToneMapping;
uniform int uTaaEnabled;
uniform int uHasHistory;
uniform int uDebugView;
uniform int uUpscalingMode;

in vec2 vUv;
layout(location = 0) out vec4 outColor;

const float PI = 3.14159265359;

vec3 safeNormalize(vec3 value, vec3 fallback)
{
    float lengthSquared = dot(value, value);
    return lengthSquared > 1e-10 ? value * inversesqrt(lengthSquared) : fallback;
}

float luminanceOf(vec3 color)
{
    return dot(color, vec3(0.2126, 0.7152, 0.0722));
}

vec3 reconstructWorld(vec2 uv, float depth)
{
    vec4 world = uInverseViewProjection * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    return world.xyz / max(abs(world.w), 1e-6) * sign(world.w);
}

vec3 atmosphere(vec3 direction)
{
    direction = safeNormalize(direction, vec3(0.0, 1.0, 0.0));
    float angle = (uTimeOfDay - 6.0) * (2.0 * PI / 24.0);
    vec3 sunDirection = safeNormalize(vec3(cos(angle), sin(angle), 0.28), vec3(0.0, 1.0, 0.0));
    float daylight = smoothstep(-0.15, 0.16, sunDirection.y);
    float horizon = pow(1.0 - clamp(direction.y, 0.0, 1.0), 2.2);
    vec3 night = mix(vec3(0.002, 0.005, 0.018), vec3(0.018, 0.026, 0.065), horizon);
    vec3 day = mix(vec3(0.035, 0.19, 0.57), vec3(0.52, 0.68, 0.88), horizon);
    float sunset = (1.0 - smoothstep(0.02, 0.38, abs(sunDirection.y))) *
        pow(max(dot(direction, vec3(sunDirection.x, 0.0, sunDirection.z)), 0.0), 3.0);
    vec3 color = mix(night, day, daylight) + vec3(1.0, 0.25, 0.035) * sunset * daylight * 0.62;
    float sunDisc = smoothstep(0.99945, 0.99988, dot(direction, sunDirection));
    color += vec3(1.0, 0.68, 0.28) * sunDisc * daylight * 7.0;
    float moonDisc = smoothstep(0.99965, 0.99993, dot(direction, -sunDirection));
    color += vec3(0.38, 0.48, 0.72) * moonDisc * (1.0 - daylight) * 1.8;
    float stars = step(0.9975, fract(sin(dot(floor(direction.xz * 720.0), vec2(12.9898, 78.233))) * 43758.5453));
    color += vec3(stars) * (1.0 - daylight) * smoothstep(0.02, 0.25, direction.y) * 0.8;
    if (direction.y > 0.08 && daylight > 0.02)
    {
        vec2 cloudUv = direction.xz / max(direction.y + 0.22, 0.14);
        float clouds = sin(cloudUv.x * 5.1 + uTimeOfDay * 0.08) *
            sin(cloudUv.y * 4.3 - uTimeOfDay * 0.05);
        clouds += sin((cloudUv.x + cloudUv.y) * 8.7) * 0.38;
        clouds = smoothstep(0.43, 0.82, clouds * 0.5 + 0.5);
        color = mix(color, vec3(0.74, 0.79, 0.86) * (0.62 + daylight * 0.38), clouds * 0.34);
    }
    return color;
}

float screenAmbientOcclusion(vec2 uv, vec3 worldPosition, vec3 normal, float centerDepth)
{
    if (uAoQuality <= 0 || centerDepth >= 0.99999) return 1.0;
    const vec2 directions[12] = vec2[12](
        vec2(1.0, 0.0), vec2(-1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, -1.0),
        vec2(0.707, 0.707), vec2(-0.707, 0.707), vec2(0.707, -0.707), vec2(-0.707, -0.707),
        vec2(0.383, 0.924), vec2(-0.383, 0.924), vec2(0.924, -0.383), vec2(-0.924, -0.383));
    int sampleCount = uAoQuality >= 3 ? 12 : (uAoQuality >= 2 ? 8 : 4);
    float occlusion = 0.0;
    float weight = 0.0;
    float pixelRadius = uAoQuality >= 3 ? 5.0 : (uAoQuality >= 2 ? 3.5 : 2.0);
    for (int index = 0; index < 12; index++)
    {
        if (index >= sampleCount) break;
        vec2 sampleUv = clamp(uv + directions[index] * uTexelSize * pixelRadius, vec2(0.002), vec2(0.998));
        float sampleDepth = texture(uDepth, sampleUv).r;
        if (sampleDepth >= 0.99999) continue;
        vec3 samplePosition = reconstructWorld(sampleUv, sampleDepth);
        vec3 delta = samplePosition - worldPosition;
        float distanceToSample = length(delta);
        if (distanceToSample < 1e-4 || distanceToSample > 3.0) continue;
        float hemisphere = max(dot(normal, delta / distanceToSample), 0.0);
        float range = 1.0 - smoothstep(0.12, 3.0, distanceToSample);
        occlusion += hemisphere * range;
        weight += range;
    }
    float normalized = weight > 1e-4 ? occlusion / weight : 0.0;
    return clamp(1.0 - normalized * (uAoQuality >= 3 ? 0.55 : 0.45), 0.48, 1.0);
}

vec4 screenReflection(vec2 uv, vec3 worldPosition, vec3 normal, float roughness)
{
    if (uReflectionQuality <= 0 || roughness > 0.82) return vec4(0.0);
    vec3 viewDirection = safeNormalize(worldPosition - uCameraPosition, vec3(0.0, 0.0, -1.0));
    vec3 rayDirection = safeNormalize(reflect(viewDirection, normal), normal);
    if (dot(rayDirection, normal) <= 0.001) return vec4(0.0);
    int steps = uReflectionQuality >= 3 ? 28 : (uReflectionQuality >= 2 ? 18 : 10);
    float stride = mix(0.32, 0.75, roughness);
    for (int index = 1; index <= 28; index++)
    {
        if (index > steps) break;
        vec3 rayPosition = worldPosition + normal * 0.04 + rayDirection * stride * float(index);
        vec4 clip = uViewProjection * vec4(rayPosition, 1.0);
        if (clip.w <= 1e-5) break;
        vec2 rayUv = clip.xy / clip.w * 0.5 + 0.5;
        if (any(lessThan(rayUv, vec2(0.004))) || any(greaterThan(rayUv, vec2(0.996)))) break;
        float sampledDepth = texture(uDepth, rayUv).r;
        if (sampledDepth >= 0.99999) continue;
        vec3 sampledWorld = reconstructWorld(rayUv, sampledDepth);
        float alongRay = dot(sampledWorld - rayPosition, rayDirection);
        float thickness = 0.12 + stride * float(index) * 0.025;
        if (abs(alongRay) < thickness)
        {
            float edge = smoothstep(0.0, 0.10, min(min(rayUv.x, rayUv.y), min(1.0 - rayUv.x, 1.0 - rayUv.y)));
            float confidence = edge * (1.0 - roughness) *
                (1.0 - float(index) / float(steps + 1));
            return vec4(texture(uHdrColor, rayUv).rgb, clamp(confidence, 0.0, 1.0));
        }
    }
    return vec4(0.0);
}

vec3 applyFogAndShafts(vec3 color, vec2 uv, vec3 worldPosition, float depth)
{
    float angle = (uTimeOfDay - 6.0) * (2.0 * PI / 24.0);
    vec3 sunDirection = safeNormalize(vec3(cos(angle), sin(angle), 0.28), vec3(0.0, 1.0, 0.0));
    float daylight = smoothstep(-0.12, 0.18, sunDirection.y);
    if (depth < 0.99999 && uVolumetricQuality > 0)
    {
        float distanceToCamera = length(worldPosition - uCameraPosition);
        float distanceFog = 1.0 - exp(-distanceToCamera * (0.0022 + float(uVolumetricQuality) * 0.0012));
        float heightFog = exp(-max(worldPosition.y - 3.0, 0.0) * 0.055) *
            (1.0 - exp(-distanceToCamera * 0.010)) * 0.12 * float(uVolumetricQuality);
        float fog = clamp(distanceFog + heightFog, 0.0, uVolumetricQuality >= 3 ? 0.66 : 0.48);
        vec3 viewRay = safeNormalize(worldPosition - uCameraPosition, vec3(0.0, 0.0, -1.0));
        float forwardScatter = pow(max(dot(viewRay, sunDirection), 0.0), 12.0);
        vec3 fogColor = mix(vec3(0.018, 0.026, 0.060), vec3(0.34, 0.49, 0.71), daylight);
        fogColor += vec3(1.0, 0.48, 0.16) * forwardScatter * daylight * 0.28;
        color = mix(color, fogColor, fog);
    }
    if (uVolumetricQuality >= 2 && daylight > 0.05)
    {
        vec4 sunClip = uViewProjection * vec4(uCameraPosition + sunDirection * 180.0, 1.0);
        if (sunClip.w > 0.0)
        {
            vec2 sunUv = sunClip.xy / sunClip.w * 0.5 + 0.5;
            if (all(greaterThan(sunUv, vec2(-0.2))) && all(lessThan(sunUv, vec2(1.2))))
            {
                int samples = uVolumetricQuality >= 3 ? 16 : 8;
                float visibility = 0.0;
                for (int index = 1; index <= 16; index++)
                {
                    if (index > samples) break;
                    vec2 sampleUv = mix(uv, sunUv, float(index) / float(samples + 1));
                    float sampleDepth = texture(uDepth, clamp(sampleUv, vec2(0.001), vec2(0.999))).r;
                    visibility += smoothstep(0.985, 1.0, sampleDepth);
                }
                visibility /= float(samples);
                float radial = 1.0 - smoothstep(0.0, 0.85, distance(uv, sunUv));
                color += vec3(1.0, 0.68, 0.34) * visibility * radial * daylight * 0.055;
            }
        }
    }
    return color;
}

vec3 addBloom(vec3 color, vec2 uv)
{
    if (uBloomEnabled == 0 || uPostQuality <= 0) return color;
    vec3 bloom = vec3(0.0);
    float weight = 0.0;
    int radius = uPostQuality >= 3 ? 3 : 2;
    for (int y = -3; y <= 3; y++)
    for (int x = -3; x <= 3; x++)
    {
        if (abs(x) > radius || abs(y) > radius) continue;
        vec2 sampleUv = clamp(uv + vec2(x, y) * uTexelSize * (1.4 + float(uPostQuality) * 0.65),
            vec2(0.001), vec2(0.999));
        vec3 sampleColor = texture(uHdrColor, sampleUv).rgb;
        float contribution = smoothstep(1.0, 2.7, luminanceOf(sampleColor));
        bloom += sampleColor * contribution;
        weight += contribution;
    }
    if (weight > 1e-4) color += bloom / weight * (0.055 + float(uPostQuality) * 0.025);
    return color;
}

vec3 toneMap(vec3 color)
{
    color = max(color, vec3(0.0)) * max(uExposure, 0.01);
    if (uToneMapping == 0) return color / (vec3(1.0) + color);
    if (uToneMapping == 1)
    {
        vec3 x = max(vec3(0.0), color - 0.004);
        return (x * (6.2 * x + 0.5)) / max(x * (6.2 * x + 1.7) + 0.06, vec3(1e-5));
    }
    return clamp((color * (2.51 * color + 0.03)) /
        max(color * (2.43 * color + 0.59) + 0.14, vec3(1e-5)), 0.0, 1.0);
}

vec3 sampleToneMapped(vec2 uv)
{
    vec3 hdr = texture(uHdrColor, clamp(uv, vec2(0.001), vec2(0.999))).rgb;
    return pow(max(toneMap(hdr), vec3(0.0)), vec3(1.0 / 2.2));
}

vec3 applyFsrSharpen(vec3 source, vec2 uv)
{
    vec2 texel = uTexelSize;
    vec3 c = source;
    vec3 sampleCenter = sampleToneMapped(uv);
    vec3 sampleUp = sampleToneMapped(uv + vec2(-1.0, 0.0) * texel);
    vec3 sampleDown = sampleToneMapped(uv + vec2(1.0, 0.0) * texel);
    vec3 sampleLeft = sampleToneMapped(uv + vec2(0.0, -1.0) * texel);
    vec3 sampleRight = sampleToneMapped(uv + vec2(0.0, 1.0) * texel);
    vec3 sampleDiagA = sampleToneMapped(uv + vec2(-1.0, -1.0) * texel * 0.9);
    vec3 sampleDiagB = sampleToneMapped(uv + vec2(1.0, -1.0) * texel * 0.9);
    vec3 sampleDiagC = sampleToneMapped(uv + vec2(-1.0, 1.0) * texel * 0.9);
    vec3 sampleDiagD = sampleToneMapped(uv + vec2(1.0, 1.0) * texel * 0.9);
    vec3 sampleUpLeft = sampleUp + sampleDown + sampleLeft + sampleRight;
    vec3 sampleDownRight = sampleDiagA + sampleDiagB + sampleDiagC + sampleDiagD;
    vec3 n = sampleUpLeft;
    vec3 nw = sampleDownRight;
    vec3 avg = (sampleCenter * 4.0 + n + nw * 0.5) / 9.5;
    float sharpness = 0.18;
    return clamp(c + (c - avg) * sharpness, vec3(0.0), vec3(1.0));
}

void main()
{
    float depth = texture(uDepth, vUv).r;
    vec4 surface = texture(uSurface, vUv);
    vec3 normal = safeNormalize(surface.rgb * 2.0 - 1.0, vec3(0.0, 1.0, 0.0));
    float roughness = clamp(surface.a, 0.045, 1.0);
    vec3 worldPosition = reconstructWorld(vUv, depth);
    vec3 color = texture(uHdrColor, vUv).rgb;
    if (depth >= 0.99999)
        color = atmosphere(safeNormalize(worldPosition - uCameraPosition, vec3(0.0, 1.0, 0.0)));

    float ao = screenAmbientOcclusion(vUv, worldPosition, normal, depth);
    vec4 reflection = depth < 0.99999
        ? screenReflection(vUv, worldPosition, normal, roughness)
        : vec4(0.0);

    if (uDebugView == 3)
    {
        float linearized = depth >= 0.99999 ? 1.0 : clamp(length(worldPosition - uCameraPosition) / 220.0, 0.0, 1.0);
        outColor = vec4(vec3(linearized), 1.0);
        return;
    }
    if (uDebugView == 7)
    {
        outColor = vec4(vec3(ao), 1.0);
        return;
    }
    if (uDebugView == 11)
    {
        outColor = vec4(pow(max(reflection.rgb * reflection.a, vec3(0.0)), vec3(1.0 / 2.2)), 1.0);
        return;
    }
    if (uDebugView == 13)
    {
        bool invalid = any(isnan(color)) || any(isinf(color)) || any(isnan(normal)) || any(isinf(normal));
        outColor = vec4(invalid ? vec3(1.0, 0.0, 1.0) : vec3(0.02), 1.0);
        return;
    }
    if (uDebugView != 0)
    {
        outColor = vec4(pow(clamp(color, vec3(0.0), vec3(1.0)), vec3(1.0 / 2.2)), 1.0);
        return;
    }

    if (depth < 0.99999)
    {
        color *= mix(0.72, 1.0, ao);
        float fresnel = pow(1.0 - max(dot(normal,
            safeNormalize(uCameraPosition - worldPosition, normal)), 0.0), 5.0);
        float reflectionStrength = reflection.a * (1.0 - roughness) * (0.12 + fresnel * 0.32);
        color = mix(color, reflection.rgb, clamp(reflectionStrength, 0.0, 0.38));
    }
    color = applyFogAndShafts(color, vUv, worldPosition, depth);
    color = addBloom(color, vUv);
    if (uPostQuality >= 2)
    {
        vec3 neighborhood = texture(uHdrColor, vUv + vec2(uTexelSize.x, 0.0)).rgb +
            texture(uHdrColor, vUv - vec2(uTexelSize.x, 0.0)).rgb +
            texture(uHdrColor, vUv + vec2(0.0, uTexelSize.y)).rgb +
            texture(uHdrColor, vUv - vec2(0.0, uTexelSize.y)).rgb;
        color += (color - neighborhood * 0.25) * (uPostQuality >= 3 ? 0.10 : 0.055);
    }
    vec3 mapped = pow(max(toneMap(color), vec3(0.0)), vec3(1.0 / 2.2));
    if (uUpscalingMode == 1)
    {
        mapped = applyFsrSharpen(mapped, vUv);
    }

    // Reproject the previous resolved image. A current-frame neighborhood clamp
    // prevents stale geometry or edited blocks from leaving a visible trail.
    if (uUpscalingMode == 2 && uTaaEnabled != 0 && uHasHistory != 0 && depth < 0.99999)
    {
        vec4 previousClip = uPreviousViewProjection * vec4(worldPosition, 1.0);
        if (previousClip.w > 1e-5)
        {
            vec2 previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
            if (all(greaterThan(previousUv, vec2(0.002))) && all(lessThan(previousUv, vec2(0.998))))
            {
                vec3 history = texture(uHistory, previousUv).rgb;
                vec3 minimumColor = mapped;
                vec3 maximumColor = mapped;
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    vec3 neighbor = pow(max(toneMap(texture(uHdrColor,
                        clamp(vUv + vec2(x, y) * uTexelSize, vec2(0.001), vec2(0.999))).rgb), vec3(0.0)),
                        vec3(1.0 / 2.2));
                    minimumColor = min(minimumColor, neighbor);
                    maximumColor = max(maximumColor, neighbor);
                }
                history = clamp(history, minimumColor, maximumColor);
                mapped = mix(mapped, history, uPostQuality >= 3 ? 0.82 : 0.72);
            }
        }
    }
    else if (uUpscalingMode == 1 && uHasHistory != 0 && depth < 0.99999)
    {
        vec3 history = pow(max(toneMap(texture(uHistory, vUv).rgb), vec3(0.0)), vec3(1.0 / 2.2));
        mapped = mix(mapped, history, 0.10);
    }

    if (any(isnan(mapped)) || any(isinf(mapped))) mapped = vec3(0.0);
    outColor = vec4(clamp(mapped, 0.0, 1.0), 1.0);
}
