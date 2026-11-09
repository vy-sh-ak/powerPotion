using Autodesk.Revit.DB;
using PowerPotion.Protocol;

namespace PowerPotion.Revit.Export;

/// <summary>
/// Converts a view's SunAndShadowSettings + the project location into a protocol SunUpdate —
/// the vendor's SunAndLocationParser behavior (docs/rea-report-revit-plugin.md §4):
/// place-and-time mode (lat/long/timezone/date) or explicit sun vector, plus true-north angle.
/// </summary>
public static class SunParser
{
    public static SunUpdate? Parse(Document doc, ElementId viewId)
    {
        try
        {
            var view = doc.GetElement(viewId) as View3D;
            if (view == null) return null;
            return Parse(doc, view);
        }
        catch { return null; }
    }

    public static SunUpdate? Parse(Document doc, View3D view)
    {
        try
        {
            var sun = view.SunAndShadowSettings;
            var result = new SunUpdate();

            // true north (radians) from the active project location
            try
            {
                var projectLocation = doc.ActiveProjectLocation;
                var position = projectLocation.GetProjectPosition(XYZ.Zero);
                result.NorthAngle = position.Angle;
            }
            catch { result.NorthAngle = 0; }

            // place: site location (or the sun settings' own place)
            try
            {
                var siteLocation = doc.ActiveProjectLocation.GetSiteLocation();
                result.Latitude = siteLocation.Latitude * 180.0 / Math.PI;
                result.Longitude = siteLocation.Longitude * 180.0 / Math.PI;
                result.TimeZoneHours = siteLocation.TimeZone;
            }
            catch { }

            // Revit 2026 still drives place-and-time through SunAndShadowSettings frames;
            // explicit static sun (Lighting mode) uses azimuth/altitude instead.
            if (sun.SunAndShadowType != SunAndShadowType.Lighting)
            {
                result.Mode = 0; // place & time
                var frame = sun.ActiveFrameTime;
                result.Day = frame.Day;
                result.Month = frame.Month;
                result.Year = frame.Year;
                result.Hour = (float)(frame.Hour + frame.Minute / 60.0 + frame.Second / 3600.0);
            }
            else
            {
                // statically placed sun (SunAndShadowType.Lighting)
                result.Mode = 1; // explicit vector
                var altitude = sun.Altitude * Math.PI / 180.0;
                var azimuth = sun.Azimuth * Math.PI / 180.0;
                // sun direction (towards the sun) in Revit space: X east, Y north, Z up
                var dir = new XYZ(
                    Math.Cos(altitude) * Math.Sin(azimuth),
                    Math.Cos(altitude) * Math.Cos(azimuth),
                    Math.Sin(altitude));
                var engine = EngineSpace.Vector(dir);
                result.SunX = (float)engine.Item1;
                result.SunY = (float)engine.Item2;
                result.SunZ = (float)engine.Item3;
            }
            return result;
        }
        catch { return null; }
    }
}
