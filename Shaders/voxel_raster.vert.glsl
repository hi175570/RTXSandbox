#version 430 core

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec4 aMaterial;
layout(location = 4) in vec4 aOptics;
layout(location = 5) in vec4 aCellAndGrid;

uniform mat4 uView;
uniform mat4 uProjection;

out vec3 vWorldPosition;
out vec3 vNormal;
out vec2 vUv;
flat out int vMaterial;
flat out int vFace;
flat out float vRoughness;
flat out float vMetallic;
flat out float vTransmission;
flat out vec3 vEmission;
flat out ivec3 vCell;
flat out int vGrid;

void main()
{
    vWorldPosition = aPosition;
    vNormal = aNormal;
    vUv = aUv;
    vMaterial = int(aMaterial.x + 0.5);
    vFace = int(aMaterial.y + 0.5);
    vRoughness = aMaterial.z;
    vMetallic = aMaterial.w;
    vTransmission = aOptics.x;
    vEmission = aOptics.yzw;
    vCell = ivec3(aCellAndGrid.xyz + vec3(0.5));
    vGrid = int(aCellAndGrid.w + 0.5);
    gl_Position = uProjection * uView * vec4(aPosition, 1.0);
}
