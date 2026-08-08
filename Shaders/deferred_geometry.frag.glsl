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
layout(location = 4) out vec4 gMotion;

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

vec3 safeNormalize(vec3 value, vec3 fallback)
{
    float lengthSquared = dot(value, value);
    if (!(lengthSquared > 1e-10) || isnan(lengthSquared) || isinf(lengthSquared)) return fallback;
    return value * inversesqrt(lengthSquared);
}

vec2 encodeOctahedral(vec3 value)
{
    vec3 normal = safeNormalize(value, vec3(0.0, 1.0, 0.0));
    normal /= max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);
    vec2 encoded = normal.xy;
    if (normal.z < 0.0)
        encoded = (vec2(1.0) - abs(encoded.yx)) * sign(encoded + vec2(1e-6));
    return encoded * 0.5 + 0.5;
}

void main()
{
    if (vTransmission > 0.0) discard;
    vec3 emission = vEmission;
    float selectionEdge = 0.0;
    vec2 detail = atlasDetail(ivec2(0));
    float roughness = clamp(uGBufferQuality > 0 ? detail.r : vRoughness, 0.045, 1.0);
    vec3 geometricNormal = safeNormalize(vNormal, vec3(0.0, 1.0, 0.0));
    vec3 normal = geometricNormal;
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
            vec3 tangent = safeNormalize((dpdx * duvdy.y - dpdy * duvdx.y) / determinant,
                abs(geometricNormal.y) < 0.99 ? normalize(cross(vec3(0.0, 1.0, 0.0), geometricNormal)) : vec3(1.0, 0.0, 0.0));
            vec3 bitangent = safeNormalize((-dpdx * duvdy.x + dpdy * duvdx.x) / determinant,
                safeNormalize(cross(geometricNormal, tangent), vec3(0.0, 0.0, 1.0)));
            normal = safeNormalize(normal - tangent * gradient.x * 1.35 - bitangent * gradient.y * 1.35,
                geometricNormal);
        }
    }
    ivec3 fragmentCell = vGrid == 0
        ? ivec3(floor(vWorldPosition - normalize(vNormal) * 0.002))
        : vCell;
    if (uHasSelection != 0 && vGrid == uSelectedGrid && all(equal(fragmentCell, uSelectedCell)))
    {
        vec2 edgeDistance = abs(vUv - vec2(0.5));
        if (max(edgeDistance.x, edgeDistance.y) > 0.455)
            selectionEdge = 1.0;
    }
    // Alpha zero is the empty-pixel sentinel; material + 1 keeps that sentinel while
    // making Material ID available without adding a sixth G-buffer attachment.
    gPosition = vec4(vWorldPosition, float(vMaterial) + 1.0);
    gAlbedo = vec4(atlasColor(), roughness);
    gNormal = vec4(normal * 0.5 + 0.5, clamp(vMetallic, 0.0, 1.0));
    gEmission = vec4(emission, selectionEdge);
    vec2 currentNdc = vCurrentClip.xy / max(abs(vCurrentClip.w), 0.0001);
    vec2 previousNdc = vPreviousClip.xy / max(abs(vPreviousClip.w), 0.0001);
    gMotion = vec4((currentNdc - previousNdc) * 0.5, encodeOctahedral(geometricNormal));
}
