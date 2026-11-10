#version 450

layout(location = 0) in vec3 fragColor;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outDepth;

void main()
{
    outColor = vec4(fragColor, 1.0);
    outDepth = vec4(1e6, 0.0, 0.0, 1.0);
}
