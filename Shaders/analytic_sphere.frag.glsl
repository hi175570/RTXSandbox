#version 330 core

in vec3 vProxyPosition;
layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec4 ForwardSurface;

uniform mat4 uView;
uniform mat4 uProjection;
uniform vec3 uCameraPosition;
uniform vec3 uSphereCenter;
uniform float uSphereRadius;
uniform vec3 uAlbedo;
uniform float uRoughness;
uniform float uMetallic;
uniform float uTimeOfDay;
uniform float uExposure;
uniform int uClassic;
uniform int uForwardHdr;
uniform int uHasSceneColor;
uniform int uBlackHole;
uniform sampler2D uSceneColor;

vec3 skyColor(vec3 direction)
{
    float daylight = clamp(sin((uTimeOfDay - 6.0) * 3.14159265 / 12.0) * 0.72 + 0.28, 0.035, 1.0);
    float horizon = pow(1.0 - abs(direction.y), 2.2);
    vec3 zenith = mix(vec3(0.004, 0.007, 0.018), vec3(0.08, 0.26, 0.62), daylight);
    vec3 rim = mix(vec3(0.012, 0.014, 0.025), vec3(0.48, 0.62, 0.82), daylight);
    return mix(zenith, rim, horizon);
}

vec4 bentAccretionDiskTransport(vec2 p)
{
    vec3 diskNormal = normalize(vec3(0.055, 0.985, 0.165));
    vec3 position = vec3(p, 7.0);
    vec3 direction = vec3(0.0, 0.0, -1.0);
    vec3 emission = vec3(0.0);
    float transmittance = 1.0;
    for (int stepIndex = 0; stepIndex < 144; ++stepIndex)
    {
        float gravityRadius = max(length(position), 0.48);
        float stepLength = mix(0.115, 0.038, smoothstep(3.4, 0.85, gravityRadius));
        vec3 acceleration = -position * (0.38 / (gravityRadius * gravityRadius * gravityRadius));
        direction = normalize(direction + acceleration * stepLength);
        vec3 nextPosition = position + direction * stepLength;
        vec3 samplePosition = mix(position, nextPosition, 0.5);
        vec3 radialVector = samplePosition - diskNormal * dot(samplePosition, diskNormal);
        float diskRadius = length(radialVector);
        if (diskRadius > 2.55 && diskRadius < 5.15)
        {
            vec3 radialDirection = radialVector / max(diskRadius, 0.0001);
            vec3 orbitalDirection = normalize(cross(diskNormal, radialDirection));
            float heat = 1.0 - smoothstep(2.55, 5.15, diskRadius);
            float radialEnvelope = smoothstep(2.55, 2.78, diskRadius) *
                (1.0 - smoothstep(4.70, 5.15, diskRadius));
            float scaleHeight = mix(0.075, 0.16, smoothstep(2.55, 5.15, diskRadius));
            float heightRatio = abs(dot(samplePosition, diskNormal)) / scaleHeight;
            vec3 diskAxis = normalize(cross(diskNormal, vec3(0.0, 0.0, 1.0)));
            vec3 diskBitangent = cross(diskNormal, diskAxis);
            float azimuth = atan(dot(radialDirection, diskBitangent), dot(radialDirection, diskAxis));
            float densityWave = 0.82 + 0.18 *
                (0.5 + 0.5 * sin(azimuth * 3.0 - diskRadius * 7.5));
            float density = radialEnvelope * exp(-0.5 * heightRatio * heightRatio) * densityWave;
            float opticalDepth = density * stepLength / 0.24;
            float emittedFraction = 1.0 - exp(-opticalDepth * 0.82);
            float towardViewer = dot(orbitalDirection, vec3(0.0, 0.0, 1.0));
            float orbitalSpeed = sqrt(0.46 / max(diskRadius, 2.55));
            float doppler = pow(clamp(
                1.0 / max(1.0 - orbitalSpeed * towardViewer, 0.30),
                0.48, 1.85), 2.35);
            float gravitationalShift = sqrt(clamp(
                1.0 - 1.0 / max(diskRadius, 1.001), 0.04, 1.0));
            vec3 gas = mix(vec3(0.035, 0.0005, 0.0001),
                vec3(0.78, 0.19, 0.018), pow(heat, 0.70));
            gas = mix(gas, vec3(1.22, 0.73, 0.24), pow(heat, 2.55));
            emission += transmittance * gas * gravitationalShift * doppler * emittedFraction;
            transmittance *= exp(-opticalDepth * 0.42);
        }
        position = nextPosition;
        if (position.z < -7.0 || length(position) > 12.0) break;
    }
    return vec4(emission, transmittance);
}

void main()
{
    vec3 rayDirection = normalize(vProxyPosition - uCameraPosition);
    vec3 offset = uCameraPosition - uSphereCenter;
    float projected = dot(offset, rayDirection);
    float discriminant = projected * projected -
        (dot(offset, offset) - uSphereRadius * uSphereRadius);

    if (uBlackHole != 0)
    {
        vec3 cameraToHole = uSphereCenter - uCameraPosition;
        float forwardDistance = dot(cameraToHole, rayDirection);
        if (forwardDistance <= 0.0) discard;
        float impact = length(cameraToHole - rayDirection * forwardDistance);
        float impactRatio = impact / max(uSphereRadius, 0.0001);
        if (impactRatio > 4.6) discard;

        vec4 centerClip = uProjection * uView * vec4(uSphereCenter, 1.0);
        if (centerClip.w <= 0.001) discard;
        vec2 centerUv = centerClip.xy / centerClip.w * 0.5 + 0.5;
        vec2 textureSizePixels = vec2(textureSize(uSceneColor, 0));
        vec2 screenUv = gl_FragCoord.xy / max(textureSizePixels, vec2(1.0));
        vec2 radialUv = screenUv - centerUv;

        vec2 metricUv = vec2(radialUv.x * textureSizePixels.x / max(textureSizePixels.y, 1.0),
                             radialUv.y);
        float falloff = 1.0 - smoothstep(1.12, 4.6, impactRatio);
        float sourceScale = 1.0 - 1.35 / max(impactRatio * impactRatio, 0.01);
        vec2 lensedMetric = metricUv * sourceScale;
        vec2 lensedRadialUv = vec2(lensedMetric.x * textureSizePixels.y /
                                   max(textureSizePixels.x, 1.0), lensedMetric.y);
        vec2 lensedUv = clamp(centerUv + mix(radialUv, lensedRadialUv, falloff),
                              vec2(0.001), vec2(0.999));
        vec3 lensed = uHasSceneColor != 0
            ? texture(uSceneColor, lensedUv).rgb
            : skyColor(rayDirection);

        vec2 p = length(metricUv) > 0.000001
            ? normalize(metricUv) * impactRatio
            : vec2(0.0);
        vec4 diskTransport = bentAccretionDiskTransport(p);
        bool eventHorizon = impactRatio <= 1.12;
        FragColor = vec4(eventHorizon
            ? diskTransport.rgb
            : lensed * diskTransport.a + diskTransport.rgb, 1.0);
        ForwardSurface = vec4(0.5, 0.5, 1.0, 1.0);

        vec3 depthPosition = uSphereCenter;
        if (eventHorizon && discriminant >= 0.0)
        {
            float root = sqrt(discriminant);
            float eventDistance = -projected - root;
            if (eventDistance > 0.0001)
                depthPosition = uCameraPosition + rayDirection * eventDistance;
        }
        vec4 depthClip = uProjection * uView * vec4(depthPosition, 1.0);
        gl_FragDepth = depthClip.z / depthClip.w * 0.5 + 0.5;
        return;
    }

    if (discriminant < 0.0) discard;
    float root = sqrt(discriminant);
    float distance = -projected - root;
    if (distance <= 0.0001) distance = -projected + root;
    if (distance <= 0.0001) discard;

    vec3 hitPosition = uCameraPosition + rayDirection * distance;
    vec3 normal = normalize(hitPosition - uSphereCenter);
    vec3 viewDirection = normalize(uCameraPosition - hitPosition);
    vec3 reflectedDirection = reflect(-viewDirection, normal);

    vec3 reflection = skyColor(reflectedDirection);
    if (uHasSceneColor != 0)
    {
        vec4 reflectedClip = uProjection * uView *
            vec4(hitPosition + reflectedDirection * (4.0 + uSphereRadius * 5.0), 1.0);
        if (reflectedClip.w > 0.001)
        {
            vec2 reflectedUv = reflectedClip.xy / reflectedClip.w * 0.5 + 0.5;
            if (all(greaterThanEqual(reflectedUv, vec2(0.002))) &&
                all(lessThanEqual(reflectedUv, vec2(0.998))))
                reflection = mix(reflection, texture(uSceneColor, reflectedUv).rgb, 0.72);
        }
    }

    float sunAngle = (uTimeOfDay - 6.0) * 6.2831853 / 24.0;
    vec3 lightDirection = normalize(vec3(cos(sunAngle), sin(sunAngle), 0.28));
    if (lightDirection.y < 0.0) lightDirection = -lightDirection;
    float nDotL = max(dot(normal, lightDirection), 0.0);
    float nDotV = max(dot(normal, viewDirection), 0.0);
    vec3 f0 = mix(vec3(0.04), clamp(uAlbedo, 0.0, 1.0), uMetallic);
    vec3 fresnel = f0 + (1.0 - f0) * pow(1.0 - nDotV, 5.0);
    float reflectionClarity = 1.0 - uRoughness * 0.72;
    vec3 diffuse = uAlbedo * (0.055 + nDotL * 0.78) * (1.0 - uMetallic);
    vec3 color = diffuse + reflection * fresnel * reflectionClarity;
    if (uClassic != 0) color = mix(uAlbedo * (0.18 + nDotL * 0.82), color, 0.38);
    if (uForwardHdr == 0)
        color = vec3(1.0) - exp(-max(color, 0.0) * max(uExposure, 0.01));
    FragColor = vec4(color, 1.0);
    ForwardSurface = vec4(normal * 0.5 + 0.5, clamp(uRoughness, 0.045, 1.0));

    vec4 clip = uProjection * uView * vec4(hitPosition, 1.0);
    float ndcDepth = clip.z / clip.w;
    gl_FragDepth = ndcDepth * 0.5 + 0.5;
}
