using PowerPotion.Protocol;
using PowerPotion.Rendering.Vulkan;

namespace PowerPotion.Rendering.Scene;

/// <summary>P2 camera: engine-space position/quaternion, perspective projection and view-projection.</summary>
internal sealed class CameraState
{
    public Vec3 Position = new(9.5f, 3.2f, 9.5f);
    public float[] Rotation = LookAtRotation(
        new Vec3(9.5f, 3.2f, 9.5f), new Vec3(0, 0, 0), new Vec3(0, 1, 0));
    public float FovDegrees = 60f;
    public int FovType;
    public bool Ortho;
    public float Near = 0.1f;
    public float Far = 500f;
    public uint Width = 1;
    public uint Height = 1;
    public float Aspect = 1f;
    public float TanHalfFovY = 1f;
    public float[] ViewProjection = Mat4.Identity();

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
        HasCamera = true;
        Rebuild();
    }

    public void SetAspect(uint width, uint height)
    {
        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);
        Rebuild();
    }

    public void Rebuild()
    {
        Aspect = Width / (float)Height;
        float fovY = FovType == 0
            ? 2f * MathF.Atan(MathF.Tan(FovDegrees * MathF.PI / 360f) / MathF.Max(Aspect, 1e-4f))
            : FovDegrees * MathF.PI / 180f;
        TanHalfFovY = MathF.Tan(fovY * 0.5f);
        var view = Mat4.ViewFromCamera(Position, Rotation);
        var projection = Mat4.PerspectiveForFov(FovDegrees, FovType, Aspect, Near, Far);
        ViewProjection = Mat4.Multiply(projection, view);
    }

    /// <summary>World-space corners of the camera frustum between two positive view distances.</summary>
    public Vec3[] FrustumCorners(float near, float far)
    {
        var right = new Vec3(Rotation[0], Rotation[1], Rotation[2]);
        var up = new Vec3(Rotation[4], Rotation[5], Rotation[6]);
        var forward = new Vec3(-Rotation[8], -Rotation[9], -Rotation[10]);
        float halfWidth = TanHalfFovY * Aspect;
        var corners = new Vec3[8];
        int index = 0;
        foreach (float z in new[] { near, far })
        {
            foreach (float sx in new[] { -1f, 1f })
            {
                foreach (float sy in new[] { -1f, 1f })
                {
                    corners[index++] = Position + forward * z + right * (sx * halfWidth * z) + up * (sy * TanHalfFovY * z);
                }
            }
        }
        return corners;
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
