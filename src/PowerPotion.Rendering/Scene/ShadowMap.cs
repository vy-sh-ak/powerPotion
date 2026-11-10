using System.Runtime.InteropServices;
using PowerPotion.Rendering.Shaders;
using PowerPotion.Rendering.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using PpPipelineCache = PowerPotion.Rendering.Vulkan.PipelineCache;

namespace PowerPotion.Rendering.Scene;

[StructLayout(LayoutKind.Sequential)]
internal struct ShadowPushConstants
{
    public float M0, M1, M2, M3;
    public float M4, M5, M6, M7;
    public float M8, M9, M10, M11;
    public float M12, M13, M14, M15;
    public float L0, L1, L2, L3;
    public float L4, L5, L6, L7;
    public float L8, L9, L10, L11;
    public float L12, L13, L14, L15;
}

/// <summary>Four-cascade sun shadow map (D32_SFLOAT array) with PCF comparison sampling.</summary>
internal sealed unsafe class ShadowMap : IDisposable
{
    public const int CascadeCount = 4;

    private readonly VulkanContext _context;
    private readonly VulkanAllocator _allocator;
    private readonly PpPipelineCache _pipelines;
    private readonly uint _size;
    private readonly DescriptorSetLayout _sceneLayout;

    private ImageAllocation? _image;
    private ImageView _arrayView;
    private ImageView[] _layerViews = Array.Empty<ImageView>();
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();
    private RenderPass _renderPass;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private Sampler _sampler;
    private bool _disposed;

    public ShadowMap(VulkanContext context, VulkanAllocator allocator, PpPipelineCache pipelines, uint size,
        DescriptorSetLayout sceneLayout)
    {
        _context = context;
        _allocator = allocator;
        _pipelines = pipelines;
        _size = size;
        _sceneLayout = sceneLayout;
        CreateResources();
    }

    public Image Image => _image!.Image;
    public ImageView ArrayView => _arrayView;
    public Sampler Sampler => _sampler;
    public float[][] Matrices { get; } = Enumerable.Range(0, CascadeCount).Select(_ => new float[16]).ToArray();
    public float[] Splits { get; } = new float[CascadeCount];
    public uint Size => _size;
    public event Action<string>? Log;

    private void CreateResources()
    {
        var image = _allocator.CreateImage(_size, _size, Format.D32Sfloat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            "sun shadow cascades", mipLevels: 1, arrayLayers: CascadeCount);
        _image = image;

        _arrayView = CreateView(ImageAspectFlags.DepthBit, 0, CascadeCount, ImageViewType.Type2DArray);
        _layerViews = new ImageView[CascadeCount];

        var attachment = new AttachmentDescription
        {
            Format = Format.D32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var depthRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 0,
            PDepthStencilAttachment = &depthRef,
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
        };
        var rpInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        var rpResult = _context.Vk.CreateRenderPass(_context.Device, &rpInfo, null, out _renderPass);
        if (rpResult != Result.Success)
            throw new InvalidOperationException($"shadow vkCreateRenderPass failed: {rpResult}");

        _framebuffers = new Framebuffer[CascadeCount];
        for (int i = 0; i < CascadeCount; i++)
        {
            _layerViews[i] = CreateView(ImageAspectFlags.DepthBit, (uint)i, 1, ImageViewType.Type2D);
            var attachments = new[] { _layerViews[i] };
            fixed (ImageView* p = attachments)
            {
                var fbInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _renderPass,
                    AttachmentCount = 1,
                    PAttachments = p,
                    Width = _size,
                    Height = _size,
                    Layers = 1,
                };
                var result = _context.Vk.CreateFramebuffer(_context.Device, &fbInfo, null, out _framebuffers[i]);
                if (result != Result.Success)
                    throw new InvalidOperationException($"shadow vkCreateFramebuffer failed: {result}");
            }
        }

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToBorder,
            CompareEnable = true,
            CompareOp = CompareOp.LessOrEqual,
            BorderColor = BorderColor.FloatOpaqueWhite,
            MinLod = 0f,
            MaxLod = 0f,
        };
        var samplerResult = _context.Vk.CreateSampler(_context.Device, &samplerInfo, null, out _sampler);
        if (samplerResult != Result.Success)
            throw new InvalidOperationException($"shadow vkCreateSampler failed: {samplerResult}");

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)sizeof(ShadowPushConstants),
        };
        _layout = _pipelines.CreateLayout(new[] { _sceneLayout }, new[] { pushRange });
        CreatePipeline();
    }

    private ImageView CreateView(ImageAspectFlags aspect, uint baseLayer, uint layerCount, ImageViewType type)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = type,
            Format = Format.D32Sfloat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = baseLayer,
                LayerCount = layerCount,
            },
        };
        var result = _context.Vk.CreateImageView(_context.Device, &info, null, out var view);
        if (result != Result.Success)
            throw new InvalidOperationException($"shadow vkCreateImageView failed: {result}");
        return view;
    }

    private void CreatePipeline()
    {
        var vert = _pipelines.GetShaderModule("shadow.vert", ShaderLibrary.Load("shadow.vert"));
        var frag = _pipelines.GetShaderModule("shadow.frag", ShaderLibrary.Load("shadow.frag"));
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
            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = (uint)sizeof(SceneVertex),
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
            };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 1,
                PVertexAttributeDescriptions = null,
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
                DepthBiasEnable = true,
                DepthBiasConstantFactor = 1.5f,
                DepthBiasSlopeFactor = 2.0f,
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
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.LessOrEqual,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 0,
                PAttachments = null,
            };
            var dynamicStates = new[] { DynamicState.Viewport, DynamicState.Scissor };
            fixed (PipelineShaderStageCreateInfo* stagePtr = stages)
            fixed (VertexInputAttributeDescription* attrPtr = attributes)
            fixed (DynamicState* dynPtr = dynamicStates)
            {
                vertexInput.PVertexAttributeDescriptions = attrPtr;
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

    public void UpdateCascades(CameraState camera, Vec3 sunDirection, float shadowDistance)
    {
        float lambda = 0.5f;
        float near = Math.Max(0.5f, camera.Near);
        float far = Math.Max(near + 1f, Math.Min(camera.Far, shadowDistance));
        float[] splits = new float[CascadeCount];
        for (int i = 0; i < CascadeCount; i++)
        {
            float p = (i + 1f) / CascadeCount;
            float log = near * MathF.Pow(far / near, p);
            float linear = near + (far - near) * p;
            splits[i] = lambda * log + (1f - lambda) * linear;
            Splits[i] = splits[i];
        }

        var sun = Vec3.Normalize(sunDirection);
        var up = MathF.Abs(Vec3.Dot(sun, new Vec3(0, 1, 0))) > 0.95f
            ? new Vec3(0, 0, 1)
            : new Vec3(0, 1, 0);

        for (int i = 0; i < CascadeCount; i++)
        {
            float nearI = i == 0 ? camera.Near : splits[i - 1];
            float farI = splits[i];
            var corners = camera.FrustumCorners(nearI, farI);

            var center = new Vec3(0, 0, 0);
            foreach (var corner in corners) center += corner;
            center *= 1f / corners.Length;

            float radius = 0f;
            foreach (var corner in corners)
                radius = MathF.Max(radius, (corner - center).Length);

            var eye = center + sun * (radius + 20f);
            var view = Mat4.LookAt(eye, center, up);

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (var corner in corners)
            {
                var l = Mat4.TransformPoint(view, corner);
                minX = MathF.Min(minX, l.X); maxX = MathF.Max(maxX, l.X);
                minY = MathF.Min(minY, l.Y); maxY = MathF.Max(maxY, l.Y);
                minZ = MathF.Min(minZ, l.Z); maxZ = MathF.Max(maxZ, l.Z);
            }

            float pad = 1.0f;
            minX -= pad; maxX += pad;
            minY -= pad; maxY += pad;
            minZ -= pad; maxZ += pad;

            // Stabilize cascade edges against texel swimming.
            float texelX = (maxX - minX) / _size;
            float texelY = (maxY - minY) / _size;
            if (texelX > 1e-6f) minX = MathF.Floor(minX / texelX) * texelX;
            if (texelY > 1e-6f) minY = MathF.Floor(minY / texelY) * texelY;

            float nearZ = MathF.Max(0.1f, -maxZ);
            float farZ = MathF.Max(nearZ + 1f, -minZ);
            var projection = Mat4.Ortho(minX, maxX, minY, maxY, nearZ, farZ);
            Matrices[i] = Mat4.Multiply(projection, view);
        }
    }

    public void Render(CommandBuffer commandBuffer, GpuScene scene, DescriptorSet sceneSet)
    {
        for (int cascade = 0; cascade < CascadeCount; cascade++)
        {
            var clear = new ClearValue
            {
                DepthStencil = new ClearDepthStencilValue { Depth = 1.0f, Stencil = 0 },
            };
            var rpInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _renderPass,
                Framebuffer = _framebuffers[cascade],
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = new Extent2D { Width = _size, Height = _size },
                },
                ClearValueCount = 1,
                PClearValues = &clear,
            };
            _context.Vk.CmdBeginRenderPass(commandBuffer, &rpInfo, SubpassContents.Inline);
            _context.Vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);

            _context.Vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout,
                0, 1, &sceneSet, 0, null);

            var viewport = new Viewport
            {
                X = 0, Y = 0, Width = _size, Height = _size, MinDepth = 0, MaxDepth = 1,
            };
            _context.Vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = new Extent2D { Width = _size, Height = _size },
            };
            _context.Vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

            foreach (var draw in scene.Draws)
            {
                if (draw.Transparent) continue;
                var model = scene.Models[draw.ModelIndex];
                var vertexBuffer = model.Vertex.Buffer;
                ulong offset = 0;
                _context.Vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);
                _context.Vk.CmdBindIndexBuffer(commandBuffer, model.Index.Buffer, 0, IndexType.Uint32);

                var push = new ShadowPushConstants();
                fixed (float* src = draw.Matrix)
                    System.Buffer.MemoryCopy(src, &push, 64, 64);
                fixed (float* src = Matrices[cascade])
                    System.Buffer.MemoryCopy(src, (byte*)&push + 64, 64, 64);

                _context.Vk.CmdPushConstants(commandBuffer, _layout,
                    ShaderStageFlags.VertexBit, 0, (uint)sizeof(ShadowPushConstants), &push);
                _context.Vk.CmdDrawIndexed(commandBuffer, draw.IndexCount, 1, draw.StartIndex, 0, 0);
            }

            _context.Vk.CmdEndRenderPass(commandBuffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var vk = _context.Vk;
        if (_pipeline.Handle != 0) vk.DestroyPipeline(_context.Device, _pipeline, null);
        if (_renderPass.Handle != 0) vk.DestroyRenderPass(_context.Device, _renderPass, null);
        foreach (var fb in _framebuffers) vk.DestroyFramebuffer(_context.Device, fb, null);
        foreach (var view in _layerViews) vk.DestroyImageView(_context.Device, view, null);
        if (_arrayView.Handle != 0) vk.DestroyImageView(_context.Device, _arrayView, null);
        if (_sampler.Handle != 0) vk.DestroySampler(_context.Device, _sampler, null);
        _image?.Destroy(immediate: true);
        _allocator.CollectGarbage(0);
    }
}
