using System.IO;
using Silk.NET.Vulkan;
using StbImageSharp;
using PowerPotion.Rendering.Vulkan;

namespace PowerPotion.Rendering.Scene;

/// <summary>P2 texture cache: PNG/JPG decode (StbImageSharp), sRGB/linear images, sampler and bindless registration.</summary>
internal sealed unsafe class TextureCache : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanAllocator _allocator;
    private readonly DescriptorManager _descriptors;
    private readonly Dictionary<string, int> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Index, float MaxAngle)> _iesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TextureEntry> _textures = new();
    private readonly Sampler _sampler;
    private bool _disposed;

    public TextureCache(VulkanContext context, VulkanAllocator allocator, DescriptorManager descriptors)
    {
        _context = context;
        _allocator = allocator;
        _descriptors = descriptors;
        _sampler = CreateSampler();
        LoadWhite();
    }

    public int Count => _textures.Count;
    public Sampler Sampler => _sampler;
    public event Action<string>? Log;

    public bool NeedsLoad(string path, bool srgb)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var key = Path.GetFullPath(path) + "|" + (srgb ? "srgb" : "linear");
        return !_byKey.ContainsKey(key);
    }

    private void Write(string message) => Log?.Invoke(message);

    private Sampler CreateSampler()
    {
        float maxAniso = _context.Properties.Limits.MaxSamplerAnisotropy;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MipLodBias = 0f,
            AnisotropyEnable = _context.SamplerAnisotropyEnabled && maxAniso > 1f,
            MaxAnisotropy = Math.Min(8f, maxAniso),
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MinLod = 0f,
            MaxLod = 16f,
            BorderColor = BorderColor.FloatOpaqueBlack,
            UnnormalizedCoordinates = false,
        };

        var result = _context.Vk.CreateSampler(_context.Device, &info, null, out var sampler);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateSampler failed: {result}");
        _context.SetObjectName(sampler.Handle, ObjectType.Sampler, "material sampler");
        return sampler;
    }

    private void LoadWhite()
    {
        var allocation = _allocator.CreateImage(1, 1, Format.R8G8B8A8Srgb,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, "white texture");
        _allocator.UploadImage(allocation, 1, 1, new[] { new byte[] { 255, 255, 255, 255 } });
        var view = CreateView(allocation.Image, Format.R8G8B8A8Srgb, 1);
        var entry = new TextureEntry(allocation, view, _textures.Count);
        _textures.Add(entry);
        _descriptors.UpdateBindlessTexture(entry.Index, view, _sampler);
        _descriptors.FillBindlessDefaults(view, _sampler);
    }

    public int Load(string path, bool srgb)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return -1;
        if (_textures.Count >= _descriptors.TextureCapacityValue)
        {
            Write("texture cache full; using the material color");
            return -1;
        }
        var fullPath = Path.GetFullPath(path);
        var key = fullPath + "|" + (srgb ? "srgb" : "linear");
        if (_byKey.TryGetValue(key, out var cached)) return cached;

        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            if (image == null || image.Width <= 0 || image.Height <= 0)
            {
                Write($"texture failed (empty): {fullPath}");
                return -1;
            }

            var format = srgb ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
            var mips = BuildMipChain(image.Width, image.Height, image.Data);
            var allocation = _allocator.CreateImage(
                (uint)image.Width, (uint)image.Height, format,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                Path.GetFileName(fullPath), (uint)mips.Count);
            _allocator.UploadImage(allocation, (uint)image.Width, (uint)image.Height, mips);
            var view = CreateView(allocation.Image, format, (uint)mips.Count);

            var index = _textures.Count;
            _textures.Add(new TextureEntry(allocation, view, index));
            _byKey[key] = index;
            _descriptors.UpdateBindlessTexture(index, view, _sampler);
            Write($"texture {index}: {Path.GetFileName(fullPath)} {image.Width}x{image.Height} srgb={srgb}");
            return index;
        }
        catch (Exception ex)
        {
            Write($"texture failed: {fullPath}: {ex.Message}");
            return -1;
        }
    }

    private static List<byte[]> BuildMipChain(int width, int height, byte[] source)
    {
        var mips = new List<byte[]> { source };
        int w = width, h = height;
        var current = source;

        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2);
            int nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
            {
                int sy0 = Math.Min(h - 1, y * 2);
                int sy1 = Math.Min(h - 1, y * 2 + 1);
                for (int x = 0; x < nw; x++)
                {
                    int sx0 = Math.Min(w - 1, x * 2);
                    int sx1 = Math.Min(w - 1, x * 2 + 1);
                    int i00 = (sy0 * w + sx0) * 4;
                    int i10 = (sy0 * w + sx1) * 4;
                    int i01 = (sy1 * w + sx0) * 4;
                    int i11 = (sy1 * w + sx1) * 4;
                    int dst = (y * nw + x) * 4;
                    for (int c = 0; c < 4; c++)
                        next[dst + c] = (byte)((current[i00 + c] + current[i10 + c] +
                                                current[i01 + c] + current[i11 + c]) / 4);
                }
            }
            mips.Add(next);
            current = next;
            w = nw;
            h = nh;
        }

        return mips;
    }

    public int LoadIes(string path, out float maxAngle)
    {
        maxAngle = MathF.PI * 0.5f;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return -1;
        var fullPath = Path.GetFullPath(path);
        if (_iesByPath.TryGetValue(fullPath, out var cached))
        {
            maxAngle = cached.MaxAngle;
            return cached.Index;
        }
        if (_textures.Count >= _descriptors.TextureCapacityValue) return -1;

        var profile = IesProfile.Load(fullPath);
        if (profile == null) return -1;

        var bytes = new byte[profile.Values.Length * 4];
        for (int i = 0; i < profile.Values.Length; i++)
        {
            byte value = (byte)Math.Clamp(profile.Values[i] * 255f, 0f, 255f);
            int offset = i * 4;
            bytes[offset] = value;
            bytes[offset + 1] = value;
            bytes[offset + 2] = value;
            bytes[offset + 3] = 255;
        }

        var allocation = _allocator.CreateImage((uint)profile.Values.Length, 1, Format.R8G8B8A8Unorm,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            "IES " + Path.GetFileName(fullPath));
        _allocator.UploadImage(allocation, (uint)profile.Values.Length, 1, bytes);
        var view = CreateView(allocation.Image, Format.R8G8B8A8Unorm, 1);

        int index = _textures.Count;
        _textures.Add(new TextureEntry(allocation, view, index));
        _iesByPath[fullPath] = (index, profile.MaxAngleRadians);
        _descriptors.UpdateBindlessTexture(index, view, _sampler);
        maxAngle = profile.MaxAngleRadians;
        Write($"IES {index}: {Path.GetFileName(fullPath)} maxAngle={profile.MaxAngleRadians * 180f / MathF.PI:0.#}deg");
        return index;
    }

    private ImageView CreateView(Image image, Format format, uint mipLevels)
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
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = mipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        var result = _context.Vk.CreateImageView(_context.Device, &info, null, out var view);
        if (result != Result.Success)
            throw new InvalidOperationException($"texture vkCreateImageView failed: {result}");
        return view;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var vk = _context.Vk;
        foreach (var texture in _textures)
        {
            vk.DestroyImageView(_context.Device, texture.View, null);
            texture.Allocation.Destroy(immediate: true);
        }
        _textures.Clear();
        _allocator.CollectGarbage(0);
        vk.DestroySampler(_context.Device, _sampler, null);
    }

    private sealed class TextureEntry
    {
        public TextureEntry(ImageAllocation allocation, ImageView view, int index)
        {
            Allocation = allocation;
            View = view;
            Index = index;
        }

        public ImageAllocation Allocation { get; }
        public ImageView View { get; }
        public int Index { get; }
    }
}
