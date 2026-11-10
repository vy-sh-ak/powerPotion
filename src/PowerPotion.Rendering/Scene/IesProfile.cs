using System.Globalization;
using System.IO;

namespace PowerPotion.Rendering.Scene;

/// <summary>
/// Minimal IES LM-63 reader. Produces a normalized 64-sample vertical intensity profile, which is
/// enough to shape spot beams in P3 (full 2D profiles are a later refinement).
/// </summary>
internal sealed class IesProfile
{
    public float[] Values { get; } = new float[64];
    public float MaxAngleRadians { get; private set; } = MathF.PI * 0.5f;

    public static IesProfile? Load(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            int tiltIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("TILT=", StringComparison.OrdinalIgnoreCase))
                {
                    tiltIndex = i;
                    break;
                }
            }
            if (tiltIndex < 0) return null;
            if (!lines[tiltIndex].Contains("NONE", StringComparison.OrdinalIgnoreCase))
                return null;

            var tokens = new List<string>();
            for (int i = tiltIndex + 1; i < lines.Length; i++)
            {
                var line = lines[i];
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line[..hash];
                foreach (var token in line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    tokens.Add(token);
            }

            int index = 0;
            double Next() => double.Parse(tokens[index++], CultureInfo.InvariantCulture);
            if (tokens.Count < 10) return null;

            _ = Next(); // lamps
            _ = Next(); // lumens per lamp
            _ = Next(); // candela multiplier
            int verticalCount = (int)Next();
            int horizontalCount = (int)Next();
            _ = Next(); // photometric type
            _ = Next(); // units type
            _ = Next(); // width
            _ = Next(); // length
            _ = Next(); // height

            if (verticalCount <= 0 || horizontalCount <= 0) return null;
            var verticalAngles = new double[verticalCount];
            for (int i = 0; i < verticalCount; i++) verticalAngles[i] = Next();
            var horizontalAngles = new double[horizontalCount];
            for (int i = 0; i < horizontalCount; i++) horizontalAngles[i] = Next();

            int valueCount = verticalCount * horizontalCount;
            if (tokens.Count < index + valueCount) return null;
            var candela = new double[valueCount];
            for (int i = 0; i < valueCount; i++) candela[i] = Next();

            // Average across horizontal angles, normalize by peak.
            var profile = new double[verticalCount];
            double max = 0;
            for (int v = 0; v < verticalCount; v++)
            {
                double sum = 0;
                for (int h = 0; h < horizontalCount; h++)
                    sum += candela[v * horizontalCount + h];
                profile[v] = sum / horizontalCount;
                max = Math.Max(max, profile[v]);
            }
            if (max <= 1e-9) return null;

            var result = new IesProfile();
            double maxAngle = Math.Max(1.0, verticalAngles[^1]);
            result.MaxAngleRadians = (float)(maxAngle * Math.PI / 180.0);

            for (int i = 0; i < result.Values.Length; i++)
            {
                double angle = maxAngle * i / (result.Values.Length - 1);
                int upper = 1;
                while (upper < verticalAngles.Length && verticalAngles[upper] < angle) upper++;
                if (upper >= verticalAngles.Length) upper = verticalAngles.Length - 1;
                int lower = Math.Max(0, upper - 1);
                double span = verticalAngles[upper] - verticalAngles[lower];
                double t = span > 1e-9 ? (angle - verticalAngles[lower]) / span : 0;
                double value = profile[lower] + (profile[upper] - profile[lower]) * t;
                result.Values[i] = (float)(value / max);
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}
