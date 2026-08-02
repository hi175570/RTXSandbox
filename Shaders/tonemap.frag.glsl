#version 430 core

in vec2 vUv;
layout(location = 0) out vec4 outColor;

uniform sampler2D uImage;
uniform vec2 uViewport;
uniform float uExposure;

vec3 aces(vec3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((color * (a * color + b)) / (color * (c * color + d) + e), 0.0, 1.0);
}

void main()
{
    vec3 hdr = texture(uImage, vUv).rgb * uExposure;
    vec3 mapped = pow(aces(hdr), vec3(1.0 / 2.2));

    // Resolution-independent, high-contrast sandbox crosshair.
    vec2 fromCenter = abs(gl_FragCoord.xy - uViewport * 0.5);
    bool horizontal = fromCenter.y < 1.25 && fromCenter.x < 8.0 && fromCenter.x > 2.5;
    bool vertical = fromCenter.x < 1.25 && fromCenter.y < 8.0 && fromCenter.y > 2.5;
    if (horizontal || vertical)
    {
        float luminance = dot(mapped, vec3(0.2126, 0.7152, 0.0722));
        mapped = vec3(luminance > 0.5 ? 0.05 : 0.95);
    }

    outColor = vec4(mapped, 1.0);
}
