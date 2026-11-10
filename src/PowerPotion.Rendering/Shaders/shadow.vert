#version 450

layout(location = 0) in vec3 inPosition;

layout(set = 0, binding = 0) uniform SceneUbo {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDirection;
    vec4 sunColor;
    vec4 ambient;
    vec4 options;
    mat4 lightViewProj[4];
    vec4 cascadeSplits;
    vec4 shadowParams;
    vec4 cameraForward;
    vec4 sectionPlanes[8];
    vec4 sectionParams;
    vec4 fogParams;
} ubo;

layout(push_constant) uniform Push {
    mat4 model;
    mat4 lightViewProj;
} pc;

out float gl_ClipDistance[8];

void main()
{
    vec4 world = pc.model * vec4(inPosition, 1.0);

    // P4: clip shadow casters against the same section planes as the visible pass.
    int count = int(ubo.sectionParams.x);
    for (int i = 0; i < 8; i++)
    {
        gl_ClipDistance[i] = i < count
            ? dot(ubo.sectionPlanes[i].xyz, world.xyz) + ubo.sectionPlanes[i].w
            : 1.0;
    }

    gl_Position = pc.lightViewProj * world;
}
