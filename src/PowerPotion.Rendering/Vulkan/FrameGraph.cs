using Silk.NET.Vulkan;

namespace PowerPotion.Rendering.Vulkan;

/// <summary>
/// P1 frame graph: ordered passes with explicit image transitions, plus a transient-image pool.
/// Passes are recorded into the caller's per-frame command buffer; the graph only owns barrier
/// insertion and transient lifetimes.
/// </summary>
public sealed unsafe class FrameGraph : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanAllocator _allocator;
    private readonly List<FramePass> _passes = new();
    private readonly Dictionary<string, TransientImage> _transients = new(StringComparer.Ordinal);
    private bool _disposed;

    public FrameGraph(VulkanContext context, VulkanAllocator allocator)
    {
        _context = context;
        _allocator = allocator;
    }

    public int PassCount => _passes.Count;
    public int TransientCount => _transients.Count;

    public void Reset() => _passes.Clear();

    public void AddPass(string name, Action<CommandBuffer> record, params ImageTransition[] transitions)
    {
        _passes.Add(new FramePass(name, record, transitions));
    }

    public void Execute(CommandBuffer commandBuffer)
    {
        foreach (var pass in _passes)
        {
            foreach (var transition in pass.Transitions)
                InsertBarrier(commandBuffer, transition);
            pass.Record(commandBuffer);
        }
        _passes.Clear();
    }

    private void InsertBarrier(CommandBuffer commandBuffer, ImageTransition transition)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = transition.SrcAccess,
            DstAccessMask = transition.DstAccess,
            OldLayout = transition.OldLayout,
            NewLayout = transition.NewLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = transition.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = transition.Aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = transition.LayerCount,
            },
        };

        _context.Vk.CmdPipelineBarrier(commandBuffer,
            transition.SrcStage, transition.DstStage,
            0, 0, null, 0, null, 1, &barrier);
    }

    public TransientImage GetTransientImage(string name, uint width, uint height, Format format,
        ImageUsageFlags usage)
    {
        var key = $"{name}:{width}x{height}:{(int)format}:{(int)usage}";
        if (_transients.TryGetValue(key, out var existing)) return existing;

        var allocation = _allocator.CreateImage(width, height, format, usage, name);
        var aspect = (usage & ImageUsageFlags.DepthStencilAttachmentBit) != 0
            ? ImageAspectFlags.DepthBit
            : ImageAspectFlags.ColorBit;

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = allocation.Image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            Components = new ComponentMapping
            {
                R = ComponentSwizzle.Identity,
                G = ComponentSwizzle.Identity,
                B = ComponentSwizzle.Identity,
                A = ComponentSwizzle.Identity,
            },
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        var result = _context.Vk.CreateImageView(_context.Device, &viewInfo, null, out var view);
        if (result != Result.Success)
            throw new InvalidOperationException($"frame graph vkCreateImageView failed: {result}");
        _context.SetObjectName(view.Handle, ObjectType.ImageView, name);

        var transient = new TransientImage(name, allocation, view, width, height, format, usage);
        _transients[key] = transient;
        return transient;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var vk = _context.Vk;
        foreach (var transient in _transients.Values)
        {
            vk.DestroyImageView(_context.Device, transient.View, null);
            transient.Allocation.Destroy(immediate: true);
        }
        _transients.Clear();
        _allocator.CollectGarbage(0);
    }

    private sealed record FramePass(string Name, Action<CommandBuffer> Record, ImageTransition[] Transitions);
}

public readonly struct ImageTransition
{
    public ImageTransition(Image image, ImageLayout oldLayout, ImageLayout newLayout,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage,
        AccessFlags srcAccess, AccessFlags dstAccess, ImageAspectFlags aspect, uint layerCount = 1)
    {
        Image = image;
        OldLayout = oldLayout;
        NewLayout = newLayout;
        SrcStage = srcStage;
        DstStage = dstStage;
        SrcAccess = srcAccess;
        DstAccess = dstAccess;
        Aspect = aspect;
        LayerCount = layerCount;
    }

    public Image Image { get; }
    public ImageLayout OldLayout { get; }
    public ImageLayout NewLayout { get; }
    public PipelineStageFlags SrcStage { get; }
    public PipelineStageFlags DstStage { get; }
    public AccessFlags SrcAccess { get; }
    public AccessFlags DstAccess { get; }
    public ImageAspectFlags Aspect { get; }
    public uint LayerCount { get; }
}

public sealed class TransientImage
{
    public TransientImage(string name, ImageAllocation allocation, ImageView view,
        uint width, uint height, Format format, ImageUsageFlags usage)
    {
        Name = name;
        Allocation = allocation;
        View = view;
        Width = width;
        Height = height;
        Format = format;
        Usage = usage;
    }

    public string Name { get; }
    public ImageAllocation Allocation { get; }
    public ImageView View { get; }
    public uint Width { get; }
    public uint Height { get; }
    public Format Format { get; }
    public ImageUsageFlags Usage { get; }
}
