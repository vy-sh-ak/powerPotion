using PowerPotion.Protocol;
using PowerPotion.Rendering.Settings;

namespace PowerPotion.Rendering;

/// <summary>
/// The renderer seam the host UI talks to. <see cref="RendererBridge"/> implements it over the
/// standalone Vulkan renderer.
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
    void CaptureScreenshot(string filePath, bool panorama, int width = 0, int height = 0);
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
    Task<bool> CaptureAsync(string path, int width = 0, int height = 0);
    Task<bool> CapturePanoramaAsync(string path, int width = 0, int height = 0);
    void FocusChild();
    void SetVsync(bool vsync);
    void SetHdr(bool hdr);
    void SetWhiteMode(bool enabled);
    void SetSettings(RendererSettings settings);
    void ApplyWorld(WorldSnapshot world, bool applyCamera = true);
    void ApplyCamera(CameraUpdate camera);
    void OverwriteMaterial(MaterialDto material);
    void ApplySun(SunUpdate sun);
    void SetSectionPlanes(float[] planes);
    event Action<long>? SceneUploaded;
    void Stop();
}
