using System.Runtime.InteropServices;
using PowerPotion.Rendering.Shaders;
using PowerPotion.Rendering.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using PpPipelineCache = PowerPotion.Rendering.Vulkan.PipelineCache;

namespace PowerPotion.Rendering.Scene;

/// <summary>P5 post parameters (one std140 block, 64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PostParams
{
    public float Exposure;
    public float Bloom;
    public float Saturation;
    public float Contrast;

    public float Temperature;
    public float Vignette;
    public float ChromaticAberration;
    public float MotionBlur;

    public float SafeFrame;
    public float LensFlare;
    public float Time;
    public float AutoContrast;

    public float SunX;
    public float SunY;
    public float SunStrength;
    public float Taa;

    public float SaoStrength;
    public float SaoRadius;
    public float Near;
    public float Far;

    public float GiStrength;
    public float GiRadius;
    public float GiFalloff;
    public float GiPad;

    public float[]? Projection;
    public float[]? InvProjection;
}

/// <summary>
/// P5 fullscreen post pass: exposure, bloom, saturation, colour temperature, contrast,
/// vignette, chromatic aberration, motion blur, lens flare and the capture safe frame.
/// The scene/sky shaders already tone-map; this pass grades the LDR result.
/// </summary>
internal sealed unsafe class PostProcessor : IDisposable
{
    private const int Frames = 3;

    private readonly VulkanContext _context;
    private readonly VulkanAllocator _allocator;
    private readonly PpPipelineCache _pipelines;
    private readonly Format _colorFormat;

    private readonly BufferAllocation?[] _ubos = new BufferAllocation?[Frames];
    private readonly IntPtr[] _mapped = new IntPtr[Frames];
    private readonly DescriptorSet[] _sets = new DescriptorSet[Frames];

    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private RenderPass _renderPass;
    private Sampler _sampler;
    private Sampler _depthSampler;
    private bool _disposed;

    public PostProcessor(VulkanContext context, VulkanAllocator allocator, PpPipelineCache pipelines,
        Format colorFormat)
    {
        _context = context;
        _allocator = allocator;
        _pipelines = pipelines;
        _colorFormat = colorFormat;
        CreateResources();
    }

    public RenderPass RenderPass => _renderPass;
    public Sampler Sampler => _sampler;

    private void CreateResources()
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
            new DescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 3,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
        };

        fixed (DescriptorSetLayoutBinding* bindingPtr = bindings)
        {
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingPtr,
            };
            var result = _context.Vk.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _setLayout);
            if (result != Result.Success)
                throw new InvalidOperationException($"post vkCreateDescriptorSetLayout failed: {result}");
        }

        var poolSizes = new[]
        {
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = Frames },
            new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = Frames * 3 },
        };
        fixed (DescriptorPoolSize* poolPtr = poolSizes)
        {
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = Frames,
                PoolSizeCount = (uint)poolSizes.Length,
                PPoolSizes = poolPtr,
            };
            var result = _context.Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, out _pool);
            if (result != Result.Success)
                throw new InvalidOperationException($"post vkCreateDescriptorPool failed: {result}");
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
                    throw new InvalidOperationException($"post vkAllocateDescriptorSets failed: {result}");
            }
        }

        for (int i = 0; i < Frames; i++)
        {
            _ubos[i] = _allocator.CreateBuffer(224, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, $"post uniform {i}");
            _mapped[i] = _ubos[i]!.Mapped;
        }

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
        var samplerResult = _context.Vk.CreateSampler(_context.Device, &samplerInfo, null, out _sampler);
        if (samplerResult != Result.Success)
            throw new InvalidOperationException($"post vkCreateSampler failed: {samplerResult}");

        var depthSamplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0f,
            MaxLod = 0f,
        };
        var depthSamplerResult = _context.Vk.CreateSampler(_context.Device, &depthSamplerInfo, null, out _depthSampler);
        if (depthSamplerResult != Result.Success)
            throw new InvalidOperationException($"post depth vkCreateSampler failed: {depthSamplerResult}");

        _layout = _pipelines.CreateLayout(new[] { _setLayout }, Array.Empty<PushConstantRange>());

        CreateRenderPass();
        CreatePipeline();
    }

    private void CreateRenderPass()
    {
        var color = new AttachmentDescription
        {
            Format = _colorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };
        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };

        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &color,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        var result = _context.Vk.CreateRenderPass(_context.Device, &info, null, out _renderPass);
        if (result != Result.Success)
            throw new InvalidOperationException($"post vkCreateRenderPass failed: {result}");
    }

    private void CreatePipeline()
    {
        var vert = _pipelines.GetShaderModule("post.vert", ShaderLibrary.Load("post.vert"));
        var frag = _pipelines.GetShaderModule("post.frag", ShaderLibrary.Load("post.frag"));
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
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 0,
                PVertexBindingDescriptions = null,
                VertexAttributeDescriptionCount = 0,
                PVertexAttributeDescriptions = null,
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
                StencilTestEnable = false,
            };
            var blend = new PipelineColorBlendAttachmentState
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 1,
                PAttachments = &blend,
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
                    PColorBlendState = &colorBlend,
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

    public void Update(int frame, ImageView sourceView, ImageView historyView, ImageView depthView,
        in PostParams parameters)
    {
        var data = new float[56];
        data[0] = parameters.Exposure;
        data[1] = parameters.Bloom;
        data[2] = parameters.Saturation;
        data[3] = parameters.Contrast;
        data[4] = parameters.Temperature;
        data[5] = parameters.Vignette;
        data[6] = parameters.ChromaticAberration;
        data[7] = parameters.MotionBlur;
        data[8] = parameters.SafeFrame;
        data[9] = parameters.LensFlare;
        data[10] = parameters.Time;
        data[11] = parameters.AutoContrast;
        data[12] = parameters.SunX;
        data[13] = parameters.SunY;
        data[14] = parameters.SunStrength;
        data[15] = parameters.Taa;
        data[16] = parameters.SaoStrength;
        data[17] = parameters.SaoRadius;
        data[18] = parameters.Near;
        data[19] = parameters.Far;
        data[20] = parameters.GiStrength;
        data[21] = parameters.GiRadius;
        data[22] = parameters.GiFalloff;
        data[23] = parameters.GiPad;
        if (parameters.Projection is { Length: 16 })
            Array.Copy(parameters.Projection, 0, data, 24, 16);
        if (parameters.InvProjection is { Length: 16 })
            Array.Copy(parameters.InvProjection, 0, data, 40, 16);

        fixed (float* p = data)
            System.Buffer.MemoryCopy(p, (void*)_mapped[frame], 224, 224);

        var uboInfo = new DescriptorBufferInfo
        {
            Buffer = _ubos[frame]!.Buffer,
            Offset = 0,
            Range = 224,
        };
        var imageInfo = new DescriptorImageInfo
        {
            ImageView = sourceView,
            Sampler = _sampler,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var historyInfo = new DescriptorImageInfo
        {
            ImageView = historyView,
            Sampler = _sampler,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var depthInfo = new DescriptorImageInfo
        {
            ImageView = depthView,
            Sampler = _depthSampler,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var writes = new[]
        {
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _sets[frame],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &uboInfo,
            },
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _sets[frame],
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageInfo,
            },
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _sets[frame],
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &historyInfo,
            },
            new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _sets[frame],
                DstBinding = 3,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &depthInfo,
            },
        };
        fixed (WriteDescriptorSet* writePtr = writes)
            _context.Vk.UpdateDescriptorSets(_context.Device, (uint)writes.Length, writePtr, 0, null);
    }

    public void Record(CommandBuffer commandBuffer, int frame, Framebuffer framebuffer, Extent2D extent)
    {
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = extent,
            },
            ClearValueCount = 0,
            PClearValues = null,
        };
        _context.Vk.CmdBeginRenderPass(commandBuffer, &begin, SubpassContents.Inline);
        _context.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);

        var viewport = new Viewport
        {
            X = 0, Y = 0, Width = extent.Width, Height = extent.Height, MinDepth = 0, MaxDepth = 1,
        };
        _context.Vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        var scissor = new Rect2D
        {
            Offset = new Offset2D { X = 0, Y = 0 },
            Extent = extent,
        };
        _context.Vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

        var set = _sets[frame];
        _context.Vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout,
            0, 1, &set, 0, null);
        _context.Vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        _context.Vk.CmdEndRenderPass(commandBuffer);
    }

    public Framebuffer CreateFramebuffer(ImageView colorView, uint width, uint height)
    {
        var attachment = colorView;
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 1,
            PAttachments = &attachment,
            Width = width,
            Height = height,
            Layers = 1,
        };
        var result = _context.Vk.CreateFramebuffer(_context.Device, &info, null, out var framebuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"post vkCreateFramebuffer failed: {result}");
        return framebuffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var vk = _context.Vk;
        if (_pipeline.Handle != 0) vk.DestroyPipeline(_context.Device, _pipeline, null);
        if (_renderPass.Handle != 0) vk.DestroyRenderPass(_context.Device, _renderPass, null);
        if (_sampler.Handle != 0) vk.DestroySampler(_context.Device, _sampler, null);
        if (_depthSampler.Handle != 0) vk.DestroySampler(_context.Device, _depthSampler, null);
        for (int i = 0; i < Frames; i++) _ubos[i]?.Destroy(immediate: true);
        if (_pool.Handle != 0) vk.DestroyDescriptorPool(_context.Device, _pool, null);
        if (_setLayout.Handle != 0) vk.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
