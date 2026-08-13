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
            if (!overlay.HasInputPassThrough)
            {
                Console.Error.WriteLine(
                    "GPU 透视窗未返回 HTTRANSPARENT/MA_NOACTIVATE。");
                return 16;
            }

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
            var systemHitTestVerified = false;
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

                if (!systemHitTestVerified && overlay.PresentedFrames > 0)
                {
                    if (!overlay.IsSkippedBySystemHitTestAt(point))
                    {
                        Console.Error.WriteLine(
                            "系统 WindowFromPoint 仍命中 GPU 覆盖窗。"
                        );
                        return 17;
                    }

                    systemHitTestVerified = true;
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

            var displayPlacementsBeforeTraversal = overlay.DisplayPlacementCount;
            var traversalPoints = new[]
            {
                new NativeMethods.Point(sourceBounds.Left + 48, sourceBounds.Top + 48),
                new NativeMethods.Point(sourceBounds.Right - 48, sourceBounds.Top + 48),
                new NativeMethods.Point(sourceBounds.Right - 48, sourceBounds.Bottom - 48),
                new NativeMethods.Point(sourceBounds.Left + 48, sourceBounds.Bottom - 48),
                center,
            };
            foreach (var traversalPoint in traversalPoints)
            {
                if (!overlay.TryUpdate(traversalPoint, out var traversalError))
                {
                    Console.Error.WriteLine(traversalError);
                    return 17;
                }

                if (!overlay.IsSkippedBySystemHitTestAt(traversalPoint))
                {
                    Console.Error.WriteLine(
                        "GPU 固定显示层在跨区域移动后重新成为系统鼠标命中目标。");
                    return 17;
                }

                highResolutionWaiter.Wait(8);
            }

            var capturedFrames = overlay.CapturedFrames;
            var presentedFrames = overlay.PresentedFrames;
            var displayPlacements = overlay.DisplayPlacementCount;
            var displayBounds = overlay.DisplayBounds;
            updateDurations.Sort();
            var p95 = Percentile(updateDurations, 0.95);
            var p99 = Percentile(updateDurations, 0.99);
            overlay.Hide();
            Console.WriteLine(
                $"调度={updates}，WGC 新帧={capturedFrames}，" +
                $"GPU 透视提交={presentedFrames}，" +
                $"显示层定位={displayPlacements}，" +
                $"前台恢复={overlay.ForegroundRecoveryCount}，" +
                $"立即层级钳制={overlay.ImmediateForegroundClampCount}，" +
                $"显示层级恢复={overlay.DisplayZOrderRecoveryCount}，" +
                $"显示层位于宿主上方={overlay.IsDisplayAboveProtected}，" +
                $"虚拟屏幕={displayBounds.Width}x{displayBounds.Height}，" +
                $"P95={p95:F2}ms，P99={p99:F2}ms，" +
                $"最慢={maximumUpdateMilliseconds:F2}ms，" +
                $"高精度定时={highResolutionWaiter.IsHighResolution}。");
            if (presentedFrames == 0)
            {
                Console.Error.WriteLine("GPU 透视窗没有提交视觉帧。");
                return 18;
            }

            if (capturedFrames >= 2 && presentedFrames < capturedFrames)
            {
                Console.Error.WriteLine(
                    $"GPU 未提交所有可用的动态 WGC 帧，捕获={capturedFrames}，提交={presentedFrames}。" );
                return 18;
            }

            if (!systemHitTestVerified)
            {
                Console.Error.WriteLine("GPU 覆盖窗未完成系统命中跳过验证。");
                return 18;
            }

            if (displayPlacementsBeforeTraversal != 1 ||
                displayPlacements != 1)
            {
                Console.Error.WriteLine(
                    $"一次 GPU 会话内显示层只能定位一次，遍历前={displayPlacementsBeforeTraversal}，最终={displayPlacements}。");
                return 18;
            }

            Console.WriteLine(
                "GPU 常驻纹理 + 固定虚拟屏幕显示层 + 输入穿透 + 羽化着色器冒烟通过。");
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
