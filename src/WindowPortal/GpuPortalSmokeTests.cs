using System.Diagnostics;

namespace WindowPortal;

internal static class GpuPortalSmokeTests
{
    internal static int Run(nint sourceWindow, int durationMilliseconds)
    {
        _ = Application.OleRequired();
        if (!NativeMethods.GetWindowRect(sourceWindow, out var sourceBounds) ||
            sourceBounds.Width <= 1 ||
            sourceBounds.Height <= 1)
        {
            Console.Error.WriteLine("GPU 透视冒烟测试的来源窗口不可用。");
            return 15;
        }

        var geometry = PortalGeometry.Rectangle(420, 280, 24);
        var center = new NativeMethods.Point(
            sourceBounds.Left + sourceBounds.Width / 2,
            sourceBounds.Top + sourceBounds.Height / 2);
        var protectedWindow = NativeMethods.GetForegroundWindow();
        if (protectedWindow == nint.Zero || protectedWindow == sourceWindow)
        {
            Console.Error.WriteLine("请让另一个应用保持在前台后再运行 GPU 透视冒烟测试。");
            return 15;
        }

        try
        {
            using var overlay = new GpuPortalOverlay(geometry);
            if (!overlay.TryShow(
                    sourceWindow,
                    protectedWindow,
                    center,
                    out var showError))
            {
                Console.Error.WriteLine(showError);
                return 16;
            }

            Console.WriteLine(
                $"GPU 透视冒烟：来源=0x{sourceWindow:X}，" +
                $"前台保护=0x{protectedWindow:X}，持续={durationMilliseconds}ms。");
            var stopwatch = Stopwatch.StartNew();
            using var highResolutionWaiter = new HighResolutionWaiter();
            var updates = 0L;
            var maximumUpdateMilliseconds = 0d;
            var updateDurations = new List<double>();
            while (stopwatch.ElapsedMilliseconds < durationMilliseconds)
            {
                var phase = stopwatch.Elapsed.TotalSeconds * 2.4;
                var point = new NativeMethods.Point(
                    center.X + (int)Math.Round(Math.Sin(phase) * 90),
                    center.Y + (int)Math.Round(Math.Cos(phase * 0.8) * 55));
                var updateStartedAt = Stopwatch.GetTimestamp();
                if (!overlay.TryUpdate(point, out var updateError))
                {
                    Console.Error.WriteLine(updateError);
                    return 17;
                }

                var updateMilliseconds =
                    Stopwatch.GetElapsedTime(updateStartedAt).TotalMilliseconds;
                updateDurations.Add(updateMilliseconds);
                maximumUpdateMilliseconds = Math.Max(
                    maximumUpdateMilliseconds,
                    updateMilliseconds);
                updates++;
                highResolutionWaiter.Wait(2);
            }

            var capturedFrames = overlay.CapturedFrames;
            var presentedFrames = overlay.PresentedFrames;
            updateDurations.Sort();
            var p95 = Percentile(updateDurations, 0.95);
            var p99 = Percentile(updateDurations, 0.99);
            overlay.Hide();
            Console.WriteLine(
                $"调度={updates}，WGC 新帧={capturedFrames}，" +
                $"GPU 透视提交={presentedFrames}，" +
                $"P95={p95:F2}ms，P99={p99:F2}ms，" +
                $"最慢={maximumUpdateMilliseconds:F2}ms，" +
                $"高精度定时={highResolutionWaiter.IsHighResolution}。");
            if (presentedFrames == 0)
            {
                Console.Error.WriteLine("GPU 透视窗没有提交视觉帧。");
                return 18;
            }

            Console.WriteLine("GPU 常驻纹理 + 独立鼠标裁剪 + 羽化着色器冒烟通过。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"GPU 透视冒烟失败：HRESULT=0x{exception.HResult:X8}，" +
                $"{exception.GetType().Name}：{exception.Message}");
            return 19;
        }
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp(
            (int)Math.Ceiling(sorted.Count * percentile) - 1,
            0,
            sorted.Count - 1);
        return sorted[index];
    }
}
