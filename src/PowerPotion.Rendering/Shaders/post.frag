#version 450

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform PostUbo {
    vec4 exposure; // x exposure, y bloom, z saturation, w contrast
    vec4 grading;  // x temperature, y vignette, z chromatic aberration, w motion blur
    vec4 params;   // x safe frame, y lens flare, z time, w auto contrast
    vec4 sun;      // xy sun screen uv, z strength, w taa weight
} ubo;

layout(set = 0, binding = 1) uniform sampler2D sceneColor;
layout(set = 0, binding = 2) uniform sampler2D historyColor;

vec3 sampleScene(vec2 uv)
{
    return texture(sceneColor, clamp(uv, vec2(0.001), vec2(0.999))).rgb;
}

// ACES filmic tonemap (Narkowicz fit).
vec3 acesFilm(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    vec2 uv = vUv;
    vec2 dir = uv - 0.5;

    // Chromatic aberration: split the channels radially.
    float ca = ubo.grading.z * 0.006;
    vec3 color;
    color.r = sampleScene(uv + dir * ca).r;
    color.g = sampleScene(uv).g;
    color.b = sampleScene(uv - dir * ca).b;

    // Cheap radial motion blur.
    if (ubo.grading.w > 0.001)
    {
        vec3 blur = vec3(0.0);
        for (int i = 1; i <= 6; i++)
            blur += sampleScene(uv - dir * (float(i) / 6.0) * 0.03 * ubo.grading.w);
        color = mix(color, blur / 6.0, 0.5 * ubo.grading.w);
    }

    // Cheap bloom: eight taps around the pixel.
    if (ubo.exposure.y > 0.001)
    {
        vec3 bloom = vec3(0.0);
        float radius = 0.004 + 0.010 * ubo.exposure.y;
        for (int i = 0; i < 8; i++)
        {
            float a = float(i) * 0.785398;
            bloom += sampleScene(uv + vec2(cos(a), sin(a)) * radius);
        }
        bloom /= 8.0;
        color += bloom * ubo.exposure.y;
    }

    // Content-adaptive contrast: sample a 3x3 grid of HDR luminance and stretch.
    if (ubo.params.w > 0.5)
    {
        float minL = 1e9;
        float maxL = -1e9;
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                vec2 p = vec2((float(x) + 0.5) / 3.0, (float(y) + 0.5) / 3.0);
                float l = dot(sampleScene(p), vec3(0.2126, 0.7152, 0.0722));
                minL = min(minL, l);
                maxL = max(maxL, l);
            }
        }
        float range = max(maxL - minL, 1e-3);
        color = clamp((color - minL) / range, 0.0, 1.0);
    }

    color *= ubo.exposure.x;
    color = acesFilm(color);
    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));

    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    color = mix(vec3(luma), color, ubo.exposure.z);
    color = (color - 0.5) * (1.0 + ubo.exposure.w) + 0.5;

    float temp = clamp(ubo.grading.x, 0.0, 1.0);
    color *= mix(vec3(0.92, 1.0, 1.10), vec3(1.10, 1.0, 0.88), temp);

    // Simple lens flare around the sun's screen position.
    if (ubo.params.y > 0.001 && ubo.sun.z > 0.001)
    {
        float d = length(ubo.sun.xy - uv);
        float flare = exp(-d * 9.0) * ubo.params.y * ubo.sun.z;
        color += vec3(1.0, 0.85, 0.6) * flare;
    }

    float vig = 1.0 - smoothstep(0.25, 0.9, length(dir));
    color *= mix(1.0, vig, clamp(ubo.grading.y, 0.0, 1.0));

    // Minimal TAA: blend with the previous post output, clamped to the current colour.
    if (ubo.sun.w > 0.001)
    {
        vec3 history = texture(historyColor, uv).rgb;
        vec3 lo = max(color - 0.08, vec3(0.0));
        vec3 hi = min(color + 0.08, vec3(1.0));
        history = clamp(history, lo, hi);
        color = mix(color, history, clamp(ubo.sun.w, 0.0, 0.95));
    }

    // Capture safe frame (a 90% border), using the actual render size.
    if (ubo.params.x > 0.5)
    {
        vec2 px = abs(uv - 0.5);
        float border = step(0.475, max(px.x, px.y));
        color = mix(color, vec3(0.95), border * 0.85);
    }

    outColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
