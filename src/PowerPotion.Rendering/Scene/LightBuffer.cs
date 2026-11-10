using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PowerPotion.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace PowerPotion.Rendering.Scene;

[StructLayout(LayoutKind.Sequential)]
internal struct LightGpu
{
    public Vector4 Position;  // xyz, w = distribution (1 spherical, 2 hemispherical, 3 spot)
    public Vector4 Color;     // rgb, w = intensity (candela-ish)
    public Vector4 Direction; // xyz forward, w = shape (1 point, 2 line, 3 rect, 4 circle)
    public Vector4 Params;    // x beam angle, y shapeParam1, z shapeParam2, w IES max angle
    public Int4 Flags;        // x IES texture index, y enabled, z pad, w pad
}

internal sealed unsafe class LightBuffer : IDisposable
{
    public const int EntrySize = 80;

    private readonly VulkanAllocator _allocator;
    private readonly BufferAllocation _allocation;
    private bool _disposed;

    public LightBuffer(VulkanAllocator allocator, int capacity)
    {
        _allocator = allocator;
        Capacity = capacity;
        _allocation = allocator.CreateBuffer(
            (ulong)capacity * EntrySize,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            "lights");
    }

    public int Capacity { get; }
    public BufferAllocation Allocation => _allocation;
    public Silk.NET.Vulkan.Buffer Buffer => _allocation.Buffer;

    public void Set(int index, in LightGpu light)
    {
        if ((uint)index >= (uint)Capacity) return;
        var ptr = (byte*)_allocation.Mapped + (nint)(index * EntrySize);
        Unsafe.Write(ptr, light);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _allocation.Destroy(immediate: true);
    }
}
