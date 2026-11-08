using System.Diagnostics;
using System.IO;
using System.Text;

namespace PowerPotion.Host;

/// <summary>
/// Lightweight host-side diagnostics: a single append-only text log next to the engine log.
/// Records managed exceptions (including WPF dispatcher exceptions) and tagged engine events
/// so a failure can be correlated with the native engine log.
/// </summary>
public static class HostLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string logDir)
    {
        try
        {
            Directory.CreateDirectory(logDir);
            _path = Path.Combine(logDir, "host.log");
            Write("---- host start ----");
        }
        catch { }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:D2}] {message}";
        try
        {
            lock (Gate)
            {
                if (_path != null) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
        try { Debug.WriteLine("[host] " + line); } catch { }
    }

    public static void Exception(string context, Exception ex) =>
        Write($"{context}: {ex.GetType().Name}: {ex.Message}\n{ex}");
}
