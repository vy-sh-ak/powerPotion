#version 450

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 3) in vec2 vBacksideUv;

layout(location = 0) out vec4 outColor;

struct Material {
    vec4 diffuse;   // rgb, opacity
    vec4 tint;      // rgb, imageFade
    vec4 emissive;  // rgb, strength
    vec4 params;    // roughness, metallic, specular, bumpAmount
    vec4 params2;   // refraction, materialType, flags, pad
    ivec4 tex;      // diffuse, bump, roughness, mask
    vec4 uv0r0; vec4 uv0r1; vec4 uv0r2;
    vec4 uv1r0; vec4 uv1r1; vec4 uv1r2;
    vec4 uv2r0; vec4 uv2r1; vec4 uv2r2;
    vec4 uv3r0; vec4 uv3r1; vec4 uv3r2;
};

layout(set = 0, binding = 0) uniform SceneUbo {
    mat4 viewProj;
    vec4 cameraPos;
    vec4 sunDirection;
    vec4 sunColor;
    vec4 ambient;
    vec4 options;       // x white mode, y light count, z light brightness, w fog
    mat4 lightViewProj[4];
    vec4 cascadeSplits;
    vec4 shadowParams;  // x sharpness, y bias, z map size, w enabled
    vec4 cameraForward;
    vec4 sectionPlanes[8]; // xyzd; dot(n,p)+d >= 0 is visible
    vec4 sectionParams;    // x plane count, y time seconds
} ubo;

layout(set = 0, binding = 1) readonly buffer MaterialBuffer {
    Material materials[];
};

struct Light {
    vec4 position;  // xyz, w = distribution
    vec4 color;     // rgb, w = intensity
    vec4 direction; // xyz forward, w = shape
    vec4 params;    // x beam angle, y shape1, z shape2, w IES max angle
    ivec4 flags;    // x IES texture index, y enabled
};

layout(set = 0, binding = 2) readonly buffer LightBuffer {
    Light lights[];
};

layout(set = 0, binding = 3) uniform sampler2DArrayShadow sunShadow;

layout(set = 1, binding = 0) uniform sampler2D textures[1024];

layout(push_constant) uniform Push {
    mat4 model;
    int frontMaterial;
    int backMaterial;
    int flags;
    int pad;
} pc;

const int FLAG_DOUBLE_SIDED = 1 << 0;
const int FLAG_SOLID_GLASS  = 1 << 1;
const int FLAG_WHITE_MODE   = 1 << 2;
const int FLAG_CUTOUT       = 1 << 3;
const int FLAG_BUMP_NORMAL  = 1 << 4;
const int FLAG_HAS_DIFFUSE  = 1 << 5;
const int FLAG_HAS_BUMP     = 1 << 6;
const int FLAG_HAS_ROUGH    = 1 << 7;
const int FLAG_HAS_MASK     = 1 << 8;
const int WCS_SHIFT         = 16;
const float PI = 3.14159265359;

vec2 applyTransform(vec4 r0, vec4 r1, vec4 r2, vec2 uv)
{
    if (r0.w > 0.5) uv.y = 1.0 - uv.y;
    return vec2(dot(r0.xy, uv) + r0.z, dot(r1.xy, uv) + r1.z);
}

vec2 wcsUv(vec2 uv, vec3 worldPos, vec3 n, int materialFlags)
{
    int wcs = (materialFlags >> WCS_SHIFT) & 0x3;
    if (wcs == 1) return worldPos.xz; // planar
    if (wcs == 2)
    {
        vec3 an = abs(n);
        if (an.x > an.y && an.x > an.z) return worldPos.zy;
        if (an.y > an.z) return worldPos.xz;
        return worldPos.xy;
    }
    return uv;
}

float distributionGGX(vec3 n, vec3 h, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float ndh = max(dot(n, h), 0.0);
    float denom = ndh * ndh * (a2 - 1.0) + 1.0;
    return a2 / max(PI * denom * denom, 1e-5);
}

float geometrySmith(float ndv, float ndl, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    float gv = ndv / (ndv * (1.0 - k) + k);
    float gl = ndl / (ndl * (1.0 - k) + k);
    return gv * gl;
}

vec3 fresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 + (1.0 - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

void main()
{
    // P4 section planes: discard fragments outside the active clip volume.
    int sectionCount = int(ubo.sectionParams.x);
    for (int i = 0; i < sectionCount; i++)
    {
        if (dot(ubo.sectionPlanes[i].xyz, vWorldPos) + ubo.sectionPlanes[i].w < 0.0)
            discard;
    }

    int mi = gl_FrontFacing ? pc.frontMaterial : pc.backMaterial;
    if (mi < 0) mi = pc.frontMaterial;
    Material m = materials[mi];
    int materialFlags = int(m.params2.z);
    int materialType = int(m.params2.y + 0.5);

    vec2 uv = gl_FrontFacing ? vUv : vBacksideUv;
    uv = wcsUv(uv, vWorldPos, normalize(vNormal), materialFlags);

    vec3 albedo = m.diffuse.rgb;
    float alpha = m.diffuse.a;

    if ((materialFlags & FLAG_HAS_DIFFUSE) != 0)
    {
        vec2 duv = applyTransform(m.uv0r0, m.uv0r1, m.uv0r2, uv);
        vec4 tex = texture(textures[m.tex.x], duv);
        tex.rgb *= m.uv0r1.w; // brightness
        albedo *= mix(vec3(1.0), tex.rgb, m.tint.w);
        alpha *= tex.a;
    }
    albedo *= m.tint.rgb;
    if (materialType == 1)
    {
        // Grass hook: subtle blade-scale variation so large lawn surfaces are not flat.
        float blades = 0.86 + 0.14 * sin(vWorldPos.x * 9.0) * sin(vWorldPos.z * 9.0 + vWorldPos.y * 3.0);
        albedo *= blades;
    }
    if (ubo.options.x > 0.5) albedo = vec3(0.92);

    if (alpha < 0.02) discard;
    if ((materialFlags & FLAG_CUTOUT) != 0 && alpha < 0.5) discard;

    float roughness = clamp(m.params.x, 0.04, 1.0);
    float metallic = clamp(m.params.y, 0.0, 1.0);
    vec3 f0 = mix(vec3(clamp(m.params.z, 0.01, 0.16)), albedo, metallic);

    if ((materialFlags & FLAG_HAS_ROUGH) != 0)
    {
        vec2 ruv = applyTransform(m.uv2r0, m.uv2r1, m.uv2r2, uv);
        roughness = clamp(roughness * texture(textures[m.tex.z], ruv).r, 0.04, 1.0);
    }

    vec3 n = normalize(vNormal);
    if (!gl_FrontFacing) n = -n;

    if ((materialFlags & FLAG_HAS_BUMP) != 0)
    {
        vec2 buv = applyTransform(m.uv1r0, m.uv1r1, m.uv1r2, uv);
        vec3 sampled = texture(textures[m.tex.y], buv).rgb;
        vec3 tangentNormal;
        if ((materialFlags & FLAG_BUMP_NORMAL) != 0)
        {
            tangentNormal = sampled * 2.0 - 1.0;
        }
        else
        {
            float h = sampled.r;
            float hx = dFdx(h);
            float hy = dFdy(h);
            tangentNormal = normalize(vec3(-hx * max(m.params.w, 0.01) * 20.0,
                                          -hy * max(m.params.w, 0.01) * 20.0,
                                          1.0));
        }

        vec3 dpdx = dFdx(vWorldPos);
        vec3 dpdy = dFdy(vWorldPos);
        vec2 duvdx = dFdx(uv);
        vec2 duvdy = dFdy(uv);
        vec3 t = dpdx * duvdy.y - dpdy * duvdx.y;
        if (length(t) < 1e-5) t = cross(n, vec3(0.0, 1.0, 0.0));
        vec3 tangent = normalize(t);
        vec3 bitangent = normalize(cross(n, tangent));
        n = normalize(tangent * tangentNormal.x + bitangent * tangentNormal.y + n * tangentNormal.z);
    }

    if (materialType == 2)
    {
        // Water hook: small animated ripple normal (time comes from the scene UBO).
        float t = ubo.sectionParams.y;
        float wave = sin(vWorldPos.x * 2.5 + t * 1.8) * cos(vWorldPos.z * 2.1 + t * 1.3);
        vec3 tangent = cross(n, vec3(0.0, 1.0, 0.0));
        if (dot(tangent, tangent) < 1e-6) tangent = vec3(1.0, 0.0, 0.0);
        tangent = normalize(tangent);
        vec3 bitangent = normalize(cross(n, tangent));
        n = normalize(n + tangent * (wave * 0.12) + bitangent * (wave * 0.07));
    }

    vec3 v = normalize(ubo.cameraPos.xyz - vWorldPos);
    vec3 l = normalize(ubo.sunDirection.xyz);
    vec3 h = normalize(v + l);

    float ndl = max(dot(n, l), 0.0);
    float ndv = max(dot(n, v), 1e-4);
    float ndh = max(dot(n, h), 0.0);
    float vdh = max(dot(v, h), 0.0);

    float d = distributionGGX(n, h, roughness);
    float g = geometrySmith(ndv, ndl, roughness);
    vec3 f = fresnelSchlick(vdh, f0);
    vec3 specular = (d * g * f) / (4.0 * ndv * ndl + 1e-4);
    vec3 kd = (vec3(1.0) - f) * (1.0 - metallic);
    vec3 diffuse = kd * albedo / PI;

    float shadow = 1.0;
    if (ubo.shadowParams.w > 0.5)
    {
        float viewDepth = dot(vWorldPos - ubo.cameraPos.xyz, ubo.cameraForward.xyz);
        int cascade = 0;
        if (viewDepth > ubo.cascadeSplits.x) cascade = 1;
        if (viewDepth > ubo.cascadeSplits.y) cascade = 2;
        if (viewDepth > ubo.cascadeSplits.z) cascade = 3;
        if (viewDepth <= ubo.cascadeSplits.w)
        {
            vec4 lp = ubo.lightViewProj[cascade] * vec4(vWorldPos, 1.0);
            vec3 proj = lp.xyz / lp.w;
            vec2 uv = proj.xy * 0.5 + 0.5;
            if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0)
            {
                float bias = ubo.shadowParams.y;
                float texel = 1.0 / max(ubo.shadowParams.z, 1.0);
                float radius = mix(1.6, 1.0, clamp(ubo.shadowParams.x, 0.0, 1.0));
                float sum = 0.0;
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        vec2 off = vec2(float(x), float(y)) * texel * radius;
                        sum += texture(sunShadow, vec4(uv + off, float(cascade), proj.z - bias));
                    }
                }
                shadow = sum / 9.0;
            }
        }
    }

    vec3 color = (diffuse + specular) * ubo.sunColor.rgb * ndl * shadow;

    // Sky-driven hemisphere ambient (the full split-sum IBL probe is a later refinement).
    float day = smoothstep(-0.08, 0.14, normalize(ubo.sunDirection.xyz).y);
    vec3 skyAmbient = mix(vec3(0.04, 0.05, 0.08), vec3(0.32, 0.45, 0.66), day);
    skyAmbient = mix(skyAmbient * 0.45, skyAmbient, clamp(n.y * 0.5 + 0.5, 0.0, 1.0));
    color += albedo * skyAmbient * ubo.ambient.rgb;

    int lightCount = int(ubo.options.y);
    float lightScale = ubo.options.z;
    for (int i = 0; i < lightCount; i++)
    {
        Light light = lights[i];
        if (light.flags.y == 0) continue;

        vec3 toLight = light.position.xyz - vWorldPos;
        float dist2 = dot(toLight, toLight);
        float dist = sqrt(dist2);
        vec3 L = toLight / max(dist, 1e-4);
        float lightNdl = max(dot(n, L), 0.0);
        if (lightNdl <= 0.0) continue;

        float radius = max(light.params.y, light.params.z) * 0.5;
        float attenuation = light.color.w / (dist2 + radius * radius + 0.01);

        float spot = 1.0;
        float ies = 1.0;
        int distribution = int(light.position.w);
        vec3 lightForward = normalize(light.direction.xyz);
        if (distribution == 2)
        {
            spot = max(dot(-L, lightForward), 0.0);
        }
        else if (distribution == 3)
        {
            float cosAngle = dot(-L, lightForward);
            float outer = cos(light.params.x * 0.5);
            float inner = cos(light.params.x * 0.25);
            spot = smoothstep(outer, inner, cosAngle);
            if (light.flags.x >= 0)
            {
                float angle = acos(clamp(cosAngle, -1.0, 1.0));
                float maxAngle = max(light.params.w, 0.01);
                ies = texture(textures[light.flags.x], vec2(clamp(angle / maxAngle, 0.0, 1.0), 0.5)).r;
            }
        }

        vec3 radiance = light.color.rgb * attenuation * spot * ies * lightScale;
        vec3 hLight = normalize(v + L);
        float ndhLight = max(dot(n, hLight), 0.0);
        vec3 specLight = fresnelSchlick(max(dot(v, hLight), 0.0), f0) * pow(ndhLight, 32.0) * 0.2;
        color += (kd * albedo / PI + specLight) * radiance * lightNdl;
    }

    color += m.emissive.rgb * max(m.emissive.w, 0.0);

    // P4 glass/refraction: Fresnel-weighted environment reflection plus a refracted
    // sky tint. (A true screen-space colour fetch arrives with the P5 post pipeline; this
    // keeps the glass responsive without an extra scene copy.)
    bool solidGlass = (materialFlags & FLAG_SOLID_GLASS) != 0;
    if (solidGlass || materialType == 2)
    {
        vec3 reflDir = reflect(-v, n);
        float skyT = clamp(reflDir.y * 0.5 + 0.5, 0.0, 1.0);
        vec3 skyRefl = mix(vec3(0.04, 0.05, 0.08), vec3(0.32, 0.45, 0.66), skyT);
        float fres = pow(clamp(1.0 - ndv, 0.0, 1.0), 4.0);

        if (solidGlass)
        {
            vec3 refrDir = refract(-v, n, 1.0 / max(m.params2.x, 1.01));
            vec3 refrTint = albedo * (0.40 + 0.60 * clamp(refrDir.y * 0.5 + 0.5, 0.0, 1.0));
            color = mix(color, mix(refrTint, skyRefl, clamp(0.18 + 0.82 * fres, 0.0, 1.0)), 0.80);
            alpha = max(alpha, mix(0.35, 0.95, fres));
        }
        else
        {
            color = mix(color, skyRefl, clamp(fres + 0.08, 0.0, 0.70));
            alpha = max(alpha, 0.72);
        }
    }

    // Simple P2 tonemap + gamma for the UNORM swapchain; P5 replaces this with ACES.
    color = color / (color + vec3(1.0));
    color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));

    outColor = vec4(color, alpha);
}
