#version 450

layout(location = 0) in vec3 inPosition;

layout(push_constant) uniform Push {
    mat4 model;
    mat4 lightViewProj;
} pc;

void main()
{
    gl_Position = pc.lightViewProj * pc.model * vec4(inPosition, 1.0);
}
