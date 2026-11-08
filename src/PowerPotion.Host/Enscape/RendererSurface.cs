using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PowerPotion.Host.Enscape;

/// <summary>
/// A WPF HwndHost that creates a placeholder child HWND and starts the Enscape engine
/// rendering into it (the engine creates its own native child window inside).
/// </summary>
public sealed class RendererSurface : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    private IntPtr _placeholder;
    private readonly EngineBridge _engine;
    private readonly Func<string> _documentName;

    public Exception? StartError { get; private set; }

    public event Action<string>? Log;
    /// <summary>Raised once the engine is initialized and the managed facade is attached.</summary>
    public event Action? EngineReady;
    public event Action<Exception>? EngineFailed;

    public RendererSurface(EngineBridge engine, Func<string> documentName)
    {
        _engine = engine;
        _documentName = documentName;
        engine.Log += m => Log?.Invoke(m);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var w = (int)(ActualWidth <= 0 ? 1280 : ActualWidth);
        var h = (int)(ActualHeight <= 0 ? 720 : ActualHeight);
        _placeholder = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE,
            0, 0, w, h, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // The engine MUST be started on the windowing thread (Chaos' host guards with
        // Dispatcher.VerifyAccess). BuildWindowCore runs on the WPF UI thread — call synchronously.
        try
        {
            var result = _engine.StartRendererNative(_placeholder, 0, 0, w, h);
            if (global::Enscape.RendererApi.EnscapeAPI.IsError(result))
                throw new InvalidOperationException($"Engine failed to start: {result}");
            _documentName(); // reserved for future use (log tagging)
        }
        catch (Exception ex)
        {
            StartError = ex;
            Log?.Invoke("Engine start failed: " + ex.Message);
            EngineFailed?.Invoke(ex);
        }

        // Attach the managed facade once initialization completes.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _engine.AttachWhenReadyAsync(CancellationToken.None);
                if (global::Enscape.RendererApi.EnscapeAPI.IsError(result))
                {
                    Log?.Invoke($"Engine initialization failed: {result}");
                    EngineFailed?.Invoke(new InvalidOperationException($"Engine init: {result}"));
                }
                else
                {
                    EngineReady?.Invoke();
                }
            }
            catch (Exception ex)
            {
                EngineFailed?.Invoke(ex);
            }
        });

        return new HandleRef(this, _placeholder);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        try { _engine.StopAsync().GetAwaiter().GetResult(); } catch { }
        if (_placeholder != IntPtr.Zero)
        {
            DestroyWindow(_placeholder);
            _placeholder = IntPtr.Zero;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.HeightChanged || sizeInfo.WidthChanged)
        {
            var w = Math.Max(64, (int)sizeInfo.NewSize.Width);
            var h = Math.Max(64, (int)sizeInfo.NewSize.Height);
            _engine.Resize(0, 0, w, h);
        }
    }
}
