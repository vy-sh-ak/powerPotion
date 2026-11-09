using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerPotion.Host.Engine;

/// <summary>
/// Hosts the native engine render window inside the WPF visual tree.
///
/// Mirrors the vendor's own RendererHost (verified against its shipped
/// host assembly): the engine is started with the *top-level* window handle and the
/// screen rectangle of the renderer placeholder; the native engine creates its own (top-level) window
/// there. Once initialization completes the host re-styles that window as a child
/// (<c>WS_POPUP</c> off / <c>WS_CHILD</c> on — the vendor's <c>ToggleChildWindowStyle</c>) and reparents it
/// into the WPF HwndHost (the vendor's <c>RendererControl : NativeControlHost</c>). Without the reparent step
/// the engine window stays a second top-level window floating above the WPF window.
/// </summary>
public sealed class RendererSurface : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_POPUP = unchecked((int)0x80000000);

    private const int GWL_STYLE = -16;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS wc);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private const string HostClassName = "PowerPotion.SurfaceHost";
    private static bool _classRegistered;
    private static IntPtr _darkBrush;
    private static WndProcDelegate? _wndProc;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const int SS_BLACKRECT = 0x00000004;

    private IntPtr _hostHwnd;
    private bool _startRequested;
    private volatile bool _suppressEngineWindow;
    private volatile bool _engineWindowShown;
    private System.Threading.Timer? _hideTimer;
    private readonly HashSet<IntPtr> _loggedHidden = new();
    private readonly EngineBridge _engine;
    private readonly Func<string> _documentName;

    public Exception? StartError { get; private set; }

    public event Action<string>? Log;
    /// <summary>Raised on the UI thread once the engine is initialized, attached and embedded.</summary>
    public event Action? EngineReady;
    public event Action<Exception>? EngineFailed;

    public RendererSurface(EngineBridge engine, Func<string> documentName)
    {
        _engine = engine;
        _documentName = documentName;
        // Engine callbacks arrive on native engine threads — never touch WPF objects from them.
        engine.Log += message => Post(() => Log?.Invoke(message));
    }

    private void Post(Action action)
    {
        try
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (Exception ex) { HostLog.Exception("RendererSurface.Post", ex); }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClass();
        // A dark native placeholder fills the HwndHost area while the engine boots (a default
        // static control paints the system gray — that gray was visible behind the splash).
        _hostHwnd = CreateWindowEx(0, HostClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        HostLog.Write($"BuildWindowCore parent=0x{hwndParent.Handle:X} host=0x{_hostHwnd:X}");

        // Start after the first WPF render pass: creating the engine takes several seconds inside the
        // native call, and blocking the layout pass would leave a blank window on screen.
        if (!_startRequested)
        {
            _startRequested = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartEngine));
        }

        return new HandleRef(this, _hostHwnd);
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered) return;
        _darkBrush = CreateSolidBrush(0x0018080F); // COLORREF 0x00BBGGRR ≈ #0F0818
        _wndProc = DefWindowProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            hbrBackground = _darkBrush,
            lpszClassName = HostClassName,
        };
        RegisterClass(ref wc);
        _classRegistered = true;
    }

    private void StartEngine()
    {
        if (_hostHwnd == IntPtr.Zero) return;
        try
        {
            // The engine wants the top-level window handle plus the placeholder's *screen* rectangle
            // (vendor host: RendererWindow.RendererScreenGeometry = placeholder bounds translated to screen).
            var window = Window.GetWindow(this);
            var topLevel = window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
            var (_, _, w, h) = GetScreenRect();

            // Create the engine window far off-screen: its vendor-branded splash/title window would
            // otherwise flash over the UI for a second before we reparent it into the surface.
            HostLog.Write($"StartEngine topLevel=0x{topLevel:X} rect=offscreen {w}x{h} doc='{_documentName()}'");

            // The engine ignores the off-screen position on some builds and shows its vendor-branded splash
            // splash window where the surface is. Hide any native engine window until initialization
            // completes and we reparent it (a background timer keeps hiding it while the UI thread is
            // blocked inside the native start call).
            _suppressEngineWindow = true;
            _hideTimer = new System.Threading.Timer(_ => HideEngineWindows(), null, 0, 25);

            var result = _engine.StartRendererNative(topLevel, -32000, -32000, w, h);
            if (global::Enscape.RendererApi.EnscapeAPI.IsError(result))
                throw new InvalidOperationException($"Engine failed to start: {result}");

            _ = InitializeAsync();
        }
        catch (Exception ex)
        {
            StartError = ex;
            HostLog.Exception("StartEngine", ex);
            Post(() => { Log?.Invoke("Engine start failed: " + ex.Message); EngineFailed?.Invoke(ex); });
        }
    }

    private void HideEngineWindows()
    {
        if (!_suppressEngineWindow) return;
        try
        {
            var pid = (uint)Environment.ProcessId;
            var embedded = _engine.NativeWindowHandle;
            var keepEmbedded = _engineWindowShown && embedded != IntPtr.Zero;

            bool IsSplashWindow(IntPtr hwnd)
            {
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, 64);
                return cls.ToString().Equals("Enscape", StringComparison.OrdinalIgnoreCase)
                       && !(keepEmbedded && hwnd == embedded);
            }

            var hidden = new HashSet<IntPtr>();
            EnumWindows((top, _) =>
            {
                GetWindowThreadProcessId(top, out var processId);
                if (processId != pid) return true;

                if (IsSplashWindow(top))
                {
                    ShowWindow(top, SW_HIDE);
                    hidden.Add(top);
                }

                // The engine sometimes parents its loading/splash window to the host window, so
                // EnumWindows alone misses it — walk the child tree as well.
                EnumChildWindows(top, (child, _) =>
                {
                    if (IsSplashWindow(child))
                    {
                        ShowWindow(child, SW_HIDE);
                        hidden.Add(child);
                    }
                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);

            foreach (var hwnd in hidden)
            {
                if (_loggedHidden.Add(hwnd))
                    HostLog.Write($"watchdog: hid engine window 0x{hwnd:X} (embedded=0x{embedded:X} shown={_engineWindowShown})");
            }
        }
        catch (Exception ex) { HostLog.Exception("HideEngineWindows", ex); }
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Continuation stays on the UI thread (WPF SynchronizationContext) — the engine's managed
            // facade must be attached from the windowing thread, like the vendor's host does.
            var result = await _engine.AttachWhenReadyAsync(CancellationToken.None);
            if (global::Enscape.RendererApi.EnscapeAPI.IsError(result))
                throw new InvalidOperationException($"Engine initialization failed: {result}");

            EmbedEngineWindow();

            HostLog.Write("Engine ready and embedded.");
            EngineReady?.Invoke();
        }
        catch (Exception ex)
        {
            HostLog.Exception("InitializeAsync", ex);
            EngineFailed?.Invoke(ex);
        }
    }

    /// <summary>the vendor's ToggleChildWindowStyle + NativeControlHost reparenting, in WPF terms.</summary>
    private void EmbedEngineWindow()
    {
        var engineWindow = _engine.NativeWindowHandle;
        if (engineWindow == IntPtr.Zero || _hostHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Renderer window handle is not available.");

        // Keep the watchdog running: the engine creates its vendor-branded splash window during
        // initialization and occasionally again when the first scene is rendered. It must stay
        // hidden until (and after) we reveal the render window ourselves.

        var style = GetWindowLongPtr(engineWindow, GWL_STYLE).ToInt64();
        style &= ~(long)WS_POPUP;
        style |= WS_CHILD;
        SetWindowLongPtr(engineWindow, GWL_STYLE, new IntPtr(style));
        SetParent(engineWindow, _hostHwnd);

        HostLog.Write($"Embedded engine window 0x{engineWindow:X} into host 0x{_hostHwnd:X} (style 0x{style:X8})");

        // The host owns navigation (keyboard/mouse/wheel) and drives SetCamera; disabling the native
        // window keeps the engine from applying a second, competing camera movement (which showed up
        // as a shaking sky and occasional double-steps).
        // Keep it hidden until the first scene is committed, so the engine's own loading screen can
        // never appear behind the branded splash.
        ShowWindow(engineWindow, SW_HIDE);
        EnableWindow(engineWindow, false);
        _engineWindowShown = false;

        ResizeEngineWindow();
    }

    /// <summary>Reveals the renderer once the first scene (or a standalone session) has been committed.</summary>
    public bool IsEngineWindowShown => _engineWindowShown;

    public void ShowEngineWindow()
    {
        if (_engineWindowShown) return;
        var engineWindow = _engine.NativeWindowHandle;
        if (engineWindow == IntPtr.Zero) return;

        // Mark first so the watchdog starts ignoring the render window itself, then hide any
        // remaining engine splash windows before revealing it.
        _engineWindowShown = true;
        HideEngineWindows();
        ShowWindow(engineWindow, SW_SHOW);
        ResizeEngineWindow();
        _hideTimer?.Change(250, 250);
        HostLog.Write("Engine window shown (first scene ready).");
    }

    private void ResizeEngineWindow()
    {
        var engineWindow = _engine.NativeWindowHandle;
        if (engineWindow == IntPtr.Zero) return;

        GetClientSize(out var w, out var h);
        SetWindowPos(engineWindow, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        _engine.Resize(0, 0, w, h);
    }

    /// <summary>Placeholder rectangle in physical pixels relative to the desktop (engine start geometry).</summary>
    private (int X, int Y, int W, int H) GetScreenRect()
    {
        int w, h;
        GetClientSize(out w, out h);
        try
        {
            var origin = PointToScreen(new Point(0, 0));
            return ((int)Math.Round(origin.X), (int)Math.Round(origin.Y), w, h);
        }
        catch
        {
            return (0, 0, w, h);
        }
    }

    private void GetClientSize(out int width, out int height)
    {
        double scaleX = 1, scaleY = 1;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var m = source.CompositionTarget.TransformToDevice;
            scaleX = m.M11;
            scaleY = m.M22;
        }
        width = Math.Max(64, (int)Math.Round(ActualWidth * scaleX));
        height = Math.Max(64, (int)Math.Round(ActualHeight * scaleY));
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            width = 1280;
            height = 720;
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        HostLog.Write($"DestroyWindowCore host=0x{_hostHwnd:X}");
        _suppressEngineWindow = false;
        _hideTimer?.Dispose();
        _hideTimer = null;
        try { _engine.StopAsync().GetAwaiter().GetResult(); } catch { }
        if (_hostHwnd != IntPtr.Zero)
        {
            DestroyWindow(_hostHwnd);
            _hostHwnd = IntPtr.Zero;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.HeightChanged || sizeInfo.WidthChanged)
            ResizeEngineWindow();
    }
}
