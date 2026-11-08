using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PowerPotion.Revit.Sync;

namespace PowerPotion.Revit.Commands;

/// <summary>Starts (or restarts) the live render session for the active 3D view.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class StartLiveCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var sync = PowerPotionApp.Instance?.GetOrCreateSync(uiApp);
        if (sync == null) return Result.Failed;

        if (sync.IsRunning)
        {
            TaskDialog.Show("PowerPotion", "Live render is already running.");
            return Result.Succeeded;
        }

        View3D? view = null;
        var uidoc = uiApp.ActiveUIDocument;
        if (uidoc?.ActiveGraphicalView is View3D v3d) view = v3d;
        sync.Start(view);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class StopLiveCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        PowerPotionApp.Instance?.GetOrCreateSync(commandData.Application).Stop();
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class PauseLiveCommand : IExternalCommand
{
    private static bool _paused;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sync = PowerPotionApp.Instance?.GetOrCreateSync(commandData.Application);
        if (sync?.IsRunning != true)
        {
            TaskDialog.Show("PowerPotion", "Start the live render first.");
            return Result.Succeeded;
        }
        _paused = !_paused;
        sync.SetPaused(_paused);
        TaskDialog.Show("PowerPotion", _paused ? "Live updates paused." : "Live updates resumed.");
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ScreenshotCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sync = PowerPotionApp.Instance?.GetOrCreateSync(commandData.Application);
        if (sync?.IsRunning != true)
        {
            TaskDialog.Show("PowerPotion", "Start the live render first.");
            return Result.Succeeded;
        }
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PowerPotion");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"PowerPotion-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png");
        sync.RequestCapture(file, panorama: false);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class PanoramaCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sync = PowerPotionApp.Instance?.GetOrCreateSync(commandData.Application);
        if (sync?.IsRunning != true)
        {
            TaskDialog.Show("PowerPotion", "Start the live render first.");
            return Result.Succeeded;
        }
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PowerPotion");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"PowerPotion-pano-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png");
        sync.RequestCapture(file, panorama: true);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class SunCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sync = PowerPotionApp.Instance?.GetOrCreateSync(commandData.Application);
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (sync?.IsRunning != true || doc == null)
        {
            TaskDialog.Show("PowerPotion", "Start the live render first.");
            return Result.Succeeded;
        }
        if (doc.ActiveView is View3D view)
            sync.PushSun(view);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        TaskDialog.Show("PowerPotion 1.0",
            "PowerPotion — a personalized real-time rendering companion for Revit.\n\n" +
            "Reuses the rendering engine of your installed Enscape product at runtime.\n" +
            "No VR, no accounts, no license or telemetry code.\n\n" +
            "See docs/ARCHITECTURE.md in the repository for the reverse-engineering notes.");
        return Result.Succeeded;
    }
}
