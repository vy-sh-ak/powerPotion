using PowerPotion.Protocol;
using PowerPotion.Rendering.Vulkan;

namespace PowerPotion.Rendering.Scene;

internal sealed class SunInfo
{
    public Vec3 Direction = new(0.4f, 0.8f, 0.3f);
    public Vec3 Color = new(2.6f, 2.5f, 2.3f);
    public float Intensity = 1f;
    public float Elevation;
    public Vec3 MoonDirection = new(-0.4f, 0.5f, -0.3f);
    public Vec3 MoonColor = new(0.05f, 0.07f, 0.12f);
}

/// <summary>
/// Solar position from the protocol's date/location fields (NOAA approximation) or an explicit
/// direction. Engine space is Y-up, north = -Z, east = +X; the project north angle is applied.
/// </summary>
internal static class SunPosition
{
    public static SunInfo Compute(SunUpdate sun, float sunPower)
    {
        Vec3 direction;
        if (sun.Mode == 1)
        {
            direction = Vec3.Normalize(new Vec3(sun.SunX, sun.SunY, sun.SunZ));
        }
        else if (sun.Mode == 0)
        {
            direction = FromPlaceAndTime(sun);
        }
        else
        {
            direction = new Vec3(0.4f, 0.8f, 0.3f);
        }

        if (direction.Length < 1e-5f) direction = new Vec3(0.4f, 0.8f, 0.3f);

        float elevation = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        float day = SmoothStep(-0.08f, 0.14f, direction.Y);
        var warm = new Vec3(1.0f, 0.42f, 0.18f);
        var white = new Vec3(1.0f, 0.96f, 0.9f);
        float warmth = SmoothStep(0.0f, 0.45f, direction.Y);
        var color = warm + (white - warm) * warmth;
        float intensity = Math.Max(0f, direction.Y) * Math.Max(sunPower, 0f) * 4.0f;

        // Moonlight: opposite the sun, cool and dim. It only contributes at night.
        var moonDir = Vec3.Normalize(new Vec3(-direction.X, Math.Max(-direction.Y, 0.2f), -direction.Z));
        var moonColor = new Vec3(0.045f, 0.065f, 0.11f) * (1f - day);

        return new SunInfo
        {
            Direction = direction,
            Color = color * intensity,
            Intensity = intensity,
            Elevation = elevation,
            MoonDirection = moonDir,
            MoonColor = moonColor,
        };
    }

    private static Vec3 FromPlaceAndTime(SunUpdate sun)
    {
        int year = Math.Clamp(sun.Year, 1900, 2200);
        int month = Math.Clamp(sun.Month, 1, 12);
        int day = Math.Clamp(sun.Day, 1, DateTime.DaysInMonth(year, month));
        var date = new DateTime(year, month, day);
        double dayOfYear = date.DayOfYear;
        double hour = sun.Hour;

        double gamma = 2.0 * Math.PI / 365.0 * (dayOfYear - 1 + (hour - 12.0) / 24.0);
        double declination =
            0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma) -
            0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma) -
            0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);

        double equationOfTime =
            229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma) -
                      0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));

        double solarTime = hour + equationOfTime / 60.0 + sun.TimeZoneHours - sun.Longitude / 15.0;
        double hourAngle = (solarTime - 12.0) * 15.0 * Math.PI / 180.0;
        double latitude = sun.Latitude * Math.PI / 180.0;

        double altitude = Math.Asin(Math.Clamp(
            Math.Sin(latitude) * Math.Sin(declination) +
            Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle), -1.0, 1.0));

        double azimuth = Math.Atan2(
            -Math.Sin(hourAngle),
            Math.Tan(declination) * Math.Cos(latitude) - Math.Sin(latitude) * Math.Cos(hourAngle));

        double worldAzimuth = azimuth + sun.NorthAngle;
        double horizontal = Math.Cos(altitude);
        return new Vec3(
            (float)(horizontal * Math.Sin(worldAzimuth)),
            (float)Math.Sin(altitude),
            (float)(-horizontal * Math.Cos(worldAzimuth)));
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
