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
                // `r_dlss` is the compatibility toggle for the FSR-style render-scale path.
                if (settings.RenderScale >= 0.999)
                    settings.RenderScale = settings.Dlss ? 0.75 : 1.0;
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
            case "r_cloudDensity":
                if (TryDouble(value, out var cloudDensity)) settings.CloudDensity = Math.Clamp(cloudDensity, 0, 1);
                return true;
            case "r_cloudCirrusAmount":
                if (TryDouble(value, out var cloudCirrus)) settings.CloudCirrus = Math.Clamp(cloudCirrus, 0, 1);
                return true;
            case "r_cloudVariety":
                if (TryDouble(value, out var cloudVariety)) settings.CloudVariety = Math.Clamp(cloudVariety, 0, 1);
                return true;
            case "r_cloudContrails":
                if (TryDouble(value, out var cloudContrails)) settings.CloudContrails = Math.Clamp(cloudContrails, 0, 1);
                return true;
            case "r_cloudOffsetX":
                if (TryDouble(value, out var cloudOffsetX)) settings.CloudOffsetX = cloudOffsetX;
                return true;
            case "r_cloudOffsetZ":
                if (TryDouble(value, out var cloudOffsetZ)) settings.CloudOffsetZ = cloudOffsetZ;
                return true;
            case "r_bloomIntensity":
                if (TryDouble(value, out var bloom)) settings.BloomIntensity = Math.Max(0, bloom);
                return true;
            case "r_postSaturation":
                if (TryDouble(value, out var saturation)) settings.Saturation = Math.Max(0, saturation);
                return true;
            case "r_postColorTemperature":
                if (TryDouble(value, out var temperature)) settings.ColorTemperature = Math.Clamp(temperature, 1000, 12000);
                return true;
            case "r_postAutoContrast":
                settings.AutoContrast = ParseBool(value, false);
                return true;
            case "r_vignette":
                if (TryDouble(value, out var vignette)) settings.Vignette = Math.Clamp(vignette, 0, 1);
                return true;
            case "r_lensFlare":
                if (TryDouble(value, out var flare)) settings.LensFlare = Math.Clamp(flare, 0, 1);
                return true;
            case "r_motionBlur":
                if (TryDouble(value, out var motionBlur)) settings.MotionBlur = Math.Clamp(motionBlur, 0, 1);
                return true;
            case "r_chromaticAberration":
                if (TryDouble(value, out var ca)) settings.ChromaticAberration = Math.Clamp(ca, 0, 1);
                return true;
            case "r_exposureAdaptionSpeed":
                if (TryDouble(value, out var adapt)) settings.ExposureAdaptionSpeed = Math.Max(0, adapt);
                return true;
            case "r_showCaptureSafeFrame":
                settings.ShowSafeFrame = ParseBool(value, false);
                return true;
            case "r_captureResolution":
                if (TryDouble(value, out var captureScale)) settings.CaptureResolutionScale = Math.Clamp(captureScale, 0.25, 4);
                return true;
            case "r_ssao":
                if (TryDouble(value, out var ssao)) settings.SsaoStrength = Math.Clamp(ssao, 0, 1);
                return true;
            case "r_ssaoRadius":
                if (TryDouble(value, out var ssaoRadius)) settings.SsaoRadius = Math.Clamp(ssaoRadius, 0.05, 200);
                return true;
            case "r_ssgi":
                if (TryDouble(value, out var ssgi)) settings.SsgiStrength = Math.Clamp(ssgi, 0, 1);
                return true;
            case "r_ssgiRadius":
                if (TryDouble(value, out var ssgiRadius)) settings.SsgiRadius = Math.Clamp(ssgiRadius, 0.001, 0.2);
                return true;
            case "r_ssgiFalloff":
                if (TryDouble(value, out var ssgiFalloff)) settings.SsgiFalloff = Math.Max(0.01, ssgiFalloff);
                return true;
            case "r_renderScale":
                if (TryDouble(value, out var renderScale)) settings.RenderScale = Math.Clamp(renderScale, 0.5, 1.0);
                return true;
            case "g_windStrength":
                if (TryDouble(value, out var wind)) settings.WindStrength = Math.Max(0, wind);
                return true;
            case "g_windDirection":
                if (TryDouble(value, out var windDirection)) settings.WindDirection = windDirection;
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
