#version 430 core

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(rgba32f, binding = 0) uniform image2D uAccumulation;

uniform usampler3D uVoxels;
uniform usampler3D uMicroCells;
uniform sampler1D uMaterialSurface;
uniform sampler1D uMaterialEmission;
uniform usampler1D uMaterialFlags;
uniform sampler2D uMaterialAtlas;
uniform sampler2D uMaterialDetailAtlas;
uniform ivec2 uResolution;
uniform vec3 uCameraPosition;
uniform vec3 uCameraForward;
uniform vec3 uCameraRight;
uniform vec3 uCameraUp;
uniform float uTanHalfFov;
uniform int uFrameIndex;
uniform float uTimeOfDay;
uniform int uMaxBounces;
uniform int uHasSelection;
uniform ivec3 uSelectedCell;
uniform int uSelectedGrid;
uniform int uDynamicBoxCount;

const ivec3 WORLD_SIZE = ivec3(64, 32, 64);
const float PI = 3.14159265359;
const int MATERIAL_TILE_SIZE = 16;
const int MATERIAL_TILES_PER_ROW = 128;
const ivec2 MATERIAL_ATLAS_SIZE = ivec2(2048, 1536);

struct Hit
{
    bool found;
    float distance;
    vec3 position;
    vec3 normal;
    ivec3 cell;
    uint material;
    int grid;
};

struct MicroBox
{
    vec4 minimumAndMaterial;
    vec4 maximumAndPadding;
};

layout(std430, binding = 1) readonly buffer MicroBoxBuffer
{
    MicroBox microBoxes[];
};

struct DynamicBox
{
    vec4 centerAndMaterial;
    vec4 halfExtentsAndGrid;
    vec4 rotation;
    vec4 cellAndPadding;
};

layout(std430, binding = 2) readonly buffer DynamicBoxBuffer
{
    DynamicBox dynamicBoxes[];
};

vec3 rotateQuaternion(vec4 quaternion, vec3 value)
{
    return value + 2.0 * cross(quaternion.xyz, cross(quaternion.xyz, value) + quaternion.w * value);
}

bool materialIsTransmissive(uint id);
bool intersectMicroBox(
    vec3 rayOrigin,
    vec3 rayDirection,
    vec3 minimum,
    vec3 maximum,
    float minimumDistance,
    float maximumDistance,
    out float hitDistance,
    out vec3 hitNormal);

Hit traceDynamicBoxes(vec3 rayOrigin, vec3 rayDirection, float maxDistance, bool ignoreTransmissive)
{
    Hit hit;
    hit.found = false;
    hit.distance = maxDistance;
    hit.normal = vec3(0.0);
    hit.grid = -1;
    for (int index = 0; index < uDynamicBoxCount; ++index)
    {
        DynamicBox box = dynamicBoxes[index];
        uint material = uint(box.centerAndMaterial.w + 0.5);
        if (ignoreTransmissive && materialIsTransmissive(material)) continue;
        vec4 inverseRotation = vec4(-box.rotation.xyz, box.rotation.w);
        vec3 localOrigin = rotateQuaternion(inverseRotation, rayOrigin - box.centerAndMaterial.xyz);
        vec3 localDirection = rotateQuaternion(inverseRotation, rayDirection);
        float distance;
        vec3 localNormal;
        if (!intersectMicroBox(
                localOrigin,
                localDirection,
                -box.halfExtentsAndGrid.xyz,
                box.halfExtentsAndGrid.xyz,
                0.0,
                hit.distance,
                distance,
                localNormal)) continue;
        hit.found = true;
        hit.distance = distance;
        hit.position = rayOrigin + rayDirection * distance;
        hit.normal = normalize(rotateQuaternion(box.rotation, localNormal));
        hit.cell = ivec3(box.cellAndPadding.xyz + vec3(0.5));
        hit.material = material;
        hit.grid = int(box.halfExtentsAndGrid.w + 0.5);
    }
    return hit;
}

uint hash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}

float random(inout uint state)
{
    state = hash(state);
    return float(state) / 4294967296.0;
}

bool insideWorld(ivec3 cell)
{
    return all(greaterThanEqual(cell, ivec3(0))) && all(lessThan(cell, WORLD_SIZE));
}

float firstBoundary(float origin, float direction, int cell)
{
    if (direction > 0.000001) return (float(cell + 1) - origin) / direction;
    if (direction < -0.000001) return (float(cell) - origin) / direction;
    return 1e30;
}

bool materialIsFluid(uint id)
{
    return (texelFetch(uMaterialFlags, int(id), 0).r & 1u) != 0u;
}

bool materialIsTransparent(uint id)
{
    return texelFetch(uMaterialFlags, int(id), 0).b != 0u;
}

float materialTransmission(uint id)
{
    return float(texelFetch(uMaterialFlags, int(id), 0).b) / 255.0;
}

bool materialIsTransmissive(uint id)
{
    return materialIsFluid(id) || materialIsTransparent(id);
}

float materialIndexOfRefraction(uint id)
{
    return max(1.0, float(texelFetch(uMaterialFlags, int(id), 0).a) / 100.0);
}

uint materialSurfacePattern(uint id)
{
    return texelFetch(uMaterialFlags, int(id), 0).g;
}

bool intersectMicroBox(
    vec3 rayOrigin,
    vec3 rayDirection,
    vec3 minimum,
    vec3 maximum,
    float minimumDistance,
    float maximumDistance,
    out float hitDistance,
    out vec3 hitNormal)
{
    float nearDistance = minimumDistance;
    float farDistance = maximumDistance;
    hitNormal = vec3(0.0);
    for (int axis = 0; axis < 3; ++axis)
    {
        float origin = rayOrigin[axis];
        float direction = rayDirection[axis];
        if (abs(direction) < 0.000001)
        {
            if (origin < minimum[axis] || origin > maximum[axis]) return false;
            continue;
        }
        float first = (minimum[axis] - origin) / direction;
        float second = (maximum[axis] - origin) / direction;
        float normalSign = -sign(direction);
        if (first > second)
        {
            float swapValue = first;
            first = second;
            second = swapValue;
        }
        if (first > nearDistance)
        {
            nearDistance = first;
            hitNormal = vec3(0.0);
            hitNormal[axis] = normalSign;
        }
        farDistance = min(farDistance, second);
        if (nearDistance > farDistance) return false;
    }
    hitDistance = nearDistance;
    return hitDistance >= minimumDistance && hitDistance <= maximumDistance;
}

Hit traceVoxelWorld(vec3 rayOrigin, vec3 rayDirection, float maxDistance, bool ignoreTransmissive)
{
    Hit hit = traceDynamicBoxes(rayOrigin, rayDirection, maxDistance, ignoreTransmissive);
    float traversalLimit = hit.found ? hit.distance : maxDistance;

    ivec3 cell = ivec3(floor(rayOrigin));
    ivec3 stepDirection = ivec3(sign(rayDirection));
    vec3 delta = vec3(
        abs(rayDirection.x) > 0.000001 ? abs(1.0 / rayDirection.x) : 1e30,
        abs(rayDirection.y) > 0.000001 ? abs(1.0 / rayDirection.y) : 1e30,
        abs(rayDirection.z) > 0.000001 ? abs(1.0 / rayDirection.z) : 1e30);
    vec3 nextBoundary = vec3(
        firstBoundary(rayOrigin.x, rayDirection.x, cell.x),
        firstBoundary(rayOrigin.y, rayDirection.y, cell.y),
        firstBoundary(rayOrigin.z, rayDirection.z, cell.z));
    vec3 normal = vec3(0.0);
    float distance = 0.0;

    for (int iteration = 0; iteration < 256 && distance <= traversalLimit; ++iteration)
    {
        if (!insideWorld(cell))
        {
            return hit;
        }

        uint material = texelFetch(uVoxels, cell, 0).r;
        if (material != 0u && !(ignoreTransmissive && materialIsTransmissive(material)))
        {
            hit.found = true;
            hit.distance = distance;
            hit.position = rayOrigin + rayDirection * distance;
            hit.normal = normal;
            hit.cell = cell;
            hit.material = material;
            hit.grid = 0;
            return hit;
        }

        uvec2 microCell = texelFetch(uMicroCells, cell, 0).rg;
        float cellExitDistance = min(nextBoundary.x, min(nextBoundary.y, nextBoundary.z));
        float nearestMicroDistance = min(cellExitDistance + 0.0001, maxDistance);
        vec3 nearestMicroNormal = vec3(0.0);
        uint nearestMicroMaterial = 0u;
        for (uint localIndex = 0u; localIndex < microCell.y; ++localIndex)
        {
            MicroBox box = microBoxes[microCell.x + localIndex];
            uint boxMaterial = uint(box.minimumAndMaterial.w + 0.5);
            if (ignoreTransmissive && materialIsTransmissive(boxMaterial)) continue;
            float boxDistance;
            vec3 boxNormal;
            if (intersectMicroBox(
                    rayOrigin,
                    rayDirection,
                    box.minimumAndMaterial.xyz,
                    box.maximumAndPadding.xyz,
                    max(0.0, distance - 0.0001),
                    nearestMicroDistance,
                    boxDistance,
                    boxNormal))
            {
                nearestMicroDistance = boxDistance;
                nearestMicroNormal = boxNormal;
                nearestMicroMaterial = boxMaterial;
            }
        }
        if (nearestMicroMaterial != 0u)
        {
            hit.found = true;
            hit.distance = nearestMicroDistance;
            hit.position = rayOrigin + rayDirection * nearestMicroDistance;
            hit.normal = nearestMicroNormal;
            hit.cell = cell;
            hit.material = nearestMicroMaterial;
            hit.grid = 0;
            return hit;
        }

        if (nextBoundary.x <= nextBoundary.y && nextBoundary.x <= nextBoundary.z)
        {
            cell.x += stepDirection.x;
            distance = nextBoundary.x;
            nextBoundary.x += delta.x;
            normal = vec3(-stepDirection.x, 0.0, 0.0);
        }
        else if (nextBoundary.y <= nextBoundary.z)
        {
            cell.y += stepDirection.y;
            distance = nextBoundary.y;
            nextBoundary.y += delta.y;
            normal = vec3(0.0, -stepDirection.y, 0.0);
        }
        else
        {
            cell.z += stepDirection.z;
            distance = nextBoundary.z;
            nextBoundary.z += delta.z;
            normal = vec3(0.0, 0.0, -stepDirection.z);
        }
    }

    return hit;
}

void celestialLight(out vec3 direction, out vec3 color, out float intensity, out float daylight)
{
    float angle = (uTimeOfDay - 6.0) * (2.0 * PI / 24.0);
    vec3 sunDirection = normalize(vec3(cos(angle), sin(angle), 0.28));
    vec3 moonDirection = -sunDirection;
    daylight = smoothstep(-0.12, 0.18, sunDirection.y);
    direction = daylight > 0.08 ? sunDirection : moonDirection;
    color = mix(vec3(0.34, 0.45, 0.78), vec3(1.0, 0.86, 0.66), daylight);
    intensity = mix(0.10, 1.75, daylight);
}

vec3 skyColor(vec3 direction)
{
    vec3 lightDirection;
    vec3 lightColor;
    float lightIntensity;
    float daylight;
    celestialLight(lightDirection, lightColor, lightIntensity, daylight);

    float horizon = smoothstep(-0.15, 0.65, direction.y);
    vec3 daySky = mix(vec3(0.78, 0.69, 0.56), vec3(0.12, 0.36, 0.82), horizon);
    vec3 nightSky = mix(vec3(0.035, 0.045, 0.09), vec3(0.006, 0.012, 0.035), horizon);
    vec3 sky = mix(nightSky, daySky, daylight);

    float discSharpness = daylight > 0.08 ? 900.0 : 2800.0;
    float disc = pow(max(dot(direction, lightDirection), 0.0), discSharpness);
    vec3 directionCell = floor(direction * 650.0);
    float starNoise = fract(sin(dot(directionCell, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
    float stars = step(0.9965, starNoise) * smoothstep(0.0, 0.45, direction.y) * (1.0 - daylight);
    return sky * mix(0.55, 0.82, daylight) + lightColor * disc * mix(1.5, 10.0, daylight) + vec3(stars * 1.8);
}

void materialProperties(uint id, out vec3 albedo, out float roughness, out float metallic, out vec3 emission)
{
    vec4 surface = texelFetch(uMaterialSurface, int(id), 0);
    vec4 lightAndMetal = texelFetch(uMaterialEmission, int(id), 0);
    albedo = surface.rgb;
    roughness = surface.a;
    emission = lightAndMetal.rgb;
    metallic = lightAndMetal.a;
}

int materialFaceIndex(vec3 normal)
{
    if (normal.y > 0.5) return 0;
    if (normal.y < -0.5) return 2;
    return 1;
}

vec2 materialFaceUv(Hit hit)
{
    vec3 local = fract(hit.position + hit.normal * 0.001);
    vec2 uv = abs(hit.normal.y) > 0.5
        ? local.xz
        : (abs(hit.normal.x) > 0.5 ? local.zy : local.xy);

    uint orientation = hash(uint(hit.cell.x) * 73856093u ^
                            uint(hit.cell.y) * 19349663u ^
                            uint(hit.cell.z) * 83492791u ^ hit.material * 2654435761u);
    if (materialFaceIndex(hit.normal) == 0)
    {
        if ((orientation & 1u) != 0u) uv.x = 1.0 - uv.x;
        if ((orientation & 2u) != 0u) uv.y = 1.0 - uv.y;
        if ((orientation & 4u) != 0u) uv = uv.yx;
    }
    else if ((orientation & 1u) != 0u)
    {
        uv.x = 1.0 - uv.x;
    }
    return fract(uv);
}

ivec2 materialAtlasTexel(uint id, int face, vec2 uv)
{
    int tile = int(id) * 3 + face;
    ivec2 origin = ivec2((tile % MATERIAL_TILES_PER_ROW) * MATERIAL_TILE_SIZE,
                         (tile / MATERIAL_TILES_PER_ROW) * MATERIAL_TILE_SIZE);
    ivec2 pixel = clamp(ivec2(floor(fract(uv) * float(MATERIAL_TILE_SIZE))), ivec2(0), ivec2(MATERIAL_TILE_SIZE - 1));
    return origin + pixel;
}

vec4 sampleMaterialAtlas(uint id, int face, vec2 uv)
{
    return texelFetch(uMaterialAtlas, materialAtlasTexel(id, face, uv), 0);
}

vec2 sampleMaterialDetail(uint id, int face, vec2 uv)
{
    return texelFetch(uMaterialDetailAtlas, materialAtlasTexel(id, face, uv), 0).rg;
}

void applySurfaceDetail(uint id, Hit hit, inout vec3 albedo, inout float roughness, inout vec3 shadingNormal)
{
    int face = materialFaceIndex(hit.normal);
    vec2 uv = materialFaceUv(hit);
    vec4 atlas = sampleMaterialAtlas(id, face, uv);
    vec2 detail = sampleMaterialDetail(id, face, uv);
    albedo = atlas.rgb;
    roughness = clamp(detail.r, 0.001, 1.0);

    float stepUv = 1.0 / float(MATERIAL_TILE_SIZE);
    float heightRight = sampleMaterialDetail(id, face, uv + vec2(stepUv, 0.0)).g;
    float heightUp = sampleMaterialDetail(id, face, uv + vec2(0.0, stepUv)).g;
    vec2 gradient = vec2(heightRight - detail.g, heightUp - detail.g);
    vec3 tangent = abs(hit.normal.y) > 0.5
        ? vec3(1.0, 0.0, 0.0)
        : (abs(hit.normal.x) > 0.5 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0));
    vec3 bitangent = abs(hit.normal.y) > 0.5 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
    vec3 detailedNormal = normalize(hit.normal - tangent * gradient.x * 1.65 - bitangent * gradient.y * 1.65);
    shadingNormal = dot(detailedNormal, hit.normal) > 0.72 ? detailedNormal : normalize(mix(hit.normal, detailedNormal, 0.35));

    uint blockHash = hash(uint(hit.cell.x) * 1597334677u ^ uint(hit.cell.y) * 3812015801u ^ uint(hit.cell.z) * 958282577u);
    float macroRandom = float(blockHash & 65535u) / 65535.0 - 0.5;
    float naturalSignal = smoothstep(0.018, 0.12, abs(detail.g - 0.502));
    albedo *= 1.0 + macroRandom * 0.12 * naturalSignal;

    uint pattern = materialSurfacePattern(id);
    if (pattern == 15u)
    {
        float phase = hit.position.x * 0.7 + hit.position.y * 0.9 + hit.position.z * 0.55 + uTimeOfDay * 0.35;
        albedo = 0.5 + 0.5 * cos(phase + vec3(0.0, 2.094, 4.188));
        roughness = 0.09;
    }
    else if (pattern == 16u)
    {
        float scan = 0.55 + 0.45 * sin((hit.position.y + uTimeOfDay * 0.25) * 42.0);
        float viewGlow = pow(1.0 - abs(dot(normalize(-hit.position), hit.normal)), 2.0);
        albedo *= 0.65 + scan * 0.35 + viewGlow;
        roughness = 0.035;
    }
    else if (pattern == 18u)
    {
        float noise = detail.g;
        float flow = sin((hit.position.x + hit.position.z) * 8.0 + uTimeOfDay * 1.4 + noise * 9.0) * 0.5 + 0.5;
        albedo = mix(vec3(0.32, 0.005, 0.0), vec3(1.0, 0.34, 0.01), smoothstep(0.28, 0.78, flow));
        roughness = 0.18;
    }
}

vec3 cosineHemisphere(vec3 normal, inout uint state)
{
    float u1 = random(state);
    float u2 = random(state);
    float radius = sqrt(u1);
    float angle = 2.0 * PI * u2;
    vec3 tangent = normalize(abs(normal.y) < 0.999
        ? cross(normal, vec3(0.0, 1.0, 0.0))
        : cross(normal, vec3(1.0, 0.0, 0.0)));
    vec3 bitangent = cross(tangent, normal);
    return normalize(tangent * (radius * cos(angle)) +
                     bitangent * (radius * sin(angle)) +
                     normal * sqrt(max(0.0, 1.0 - u1)));
}

vec3 samplePath(vec3 rayOrigin, vec3 rayDirection, inout uint state)
{
    vec3 radiance = vec3(0.0);
    vec3 throughput = vec3(1.0);
    ivec3 originCell = ivec3(floor(rayOrigin));
    uint originMaterial = insideWorld(originCell) ? texelFetch(uVoxels, originCell, 0).r : 0u;
    bool travellingThroughTransmissive = materialIsTransmissive(originMaterial);
    if (travellingThroughTransmissive)
    {
        // Camera rays that begin inside a transparent cell skip the containing medium
        // instead of repeatedly hitting the zero-distance voxel with a zero normal.
        vec4 startSurface = texelFetch(uMaterialSurface, int(originMaterial), 0);
        throughput *= mix(vec3(0.62), clamp(startSurface.rgb * 1.35 + 0.12, 0.18, 1.0), 0.72);
    }
    vec3 primaryLightDirection;
    vec3 primaryLightColor;
    float primaryLightIntensity;
    float daylight;
    celestialLight(primaryLightDirection, primaryLightColor, primaryLightIntensity, daylight);

    for (int bounce = 0; bounce < 12; ++bounce)
    {
        if (bounce >= uMaxBounces) break;
        Hit hit = traceVoxelWorld(rayOrigin, rayDirection, 140.0, travellingThroughTransmissive);
        if (!hit.found)
        {
            radiance += throughput * skyColor(rayDirection);
            break;
        }

        travellingThroughTransmissive = false;

        // Selection is part of the light transport instead of a screen-space overlay.
        // Emitting it at every bounce makes the frame visible in mirrors, water and glass.
        if (uHasSelection != 0 && hit.grid == uSelectedGrid && all(equal(hit.cell, uSelectedCell)))
        {
            vec3 local = abs(fract(hit.position + hit.normal * 0.001) - 0.5);
            vec3 tangentMask = vec3(1.0) - abs(hit.normal);
            float edge = max(local.x * tangentMask.x, max(local.y * tangentMask.y, local.z * tangentMask.z));
            if (edge > 0.455)
            {
                radiance += throughput * vec3(0.8, 0.583, 0.06);
            }
        }

        float transmission = materialTransmission(hit.material);
        if (materialIsTransmissive(hit.material) &&
            (materialIsFluid(hit.material) || random(state) < transmission))
        {
            vec3 mediumAlbedo;
            float mediumRoughness;
            float mediumMetallic;
            vec3 mediumEmission;
            materialProperties(hit.material, mediumAlbedo, mediumRoughness, mediumMetallic, mediumEmission);
            radiance += throughput * mediumEmission;
            float ior = materialIsFluid(hit.material) ? 1.333 : materialIndexOfRefraction(hit.material);
            float facing = clamp(dot(-rayDirection, hit.normal), 0.0, 1.0);
            float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
            float fresnel = f0 + (1.0 - f0) * pow(1.0 - facing, 5.0);
            if (random(state) < fresnel)
            {
                rayDirection = reflect(rayDirection, hit.normal);
                rayOrigin = hit.position + hit.normal * 0.004;
                throughput *= mix(vec3(0.82), mediumAlbedo, 0.18);
            }
            else
            {
                vec3 transmitted = refract(rayDirection, hit.normal, 1.0 / ior);
                rayDirection = length(transmitted) > 0.001 ? normalize(transmitted) : rayDirection;
                if (mediumRoughness > 0.08)
                {
                    vec3 scattered = cosineHemisphere(-hit.normal, state);
                    rayDirection = normalize(mix(rayDirection, scattered, min(mediumRoughness * 0.32, 0.28)));
                }
                rayOrigin = hit.position - hit.normal * 0.006;
                throughput *= mix(vec3(0.72), clamp(mediumAlbedo * 1.25 + 0.08, 0.12, 1.0), 0.58);
                travellingThroughTransmissive = true;
            }
            continue;
        }

        vec3 albedo;
        float roughness;
        float metallic;
        vec3 emission;
        materialProperties(hit.material, albedo, roughness, metallic, emission);
        vec3 shadingNormal = hit.normal;
        applySurfaceDetail(hit.material, hit, albedo, roughness, shadingNormal);
        radiance += throughput * emission;

        Hit blocker = traceVoxelWorld(hit.position + hit.normal * 0.0025, primaryLightDirection, 140.0, true);
        float direct = blocker.found ? 0.0 : max(dot(shadingNormal, primaryLightDirection), 0.0);
        radiance += throughput * albedo * primaryLightColor * direct * primaryLightIntensity * (1.0 - metallic);

        vec3 diffuseDirection = cosineHemisphere(shadingNormal, state);
        vec3 reflectedDirection = reflect(rayDirection, shadingNormal);
        float viewFacing = clamp(dot(-rayDirection, shadingNormal), 0.0, 1.0);
        float dielectricFresnel = 0.04 + 0.96 * pow(1.0 - viewFacing, 5.0);
        float specularChance = clamp(mix(dielectricFresnel, 0.92, metallic), 0.04, 0.96);
        bool chooseSpecular = random(state) < specularChance;
        rayDirection = chooseSpecular
            ? normalize(mix(reflectedDirection, diffuseDirection, roughness * roughness))
            : diffuseDirection;

        if (chooseSpecular)
        {
            vec3 f0 = mix(vec3(0.04), albedo, metallic);
            vec3 fresnelColor = f0 + (vec3(1.0) - f0) * pow(1.0 - viewFacing, 5.0);
            throughput *= fresnelColor / max(specularChance, 0.001);
        }
        else
        {
            throughput *= albedo * (1.0 - metallic) * (1.0 - dielectricFresnel) /
                          max(1.0 - specularChance, 0.001);
        }

        rayOrigin = hit.position + hit.normal * 0.003;

        if (bounce > 0)
        {
            float survive = clamp(max(throughput.r, max(throughput.g, throughput.b)), 0.15, 0.92);
            if (random(state) > survive) break;
            throughput /= survive;
        }
    }

    return radiance;
}

void main()
{
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(pixel, uResolution))) return;

    uint state = uint(pixel.x) * 1973u + uint(pixel.y) * 9277u + uint(uFrameIndex + 1) * 26699u + 911u;
    vec2 jitter = vec2(random(state), random(state)) - 0.5;
    vec2 ndc = ((vec2(pixel) + 0.5 + jitter) / vec2(uResolution)) * 2.0 - 1.0;
    ndc.x *= float(uResolution.x) / float(uResolution.y);
    vec3 rayDirection = normalize(
        uCameraForward +
        uCameraRight * ndc.x * uTanHalfFov +
        uCameraUp * ndc.y * uTanHalfFov);

    vec3 sampleColor = samplePath(uCameraPosition, rayDirection, state);
    vec3 previous = imageLoad(uAccumulation, pixel).rgb;
    float sampleCount = float(uFrameIndex + 1);
    vec3 accumulated = (uFrameIndex == 0)
        ? sampleColor
        : previous + (sampleColor - previous) / sampleCount;
    imageStore(uAccumulation, pixel, vec4(accumulated, 1.0));
}
