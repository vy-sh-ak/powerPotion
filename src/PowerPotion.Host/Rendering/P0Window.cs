using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerPotion.Host.Rendering;

/// <summary>
/// P0 spike window: the same WPF layout seam as the real host (a Grid holding an HwndHost),
/// hosting the standalone Vulkan renderer.
/// </summary>
public sealed class P0Window : Window
{
    public VulkanSurface Surface { get; }

    public P0Window()
    {
        Title = "PowerPotion — P0 Vulkan Spike";
        Width = 1280;
        Height = 800;
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x08, 0x18));

        Surface = new VulkanSurface();
        Content = Surface;
        Surface.Log += message => Console.WriteLine("[vk] " + message);
    }
}

public static class P0Spike
{
    public static int Run(string? shotPath, int seconds, bool resizeTest)
    {
        var app = new Application();
        var window = new P0Window();
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
                    Console.WriteLine("P0 FAILED: renderer did not become ready.");
                    if (window.Surface.Error != null)
                        Console.WriteLine("P0 error: " + window.Surface.Error);
                    app.Shutdown(2);
                    return;
                }

                Console.WriteLine($"P0 renderer ready: {window.Surface.DeviceName}");
                Console.WriteLine($"P0 child HWND: 0x{window.Surface.ChildHwnd:X}");
                var initialSize = window.Surface.SurfaceSize;
                Console.WriteLine($"P0 surface size: {initialSize.Width}x{initialSize.Height}");

                await Task.Delay(1200);

                if (resizeTest)
                {
                    Console.WriteLine("P0 resize: 1280x800 -> 960x600");
                    window.Width = 960;
                    window.Height = 600;
                    await Task.Delay(1200);
                    var resized = window.Surface.SurfaceSize;
                    Console.WriteLine($"P0 surface size after resize: {resized.Width}x{resized.Height}");
                    if (resized == initialSize)
                    {
                        Console.WriteLine("P0 FAILED: surface did not resize.");
                        app.Shutdown(5);
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(shotPath))
                {
                    bool captured = await window.Surface.CaptureAsync(shotPath);
                    Console.WriteLine($"P0 capture: {(captured ? "ok" : "FAILED")} {shotPath}");
                    if (!captured)
                    {
                        app.Shutdown(3);
                        return;
                    }

                    var (width, height, cubeFraction) = InspectPng(shotPath);
                    Console.WriteLine($"P0 image: {width}x{height}, non-background pixels={cubeFraction:P1}");
                    if (cubeFraction < 0.03)
                    {
                        Console.WriteLine("P0 FAILED: captured image does not contain the cube.");
                        app.Shutdown(6);
                        return;
                    }
                }

                var remaining = (int)(seconds * 1000 - clock.ElapsedMilliseconds);
                if (remaining > 0) await Task.Delay(remaining);

                window.Surface.Stop();
                exitCode = 0;
                Console.WriteLine("P0 clean shutdown.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("P0 FAILED: " + ex);
                app.Shutdown(4);
            }
        };

        app.Run(window);
        return exitCode;
    }

    /// <summary>Loads the capture and measures the fraction of pixels that are not the clear color.</summary>
    private static (int Width, int Height, double Fraction) InspectPng(string path)
    {
        var frame = BitmapFrame.Create(new Uri(System.IO.Path.GetFullPath(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var buffer = new byte[stride * height];
        converted.CopyPixels(buffer, stride, 0);

        int lit = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            int b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            // The clear color is ~ (14, 11, 27); the cube faces are bright.
            if (r + g + b > 150 && Math.Max(r, Math.Max(g, b)) > 70) lit++;
        }

        return (width, height, lit / (double)(width * height));
    }
}
