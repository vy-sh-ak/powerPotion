using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using PowerPotion.Protocol;

namespace PowerPotion.Revit.Export;

public static class LightParser
{
    public static LightDto? Parse(Document doc, LightNode node)
    {
        try
        {
            Asset? asset = null;
            try { asset = node.GetAsset(); } catch { }
            if (asset == null) return null;

            var light = new LightDto { Id = StableId(node) };

            var transform = node.GetTransform();
            var pos = EngineSpace.Point(transform.Origin);
            light.X = (float)pos.Item1;
            light.Y = (float)pos.Item2;
            light.Z = (float)pos.Item3;

            // --- shape ---
            var areaType = FindString(asset, "lightobjectareatype");
            switch (areaType)
            {
                case "Rectangle":
                    light.Shape = 3; // RECT
                    light.ShapeParam1 = (float)(FindDouble(asset, "rectangle_length", 0) * EngineSpace.FeetToMeters);
                    light.ShapeParam2 = (float)(FindDouble(asset, "rectangle_width", 0) * EngineSpace.FeetToMeters);
                    break;
                case "Disc":
                    light.Shape = 4; // CIRCLE
                    light.ShapeParam1 = (float)(FindDouble(asset, "disc_radius", 0) * EngineSpace.FeetToMeters);
                    break;
                default:
                    light.Shape = 1; // POINT
                    break;
            }
            if (FindBoolean(asset, "isLinearLight", false))
                light.Shape = 2; // LINE

            // --- distribution ---
            switch (FindString(asset, "distribution"))
            {
                case "Spherical": light.Distribution = 1; break;
                case "Spot": light.Distribution = 3; break;
                case "Hemispherical": light.Distribution = 2; break;
                case "Ies": light.Distribution = 3; break; // spot with IES profile
                default: light.Distribution = 1; break;
            }
            light.BeamAngle = (float)FindDouble(asset, "hotSpot", Math.PI / 6);

            var iesPath = FindString(asset, "lightProfileFileName");
            if (!string.IsNullOrEmpty(iesPath))
                light.IesPath = iesPath;

            // --- color ---
            double r = 1, g = 1, b = 1;
            var temperature = FindDouble(asset, "lightTemperature", 0);
            if (temperature > 0)
                (r, g, b) = KelvinToRgb(temperature);
            if (FindColor(asset, "filterColor") is { } filter)
            {
                r = filter.Item1; g = filter.Item2; b = filter.Item3;
            }
            light.R = (float)r; light.G = (float)g; light.B = (float)b; light.A = 1f;

            // --- intensity ---
            var units = FindString(asset, "intensityUnits");
            var value = FindDouble(asset, "intensityValue", 0);
            switch (units)
            {
                case "LuminousIntensity":
                    light.Intensity = (float)value;
                    break;
                case "LuminousFlux":
                    light.Intensity = (float)(value / (4.0 * Math.PI));
                    break;
                case "Illuminance":
                    var at = FindDouble(asset, "illuminanceAt", 3.0) * EngineSpace.FeetToMeters;
                    light.Intensity = (float)(value * at * at);
                    break;
                case "Wattage":
                    var eff = FindDouble(asset, "electricalEfficiency", 15.0);
                    light.Intensity = (float)(value * eff / (4.0 * Math.PI));
                    break;
                default:
                    light.Intensity = (float)(value > 0 ? value : 800);
                    break;
            }

            var bx = EngineSpace.Vector(transform.BasisX);
            var by = EngineSpace.Vector(transform.BasisY);
            var bz = EngineSpace.Vector(transform.BasisZ);
            light.Rotation = new float[]
            {
                (float)bx.Item1, (float)bx.Item2, (float)bx.Item3,
                (float)by.Item1, (float)by.Item2, (float)by.Item3,
                (float)bz.Item1, (float)bz.Item2, (float)bz.Item3,
            };
            return light;
        }
        catch
        {
            return null;
        }
    }

    private static int StableId(LightNode node) =>
        Math.Abs(node.GetHashCode()) % 900000 + 100000;

    private static (double, double, double) KelvinToRgb(double kelvin)
    {
        double t = kelvin / 100.0;
        double r, g, b;
        if (t <= 66) r = 255; else r = Math.Clamp(329.698727446 * Math.Pow(t - 60, -0.1332047592), 0, 255);
        if (t <= 66) g = Math.Clamp(99.4708025861 * Math.Log(t) - 161.1195681661, 0, 255);
        else g = Math.Clamp(288.1221695283 * Math.Pow(t - 60, -0.0755148492), 0, 255);
        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = Math.Clamp(138.5177312231 * Math.Log(t - 10) - 305.0447927307, 0, 255);
        return (r / 255.0, g / 255.0, b / 255.0);
    }

    // Revit 2026: assets expose FindByName/Get directly (no Asset.Properties member)

    internal static AssetProperty? Find(Asset asset, string name) => asset.FindByName(name);

    internal static double FindDouble(Asset asset, string name, double fallback) =>
        asset.FindByName(name) is AssetPropertyDouble d ? d.Value
        : asset.FindByName(name) is AssetPropertyDistance dist ? dist.Value
        : fallback;

    internal static bool FindBoolean(Asset asset, string name, bool fallback) =>
        asset.FindByName(name) is AssetPropertyBoolean b ? b.Value : fallback;

    internal static string? FindString(Asset asset, string name) =>
        asset.FindByName(name) is AssetPropertyString s ? s.Value : null;

    internal static (double, double, double)? FindColor(Asset asset, string name) =>
        asset.FindByName(name) is AssetPropertyDoubleArray4d c
            ? c.GetValueAsColor() is { } col
                ? (col.Red / 255.0, col.Green / 255.0, col.Blue / 255.0)
                : null
            : null;
}
