using PowerPotion.Protocol;
using PowerPotion.Rendering.Vulkan;

namespace PowerPotion.Rendering.Scene;

/// <summary>
/// Engine-space camera: perspective/orthographic projection, horizontal/vertical FOV,
/// two-point perspective (verticals stay vertical) and scene-bounds-derived near/far planes.
/// </summary>
internal sealed class CameraState
{
    public Vec3 Position = new(9.5f, 3.2f, 9.5f);
    public float[] Rotation = LookAtRotation(
        new Vec3(9.5f, 3.2f, 9.5f), new Vec3(0, 0, 0), new Vec3(0, 1, 0));
    public float FovDegrees = 60f;
    public int FovType;
    public bool Ortho;
    public float OrthoLeft = -5f;
    public float OrthoRight = 5f;
    public float OrthoBottom = -3f;
    public float OrthoTop = 3f;
    public bool TwoPointPerspective;
    public bool AutoExposure = true;
    public float Exposure;
    public float Near = 0.1f;
    public float Far = 500f;
    public uint Width = 1;
    public uint Height = 1;
    public float Aspect = 1f;
    public float TanHalfFovY = 1f;
    public float[] ViewProjection = Mat4.Identity();
    public float[] Projection = Mat4.Identity();
    public float JitterX;
    public float JitterY;

    /// <summary>Rotation actually used for the view matrix (yaw-only for two-point perspective).</summary>
    public float[] ViewRotation = Mat4.Identity();

    public bool HasCamera { get; private set; }

    public void Update(CameraUpdate camera)
    {
        if (camera.PosX == 0 && camera.PosY == 0 && camera.PosZ == 0 &&
            camera.QuatX == 0 && camera.QuatY == 0 && camera.QuatZ == 0 && camera.QuatW == 0)
            return;

        Position = new Vec3(camera.PosX, camera.PosY, camera.PosZ);
        Rotation = Mat4.FromQuaternion(camera.QuatX, camera.QuatY, camera.QuatZ, camera.QuatW);
        if (camera.FovDegrees > 0) FovDegrees = camera.FovDegrees;
        FovType = camera.FovType;
        Ortho = camera.Ortho;
        if (camera.OrthoRight > camera.OrthoLeft && camera.OrthoTop > camera.OrthoBottom)
        {
            OrthoLeft = camera.OrthoLeft;
            OrthoRight = camera.OrthoRight;
            OrthoBottom = camera.OrthoBottom;
            OrthoTop = camera.OrthoTop;
        }
        TwoPointPerspective = camera.TwoPointPerspective;
        AutoExposure = camera.AutoExposure;
        Exposure = camera.Exposure;
        HasCamera = true;
        Rebuild();
    }

    public void SetAspect(uint width, uint height)
    {
        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);
        Rebuild();
    }

    /// <summary>Derives near/far from the uploaded scene AABB (P4).</summary>
    public void SetSceneBounds(Vec3 min, Vec3 max)
    {
        float nearest = DistanceToBounds(Position, min, max);
        float farthest = FarthestCornerDistance(Position, min, max);
        Near = MathF.Max(0.05f, nearest * 0.5f);
        Far = MathF.Max(Near + 1f, farthest * 1.15f + 5f);
        Rebuild();
    }

    public void Rebuild()
    {
        Aspect = Width / (float)Height;
        float fovY = FovType == 0
            ? 2f * MathF.Atan(MathF.Tan(FovDegrees * MathF.PI / 360f) / MathF.Max(Aspect, 1e-4f))
            : FovDegrees * MathF.PI / 180f;
        TanHalfFovY = MathF.Tan(fovY * 0.5f);

        var viewRotation = Rotation;
        float verticalShift = 0f;
        if (!Ortho && TwoPointPerspective)
        {
            var forward = new Vec3(-Rotation[8], -Rotation[9], -Rotation[10]);
            var horizontal = new Vec3(forward.X, 0f, forward.Z);
            if (horizontal.Length > 1e-4f)
            {
                var level = Normalize(horizontal);
                viewRotation = LookAtRotation(Position, Position + level, new Vec3(0, 1, 0));
                float pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
                // Shift the projection instead of pitching the view: vertical lines stay vertical.
                verticalShift = MathF.Tan(pitch);
            }
        }
        ViewRotation = viewRotation;

        var view = Mat4.ViewFromCamera(Position, viewRotation);
        float[] projection;
        if (Ortho)
        {
            float left = OrthoLeft, right = OrthoRight, bottom = OrthoBottom, top = OrthoTop;
            if (right - left < 1e-4f || top - bottom < 1e-4f)
            {
                float halfHeight = TanHalfFovY * 10f;
                float halfWidth = halfHeight * Aspect;
                left = -halfWidth; right = halfWidth;
                bottom = -halfHeight; top = halfHeight;
            }
            projection = Mat4.Ortho(left, right, bottom, top, Near, Far);
        }
        else
        {
            projection = Mat4.PerspectiveForFov(FovDegrees, FovType, Aspect, Near, Far);
            if (verticalShift != 0f)
            {
                // Row 1, column 2 of the column-major projection: clipY += m[9] * zView.
                projection[9] = -verticalShift / MathF.Max(TanHalfFovY, 1e-5f);
            }
        }
        if (JitterX != 0f || JitterY != 0f)
        {
            projection[8] -= JitterX;
            projection[9] -= JitterY;
        }
        Projection = projection;
        ViewProjection = Mat4.Multiply(projection, view);
    }

    public void SetJitter(float x, float y)
    {
        if (JitterX == x && JitterY == y) return;
        JitterX = x;
        JitterY = y;
        Rebuild();
    }

    /// <summary>World-space corners of the camera frustum between two positive view distances.</summary>
    public Vec3[] FrustumCorners(float near, float far)
    {
        var rotation = ViewRotation;
        var right = new Vec3(rotation[0], rotation[1], rotation[2]);
        var up = new Vec3(rotation[4], rotation[5], rotation[6]);
        var forward = new Vec3(-rotation[8], -rotation[9], -rotation[10]);
        var corners = new Vec3[8];
        int index = 0;

        if (Ortho)
        {
            float halfWidth = (OrthoRight - OrthoLeft) * 0.5f;
            float halfHeight = (OrthoTop - OrthoBottom) * 0.5f;
            float offsetX = (OrthoLeft + OrthoRight) * 0.5f;
            float offsetY = (OrthoBottom + OrthoTop) * 0.5f;
            foreach (float z in new[] { near, far })
            {
                foreach (float sx in new[] { -1f, 1f })
                {
                    foreach (float sy in new[] { -1f, 1f })
                    {
                        corners[index++] = Position + forward * z +
                                            right * (offsetX + sx * halfWidth) +
                                            up * (offsetY + sy * halfHeight);
                    }
                }
            }
            return corners;
        }

        float halfW = TanHalfFovY * Aspect;
        foreach (float z in new[] { near, far })
        {
            foreach (float sx in new[] { -1f, 1f })
            {
                foreach (float sy in new[] { -1f, 1f })
                {
                    corners[index++] = Position + forward * z + right * (sx * halfW * z) + up * (sy * TanHalfFovY * z);
                }
            }
        }
        return corners;
    }

    private static float DistanceToBounds(Vec3 p, Vec3 min, Vec3 max)
    {
        float dx = MathF.Max(MathF.Max(min.X - p.X, 0f), p.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - p.Y, 0f), p.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - p.Z, 0f), p.Z - max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float FarthestCornerDistance(Vec3 p, Vec3 min, Vec3 max)
    {
        float farthest = 0f;
        foreach (float x in new[] { min.X, max.X })
        {
            foreach (float y in new[] { min.Y, max.Y })
            {
                foreach (float z in new[] { min.Z, max.Z })
                {
                    float dx = p.X - x, dy = p.Y - y, dz = p.Z - z;
                    farthest = MathF.Max(farthest, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
                }
            }
        }
        return farthest;
    }

    public static float[] LookAtRotation(Vec3 eye, Vec3 center, Vec3 up)
    {
        var forward = Normalize(Sub(center, eye));
        var right = Normalize(Cross(forward, up));
        var cameraUp = Cross(right, forward);

        // Camera local axes: X=right, Y=up, Z=-forward (right-handed, looking down -Z).
        return new float[]
        {
            right.X, right.Y, right.Z, 0,
            cameraUp.X, cameraUp.Y, cameraUp.Z, 0,
            -forward.X, -forward.Y, -forward.Z, 0,
            0, 0, 0, 1,
        };
    }

    private static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vec3 Normalize(Vec3 v)
    {
        float length = MathF.Sqrt(Dot(v, v));
        return length > 1e-6f ? new Vec3(v.X / length, v.Y / length, v.Z / length) : new Vec3(0, 0, -1);
    }
}
