using System.Diagnostics;
using System.Windows;

namespace PowerPotion.Host.Rendering;

/// <summary>
/// P1 stress check: repeated swapchain recreation, vsync/HDR toggles, minimize/restore,
/// capture and validation-error accounting. Exits non-zero on failure.
/// </summary>
public static class P1Spike
{
    public static int Run(string? shotPath)
    {
        var app = new Application();
        var window = new P0Window { Title = "PowerPotion — P1 Vulkan Stress" };
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
                    Console.WriteLine("P1 FAILED: renderer did not become ready.");
                    if (window.Surface.Error != null) Console.WriteLine("P1 error: " + window.Surface.Error);
                    app.Shutdown(2);
                    return;
                }

                var surface = window.Surface;
                Console.WriteLine($"P1 renderer: {surface.DeviceName}");
                Console.WriteLine($"P1 validation: enabled={surface.ValidationEnabled}");
                Console.WriteLine($"P1 swapchain: {surface.SwapchainInfo}");

                var initialFrames = surface.FrameCount;
                var initialRecreations = surface.SwapchainRecreationCount;
                var sizes = new (int W, int H)[]
                {
                    (1100, 700), (900, 600), (1280, 800), (1000, 640), (1200, 760), (800, 560),
                };

                for (int i = 0; i < 36; i++)
                {
                    var size = sizes[i % sizes.Length];
                    window.Width = size.W;
                    window.Height = size.H;

                    if (i % 6 == 0) surface.SetVsync(i % 12 != 0);
                    if (i == 18) surface.SetHdr(true);
                    if (i == 24) surface.SetHdr(false);

                    await Task.Delay(80);
                    if (!surface.IsReady)
                    {
                        Console.WriteLine("P1 FAILED: renderer stopped during resize stress.");
                        app.Shutdown(3);
                        return;
                    }
                }

                Console.WriteLine("P1 minimize/restore...");
                window.WindowState = WindowState.Minimized;
                await Task.Delay(700);
                window.WindowState = WindowState.Normal;
                await Task.Delay(900);

                var liveBeforeLeakCheck = surface.LiveAllocations;
                var bytesBeforeLeakCheck = surface.AllocatedBytes;
                for (int i = 0; i < 24; i++)
                {
                    var size = sizes[i % sizes.Length];
                    window.Width = size.W;
                    window.Height = size.H;
                    await Task.Delay(50);
                }
                await Task.Delay(400);
                var liveAfterLeakCheck = surface.LiveAllocations;
                var bytesAfterLeakCheck = surface.AllocatedBytes;
                Console.WriteLine(
                    $"P1 leak check: liveAllocations {liveBeforeLeakCheck}->{liveAfterLeakCheck}, " +
                    $"allocatedBytes {bytesBeforeLeakCheck}->{bytesAfterLeakCheck}");
                bool leakOk = liveAfterLeakCheck == liveBeforeLeakCheck &&
                              bytesAfterLeakCheck <= bytesBeforeLeakCheck + 8UL * 1024 * 1024;

                var targetFrames = initialFrames + 60;
                var wait = Stopwatch.StartNew();
                while (surface.FrameCount < targetFrames && wait.Elapsed < TimeSpan.FromSeconds(10))
                    await Task.Delay(50);

                if (!string.IsNullOrEmpty(shotPath))
                {
                    bool captured = await surface.CaptureAsync(shotPath);
                    Console.WriteLine($"P1 capture: {(captured ? "ok" : "FAILED")} {shotPath}");
                    if (!captured)
                    {
                        app.Shutdown(4);
                        return;
                    }
                }

                var frames = surface.FrameCount;
                var recreations = surface.SwapchainRecreationCount - initialRecreations;
                var errors = surface.ValidationErrorCount;
                var warnings = surface.ValidationWarningCount;
                var allocated = surface.AllocatedBytes;
                var live = surface.LiveAllocations;

                Console.WriteLine(
                    $"P1 stats: frames={frames} (+{frames - initialFrames}), " +
                    $"swapchainRecreations={recreations}, validationErrors={errors}, " +
                    $"validationWarnings={warnings}, allocatedBytes={allocated}, liveAllocations={live}");

                bool ok = frames > initialFrames + 30 && recreations >= 5 && errors == 0 && leakOk;
                if (!ok)
                {
                    Console.WriteLine("P1 FAILED: frame/recreation/validation thresholds not met.");
                    app.Shutdown(5);
                    return;
                }

                surface.Stop();
                exitCode = 0;
                Console.WriteLine("P1 PASSED: stable frame loop, resize stress, no validation errors.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("P1 FAILED: " + ex);
                app.Shutdown(6);
            }
        };

        app.Run(window);
        return exitCode;
    }
}
