using System.Runtime.InteropServices;
using System.Windows.Threading;
using PowerPotion.Protocol;

namespace PowerPotion.Host.Engine;

/// <summary>
/// smooth free-form navigation for the embedded renderer.
///
/// • Fly mode  – 6-DOF: W/A/S/D move, Q/E down/up, right-mouse look, wheel dollies, Shift fast, Ctrl slow.
/// • Walk mode – eye height fixed at 1.7 m: W/A/S/D + look, wheel walks forward/back, Q/E disabled.
///
/// Keyboard input is polled at 60 Hz; right-button look and the wheel are captured with a low-level
/// mouse hook (and swallowed while the cursor is over the render surface, so the engine cannot apply
/// a second navigation step, and the settings sidebar still scrolls normally).
/// The first interaction detaches from the Revit camera; the toolbar re-attaches to the latest view.
/// </summary>
public sealed class NavigationController : IDisposable
{
    public enum NavigationMode { Fly, Walk }

    private const float WalkEyeHeightFallback = 1.7f;
    private const float FlySpeed = 6.0f;      // m/s
    private const float WalkSpeed = 2.4f;     // m/s

    private readonly EngineBridge _engine;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _timer;
    private IntPtr _renderHwnd = IntPtr.Zero;

    private float _x, _y, _z;
    private float _yaw, _pitch;
    private float _fov = 60f;
    private float _walkEyeY = WalkEyeHeightFallback;
    private float _groundY;
    private bool _hasGround;
    private bool _hasCamera;
    private bool _followRevit = true;
    private bool _engineReady;
    private NavigationMode _mode = NavigationMode.Fly;

    private CameraUpdate? _lastRevitCamera;

    // input state
    private long _wheelDelta;
    private IntPtr _mouseHook;
    private Win32.LowLevelMouseProc? _mouseProc;

    public event Action? FollowChanged;
    public event Action? ModeChanged;

    public bool FollowRevit => _followRevit;
    public NavigationMode Mode => _mode;

    public (float X, float Y, float Z, float Yaw, float Pitch) Pose => (_x, _y, _z, _yaw, _pitch);
    public bool Enabled
    {
        get => _engineReady && _timer != null;
        set
        {
            _engineReady = value;
            if (value) Start();
            else StopTimer();
        }
    }

    public NavigationController(EngineBridge engine)
    {
        _engine = engine;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void SetRenderWindow(IntPtr hwnd) => _renderHwnd = hwnd;

    /// <summary>Floor height (engine Y) under the camera, from the last world snapshot.</summary>
    public void SetGroundY(float y)
    {
        _groundY = y;
        _hasGround = true;
    }

    private void Start()
    {
        if (_timer != null) return;
        InstallMouseHook();
        _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        RemoveMouseHook();
    }

    public void Dispose()
    {
        StopTimer();
        _followRevit = true;
    }

    // ------------------------------------------------------------------
    // Camera state
    // ------------------------------------------------------------------

    /// <summary>Applies a camera coming from Revit (used while "follow Revit" is enabled).</summary>
    public void OnRevitCamera(CameraUpdate camera, bool applyImmediately = true)
    {
        _lastRevitCamera = camera;
        if (!_followRevit) return;
        if (applyImmediately) FromCamera(camera);
        if (_engineReady) _engine.ApplyCamera(camera);
    }

    public void SetFollowRevit(bool follow)
    {
        if (_followRevit == follow) return;
        _followRevit = follow;
        if (follow && _lastRevitCamera != null)
            FromCamera(_lastRevitCamera);
        FollowChanged?.Invoke();
    }

    public void SetMode(NavigationMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        if (mode == NavigationMode.Walk)
        {
            // Place the eye 1.7 m above the scene floor under the camera (computed from the world
            // snapshot) — the old code guessed 0.2 m and the first move snapped the height.
            SyncFromEngine();
            _walkEyeY = _hasGround ? _groundY + 1.7f : _y;
            _y = _walkEyeY;
            ApplyCurrent();
        }
        var fly = mode == NavigationMode.Fly ? 1 : 0;
        _engine.ExecuteConsoleCommand($"sp_fly {fly}", false);
        HostLog.Write($"nav mode: {(mode == NavigationMode.Fly ? "fly" : "walk")} (sp_fly {fly}, eyeY={_walkEyeY:0.##})");
        ModeChanged?.Invoke();
    }

    /// <summary>Pushes the current mode to the engine (called once the engine is ready).</summary>
    public void ApplyModeToEngine()
    {
        var fly = _mode == NavigationMode.Fly ? 1 : 0;
        _engine.ExecuteConsoleCommand($"sp_fly {fly}", false);
        HostLog.Write($"nav mode: {(_mode == NavigationMode.Fly ? "fly" : "walk")} (sp_fly {fly})");
    }

    public void Look(float deltaYawDegrees, float deltaPitchDegrees)
    {
        Detach();
        _yaw += deltaYawDegrees;
        _pitch = Math.Clamp(_pitch + deltaPitchDegrees, -89f, 89f);
        ApplyCurrent();
    }

    public void Move(float forward, float right, float up)
    {
        Detach();
        MoveBy(forward, right, up);
        ApplyCurrent();
    }

    public void Teleport(float x, float y, float z)
    {
        Detach();
        _x = x; _y = y; _z = z;
        _walkEyeY = y;
        ApplyCurrent();
    }

    public void SetFov(float degrees)
    {
        _fov = Math.Clamp(degrees, 10f, 120f);
        ApplyCurrent();
    }

    private void Detach()
    {
        if (_followRevit)
        {
            SyncFromEngine();
            _followRevit = false;
            _engine.FocusRenderWindow();
            FollowChanged?.Invoke();
        }
        if (!_hasCamera && _lastRevitCamera != null) FromCamera(_lastRevitCamera);
    }

    /// <summary>Adopts the engine's current camera (the engine owns mouse-look, wheel and pan).</summary>
    public void SyncFromEngine()
    {
        var camera = _engine.ReadCamera();
        if (camera != null) FromCamera(camera);
    }

    private void FromCamera(CameraUpdate c)
    {
        _x = c.PosX; _y = c.PosY; _z = c.PosZ;
        var forward = RotateVector(new[] { 0f, 0f, -1f }, c.QuatX, c.QuatY, c.QuatZ, c.QuatW);
        _yaw = MathF.Atan2(forward[0], -forward[2]) * (180f / MathF.PI);
        _pitch = MathF.Asin(Math.Clamp(forward[1], -1f, 1f)) * (180f / MathF.PI);
        if (c.FovDegrees > 0) _fov = c.FovDegrees;
        _walkEyeY = _y;
        _hasCamera = true;
    }

    private static float[] RotateVector(float[] v, float qx, float qy, float qz, float qw)
    {
        // v' = v + 2 * cross(q.xyz, cross(q.xyz, v) + q.w * v)
        var cx = qy * v[2] - qz * v[1] + qw * v[0];
        var cy = qz * v[0] - qx * v[2] + qw * v[1];
        var cz = qx * v[1] - qy * v[0] + qw * v[2];
        return new[]
        {
            v[0] + 2f * (qy * cz - qz * cy),
            v[1] + 2f * (qz * cx - qx * cz),
            v[2] + 2f * (qx * cy - qy * cx),
        };
    }

    private void MoveBy(float forward, float right, float up)
    {
        var (fx, fy, fz) = Forward();
        if (_mode == NavigationMode.Walk) fy = 0f;
        var len = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
        if (len > 1e-5f) { fx /= len; fy /= len; fz /= len; }
        var rx = MathF.Cos(_yaw * MathF.PI / 180f);
        var rz = MathF.Sin(_yaw * MathF.PI / 180f);

        _x += fx * forward + rx * right;
        _y += fy * forward + up;
        _z += fz * forward + rz * right;

        if (_mode == NavigationMode.Walk) _y = _walkEyeY;
    }

    private (float, float, float) Forward()
    {
        var yaw = _yaw * MathF.PI / 180f;
        var pitch = _pitch * MathF.PI / 180f;
        var cp = MathF.Cos(pitch);
        return (MathF.Sin(yaw) * cp, MathF.Sin(pitch), -MathF.Cos(yaw) * cp);
    }

    private void ApplyCurrent()
    {
        if (!_engineReady || !_hasCamera) return;
        var (fx, fy, fz) = Forward();
        var (rx, ry, rz) = Right(fx, fy, fz);
        var (ux, uy, uz) = Cross(rx, ry, rz, fx, fy, fz);
        var q = MatrixToQuaternion(rx, ux, -fx, ry, uy, -fy, rz, uz, -fz);
        _engine.ApplyCamera(new CameraUpdate
        {
            PosX = _x, PosY = _y, PosZ = _z,
            QuatX = q.Item1, QuatY = q.Item2, QuatZ = q.Item3, QuatW = q.Item4,
            FovDegrees = _fov,
            FovType = 0,
            Ortho = false,
            AutoExposure = true,
        });
    }

    private static (float, float, float) Right(float fx, float fy, float fz)
    {
        // right = normalize(cross(forward, worldUp))
        var rx = -fz;
        var rz = fx;
        var len = MathF.Sqrt(rx * rx + rz * rz);
        if (len < 1e-5f) return (1f, 0f, 0f);
        return (rx / len, 0f, rz / len);
    }

    private static (float, float, float) Cross(float ax, float ay, float az, float bx, float by, float bz) =>
        (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx);

    private static (float, float, float, float) MatrixToQuaternion(
        float m00, float m01, float m02,
        float m10, float m11, float m12,
        float m20, float m21, float m22)
    {
        // columns [right, up, -forward]
        float r00 = m00, r01 = m01, r02 = m02;
        float r10 = m10, r11 = m11, r12 = m12;
        float r20 = m20, r21 = m21, r22 = m22;
        var tr = r00 + r11 + r22;
        float x, y, z, w;
        if (tr > 0)
        {
            var s = MathF.Sqrt(tr + 1f) * 2f;
            w = 0.25f * s;
            x = (r21 - r12) / s;
            y = (r02 - r20) / s;
            z = (r10 - r01) / s;
        }
        else if (r00 > r11 && r00 > r22)
        {
            var s = MathF.Sqrt(1f + r00 - r11 - r22) * 2f;
            w = (r21 - r12) / s;
            x = 0.25f * s;
            y = (r01 + r10) / s;
            z = (r02 + r20) / s;
        }
        else if (r11 > r22)
        {
            var s = MathF.Sqrt(1f + r11 - r00 - r22) * 2f;
            w = (r02 - r20) / s;
            x = (r01 + r10) / s;
            y = 0.25f * s;
            z = (r12 + r21) / s;
        }
        else
        {
            var s = MathF.Sqrt(1f + r22 - r00 - r11) * 2f;
            w = (r10 - r01) / s;
            x = (r02 + r20) / s;
            y = (r12 + r21) / s;
            z = 0.25f * s;
        }
        var len = MathF.Sqrt(x * x + y * y + z * z + w * w);
        return (x / len, y / len, z / len, w / len);
    }

    // ------------------------------------------------------------------
    // Input polling (60 Hz)
    // ------------------------------------------------------------------

    private bool _loggedTick, _loggedNoForeground;

    private void Tick()
    {
        if (!_engineReady) return;

        var foreground = Win32.IsOurProcessForeground();
        if (!foreground && !_loggedNoForeground)
        {
            _loggedNoForeground = true;
            HostLog.Write("nav tick: foreground is not the host process");
        }
        if (foreground && !_loggedTick)
        {
            _loggedTick = true;
            HostLog.Write("nav tick: active");
        }

        PollEngineCamera();

        var dt = 1f / 60f;
        var fast = foreground && Win32.IsKeyDown(0x10);   // Shift
        var slow = foreground && Win32.IsKeyDown(0x11);   // Ctrl

        float forward = 0, right = 0, up = 0;
        if (foreground)
        {
            if (Win32.IsKeyDown(0x57)) forward += 1f; // W
            if (Win32.IsKeyDown(0x53)) forward -= 1f; // S
            if (Win32.IsKeyDown(0x44)) right += 1f;   // D
            if (Win32.IsKeyDown(0x41)) right -= 1f;   // A
            if (_mode == NavigationMode.Fly)
            {
                if (Win32.IsKeyDown(0x45)) up += 1f;  // E
                if (Win32.IsKeyDown(0x51)) up -= 1f;  // Q
            }
        }

        var lookX = Interlocked.Exchange(ref _lookAccumX, 0);
        var lookY = Interlocked.Exchange(ref _lookAccumY, 0);
        var rightButton = _hookLooking;
        var wheel = (float)Interlocked.Exchange(ref _wheelDelta, 0L) / 120f;

        var anyInput = forward != 0 || right != 0 || up != 0 || wheel != 0
                       || lookX != 0 || lookY != 0 || rightButton;
        if (!anyInput) return;

        LogInput(forward != 0 || right != 0 || up != 0, rightButton, false, wheel);

        if (_followRevit) Detach();
        _lastInput = DateTime.UtcNow;

        // Base the new pose on the engine's current camera, then apply our deltas. The engine window
        // is disabled, so this is the only camera writer and there is no competing movement.
        SyncFromEngine();

        if (lookX != 0 || lookY != 0)
        {
            _yaw += lookX * MouseSensitivity;
            _pitch = Math.Clamp(_pitch - lookY * MouseSensitivity, -89f, 89f);
        }
        if (forward != 0 || right != 0 || up != 0)
        {
            var speed = (_mode == NavigationMode.Walk ? WalkSpeed : FlySpeed) * (fast ? 3f : slow ? 0.3f : 1f);
            var step = speed * dt;
            MoveBy(forward * step, right * step, up * step);
        }
        if (wheel != 0)
        {
            MoveBy(0.8f * wheel, 0f, 0f);
        }
        ApplyCurrent();
    }

    private bool _loggedKeys, _loggedLook, _loggedWheel, _loggedPan;
    private DateTime _lastPoseLog = DateTime.MinValue;
    private DateTime _lastCameraPoll = DateTime.MinValue;
    private DateTime _lastInput = DateTime.MinValue;

    // low-level mouse hook state: look and wheel are swallowed while the cursor is over the renderer
    private bool _hookLooking;
    private int _hookLastX, _hookLastY;
    private int _lookAccumX, _lookAccumY;
    private bool _recentering;

    private const float MouseSensitivity = 0.12f; // degrees per pixel

    // low-level mouse hook state (mouse look / wheel are captured here and swallowed, so the
    // engine cannot apply a second, conflicting navigation step)


    private void PollEngineCamera()
    {
        if (_followRevit) return;
        var camera = _engine.ReadCamera();
        if (camera != null) FromCamera(camera);
        if ((DateTime.UtcNow - _lastCameraPoll).TotalSeconds < 0.5) return;
        _lastCameraPoll = DateTime.UtcNow;
        if (camera == null) return;
        HostLog.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "nav camera: x={0:0.###} y={1:0.###} z={2:0.###} quat={3:0.####},{4:0.####},{5:0.####},{6:0.####}",
            camera.PosX, camera.PosY, camera.PosZ,
            camera.QuatX, camera.QuatY, camera.QuatZ, camera.QuatW));
    }

    private void LogInput(bool keys, bool rightButton, bool middleButton, float wheel)
    {
        try
        {
            if (keys && !_loggedKeys)
            {
                _loggedKeys = true;
                HostLog.Write("nav input: keyboard (W/A/S/D Q/E)");
            }
            if (rightButton && !_loggedLook)
            {
                _loggedLook = true;
                HostLog.Write("nav input: mouse look (right button)");
            }
            if (wheel != 0 && !_loggedWheel)
            {
                _loggedWheel = true;
                HostLog.Write("nav input: mouse wheel");
            }
            if (middleButton && !_loggedPan)
            {
                _loggedPan = true;
                HostLog.Write("nav input: middle-button pan");
            }
            if ((DateTime.UtcNow - _lastPoseLog).TotalSeconds > 0.5)
            {
                _lastPoseLog = DateTime.UtcNow;
                HostLog.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "nav pose: mode={0} x={1:0.###} y={2:0.###} z={3:0.###} yaw={4:0.##} pitch={5:0.##}",
                    _mode, _x, _y, _z, _yaw, _pitch));
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Low-level wheel hook (wheel messages go to the engine child window)
    // ------------------------------------------------------------------

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = LowLevelMouse;
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc,
            Win32.GetModuleHandle(null), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
    }

    private IntPtr LowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            var overRenderer = Win32.IsOverWindow(_renderHwnd);
            var foreground = Win32.IsOurProcessForeground();

            if ((message == Win32.WM_RBUTTONDOWN || message == Win32.WM_LBUTTONDOWN) && overRenderer)
            {
                if (!foreground)
                {
                    var root = Win32.GetAncestor(_renderHwnd, Win32.GA_ROOT);
                    if (root != IntPtr.Zero) Win32.SetForegroundWindow(root);
                }
                _hookLooking = true;
                _hookLastX = data.pt.X;
                _hookLastY = data.pt.Y;
                // Do NOT swallow the button-down: the click must reach the window so Windows
                // activates the host (swallowing it was why several clicks were needed).
                return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            if (message == Win32.WM_RBUTTONUP || message == Win32.WM_LBUTTONUP)
            {
                _hookLooking = false;
                return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            if (message == Win32.WM_MOUSEMOVE && _hookLooking)
            {
                if (_recentering)
                {
                    _recentering = false;
                    return (IntPtr)1;
                }
                var dx = data.pt.X - _hookLastX;
                var dy = data.pt.Y - _hookLastY;
                if (dx != 0 || dy != 0)
                {
                    Interlocked.Add(ref _lookAccumX, dx);
                    Interlocked.Add(ref _lookAccumY, dy);
                    if (Win32.CenterCursor(_renderHwnd, out var cx, out var cy))
                    {
                        _hookLastX = cx;
                        _hookLastY = cy;
                        _recentering = true;
                    }
                }
                return (IntPtr)1;
            }

            if (foreground && message == Win32.WM_MOUSEWHEEL && overRenderer)
            {
                var delta = (short)((data.mouseData >> 16) & 0xFFFF);
                Interlocked.Add(ref _wheelDelta, delta);
                return (IntPtr)1;
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }
}
