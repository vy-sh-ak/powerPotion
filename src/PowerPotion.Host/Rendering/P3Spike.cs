using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerPotion.Protocol;
using PowerPotion.Rendering.Settings;

namespace PowerPotion.Host.Rendering;

/// <summary>
/// P3 check: date/location sun, sky, sun shadows, a point light and an IES spot light.
/// Captures the same scene with shadows on/off and lights on/off to prove the contributions.
/// </summary>
public static class P3Spike
{
    public static int Run(string? shotPath)
    {
        var app = new Application();
        var window = new P0Window { Title = "PowerPotion — P3 Sun / Sky / Lights" };
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
                    Console.WriteLine("P3 FAILED: renderer did not become ready.");
                    if (window.Surface.Error != null) Console.WriteLine("P3 error: " + window.Surface.Error);
                    app.Shutdown(2);
                    return;
                }

                var surface = window.Surface;
                Console.WriteLine($"P3 renderer: {surface.DeviceName}");

                var checker = P2Spike.CreateCheckerTexture();
                var iesPath = CreateIesFile();
                var world = P2Spike.BuildWorld(checker);
                world.Revision = 300;
                world.Sun = new SunUpdate
                {
                    Mode = 0,
                    Latitude = 48.14,
                    Longitude = 11.58,
                    NorthAngle = 0,
                    TimeZoneHours = 1,
                    Year = 2026,
                    Month = 6,
                    Day = 21,
                    Hour = 14f,
                };
                world.Lights.Add(new LightDto
                {
                    Id = 1,
                    X = 0f, Y = 2.5f, Z = 0f,
                    R = 1f, G = 0.86f, B = 0.70f, A = 1f,
                    Intensity = 260f,
                    Distribution = 1,
                    Shape = 1,
                });
                world.Lights.Add(new LightDto
                {
                    Id = 2,
                    X = 4f, Y = 4f, Z = 4f,
                    R = 0.85f, G = 0.92f, B = 1f, A = 1f,
                    Intensity = 420f,
                    Distribution = 3,
                    Shape = 1,
                    BeamAngle = 35f * MathF.PI / 180f,
                    Rotation = DirectionRows(-4f, -4f, -4f),
                    IesPath = iesPath,
                });

                surface.ApplyWorld(world);
                surface.ApplyCamera(world.Camera);
                await Task.Delay(1600);
                Console.WriteLine($"P3 sun: elevation={surface.SunElevationDegrees:0.#}deg, " +
                                  $"azimuth={surface.SunAzimuthDegrees:0.#}deg, intensity={surface.SunIntensity:0.##}");

                var basePath = shotPath ?? Path.Combine(Path.GetTempPath(), "powerpotion_p3_sun.png");
                bool captured = await surface.CaptureAsync(basePath);
                var withShadows = P2Spike.Inspect(basePath);
                double groundWithShadows = BottomAverage(basePath);
                double darkWithShadows = BottomDark(basePath);
                Console.WriteLine($"P3 capture: {(captured ? "ok" : "FAILED")} {basePath}");
                int lightCount = surface.SceneLightCount;
                int textureCount = surface.SceneTextureCount;
                Console.WriteLine($"P3 stats: {surface.SceneStats}");
                Console.WriteLine($"P3 lights={lightCount}, textures={textureCount}, " +
                                  $"drawCalls={surface.SceneDrawCalls}, groundLuma={groundWithShadows:0.0}");
                Console.WriteLine($"P3 pixels: nonBg={withShadows.NonBackground:P1}, bright={withShadows.Bright:P2}");

                // HDR skybox: load a small equirect Radiance file through the settings path.
                var hdrPath = CreateSkyboxHdr();
                surface.SetSettings(new RendererSettings
                {
                    RtSunShadows = true,
                    SunPower = 1,
                    SkyboxFromFile = true,
                    SkyboxFile = hdrPath,
                    SkyboxRotationDegrees = 45,
                    SkyboxBrightness = 2,
                    SkyboxUseSunSpot = true,
                    GiAmbientAmount = 1,
                    LightBrightness = 1,
                });
                await Task.Delay(700);
                var skyboxPath = Path.Combine(
                    Path.GetDirectoryName(basePath)!, Path.GetFileNameWithoutExtension(basePath) + "_skybox.png");
                bool capturedSkybox = await surface.CaptureAsync(skyboxPath);
                var skybox = P2Spike.Inspect(skyboxPath);
                bool skyboxChanged = skybox.Hash != withShadows.Hash;
                Console.WriteLine($"P3 skybox: captured={capturedSkybox}, changed={skyboxChanged}, " +
                                  $"bright={skybox.Bright:P2}");

                var exrPath = CreateSkyboxExr();
                surface.SetSettings(new RendererSettings
                {
                    RtSunShadows = true,
                    SunPower = 1,
                    SkyboxFromFile = true,
                    SkyboxFile = exrPath,
                    SkyboxRotationDegrees = -30,
                    SkyboxBrightness = 1.5,
                    SkyboxUseSunSpot = true,
                    GiAmbientAmount = 1,
                    LightBrightness = 1,
                });
                await Task.Delay(700);
                var exrShot = Path.Combine(
                    Path.GetDirectoryName(basePath)!, Path.GetFileNameWithoutExtension(basePath) + "_skybox_exr.png");
                bool capturedExr = await surface.CaptureAsync(exrShot);
                var exrImage = P2Spike.Inspect(exrShot);
                bool exrChanged = exrImage.Hash != withShadows.Hash && exrImage.Hash != skybox.Hash;
                Console.WriteLine($"P3 EXR skybox: captured={capturedExr}, changed={exrChanged}, bright={exrImage.Bright:P2}");
                surface.SetSettings(new RendererSettings { RtSunShadows = true, SunPower = 1 });

                // Shadows off: the same scene should get brighter on the ground.
                var noShadowSettings = new RendererSettings
                {
                    RtSunShadows = false,
                    SunPower = 1,
                    GiAmbientAmount = 1,
                    LightBrightness = 1,
                    SkyboxFromFile = false,
                };
                surface.SetSettings(noShadowSettings);
                await Task.Delay(600);
                var noShadowPath = Path.Combine(
                    Path.GetDirectoryName(basePath)!, Path.GetFileNameWithoutExtension(basePath) + "_noshadow.png");
                bool capturedNoShadow = await surface.CaptureAsync(noShadowPath);
                var noShadows = P2Spike.Inspect(noShadowPath);
                double groundNoShadows = BottomAverage(noShadowPath);
                double darkNoShadows = BottomDark(noShadowPath);
                Console.WriteLine($"P3 shadows: onGround={groundWithShadows:0.0}, offGround={groundNoShadows:0.0}, " +
                                  $"dark on/off={darkWithShadows:P2}/{darkNoShadows:P2}, " +
                                  $"bright on/off={withShadows.Bright:P2}/{noShadows.Bright:P2}");

                surface.SetSettings(new RendererSettings { RtSunShadows = true, SunPower = 1 });

                // Lights off: same scene, same sun, no artificial lights.
                var noLightsWorld = P2Spike.BuildWorld(checker);
                noLightsWorld.Revision = 301;
                noLightsWorld.Sun = world.Sun;
                noLightsWorld.Camera = world.Camera;
                surface.ApplyWorld(noLightsWorld);
                await Task.Delay(1000);
                var noLightsPath = Path.Combine(
                    Path.GetDirectoryName(basePath)!, Path.GetFileNameWithoutExtension(basePath) + "_nolights.png");
                bool capturedNoLights = await surface.CaptureAsync(noLightsPath);
                var noLights = P2Spike.Inspect(noLightsPath);
                Console.WriteLine($"P3 lights off: bright={noLights.Bright:P2}, changed={withShadows.Hash != noLights.Hash}");

                // Same lights, but the spot's IES profile removed: the beam should change.
                var noIesWorld = P2Spike.BuildWorld(checker);
                noIesWorld.Revision = 303;
                noIesWorld.Sun = world.Sun;
                noIesWorld.Camera = world.Camera;
                noIesWorld.Lights.Add(new LightDto
                {
                    Id = 1, X = 0f, Y = 2.5f, Z = 0f,
                    R = 1f, G = 0.86f, B = 0.70f, A = 1f,
                    Intensity = 260f, Distribution = 1, Shape = 1,
                });
                noIesWorld.Lights.Add(new LightDto
                {
                    Id = 2, X = 4f, Y = 4f, Z = 4f,
                    R = 0.85f, G = 0.92f, B = 1f, A = 1f,
                    Intensity = 420f, Distribution = 3, Shape = 1,
                    BeamAngle = 35f * MathF.PI / 180f,
                    Rotation = DirectionRows(-4f, -4f, -4f),
                    IesPath = "",
                });
                surface.ApplyWorld(noIesWorld);
                await Task.Delay(1000);
                var noIesPath = Path.Combine(
                    Path.GetDirectoryName(basePath)!, Path.GetFileNameWithoutExtension(basePath) + "_noies.png");
                bool capturedNoIes = await surface.CaptureAsync(noIesPath);
                var noIes = P2Spike.Inspect(noIesPath);
                bool iesChanged = noIes.Hash != withShadows.Hash;
                Console.WriteLine($"P3 IES: captured={capturedNoIes}, changed={iesChanged}, bright={noIes.Bright:P2}");

                bool shadowsWork = darkWithShadows > darkNoShadows + 0.002 ||
                                   groundWithShadows < groundNoShadows - 0.5;
                bool lightsWork = withShadows.Bright > noLights.Bright + 0.002 ||
                                  withShadows.Light > noLights.Light + 0.002;
                bool iesLoaded = textureCount >= 3 && lightCount >= 2;
                bool ok = captured && capturedNoShadow && capturedNoLights && capturedSkybox && capturedExr
                          && capturedNoIes && shadowsWork && lightsWork && iesLoaded && iesChanged
                          && skyboxChanged && exrChanged
                          && withShadows.NonBackground > 0.05;

                Console.WriteLine($"P3 checks: shadows={shadowsWork}, lights={lightsWork}, ies={iesLoaded}");
                if (!ok)
                {
                    Console.WriteLine("P3 FAILED: sun/sky/shadow/light thresholds not met.");
                    app.Shutdown(5);
                    return;
                }

                surface.Stop();
                exitCode = 0;
                Console.WriteLine("P3 PASSED: sun position, sky, shadows, artificial lights and IES verified.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("P3 FAILED: " + ex);
                app.Shutdown(6);
            }
        };

        app.Run(window);
        return exitCode;
    }

    private static float[] DirectionRows(float x, float y, float z)
    {
        float length = MathF.Sqrt(x * x + y * y + z * z);
        x /= length; y /= length; z /= length;
        return new float[] { 1, 0, 0, 0, 1, 0, x, y, z };
    }

    private static string CreateSkyboxExr()
    {
        var path = Path.Combine(Path.GetTempPath(), "powerpotion_skybox.exr");
        const int width = 64;
        const int height = 32;
        var bytes = new List<byte>();

        void CString(string value)
        {
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(value));
            bytes.Add(0);
        }

        void Attribute(string name, string type, byte[] data)
        {
            CString(name);
            CString(type);
            bytes.AddRange(BitConverter.GetBytes(data.Length));
            bytes.AddRange(data);
        }

        bytes.AddRange(BitConverter.GetBytes(0x01312f76u));
        bytes.AddRange(BitConverter.GetBytes(2)); // version 2, scanline

        var channels = new List<byte>();
        foreach (var name in new[] { "B", "G", "R" })
        {
            channels.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
            channels.Add(0);
            channels.AddRange(BitConverter.GetBytes(2)); // FLOAT
            channels.Add(0); // pLinear
            channels.AddRange(new byte[3]);
            channels.AddRange(BitConverter.GetBytes(1)); // xSampling
            channels.AddRange(BitConverter.GetBytes(1)); // ySampling
        }
        channels.Add(0);
        Attribute("channels", "chlist", channels.ToArray());
        Attribute("compression", "compression", new byte[] { 0 }); // uncompressed

        var box = new List<byte>();
        box.AddRange(BitConverter.GetBytes(0));
        box.AddRange(BitConverter.GetBytes(0));
        box.AddRange(BitConverter.GetBytes(width - 1));
        box.AddRange(BitConverter.GetBytes(height - 1));
        Attribute("dataWindow", "box2i", box.ToArray());
        Attribute("displayWindow", "box2i", box.ToArray());
        Attribute("lineOrder", "lineOrder", new byte[] { 0 });
        Attribute("pixelAspectRatio", "float", BitConverter.GetBytes(1f));
        Attribute("screenWindowCenter", "v2f", new byte[8]);
        Attribute("screenWindowWidth", "float", BitConverter.GetBytes(1f));
        bytes.Add(0); // end of header

        long headerEnd = bytes.Count;
        int blockSize = 8 + width * 3 * 4;
        for (int y = 0; y < height; y++)
            bytes.AddRange(BitConverter.GetBytes(headerEnd + height * 8 + y * blockSize));

        for (int y = 0; y < height; y++)
        {
            float t = y / (float)(height - 1);
            float r = 0.12f + 0.80f * t;
            float g = 0.30f + 0.20f * t;
            float b = 1.00f - 0.80f * t;
            bytes.AddRange(BitConverter.GetBytes(y));
            bytes.AddRange(BitConverter.GetBytes(width * 3 * 4));
            for (int c = 0; c < 3; c++)
            {
                float value = c == 0 ? b : c == 1 ? g : r;
                for (int x = 0; x < width; x++)
                    bytes.AddRange(BitConverter.GetBytes(value));
            }
        }

        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private static string CreateSkyboxHdr()
    {
        var path = Path.Combine(Path.GetTempPath(), "powerpotion_skybox.hdr");
        const int width = 64;
        const int height = 32;
        var bytes = new List<byte>();
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(
            "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 32 +X 64\n"));

        for (int y = 0; y < height; y++)
        {
            float t = y / (float)(height - 1);
            float r = 0.15f + 0.75f * t;
            float g = 0.30f + 0.30f * t;
            float b = 1.00f - 0.65f * t;
            for (int x = 0; x < width; x++)
            {
                bytes.Add((byte)Math.Clamp(r * 256f, 0, 255));
                bytes.Add((byte)Math.Clamp(g * 256f, 0, 255));
                bytes.Add((byte)Math.Clamp(b * 256f, 0, 255));
                bytes.Add(128); // RGBE exponent: 2^-8
            }
        }

        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private static string CreateIesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "powerpotion_test.ies");
        var angles = string.Join(" ", Enumerable.Range(0, 19).Select(i => (i * 5).ToString()));
        var values = string.Join(" ", new[]
        {
            1.0, 0.99, 0.97, 0.94, 0.90, 0.84, 0.76, 0.66, 0.55, 0.44,
            0.34, 0.25, 0.18, 0.12, 0.08, 0.05, 0.03, 0.02, 0.01,
        });
        var text =
            "IESNA91\n" +
            "[TEST] PowerPotion P3 synthetic IES\n" +
            "TILT=NONE\n" +
            "1 1000 1 19 1 1 2 0 0 0\n" +
            angles + "\n" +
            "0\n" +
            values + "\n";
        File.WriteAllText(path, text);
        return path;
    }

    private static double BottomDark(string path)
    {
        var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        int start = (int)(height * 0.55);
        int rows = height - start;
        var buffer = new byte[stride * rows];
        converted.CopyPixels(new Int32Rect(0, start, width, rows), buffer, stride, 0);
        int dark = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            double luma = 0.2126 * buffer[i + 2] + 0.7152 * buffer[i + 1] + 0.0722 * buffer[i];
            if (luma > 30 && luma < 160) dark++;
        }
        return dark / (double)(width * rows);
    }

    private static double BottomAverage(string path)
    {
        var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        int start = (int)(height * 0.65);
        int rows = height - start;
        var buffer = new byte[stride * rows];
        converted.CopyPixels(new Int32Rect(0, start, width, rows), buffer, stride, 0);
        double sum = 0;
        for (int i = 0; i < buffer.Length; i += 4)
            sum += 0.2126 * buffer[i + 2] + 0.7152 * buffer[i + 1] + 0.0722 * buffer[i];
        return sum / (width * rows);
    }
}
