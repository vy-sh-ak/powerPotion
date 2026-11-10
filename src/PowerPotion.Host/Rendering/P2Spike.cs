using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerPotion.Protocol;

namespace PowerPotion.Host.Rendering;

/// <summary>
/// P2 scene check: a synthetic Revit-shaped world (ground, walls, colored block, textured quad),
/// camera upload, PBR render, capture, and a live material hot-swap.
/// </summary>
public static class P2Spike
{
    public static int Run(string? shotPath)
    {
        var app = new Application();
        var window = new P0Window { Title = "PowerPotion — P2 Scene" };
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
                    Console.WriteLine("P2 FAILED: renderer did not become ready.");
                    if (window.Surface.Error != null) Console.WriteLine("P2 error: " + window.Surface.Error);
                    app.Shutdown(2);
                    return;
                }

                var surface = window.Surface;
                Console.WriteLine($"P2 renderer: {surface.DeviceName}");
                Console.WriteLine($"P2 swapchain: {surface.SwapchainInfo}");

                var checkerPath = CreateCheckerTexture();
                var world = BuildWorld(checkerPath);
                surface.ApplyWorld(world);
                surface.ApplyCamera(world.Camera);
                await Task.Delay(1600);

                var firstShot = shotPath ?? Path.Combine(Path.GetTempPath(), "powerpotion_p2_scene.png");
                bool captured = await surface.CaptureAsync(firstShot);
                Console.WriteLine($"P2 capture: {(captured ? "ok" : "FAILED")} {firstShot}");
                if (!captured)
                {
                    app.Shutdown(3);
                    return;
                }

                var first = Inspect(firstShot);
                Console.WriteLine($"P2 scene stats: {surface.SceneStats}");
                Console.WriteLine($"P2 draw calls: {surface.SceneDrawCalls}, materials={surface.SceneMaterialCount}, textures={surface.SceneTextureCount}");
                Console.WriteLine($"P2 pixels: nonBg={first.NonBackground:P1}, red={first.Red:P2}, " +
                                  $"checkerBlue={first.CheckerBlue:P2}, bright={first.Bright:P2}");

                // Camera-only frame: move back and verify the scene is not re-uploaded.
                var statsBeforeCamera = surface.SceneStats;
                var framesBeforeCamera = surface.FrameCount;
                var cameraQuat = CameraQuaternion(new[] { -12f, -3f, -12f }, new[] { 0f, 1f, 0f });
                surface.ApplyCamera(new CameraUpdate
                {
                    PosX = 12f, PosY = 4f, PosZ = 12f,
                    QuatX = cameraQuat[0], QuatY = cameraQuat[1], QuatZ = cameraQuat[2], QuatW = cameraQuat[3],
                    FovDegrees = 60f, FovType = 0, AutoExposure = true,
                });
                await Task.Delay(600);
                var cameraShot = Path.Combine(
                    Path.GetDirectoryName(firstShot)!, Path.GetFileNameWithoutExtension(firstShot) + "_camera.png");
                bool capturedCamera = await surface.CaptureAsync(cameraShot);
                var cameraImage = Inspect(cameraShot);
                bool cameraChanged = first.Hash != cameraImage.Hash;
                bool sceneUnchanged = surface.SceneStats == statsBeforeCamera;
                Console.WriteLine($"P2 camera-only: captured={capturedCamera}, changed={cameraChanged}, " +
                                  $"sceneUnchanged={sceneUnchanged}, frames+={surface.FrameCount - framesBeforeCamera}");

                // Live material edit: accent block red -> green, no geometry re-upload.
                var accent = world.Materials.First(m => m.Id == 3);
                accent.DiffuseR = 0.10f;
                accent.DiffuseG = 0.80f;
                accent.DiffuseB = 0.18f;
                surface.OverwriteMaterial(accent);
                await Task.Delay(700);

                var secondShot = Path.Combine(
                    Path.GetDirectoryName(firstShot)!, Path.GetFileNameWithoutExtension(firstShot) + "_after.png");
                bool capturedAfter = await surface.CaptureAsync(secondShot);
                Console.WriteLine($"P2 material update capture: {(capturedAfter ? "ok" : "FAILED")} {secondShot}");
                if (!capturedAfter)
                {
                    app.Shutdown(4);
                    return;
                }

                var second = Inspect(secondShot);
                bool changed = first.Hash != second.Hash;
                Console.WriteLine($"P2 pixels after material edit: nonBg={second.NonBackground:P1}, " +
                                  $"green={second.Green:P2}, changed={changed}, " +
                                  $"updateMs={surface.MaterialUpdateMilliseconds:0.##}");

                surface.SetWhiteMode(true);
                await Task.Delay(500);
                var whiteShot = Path.Combine(
                    Path.GetDirectoryName(firstShot)!, Path.GetFileNameWithoutExtension(firstShot) + "_white.png");
                bool capturedWhite = await surface.CaptureAsync(whiteShot);
                var whiteImage = Inspect(whiteShot);
                surface.SetWhiteMode(false);
                Console.WriteLine($"P2 white mode: captured={capturedWhite}, light={whiteImage.Light:P1}");

                bool ok = first.NonBackground > 0.05
                          && first.Red > 0.0005
                          && (first.CheckerBlue > 0.002 || first.Bright > 0.01)
                          && capturedCamera && cameraChanged && sceneUnchanged
                          && changed
                          && capturedWhite && whiteImage.Light > 0.20
                          && second.Green > first.Green + 0.0003
                          && surface.SceneMaterialCount >= 4
                          && surface.SceneTextureCount >= 2
                          && surface.SceneDrawCalls >= 4;

                if (!ok)
                {
                    Console.WriteLine("P2 FAILED: scene/material/texture thresholds not met.");
                    app.Shutdown(5);
                    return;
                }

                surface.Stop();
                exitCode = 0;
                Console.WriteLine("P2 PASSED: geometry, materials, texture and live material update verified.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("P2 FAILED: " + ex);
                app.Shutdown(6);
            }
        };

        app.Run(window);
        return exitCode;
    }

    internal static WorldSnapshot BuildWorld(string checkerPath)
    {
        var world = new WorldSnapshot
        {
            Revision = 100,
            DocumentName = "P2Synthetic.rvt",
            ViewName = "{3D}",
        };

        world.Materials.Add(new MaterialDto { Id = 1, Name = "Ground", DiffuseR = 0.52f, DiffuseG = 0.53f, DiffuseB = 0.55f, Roughness = 0.92f });
        world.Materials.Add(new MaterialDto { Id = 2, Name = "Wall", DiffuseR = 0.86f, DiffuseG = 0.84f, DiffuseB = 0.78f, Roughness = 0.68f });
        world.Materials.Add(new MaterialDto { Id = 3, Name = "Accent", DiffuseR = 0.78f, DiffuseG = 0.12f, DiffuseB = 0.10f, Roughness = 0.42f });
        world.Materials.Add(new MaterialDto
        {
            Id = 4,
            Name = "Checker",
            DiffuseR = 1f,
            DiffuseG = 1f,
            DiffuseB = 1f,
            Roughness = 0.55f,
            DiffuseTexture = new TextureSlotDto
            {
                File = checkerPath,
                Brightness = 1f,
                Transformation = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f },
                Source = 0,
                WcsType = 0,
            },
        });

        var ground = Box(100, 1, -15f, -0.2f, -15f, 15f, 0f, 15f);
        var wallA = Box(101, 2, -5f, 0f, -4f, 5f, 3f, -3.7f);
        var wallB = Box(102, 2, 4.7f, 0f, -4f, 5f, 3f, 4f);
        var block = Box(103, 3, -1f, 0f, -1f, 1f, 2f, 1f);
        var checker = Quad(104, 4, -3f, 0.2f, 3.4f, 3f, 2.6f, 3.4f);

        foreach (var model in new[] { ground, wallA, wallB, block, checker })
            world.Models.Add(model);

        for (int i = 0; i < world.Models.Count; i++)
            world.Instances.Add(new InstanceDto { ModelId = world.Models[i].Id, InstanceId = i + 1, Matrix = Identity() });

        world.Sun = new SunUpdate { Mode = 1, SunX = 0.4f, SunY = 0.8f, SunZ = 0.3f };
        var quat = CameraQuaternion(new[] { -9.5f, -2.2f, -9.5f }, new[] { 0f, 1f, 0f });
        world.Camera = new CameraUpdate
        {
            PosX = 9.5f, PosY = 3.2f, PosZ = 9.5f,
            QuatX = quat[0], QuatY = quat[1], QuatZ = quat[2], QuatW = quat[3],
            FovDegrees = 60f, FovType = 0, AutoExposure = true,
        };
        return world;
    }

    internal static string CreateCheckerTexture()
    {
        var path = Path.Combine(Path.GetTempPath(), "powerpotion_checker.png");
        const int size = 64;
        const int cell = 8;
        int stride = size * 4;
        var pixels = new byte[stride * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * stride + x * 4;
                bool light = ((x / cell) + (y / cell)) % 2 == 0;
                if (light)
                {
                    pixels[i] = 245; pixels[i + 1] = 245; pixels[i + 2] = 245;
                }
                else
                {
                    pixels[i] = 190; pixels[i + 1] = 70; pixels[i + 2] = 35; // B,G,R: deep blue
                }
                pixels[i + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    internal static (int Width, int Height, double NonBackground, double Red, double Green, double CheckerBlue, double Bright, double Light, string Hash) Inspect(string path)
    {
        var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var buffer = new byte[stride * height];
        converted.CopyPixels(buffer, stride, 0);

        int nonBg = 0, red = 0, green = 0, checkerBlue = 0, bright = 0, light = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            int b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            if (r + g + b > 120) nonBg++;
            if (r > 100 && r > g + 30 && r > b + 30) red++;
            if (g > 100 && g > r + 30 && g > b + 30) green++;
            if (b > 100 && b > r + 40 && b > g + 20) checkerBlue++;
            if (r > 180 && g > 180 && b > 180) bright++;
            if (r > 120 && g > 120 && b > 120) light++;
        }

        int total = width * height;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(buffer));
        return (width, height, nonBg / (double)total, red / (double)total, green / (double)total,
            checkerBlue / (double)total, bright / (double)total, light / (double)total, hash);
    }

    internal static ModelDto Box(int id, int material, float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var indices = new List<uint>();

        void Quad(float[] a, float[] b, float[] c, float[] d, float[] n)
        {
            var baseIndex = positions.Count / 3;
            foreach (var (p, uv) in new[]
                     {
                         (a, new[] { 0f, 0f }), (b, new[] { 1f, 0f }),
                         (c, new[] { 1f, 1f }), (d, new[] { 0f, 1f }),
                     })
            {
                positions.AddRange(p);
                normals.AddRange(n);
                uvs.AddRange(uv);
            }
            indices.AddRange(new[]
            {
                (uint)baseIndex, (uint)(baseIndex + 1), (uint)(baseIndex + 2),
                (uint)baseIndex, (uint)(baseIndex + 2), (uint)(baseIndex + 3),
            });
        }

        var p000 = new[] { x0, y0, z0 }; var p100 = new[] { x1, y0, z0 };
        var p010 = new[] { x0, y1, z0 }; var p110 = new[] { x1, y1, z0 };
        var p001 = new[] { x0, y0, z1 }; var p101 = new[] { x1, y0, z1 };
        var p011 = new[] { x0, y1, z1 }; var p111 = new[] { x1, y1, z1 };

        Quad(p001, p101, p111, p011, new[] { 0f, 0f, 1f });
        Quad(p100, p000, p010, p110, new[] { 0f, 0f, -1f });
        Quad(p101, p100, p110, p111, new[] { 1f, 0f, 0f });
        Quad(p000, p001, p011, p010, new[] { -1f, 0f, 0f });
        Quad(p011, p111, p110, p010, new[] { 0f, 1f, 0f });
        Quad(p000, p100, p101, p001, new[] { 0f, -1f, 0f });

        return Model(id, material, positions, normals, uvs, indices);
    }

    private static ModelDto Quad(int id, int material, float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var positions = new List<float>
        {
            x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0,
        };
        var normals = new List<float>();
        for (int i = 0; i < 4; i++) normals.AddRange(new[] { 0f, 0f, 1f });
        var uvs = new List<float> { 0, 0, 1, 0, 1, 1, 0, 1 };
        var indices = new List<uint> { 0, 1, 2, 0, 2, 3 };
        return Model(id, material, positions, normals, uvs, indices);
    }

    private static ModelDto Model(int id, int material, List<float> positions, List<float> normals,
        List<float> uvs, List<uint> indices) => new()
    {
        Id = id,
        FamilyClass = 0,
        Positions = positions.ToArray(),
        Normals = normals.ToArray(),
        Uvs = uvs.ToArray(),
        BacksideUvs = uvs.ToArray(),
        Indices = indices.ToArray(),
        Groups = new[]
        {
            new MaterialIndexGroupDto
            {
                StartIndex = 0,
                NumIndices = (uint)indices.Count,
                FrontFaceMaterialId = (uint)material,
                BackFaceMaterialId = 0xFFFFFFFF,
                VisibleLodStart = 0,
                VisibleLodEnd = 1000,
            },
        },
    };

    private static float[] CameraQuaternion(float[] forward, float[] up)
    {
        static float[] Norm(float[] v)
        {
            var l = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return new[] { v[0] / l, v[1] / l, v[2] / l };
        }
        static float[] Cross(float[] a, float[] b) => new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0],
        };

        var f = Norm(forward);
        var r = Norm(Cross(f, up));
        var u = Cross(r, f);
        float r00 = r[0], r10 = r[1], r20 = r[2];
        float r01 = u[0], r11 = u[1], r21 = u[2];
        float r02 = -f[0], r12 = -f[1], r22 = -f[2];
        var tr = r00 + r11 + r22;
        var s = MathF.Sqrt(tr + 1f) * 2f;
        return new[] { (r21 - r12) / s, (r02 - r20) / s, (r10 - r01) / s, 0.25f * s };
    }

    private static float[] Identity() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
}
