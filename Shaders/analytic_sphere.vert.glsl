#version 330 core

uniform mat4 uView;
uniform mat4 uProjection;
uniform vec3 uSphereCenter;
uniform float uSphereRadius;
uniform vec3 uCameraPosition;
uniform vec3 uCameraRight;
uniform vec3 uCameraUp;
uniform int uBlackHole;

out vec3 vProxyPosition;

void main()
{
    const vec2 corners[6] = vec2[6](
        vec2(-1.0, -1.0), vec2( 1.0, -1.0), vec2( 1.0,  1.0),
        vec2(-1.0, -1.0), vec2( 1.0,  1.0), vec2(-1.0,  1.0));
    vec2 corner = corners[gl_VertexID];
    float distanceToCenter = length(uSphereCenter - uCameraPosition);
    float tangentDenominator = sqrt(max(
        distanceToCenter * distanceToCenter - uSphereRadius * uSphereRadius,
        uSphereRadius * uSphereRadius * 0.04));
    float proxyRadius = min(
        uSphereRadius * distanceToCenter / tangentDenominator,
        uSphereRadius * 5.0);
    if (uBlackHole != 0)
        proxyRadius *= 4.75;
    vProxyPosition = uSphereCenter +
        (uCameraRight * corner.x + uCameraUp * corner.y) * proxyRadius;
    gl_Position = uProjection * uView * vec4(vProxyPosition, 1.0);
}
