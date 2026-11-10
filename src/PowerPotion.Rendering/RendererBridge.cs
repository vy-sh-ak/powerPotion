using PowerPotion.Protocol;
using PowerPotion.Rendering.Settings;

namespace PowerPotion.Rendering;

/// <summary>
/// Adapter between the existing host seam and the standalone Vulkan renderer. The render surface
/// owns the Vulkan device/swapchain; this class keeps the same public shape as the old Enscape
/// bridge so <c>MainWindow</c> and <c>NavigationController</c> need almost no changes.
///
/// P1 wires swapchain options; P2 applies worlds/materials/camera; P3 maps the CVar surface into
/// <see cref="Settings.RendererSettings"/> and forwards sun/sky/shadow/light controls.
/// </summary>
public sealed class RendererBridge : IRendererBridge
{
    private IRenderSurface? _surface;
    private WorldSnapshot? _lastWorld;
    private CameraUpdate? _camera;
    private readonly RendererSettings _settings = new();
    private TaskCompletionSource<bool>? _captureCompletion;
    private int _cameraUpdateCount;
    private bool _disposed;

    public bool IsRunning => _surface?.IsReady == true;
    public string EngineVersion => "PowerPotion Vulkan P0" +
        (string.IsNullOrEmpty(_surface?.DeviceName) ? "" : " / " + _surface!.DeviceName);
    public IntPtr NativeWindowHandle => _surface?.ChildHwnd ?? IntPtr.Zero;
    public List<string> PreInitCommands { get; } = new();

    public event Action<string>? Log;
    public event Action? ClosedFromEngine;
    public event Action<bool>? CaptureFinished;
    public event Action<int>? WorldUpdateFinished;

    public void AttachSurface(IRenderSurface surface)
    {
        _surface = surface;
        surface.SetSettings(_settings.Clone());
        surface.SceneUploaded += revision =>
        {
            Log?.Invoke("World update finished: 0");
            WorldUpdateFinished?.Invoke(0);
        };
        Log?.Invoke("Vulkan surface attached.");
    }

    public void ApplyWorld(WorldSnapshot world, bool applyCamera = true)
    {
        _lastWorld = world;
        if (applyCamera) _camera = world.Camera;
        _surface?.ApplyWorld(world, applyCamera);
        Log?.Invoke($"P2 bridge: world queued ({world.Models.Count} models, {world.Materials.Count} materials, " +
                    $"{world.Lights.Count} lights); upload is revision-gated.");
    }

    public void OverwriteMaterial(MaterialDto material)
    {
        _surface?.OverwriteMaterial(material);
        Log?.Invoke($"P2 bridge: material '{material.Name}' updated.");
    }

    public void ApplySun(SunUpdate sun) => _surface?.ApplySun(sun);

    public void ApplyCamera(CameraUpdate camera)
    {
        _camera = camera;
        _surface?.ApplyCamera(camera);
        if (++_cameraUpdateCount % 30 == 0)
            Log?.Invoke($"camera stream: {_cameraUpdateCount} updates");
    }
    public CameraUpdate? ReadCamera() => _camera;

    private float[] _sectionPlanes = Array.Empty<float>();
    public void SetSectionPlanes(float[] planes)
    {
        _sectionPlanes = planes ?? Array.Empty<float>();
        _surface?.SetSectionPlanes(_sectionPlanes);
        Log?.Invoke($"P4 bridge: {_sectionPlanes.Length / 4} section plane(s).");
    }

    public void ExecuteConsoleCommand(string command, bool deferred)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var value))
        {
            if (parts[0] == "r_vsync")
            {
                _surface?.SetVsync(value != 0);
                Log?.Invoke($"P1 bridge: vsync={value != 0}");
                return;
            }
            if (parts[0] == "r_hdr")
            {
                _surface?.SetHdr(value != 0);
                Log?.Invoke($"P1 bridge: hdr={value != 0}");
                return;
            }
        }

        if (CvarAdapter.TryApply(command, _settings))
        {
            _surface?.SetSettings(_settings.Clone());
            Log?.Invoke($"P3 settings: {command}");
            return;
        }

        Log?.Invoke($"P3 bridge: unknown console command '{command}' (deferred={deferred}) ignored.");
    }

    public void WaitForIdle() { }
    public void WaitForFrames(ulong frames) => _ = frames;

    public Task<bool> CaptureDone =>
        (_captureCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void CaptureScreenshot(string filePath, bool panorama)
    {
        _captureCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CaptureAsync(filePath, panorama);
    }

    private async Task CaptureAsync(string filePath, bool panorama)
    {
        bool ok = false;
        try
        {
            if (_surface == null)
            {
                Log?.Invoke("P0 capture failed: no surface attached.");
            }
            else
            {
                if (panorama) Log?.Invoke("P0 capture: panorama requested; writing a normal frame for now.");
                ok = await _surface.CaptureAsync(filePath);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("P0 capture failed: " + ex.Message);
        }

        _captureCompletion?.TrySetResult(ok);
        CaptureFinished?.Invoke(ok);
    }

    public Task StopAsync()
    {
        _surface?.Stop();
        return Task.CompletedTask;
    }

    public void NotifyFocus(bool focused) => _ = focused;
    public void NotifyMinimize(bool minimized) => _ = minimized;
    public void FocusRenderWindow() => _surface?.FocusChild();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface?.Stop();
    }
}
