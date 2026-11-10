#version 430 core

in vec2 vUv;
layout(location = 0) out vec4 outColor;

uniform sampler2D uImage;
uniform vec2 uViewport;
uniform float uExposure;
uniform int uBlackHoleCount;
uniform vec4 uBlackHoles[4];

vec3 aces(vec3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((color * (a * color + b)) / (color * (c * color + d) + e), 0.0, 1.0);
}

vec4 bentAccretionDiskTransport(vec2 p)
{
    // Trace a camera ray through a compact inverse-square field and intersect a
    // genuinely three-dimensional, tilted disk. The secondary image is therefore
    // produced by a ray that went around the hole, rather than by drawing an oval.
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
    // Every displayed pixel comes from its own primary DXR ray.  Do not spatially
    // reconstruct missing checkerboard samples: that saved rays by guessing half of
    // the image and visibly softened the entire scene.
    vec2 sampleUv = vUv;
    vec3 lensEmission = vec3(0.0);
    float lensTransmittance = 1.0;
    bool insideEventHorizon = false;
    for (int index = 0; index < uBlackHoleCount; ++index)
    {
        vec4 hole = uBlackHoles[index];
        vec2 delta = sampleUv - hole.xy;
        // Screen UVs are not square on a widescreen viewport. Measure the
        // gravitational field in aspect-corrected screen space so a spherical
        // event horizon remains circular instead of stretching horizontally.
        vec2 metricDelta = vec2(delta.x * (uViewport.x / max(uViewport.y, 1.0)), delta.y);
        float ratio = length(metricDelta) / max(hole.z, 0.00001);
        if (ratio > 4.6) continue;
        if (ratio <= 1.12)
        {
            insideEventHorizon = true;
            continue;
        }

        float falloff = 1.0 - smoothstep(1.12, 4.6, ratio);
        // Point-lens mapping beta = theta - theta_E^2/theta.  The sign change near
        // the shadow produces the inverted secondary image and a real-looking
        // Einstein-ring distortion instead of a generic magnifying-glass bulge.
        float sourceScale = 1.0 - 1.35 / max(ratio * ratio, 0.01);
        vec2 lensedMetric = metricDelta * sourceScale;
        vec2 lensedDelta = vec2(lensedMetric.x / (uViewport.x / max(uViewport.y, 1.0)),
                                lensedMetric.y);
        vec2 warpedUv = clamp(hole.xy + lensedDelta, vec2(0.001), vec2(0.999));
        sampleUv = mix(sampleUv, warpedUv, falloff * hole.w);
        vec2 p = metricDelta / max(hole.z, 0.00001);
        vec4 diskTransport = bentAccretionDiskTransport(p);
        lensEmission += lensTransmittance * diskTransport.rgb;
        lensTransmittance *= diskTransport.a;
    }
    vec4 source = texture(uImage, sampleUv);
    float presentationAlpha = insideEventHorizon ? 1.0 : source.a;
    vec3 hdr = insideEventHorizon
        ? vec3(0.0)
        : source.rgb * uExposure * lensTransmittance + lensEmission;
    vec3 mapped = pow(aces(hdr), vec3(1.0 / 2.2));

    // Resolution-independent, high-contrast sandbox crosshair.
    vec2 fromCenter = abs(gl_FragCoord.xy - uViewport * 0.5);
    bool horizontal = fromCenter.y < 1.25 && fromCenter.x < 8.0 && fromCenter.x > 2.5;
    bool vertical = fromCenter.x < 1.25 && fromCenter.y < 8.0 && fromCenter.y > 2.5;
    if (horizontal || vertical)
    {
        float luminance = dot(mapped, vec3(0.2126, 0.7152, 0.0722));
        mapped = vec3(luminance > 0.5 ? 0.05 : 0.95);
        presentationAlpha = 1.0;
    }

    outColor = vec4(mapped, presentationAlpha);
}
