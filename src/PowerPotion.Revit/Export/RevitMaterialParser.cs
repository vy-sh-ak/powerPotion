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
    private readonly List<string> _textureSearchPaths;
    private readonly Document _doc;

    public RevitMaterialParser(Document doc, IEnumerable<string>? extraTexturePaths = null)
    {
        _doc = doc;
        _textureSearchPaths = new List<string>(extraTexturePaths ?? Array.Empty<string>());
    }

    public MaterialDto? GetOrParse(MaterialNode node) => GetOrParse(node.MaterialId);

    public MaterialDto? GetOrParse(ElementId materialId)
    {
        if (_cache.TryGetValue(materialId, out var cached))
            return cached;
        var dto = Parse(materialId);
        if (dto != null) _cache[materialId] = dto;
        return dto;
    }

    /// <summary>All materials parsed so far (deduplicated).</summary>
    public IEnumerable<MaterialDto> AllParsed() => _cache.Values;

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
        if (assetId != ElementId.InvalidElementId)
        {
            try
            {
                if (_doc.GetElement(assetId) is AppearanceAssetElement assetElement)
                    ApplyAsset(result, assetElement.GetRenderingAsset());
            }
            catch
            {
                // malformed assets: keep graphics fallback
            }
        }

        ApplyKeywordType(result);
        return result;
    }

    private void ApplyAsset(MaterialDto m, Asset asset)
    {
        var schema = FindString(asset, "BaseSchema");

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
                if (FindColor(asset, "opaque_albedo") is { } albedo)
                {
                    m.DiffuseR = (float)albedo.Item1;
                    m.DiffuseG = (float)albedo.Item2;
                    m.DiffuseB = (float)albedo.Item3;
                }
                m.Roughness = (float)FindDouble(asset, "surface_roughness", 0.5);
                break;
            default:
                ApplyCommonColor(m, asset);
                break;
        }

        // bump maps are shared by nearly all schemas via connected properties
        var bump = FindTextureSlot(asset, "generic_bump_map");
        if (bump != null)
        {
            m.BumpTexture = bump;
            m.BumpAmount = (float)FindDouble(asset, "generic_bump_amount", 0.0) * 0.05f;
            m.BumpMapKind = FindString(asset, "bumpmap_Type")?.Contains("Normal", StringComparison.OrdinalIgnoreCase) == true
                ? 2 /* NORMAL */ : 1 /* BUMP */;
        }
    }

    private void ApplyGeneric(MaterialDto m, Asset asset)
    {
        if (FindColor(asset, "generic_diffuse") is { } diffuse)
        {
            m.DiffuseR = (float)diffuse.Item1;
            m.DiffuseG = (float)diffuse.Item2;
            m.DiffuseB = (float)diffuse.Item3;
        }
        var diffuseTexture = FindTextureSlot(asset, "generic_diffuse");
        if (diffuseTexture != null)
        {
            m.DiffuseTexture = diffuseTexture;
            m.ImageFade = (float)FindDouble(asset, "generic_diffuse_image_fade", 1.0);
        }

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
        if (FindColor(asset, "metal_color") is { } c)
        {
            m.DiffuseR = (float)c.Item1; m.DiffuseG = (float)c.Item2; m.DiffuseB = (float)c.Item3;
        }
        m.Metallic = 1f;
        m.Specular = 0.9f;
        m.Roughness = 0.35f;
    }

    private void ApplyCommonColor(MaterialDto m, Asset asset)
    {
        var names = new[]
        {
            "ceramic_color", "concrete_color", "hardwood_color", "masonrycmu_color",
            "metallicpaint_base_color", "plasticvinyl_color", "stone_color",
            "wallpaint_color", "mirror_tintcolor", "glazing_transmittance_color",
        };
        foreach (var n in names)
        {
            if (FindColor(asset, n) is { } c)
            {
                m.DiffuseR = (float)c.Item1; m.DiffuseG = (float)c.Item2; m.DiffuseB = (float)c.Item3;
                break;
            }
        }
        var tex = FindTextureSlot(asset, "generic_diffuse");
        if (tex != null) m.DiffuseTexture = tex;
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

            // find the connected bitmap string (unifiedbitmap_Bitmap lives on a connected asset)
            var connected = prop.GetAllConnectedProperties()
                .OfType<AssetPropertyString>()
                .Where(p => !string.IsNullOrEmpty(p.Value) && FileExistsAnywhere(p.Value))
                .FirstOrDefault();
            if (connected == null)
            {
                var single = prop.GetSingleConnectedAsset();
                if (single != null)
                {
                    connected = single.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (connected == null || string.IsNullOrEmpty(connected.Value) || !FileExistsAnywhere(connected.Value))
                        connected = null;
                }
            }
            if (connected == null) return null;

            var path = ResolveTexturePath(connected.Value);
            if (path == null) return null;

            var slot = new TextureSlotDto { File = path, Brightness = 1f, Source = 0, WcsType = 0 };

            // real-world scale / offset / rotation → UV transform
            double sx = FindConnectedDouble(prop, "texture_RealWorldScaleX", 1) * EngineSpace.FeetToMeters;
            double sy = FindConnectedDouble(prop, "texture_RealWorldScaleY", 1) * EngineSpace.FeetToMeters;
            double ox = FindConnectedDouble(prop, "texture_RealWorldOffsetX", 0) * EngineSpace.FeetToMeters;
            double oy = FindConnectedDouble(prop, "texture_RealWorldOffsetY", 0) * EngineSpace.FeetToMeters;
            double angle = FindConnectedDouble(prop, "texture_WAngle", 0);

            if (sx > 0 && sy > 0)
            {
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                slot.Transformation = new float[]
                {
                    (float)(cos / sx), (float)(-sin / sy), 0,
                    (float)(sin / sx), (float)(cos / sy), 0,
                    (float)ox, (float)oy, 1,
                };
            }
            return slot;
        }
        catch
        {
            return null;
        }
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

    private bool FileExistsAnywhere(string value)
    {
        if (File.Exists(value)) return true;
        foreach (var dir in _textureSearchPaths)
        {
            try { if (File.Exists(Path.Combine(dir, value))) return true; }
            catch { }
        }
        return false;
    }

    private string? ResolveTexturePath(string value)
    {
        if (File.Exists(value)) return Path.GetFullPath(value);
        foreach (var dir in _textureSearchPaths)
        {
            try
            {
                var candidate = Path.Combine(dir, value);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }
}
