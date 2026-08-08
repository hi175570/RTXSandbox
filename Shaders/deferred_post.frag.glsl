#version 430 core

uniform sampler2D uHdrColor;
uniform sampler2D uPosition;
uniform vec2 uTexelSize;
uniform vec3 uCameraPosition;
uniform float uExposure;
uniform float uTimeOfDay;
uniform int uHdrEnabled;
uniform int uBloomEnabled;
uniform int uPostQuality;
uniform int uFogQuality;
uniform int uToneMapping;
uniform int uDebugView;

in vec2 vUv;
layout(location = 0) out vec4 outColor;

float luminanceOf(vec3 color)
{
    return dot(color, vec3(0.2126, 0.7152, 0.0722));
}

vec3 fxaa(vec2 uv)
{
    vec3 center = texture(uHdrColor, uv).rgb;
    vec3 north = texture(uHdrColor, clamp(uv + vec2(0.0, uTexelSize.y), vec2(0.0), vec2(1.0))).rgb;
    vec3 south = texture(uHdrColor, clamp(uv - vec2(0.0, uTexelSize.y), vec2(0.0), vec2(1.0))).rgb;
    vec3 east = texture(uHdrColor, clamp(uv + vec2(uTexelSize.x, 0.0), vec2(0.0), vec2(1.0))).rgb;
    vec3 west = texture(uHdrColor, clamp(uv - vec2(uTexelSize.x, 0.0), vec2(0.0), vec2(1.0))).rgb;
    float centerLuma = luminanceOf(center);
    float minimumLuma = min(centerLuma, min(min(luminanceOf(north), luminanceOf(south)),
        min(luminanceOf(east), luminanceOf(west))));
    float maximumLuma = max(centerLuma, max(max(luminanceOf(north), luminanceOf(south)),
        max(luminanceOf(east), luminanceOf(west))));
    float contrast = maximumLuma - minimumLuma;
    if (contrast < max(0.0312, maximumLuma * 0.08)) return center;
    vec2 direction = vec2(luminanceOf(west) - luminanceOf(east),
                          luminanceOf(south) - luminanceOf(north));
    direction = clamp(direction / max(abs(direction.x) + abs(direction.y), 1e-4), vec2(-1.0), vec2(1.0));
    vec3 edge = 0.5 * (
        texture(uHdrColor, clamp(uv + direction * uTexelSize * 0.45, vec2(0.0), vec2(1.0))).rgb +
        texture(uHdrColor, clamp(uv - direction * uTexelSize * 0.45, vec2(0.0), vec2(1.0))).rgb);
    return mix(center, edge, 0.72);
}

vec3 toneMap(vec3 color)
{
    if (uHdrEnabled == 0) return clamp(color * uExposure, 0.0, 1.0);
    color *= uExposure;
    if (uToneMapping == 0) return color / (vec3(1.0) + color);
    if (uToneMapping == 1)
    {
        vec3 x = max(vec3(0.0), color - 0.004);
        return (x * (6.2 * x + 0.5)) / (x * (6.2 * x + 1.7) + 0.06);
    }
    return clamp((color * (2.51 * color + 0.03)) /
        max(color * (2.43 * color + 0.59) + 0.14, vec3(1e-5)), 0.0, 1.0);
}

void main()
{
    vec3 color = uPostQuality >= 2 && uDebugView == 0
        ? fxaa(vUv)
        : texture(uHdrColor, vUv).rgb;
    if (uDebugView != 0)
    {
        outColor = vec4(pow(clamp(color, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
        return;
    }

    vec4 position = texture(uPosition, vUv);
    if (uFogQuality > 0 && position.a > 0.5)
    {
        vec3 viewRay = position.xyz - uCameraPosition;
        float viewDistance = length(viewRay);
        float distanceFog = 1.0 - exp(-viewDistance *
            (0.0025 + float(uFogQuality) * 0.0018));
        float heightFog = exp(-max(position.y - 4.0, 0.0) * 0.045) *
            (1.0 - exp(-viewDistance * 0.012)) * 0.16 * float(uFogQuality);
        float fog = clamp(distanceFog + heightFog, 0.0, uFogQuality >= 3 ? 0.78 : 0.58);
        float angle = (uTimeOfDay - 6.0) * (6.28318530718 / 24.0);
        vec3 sunDirection = normalize(vec3(cos(angle), sin(angle), 0.28));
        float daylight = smoothstep(-0.12, 0.18, sunDirection.y);
        vec3 nightFog = vec3(0.018, 0.025, 0.055);
        vec3 dayFog = vec3(0.34, 0.48, 0.68);
        vec3 sunsetFog = vec3(0.82, 0.34, 0.15);
        float sunset = (1.0 - smoothstep(0.05, 0.42, abs(sunDirection.y))) * daylight;
        vec3 fogColor = mix(mix(nightFog, dayFog, daylight), sunsetFog, sunset * 0.48);
        float forwardScatter = pow(max(dot(normalize(viewRay + vec3(1e-5)), sunDirection), 0.0),
            mix(7.0, 18.0, float(uFogQuality) / 3.0));
        fogColor += vec3(1.0, 0.62, 0.30) * forwardScatter * daylight * 0.22;
        color = mix(color, fogColor, fog);
    }

    if (uBloomEnabled != 0 && uPostQuality > 0)
    {
        vec3 bloom = vec3(0.0);
        float weight = 0.0;
        float radius = 1.0 + float(uPostQuality);
        for (int y = -2; y <= 2; y++)
        for (int x = -2; x <= 2; x++)
        {
            vec2 offset = vec2(x, y) * uTexelSize * radius;
            vec3 sampleColor = texture(uHdrColor, clamp(vUv + offset, vec2(0.001), vec2(0.999))).rgb;
            float luminance = dot(sampleColor, vec3(0.2126, 0.7152, 0.0722));
            float contribution = smoothstep(0.9, 2.4, luminance);
            bloom += sampleColor * contribution;
            weight += contribution;
        }
        if (weight > 0.001) color += bloom / weight * (0.06 + float(uPostQuality) * 0.035);
    }

    if (uPostQuality >= 2)
    {
        vec3 neighborhood =
            texture(uHdrColor, vUv + vec2(uTexelSize.x, 0.0)).rgb +
            texture(uHdrColor, vUv - vec2(uTexelSize.x, 0.0)).rgb +
            texture(uHdrColor, vUv + vec2(0.0, uTexelSize.y)).rgb +
            texture(uHdrColor, vUv - vec2(0.0, uTexelSize.y)).rgb;
        color += (color - neighborhood * 0.25) * (uPostQuality >= 3 ? 0.16 : 0.08);
    }

    // Subtle shared color grade; avoid a stylized LUT dependency in V1.
    color = mix(vec3(dot(color, vec3(0.2126, 0.7152, 0.0722))), color, 1.06);
    if (any(isnan(color)) || any(isinf(color))) color = vec3(0.0);
    vec3 mapped = toneMap(max(color, vec3(0.0)));
    outColor = vec4(pow(max(mapped, vec3(0.0)), vec3(1.0 / 2.2)), 1.0);
}
