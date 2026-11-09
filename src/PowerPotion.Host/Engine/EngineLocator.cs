using System.IO;
using Microsoft.Win32;

namespace PowerPotion.Host.Engine;

/// <summary>Locates the installed third-party rendering engine product that provides the native rendering engine.</summary>
public static class EngineLocator
{
    public const string DefaultDir = @"C:\Program Files\Enscape\RendererHost";

    public static string? FindRendererHostDir()
    {
        // 1. explicit override
        var env = Environment.GetEnvironmentVariable("POWERPOTION_ENGINE_DIR");
        if (!string.IsNullOrWhiteSpace(env) && IsValid(env)) return env;

        // 2. default install location
        if (IsValid(DefaultDir)) return DefaultDir;

        // 3. registry (uninstall key carries DisplayIcon pointing into the install)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Enscape");
            if (key?.GetValue("DisplayIcon") is string icon)
            {
                var path = icon.Trim('"', ' ');
                var rootIdx = path.IndexOf(@"\Enscape\", StringComparison.OrdinalIgnoreCase);
                if (rootIdx > 0)
                {
                    var candidate = Path.Combine(path[..rootIdx], "Enscape", "RendererHost");
                    if (IsValid(candidate)) return candidate;
                }
            }
        }
        catch { /* registry unavailable */ }

        return null;
    }

    public static bool IsValid(string dir) =>
        File.Exists(Path.Combine(dir, "Enscape.RendererApi.dll")) &&
        Directory.Exists(Path.Combine(dir, "Renderer", "Bin64"));
}
