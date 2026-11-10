#version 450

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform PostUbo {
    vec4 exposure; // x exposure, y bloom, z saturation, w contrast
    vec4 grading;  // x temperature, y vignette, z chromatic aberration, w motion blur
    vec4 params;   // x safe frame, y lens flare, z time, w auto contrast
    vec4 sun;      // xy sun screen uv, z strength, w taa weight
    vec4 sao;      // x strength, y radius, z near, w far
    vec4 gi;       // x strength, y radius, z falloff, w pad
    mat4 proj;
    mat4 invProj;
} ubo;

layout(set = 0, binding = 1) uniform sampler2D sceneColor;
layout(set = 0, binding = 2) uniform sampler2D historyColor;
layout(set = 0, binding = 3) uniform sampler2D depthTexture;

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

vec3 viewPosition(vec2 uv)
{
    float d = texture(depthTexture, uv).r;
    vec4 clip = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
    vec4 ray = ubo.invProj * clip;
    vec3 dir = ray.xyz / ray.w;
    return dir * (d / max(-dir.z, 1e-4));
}

float ssao(vec2 uv)
{
    float depth = texture(depthTexture, uv).r;
    if (depth >= 1.0e5) return 1.0;

    vec3 P = viewPosition(uv);
    vec3 N = normalize(cross(dFdx(P), dFdy(P)));
    if (N.z < 0.0) N = -N;
    vec3 T = normalize(cross(N, vec3(0.0, 1.0, 0.0)) + vec3(1e-4));
    vec3 B = cross(N, T);

    float radius = max(ubo.sao.y, 0.05);
    float centerZ = -P.z;
    float bias = max(0.02, centerZ * 0.002);
    float occlusion = 0.0;

    const vec3 kernel[8] = vec3[](
        vec3(0.10, 0.00, 0.20), vec3(-0.10, 0.00, 0.20),
        vec3(0.00, 0.10, 0.20), vec3(0.00, -0.10, 0.20),
        vec3(0.07, 0.07, 0.35), vec3(-0.07, 0.07, 0.35),
        vec3(0.07, -0.07, 0.35), vec3(-0.07, -0.07, 0.35));

    for (int i = 0; i < 8; i++)
    {
        vec3 samplePos = P + (T * kernel[i].x + B * kernel[i].y + N * kernel[i].z) * radius;
        vec4 clip = ubo.proj * vec4(samplePos, 1.0);
        if (clip.w <= 0.0) continue;
        vec2 sampleUv = clip.xy / clip.w * 0.5 + 0.5;
        if (sampleUv.x < 0.0 || sampleUv.x > 1.0 || sampleUv.y < 0.0 || sampleUv.y > 1.0) continue;
        float sceneDepth = texture(depthTexture, sampleUv).r;
        float sampleDepth = -samplePos.z;
        if (sceneDepth < sampleDepth - bias && abs(sceneDepth - depth) < radius) occlusion += 1.0;
    }
    return clamp(1.0 - ubo.sao.x * (occlusion / 8.0), 0.0, 1.0);
}

// Screen-space diffuse GI: depth-weighted 8-tap gather (the weighted average acts as a denoise).
vec3 ssgi(vec2 uv, float centerDepth)
{
    vec3 sum = vec3(0.0);
    float weightSum = 0.0;
    const vec2 kernel[8] = vec2[](
        vec2(0.60, 0.00), vec2(-0.60, 0.00), vec2(0.00, 0.60), vec2(0.00, -0.60),
        vec2(0.42, 0.42), vec2(-0.42, 0.42), vec2(0.42, -0.42), vec2(-0.42, -0.42));
    for (int i = 0; i < 8; i++)
    {
        vec2 suv = clamp(uv + kernel[i] * ubo.gi.y, vec2(0.001), vec2(0.999));
        float d = texture(depthTexture, suv).r;
        if (d >= 1.0e5) continue;
        float w = exp(-abs(d - centerDepth) * ubo.gi.z);
        sum += max(sampleScene(suv) - vec3(0.08), vec3(0.0)) * w;
        weightSum += w;
    }
    return weightSum > 1e-4 ? sum / weightSum : vec3(0.0);
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

    // RCAS-style sharpening when the scene is upscaled from a render scale.
    if (ubo.gi.w > 0.001)
    {
        vec2 texel = 1.0 / vec2(textureSize(sceneColor, 0));
        vec3 n = sampleScene(uv + vec2(0.0, texel.y));
        vec3 s = sampleScene(uv - vec2(0.0, texel.y));
        vec3 e = sampleScene(uv + vec2(texel.x, 0.0));
        vec3 w = sampleScene(uv - vec2(texel.x, 0.0));
        vec3 sharpened = color + (color * 4.0 - n - s - e - w) * ubo.gi.w * 0.12;
        color = mix(color, sharpened, 0.85);
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

    if (ubo.sao.x > 0.001)
        color *= ssao(uv);

    if (ubo.gi.x > 0.001)
    {
        float centerDepth = texture(depthTexture, uv).r;
        if (centerDepth < 1.0e5)
            color += ssgi(uv, centerDepth) * ubo.gi.x * 0.35;
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
