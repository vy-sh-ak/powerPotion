using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using PowerPotion.Protocol;
using static PowerPotion.Revit.Export.LightParser;

namespace PowerPotion.Revit.Export;

/// <summary>
/// Converts Revit materials into protocol MaterialDto: graphics fallback (color/transparency)
/// plus appearance assets (generic + common Revit schemas, textures, bump, self-illumination)
/// and name-keyword material typing — mirroring Enscape's MaterialParser/AssetFinder pipeline
/// (docs/rea-report-revit-plugin.md §5).
/// </summary>
public sealed class RevitMaterialParser
{
    private readonly Dictionary<ElementId, MaterialDto> _cache = new();
    private readonly Dictionary<int, MaterialDto> _overrideCache = new();
    private readonly List<string> _textureSearchPaths;
    private readonly Document _doc;
    private Dictionary<string, string>? _textureFileIndex;
    private readonly Dictionary<string, string?> _textureResolution = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Dictionary<string, string>> IndexCache = new();

    /// <summary>Diagnostics sink (revit-sync.log).</summary>
    public Action<string>? Log;

    public RevitMaterialParser(Document doc, IEnumerable<string>? extraTexturePaths = null)
    {
        _doc = doc;
        _textureSearchPaths = new List<string>(extraTexturePaths ?? Array.Empty<string>());
    }

    /// <summary>
    /// Material for one exported face. Uses the face's effective appearance asset when the face has a
    /// paint/view override, otherwise the document material (Enscape's MaterialSourceSelector does the
    /// same via MaterialNode.HasOverriddenAppearance / GetAppearanceOverride).
    /// </summary>
    public MaterialDto? GetOrParse(MaterialNode node)
    {
        var dto = GetOrParse(node.MaterialId);
        if (!node.HasOverriddenAppearance && dto != null) return dto;

        var id = node.MaterialId == ElementId.InvalidElementId
            ? 700999999
            : 700000000 + (int)(node.MaterialId.Value % 1000000);
        if (_overrideCache.TryGetValue(id, out var cached)) return cached;

        try
        {
            var asset = node.HasOverriddenAppearance ? node.GetAppearanceOverride() : node.GetAppearance();
            if (asset == null) return dto;

            var result = new MaterialDto
            {
                Id = id,
                Name = (dto?.Name ?? "Material") + (node.HasOverriddenAppearance ? " (override)" : " (face)"),
                Opacity = dto?.Opacity ?? 1f,
                Roughness = dto?.Roughness ?? 0.5f,
                Metallic = dto?.Metallic ?? 0f,
                Specular = dto?.Specular ?? 0.04f,
            };
            try
            {
                if (node.Color is { } c)
                {
                    result.DiffuseR = c.Red / 255f;
                    result.DiffuseG = c.Green / 255f;
                    result.DiffuseB = c.Blue / 255f;
                }
            }
            catch { }

            ApplyAsset(result, asset);
            ApplyKeywordType(result);
            _overrideCache[id] = result;
            Log?.Invoke($"material {result.Id} '{result.Name}' from face appearance");
            return result;
        }
        catch
        {
            return dto;
        }
    }

    public MaterialDto? GetOrParse(ElementId materialId)
    {
        if (_cache.TryGetValue(materialId, out var cached))
            return cached;
        var dto = Parse(materialId);
        if (dto != null) _cache[materialId] = dto;
        return dto;
    }

    /// <summary>All materials parsed so far (deduplicated).</summary>
    public IEnumerable<MaterialDto> AllParsed() =>
        _cache.Values.Concat(_overrideCache.Values);

    private MaterialDto? Parse(ElementId materialId)
    {
        if (materialId == ElementId.InvalidElementId) return null;
        var result = new MaterialDto { Id = (int)materialId.Value };
        if (result.Id <= 0) result.Id = (int)(1000000 + materialId.Value);

        if (_doc.GetElement(materialId) is not Material revitMaterial)
        {
            result.Name = "(none)";
            return result;
        }

        result.Name = revitMaterial.Name;

        // --- graphics properties (always available) ---
        var color = revitMaterial.Color;
        result.DiffuseR = color.Red / 255f;
        result.DiffuseG = color.Green / 255f;
        result.DiffuseB = color.Blue / 255f;
        result.Opacity = 1f - (revitMaterial.Transparency / 100f);

        // --- appearance asset ---
        var assetId = revitMaterial.AppearanceAssetId;
        var schema = "(graphics only)";
        if (assetId != ElementId.InvalidElementId)
        {
            try
            {
                if (_doc.GetElement(assetId) is AppearanceAssetElement assetElement)
                    schema = ApplyAsset(result, assetElement.GetRenderingAsset());
            }
            catch
            {
                // malformed assets: keep graphics fallback
            }
        }

        ApplyKeywordType(result);

        Log?.Invoke($"material {result.Id} '{result.Name}' schema={schema} " +
                    $"rgb=({result.DiffuseR:0.##},{result.DiffuseG:0.##},{result.DiffuseB:0.##}) " +
                    $"tex='{result.DiffuseTexture.File}'");
        return result;
    }

    /// <summary>
    /// Schema → albedo property, from Enscape's MaterialParser literals (REA evidence:
    /// Enscape.Revit.Plugin.Materials.MaterialParser #US). The grouped bitmap for a Revit material is
    /// connected to this property, not always to generic_diffuse — this is why e.g. a MasonryCMU roof
    /// used to lose its terracotta texture and fall back to white.
    /// </summary>
    private static string AlbedoProperty(string schema) => schema switch
    {
        "CeramicSchema" => "ceramic_color",
        "ConcreteSchema" => "concrete_color",
        "GlazingSchema" => "glazing_transmittance_color",
        "HardwoodSchema" => "hardwood_color",
        "MasonryCMUSchema" => "masonrycmu_color",
        "MetallicPaintSchema" => "metallicpaint_base_color",
        "MetalSchema" => "metal_color",
        "MirrorSchema" => "mirror_tintcolor",
        "PlasticVinylSchema" => "plasticvinyl_color",
        "PrismGlazingSchema" => "glazing_transmission_color",
        "PrismLayeredSchema" => "layered_diffuse",
        "PrismMetalSchema" => "metal_f0",
        "PrismOpaqueSchema" => "opaque_albedo",
        "PrismTransparentSchema" => "transparent_color",
        "SolidGlassSchema" => "solidglass_transmittance",
        "StoneSchema" => "stone_color",
        "WallPaintSchema" => "wallpaint_color",
        _ => "generic_diffuse",
    };

    private string ApplyAsset(MaterialDto m, Asset asset)
    {
        var schema = FindString(asset, "BaseSchema") ?? "(unknown)";

        switch (schema)
        {
            case "GenericSchema":
                ApplyGeneric(m, asset);
                break;
            case "SolidGlassSchema":
                ApplySolidGlass(m, asset);
                break;
            case "MetalSchema":
                ApplyMetal(m, asset);
                break;
            case "WaterSchema":
                m.MaterialType = 2; // WATER
                m.Roughness = 0.05f;
                m.Opacity = 0.75f;
                m.Refraction = 1.33f;
                m.Specular = 0.9f;
                break;
            case "PrismOpaqueSchema":
                ApplyAlbedo(m, asset, "opaque_albedo");
                m.Roughness = (float)FindDouble(asset, "surface_roughness", 0.5);
                break;
            default:
                ApplyAlbedo(m, asset, AlbedoProperty(schema));
                break;
        }

        // bump maps are shared by nearly all schemas via connected properties
        var bump = FindTextureSlot(asset, "generic_bump_map") ?? FindTextureSlot(asset, "bumpmap_Bitmap");
        if (bump != null)
        {
            m.BumpTexture = bump;
            m.BumpAmount = (float)FindDouble(asset, "generic_bump_amount", 0.0) * 0.05f;
            if (m.BumpAmount <= 0)
                m.BumpAmount = (float)FindDouble(asset, "bumpmap_Depth", 0.1) * 0.05f;
            m.BumpMapKind = FindString(asset, "bumpmap_Type")?.Contains("Normal", StringComparison.OrdinalIgnoreCase) == true
                ? 2 /* NORMAL */ : 1 /* BUMP */;
        }
        return schema;
    }

    /// <summary>Reads the albedo color and (connected) bitmap of one schema property.</summary>
    private void ApplyAlbedo(MaterialDto m, Asset asset, string property)
    {
        if (FindColor(asset, property) is { } c)
        {
            m.DiffuseR = (float)c.Item1;
            m.DiffuseG = (float)c.Item2;
            m.DiffuseB = (float)c.Item3;
        }

        var texture = FindTextureSlot(asset, property);
        if (texture != null)
        {
            m.DiffuseTexture = texture;
            m.ImageFade = (float)FindDouble(asset, property + "_image_fade", 1.0);
        }
    }

    private void ApplyGeneric(MaterialDto m, Asset asset)
    {
        ApplyAlbedo(m, asset, "generic_diffuse");

        var gloss = FindDouble(asset, "generic_glossiness", 0.5);
        m.Roughness = (float)Math.Clamp(1.0 - gloss, 0.02, 1.0);
        m.Metallic = FindBoolean(asset, "generic_is_metal", false) ? 1f : 0f;

        var transparency = FindDouble(asset, "generic_transparency", 0.0);
        if (transparency > 0)
        {
            m.Opacity = (float)Math.Clamp(1.0 - transparency, 0.02, 1.0);
            m.Refraction = (float)FindDouble(asset, "generic_refraction_index", 1.5);
        }

        var lumens = FindDouble(asset, "generic_self_illum_luminance", 0.0);
        if (lumens > 0)
        {
            m.EmissiveStrength = (float)(lumens / 1000.0);
            if (FindColor(asset, "generic_self_illum_color") is { } ec)
            {
                m.EmissiveR = (float)ec.Item1; m.EmissiveG = (float)ec.Item2; m.EmissiveB = (float)ec.Item3;
            }
            else
            {
                m.EmissiveR = m.DiffuseR; m.EmissiveG = m.DiffuseG; m.EmissiveB = m.DiffuseB;
            }
        }

        if (FindColor(asset, "common_Tint_color") is { } tint)
        {
            m.TintR = (float)tint.Item1; m.TintG = (float)tint.Item2; m.TintB = (float)tint.Item3;
        }
    }

    private void ApplySolidGlass(MaterialDto m, Asset asset)
    {
        if (FindColor(asset, "solidglass_transmittance") is { } trans)
        {
            m.DiffuseR = (float)trans.Item1;
            m.DiffuseG = (float)trans.Item2;
            m.DiffuseB = (float)trans.Item3;
        }
        m.Roughness = (float)Math.Clamp(1.0 - FindDouble(asset, "solidglass_glossiness", 0.9), 0.01, 0.4);
        m.Refraction = (float)FindDouble(asset, "solidglass_refr_ior", 1.52);
        m.Opacity = 0.10f;
        m.Specular = 0.9f;
        m.IsSolidGlass = true;
    }

    private void ApplyMetal(MaterialDto m, Asset asset)
    {
        ApplyAlbedo(m, asset, "metal_color");
        m.Metallic = 1f;
        m.Specular = 0.9f;
        m.Roughness = 0.35f;
    }

    private void ApplyCommonColor(MaterialDto m, Asset asset)
    {
        // Kept for callers that only know a material has a color; all known schemas use ApplyAlbedo.
        ApplyAlbedo(m, asset, "generic_diffuse");
    }

    private static void ApplyKeywordType(MaterialDto m)
    {
        var name = m.Name.ToLowerInvariant();
        if (name.Contains("grass")) m.MaterialType = 1;                                  // GRASS
        else if (name.Contains("water") || name.Contains("river") || name.Contains("ocean")) m.MaterialType = 2; // WATER
        else if (name.Contains("foliage") || name.Contains("leaf")) m.MaterialType = 3;   // FOLIAGE
        else if (name.Contains("glass") || name.Contains("glazing")) m.Specular = 0.9f;
        else if (name.Contains("mirror")) m.Specular = 1f;
    }

    // ------------------------------------------------------------------
    // Texture slot resolution (connected properties in Revit 2026 API)
    // ------------------------------------------------------------------

    private TextureSlotDto? FindTextureSlot(Asset asset, string propertyName)
    {
        try
        {
            var prop = asset.FindByName(propertyName);
            if (prop == null) return null;

            // The bitmap path is an AssetPropertyString on the connected UnifiedBitmap asset. Prefer a
            // property literally named unifiedbitmap_Bitmap; otherwise keep the first resolvable string.
            AssetPropertyString? bitmap = null;
            foreach (var connected in prop.GetAllConnectedProperties())
            {
                if (connected is not AssetPropertyString s || string.IsNullOrWhiteSpace(s.Value)) continue;
                if (connected.Name == "unifiedbitmap_Bitmap") { bitmap = s; break; }
                if (bitmap == null && ResolveTexturePath(s.Value) != null) bitmap = s;
            }

            var single = prop.GetSingleConnectedAsset();
            var direct = single?.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
            if (single != null && (direct == null || string.IsNullOrWhiteSpace(direct.Value)))
            {
                foreach (var connected in single.GetAllConnectedProperties())
                {
                    if (connected is AssetPropertyString s2
                        && connected.Name == "unifiedbitmap_Bitmap"
                        && !string.IsNullOrWhiteSpace(s2.Value))
                    {
                        direct = s2;
                        break;
                    }
                }
            }
            if (direct != null && !string.IsNullOrWhiteSpace(direct.Value)
                && (bitmap == null || ResolveTexturePath(direct.Value) != null))
                bitmap = direct;

            if (bitmap == null || string.IsNullOrWhiteSpace(bitmap.Value))
            {
                Log?.Invoke($"texture slot '{propertyName}': no bitmap " +
                            $"(prop {prop.Type}, connected {prop.NumberOfConnectedProperties})");
                return null;
            }

            var raw = bitmap.Value.Trim();
            var path = ResolveTexturePath(raw);
            if (path == null)
            {
                Log?.Invoke($"texture unresolved on '{propertyName}': raw='{raw}' " +
                            $"(roots {_textureSearchPaths.Count})");
                return null;
            }

            var slot = new TextureSlotDto
            {
                File = path,
                Brightness = (float)FindConnectedDouble(prop, "unifiedbitmap_RGBAmount", 1.0),
                IsInverted = FindConnectedBoolean(prop, "unifiedbitmap_Invert", false),
                Source = 0,
                WcsType = 0,
            };

            // real-world scale / offset / rotation → UV transform (aliases differ per schema)
            double sx = AliasDouble(prop, "texture_RealWorldScaleX", "unifiedbitmap_RealWorldScaleX", 1);
            double sy = AliasDouble(prop, "texture_RealWorldScaleY", "unifiedbitmap_RealWorldScaleY", 1);
            double ox = AliasDouble(prop, "texture_RealWorldOffsetX", "unifiedbitmap_RealWorldOffsetX", 0);
            double oy = AliasDouble(prop, "texture_RealWorldOffsetY", "unifiedbitmap_RealWorldOffsetY", 0);
            double angle = AliasDouble(prop, "texture_WAngle", "unifiedbitmap_WAngle", 0);

            sx *= EngineSpace.FeetToMeters;
            sy *= EngineSpace.FeetToMeters;
            ox *= EngineSpace.FeetToMeters;
            oy *= EngineSpace.FeetToMeters;

            if (sx > 0 && sy > 0)
            {
                double rad = angle * Math.PI / 180.0; // Revit stores the angle in degrees
                double cos = Math.Cos(rad), sin = Math.Sin(rad);
                slot.Transformation = new float[]
                {
                    (float)(cos / sx), (float)(-sin / sy), 0,
                    (float)(sin / sx), (float)(cos / sy), 0,
                    (float)ox, (float)oy, 1,
                };
            }
            return slot;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"texture parse failed on '{propertyName}': {ex.Message}");
            return null;
        }
    }

    private static double AliasDouble(AssetProperty prop, string primary, string alias, double fallback)
    {
        var value = FindConnectedDouble(prop, primary, double.NaN);
        return double.IsNaN(value) ? FindConnectedDouble(prop, alias, fallback) : value;
    }

    private static double FindConnectedDouble(AssetProperty prop, string name, double fallback)
    {
        try
        {
            foreach (var connected in prop.GetAllConnectedProperties())
            {
                if (connected.Name == name)
                {
                    if (connected is AssetPropertyDouble d) return d.Value;
                    if (connected is AssetPropertyDistance dist) return dist.Value;
                }
            }
            var single = prop.GetSingleConnectedAsset();
            if (single != null)
            {
                var found = single.FindByName(name);
                if (found is AssetPropertyDouble d2) return d2.Value;
                if (found is AssetPropertyDistance dist2) return dist2.Value;
            }
        }
        catch { }
        return fallback;
    }

    private static bool FindConnectedBoolean(AssetProperty prop, string name, bool fallback)
    {
        try
        {
            foreach (var connected in prop.GetAllConnectedProperties())
                if (connected.Name == name && connected is AssetPropertyBoolean b) return b.Value;
            if (prop.GetSingleConnectedAsset()?.FindByName(name) is AssetPropertyBoolean b2) return b2.Value;
        }
        catch { }
        return fallback;
    }

    /// <summary>Resolves a stored Revit bitmap reference against the installed material libraries.</summary>
    private string? ResolveTexturePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var raw = Environment.ExpandEnvironmentVariables(value).Trim().Trim('"');
        if (raw.StartsWith(".\\", StringComparison.Ordinal)) raw = raw.Substring(2);
        if (_textureResolution.TryGetValue(raw, out var cached)) return cached;

        string? result = null;
        try
        {
            if (File.Exists(raw))
            {
                result = Path.GetFullPath(raw);
            }
            else if (!Path.IsPathRooted(raw))
            {
                foreach (var root in _textureSearchPaths)
                {
                    var candidate = Path.Combine(root, raw);
                    if (File.Exists(candidate)) { result = Path.GetFullPath(candidate); break; }
                }
            }

            if (result == null)
            {
                var name = Path.GetFileName(raw);
                if (!string.IsNullOrEmpty(name) && TextureFileIndex().TryGetValue(name, out var hit))
                    result = Path.GetFullPath(hit);
            }
        }
        catch { }

        _textureResolution[raw] = result;
        return result;
    }

    /// <summary>Filename → full path index of the installed Revit material libraries (cached per root set).</summary>
    private Dictionary<string, string> TextureFileIndex()
    {
        if (_textureFileIndex != null) return _textureFileIndex;

        var key = string.Join("|", _textureSearchPaths);
        lock (IndexCache)
        {
            if (IndexCache.TryGetValue(key, out var cached))
                return _textureFileIndex = cached;
        }

        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 12,
        };
        foreach (var root in _textureSearchPaths)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.*", options))
                    index.TryAdd(Path.GetFileName(file), file);
            }
            catch { }
        }

        lock (IndexCache) IndexCache[key] = index;
        _textureFileIndex = index;
        Log?.Invoke($"texture index: {index.Count} files in {_textureSearchPaths.Count} roots");
        return index;
    }
}
