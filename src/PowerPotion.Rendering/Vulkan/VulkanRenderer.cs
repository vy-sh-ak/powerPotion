using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerPotion.Protocol;
using PowerPotion.Rendering.Capture;
using PowerPotion.Rendering.Scene;
using PowerPotion.Rendering.Settings;
using PowerPotion.Rendering.Shaders;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace PowerPotion.Rendering.Vulkan;

/// <summary>
/// Standalone Vulkan renderer. P0 supplied the walking skeleton; P1 split the foundation into
/// <see cref="VulkanContext"/>, <see cref="Swapchain"/>, <see cref="VulkanAllocator"/>,
/// <see cref="DescriptorManager"/>, <see cref="PipelineCache"/> and <see cref="FrameGraph"/>.
/// P2 adds WorldSnapshot scene/material/texture upload and PBR; P3 adds the NOAA sun, procedural
/// and HDR/EXR sky, four-cascade PCF shadows, artificial lights and IES.
///
/// It uses only the public Vulkan API through Silk.NET and ships its own GLSL shaders; it has no
/// Enscape dependency of any kind.
/// </summary>
public sealed unsafe class VulkanRenderer : IDisposable
{
    private const int MaxFramesInFlight = 3;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private VulkanContext _context = null!;

    private Vk _vk => _context.Vk;
    private Instance _instance => _context.Instance;
    private PhysicalDevice _physicalDevice => _context.PhysicalDevice;
    private Device _device => _context.Device;
    private Queue _graphicsQueue => _context.GraphicsQueue;
    private Queue _presentQueue => _context.PresentQueue;
    private uint _graphicsFamily => _context.GraphicsFamily;
    private uint _presentFamily => _context.PresentFamily;
    private KhrSurface _khrSurface => _context.KhrSurface;
    private KhrSwapchain _khrSwapchain => _context.KhrSwapchain;
    private SurfaceKHR _surface => _context.Surface;

    private Swapchain _swapchainState = null!;
    private SwapchainKHR _swapchain => _swapchainState.Handle;
    private Image[] _swapchainImages => _swapchainState.Images;
    private ImageView[] _swapchainImageViews => _swapchainState.ImageViews;
    private Format _swapchainFormat => _swapchainState.Format;
    private Extent2D _swapchainExtent => _swapchainState.Extent;
    private bool _vsync = true;
    private bool _hdr;
    private bool _whiteMode;
    private float _renderScale = 1f;
    private Extent2D _renderExtent;

    private RenderPass _renderPass;
    private RenderPass _transparentRenderPass;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private PipelineCache _pipelines = null!;
    private DescriptorManager _descriptors = null!;

    private readonly BufferAllocation?[] _uniformBuffers = new BufferAllocation?[MaxFramesInFlight];
    private readonly IntPtr[] _uniformMapped = new IntPtr[MaxFramesInFlight];

    private readonly BufferAllocation?[] _sceneUniformBuffers = new BufferAllocation?[MaxFramesInFlight];
    private readonly IntPtr[] _sceneUniformMapped = new IntPtr[MaxFramesInFlight];
    private MaterialBuffer _materialBuffer = null!;
    private LightBuffer _lightBuffer = null!;
    private TextureCache _textures = null!;
    private GpuScene? _scene;
    private readonly CameraState _camera = new();
    private bool _cameraModeLogged;
    private bool _lastCameraOrtho;
    private bool _lastCameraTwoPoint;
    private float _smoothedExposure = 1f;
    private readonly float[] _sectionPlanes = new float[MaxSectionPlanes * 4];
    private int _sectionPlaneCount;
    private const int MaxSectionPlanes = 8;
    private SunUpdate _sun = new();
    private SunInfo _sunInfo = SunPosition.Compute(new SunUpdate(), 1f);
    private RendererSettings _settings = new();
    private PipelineLayout _scenePipelineLayout;
    private Pipeline _sceneOpaquePipeline;
    private Pipeline _sceneDoubleSidedPipeline;
    private Pipeline _sceneTransparentPipeline;
    private long _lastSceneRevision = -1;
    private long _latestRequestedRevision = -1;
    private readonly ConcurrentQueue<SceneUploadData> _pendingScenes = new();
    private int _sceneDrawCalls;
    private bool _cullWarned;

    private BufferAllocation? _vertexBuffer;
    private BufferAllocation? _indexBuffer;

    private ImageAllocation? _depthImage;
    private ImageView _depthView;
    private VulkanAllocator _allocator = null!;
    private FrameGraph _frameGraph = null!;
    private SkyRenderer _sky = null!;
    private ShadowMap _shadowMap = null!;
    private PostProcessor _post = null!;
    private Format _postFormat;
    private Format _sceneColorFormat;
    private ImageAllocation? _sceneColorImage;
    private ImageView _sceneColorView;
    private ImageAllocation? _sceneDepthColorImage;
    private ImageView _sceneDepthColorView;
    private ImageAllocation? _taaHistoryImage;
    private ImageView _taaHistoryView;
    private ImageLayout _taaHistoryLayout = ImageLayout.Undefined;
    private bool _taaHistoryValid;
    private bool _taaLogged;
    private Framebuffer[] _postFramebuffers = Array.Empty<Framebuffer>();
    private Format _depthFormat;
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();
    private Framebuffer[] _transparentFramebuffers = Array.Empty<Framebuffer>();

    private ImageAllocation? _refractionImage;
    private ImageView _refractionView;
    private Sampler _refractionSampler;
    private ImageLayout _refractionLayout = ImageLayout.Undefined;
    private bool _screenRefractionEnabled;
    private const int PushFlagScreenRefraction = 1 << 12;

    private CommandPool _commandPool;
    private readonly CommandBuffer[] _commandBuffers = new CommandBuffer[MaxFramesInFlight];
    private readonly VkSemaphore[] _imageAvailable = new VkSemaphore[MaxFramesInFlight];
    private readonly VkSemaphore[] _renderFinished = new VkSemaphore[MaxFramesInFlight];
    private readonly Fence[] _inFlight = new Fence[MaxFramesInFlight];
    private int _currentFrame;

    private uint _pendingWidth;
    private uint _pendingHeight;
    private bool _framebufferResized;
    private bool _disposed;

    public string DeviceName { get; private set; } = "";
    public string DriverInfo { get; private set; } = "";
    public ulong FrameCount { get; private set; }
    public int SwapchainRecreationCount { get; private set; }
    public int ValidationErrorCount => _context?.ValidationErrorCount ?? 0;
    public int ValidationWarningCount => _context?.ValidationWarningCount ?? 0;
    public bool ValidationEnabled => _context?.ValidationEnabled == true;
    public bool BindlessSupported => _context?.SupportsBindless == true;
    public ulong AllocatedBytes => _allocator?.AllocatedBytes ?? 0;
    public int LiveAllocations => _allocator?.LiveAllocations ?? 0;
    public string SwapchainInfo => _swapchainState == null
        ? ""
        : $"{_swapchainFormat} {_swapchainExtent.Width}x{_swapchainExtent.Height} " +
          $"{_swapchainState.PresentMode} hdr={_swapchainState.IsHdr} vsync={_swapchainState.Vsync}";
    public bool IsInitialized => _context?.Device.Handle != 0;
    public Format SwapchainFormat => _swapchainFormat;
    public Extent2D Extent => _swapchainExtent;
    public uint RenderWidth => _renderExtent.Width;
    public uint RenderHeight => _renderExtent.Height;
    public double LastFrameMilliseconds { get; private set; }
    public double FramesPerSecond { get; private set; }

    public event Action<string>? Log;
    public event Action<long>? SceneUploaded;

    private void Write(string message) => Log?.Invoke(message);

    // ------------------------------------------------------------------
    // Lifetime
    // ------------------------------------------------------------------

    public void Initialize(IntPtr hwnd, uint width, uint height)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("HWND is null.", nameof(hwnd));

        _pendingWidth = Math.Max(width, 64);
        _pendingHeight = Math.Max(height, 64);

        var validation = string.Equals(
            Environment.GetEnvironmentVariable("POWERPOTION_VK_VALIDATION"), "1", StringComparison.Ordinal);
#if DEBUG
        validation = true;
#endif
        _context = new VulkanContext();
        _context.Log += Write;
        _context.Initialize(hwnd, validation);

        DeviceName = _context.DeviceName;
        DriverInfo = _context.DriverInfo;

        _swapchainState = new Swapchain(_context, _vsync, _hdr);
        _swapchainState.Log += Write;
        _swapchainState.Create(_pendingWidth, _pendingHeight);
        _sceneColorFormat = ChooseSceneColorFormat();
        _renderScale = Math.Clamp((float)_settings.RenderScale, 0.5f, 1f);
        _renderExtent = ComputeRenderExtent();
        Write($"render scale: {_renderScale:0.##} -> {_renderExtent.Width}x{_renderExtent.Height} " +
              $"(swapchain {_swapchainExtent.Width}x{_swapchainExtent.Height})");

        CreateRenderPass();
        _descriptors = new DescriptorManager(_context, MaxFramesInFlight);
        _pipelines = new PipelineCache(_context);
        CreatePipeline();
        CreateCommandPool();
        _allocator = new VulkanAllocator(_context);
        _allocator.Log += Write;
        _frameGraph = new FrameGraph(_context, _allocator);
        _sky = new SkyRenderer(_context, _allocator, _pipelines, _renderPass);
        _sky.Log += Write;
        _post = new PostProcessor(_context, _allocator, _pipelines, _swapchainFormat);
        _postFormat = _swapchainFormat;
        uint shadowSize = _settings.QualityLevel >= 3 ? 4096u : _settings.QualityLevel >= 2 ? 2048u : 1024u;
        _shadowMap = new ShadowMap(_context, _allocator, _pipelines, shadowSize, _descriptors.SceneLayout);
        _shadowMap.Log += Write;
        CreateDepthResources();
        CreateSceneColorResources();
        CreateFramebuffers();
        CreateRefractionResources();
        CreateTaaResources();
        Write($"post: HDR scene target {_sceneColorFormat}, ACES tonemap, TAA history enabled");
        CreateVertexBuffer();
        CreateIndexBuffer();
        CreateUniformBuffers();
        _descriptors.CreateUniformSets(_uniformBuffers);

        _materialBuffer = new MaterialBuffer(_allocator, 1024);
        _lightBuffer = new LightBuffer(_allocator, 128);
        _descriptors.CreateBindlessSet();
        _textures = new TextureCache(_context, _allocator, _descriptors);
        _textures.Log += Write;
        CreateSceneUniformBuffers();
        _descriptors.CreateSceneSets(_sceneUniformBuffers, _materialBuffer.Allocation,
            _lightBuffer.Allocation, _shadowMap.ArrayView, _shadowMap.Sampler);
        CreateScenePipelines();
        _camera.SetAspect(_renderExtent.Width, _renderExtent.Height);
        UpdateSceneUniforms(0);

        CreateSyncObjects();

        Write($"Vulkan renderer ready: {DeviceName} ({DriverInfo}), format={_swapchainFormat}, extent={_swapchainExtent.Width}x{_swapchainExtent.Height}");
    }

    private void CreateRenderPass()
    {
        _depthFormat = ChooseDepthFormat();
        _renderPass = BuildRenderPass(clear: true);
        _transparentRenderPass = BuildRenderPass(clear: false);
    }

    private RenderPass BuildRenderPass(bool clear)
    {
        var color = new AttachmentDescription
        {
            Format = _sceneColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = clear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };

        var linearDepth = new AttachmentDescription
        {
            Format = Format.R32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = clear ? AttachmentLoadOp.DontCare : AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };

        var depth = new AttachmentDescription
        {
            Format = _depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = clear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var attachments = new[] { color, linearDepth, depth };
        var colorRefs = new[]
        {
            new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal },
            new AttachmentReference { Attachment = 1, Layout = ImageLayout.ColorAttachmentOptimal },
        };
        var depthRef = new AttachmentReference { Attachment = 2, Layout = ImageLayout.DepthStencilAttachmentOptimal };

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = clear
                ? 0
                : AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };

        fixed (AttachmentReference* colorPtr = colorRefs)
        fixed (AttachmentDescription* attachmentPtr = attachments)
        {
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 2,
                PColorAttachments = colorPtr,
                PDepthStencilAttachment = &depthRef,
            };
            var info = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachments.Length,
                PAttachments = attachmentPtr,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };

            var result = _vk.CreateRenderPass(_device, &info, null, out var renderPass);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateRenderPass failed: {result}");
            return renderPass;
        }
    }
    private Format ChooseDepthFormat()
    {
        foreach (var format in new[] { Format.D32Sfloat, Format.D24UnormS8Uint, Format.D16Unorm })
        {
            var props = _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format);
            if ((props.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0 &&
                (props.OptimalTilingFeatures & FormatFeatureFlags.SampledImageBit) != 0)
            {
                Write($"depth format: {format} (sampled)");
                return format;
            }
        }
        throw new InvalidOperationException("No sampled depth format found.");
    }

    /// <summary>P5: HDR scene-colour target for ACES tonemapping in the post pass.</summary>
    private Format ChooseSceneColorFormat()
    {
        foreach (var format in new[]
                 {
                     Format.R16G16B16A16Sfloat,
                     Format.B10G11R11UfloatPack32,
                     Format.A2B10G10R10UnormPack32,
                 })
        {
            var props = _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format);
            if ((props.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBit) != 0 &&
                (props.OptimalTilingFeatures & FormatFeatureFlags.SampledImageBit) != 0)
                return format;
        }
        return _swapchainFormat;
    }

    /// <summary>P6: render-scale target (scene passes render here; post upscales to the swapchain).</summary>
    private Extent2D ComputeRenderExtent()
    {
        float scale = Math.Clamp(_renderScale, 0.5f, 1f);
        return new Extent2D
        {
            Width = Math.Max(1u, (uint)(_swapchainExtent.Width * scale)),
            Height = Math.Max(1u, (uint)(_swapchainExtent.Height * scale)),
        };
    }

    private void CreatePipeline()
    {
        var vertBytes = ShaderLibrary.Load("cube.vert");
        var fragBytes = ShaderLibrary.Load("cube.frag");

        var vertModule = _pipelines.GetShaderModule("cube.vert", vertBytes);
        var fragModule = _pipelines.GetShaderModule("cube.frag", fragBytes);

        var entry = SilkMarshal.StringToPtr("main");
        try
        {
            var stages = new[]
            {
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertModule,
                    PName = (byte*)entry,
                },
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragModule,
                    PName = (byte*)entry,
                },
            };

            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = (uint)sizeof(Vertex),
                InputRate = VertexInputRate.Vertex,
            };

            var attributes = new[]
            {
                new VertexInputAttributeDescription
                {
                    Location = 0,
                    Binding = 0,
                    Format = Format.R32G32B32Sfloat,
                    Offset = 0,
                },
                new VertexInputAttributeDescription
                {
                    Location = 1,
                    Binding = 0,
                    Format = Format.R32G32B32Sfloat,
                    Offset = 12,
                },
            };

            var dynamicStates = new[] { DynamicState.Viewport, DynamicState.Scissor };
            var colorBlendAttachments = new[]
            {
                new PipelineColorBlendAttachmentState
                {
                    BlendEnable = false,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                },
                new PipelineColorBlendAttachmentState
                {
                    BlendEnable = false,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                },
            };

            fixed (VertexInputAttributeDescription* attributePtr = attributes)
            fixed (DynamicState* dynamicStatePtr = dynamicStates)
            fixed (PipelineShaderStageCreateInfo* stagePtr = stages)
            fixed (PipelineColorBlendAttachmentState* blendPtr = colorBlendAttachments)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = (uint)attributes.Length,
                    PVertexAttributeDescriptions = attributePtr,
                };

                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                    PrimitiveRestartEnable = false,
                };

                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1,
                };

                var raster = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = false,
                    RasterizerDiscardEnable = false,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.CounterClockwise,
                    DepthBiasEnable = false,
                    LineWidth = 1.0f,
                };

                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                    SampleShadingEnable = false,
                };

                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.LessOrEqual,
                    DepthBoundsTestEnable = false,
                    StencilTestEnable = false,
                };

                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    AttachmentCount = 2,
                    PAttachments = blendPtr,
                };

                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)dynamicStates.Length,
                    PDynamicStates = dynamicStatePtr,
                };

                _pipelineLayout = _pipelines.CreateLayout(_descriptors.Layout);

                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = (uint)stages.Length,
                    PStages = stagePtr,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &raster,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };

                _pipeline = _pipelines.CreateGraphicsPipeline(ref pipelineInfo);
            }
        }
        finally
        {
            SilkMarshal.FreeString(entry);
        }
    }

    private void CreateCommandPool()
    {
        var info = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsFamily,
        };

        var result = _vk.CreateCommandPool(_device, &info, null, out _commandPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateCommandPool failed: {result}");

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MaxFramesInFlight,
        };

        var buffers = new CommandBuffer[MaxFramesInFlight];
        fixed (CommandBuffer* p = buffers)
        {
            var allocResult = _vk.AllocateCommandBuffers(_device, &allocInfo, p);
            if (allocResult != Result.Success)
                throw new InvalidOperationException($"vkAllocateCommandBuffers failed: {allocResult}");
        }
        Array.Copy(buffers, _commandBuffers, buffers.Length);
    }

    private void CreateDepthResources()
    {
        _depthImage = _allocator.CreateImage(
            _renderExtent.Width,
            _renderExtent.Height,
            _depthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            "depth buffer");

        _depthView = CreateImageView(_depthImage.Image, _depthFormat, ImageAspectFlags.DepthBit);
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new Framebuffer[_swapchainImageViews.Length];
        _transparentFramebuffers = new Framebuffer[_swapchainImageViews.Length];
        _postFramebuffers = new Framebuffer[_swapchainImageViews.Length];
        for (int i = 0; i < _swapchainImageViews.Length; i++)
        {
            _framebuffers[i] = CreateFramebuffer(_renderPass, _sceneColorView);
            _transparentFramebuffers[i] = CreateFramebuffer(_transparentRenderPass, _sceneColorView);
            _postFramebuffers[i] = _post.CreateFramebuffer(
                _swapchainImageViews[i], _swapchainExtent.Width, _swapchainExtent.Height);
        }
    }

    private void CreateSceneColorResources()
    {
        DestroySceneColorResources();
        _sceneColorImage = _allocator.CreateImage(
            _renderExtent.Width,
            _renderExtent.Height,
            _sceneColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
            "scene colour");
        _sceneColorView = CreateImageView(_sceneColorImage.Image, _sceneColorFormat, ImageAspectFlags.ColorBit);

        _sceneDepthColorImage = _allocator.CreateImage(
            _renderExtent.Width,
            _renderExtent.Height,
            Format.R32Sfloat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            "scene linear depth");
        _sceneDepthColorView = CreateImageView(_sceneDepthColorImage.Image, Format.R32Sfloat,
            ImageAspectFlags.ColorBit);
    }

    private void DestroySceneColorResources()
    {
        if (_sceneColorView.Handle != 0) _vk.DestroyImageView(_device, _sceneColorView, null);
        _sceneColorView = default;
        _sceneColorImage?.Destroy(immediate: true);
        _sceneColorImage = null;
        if (_sceneDepthColorView.Handle != 0) _vk.DestroyImageView(_device, _sceneDepthColorView, null);
        _sceneDepthColorView = default;
        _sceneDepthColorImage?.Destroy(immediate: true);
        _sceneDepthColorImage = null;
    }

    private void CreateTaaResources()
    {
        DestroyTaaResources();
        _taaHistoryImage = _allocator.CreateImage(
            _swapchainExtent.Width,
            _swapchainExtent.Height,
            _swapchainFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            "taa history");
        _taaHistoryView = CreateImageView(_taaHistoryImage.Image, _swapchainFormat, ImageAspectFlags.ColorBit);
        _taaHistoryLayout = ImageLayout.Undefined;
        _taaHistoryValid = false;

        var commandBuffer = BeginSingleTimeCommands();
        TransitionImage(commandBuffer, _taaHistoryImage.Image,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit,
            0, AccessFlags.ShaderReadBit);
        EndSingleTimeCommands(commandBuffer);
        _taaHistoryLayout = ImageLayout.ShaderReadOnlyOptimal;
    }

    private void DestroyTaaResources()
    {
        if (_taaHistoryView.Handle != 0) _vk.DestroyImageView(_device, _taaHistoryView, null);
        _taaHistoryView = default;
        _taaHistoryImage?.Destroy(immediate: true);
        _taaHistoryImage = null;
        _taaHistoryLayout = ImageLayout.Undefined;
        _taaHistoryValid = false;
    }

    private Framebuffer CreateFramebuffer(RenderPass renderPass, ImageView colorView)
    {
        var attachments = new[] { colorView, _sceneDepthColorView, _depthView };
        fixed (ImageView* p = attachments)
        {
            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = (uint)attachments.Length,
                PAttachments = p,
                Width = _renderExtent.Width,
                Height = _renderExtent.Height,
                Layers = 1,
            };

            var result = _vk.CreateFramebuffer(_device, &info, null, out var framebuffer);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateFramebuffer failed: {result}");
            return framebuffer;
        }
    }

    private void CreateRefractionResources()
    {
        DestroyRefractionResources();
        if (!_swapchainState.SupportsTransferSrc)
        {
            Write("screen refraction: swapchain does not support transfer source; using environment refraction");
            return;
        }

        _refractionImage = _allocator.CreateImage(
            _renderExtent.Width,
            _renderExtent.Height,
            _sceneColorFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            "scene colour copy");
        _refractionView = CreateImageView(_refractionImage.Image, _sceneColorFormat, ImageAspectFlags.ColorBit);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0f,
            MaxLod = 0f,
        };
        var result = _vk.CreateSampler(_device, &samplerInfo, null, out _refractionSampler);
        if (result != Result.Success)
            throw new InvalidOperationException($"refraction vkCreateSampler failed: {result}");

        _refractionLayout = ImageLayout.Undefined;
        _descriptors.CreateScreenSet(_refractionView, _refractionSampler);

        var commandBuffer = BeginSingleTimeCommands();
        TransitionImage(commandBuffer, _refractionImage.Image,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit,
            0, AccessFlags.ShaderReadBit);
        EndSingleTimeCommands(commandBuffer);
        _refractionLayout = ImageLayout.ShaderReadOnlyOptimal;

        _screenRefractionEnabled = true;
        Write($"screen refraction: enabled ({_swapchainExtent.Width}x{_swapchainExtent.Height} scene-colour copy)");
    }

    private void DestroyRefractionResources()
    {
        if (_refractionSampler.Handle != 0) _vk.DestroySampler(_device, _refractionSampler, null);
        _refractionSampler = default;
        if (_refractionView.Handle != 0) _vk.DestroyImageView(_device, _refractionView, null);
        _refractionView = default;
        _refractionImage?.Destroy(immediate: true);
        _refractionImage = null;
        _refractionLayout = ImageLayout.Undefined;
        _screenRefractionEnabled = false;
    }

    private void CreateVertexBuffer()
    {
        var vertices = CubeGeometry.Vertices;
        ulong size = (ulong)(vertices.Length * sizeof(Vertex));
        _vertexBuffer = _allocator.CreateBuffer(size,
            BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit, "cube vertices");
        _allocator.UploadBuffer(_vertexBuffer, MemoryMarshal.AsBytes(vertices.AsSpan()));
    }

    private void CreateIndexBuffer()
    {
        var indices = CubeGeometry.Indices;
        ulong size = (ulong)(indices.Length * sizeof(ushort));
        _indexBuffer = _allocator.CreateBuffer(size,
            BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit, "cube indices");
        _allocator.UploadBuffer(_indexBuffer, MemoryMarshal.AsBytes(indices.AsSpan()));
    }

    private void CreateUniformBuffers()
    {
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            _uniformBuffers[i] = _allocator.CreateBuffer(64, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, $"uniform {i}");
            _uniformMapped[i] = _uniformBuffers[i]!.Mapped;
        }
    }

    private void CreateSceneUniformBuffers()
    {
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            _sceneUniformBuffers[i] = _allocator.CreateBuffer(608, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, $"scene uniform {i}");
            _sceneUniformMapped[i] = _sceneUniformBuffers[i]!.Mapped;
        }
    }

    private void CreateScenePipelines()
    {
        var vertBytes = ShaderLibrary.Load("scene.vert");
        var fragBytes = ShaderLibrary.Load("scene.frag");
        var vertModule = _pipelines.GetShaderModule("scene.vert", vertBytes);
        var fragModule = _pipelines.GetShaderModule("scene.frag", fragBytes);
        var entry = SilkMarshal.StringToPtr("main");

        try
        {
            var stages = new[]
            {
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertModule,
                    PName = (byte*)entry,
                },
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragModule,
                    PName = (byte*)entry,
                },
            };

            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = (uint)sizeof(SceneVertex),
                InputRate = VertexInputRate.Vertex,
            };

            var attributes = new[]
            {
                new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
                new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 },
                new VertexInputAttributeDescription { Location = 3, Binding = 0, Format = Format.R32G32Sfloat, Offset = 32 },
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false,
            };

            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
                SampleShadingEnable = false,
            };

            var dynamicStates = new[] { DynamicState.Viewport, DynamicState.Scissor };

            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)sizeof(ScenePushConstants),
            };

            _scenePipelineLayout = _pipelines.CreateLayout(
                new[] { _descriptors.SceneLayout, _descriptors.BindlessLayout, _descriptors.ScreenLayout },
                new[] { pushRange });

            fixed (PipelineShaderStageCreateInfo* stagePtr = stages)
            fixed (VertexInputAttributeDescription* attrPtr = attributes)
            fixed (DynamicState* dynPtr = dynamicStates)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = (uint)attributes.Length,
                    PVertexAttributeDescriptions = attrPtr,
                };

                var dynamic = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)dynamicStates.Length,
                    PDynamicStates = dynPtr,
                };

                _sceneOpaquePipeline = CreateScenePipelineVariant(
                    stagePtr, (uint)stages.Length, &vertexInput, &inputAssembly,
                    &viewportState, &multisample, &dynamic, cullNone: false, transparent: false,
                    renderPass: _renderPass);
                _sceneDoubleSidedPipeline = CreateScenePipelineVariant(
                    stagePtr, (uint)stages.Length, &vertexInput, &inputAssembly,
                    &viewportState, &multisample, &dynamic, cullNone: true, transparent: false,
                    renderPass: _renderPass);
                _sceneTransparentPipeline = CreateScenePipelineVariant(
                    stagePtr, (uint)stages.Length, &vertexInput, &inputAssembly,
                    &viewportState, &multisample, &dynamic, cullNone: true, transparent: true,
                    renderPass: _transparentRenderPass);
            }
        }
        finally
        {
            SilkMarshal.FreeString(entry);
        }
    }

    private Pipeline CreateScenePipelineVariant(
        PipelineShaderStageCreateInfo* stages, uint stageCount,
        PipelineVertexInputStateCreateInfo* vertexInput,
        PipelineInputAssemblyStateCreateInfo* inputAssembly,
        PipelineViewportStateCreateInfo* viewportState,
        PipelineMultisampleStateCreateInfo* multisample,
        PipelineDynamicStateCreateInfo* dynamicState,
        bool cullNone, bool transparent, RenderPass renderPass)
    {
        var raster = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = PolygonMode.Fill,
            CullMode = cullNone ? CullModeFlags.None : CullModeFlags.BackBit,
            FrontFace = FrontFace.CounterClockwise,
            DepthBiasEnable = false,
            LineWidth = 1.0f,
        };

        var depth = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = true,
            DepthWriteEnable = !transparent,
            DepthCompareOp = CompareOp.LessOrEqual,
            DepthBoundsTestEnable = false,
            StencilTestEnable = false,
        };

        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = transparent,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                             ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var linearDepthBlendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = false,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                             ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var blendAttachments = new[] { blendAttachment, linearDepthBlendAttachment };

        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            AttachmentCount = 2,
            PAttachments = null,
        };

        fixed (PipelineColorBlendAttachmentState* blendPtr = blendAttachments)
        {
            colorBlend.PAttachments = blendPtr;
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = stageCount,
                PStages = stages,
                PVertexInputState = vertexInput,
                PInputAssemblyState = inputAssembly,
                PViewportState = viewportState,
                PRasterizationState = &raster,
                PMultisampleState = multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &colorBlend,
                PDynamicState = dynamicState,
                Layout = _scenePipelineLayout,
                RenderPass = renderPass,
                Subpass = 0,
            };

            return _pipelines.CreateGraphicsPipeline(ref info);
        }
    }

    private void UpdateSceneUniforms(int frame)
    {
        var data = new float[152];
        Array.Copy(_camera.ViewProjection, data, 16);
        data[16] = _camera.Position.X;
        data[17] = _camera.Position.Y;
        data[18] = _camera.Position.Z;
        data[19] = 1f;

        var sunDir = _sunInfo.Direction;
        var sunColor = _sunInfo.Color;
        if (_sunInfo.Intensity < 0.01f)
        {
            sunDir = _sunInfo.MoonDirection;
            sunColor = _sunInfo.MoonColor;
        }

        data[16] = _camera.Position.X;
        data[17] = _camera.Position.Y;
        data[18] = _camera.Position.Z;
        data[19] = 1f;
        data[20] = sunDir.X; data[21] = sunDir.Y; data[22] = sunDir.Z; data[23] = 0f;
        data[24] = sunColor.X; data[25] = sunColor.Y; data[26] = sunColor.Z; data[27] = 1f;
        float ambient = (float)_settings.GiAmbientAmount;
        data[28] = ambient; data[29] = ambient; data[30] = ambient; data[31] = 1f;
        data[32] = _whiteMode ? 1f : 0f;
        data[33] = _scene?.LightCount ?? 0;
        data[34] = (float)_settings.LightBrightness;
        data[35] = (float)_settings.FogAmount;

        bool shadows = _scene is { HasGeometry: true } &&
                       _settings.RtSunShadows &&
                       _sunInfo.Intensity > 0.01f;
        if (shadows)
        {
            for (int i = 0; i < ShadowMap.CascadeCount; i++)
                Array.Copy(_shadowMap.Matrices[i], 0, data, 36 + i * 16, 16);
            Array.Copy(_shadowMap.Splits, 0, data, 100, ShadowMap.CascadeCount);
            data[104] = (float)_settings.SunShadowSharpness;
            data[105] = 0.0015f;
            data[106] = _shadowMap.Size;
            data[107] = 1f;
        }

        var forward = new Vec3(-_camera.Rotation[8], -_camera.Rotation[9], -_camera.Rotation[10]);
        data[108] = forward.X; data[109] = forward.Y; data[110] = forward.Z; data[111] = 0f;

        // P4 section planes: xyzd, dot(n, p) + d >= 0 is visible.
        for (int i = 0; i < _sectionPlaneCount; i++)
        {
            int o = 112 + i * 4;
            data[o] = _sectionPlanes[i * 4];
            data[o + 1] = _sectionPlanes[i * 4 + 1];
            data[o + 2] = _sectionPlanes[i * 4 + 2];
            data[o + 3] = _sectionPlanes[i * 4 + 3];
        }
        data[144] = _sectionPlaneCount;
        data[145] = (float)_clock.Elapsed.TotalSeconds; // water animation time
        data[146] = 0f;
        data[147] = 0f;
        data[148] = (float)_settings.FogAmount;
        data[149] = (float)_settings.FogHeight;
        data[150] = 1f;
        data[151] = (float)_clock.Elapsed.TotalSeconds;

        fixed (float* p = data)
            System.Buffer.MemoryCopy(p, (void*)_sceneUniformMapped[frame], 608, 608);
    }

    /// <summary>P4: sets up to 8 world-space clip planes (xyzd; dot(n,p)+d &gt;= 0 is kept).</summary>
    public void SetSectionPlanes(float[] planes)
    {
        if (planes == null) planes = Array.Empty<float>();
        int count = Math.Min(planes.Length / 4, MaxSectionPlanes);
        _sectionPlaneCount = count;
        Array.Clear(_sectionPlanes);
        if (count > 0)
            Array.Copy(planes, _sectionPlanes, count * 4);
        Write($"section: {count} plane(s)");
    }

    public void ApplyWorld(WorldSnapshot world, bool applyCamera = true)
    {
        if (world == null) return;
        if (world.Models.Count == 0)
        {
            Write("scene: ignored empty world");
            return;
        }
        if (world.Revision > 0 && world.Revision <= _lastSceneRevision)
        {
            Write($"scene: ignored duplicate revision {world.Revision}");
            return;
        }

        _lastSceneRevision = world.Revision;
        _latestRequestedRevision = world.Revision;
        if (applyCamera) _camera.Update(world.Camera);
        ApplySun(world.Sun);
        _camera.SetAspect(_renderExtent.Width, _renderExtent.Height);

        Write($"scene: building rev {world.Revision} asynchronously...");
        _ = Task.Run(() =>
        {
            try
            {
                var data = SceneUploadData.Build(world);
                _pendingScenes.Enqueue(data);
            }
            catch (Exception ex)
            {
                Write("scene: CPU build failed: " + ex.Message);
            }
        });
    }

    private void ProcessPendingScenes()
    {
        SceneUploadData? latest = null;
        while (_pendingScenes.TryDequeue(out var data))
        {
            if (data.Revision == 0 || data.Revision >= _latestRequestedRevision)
                latest = data;
        }
        if (latest == null) return;

        _vk.DeviceWaitIdle(_device);
        var old = _scene;
        _scene = new GpuScene(_context, _allocator, _materialBuffer, _lightBuffer, _textures, latest, _camera);
        _camera.SetSceneBounds(_scene.BoundsMin, _scene.BoundsMax);
        old?.Dispose();
        _allocator.CollectGarbage(0);
        _sceneDrawCalls = 0;
        SceneUploaded?.Invoke(latest.Revision);
        Write($"scene: uploaded rev {latest.Revision}, {_scene.Models.Count} models, " +
              $"{_scene.InstanceCount} instances, {_scene.Draws.Count} draws, " +
              $"{_scene.MaterialCount} materials, {_scene.TextureCount} textures");
        Write("scene: draw materials=" + string.Join(",", _scene.Draws.Select(d => d.FrontMaterial)));
        for (int i = 0; i < _scene.MaterialCount; i++)
        {
            var m = _scene.MaterialDtos[i];
            Write($"scene: material[{i}] id={m.Id} name='{m.Name}' rgb=({m.DiffuseR:0.##},{m.DiffuseG:0.##},{m.DiffuseB:0.##}) tex='{m.DiffuseTexture.File}'");
        }
    }

    public void ApplyCamera(CameraUpdate camera)
    {
        _camera.Update(camera);
        _camera.SetAspect(_renderExtent.Width, _renderExtent.Height);
        if (!_cameraModeLogged ||
            camera.Ortho != _lastCameraOrtho ||
            camera.TwoPointPerspective != _lastCameraTwoPoint)
        {
            _cameraModeLogged = true;
            _lastCameraOrtho = camera.Ortho;
            _lastCameraTwoPoint = camera.TwoPointPerspective;
            Write($"camera mode: {(camera.Ortho ? "ortho" : camera.TwoPointPerspective ? "two-point" : "perspective")}");
        }
    }

    public void ApplySun(SunUpdate sun)
    {
        _sun = sun;
        _sunInfo = SunPosition.Compute(sun, (float)_settings.SunPower);
    }

    public double MaterialUpdateMilliseconds { get; private set; }

    public void OverwriteMaterial(MaterialDto material)
    {
        if (_scene == null) return;
        if (!_scene.MaterialIndexById.TryGetValue(material.Id, out var index)) return;

        var timer = Stopwatch.StartNew();
        // A texture-set change needs a device idle before descriptor updates. A pure parameter
        // edit is a single coherent SSBO write; P2.5 will double-buffer materials for fully
        // tear-free updates, but this keeps live color/roughness edits off the stall path.
        bool newTextures =
            _textures.NeedsLoad(material.DiffuseTexture.File, srgb: true) ||
            _textures.NeedsLoad(material.BumpTexture.File, srgb: false) ||
            _textures.NeedsLoad(material.RoughnessTexture.File, srgb: false) ||
            _textures.NeedsLoad(material.MaskTexture.File, srgb: false);
        if (newTextures) _vk.DeviceWaitIdle(_device);
        _scene.MaterialDtos[index] = material;
        int diffuse = _textures.Load(material.DiffuseTexture.File, srgb: true);
        int bump = _textures.Load(material.BumpTexture.File, srgb: false);
        int rough = _textures.Load(material.RoughnessTexture.File, srgb: false);
        int mask = _textures.Load(material.MaskTexture.File, srgb: false);
        _materialBuffer.Set(index, MaterialMapper.Build(material, diffuse, bump, rough, mask));

        for (int i = 0; i < _scene.Draws.Count; i++)
        {
            var draw = _scene.Draws[i];
            if (draw.FrontMaterial != index && draw.BackMaterial != index) continue;
            bool doubleSided = material.DoubleSided || draw.BackMaterial != draw.FrontMaterial;
            bool transparent = material.Opacity < 0.999f || material.IsSolidGlass || material.MaterialType == 2;
            _scene.Draws[i] = new SceneDraw(draw.ModelIndex, draw.Matrix, draw.StartIndex, draw.IndexCount,
                draw.FrontMaterial, draw.BackMaterial, doubleSided, transparent, draw.Distance,
                draw.Center, draw.Radius);
        }
        MaterialUpdateMilliseconds = timer.Elapsed.TotalMilliseconds;
        Write($"scene: material {material.Id} updated at index {index} in {MaterialUpdateMilliseconds:0.##} ms");
    }

    public string SceneStats => _scene == null
        ? "no scene"
        : $"{_scene.Models.Count} models, {_scene.InstanceCount} instances, {_scene.Draws.Count} draws, " +
          $"{_scene.MaterialCount} materials, {_scene.TextureCount} textures, " +
          $"{_scene.VertexBytes + _scene.IndexBytes} geometry bytes";
    public int SceneDrawCalls => _sceneDrawCalls;
    public int SceneMaterialCount => _scene?.MaterialCount ?? 0;
    public int SceneTextureCount => _scene?.TextureCount ?? 0;
    public int SceneLightCount => _scene?.LightCount ?? 0;
    public float SunElevationDegrees => _sunInfo.Elevation * 180f / MathF.PI;
    public float SunAzimuthDegrees
    {
        get
        {
            float azimuth = MathF.Atan2(_sunInfo.Direction.X, -_sunInfo.Direction.Z) * 180f / MathF.PI;
            return azimuth < 0 ? azimuth + 360f : azimuth;
        }
    }
    public float SunIntensity => _sunInfo.Intensity;

    private void CreateSyncObjects()
    {
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailable[i]) != Result.Success)
                throw new InvalidOperationException("vkCreateSemaphore failed.");
            if (_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinished[i]) != Result.Success)
                throw new InvalidOperationException("vkCreateSemaphore failed.");
            if (_vk.CreateFence(_device, &fenceInfo, null, out _inFlight[i]) != Result.Success)
                throw new InvalidOperationException("vkCreateFence failed.");
        }
    }

    // ------------------------------------------------------------------
    // Frame loop
    // ------------------------------------------------------------------

    public bool RenderFrame()
    {
        if (_disposed || !IsInitialized) return false;

        ProcessPendingScenes();

        if (_framebufferResized)
        {
            RecreateSwapchain();
            _framebufferResized = false;
            if (_swapchain.Handle == 0 || _swapchainExtent.Width == 0 || _swapchainExtent.Height == 0)
                return false;
        }

        // Minimized windows have no surface extent; skip frames until a resize recreates the swapchain.
        if (_swapchain.Handle == 0) return false;

        var fence = _inFlight[_currentFrame];
        _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue);

        uint imageIndex;
        var acquire = _khrSwapchain.AcquireNextImage(
            _device, _swapchain, ulong.MaxValue, _imageAvailable[_currentFrame], default, &imageIndex);

        if (acquire == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return false;
        }
        if (acquire != Result.Success && acquire != Result.SuboptimalKhr)
            throw new InvalidOperationException($"vkAcquireNextImageKHR failed: {acquire}");

        _vk.ResetFences(_device, 1, &fence);

        UpdateUniformBuffer(_currentFrame);
        if (_scene is { HasGeometry: true } && _settings.RtSunShadows && _sunInfo.Intensity > 0.01f)
            _shadowMap.UpdateCascades(_camera, _sunInfo.Direction, 120f);
        if (_taaHistoryValid)
        {
            float jx = (Halton(FrameCount + 1, 2) - 0.5f) * 2f / _renderExtent.Width;
            float jy = (Halton(FrameCount + 1, 3) - 0.5f) * 2f / _renderExtent.Height;
            _camera.SetJitter(jx, jy);
        }
        else
        {
            _camera.SetJitter(0f, 0f);
        }
        UpdateSceneUniforms(_currentFrame);
        RecordCommandBuffer(_commandBuffers[_currentFrame], imageIndex);

        var waitSemaphore = _imageAvailable[_currentFrame];
        var signalSemaphore = _renderFinished[_currentFrame];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var commandBuffer = _commandBuffers[_currentFrame];

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        var submitResult = _vk.QueueSubmit(_graphicsQueue, 1, &submit, fence);
        if (submitResult != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit failed: {submitResult}");

        var swapchain = _swapchain;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        var presentResult = _khrSwapchain.QueuePresent(_presentQueue, &present);
        if (presentResult == Result.ErrorOutOfDateKhr || presentResult == Result.SuboptimalKhr)
        {
            _framebufferResized = true;
        }
        else if (presentResult != Result.Success)
        {
            throw new InvalidOperationException($"vkQueuePresentKHR failed: {presentResult}");
        }

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
        FrameCount++;
        return true;
    }

    private void UpdateUniformBuffer(int frame)
    {
        float time = (float)_clock.Elapsed.TotalSeconds;
        float aspect = _swapchainExtent.Width / (float)_swapchainExtent.Height;

        var model = Mat4.RotateY(time * 0.7f);
        model = Mat4.Multiply(Mat4.RotateX(time * 0.31f), model);
        var view = Mat4.LookAt(new Vec3(2.4f, 1.9f, 2.4f), new Vec3(0, 0, 0), new Vec3(0, 1, 0));
        var projection = Mat4.Perspective(60f * MathF.PI / 180f, aspect, 0.1f, 100f);
        var mvp = Mat4.Multiply(projection, Mat4.Multiply(view, model));

        fixed (float* p = mvp)
            System.Buffer.MemoryCopy(p, (void*)_uniformMapped[frame], 64, 64);
    }

    private void RecordCommandBuffer(CommandBuffer commandBuffer, uint imageIndex)
    {
        _sceneDrawCalls = 0;
        _vk.ResetCommandBuffer(commandBuffer, 0);

        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        _vk.BeginCommandBuffer(commandBuffer, &begin);

        _sky.Update(_currentFrame, _camera, _sunInfo, _settings, _whiteMode,
            (float)_clock.Elapsed.TotalSeconds);
        _frameGraph.Reset();
        bool shadows = _scene is { HasGeometry: true } &&
                       _settings.RtSunShadows &&
                       _sunInfo.Intensity > 0.01f;
        if (shadows)
        {
            _frameGraph.AddPass("shadows", cmd => _shadowMap.Render(cmd, _scene!, _descriptors.SceneSets[_currentFrame]));
            _frameGraph.AddPass("shadow-read", _ => { },
                new ImageTransition(_shadowMap.Image,
                    ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.LateFragmentTestsBit, PipelineStageFlags.FragmentShaderBit,
                    AccessFlags.DepthStencilAttachmentWriteBit, AccessFlags.ShaderReadBit,
                    ImageAspectFlags.DepthBit, ShadowMap.CascadeCount));
        }
        _frameGraph.AddPass("opaque",
            cmd => RecordOpaquePass(cmd, imageIndex),
            new ImageTransition(_sceneColorImage!.Image,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                0, AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ImageAspectFlags.ColorBit),
            new ImageTransition(_sceneDepthColorImage!.Image,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.ColorAttachmentOutputBit,
                0, AccessFlags.ColorAttachmentWriteBit,
                ImageAspectFlags.ColorBit),
            new ImageTransition(_depthImage!.Image,
                ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal,
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                0, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ImageAspectFlags.DepthBit));

        if (_screenRefractionEnabled)
        {
            _frameGraph.AddPass("refraction-copy",
                cmd => RecordRefractionCopy(cmd, _sceneColorImage!.Image));
        }

        _frameGraph.AddPass("transparent",
            cmd => RecordTransparentPass(cmd, imageIndex));

        _frameGraph.AddPass("post",
            cmd => RecordPostPass(cmd, imageIndex),
            new ImageTransition(_sceneColorImage!.Image,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit,
                AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit,
                ImageAspectFlags.ColorBit),
            new ImageTransition(_sceneDepthColorImage!.Image,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit,
                AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit,
                ImageAspectFlags.ColorBit),
            new ImageTransition(_swapchainImages[imageIndex],
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.ColorAttachmentOutputBit,
                0, AccessFlags.ColorAttachmentWriteBit,
                ImageAspectFlags.ColorBit));

        _frameGraph.AddPass("taa-copy", cmd => RecordTaaCopy(cmd, imageIndex));

        _frameGraph.AddPass("present",
            _ => { },
            new ImageTransition(_swapchainImages[imageIndex],
                ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.BottomOfPipeBit,
                AccessFlags.ColorAttachmentWriteBit, 0, ImageAspectFlags.ColorBit));

        _frameGraph.Execute(commandBuffer);
        _vk.EndCommandBuffer(commandBuffer);
    }

    private void RecordOpaquePass(CommandBuffer commandBuffer, uint imageIndex) =>
        RecordRenderPass(commandBuffer, _framebuffers[imageIndex], _renderExtent, transparent: false);

    private void RecordTransparentPass(CommandBuffer commandBuffer, uint imageIndex) =>
        RecordRenderPass(commandBuffer, _transparentFramebuffers[imageIndex], _renderExtent, transparent: true);

    private void RecordPostPass(CommandBuffer commandBuffer, uint imageIndex)
    {
        UpdatePostParams(_currentFrame);
        _context.BeginLabel(commandBuffer, "post pass");
        _post.Record(commandBuffer, _currentFrame, _postFramebuffers[imageIndex], _swapchainExtent);
        _context.EndLabel(commandBuffer);
    }

    private void UpdatePostParams(int frame) =>
        _post.Update(frame, _sceneColorView, _taaHistoryView, _sceneDepthColorView,
            BuildPostParams(immediate: false));

    private PostParams BuildPostParams(bool immediate = false)
    {
        float exposure;
        if (_camera.AutoExposure)
        {
            float target = Math.Clamp(0.9f / MathF.Max(0.5f,
                _sunInfo.Intensity * 0.25f + (float)_settings.GiAmbientAmount * 0.10f), 0.5f, 2.5f);
            if (immediate)
            {
                _smoothedExposure = target;
            }
            else
            {
                float k = Math.Clamp((float)_settings.ExposureAdaptionSpeed * 0.08f, 0.01f, 1f);
                _smoothedExposure += (target - _smoothedExposure) * k;
            }
            exposure = _smoothedExposure;
        }
        else
        {
            exposure = MathF.Pow(2f, _camera.Exposure);
        }

        float temperature = Math.Clamp((10000f - (float)_settings.ColorTemperature) / 8000f, 0f, 1f);

        var sunWorld = _camera.Position + _sunInfo.Direction * 100f;
        var m = _camera.ViewProjection;
        float x = m[0] * sunWorld.X + m[4] * sunWorld.Y + m[8] * sunWorld.Z + m[12];
        float y = m[1] * sunWorld.X + m[5] * sunWorld.Y + m[9] * sunWorld.Z + m[13];
        float w = m[3] * sunWorld.X + m[7] * sunWorld.Y + m[11] * sunWorld.Z + m[15];
        float sunX = 0.5f, sunY = 0.5f, sunStrength = 0f;
        if (MathF.Abs(w) > 1e-5f)
        {
            sunX = x / w * 0.5f + 0.5f;
            sunY = y / w * 0.5f + 0.5f;
            var forward = new Vec3(-_camera.Rotation[8], -_camera.Rotation[9], -_camera.Rotation[10]);
            sunStrength = MathF.Max(0f, Vec3.Dot(forward, _sunInfo.Direction));
        }

        var parameters = new PostParams
        {
            Exposure = exposure,
            Bloom = (float)_settings.BloomIntensity,
            Saturation = (float)_settings.Saturation,
            Contrast = 0f,
            Temperature = temperature,
            Vignette = (float)_settings.Vignette,
            ChromaticAberration = (float)_settings.ChromaticAberration,
            MotionBlur = (float)_settings.MotionBlur,
            SafeFrame = _settings.ShowSafeFrame ? 1f : 0f,
            LensFlare = (float)_settings.LensFlare,
            Time = (float)_clock.Elapsed.TotalSeconds,
            AutoContrast = _settings.AutoContrast ? 1f : 0f,
            SunX = sunX,
            SunY = sunY,
            SunStrength = sunStrength,
            Taa = (!immediate && _taaHistoryValid && !_settings.ShowSafeFrame) ? 0.85f : 0f,
            SaoStrength = (float)_settings.SsaoStrength,
            SaoRadius = (float)_settings.SsaoRadius,
            Near = _camera.Near,
            Far = _camera.Far,
            GiStrength = (float)_settings.SsgiStrength,
            GiRadius = (float)_settings.SsgiRadius,
            GiFalloff = (float)_settings.SsgiFalloff,
            GiPad = _settings.RenderScale < 0.999 ? 1f : 0f,
            Projection = _camera.Projection,
            InvProjection = Mat4.Invert(_camera.Projection),
        };
        return parameters;
    }

    private void RecordRenderPass(CommandBuffer commandBuffer, Framebuffer framebuffer, Extent2D extent,
        bool transparent)
    {
        _context.BeginLabel(commandBuffer, transparent ? "transparent pass" : "opaque pass");
        var clearValues = new[]
        {
            new ClearValue { Color = new ClearColorValue { Float32_0 = 0.055f, Float32_1 = 0.043f, Float32_2 = 0.105f, Float32_3 = 1.0f } },
            new ClearValue { Color = new ClearColorValue { Float32_0 = 0f, Float32_1 = 0f, Float32_2 = 0f, Float32_3 = 0f } },
            new ClearValue { DepthStencil = new ClearDepthStencilValue { Depth = 1.0f, Stencil = 0 } },
        };

        fixed (ClearValue* clearPtr = clearValues)
        {
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = transparent ? _transparentRenderPass : _renderPass,
                Framebuffer = framebuffer,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = extent,
                },
                ClearValueCount = transparent ? 0u : (uint)clearValues.Length,
                PClearValues = transparent ? null : clearPtr,
            };

            _vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);
        }

        if (!transparent && _scene is { HasGeometry: true })
            _sky.Record(commandBuffer, _currentFrame, extent);

        BindScene(commandBuffer, _currentFrame, transparent);
        _vk.CmdEndRenderPass(commandBuffer);
        _context.EndLabel(commandBuffer);
    }

    private void BindScene(CommandBuffer commandBuffer, int frame, bool transparentPass)
    {
        if (_scene is { HasGeometry: true })
            RecordGpuScene(commandBuffer, frame, transparentPass);
        else if (!transparentPass)
            BindCubeScene(commandBuffer, frame);
    }

    private void RecordRefractionCopy(CommandBuffer commandBuffer, Image sourceColor)
    {
        if (!_screenRefractionEnabled || _refractionImage == null) return;

        var oldLayout = _refractionLayout;
        var srcStage = oldLayout == ImageLayout.Undefined
            ? PipelineStageFlags.TopOfPipeBit
            : PipelineStageFlags.FragmentShaderBit;
        var srcAccess = oldLayout == ImageLayout.Undefined ? 0 : AccessFlags.ShaderReadBit;

        TransitionImage(commandBuffer, sourceColor,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        TransitionImage(commandBuffer, _refractionImage.Image,
            oldLayout, ImageLayout.TransferDstOptimal,
            srcStage, PipelineStageFlags.TransferBit,
            srcAccess, AccessFlags.TransferWriteBit);

        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1,
            },
            SrcOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
            DstOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
            Extent = new Extent3D
            {
                Width = _renderExtent.Width, Height = _renderExtent.Height, Depth = 1,
            },
        };
        _vk.CmdCopyImage(commandBuffer, sourceColor, ImageLayout.TransferSrcOptimal,
            _refractionImage.Image, ImageLayout.TransferDstOptimal, 1, &copy);

        TransitionImage(commandBuffer, sourceColor,
            ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentWriteBit);
        TransitionImage(commandBuffer, _refractionImage.Image,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        _refractionLayout = ImageLayout.ShaderReadOnlyOptimal;
    }

    private void RecordTaaCopy(CommandBuffer commandBuffer, uint imageIndex)
    {
        if (_taaHistoryImage == null) return;

        TransitionImage(commandBuffer, _swapchainImages[imageIndex],
            ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        TransitionImage(commandBuffer, _taaHistoryImage.Image,
            _taaHistoryLayout, ImageLayout.TransferDstOptimal,
            PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit, AccessFlags.TransferWriteBit);

        var copy = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1,
            },
            SrcOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
            DstOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
            Extent = new Extent3D
            {
                Width = _swapchainExtent.Width, Height = _swapchainExtent.Height, Depth = 1,
            },
        };
        _vk.CmdCopyImage(commandBuffer, _swapchainImages[imageIndex], ImageLayout.TransferSrcOptimal,
            _taaHistoryImage.Image, ImageLayout.TransferDstOptimal, 1, &copy);

        TransitionImage(commandBuffer, _swapchainImages[imageIndex],
            ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.TransferReadBit, AccessFlags.ColorAttachmentWriteBit);
        TransitionImage(commandBuffer, _taaHistoryImage.Image,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        _taaHistoryLayout = ImageLayout.ShaderReadOnlyOptimal;
        _taaHistoryValid = true;
        if (!_taaLogged)
        {
            _taaLogged = true;
            Write("taa: history active (jitter + temporal blend)");
        }
    }

    private void TransitionImage(CommandBuffer commandBuffer, Image image,
        ImageLayout oldLayout, ImageLayout newLayout,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage,
        AccessFlags srcAccess, AccessFlags dstAccess)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        _vk.CmdPipelineBarrier(commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private void BindCubeScene(CommandBuffer commandBuffer, int frame)
    {
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);

        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = _swapchainExtent.Width,
            Height = _swapchainExtent.Height,
            MinDepth = 0,
            MaxDepth = 1,
        };
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);

        var scissor = new Rect2D
        {
            Offset = new Offset2D { X = 0, Y = 0 },
            Extent = _swapchainExtent,
        };
        _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

        var vertexBuffer = _vertexBuffer!.Buffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);
        _vk.CmdBindIndexBuffer(commandBuffer, _indexBuffer!.Buffer, 0, IndexType.Uint16);

        var descriptorSet = _descriptors.Sets[frame];
        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &descriptorSet, 0, null);

        _vk.CmdDrawIndexed(commandBuffer, (uint)CubeGeometry.Indices.Length, 1, 0, 0, 0);
    }

    private void RecordGpuScene(CommandBuffer commandBuffer, int frame, bool transparentPass)
    {
        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = _swapchainExtent.Width,
            Height = _swapchainExtent.Height,
            MinDepth = 0,
            MaxDepth = 1,
        };
        _vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);

        var scissor = new Rect2D
        {
            Offset = new Offset2D { X = 0, Y = 0 },
            Extent = _swapchainExtent,
        };
        _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

        var sceneSet = _descriptors.SceneSets[frame];
        var bindlessSet = _descriptors.BindlessSet;
        var screenSet = _descriptors.ScreenSet;
        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _scenePipelineLayout,
            0, 1, &sceneSet, 0, null);
        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _scenePipelineLayout,
            1, 1, &bindlessSet, 0, null);
        if (screenSet.Handle != 0)
            _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _scenePipelineLayout,
                2, 1, &screenSet, 0, null);

        var draws = _scene!.Draws;
        var planes = BuildFrustumPlanes(_camera.ViewProjection);
        if (!transparentPass)
        {
            int visible = 0;
            for (int i = 0; i < draws.Count; i++)
            {
                if (!draws[i].Transparent &&
                    SphereInFrustum(draws[i].Center, draws[i].Radius, planes))
                {
                    visible++;
                    DrawSceneItem(commandBuffer, draws[i], screenRefraction: false);
                }
            }
            if (visible == 0 && draws.Count > 0 && !_cullWarned)
            {
                _cullWarned = true;
                Write($"cull: all {draws.Count} draws culled (cam {_camera.Position.X:0.#}," +
                      $"{_camera.Position.Y:0.#},{_camera.Position.Z:0.#}, extent {_renderExtent.Width}x{_renderExtent.Height})");
            }
            return;
        }

        // P4: transparent draws are sorted per frame by distance from the current camera.
        // P6: both opaque and transparent draws are frustum-culled.
        var cameraPos = _camera.Position;
        var transparent = draws
            .Where(d => d.Transparent && SphereInFrustum(d.Center, d.Radius, planes))
            .OrderByDescending(d => DistanceSquared(d.Center, cameraPos));
        foreach (var draw in transparent)
            DrawSceneItem(commandBuffer, draw, screenRefraction: _screenRefractionEnabled);
    }

    private static float DistanceSquared(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>P6: six normalized frustum planes from a column-major view-projection matrix.</summary>
    private static float[] BuildFrustumPlanes(float[] m)
    {
        var planes = new float[24];
        void Set(int index, float a, float b, float c, float d)
        {
            float length = MathF.Sqrt(a * a + b * b + c * c);
            if (length < 1e-6f) length = 1f;
            planes[index * 4] = a / length;
            planes[index * 4 + 1] = b / length;
            planes[index * 4 + 2] = c / length;
            planes[index * 4 + 3] = d / length;
        }
        // Rows: r0=(m0,m4,m8,m12), r1=(m1,m5,m9,m13), r2=(m2,m6,m10,m14), r3=(m3,m7,m11,m15).
        Set(0, m[3] + m[0], m[7] + m[4], m[11] + m[8], m[15] + m[12]);   // left
        Set(1, m[3] - m[0], m[7] - m[4], m[11] - m[8], m[15] - m[12]);   // right
        Set(2, m[3] + m[1], m[7] + m[5], m[11] + m[9], m[15] + m[13]);   // bottom
        Set(3, m[3] - m[1], m[7] - m[5], m[11] - m[9], m[15] - m[13]);   // top
        Set(4, m[2], m[6], m[10], m[14]);                                 // near
        Set(5, m[3] - m[2], m[7] - m[6], m[11] - m[10], m[15] - m[14]);   // far
        return planes;
    }

    private static bool SphereInFrustum(Vec3 center, float radius, float[] planes)
    {
        for (int i = 0; i < 6; i++)
        {
            int o = i * 4;
            float distance = planes[o] * center.X + planes[o + 1] * center.Y +
                             planes[o + 2] * center.Z + planes[o + 3];
            if (distance < -radius) return false;
        }
        return true;
    }

    private static float Halton(ulong index, int baseValue)
    {
        float result = 0f;
        float f = 1f / baseValue;
        while (index > 0)
        {
            result += f * (index % (ulong)baseValue);
            index /= (ulong)baseValue;
            f /= baseValue;
        }
        return result;
    }

    private static float HalfToFloat(ushort value) => (float)BitConverter.UInt16BitsToHalf(value);

    private void DrawSceneItem(CommandBuffer commandBuffer, SceneDraw draw, bool screenRefraction)
    {
        var model = _scene!.Models[draw.ModelIndex];
        var pipeline = draw.Transparent
            ? _sceneTransparentPipeline
            : draw.DoubleSided ? _sceneDoubleSidedPipeline : _sceneOpaquePipeline;

        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

        var vertexBuffer = model.Vertex.Buffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);
        _vk.CmdBindIndexBuffer(commandBuffer, model.Index.Buffer, 0, IndexType.Uint32);

        var push = new ScenePushConstants
        {
            FrontMaterial = draw.FrontMaterial,
            BackMaterial = draw.BackMaterial,
            Flags = screenRefraction ? PushFlagScreenRefraction : 0,
        };
        fixed (float* src = draw.Matrix)
            System.Buffer.MemoryCopy(src, &push, 64, 64);

        _vk.CmdPushConstants(commandBuffer, _scenePipelineLayout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0, (uint)sizeof(ScenePushConstants), &push);

        _vk.CmdDrawIndexed(commandBuffer, draw.IndexCount, 1, draw.StartIndex, 0, 0);
        _sceneDrawCalls++;
    }

    // ------------------------------------------------------------------
    // Capture
    // ------------------------------------------------------------------

    public bool Capture(string path) => CaptureInternal(path, 0, 0);

    /// <summary>P5: captures at an explicit resolution (0/negative = window size × capture scale).</summary>
    public bool Capture(string path, int width, int height) =>
        CaptureInternal(path, (uint)Math.Max(0, width), (uint)Math.Max(0, height));

    private bool CaptureInternal(string path, uint width, uint height)
    {
        if (_disposed || !IsInitialized) return false;
        ProcessPendingScenes();
        _vk.DeviceWaitIdle(_device);

        if (width == 0 || height == 0)
        {
            float scale = Math.Clamp((float)_settings.CaptureResolutionScale, 0.25f, 4f);
            width = Math.Max(1u, (uint)(_swapchainExtent.Width * scale));
            height = Math.Max(1u, (uint)(_swapchainExtent.Height * scale));
        }
        if (width == 0 || height == 0) return false;

        // Captures can be a different size than the swapchain; the screen-colour copy is
        // swapchain-sized, so use the environment refraction fallback for captures.
        bool savedScreenRefraction = _screenRefractionEnabled;
        _screenRefractionEnabled = false;
        float savedJitterX = _camera.JitterX;
        float savedJitterY = _camera.JitterY;
        _camera.SetJitter(0f, 0f);

        var colorTransient = _frameGraph.GetTransientImage("capture-color", width, height, _sceneColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit);
        var depthTransient = _frameGraph.GetTransientImage("capture-depth", width, height, _depthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit);
        var depthColorTransient = _frameGraph.GetTransientImage("capture-linear-depth", width, height, Format.R32Sfloat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit);
        var postTransient = _frameGraph.GetTransientImage("capture-post", width, height, _swapchainFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit);

        Framebuffer opaqueFramebuffer = CreateCaptureFramebuffer(
            _renderPass, colorTransient.View, depthColorTransient.View, depthTransient.View, width, height);
        Framebuffer transparentFramebuffer = CreateCaptureFramebuffer(
            _transparentRenderPass, colorTransient.View, depthColorTransient.View, depthTransient.View, width, height);
        Framebuffer postFramebuffer = default;
        bool hdrExr = string.Equals(Path.GetExtension(path), ".exr", StringComparison.OrdinalIgnoreCase)
                      && _sceneColorFormat == Format.R16G16B16A16Sfloat;
        if (!hdrExr)
            postFramebuffer = _post.CreateFramebuffer(postTransient.View, width, height);

        _camera.SetAspect(width, height);

        ulong bufferSize = hdrExr
            ? (ulong)width * height * 8
            : (ulong)width * height * 4;
        var readbackAllocation = _allocator.CreateBuffer(bufferSize, BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, "capture readback");

        if (_scene is { HasGeometry: true } && _settings.RtSunShadows && _sunInfo.Intensity > 0.01f)
            _shadowMap.UpdateCascades(_camera, _sunInfo.Direction, 120f);
        UpdateSceneUniforms(_currentFrame);
        _sky.Update(_currentFrame, _camera, _sunInfo, _settings, _whiteMode,
            (float)_clock.Elapsed.TotalSeconds);
        var commandBuffer = BeginSingleTimeCommands();
        var extent = new Extent2D { Width = width, Height = height };

        _frameGraph.Reset();
        _frameGraph.AddPass("capture-opaque",
            cmd => RecordRenderPass(cmd, opaqueFramebuffer, extent, transparent: false),
            new ImageTransition(colorTransient.Allocation.Image,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                0, AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ImageAspectFlags.ColorBit),
            new ImageTransition(depthColorTransient.Allocation.Image,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.ColorAttachmentOutputBit,
                0, AccessFlags.ColorAttachmentWriteBit,
                ImageAspectFlags.ColorBit),
            new ImageTransition(depthTransient.Allocation.Image,
                ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal,
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                0, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                ImageAspectFlags.DepthBit));

        if (_screenRefractionEnabled)
        {
            _frameGraph.AddPass("capture-refraction",
                cmd => RecordRefractionCopy(cmd, colorTransient.Allocation.Image));
        }

        _frameGraph.AddPass("capture-transparent",
            cmd => RecordRenderPass(cmd, transparentFramebuffer, extent, transparent: true));

        if (hdrExr)
        {
            _frameGraph.AddPass("capture-hdr-copy",
                cmd =>
                {
                    var copy = new BufferImageCopy
                    {
                        BufferOffset = 0,
                        BufferRowLength = 0,
                        BufferImageHeight = 0,
                        ImageSubresource = new ImageSubresourceLayers
                        {
                            AspectMask = ImageAspectFlags.ColorBit,
                            MipLevel = 0,
                            BaseArrayLayer = 0,
                            LayerCount = 1,
                        },
                        ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                        ImageExtent = new Extent3D { Width = width, Height = height, Depth = 1 },
                    };
                    _vk.CmdCopyImageToBuffer(cmd, colorTransient.Allocation.Image,
                        ImageLayout.TransferSrcOptimal, readbackAllocation.Buffer, 1, &copy);
                },
                new ImageTransition(colorTransient.Allocation.Image,
                    ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
                    PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
                    AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit,
                    ImageAspectFlags.ColorBit));
        }
        else
        {
            _frameGraph.AddPass("capture-post",
                cmd =>
                {
                    _post.Update(_currentFrame, colorTransient.View, _taaHistoryView, depthColorTransient.View,
                        BuildPostParams(immediate: true));
                    _post.Record(cmd, _currentFrame, postFramebuffer, extent);
                },
                new ImageTransition(colorTransient.Allocation.Image,
                    ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit,
                    AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit,
                    ImageAspectFlags.ColorBit),
                new ImageTransition(depthColorTransient.Allocation.Image,
                    ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit,
                    AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit,
                    ImageAspectFlags.ColorBit),
                new ImageTransition(postTransient.Allocation.Image,
                    ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                    PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.ColorAttachmentOutputBit,
                    0, AccessFlags.ColorAttachmentWriteBit,
                    ImageAspectFlags.ColorBit));

            _frameGraph.AddPass("capture-copy",
                cmd =>
                {
                    var copy = new BufferImageCopy
                    {
                        BufferOffset = 0,
                        BufferRowLength = 0,
                        BufferImageHeight = 0,
                        ImageSubresource = new ImageSubresourceLayers
                        {
                            AspectMask = ImageAspectFlags.ColorBit,
                            MipLevel = 0,
                            BaseArrayLayer = 0,
                            LayerCount = 1,
                        },
                        ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                        ImageExtent = new Extent3D { Width = width, Height = height, Depth = 1 },
                    };
                    _vk.CmdCopyImageToBuffer(cmd, postTransient.Allocation.Image,
                        ImageLayout.TransferSrcOptimal, readbackAllocation.Buffer, 1, &copy);
                },
                new ImageTransition(postTransient.Allocation.Image,
                    ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
                    PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
                    AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit,
                    ImageAspectFlags.ColorBit));
        }

        _frameGraph.Execute(commandBuffer);
        EndSingleTimeCommands(commandBuffer);
        if (hdrExr)
        {
            var halfPixels = new byte[(int)bufferSize];
            Marshal.Copy(readbackAllocation.Mapped, halfPixels, 0, halfPixels.Length);
            var rgb = new float[width * height * 3];
            for (int i = 0; i < width * height; i++)
            {
                int o = i * 8;
                rgb[i * 3] = HalfToFloat(BitConverter.ToUInt16(halfPixels, o));
                rgb[i * 3 + 1] = HalfToFloat(BitConverter.ToUInt16(halfPixels, o + 2));
                rgb[i * 3 + 2] = HalfToFloat(BitConverter.ToUInt16(halfPixels, o + 4));
            }
            ExrWriter.WriteFloatRgb(path, (int)width, (int)height, rgb);
        }
        else
        {
            var pixels = new byte[(int)bufferSize];
            Marshal.Copy(readbackAllocation.Mapped, pixels, 0, pixels.Length);
            CaptureWriter.Write(path, (int)width, (int)height, pixels);
        }

        _vk.DestroyFramebuffer(_device, opaqueFramebuffer, null);
        _vk.DestroyFramebuffer(_device, transparentFramebuffer, null);
        if (postFramebuffer.Handle != 0) _vk.DestroyFramebuffer(_device, postFramebuffer, null);
        readbackAllocation.Destroy(immediate: true);
        _allocator.CollectGarbage(0);
        _camera.SetAspect(_renderExtent.Width, _renderExtent.Height);
        _camera.SetJitter(savedJitterX, savedJitterY);
        _screenRefractionEnabled = savedScreenRefraction;

        Write($"Captured {width}x{height} to '{path}'.");
        return true;
    }

    private static readonly (Vec3 Forward, Vec3 Up)[] PanoramaFaces =
    {
        (new Vec3(1, 0, 0), new Vec3(0, 1, 0)),
        (new Vec3(-1, 0, 0), new Vec3(0, 1, 0)),
        (new Vec3(0, 1, 0), new Vec3(0, 0, -1)),
        (new Vec3(0, -1, 0), new Vec3(0, 0, 1)),
        (new Vec3(0, 0, 1), new Vec3(0, 1, 0)),
        (new Vec3(0, 0, -1), new Vec3(0, 1, 0)),
    };

    /// <summary>
    /// P5 panorama: renders six 90-degree faces and stitches them into an equirectangular image.
    /// </summary>
    public bool CapturePanorama(string path, int width, int height)
    {
        if (_disposed || !IsInitialized) return false;
        if (width <= 0) width = 2048;
        if (height <= 0) height = width / 2;
        int faceSize = Math.Clamp(width / 4, 256, 1024);

        var savedPosition = _camera.Position;
        var savedRotation = _camera.Rotation;
        var savedViewRotation = _camera.ViewRotation;
        float savedFov = _camera.FovDegrees;
        int savedFovType = _camera.FovType;
        bool savedOrtho = _camera.Ortho;
        float savedLeft = _camera.OrthoLeft, savedRight = _camera.OrthoRight;
        float savedBottom = _camera.OrthoBottom, savedTop = _camera.OrthoTop;
        bool savedTwoPoint = _camera.TwoPointPerspective;
        bool savedAutoExposure = _camera.AutoExposure;
        float savedExposure = _camera.Exposure;

        var tempDir = Path.Combine(Path.GetTempPath(), "powerpotion_pano_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var faces = new byte[6][];

        try
        {
            for (int i = 0; i < PanoramaFaces.Length; i++)
            {
                var (forward, up) = PanoramaFaces[i];
                _camera.Ortho = false;
                _camera.TwoPointPerspective = false;
                _camera.FovDegrees = 90f;
                _camera.FovType = 1;
                _camera.Rotation = CameraState.LookAtRotation(
                    _camera.Position, _camera.Position + forward, up);
                _camera.SetAspect((uint)faceSize, (uint)faceSize);
                _camera.Rebuild();

                var facePath = Path.Combine(tempDir, $"face{i}.png");
                if (!Capture(facePath, faceSize, faceSize)) return false;
                faces[i] = LoadBgra(facePath, faceSize, faceSize);
            }

            var equirect = StitchEquirect(faces, faceSize, width, height);
            CaptureWriter.Write(path, width, height, equirect);
            Write($"Captured panorama {width}x{height} to '{path}'.");
            return true;
        }
        catch (Exception ex)
        {
            Write("panorama failed: " + ex.Message);
            return false;
        }
        finally
        {
            _camera.Position = savedPosition;
            _camera.Rotation = savedRotation;
            _camera.ViewRotation = savedViewRotation;
            _camera.FovDegrees = savedFov;
            _camera.FovType = savedFovType;
            _camera.Ortho = savedOrtho;
            _camera.OrthoLeft = savedLeft;
            _camera.OrthoRight = savedRight;
            _camera.OrthoBottom = savedBottom;
            _camera.OrthoTop = savedTop;
            _camera.TwoPointPerspective = savedTwoPoint;
            _camera.AutoExposure = savedAutoExposure;
            _camera.Exposure = savedExposure;
            _camera.SetAspect(_renderExtent.Width, _renderExtent.Height);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static byte[] LoadBgra(string path, int width, int height)
    {
        var frame = BitmapFrame.Create(
            new Uri(Path.GetFullPath(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte[] StitchEquirect(byte[][] faces, int faceSize, int width, int height)
    {
        var output = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            float lat = ((y + 0.5f) / height - 0.5f) * MathF.PI;
            float cosLat = MathF.Cos(lat);
            float sinLat = MathF.Sin(lat);
            for (int x = 0; x < width; x++)
            {
                float lon = ((x + 0.5f) / width - 0.5f) * 2f * MathF.PI;
                var direction = new Vec3(cosLat * MathF.Cos(lon), sinLat, cosLat * MathF.Sin(lon));

                int bestFace = 0;
                float bestDenom = float.MinValue;
                float bestU = 0f, bestV = 0f;
                for (int f = 0; f < PanoramaFaces.Length; f++)
                {
                    var (forward, up) = PanoramaFaces[f];
                    float denom = Vec3.Dot(direction, forward);
                    if (denom <= bestDenom) continue;
                    var right = Vec3.Normalize(Vec3.Cross(forward, up));
                    bestFace = f;
                    bestDenom = denom;
                    bestU = Vec3.Dot(direction, right) / denom;
                    bestV = Vec3.Dot(direction, up) / denom;
                }

                float texU = Math.Clamp((bestU + 1f) * 0.5f, 0f, 0.999f);
                float texV = Math.Clamp((1f - bestV) * 0.5f, 0f, 0.999f);
                int px = Math.Clamp((int)(texU * faceSize), 0, faceSize - 1);
                int py = Math.Clamp((int)(texV * faceSize), 0, faceSize - 1);
                int src = (py * faceSize + px) * 4;
                int dst = (y * width + x) * 4;
                var face = faces[bestFace];
                output[dst] = face[src];
                output[dst + 1] = face[src + 1];
                output[dst + 2] = face[src + 2];
                output[dst + 3] = 255;
            }
        }
        return output;
    }

    private Framebuffer CreateCaptureFramebuffer(RenderPass renderPass, ImageView colorView,
        ImageView depthColorView, ImageView depthView, uint width, uint height)
    {
        var attachments = new[] { colorView, depthColorView, depthView };
        fixed (ImageView* p = attachments)
        {
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 3,
                PAttachments = p,
                Width = width,
                Height = height,
                Layers = 1,
            };
            var fbResult = _vk.CreateFramebuffer(_device, &fbInfo, null, out var framebuffer);
            if (fbResult != Result.Success)
                throw new InvalidOperationException($"capture vkCreateFramebuffer failed: {fbResult}");
            return framebuffer;
        }
    }

    private CommandBuffer BeginSingleTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        _vk.AllocateCommandBuffers(_device, &allocInfo, out var commandBuffer);

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _vk.BeginCommandBuffer(commandBuffer, &begin);
        return commandBuffer;
    }

    private void EndSingleTimeCommands(CommandBuffer commandBuffer)
    {
        _vk.EndCommandBuffer(commandBuffer);

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };

        _vk.QueueSubmit(_graphicsQueue, 1, &submit, default);
        _vk.QueueWaitIdle(_graphicsQueue);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
    }

    // ------------------------------------------------------------------
    // Resize / recreation
    // ------------------------------------------------------------------

    public void Resize(uint width, uint height)
    {
        if (_disposed) return;
        if (width == 0 || height == 0) return;
        _pendingWidth = width;
        _pendingHeight = height;
        _framebufferResized = true;
    }

    public void SetVsync(bool vsync)
    {
        if (_vsync == vsync) return;
        _vsync = vsync;
        _swapchainState?.SetVsync(vsync);
        _framebufferResized = true;
    }

    public void SetHdr(bool hdr)
    {
        if (_hdr == hdr) return;
        _hdr = hdr;
        _swapchainState?.SetHdr(hdr);
        _framebufferResized = true;
    }

    public void SetWhiteMode(bool enabled)
    {
        _whiteMode = enabled;
        _settings.WhiteBackground = enabled;
    }

    public void SetSettings(RendererSettings settings)
    {
        _settings = settings.Clone();
        _whiteMode = _settings.WhiteBackground;
        _sunInfo = SunPosition.Compute(_sun, (float)_settings.SunPower);
        float newScale = Math.Clamp((float)_settings.RenderScale, 0.5f, 1f);
        if (MathF.Abs(newScale - _renderScale) > 1e-4f)
        {
            _renderScale = newScale;
            _framebufferResized = true;
        }
        _sky?.SetSkybox(
            _settings.SkyboxFromFile ? _settings.SkyboxFile : "",
            _settings);
    }

    private void RecreateSwapchain()
    {
        if (_disposed) return;
        _vk.DeviceWaitIdle(_device);

        CleanupSwapchain();

        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var caps);
        if (caps.CurrentExtent.Width == 0 || caps.CurrentExtent.Height == 0)
        {
            // Minimized: leave the swapchain destroyed and retry on the next resize notification.
            return;
        }

        _swapchainState.Recreate(_pendingWidth, _pendingHeight);
        SwapchainRecreationCount++;
        _renderScale = Math.Clamp((float)_settings.RenderScale, 0.5f, 1f);
        _renderExtent = ComputeRenderExtent();
        Write($"render scale: {_renderScale:0.##} -> {_renderExtent.Width}x{_renderExtent.Height} " +
              $"(swapchain {_swapchainExtent.Width}x{_swapchainExtent.Height})");
        if (_postFormat != _swapchainFormat)
        {
            _post.Dispose();
            _post = new PostProcessor(_context, _allocator, _pipelines, _swapchainFormat);
            _postFormat = _swapchainFormat;
        }
        CreateDepthResources();
        CreateSceneColorResources();
        CreateFramebuffers();
        CreateRefractionResources();
        CreateTaaResources();
        _camera.SetAspect(_renderExtent.Width, _renderExtent.Height);
    }

    private void CleanupSwapchain()
    {
        foreach (var framebuffer in _framebuffers)
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        _framebuffers = Array.Empty<Framebuffer>();
        foreach (var framebuffer in _transparentFramebuffers)
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        _transparentFramebuffers = Array.Empty<Framebuffer>();
        foreach (var framebuffer in _postFramebuffers)
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        _postFramebuffers = Array.Empty<Framebuffer>();

        DestroySceneColorResources();
        DestroyRefractionResources();
        DestroyTaaResources();

        if (_depthView.Handle != 0) _vk.DestroyImageView(_device, _depthView, null);
        _depthView = default;
        _depthImage?.Destroy(immediate: true);
        _depthImage = null;
        _allocator?.CollectGarbage(0);

        _swapchainState?.Destroy();
    }

    // ------------------------------------------------------------------
    // Resource helpers
    // ------------------------------------------------------------------

    private ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspect)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
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

        var result = _vk.CreateImageView(_device, &info, null, out var view);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView failed: {result}");
        return view;
    }

    // ------------------------------------------------------------------
    // Disposal
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_device.Handle != 0)
            {
                _vk.DeviceWaitIdle(_device);
                CleanupSwapchain();

                if (_sceneOpaquePipeline.Handle != 0) _vk.DestroyPipeline(_device, _sceneOpaquePipeline, null);
                if (_sceneDoubleSidedPipeline.Handle != 0) _vk.DestroyPipeline(_device, _sceneDoubleSidedPipeline, null);
                if (_sceneTransparentPipeline.Handle != 0) _vk.DestroyPipeline(_device, _sceneTransparentPipeline, null);
                if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
                _shadowMap?.Dispose();
                _sky?.Dispose();
                _post?.Dispose();
                _pipelines?.Dispose();
                _descriptors?.Dispose();
                if (_renderPass.Handle != 0) _vk.DestroyRenderPass(_device, _renderPass, null);
                if (_transparentRenderPass.Handle != 0) _vk.DestroyRenderPass(_device, _transparentRenderPass, null);

                for (int i = 0; i < MaxFramesInFlight; i++)
                    _uniformBuffers[i]?.Destroy(immediate: true);
                for (int i = 0; i < MaxFramesInFlight; i++)
                    _sceneUniformBuffers[i]?.Destroy(immediate: true);
                _vertexBuffer?.Destroy(immediate: true);
                _indexBuffer?.Destroy(immediate: true);
                _scene?.Dispose();
                _textures?.Dispose();
                _materialBuffer?.Dispose();
                _lightBuffer?.Dispose();
                _frameGraph?.Dispose();
                _allocator?.CollectGarbage(0);
                _allocator?.Dispose();

                for (int i = 0; i < MaxFramesInFlight; i++)
                {
                    if (_imageAvailable[i].Handle != 0) _vk.DestroySemaphore(_device, _imageAvailable[i], null);
                    if (_renderFinished[i].Handle != 0) _vk.DestroySemaphore(_device, _renderFinished[i], null);
                    if (_inFlight[i].Handle != 0) _vk.DestroyFence(_device, _inFlight[i], null);
                }

                if (_commandPool.Handle != 0) _vk.DestroyCommandPool(_device, _commandPool, null);
            }

            _context?.Dispose();
        }
        catch (Exception ex)
        {
            Write("Vulkan dispose error: " + ex.Message);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ScenePushConstants
{
    public float M0, M1, M2, M3;
    public float M4, M5, M6, M7;
    public float M8, M9, M10, M11;
    public float M12, M13, M14, M15;
    public int FrontMaterial;
    public int BackMaterial;
    public int Flags;
    public int Pad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vertex
{
    public float X;
    public float Y;
    public float Z;
    public float R;
    public float G;
    public float B;
}

internal readonly struct Vec3
{
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vec3 Normalize(Vec3 v)
    {
        float length = v.Length;
        return length > 1e-6f ? new Vec3(v.X / length, v.Y / length, v.Z / length) : new Vec3(0, 0, 0);
    }
}

/// <summary>Small column-major matrix helper (Vulkan clip space: Y down, depth 0..1).</summary>
internal static class Mat4
{
    public static float[] Identity() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    /// <summary>Column-major rotation matrix from an engine-space quaternion.</summary>
    public static float[] FromQuaternion(float x, float y, float z, float w)
    {
        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;

        float m00 = 1f - 2f * (yy + zz);
        float m01 = 2f * (xy - wz);
        float m02 = 2f * (xz + wy);
        float m10 = 2f * (xy + wz);
        float m11 = 1f - 2f * (xx + zz);
        float m12 = 2f * (yz - wx);
        float m20 = 2f * (xz - wy);
        float m21 = 2f * (yz + wx);
        float m22 = 1f - 2f * (xx + yy);

        return new float[]
        {
            m00, m10, m20, 0,
            m01, m11, m21, 0,
            m02, m12, m22, 0,
            0, 0, 0, 1,
        };
    }

    /// <summary>Right-handed view matrix from a camera position and orientation quaternion.</summary>
    public static float[] ViewFromCamera(Vec3 position, float[] rotation)
    {
        var view = new float[16];
        for (int c = 0; c < 3; c++)
        {
            for (int row = 0; row < 3; row++)
                view[c * 4 + row] = rotation[row * 4 + c];

            view[12 + c] = -(
                rotation[c * 4 + 0] * position.X +
                rotation[c * 4 + 1] * position.Y +
                rotation[c * 4 + 2] * position.Z);
        }
        view[15] = 1;
        return view;
    }

    public static float[] Ortho(float left, float right, float bottom, float top, float near, float far)
    {
        return new float[]
        {
            2f / (right - left), 0, 0, 0,
            0, -2f / (top - bottom), 0, 0,
            0, 0, 1f / (near - far), 0,
            -(right + left) / (right - left),
            (top + bottom) / (top - bottom),
            near / (near - far),
            1,
        };
    }

    /// <summary>General 4x4 inverse for column-major matrices.</summary>
    public static float[] Invert(float[] m)
    {
        float a00 = m[0], a01 = m[1], a02 = m[2], a03 = m[3];
        float a10 = m[4], a11 = m[5], a12 = m[6], a13 = m[7];
        float a20 = m[8], a21 = m[9], a22 = m[10], a23 = m[11];
        float a30 = m[12], a31 = m[13], a32 = m[14], a33 = m[15];

        float b00 = a00 * a11 - a01 * a10;
        float b01 = a00 * a12 - a02 * a10;
        float b02 = a00 * a13 - a03 * a10;
        float b03 = a01 * a12 - a02 * a11;
        float b04 = a01 * a13 - a03 * a11;
        float b05 = a02 * a13 - a03 * a12;
        float b06 = a20 * a31 - a21 * a30;
        float b07 = a20 * a32 - a22 * a30;
        float b08 = a20 * a33 - a23 * a30;
        float b09 = a21 * a32 - a22 * a31;
        float b10 = a21 * a33 - a23 * a31;
        float b11 = a22 * a33 - a23 * a32;

        float det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
        if (MathF.Abs(det) < 1e-8f) return Identity();
        float invDet = 1f / det;

        return new float[]
        {
            (a11 * b11 - a12 * b10 + a13 * b09) * invDet,
            (a02 * b10 - a01 * b11 - a03 * b09) * invDet,
            (a31 * b05 - a32 * b04 + a33 * b03) * invDet,
            (a22 * b04 - a21 * b05 - a23 * b03) * invDet,
            (a12 * b08 - a10 * b11 - a13 * b07) * invDet,
            (a00 * b11 - a02 * b08 + a03 * b07) * invDet,
            (a32 * b02 - a30 * b05 - a33 * b01) * invDet,
            (a20 * b05 - a22 * b02 + a23 * b01) * invDet,
            (a10 * b10 - a11 * b08 + a13 * b06) * invDet,
            (a01 * b08 - a00 * b10 - a03 * b06) * invDet,
            (a30 * b04 - a31 * b02 + a33 * b00) * invDet,
            (a21 * b02 - a20 * b04 - a23 * b00) * invDet,
            (a11 * b07 - a10 * b09 - a12 * b06) * invDet,
            (a00 * b09 - a01 * b07 + a02 * b06) * invDet,
            (a31 * b01 - a30 * b03 - a32 * b00) * invDet,
            (a20 * b03 - a21 * b01 + a22 * b00) * invDet,
        };
    }

    public static Vec3 TransformPoint(float[] m, Vec3 p) =>
        new(
            m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12],
            m[1] * p.X + m[5] * p.Y + m[9] * p.Z + m[13],
            m[2] * p.X + m[6] * p.Y + m[10] * p.Z + m[14]);

    public static float[] PerspectiveForFov(float fovDegrees, int fovType, float aspect, float near, float far)
    {
        float fovY = fovType == 0
            ? 2f * MathF.Atan(MathF.Tan(fovDegrees * MathF.PI / 360f) / MathF.Max(aspect, 1e-4f))
            : fovDegrees * MathF.PI / 180f;
        return Perspective(fovY, aspect, near, far);
    }

    public static float[] LookAt(Vec3 eye, Vec3 center, Vec3 up)
    {
        var f = Normalize(Sub(center, eye));
        var s = Normalize(Cross(f, up));
        var u = Cross(s, f);

        return new float[]
        {
            s.X, u.X, -f.X, 0,
            s.Y, u.Y, -f.Y, 0,
            s.Z, u.Z, -f.Z, 0,
            -Dot(s, eye), -Dot(u, eye), Dot(f, eye), 1,
        };
    }

    public static float[] Perspective(float fovY, float aspect, float near, float far)
    {
        float f = 1.0f / MathF.Tan(fovY * 0.5f);
        return new float[]
        {
            f / aspect, 0, 0, 0,
            0, -f, 0, 0,
            0, 0, far / (near - far), -1,
            0, 0, (far * near) / (near - far), 0,
        };
    }

    public static float[] RotateY(float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new float[]
        {
            c, 0, -s, 0,
            0, 1, 0, 0,
            s, 0, c, 0,
            0, 0, 0, 1,
        };
    }

    public static float[] RotateX(float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new float[]
        {
            1, 0, 0, 0,
            0, c, s, 0,
            0, -s, c, 0,
            0, 0, 0, 1,
        };
    }

    public static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[16];
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
                r[c * 4 + row] =
                    a[row] * b[c * 4] +
                    a[4 + row] * b[c * 4 + 1] +
                    a[8 + row] * b[c * 4 + 2] +
                    a[12 + row] * b[c * 4 + 3];
        return r;
    }

    private static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vec3 Normalize(Vec3 v)
    {
        float length = MathF.Sqrt(Dot(v, v));
        return new Vec3(v.X / length, v.Y / length, v.Z / length);
    }
}

internal static class CubeGeometry
{
    // 6 faces x 4 vertices, CCW when viewed from outside.
    public static readonly Vertex[] Vertices =
    {
        // +X (red)
        V(1, -1, -1, 0.90f, 0.20f, 0.18f), V(1, 1, -1, 0.90f, 0.20f, 0.18f), V(1, 1, 1, 0.90f, 0.20f, 0.18f), V(1, -1, 1, 0.90f, 0.20f, 0.18f),
        // -X (green)
        V(-1, -1, -1, 0.22f, 0.80f, 0.30f), V(-1, -1, 1, 0.22f, 0.80f, 0.30f), V(-1, 1, 1, 0.22f, 0.80f, 0.30f), V(-1, 1, -1, 0.22f, 0.80f, 0.30f),
        // +Y (blue)
        V(-1, 1, -1, 0.20f, 0.40f, 0.95f), V(-1, 1, 1, 0.20f, 0.40f, 0.95f), V(1, 1, 1, 0.20f, 0.40f, 0.95f), V(1, 1, -1, 0.20f, 0.40f, 0.95f),
        // -Y (yellow)
        V(-1, -1, -1, 0.95f, 0.82f, 0.20f), V(1, -1, -1, 0.95f, 0.82f, 0.20f), V(1, -1, 1, 0.95f, 0.82f, 0.20f), V(-1, -1, 1, 0.95f, 0.82f, 0.20f),
        // +Z (magenta)
        V(-1, -1, 1, 0.85f, 0.25f, 0.75f), V(1, -1, 1, 0.85f, 0.25f, 0.75f), V(1, 1, 1, 0.85f, 0.25f, 0.75f), V(-1, 1, 1, 0.85f, 0.25f, 0.75f),
        // -Z (cyan)
        V(-1, -1, -1, 0.20f, 0.80f, 0.85f), V(-1, 1, -1, 0.20f, 0.80f, 0.85f), V(1, 1, -1, 0.20f, 0.80f, 0.85f), V(1, -1, -1, 0.20f, 0.80f, 0.85f),
    };

    public static readonly ushort[] Indices =
    {
        0, 1, 2, 0, 2, 3,
        4, 5, 6, 4, 6, 7,
        8, 9, 10, 8, 10, 11,
        12, 13, 14, 12, 14, 15,
        16, 17, 18, 16, 18, 19,
        20, 21, 22, 20, 22, 23,
    };

    private static Vertex V(float x, float y, float z, float r, float g, float b) =>
        new() { X = x, Y = y, Z = z, R = r, G = g, B = b };
}
