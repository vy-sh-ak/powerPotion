using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PowerPotion.Protocol;
using PowerPotion.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace PowerPotion.Rendering.Scene;

[StructLayout(LayoutKind.Sequential)]
internal struct Int4
{
    public int X, Y, Z, W;
    public Int4(int x, int y, int z, int w) { X = x; Y = y; Z = z; W = w; }
}

/// <summary>GPU material record (std430-compatible, 288 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MaterialGpu
{
    public Vector4 Diffuse;    // rgb, opacity
    public Vector4 Tint;       // rgb, imageFade
    public Vector4 Emissive;   // rgb, strength
    public Vector4 Params;     // roughness, metallic, specular, bumpAmount
    public Vector4 Params2;    // refraction, materialType, flags, pad
    public Int4 Tex;           // diffuse, bump, roughness, mask (-1 = none)
    public Vector4 Uv0R0, Uv0R1, Uv0R2; // diffuse transform rows + inversion/brightness
    public Vector4 Uv1R0, Uv1R1, Uv1R2; // bump
    public Vector4 Uv2R0, Uv2R1, Uv2R2; // roughness
    public Vector4 Uv3R0, Uv3R1, Uv3R2; // mask
}

internal static class MaterialFlags
{
    public const int DoubleSided = 1 << 0;
    public const int SolidGlass = 1 << 1;
    public const int WhiteMode = 1 << 2;
    public const int Cutout = 1 << 3;
    public const int BumpIsNormal = 1 << 4;
    public const int HasDiffuse = 1 << 5;
    public const int HasBump = 1 << 6;
    public const int HasRoughness = 1 << 7;
    public const int HasMask = 1 << 8;
    public const int WcsShift = 16;
}

internal static class MaterialMapper
{
    public static MaterialGpu Build(MaterialDto m, int diffuseTex, int bumpTex, int roughTex, int maskTex)
    {
        int flags = 0;
        if (m.DoubleSided) flags |= MaterialFlags.DoubleSided;
        if (m.IsSolidGlass) flags |= MaterialFlags.SolidGlass;
        if (m.MaterialType == 3) flags |= MaterialFlags.Cutout; // FOLIAGE
        if (m.BumpMapKind == 2) flags |= MaterialFlags.BumpIsNormal;
        if (diffuseTex >= 0) flags |= MaterialFlags.HasDiffuse;
        if (bumpTex >= 0) flags |= MaterialFlags.HasBump;
        if (roughTex >= 0) flags |= MaterialFlags.HasRoughness;
        if (maskTex >= 0) flags |= MaterialFlags.HasMask;
        flags |= (m.DiffuseTexture.WcsType & 0x3) << MaterialFlags.WcsShift;

        var result = new MaterialGpu
        {
            Diffuse = new Vector4(m.DiffuseR, m.DiffuseG, m.DiffuseB, Math.Clamp(m.Opacity, 0f, 1f)),
            Tint = new Vector4(m.TintR, m.TintG, m.TintB, Math.Clamp(m.ImageFade, 0f, 1f)),
            Emissive = new Vector4(m.EmissiveR, m.EmissiveG, m.EmissiveB, Math.Max(m.EmissiveStrength, 0f)),
            Params = new Vector4(
                Math.Clamp(m.Roughness, 0.02f, 1f),
                Math.Clamp(m.Metallic, 0f, 1f),
                Math.Clamp(m.Specular, 0f, 1f),
                Math.Max(m.BumpAmount, 0f)),
            Params2 = new Vector4(m.Refraction, m.MaterialType, flags, 0),
            Tex = new Int4(diffuseTex, bumpTex, roughTex, maskTex),
        };

        SetTransform(ref result.Uv0R0, ref result.Uv0R1, ref result.Uv0R2, m.DiffuseTexture);
        SetTransform(ref result.Uv1R0, ref result.Uv1R1, ref result.Uv1R2, m.BumpTexture);
        SetTransform(ref result.Uv2R0, ref result.Uv2R1, ref result.Uv2R2, m.RoughnessTexture);
        SetTransform(ref result.Uv3R0, ref result.Uv3R1, ref result.Uv3R2, m.MaskTexture);
        return result;
    }

    private static void SetTransform(ref Vector4 r0, ref Vector4 r1, ref Vector4 r2, TextureSlotDto slot)
    {
        var t = slot.Transformation;
        if (t is not { Length: >= 9 })
            t = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };

        r0 = new Vector4(t[0], t[1], t[2], slot.IsInverted ? 1f : 0f);
        r1 = new Vector4(t[3], t[4], t[5], Math.Max(slot.Brightness, 0f));
        r2 = new Vector4(t[6], t[7], t[8], 0f);
    }
}

/// <summary>Host-visible material storage buffer with per-entry hot updates.</summary>
internal sealed unsafe class MaterialBuffer : IDisposable
{
    public const int EntrySize = 288;
    private readonly VulkanAllocator _allocator;
    private readonly BufferAllocation _allocation;
    private bool _disposed;

    public MaterialBuffer(VulkanAllocator allocator, int capacity)
    {
        _allocator = allocator;
        Capacity = capacity;
        _allocation = allocator.CreateBuffer(
            (ulong)capacity * EntrySize,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            "materials");
    }

    public int Capacity { get; }
    public BufferAllocation Allocation => _allocation;
    public Silk.NET.Vulkan.Buffer Buffer => _allocation.Buffer;

    public void Set(int index, in MaterialGpu material)
    {
        if ((uint)index >= (uint)Capacity) return;
        var ptr = (byte*)_allocation.Mapped + (nint)(index * EntrySize);
        Unsafe.Write(ptr, material);
    }

    public void Clear()
    {
        var ptr = (void*)_allocation.Mapped;
        Unsafe.InitBlock(ptr, 0, (uint)(Capacity * EntrySize));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _allocation.Destroy(immediate: true);
    }
}
