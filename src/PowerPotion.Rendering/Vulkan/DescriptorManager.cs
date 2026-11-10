using Silk.NET.Vulkan;

namespace PowerPotion.Rendering.Vulkan;

/// <summary>
/// Descriptor manager: cube uniform sets, P2 scene sets (UBO + material SSBO) and a bindless
/// texture array. Bindless uses VK_EXT_descriptor_indexing when available; otherwise the array
/// is fully populated with the white fallback texture.
/// </summary>
public sealed unsafe class DescriptorManager : IDisposable
{
    private const int TextureCapacity = 1024;

    private readonly VulkanContext _context;
    private readonly int _frames;
    private DescriptorSetLayout _uniformLayout;
    private DescriptorSetLayout _sceneLayout;
    private DescriptorSetLayout _bindlessLayout;
    private DescriptorPool _pool;
    private DescriptorPool _scenePool;
    private DescriptorPool _bindlessPool;
    private DescriptorSet[] _sets = Array.Empty<DescriptorSet>();
    private DescriptorSet[] _sceneSets = Array.Empty<DescriptorSet>();
    private DescriptorSet _bindlessSet;
    private bool _disposed;

    public DescriptorManager(VulkanContext context, int frames)
    {
        _context = context;
        _frames = frames;
        CreateLayouts();
        CreatePools();
    }

    public DescriptorSetLayout Layout => _uniformLayout;
    public DescriptorSetLayout SceneLayout => _sceneLayout;
    public DescriptorSetLayout BindlessLayout => _bindlessLayout;
    public DescriptorSet[] Sets => _sets;
    public DescriptorSet[] SceneSets => _sceneSets;
    public DescriptorSet BindlessSet => _bindlessSet;
    public bool BindlessSupported => _context.SupportsBindless;
    public int TextureCapacityValue => TextureCapacity;

    private void CreateLayouts()
    {
        var cubeBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit,
        };

        var cubeInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &cubeBinding,
        };

        var result = _context.Vk.CreateDescriptorSetLayout(_context.Device, &cubeInfo, null, out _uniformLayout);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorSetLayout (uniform) failed: {result}");

        var sceneBindings = new[]
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = DescriptorType.StorageBuffer,
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

        fixed (DescriptorSetLayoutBinding* bindingPtr = sceneBindings)
        {
            var sceneInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)sceneBindings.Length,
                PBindings = bindingPtr,
            };

            var sceneResult = _context.Vk.CreateDescriptorSetLayout(
                _context.Device, &sceneInfo, null, out _sceneLayout);
            if (sceneResult != Result.Success)
                throw new InvalidOperationException($"vkCreateDescriptorSetLayout (scene) failed: {sceneResult}");
        }

        var textureBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = TextureCapacity,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        if (_context.SupportsBindless)
        {
            var flags = new[] { DescriptorBindingFlags.PartiallyBoundBit };
            fixed (DescriptorBindingFlags* flagPtr = flags)
            {
                var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                    BindingCount = 1,
                    PBindingFlags = flagPtr,
                };

                var bindlessInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    PNext = &bindingFlags,
                    BindingCount = 1,
                    PBindings = &textureBinding,
                };

                var bindlessResult = _context.Vk.CreateDescriptorSetLayout(
                    _context.Device, &bindlessInfo, null, out _bindlessLayout);
                if (bindlessResult != Result.Success)
                    throw new InvalidOperationException($"vkCreateDescriptorSetLayout (bindless) failed: {bindlessResult}");
            }
        }
        else
        {
            var bindlessInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &textureBinding,
            };

            var bindlessResult = _context.Vk.CreateDescriptorSetLayout(
                _context.Device, &bindlessInfo, null, out _bindlessLayout);
            if (bindlessResult != Result.Success)
                throw new InvalidOperationException($"vkCreateDescriptorSetLayout (texture array) failed: {bindlessResult}");
        }

        _context.SetObjectName(_bindlessLayout.Handle, ObjectType.DescriptorSetLayout, "bindless textures");
    }

    private void CreatePools()
    {
        var cubeSize = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = (uint)_frames,
        };

        var cubeInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)_frames,
            PoolSizeCount = 1,
            PPoolSizes = &cubeSize,
        };

        var result = _context.Vk.CreateDescriptorPool(_context.Device, &cubeInfo, null, out _pool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorPool (cube) failed: {result}");

        var sceneSizes = new[]
        {
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)_frames },
            new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = (uint)(_frames * 2) },
            new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)_frames },
        };

        fixed (DescriptorPoolSize* sizePtr = sceneSizes)
        {
            var sceneInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = (uint)_frames,
                PoolSizeCount = (uint)sceneSizes.Length,
                PPoolSizes = sizePtr,
            };

            var sceneResult = _context.Vk.CreateDescriptorPool(_context.Device, &sceneInfo, null, out _scenePool);
            if (sceneResult != Result.Success)
                throw new InvalidOperationException($"vkCreateDescriptorPool (scene) failed: {sceneResult}");
        }

        var bindlessSize = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = TextureCapacity,
        };

        var bindlessInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &bindlessSize,
        };

        var bindlessResult = _context.Vk.CreateDescriptorPool(_context.Device, &bindlessInfo, null, out _bindlessPool);
        if (bindlessResult != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorPool (bindless) failed: {bindlessResult}");
    }

    public void CreateUniformSets(IReadOnlyList<BufferAllocation?> buffers)
    {
        if (buffers.Count < _frames)
            throw new ArgumentException("Not enough uniform buffers.", nameof(buffers));

        var layouts = new DescriptorSetLayout[_frames];
        Array.Fill(layouts, _uniformLayout);
        fixed (DescriptorSetLayout* p = layouts)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pool,
                DescriptorSetCount = (uint)_frames,
                PSetLayouts = p,
            };

            _sets = new DescriptorSet[_frames];
            fixed (DescriptorSet* setPtr = _sets)
            {
                var result = _context.Vk.AllocateDescriptorSets(_context.Device, &allocInfo, setPtr);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkAllocateDescriptorSets (cube) failed: {result}");
            }
        }

        for (int i = 0; i < _frames; i++)
        {
            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = buffers[i]!.Buffer,
                Offset = 0,
                Range = 64,
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _sets[i],
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &bufferInfo,
            };

            _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
        }
    }

    public void CreateSceneSets(IReadOnlyList<BufferAllocation?> uniformBuffers, BufferAllocation materialBuffer,
        BufferAllocation lightBuffer, ImageView shadowView, Sampler shadowSampler)
    {
        if (uniformBuffers.Count < _frames)
            throw new ArgumentException("Not enough scene uniform buffers.", nameof(uniformBuffers));

        var layouts = new DescriptorSetLayout[_frames];
        Array.Fill(layouts, _sceneLayout);
        fixed (DescriptorSetLayout* p = layouts)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _scenePool,
                DescriptorSetCount = (uint)_frames,
                PSetLayouts = p,
            };

            _sceneSets = new DescriptorSet[_frames];
            fixed (DescriptorSet* setPtr = _sceneSets)
            {
                var result = _context.Vk.AllocateDescriptorSets(_context.Device, &allocInfo, setPtr);
                if (result != Result.Success)
                    throw new InvalidOperationException($"vkAllocateDescriptorSets (scene) failed: {result}");
            }
        }

        for (int i = 0; i < _frames; i++)
        {
            var uboInfo = new DescriptorBufferInfo
            {
                Buffer = uniformBuffers[i]!.Buffer,
                Offset = 0,
                Range = 448,
            };
            var ssboInfo = new DescriptorBufferInfo
            {
                Buffer = materialBuffer.Buffer,
                Offset = 0,
                Range = materialBuffer.Size,
            };
            var lightInfo = new DescriptorBufferInfo
            {
                Buffer = lightBuffer.Buffer,
                Offset = 0,
                Range = lightBuffer.Size,
            };
            var shadowInfo = new DescriptorImageInfo
            {
                ImageView = shadowView,
                Sampler = shadowSampler,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };

            var writes = new[]
            {
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sceneSets[i],
                    DstBinding = 0,
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer,
                    PBufferInfo = &uboInfo,
                },
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sceneSets[i],
                    DstBinding = 1,
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &ssboInfo,
                },
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sceneSets[i],
                    DstBinding = 2,
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &lightInfo,
                },
                new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sceneSets[i],
                    DstBinding = 3,
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &shadowInfo,
                },
            };

            fixed (WriteDescriptorSet* writePtr = writes)
                _context.Vk.UpdateDescriptorSets(_context.Device, (uint)writes.Length, writePtr, 0, null);
        }
    }

    public void CreateBindlessSet()
    {
        var layouts = new[] { _bindlessLayout };
        fixed (DescriptorSetLayout* p = layouts)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _bindlessPool,
                DescriptorSetCount = 1,
                PSetLayouts = p,
            };

            var result = _context.Vk.AllocateDescriptorSets(_context.Device, &allocInfo, out _bindlessSet);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkAllocateDescriptorSets (bindless) failed: {result}");
        }
    }

    public void UpdateBindlessTexture(int index, ImageView view, Sampler sampler)
    {
        if (_bindlessSet.Handle == 0 || (uint)index >= TextureCapacity) return;

        var imageInfo = new DescriptorImageInfo
        {
            ImageView = view,
            Sampler = sampler,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _bindlessSet,
            DstBinding = 0,
            DstArrayElement = (uint)index,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };

        _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    /// <summary>Fills the whole texture array with the white fallback (required when bindless is unavailable).</summary>
    public void FillBindlessDefaults(ImageView whiteView, Sampler sampler)
    {
        if (_context.SupportsBindless || _bindlessSet.Handle == 0) return;

        var infos = new DescriptorImageInfo[TextureCapacity];
        for (int i = 0; i < infos.Length; i++)
        {
            infos[i] = new DescriptorImageInfo
            {
                ImageView = whiteView,
                Sampler = sampler,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
        }

        fixed (DescriptorImageInfo* infoPtr = infos)
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _bindlessSet,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = TextureCapacity,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = infoPtr,
            };

            _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var vk = _context.Vk;
        if (_pool.Handle != 0) vk.DestroyDescriptorPool(_context.Device, _pool, null);
        if (_scenePool.Handle != 0) vk.DestroyDescriptorPool(_context.Device, _scenePool, null);
        if (_bindlessPool.Handle != 0) vk.DestroyDescriptorPool(_context.Device, _bindlessPool, null);
        if (_uniformLayout.Handle != 0) vk.DestroyDescriptorSetLayout(_context.Device, _uniformLayout, null);
        if (_sceneLayout.Handle != 0) vk.DestroyDescriptorSetLayout(_context.Device, _sceneLayout, null);
        if (_bindlessLayout.Handle != 0) vk.DestroyDescriptorSetLayout(_context.Device, _bindlessLayout, null);
    }
}
