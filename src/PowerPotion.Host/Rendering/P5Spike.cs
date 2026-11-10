using System.Diagnostics;
using System.IO;
using System.Windows;
using PowerPotion.Protocol;
using PowerPotion.Rendering.Settings;

namespace PowerPotion.Host.Rendering;

/// <summary>
/// P5 check: post-processing, capture formats/resolution, panorama, safe frame and CVar coverage.
/// </summary>
public static class P5Spike
{
    public static int Run(string? shotPath)
    {
        var app = new Application();
        var window = new P0Window { Title = "PowerPotion — P5 Post / Capture" };
        var exitCode = 1;

        window.Loaded += async (_, _) =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                while (!window.Surface.IsReady && clock.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(50);

                if (!window.Surface.IsReady)
                {
                    Console.WriteLine("P5 FAILED: renderer did not become ready.");
                    if (window.Surface.Error != null) Console.WriteLine("P5 error: " + window.Surface.Error);
                    app.Shutdown(2);
                    return;
                }

                var surface = window.Surface;
                Console.WriteLine($"P5 renderer: {surface.DeviceName}");
                var checker = P2Spike.CreateCheckerTexture();
                var world = P2Spike.BuildWorld(checker);
                world.Revision = 500;
                surface.ApplyWorld(world);
                surface.ApplyCamera(world.Camera);

                var basePath = shotPath ?? Path.Combine(Path.GetTempPath(), "powerpotion_p5.png");
                var dir = Path.GetDirectoryName(basePath)!;
                string Variant(string suffix) =>
                    Path.Combine(dir, Path.GetFileNameWithoutExtension(basePath) + "_" + suffix + ".png");

                // Baseline.
                surface.SetSettings(new RendererSettings
                {
                    RtSunShadows = true, SunPower = 1, GiAmbientAmount = 1, LightBrightness = 1,
                });
                await Task.Delay(1000);
                bool capturedBase = await surface.CaptureAsync(basePath);
                var baseline = P2Spike.Inspect(basePath);
                Console.WriteLine($"P5 base: captured={capturedBase} nonBg={baseline.NonBackground:P1}");

                // Manual exposure.
                var manualCamera = CloneCamera(world.Camera);
                manualCamera.AutoExposure = false;
                manualCamera.Exposure = 1.5f;
                surface.ApplyCamera(manualCamera);
                await Task.Delay(500);
                var exposurePath = Variant("exposure");
                bool capturedExposure = await surface.CaptureAsync(exposurePath);
                var exposure = P2Spike.Inspect(exposurePath);
                bool exposureChanged = exposure.Hash != baseline.Hash;
                Console.WriteLine($"P5 exposure: captured={capturedExposure} changed={exposureChanged} " +
                                  $"bright={exposure.Bright:P2}");

                // Bloom + saturation + temperature + vignette + CA + motion blur + flare + safe frame.
                surface.SetSettings(new RendererSettings
                {
                    RtSunShadows = true, SunPower = 1, GiAmbientAmount = 1, LightBrightness = 1,
                    BloomIntensity = 1.2, Saturation = 1.8, ColorTemperature = 2200,
                    Vignette = 0.8, ChromaticAberration = 1.0, MotionBlur = 0.8,
                    LensFlare = 1.0, AutoContrast = true, ShowSafeFrame = true,
                });
                await Task.Delay(700);
                var postPath = Variant("post");
                bool capturedPost = await surface.CaptureAsync(postPath);
                var post = P2Spike.Inspect(postPath);
                bool postChanged = post.Hash != baseline.Hash;
                Console.WriteLine($"P5 post: captured={capturedPost} changed={postChanged} " +
                                  $"bright={post.Bright:P2} light={post.Light:P2}");

                // Safe-frame border: top-left corner should be bright when enabled.
                bool safeFrame = SafeFrameBorder(postPath);
                Console.WriteLine($"P5 safe frame: border={safeFrame}");

                // Explicit resolution.
                var resPath = Variant("800x450");
                bool capturedRes = await surface.CaptureAsync(resPath, 800, 450);
                var res = P2Spike.Inspect(resPath);
                bool resolutionOk = capturedRes && res.Width == 800 && res.Height == 450;
                Console.WriteLine($"P5 resolution: captured={capturedRes} size={res.Width}x{res.Height} ok={resolutionOk}");

                // JPG + EXR.
                var jpgPath = Path.ChangeExtension(basePath, ".jpg");
                var exrPath = Path.ChangeExtension(basePath, ".exr");
                bool capturedJpg = await surface.CaptureAsync(jpgPath);
                bool capturedExr = await surface.CaptureAsync(exrPath);
                bool jpgOk = capturedJpg && File.Exists(jpgPath) && new FileInfo(jpgPath).Length > 1000;
                bool exrOk = capturedExr && File.Exists(exrPath) &&
                             new FileInfo(exrPath).Length > (long)baseline.Width * baseline.Height * 8;
                Console.WriteLine($"P5 formats: jpg={jpgOk} exr={exrOk} " +
                                  $"exrBytes={(File.Exists(exrPath) ? new FileInfo(exrPath).Length : 0)}");

                // Panorama.
                var panoPath = Variant("pano");
                bool capturedPano = await surface.CapturePanoramaAsync(panoPath, 1024, 512);
                var pano = P2Spike.Inspect(panoPath);
                bool panoOk = capturedPano && pano.Width == 1024 && pano.Height == 512
                              && pano.NonBackground > 0.1;
                Console.WriteLine($"P5 panorama: captured={capturedPano} size={pano.Width}x{pano.Height} " +
                                  $"nonBg={pano.NonBackground:P1} ok={panoOk}");

                // CVar coverage: every command emitted by the host settings must be recognised.
                var cvarSettings = new RendererSettings();
                var hostSettings = new HostSettings();
                var unknown = new List<string>();
                foreach (var (command, _) in hostSettings.ToCommands())
                    if (!CvarAdapter.TryApply(command, cvarSettings)) unknown.Add(command);
                foreach (var command in hostSettings.ToPreInitCommands())
                    if (!CvarAdapter.TryApply(command, cvarSettings)) unknown.Add(command);
                bool cvarOk = unknown.Count == 0;
                Console.WriteLine($"P5 CVars: ok={cvarOk} unknown={unknown.Count}" +
                                  (unknown.Count == 0 ? "" : " (" + string.Join(", ", unknown) + ")"));

                Console.WriteLine($"P5 validation: errors={surface.ValidationErrorCount} " +
                                  $"warnings={surface.ValidationWarningCount}");

                bool ok = capturedBase && baseline.NonBackground > 0.05
                          && capturedExposure && exposureChanged
                          && capturedPost && postChanged && safeFrame
                          && resolutionOk && jpgOk && exrOk && panoOk && cvarOk
                          && surface.ValidationErrorCount == 0;

                if (!ok)
                {
                    Console.WriteLine("P5 FAILED: post/capture/panorama/CVar thresholds not met.");
                    app.Shutdown(5);
                    return;
                }

                surface.Stop();
                exitCode = 0;
                Console.WriteLine("P5 PASSED: post, formats, resolution, panorama and CVar coverage verified.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("P5 FAILED: " + ex);
                app.Shutdown(6);
            }
        };

        app.Run(window);
        return exitCode;
    }

    private static CameraUpdate CloneCamera(CameraUpdate c) => new()
    {
        PosX = c.PosX, PosY = c.PosY, PosZ = c.PosZ,
        QuatX = c.QuatX, QuatY = c.QuatY, QuatZ = c.QuatZ, QuatW = c.QuatW,
        FovDegrees = c.FovDegrees,
        FovType = c.FovType,
        Ortho = c.Ortho,
        OrthoLeft = c.OrthoLeft,
        OrthoRight = c.OrthoRight,
        OrthoBottom = c.OrthoBottom,
        OrthoTop = c.OrthoTop,
        AutoExposure = c.AutoExposure,
        Exposure = c.Exposure,
        TwoPointPerspective = c.TwoPointPerspective,
    };

    private static bool SafeFrameBorder(string path)
    {
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri(Path.GetFullPath(path)),
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        int x = (int)(width * 0.02);
        int y = (int)(height * 0.5);
        int i = (y * width + x) * 4;
        return pixels[i] > 180 && pixels[i + 1] > 180 && pixels[i + 2] > 180;
    }
}
