using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerPotion.Host;

/// <summary>
/// Persisted visual settings, applied to the engine as console commands (CVars).
/// Names follow the engine's observed CVar surface (see docs/rea-report-rendererhost.md §2).
/// </summary>
public sealed class HostSettings
{
    // --- quality ---
    public int QualityLevel { get; set; } = 2;            // sys_spec 0..3 (draft..ultra)
    public bool HardwareRayTracing { get; set; } = true;  // r_hwrt
    public bool Dlss { get; set; } = true;                // r_dlss
    public bool RtSunShadows { get; set; } = true;        // r_rtSunShadows
    public bool RtArtificialLights { get; set; } = true;  // r_restir

    // --- sky & atmosphere ---
    public double SkyboxRotation { get; set; } = 0;        // r_skyboxRotation (degrees)
    public string SkyboxFile { get; set; } = "";           // r_skyboxFile + r_skyBoxFromFile 1
    public double SkyboxBrightness { get; set; } = 1;     // r_skyboxBrightnessCorrection
    public bool SkyboxUseSunSpot { get; set; } = true;     // r_skyBoxUseSunSpot
    public double CloudDensity { get; set; } = 0.5;        // r_cloudDensity
    public double CloudCirrus { get; set; } = 0.3;         // r_cloudCirrusAmount
    public double CloudVariety { get; set; } = 0.5;        // r_cloudVariety
    public double CloudContrails { get; set; } = 0;        // r_cloudContrails
    public double CloudOffsetX { get; set; } = 0;          // r_cloudOffsetX
    public double CloudOffsetZ { get; set; } = 0;          // r_cloudOffsetZ
    public double FogAmount { get; set; } = 0;             // r_fog
    public double FogHeight { get; set; } = 30;            // r_fogHeight
    public double StarBrightness { get; set; } = 0;       // r_starBrightness

    // --- sun & moon ---
    public double SunPower { get; set; } = 1;              // g_SunPower
    public double SunShadowSharpness { get; set; } = 1;   // r_sunShadowSharpness
    public double MoonSize { get; set; } = 1;              // r_moonSize

    // --- image / post ---
    public double AmbientAmount { get; set; } = 1;        // r_giAmbientAmount
    public double BloomIntensity { get; set; } = 0.25;    // r_bloomIntensity
    public double Saturation { get; set; } = 1;           // r_postSaturation
    public double ColorTemperature { get; set; } = 6500;  // r_postColorTemperature
    public bool AutoContrast { get; set; } = false;        // r_postAutoContrast
    public double Vignette { get; set; } = 0;             // r_vignette
    public double LensFlare { get; set; } = 0;            // r_lensFlare
    public double MotionBlur { get; set; } = 0;           // r_motionBlur
    public double ChromaticAberration { get; set; } = 0;  // r_chromaticAberration
    public double ExposureAdaptionSpeed { get; set; } = 1;// r_exposureAdaptionSpeed
    public double LightBrightness { get; set; } = 1;      // r_lightBrightness

    // --- artistic modes ---
    public bool WhiteBackground { get; set; } = false;     // r_whiteBackground

    // --- capture ---
    public double CaptureResolutionScale { get; set; } = 1; // r_captureResolution multiplier is applied by sender
    public bool ShowSafeFrame { get; set; } = false;       // r_showCaptureSafeFrame

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerPotion", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static HostSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new HostSettings();
        }
        catch { }
        return new HostSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }

    private static string I(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string I(bool v) => v ? "1" : "0";

    /// <summary>Commands that the engine only accepts before initialization (r_hwrt / r_dlss).</summary>
    public IEnumerable<string> ToPreInitCommands() => new[]
    {
        $"r_hwrt {I(HardwareRayTracing)}",
        $"r_dlss {I(Dlss)}",
    };

    /// <summary>Applies every setting to the engine as console commands.</summary>
    public IEnumerable<(string Command, bool Deferred)> ToCommands() => new[]
    {
        ($"sys_spec {I(QualityLevel)}", false),
        ($"r_rtSunShadows {I(RtSunShadows)}", true),
        ($"r_restir {I(RtArtificialLights)}", true),
        ($"r_skyboxRotation {I(SkyboxRotation)}", true),
        ($"r_skyboxBrightnessCorrection {I(SkyboxBrightness)}", true),
        ($"r_skyBoxUseSunSpot {I(SkyboxUseSunSpot)}", true),
        ($"r_skyBoxFromFile {(string.IsNullOrWhiteSpace(SkyboxFile) ? 0 : 1)}", true),
        ($"r_skyboxFile {(string.IsNullOrWhiteSpace(SkyboxFile) ? "\"\"" : $"\"{SkyboxFile.Replace("\"", "")}\"")}", true),
        ($"r_cloudDensity {I(CloudDensity)}", true),
        ($"r_cloudCirrusAmount {I(CloudCirrus)}", true),
        ($"r_cloudVariety {I(CloudVariety)}", true),
        ($"r_cloudContrails {I(CloudContrails)}", true),
        ($"r_cloudOffsetX {I(CloudOffsetX)}", true),
        ($"r_cloudOffsetZ {I(CloudOffsetZ)}", true),
        ($"r_fog {I(FogAmount)}", true),
        ($"r_fogHeight {I(FogHeight)}", true),
        ($"r_starBrightness {I(StarBrightness)}", true),
        ($"g_SunPower {I(SunPower)}", true),
        ($"r_sunShadowSharpness {I(SunShadowSharpness)}", true),
        ($"r_moonSize {I(MoonSize)}", true),
        ($"r_giAmbientAmount {I(AmbientAmount)}", true),
        ($"r_bloomIntensity {I(BloomIntensity)}", true),
        ($"r_postSaturation {I(Saturation)}", true),
        ($"r_postColorTemperature {I(ColorTemperature)}", true),
        ($"r_postAutoContrast {I(AutoContrast)}", true),
        ($"r_vignette {I(Vignette)}", true),
        ($"r_lensFlare {I(LensFlare)}", true),
        ($"r_motionBlur {I(MotionBlur)}", true),
        ($"r_chromaticAberration {I(ChromaticAberration)}", true),
        ($"r_exposureAdaptionSpeed {I(ExposureAdaptionSpeed)}", true),
        ($"r_lightBrightness {I(LightBrightness)}", true),
        ($"r_whiteBackground {I(WhiteBackground)}", true),
        ($"r_showCaptureSafeFrame {I(ShowSafeFrame)}", true),
    };
}
