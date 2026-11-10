using System.Globalization;

namespace PowerPotion.Rendering.Settings;

/// <summary>Maps the host's existing CVar command lines onto <see cref="RendererSettings"/>.</summary>
public static class CvarAdapter
{
    public static bool TryApply(string command, RendererSettings settings)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var trimmed = command.Trim();
        int space = trimmed.IndexOf(' ');
        string key = space < 0 ? trimmed : trimmed[..space];
        string value = space < 0 ? "" : trimmed[(space + 1)..].Trim();

        switch (key)
        {
            case "sys_spec":
                if (TryInt(value, out var quality)) settings.QualityLevel = Math.Clamp(quality, 0, 3);
                return true;
            case "r_rtSunShadows":
                settings.RtSunShadows = ParseBool(value, true);
                return true;
            case "r_hwrt":
                settings.HardwareRayTracing = ParseBool(value, true);
                return true;
            case "r_dlss":
                settings.Dlss = ParseBool(value, true);
                return true;
            case "r_restir":
                settings.RtArtificialLights = ParseBool(value, true);
                return true;
            case "r_skyboxRotation":
                if (TryDouble(value, out var rotation)) settings.SkyboxRotationDegrees = rotation;
                return true;
            case "r_skyboxBrightnessCorrection":
                if (TryDouble(value, out var brightness)) settings.SkyboxBrightness = Math.Max(0, brightness);
                return true;
            case "r_skyBoxUseSunSpot":
                settings.SkyboxUseSunSpot = ParseBool(value, true);
                return true;
            case "r_skyBoxFromFile":
                settings.SkyboxFromFile = ParseBool(value, false);
                return true;
            case "r_skyboxFile":
                settings.SkyboxFile = Unquote(value);
                return true;
            case "g_SunPower":
                if (TryDouble(value, out var sunPower)) settings.SunPower = Math.Max(0, sunPower);
                return true;
            case "r_sunShadowSharpness":
                if (TryDouble(value, out var sharpness)) settings.SunShadowSharpness = Math.Clamp(sharpness, 0, 1);
                return true;
            case "r_moonSize":
                if (TryDouble(value, out var moonSize)) settings.MoonSize = Math.Max(0, moonSize);
                return true;
            case "r_giAmbientAmount":
                if (TryDouble(value, out var ambient)) settings.GiAmbientAmount = Math.Max(0, ambient);
                return true;
            case "r_starBrightness":
                if (TryDouble(value, out var stars)) settings.StarBrightness = Math.Clamp(stars, 0, 1);
                return true;
            case "r_lightBrightness":
                if (TryDouble(value, out var light)) settings.LightBrightness = Math.Max(0, light);
                return true;
            case "r_fog":
                if (TryDouble(value, out var fog)) settings.FogAmount = Math.Clamp(fog, 0, 1);
                return true;
            case "r_fogHeight":
                if (TryDouble(value, out var fogHeight)) settings.FogHeight = Math.Max(0, fogHeight);
                return true;
            case "r_whiteBackground":
                settings.WhiteBackground = ParseBool(value, false);
                return true;
            default:
                return false;
        }
    }

    private static bool ParseBool(string value, bool fallback) =>
        value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
        : value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
        : fallback;

    private static bool TryInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryDouble(string value, out double result) =>
        double.TryParse(Unquote(value), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static string Unquote(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        return v;
    }
}
