struct Payload
{
    float4 colorAndDistance;
    uint depth;
    uint diagnosticFlags;
};

struct ShadowPayload
{
    uint visible;
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
    float3 emission;
};

struct EmissiveLight
{
    float3 position;
    float radius;
    float3 color;
    float intensity;
};

RWTexture2D<float4> Output : register(u0);
RaytracingAccelerationStructure Scene : register(t0);
StructuredBuffer<WorldVertex> StaticVertices : register(t1);
Texture2D<float4> MaterialAtlas : register(t2);
Texture2D<float2> MaterialDetailAtlas : register(t3);
StructuredBuffer<EmissiveLight> EmissiveLights : register(t4);
StructuredBuffer<WorldVertex> DynamicVertices : register(t5);

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
};

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
    ray.TMax = 220.0;

    Payload payload;
    payload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
    payload.depth = 0;
    payload.diagnosticFlags = 0;
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
    // bit 6: wax mode reflection ray.
    // The OpenGL display pass uses RGB only.
    Output[pixel] = float4(payload.colorAndDistance.rgb, payload.diagnosticFlags / 255.0);
}

[shader("miss")]
void ShadowMiss(inout ShadowPayload payload)
{
    payload.visible = 1;
}

[shader("miss")]
void Miss(inout Payload payload)
{
    float3 direction = WorldRayDirection();
    float horizon = saturate(direction.y * 0.65 + 0.45);
    payload.colorAndDistance = float4(
        lerp(float3(0.52, 0.62, 0.76), float3(0.035, 0.16, 0.52), horizon),
        -1.0);
}

[shader("anyhit")]
void AnyHit(inout Payload payload, Attributes attributes)
{
    // Primary/reflection/refraction rays still accept the nearest surface normally.
    // Only the accept-first shadow ray needs material-aware traversal: transparent
    // water/glass must not become an opaque black shadow caster.
    if ((RayFlags() & RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH) == 0)
        return;

    uint first = PrimitiveIndex() * 3;
    if (RayTCurrent() < 0.09 || LoadVertex(first).transmission > 0.001)
        IgnoreHit();
    else
        AcceptHitAndEndSearch();
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

    if (payload.depth == 0 && transmission > 0.001)
    {
        payload.diagnosticFlags |= 128;
        float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
        float3 facingNormal = dot(WorldRayDirection(), normal) < 0.0 ? normal : -normal;
        bool fluid = (materialFlags & 1) != 0;
        float eta = fluid ? (1.0 / 1.333) : (1.0 / 1.52);
        float3 refractedDirection = refract(WorldRayDirection(), facingNormal, eta);
        if (dot(refractedDirection, refractedDirection) < 0.001)
            refractedDirection = reflect(WorldRayDirection(), facingNormal);
        Payload refractionPayload;
        refractionPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
        refractionPayload.depth = 1;
        refractionPayload.diagnosticFlags = 0;
        RayDesc refractionRay;
        refractionRay.Origin = hitPosition - facingNormal * 0.035;
        refractionRay.Direction = normalize(refractedDirection);
        refractionRay.TMin = 0.02;
        refractionRay.TMax = 220.0;
        TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, refractionRay, refractionPayload);

        float3 reflectedColor = float3(0.08, 0.27, 0.62);
        if (quality >= 2)
        {
            Payload reflectedPayload;
            reflectedPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
            reflectedPayload.depth = 1;
            reflectedPayload.diagnosticFlags = 0;
            RayDesc reflectedRay;
            reflectedRay.Origin = hitPosition + facingNormal * 0.035;
            reflectedRay.Direction = normalize(reflect(WorldRayDirection(), facingNormal));
            reflectedRay.TMin = 0.02;
            reflectedRay.TMax = 220.0;
            TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, reflectedRay, reflectedPayload);
            reflectedColor = reflectedPayload.colorAndDistance.rgb;
        }
        float fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(-WorldRayDirection(), facingNormal)), 5.0);
        float3 transmitted = refractionPayload.colorAndDistance.rgb * lerp(float3(1.0, 1.0, 1.0), albedo, fluid ? 0.32 : 0.16);
        if (fluid) transmitted = lerp(transmitted, float3(0.018, 0.18, 0.31), 0.18);
        payload.colorAndDistance = float4(lerp(transmitted, reflectedColor, saturate(fresnel * 0.78)) + emission,
            RayTCurrent());
        return;
    }
    float3 sunDirection = normalize(SunDirection.xyz);
    float geometricDirect = dot(geometricNormal, sunDirection);
    float direct = geometricDirect > 0.001 ? max(dot(normal, sunDirection), 0.0) : 0.0;
    float visibility = 1.0;

    // This is a real secondary DXR shadow ray. Occlusion leaves visible at zero;
    // only the dedicated shadow miss record changes it to one.
    if (payload.depth == 0 && quality >= 1 && direct > 0.0)
    {
        payload.diagnosticFlags |= 2;
        ShadowPayload shadowPayload;
        shadowPayload.visible = 0;
        RayDesc shadowRay;
        float3 facingGeometricNormal = dot(WorldRayDirection(), geometricNormal) < 0.0
            ? geometricNormal : -geometricNormal;
        shadowRay.Origin = WorldRayOrigin() + WorldRayDirection() * RayTCurrent() +
            facingGeometricNormal * 0.045;
        shadowRay.Direction = sunDirection;
        shadowRay.TMin = 0.025;
        shadowRay.TMax = 220.0;
        TraceRay(Scene,
            RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER |
                RAY_FLAG_CULL_BACK_FACING_TRIANGLES,
            0xff, 0, 0, 1, shadowRay, shadowPayload);
        visibility = shadowPayload.visible != 0 ? 1.0 : 0.0;
        if (shadowPayload.visible == 0)
            payload.diagnosticFlags |= 4;
    }

    float upwardAmbient = 0.12 + 0.13 * max(normal.y, 0.0);
    float3 localColor = albedo * (upwardAmbient + direct * visibility * 0.88) + emission;
    uint emissiveCount = min((uint)round(RenderOptions.z), 32u);
    float3 hitPosition = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
    for (uint lightIndex = 0; lightIndex < emissiveCount; lightIndex++)
    {
        EmissiveLight light = EmissiveLights[lightIndex];
        float3 toLight = light.position - hitPosition;
        float distanceToLight = length(toLight);
        if (distanceToLight >= light.radius || distanceToLight < 0.05) continue;
        float3 lightDirection = toLight / distanceToLight;
        float attenuation = pow(saturate(1.0 - distanceToLight / light.radius), 2.0) /
            (1.0 + distanceToLight * distanceToLight * 0.08);
        float lightDiffuse = max(dot(normal, lightDirection), 0.0);
        localColor += albedo * light.color * light.intensity * attenuation * lightDiffuse * 3.2;
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
    bool reflectionAllowed = quality >= 2 &&
        (waxMode || referenceMode || metallic > 0.15 || roughness < (quality >= 3 ? 0.72 : 0.45));
    if (payload.depth == 0 && reflectionAllowed && reflectivity > 0.015)
    {
        payload.diagnosticFlags |= 8;
        if (waxMode)
            payload.diagnosticFlags |= 64;
        RayDesc reflectionRay;
        reflectionRay.Origin = WorldRayOrigin() + WorldRayDirection() * RayTCurrent() + normal * 0.025;
        reflectionRay.Direction = normalize(reflect(WorldRayDirection(), normal));
        reflectionRay.TMin = 0.01;
        reflectionRay.TMax = 220.0;

        Payload reflectionPayload;
        reflectionPayload.colorAndDistance = float4(0.0, 0.0, 0.0, -1.0);
        reflectionPayload.depth = 1;
        reflectionPayload.diagnosticFlags = 0;
        TraceRay(Scene, RAY_FLAG_NONE, 0xff, 0, 0, 0, reflectionRay, reflectionPayload);

        float viewFresnel = pow(1.0 - saturate(dot(-WorldRayDirection(), normal)), 5.0);
        float reflectionWeight = saturate(reflectivity + viewFresnel * (1.0 - reflectivity));
        if (metallic > 0.15 || referenceMode)
        {
            localColor = lerp(localColor, reflectionPayload.colorAndDistance.rgb, reflectionWeight);
        }
        else
        {
            // Dielectric blocks are opaque: retain nearly all diffuse/albedo lighting
            // and layer only a bounded reflected highlight over it. This avoids the
            // previous glass-like look caused by replacing the surface with the scene.
            float solidReflectionWeight = min(reflectionWeight, waxMode ? 0.28 : 0.16);
            localColor = localColor * (1.0 - solidReflectionWeight * 0.35) +
                reflectionPayload.colorAndDistance.rgb * solidReflectionWeight;
        }
    }

    payload.colorAndDistance = float4(localColor, RayTCurrent());
}
