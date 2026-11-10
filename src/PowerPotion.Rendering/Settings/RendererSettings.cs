namespace PowerPotion.Rendering.Settings;

/// <summary>Typed renderer parameters populated from the existing CVar surface.</summary>
public sealed class RendererSettings
{
    public int QualityLevel = 2;
    public bool RtSunShadows = true;
    public bool HardwareRayTracing = true;
    public bool Dlss = true;
    public bool RtArtificialLights = true;

    public double SkyboxRotationDegrees;
    public bool SkyboxFromFile;
    public string SkyboxFile = "";
    public double SkyboxBrightness = 1;
    public bool SkyboxUseSunSpot = true;

    public double SunPower = 1;
    public double SunShadowSharpness = 1;
    public double MoonSize = 1;
    public double GiAmbientAmount = 1;
    public double StarBrightness;
    public double LightBrightness = 1;
    public double FogAmount;
    public double FogHeight = 30;

    // P5 post / capture surface.
    public bool AutoExposure = true;
    public double Exposure;
    public double ExposureAdaptionSpeed = 1;
    public double BloomIntensity = 0.25;
    public double Saturation = 1;
    public double ColorTemperature = 6500;
    public bool AutoContrast;
    public double Vignette;
    public double LensFlare;
    public double MotionBlur;
    public double ChromaticAberration;
    public bool ShowSafeFrame;
    public double CaptureResolutionScale = 1;

    // Atmosphere controls are accepted now and consumed by P6.
    public double CloudDensity = 0.5;
    public double CloudCirrus = 0.3;
    public double CloudVariety = 0.5;
    public double CloudContrails;
    public double CloudOffsetX;
    public double CloudOffsetZ;
    public double WindStrength = 0.5;
    public double WindDirection;

    // P6 quality.
    public double SsaoStrength = 0.6;
    public double SsaoRadius = 5.0;
    public double SsgiStrength = 0.6;
    public double SsgiRadius = 0.03;
    public double SsgiFalloff = 4.0;
    public double RenderScale = 1.0;

    public bool WhiteBackground;

    public RendererSettings Clone() => (RendererSettings)MemberwiseClone();
}
