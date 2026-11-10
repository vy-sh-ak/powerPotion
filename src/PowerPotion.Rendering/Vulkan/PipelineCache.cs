using System.IO;
using Silk.NET.Vulkan;

namespace PowerPotion.Rendering.Vulkan;

/// <summary>
/// P1 pipeline cache: persistent VkPipelineCache, shader-module cache and pipeline-layout cache.
/// The cache file lives under %LOCALAPPDATA%\PowerPotion and is best-effort.
/// </summary>
public sealed unsafe class PipelineCache : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Dictionary<string, ShaderModule> _modules = new(StringComparer.Ordinal);
    private readonly List<PipelineLayout> _layouts = new();
    private readonly string _cachePath;
    private PipelineCacheHandle _cache;
    private bool _disposed;

    public PipelineCache(VulkanContext context, string? cachePath = null)
    {
        _context = context;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerPotion", "pipeline.cache");

        byte[]? initial = null;
        try
        {
            if (File.Exists(_cachePath))
                initial = File.ReadAllBytes(_cachePath);
        }
        catch { }

        var info = new PipelineCacheCreateInfo
        {
            SType = StructureType.PipelineCacheCreateInfo,
        };

        Result result;
        if (initial is { Length: > 0 })
        {
            fixed (byte* p = initial)
            {
                info.InitialDataSize = (nuint)initial.Length;
                info.PInitialData = p;
                result = _context.Vk.CreatePipelineCache(_context.Device, &info, null, out _cache.Value);
            }
        }
        else
        {
            result = _context.Vk.CreatePipelineCache(_context.Device, &info, null, out _cache.Value);
        }

        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreatePipelineCache failed: {result}");
    }

    public ulong CacheSize { get; private set; }

    public ShaderModule GetShaderModule(string key, byte[] spirv)
    {
        if (_modules.TryGetValue(key, out var existing)) return existing;

        fixed (byte* p = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)p,
            };

            var result = _context.Vk.CreateShaderModule(_context.Device, &info, null, out var module);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateShaderModule failed: {result}");
            _modules[key] = module;
            _context.SetObjectName(module.Handle, ObjectType.ShaderModule, key);
            return module;
        }
    }

    public PipelineLayout CreateLayout(DescriptorSetLayout setLayout) =>
        CreateLayout(new[] { setLayout }, null);

    public PipelineLayout CreateLayout(DescriptorSetLayout[] setLayouts, PushConstantRange[]? pushConstants)
    {
        var ranges = pushConstants ?? Array.Empty<PushConstantRange>();
        fixed (DescriptorSetLayout* layoutPtr = setLayouts)
        fixed (PushConstantRange* rangePtr = ranges)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = layoutPtr,
                PushConstantRangeCount = (uint)ranges.Length,
                PPushConstantRanges = rangePtr,
            };

            var result = _context.Vk.CreatePipelineLayout(_context.Device, &info, null, out var layout);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreatePipelineLayout failed: {result}");
            _layouts.Add(layout);
            _context.SetObjectName(layout.Handle, ObjectType.PipelineLayout, "main layout");
            return layout;
        }
    }

    public Pipeline CreateGraphicsPipeline(ref GraphicsPipelineCreateInfo info)
    {
        var createInfo = info;
        var result = _context.Vk.CreateGraphicsPipelines(
            _context.Device, _cache.Value, 1, &createInfo, null, out var pipeline);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateGraphicsPipelines failed: {result}");
        _context.SetObjectName(pipeline.Handle, ObjectType.Pipeline, "cube pipeline");
        return pipeline;
    }

    private void Save()
    {
        if (_cache.Value.Handle == 0) return;
        try
        {
            nuint size = 0;
            _context.Vk.GetPipelineCacheData(_context.Device, _cache.Value, ref size, null);
            if (size == 0) return;
            var bytes = new byte[(int)size];
            fixed (byte* p = bytes)
                _context.Vk.GetPipelineCacheData(_context.Device, _cache.Value, ref size, p);
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllBytes(_cachePath, bytes);
            CacheSize = (ulong)bytes.Length;
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Save();
        var vk = _context.Vk;
        foreach (var layout in _layouts)
            vk.DestroyPipelineLayout(_context.Device, layout, null);
        foreach (var module in _modules.Values)
            vk.DestroyShaderModule(_context.Device, module, null);
        if (_cache.Value.Handle != 0)
            vk.DestroyPipelineCache(_context.Device, _cache.Value, null);
    }

    private struct PipelineCacheHandle
    {
        public Silk.NET.Vulkan.PipelineCache Value;
    }
}
