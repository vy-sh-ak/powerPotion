using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerPotion.Host.Enscape;

/// <summary>
/// Hosts the native Enscape render window inside the WPF visual tree.
///
/// Mirrors Chaos' own RendererHost (verified against the shipped
/// <c>Enscape.RendererHost.dll</c>): the engine is started with the *top-level* window handle and the
/// screen rectangle of the renderer placeholder; the native engine creates its own (top-level) window
/// there. Once initialization completes the host re-styles that window as a child
/// (<c>WS_POPUP</c> off / <c>WS_CHILD</c> on — Enscape's <c>ToggleChildWindowStyle</c>) and reparents it
/// into the WPF HwndHost (Enscape's <c>RendererControl : NativeControlHost</c>). Without the reparent step
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

    private const int SS_BLACKRECT = 0x00000004;

    private IntPtr _hostHwnd;
    private bool _startRequested;
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
        // A plain child window fills the HwndHost area while the engine boots. SS_BLACKRECT keeps it
        // dark (a default static control would paint the system window colour — the white flash).
        _hostHwnd = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | SS_BLACKRECT,
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

    private void StartEngine()
    {
        if (_hostHwnd == IntPtr.Zero) return;
        try
        {
            // The engine wants the top-level window handle plus the placeholder's *screen* rectangle
            // (Enscape: RendererWindow.RendererScreenGeometry = placeholder bounds translated to screen).
            var window = Window.GetWindow(this);
            var topLevel = window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
            var (x, y, w, h) = GetScreenRect();

            HostLog.Write($"StartEngine topLevel=0x{topLevel:X} rect={x},{y} {w}x{h} doc='{_documentName()}'");
            var result = _engine.StartRendererNative(topLevel, x, y, w, h);
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

    private async Task InitializeAsync()
    {
        try
        {
            // Continuation stays on the UI thread (WPF SynchronizationContext) — the engine's managed
            // facade must be attached from the windowing thread, like Chaos' host does.
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

    /// <summary>Enscape's ToggleChildWindowStyle + NativeControlHost reparenting, in WPF terms.</summary>
    private void EmbedEngineWindow()
    {
        var engineWindow = _engine.NativeWindowHandle;
        if (engineWindow == IntPtr.Zero || _hostHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Renderer window handle is not available.");

        var style = GetWindowLongPtr(engineWindow, GWL_STYLE).ToInt64();
        style &= ~(long)WS_POPUP;
        style |= WS_CHILD;
        SetWindowLongPtr(engineWindow, GWL_STYLE, new IntPtr(style));
        SetParent(engineWindow, _hostHwnd);

        HostLog.Write($"Embedded engine window 0x{engineWindow:X} into host 0x{_hostHwnd:X} (style 0x{style:X8})");
        ResizeEngineWindow();
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
