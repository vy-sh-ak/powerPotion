using Silk.NET.Vulkan;

namespace PowerPotion.Rendering.Vulkan;

/// <summary>
/// Owns the swapchain, its images/views, format selection and present mode. P1 adds:
/// HDR format selection, vsync on/off, robust recreation and debug names.
/// </summary>
public sealed unsafe class Swapchain : IDisposable
{
    private readonly VulkanContext _context;
    private SwapchainKHR _swapchain;
    private Image[] _images = Array.Empty<Image>();
    private ImageView[] _views = Array.Empty<ImageView>();
    private Format _format;
    private ColorSpaceKHR _colorSpace;
    private Extent2D _extent;
    private PresentModeKHR _presentMode;
    private bool _hdr;
    private bool _vsync = true;
    private bool _disposed;

    /// <summary>True when the surface supports copying the swapchain image into a sampled texture.</summary>
    public bool SupportsTransferSrc { get; private set; }

    public Swapchain(VulkanContext context, bool vsync, bool hdr)
    {
        _context = context;
        _vsync = vsync;
        _hdr = hdr;
    }

    public SwapchainKHR Handle => _swapchain;
    public Image[] Images => _images;
    public ImageView[] ImageViews => _views;
    public Format Format => _format;
    public ColorSpaceKHR ColorSpace => _colorSpace;
    public Extent2D Extent => _extent;
    public PresentModeKHR PresentMode => _presentMode;
    public bool IsHdr => _hdr;
    public bool Vsync => _vsync;
    public int ImageCount => _images.Length;

    public event Action<string>? Log;

    private void Write(string message) => Log?.Invoke(message);

    public void Create(uint width, uint height)
    {
        var vk = _context.Vk;
        var khrSurface = _context.KhrSurface;
        var khrSwapchain = _context.KhrSwapchain;
        var physical = _context.PhysicalDevice;
        var device = _context.Device;

        khrSurface.GetPhysicalDeviceSurfaceCapabilities(physical, _context.Surface, out var caps);

        uint formatCount = 0;
        khrSurface.GetPhysicalDeviceSurfaceFormats(physical, _context.Surface, ref formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* p = formats)
            khrSurface.GetPhysicalDeviceSurfaceFormats(physical, _context.Surface, ref formatCount, p);

        uint presentCount = 0;
        khrSurface.GetPhysicalDeviceSurfacePresentModes(physical, _context.Surface, ref presentCount, null);
        var presentModes = new PresentModeKHR[presentCount];
        fixed (PresentModeKHR* p = presentModes)
            khrSurface.GetPhysicalDeviceSurfacePresentModes(physical, _context.Surface, ref presentCount, p);

        var chosen = ChooseSurfaceFormat(formats, _hdr);
        _format = chosen.Format;
        _colorSpace = chosen.ColorSpace;

        var actualHdr = (chosen.Format == Format.A2B10G10R10UnormPack32 &&
                         chosen.ColorSpace == ColorSpaceKHR.SpaceHdr10ST2084Ext) ||
                        (chosen.Format == Format.R16G16B16A16Sfloat &&
                         chosen.ColorSpace == ColorSpaceKHR.SpaceExtendedSrgbLinearExt);
        if (_hdr && !actualHdr)
            Write("HDR requested but the surface exposes no HDR10/linear format; using SDR.");
        _hdr = actualHdr;

        _presentMode = ChoosePresentMode(presentModes, _vsync);
        _extent = ChooseExtent(caps, width, height);
        SupportsTransferSrc = (caps.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;

        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
            imageCount = caps.MaxImageCount;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _context.Surface,
            MinImageCount = imageCount,
            ImageFormat = _format,
            ImageColorSpace = _colorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit |
                         (SupportsTransferSrc ? ImageUsageFlags.TransferSrcBit : 0),
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = _presentMode,
            Clipped = true,
            OldSwapchain = default,
        };

        uint[] families = { _context.GraphicsFamily, _context.PresentFamily };
        fixed (uint* familyPtr = families)
        {
            if (_context.GraphicsFamily != _context.PresentFamily)
            {
                createInfo.ImageSharingMode = SharingMode.Concurrent;
                createInfo.QueueFamilyIndexCount = 2;
                createInfo.PQueueFamilyIndices = familyPtr;
            }
            else
            {
                createInfo.ImageSharingMode = SharingMode.Exclusive;
            }

            var result = khrSwapchain.CreateSwapchain(device, &createInfo, null, out _swapchain);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateSwapchainKHR failed: {result}");
        }

        uint imageCountActual = 0;
        khrSwapchain.GetSwapchainImages(device, _swapchain, ref imageCountActual, null);
        _images = new Image[imageCountActual];
        fixed (Image* p = _images)
            khrSwapchain.GetSwapchainImages(device, _swapchain, ref imageCountActual, p);

        _views = new ImageView[_images.Length];
        for (int i = 0; i < _images.Length; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = _format,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };

            var result = vk.CreateImageView(device, &viewInfo, null, out _views[i]);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateImageView failed: {result}");

            _context.SetObjectName(_images[i].Handle, ObjectType.Image, $"swapchain image {i}");
            _context.SetObjectName(_views[i].Handle, ObjectType.ImageView, $"swapchain view {i}");
        }

        _context.SetObjectName(_swapchain.Handle, ObjectType.SwapchainKhr, "main swapchain");
        Write($"Swapchain: {_extent.Width}x{_extent.Height}, {imageCountActual} images, {_format}, " +
              $"{_colorSpace}, {_presentMode}, hdr={_hdr}, vsync={_vsync}");
    }

    public void Recreate(uint width, uint height)
    {
        DestroySwapchain();
        Create(width, height);
    }

    public void Destroy() => DestroySwapchain();

    public void SetVsync(bool vsync)
    {
        if (_vsync == vsync) return;
        _vsync = vsync;
        // The caller recreates the swapchain; present mode is not mutable in place.
    }

    public void SetHdr(bool hdr)
    {
        if (_hdr == hdr) return;
        _hdr = hdr;
    }

    public bool SupportsHdr(IReadOnlyList<SurfaceFormatKHR> formats) =>
        formats.Any(f => (f.Format == Format.A2B10G10R10UnormPack32 && f.ColorSpace == ColorSpaceKHR.SpaceHdr10ST2084Ext) ||
                         (f.Format == Format.R16G16B16A16Sfloat && f.ColorSpace == ColorSpaceKHR.SpaceExtendedSrgbLinearExt));

    private static SurfaceFormatKHR ChooseSurfaceFormat(SurfaceFormatKHR[] formats, bool hdr)
    {
        if (formats.Length == 1 && formats[0].Format == Format.Undefined)
            return new SurfaceFormatKHR
            {
                Format = Format.B8G8R8A8Unorm,
                ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            };

        if (hdr)
        {
            foreach (var f in formats)
                if (f.Format == Format.A2B10G10R10UnormPack32 && f.ColorSpace == ColorSpaceKHR.SpaceHdr10ST2084Ext)
                    return f;
            foreach (var f in formats)
                if (f.Format == Format.R16G16B16A16Sfloat && f.ColorSpace == ColorSpaceKHR.SpaceExtendedSrgbLinearExt)
                    return f;
        }

        foreach (var f in formats)
            if (f.Format == Format.B8G8R8A8Unorm && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return f;

        foreach (var f in formats)
            if (f.Format == Format.B8G8R8A8Srgb)
                return f;

        return formats[0];
    }

    private static PresentModeKHR ChoosePresentMode(PresentModeKHR[] modes, bool vsync)
    {
        if (vsync)
        {
            foreach (var mode in modes)
                if (mode == PresentModeKHR.FifoKhr)
                    return mode;
            return PresentModeKHR.FifoKhr;
        }

        foreach (var mode in modes)
            if (mode == PresentModeKHR.MailboxKhr)
                return mode;
        foreach (var mode in modes)
            if (mode == PresentModeKHR.ImmediateKhr)
                return mode;
        return PresentModeKHR.FifoKhr;
    }

    private static Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps, uint width, uint height)
    {
        if (caps.CurrentExtent.Width != uint.MaxValue)
            return caps.CurrentExtent;

        return new Extent2D
        {
            Width = Math.Clamp(width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
            Height = Math.Clamp(height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height),
        };
    }

    private void DestroySwapchain()
    {
        if (_swapchain.Handle == 0) return;
        var vk = _context.Vk;
        foreach (var view in _views)
            vk.DestroyImageView(_context.Device, view, null);
        _views = Array.Empty<ImageView>();
        _context.KhrSwapchain.DestroySwapchain(_context.Device, _swapchain, null);
        _swapchain = default;
        _images = Array.Empty<Image>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroySwapchain();
    }
}
