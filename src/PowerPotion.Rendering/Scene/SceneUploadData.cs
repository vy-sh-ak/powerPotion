using PowerPotion.Protocol;

namespace PowerPotion.Rendering.Scene;

internal sealed class ModelUpload
{
    public int Id;
    public SceneVertex[] Vertices = Array.Empty<SceneVertex>();
    public uint[] Indices = Array.Empty<uint>();
    public MaterialIndexGroupDto[] Groups = Array.Empty<MaterialIndexGroupDto>();
}

/// <summary>
/// CPU-side scene build (vertices, indices, material mapping, instances). This runs on a
/// background task so a large Revit export does not block the render thread; the render thread
/// only performs the GPU upload when the data is ready.
/// </summary>
internal sealed class SceneUploadData
{
    public long Revision;
    public List<MaterialDto> MaterialDtos { get; } = new();
    public Dictionary<int, int> MaterialIndexById { get; } = new();
    public List<ModelUpload> Models { get; } = new();
    public List<InstanceDto> Instances { get; } = new();
    public List<LightDto> Lights { get; } = new();

    public static SceneUploadData Build(WorldSnapshot world)
    {
        var data = new SceneUploadData { Revision = world.Revision };
        data.MaterialDtos.Add(new MaterialDto
        {
            Id = 0,
            Name = "default",
            DiffuseR = 0.55f,
            DiffuseG = 0.55f,
            DiffuseB = 0.55f,
            Roughness = 0.8f,
        });
        data.MaterialIndexById[0] = 0;

        foreach (var material in world.Materials)
        {
            if (data.MaterialIndexById.ContainsKey(material.Id)) continue;
            data.MaterialIndexById[material.Id] = data.MaterialDtos.Count;
            data.MaterialDtos.Add(material);
        }

        foreach (var model in world.Models)
        {
            if (model.Positions.Length < 3 || model.Indices.Length == 0) continue;

            int vertexCount = model.Positions.Length / 3;
            var vertices = new SceneVertex[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int p = i * 3;
                int uv = i * 2;
                float nx = model.Normals.Length >= p + 3 ? model.Normals[p] : 0f;
                float ny = model.Normals.Length >= p + 3 ? model.Normals[p + 1] : 1f;
                float nz = model.Normals.Length >= p + 3 ? model.Normals[p + 2] : 0f;
                float u = model.Uvs.Length >= uv + 2 ? model.Uvs[uv] : 0f;
                float v = model.Uvs.Length >= uv + 2 ? model.Uvs[uv + 1] : 0f;
                float bu = model.BacksideUvs.Length == model.Uvs.Length && model.BacksideUvs.Length >= uv + 2
                    ? model.BacksideUvs[uv] : u;
                float bv = model.BacksideUvs.Length == model.Uvs.Length && model.BacksideUvs.Length >= uv + 2
                    ? model.BacksideUvs[uv + 1] : v;

                vertices[i] = new SceneVertex
                {
                    Px = model.Positions[p], Py = model.Positions[p + 1], Pz = model.Positions[p + 2],
                    Nx = nx, Ny = ny, Nz = nz,
                    U = u, V = v, BU = bu, BV = bv,
                };
            }

            var groups = model.Groups.Length > 0
                ? (MaterialIndexGroupDto[])model.Groups.Clone()
                : new[]
                {
                    new MaterialIndexGroupDto
                    {
                        StartIndex = 0,
                        NumIndices = (uint)model.Indices.Length,
                        FrontFaceMaterialId = 0,
                        BackFaceMaterialId = 0xFFFFFFFF,
                        VisibleLodStart = 0,
                        VisibleLodEnd = 1000,
                    },
                };

            data.Models.Add(new ModelUpload
            {
                Id = model.Id,
                Vertices = vertices,
                Indices = (uint[])model.Indices.Clone(),
                Groups = groups,
            });
        }

        data.Lights.AddRange(world.Lights);

        if (world.Instances.Count > 0)
        {
            data.Instances.AddRange(world.Instances);
        }
        else
        {
            for (int i = 0; i < data.Models.Count; i++)
            {
                data.Instances.Add(new InstanceDto
                {
                    ModelId = data.Models[i].Id,
                    InstanceId = i,
                    Matrix = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
                });
            }
        }

        return data;
    }
}
