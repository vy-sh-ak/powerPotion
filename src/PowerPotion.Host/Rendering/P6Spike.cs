using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerPotion.Protocol;
using PowerPotion.Rendering.Settings;

namespace PowerPotion.Host.Rendering;

/// <summary>
/// P6 check: SSAO, height fog, raymarched clouds/wind, and frustum culling.
/// </summary>
public static class P6Spike
{
    public static int Run(string? shotPath)
    {
        var app = new Application();
        var window = new P0Window { Title = "PowerPotion — P6 Quality" };
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
                    Console.WriteLine("P6 FAILED: renderer did not become ready.");
                    if (window.Surface.Error != null) Console.WriteLine("P6 error: " + window.Surface.Error);
                    app.Shutdown(2);
                    return;
                }

                var surface = window.Surface;
                Console.WriteLine($"P6 renderer: {surface.DeviceName}");
                var checker = P2Spike.CreateCheckerTexture();
                var world = P2Spike.BuildWorld(checker);
                world.Revision = 600;
                surface.ApplyWorld(world);
                surface.ApplyCamera(world.Camera);

                var basePath = shotPath ?? Path.Combine(Path.GetTempPath(), "powerpotion_p6.png");
                var dir = Path.GetDirectoryName(basePath)!;
                string Variant(string suffix) =>
                    Path.Combine(dir, Path.GetFileNameWithoutExtension(basePath) + "_" + suffix + ".png");

                RendererSettings Base() => new()
                {
                    RtSunShadows = true, SunPower = 1, GiAmbientAmount = 1, LightBrightness = 1,
                    SsaoStrength = 0, SsgiStrength = 0, FogAmount = 0, CloudDensity = 0,
                    RenderScale = 1,
                };

                surface.SetSettings(Base());
                await Task.Delay(1000);
                bool capturedBase = await surface.CaptureAsync(basePath);
                var baseline = P2Spike.Inspect(basePath);
                int baselineDraws = surface.SceneDrawCalls;
                Console.WriteLine($"P6 base: captured={capturedBase} nonBg={baseline.NonBackground:P1} draws={baselineDraws}");

                // SSAO.
                var ssaoSettings = Base();
                ssaoSettings.SsaoStrength = 1.0;
                ssaoSettings.SsaoRadius = 20.0;
                surface.SetSettings(ssaoSettings);
                await Task.Delay(500);
                var ssaoPath = Variant("ssao");
                bool capturedSsao = await surface.CaptureAsync(ssaoPath);
                var ssao = P2Spike.Inspect(ssaoPath);
                bool ssaoChanged = ssao.Hash != baseline.Hash;
                double ssaoDiff = MeanDifference(basePath, ssaoPath);
                Console.WriteLine($"P6 SSAO: captured={capturedSsao} changed={ssaoChanged} " +
                                  $"diff={ssaoDiff:0.00} bright={ssao.Bright:P2}");

                // Screen-space GI (depth-weighted gather + denoise).
                var ssgiSettings = Base();
                ssgiSettings.SsgiStrength = 1.0;
                ssgiSettings.SsgiRadius = 0.03;
                ssgiSettings.SsgiFalloff = 4.0;
                surface.SetSettings(ssgiSettings);
                await Task.Delay(500);
                var ssgiPath = Variant("ssgi");
                bool capturedSsgi = await surface.CaptureAsync(ssgiPath);
                var ssgi = P2Spike.Inspect(ssgiPath);
                double ssgiDiff = MeanDifference(basePath, ssgiPath);
                bool ssgiChanged = ssgi.Hash != baseline.Hash && ssgiDiff > 0.2;
                Console.WriteLine($"P6 SSGI: captured={capturedSsgi} changed={ssgiChanged} " +
                                  $"diff={ssgiDiff:0.00} bright={ssgi.Bright:P2}");

                // Height fog.
                var fogSettings = Base();
                fogSettings.FogAmount = 0.9;
                fogSettings.FogHeight = 0;
                surface.SetSettings(fogSettings);
                await Task.Delay(500);
                var fogPath = Variant("fog");
                bool capturedFog = await surface.CaptureAsync(fogPath);
                var fog = P2Spike.Inspect(fogPath);
                bool fogChanged = fog.Hash != baseline.Hash;
                Console.WriteLine($"P6 fog: captured={capturedFog} changed={fogChanged} " +
                                  $"bright={fog.Bright:P2}");

                // Clouds + wind.
                var cloudSettings = Base();
                cloudSettings.CloudDensity = 0.9;
                cloudSettings.CloudCirrus = 0.4;
                cloudSettings.CloudVariety = 0.8;
                cloudSettings.WindStrength = 2;
                cloudSettings.WindDirection = 45;
                surface.SetSettings(cloudSettings);
                await Task.Delay(600);
                var cloudPath = Variant("clouds");
                bool capturedCloud = await surface.CaptureAsync(cloudPath);
                var clouds = P2Spike.Inspect(cloudPath);
                bool cloudChanged = clouds.Hash != baseline.Hash;
                Console.WriteLine($"P6 clouds: captured={capturedCloud} changed={cloudChanged} " +
                                  $"bright={clouds.Bright:P2}");

                // Frustum culling: look away from the scene.
                var away = CloneCamera(world.Camera);
                var q = P2Spike.CameraQuaternion(new[] { 100f, 100f, 100f }, new[] { 0f, 1f, 0f });
                away.PosX = 100; away.PosY = 100; away.PosZ = 100;
                away.QuatX = q[0]; away.QuatY = q[1]; away.QuatZ = q[2]; away.QuatW = q[3];
                surface.ApplyCamera(away);
                await Task.Delay(600);
                int culledDraws = surface.SceneDrawCalls;
                bool culled = culledDraws < baselineDraws;
                Console.WriteLine($"P6 culling: baselineDraws={baselineDraws} awayDraws={culledDraws} culled={culled}");

                surface.ApplyCamera(world.Camera);
                await Task.Delay(300);

                // Dynamic resolution: render at 50% and upscale to the swapchain.
                var scaleSettings = Base();
                scaleSettings.RenderScale = 0.5;
                surface.SetSettings(scaleSettings);
                await Task.Delay(1200);
                var scalePath = Variant("renderScale");
                bool capturedScale = await surface.CaptureAsync(scalePath);
                var scaled = P2Spike.Inspect(scalePath);
                bool scaleOk = capturedScale && scaled.Width == baseline.Width
                               && surface.RenderWidth > 0
                               && surface.RenderWidth < surface.SurfaceSize.Width * 0.75;
                Console.WriteLine($"P6 render scale: captured={capturedScale} " +
                                  $"render={surface.RenderWidth}x{surface.RenderHeight} " +
                                  $"capture={scaled.Width}x{scaled.Height} ok={scaleOk}");

                // CVar coverage for the P6 controls.
                var cvars = new RendererSettings();
                bool cvarOk = CvarAdapter.TryApply("r_ssao 0.8", cvars) && Math.Abs(cvars.SsaoStrength - 0.8) < 1e-6
                              && CvarAdapter.TryApply("r_ssaoRadius 2.5", cvars) && Math.Abs(cvars.SsaoRadius - 2.5) < 1e-6
                              && CvarAdapter.TryApply("r_ssgi 0.8", cvars) && Math.Abs(cvars.SsgiStrength - 0.8) < 1e-6
                              && CvarAdapter.TryApply("r_ssgiRadius 0.04", cvars) && Math.Abs(cvars.SsgiRadius - 0.04) < 1e-6
                              && CvarAdapter.TryApply("r_renderScale 0.75", cvars) && Math.Abs(cvars.RenderScale - 0.75) < 1e-6
                              && CvarAdapter.TryApply("g_windStrength 2", cvars) && Math.Abs(cvars.WindStrength - 2) < 1e-6
                              && CvarAdapter.TryApply("g_windDirection 45", cvars) && Math.Abs(cvars.WindDirection - 45) < 1e-6;
                Console.WriteLine($"P6 CVars: ok={cvarOk}");

                Console.WriteLine($"P6 validation: errors={surface.ValidationErrorCount} " +
                                  $"warnings={surface.ValidationWarningCount}");

                bool ok = capturedBase && baseline.NonBackground > 0.05
                          && capturedSsao && ssaoChanged && ssaoDiff > 0.2
                          && capturedSsgi && ssgiChanged
                          && capturedFog && fogChanged
                          && capturedCloud && cloudChanged
                          && culled && scaleOk && cvarOk
                          && surface.ValidationErrorCount == 0;

                if (!ok)
                {
                    Console.WriteLine("P6 FAILED: quality/culling thresholds not met.");
                    app.Shutdown(5);
                    return;
                }

                surface.Stop();
                exitCode = 0;
                Console.WriteLine("P6 PASSED: SSAO, SSGI, height fog, clouds/wind, culling and render scale verified.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("P6 FAILED: " + ex);
                app.Shutdown(6);
            }
        };

        app.Run(window);
        return exitCode;
    }

    private static float MeanLuma(byte[] pixels, int index) =>
        (0.2126f * pixels[index + 2] + 0.7152f * pixels[index + 1] + 0.0722f * pixels[index]) / 255f;

    private static (int Width, int Height, byte[] Pixels) LoadBgra(string path)
    {
        var frame = BitmapFrame.Create(
            new Uri(Path.GetFullPath(path)),
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return (width, height, pixels);
    }

    private static double MeanDifference(string aPath, string bPath)
    {
        var a = LoadBgra(aPath);
        var b = LoadBgra(bPath);
        int width = Math.Min(a.Width, b.Width);
        int height = Math.Min(a.Height, b.Height);
        double sum = 0;
        int count = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * a.Width + x) * 4;
                int j = (y * b.Width + x) * 4;
                sum += Math.Abs(MeanLuma(a.Pixels, i) - MeanLuma(b.Pixels, j));
                count++;
            }
        }
        return count == 0 ? 0 : sum / count * 255.0;
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
}
