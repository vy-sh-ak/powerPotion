using PowerPotion.Protocol;
using PowerPotion.Rendering.Settings;

namespace PowerPotion.Rendering;

/// <summary>
/// Adapter between the host seam and the standalone Vulkan renderer. The render surface owns the
/// Vulkan device/swapchain; this class forwards worlds/materials/camera and maps the CVar surface
/// into <see cref="Settings.RendererSettings"/>.
/// </summary>
public sealed class RendererBridge : IRendererBridge
{
    private IRenderSurface? _surface;
    private CameraUpdate? _camera;
    private readonly RendererSettings _settings = new();
    private int _cameraUpdateCount;
    private bool _disposed;

    public string EngineVersion => "PowerPotion Vulkan P0" +
        (string.IsNullOrEmpty(_surface?.DeviceName) ? "" : " / " + _surface!.DeviceName);
    public IntPtr NativeWindowHandle => _surface?.ChildHwnd ?? IntPtr.Zero;
    public List<string> PreInitCommands { get; } = new();

    public event Action<string>? Log;
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

    public void CaptureScreenshot(string filePath, bool panorama, int width = 0, int height = 0)
    {
        _ = CaptureAsync(filePath, panorama, width, height);
    }

    private async Task CaptureAsync(string filePath, bool panorama, int width, int height)
    {
        bool ok = false;
        try
        {
            if (_surface == null)
            {
                Log?.Invoke("capture failed: no surface attached.");
            }
            else
            {
                ok = panorama
                    ? await _surface.CapturePanoramaAsync(filePath, width, height)
                    : await _surface.CaptureAsync(filePath, width, height);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("capture failed: " + ex.Message);
        }

        CaptureFinished?.Invoke(ok);
    }

    public void NotifyFocus(bool focused) => _ = focused;
    public void FocusRenderWindow() => _surface?.FocusChild();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface?.Stop();
    }
}
