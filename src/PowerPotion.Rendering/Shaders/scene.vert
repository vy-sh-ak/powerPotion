#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec2 inBacksideUv;

layout(set = 0, binding = 0) uniform SceneUbo {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDirection;
    vec4 sunColor;
    vec4 ambient;
} ubo;

layout(push_constant) uniform Push {
    mat4 model;
    int frontMaterial;
    int backMaterial;
    int flags;
    int pad;
} pc;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) out vec2 vBacksideUv;

void main()
{
    vec4 world = pc.model * vec4(inPosition, 1.0);
    vWorldPos = world.xyz;
    vNormal = mat3(pc.model) * inNormal;
    vUv = inUv;
    vBacksideUv = inBacksideUv;
    gl_Position = ubo.viewProj * world;
}
