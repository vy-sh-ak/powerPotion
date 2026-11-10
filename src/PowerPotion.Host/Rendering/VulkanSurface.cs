using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PowerPotion.Protocol;
using PowerPotion.Rendering;
using PowerPotion.Rendering.Settings;
using PowerPotion.Rendering.Vulkan;

namespace PowerPotion.Host.Rendering;

/// <summary>
/// WPF <see cref="HwndHost"/> that owns a child HWND and hosts the standalone Vulkan renderer.
/// This replaces the Enscape "start offscreen then reparent" dance with a direct child surface.
/// </summary>
public sealed class VulkanSurface : HwndHost, IRenderSurface
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS wc);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hBackground;
        public string? menuName;
        public string className;
    }

    private const string HostClassName = "PowerPotion.VulkanSurface";
    private static bool _classRegistered;
    private static WndProcDelegate? _wndProc;

    private readonly object _stateLock = new();
    private readonly object _captureLock = new();
    private readonly ConcurrentQueue<Action<VulkanRenderer>> _commands = new();
    private IntPtr _hwnd;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _resizePending;
    private volatile int _width = 1280;
    private volatile int _height = 720;
    private VulkanRenderer? _renderer;
    private Exception? _error;
    private string? _capturePath;
    private TaskCompletionSource<bool>? _captureCompletion;

    public event Action<string>? Log;
    public event Action? Ready;
    public event Action<Exception>? Failed;
    public event Action<long>? SceneUploaded;

    public IntPtr ChildHwnd => _hwnd;
    public (int Width, int Height) SurfaceSize => (_width, _height);
    public bool IsReady { get { lock (_stateLock) return _renderer != null && _renderer.IsInitialized; } }
    public string DeviceName { get { lock (_stateLock) return _renderer?.DeviceName ?? ""; } }
    public Exception? Error { get { lock (_stateLock) return _error; } }
    public ulong FrameCount { get { lock (_stateLock) return _renderer?.FrameCount ?? 0; } }
    public int SwapchainRecreationCount { get { lock (_stateLock) return _renderer?.SwapchainRecreationCount ?? 0; } }
    public int ValidationErrorCount { get { lock (_stateLock) return _renderer?.ValidationErrorCount ?? 0; } }
    public int ValidationWarningCount { get { lock (_stateLock) return _renderer?.ValidationWarningCount ?? 0; } }
    public bool ValidationEnabled { get { lock (_stateLock) return _renderer?.ValidationEnabled == true; } }
    public string SwapchainInfo { get { lock (_stateLock) return _renderer?.SwapchainInfo ?? ""; } }
    public ulong AllocatedBytes { get { lock (_stateLock) return _renderer?.AllocatedBytes ?? 0; } }
    public int LiveAllocations { get { lock (_stateLock) return _renderer?.LiveAllocations ?? 0; } }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClass();

        int width = 1280, height = 720;
        if (GetClientRect(hwndParent.Handle, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
        }
        _width = Math.Max(width, 64);
        _height = Math.Max(height, 64);

        _hwnd = CreateWindowEx(0, HostClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, _width, _height, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the Vulkan child window.");

        _thread = new Thread(RenderThread)
        {
            IsBackground = true,
            Name = "PowerPotion.Vulkan",
        };
        _thread.Start();

        return new HandleRef(this, _hwnd);
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered) return;
        _wndProc = DefWindowProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            className = HostClassName,
        };
        RegisterClass(ref wc);
        _classRegistered = true;
    }

    private void RenderThread()
    {
        VulkanRenderer? renderer = null;
        try
        {
            renderer = new VulkanRenderer();
            renderer.Log += message => Post(() => Log?.Invoke(message));
            renderer.SceneUploaded += revision => Post(() => SceneUploaded?.Invoke(revision));
            renderer.Initialize(_hwnd, (uint)_width, (uint)_height);

            lock (_stateLock) _renderer = renderer;
            Post(() => Ready?.Invoke());

            while (!_stop)
            {
                while (_commands.TryDequeue(out var command))
                {
                    try { command(renderer); }
                    catch (Exception ex) { Post(() => Log?.Invoke("render command failed: " + ex.Message)); }
                }

                if (_resizePending)
                {
                    _resizePending = false;
                    renderer.Resize((uint)_width, (uint)_height);
                }

                renderer.RenderFrame();

                string? capturePath = null;
                TaskCompletionSource<bool>? capture = null;
                lock (_captureLock)
                {
                    if (_capturePath != null)
                    {
                        capturePath = _capturePath;
                        capture = _captureCompletion;
                        _capturePath = null;
                        _captureCompletion = null;
                    }
                }

                if (capturePath != null)
                {
                    bool ok = false;
                    try { ok = renderer.Capture(capturePath); }
                    catch (Exception ex) { Post(() => Log?.Invoke("capture failed: " + ex.Message)); }
                    capture?.TrySetResult(ok);
                }

                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock) _error = ex;
            Post(() => Log?.Invoke("Vulkan thread failed: " + ex));
            Post(() => Failed?.Invoke(ex));
        }
        finally
        {
            renderer?.Dispose();
            lock (_stateLock) _renderer = null;
        }
    }

    private void Post(Action action)
    {
        try
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch { }
    }

    public Task<bool> CaptureAsync(string path)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_captureLock)
        {
            _capturePath = path;
            _captureCompletion = completion;
        }
        return completion.Task;
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;
        _resizePending = true;
    }

    public void FocusChild()
    {
        if (_hwnd != IntPtr.Zero) SetFocus(_hwnd);
    }

    public void SetVsync(bool vsync) => _commands.Enqueue(renderer => renderer.SetVsync(vsync));
    public void SetHdr(bool hdr) => _commands.Enqueue(renderer => renderer.SetHdr(hdr));
    public void SetWhiteMode(bool enabled) => _commands.Enqueue(renderer => renderer.SetWhiteMode(enabled));
    public void SetSettings(RendererSettings settings) =>
        _commands.Enqueue(renderer => renderer.SetSettings(settings));
    public void ApplyWorld(WorldSnapshot world, bool applyCamera = true) =>
        _commands.Enqueue(renderer => renderer.ApplyWorld(world, applyCamera));
    public void ApplyCamera(CameraUpdate camera) => _commands.Enqueue(renderer => renderer.ApplyCamera(camera));
    public void OverwriteMaterial(MaterialDto material) => _commands.Enqueue(renderer => renderer.OverwriteMaterial(material));
    public void ApplySun(SunUpdate sun) => _commands.Enqueue(renderer => renderer.ApplySun(sun));
    public void SetSectionPlanes(float[] planes) => _commands.Enqueue(renderer => renderer.SetSectionPlanes(planes));

    public string SceneStats { get { lock (_stateLock) return _renderer?.SceneStats ?? "no renderer"; } }
    public int SceneDrawCalls { get { lock (_stateLock) return _renderer?.SceneDrawCalls ?? 0; } }
    public int SceneMaterialCount { get { lock (_stateLock) return _renderer?.SceneMaterialCount ?? 0; } }
    public int SceneTextureCount { get { lock (_stateLock) return _renderer?.SceneTextureCount ?? 0; } }
    public int SceneLightCount { get { lock (_stateLock) return _renderer?.SceneLightCount ?? 0; } }
    public float SunElevationDegrees { get { lock (_stateLock) return _renderer?.SunElevationDegrees ?? 0; } }
    public float SunAzimuthDegrees { get { lock (_stateLock) return _renderer?.SunAzimuthDegrees ?? 0; } }
    public float SunIntensity { get { lock (_stateLock) return _renderer?.SunIntensity ?? 0; } }
    public double MaterialUpdateMilliseconds { get { lock (_stateLock) return _renderer?.MaterialUpdateMilliseconds ?? 0; } }

    public void Stop()
    {
        _stop = true;
        try { _thread?.Join(TimeSpan.FromSeconds(5)); } catch { }
        _thread = null;
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Stop();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!sizeInfo.WidthChanged && !sizeInfo.HeightChanged) return;
        UpdateSurfaceSize();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateSurfaceSize();
    }

    private void UpdateSurfaceSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double scaleX = 1, scaleY = 1;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var m = source.CompositionTarget.TransformToDevice;
            scaleX = m.M11;
            scaleY = m.M22;
        }

        int width = Math.Max(64, (int)Math.Round(ActualWidth * scaleX));
        int height = Math.Max(64, (int)Math.Round(ActualHeight * scaleY));
        if (width == _width && height == _height) return;

        _width = width;
        _height = height;
        _resizePending = true;
        Log?.Invoke($"surface resize: {width}x{height} (scale {scaleX:0.##})");
    }
}
