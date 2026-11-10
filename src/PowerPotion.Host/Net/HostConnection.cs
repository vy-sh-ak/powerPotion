using System.IO;
using System.Net.Sockets;
using PowerPotion.Protocol;

namespace PowerPotion.Host.Net;

/// <summary>TCP client that talks to the Revit add-in's server and drives the engine on the UI thread.</summary>
public sealed class HostConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private readonly Action<Action> _onUi;

    public event Action<string>? Log;
    public event Action? Disconnected;
    public event Action<WorldSnapshot>? WorldReceived;
    public event Action<CameraUpdate>? CameraReceived;
    public event Action<SunUpdate>? SunReceived;
    public event Action<ConsoleCommand>? CommandReceived;
    public event Action<CaptureRequest>? CaptureReceived;
    public event Action<MaterialUpdate>? MaterialReceived;
    public event Action<LiveUpdateState>? LiveUpdateReceived;
    public event Action<ViewActivation>? ViewActivationReceived;
    public event Action<SectionPlanesUpdate>? SectionPlanesReceived;
    public bool LiveUpdatesPaused { get; private set; }

    public HostConnection(TcpClient client, Action<Action> onUi)
    {
        _client = client;
        _stream = client.GetStream();
        _onUi = onUi;
    }

    public bool IsConnected => _client.Connected;

    public async Task SendHelloAsync(string engineVersion, string hostVersion)
    {
        await MessageCodec.SendAsync(_stream, MessageType.Hello, w =>
        {
            w.Write(Hello.CurrentVersion);
            w.Write("");
            w.Write(engineVersion ?? "");
            w.Write(hostVersion ?? "");
        });
    }

    public async Task SendEngineReadyAsync(bool started, string error) =>
        await MessageCodec.SendAsync(_stream, MessageType.EngineReady, w =>
        {
            w.Write(started);
            w.Write(error ?? "");
        });

    public async Task SendCaptureResultAsync(CaptureResult result) =>
        await MessageCodec.SendAsync(_stream, MessageType.CaptureResult, w =>
        {
            w.Write(result.Ok);
            w.Write(result.FilePath);
            w.Write(result.Error);
        });

    public async Task SendRendererClosedAsync(string reason) =>
        await MessageCodec.SendAsync(_stream, MessageType.RendererClosed, w => w.Write(reason));

    public async Task SendLiveUpdateStateAsync(bool paused) =>
        await MessageCodec.SendAsync(_stream, MessageType.LiveUpdateState, w => w.Write(paused));

    public async Task SendStatusAsync(string text, int level = 0) =>
        await MessageCodec.SendAsync(_stream, MessageType.Status, w =>
        {
            w.Write(text);
            w.Write(level);
        });

    public void Start()
    {
        _loop ??= Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await MessageCodec.ReadFrameAsync(_stream, ct);
                if (frame is null) break;
                var (type, payload) = frame.Value;
                object message;
                try { message = MessageCodec.Decode(type, payload); }
                catch (Exception ex)
                {
                    Log?.Invoke($"Decode {type} failed: {ex.Message}");
                    continue;
                }

                switch (message)
                {
                    case WorldSnapshot world:
                        _onUi(() => WorldReceived?.Invoke(world));
                        break;
                    case CameraUpdate cam:
                        _onUi(() => CameraReceived?.Invoke(cam));
                        break;
                    case SunUpdate sun:
                        _onUi(() => SunReceived?.Invoke(sun));
                        break;
                    case ConsoleCommand cmd:
                        _onUi(() => CommandReceived?.Invoke(cmd));
                        break;
                    case CaptureRequest cap:
                        _onUi(() => CaptureReceived?.Invoke(cap));
                        break;
                    case MaterialUpdate mat:
                        _onUi(() => MaterialReceived?.Invoke(mat));
                        break;
                    case LiveUpdateState live:
                        LiveUpdatesPaused = live.Paused;
                        _onUi(() => LiveUpdateReceived?.Invoke(live));
                        break;
                    case ViewActivation view:
                        _onUi(() => ViewActivationReceived?.Invoke(view));
                        break;
                    case SectionPlanesUpdate planes:
                        _onUi(() => SectionPlanesReceived?.Invoke(planes));
                        break;
                    case Shutdown:
                        Log?.Invoke("Revit requested shutdown.");
                        _ = SendRendererClosedAsync("requested");
                        return;
                    case Status st:
                        Log?.Invoke("Revit: " + st.Text);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke("Connection error: " + ex.Message);
        }
        finally
        {
            _onUi(() => Disconnected?.Invoke());
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _stream.Close(); } catch { }
        try { _client.Close(); } catch { }
    }
}
