using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PowerPotion.Protocol;
using PowerPotion.Revit.Export;

namespace PowerPotion.Revit.Sync;

/// <summary>
/// Owns the live-rendering session: exports the active 3D view, starts the renderer host
/// process, serves the world over a localhost TCP socket (same architecture as Enscape's
/// RemoteCadServer → RendererHost --port), pushes camera updates on Idling and re-exports
/// on DocumentChanged with a debounce.
/// </summary>
public sealed class LiveSyncService : IDisposable
{
    private readonly UIApplication _uiApp;
    private readonly Dispatcher _dispatcher;
    private readonly string _hostExePath;
    private readonly string _pluginDir;

    private TcpListener? _listener;
    private NetworkStream? _stream;
    private TcpClient? _client;
    private Process? _hostProcess;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    private Document? _document;
    private View3D? _view;
    private SceneExportContext? _lastContext;
    private RevitMaterialParser? _materials;
    private bool _liveUpdatesPaused;
    private bool _running;
    private DateTime _lastExport = DateTime.MinValue;
    private volatile bool _reexportQueued;
    private volatile bool _hostConnected;
    private bool _initialWorldSent;
    private DateTime _lastCameraPush = DateTime.MinValue;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ExternalEvent? _exportEvent;
    private bool _retryExportScheduled;

    public bool IsRunning => _running;
    public event Action<string>? Log;

    public LiveSyncService(UIApplication uiApp, string hostExePath, string pluginDir)
    {
        _uiApp = uiApp;
        _hostExePath = hostExePath;
        _pluginDir = pluginDir;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    private void LogLine(string msg) => Log?.Invoke(msg);

    // ------------------------------------------------------------------
    // Start / stop
    // ------------------------------------------------------------------

    public void Start(View3D? preferredView = null)
    {
        if (_running) return;
        var uidoc = _uiApp.ActiveUIDocument;
        _document = uidoc?.Document;
        if (_document == null)
        {
            TaskDialog.Show("PowerPotion", "Open a document with a 3D view first.");
            return;
        }

        _view = preferredView ?? ActiveOrDefault3DView();
        if (_view == null)
        {
            TaskDialog.Show("PowerPotion", "No 3D view found. Create or open a 3D view, then start PowerPotion.");
            return;
        }

        var port = StartListener();
        if (port == null)
        {
            TaskDialog.Show("PowerPotion", "Could not open a local port for the renderer connection.");
            return;
        }

        LaunchHost(port.Value);
        _exportEvent = ExternalEvent.Create(new ExportEventHandler(this));
        HookEvents();
        _running = true;
        LogLine($"Live sync started on port {port} (view: {_view.Name}).");

        // The initial export MUST run inside a Revit API context (CustomExporter is not
        // thread-safe and cannot be called from a worker thread). The ExternalEvent mirrors
        // Enscape's RevitCadDispatcher and runs as soon as the host is connected.
        _ = Task.Run(async () =>
        {
            try
            {
                await WaitForClientAsync();
                _hostConnected = true;
                RequestExport();
            }
            catch (Exception ex) { LogLine("start: " + ex.Message); }
        });
    }

    private sealed class ExportEventHandler : IExternalEventHandler
    {
        private readonly LiveSyncService _owner;
        public ExportEventHandler(LiveSyncService owner) => _owner = owner;
        public string GetName() => "PowerPotion.SceneExport";
        public void Execute(UIApplication app) => _owner.ExecutePendingExport();
    }

    /// <summary>Queues an export on Revit's API thread (ExternalEvent, like Enscape's CAD dispatcher).</summary>
    private void RequestExport()
    {
        try
        {
            if (!_running || _exportEvent == null) return;
            if (_exportEvent.Raise() != ExternalEventRequest.Accepted)
                ScheduleExportRetry();
        }
        catch (Exception ex) { LogLine("export request: " + ex.Message); }
    }

    private void ScheduleExportRetry()
    {
        if (_retryExportScheduled) return;
        _retryExportScheduled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300);
                _retryExportScheduled = false;
                if (_running) RequestExport();
            }
            catch { _retryExportScheduled = false; }
        });
    }

    private void ExecutePendingExport()
    {
        if (!_running) return;
        if (DateTime.UtcNow - _lastExport < TimeSpan.FromMilliseconds(250))
        {
            _reexportQueued = true;
            ScheduleExportRetry();
            return;
        }
        _reexportQueued = false;
        _initialWorldSent = true;
        ExportAndSendWorld(fullExport: true);
        if (!_liveUpdatesPaused) PushCamera();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _hostConnected = false;
        _initialWorldSent = false;
        try { _exportEvent?.Dispose(); } catch { }
        _exportEvent = null;
        UnhookEvents();

        try { _ = SendAsync(MessageType.LiveUpdateState, w => w.Write(true)); } catch { }
        try { _ = SendAsync(MessageType.Shutdown, _ => { }); } catch { }

        try { _cts?.Cancel(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try
        {
            if (_hostProcess is { HasExited: false })
            {
                _hostProcess.Kill();
            }
        }
        catch { }
        _hostProcess = null;
        _stream = null;
        _client = null;
        LogLine("Live sync stopped.");
    }

    public void Dispose() => Stop();

    public void SetPaused(bool paused)
    {
        _liveUpdatesPaused = paused;
        _ = SendAsync(MessageType.LiveUpdateState, w => w.Write(paused));
    }

    // ------------------------------------------------------------------
    // View / document helpers
    // ------------------------------------------------------------------

    private View3D? ActiveOrDefault3DView()
    {
        var uidoc = _uiApp.ActiveUIDocument;
        if (uidoc?.Document != _document) return null;
        if (uidoc.ActiveGraphicalView is View3D v3d) return v3d;

        var collector = new FilteredElementCollector(_document)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate && !v.Name.StartsWith("{"));
        return collector.FirstOrDefault(v => v.Name.Contains("Default")) ?? collector.FirstOrDefault();
    }

    // ------------------------------------------------------------------
    // Networking
    // ------------------------------------------------------------------

    private int? StartListener()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return port;
        }
        catch (Exception ex)
        {
            LogLine("listener: " + ex.Message);
            return null;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _client = client;
                _stream = client.GetStream();
                LogLine("Renderer host connected.");
                _ = ReadLoopAsync(ct);
                break; // single client
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogLine("accept: " + ex.Message); }
    }

    private async Task WaitForClientAsync()
    {
        var sw = Stopwatch.StartNew();
        while (_stream == null && sw.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(200);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream != null)
            {
                var frame = await MessageCodec.ReadFrameAsync(_stream, ct);
                if (frame is null) break;
                var (type, payload) = frame.Value;
                switch (type)
                {
                    case MessageType.Hello:
                        var hello = (Hello)MessageCodec.Decode(type, payload);
                        LogLine($"Host hello: engine {hello.EngineVersion}");
                        break;
                    case MessageType.EngineReady:
                        var ready = (EngineReady)MessageCodec.Decode(type, payload);
                        LogLine(ready.Started ? "Engine ready." : $"Engine failed: {ready.Error}");
                        break;
                    case MessageType.RendererClosed:
                        _dispatcher.Invoke(() => Stop());
                        return;
                    case MessageType.CaptureResult:
                        var capture = (CaptureResult)MessageCodec.Decode(type, payload);
                        _dispatcher.Invoke(() =>
                        {
                            TaskDialog.Show("PowerPotion", capture.Ok
                                ? $"Image saved:\n{capture.FilePath}"
                                : $"Capture failed: {capture.Error}");
                        });
                        break;
                    case MessageType.LiveUpdateState:
                        var state = (LiveUpdateState)MessageCodec.Decode(type, payload);
                        _liveUpdatesPaused = state.Paused;
                        break;
                    case MessageType.Status:
                        var status = (Status)MessageCodec.Decode(type, payload);
                        LogLine("host: " + status.Text);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogLine("read: " + ex.Message); }
        finally
        {
            _dispatcher.Invoke(() => { if (_running) Stop(); });
        }
    }

    private async Task SendAsync(MessageType type, Action<ProtocolWriter> body)
    {
        var stream = _stream;
        if (stream == null) return;
        // Frames are written as one buffer; concurrent writers would interleave and corrupt
        // the framing (camera updates at ~30 Hz + multi-MB world snapshots).
        await _sendGate.WaitAsync();
        try
        {
            await MessageCodec.SendAsync(stream, type, body);
        }
        catch (Exception ex)
        {
            LogLine("send: " + ex.Message);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void LaunchHost(int port)
    {
        var docName = Path.GetFileName(_document?.Title ?? "document");
        var psi = new ProcessStartInfo(_hostExePath)
        {
            Arguments = $"--port {port} --doc \"{docName}\"",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(_hostExePath)!,
        };
        _hostProcess = Process.Start(psi);
    }

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    private void HookEvents()
    {
        _uiApp.Idling += OnIdling;
        _uiApp.Application.DocumentChanged += OnDocumentChanged;
    }

    private void UnhookEvents()
    {
        _uiApp.Idling -= OnIdling;
        _uiApp.Application.DocumentChanged -= OnDocumentChanged;
    }

    private void OnDocumentChanged(object? sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
    {
        if (!_running || _liveUpdatesPaused) return;
        // Document objects can be different wrappers around the same model; compare identity by title+path.
        var changed = e.GetDocument();
        if (changed == null) return;
        if (_document != null && !ReferenceEquals(changed, _document))
        {
            var sameFile = string.Equals(changed.PathName, _document.PathName, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(changed.Title, _document.Title, StringComparison.Ordinal);
            if (!sameFile) return;
        }

        // Do not export inside the transaction/commit path: queue an API-context export.
        _reexportQueued = true;
        LogLine("document changed -> export queued");
        RequestExport();
    }

    private void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if (!_running) return;

        // Keep the idle loop hot: Revit otherwise raises Idling only sporadically once the
        // renderer window has focus, which freezes camera streaming and live model updates.
        try { e.SetRaiseWithoutDelay(); } catch { }

        // Fallback: if the ExternalEvent could not run (modal dialog, etc.), export here.
        if (!_initialWorldSent && _hostConnected)
        {
            RequestExport();
            return;
        }

        if (_reexportQueued && !_liveUpdatesPaused && DateTime.UtcNow - _lastExport >= TimeSpan.FromMilliseconds(250))
        {
            RequestExport();
            return;
        }

        // camera sync at ~30 Hz
        if (!_liveUpdatesPaused && DateTime.UtcNow - _lastCameraPush >= TimeSpan.FromMilliseconds(33))
        {
            PushCamera();
        }
    }

    // ------------------------------------------------------------------
    // Export + camera
    // ------------------------------------------------------------------

    private void ExportAndSendWorld(bool fullExport)
    {
        if (_document == null || _stream == null) return;
        try
        {
            var sw = Stopwatch.StartNew();

            _materials = new RevitMaterialParser(_document, TextureSearchPaths());
            var context = new SceneExportContext(_document, _view!, _materials);
            var exporter = new CustomExporter(_document, context)
            {
                ShouldStopOnError = false,
                IncludeGeometricObjects = true,
            };
            exporter.Export(_view!);
            _lastContext = context;
            _lastExport = DateTime.UtcNow;

            var world = context.ToWorldSnapshot();
            LogLine($"Exported {world.Models.Count} models / {world.Instances.Count} instances / " +
                    $"{world.Materials.Count} materials / {world.Lights.Count} lights in {sw.ElapsedMilliseconds} ms.");

            _ = Task.Run(() => _ = SendWorldAsync(world));
        }
        catch (Exception ex)
        {
            LogLine("export: " + ex.Message);
        }
    }

    private async Task SendWorldAsync(WorldSnapshot world)
    {
        var stream = _stream;
        if (stream == null) return;
        await _sendGate.WaitAsync();
        try
        {
            await MessageCodec.SendAsync(stream, MessageType.WorldSnapshot, w => MessageCodec.WriteWorld(w, world));
            // sun lives in the world message; also push the camera right after
            await MessageCodec.SendAsync(stream, MessageType.CameraUpdate, w => MessageCodec.WriteCamera(w, world.Camera));
            LogLine("World sent.");
        }
        catch (Exception ex)
        {
            LogLine("send world: " + ex.Message);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void PushCamera()
    {
        try
        {
            var uidoc = _uiApp.ActiveUIDocument;
            if (uidoc?.Document != _document) return;
            var view = _view;
            if (view == null) return;

            var orientation = view.GetOrientation();
            var eye = EngineSpace.Point(orientation.EyePosition);
            var forward = EngineSpace.Vector(orientation.ForwardDirection);
            var up = EngineSpace.Vector(orientation.UpDirection);
            var quat = EngineSpace.CameraQuaternion(forward, up);

            var camera = new CameraUpdate
            {
                PosX = (float)eye.Item1,
                PosY = (float)eye.Item2,
                PosZ = (float)eye.Item3,
                QuatX = quat.X, QuatY = quat.Y, QuatZ = quat.Z, QuatW = quat.W,
                FovDegrees = _lastContext?.World.Camera.FovDegrees ?? 60f,
                FovType = _lastContext?.World.Camera.FovType ?? 0,
                Ortho = view.IsPerspective == false,
                OrthoLeft = _lastContext?.World.Camera.OrthoLeft ?? -5f,
                OrthoRight = _lastContext?.World.Camera.OrthoRight ?? 5f,
                OrthoBottom = _lastContext?.World.Camera.OrthoBottom ?? -3f,
                OrthoTop = _lastContext?.World.Camera.OrthoTop ?? 3f,
                AutoExposure = true,
            };
            _lastCameraPush = DateTime.UtcNow;
            _ = SendAsync(MessageType.CameraUpdate, w => MessageCodec.WriteCamera(w, camera));
        }
        catch { }
    }

    public void PushSun(View3D view)
    {
        var sun = SunParser.Parse(_document!, view);
        if (sun != null)
            _ = SendAsync(MessageType.SunUpdate, w => MessageCodec.WriteSun(w, sun));
    }

    public void RequestCapture(string filePath, bool panorama)
    {
        _ = SendAsync(MessageType.CaptureRequest, w =>
        {
            w.Write(filePath);
            w.Write(0);
            w.Write(0);
            w.Write(panorama);
        });
    }

    private static IEnumerable<string> TextureSearchPaths()
    {
        var paths = new List<string>();
        // Revit's built-in material textures
        var revitData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk", "RVT", "Materials", "Textures");
        if (Directory.Exists(revitData)) paths.Add(revitData);
        var adskShared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Autodesk Shared", "Materials", "Textures");
        if (Directory.Exists(adskShared)) paths.Add(adskShared);
        return paths;
    }
}
