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
    private DateTime _lastDocumentChange = DateTime.MinValue;
    private volatile bool _reexportQueued;
    private volatile bool _hostConnected;
    private bool _initialWorldSent;
    private int _lastWorldModels;
    private long _worldRevision;
    private volatile bool _exportInFlight;
    private bool _hasSentScene;
    private bool _forceNextSend;
    private ulong _lastSceneSignature;
    private DateTime _lastCameraPush = DateTime.MinValue;
    private DateTime _lastSectionPush = DateTime.MinValue;
    private string _lastSectionSignature = "";
    private bool _lastOrtho;
    private bool _lastTwoPoint;
    private bool _cameraModeLogged;
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
                RequestExport("initial");
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
    private void RequestExport(string reason)
    {
        try
        {
            if (!_running || _exportEvent == null) return;
            LogLine($"export request: {reason}");
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
                if (_running) RequestExport("retry");
            }
            catch { _retryExportScheduled = false; }
        });
    }

    private void ExecutePendingExport()
    {
        if (!_running || _exportInFlight) return;
        if (DateTime.UtcNow - _lastExport < TimeSpan.FromMilliseconds(250))
        {
            _reexportQueued = true;
            ScheduleExportRetry();
            return;
        }
        _reexportQueued = false;
        _exportInFlight = true;
        try
        {
            ExportAndSendWorld(fullExport: true);
            if (!_liveUpdatesPaused) PushCamera();
            PushSectionPlanes(force: true);
        }
        finally
        {
            _exportInFlight = false;
        }
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
        if (!paused) Refresh();
    }

    /// <summary>Forces an immediate full re-export of the active document/view (ribbon Refresh).</summary>
    public void Refresh()
    {
        if (!_running) return;
        _lastDocumentChange = DateTime.MinValue;
        _reexportQueued = true;
        _forceNextSend = true;
        LogLine("refresh requested");
        RequestExport("refresh");
    }

    // ------------------------------------------------------------------
    // View / document helpers
    // ------------------------------------------------------------------

    private View3D? ActiveOrDefault3DView()
    {
        var uidoc = _uiApp.ActiveUIDocument;
        var document = uidoc?.Document;
        if (document == null) return null;
        if (uidoc!.ActiveGraphicalView is View3D active) return active;

        var views = new FilteredElementCollector(document)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate)
            .ToList();
        return views.FirstOrDefault(v => v.Name.Contains("Default"))
               ?? views.FirstOrDefault(v => !v.Name.StartsWith("{"))
               ?? views.FirstOrDefault();
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
                        if (ready.Started) RequestExport("engine-ready");
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
        // The plugin ships the standalone Vulkan renderer. An explicit environment value can
        // still select the legacy Enscape bridge for A/B testing.
        var renderer = Environment.GetEnvironmentVariable("POWERPOTION_RENDERER");
        if (string.IsNullOrWhiteSpace(renderer)) renderer = "pp";
        psi.Environment["POWERPOTION_RENDERER"] = renderer;
        LogLine($"launching host with renderer='{renderer}'");
        _hostProcess = Process.Start(psi);
    }

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    private void HookEvents()
    {
        _uiApp.Idling += OnIdling;
        _uiApp.ViewActivated += OnViewActivated;
        _uiApp.Application.DocumentChanged += OnDocumentChanged;
        _uiApp.Application.DocumentOpened += OnDocumentOpened;
    }

    private void UnhookEvents()
    {
        _uiApp.Idling -= OnIdling;
        _uiApp.ViewActivated -= OnViewActivated;
        _uiApp.Application.DocumentChanged -= OnDocumentChanged;
        _uiApp.Application.DocumentOpened -= OnDocumentOpened;
    }

    private void OnViewActivated(object? sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
    {
        if (!_running) return;
        var document = _uiApp.ActiveUIDocument?.Document;
        if (document == null) return;
        if (e.CurrentActiveView is View3D view)
        {
            _document = document;
            _view = view;
            _lastDocumentChange = DateTime.MinValue;
            _reexportQueued = true;
            RequestExport("view");
        }
    }

    private void OnDocumentOpened(object? sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
    {
        if (!_running) return;
        _document = e.Document;
        _view = ActiveOrDefault3DView();
        _lastWorldModels = 0;
        _lastDocumentChange = DateTime.MinValue;
        _reexportQueued = true;
        RequestExport("document");
    }

    private void OnDocumentChanged(object? sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
    {        if (!_running || _liveUpdatesPaused) return;
        // Document objects can be different wrappers around the same model; compare identity by title+path.
        var changed = e.GetDocument();
        if (changed == null) return;
        if (_document != null && !ReferenceEquals(changed, _document))
        {
            var sameFile = string.Equals(changed.PathName, _document.PathName, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(changed.Title, _document.Title, StringComparison.Ordinal);
            if (!sameFile) return;
        }

        // Do not export inside the transaction/commit path: queue an API-context export and let the
        // Idling loop wait for a short quiet period (debounce) so a burst of edits renders once.
        _lastDocumentChange = DateTime.UtcNow;
        _reexportQueued = true;
        LogLine("document changed -> export queued (debounced)");
    }

    private void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if (!_running) return;

        // Keep the idle loop hot: Revit otherwise raises Idling only sporadically once the
        // renderer window has focus, which freezes camera streaming and live model updates.
        try { e.SetRaiseWithoutDelay(); } catch { }

        // Only real, debounced changes trigger exports. There is deliberately no "initial world"
        // polling here: the initial export is requested by the connection task and EngineReady.
        if (_reexportQueued && !_liveUpdatesPaused
            && DateTime.UtcNow - _lastDocumentChange >= TimeSpan.FromMilliseconds(500)
            && DateTime.UtcNow - _lastExport >= TimeSpan.FromMilliseconds(250))
        {
            RequestExport("change");
            return;
        }

        // camera sync at ~30 Hz
        if (!_liveUpdatesPaused && DateTime.UtcNow - _lastCameraPush >= TimeSpan.FromMilliseconds(33))
        {
            PushCamera();
        }

        // Section-box changes do not always raise DocumentChanged; poll at 2 Hz.
        if (!_liveUpdatesPaused && DateTime.UtcNow - _lastSectionPush >= TimeSpan.FromMilliseconds(500))
        {
            PushSectionPlanes();
        }
    }

    // ------------------------------------------------------------------
    // Export + camera
    // ------------------------------------------------------------------

    private void ExportAndSendWorld(bool fullExport)
    {
        if (_stream == null) return;
        try
        {
            // Resolve the document/view at export time. Revit can reopen a document after the
            // plugin was started (Document objects are replaced), so a view cached at Start may
            // belong to a closed document and CustomExporter then returns an empty world.
            var uidoc = _uiApp.ActiveUIDocument;
            var document = uidoc?.Document;
            if (document == null || document.IsFamilyDocument)
            {
                // The family editor is not a renderable project scene; fall back to the open project.
                var project = _uiApp.Application.Documents
                    .Cast<Document>()
                    .FirstOrDefault(d => !d.IsFamilyDocument && !d.IsLinked);
                if (project != null)
                {
                    document = project;
                    uidoc = null;
                }
            }
            if (document == null)
            {
                LogLine("export skipped: no active document");
                return;
            }

            var sw = Stopwatch.StartNew();
            var candidates = CandidateViews(uidoc, document);
            WorldSnapshot? world = null;
            View3D? chosen = null;
            string? firstView = null;

            // A view can legitimately be empty (elements hidden/isolated while editing a material).
            // Try the active 3D view first, then the document's other 3D views, so a hidden active
            // view does not leave the renderer blank.
            foreach (var view in candidates)
            {
                firstView ??= view.Name;
                var candidate = ExportView(document, view);
                if (world == null) world = candidate;
                if (candidate.Models.Count > 0)
                {
                    world = candidate;
                    chosen = view;
                    break;
                }
                LogLine($"view '{view.Name}' exported 0 models — trying next 3D view");
            }

            _lastExport = DateTime.UtcNow;
            if (world == null)
            {
                LogLine("export skipped: no 3D view");
                return;
            }

            if (chosen != null && (_view?.Id != chosen.Id || !ReferenceEquals(document, _document)))
            {
                LogLine($"export target: '{document.Title}' view '{chosen.Name}'");
                _document = document;
                _view = chosen;
            }
            else if (_view == null)
            {
                _document = document;
                _view = candidates.FirstOrDefault();
            }

            LogLine($"Exported {world.Models.Count} models / {world.Instances.Count} instances / " +
                    $"{world.Materials.Count} materials / {world.Lights.Count} lights in {sw.ElapsedMilliseconds} ms.");

            if (world.Models.Count == 0)
            {
                var message = $"No visible geometry in the 3D view ('{firstView}'). Unhide the model or pick another 3D view.";
                LogLine(message);
                _ = SendAsync(MessageType.Status, w => w.Write(message));
                return;
            }

            // Never replace a live scene with a transient empty export (view regenerating, document
            // reopening, etc.): that made the engine flash its loading screen forever.
            var signature = ComputeSceneSignature(world);
            if (!_forceNextSend && _hasSentScene && signature == _lastSceneSignature)
            {
                LogLine("scene unchanged — world send skipped");
                return;
            }
            _forceNextSend = false;
            _hasSentScene = true;
            _lastSceneSignature = signature;

            world.Revision = ++_worldRevision;
            _lastWorldModels = world.Models.Count;
            _ = Task.Run(() => _ = SendWorldAsync(world));
        }
        catch (Exception ex)
        {
            LogLine("export: " + ex.Message);
        }
    }

    /// <summary>Active 3D view first, then default/other 3D views (deduplicated, at most 4).</summary>
    private static List<View3D> CandidateViews(UIDocument? uidoc, Document document)
    {
        var views = new List<View3D>();
        void Add(View3D? view)
        {
            if (view == null || view.IsTemplate) return;
            if (views.Any(v => v.Id == view.Id)) return;
            views.Add(view);
        }

        Add(uidoc?.ActiveGraphicalView as View3D);
        try
        {
            var all = new FilteredElementCollector(document)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate)
                .ToList();
            foreach (var v in all.Where(v => v.Name.Contains("Default", StringComparison.OrdinalIgnoreCase)))
                Add(v);
            foreach (var v in all) Add(v);
        }
        catch { }
        return views.Take(4).ToList();
    }

    private WorldSnapshot ExportView(Document document, View3D view)
    {
        _materials = new RevitMaterialParser(document, TextureSearchPaths(document)) { Log = LogLine };
        var context = new SceneExportContext(document, view, _materials);
        var exporter = new CustomExporter(document, context)
        {
            ShouldStopOnError = false,
            IncludeGeometricObjects = true,
        };
        exporter.Export(view);
        _lastContext = context;
        return context.ToWorldSnapshot();
    }

    /// <summary>
    /// Cheap scene fingerprint used to avoid re-sending an identical full world (startup triggers,
    /// duplicate view/document events). Camera, material properties/textures and geometry endpoints
    /// are included; interior-only edits that keep every count and endpoint unchanged are rare.
    /// </summary>
    private static ulong ComputeSceneSignature(WorldSnapshot world)
    {
        var h = new HashCode();
        h.Add(world.DocumentName);
        h.Add(world.ViewName);
        h.Add(world.Models.Count);
        h.Add(world.Instances.Count);
        h.Add(world.Materials.Count);
        h.Add(world.Lights.Count);
        h.Add(world.Camera.PosX); h.Add(world.Camera.PosY); h.Add(world.Camera.PosZ);
        h.Add(world.Camera.QuatX); h.Add(world.Camera.QuatY); h.Add(world.Camera.QuatZ); h.Add(world.Camera.QuatW);
        h.Add(world.Camera.FovDegrees); h.Add(world.Camera.Ortho);

        foreach (var m in world.Materials)
        {
            h.Add(m.Id); h.Add(m.Name);
            h.Add(m.DiffuseR); h.Add(m.DiffuseG); h.Add(m.DiffuseB);
            h.Add(m.Opacity); h.Add(m.Roughness); h.Add(m.Metallic); h.Add(m.Specular);
            h.Add(m.DiffuseTexture.File); h.Add(m.BumpTexture.File);
        }

        foreach (var model in world.Models)
        {
            h.Add(model.Id);
            h.Add(model.Positions.Length);
            h.Add(model.Indices.Length);
            if (model.Positions.Length >= 3)
            {
                h.Add(model.Positions[0]); h.Add(model.Positions[1]); h.Add(model.Positions[2]);
                int last = model.Positions.Length - 3;
                h.Add(model.Positions[last]); h.Add(model.Positions[last + 1]); h.Add(model.Positions[last + 2]);
            }
            foreach (var g in model.Groups)
            {
                h.Add(g.StartIndex); h.Add(g.NumIndices); h.Add(g.FrontFaceMaterialId);
            }
        }

        return unchecked((ulong)h.ToHashCode());
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
            var sectionPlanes = _lastContext?.GetSectionPlanes() ?? Array.Empty<float>();
            if (sectionPlanes.Length > 0)
                await MessageCodec.SendAsync(stream, MessageType.SectionPlanesUpdate,
                    w => MessageCodec.WriteSectionPlanes(w, sectionPlanes));
            LogLine("World sent.");
            _initialWorldSent = true;
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

            // Revit's two-point perspective keeps the camera up axis vertical; detect it from
            // the engine-space up vector (world Z -> engine +Y).
            bool twoPoint = view.IsPerspective &&
                            MathF.Abs((float)up.Item1) < 1e-3f &&
                            MathF.Abs((float)up.Item3) < 1e-3f;

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
                TwoPointPerspective = twoPoint,
            };
            if (!_cameraModeLogged ||
                camera.Ortho != _lastOrtho ||
                camera.TwoPointPerspective != _lastTwoPoint)
            {
                _cameraModeLogged = true;
                _lastOrtho = camera.Ortho;
                _lastTwoPoint = camera.TwoPointPerspective;
                LogLine($"camera mode: {(camera.Ortho ? "ortho" : camera.TwoPointPerspective ? "two-point" : "perspective")}");
            }
            _lastCameraPush = DateTime.UtcNow;
            _ = SendAsync(MessageType.CameraUpdate, w => MessageCodec.WriteCamera(w, camera));
        }
        catch { }
    }

    /// <summary>P4: pushes the active view section box (or an empty list) when it changes.</summary>
    private void PushSectionPlanes(bool force = false)
    {
        try
        {
            var planes = _lastContext?.GetSectionPlanes() ?? Array.Empty<float>();
            var signature = string.Join(",", planes);
            if (!force && signature == _lastSectionSignature) return;
            _lastSectionSignature = signature;
            _lastSectionPush = DateTime.UtcNow;
            LogLine($"section planes: {planes.Length / 4}");
            _ = SendAsync(MessageType.SectionPlanesUpdate, w => MessageCodec.WriteSectionPlanes(w, planes));
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

    /// <summary>
    /// Revit texture search roots, mirroring Enscape's FileLocator: Revit.ini material libraries and
    /// additional render appearance paths, the per-version RVT material libraries, the shared Autodesk
    /// material libraries, and the document folder.
    /// </summary>
    internal static IEnumerable<string> TextureSearchPaths(Document document)
    {
        var paths = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (Directory.Exists(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    paths.Add(path);
            }
            catch { }
        }

        try
        {
            var ini = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Autodesk Revit 2026", "Revit.ini");
            if (File.Exists(ini))
            {
                foreach (var rawLine in File.ReadAllLines(ini))
                {
                    var line = rawLine.Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (key.Equals("MaterialLibraryFiles", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(value);
                    }
                    else if (key.Equals("AdditionalRenderAppearancePaths", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var part in value.Split(';')) Add(part);
                    }
                }
            }
        }
        catch { }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var autodesk = Path.Combine(programData, "Autodesk");
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(autodesk, "RVT *"))
            {
                Add(Path.Combine(dir, "Materials"));
                Add(Path.Combine(dir, "Materials", "1"));
                Add(Path.Combine(dir, "Materials", "2"));
                Add(Path.Combine(dir, "Materials", "3"));
            }
        }
        catch { }
        Add(Path.Combine(autodesk, "RVT", "Materials", "Textures"));
        Add(Path.Combine(programData, "Autodesk Shared", "Materials", "Textures"));

        var sharedMaterials = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            "Autodesk Shared", "Materials");
        Add(sharedMaterials);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(sharedMaterials)) Add(dir);
        }
        catch { }

        try { Add(Path.GetDirectoryName(document?.PathName ?? "")); } catch { }

        return paths;
    }
}
