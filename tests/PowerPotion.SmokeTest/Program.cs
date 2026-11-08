using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerPotion.Protocol;

namespace PowerPotion.SmokeTest;

/// <summary>
/// End-to-end smoke test of the Revit → renderer-host path without Revit:
/// acts as the Revit side of the protocol (listener + world/camera messages), launches the real
/// PowerPotion.Host, streams the camera at ~30 Hz, sends two world snapshots, requests captures,
/// and verifies the rendered PNGs, a single top-level window and the host-side evidence.
/// </summary>
public static class Program
{
    private static readonly SemaphoreSlim SendGate = new(1, 1);
    private static NetworkStream _stream = null!;

    public static async Task<int> Main(string[] args)
    {
        var realInput = args.Any(a => a == "--real-input");
        var backgroundInput = args.Any(a => a == "--background-input");
        var lateWorld = args.Any(a => a == "--late-world");
        var hostArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var hostExe = hostArg != null
            ? hostArg
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "PowerPotion.Host", "bin", "Release", "net8.0-windows", "PowerPotion.Host.exe"));
        var shotA = Path.Combine(Path.GetTempPath(), "powerpotion_smoke_a.png");
        var shotB = Path.Combine(Path.GetTempPath(), "powerpotion_smoke_b.png");
        var shotSky = Path.Combine(Path.GetTempPath(), "powerpotion_nav_sky.png");
        var shotCanvas = Path.Combine(Path.GetTempPath(), "powerpotion_nav_canvas.png");
        var shotWalk = Path.Combine(Path.GetTempPath(), "powerpotion_nav_walk.png");
        var shotFly = Path.Combine(Path.GetTempPath(), "powerpotion_nav_fly.png");
        var hostLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerPotion", "Logs", "host.log");

        foreach (var f in new[] { shotA, shotB, shotSky, shotCanvas, shotWalk, shotFly }) if (File.Exists(f)) File.Delete(f);
        try { if (File.Exists(hostLog)) File.Delete(hostLog); } catch { }

        if (!File.Exists(hostExe))
        {
            Console.WriteLine($"host not found: {hostExe}");
            return 2;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"host: {hostExe}");
        Console.WriteLine($"listening on {port}");

        using var host = Process.Start(new ProcessStartInfo(hostExe, $"--port {port} --doc SmokeTest")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(hostExe)!,
        })!;
        Console.WriteLine($"host pid {host.Id}");

        using var client = await listener.AcceptTcpClientAsync();
        _stream = client.GetStream();
        Console.WriteLine("host connected");

        await Send(MessageType.Hello, w =>
        {
            w.Write(Hello.CurrentVersion);
            w.Write("SmokeTest.rvt");
            w.Write("smoke");
            w.Write("1.0.0");
        });

        // World immediately: the engine is still booting, so this also checks host-side buffering.
        if (lateWorld)
        {
            // Real Revit order: the plugin exports only after the host reports EngineReady, so the
            // native window spends several seconds visible-but-empty. Make sure it stays hidden and
            // does not show its own Enscape splash during that window.
            Console.WriteLine("deferring world #1 until the host signals EngineReady");
            using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            while (true)
            {
                var frame = await MessageCodec.ReadFrameAsync(_stream, readyCts.Token);
                if (frame is null) throw new InvalidOperationException("host disconnected before EngineReady");
                if (frame.Value.Type == MessageType.EngineReady)
                {
                    Console.WriteLine("host signalled EngineReady; waiting 3 s before exporting");
                    break;
                }
            }
            await Task.Delay(3000);
        }

        var world = BuildWorld(extra: false);
        Console.WriteLine($"world #1: {world.Models.Count} models, {world.Materials.Count} materials (sent before engine ready)");
        await Send(MessageType.WorldSnapshot, w => MessageCodec.WriteWorld(w, world));

        // Camera stream at ~30 Hz, like LiveSyncService.OnIdling.
        using var cameraCts = new CancellationTokenSource();
        var cameraTask = StreamCamera(world.Camera, cameraCts.Token);

        await Task.Delay(14000); // engine boot + world apply
        var a = await CaptureAndWait(shotA, TimeSpan.FromSeconds(90));
        Console.WriteLine($"capture A: {Describe(shotA, a)}");

        // Second world snapshot = what DocumentChanged triggers in Revit.
        var world2 = BuildWorld(extra: true);
        Console.WriteLine($"world #2: {world2.Models.Count} models (live update)");
        await Send(MessageType.WorldSnapshot, w => MessageCodec.WriteWorld(w, world2));
        await Task.Delay(3000);
        var b = await CaptureAndWait(shotB, TimeSpan.FromSeconds(60));
        Console.WriteLine($"capture B: {Describe(shotB, b)}");

        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav follow off"); w.Write(false); });
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav teleport 0 1.7 12"); w.Write(false); });
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav look 0 30"); w.Write(false); });
        await Task.Delay(800);
        var sky = await CaptureAndWait(shotSky, TimeSpan.FromSeconds(60));
        Console.WriteLine($"capture sky: {Describe(shotSky, sky)}");

        // classic "canvas" framing: model on the ground, horizon and sky behind it
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav teleport 8 2.5 13"); w.Write(false); });
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav look 30 -22"); w.Write(false); });
        await Task.Delay(600);
        var canvas = await CaptureAndWait(shotCanvas, TimeSpan.FromSeconds(60));
        Console.WriteLine($"capture canvas: {Describe(shotCanvas, canvas)}");

        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav ground 0"); w.Write(false); });
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav teleport 0 20 10"); w.Write(false); });
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav mode walk"); w.Write(false); });
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav move 3 0 0"); w.Write(false); });
        await Task.Delay(1300);
        var walk = await CaptureAndWait(shotWalk, TimeSpan.FromSeconds(60));
        Console.WriteLine($"capture walk: {Describe(shotWalk, walk)}");

        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav mode fly"); w.Write(false); });
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav move 0 0 3"); w.Write(false); });
        await Task.Delay(600);
        var fly = await CaptureAndWait(shotFly, TimeSpan.FromSeconds(60));
        Console.WriteLine($"capture fly: {Describe(shotFly, fly)}");

        cameraCts.Cancel();
        try { await cameraTask; } catch { }
        await Task.Delay(500);

        var windows = WindowProbe.ListTopLevel(host.Id);
        Console.WriteLine("windows:");
        foreach (var w in windows) Console.WriteLine("  " + w);

        var log = File.Exists(hostLog) ? File.ReadAllText(hostLog) : "";
        var cameraLines = log.Split('\n').Count(l => l.Contains("camera stream:"));
        var worldApplies = log.Split('\n').Count(l => l.Contains("OnWorld apply:"));
        var worldCommits = log.Split('\n').Count(l => l.Contains("World update finished"));
        var embedded = log.Contains("Embedded engine window");
        var shownIdx = log.IndexOf("Engine window shown", StringComparison.Ordinal);
        var applyIdx = log.IndexOf("OnWorld apply:", StringComparison.Ordinal);
        var shownAfterScene = shownIdx > 0 && applyIdx > 0 && shownIdx > applyIdx;
        var dispatcherError = log.Contains("Dispatcher unhandled") || log.Contains("AppDomain unhandled");

        double walkY = double.NaN, flyY = double.NaN;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     log, @"pp_nav pose mode=(\w+) follow=(\w+) x=([-\d.]+) y=([-\d.]+) z=([-\d.]+)"))
        {
            var y = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "Walk") walkY = y;
            else if (m.Groups[1].Value == "Fly") flyY = y;
        }

        var top = (R: 0.0, G: 0.0, B: 0.0);
        var canvasTop = (R: 0.0, G: 0.0, B: 0.0);
        var canvasBottom = (R: 0.0, G: 0.0, B: 0.0);
        try { top = AverageTop(shotSky, 0.18); } catch { }
        try
        {
            canvasTop = AverageRegion(shotCanvas, 0.0, 0.15);
            canvasBottom = AverageRegion(shotCanvas, 0.75, 1.0);
        }
        catch { }
        var skyVisible = top.B > top.R + 8 && top.B > 60;
        var canvasHasSky = canvasTop.B > canvasTop.R + 6;
        var canvasHasGround = canvasBottom.R + canvasBottom.G + canvasBottom.B > 45
                              && (canvasBottom.B - canvasBottom.R) < (canvasTop.B - canvasTop.R);
        var navChangedView = File.Exists(shotSky) && File.Exists(shotWalk) && Hash(shotSky) != Hash(shotWalk);

        Console.WriteLine();
        Console.WriteLine("verification:");
        Console.WriteLine($"  windows visible          : {windows.Count} (expected 1)");
        Console.WriteLine($"  engine window embedded   : {embedded}");
        Console.WriteLine($"  renderer shown after scene : {shownAfterScene}");
        Console.WriteLine($"  camera stream log lines  : {cameraLines} (expected >= 3, 30 updates each)");
        Console.WriteLine($"  world applies            : {worldApplies} (expected 2)");
        Console.WriteLine($"  world commits            : {worldCommits} (expected >= 2)");
        Console.WriteLine($"  dispatcher errors        : {dispatcherError}");
        Console.WriteLine($"  navigation changed view  : {navChangedView}");
        Console.WriteLine($"  sky visible (top RGB)    : {skyVisible} ({top.R:0},{top.G:0},{top.B:0})");
        Console.WriteLine($"  canvas sky (top RGB)     : {canvasHasSky} ({canvasTop.R:0},{canvasTop.G:0},{canvasTop.B:0})");
        Console.WriteLine($"  canvas ground (bottom)   : {canvasHasGround} ({canvasBottom.R:0},{canvasBottom.G:0},{canvasBottom.B:0})");
        Console.WriteLine($"  walk eye height          : {walkY:0.##} (expected 1.7)");
        Console.WriteLine($"  fly vertical move        : {flyY - 1.7:0.##} m (expected ~3)");
        Console.WriteLine($"  screenshots              : A={FileSize(shotA)} B={FileSize(shotB)} sky={FileSize(shotSky)} canvas={FileSize(shotCanvas)} walk={FileSize(shotWalk)} fly={FileSize(shotFly)}");

        var ok = windows.Count == 1 && embedded && shownAfterScene && cameraLines >= 3 && worldApplies == 2
                 && worldCommits >= 2 && !dispatcherError && a && b && sky && canvas && walk && fly
                 && navChangedView && skyVisible && canvasHasSky && canvasHasGround
                 && !double.IsNaN(walkY) && Math.Abs(walkY - 1.7) < 0.2
                 && !double.IsNaN(flyY) && flyY - 1.7 > 2.5;
        if (realInput && ok)
        {
            try { ok = await RunRealInput(host.Id, hostLog, backgroundInput); }
            catch (Exception ex)
            {
                Console.WriteLine("real input test error: " + ex.Message);
                ok = false;
            }
        }

        Console.WriteLine(ok ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED");

        try
        {
            host.Kill(true);
            host.WaitForExit(5000);
        }
        catch { }
        listener.Stop();
        return ok ? 0 : 1;
    }

    private static async Task<bool> RunRealInput(int pid, string hostLogPath, bool background)
    {
        Console.WriteLine();
        Console.WriteLine("--- real input test (SendInput) ---");
        var hwnd = WindowProbe.FindMain(pid);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("  host window not found");
            return false;
        }

        var pA = Path.Combine(Path.GetTempPath(), "powerpotion_input_a.png");
        var pB = Path.Combine(Path.GetTempPath(), "powerpotion_input_b.png");
        var pC = Path.Combine(Path.GetTempPath(), "powerpotion_input_c.png");
        var pD = Path.Combine(Path.GetTempPath(), "powerpotion_input_d.png");
        foreach (var f in new[] { pA, pB, pC, pD }) if (File.Exists(f)) File.Delete(f);

        // framing: near the ground, looking at the model with the horizon in view
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav teleport 0 1.7 14"); w.Write(false); });
        await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav look 0 20"); w.Write(false); });
        await Task.Delay(700);

        var okA = await CaptureAndWait(pA, TimeSpan.FromSeconds(90));
        Console.WriteLine($"  before input : {Describe(pA, okA)}");

        if (background)
        {
            RealInput.ActivateShell();
            await Task.Delay(600);
            Console.WriteLine($"  host foreground after shell activation: {RealInput.IsForeground(pid)} (expected False)");
        }
        else
        {
            if (!RealInput.ForceForeground(hwnd, pid))
            {
                Console.WriteLine("  foreground not acquired - no input injected (safe abort)");
                return false;
            }
            await Send(MessageType.ConsoleCommand, w => { w.Write("pp_nav focus"); w.Write(false); });
            await Task.Delay(300);
            RealInput.CenterCursor(hwnd, out _, out _);
            await Task.Delay(150);
            RealInput.LeftClick();
            await Task.Delay(400);
            Console.WriteLine("  host window is foreground");
        }

        // 1) right-button drag: starts even from a background host (the first click activates it)
        RealInput.CenterCursor(hwnd, out _, out _);
        await Task.Delay(150);
        RealInput.LeftButton(true);
        await Task.Delay(150);
        RealInput.MoveRelative(160, -60);
        await Task.Delay(250);
        RealInput.MoveRelative(160, 0);
        await Task.Delay(250);
        RealInput.LeftButton(false);
        await Task.Delay(600);
        var okB = await CaptureAndWait(pB, TimeSpan.FromSeconds(60));
        Console.WriteLine($"  after drag   : {Describe(pB, okB)}  foreground={RealInput.IsForeground(pid)}");

        // 2) W held for 800 ms
        RealInput.Key(0x57, true);
        await Task.Delay(800);
        RealInput.Key(0x57, false);
        await Task.Delay(500);
        var okC = await CaptureAndWait(pC, TimeSpan.FromSeconds(60));
        Console.WriteLine($"  after W      : {Describe(pC, okC)}");

        // 3) wheel
        for (int i = 0; i < 3; i++)
        {
            RealInput.Wheel(120);
            await Task.Delay(150);
        }
        await Task.Delay(500);
        var okD = await CaptureAndWait(pD, TimeSpan.FromSeconds(60));
        Console.WriteLine($"  after wheel  : {Describe(pD, okD)}");

        await Task.Delay(500);
        var log = File.Exists(hostLogPath) ? File.ReadAllText(hostLogPath) : "";
        var keySeen = log.Contains("nav input: keyboard");
        var lookSeen = log.Contains("nav input: mouse look");
        var wheelSeen = log.Contains("nav input: mouse wheel");
        var detached = log.Contains("nav ui: follow=False");

        double distance = 0;
        var marker = log.LastIndexOf("pp_nav look 0 20", StringComparison.Ordinal);
        var segment = marker >= 0 ? log.Substring(marker) : log;
        var poses = System.Text.RegularExpressions.Regex.Matches(
            segment, @"nav camera: x=([-\d.]+) y=([-\d.]+) z=([-\d.]+)");
        if (poses.Count >= 2)
        {
            static (double, double, double) Xyz(System.Text.RegularExpressions.Match m) => (
                double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
            var first = Xyz(poses[0]);
            var last = Xyz(poses[poses.Count - 1]);
            distance = Math.Sqrt(Math.Pow(last.Item1 - first.Item1, 2)
                                 + Math.Pow(last.Item2 - first.Item2, 2)
                                 + Math.Pow(last.Item3 - first.Item3, 2));
        }

        var changedLook = okA && okB && Hash(pA) != Hash(pB);
        var changedW = okB && okC && Hash(pB) != Hash(pC);
        var changedWheel = okC && okD && Hash(pC) != Hash(pD);

        Console.WriteLine();
        Console.WriteLine("  input verification:");
        Console.WriteLine($"    drag changed the view         : {changedLook}");
        Console.WriteLine($"    W changed the view            : {changedW}");
        Console.WriteLine($"    wheel changed the view        : {changedWheel}");
        Console.WriteLine($"    host saw keyboard input       : {keySeen}");
        Console.WriteLine($"    host saw mouse-look input     : {lookSeen}");
        Console.WriteLine($"    host saw wheel input          : {wheelSeen}");
        Console.WriteLine($"    navigation detached from Revit : {detached}");
        Console.WriteLine($"    camera travelled              : {distance:0.##} m (engine camera log)");

        return changedLook && changedW && changedWheel && keySeen && lookSeen && wheelSeen
               && detached && distance > 0.3;
    }
    private static async Task Send(MessageType type, Action<ProtocolWriter> body)
    {
        await SendGate.WaitAsync();
        try { await MessageCodec.SendAsync(_stream, type, body); }
        finally { SendGate.Release(); }
    }

    private static async Task StreamCamera(CameraUpdate camera, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Send(MessageType.CameraUpdate, w => MessageCodec.WriteCamera(w, camera));
                await Task.Delay(33, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<bool> CaptureAndWait(string path, TimeSpan timeout)
    {
        await Send(MessageType.CaptureRequest, w =>
        {
            w.Write(path);
            w.Write(1920);
            w.Write(1080);
            w.Write(false);
        });

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 20_000)
            {
                await Task.Delay(300);
                return true;
            }
            using var cts = new CancellationTokenSource(500);
            try
            {
                var frame = await MessageCodec.ReadFrameAsync(_stream, cts.Token);
                if (frame is null) break;
                if (frame.Value.Type == MessageType.CaptureResult)
                {
                    var result = (CaptureResult)MessageCodec.Decode(frame.Value.Type, frame.Value.Payload);
                    Console.WriteLine($"  capture ack ok={result.Ok} path={result.FilePath}");
                }
            }
            catch (OperationCanceledException) { }
        }
        return File.Exists(path) && new FileInfo(path).Length > 20_000;
    }

    private static string Describe(string path, bool ok) =>
        ok ? $"{path} ({new FileInfo(path).Length} bytes)" : $"{path} MISSING/TOO SMALL";

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static string Hash(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sha = System.Security.Cryptography.SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch (IOException) { Thread.Sleep(100); }
        }
        throw new IOException("could not read " + path);
    }

    /// <summary>Average RGB of the top fraction of an image (used to prove the sky renders).</summary>
    private static (double R, double G, double B) AverageTop(string path, double fraction) =>
        AverageRegion(path, 0.0, fraction);

    /// <summary>Average RGB of a horizontal band (start/end are fractions of the image height).</summary>
    private static (double R, double G, double B) AverageRegion(string path, double startFraction, double endFraction)
    {
        var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int start = Math.Clamp((int)(height * startFraction), 0, height - 1);
        int end = Math.Clamp((int)(height * endFraction), start + 1, height);
        int rows = end - start;
        int stride = width * 4;
        var buffer = new byte[stride * rows];
        converted.CopyPixels(new Int32Rect(0, start, width, rows), buffer, stride, 0);
        double r = 0, g = 0, b = 0;
        int count = width * rows;
        for (int i = 0; i < buffer.Length; i += 4) { b += buffer[i]; g += buffer[i + 1]; r += buffer[i + 2]; }
        return (r / count, g / count, b / count);
    }

    private static WorldSnapshot BuildWorld(bool extra)
    {
        var world = new WorldSnapshot { DocumentName = "SmokeTest.rvt", ViewName = "{3D}" };

        world.Materials.Add(new MaterialDto { Id = 1, Name = "Ground", DiffuseR = 0.55f, DiffuseG = 0.55f, DiffuseB = 0.55f, Roughness = 0.9f });
        world.Materials.Add(new MaterialDto { Id = 2, Name = "Wall", DiffuseR = 0.85f, DiffuseG = 0.83f, DiffuseB = 0.78f, Roughness = 0.7f });
        world.Materials.Add(new MaterialDto { Id = 3, Name = "Accent", DiffuseR = 0.75f, DiffuseG = 0.12f, DiffuseB = 0.10f, Roughness = 0.5f });

        var ground = Box(100, 1, -15f, -0.1f, -15f, 15f, 0f, 15f);
        var wallA = Box(101, 2, -5f, 0f, -4f, 5f, 3f, -3.7f);
        var wallB = Box(102, 2, 4.7f, 0f, -4f, 5f, 3f, 4f);
        var block = Box(103, 3, -1f, 0f, -1f, 1f, 2f, 1f);

        foreach (var model in new[] { ground, wallA, wallB, block }) world.Models.Add(model);
        for (int i = 0; i < world.Models.Count; i++)
            world.Instances.Add(new InstanceDto { ModelId = world.Models[i].Id, InstanceId = i + 1, Matrix = Identity() });

        if (extra)
        {
            var column = Box(104, 3, 2.5f, 0f, 2.5f, 3.2f, 4f, 3.2f);
            world.Models.Add(column);
            world.Instances.Add(new InstanceDto { ModelId = column.Id, InstanceId = 99, Matrix = Identity() });
        }

        world.Sun = new SunUpdate { Mode = 0, Latitude = 48.14, Longitude = 11.58, NorthAngle = 0, TimeZoneHours = 1, Hour = 14f };
        var quat = CameraQuaternion(new[] { -9.5f, -3.2f, -9.5f }, new[] { 0f, 1f, 0f });
        world.Camera = new CameraUpdate
        {
            PosX = 9.5f, PosY = 3.2f, PosZ = 9.5f,
            QuatX = quat[0], QuatY = quat[1], QuatZ = quat[2], QuatW = quat[3],
            FovDegrees = 60f, FovType = 0, AutoExposure = true,
        };
        return world;
    }

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
        float R00 = r[0], R10 = r[1], R20 = r[2];
        float R01 = u[0], R11 = u[1], R21 = u[2];
        float R02 = -f[0], R12 = -f[1], R22 = -f[2];
        var tr = R00 + R11 + R22;
        var s = MathF.Sqrt(tr + 1f) * 2f;
        return new[] { (R21 - R12) / s, (R02 - R20) / s, (R10 - R01) / s, 0.25f * s };
    }

    private static ModelDto Box(int id, int material, float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<uint>();

        void Quad(float[] a, float[] b, float[] c, float[] d, float[] n)
        {
            var baseIdx = positions.Count / 3;
            foreach (var p in new[] { a, b, c, d })
            {
                positions.AddRange(p);
                normals.AddRange(n);
            }
            indices.AddRange(new[]
            {
                (uint)baseIdx, (uint)(baseIdx + 1), (uint)(baseIdx + 2),
                (uint)baseIdx, (uint)(baseIdx + 2), (uint)(baseIdx + 3),
            });
        }

        var p000 = new[] { x0, y0, z0 }; var p100 = new[] { x1, y0, z0 };
        var p010 = new[] { x0, y1, z0 }; var p110 = new[] { x1, y1, z0 };
        var p001 = new[] { x0, y0, z1 }; var p101 = new[] { x1, y0, z1 };
        var p011 = new[] { x0, y1, z1 }; var p111 = new[] { x1, y1, z1 };

        Quad(p001, p101, p111, p011, new[] { 0f, 0f, 1f });   // +Z
        Quad(p100, p000, p010, p110, new[] { 0f, 0f, -1f });  // -Z
        Quad(p101, p100, p110, p111, new[] { 1f, 0f, 0f });   // +X
        Quad(p000, p001, p011, p010, new[] { -1f, 0f, 0f });  // -X
        Quad(p011, p111, p110, p010, new[] { 0f, 1f, 0f });   // +Y
        Quad(p000, p100, p101, p001, new[] { 0f, -1f, 0f });  // -Y

        return new ModelDto
        {
            Id = id,
            FamilyClass = 0,
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Uvs = new float[positions.Count / 3 * 2],
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
    }

    private static float[] Identity() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
}

internal static class WindowProbe
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder s, int n);

    public static List<string> ListTopLevel(int pid)
    {
        var list = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var p);
            if (p == pid && IsWindowVisible(hwnd))
            {
                var title = new StringBuilder(256);
                GetWindowTextW(hwnd, title, 256);
                if (title.Length > 0) list.Add($"{hwnd:X} '{title}'");
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr FindMain(int pid)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var p);
            if (p == pid && IsWindowVisible(hwnd))
            {
                var title = new StringBuilder(256);
                GetWindowTextW(hwnd, title, 256);
                if (title.Length > 0 && found == IntPtr.Zero) found = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
