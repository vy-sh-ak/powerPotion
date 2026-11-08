using System.IO;
using Autodesk.Revit.DB;
using PowerPotion.Protocol;

namespace PowerPotion.Revit.Export;

/// <summary>
/// Revit CustomExporter context that walks the 3D view and produces a protocol
/// WorldSnapshot — the same pipeline Enscape uses (IExportContext → ExportedWorld),
/// see docs/rea-report-revit-plugin.md §1–2: instance definitions deduplicated by
/// SymbolGeometryId, triangulation via PolymeshTopology, front/back materials,
/// photometric lights, and per-face UVs.
/// </summary>
public sealed class SceneExportContext : IExportContext
{
    private readonly Document _document;
    private readonly RevitMaterialParser _materials;
    private readonly WorldSnapshot _world;

    // element-level state
    private readonly List<float> _positions = new();
    private readonly List<float> _normals = new();
    private readonly List<float> _uvs = new();
    private readonly List<uint> _indices = new();
    private readonly List<MaterialIndexGroupDto> _groups = new();
    private readonly List<LightDto> _lights = new();
    private int _vertexBase;

    // instance definition dedup (family symbols): geometry key → model id
    private readonly Dictionary<string, int> _definitionIds = new();
    private readonly List<ModelDto> _models = new();
    private readonly List<InstanceDto> _instances = new();
    private readonly HashSet<int> _modelIdsUsed = new();

    // stack of geometry collectors for nested instances/links
    private readonly Stack<Collector> _collectors = new();
    private ElementId _currentMaterialId = ElementId.InvalidElementId;
    private MaterialNode? _currentMaterialNode;
    private Transform _currentTransform = Transform.Identity;
    private bool _isInstanceContext;
    private string? _currentInstanceKey;
    private int _currentElementModelId;
    private ulong _currentFamilyClass;

    private readonly Dictionary<string, ulong> _categoryClassMap = new();

    public WorldSnapshot World => _world;

    public SceneExportContext(Document document, View3D view, RevitMaterialParser materials)
    {
        _document = document;
        _materials = materials;
        _world = new WorldSnapshot
        {
            DocumentName = Path.GetFileName(document.Title ?? "document"),
            ViewName = view?.Name ?? "3D View",
        };
    }

    // ------------------------------------------------------------------
    // IExportContext
    // ------------------------------------------------------------------

    public bool Start() => true;
    public void Finish() { }
    public bool IsCanceled() => false;

    public RenderNodeAction OnViewBegin(ViewNode node)
    {
        try
        {
            var sun = SunParser.Parse(_document, node.ViewId);
            if (sun != null) _world.Sun = sun;
            var camera = CameraFromView(node);
            if (camera != null) _world.Camera = camera;
        }
        catch { }
        return RenderNodeAction.Proceed;
    }

    public void OnViewEnd(ElementId elementId) { }

    public RenderNodeAction OnElementBegin(ElementId elementId)
    {
        try
        {
            var element = _document.GetElement(elementId);
            if (element == null) return RenderNodeAction.Skip;
            var category = element.Category;
            if (category != null && category.CategoryType == CategoryType.Model && category.SubCategories != null)
            {
                // skip analysis/grid/legend-like categories quickly (Enscape's sanitizer set)
                var n = category.Name;
                if (n is "Cameras" or "Lines" or "Detail Items" or "<Sketch>") return RenderNodeAction.Skip;
            }
            if (element is RevitLinkInstance)
                return RenderNodeAction.Proceed; // link traversal begins
        }
        catch { }
        ResetElementState();
        _currentElementModelId = NextModelId();
        _currentFamilyClass = FamilyClassOf(elementId);
        return RenderNodeAction.Proceed;
    }

    public void OnElementEnd(ElementId elementId)
    {
        FlushElementGeometry(persistentKey: null, transformation: null);
    }

    public RenderNodeAction OnInstanceBegin(InstanceNode node)
    {
        var symbolGeometryId = node.GetSymbolGeometryId();
        _currentInstanceKey = symbolGeometryId?.AsUniqueIdentifier() ?? Guid.NewGuid().ToString("N");
        ResetElementState();
        _isInstanceContext = true;
        return RenderNodeAction.Proceed;
    }

    public void OnInstanceEnd(InstanceNode node)
    {
        // dedup: reuse the definition if this symbol was already exported
        if (_currentInstanceKey != null && _definitionIds.TryGetValue(_currentInstanceKey, out var existing))
        {
            // geometry already stored under `existing`; only add the instance placement
            var transform = node.GetTransform();
            _instances.Add(new InstanceDto
            {
                ModelId = existing,
                InstanceId = NextInstanceId(),
                Matrix = EngineSpace.Matrix(transform),
            });
            _modelIdsUsed.Add(existing);
        }
        else
        {
            var modelId = _currentElementModelId != 0 ? _currentElementModelId : NextModelId();
            if (_currentInstanceKey != null)
                _definitionIds[_currentInstanceKey] = modelId;
            FlushElementGeometry(persistentKey: _currentInstanceKey, transformation: node.GetTransform());
        }
        _isInstanceContext = false;
        _currentInstanceKey = null;
    }

    public RenderNodeAction OnLinkBegin(LinkNode node) => RenderNodeAction.Proceed;
    public void OnLinkEnd(LinkNode node) { }

    public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
    public void OnFaceEnd(FaceNode node) { }

    public void OnMaterial(MaterialNode node)
    {
        _currentMaterialNode = node;
        _currentMaterialId = node.MaterialId;
    }

    public void OnPolymesh(PolymeshTopology topology)
    {
        try
        {
            AppendPolymesh(topology);
        }
        catch { }
    }

    public void OnLight(LightNode node)
    {
        try
        {
            var light = LightParser.Parse(_document, node);
            if (light != null) _lights.Add(light);
        }
        catch { }
    }

    public void OnRPC(RPCNode node) { }

    // ------------------------------------------------------------------
    // Geometry assembly
    // ------------------------------------------------------------------

    private void ResetElementState()
    {
        _positions.Clear();
        _normals.Clear();
        _uvs.Clear();
        _indices.Clear();
        _groups.Clear();
        _vertexBase = 0;
        _currentMaterialId = ElementId.InvalidElementId;
        _currentMaterialNode = null;
    }

    private void AppendPolymesh(PolymeshTopology topology)
    {
        var points = topology.GetPoints();
        var facets = topology.GetFacets();
        var normalDistribution = topology.DistributionOfNormals;
        var usePerVertexNormals = normalDistribution == DistributionOfNormals.AtEachPoint;
        var numberOfNormals = topology.NumberOfNormals;
        int facetIndexCounter = 0;

        // material group start for this polymesh run
        var groupIdStart = (uint)_indices.Count;
        var matDto = _currentMaterialId != ElementId.InvalidElementId ? _materials.GetOrParse(_currentMaterialId) : null;
        int materialEngineId = matDto?.Id ?? 0;

        foreach (var facet in facets)
        {
            var i0 = facet.V1;
            var i1 = facet.V2;
            var i2 = facet.V3;

            var p0 = points[i0];
            var p1 = points[i1];
            var p2 = points[i2];

            XYZ n0, n1, n2;
            if (usePerVertexNormals)
            {
                n0 = topology.GetNormal(i0);
                n1 = topology.GetNormal(i1);
                n2 = topology.GetNormal(i2);
            }
            else if (numberOfNormals == 1)
            {
                var fn = topology.GetNormal(0);
                n0 = fn; n1 = fn; n2 = fn;
            }
            else
            {
                var fn = topology.GetNormal(Math.Min(facetIndexCounter, numberOfNormals - 1));
                n0 = fn; n1 = fn; n2 = fn;
            }
            facetIndexCounter++;

            var uvs = topology.GetUVs();
            var uv0 = i0 < uvs.Count ? uvs[i0] : UV.Zero;
            var uv1 = i1 < uvs.Count ? uvs[i1] : UV.Zero;
            var uv2 = i2 < uvs.Count ? uvs[i2] : UV.Zero;

            // vertex positions/normals in engine space
            var a = EngineSpace.Point(p0);
            var b = EngineSpace.Point(p1);
            var c = EngineSpace.Point(p2);
            var na = EngineSpace.Vector(n0);
            var nb = EngineSpace.Vector(n1);
            var nc = EngineSpace.Vector(n2);

            int baseIndex = _vertexBase;
            _positions.Add((float)a.Item1); _positions.Add((float)a.Item2); _positions.Add((float)a.Item3);
            _positions.Add((float)b.Item1); _positions.Add((float)b.Item2); _positions.Add((float)b.Item3);
            _positions.Add((float)c.Item1); _positions.Add((float)c.Item2); _positions.Add((float)c.Item3);
            _normals.Add((float)na.Item1); _normals.Add((float)na.Item2); _normals.Add((float)na.Item3);
            _normals.Add((float)nb.Item1); _normals.Add((float)nb.Item2); _normals.Add((float)nb.Item3);
            _normals.Add((float)nc.Item1); _normals.Add((float)nc.Item2); _normals.Add((float)nc.Item3);
            _uvs.Add((float)uv0.U); _uvs.Add((float)uv0.V);
            _uvs.Add((float)uv1.U); _uvs.Add((float)uv1.V);
            _uvs.Add((float)uv2.U); _uvs.Add((float)uv2.V);
            _vertexBase += 3;

            _indices.Add((uint)baseIndex);
            _indices.Add((uint)(baseIndex + 1));
            _indices.Add((uint)(baseIndex + 2));
        }

        _groups.Add(new MaterialIndexGroupDto
        {
            StartIndex = groupIdStart,
            NumIndices = (uint)_indices.Count - groupIdStart,
            FrontFaceMaterialId = (uint)materialEngineId,
            BackFaceMaterialId = 0xFFFFFFFF,
            VisibleLodStart = 0,
            VisibleLodEnd = 1000,
        });
    }

    private void FlushElementGeometry(string? persistentKey, Transform? transformation)
    {
        if (_positions.Count == 0) return;

        var modelId = _currentElementModelId;
        if (_isInstanceContext && persistentKey != null && _definitionIds.TryGetValue(persistentKey, out var existingId))
        {
            modelId = existingId;
        }

        _models.Add(new ModelDto
        {
            Id = modelId,
            FamilyClass = _currentFamilyClass,
            Positions = _positions.ToArray(),
            Normals = _normals.ToArray(),
            Uvs = _uvs.ToArray(),
            Indices = _indices.ToArray(),
            Groups = _groups.ToArray(),
        });
        _modelIdsUsed.Add(modelId);

        _instances.Add(new InstanceDto
        {
            ModelId = modelId,
            InstanceId = NextInstanceId(),
            Matrix = transformation != null
                ? EngineSpace.Matrix(transformation)
                : IdentityMatrix(),
        });
    }

    private static float[] IdentityMatrix() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    private int _nextModelId = 100;
    private int _nextInstanceId = 1000;
    private int NextModelId() => _nextModelId++;
    private int NextInstanceId() => _nextInstanceId++;

    private ulong FamilyClassOf(ElementId elementId)
    {
        try
        {
            var element = _document.GetElement(elementId);
            var category = element?.Category;
            if (category == null) return 0;
            if (_categoryClassMap.TryGetValue(category.Name, out var cls)) return cls;
            var mapped = category.Name switch
            {
                "Doors" => 1,       // DOOR
                "Walls" => 2,       // WALL
                "Ceilings" => 3,    // CEILING
                "Windows" => 4,     // WINDOW
                "Planting" => 5,    // PLANTING
                "Stairs" => 6,      // STAIRS
                "Railings" => 7,   // RAILINGS
                "Casework" => 8,   // CASEWORK
                "Lighting Fixtures" => 9, // LIGHTING_FIXTURES
                "Plumbing Fixtures" => 10,
                "Curtain Panels" => 11,
                "Curtain Wall Mullions" => 12,
                "Furniture" => 15,
                "Furniture Systems" => 16,
                "Specialty Equipment" => 17,
                "Columns" => 18,
                "Site" => 19,
                "Entourage" => 20,
                _ => 0,
            };
            _categoryClassMap[category.Name] = (ulong)mapped;
            return (ulong)mapped;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------
    // Camera from the exported view (CameraInfo — same trick Enscape uses)
    // ------------------------------------------------------------------

    private CameraUpdate? CameraFromView(ViewNode node)
    {
        try
        {
            var cameraInfo = node.GetCameraInfo();
            var view = _document.GetElement(node.ViewId) as View3D;
            if (view == null) return null;

            var orientation = view.GetOrientation();
            var eye = EngineSpace.Point(orientation.EyePosition);
            var forward = EngineSpace.Vector(orientation.ForwardDirection);
            var up = EngineSpace.Vector(orientation.UpDirection);

            var quat = EngineSpace.CameraQuaternion(forward, up);
            var camera = new CameraUpdate
            {
                PosX = (float)eye.Item1,
                PosY = (float)eye.Item2,
                PosZ = (float)eye.Item3,
                QuatX = quat.X, QuatY = quat.Y, QuatZ = quat.Z, QuatW = quat.W,
                AutoExposure = true,
            };

            if (cameraInfo != null)
            {
                if (cameraInfo.IsPerspective)
                {
                    // horizontal FOV = 2·atan(hExtent / (2·targetDistance)) — Enscape's formula
                    var target = cameraInfo.TargetDistance;
                    if (target > 0)
                    {
                        var hExtent = cameraInfo.HorizontalExtent;
                        camera.FovDegrees = (float)(2.0 * Math.Atan(hExtent / (2.0 * target)) * 180.0 / Math.PI);
                        camera.FovType = 0; // horizontal
                    }
                }
                else
                {
                    camera.Ortho = true;
                    // ortho extents in feet → meters
                    camera.OrthoLeft = (float)(-cameraInfo.HorizontalExtent / 2.0 * EngineSpace.FeetToMeters);
                    camera.OrthoRight = (float)(cameraInfo.HorizontalExtent / 2.0 * EngineSpace.FeetToMeters);
                    camera.OrthoBottom = (float)(-cameraInfo.VerticalExtent / 2.0 * EngineSpace.FeetToMeters);
                    camera.OrthoTop = (float)(cameraInfo.VerticalExtent / 2.0 * EngineSpace.FeetToMeters);
                }
            }
            return camera;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Collects the exported world (call after CustomExporter.Export).</summary>
    public WorldSnapshot ToWorldSnapshot()
    {
        _world.Models.AddRange(_models);
        _world.Instances.AddRange(_instances);
        _world.Lights.AddRange(_lights);
        foreach (var m in _materials.AllParsed())
            _world.Materials.Add(m);
        return _world;
    }

    private sealed class Collector
    {
        public List<float> Positions = new();
    }
}
