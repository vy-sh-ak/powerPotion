using PowerPotion.Protocol;
using PowerPotion.Rendering.Settings;

namespace PowerPotion.Rendering;

/// <summary>
/// The renderer seam the host UI already talks to (the shape of the old <c>EngineBridge</c>).
/// <see cref="Host.Enscape.EngineBridge"/> and <see cref="RendererBridge"/> both implement it.
/// </summary>
public interface IRendererBridge : IDisposable
{
    bool IsRunning { get; }
    string EngineVersion { get; }
    IntPtr NativeWindowHandle { get; }
    List<string> PreInitCommands { get; }

    event Action<string>? Log;
    event Action? ClosedFromEngine;
    event Action<bool>? CaptureFinished;
    event Action<int>? WorldUpdateFinished;

    void ApplyWorld(WorldSnapshot world, bool applyCamera = true);
    void OverwriteMaterial(MaterialDto material);
    void ApplySun(SunUpdate sun);
    void ApplyCamera(CameraUpdate camera);
    CameraUpdate? ReadCamera();
    void SetSectionPlanes(float[] planes);
    void ExecuteConsoleCommand(string command, bool deferred);
    void WaitForIdle();
    void WaitForFrames(ulong frames);
    void CaptureScreenshot(string filePath, bool panorama);
    Task<bool> CaptureDone { get; }
    Task StopAsync();
    void NotifyFocus(bool focused);
    void NotifyMinimize(bool minimized);
    void FocusRenderWindow();
}

/// <summary>A window-backed render surface (the HwndHost side of the bridge).</summary>
public interface IRenderSurface
{
    IntPtr ChildHwnd { get; }
    bool IsReady { get; }
    string DeviceName { get; }
    void Resize(int width, int height);
    Task<bool> CaptureAsync(string path);
    void FocusChild();
    void SetVsync(bool vsync);
    void SetHdr(bool hdr);
    void SetWhiteMode(bool enabled);
    void SetSettings(RendererSettings settings);
    void ApplyWorld(WorldSnapshot world, bool applyCamera = true);
    void ApplyCamera(CameraUpdate camera);
    void OverwriteMaterial(MaterialDto material);
    void ApplySun(SunUpdate sun);
    event Action<long>? SceneUploaded;
    void Stop();
}
