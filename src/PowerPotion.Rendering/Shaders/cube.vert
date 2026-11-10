#version 450

// P0 spike shader: a single transformed, per-face colored cube.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;

layout(set = 0, binding = 0) uniform UniformBufferObject {
    mat4 mvp;
} ubo;

layout(location = 0) out vec3 fragColor;

void main()
{
    gl_Position = ubo.mvp * vec4(inPosition, 1.0);
    fragColor = inColor;
}
