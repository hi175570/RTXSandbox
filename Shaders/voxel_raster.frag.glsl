#version 430 core

uniform sampler2D uMaterialAtlas;
uniform vec3 uCameraPosition;
uniform float uTimeOfDay;
uniform float uExposure;
uniform int uClassic;
uniform int uHasSelection;
uniform ivec3 uSelectedCell;
uniform int uSelectedGrid;

in vec3 vWorldPosition;
in vec3 vNormal;
in vec2 vUv;
flat in int vMaterial;
flat in int vFace;
flat in float vRoughness;
flat in float vMetallic;
flat in float vTransmission;
flat in vec3 vEmission;
flat in ivec3 vCell;
flat in int vGrid;

layout(location = 0) out vec4 outColor;

const float PI = 3.14159265359;
const int TILE_SIZE = 16;
const int TILES_PER_ROW = 128;
const vec2 ATLAS_SIZE = vec2(2048.0, 1536.0);

vec3 sky(vec3 direction, float daylight)
{
    float horizon = smoothstep(-0.2, 0.7, direction.y);
    vec3 day = mix(vec3(0.68, 0.73, 0.82), vec3(0.08, 0.30, 0.72), horizon);
    vec3 night = mix(vec3(0.025, 0.035, 0.075), vec3(0.004, 0.009, 0.025), horizon);
    return mix(night, day, daylight);
}

vec3 atlasColor()
{
    int tile = vMaterial * 3 + vFace;
    ivec2 tileOrigin = ivec2((tile % TILES_PER_ROW) * TILE_SIZE, (tile / TILES_PER_ROW) * TILE_SIZE);
    ivec2 pixel = clamp(ivec2(floor(fract(vUv) * float(TILE_SIZE))), ivec2(0), ivec2(TILE_SIZE - 1));
    return texelFetch(uMaterialAtlas, tileOrigin + pixel, 0).rgb;
}

void main()
{
    vec3 albedo = atlasColor();
    vec3 normal = normalize(vNormal);
    vec3 viewDirection = normalize(uCameraPosition - vWorldPosition);
    float angle = (uTimeOfDay - 6.0) * (2.0 * PI / 24.0);
    vec3 sunDirection = normalize(vec3(cos(angle), sin(angle), 0.28));
    float daylight = smoothstep(-0.12, 0.18, sunDirection.y);
    vec3 lightDirection = daylight > 0.08 ? sunDirection : -sunDirection;
    vec3 color = albedo;

    if (uClassic == 0)
    {
        float diffuse = max(dot(normal, lightDirection), 0.0);
        vec3 halfDirection = normalize(lightDirection + viewDirection);
        float exponent = mix(4.0, 320.0, pow(1.0 - vRoughness, 2.0));
        float specular = pow(max(dot(normal, halfDirection), 0.0), exponent);
        float facing = clamp(dot(normal, viewDirection), 0.0, 1.0);
        vec3 f0 = mix(vec3(0.04), albedo, vMetallic);
        vec3 fresnel = f0 + (vec3(1.0) - f0) * pow(1.0 - facing, 5.0);
        vec3 reflectedSky = sky(reflect(-viewDirection, normal), daylight);
        color = albedo * (0.16 + diffuse * mix(0.92, 0.30, vMetallic));
        color += reflectedSky * fresnel * (1.0 - vRoughness * 0.82);
        color += vec3(specular) * fresnel * (1.0 - vRoughness) * 1.8;
        color += vEmission;
    }

    if (uHasSelection != 0 && vGrid == uSelectedGrid && all(equal(vCell, uSelectedCell)))
    {
        vec3 edgeDistance = abs(fract(vWorldPosition + normal * 0.001) - 0.5);
        vec3 tangentMask = vec3(1.0) - abs(normal);
        float edge = max(edgeDistance.x * tangentMask.x, max(edgeDistance.y * tangentMask.y, edgeDistance.z * tangentMask.z));
        if (edge > 0.455) color += vec3(0.667, 0.45, 0.027);
    }

    color = vec3(1.0) - exp(-color * uExposure);
    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
    float alpha = vTransmission > 0.0 ? clamp(1.0 - vTransmission * 0.72, 0.16, 0.92) : 1.0;
    outColor = vec4(color, alpha);
}
