#version 430 core

uniform sampler2D uMaterialAtlas;
uniform sampler2D uMaterialDetailAtlas;
uniform sampler2D uSceneColor;
uniform vec3 uCameraPosition;
uniform float uTimeOfDay;
uniform float uExposure;
uniform int uClassic;
uniform int uSpecularQuality;
uniform int uReflectionQuality;
uniform int uClassicLightingQuality;
uniform int uTransparentOnly;
uniform int uDeferredTransparent;
uniform int uWaterQuality;
uniform int uTransparencyQuality;
uniform vec2 uScreenSize;
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

vec2 atlasDetail(ivec2 offset)
{
    int tile = vMaterial * 3 + vFace;
    ivec2 tileOrigin = ivec2((tile % TILES_PER_ROW) * TILE_SIZE, (tile / TILES_PER_ROW) * TILE_SIZE);
    ivec2 pixel = clamp(ivec2(floor(fract(vUv) * float(TILE_SIZE))) + offset,
        ivec2(0), ivec2(TILE_SIZE - 1));
    return texelFetch(uMaterialDetailAtlas, tileOrigin + pixel, 0).rg;
}

void main()
{
    if (uTransparentOnly > 0 && vTransmission <= 0.0) discard;
    if (uTransparentOnly < 0 && vTransmission > 0.0) discard;
    // Water uses a continuous world-space color/wave model. Sampling a non-tiling
    // 16x16 block atlas independently on every fluid cell exposed its tile borders.
    vec3 albedo = vFluid != 0 ? vec3(0.035, 0.28, 0.48) : atlasColor();
    vec3 normal = normalize(vNormal);
    vec2 materialDetail = vFluid != 0 ? vec2(vRoughness, 0.5) : atlasDetail(ivec2(0));
    vec2 heightGradient = vec2(0.0);
    if (vFluid == 0)
        heightGradient = vec2(
            atlasDetail(ivec2(1, 0)).g - materialDetail.g,
            atlasDetail(ivec2(0, 1)).g - materialDetail.g);
    vec3 surfaceTangent = normalize(abs(normal.y) < 0.95
        ? cross(vec3(0.0, 1.0, 0.0), normal)
        : vec3(1.0, 0.0, 0.0));
    vec3 surfaceBitangent = normalize(cross(normal, surfaceTangent));
    if (vFluid == 0)
        normal = normalize(normal - surfaceTangent * heightGradient.x * 1.4 -
            surfaceBitangent * heightGradient.y * 1.4);
    if (vFluid != 0)
    {
        float time = uTimeOfDay * 3.2;
        vec2 waveGradient = vec2(
            cos(vWorldPosition.x * 5.7 + vWorldPosition.z * 2.1 + time) * 0.055,
            sin(vWorldPosition.z * 6.3 - vWorldPosition.x * 1.7 - time * 0.83) * 0.055);
        vec3 tangent = normalize(abs(normal.y) < 0.95
            ? cross(vec3(0.0, 1.0, 0.0), normal)
            : vec3(1.0, 0.0, 0.0));
        vec3 bitangent = normalize(cross(normal, tangent));
        normal = normalize(normal + tangent * waveGradient.x + bitangent * waveGradient.y);
    }
    vec3 viewDirection = normalize(uCameraPosition - vWorldPosition);
    float angle = (uTimeOfDay - 6.0) * (2.0 * PI / 24.0);
    vec3 sunDirection = normalize(vec3(cos(angle), sin(angle), 0.28));
    float daylight = smoothstep(-0.12, 0.18, sunDirection.y);
    vec3 lightDirection = daylight > 0.08 ? sunDirection : -sunDirection;
    vec3 color = albedo;

    if (uDeferredTransparent != 0 && vTransmission > 0.0)
    {
        vec2 screenUv = gl_FragCoord.xy / max(uScreenSize, vec2(1.0));
        float distortion = vFluid != 0 ? 0.018 : 0.006;
        distortion *= 0.35 + float(max(uWaterQuality, uTransparencyQuality)) * 0.22;
        vec2 refractedUv = clamp(screenUv + normal.xz * distortion, vec2(0.002), vec2(0.998));
        vec3 refracted = texture(uSceneColor, refractedUv).rgb;
        float blurRadius = materialDetail.r * materialDetail.r *
            (0.0015 + float(max(uTransparencyQuality, 1)) * 0.0012);
        if (vFluid == 0 && blurRadius > 0.0005)
        {
            vec3 blurred = refracted;
            blurred += texture(uSceneColor, clamp(refractedUv + vec2( blurRadius, 0.0), vec2(0.002), vec2(0.998))).rgb;
            blurred += texture(uSceneColor, clamp(refractedUv + vec2(-blurRadius, 0.0), vec2(0.002), vec2(0.998))).rgb;
            blurred += texture(uSceneColor, clamp(refractedUv + vec2(0.0,  blurRadius), vec2(0.002), vec2(0.998))).rgb;
            blurred += texture(uSceneColor, clamp(refractedUv + vec2(0.0, -blurRadius), vec2(0.002), vec2(0.998))).rgb;
            refracted = mix(refracted, blurred * 0.2, clamp(materialDetail.r, 0.0, 0.92));
        }
        vec3 reflected = sky(reflect(-viewDirection, normal), daylight);
        float fresnel = 0.035 + 0.965 * pow(1.0 - max(dot(normal, viewDirection), 0.0), 5.0);
        if (vFluid != 0)
        {
            float waterFog = 1.0 - exp(-length(uCameraPosition - vWorldPosition) * 0.018 * float(max(uWaterQuality, 1)));
            refracted = mix(refracted, vec3(0.015, 0.16, 0.27), clamp(waterFog, 0.0, 0.72));
            color = mix(refracted, reflected, clamp(fresnel * 0.72, 0.05, 0.76));
        }
        else
        {
            color = mix(refracted * mix(vec3(1.0), albedo, 0.26), reflected,
                clamp(fresnel, 0.04, 0.68));
        }
        color += vEmission;
    }

    else if (uClassic == 0)
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
        float reflectionStrength = float(uReflectionQuality) / 3.0;
        float specularStrength = float(uSpecularQuality) / 3.0;
        color += reflectedSky * fresnel * (1.0 - vRoughness * 0.82) * reflectionStrength;
        color += vec3(specular) * fresnel * (1.0 - vRoughness) * 1.8 * specularStrength;
        color += vEmission;
    }
    else if (uClassicLightingQuality > 0)
    {
        float faceLight = 0.62 + max(normal.y, 0.0) * 0.38;
        if (uClassicLightingQuality > 1)
            faceLight = 0.28 + max(dot(normal, lightDirection), 0.0) * 0.72;
        color *= faceLight;
    }

    ivec3 fragmentCell = vGrid == 0
        ? ivec3(floor(vWorldPosition - normalize(vNormal) * 0.002))
        : vCell;
    if (uHasSelection != 0 && vGrid == uSelectedGrid && all(equal(fragmentCell, uSelectedCell)))
    {
        // Mesh UVs are face-local, so the outline stays attached to a rotated Grid.
        // World-space fract(position) made the frame remain axis-aligned while its body moved.
        vec2 edgeDistance = abs(vUv - vec2(0.5));
        float edge = max(edgeDistance.x, edgeDistance.y);
        if (edge > 0.455) color += vec3(0.667, 0.45, 0.027);
    }

    color = vec3(1.0) - exp(-color * uExposure);
    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
    float alpha = vFluid != 0
        ? clamp(1.0 - vTransmission * 0.82, 0.12, 0.42)
        : (vTransmission > 0.0 ? clamp(1.0 - vTransmission * 0.72, 0.16, 0.92) : 1.0);
    outColor = vec4(color, alpha);
}
