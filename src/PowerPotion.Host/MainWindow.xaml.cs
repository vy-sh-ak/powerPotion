using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PowerPotion.Host.Enscape;
using PowerPotion.Host.Net;
using PowerPotion.Host.Rendering;
using PowerPotion.Protocol;
using PowerPotion.Rendering;

namespace PowerPotion.Host;

public partial class MainWindow : Window
{
    private readonly IRendererBridge _engine;
    private readonly HostSettings _settings;
    private HostConnection? _connection;
    private RendererSurface? _surface;
    private VulkanSurface? _vulkanSurface;
    private readonly List<MaterialDto> _materials = new();
    private MaterialDto? _selectedMaterial;
    private bool _sidebarIsMaterials;
    private bool _suppressPanelEvents;
    private bool _engineReady;
    private readonly bool _connectedToRevit;
    private bool _anyWorldApplied;
    private WorldSnapshot? _pendingWorld;
    private WorldSnapshot? _lastWorld;
    private WorldSnapshot? _latestWorld;
    private bool _worldApplyInFlight;
    private long _lastWorldRevision;
    private DispatcherTimer? _worldApplyTimer;
    private readonly NavigationController _nav;

    private void OnNavigationCommand(ConsoleCommand cmd)
    {
        var parts = (cmd.Command ?? "").Trim().Split(' ');
        if (parts.Length == 0 || parts[0] != "pp_nav") { _engine.ExecuteConsoleCommand(cmd.Command ?? "", cmd.Deferred); return; }
        try
        {
            switch (parts.Length > 1 ? parts[1] : "")
            {
                case "mode" when parts.Length > 2:
                    _nav.SetMode(parts[2] == "walk" ? NavigationController.NavigationMode.Walk : NavigationController.NavigationMode.Fly);
                    break;
                case "follow" when parts.Length > 2:
                    _nav.SetFollowRevit(parts[2] is "on" or "1" or "true");
                    break;
                case "look" when parts.Length > 3:
                    _nav.Look(float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                              float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "move" when parts.Length > 4:
                    _nav.Move(float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                              float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture),
                              float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "teleport" when parts.Length > 4:
                    _nav.Teleport(float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                                  float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture),
                                  float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "fov" when parts.Length > 2:
                    _nav.SetFov(float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "ground" when parts.Length > 2:
                    _nav.SetGroundY(float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "focus":
                    _engine.FocusRenderWindow();
                    break;
                default:
                    HostLog.Write("pp_nav: unknown command '" + cmd.Command + "'");
                    break;
            }
            HostLog.Write("pp_nav: " + cmd.Command);
            var p = _nav.Pose;
            HostLog.Write(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "pp_nav pose mode={0} follow={1} x={2:0.###} y={3:0.###} z={4:0.###} yaw={5:0.##} pitch={6:0.##}",
                _nav.Mode, _nav.FollowRevit, p.X, p.Y, p.Z, p.Yaw, p.Pitch));
        }
        catch (Exception ex) { HostLog.Exception("pp_nav", ex); }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            var value = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
        catch { }
    }

    private void PresentWindow()
    {
        ShowInTaskbar = true;
        if (Left < -10000 || Top < -10000)
        {
            Left = _presentLeft;
            Top = _presentTop;
        }
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnWorldUpdateFinished(int result)
    {
        _worldApplyInFlight = false;

        if (result == 0)
        {
            _anyWorldApplied = true;
            HideRefresh();
            SetStatus("Scene updated.");
            PresentWindow();

            // Right after the first world switch the engine paints its own "chaos Enscape
            // Loading …" screen in the render window for a moment (shader/texture warm-up).
            // Keep the branded splash on top for a short settle period, then reveal the renderer.
            if (_surface is { IsEngineWindowShown: false })
            {
                _splash?.SetStatus("Finalizing renderer…");
                var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                delay.Tick += (_, _) =>
                {
                    delay.Stop();
                    _surface?.ShowEngineWindow();
                    HideLoading();
                };
                delay.Start();
            }
            else
            {
                HideLoading();
            }
        }
        else
        {
            HideRefresh();
            HideLoading();
            PresentWindow();
            SetStatus($"Scene update failed ({result}).");
        }

        // Apply the latest queued snapshot (if any) once this commit has fully settled.
        TryApplyLatestWorld();
    }

    private SplashPresenter? _splash;

    private void ShowLoading(string message)
    {
        if (_splash == null)
        {
            _splash = new SplashPresenter();
            _splash.Show(message);
        }
        else
        {
            _splash.SetStatus(message);
        }
    }

    private void HideLoading()
    {
        _splash?.Close();
        _splash = null;
    }

    private void ShowRefresh()
    {
        if (RefreshBadge.Visibility == Visibility.Visible) return;
        RefreshBadge.Visibility = Visibility.Visible;
        RefreshSpin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.0)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void HideRefresh()
    {
        RefreshSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        RefreshBadge.Visibility = Visibility.Collapsed;
    }

    private void UpdateNavigationUi()
    {
        BtnNavMode.Content = _nav.Mode == NavigationController.NavigationMode.Walk ? "🚶 Walk" : "🛫 Fly";
        BtnFollow.Content = _nav.FollowRevit ? "🔗 Follow Revit: on" : "🔗 Follow Revit: off";
        HostLog.Write($"nav ui: follow={_nav.FollowRevit} mode={_nav.Mode}");
        SetStatus(_nav.FollowRevit
            ? "Following the Revit view."
            : $"Free look ({_nav.Mode}) — W/A/S/D, drag look, wheel, Shift/Ctrl; Q/E in fly mode.");
    }

    private void OnToggleNavMode(object sender, RoutedEventArgs e)
    {
        if (_nav.Mode == NavigationController.NavigationMode.Fly)
            _nav.SetGroundY(ComputeGroundY());
        _nav.SetMode(_nav.Mode == NavigationController.NavigationMode.Walk
            ? NavigationController.NavigationMode.Fly
            : NavigationController.NavigationMode.Walk);
    }

    private void OnToggleFollow(object sender, RoutedEventArgs e) =>
        _nav.SetFollowRevit(!_nav.FollowRevit);

    /// <summary>Lowest scene vertex near the camera — the floor used by walk mode.</summary>
    private float ComputeGroundY()
    {
        var world = _lastWorld;
        if (world == null) return 0f;
        var p = _nav.Pose;
        var best = float.MaxValue;
        var any = false;
        foreach (var model in world.Models)
        {
            var positions = model.Positions;
            for (var i = 0; i + 2 < positions.Length; i += 3)
            {
                var x = positions[i];
                var y = positions[i + 1];
                var z = positions[i + 2];
                if (Math.Abs(x - p.X) > 30f || Math.Abs(z - p.Z) > 30f) continue;
                if (y > p.Y + 0.3f) continue;
                if (y < best) { best = y; any = true; }
            }
        }
        return any ? best : 0f;
    }

    private readonly string _captureDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PowerPotion");
    private readonly double _presentLeft;
    private readonly double _presentTop;

    public MainWindow(IRendererBridge engine, HostSettings settings, HostConnection? connection)
    {
        _engine = engine;
        _settings = settings;
        _connection = connection;
        _connectedToRevit = connection != null;
        Directory.CreateDirectory(_captureDir);

        InitializeComponent();
        Title = "PowerPotion — Live Renderer";
        ApplyDarkTitleBar();

        // Start off-screen: the engine start blocks this UI thread for several seconds and a visible
        // window would make Windows show the busy/loading cursor. The splash lives on its own thread;
        // the main window is moved on-screen once the renderer is ready.
        var work = SystemParameters.WorkArea;
        _presentLeft = work.Left + Math.Max(0, (work.Width - Width) / 2);
        _presentTop = work.Top + Math.Max(0, (work.Height - Height) / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Left = -32000;
        Top = -32000;

        _nav = new NavigationController(_engine);
        _nav.FollowChanged += UpdateNavigationUi;
        _nav.ModeChanged += UpdateNavigationUi;

        // Coalesce world snapshots: Revit can send a burst of full worlds while editing. Apply the
        // newest one only, and only after the previous engine commit, instead of clearing/rebuilding
        // the scene dozens of times per second (which keeps the engine on its loading screen).
        _worldApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _worldApplyTimer.Tick += (_, _) => TryApplyLatestWorld();
        _worldApplyTimer.Start();

        // Clicking the render area must focus the native window so the engine receives WASD;
        // clicking the toolbar must not steal focus from the buttons.
        Activated += (_, _) =>
        {
            if (_engine.NativeWindowHandle != IntPtr.Zero && Enscape.Win32.IsOverWindow(_engine.NativeWindowHandle))
                _engine.FocusRenderWindow();
        };
        Deactivated += (_, _) => _engine.NotifyFocus(false);

        _engine.WorldUpdateFinished += result =>
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => OnWorldUpdateFinished(result)));
        ShowLoading(connection != null ? "Connecting to Revit…" : "Starting renderer…");

        _engine.Log += m =>
        {
            // Engine diagnostics stay in the log; the status bar must never surface engine-branded
            // text/paths (e.g. the installed Enscape directory).
            HostLog.Write("engine: " + m);
        };
        _engine.ClosedFromEngine += () =>
        {
            HostLog.Write("Engine closed from engine callback");
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(OnEngineClosed));
        };
        _engine.CaptureFinished += ok => Dispatcher.Invoke(() => SetStatus(ok ? "Capture saved." : "Capture failed."));

        if (_connection != null)
        {
            _connection.Log += m => Dispatcher.Invoke(() => SetStatus(m));
            _connection.WorldReceived += world => Dispatcher.Invoke(() => OnWorld(world));
            _connection.CameraReceived += cam => _nav.OnRevitCamera(cam);
            _connection.SunReceived += sun => Dispatcher.Invoke(() => _engine.ApplySun(sun));
            _connection.CommandReceived += OnNavigationCommand;
            _connection.CaptureReceived += req => Dispatcher.Invoke(() => OnCaptureRequest(req));
            _connection.MaterialReceived += mat => Dispatcher.Invoke(() =>
            {
                _engine.ExecuteConsoleCommand("echo material", false);
                _engine.OverwriteMaterial(mat.Material);
            });
            _connection.ViewActivationReceived += view =>
            {
                _nav.OnRevitCamera(view.Camera);
                _engine.ApplySun(view.Sun);
                SetStatus($"View: {view.Name}");
            };
            _connection.SectionPlanesReceived += planes =>
                Dispatcher.Invoke(() => _engine.SetSectionPlanes(planes.Planes));
            _connection.Disconnected += () => Dispatcher.Invoke(() =>
            {
                SetStatus("Disconnected from Revit.");
                _connection = null;
                // The renderer host is a child of the Revit session: do not leave an orphan window
                // behind when Revit closes/crashes (Enscape's host behaves the same way).
                if (_connectedToRevit) Close();
            });
        }

        EngineText.Text = "PowerPotion " + Program.ThisVersion();

        // r_hwrt/r_dlss are only accepted before the engine is initialized.
        foreach (var cmd in _settings.ToPreInitCommands())
            _engine.PreInitCommands.Add(cmd);

        // Build the renderer surface once loaded.
        Loaded += (_, _) =>
        {
            if (_surface != null || _vulkanSurface != null) return;

            if (_engine is RendererBridge rendererBridge)
            {
                HostLog.Write("MainWindow.Loaded → creating VulkanSurface (standalone renderer)");
                var surface = new VulkanSurface();
                _vulkanSurface = surface;
                rendererBridge.AttachSurface(surface);
                surface.Log += m =>
                {
                    HostLog.Write("renderer: " + m);
                    SetStatus(m);
                };
                surface.Ready += () =>
                {
                    HostLog.Write("VulkanSurface.Ready → applying visual settings");
                    _engineReady = true;
                    _nav.SetRenderWindow(surface.ChildHwnd);
                    _nav.Enabled = true;
                    SetStatus("Renderer ready (Vulkan P0).");
                    ApplySettings();
                    _nav.ApplyModeToEngine();
                    UpdateNavigationUi();
                    PresentWindow();
                    HideLoading();
                    _ = _connection?.SendEngineReadyAsync(true, "");
                    if (_pendingWorld != null)
                    {
                        var world = _pendingWorld;
                        _pendingWorld = null;
                        OnWorld(world);
                    }
                };
                surface.Failed += ex =>
                {
                    HostLog.Exception("VulkanSurface.Failed", ex);
                    ShowLoading("Renderer failed to start — see logs.");
                    PresentWindow();
                    _ = _connection?.SendEngineReadyAsync(false, ex.Message);
                };
                SurfaceHost.Children.Add(surface);
                return;
            }

            HostLog.Write("MainWindow.Loaded → creating RendererSurface");
            if (_engine is not EngineBridge engineBridge)
            {
                ShowLoading("Unsupported renderer bridge.");
                PresentWindow();
                return;
            }
            _surface = new RendererSurface(engineBridge, () => DocLabel.Text);
            _surface.Log += m => SetStatus(m);
            _surface.EngineReady += () =>
            {
                HostLog.Write("RendererSurface.EngineReady → applying visual settings");
                _engineReady = true;
                _nav.SetRenderWindow(_engine.NativeWindowHandle);
                _nav.Enabled = true;
                SetStatus("Renderer ready.");
                ApplySettings();
                _nav.ApplyModeToEngine();
                UpdateNavigationUi();
                if (_pendingWorld == null)
                {
                    // Do not reveal the native window yet: the engine paints its own "Enscape"
                    // splash there until it has a scene, and that native splash used to appear
                    // behind the branded loader. The window is shown on the first world commit.
                    ShowLoading("Renderer ready — waiting for scene from Revit…");
                    if (!_connectedToRevit) PresentWindow();
                }
                _ = _connection?.SendEngineReadyAsync(true, "");
                if (_pendingWorld != null)
                {
                    var world = _pendingWorld;
                    _pendingWorld = null;
                    OnWorld(world);
                }
            };
            _surface.EngineFailed += ex =>
            {
                HostLog.Exception("RendererSurface.EngineFailed", ex);
                ShowLoading("Renderer failed to start — see logs.");
                PresentWindow();
                var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                hide.Tick += (_, _) => { hide.Stop(); HideLoading(); };
                hide.Start();
                _ = _connection?.SendEngineReadyAsync(false, ex.Message);
            };
            SurfaceHost.Children.Add(_surface);
        };

        BuildSettingsPanel();
        ApplySettings();
        SetStatus(connection != null ? "Waiting for scene from Revit…" : "Standalone mode (no Revit connection).");
    }

    private void OnWorld(WorldSnapshot world)
    {
        // Transient empty exports (document reopening, view regenerating, stale Revit view) used to
        // clear the renderer over and over and flash its loading screen. Keep the current scene (or
        // the branded splash) until a world with geometry arrives.
        if (world.Models.Count == 0)
        {
            HostLog.Write("OnWorld ignored: empty world (scene kept)");
            if (!_anyWorldApplied)
                ShowLoading("No visible geometry in the active 3D view — unhide the model or pick another 3D view.");
            SetStatus("Revit sent an empty scene — keeping the current view.");
            return;
        }

        if (world.Revision > 0 && world.Revision <= _lastWorldRevision)
        {
            HostLog.Write($"OnWorld ignored: duplicate revision {world.Revision}");
            return;
        }

        if (!_engineReady)
        {
            // Revit exports and sends the scene immediately after connecting; the native engine
            // takes several seconds to come up. Keep the latest snapshot until it is ready.
            _pendingWorld = world;
            ShowLoading("Scene received — starting renderer…");
            SetStatus($"Scene received ({world.Models.Count} models) — waiting for renderer…");
            HostLog.Write($"OnWorld queued: {world.Models.Count} models, {world.Instances.Count} instances, " +
                          $"{world.Materials.Count} materials, {world.Lights.Count} lights");
            return;
        }

        // Latest wins: a burst of exports results in a single apply of the newest snapshot.
        _latestWorld = world;
        if (_anyWorldApplied) ShowRefresh();
        else ShowLoading("Importing scene from Revit…");
    }

    /// <summary>Applies the newest queued world, one at a time, after the previous engine commit.</summary>
    private void TryApplyLatestWorld()
    {
        if (!_engineReady || _worldApplyInFlight) return;
        var world = _latestWorld;
        if (world == null) return;

        if (world.Revision > 0 && world.Revision <= _lastWorldRevision)
        {
            _latestWorld = null;
            return;
        }

        _latestWorld = null;
        _worldApplyInFlight = true;
        if (world.Revision > 0) _lastWorldRevision = world.Revision;

        SetStatus($"Received world '{world.ViewName}' — {world.Models.Count} models, {world.Materials.Count} materials, {world.Lights.Count} lights. Updating…");
        HostLog.Write($"OnWorld apply: {world.Models.Count} models, {world.Instances.Count} instances, " +
                      $"{world.Materials.Count} materials, {world.Lights.Count} lights (rev {world.Revision})");
        if (!_anyWorldApplied) ShowLoading("Importing scene from Revit…");
        else ShowRefresh();
        try
        {
            _engine.ApplyWorld(world, applyCamera: _nav.FollowRevit);
        }
        catch (Exception ex)
        {
            _worldApplyInFlight = false;
            HostLog.Exception("ApplyWorld", ex);
            SetStatus("Renderer rejected the scene — see logs.");
            return;
        }
        _materials.Clear();
        _materials.AddRange(world.Materials);
        if (_sidebarIsMaterials) BuildMaterialsPanel();
        DocLabel.Text = world.DocumentName;
        _lastWorld = world;
        SetStatus("World updated.");
    }

    private void OnCaptureRequest(CaptureRequest req)
    {
        var path = string.IsNullOrWhiteSpace(req.FilePath)
            ? Path.Combine(_captureDir, $"PowerPotion-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png")
            : req.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _engine.CaptureScreenshot(path, req.Panorama, req.Width, req.Height);
        if (_connection != null)
        {
            // result is reported via engine callback; also ack immediately with path
            _ = _connection.SendCaptureResultAsync(new CaptureResult { Ok = true, FilePath = path });
        }
        SetStatus($"Capturing to {Path.GetFileName(path)}…");
    }

    private void OnEngineClosed()
    {
        SetStatus("Renderer closed.");
        if (_connection != null) _ = _connection.SendRendererClosedAsync("engine-closed");
        _ = _connection?.SendRendererClosedAsync("engine-closed");
        _connection = null;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    public void SetDocumentName(string name) => DocLabel.Text = name;

    // ------------------------------------------------------------------
    // Toolbar
    // ------------------------------------------------------------------

    private void OnToggleLive(object sender, RoutedEventArgs e)
    {
        if (_connection == null) { SetStatus("No Revit connection."); return; }
        var paused = !_connection.LiveUpdatesPaused;
        _ = _connection.SendLiveUpdateStateAsync(paused);
        BtnLive.Content = paused ? "▶ Resume Updates" : "⏸ Pause Updates";
    }

    private void OnScreenshot(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_captureDir, $"PowerPotion-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _engine.CaptureScreenshot(path, panorama: false);
        SetStatus($"Capturing to {Path.GetFileName(path)}…");
    }

    private void OnPanorama(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_captureDir, $"PowerPotion-pano-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _engine.CaptureScreenshot(path, panorama: true);
        SetStatus("Capturing panorama…");
    }

    private void OnToggleSettings(object sender, RoutedEventArgs e)
    {
        _sidebarIsMaterials = false;
        SidebarColumn.Width = SidebarColumn.Width.Value > 0 && !_sidebarIsMaterials ? new GridLength(0) : new GridLength(340);
        if (SidebarColumn.Width.Value > 0) BuildSettingsPanel();
    }

    private void OnToggleMaterials(object sender, RoutedEventArgs e)
    {
        _sidebarIsMaterials = true;
        SidebarColumn.Width = SidebarColumn.Width.Value > 0 && _sidebarIsMaterials ? new GridLength(0) : new GridLength(340);
        if (SidebarColumn.Width.Value > 0) BuildMaterialsPanel();
    }

    // ------------------------------------------------------------------
    // Settings panel (generated from HostSettings)
    // ------------------------------------------------------------------

    private void ApplySettings()
    {
        foreach (var (command, deferred) in _settings.ToCommands())
            _engine.ExecuteConsoleCommand(command, deferred);
    }

    private void BuildSettingsPanel()
    {
        _suppressPanelEvents = true;
        SidebarTitle.Text = "Visual Settings";
        SidebarContent.Children.Clear();

        AddGroup("Quality");
        AddCombo("Quality preset", nameof(_settings.QualityLevel), new[] { "Draft", "Medium", "High", "Ultra" }, _settings.QualityLevel);
        AddToggle("Hardware ray tracing", nameof(_settings.HardwareRayTracing), _settings.HardwareRayTracing);
        AddToggle("DLSS upscaling", nameof(_settings.Dlss), _settings.Dlss);
        AddToggle("Ray traced sun shadows", nameof(_settings.RtSunShadows), _settings.RtSunShadows);
        AddToggle("Ray traced artificial lights", nameof(_settings.RtArtificialLights), _settings.RtArtificialLights);

        AddGroup("Sky & Clouds");
        AddSlider("Skybox rotation (°)", nameof(_settings.SkyboxRotation), -180, 180, _settings.SkyboxRotation, 1);
        AddSlider("Skybox brightness", nameof(_settings.SkyboxBrightness), 0, 2, _settings.SkyboxBrightness, 0.01);
        AddToggle("Sun spot in skybox", nameof(_settings.SkyboxUseSunSpot), _settings.SkyboxUseSunSpot);
        AddFilePicker("Skybox image", nameof(_settings.SkyboxFile), _settings.SkyboxFile);
        AddSlider("Cloud density", nameof(_settings.CloudDensity), 0, 1, _settings.CloudDensity, 0.01);
        AddSlider("Cirrus clouds", nameof(_settings.CloudCirrus), 0, 1, _settings.CloudCirrus, 0.01);
        AddSlider("Cloud variety", nameof(_settings.CloudVariety), 0, 1, _settings.CloudVariety, 0.01);
        AddSlider("Contrails", nameof(_settings.CloudContrails), 0, 1, _settings.CloudContrails, 0.01);
        AddSlider("Cloud offset X", nameof(_settings.CloudOffsetX), -10, 10, _settings.CloudOffsetX, 0.1);
        AddSlider("Cloud offset Z", nameof(_settings.CloudOffsetZ), -10, 10, _settings.CloudOffsetZ, 0.1);
        AddSlider("Fog amount", nameof(_settings.FogAmount), 0, 1, _settings.FogAmount, 0.01);
        AddSlider("Fog height (m)", nameof(_settings.FogHeight), 0, 500, _settings.FogHeight, 1);
        AddSlider("Star brightness", nameof(_settings.StarBrightness), 0, 1, _settings.StarBrightness, 0.01);

        AddGroup("Sun & Light");
        AddSlider("Sun power", nameof(_settings.SunPower), 0, 3, _settings.SunPower, 0.05);
        AddSlider("Sun shadow sharpness", nameof(_settings.SunShadowSharpness), 0, 1, _settings.SunShadowSharpness, 0.01);
        AddSlider("Artificial light brightness", nameof(_settings.LightBrightness), 0, 3, _settings.LightBrightness, 0.05);
        AddSlider("Ambient light amount", nameof(_settings.AmbientAmount), 0, 3, _settings.AmbientAmount, 0.05);

        AddGroup("Image");
        AddSlider("Saturation", nameof(_settings.Saturation), 0, 2, _settings.Saturation, 0.01);
        AddSlider("Color temperature (K)", nameof(_settings.ColorTemperature), 2000, 20000, _settings.ColorTemperature, 100);
        AddSlider("Bloom intensity", nameof(_settings.BloomIntensity), 0, 1, _settings.BloomIntensity, 0.01);
        AddToggle("Auto contrast", nameof(_settings.AutoContrast), _settings.AutoContrast);
        AddSlider("Vignette", nameof(_settings.Vignette), 0, 1, _settings.Vignette, 0.01);
        AddSlider("Lens flare", nameof(_settings.LensFlare), 0, 1, _settings.LensFlare, 0.01);
        AddSlider("Motion blur", nameof(_settings.MotionBlur), 0, 1, _settings.MotionBlur, 0.01);
        AddSlider("Chromatic aberration", nameof(_settings.ChromaticAberration), 0, 1, _settings.ChromaticAberration, 0.01);
        AddToggle("White background (concept mode)", nameof(_settings.WhiteBackground), _settings.WhiteBackground);

        var apply = new Button { Content = "Apply to renderer", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        apply.Click += (_, _) => { ApplySettings(); _settings.Save(); SetStatus("Settings applied."); };
        SidebarContent.Children.Add(apply);
        _suppressPanelEvents = false;
    }

    private void AddGroup(string title)
    {
        SidebarContent.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0x88, 0xFF)),
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Margin = new Thickness(0, 16, 0, 6),
        });
    }

    private Slider AddSlider(string label, string prop, double min, double max, double value, double step)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var header = new DockPanel();
        var tb = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xCC, 0xF0)), FontSize = 11 };
        var val = new TextBlock
        {
            Text = value.ToString("0.##"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0x96, 0xC9)),
            FontSize = 11,
        };
        DockPanel.SetDock(val, Dock.Right);
        header.Children.Add(val);
        header.Children.Add(tb);

        var slider = new Slider { Minimum = min, Maximum = max, Value = value };
        if (step >= 1) { slider.IsSnapToTickEnabled = true; slider.TickFrequency = step; }
        else slider.SmallChange = step;

        slider.ValueChanged += (_, _) =>
        {
            if (_suppressPanelEvents) return;
            val.Text = slider.Value.ToString("0.##");
            SetSetting(prop, slider.Value);
            ApplySettings();
        };

        sp.Children.Add(header);
        sp.Children.Add(slider);
        SidebarContent.Children.Add(sp);
        return slider;
    }

    private void AddToggle(string label, string prop, bool value)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xCC, 0xF0)),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 4),
        };
        cb.Checked += (_, _) => { if (!_suppressPanelEvents) { SetSetting(prop, true); ApplySettings(); } };
        cb.Unchecked += (_, _) => { if (!_suppressPanelEvents) { SetSetting(prop, false); ApplySettings(); } };
        SidebarContent.Children.Add(cb);
    }

    private void AddCombo(string label, string prop, string[] options, int index)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xCC, 0xF0)),
            FontSize = 11,
        });
        var combo = new ComboBox();
        foreach (var o in options) combo.Items.Add(o);
        combo.SelectedIndex = index;
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppressPanelEvents || combo.SelectedIndex < 0) return;
            SetSetting(prop, (double)combo.SelectedIndex);
            ApplySettings();
        };
        sp.Children.Add(combo);
        SidebarContent.Children.Add(sp);
    }

    private void AddFilePicker(string label, string prop, string current)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xCC, 0xF0)),
            FontSize = 11,
        });
        var dock = new DockPanel();
        var clear = new Button { Content = "×", Width = 24, ToolTip = "Clear" };
        DockPanel.SetDock(clear, Dock.Right);
        clear.Click += (_, _) =>
        {
            SetSetting(prop, "");
            ApplySettings();
            BuildSettingsPanel();
        };
        var browse = new Button { Content = "…", Width = 28 };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select skybox image (HDR/PNG/JPG panorama)",
                Filter = "Skybox images|*.hdr;*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
            };
            if (dlg.ShowDialog(this) == true)
            {
                SetSetting(prop, dlg.FileName);
                ApplySettings();
                BuildSettingsPanel();
            }
        };
        var path = new TextBlock
        {
            Text = string.IsNullOrEmpty(current) ? "(engine default sky)" : System.IO.Path.GetFileName(current),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0x96, 0xC9)),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dock.Children.Add(clear);
        dock.Children.Add(browse);
        dock.Children.Add(path);
        sp.Children.Add(dock);
        SidebarContent.Children.Add(sp);
    }

    private void SetSetting(string property, object value)
    {
        var pi = typeof(HostSettings).GetProperty(property);
        if (pi == null) return;
        try
        {
            var target = pi.PropertyType == typeof(double) && value is double d ? d
                : pi.PropertyType == typeof(bool) && value is bool b ? b
                : pi.PropertyType == typeof(string) && value is string s ? s
                : Convert.ChangeType(value, pi.PropertyType);
            pi.SetValue(_settings, target);
        }
        catch { }
        _settings.Save();
    }

    // ------------------------------------------------------------------
    // Materials panel
    // ------------------------------------------------------------------

    private void BuildMaterialsPanel()
    {
        SidebarTitle.Text = "Materials";
        SidebarContent.Children.Clear();

        var info = new TextBlock
        {
            Text = $"{_materials.Count} materials in scene. Select one to tweak it live in the renderer " +
                   "(changes are session-only; use the Material Editor in Revit to write back).",
            Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0x96, 0xC9)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        SidebarContent.Children.Add(info);

        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        foreach (var m in _materials)
        {
            var item = new ListBoxItem { Content = m.Name, Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xCC, 0xF0)) };
            item.Selected += (_, _) => ShowMaterialEditor(m);
            list.Items.Add(item);
        }
        SidebarContent.Children.Add(list);
    }

    private void ShowMaterialEditor(MaterialDto m)
    {
        SidebarContent.Children.Clear();
        SidebarTitle.Text = "Material: " + m.Name;

        var back = new Button { Content = "← back to list", Margin = new Thickness(0, 0, 0, 10) };
        back.Click += (_, _) => BuildMaterialsPanel();
        SidebarContent.Children.Add(back);

        AddGroup("Albedo");
        AddMaterialSlider("Color R", v => m.DiffuseR = v, m.DiffuseR);
        AddMaterialSlider("Color G", v => m.DiffuseG = v, m.DiffuseG);
        AddMaterialSlider("Color B", v => m.DiffuseB = v, m.DiffuseB);

        AddGroup("Reflection");
        AddMaterialSlider("Roughness", v => m.Roughness = v, m.Roughness);
        AddMaterialSlider("Metallic", v => m.Metallic = v, m.Metallic);
        AddMaterialSlider("Specular", v => m.Specular = v, m.Specular);

        AddGroup("Transparency");
        AddMaterialSlider("Opacity", v => m.Opacity = v, m.Opacity);
        AddMaterialSlider("Refraction index", v => m.Refraction = v, m.Refraction);

        AddGroup("Emission");
        AddMaterialSlider("Emissive strength", v => m.EmissiveStrength = v, m.EmissiveStrength);
        AddMaterialSlider("Emissive R", v => m.EmissiveR = v, m.EmissiveR);
        AddMaterialSlider("Emissive G", v => m.EmissiveG = v, m.EmissiveG);
        AddMaterialSlider("Emissive B", v => m.EmissiveB = v, m.EmissiveB);

        var apply = new Button { Content = "Push to renderer", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        apply.Click += (_, _) =>
        {
            _engine.ExecuteConsoleCommand("echo", false); // keep scope alive pattern consistent
            PushMaterialToEngine(m);
            SetStatus($"Material '{m.Name}' updated in renderer.");
        };
        SidebarContent.Children.Add(apply);
    }

    private void AddMaterialSlider(string label, Action<float> set, float initial)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xCC, 0xF0)), FontSize = 11 });
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = Math.Clamp(initial, 0, 1) };
        slider.ValueChanged += (_, _) => set((float)slider.Value);
        sp.Children.Add(slider);
        SidebarContent.Children.Add(sp);
    }

    private void PushMaterialToEngine(MaterialDto m)
    {
        // Overwrite semantics: remove + re-add with the same id (mirrors Cad_RenderingSession_OverwriteMaterial).
        try
        {
            _engine.ExecuteConsoleCommand("echo", false);
        }
        catch { }
        // Full path: re-apply whole world materials through a targeted update — simplest correct approach
        // is to send an update through the engine bridge; we re-add the material by id after removal.
        _engine.OverwriteMaterial(m);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _splash?.Close();
            _nav.Dispose();
            _connection?.Dispose();
            _vulkanSurface?.Stop();
            _engine.Dispose();
        }
        catch { }
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
