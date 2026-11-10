using System.Runtime.InteropServices;
using PowerPotion.Protocol;
using PowerPotion.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace PowerPotion.Rendering.Scene;

[StructLayout(LayoutKind.Sequential)]
internal struct SceneVertex
{
    public float Px, Py, Pz;
    public float Nx, Ny, Nz;
    public float U, V;
    public float BU, BV;
}

internal sealed class GpuModel
{
    public GpuModel(int id, BufferAllocation vertex, BufferAllocation index, MaterialIndexGroupDto[] groups)
    {
        Id = id;
        Vertex = vertex;
        Index = index;
        Groups = groups;
    }

    public int Id { get; }
    public BufferAllocation Vertex { get; }
    public BufferAllocation Index { get; }
    public MaterialIndexGroupDto[] Groups { get; }

    public void Dispose()
    {
        Vertex.Destroy(immediate: true);
        Index.Destroy(immediate: true);
    }
}

internal readonly struct SceneDraw
{
    public SceneDraw(int modelIndex, float[] matrix, uint startIndex, uint indexCount,
        int frontMaterial, int backMaterial, bool doubleSided, bool transparent, float distance)
    {
        ModelIndex = modelIndex;
        Matrix = matrix;
        StartIndex = startIndex;
        IndexCount = indexCount;
        FrontMaterial = frontMaterial;
        BackMaterial = backMaterial;
        DoubleSided = doubleSided;
        Transparent = transparent;
        Distance = distance;
    }

    public int ModelIndex { get; }
    public float[] Matrix { get; }
    public uint StartIndex { get; }
    public uint IndexCount { get; }
    public int FrontMaterial { get; }
    public int BackMaterial { get; }
    public bool DoubleSided { get; }
    public bool Transparent { get; }
    public float Distance { get; }
}

/// <summary>GPU scene: uploads CPU-built scene data and builds the material-group draw list.</summary>
internal sealed unsafe class GpuScene : IDisposable
{
    private readonly List<GpuModel> _models = new();
    private bool _disposed;

    public GpuScene(VulkanContext context, VulkanAllocator allocator, MaterialBuffer materialBuffer,
        LightBuffer lightBuffer, TextureCache textures, SceneUploadData data, CameraState? camera)
    {
        Revision = data.Revision;
        MaterialDtos.AddRange(data.MaterialDtos);
        foreach (var pair in data.MaterialIndexById)
            MaterialIndexById[pair.Key] = pair.Value;

        int materialCount = Math.Min(MaterialDtos.Count, materialBuffer.Capacity);
        MaterialCount = materialCount;
        for (int i = 0; i < materialCount; i++)
        {
            var material = MaterialDtos[i];
            int diffuse = textures.Load(material.DiffuseTexture.File, srgb: true);
            int bump = textures.Load(material.BumpTexture.File, srgb: false);
            int rough = textures.Load(material.RoughnessTexture.File, srgb: false);
            int mask = textures.Load(material.MaskTexture.File, srgb: false);
            materialBuffer.Set(i, MaterialMapper.Build(material, diffuse, bump, rough, mask));
        }

        foreach (var upload in data.Models)
        {
            var vertexBuffer = allocator.CreateBuffer(
                (ulong)(upload.Vertices.Length * sizeof(SceneVertex)),
                BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit,
                $"model {upload.Id} vertices");
            allocator.UploadBuffer(vertexBuffer, MemoryMarshal.AsBytes(upload.Vertices.AsSpan()));

            var indexBuffer = allocator.CreateBuffer(
                (ulong)(upload.Indices.Length * sizeof(uint)),
                BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit,
                $"model {upload.Id} indices");
            allocator.UploadBuffer(indexBuffer, MemoryMarshal.AsBytes(upload.Indices.AsSpan()));

            _models.Add(new GpuModel(upload.Id, vertexBuffer, indexBuffer, upload.Groups));
            VertexBytes += (ulong)(upload.Vertices.Length * sizeof(SceneVertex));
            IndexBytes += (ulong)(upload.Indices.Length * sizeof(uint));
        }

        var modelById = new Dictionary<int, GpuModel>();
        foreach (var model in _models)
            modelById[model.Id] = model;
        var cameraPos = camera?.Position ?? new Vec3(9.5f, 3.2f, 9.5f);
        foreach (var instance in data.Instances)
        {
            if (!modelById.TryGetValue(instance.ModelId, out var model)) continue;
            int modelIndex = _models.IndexOf(model);
            var matrix = instance.Matrix is { Length: 16 } && instance.Matrix[15] != 0
                ? instance.Matrix
                : Identity();
            float dx = matrix[12] - cameraPos.X;
            float dy = matrix[13] - cameraPos.Y;
            float dz = matrix[14] - cameraPos.Z;
            float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            InstanceCount++;

            foreach (var group in model.Groups)
            {
                if (group.NumIndices == 0) continue;
                int front = ResolveMaterial((int)group.FrontFaceMaterialId);
                int back = group.BackFaceMaterialId == 0xFFFFFFFF
                    ? front
                    : ResolveMaterial((int)group.BackFaceMaterialId);
                var dto = MaterialDtos[front];
                bool doubleSided = dto.DoubleSided || back != front;
                bool transparent = dto.Opacity < 0.999f;
                Draws.Add(new SceneDraw(modelIndex, matrix, group.StartIndex, group.NumIndices,
                    front, back, doubleSided, transparent, distance));
            }
        }

        int lightCount = 0;
        foreach (var light in data.Lights)
        {
            if (lightCount >= lightBuffer.Capacity) break;
            int iesIndex = -1;
            float iesMaxAngle = MathF.PI * 0.5f;
            if (!string.IsNullOrWhiteSpace(light.IesPath))
                iesIndex = textures.LoadIes(light.IesPath, out iesMaxAngle);

            var direction = light.Rotation is { Length: >= 9 }
                ? Vec3.Normalize(new Vec3(light.Rotation[6], light.Rotation[7], light.Rotation[8]))
                : new Vec3(0, 0, -1);

            var gpu = new LightGpu
            {
                Position = new System.Numerics.Vector4(light.X, light.Y, light.Z, light.Distribution),
                Color = new System.Numerics.Vector4(light.R, light.G, light.B, Math.Max(light.Intensity, 0f)),
                Direction = new System.Numerics.Vector4(direction.X, direction.Y, direction.Z, light.Shape),
                Params = new System.Numerics.Vector4(light.BeamAngle, light.ShapeParam1, light.ShapeParam2, iesMaxAngle),
                Flags = new Int4(iesIndex, 1, 0, 0),
            };
            lightBuffer.Set(lightCount++, gpu);
        }
        LightCount = lightCount;
        TextureCount = textures.Count;
    }

    public long Revision { get; }
    public int MaterialCount { get; }
    public int TextureCount { get; }
    public int LightCount { get; }
    public int InstanceCount { get; }
    public ulong VertexBytes { get; }
    public ulong IndexBytes { get; }
    public List<SceneDraw> Draws { get; } = new();
    public List<MaterialDto> MaterialDtos { get; } = new();
    public Dictionary<int, int> MaterialIndexById { get; } = new();
    public IReadOnlyList<GpuModel> Models => _models;
    public bool HasGeometry => Draws.Count > 0;

    public int ResolveMaterial(int id) =>
        MaterialIndexById.TryGetValue(id, out var index) && index < MaterialCount ? index : 0;

    private static float[] Identity() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var model in _models)
            model.Dispose();
        _models.Clear();
    }
}
