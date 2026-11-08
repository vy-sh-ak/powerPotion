using System.IO;
using PowerPotion.Host.Enscape;
using PowerPotion.Protocol;

namespace PowerPotion.Host;

/// <summary>
/// Headless validation of the native bridge (no Revit needed):
/// starts the embedded engine, pushes a synthetic world of colored boxes,
/// positions the camera, waits for frames and captures a screenshot.
/// </summary>
public static class SelfTest
{
    private static StreamWriter? _log;

    private static void Log(string m)
    {
        _log?.WriteLine(m);
        _log?.Flush();
        try { Console.WriteLine(m); } catch { }
    }

    public static int Run(string enscapeDir, string dataDir, int frames, string? shotPath)
    {
        Directory.CreateDirectory(dataDir);
        shotPath = Path.Combine(dataDir, "selftest.png");
        var logPath = Path.Combine(dataDir, "selftest.log");
        _log = new StreamWriter(logPath, append: false);

        Log($"PowerPotion selftest — engine dir: {enscapeDir}");
        Log($"Screenshot target: {shotPath}");

        var engine = new EngineBridge(enscapeDir, dataDir);
        engine.Log += Log;

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = 1;

        var uiThread = new Thread(() =>
        {
            try
            {
                var app = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                };
                var window = new System.Windows.Window
                {
                    Width = 1280,
                    Height = 720,
                    WindowStyle = System.Windows.WindowStyle.SingleBorderWindow,
                    Title = "PowerPotion selftest",
                };
                var grid = new System.Windows.Controls.Grid();
                window.Content = grid;
                var surface = new RendererSurface(engine, () => "selftest");
                surface.Log += Log;
                grid.Children.Add(surface);
                window.Show();

                // drive the test from a worker thread; engine calls from non-UI threads are fine
                // (Chaos' own host drives world APIs from its CAD message-loop thread).
                var worker = new Thread(() => TestSequence(engine, app, tcs, frames, shotPath).GetAwaiter().GetResult())
                {
                    IsBackground = true,
                };
                worker.Start();

                app.Run();
                result = tcs.Task.IsCompleted ? tcs.Task.Result : 1;
            }
            catch (Exception ex)
            {
                Log("SELFTEST CRASHED (ui): " + ex);
                tcs.TrySetResult(1);
            }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();

        _log?.Dispose();
        return result;
    }

    private static async Task TestSequence(EngineBridge engine, System.Windows.Application app,
        TaskCompletionSource<int> tcs, int frames, string shotPath)
    {
        try
        {
            // wait for engine startup (RendererSurface starts it asynchronously)
            for (int i = 0; i < 120 && !engine.IsRunning; i++)
                await Task.Delay(500);
            if (!engine.IsRunning)
            {
                Log("SELFTEST FAILED: engine did not reach running state within 60s.");
                tcs.TrySetResult(1);
            }
            else
            {
                Log("Engine is running. Pushing world…");
                var world = BuildSyntheticWorld();
                Log($"World: {world.Models.Count} models, {world.Materials.Count} materials, {world.Lights.Count} lights.");
                engine.ExecuteConsoleCommand("g_SunPower 0.55", true);
                engine.ExecuteConsoleCommand("r_exposureAdaptionSpeed 0", true);
                engine.ExecuteConsoleCommand("r_postAutoContrast 0", true);
                var camera = world.Camera;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POWERPOTION_NOCAM")))
                    world.Camera = new CameraUpdate(); // empty → host skips SetCamera
                engine.ApplyWorld(world);
                world.Camera = camera;
                // Ground-truth: ask the engine where it thinks the box instance is.
                if (world.Instances.Count > 0)
                    await engine.QueryInstanceBoundsAsync(501, (uint)world.Models[0].Id, (uint)world.Instances[0].InstanceId);
                Log("World applied. Waiting for frames…");
                engine.WaitForFrames((ulong)Math.Max(frames, 600));
                Log("Frames rendered. Requesting capture…");

                engine.CaptureScreenshot(shotPath, panorama: false);
                var done = await Task.WhenAny(engine.CaptureDone, Task.Delay(45000));
                if (done != engine.CaptureDone)
                {
                    Log("SELFTEST FAILED: capture timed out.");
                    tcs.TrySetResult(5);
                }
                else
                {
                    var ok = await engine.CaptureDone;
                    if (File.Exists(shotPath))
                    {
                        Log($"SELFTEST OK — screenshot written: {shotPath} ({new FileInfo(shotPath).Length} bytes)");
                        tcs.TrySetResult(0);
                    }
                    else
                    {
                        Log($"SELFTEST FAILED — capture callback ok={ok} but file missing.");
                        tcs.TrySetResult(5);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("SELFTEST CRASHED (worker): " + ex);
            tcs.TrySetResult(1);
        }
        finally
        {
            try { await engine.StopAsync(); } catch { }
            await app.Dispatcher.InvokeAsync(() => app.Shutdown());
        }
    }

    /// <summary>Axis gizmo: red box on +X (east), green box up, blue box on -Z (north), plus a floor.</summary>
    private static WorldSnapshot BuildSyntheticWorld()
    {
        var world = new WorldSnapshot { DocumentName = "selftest", ViewName = "synthetic" };

        world.Materials.Add(new MaterialDto { Id = 101, Name = "Red", DiffuseR = 0.85f, DiffuseG = 0.05f, DiffuseB = 0.05f, Roughness = 0.4f });
        world.Materials.Add(new MaterialDto { Id = 102, Name = "Green", DiffuseR = 0.05f, DiffuseG = 0.85f, DiffuseB = 0.05f, Roughness = 0.4f });
        world.Materials.Add(new MaterialDto { Id = 103, Name = "Blue", DiffuseR = 0.05f, DiffuseG = 0.05f, DiffuseB = 0.85f, Roughness = 0.4f });
        world.Materials.Add(new MaterialDto { Id = 104, Name = "Floor", DiffuseR = 0.9f, DiffuseG = 0.1f, DiffuseB = 0.9f, Roughness = 0.8f });
        world.Materials.Add(new MaterialDto { Id = 105, Name = "White", DiffuseR = 0.95f, DiffuseG = 0.95f, DiffuseB = 0.95f, Roughness = 0.9f });

        // Full validation scene in TRUE METERS (the EngineBridge applies the measured calibration).
        const float F = 1.0f;

        var boxRed = MakeBox(101, -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f);
        var boxGreen = MakeBox(102, -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f);
        var boxBlue = MakeBox(103, -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f);
        var floor = MakeQuad(104, 5f); // 10x10 m floor at the origin
        world.Models.AddRange(new[] { boxRed, boxGreen, boxBlue, floor });

        world.Instances.Add(new InstanceDto { ModelId = boxRed.Id, InstanceId = 101, Matrix = Translate(2.0f, 1.5f, 0f) });    // 2 m east
        world.Instances.Add(new InstanceDto { ModelId = boxGreen.Id, InstanceId = 102, Matrix = Translate(0f, 3.5f, 0f) });    // 3.5 m up
        world.Instances.Add(new InstanceDto { ModelId = boxBlue.Id, InstanceId = 103, Matrix = Translate(0f, 1.5f, -2f) });     // 2 m north
        world.Instances.Add(new InstanceDto { ModelId = floor.Id, InstanceId = 104, Matrix = Translate(0f, 0f, 0f) });
        world.Sun = new SunUpdate { Mode = 0, Latitude = 48.14, Longitude = 11.58, NorthAngle = 0, TimeZoneHours = 1, Hour = 14f };

        // Calibration camera: 50 m straight above origin, looking straight down (-Y forward),
        // with engine-north (-Z) as screen-up. Expected view: floor quad with the three boxes,
        // red box LEFT of green (engine +X renders screen-left when facing -Z), blue box ABOVE green.
        world.Camera = TopDownCamera();
        return world;
    }

    /// <summary>
    /// Top-down calibration camera: 50 m above origin looking straight down, north (-Z) as screen-up.
    /// Basis (GLM right-handed lookAt): f=(0,-1,0), u=(0,0,-1) → r=cross(f,u)=(1,0,0), u=cross(r,f)=(0,0,-1),
    /// columns [r, u, -f] → quaternion (-√2/2, 0, 0, √2/2) (i.e. −90° about X).
    /// </summary>
    private static CameraUpdate TopDownCamera()
    {
        const float s = 0.70710678f;
        return new CameraUpdate
        {
            // 50 m up: the raw (×11.25) culling frustum opens downward from the raw camera,
            // so the camera's raw Y (50) must exceed the highest raw geometry Y (green top:
            // (3.5+0.5)×11.25 = 45).
            PosX = 0f, PosY = 50f, PosZ = 0f,
            QuatX = -s, QuatY = 0f, QuatZ = 0f, QuatW = s,
            FovDegrees = 60f,
            Ortho = false,
            AutoExposure = true,
        };
    }

    private static float[] Translate(float x, float y, float z) => new float[]
    {
        1,0,0,0,
        0,1,0,0,
        0,0,1,0,
        x,y,z,1,
    };

    private static ModelDto MakeBox(int materialId, float minx, float miny, float minz, float maxx, float maxy, float maxz)
    {
        var min = new[] { minx, miny, minz };
        var max = new[] { maxx, maxy, maxz };
        var m = new ModelDto { Id = materialId * 10 + 1, FamilyClass = 0 };
        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var indices = new List<uint>();

        float Vx(int i) => (i & 1) == 0 ? min[0] : max[0];
        float Vy(int i) => (i & 2) == 0 ? min[1] : max[1];
        float Vz(int i) => (i & 4) == 0 ? min[2] : max[2];

        void Quad(int a, int b, int c, int d, float nx, float ny, float nz)
        {
            int baseIdx = positions.Count / 3;
            foreach (var i in new[] { a, b, c, d })
            {
                positions.AddRange(new[] { Vx(i), Vy(i), Vz(i) });
                normals.AddRange(new[] { nx, ny, nz });
                uvs.AddRange(new[] { 0f, 0f });
            }
            indices.AddRange(new uint[] { (uint)baseIdx, (uint)(baseIdx + 1), (uint)(baseIdx + 2), (uint)baseIdx, (uint)(baseIdx + 2), (uint)(baseIdx + 3) });
        }

        Quad(0, 4, 6, 2, -1, 0, 0);  // -X
        Quad(1, 3, 7, 5, 1, 0, 0);   // +X
        Quad(0, 1, 5, 4, 0, -1, 0);  // -Y
        Quad(2, 6, 7, 3, 0, 1, 0);   // +Y
        Quad(0, 2, 3, 1, 0, 0, -1);  // -Z
        Quad(4, 5, 7, 6, 0, 0, 1);   // +Z

        m.Positions = positions.ToArray();
        m.Normals = normals.ToArray();
        m.Uvs = uvs.ToArray();
        m.Indices = indices.ToArray();
        m.Groups = new[] { new MaterialIndexGroupDto
        {
            StartIndex = 0,
            NumIndices = (uint)indices.Count,
            FrontFaceMaterialId = (uint)materialId,
            BackFaceMaterialId = 0xFFFFFFFF,
            VisibleLodStart = 0,
            VisibleLodEnd = 1000,
        }};
        return m;
    }

    private static ModelDto MakeQuad(int materialId, float halfSize)
    {
        var m = new ModelDto { Id = 1001, FamilyClass = 0 };
        m.Positions = new float[] { -halfSize, 0, -halfSize, halfSize, 0, -halfSize, halfSize, 0, halfSize, -halfSize, 0, halfSize };
        m.Normals = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 };
        m.Uvs = new float[] { 0, 0, 1, 0, 1, 1, 0, 1 };
        m.Indices = new uint[] { 0, 1, 2, 0, 2, 3 };
        m.Groups = new[] { new MaterialIndexGroupDto
        {
            StartIndex = 0,
            NumIndices = 6,
            FrontFaceMaterialId = (uint)materialId,
            BackFaceMaterialId = 0xFFFFFFFF,
            VisibleLodStart = 0,
            VisibleLodEnd = 1000,
        }};
        return m;
    }
}
