using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace PowerPotion.Rendering.Vulkan;

/// <summary>
/// P1 memory allocator: dedicated image allocations, sub-allocated buffer blocks, a persistent
/// staging ring for uploads, and frame-deferred frees. No VMA dependency.
/// </summary>
public sealed unsafe class VulkanAllocator : IDisposable
{
    private const ulong DeviceBlockSize = 64UL * 1024 * 1024;
    private const ulong HostBlockSize = 16UL * 1024 * 1024;
    private const ulong StagingRingSize = 16UL * 1024 * 1024;
    private const ulong Alignment = 256;

    private readonly VulkanContext _context;
    private readonly List<MemoryBlock> _blocks = new();
    private readonly List<DeferredFree> _deferred = new();
    private readonly CommandPool _uploadPool;
    private readonly VkBuffer _stagingBuffer;
    private readonly DeviceMemory _stagingMemory;
    private readonly IntPtr _stagingMapped;
    private ulong _stagingOffset;
    private bool _disposed;

    public VulkanAllocator(VulkanContext context)
    {
        _context = context;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = context.GraphicsFamily,
        };
        var poolResult = context.Vk.CreateCommandPool(context.Device, &poolInfo, null, out _uploadPool);
        if (poolResult != Result.Success)
            throw new InvalidOperationException($"allocator vkCreateCommandPool failed: {poolResult}");

        CreateBufferRaw(StagingRingSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _stagingBuffer, out _stagingMemory, out var stagingOffset);
        _stagingOffset = stagingOffset;
        void* mapped;
        var mapResult = context.Vk.MapMemory(context.Device, _stagingMemory, 0, StagingRingSize, 0, &mapped);
        if (mapResult != Result.Success)
            throw new InvalidOperationException($"allocator vkMapMemory failed: {mapResult}");
        _stagingMapped = (IntPtr)mapped;
    }

    public ulong AllocatedBytes { get; private set; }
    public int BlockCount => _blocks.Count;
    public int LiveAllocations { get; private set; }
    public int StagingUploads { get; private set; }

    public event Action<string>? Log;

    private void Write(string message) => Log?.Invoke(message);

    public BufferAllocation CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, string name = "")
    {
        var vk = _context.Vk;
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        var result = vk.CreateBuffer(_context.Device, &info, null, out var buffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateBuffer failed: {result}");

        vk.GetBufferMemoryRequirements(_context.Device, buffer, out var requirements);
        var memoryType = _context.FindMemoryType(requirements.MemoryTypeBits, properties);
        var block = FindOrCreateBlock(memoryType, properties, requirements.Size, requirements.Alignment);
        var offset = AllocateRange(block, requirements.Size, requirements.Alignment);

        var bindResult = vk.BindBufferMemory(_context.Device, buffer, block.Memory, offset);
        if (bindResult != Result.Success)
            throw new InvalidOperationException($"vkBindBufferMemory failed: {bindResult}");

        block.LiveAllocations++;
        LiveAllocations++;
        _context.SetObjectName(buffer.Handle, ObjectType.Buffer, string.IsNullOrEmpty(name) ? "buffer" : name);

        return new BufferAllocation(this, buffer, block, offset, requirements.Size,
            block.Mapped ? block.MappedPtr + (nint)offset : IntPtr.Zero);
    }

    public ImageAllocation CreateImage(uint width, uint height, Format format, ImageUsageFlags usage,
        string name = "", uint mipLevels = 1, uint arrayLayers = 1)
    {
        var vk = _context.Vk;
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D { Width = width, Height = height, Depth = 1 },
            MipLevels = mipLevels,
            ArrayLayers = arrayLayers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        var result = vk.CreateImage(_context.Device, &info, null, out var image);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage failed: {result}");

        vk.GetImageMemoryRequirements(_context.Device, image, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _context.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        var allocResult = vk.AllocateMemory(_context.Device, &allocInfo, null, out var memory);
        if (allocResult != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory (image) failed: {allocResult}");

        vk.BindImageMemory(_context.Device, image, memory, 0);
        AllocatedBytes += requirements.Size;
        LiveAllocations++;
        _context.SetObjectName(image.Handle, ObjectType.Image, string.IsNullOrEmpty(name) ? "image" : name);

        return new ImageAllocation(this, image, memory, requirements.Size);
    }

    public void UploadImage(ImageAllocation destination, uint width, uint height, ReadOnlySpan<byte> rgba)
        => UploadImage(destination, width, height, new[] { rgba.ToArray() });

    public void UploadImage(ImageAllocation destination, uint width, uint height, IReadOnlyList<byte[]> mips)
    {
        if (mips.Count == 0) return;

        var vk = _context.Vk;
        ulong totalSize = 0;
        foreach (var mip in mips) totalSize += (ulong)mip.Length;

        CreateBufferRaw(totalSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var memory, out _);

        try
        {
            void* mapped;
            vk.MapMemory(_context.Device, memory, 0, totalSize, 0, &mapped);
            ulong offset = 0;
            foreach (var mip in mips)
            {
                fixed (byte* p = mip)
                    System.Buffer.MemoryCopy(p, (byte*)mapped + (nint)offset, (long)mip.Length, mip.Length);
                offset += (ulong)mip.Length;
            }
            vk.UnmapMemory(_context.Device, memory);

            uint levelCount = (uint)mips.Count;
            var commandBuffer = BeginSingleTimeCommands();
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = destination.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = levelCount,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
            };
            vk.CmdPipelineBarrier(commandBuffer,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransfer);

            ulong bufferOffset = 0;
            for (int i = 0; i < mips.Count; i++)
            {
                uint mipWidth = Math.Max(1, width >> i);
                uint mipHeight = Math.Max(1, height >> i);
                var region = new BufferImageCopy
                {
                    BufferOffset = bufferOffset,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = (uint)i,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                    ImageExtent = new Extent3D { Width = mipWidth, Height = mipHeight, Depth = 1 },
                };
                vk.CmdCopyBufferToImage(commandBuffer, staging, destination.Image,
                    ImageLayout.TransferDstOptimal, 1, &region);
                bufferOffset += (ulong)mips[i].Length;
            }

            var toShader = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = destination.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = levelCount,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
            };
            vk.CmdPipelineBarrier(commandBuffer,
                PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
                0, 0, null, 0, null, 1, &toShader);

            EndSingleTimeCommands(commandBuffer);
        }
        finally
        {
            vk.DestroyBuffer(_context.Device, staging, null);
            vk.FreeMemory(_context.Device, memory, null);
        }
    }

    public void UploadBuffer(BufferAllocation destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0)
    {
        if (data.Length == 0) return;

        ulong aligned = Align((ulong)data.Length);
        if (aligned > StagingRingSize)
        {
            UploadViaDedicatedStaging(destination, data, destinationOffset);
            return;
        }

        if (_stagingOffset + aligned > StagingRingSize)
            _stagingOffset = 0;

        var source = IntPtr.Add(_stagingMapped, (int)_stagingOffset);
        fixed (byte* p = data)
            System.Buffer.MemoryCopy(p, (void*)source, (long)aligned, data.Length);

        CopyBuffer(_stagingBuffer, _stagingOffset, destination.Buffer, destinationOffset, (ulong)data.Length);
        _stagingOffset += aligned;
        StagingUploads++;
    }

    private void UploadViaDedicatedStaging(BufferAllocation destination, ReadOnlySpan<byte> data, ulong destinationOffset)
    {
        CreateBufferRaw((ulong)data.Length, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var memory, out _);
        try
        {
            void* mapped;
            _context.Vk.MapMemory(_context.Device, memory, 0, (ulong)data.Length, 0, &mapped);
            fixed (byte* p = data)
                System.Buffer.MemoryCopy(p, mapped, data.Length, data.Length);
            _context.Vk.UnmapMemory(_context.Device, memory);
            CopyBuffer(staging, 0, destination.Buffer, destinationOffset, (ulong)data.Length);
        }
        finally
        {
            _context.Vk.DestroyBuffer(_context.Device, staging, null);
            _context.Vk.FreeMemory(_context.Device, memory, null);
        }
    }

    private void CopyBuffer(VkBuffer source, ulong sourceOffset, VkBuffer destination, ulong destinationOffset, ulong size)
    {
        var commandBuffer = BeginSingleTimeCommands();
        var region = new BufferCopy
        {
            SrcOffset = sourceOffset,
            DstOffset = destinationOffset,
            Size = size,
        };
        _context.Vk.CmdCopyBuffer(commandBuffer, source, destination, 1, &region);
        EndSingleTimeCommands(commandBuffer);
    }

    public void Destroy(BufferAllocation allocation, bool immediate = false)
    {
        if (allocation.Buffer.Handle == 0) return;
        _deferred.Add(new DeferredFree
        {
            Buffer = allocation.Buffer,
            Image = default,
            Memory = default,
            Block = allocation.Block as MemoryBlock,
            Offset = allocation.Offset,
            Size = allocation.Size,
            IsImage = false,
            FreeFrame = immediate ? 0 : allocation.FreeFrame,
        });
        allocation.Buffer = default;
    }

    public void Destroy(ImageAllocation allocation, bool immediate = false)
    {
        if (allocation.Image.Handle == 0) return;
        _deferred.Add(new DeferredFree
        {
            Buffer = default,
            Image = allocation.Image,
            Memory = allocation.Memory,
            Block = null,
            Offset = 0,
            Size = allocation.Size,
            IsImage = true,
            FreeFrame = immediate ? 0 : allocation.FreeFrame,
        });
        allocation.Image = default;
    }

    /// <summary>Frees resources whose deferral frame has completed.</summary>
    public void CollectGarbage(ulong completedFrame)
    {
        for (int i = _deferred.Count - 1; i >= 0; i--)
        {
            var free = _deferred[i];
            if (free.FreeFrame > completedFrame) continue;
            _deferred.RemoveAt(i);
            FreeNow(free);
        }
    }

    private void FreeNow(DeferredFree free)
    {
        var vk = _context.Vk;
        if (free.IsImage)
        {
            vk.DestroyImage(_context.Device, free.Image, null);
            vk.FreeMemory(_context.Device, free.Memory, null);
            AllocatedBytes -= free.Size;
            LiveAllocations--;
            return;
        }

        vk.DestroyBuffer(_context.Device, free.Buffer, null);
        LiveAllocations--;
        var block = free.Block;
        if (block == null) return;

        block.FreeRanges.Add(new FreeRange(free.Offset, free.Size));
        block.LiveAllocations--;
        Coalesce(block);
        if (block.LiveAllocations == 0)
        {
            vk.FreeMemory(_context.Device, block.Memory, null);
            AllocatedBytes -= block.Size;
            _blocks.Remove(block);
        }
    }

    private MemoryBlock FindOrCreateBlock(uint memoryType, MemoryPropertyFlags properties, ulong size, ulong alignment)
    {
        foreach (var block in _blocks)
        {
            if (block.MemoryTypeIndex != memoryType) continue;
            if (CanAllocate(block, size, alignment)) return block;
        }

        var blockSize = Math.Max(size, properties.HasFlag(MemoryPropertyFlags.HostVisibleBit) ? HostBlockSize : DeviceBlockSize);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = blockSize,
            MemoryTypeIndex = memoryType,
        };

        var result = _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out var memory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory (block) failed: {result}");

        var newBlock = new MemoryBlock
        {
            Memory = memory,
            Size = blockSize,
            MemoryTypeIndex = memoryType,
            FreeRanges = new List<FreeRange> { new(0, blockSize) },
        };

        if (properties.HasFlag(MemoryPropertyFlags.HostVisibleBit))
        {
            void* mapped;
            _context.Vk.MapMemory(_context.Device, memory, 0, blockSize, 0, &mapped);
            newBlock.Mapped = true;
            newBlock.MappedPtr = (IntPtr)mapped;
        }

        _blocks.Add(newBlock);
        AllocatedBytes += blockSize;
        Write($"Allocator: new block {blockSize / (1024 * 1024)} MB (type {memoryType}), total blocks={_blocks.Count}");
        return newBlock;
    }

    private static ulong AllocateRange(MemoryBlock block, ulong size, ulong alignment)
    {
        if (!TryAllocate(block, size, alignment, out var offset))
            throw new InvalidOperationException("Allocator block unexpectedly full.");
        return offset;
    }

    private static bool CanAllocate(MemoryBlock block, ulong size, ulong alignment)
    {
        ulong align = Math.Max(alignment, Alignment);
        foreach (var range in block.FreeRanges)
        {
            ulong aligned = AlignUp(range.Offset, align);
            if (aligned + size <= range.Offset + range.Size) return true;
        }
        return false;
    }

    private static bool TryAllocate(MemoryBlock block, ulong size, ulong alignment, out ulong offset)
    {
        ulong align = Math.Max(alignment, Alignment);
        for (int i = 0; i < block.FreeRanges.Count; i++)
        {
            var range = block.FreeRanges[i];
            ulong aligned = AlignUp(range.Offset, align);
            if (aligned + size > range.Offset + range.Size) continue;

            block.FreeRanges.RemoveAt(i);
            if (aligned > range.Offset)
                block.FreeRanges.Add(new FreeRange(range.Offset, aligned - range.Offset));
            if (aligned + size < range.Offset + range.Size)
                block.FreeRanges.Add(new FreeRange(aligned + size, range.Offset + range.Size - aligned - size));
            Sort(block);
            offset = aligned;
            return true;
        }

        offset = 0;
        return false;
    }

    private static void Coalesce(MemoryBlock block)
    {
        Sort(block);
        for (int i = 0; i < block.FreeRanges.Count - 1; i++)
        {
            var a = block.FreeRanges[i];
            var b = block.FreeRanges[i + 1];
            if (a.Offset + a.Size >= b.Offset)
            {
                block.FreeRanges[i] = new FreeRange(a.Offset, Math.Max(a.Size, b.Offset + b.Size - a.Offset));
                block.FreeRanges.RemoveAt(i + 1);
                i--;
            }
        }
    }

    private static void Sort(MemoryBlock block) =>
        block.FreeRanges.Sort((a, b) => a.Offset.CompareTo(b.Offset));

    private static ulong Align(ulong value) => AlignUp(value, Alignment);
    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    private void CreateBufferRaw(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
        out VkBuffer buffer, out DeviceMemory memory, out ulong offset)
    {
        var vk = _context.Vk;
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        var result = vk.CreateBuffer(_context.Device, &info, null, out buffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateBuffer failed: {result}");

        vk.GetBufferMemoryRequirements(_context.Device, buffer, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _context.FindMemoryType(requirements.MemoryTypeBits, properties),
        };

        var allocResult = vk.AllocateMemory(_context.Device, &allocInfo, null, out memory);
        if (allocResult != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory failed: {allocResult}");

        vk.BindBufferMemory(_context.Device, buffer, memory, 0);
        offset = 0;
        AllocatedBytes += requirements.Size;
    }

    private CommandBuffer BeginSingleTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _uploadPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        _context.Vk.AllocateCommandBuffers(_context.Device, &allocInfo, out var commandBuffer);
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _context.Vk.BeginCommandBuffer(commandBuffer, &begin);
        return commandBuffer;
    }

    private void EndSingleTimeCommands(CommandBuffer commandBuffer)
    {
        _context.Vk.EndCommandBuffer(commandBuffer);
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        _context.Vk.QueueSubmit(_context.GraphicsQueue, 1, &submit, default);
        _context.Vk.QueueWaitIdle(_context.GraphicsQueue);
        _context.Vk.FreeCommandBuffers(_context.Device, _uploadPool, 1, &commandBuffer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var vk = _context.Vk;
        vk.DeviceWaitIdle(_context.Device);

        foreach (var free in _deferred)
            FreeNow(free);
        _deferred.Clear();

        foreach (var block in _blocks.ToArray())
        {
            if (block.Mapped) vk.UnmapMemory(_context.Device, block.Memory);
            vk.FreeMemory(_context.Device, block.Memory, null);
        }
        _blocks.Clear();

        vk.DestroyBuffer(_context.Device, _stagingBuffer, null);
        vk.FreeMemory(_context.Device, _stagingMemory, null);
        vk.DestroyCommandPool(_context.Device, _uploadPool, null);
        AllocatedBytes = 0;
        LiveAllocations = 0;
    }

    private sealed class MemoryBlock
    {
        public DeviceMemory Memory;
        public ulong Size;
        public uint MemoryTypeIndex;
        public bool Mapped;
        public IntPtr MappedPtr;
        public int LiveAllocations;
        public List<FreeRange> FreeRanges = new();
    }

    private readonly struct FreeRange
    {
        public FreeRange(ulong offset, ulong size) { Offset = offset; Size = size; }
        public ulong Offset { get; }
        public ulong Size { get; }
    }

    private sealed class DeferredFree
    {
        public VkBuffer Buffer;
        public Image Image;
        public DeviceMemory Memory;
        public MemoryBlock? Block;
        public ulong Offset;
        public ulong Size;
        public bool IsImage;
        public ulong FreeFrame;
    }
}

public sealed class BufferAllocation
{
    internal BufferAllocation(VulkanAllocator owner, VkBuffer buffer, object block, ulong offset, ulong size, IntPtr mapped)
    {
        Owner = owner;
        Buffer = buffer;
        Block = block;
        Offset = offset;
        Size = size;
        Mapped = mapped;
    }

    internal VulkanAllocator Owner { get; }
    internal object Block { get; }
    public VkBuffer Buffer { get; internal set; }
    public ulong Offset { get; }
    public ulong Size { get; }
    public IntPtr Mapped { get; }
    public ulong FreeFrame { get; set; }
    public void Destroy(bool immediate = false) => Owner.Destroy(this, immediate);
}

public sealed class ImageAllocation
{
    internal ImageAllocation(VulkanAllocator owner, Image image, DeviceMemory memory, ulong size)
    {
        Owner = owner;
        Image = image;
        Memory = memory;
        Size = size;
    }

    internal VulkanAllocator Owner { get; }
    public Image Image { get; internal set; }
    public DeviceMemory Memory { get; }
    public ulong Size { get; }
    public ulong FreeFrame { get; set; }
    public void Destroy(bool immediate = false) => Owner.Destroy(this, immediate);
}
