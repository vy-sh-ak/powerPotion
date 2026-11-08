using System.Threading;
using System.Windows.Threading;

namespace PowerPotion.Host;

/// <summary>
/// Shows the branded splash on its own STA UI thread. The native engine start blocks the main WPF
/// thread for several seconds (StartRendererNative must run on the windowing thread); keeping the
/// splash on a separate thread means Windows never marks the visible splash as unresponsive, so the
/// cursor does not sit in the busy/loading state during startup.
/// </summary>
internal sealed class SplashPresenter : IDisposable
{
    private Thread? _thread;
    private SplashWindow? _window;
    private Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _started = new(false);

    public void Show(string status)
    {
        if (_thread != null)
        {
            SetStatus(status);
            return;
        }

        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _window = new SplashWindow { ShowInTaskbar = false };
            _window.SetStatus(status);
            _window.Show();
            _started.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "PowerPotion splash",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait(2000);
    }

    public void SetStatus(string status)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { _window?.SetStatus(status); } catch { }
            }));
        }
        catch { }
    }

    public void Close()
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { _window?.Close(); } catch { }
                _window = null;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }));
        }
        catch { }

        _dispatcher = null;
        _thread = null;
        _started.Reset();
    }

    public void Dispose() => Close();
}
