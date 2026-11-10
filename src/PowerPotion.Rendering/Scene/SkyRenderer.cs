using System.IO;
using System.Runtime.InteropServices;
using PowerPotion.Rendering.Settings;
using PowerPotion.Rendering.Shaders;
using PowerPotion.Rendering.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using PpPipelineCache = PowerPotion.Rendering.Vulkan.PipelineCache;

namespace PowerPotion.Rendering.Scene;

/// <summary>
/// P3 sky pass: procedural atmosphere with sun/moon/stars, optional equirectangular HDR skybox,
/// and a fullscreen triangle rendered before the opaque scene.
/// </summary>
internal sealed unsafe class SkyRenderer : IDisposable
{
    private const int Frames = 3;
    private const int UboSize = 160;

    private readonly VulkanContext _context;
    private readonly VulkanAllocator _allocator;
    private readonly PpPipelineCache _pipelines;
    private readonly RenderPass _renderPass;
    private readonly BufferAllocation?[] _ubos = new BufferAllocation?[Frames];
    private readonly IntPtr[] _mapped = new IntPtr[Frames];
    private readonly DescriptorSet[] _sets = new DescriptorSet[Frames];

    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private ImageAllocation? _skyboxImage;
    private ImageView _skyboxView;
    private Sampler _sampler;
    private bool _hasSkybox;
    private string _loadedPath = "";
    private bool _disposed;

    public SkyRenderer(VulkanContext context, VulkanAllocator allocator, PpPipelineCache pipelines, RenderPass renderPass)
    {
        _context = context;
        _allocator = allocator;
        _pipelines = pipelines;
        _renderPass = renderPass;
        CreateResources();
    }

    public bool HasSkybox => _hasSkybox;
    public event Action<string>? Log;

    private void Write(string message) => Log?.Invoke(message);

    private void CreateLayout()
    {
        var bindings = new[]
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
        };

        fixed (DescriptorSetLayoutBinding* bindingPtr = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingPtr,
            };
            var result = _context.Vk.CreateDescriptorSetLayout(_context.Device, &info, null, out _setLayout);
            if (result != Result.Success)
                throw new InvalidOperationException($"sky vkCreateDescriptorSetLayout failed: {result}");
        }

        _layout = _pipelines.CreateLayout(new[] { _setLayout }, null);

        var poolSizes = new[]
        {
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = Frames },
            new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = Frames },
        };
        fixed (DescriptorPoolSize* sizePtr = poolSizes)
        {
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = Frames,
                PoolSizeCount = (uint)poolSizes.Length,
                PPoolSizes = sizePtr,
            };
            var result = _context.Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, out _pool);
            if (result != Result.Success)
                throw new InvalidOperationException($"sky vkCreateDescriptorPool failed: {result}");
        }

        var layouts = new DescriptorSetLayout[Frames];
        Array.Fill(layouts, _setLayout);
        fixed (DescriptorSetLayout* layoutPtr = layouts)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pool,
                DescriptorSetCount = Frames,
                PSetLayouts = layoutPtr,
            };
            fixed (DescriptorSet* setPtr = _sets)
            {
                var result = _context.Vk.AllocateDescriptorSets(_context.Device, &allocInfo, setPtr);
                if (result != Result.Success)
                    throw new InvalidOperationException($"sky vkAllocateDescriptorSets failed: {result}");
            }
        }
    }

    private void CreateResources()
    {
        CreateLayout();

        for (int i = 0; i < Frames; i++)
        {
            _ubos[i] = _allocator.CreateBuffer(UboSize, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, $"sky uniform {i}");
            _mapped[i] = _ubos[i]!.Mapped;
        }

        _sampler = CreateSampler();
        CreateDefaultSkybox();
        UpdateDescriptors();

        var vert = _pipelines.GetShaderModule("sky.vert", ShaderLibrary.Load("sky.vert"));
        var frag = _pipelines.GetShaderModule("sky.frag", ShaderLibrary.Load("sky.frag"));
        var entry = SilkMarshal.StringToPtr("main");
        try
        {
            var stages = new[]
            {
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vert,
                    PName = (byte*)entry,
                },
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = frag,
                    PName = (byte*)entry,
                },
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 0,
                VertexAttributeDescriptionCount = 0,
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
                CullMode = CullModeFlags.None,
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
            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.Always,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false,
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var dynamicStates = new[] { DynamicState.Viewport, DynamicState.Scissor };
            fixed (PipelineShaderStageCreateInfo* stagePtr = stages)
            fixed (DynamicState* dynPtr = dynamicStates)
            {
                var dynamic = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)dynamicStates.Length,
                    PDynamicStates = dynPtr,
                };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = (uint)stages.Length,
                    PStages = stagePtr,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &raster,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depth,
                    PColorBlendState = &blend,
                    PDynamicState = &dynamic,
                    Layout = _layout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                _pipeline = _pipelines.CreateGraphicsPipeline(ref info);
            }
        }
        finally
        {
            SilkMarshal.FreeString(entry);
        }
    }

    private Sampler CreateSampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0f,
            MaxLod = 0f,
            BorderColor = BorderColor.FloatOpaqueBlack,
        };
        var result = _context.Vk.CreateSampler(_context.Device, &info, null, out var sampler);
        if (result != Result.Success)
            throw new InvalidOperationException($"sky vkCreateSampler failed: {result}");
        return sampler;
    }

    private void CreateDefaultSkybox()
    {
        var allocation = _allocator.CreateImage(1, 1, Format.R32G32B32A32Sfloat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, "default skybox");
        var pixel = new float[] { 0f, 0f, 0f, 1f };
        _allocator.UploadImage(allocation, 1, 1, MemoryMarshal.AsBytes(pixel.AsSpan()).ToArray());
        _skyboxImage = allocation;
        _skyboxView = CreateView(allocation.Image);
    }

    private ImageView CreateView(Image image)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = Format.R32G32B32A32Sfloat,
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
        var result = _context.Vk.CreateImageView(_context.Device, &info, null, out var view);
        if (result != Result.Success)
            throw new InvalidOperationException($"sky vkCreateImageView failed: {result}");
        return view;
    }

    private void UpdateDescriptors()
    {
        for (int i = 0; i < Frames; i++)
        {
            var uboInfo = new DescriptorBufferInfo
            {
                Buffer = _ubos[i]!.Buffer,
                Offset = 0,
                Range = UboSize,
            };
            var imageInfo = new DescriptorImageInfo
            {
                ImageView = _skyboxView,
                Sampler = _sampler,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var writes = new[]
            {
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[i],
                    DstBinding = 0,
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer,
                    PBufferInfo = &uboInfo,
                },
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[i],
                    DstBinding = 1,
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &imageInfo,
                },
            };
            fixed (WriteDescriptorSet* writePtr = writes)
                _context.Vk.UpdateDescriptorSets(_context.Device, (uint)writes.Length, writePtr, 0, null);
        }
    }

    public void SetSkybox(string path, RendererSettings settings)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
        if (normalized == _loadedPath) return;
        _loadedPath = normalized;

        if (string.IsNullOrWhiteSpace(normalized) || !File.Exists(normalized))
        {
            _hasSkybox = false;
            Write("sky: procedural atmosphere (no skybox file)");
            return;
        }

        bool isExr = normalized.EndsWith(".exr", StringComparison.OrdinalIgnoreCase);
        bool isHdr = normalized.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase);
        if (!isHdr && !isExr)
        {
            _hasSkybox = false;
            Write($"sky: unsupported skybox format '{Path.GetFileName(normalized)}'; using procedural atmosphere");
            return;
        }

        try
        {
            _context.Vk.DeviceWaitIdle(_context.Device);
            var hdr = isExr ? ExrLoader.Load(normalized) : HdrLoader.Load(normalized);
            var allocation = _allocator.CreateImage((uint)hdr.Width, (uint)hdr.Height,
                Format.R32G32B32A32Sfloat,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                "skybox " + Path.GetFileName(normalized));
            _allocator.UploadImage(allocation, (uint)hdr.Width, (uint)hdr.Height,
                MemoryMarshal.AsBytes(hdr.Rgba.AsSpan()).ToArray());

            var oldImage = _skyboxImage;
            var oldView = _skyboxView;
            _skyboxImage = allocation;
            _skyboxView = CreateView(allocation.Image);
            UpdateDescriptors();

            if (oldView.Handle != 0) _context.Vk.DestroyImageView(_context.Device, oldView, null);
            oldImage?.Destroy(immediate: true);
            _allocator.CollectGarbage(0);
            _hasSkybox = true;
            Write($"sky: loaded {Path.GetFileName(normalized)} {hdr.Width}x{hdr.Height}");
        }
        catch (Exception ex)
        {
            _hasSkybox = false;
            Write($"sky: failed to load {Path.GetFileName(normalized)}: {ex.Message}");
        }
    }

    public void Update(int frame, CameraState camera, SunInfo sun, RendererSettings settings, bool whiteMode)
    {
        var data = new float[40];
        var inv = Mat4.Invert(camera.ViewProjection);
        Array.Copy(inv, data, 16);
        data[16] = camera.Position.X; data[17] = camera.Position.Y; data[18] = camera.Position.Z; data[19] = 1f;
        data[20] = sun.Direction.X; data[21] = sun.Direction.Y; data[22] = sun.Direction.Z; data[23] = 0f;
        data[24] = sun.Color.X; data[25] = sun.Color.Y; data[26] = sun.Color.Z; data[27] = 1f;
        data[28] = sun.MoonDirection.X; data[29] = sun.MoonDirection.Y; data[30] = sun.MoonDirection.Z; data[31] = 0f;
        data[32] = (float)settings.StarBrightness; data[33] = (float)settings.SunPower;
        data[34] = whiteMode ? 1f : 0f; data[35] = _hasSkybox ? 1f : 0f;
        data[36] = (float)(settings.SkyboxRotationDegrees * Math.PI / 180.0);
        data[37] = (float)settings.SkyboxBrightness;
        data[38] = settings.SkyboxUseSunSpot ? 1f : 0f;
        data[39] = (float)settings.MoonSize;

        fixed (float* p = data)
            System.Buffer.MemoryCopy(p, (void*)_mapped[frame], UboSize, UboSize);
    }

    public void Record(CommandBuffer commandBuffer, int frame, Extent2D extent)
    {
        _context.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        var set = _sets[frame];
        _context.Vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout,
            0, 1, &set, 0, null);

        var viewport = new Viewport
        {
            X = 0, Y = 0,
            Width = extent.Width, Height = extent.Height,
            MinDepth = 0, MaxDepth = 1,
        };
        _context.Vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        var scissor = new Rect2D
        {
            Offset = new Offset2D { X = 0, Y = 0 },
            Extent = extent,
        };
        _context.Vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        _context.Vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var vk = _context.Vk;
        if (_pipeline.Handle != 0) vk.DestroyPipeline(_context.Device, _pipeline, null);
        if (_pool.Handle != 0) vk.DestroyDescriptorPool(_context.Device, _pool, null);
        if (_setLayout.Handle != 0) vk.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        foreach (var ubo in _ubos)
            ubo?.Destroy(immediate: true);
        if (_skyboxView.Handle != 0) vk.DestroyImageView(_context.Device, _skyboxView, null);
        _skyboxImage?.Destroy(immediate: true);
        if (_sampler.Handle != 0) vk.DestroySampler(_context.Device, _sampler, null);
        _allocator.CollectGarbage(0);
    }
}
