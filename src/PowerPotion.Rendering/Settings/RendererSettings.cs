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

    public bool WhiteBackground;

    public RendererSettings Clone() => (RendererSettings)MemberwiseClone();
}
