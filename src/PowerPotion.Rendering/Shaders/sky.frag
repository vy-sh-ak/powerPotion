#version 450

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outDepth;

layout(set = 0, binding = 0) uniform SkyUbo {
    mat4 invViewProj;
    vec4 cameraPos;
    vec4 sunDirection;
    vec4 sunColor;
    vec4 moonDirection;
    vec4 skyParams;    // x starBrightness, y sunPower, z whiteMode, w useSkybox
    vec4 skyboxParams; // x rotation, y brightness, z useSunSpot, w moonSize
    vec4 cloudParams;  // x density, y cirrus, z variety, w time
    vec4 windParams;   // x offsetX, y offsetZ, z strength, w direction
} ubo;

layout(set = 0, binding = 1) uniform sampler2D skybox;

const float PI = 3.14159265359;

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float noise2(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x),
               mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 3; i++)
    {
        v += a * noise2(p);
        p = p * 2.03 + 17.0;
        a *= 0.5;
    }
    return v;
}

void main()
{
    outDepth = vec4(1e6, 0.0, 0.0, 1.0);
    vec2 ndc = vUv * 2.0 - 1.0;
    vec4 nearPoint = ubo.invViewProj * vec4(ndc, 0.0, 1.0);
    vec4 farPoint = ubo.invViewProj * vec4(ndc, 1.0, 1.0);
    vec3 dir = normalize(farPoint.xyz / farPoint.w - nearPoint.xyz / nearPoint.w);

    if (ubo.skyParams.z > 0.5)
    {
        outColor = vec4(vec3(0.88), 1.0);
        return;
    }

    vec3 sunDir = normalize(ubo.sunDirection.xyz);
    vec3 sunColor = max(ubo.sunColor.rgb, vec3(0.0));
    float sunDot = max(dot(dir, sunDir), 0.0);

    if (ubo.skyParams.w > 0.5)
    {
        float lon = atan(dir.z, dir.x) / (2.0 * PI) + 0.5 + ubo.skyboxParams.x;
        float lat = acos(clamp(dir.y, -1.0, 1.0)) / PI;
        vec3 color = texture(skybox, vec2(fract(lon), lat)).rgb * ubo.skyboxParams.y;

        if (ubo.skyboxParams.z > 0.5)
        {
            float disk = smoothstep(0.9994, 0.9998, sunDot);
            color += disk * sunColor * 4.0;
        }

        outColor = vec4(color, 1.0);
        return;
    }

    float day = smoothstep(-0.08, 0.14, sunDir.y);
    float up = clamp(dir.y, -0.2, 1.0);

    vec3 zenith = mix(vec3(0.012, 0.018, 0.045), vec3(0.05, 0.18, 0.50), day);
    vec3 horizon = mix(vec3(0.025, 0.03, 0.055), vec3(0.50, 0.65, 0.85), day);
    vec3 sky = mix(horizon, zenith, pow(clamp(up, 0.0, 1.0), 0.35));

    // Sun glow and disk.
    float sunPower = max(ubo.skyParams.y, 0.0);
    sky += sunColor * pow(sunDot, 32.0) * 0.08 * sunPower;
    sky += sunColor * smoothstep(0.9994, 0.9998, sunDot) * 8.0 * sunPower;

    // Moon disk (dim, cool) and procedural stars.
    float moonDot = max(dot(dir, normalize(ubo.moonDirection.xyz)), 0.0);
    sky += smoothstep(0.9992, 0.9997, moonDot) * ubo.skyboxParams.w *
           vec3(0.55, 0.62, 0.85) * (1.0 - day) * 0.6;

    float night = 1.0 - day;
    if (ubo.skyParams.x > 0.0 && up > 0.0)
    {
        vec3 cell = floor(dir * 900.0);
        float star = step(0.9985, hash13(cell));
        float twinkle = 0.7 + 0.3 * hash13(cell + 17.0);
        sky += vec3(star * twinkle) * ubo.skyParams.x * night;
    }

    // Simple height-based horizon haze.
    float haze = pow(1.0 - clamp(up, 0.0, 1.0), 8.0);
    sky = mix(sky, horizon, haze * 0.25);

    // Procedural raymarched cloud layer (P6), advected by wind.
    if (ubo.cloudParams.x > 0.001 && dir.y > 0.02)
    {
        vec2 wind = vec2(cos(ubo.windParams.w), sin(ubo.windParams.w)) *
                    (ubo.windParams.z * ubo.cloudParams.w * 12.0);
        vec2 offset = vec2(ubo.windParams.x, ubo.windParams.y) + wind;
        float transmittance = 1.0;
        vec3 scattered = vec3(0.0);
        for (int i = 0; i < 5; i++)
        {
            float h = 500.0 + float(i) * 180.0;
            float t = (h - ubo.cameraPos.y) / max(dir.y, 0.05);
            if (t <= 0.0) continue;
            vec3 p = ubo.cameraPos.xyz + dir * t;
            vec2 q = p.xz * 0.0015 + offset * 0.0015;
            float n = fbm(q);
            float density = smoothstep(0.55 - ubo.cloudParams.x * 0.30, 0.78, n)
                          * clamp(ubo.cloudParams.x, 0.0, 1.0)
                          * exp(-max(h - 500.0, 0.0) / 700.0);
            density += smoothstep(0.58, 0.78, fbm(q * 3.1)) * ubo.cloudParams.y * 0.35;
            float alpha = clamp(density * 0.45, 0.0, 0.9);
            vec3 cloudColor = mix(vec3(0.55, 0.60, 0.68), sunColor * 0.8, 0.45);
            scattered += cloudColor * alpha * transmittance;
            transmittance *= (1.0 - alpha);
        }
        sky = sky * transmittance + scattered;
    }

    outColor = vec4(sky, 1.0);
}
