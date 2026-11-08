using System.IO;
using System.Net.Sockets;
using System.Windows;
using PowerPotion.Host;
using PowerPotion.Host.Enscape;
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

        var enscapeDir = EnscapeLocator.FindRendererHostDir();
        if (enscapeDir == null)
        {
            Console.Error.WriteLine("PowerPotion: no Enscape installation found. " +
                "Install Enscape or set POWERPOTION_ENSCAPE_DIR to its RendererHost directory.");
            return 3;
        }

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerPotion");
        EngineBridge.InstallAssemblyResolver(enscapeDir);

        if (selftest)
        {
            return SelfTest.Run(enscapeDir, dataDir, frames, shot);
        }

        var settings = HostSettings.Load();
        var app = new Application();
        var engine = new EngineBridge(enscapeDir, dataDir);

        HostConnection? connection = null;
        if (port > 0)
        {
            try
            {
                var client = new TcpClient();
                client.Connect("127.0.0.1", port);
                connection = new HostConnection(client, a => app.Dispatcher.Invoke(a));
                connection.Start();
                _ = connection.SendHelloAsync(engine.EngineVersion, ThisVersion());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not connect to Revit on port {port}: {ex.Message}",
                    "PowerPotion", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        var window = new MainWindow(engine, settings, connection);
        if (!string.IsNullOrEmpty(doc)) window.SetDocumentName(doc);
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
