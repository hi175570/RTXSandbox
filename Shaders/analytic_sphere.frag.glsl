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
uniform sampler2D uSceneColor;

vec3 skyColor(vec3 direction)
{
    float daylight = clamp(sin((uTimeOfDay - 6.0) * 3.14159265 / 12.0) * 0.72 + 0.28, 0.035, 1.0);
    float horizon = pow(1.0 - abs(direction.y), 2.2);
    vec3 zenith = mix(vec3(0.004, 0.007, 0.018), vec3(0.08, 0.26, 0.62), daylight);
    vec3 rim = mix(vec3(0.012, 0.014, 0.025), vec3(0.48, 0.62, 0.82), daylight);
    return mix(zenith, rim, horizon);
}

void main()
{
    vec3 rayDirection = normalize(vProxyPosition - uCameraPosition);
    vec3 offset = uCameraPosition - uSphereCenter;
    float projected = dot(offset, rayDirection);
    float discriminant = projected * projected -
        (dot(offset, offset) - uSphereRadius * uSphereRadius);
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
