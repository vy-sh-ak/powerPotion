using Autodesk.Revit.DB;

namespace PowerPotion.Revit.Export;

/// <summary>
/// Converts Revit geometry (feet, Z-up, X-east/Y-north) into PowerPotion protocol space
/// (meters, Y-up, X-east, Z-south — validated against the engine's render behavior).
/// </summary>
public static class EngineSpace
{
    public const double FeetToMeters = 0.3048;

    /// <summary>Maps a Revit point (ft) to engine space (m).</summary>
    public static (double X, double Y, double Z) Point(XYZ p) => (p.X * FeetToMeters, p.Z * FeetToMeters, -p.Y * FeetToMeters);

    /// <summary>Maps a Revit direction to engine space (unit-preserving).</summary>
    public static (double X, double Y, double Z) Vector(XYZ v) => (v.X, v.Z, -v.Y);

    /// <summary>Engine-space 4x4 column-major instance matrix from a Revit transform (feet→m).</summary>
    public static float[] Matrix(Transform t)
    {
        // Rotation: basis images mapped through the axis conversion (Q·R·Qᵀ where Q is the
        // axis map — equals mapping each basis vector). Translation: mapped origin.
        var bx = Vector(t.BasisX);
        var by = Vector(t.BasisY);
        var bz = Vector(t.BasisZ);
        var o = Point(t.Origin);
        // Column-major: col j = image of local axis j; col 3 = translation.
        return new float[]
        {
            (float)bx.Item1, (float)bx.Item2, (float)bx.Item3, 0f,
            (float)by.Item1, (float)by.Item2, (float)by.Item3, 0f,
            (float)bz.Item1, (float)bz.Item2, (float)bz.Item3, 0f,
            (float)o.Item1, (float)o.Item2, (float)o.Item3, 1f,
        };
    }

    /// <summary>
    /// Camera orientation quaternion (x,y,z,w) for an engine-space view direction and up vector.
    /// Camera basis (validated): columns [right=cross(f,u), up, -forward] as a GLM mat3 → quat.
    /// </summary>
    public static (float X, float Y, float Z, float W) CameraQuaternion((double, double, double) forward, (double, double, double) up)
    {
        var (fx, fy, fz) = forward;
        var (ux, uy, uz) = up;
        // right = cross(f, u) — verified against a live engine render (top-down camera).
        var rx = fy * uz - fz * uy;
        var ry = fz * ux - fx * uz;
        var rz = fx * uy - fy * ux;
        // Re-orthogonalize up: up' = cross(right, forward)
        var ux2 = ry * fz - rz * fy;
        var uy2 = rz * fx - rx * fz;
        var uz2 = rx * fy - ry * fx;
        // Matrix columns [right, up', -forward] (column-major → rows for the quat formula):
        double m00 = rx, m10 = ry, m20 = rz;         // column 0
        double m01 = ux2, m11 = uy2, m21 = uz2;      // column 1
        double m02 = -fx, m12 = -fy, m22 = -fz;     // column 2
        return Mat3ToQuaternion(m00, m01, m02, m10, m11, m12, m20, m21, m22);
    }

    /// <summary>Quaternion (x,y,z,w) from a column-major 3x3 rotation matrix.</summary>
    private static (float, float, float, float) Mat3ToQuaternion(
        double m00, double m01, double m02,
        double m10, double m11, double m12,
        double m20, double m21, double m22)
    {
        double tr = m00 + m11 + m22;
        double x, y, z, w;
        if (tr > 0)
        {
            var s = Math.Sqrt(tr + 1.0) * 2;
            w = 0.25 * s;
            x = (m21 - m12) / s;
            y = (m02 - m20) / s;
            z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            var s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
            w = (m21 - m12) / s;
            x = 0.25 * s;
            y = (m01 + m10) / s;
            z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            var s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
            w = (m02 - m20) / s;
            x = (m01 + m10) / s;
            y = 0.25 * s;
            z = (m12 + m21) / s;
        }
        else
        {
            var s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
            w = (m10 - m01) / s;
            x = (m02 + m20) / s;
            y = (m12 + m21) / s;
            z = 0.25 * s;
        }
        var len = Math.Sqrt(x * x + y * y + z * z + w * w);
        return ((float)(x / len), (float)(y / len), (float)(z / len), (float)(w / len));
    }
}
