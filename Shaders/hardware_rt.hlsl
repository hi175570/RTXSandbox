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
Texture2D<float4> ReflectionTailHistory : register(t6);
StructuredBuffer<AnalyticSphereData> AnalyticSpheres : register(t7);

static const int MaterialTileSize = 16;
static const int MaterialTilesPerRow = 128;

WorldVertex LoadVertex(uint index)
{
    if (InstanceID() == 0)
        return StaticVertices[index];
    return DynamicVertices[index];
}

int2 MaterialAtlasTexel(uint materialId, uint face, float2 uv)
{
    int tile = int(materialId) * 3 + int(face);
    int2 origin = int2((tile % MaterialTilesPerRow) * MaterialTileSize,
                       (tile / MaterialTilesPerRow) * MaterialTileSize);
    int2 pixel = clamp(int2(floor(frac(uv) * MaterialTileSize)), 0, MaterialTileSize - 1);
    return origin + pixel;
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

uint CurrentPrimitiveKey()
{
    // TLAS currently has static (0) and dynamic (1) geometry instances. Zero is
    // reserved for "no primitive", allowing a child ray to ignore exactly the
    // surface that spawned it without skipping a coplanar opaque neighbor.
    return ((InstanceID() & 1u) << 31) | ((PrimitiveIndex() + 1u) & 0x7fffffffu);
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
    payload.mirrorTintAndGap = float4(1.0, 1.0, 1.0, 0.0);
    payload.mirrorCycleCount = 0;
    payload.padding = 0;
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
    payload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
    payload.depth = 0;
    payload.diagnosticFlags = 0;
    ResetMirrorState(payload);
    TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, ray, payload);
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
    Output[pixel] = float4(safeOutput, payload.diagnosticFlags / 255.0);
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
    float3 direction = WorldRayDirection();
    float horizon = saturate(direction.y * 0.65 + 0.45);
    float daylight = saturate(SunDirection.w);
    float3 daySky = lerp(float3(0.52, 0.62, 0.76), float3(0.035, 0.16, 0.52), horizon);
    float3 nightSky = lerp(float3(0.0012, 0.0018, 0.0045), float3(0.00008, 0.0002, 0.0008), horizon);
    payload.colorAndDistance = float4(
        lerp(nightSky, daySky, daylight),
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
            WorldVertex shadowVertex1 = LoadVertex(first + 1);
            WorldVertex shadowVertex2 = LoadVertex(first + 2);
            float3 bary = float3(1.0 - attributes.barycentrics.x - attributes.barycentrics.y,
                                 attributes.barycentrics.x, attributes.barycentrics.y);
            float2 uv = shadowVertex.uv * bary.x + shadowVertex1.uv * bary.y + shadowVertex2.uv * bary.z;
            float3 tint = MaterialAtlas.Load(int3(
                MaterialAtlasTexel(shadowVertex.materialId, shadowVertex.materialFace, uv), 0)).rgb;
            payload.colorAndDistance.rgb *= GlassInterfaceFilter(tint, shadowVertex.transmission);
            if (max(payload.colorAndDistance.r, max(payload.colorAndDistance.g, payload.colorAndDistance.b)) < 0.004)
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
    uint maximumReflectionBounces = (uint)round(TraceOptions.x);
    uint first = PrimitiveIndex() * 3;
    WorldVertex vertex0 = LoadVertex(first);
    if (vertex0.transmission > 0.001)
    {
        // Do not shade a transparent terminal surface as opaque. At the recursion
        // budget, continue to the next real surface instead of producing a gray wall.
        if (payload.depth >= maximumReflectionBounces)
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
    int2 atlasTexel = MaterialAtlasTexel(materialId, materialFace, uv);
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

    // Use the same baked height detail as the path tracer to keep material texture
    // and roughness consistent instead of shading every block as a smooth wax cube.
    float2 heightGradient = float2(0.0, 0.0);
    if (!fluidMaterial)
    {
        int2 rightTexel = MaterialAtlasTexel(materialId, materialFace, uv + float2(1.0 / MaterialTileSize, 0.0));
        int2 upTexel = MaterialAtlasTexel(materialId, materialFace, uv + float2(0.0, 1.0 / MaterialTileSize));
        heightGradient = float2(
            MaterialDetailAtlas.Load(int3(rightTexel, 0)).g - detail.g,
            MaterialDetailAtlas.Load(int3(upTexel, 0)).g - detail.g);
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
        normal = normalize(normal - tangent * heightGradient.x * 1.65 - bitangent * heightGradient.y * 1.65);
    }
    uint quality = (uint)round(RenderOptions.x);
    uint maximumReflectionBounces = (uint)round(TraceOptions.x);
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    float3 selectionOutline = SelectionOutline(vertex0, hitPosition, geometricNormal, uv, payload.depth);
    float3 displayEmission = DisplayEmission(emission) * HardwareOptions.z;

    if (transmission > 0.001 && payload.depth < maximumReflectionBounces)
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
        RayDesc refractionRay;
        refractionRay.Direction = normalize(refractedDirection);
        // Start on the incident side and ignore only this triangle in AnyHit. If
        // glass touches a solid block, its coplanar face remains a valid hit; the
        // old 0.035 offset started inside that block and produced wall penetration.
        refractionRay.Origin = hitPosition - normalize(WorldRayDirection()) * 0.002;
        refractionRay.TMin = 0.0001;
        refractionRay.TMax = HardwareOptions.x;
        refractionPayload.padding = CurrentPrimitiveKey();
        TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, refractionRay, refractionPayload);

        float3 reflectedColor = float3(0.08, 0.27, 0.62);
        // Refraction remains recursive so a ray can cross both sides of glass.
        // Capping only the secondary Fresnel branch prevents an exponential ray
        // tree when glass is reflected between 29-layer mirrors (which can TDR the
        // device); opaque mirror recursion still uses the full configured depth.
        if (quality >= 2 && payload.depth < 3)
        {
            Payload reflectedPayload;
            reflectedPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
            reflectedPayload.depth = payload.depth + 1;
            reflectedPayload.diagnosticFlags = 0;
            ResetMirrorState(reflectedPayload);
            RayDesc reflectedRay;
            reflectedRay.Direction = normalize(reflect(WorldRayDirection(), facingNormal));
            reflectedRay.Origin = hitPosition - normalize(WorldRayDirection()) * 0.002;
            reflectedRay.TMin = 0.0001;
            reflectedRay.TMax = HardwareOptions.x;
            reflectedPayload.padding = CurrentPrimitiveKey();
            TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, reflectedRay, reflectedPayload);
            reflectedColor = reflectedPayload.colorAndDistance.rgb;
        }
        float fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(-WorldRayDirection(), facingNormal)), 5.0);
        float3 transmitted = refractionPayload.colorAndDistance.rgb *
            (fluid ? lerp(float3(1.0, 1.0, 1.0), albedo, 0.32)
                   : GlassInterfaceFilter(albedo, transmission));
        if (fluid) transmitted = lerp(transmitted, float3(0.018, 0.18, 0.31), 0.18);
        payload.colorAndDistance = float4(
            lerp(transmitted, reflectedColor, saturate(fresnel * 0.78)) + displayEmission + selectionOutline,
            RayTCurrent());
        return;
    }
    float3 sunDirection = normalize(SunDirection.xyz);
    float geometricDirect = dot(geometricNormal, sunDirection);
    float direct = geometricDirect > 0.001 ? max(dot(normal, sunDirection), 0.0) : 0.0;
    float3 visibility = float3(1.0, 1.0, 1.0);

    // This is a real secondary DXR shadow ray. Occlusion leaves visible at zero;
    // only the dedicated shadow miss record changes it to one.
    if (quality >= 1 && direct > 0.0)
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
        TraceRay(Scene,
            RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER |
                RAY_FLAG_CULL_BACK_FACING_TRIANGLES,
            0xff, 0, 0, 1, shadowRay, shadowPayload);
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
    float3 localColor = albedo * (upwardAmbient + direct * visibility * directStrength) +
        displayEmission + selectionOutline;
    uint emissiveCount = min((uint)round(RenderOptions.z), 32u);
    for (uint lightIndex = 0; lightIndex < emissiveCount; lightIndex++)
    {
        EmissiveLight light = EmissiveLights[lightIndex];
        float3 toLight = light.position - hitPosition;
        float distanceToLight = length(toLight);
        if (distanceToLight >= light.radius || distanceToLight < 0.05) continue;
        float3 lightDirection = toLight / distanceToLight;
        float attenuation = pow(saturate(1.0 - distanceToLight / light.radius), 2.0) /
            (1.0 + distanceToLight * distanceToLight * 0.08);
        float geometricLightDiffuse = dot(geometricNormal, lightDirection);
        if (geometricLightDiffuse <= 0.002) continue;
        float lightDiffuse = max(dot(normal, lightDirection), 0.0) *
            smoothstep(0.002, 0.08, geometricLightDiffuse);
        if (lightDiffuse <= 0.0) continue;

        // A glowing block contributes only when the real segment to its bounding
        // surface is unobstructed. Stopping before the emitter avoids treating its
        // own geometry specially, while every intervening opaque face still blocks.
        Payload emissiveShadowPayload;
        emissiveShadowPayload.colorAndDistance = float4(1.0, 1.0, 1.0, -1.0);
        emissiveShadowPayload.depth = 0;
        emissiveShadowPayload.diagnosticFlags = 0;
        ResetMirrorState(emissiveShadowPayload);
        RayDesc emissiveShadowRay;
        emissiveShadowRay.Origin = hitPosition + geometricNormal * 0.012;
        emissiveShadowRay.Direction = lightDirection;
        emissiveShadowRay.TMin = 0.003;
        float targetClearance = 0.5 /
            max(max(abs(lightDirection.x), abs(lightDirection.y)), abs(lightDirection.z)) + 0.012;
        emissiveShadowRay.TMax = max(distanceToLight - targetClearance, 0.004);
        payload.diagnosticFlags |= 2;
        TraceRay(Scene,
            RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
            0xff, 0, 0, 1, emissiveShadowRay, emissiveShadowPayload);
        float3 emissiveVisibility = saturate(emissiveShadowPayload.colorAndDistance.rgb);
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
    float reflectivity = saturate(max(metallic * (1.0 - roughness * 0.72), dielectricReflection));
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
    // At the requested limit, keep this hit's actual DXR direct lighting/shadows
    // but emit no extra reflection ray. This avoids both a black terminal layer
    // and a non-recursive "fake world" beyond the configured reflection count.
    if (payload.depth < maximumReflectionBounces && reflectionAllowed && reflectivity > 0.015)
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
        }
        TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, reflectionRay, reflectionPayload);
        payload.diagnosticFlags |= reflectionPayload.diagnosticFlags;

        float viewFresnel = pow(1.0 - saturate(dot(-WorldRayDirection(), normal)), 5.0);
        float reflectionWeight = saturate(reflectivity + viewFresnel * (1.0 - reflectivity));
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
        if (metallic > 0.15 || referenceMode)
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
    uint quality = (uint)round(RenderOptions.x);
    uint maximumReflectionBounces = (uint)round(TraceOptions.x);
    if (payload.depth == 0)
    {
        payload.diagnosticFlags |= 1;
        payload.diagnosticFlags |= 32;
    }

    float3 sunDirection = normalize(SunDirection.xyz);
    float direct = max(dot(normal, sunDirection), 0.0);
    float3 visibility = float3(1.0, 1.0, 1.0);
    if (quality >= 1 && direct > 0.0)
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
        TraceRay(Scene,
            RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
            0xff, 0, 0, 1, shadowRay, shadowPayload);
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
    float reflectivity = saturate(max(metallic * (1.0 - roughness * 0.72), dielectricReflection));
    if (waxMode)
        reflectivity = metallic > 0.15 ? saturate(reflectivity * 2.4) : min(reflectivity * 2.4, 0.30);
    reflectivity = saturate(reflectivity * HardwareOptions.w);
    bool reflectionAllowed = quality >= 2 &&
        (waxMode || referenceMode || metallic > 0.15 || roughness < (quality >= 3 ? 0.72 : 0.45));
    if (payload.depth < maximumReflectionBounces && reflectionAllowed && reflectivity > 0.015)
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
        TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, reflectionRay, reflectionPayload);
        payload.diagnosticFlags |= 8 | reflectionPayload.diagnosticFlags;
        float viewFresnel = pow(1.0 - saturate(dot(-WorldRayDirection(), normal)), 5.0);
        float reflectionWeight = saturate(reflectivity + viewFresnel * (1.0 - reflectivity));
        localColor = metallic > 0.15 || referenceMode
            ? lerp(localColor, reflectionPayload.colorAndDistance.rgb, reflectionWeight)
            : localColor + reflectionPayload.colorAndDistance.rgb * min(reflectionWeight, 0.16);
    }
    payload.colorAndDistance = float4(localColor, RayTCurrent());
}
