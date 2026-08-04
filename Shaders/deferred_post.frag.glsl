#version 430 core

uniform sampler2D uHdrColor;
uniform sampler2D uPosition;
uniform vec2 uTexelSize;
uniform vec3 uCameraPosition;
uniform float uExposure;
uniform int uHdrEnabled;
uniform int uBloomEnabled;
uniform int uPostQuality;
uniform int uFogQuality;
uniform int uToneMapping;
uniform int uDebugView;

in vec2 vUv;
layout(location = 0) out vec4 outColor;

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
        (color * (2.43 * color + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    vec3 color = texture(uHdrColor, vUv).rgb;
    if (uDebugView != 0)
    {
        outColor = vec4(pow(clamp(color, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
        return;
    }

    vec4 position = texture(uPosition, vUv);
    if (uFogQuality > 0 && position.a > 0.5)
    {
        float distanceFog = 1.0 - exp(-length(position.xyz - uCameraPosition) *
            (0.0025 + float(uFogQuality) * 0.0018));
        float heightFog = exp(-max(position.y, 0.0) * 0.055) * 0.22 * float(uFogQuality);
        float fog = clamp(distanceFog + heightFog, 0.0, uFogQuality >= 3 ? 0.78 : 0.58);
        color = mix(color, vec3(0.19, 0.28, 0.42), fog);
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
    vec3 mapped = toneMap(max(color, vec3(0.0)));
    outColor = vec4(pow(max(mapped, vec3(0.0)), vec3(1.0 / 2.2)), 1.0);
}
