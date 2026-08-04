#version 430 core

uniform sampler2D uMaterialAtlas;
uniform sampler2D uMaterialDetailAtlas;
uniform int uGBufferQuality;
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
flat in int vFluid;
flat in vec3 vEmission;
flat in ivec3 vCell;
flat in int vGrid;
in vec4 vCurrentClip;
in vec4 vPreviousClip;

layout(location = 0) out vec4 gPosition;
layout(location = 1) out vec4 gAlbedo;
layout(location = 2) out vec4 gNormal;
layout(location = 3) out vec4 gEmission;
layout(location = 4) out vec2 gMotion;

const int TILE_SIZE = 16;
const int TILES_PER_ROW = 128;

vec3 atlasColor()
{
    int tile = vMaterial * 3 + vFace;
    ivec2 origin = ivec2((tile % TILES_PER_ROW) * TILE_SIZE, (tile / TILES_PER_ROW) * TILE_SIZE);
    ivec2 pixel = clamp(ivec2(floor(fract(vUv) * float(TILE_SIZE))), ivec2(0), ivec2(TILE_SIZE - 1));
    return texelFetch(uMaterialAtlas, origin + pixel, 0).rgb;
}

vec2 atlasDetail(ivec2 offset)
{
    int tile = vMaterial * 3 + vFace;
    ivec2 origin = ivec2((tile % TILES_PER_ROW) * TILE_SIZE, (tile / TILES_PER_ROW) * TILE_SIZE);
    ivec2 pixel = clamp(
        ivec2(floor(fract(vUv) * float(TILE_SIZE))) + offset,
        ivec2(0), ivec2(TILE_SIZE - 1));
    return texelFetch(uMaterialDetailAtlas, origin + pixel, 0).rg;
}

void main()
{
    if (vTransmission > 0.0) discard;
    vec3 emission = vEmission;
    vec2 detail = atlasDetail(ivec2(0));
    float roughness = uGBufferQuality > 0 ? detail.r : vRoughness;
    vec3 normal = normalize(vNormal);
    if (uGBufferQuality >= 2)
    {
        vec2 gradient = vec2(
            atlasDetail(ivec2(1, 0)).g - detail.g,
            atlasDetail(ivec2(0, 1)).g - detail.g);
        vec3 dpdx = dFdx(vWorldPosition);
        vec3 dpdy = dFdy(vWorldPosition);
        vec2 duvdx = dFdx(vUv);
        vec2 duvdy = dFdy(vUv);
        float determinant = duvdx.x * duvdy.y - duvdx.y * duvdy.x;
        if (abs(determinant) > 0.00001)
        {
            vec3 tangent = normalize((dpdx * duvdy.y - dpdy * duvdx.y) / determinant);
            vec3 bitangent = normalize((-dpdx * duvdy.x + dpdy * duvdx.x) / determinant);
            normal = normalize(normal - tangent * gradient.x * 1.65 - bitangent * gradient.y * 1.65);
        }
    }
    if (uHasSelection != 0 && vGrid == uSelectedGrid && all(equal(vCell, uSelectedCell)))
    {
        vec2 edgeDistance = abs(vUv - vec2(0.5));
        if (max(edgeDistance.x, edgeDistance.y) > 0.455)
            emission += vec3(0.267, 0.194, 0.02);
    }
    gPosition = vec4(vWorldPosition, 1.0);
    gAlbedo = vec4(atlasColor(), roughness);
    gNormal = vec4(normal * 0.5 + 0.5, vMetallic);
    gEmission = vec4(emission, 1.0);
    vec2 currentNdc = vCurrentClip.xy / max(abs(vCurrentClip.w), 0.0001);
    vec2 previousNdc = vPreviousClip.xy / max(abs(vPreviousClip.w), 0.0001);
    gMotion = (currentNdc - previousNdc) * 0.5;
}
