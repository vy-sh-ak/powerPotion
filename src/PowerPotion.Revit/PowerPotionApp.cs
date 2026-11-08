using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PowerPotion.Revit.Commands;
using PowerPotion.Revit.Sync;

namespace PowerPotion.Revit;

/// <summary>
/// PowerPotion Revit add-in entry point: builds the ribbon and owns the LiveSyncService.
/// Deliberately contains no license, account, telemetry, or VR functionality.
/// </summary>
public sealed class PowerPotionApp : IExternalApplication
{
    public const string TabName = "PowerPotion";
    private LiveSyncService? _sync;

    public static PowerPotionApp? Instance { get; private set; }
    public static UIControlledApplication? UiApp { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;
        UiApp = application;
        try
        {
            CreateRibbon(application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PowerPotion startup failed: " + ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try { _sync?.Dispose(); } catch { }
        return Result.Succeeded;
    }

    public LiveSyncService GetOrCreateSync(UIApplication uiApp)
    {
        if (_sync == null || !_sync.IsRunning)
        {
            _sync?.Dispose();
            var hostExe = HostExePath();
            var sync = new LiveSyncService(uiApp, hostExe, PluginDir);
            sync.Log += m => System.Diagnostics.Debug.WriteLine("[PowerPotion] " + m);
            _sync = sync;
        }
        return _sync;
    }

    // ------------------------------------------------------------------
    // Ribbon
    // ------------------------------------------------------------------

    private void CreateRibbon(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(TabName); } catch { /* already exists */ }

        var panelControl = app.CreateRibbonPanel(TabName, "Live Rendering");
        var panelTools = app.CreateRibbonPanel(TabName, "Tools");
        var panelHelp = app.CreateRibbonPanel(TabName, "About");

        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        AddButton(panelControl, new PushButtonData(
            "PowerPotionStartLive", "Start\nLive Render", assemblyPath,
            typeof(StartLiveCommand).FullName),
            "Start real-time rendering of the active 3D view.");

        AddButton(panelControl, new PushButtonData(
            "PowerPotionStopLive", "Stop", assemblyPath,
            typeof(StopLiveCommand).FullName),
            "Stop the live render session.");

        AddButton(panelControl, new PushButtonData(
            "PowerPotionPauseLive", "Pause\nUpdates", assemblyPath,
            typeof(PauseLiveCommand).FullName),
            "Pause/resume pushing model changes to the renderer.");

        AddButton(panelTools, new PushButtonData(
            "PowerPotionScreenshot", "Capture\nImage", assemblyPath,
            typeof(ScreenshotCommand).FullName),
            "Save a high-resolution image of the current renderer view.");

        AddButton(panelTools, new PushButtonData(
            "PowerPotionPanorama", "Capture\nPanorama", assemblyPath,
            typeof(PanoramaCommand).FullName),
            "Save a 360° panorama from the current renderer view.");

        AddButton(panelTools, new PushButtonData(
            "PowerPotionMaterials", "Material\nEditor", assemblyPath,
            typeof(MaterialEditorCommand).FullName),
            "Edit Enscape-style material overrides for the current document.");

        AddButton(panelTools, new PushButtonData(
            "PowerPotionSun", "Sun &&\nLocation", assemblyPath,
            typeof(SunCommand).FullName),
            "Re-push sun, date and location to the renderer.");

        AddButton(panelHelp, new PushButtonData(
            "PowerPotionAbout", "About", assemblyPath,
            typeof(AboutCommand).FullName),
            "About PowerPotion.");
    }

    private void AddButton(RibbonPanel panel, PushButtonData data, string tooltip)
    {
        var button = panel.AddItem(data) as PushButton;
        if (button != null)
        {
            button.ToolTip = tooltip;
            button.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
        }
    }

    // ------------------------------------------------------------------
    // Paths
    // ------------------------------------------------------------------

    public static string PluginDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

    public static string HostExePath()
    {
        var candidate = Path.Combine(PluginDir, "PowerPotion.Host.exe");
        if (File.Exists(candidate)) return candidate;

        // dev layout: bin next to the plugin
        var dev = Path.Combine(PluginDir, "..", "..", "..", "..",
            "PowerPotion.Host", "bin", "Release", "net8.0-windows", "PowerPotion.Host.exe");
        dev = Path.GetFullPath(dev);
        if (File.Exists(dev)) return dev;

        // installed layout (per-user)
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerPotion", "PowerPotion.Host.exe");
        return installed;
    }

    /// <summary>Command availability: always visible (mirrors Enscape's AlwaysVisibleExternalCommandAvailability).</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
