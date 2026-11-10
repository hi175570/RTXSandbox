struct Payload
{
    float4 colorAndDistance;
    float4 lastMirrorPlane;
    float4 mirrorTintAndGap;
    uint depth;
    uint diagnosticFlags;
    uint mirrorCycleCount;
    uint padding;
};

struct Attributes
{
    float2 barycentrics;
};

struct WorldVertex
{
    float3 position;
    float3 normal;
    float2 uv;
    uint materialId;
    uint materialFace;
    float roughness;
    float metallic;
    uint gridMarker;
    float transmission;
    uint materialFlags;
    int3 cell;
    float3 emission;
};

struct EmissiveLight
{
    float3 position;
    float radius;
    float3 color;
    float intensity;
};

struct SphereAttributes
{
    float2 coordinates;
};

struct AnalyticSphereData
{
    float4 albedoAndRoughness;
    float4 metallicAndMaterial;
    float4 centerAndRadius;
};

RWTexture2D<float4> Output : register(u0);
RWTexture2D<float4> ReflectionTailWrite : register(u1);
RaytracingAccelerationStructure Scene : register(t0);
StructuredBuffer<WorldVertex> StaticVertices : register(t1);
Texture2D<float4> MaterialAtlas : register(t2);
Texture2D<float2> MaterialDetailAtlas : register(t3);
StructuredBuffer<EmissiveLight> EmissiveLights : register(t4);
StructuredBuffer<WorldVertex> DynamicVertices : register(t5);
StructuredBuffer<WorldVertex> SettledDebrisVertices : register(t8);
Texture2D<float4> ReflectionTailHistory : register(t6);
StructuredBuffer<AnalyticSphereData> AnalyticSpheres : register(t7);

static const int MaterialTileSize = 16;
static const int MaterialTilesPerRow = 128;

WorldVertex LoadVertex(uint index)
{
    if (InstanceID() == 0)
        return StaticVertices[index];
    if (InstanceID() == 1)
        return DynamicVertices[index];
    return SettledDebrisVertices[index];
}

int2 MaterialAtlasTexel(uint materialId, uint face, float2 uv)
{
    int tile = int(materialId) * 3 + int(face);
    int2 origin = int2((tile % MaterialTilesPerRow) * MaterialTileSize,
                       (tile / MaterialTilesPerRow) * MaterialTileSize);
    int2 pixel = clamp(int2(floor(frac(uv) * MaterialTileSize)), 0, MaterialTileSize - 1);
    return origin + pixel;
}

uint MaterialVariantHash(WorldVertex vertex, float2 uv)
{
    // Dynamic structures retain local cell coordinates while they move. Static
    // greedy faces use the integer UV tile. In both cases the variant is stable
    // on the object instead of swimming in world space as the camera/object moves.
    int2 tileCell = vertex.gridMarker != 0
        ? (vertex.materialFace == 0u || vertex.materialFace == 2u
            ? vertex.cell.xz
            : (vertex.materialFace == 1u ? vertex.cell.zy : vertex.cell.xy))
        : int2(floor(uv));
    uint hash = asuint(tileCell.x) * 0x8da6b343u;
    hash ^= asuint(tileCell.y) * 0xd8163841u;
    hash ^= vertex.materialId * 0xcb1ab31fu;
    hash ^= vertex.materialFace * 0x165667b1u;
    hash ^= hash >> 15;
    hash *= 0x2c1b3c6du;
    hash ^= hash >> 12;
    return hash;
}

float2 RotateMaterialUv(float2 value, uint rotation)
{
    if (rotation == 1u) return float2(value.y, 1.0 - value.x);
    if (rotation == 2u) return 1.0 - value;
    if (rotation == 3u) return float2(1.0 - value.y, value.x);
    return value;
}

float2 RotateMaterialDelta(float2 value, uint rotation)
{
    if (rotation == 1u) return float2(value.y, -value.x);
    if (rotation == 2u) return -value;
    if (rotation == 3u) return float2(-value.y, value.x);
    return value;
}

float2 MaterialVariantUv(WorldVertex vertex, float2 uv, out uint rotation)
{
    // Base iron (136), tread plate (157), and hazard stripes (158) use authored,
    // edge-periodic layouts.
    // Keeping one orientation makes adjacent voxel faces form a continuous surface.
    if (vertex.materialId == 136u || vertex.materialId == 157u || vertex.materialId == 158u)
    {
        rotation = 0u;
        return frac(uv);
    }
    uint hash = MaterialVariantHash(vertex, uv);
    rotation = hash & 3u;
    float2 local = RotateMaterialUv(frac(uv), rotation);
    // Pixel-aligned toroidal offsets preserve the deliberately pixel-like atlas
    // while providing 256 offsets in addition to four orientations. Adjacent
    // bronze/iron blocks therefore no longer stamp the same 16x16 patch.
    float2 offset = float2((hash >> 8) & 15u, (hash >> 16) & 15u) / MaterialTileSize;
    return frac(local + offset);
}

cbuffer CameraConstants : register(b0)
{
    float4 CameraPositionAndTanHalfFov;
    float4 CameraForwardAndAspect;
    float4 CameraRight;
    float4 CameraUp;
    float4 SunDirection;
    float4 RenderOptions;
    float4 TraceOptions;
    float4 TailOptions;
    // x ray distance, y shadow strength, z emissive strength, w reflection strength.
    float4 HardwareOptions;
    float4 PreviousCameraPositionAndTanHalfFov;
    float4 PreviousCameraForwardAndAspect;
    float4 PreviousCameraRight;
    float4 PreviousCameraUp;
    float4 SelectedCellAndGrid;
};

float3 DisplayEmission(float3 emission)
{
    float peak = max(emission.r, max(emission.g, emission.b));
    if (peak <= 0.0001)
        return 0.0;
    float3 chroma = saturate(emission / peak);
    chroma = pow(chroma, 1.65);
    // Keep the emitter visibly hot without letting ACES turn every LED face into
    // a featureless white patch. Its actual illumination is traced separately.
    return chroma * min(peak * 0.18, 1.55);
}

float3 GlassInterfaceFilter(float3 tint, float transmission)
{
    // Material albedo describes the volume filter, not an opaque surface coat.
    // The square root is intentional: a closed glass block normally crosses two
    // interfaces, and both interfaces together converge on this target tint.
    float peak = max(tint.r, max(tint.g, tint.b));
    float3 chromaticTint = peak > 0.001 ? saturate(tint / peak) : float3(1.0, 1.0, 1.0);
    // Beer-style chromatic absorption: two interfaces of a closed block combine
    // into a clearly colored, but still transmissive, light path.
    chromaticTint = pow(max(chromaticTint, float3(0.008, 0.008, 0.008)), 1.28);
    float3 volumeFilter = lerp(float3(1.0, 1.0, 1.0), chromaticTint,
        saturate(0.58 + transmission * 0.40));
    float transmittedEnergy = lerp(0.80, 0.975, saturate(transmission));
    return sqrt(saturate(volumeFilter * transmittedEnergy));
}

float3 GlassShadowFilter(uint materialId, uint face, float transmission, float roughness)
{
    // A shadow ray represents the finite solar disc, not a projector sampling one
    // nearest atlas texel.  Average stable points across the material tile so a
    // mosaic/frost pattern produces softly mixed transmitted light instead of a
    // razor-sharp checkerboard painted onto the receiver.
    int tile = int(materialId) * 3 + int(face);
    int2 origin = int2((tile % MaterialTilesPerRow) * MaterialTileSize,
                       (tile / MaterialTilesPerRow) * MaterialTileSize);
    float3 tint = (
        MaterialAtlas.Load(int3(origin + int2(2, 3), 0)).rgb +
        MaterialAtlas.Load(int3(origin + int2(12, 2), 0)).rgb +
        MaterialAtlas.Load(int3(origin + int2(4, 12), 0)).rgb +
        MaterialAtlas.Load(int3(origin + int2(13, 13), 0)).rgb) * 0.25;
    float peak = max(tint.r, max(tint.g, tint.b));
    float3 chroma = peak > 0.001 ? saturate(tint / peak) : float3(1.0, 1.0, 1.0);
    chroma = pow(max(chroma, float3(0.025, 0.025, 0.025)), 1.16);
    float colorStrength = saturate(0.30 + transmission * 0.45 - roughness * 0.22);
    float3 colored = lerp(float3(1.0, 1.0, 1.0), chroma, colorStrength);
    float energy = lerp(0.34, 0.76, saturate(transmission)) *
                   lerp(1.0, 0.72, saturate(roughness));
    // A closed volume normally crosses two interfaces; sqrt makes their product
    // converge on the intended volume attenuation without double-darkening.
    return sqrt(saturate(colored * energy));
}

uint CurrentPrimitiveKey()
{
    // TLAS has static (0), active dynamic (1), and settled debris (2) triangle
    // instances. Keep two bits for that identity; otherwise settled shards alias
    // static-world triangles and child rays can ignore the wrong surface.
    // Primitive zero remains reserved for "no primitive".
    return ((InstanceID() & 3u) << 30) | ((PrimitiveIndex() + 1u) & 0x3fffffffu);
}

float3 SelectionOutline(WorldVertex vertex, float3 hitPosition, float3 geometricNormal,
    float2 uv, uint rayDepth)
{
    if (TraceOptions.z < 0.5 || (rayDepth > 0 && TraceOptions.w < 0.5))
        return 0.0;
    uint selectedGrid = (uint)round(SelectedCellAndGrid.w);
    int3 selectedCell = int3(round(SelectedCellAndGrid.xyz));
    int3 hitCell = vertex.gridMarker == 0
        ? int3(floor(hitPosition - geometricNormal * 0.002))
        : vertex.cell;
    if (vertex.gridMarker != selectedGrid || any(hitCell != selectedCell))
        return 0.0;
    float2 edgeDistance = abs(frac(uv) - float2(0.5, 0.5));
    return max(edgeDistance.x, edgeDistance.y) > 0.455
        ? float3(0.8, 0.583, 0.06)
        : 0.0;
}

void ResetMirrorState(inout Payload payload)
{
    payload.lastMirrorPlane = float4(0.0, 0.0, 0.0, 0.0);
    // W carries conservative path throughput until a planar-mirror cycle uses it
    // for its measured gap. It lets visually irrelevant diffuse tails terminate
    // without changing the configured bounce limit for mirrors.
    payload.mirrorTintAndGap = float4(1.0, 1.0, 1.0, 1.0);
    payload.mirrorCycleCount = 0;
    payload.padding = 0;
}

float PathThroughput(Payload payload)
{
    // Planar mirrors must always retain the user's full recursion depth. Their
    // tail uses RGB tint/plane history instead of this scalar estimate.
    return dot(payload.lastMirrorPlane.xyz, payload.lastMirrorPlane.xyz) > 0.25
        ? 1.0
        : saturate(payload.mirrorTintAndGap.w);
}

bool ProjectIntoPreviousCamera(float3 worldPosition, out float2 uv)
{
    float3 offset = worldPosition - PreviousCameraPositionAndTanHalfFov.xyz;
    float forwardDistance = dot(offset, PreviousCameraForwardAndAspect.xyz);
    if (forwardDistance <= 0.02)
    {
        uv = float2(0.5, 0.5);
        return false;
    }
    float tanHalfFov = max(PreviousCameraPositionAndTanHalfFov.w, 0.001);
    float2 ndc = float2(
        dot(offset, PreviousCameraRight.xyz) /
            (forwardDistance * tanHalfFov * max(PreviousCameraForwardAndAspect.w, 0.001)),
        -dot(offset, PreviousCameraUp.xyz) / (forwardDistance * tanHalfFov));
    uv = ndc * 0.5 + 0.5;
    return all(uv >= 0.0) && all(uv <= 1.0);
}

float3 ReadReprojectedTail(float3 virtualPoint, float3 fallback)
{
    if (TailOptions.z < 0.5)
        return fallback;
    float2 uv;
    if (!ProjectIntoPreviousCamera(virtualPoint, uv))
        return fallback;
    uint width;
    uint height;
    ReflectionTailHistory.GetDimensions(width, height);
    int2 texel = clamp(int2(uv * float2(width, height)), int2(0, 0), int2(width - 1, height - 1));
    float3 history = ReflectionTailHistory.Load(int3(texel, 0)).rgb;
    bool valid = all(isfinite(history)) && max(history.r, max(history.g, history.b)) < 64.0;
    return valid ? max(history, 0.0) : fallback;
}

bool DetectPlanarMirrorCycle(float4 previousPlane, float3 currentNormal, float3 hitPosition,
    out float mirrorGap)
{
    mirrorGap = 0.0;
    if (dot(previousPlane.xyz, previousPlane.xyz) < 0.5)
        return false;
    float3 previousNormal = normalize(previousPlane.xyz);
    float parallel = abs(dot(previousNormal, currentNormal));
    float3 previousPoint = -previousPlane.w * previousNormal;
    float currentDistance = -dot(currentNormal, hitPosition);
    mirrorGap = abs(dot(currentNormal, previousPoint) + currentDistance);
    return parallel > 0.995 && mirrorGap > 0.05 && mirrorGap < 128.0;
}

float3 ApproximateMirrorTail(float3 seedColor, float3 localColor, float3 hitPosition,
    float3 reflectedDirection, float3 mirrorNormal, float mirrorGap, float3 mirrorTint,
    float reflectivity, float roughness, uint realDepth)
{
    uint targetLayer = (uint)round(TailOptions.y);
    if (TailOptions.x < 0.5 || targetLayer <= 29 || mirrorGap <= 0.0)
        return seedColor;

    const uint ReprojectedStart = 30;
    uint reprojectedEnd = min(targetLayer, 128u);
    uint sampleCount = min(8u, max(1u, reprojectedEnd - ReprojectedStart + 1u));
    float step = max(1.0, float(reprojectedEnd - ReprojectedStart + 1u) / float(sampleCount));
    float3 accumulated = float3(0.0, 0.0, 0.0);
    float accumulatedWeight = 0.0;
    float safeReflectivity = clamp(reflectivity, 0.02, 0.9995);
    float3 safeTint = clamp(mirrorTint, 0.08, 1.0);
    for (uint sampleIndex = 0; sampleIndex < 8; sampleIndex++)
    {
        if (sampleIndex >= sampleCount) break;
        float layer = min(float(reprojectedEnd), float(ReprojectedStart) +
            (float(sampleIndex) + 0.5) * step);
        float cycleDistance = mirrorGap * 2.0 * max(layer - float(realDepth), 1.0);
        float3 virtualPoint = hitPosition + reflectedDirection * cycleDistance -
            mirrorNormal * mirrorGap * (0.5 + fmod(layer, 2.0));
        float3 history = ReadReprojectedTail(virtualPoint, seedColor);
        float accumulatedRoughness = saturate(roughness * sqrt(max(layer - 29.0, 1.0)));
        history = lerp(history, localColor, accumulatedRoughness * 0.42);
        float energy = exp(log(safeReflectivity) * max(layer - float(realDepth), 1.0));
        float3 absorption = exp(log(safeTint) * min((layer - 29.0) / 96.0, 4.0));
        accumulated += max(history, 0.0) * absorption * energy;
        accumulatedWeight += energy;
    }
    float3 reprojected = accumulatedWeight > 0.00001
        ? accumulated / accumulatedWeight
        : seedColor;

    if (targetLayer > 128u)
    {
        float tailLayers = float(targetLayer - 128u);
        float firstTailEnergy = exp(log(safeReflectivity) * max(129.0 - float(realDepth), 1.0));
        float series = firstTailEnergy *
            (1.0 - exp(log(safeReflectivity) * tailLayers)) /
            max(1.0 - safeReflectivity, 0.0005);
        float convergence = saturate(series / (1.0 + series));
        float3 terminalAbsorption = exp(log(safeTint) * min(tailLayers / 192.0, 6.0));
        float3 analytic = lerp(reprojected, localColor * terminalAbsorption,
            saturate(roughness * 2.2 + 0.08));
        reprojected = lerp(reprojected, analytic, convergence * 0.72);
    }
    return clamp(reprojected, 0.0, 32.0);
}

bool IsBlackHole(AnalyticSphereData sphere)
{
    return sphere.metallicAndMaterial.z > 0.5 ||
           (uint)round(sphere.metallicAndMaterial.y) == 208u;
}

bool FindBlackHoleInfluence(
    float3 rayOrigin,
    float3 rayDirection,
    out float3 center,
    out float radius,
    out float entryDistance)
{
    center = 0.0;
    radius = 0.0;
    entryDistance = 1e30;
    bool found = false;
    // CameraRight.w contains the exact logical element count. Root SRVs do not
    // carry a descriptor range, so GetDimensions is not reliable for this buffer.
    uint sphereCount = (uint)max(round(CameraRight.w), 0.0);
    for (uint index = 0; index < sphereCount; index++)
    {
        AnalyticSphereData sphere = AnalyticSpheres[index];
        if (!IsBlackHole(sphere)) continue;
        float candidateRadius = max(sphere.centerAndRadius.w, 0.0001);
        float influenceRadius = candidateRadius * 9.0;
        float3 offset = rayOrigin - sphere.centerAndRadius.xyz;
        float projected = dot(offset, rayDirection);
        float discriminant = projected * projected -
            (dot(offset, offset) - influenceRadius * influenceRadius);
        if (discriminant < 0.0) continue;
        float root = sqrt(discriminant);
        float enter = max(-projected - root, 0.0);
        float leave = -projected + root;
        if (leave <= 0.0 || enter >= entryDistance) continue;
        found = true;
        entryDistance = enter;
        center = sphere.centerAndRadius.xyz;
        radius = candidateRadius;
    }
    return found;
}

void AccumulateAccretionDiskSegment(
    float3 rayOrigin,
    float3 rayDirection,
    float segmentLength,
    float3 center,
    float radius,
    inout float3 radiance,
    inout float transmittance)
{
    static const float3 DiskNormal = float3(0.081760, 0.980581, 0.178385);
    if (segmentLength <= 0.00001 || transmittance <= 0.002) return;

    float denominator = dot(rayDirection, DiskNormal);
    float startHeight = dot(rayOrigin - center, DiskNormal);
    float closestDistance = abs(denominator) > 0.00001
        ? clamp(-startHeight / denominator, 0.0, segmentLength)
        : segmentLength * 0.5;
    float3 diskPoint = rayOrigin + rayDirection * closestDistance;
    float3 offset = diskPoint - center;
    float3 radialVector = offset - DiskNormal * dot(offset, DiskNormal);
    float radialDistance = length(radialVector);
    float ratio = radialDistance / max(radius, 0.0001);
    if (ratio < 2.55 || ratio > 5.15) return;

    float3 radialDirection = radialVector / max(radialDistance, 0.0001);
    float3 tangent = normalize(cross(DiskNormal, radialDirection));
    float innerHeat = 1.0 - smoothstep(2.55, 5.15, ratio);
    float radialEnvelope = smoothstep(2.55, 2.78, ratio) *
        (1.0 - smoothstep(4.70, 5.15, ratio));
    float scaleHeight = lerp(0.075, 0.16, smoothstep(2.55, 5.15, ratio));
    float heightRatio = abs(dot(offset, DiskNormal)) /
        max(radius * scaleHeight, 0.0001);
    float3 diskAxis = normalize(cross(DiskNormal, float3(0.0, 0.0, 1.0)));
    float3 diskBitangent = cross(DiskNormal, diskAxis);
    float azimuth = atan2(dot(radialDirection, diskBitangent), dot(radialDirection, diskAxis));
    float densityWave = 0.82 + 0.18 *
        (0.5 + 0.5 * sin(azimuth * 3.0 - ratio * 7.5));
    float density = radialEnvelope * exp(-0.5 * heightRatio * heightRatio) * densityWave;
    if (density <= 0.00001) return;

    float pathLength = abs(denominator) > 0.00001
        ? min(segmentLength, 2.506628 * radius * scaleHeight / abs(denominator))
        : segmentLength;
    float opticalDepth = density * pathLength / max(radius * 0.24, 0.0001);
    float emittedFraction = 1.0 - exp(-opticalDepth * 0.82);
    float attenuation = exp(-opticalDepth * 0.42);

    float orbitalSpeed = sqrt(0.46 / max(ratio, 2.55));
    float lineOfSight = dot(tangent, -rayDirection);
    float doppler = pow(clamp(
        1.0 / max(1.0 - orbitalSpeed * lineOfSight, 0.30),
        0.46, 1.95), 2.35);
    float gravitationalShift = sqrt(clamp(
        1.0 - 1.0 / max(ratio, 1.001), 0.04, 1.0));
    float3 gas = lerp(float3(0.11, 0.0015, 0.0002),
        float3(2.8, 0.72, 0.055), pow(innerHeat, 0.70));
    gas = lerp(gas, float3(4.8, 3.0, 1.05), pow(innerHeat, 2.55));
    radiance += transmittance * gas * gravitationalShift * doppler *
        emittedFraction * HardwareOptions.z;
    transmittance *= attenuation;
}

void InitializePrimaryPayload(out Payload payload)
{
    payload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
    payload.depth = 0;
    payload.diagnosticFlags = 0;
    ResetMirrorState(payload);
}

void ResetRadianceTraceResult(Payload seed, out Payload payload)
{
    payload = seed;
    payload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
    payload.diagnosticFlags = 0;
}

// Schwarzschild optical metric in isotropic coordinates. The analytic sphere
// radius is the isotropic event horizon Rh = r_s / 4. Rather than applying a
// tuned screen displacement, DXR integrates Fermat transport in
// n(r) = (1 + Rh/r)^3 / (1 - Rh/r) for every radiance/visibility chord.
float3 SchwarzschildOpticalGradient(float3 position, float3 center, float horizonRadius)
{
    float3 offset = position - center;
    float radius = max(length(offset), horizonRadius * 1.001);
    float a = clamp(horizonRadius / radius, 0.0, 0.999);
    float radialDerivative = -a / radius *
        (3.0 / (1.0 + a) + 1.0 / (1.0 - a));
    return offset / radius * radialDerivative;
}

float3 SchwarzschildDirectionStep(
    float3 position,
    float3 direction,
    float3 center,
    float horizonRadius,
    float distance)
{
    float3 gradient = SchwarzschildOpticalGradient(position, center, horizonRadius);
    float3 curvature = gradient - direction * dot(direction, gradient);
    return normalize(direction + curvature * distance);
}

// Advance a ray through the same optical metric used by the DXR trace without
// querying scene geometry. Lighting uses this transport-only pass as the
// forward model for a shooting solve: a finite emitter is a boundary condition,
// not merely the direction of an initially straight shadow ray.
void IntegrateSchwarzschildPath(
    float3 rayOrigin,
    float3 initialDirection,
    float maximumDistance,
    out float3 endpoint,
    out float3 exitDirection,
    out bool captured)
{
    endpoint = rayOrigin;
    exitDirection = normalize(initialDirection);
    captured = false;
    if (maximumDistance <= 0.0001) return;

    float3 blackHoleCenter;
    float blackHoleRadius;
    float entryDistance;
    if (!FindBlackHoleInfluence(rayOrigin, exitDirection,
            blackHoleCenter, blackHoleRadius, entryDistance) ||
        entryDistance >= maximumDistance)
    {
        endpoint += exitDirection * maximumDistance;
        return;
    }

    float travelled = max(entryDistance, 0.0);
    endpoint += exitDirection * travelled;
    float previousRadius = length(endpoint - blackHoleCenter);
    bool passedPeriapsis = false;
    uint quality = (uint)round(RenderOptions.x);
    uint maximumSteps = quality >= 3u ? 144u : (quality >= 2u ? 96u : 64u);
    [loop]
    for (uint stepIndex = 0; stepIndex < 144u; stepIndex++)
    {
        if (stepIndex >= maximumSteps || travelled >= maximumDistance) break;
        float distanceToHole = length(endpoint - blackHoleCenter);
        if (distanceToHole <= blackHoleRadius * 1.002)
        {
            captured = true;
            return;
        }

        passedPeriapsis = passedPeriapsis ||
            distanceToHole > previousRadius + blackHoleRadius * 0.002;
        previousRadius = distanceToHole;
        float segmentLength = min(maximumDistance - travelled, clamp(
            (distanceToHole - blackHoleRadius) * 0.10,
            blackHoleRadius * 0.004,
            blackHoleRadius * 0.30));
        float3 midpointDirection = SchwarzschildDirectionStep(
            endpoint, exitDirection, blackHoleCenter, blackHoleRadius,
            segmentLength * 0.50);
        endpoint += midpointDirection * segmentLength;
        travelled += segmentLength;
        exitDirection = SchwarzschildDirectionStep(
            endpoint, midpointDirection, blackHoleCenter, blackHoleRadius,
            segmentLength * 0.50);
        if (passedPeriapsis &&
            length(endpoint - blackHoleCenter) > blackHoleRadius * 9.05)
            break;
    }

    endpoint += exitDirection * max(maximumDistance - travelled, 0.0);
}

bool SolvePointLightGeodesic(
    float3 rayOrigin,
    float3 target,
    out float3 localDirection,
    out float pathDistance,
    out float endpointError)
{
    float3 targetVector = target - rayOrigin;
    float geometricDistance = length(targetVector);
    localDirection = targetVector / max(geometricDistance, 0.0001);
    pathDistance = geometricDistance;
    endpointError = 0.0;
    if (geometricDistance <= 0.0001) return true;

    float3 blackHoleCenter;
    float blackHoleRadius;
    float entryDistance;
    if (!FindBlackHoleInfluence(rayOrigin, localDirection,
            blackHoleCenter, blackHoleRadius, entryDistance) ||
        entryDistance >= geometricDistance)
        return true;

    bool captured = false;
    [unroll]
    for (uint iteration = 0; iteration < 6u; iteration++)
    {
        float3 endpoint;
        float3 finalDirection;
        IntegrateSchwarzschildPath(rayOrigin, localDirection, pathDistance,
            endpoint, finalDirection, captured);
        float3 error = target - endpoint;
        endpointError = length(error);
        if (!captured && endpointError <= max(0.018, geometricDistance * 0.0015))
            return true;

        // Correct path length along the arriving tangent and initial angle in
        // its tangent plane. Damping keeps the solve stable near the photon
        // sphere, where multiple valid images and very large Jacobians exist.
        pathDistance = clamp(pathDistance + dot(error, finalDirection) * 0.72,
            geometricDistance * 0.72, geometricDistance * 2.5);
        float3 transverseError = error - finalDirection * dot(error, finalDirection);
        float3 angularCorrection = transverseError / max(pathDistance, 0.05);
        if (captured)
        {
            float3 radial = normalize(blackHoleCenter - rayOrigin);
            float3 away = localDirection - radial * dot(localDirection, radial);
            if (dot(away, away) < 0.000001)
                away = cross(localDirection, abs(localDirection.y) < 0.9
                    ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0));
            angularCorrection += normalize(away) * 0.055;
        }
        localDirection = normalize(localDirection + angularCorrection * 0.78);
    }

    float3 solvedEndpoint;
    float3 solvedFinalDirection;
    IntegrateSchwarzschildPath(rayOrigin, localDirection, pathDistance,
        solvedEndpoint, solvedFinalDirection, captured);
    endpointError = length(target - solvedEndpoint);
    return !captured && endpointError <= max(0.055, geometricDistance * 0.006);
}

bool SolveDirectionalLightGeodesic(
    float3 rayOrigin,
    float3 directionAtInfinity,
    out float3 localDirection)
{
    float3 targetDirection = normalize(directionAtInfinity);
    localDirection = targetDirection;
    float3 blackHoleCenter;
    float blackHoleRadius;
    float entryDistance;
    if (!FindBlackHoleInfluence(rayOrigin, localDirection,
            blackHoleCenter, blackHoleRadius, entryDistance) ||
        entryDistance >= HardwareOptions.x)
        return true;

    bool captured = false;
    [unroll]
    for (uint iteration = 0; iteration < 6u; iteration++)
    {
        float3 endpoint;
        float3 finalDirection;
        IntegrateSchwarzschildPath(rayOrigin, localDirection, HardwareOptions.x,
            endpoint, finalDirection, captured);
        if (!captured && dot(finalDirection, targetDirection) > 0.99996)
            return true;
        float3 angularError = targetDirection - finalDirection *
            dot(targetDirection, finalDirection);
        if (captured)
        {
            float3 radial = normalize(blackHoleCenter - rayOrigin);
            float3 away = localDirection - radial * dot(localDirection, radial);
            if (dot(away, away) < 0.000001)
                away = cross(localDirection, abs(localDirection.y) < 0.9
                    ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0));
            angularError += normalize(away) * 0.055;
        }
        localDirection = normalize(localDirection + angularError * 0.82);
    }

    float3 solvedEndpoint;
    float3 solvedDirection;
    IntegrateSchwarzschildPath(rayOrigin, localDirection, HardwareOptions.x,
        solvedEndpoint, solvedDirection, captured);
    return !captured && dot(solvedDirection, targetDirection) > 0.9985;
}

// Integrate every radiance ray through the bounded gravitational field. Each
// chord is submitted to the real TLAS, so primary, reflected and refracted light
// paths are bent by transport itself instead of warping a completed camera image.
void TraceCurvedRadiance(RayDesc initialRay, inout Payload payload)
{
    Payload seed = payload;
    float3 blackHoleCenter;
    float blackHoleRadius;
    float entryDistance;
    if (!FindBlackHoleInfluence(initialRay.Origin, initialRay.Direction,
            blackHoleCenter, blackHoleRadius, entryDistance))
    {
        ResetRadianceTraceResult(seed, payload);
        TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, initialRay, payload);
        return;
    }

    RayDesc segmentRay = initialRay;
    float travelled = 0.0;
    uint accumulatedFlags = 0;
    float3 diskRadiance = 0.0;
    float diskTransmittance = 1.0;
    if (entryDistance > 0.0005)
    {
        segmentRay.TMin = initialRay.TMin;
        segmentRay.TMax = min(entryDistance, initialRay.TMax);
        ResetRadianceTraceResult(seed, payload);
        TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, segmentRay, payload);
        accumulatedFlags |= payload.diagnosticFlags;
        if (payload.colorAndDistance.a >= 0.0) return;
        segmentRay.Origin += segmentRay.Direction * segmentRay.TMax;
        travelled += segmentRay.TMax;
        if (travelled >= initialRay.TMax)
        {
            payload.diagnosticFlags = accumulatedFlags;
            return;
        }
    }

    float previousRadius = length(segmentRay.Origin - blackHoleCenter);
    bool passedPeriapsis = false;
    uint quality = (uint)round(RenderOptions.x);
    uint maximumSteps = quality >= 3u ? 144u : (quality >= 2u ? 96u : 64u);
    [loop]
    for (uint stepIndex = 0; stepIndex < 144u; stepIndex++)
    {
        if (stepIndex >= maximumSteps || travelled >= initialRay.TMax) break;
        float3 fromHole = segmentRay.Origin - blackHoleCenter;
        float distanceToHole = length(fromHole);
        if (distanceToHole <= blackHoleRadius * 1.002)
        {
            ResetRadianceTraceResult(seed, payload);
            payload.colorAndDistance.rgb = diskRadiance;
            payload.colorAndDistance.a = travelled;
            payload.diagnosticFlags = accumulatedFlags | 1u;
            return;
        }

        passedPeriapsis = passedPeriapsis ||
            distanceToHole > previousRadius + blackHoleRadius * 0.002;
        previousRadius = distanceToHole;
        float remaining = initialRay.TMax - travelled;
        float segmentLength = min(remaining, clamp(
            (distanceToHole - blackHoleRadius) * 0.10,
            blackHoleRadius * 0.004,
            blackHoleRadius * 0.30));
        float3 midpointDirection = SchwarzschildDirectionStep(
            segmentRay.Origin, segmentRay.Direction, blackHoleCenter,
            blackHoleRadius, segmentLength * 0.50);

        segmentRay.TMin = 0.0005;
        segmentRay.TMax = segmentLength;
        segmentRay.Direction = midpointDirection;
        ResetRadianceTraceResult(seed, payload);
        TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, segmentRay, payload);
        accumulatedFlags |= payload.diagnosticFlags;
        float transportedDistance = payload.colorAndDistance.a >= 0.0
            ? min(payload.colorAndDistance.a, segmentLength)
            : segmentLength;
        AccumulateAccretionDiskSegment(
            segmentRay.Origin, midpointDirection, transportedDistance,
            blackHoleCenter, blackHoleRadius, diskRadiance, diskTransmittance);
        if (payload.colorAndDistance.a >= 0.0)
        {
            payload.colorAndDistance.rgb = diskRadiance +
                diskTransmittance * payload.colorAndDistance.rgb;
            payload.colorAndDistance.a += travelled;
            payload.diagnosticFlags = accumulatedFlags;
            return;
        }

        segmentRay.Origin += segmentRay.Direction * segmentLength;
        travelled += segmentLength;
        distanceToHole = max(length(blackHoleCenter - segmentRay.Origin),
            blackHoleRadius * 1.001);
        segmentRay.Direction = SchwarzschildDirectionStep(
            segmentRay.Origin, midpointDirection, blackHoleCenter,
            blackHoleRadius, segmentLength * 0.50);
        if (passedPeriapsis && distanceToHole > blackHoleRadius * 9.05) break;
    }

    segmentRay.TMin = 0.0005;
    segmentRay.TMax = max(initialRay.TMax - travelled, 0.0005);
    ResetRadianceTraceResult(seed, payload);
    TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, segmentRay, payload);
    accumulatedFlags |= payload.diagnosticFlags;
    payload.colorAndDistance.rgb = diskRadiance +
        diskTransmittance * payload.colorAndDistance.rgb;
    if (payload.colorAndDistance.a >= 0.0)
        payload.colorAndDistance.a += travelled;
    payload.diagnosticFlags = accumulatedFlags;
}

// Shadow and next-event-estimation rays are photons too.  March the actual
// visibility path through the same field instead of checking a straight chord;
// an event-horizon capture removes all remaining transported energy.
void TraceCurvedVisibility(RayDesc initialRay, uint rayFlags, inout Payload payload)
{
    float3 blackHoleCenter;
    float blackHoleRadius;
    float entryDistance;
    if (!FindBlackHoleInfluence(initialRay.Origin, initialRay.Direction,
            blackHoleCenter, blackHoleRadius, entryDistance))
    {
        TraceRay(Scene, rayFlags, 0xff, 0, 0, 1, initialRay, payload);
        return;
    }

    RayDesc segmentRay = initialRay;
    float travelled = 0.0;
    if (entryDistance > 0.0005)
    {
        segmentRay.TMin = initialRay.TMin;
        segmentRay.TMax = min(entryDistance, initialRay.TMax);
        TraceRay(Scene, rayFlags, 0xff, 0, 0, 1, segmentRay, payload);
        if (max(payload.colorAndDistance.r,
                max(payload.colorAndDistance.g, payload.colorAndDistance.b)) < 0.004)
            return;
        segmentRay.Origin += segmentRay.Direction * segmentRay.TMax;
        travelled += segmentRay.TMax;
        if (travelled >= initialRay.TMax) return;
    }

    float previousRadius = length(segmentRay.Origin - blackHoleCenter);
    bool passedPeriapsis = false;
    uint quality = (uint)round(RenderOptions.x);
    uint maximumSteps = quality >= 3u ? 144u : (quality >= 2u ? 96u : 64u);
    [loop]
    for (uint stepIndex = 0; stepIndex < 144u; stepIndex++)
    {
        if (stepIndex >= maximumSteps || travelled >= initialRay.TMax) break;
        float3 fromHole = segmentRay.Origin - blackHoleCenter;
        float distanceToHole = length(fromHole);
        if (distanceToHole <= blackHoleRadius * 1.002)
        {
            payload.colorAndDistance.rgb = 0.0;
            return;
        }

        passedPeriapsis = passedPeriapsis ||
            distanceToHole > previousRadius + blackHoleRadius * 0.002;
        previousRadius = distanceToHole;
        float segmentLength = min(initialRay.TMax - travelled, clamp(
            (distanceToHole - blackHoleRadius) * 0.10,
            blackHoleRadius * 0.004,
            blackHoleRadius * 0.30));
        float3 midpointDirection = SchwarzschildDirectionStep(
            segmentRay.Origin, segmentRay.Direction, blackHoleCenter,
            blackHoleRadius, segmentLength * 0.50);

        segmentRay.TMin = 0.0005;
        segmentRay.TMax = segmentLength;
        segmentRay.Direction = midpointDirection;
        TraceRay(Scene, rayFlags, 0xff, 0, 0, 1, segmentRay, payload);
        if (max(payload.colorAndDistance.r,
                max(payload.colorAndDistance.g, payload.colorAndDistance.b)) < 0.004)
            return;

        segmentRay.Origin += midpointDirection * segmentLength;
        travelled += segmentLength;
        distanceToHole = max(length(blackHoleCenter - segmentRay.Origin),
            blackHoleRadius * 1.001);
        segmentRay.Direction = SchwarzschildDirectionStep(
            segmentRay.Origin, midpointDirection, blackHoleCenter,
            blackHoleRadius, segmentLength * 0.50);
        if (passedPeriapsis && distanceToHole > blackHoleRadius * 9.05) break;
    }

    segmentRay.TMin = 0.0005;
    segmentRay.TMax = max(initialRay.TMax - travelled, 0.0005);
    TraceRay(Scene, rayFlags, 0xff, 0, 0, 1, segmentRay, payload);
}

[shader("raygeneration")]
void RayGen()
{
    uint2 pixel = DispatchRaysIndex().xy;
    float2 dimensions = float2(DispatchRaysDimensions().xy);
    float2 screen = ((float2(pixel) + 0.5) / dimensions) * 2.0 - 1.0;

    RayDesc ray;
    ray.Origin = CameraPositionAndTanHalfFov.xyz;
    ray.Direction = normalize(
        CameraForwardAndAspect.xyz +
        CameraRight.xyz * screen.x * CameraForwardAndAspect.w * CameraPositionAndTanHalfFov.w -
        CameraUp.xyz * screen.y * CameraPositionAndTanHalfFov.w);
    ray.TMin = 0.025;
    ray.TMax = HardwareOptions.x;

    Payload payload;
    InitializePrimaryPayload(payload);
    TraceCurvedRadiance(ray, payload);
    if (RenderOptions.w > 0.5)
    {
        float underwaterDistance = payload.colorAndDistance.a > 0.0 ? payload.colorAndDistance.a : 36.0;
        float fog = 1.0 - exp(-underwaterDistance * 0.075);
        payload.colorAndDistance.rgb = lerp(payload.colorAndDistance.rgb * float3(0.62, 0.88, 0.94),
            float3(0.012, 0.16, 0.27), saturate(fog * 0.78));
    }
    // Alpha stores a diagnostic bit mask consumed by the strict DXR smoke test.
    // bit 0: primary hit, bit 1: shadow ray, bit 2: occluded, bit 3: reflection ray,
    // bit 4: Physics Grid hit, bit 5: shared material atlas sampled,
    // bit 6: wax mode reached its third reflected ray.
    // The OpenGL display pass uses RGB only.
    float3 safeOutput = all(isfinite(payload.colorAndDistance.rgb))
        ? clamp(payload.colorAndDistance.rgb, 0.0, 32.0)
        : float3(0.0, 0.0, 0.0);
    float presentationAlpha = PreviousCameraUp.w < 0.5
        ? (payload.colorAndDistance.a >= 0.0 ? 1.0 : 0.0)
        : payload.diagnosticFlags / 255.0;
    Output[pixel] = float4(safeOutput, presentationAlpha);
    uint atlasScale = 4u;
    if (TailOptions.x > 0.5 && all((pixel % atlasScale) == 0u))
        ReflectionTailWrite[pixel / atlasScale] = float4(safeOutput, payload.colorAndDistance.a);
}

[shader("miss")]
void ShadowMiss(inout Payload payload)
{
    // Transmittance already starts at one and is accumulated by AnyHit.
}

[shader("miss")]
void Miss(inout Payload payload)
{
    if (PreviousCameraUp.w < 0.5)
    {
        payload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
        return;
    }
    float3 direction = WorldRayDirection();
    float horizon = saturate(direction.y * 0.65 + 0.45);
    float daylight = saturate(SunDirection.w);
    float3 daySky = lerp(float3(0.52, 0.62, 0.76), float3(0.035, 0.16, 0.52), horizon);
    float3 nightSky = lerp(float3(0.0012, 0.0018, 0.0045), float3(0.00008, 0.0002, 0.0008), horizon);
    // This is radiance at infinity, not a mesh around the black hole.  The
    // final TraceRay uses the direction produced by null-ray integration, so
    // the environment is lensed without placing fake glowing geometry in space.
    float3 galacticNormal = normalize(float3(0.23, 0.91, -0.34));
    float latitude = abs(dot(direction, galacticNormal));
    float milkyWay = exp(-latitude * latitude * 95.0);
    float3 fineCell = floor(direction * 920.0);
    float3 brightCell = floor(direction * 410.0 + 17.0);
    float fineHash = frac(sin(dot(fineCell, float3(12.9898, 78.233, 37.719))) * 43758.5453);
    float brightHash = frac(sin(dot(brightCell, float3(39.346, 11.135, 83.155))) * 24634.6345);
    float fineStar = smoothstep(0.9968 - milkyWay * 0.0020, 0.99995, fineHash);
    float brightStar = smoothstep(0.9985, 0.99998, brightHash);
    float temperature = frac(brightHash * 7.13 + fineHash * 3.71);
    float3 starColor = lerp(float3(0.48, 0.68, 1.0), float3(1.0, 0.58, 0.28), temperature);
    float3 starField = starColor * (fineStar * (0.75 + milkyWay * 1.1) + brightStar * 2.8);
    float3 galacticGlow = float3(0.075, 0.095, 0.16) * milkyWay;
    payload.colorAndDistance = float4(
        lerp(nightSky + starField + galacticGlow, daySky, daylight),
        -1.0);
}

[shader("anyhit")]
void AnyHit(inout Payload payload, Attributes attributes)
{
    // The accept-first shadow ray needs material-aware traversal: transparent
    // water/glass must not become an opaque black shadow caster.
    if ((RayFlags() & RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH) != 0)
    {
        uint first = PrimitiveIndex() * 3;
        WorldVertex shadowVertex = LoadVertex(first);
        if (RayTCurrent() < 0.006)
        {
            IgnoreHit();
        }
        else if (shadowVertex.transmission > 0.001)
        {
            payload.colorAndDistance.rgb *= GlassShadowFilter(
                shadowVertex.materialId, shadowVertex.materialFace,
                shadowVertex.transmission, shadowVertex.roughness);
            bool shadowDebris = (shadowVertex.materialFlags & 4u) != 0;
            if (shadowDebris)
                payload.mirrorCycleCount++;
            if (max(payload.colorAndDistance.r, max(payload.colorAndDistance.g, payload.colorAndDistance.b)) < 0.004)
                AcceptHitAndEndSearch();
            else if (shadowDebris && payload.mirrorCycleCount >= 4u)
                AcceptHitAndEndSearch();
            else
                IgnoreHit();
        }
        else
        {
            payload.colorAndDistance.rgb = float3(0.0, 0.0, 0.0);
            AcceptHitAndEndSearch();
        }
        return;
    }

    if (payload.padding != 0 && payload.padding == CurrentPrimitiveKey())
    {
        payload.padding = 0;
        IgnoreHit();
        return;
    }

    // A recursion limit may stop spawning another ray, but it must never change
    // which solid surface the current ray hits. Ignoring reflective opaque hits
    // here made glass refraction see through walls in Wax/Reference modes.
    uint first = PrimitiveIndex() * 3;
    WorldVertex vertex0 = LoadVertex(first);
    bool debrisHit = (vertex0.materialFlags & 4u) != 0;
    uint maximumReflectionBounces = (uint)round(TraceOptions.x);
    uint transparentReflectionBounces = min(maximumReflectionBounces,
        (uint)(RenderOptions.x >= 3.0 ? 8 : (RenderOptions.x >= 2.0 ? 6 : 4)));
    // Settled shards are stored in one immutable BLAS (instance 2). A primary ray
    // still traces every display pixel and intersects their exact triangles, but a
    // deep pile must not branch into 6-8 recursive glass layers per shard.
    if (debrisHit)
        transparentReflectionBounces = min(transparentReflectionBounces,
            InstanceID() == 2 ? 2u : 1u);
    if (vertex0.transmission > 0.001)
    {
        // Do not shade a transparent terminal surface as opaque. At the recursion
        // budget, continue to the next real surface instead of producing a gray wall.
        if (payload.depth >= transparentReflectionBounces)
            IgnoreHit();
        return;
    }
    // Every opaque hit proceeds to ClosestHit and remains an occluder, including
    // mirrors at the configured terminal depth.
}

[shader("closesthit")]
void ClosestHit(inout Payload payload, Attributes attributes)
{
    uint first = PrimitiveIndex() * 3;
    WorldVertex vertex0 = LoadVertex(first + 0);
    WorldVertex vertex1 = LoadVertex(first + 1);
    WorldVertex vertex2 = LoadVertex(first + 2);
    if (payload.depth == 0)
    {
        payload.diagnosticFlags |= 1;
        payload.diagnosticFlags |= 32;
        if (vertex0.gridMarker != 0)
            payload.diagnosticFlags |= 16;
    }
    float3 bary = float3(1.0 - attributes.barycentrics.x - attributes.barycentrics.y,
                         attributes.barycentrics.x,
                         attributes.barycentrics.y);
    float3 geometricNormal = normalize(
        vertex0.normal * bary.x +
        vertex1.normal * bary.y +
        vertex2.normal * bary.z);
    float3 normal = geometricNormal;
    float2 uv =
        vertex0.uv * bary.x +
        vertex1.uv * bary.y +
        vertex2.uv * bary.z;
    uint materialId = vertex0.materialId;
    uint materialFace = vertex0.materialFace;
    uint materialFlags = vertex0.materialFlags;
    bool fluidMaterial = (materialFlags & 1) != 0;
    bool porousFoam = (materialFlags & (1u << 3)) != 0;
    bool prismaticGlass = (materialFlags & (1u << 4)) != 0;
    bool frostedGlass = (materialFlags & (1u << 5)) != 0;
    bool lightAbsorber = (materialFlags & (1u << 7)) != 0;
    uint materialUvRotation = 0u;
    float2 materialUv = MaterialVariantUv(vertex0, uv, materialUvRotation);
    int2 atlasTexel = MaterialAtlasTexel(materialId, materialFace, materialUv);
    float3 albedo = fluidMaterial
        ? float3(0.035, 0.28, 0.48)
        : MaterialAtlas.Load(int3(atlasTexel, 0)).rgb;
    float2 detail = MaterialDetailAtlas.Load(int3(atlasTexel, 0));
    float roughness = fluidMaterial
        ? clamp(vertex0.roughness, 0.001, 1.0)
        : clamp(detail.r, 0.001, 1.0);
    float metallic =
        vertex0.metallic * bary.x +
        vertex1.metallic * bary.y +
        vertex2.metallic * bary.z;
    float transmission = vertex0.transmission;
    float3 emission = vertex0.emission;
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();

    // Use the same baked height detail as the path tracer to keep material texture
    // and roughness consistent instead of shading every block as a smooth wax cube.
    float2 heightGradient = float2(0.0, 0.0);
    if (!fluidMaterial)
    {
        float2 rightDelta = RotateMaterialDelta(float2(1.0 / MaterialTileSize, 0.0), materialUvRotation);
        float2 upDelta = RotateMaterialDelta(float2(0.0, 1.0 / MaterialTileSize), materialUvRotation);
        int2 rightTexel = MaterialAtlasTexel(materialId, materialFace, materialUv + rightDelta);
        int2 leftTexel = MaterialAtlasTexel(materialId, materialFace, materialUv - rightDelta);
        int2 upTexel = MaterialAtlasTexel(materialId, materialFace, materialUv + upDelta);
        int2 downTexel = MaterialAtlasTexel(materialId, materialFace, materialUv - upDelta);
        heightGradient = float2(
            (MaterialDetailAtlas.Load(int3(rightTexel, 0)).g - MaterialDetailAtlas.Load(int3(leftTexel, 0)).g) * 0.5,
            (MaterialDetailAtlas.Load(int3(upTexel, 0)).g - MaterialDetailAtlas.Load(int3(downTexel, 0)).g) * 0.5);
    }
    float3 edge1 = vertex1.position - vertex0.position;
    float3 edge2 = vertex2.position - vertex0.position;
    float2 uvEdge1 = vertex1.uv - vertex0.uv;
    float2 uvEdge2 = vertex2.uv - vertex0.uv;
    float determinant = uvEdge1.x * uvEdge2.y - uvEdge1.y * uvEdge2.x;
    if (!fluidMaterial && abs(determinant) > 0.00001)
    {
        float inverse = 1.0 / determinant;
        float3 tangent = normalize((edge1 * uvEdge2.y - edge2 * uvEdge1.y) * inverse);
        float3 bitangent = normalize((edge2 * uvEdge1.x - edge1 * uvEdge2.x) * inverse);
        normal = normalize(normal - tangent * heightGradient.x * 3.2 - bitangent * heightGradient.y * 3.2);
    }
    else if (fluidMaterial && abs(geometricNormal.y) > 0.65)
    {
        // Optical water waves stay analytic in DXR. The BLAS remains static, while
        // every ray sees the same continuous world-space phase across block/chunk
        // boundaries, avoiding both grid seams and per-frame acceleration rebuilds.
        float phase = TailOptions.w;
        float2 gradient = float2(
            cos(hitPosition.x * 0.72 + hitPosition.z * 0.31 + phase * 1.10) * 0.055 +
                cos(hitPosition.x * 2.10 - hitPosition.z * 1.40 - phase * 0.73) * 0.020,
            sin(hitPosition.z * 0.58 - hitPosition.x * 0.23 - phase * 0.83) * 0.055 +
                sin(hitPosition.z * 1.80 + hitPosition.x * 1.25 + phase * 0.61) * 0.020);
        float3 tangent = normalize(cross(float3(0.0, 0.0, 1.0), geometricNormal));
        float3 bitangent = normalize(cross(geometricNormal, tangent));
        normal = normalize(geometricNormal + tangent * gradient.x + bitangent * gradient.y);
        roughness = 0.035;
    }
    if (frostedGlass)
    {
        // A subtle stable microfacet layer softens the view without turning a real
        // frosted pane into noisy, violently refracting crystal.
        int2 microCell = int2(floor(frac(materialUv) * 256.0));
        uint microHash = asuint(microCell.x) * 0x9e3779b9u ^
                         asuint(microCell.y) * 0x85ebca6bu ^
                         materialId * 0xc2b2ae35u;
        microHash ^= microHash >> 16;
        float2 grain = float2(
            float(microHash & 1023u) / 511.5 - 1.0,
            float((microHash >> 10) & 1023u) / 511.5 - 1.0);
        float3 frostTangent = normalize(abs(normal.y) < 0.92
            ? cross(float3(0.0, 1.0, 0.0), normal)
            : cross(float3(1.0, 0.0, 0.0), normal));
        float3 frostBitangent = normalize(cross(normal, frostTangent));
        normal = normalize(normal + frostTangent * grain.x * 0.085 +
            frostBitangent * grain.y * 0.085);
        roughness = max(roughness, 0.50);
    }
    uint quality = (uint)round(RenderOptions.x);
    uint maximumReflectionBounces = (uint)round(TraceOptions.x);
    uint transparentReflectionBounces = min(maximumReflectionBounces,
        quality >= 3 ? 8u : (quality >= 2 ? 6u : 4u));
    if (frostedGlass)
        transparentReflectionBounces = min(transparentReflectionBounces, 4u);
    bool debrisHit = (materialFlags & 4u) != 0;
    if (debrisHit)
        transparentReflectionBounces = min(transparentReflectionBounces,
            InstanceID() == 2 ? 2u : 1u);
    float3 selectionOutline = SelectionOutline(vertex0, hitPosition, geometricNormal, uv, payload.depth);
    float3 displayEmission = DisplayEmission(emission) * HardwareOptions.z;

    // Absorption is semantic and absolute. Do not route this material through the
    // dielectric Fresnel fallback: reflection strength and render style must never
    // turn it into a dim mirror. Keep only the explicit selection outline usable.
    if (lightAbsorber)
    {
        payload.colorAndDistance = float4(selectionOutline, RayTCurrent());
        return;
    }

    if (transmission > 0.001 && payload.depth < transparentReflectionBounces)
    {
        payload.diagnosticFlags |= 128;
        bool entering = dot(WorldRayDirection(), geometricNormal) < 0.0;
        float3 facingNormal = entering ? normal : -normal;
        bool fluid = (materialFlags & 1) != 0;
        float encodedIor = float((materialFlags >> 8) & 0xffu) / 100.0;
        float materialIor = encodedIor >= 1.0 ? encodedIor : 1.52;
        float eta = entering
            ? (fluid ? (1.0 / 1.333) : (1.0 / materialIor))
            : (fluid ? 1.333 : materialIor);
        float3 refractedDirection = refract(WorldRayDirection(), facingNormal, eta);
        if (dot(refractedDirection, refractedDirection) < 0.001)
            refractedDirection = reflect(WorldRayDirection(), facingNormal);
        Payload refractionPayload;
        refractionPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
        refractionPayload.depth = payload.depth + 1;
        refractionPayload.diagnosticFlags = 0;
        ResetMirrorState(refractionPayload);
        refractionPayload.mirrorTintAndGap.w = PathThroughput(payload) * max(transmission, 0.05);
        RayDesc refractionRay;
        refractionRay.Direction = normalize(refractedDirection);
        // Start on the incident side and ignore only this triangle in AnyHit. If
        // glass touches a solid block, its coplanar face remains a valid hit; the
        // old 0.035 offset started inside that block and produced wall penetration.
        refractionRay.Origin = hitPosition - normalize(WorldRayDirection()) * 0.002;
        refractionRay.TMin = 0.0001;
        refractionRay.TMax = HardwareOptions.x;
        refractionPayload.padding = CurrentPrimitiveKey();
        TraceCurvedRadiance(refractionRay, refractionPayload);

        float3 refractedRadiance = refractionPayload.colorAndDistance.rgb;
        if (prismaticGlass && payload.depth == 0 && quality >= 2 && !debrisHit)
        {
            // Real spectral separation: red and blue use different IOR rays while
            // green keeps the base ray. This is material-specific dispersion, not a
            // colored overlay, so silhouettes split only where refraction warrants it.
            float redIor = max(1.01, materialIor - 0.030);
            float blueIor = min(2.55, materialIor + 0.045);
            float redEta = entering ? 1.0 / redIor : redIor;
            float blueEta = entering ? 1.0 / blueIor : blueIor;
            float3 redDirection = refract(WorldRayDirection(), facingNormal, redEta);
            float3 blueDirection = refract(WorldRayDirection(), facingNormal, blueEta);
            if (dot(redDirection, redDirection) < 0.001)
                redDirection = reflect(WorldRayDirection(), facingNormal);
            if (dot(blueDirection, blueDirection) < 0.001)
                blueDirection = reflect(WorldRayDirection(), facingNormal);

            Payload redPayload = refractionPayload;
            redPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
            redPayload.padding = CurrentPrimitiveKey();
            RayDesc redRay = refractionRay;
            redRay.Direction = normalize(redDirection);
            TraceCurvedRadiance(redRay, redPayload);

            Payload bluePayload = refractionPayload;
            bluePayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
            bluePayload.padding = CurrentPrimitiveKey();
            RayDesc blueRay = refractionRay;
            blueRay.Direction = normalize(blueDirection);
            TraceCurvedRadiance(blueRay, bluePayload);
            refractedRadiance = float3(
                redPayload.colorAndDistance.r,
                refractionPayload.colorAndDistance.g,
                bluePayload.colorAndDistance.b);
        }

        float viewCosine = saturate(dot(-WorldRayDirection(), facingNormal));
        float fresnel = 0.02 + 0.98 * pow(1.0 - viewCosine, 5.0);
        // At an internal near-normal interface the omitted reflection contributes
        // only a few percent. Reusing the transmitted radiance is a stable local
        // approximation and avoids an arbitrary blue cast when the branch is culled.
        float3 reflectedColor = refractedRadiance;
        // Refraction remains recursive so a ray can cross both sides of glass.
        // Capping only the secondary Fresnel branch prevents an exponential ray
        // tree when glass is reflected between 29-layer mirrors (which can TDR the
        // device); opaque mirror recursion still uses the full configured depth.
        float reflectedThroughput = PathThroughput(payload) * fresnel;
        bool primaryInterface = payload.depth == 0;
        // Front-facing clean glass is dominated by transmission (about 98%). Tracing
        // a second exact reflection ray for every full-screen pixel nearly doubled
        // close-up cost while contributing only a few percent. Keep real recursive
        // refraction, and promote reflection to an exact ray only once its Fresnel
        // energy is actually visible (grazing angles). No temporal guess or blur is
        // involved; the culled energy reuses the already traced transmitted radiance.
        bool visiblePrimaryReflection = primaryInterface &&
            fresnel >= 0.055 && reflectedThroughput >= 0.018;
        bool visibleInternalReflection = fresnel >= 0.16 && reflectedThroughput >= 0.028;
        if (quality >= 2 && payload.depth < 3 && !debrisHit && !frostedGlass &&
            (visiblePrimaryReflection || visibleInternalReflection))
        {
            Payload reflectedPayload;
            reflectedPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
            reflectedPayload.depth = payload.depth + 1;
            reflectedPayload.diagnosticFlags = 0;
            ResetMirrorState(reflectedPayload);
            reflectedPayload.mirrorTintAndGap.w = reflectedThroughput;
            RayDesc reflectedRay;
            reflectedRay.Direction = normalize(reflect(WorldRayDirection(), facingNormal));
            reflectedRay.Origin = hitPosition - normalize(WorldRayDirection()) * 0.002;
            reflectedRay.TMin = 0.0001;
            reflectedRay.TMax = HardwareOptions.x;
            reflectedPayload.padding = CurrentPrimitiveKey();
            TraceCurvedRadiance(reflectedRay, reflectedPayload);
            reflectedColor = reflectedPayload.colorAndDistance.rgb;
        }
        float3 transmitted = refractedRadiance *
            (fluid ? lerp(float3(1.0, 1.0, 1.0), albedo, 0.32)
                   : GlassInterfaceFilter(albedo, transmission));
        if (fluid) transmitted = lerp(transmitted, float3(0.018, 0.18, 0.31), 0.18);
        payload.colorAndDistance = float4(
            lerp(transmitted, reflectedColor, saturate(fresnel * 0.78)) + displayEmission + selectionOutline,
            RayTCurrent());
        return;
    }
    float3 sunDirection;
    bool sunPathSolved = SolveDirectionalLightGeodesic(
        hitPosition + geometricNormal * 0.012, normalize(SunDirection.xyz), sunDirection);
    float geometricDirect = dot(geometricNormal, sunDirection);
    float direct = sunPathSolved && geometricDirect > 0.001
        ? max(dot(normal, sunDirection), 0.0) : 0.0;
    float3 visibility = float3(1.0, 1.0, 1.0);

    // This is a real secondary DXR shadow ray. Occlusion leaves visible at zero;
    // only the dedicated shadow miss record changes it to one.
    // In the 20k full-resolution stress tier, secondary reflection/refraction
    // rays keep their real hit material and emitter surfaces but do not branch
    // again into sun visibility rays. Primary shadows remain exact.
    bool extremeSecondaryRay = CameraUp.w > 0.5 && payload.depth > 0;
    if (quality >= 1 && direct > 0.0 && !extremeSecondaryRay)
    {
        payload.diagnosticFlags |= 2;
        Payload shadowPayload;
        shadowPayload.colorAndDistance = float4(1.0, 1.0, 1.0, -1.0);
        shadowPayload.depth = 0;
        shadowPayload.diagnosticFlags = 0;
        ResetMirrorState(shadowPayload);
        RayDesc shadowRay;
        shadowRay.Origin = hitPosition + geometricNormal * 0.012;
        shadowRay.Direction = sunDirection;
        shadowRay.TMin = 0.003;
        shadowRay.TMax = HardwareOptions.x;
        TraceCurvedVisibility(shadowRay,
            RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER |
                RAY_FLAG_CULL_BACK_FACING_TRIANGLES,
            shadowPayload);
        visibility = lerp(float3(1.0, 1.0, 1.0),
            saturate(shadowPayload.colorAndDistance.rgb), saturate(HardwareOptions.y));
        if (min(visibility.r, min(visibility.g, visibility.b)) < 0.99)
            payload.diagnosticFlags |= 4;
    }

    float daylight = saturate(SunDirection.w);
    float directStrength = lerp(0.025, 0.88, daylight);
    // The old fixed 0.12-0.25 term illuminated sealed mirror rooms without any
    // light path. Retain only a very small night floor and daylight sky fill;
    // direct and emissive energy remain controlled by real visibility rays.
    float upwardAmbient = lerp(0.00035, 0.018 + 0.032 * max(normal.y, 0.0), daylight);
    // Open-cell foam has strong diffuse interreflection inside its pores. Give only
    // tagged foam a restrained rough bounce so a shadowed yellow sponge remains
    // yellow instead of becoming a black metallic-looking knot.
    if (porousFoam)
        upwardAmbient += lerp(0.003, 0.040, daylight) *
            (0.62 + 0.38 * abs(normal.y));
    // The height channel contributes normal relief and cavity occlusion only.
    // Reflection remains controlled solely by metallic/roughness, and emission
    // remains the explicit emission channel.
    float microOcclusion = fluidMaterial ? 1.0 : lerp(0.58, 1.0,
        smoothstep(0.20, 0.66, detail.g));
    float3 localColor = albedo * (upwardAmbient * microOcclusion + direct * visibility * directStrength) +
        displayEmission + selectionOutline;
    uint emissiveCount = extremeSecondaryRay
        ? 0u
        : min((uint)round(RenderOptions.z), debrisHit ? 8u : 32u);
    for (uint lightIndex = 0; lightIndex < emissiveCount; lightIndex++)
    {
        EmissiveLight light = EmissiveLights[lightIndex];
        float3 toLight = light.position - hitPosition;
        float distanceToLight = length(toLight);
        if (distanceToLight >= light.radius || distanceToLight < 0.05) continue;
        float3 lightDirection = toLight / distanceToLight;
        float solvedLightDistance = distanceToLight;
        float lightEndpointError = 0.0;
        bool lightPathSolved = SolvePointLightGeodesic(
            hitPosition + geometricNormal * 0.004, light.position,
            lightDirection, solvedLightDistance, lightEndpointError);
        if (!lightPathSolved) continue;
        float attenuation = pow(saturate(1.0 - distanceToLight / light.radius), 2.0) /
            (1.0 + distanceToLight * distanceToLight * 0.08);
        if (PathThroughput(payload) * light.intensity * attenuation < 0.00065)
            continue;
        float geometricLightDiffuse = dot(geometricNormal, lightDirection);
        if (geometricLightDiffuse <= 0.002) continue;
        float lightDiffuse = max(dot(normal, lightDirection), 0.0) *
            smoothstep(0.002, 0.08, geometricLightDiffuse);
        if (lightDiffuse <= 0.0) continue;

        // A glowing block contributes only when the real segment to its bounding
        // surface is unobstructed. Stopping before the emitter avoids treating its
        // own geometry specially, while every intervening opaque face still blocks.
        // A single centre ray can pass exactly through a shared voxel edge. DXR is
        // then allowed to miss both coplanar triangles and turns that numerical gap
        // into a long, perfectly straight light leak. Sample the emitter interior
        // at three stable locations and keep the most occluded value per channel.
        // Real glass transmission is retained, while a single edge miss can no
        // longer dominate the complete light contribution.
        float3 tangent = normalize(abs(lightDirection.y) < 0.92
            ? cross(lightDirection, float3(0.0, 1.0, 0.0))
            : cross(lightDirection, float3(1.0, 0.0, 0.0)));
        float3 bitangent = normalize(cross(lightDirection, tangent));
        float3 emissiveVisibility = float3(1.0, 1.0, 1.0);
        payload.diagnosticFlags |= 2;
        uint visibilitySampleCount = debrisHit ? 1u : (payload.depth == 0 ? 3u : 2u);
        [loop]
        for (uint visibilitySample = 0; visibilitySample < visibilitySampleCount; visibilitySample++)
        {
            float2 sampleOffset = visibilitySample == 0 ? float2(0.0, 0.0) :
                (visibilitySample == 1 ? float2(0.17, -0.11) : float2(-0.13, 0.16));
            float3 target = light.position + tangent * sampleOffset.x + bitangent * sampleOffset.y;
            float3 sampleVector = target - hitPosition;
            float sampleDistance = length(sampleVector);
            float3 sampleDirection = sampleVector / max(sampleDistance, 0.0001);
            float samplePathDistance = sampleDistance;
            float sampleEndpointError = 0.0;
            if (!SolvePointLightGeodesic(
                    hitPosition + geometricNormal * 0.004, target,
                    sampleDirection, samplePathDistance, sampleEndpointError))
            {
                emissiveVisibility = 0.0;
                break;
            }
            Payload emissiveShadowPayload;
            emissiveShadowPayload.colorAndDistance = float4(1.0, 1.0, 1.0, -1.0);
            emissiveShadowPayload.depth = 0;
            emissiveShadowPayload.diagnosticFlags = 0;
            ResetMirrorState(emissiveShadowPayload);
            RayDesc emissiveShadowRay;
            emissiveShadowRay.Origin = hitPosition + geometricNormal * 0.004;
            emissiveShadowRay.Direction = sampleDirection;
            emissiveShadowRay.TMin = 0.0015;
            float targetClearance = 0.48 /
                max(max(abs(sampleDirection.x), abs(sampleDirection.y)), abs(sampleDirection.z)) + 0.012;
            emissiveShadowRay.TMax = max(samplePathDistance - targetClearance, 0.002);
            TraceCurvedVisibility(emissiveShadowRay,
                RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
                emissiveShadowPayload);
            emissiveVisibility = min(emissiveVisibility,
                saturate(emissiveShadowPayload.colorAndDistance.rgb));
        }
        if (max(emissiveVisibility.r, max(emissiveVisibility.g, emissiveVisibility.b)) < 0.004)
        {
            payload.diagnosticFlags |= 4;
            continue;
        }
        if (min(emissiveVisibility.r, min(emissiveVisibility.g, emissiveVisibility.b)) < 0.99)
            payload.diagnosticFlags |= 4;
        localColor += albedo * light.color * emissiveVisibility *
            light.intensity * attenuation * lightDiffuse * 1.05 * HardwareOptions.z;
    }

    // Normal mode keeps reflection rays on metallic or genuinely smooth materials.
    // Wax mode deliberately enables them for every opaque material, while multiplying
    // (and capping) the material's original reflectivity so ordinary blocks stay solid.
    uint renderStyle = (uint)round(RenderOptions.y);
    bool waxMode = renderStyle == 1;
    bool referenceMode = renderStyle == 2;
    if (renderStyle == 3)
    {
        payload.colorAndDistance = float4(
            normal * 0.5 + 0.5 + float3(roughness * 0.18, metallic * 0.18, 0.0),
            RayTCurrent());
        return;
    }
    float dielectricReflection = 0.025 + saturate(1.0 - roughness) * 0.10;
    // Metallic is not a mirror-opacity slider. Conductors keep a coloured F0 and
    // rough surfaces lose coherent reflected energy. The old scalar metallic blend
    // replaced bronze, brass and iron alike with the same sharp environment image.
    float3 specularF0 = lerp(float3(0.04, 0.04, 0.04), saturate(albedo), metallic);
    float roughnessEnergy = saturate(1.0 - roughness * 0.82);
    float reflectivity = saturate(max(
        max(specularF0.r, max(specularF0.g, specularF0.b)) * roughnessEnergy,
        dielectricReflection));
    if (waxMode)
        reflectivity = metallic > 0.15 ? saturate(reflectivity * 2.4) : min(reflectivity * 2.4, 0.30);
    reflectivity = saturate(reflectivity * HardwareOptions.w);
    bool reflectionAllowed = quality >= 2 &&
        (waxMode || referenceMode || metallic > 0.15 || roughness < (quality >= 3 ? 0.72 : 0.45));
    bool planarMirror = transmission <= 0.001 && metallic > 0.90 && roughness < 0.035;
    float4 currentMirrorPlane = float4(normal, -dot(normal, hitPosition));
    float mirrorGap = 0.0;
    bool mirrorCycle = planarMirror &&
        DetectPlanarMirrorCycle(payload.lastMirrorPlane, normal, hitPosition, mirrorGap);
    float3 cycleTint = clamp(payload.mirrorTintAndGap.rgb * lerp(float3(1.0, 1.0, 1.0), albedo, 0.22),
        0.04, 1.0);
    uint infiniteMirrorMode = (uint)round(TraceOptions.y);
    float transitionStart = max(1.0, float(maximumReflectionBounces) - 5.0);
    float viewFresnel = pow(1.0 - saturate(dot(-WorldRayDirection(), normal)), 5.0);
    float reflectionWeight = saturate(reflectivity + viewFresnel * (1.0 - reflectivity));
    float3 conductorReflectionWeight = saturate(
        (specularF0 + (1.0 - specularF0) * viewFresnel) * roughnessEnergy * HardwareOptions.w);
    if (waxMode)
        conductorReflectionWeight = saturate(conductorReflectionWeight * 2.4);
    float projectedContribution = PathThroughput(payload) * reflectionWeight;
    bool persistentSpecular = planarMirror || (metallic > 0.90 && roughness < 0.035);
    bool meaningfulReflection = persistentSpecular || payload.depth < 2 || projectedContribution >= 0.0015;
    uint opaqueReflectionLimit = CameraUp.w > 0.5
        ? min(maximumReflectionBounces, 1u)
        : maximumReflectionBounces;
    uint ordinaryReflectionBounces = min(opaqueReflectionLimit,
        quality >= 3 ? 4u : (quality >= 2 ? 3u : 2u));
    uint materialReflectionBounces = persistentSpecular
        ? opaqueReflectionLimit
        : ordinaryReflectionBounces;
    // At the requested limit, keep this hit's actual DXR direct lighting/shadows
    // but emit no extra reflection ray. This avoids both a black terminal layer
    // and a non-recursive "fake world" beyond the configured reflection count.
    if (payload.depth < materialReflectionBounces && reflectionAllowed && reflectivity > 0.015 &&
        meaningfulReflection)
    {
        payload.diagnosticFlags |= 8;
        if (waxMode && payload.depth >= 2)
            payload.diagnosticFlags |= 64;
        RayDesc reflectionRay;
        reflectionRay.Origin = WorldRayOrigin() + WorldRayDirection() * RayTCurrent() + normal * 0.025;
        reflectionRay.Direction = normalize(reflect(WorldRayDirection(), normal));
        reflectionRay.TMin = 0.01;
        reflectionRay.TMax = HardwareOptions.x;

        Payload reflectionPayload;
        reflectionPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
        reflectionPayload.depth = payload.depth + 1;
        reflectionPayload.diagnosticFlags = 0;
        reflectionPayload.padding = 0;
        if (planarMirror)
        {
            reflectionPayload.lastMirrorPlane = currentMirrorPlane;
            reflectionPayload.mirrorTintAndGap = float4(cycleTint, mirrorCycle ? mirrorGap : payload.mirrorTintAndGap.w);
            reflectionPayload.mirrorCycleCount = mirrorCycle ? payload.mirrorCycleCount + 1 : 0;
        }
        else
        {
            ResetMirrorState(reflectionPayload);
            reflectionPayload.mirrorTintAndGap.w = projectedContribution;
        }
        TraceCurvedRadiance(reflectionRay, reflectionPayload);
        payload.diagnosticFlags |= reflectionPayload.diagnosticFlags;

        float3 reflectedResult = reflectionPayload.colorAndDistance.rgb;
        if (infiniteMirrorMode > 0 && mirrorCycle && payload.depth + 1 >= (uint)transitionStart)
        {
            float3 approximatedTail = ApproximateMirrorTail(
                reflectedResult,
                localColor,
                hitPosition,
                reflectionRay.Direction,
                normal,
                mirrorGap,
                cycleTint,
                reflectionWeight,
                roughness,
                payload.depth + 1);
            float transition = smoothstep(transitionStart, float(maximumReflectionBounces),
                float(payload.depth + 1));
            reflectedResult = lerp(reflectedResult, approximatedTail, transition * 0.82);
        }
        if (metallic > 0.15)
        {
            // Preserve the material's own lighting and tint each reflected channel by
            // the conductor F0. Perfect mirrors still converge to full reflection;
            // ordinary rough metals retain recognisable bronze/brass/iron colour.
            localColor = localColor * (1.0 - conductorReflectionWeight) +
                reflectedResult * conductorReflectionWeight;
        }
        else if (referenceMode)
        {
            localColor = lerp(localColor, reflectedResult, reflectionWeight);
        }
        else
        {
            // Dielectric blocks are opaque: retain nearly all diffuse/albedo lighting
            // and layer only a bounded reflected highlight over it. This avoids the
            // previous glass-like look caused by replacing the surface with the scene.
            float solidReflectionWeight = min(
                waxMode ? reflectionWeight * 1.7 : reflectionWeight,
                waxMode ? 0.48 : 0.16);
            localColor = localColor * (1.0 - solidReflectionWeight * 0.35) +
                reflectedResult * solidReflectionWeight;
        }
    }
    else if (infiniteMirrorMode > 0 && payload.depth >= maximumReflectionBounces &&
             reflectionAllowed && planarMirror && mirrorCycle)
    {
        float3 terminalDirection = normalize(reflect(WorldRayDirection(), normal));
        float3 tail = ApproximateMirrorTail(
            localColor,
            localColor,
            hitPosition,
            terminalDirection,
            normal,
            mirrorGap,
            cycleTint,
            reflectivity,
            roughness,
            payload.depth);
        float terminalWeight = saturate(reflectivity + 0.18);
        localColor = lerp(localColor, tail, terminalWeight * 0.88);
    }

    payload.colorAndDistance = float4(localColor, RayTCurrent());
}

[shader("intersection")]
void SphereIntersection()
{
    // Every TLAS sphere instance scales/translates this one unit procedural AABB.
    // DXR transforms the ray into object space; solving the implicit equation here
    // gives a genuinely smooth sphere with no triangle tessellation at any distance.
    float3 origin = ObjectRayOrigin();
    float3 direction = ObjectRayDirection();
    float a = dot(direction, direction);
    float b = dot(origin, direction);
    float c = dot(origin, origin) - 1.0;
    float discriminant = b * b - a * c;
    if (discriminant < 0.0 || a <= 0.0000001)
        return;
    float root = sqrt(discriminant);
    float first = (-b - root) / a;
    float second = (-b + root) / a;
    float candidate = first >= RayTMin() ? first : second;
    if (candidate < RayTMin() || candidate > RayTCurrent())
        return;
    SphereAttributes attributes;
    attributes.coordinates = float2(0.0, 0.0);
    ReportHit(candidate, 0, attributes);
}

[shader("anyhit")]
void SphereAnyHit(inout Payload payload, SphereAttributes attributes)
{
    if ((RayFlags() & RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH) != 0)
    {
        payload.colorAndDistance.rgb = float3(0.0, 0.0, 0.0);
        AcceptHitAndEndSearch();
    }
}

[shader("closesthit")]
void SphereClosestHit(inout Payload payload, SphereAttributes attributes)
{
    AnalyticSphereData sphere = AnalyticSpheres[InstanceID()];
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float radius = max(sphere.centerAndRadius.w, 0.0001);
    float3 normal = normalize((hitPosition - sphere.centerAndRadius.xyz) / radius);
    float3 albedo = saturate(sphere.albedoAndRoughness.rgb);
    float roughness = clamp(sphere.albedoAndRoughness.w, 0.001, 1.0);
    float metallic = saturate(sphere.metallicAndMaterial.x);
    bool blackHole = sphere.metallicAndMaterial.z > 0.5 ||
                     (uint)round(sphere.metallicAndMaterial.y) == 208u;
    uint quality = (uint)round(RenderOptions.x);
    uint maximumReflectionBounces = (uint)round(TraceOptions.x);
    if (payload.depth == 0)
    {
        payload.diagnosticFlags |= 1;
        payload.diagnosticFlags |= 32;
    }
    if (blackHole)
    {
        payload.colorAndDistance = float4(0.0, 0.0, 0.0, RayTCurrent());
        return;
    }

    float3 sunDirection;
    bool sunPathSolved = SolveDirectionalLightGeodesic(
        hitPosition + normal * 0.004, normalize(SunDirection.xyz), sunDirection);
    float direct = sunPathSolved ? max(dot(normal, sunDirection), 0.0) : 0.0;
    float3 visibility = float3(1.0, 1.0, 1.0);
    bool extremeSecondaryRay = CameraUp.w > 0.5 && payload.depth > 0;
    if (quality >= 1 && direct > 0.0 && !extremeSecondaryRay)
    {
        Payload shadowPayload;
        shadowPayload.colorAndDistance = float4(1.0, 1.0, 1.0, -1.0);
        shadowPayload.depth = 0;
        shadowPayload.diagnosticFlags = 0;
        ResetMirrorState(shadowPayload);
        RayDesc shadowRay;
        shadowRay.Origin = hitPosition + normal * 0.004;
        shadowRay.Direction = sunDirection;
        shadowRay.TMin = 0.002;
        shadowRay.TMax = HardwareOptions.x;
        TraceCurvedVisibility(shadowRay,
            RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
            shadowPayload);
        visibility = lerp(float3(1.0, 1.0, 1.0),
            saturate(shadowPayload.colorAndDistance.rgb), saturate(HardwareOptions.y));
        payload.diagnosticFlags |= 2;
        if (min(visibility.r, min(visibility.g, visibility.b)) < 0.99)
            payload.diagnosticFlags |= 4;
    }

    float daylight = saturate(SunDirection.w);
    float upwardAmbient = lerp(0.00035, 0.018 + 0.032 * max(normal.y, 0.0), daylight);
    float3 localColor = albedo *
        (upwardAmbient + direct * visibility * lerp(0.025, 0.88, daylight));

    uint renderStyle = (uint)round(RenderOptions.y);
    bool waxMode = renderStyle == 1;
    bool referenceMode = renderStyle == 2;
    if (renderStyle == 3)
    {
        payload.colorAndDistance = float4(
            normal * 0.5 + 0.5 + float3(roughness * 0.18, metallic * 0.18, 0.0),
            RayTCurrent());
        return;
    }

    float dielectricReflection = 0.025 + saturate(1.0 - roughness) * 0.10;
    float3 specularF0 = lerp(float3(0.04, 0.04, 0.04), saturate(albedo), metallic);
    float roughnessEnergy = saturate(1.0 - roughness * 0.82);
    float reflectivity = saturate(max(
        max(specularF0.r, max(specularF0.g, specularF0.b)) * roughnessEnergy,
        dielectricReflection));
    if (waxMode)
        reflectivity = metallic > 0.15 ? saturate(reflectivity * 2.4) : min(reflectivity * 2.4, 0.30);
    reflectivity = saturate(reflectivity * HardwareOptions.w);
    bool reflectionAllowed = quality >= 2 &&
        (waxMode || referenceMode || metallic > 0.15 || roughness < (quality >= 3 ? 0.72 : 0.45));
    float viewFresnel = pow(1.0 - saturate(dot(-WorldRayDirection(), normal)), 5.0);
    float reflectionWeight = saturate(reflectivity + viewFresnel * (1.0 - reflectivity));
    float3 conductorReflectionWeight = saturate(
        (specularF0 + (1.0 - specularF0) * viewFresnel) * roughnessEnergy * HardwareOptions.w);
    if (waxMode)
        conductorReflectionWeight = saturate(conductorReflectionWeight * 2.4);
    float projectedContribution = PathThroughput(payload) * reflectionWeight;
    bool persistentSpecular = metallic > 0.90 && roughness < 0.035;
    uint opaqueReflectionLimit = CameraUp.w > 0.5
        ? min(maximumReflectionBounces, 1u)
        : maximumReflectionBounces;
    uint ordinaryReflectionBounces = min(opaqueReflectionLimit,
        quality >= 3 ? 4u : (quality >= 2 ? 3u : 2u));
    uint materialReflectionBounces = persistentSpecular
        ? opaqueReflectionLimit
        : ordinaryReflectionBounces;
    if (payload.depth < materialReflectionBounces && reflectionAllowed && reflectivity > 0.015 &&
        (persistentSpecular || payload.depth < 2 || projectedContribution >= 0.0015))
    {
        RayDesc reflectionRay;
        reflectionRay.Origin = hitPosition + normal * 0.004;
        reflectionRay.Direction = normalize(reflect(WorldRayDirection(), normal));
        reflectionRay.TMin = 0.002;
        reflectionRay.TMax = HardwareOptions.x;
        Payload reflectionPayload;
        reflectionPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
        reflectionPayload.depth = payload.depth + 1;
        reflectionPayload.diagnosticFlags = 0;
        ResetMirrorState(reflectionPayload);
        reflectionPayload.mirrorTintAndGap.w = projectedContribution;
        TraceCurvedRadiance(reflectionRay, reflectionPayload);
        payload.diagnosticFlags |= 8 | reflectionPayload.diagnosticFlags;
        localColor = metallic > 0.15
            ? localColor * (1.0 - conductorReflectionWeight) +
                reflectionPayload.colorAndDistance.rgb * conductorReflectionWeight
            : referenceMode
                ? lerp(localColor, reflectionPayload.colorAndDistance.rgb, reflectionWeight)
                : localColor + reflectionPayload.colorAndDistance.rgb * min(reflectionWeight, 0.16);
    }
    payload.colorAndDistance = float4(localColor, RayTCurrent());
}
