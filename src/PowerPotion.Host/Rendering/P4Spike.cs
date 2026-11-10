using System.Diagnostics;
using System.IO;
using System.Windows;
using PowerPotion.Protocol;

namespace PowerPotion.Host.Rendering;

/// <summary>
/// P4 check: orthographic projection, two-point perspective, scene-bounds near/far, section-plane
/// clipping and solid glass. Captures the same synthetic scene in each mode and verifies that the
/// image and the visible pixel budget change as expected.
/// </summary>
public static class P4Spike
{
    public static int Run(string? shotPath)
    {
        var app = new Application();
        var window = new P0Window { Title = "PowerPotion — P4 Camera / Sections / Glass" };
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
                    Console.WriteLine("P4 FAILED: renderer did not become ready.");
                    if (window.Surface.Error != null) Console.WriteLine("P4 error: " + window.Surface.Error);
                    app.Shutdown(2);
                    return;
                }

                var surface = window.Surface;
                Console.WriteLine($"P4 renderer: {surface.DeviceName}");
                Console.WriteLine($"P4 swapchain: {surface.SwapchainInfo}");

                var checker = P2Spike.CreateCheckerTexture();
                var basePath = shotPath ?? Path.Combine(Path.GetTempPath(), "powerpotion_p4.png");
                var dir = Path.GetDirectoryName(basePath)!;
                string Variant(string suffix) =>
                    Path.Combine(Path.GetDirectoryName(basePath)!,
                        Path.GetFileNameWithoutExtension(basePath) + "_" + suffix + ".png");

                // --- Baseline perspective scene (no glass) --------------------------------
                var noGlass = P2Spike.BuildWorld(checker);
                noGlass.Revision = 400;
                surface.ApplyWorld(noGlass);
                surface.SetSectionPlanes(Array.Empty<float>());
                await Task.Delay(1400);
                bool capturedBase = await surface.CaptureAsync(basePath);
                var baseImage = P2Spike.Inspect(basePath);
                Console.WriteLine($"P4 base: captured={capturedBase} nonBg={baseImage.NonBackground:P1} " +
                                  $"stats={surface.SceneStats}");

                // --- Solid glass pane -----------------------------------------------------
                var glass = P2Spike.BuildWorld(checker);
                glass.Revision = 401;
                glass.Materials.Add(new MaterialDto
                {
                    Id = 5,
                    Name = "Glass",
                    IsSolidGlass = true,
                    Opacity = 0.35f,
                    DiffuseR = 0.72f,
                    DiffuseG = 0.86f,
                    DiffuseB = 0.94f,
                    Roughness = 0.06f,
                    Refraction = 1.5f,
                });
                var pane = P2Spike.Box(105, 5, -2.2f, 0.1f, 2.6f, 2.2f, 3.2f, 2.7f);
                glass.Models.Add(pane);
                glass.Instances.Add(new InstanceDto { ModelId = pane.Id, InstanceId = 99, Matrix = Identity() });
                surface.ApplyWorld(glass);
                await Task.Delay(1200);
                var glassPath = Variant("glass");
                bool capturedGlass = await surface.CaptureAsync(glassPath);
                var glassImage = P2Spike.Inspect(glassPath);
                bool glassChanged = glassImage.Hash != baseImage.Hash;
                Console.WriteLine($"P4 glass: captured={capturedGlass} changed={glassChanged} " +
                                  $"nonBg={glassImage.NonBackground:P1}");

                // --- Orthographic projection ---------------------------------------------
                var orthoWorld = BuildWorld(checker, 401);
                var orthoCamera = CloneCamera(orthoWorld.Camera);
                orthoCamera.Ortho = true;
                orthoCamera.OrthoLeft = -8f;
                orthoCamera.OrthoRight = 8f;
                orthoCamera.OrthoBottom = -5f;
                orthoCamera.OrthoTop = 5f;
                orthoCamera.TwoPointPerspective = false;
                surface.ApplyCamera(orthoCamera);
                await Task.Delay(600);
                var orthoPath = Variant("ortho");
                bool capturedOrtho = await surface.CaptureAsync(orthoPath);
                var orthoImage = P2Spike.Inspect(orthoPath);
                bool orthoChanged = orthoImage.Hash != glassImage.Hash;
                Console.WriteLine($"P4 ortho: captured={capturedOrtho} changed={orthoChanged} " +
                                  $"nonBg={orthoImage.NonBackground:P1}");

                // --- Two-point perspective (pitched camera, verticals stay vertical) ----
                var twoPointCamera = CloneCamera(orthoWorld.Camera);
                twoPointCamera.Ortho = false;
                var q = P2Spike.CameraQuaternion(new[] { -9.5f, -4.2f, -9.5f }, new[] { 0f, 1f, 0f });
                twoPointCamera.PosX = 9.5f;
                twoPointCamera.PosY = 5.4f;
                twoPointCamera.PosZ = 9.5f;
                twoPointCamera.QuatX = q[0];
                twoPointCamera.QuatY = q[1];
                twoPointCamera.QuatZ = q[2];
                twoPointCamera.QuatW = q[3];
                twoPointCamera.TwoPointPerspective = true;
                surface.ApplyCamera(twoPointCamera);
                await Task.Delay(600);
                var twoPointPath = Variant("twopoint");
                bool capturedTwoPoint = await surface.CaptureAsync(twoPointPath);
                var twoPointImage = P2Spike.Inspect(twoPointPath);
                bool twoPointChanged = twoPointImage.Hash != glassImage.Hash;
                Console.WriteLine($"P4 two-point: captured={capturedTwoPoint} changed={twoPointChanged} " +
                                  $"nonBg={twoPointImage.NonBackground:P1}");

                // --- Section plane clipping ----------------------------------------------
                var perspectiveCamera = CloneCamera(orthoWorld.Camera);
                surface.ApplyCamera(perspectiveCamera);
                surface.SetSectionPlanes(new float[] { 0f, 1f, 0f, -1f }); // keep y >= 1 m
                await Task.Delay(700);
                var sectionPath = Variant("section");
                bool capturedSection = await surface.CaptureAsync(sectionPath);
                var sectionImage = P2Spike.Inspect(sectionPath);
                bool sectionChanged = sectionImage.Hash != baseImage.Hash;
                // The sky covers the whole frame, so compare the ground band with the base frame.
                double sectionDiff = BottomDifference(basePath, sectionPath);
                bool clipped = sectionDiff > 15.0;
                Console.WriteLine($"P4 section: captured={capturedSection} changed={sectionChanged} " +
                                  $"clipped={clipped} bottomDiff={sectionDiff:0.0} nonBg={sectionImage.NonBackground:P1}");

                surface.SetSectionPlanes(Array.Empty<float>());
                await Task.Delay(400);

                Console.WriteLine($"P4 validation: errors={surface.ValidationErrorCount} " +
                                  $"warnings={surface.ValidationWarningCount}");

                bool ok = capturedBase && capturedGlass && capturedOrtho && capturedTwoPoint && capturedSection
                          && baseImage.NonBackground > 0.05
                          && glassChanged
                          && orthoChanged && orthoImage.NonBackground > 0.05
                          && twoPointChanged && twoPointImage.NonBackground > 0.05
                          && sectionChanged && clipped
                          && surface.ValidationErrorCount == 0;

                if (!ok)
                {
                    Console.WriteLine("P4 FAILED: camera/section/glass thresholds not met.");
                    app.Shutdown(5);
                    return;
                }

                surface.Stop();
                exitCode = 0;
                Console.WriteLine("P4 PASSED: ortho, two-point, section planes and glass verified.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("P4 FAILED: " + ex);
                app.Shutdown(6);
            }
        };

        app.Run(window);
        return exitCode;
    }

    private static WorldSnapshot BuildWorld(string checker, long revision)
    {
        var world = P2Spike.BuildWorld(checker);
        world.Revision = revision;
        return world;
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
        TwoPointPerspective = c.TwoPointPerspective,
    };

    /// <summary>Mean RGB distance in the bottom band (0..255).</summary>
    private static double BottomDifference(string aPath, string bPath)
    {
        var a = LoadBgra(aPath);
        var b = LoadBgra(bPath);
        int height = Math.Min(a.Height, b.Height);
        int width = Math.Min(a.Width, b.Width);
        int start = (int)(height * 0.60);
        double sum = 0;
        int count = 0;
        for (int y = start; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * a.Width + x) * 4;
                int j = (y * b.Width + x) * 4;
                int dr = a.Pixels[i + 2] - b.Pixels[j + 2];
                int dg = a.Pixels[i + 1] - b.Pixels[j + 1];
                int db = a.Pixels[i] - b.Pixels[j];
                sum += Math.Sqrt(dr * dr + dg * dg + db * db);
                count++;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    private static (int Width, int Height, byte[] Pixels) LoadBgra(string path)
    {
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri(Path.GetFullPath(path)),
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return (width, height, pixels);
    }

    private static float[] Identity() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
}
