using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerPotion.Host.Enscape;
using PowerPotion.Host.Net;
using PowerPotion.Protocol;

namespace PowerPotion.Host;

public partial class MainWindow : Window
{
    private readonly EngineBridge _engine;
    private readonly HostSettings _settings;
    private HostConnection? _connection;
    private RendererSurface? _surface;
    private readonly List<MaterialDto> _materials = new();
    private MaterialDto? _selectedMaterial;
    private bool _sidebarIsMaterials;
    private bool _suppressPanelEvents;

    private readonly string _captureDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PowerPotion");

    public MainWindow(EngineBridge engine, HostSettings settings, HostConnection? connection)
    {
        _engine = engine;
        _settings = settings;
        _connection = connection;
        Directory.CreateDirectory(_captureDir);

        InitializeComponent();
        Title = "PowerPotion — Live Renderer";

        _engine.Log += m => Dispatcher.Invoke(() => SetStatus(m));
        _engine.ClosedFromEngine += () => Dispatcher.Invoke(OnEngineClosed);
        _engine.CaptureFinished += ok => Dispatcher.Invoke(() => SetStatus(ok ? "Capture saved." : "Capture failed."));

        if (_connection != null)
        {
            _connection.Log += m => Dispatcher.Invoke(() => SetStatus(m));
            _connection.WorldReceived += world => Dispatcher.Invoke(() => OnWorld(world));
            _connection.CameraReceived += cam => Dispatcher.Invoke(() => _engine.ApplyCamera(cam));
            _connection.SunReceived += sun => Dispatcher.Invoke(() => _engine.ApplySun(sun));
            _connection.CommandReceived += cmd => Dispatcher.Invoke(() =>
                _engine.ExecuteConsoleCommand(cmd.Command, cmd.Deferred));
            _connection.CaptureReceived += req => Dispatcher.Invoke(() => OnCaptureRequest(req));
            _connection.MaterialReceived += mat => Dispatcher.Invoke(() =>
            {
                _engine.ExecuteConsoleCommand("echo material", false);
            });
            _connection.ViewActivationReceived += view => Dispatcher.Invoke(() =>
            {
                _engine.ApplyCamera(view.Camera);
                _engine.ApplySun(view.Sun);
                SetStatus($"View: {view.Name}");
            });
            _connection.Disconnected += () => Dispatcher.Invoke(() =>
            {
                SetStatus("Disconnected from Revit.");
                _connection = null;
            });
        }

        EngineText.Text = "engine " + _engine.EngineVersion;

        // Build the renderer surface once loaded.
        Loaded += (_, _) =>
        {
            if (_surface != null) return;
            _surface = new RendererSurface(_engine, () => DocLabel.Text);
            _surface.Log += m => SetStatus(m);
            SurfaceHost.Children.Add(_surface);
        };

        BuildSettingsPanel();
        ApplySettings();
        SetStatus(connection != null ? "Waiting for scene from Revit…" : "Standalone mode (no Revit connection).");
    }

    private void OnWorld(WorldSnapshot world)
    {
        SetStatus($"Received world '{world.ViewName}' — {world.Models.Count} models, {world.Materials.Count} materials, {world.Lights.Count} lights. Updating…");
        _engine.ApplyWorld(world);
        _materials.Clear();
        _materials.AddRange(world.Materials);
        if (_sidebarIsMaterials) BuildMaterialsPanel();
        DocLabel.Text = world.DocumentName;
        SetStatus("World updated.");
    }

    private void OnCaptureRequest(CaptureRequest req)
    {
        var path = string.IsNullOrWhiteSpace(req.FilePath)
            ? Path.Combine(_captureDir, $"PowerPotion-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png")
            : req.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _engine.CaptureScreenshot(path, req.Panorama);
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x7C, 0xFF)),
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Margin = new Thickness(0, 16, 0, 6),
        });
    }

    private Slider AddSlider(string label, string prop, double min, double max, double value, double step)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var header = new DockPanel();
        var tb = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)), FontSize = 11 };
        var val = new TextBlock
        {
            Text = value.ToString("0.##"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)),
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
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        SidebarContent.Children.Add(info);

        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        foreach (var m in _materials)
        {
            var item = new ListBoxItem { Content = m.Name, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)) };
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
        sp.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)), FontSize = 11 });
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
            _connection?.Dispose();
            _engine.Dispose();
        }
        catch { }
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
