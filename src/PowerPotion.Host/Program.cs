using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using PowerPotion.Host;
using PowerPotion.Host.Enscape;
using PowerPotion.Host.Net;
using PowerPotion.Host.Rendering;
using PowerPotion.Rendering;

namespace PowerPotion.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        const string usage = "PowerPotion.Host --port <n> [--doc <name>] | --selftest [--frames N] [--shot <path>] | --p0 [--shot <path>] [--seconds N] [--resize] | --p1 [--shot <path>] | --p2 [--shot <path>] | --p3 [--shot <path>] | --locate";
        int port = 0;
        string doc = "";
        bool selftest = false;
        bool p0 = false;
        bool p1 = false;
        bool p2 = false;
        bool p3 = false;
        bool resizeTest = false;
        string? shot = null;
        int frames = 60;
        int seconds = 6;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--doc" when i + 1 < args.Length: doc = args[++i]; break;
                case "--selftest": selftest = true; break;
                case "--p0": p0 = true; break;
                case "--p1": p1 = true; break;
                case "--p2": p2 = true; break;
                case "--p3": p3 = true; break;
                case "--resize": resizeTest = true; break;
                case "--shot" when i + 1 < args.Length: shot = args[++i]; break;
                case "--frames" when i + 1 < args.Length: frames = int.Parse(args[++i]); break;
                case "--seconds" when i + 1 < args.Length: seconds = int.Parse(args[++i]); break;
                case "--locate": return Locate();
                case "--help" or "-h" or "/?": Console.WriteLine(usage); return 0;
                default: Console.WriteLine($"Unknown argument '{args[i]}'. {usage}"); return 2;
            }
        }

        // P0-P3 run the standalone Vulkan renderer and deliberately do not touch Enscape.
        if (p3) return P3Spike.Run(shot);
        if (p2) return P2Spike.Run(shot);
        if (p1) return P1Spike.Run(shot);
        if (p0) return P0Spike.Run(shot, seconds, resizeTest);

        var useStandalone = string.Equals(
            Environment.GetEnvironmentVariable("POWERPOTION_RENDERER"), "pp", StringComparison.OrdinalIgnoreCase);

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerPotion");
        HostLog.Initialize(Path.Combine(dataDir, "Logs"));

        string? enscapeDir = null;
        if (!useStandalone)
        {
            enscapeDir = EnscapeLocator.FindRendererHostDir();
            if (enscapeDir == null)
            {
                Console.Error.WriteLine("PowerPotion: no compatible renderer installation found. " +
                    "Install the renderer or set POWERPOTION_ENSCAPE_DIR to its RendererHost directory. " +
                    "Use POWERPOTION_RENDERER=pp for the standalone renderer.");
                return 3;
            }
            EngineBridge.InstallAssemblyResolver(enscapeDir);
        }

        if (selftest)
        {
            if (useStandalone)
            {
                Console.Error.WriteLine("--selftest currently targets the Enscape bridge; use --p0 for the standalone spike.");
                return 4;
            }
            return SelfTest.Run(enscapeDir!, dataDir, frames, shot);
        }

        var settings = HostSettings.Load();
        var app = new Application();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HostLog.Write("AppDomain unhandled: " + e.ExceptionObject);
        app.DispatcherUnhandledException += (_, e) =>
        {
            HostLog.Exception("Dispatcher unhandled", e.Exception);
            HostLog.Write("Dispatcher unhandled stack: " + Environment.StackTrace);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            HostLog.Exception("Unobserved task", e.Exception);

        IRendererBridge engine = useStandalone
            ? new RendererBridge()
            : new EngineBridge(enscapeDir!, dataDir);

        HostConnection? connection = null;
        if (port > 0)
        {
            try
            {
                var client = new TcpClient();
                client.Connect("127.0.0.1", port);
                connection = new HostConnection(client, a =>
                {
                    if (app.Dispatcher.CheckAccess()) a();
                    else app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, a);
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not connect to Revit on port {port}: {ex.Message}",
                    "PowerPotion", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        var window = new MainWindow(engine, settings, connection);
        if (!string.IsNullOrEmpty(doc)) window.SetDocumentName(doc);

        // Start reading only after the window subscribed to the connection events — otherwise the
        // scene Revit sends right after connecting can arrive before any handler exists and be lost.
        if (connection != null)
        {
            connection.Start();
            _ = connection.SendHelloAsync(engine.EngineVersion, ThisVersion());
        }

        app.Run(window);
        return 0;
    }

    private static int Locate()
    {
        var dir = EnscapeLocator.FindRendererHostDir();
        Console.WriteLine(dir is null ? "not found" : dir);
        return dir is null ? 1 : 0;
    }

    public static string ThisVersion() =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
