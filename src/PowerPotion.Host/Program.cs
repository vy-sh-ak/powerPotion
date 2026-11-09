using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using PowerPotion.Host;
using PowerPotion.Host.Engine;
using PowerPotion.Host.Net;

namespace PowerPotion.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        const string usage = "PowerPotion.Host --port <n> [--doc <name>] | --selftest [--frames N] [--shot <path>] | --locate";
        int port = 0;
        string doc = "";
        bool selftest = false;
        string? shot = null;
        int frames = 60;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--doc" when i + 1 < args.Length: doc = args[++i]; break;
                case "--selftest": selftest = true; break;
                case "--shot" when i + 1 < args.Length: shot = args[++i]; break;
                case "--frames" when i + 1 < args.Length: frames = int.Parse(args[++i]); break;
                case "--locate": return Locate();
                case "--help" or "-h" or "/?": Console.WriteLine(usage); return 0;
                default: Console.WriteLine($"Unknown argument '{args[i]}'. {usage}"); return 2;
            }
        }

        var engineDir = EngineLocator.FindRendererHostDir();
        if (engineDir == null)
        {
            Console.Error.WriteLine("PowerPotion: no compatible renderer installation found. " +
                "Install the renderer or set POWERPOTION_ENGINE_DIR to its RendererHost directory.");
            return 3;
        }

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerPotion");
        EngineBridge.InstallAssemblyResolver(engineDir);
        HostLog.Initialize(Path.Combine(dataDir, "Logs"));

        if (selftest)
        {
            return SelfTest.Run(engineDir, dataDir, frames, shot);
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

        var engine = new EngineBridge(engineDir, dataDir);

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
        var dir = EngineLocator.FindRendererHostDir();
        Console.WriteLine(dir is null ? "not found" : dir);
        return dir is null ? 1 : 0;
    }

    public static string ThisVersion() =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
