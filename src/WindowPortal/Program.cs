using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace WindowPortal;

internal static class Program
{
    private static volatile bool _exitRequested;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        NativeMethods.TryEnablePerMonitorDpiAwareness();

        PortalOptions options;
        try
        {
            options = PortalOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.ShowVersion)
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown";
            Console.WriteLine($"WindowPortal {version}");
            return 0;
        }

        if (options.SelfTest)
        {
            return SelfTests.Run();
        }

        if (options.ListWindows)
        {
            return WindowInventory.PrintVisibleWindows();
        }

        if (options.CompatibilityReport)
        {
            return WindowInventory.PrintCompatibilityReport();
        }

        if (options.InspectWindow is { } inspectWindow)
        {
            return WindowHierarchyInspector.Inspect(inspectWindow, options.InspectPoint);
        }

        using var controller = new WindowRegionController(options.Radius);
        using var visualOverlay = new DwmPortalOverlay(options.Radius);
        RegisterEmergencyRestoration(controller, visualOverlay);

        return options.ProbeWindow is { } probeWindow
            ? RunProbe(
                controller,
                visualOverlay,
                probeWindow,
                options.ProbeDurationMilliseconds,
                options.Radius)
            : RunInteractive(controller, visualOverlay, options.PollMilliseconds);
    }

    private static int RunInteractive(
        WindowRegionController controller,
        DwmPortalOverlay visualOverlay,
        int pollMilliseconds)
    {
        Console.WriteLine("WindowPortal 技术验证已运行。");
        Console.WriteLine("按住 F8：在鼠标所在窗口创建跟随圆洞；松开 F8：恢复窗口。");
        Console.WriteLine("Esc 或 Ctrl+Shift+Q：恢复窗口并退出。");
        Console.WriteLine();

        var wasActivationHeld = false;
        var wasEscapeHeld = false;
        var wasExitChordHeld = false;
        var visualWarningShown = false;

        try
        {
            while (!_exitRequested)
            {
                var loopStartedAt = Stopwatch.GetTimestamp();
                var activationHeld = NativeMethods.IsKeyDown(NativeMethods.VkF8);
                var escapeHeld = NativeMethods.IsKeyDown(NativeMethods.VkEscape);
                var exitChordHeld =
                    NativeMethods.IsKeyDown(NativeMethods.VkControl) &&
                    NativeMethods.IsKeyDown(NativeMethods.VkShift) &&
                    NativeMethods.IsKeyDown(NativeMethods.VkQ);

                if ((escapeHeld && !wasEscapeHeld) || (exitChordHeld && !wasExitChordHeld))
                {
                    _exitRequested = true;
                    continue;
                }

                if (activationHeld && !wasActivationHeld)
                {
                    visualWarningShown = false;
                    if (controller.TryBeginAtCursor(out var message))
                    {
                        Console.WriteLine(message);
                    }
                    else
                    {
                        Console.Error.WriteLine(message);
                    }
                }

                if (activationHeld && controller.IsActive)
                {
                    if (!NativeMethods.GetCursorPos(out var cursor))
                    {
                        if (!visualWarningShown)
                        {
                            Console.Error.WriteLine("无法读取鼠标位置。");
                            visualWarningShown = true;
                        }
                    }
                    else if (visualOverlay.IsVisible)
                    {
                        // 同一鼠标采样先提交完整视觉帧，再立即移动命中测试圆洞。
                        // 如果视觉帧未能提交，实体圆洞也保持原位，避免再次分裂成两个圆。
                        if (!visualOverlay.TryUpdate(cursor, out var visualError))
                        {
                            if (!visualWarningShown)
                            {
                                Console.Error.WriteLine($"视觉穿透暂不可用：{visualError}");
                                visualWarningShown = true;
                            }
                        }
                        else if (!controller.Update(cursor, out var error))
                        {
                            visualOverlay.Hide();
                            Console.Error.WriteLine(error);
                        }
                    }
                    else if (!controller.Update(cursor, out var error))
                    {
                        visualOverlay.Hide();
                        Console.Error.WriteLine(error);
                    }
                    else if (!TryUpdateVisualPortal(controller, visualOverlay, cursor, out var visualError) &&
                             !visualWarningShown)
                    {
                        Console.Error.WriteLine($"视觉穿透暂不可用：{visualError}");
                        visualWarningShown = true;
                    }
                }

                if (!activationHeld && wasActivationHeld && controller.IsActive)
                {
                    var restored = controller.ActiveDescription;
                    visualOverlay.Hide();
                    controller.Restore();
                    Console.WriteLine($"已恢复：{restored}");
                }

                wasActivationHeld = activationHeld;
                wasEscapeHeld = escapeHeld;
                wasExitChordHeld = exitChordHeld;

                // --poll-ms 表示完整轮询周期，而不是每帧处理完成后的额外延迟。
                var remainingMilliseconds =
                    pollMilliseconds - Stopwatch.GetElapsedTime(loopStartedAt).TotalMilliseconds;
                if (remainingMilliseconds >= 1)
                {
                    Thread.Sleep((int)Math.Floor(remainingMilliseconds));
                }
                else
                {
                    Thread.Yield();
                }
            }

            return 0;
        }
        finally
        {
            visualOverlay.Hide();
            controller.Restore();
            Console.WriteLine("窗口已恢复，WindowPortal 已退出。");
        }
    }

    private static int RunProbe(
        WindowRegionController controller,
        DwmPortalOverlay visualOverlay,
        nint window,
        int durationMilliseconds,
        int radius)
    {
        Console.WriteLine($"开始探测窗口 0x{window:X}。");
        Console.WriteLine($"前台核对：当前前台 HWND=0x{NativeMethods.GetForegroundWindow():X}。");
        var originalRegionType = WindowRegionController.ReadWindowRegionType(window);

        if (!controller.TryBegin(window, out var message))
        {
            Console.Error.WriteLine(message);
            return 3;
        }

        Console.WriteLine(message);
        var exitCode = 0;

        try
        {
            if (!NativeMethods.GetWindowRect(window, out var rect))
            {
                Console.Error.WriteLine("无法读取探测窗口尺寸。");
                exitCode = 3;
            }
            else
            {
                var center = new NativeMethods.Point(
                    rect.Left + (rect.Width / 2),
                    rect.Top + (rect.Height / 2));
                var movedCenter = new NativeMethods.Point(
                    rect.Left + ((rect.Width * 3) / 4),
                    rect.Top + (rect.Height / 2));

                if (!controller.Update(center, out var firstError))
                {
                    Console.Error.WriteLine(firstError);
                    exitCode = 3;
                }
                else
                {
                    if (!TryUpdateVisualPortal(controller, visualOverlay, center, out var visualError))
                    {
                        Console.Error.WriteLine($"视觉穿透探测失败：{visualError}");
                        exitCode = 6;
                    }

                    var firstInspection = controller.InspectCurrentHole(center);
                    Console.WriteLine(
                        $"中心探测：区域类型={firstInspection.RegionType}，圆心排除={firstInspection.CenterExcluded}：{firstInspection.Detail}");

                    if (!firstInspection.CenterExcluded)
                    {
                        exitCode = 4;
                    }

                    // 给诊断截图和非激活点击测试留出一个稳定的中心帧。
                    Thread.Sleep(500);
                }

                if (exitCode == 0)
                {
                    const int sweepFrameCount = 30;
                    var frameDurations = new List<double>(sweepFrameCount);
                    for (var frame = 1; frame <= sweepFrameCount; frame++)
                    {
                        var frameCenter = new NativeMethods.Point(
                            center.X + (((movedCenter.X - center.X) * frame) / sweepFrameCount),
                            center.Y + (((movedCenter.Y - center.Y) * frame) / sweepFrameCount));
                        var startedAt = Stopwatch.GetTimestamp();

                        if (!visualOverlay.TryUpdate(frameCenter, out var visualError))
                        {
                            Console.Error.WriteLine($"视觉穿透移动失败：{visualError}");
                            exitCode = 6;
                            break;
                        }

                        if (!controller.Update(frameCenter, out var moveError))
                        {
                            Console.Error.WriteLine(moveError);
                            exitCode = 3;
                            break;
                        }

                        frameDurations.Add(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                        Thread.Sleep(8);
                    }

                    if (frameDurations.Count > 0)
                    {
                        Console.WriteLine(
                            $"连续换帧：{frameDurations.Count} 帧，平均={frameDurations.Average():F2}ms，最慢={frameDurations.Max():F2}ms。"
                        );
                    }
                }

                if (exitCode == 0)
                {
                    var movedInspection = controller.InspectCurrentHole(movedCenter);
                    Console.WriteLine(
                        $"移动探测：区域类型={movedInspection.RegionType}，新圆心排除={movedInspection.CenterExcluded}：{movedInspection.Detail}");

                    if (!movedInspection.CenterExcluded)
                    {
                        exitCode = 4;
                    }

                    if (Math.Abs(movedCenter.X - center.X) > radius + 2)
                    {
                        var previousCenterInspection = controller.InspectCurrentHole(center);
                        var previousCenterRestored = !previousCenterInspection.CenterExcluded;
                        Console.WriteLine($"旧圆心重新纳入窗口区域={previousCenterRestored}。");
                        if (!previousCenterRestored)
                        {
                            exitCode = 4;
                        }
                    }

                    var hitChild = NativeMethods.WindowFromPoint(movedCenter);
                    var hitRoot = hitChild == nint.Zero
                        ? nint.Zero
                        : NativeMethods.GetAncestor(hitChild, NativeMethods.GaRoot);
                    NativeMethods.GetWindowThreadProcessId(hitRoot, out var hitProcessId);
                    var targetWindowIsExcluded = hitRoot != nint.Zero && hitRoot != window;
                    Console.WriteLine(
                        $"穿透命中：HWND=0x{hitRoot:X}，进程={hitProcessId}，已排除目标窗口={targetWindowIsExcluded}。"
                    );
                    if (!targetWindowIsExcluded)
                    {
                        exitCode = 7;
                    }
                }

                if (exitCode == 0)
                {
                    Thread.Sleep(durationMilliseconds);
                    Console.WriteLine(
                        $"前台焦点守卫：回滚次数={visualOverlay.ForegroundRecoveryCount}。"
                    );
                    Console.WriteLine(
                        $"受限层级提升：次数={visualOverlay.BackgroundPromotionCount}。"
                    );
                    Console.WriteLine(
                        $"多层合成：可渲染层数={visualOverlay.VisibleLayerCount}；" +
                        visualOverlay.CompatibilitySummary
                    );
                }
            }
        }
        finally
        {
            visualOverlay.Hide();
            controller.Restore();
            Console.WriteLine("探测结束，目标窗口已恢复。");
        }

        var restoredRegionType = WindowRegionController.ReadWindowRegionType(window);
        var regionTypeRestored = restoredRegionType == originalRegionType;
        Console.WriteLine(
            $"恢复核对：原始区域类型={originalRegionType}，恢复后区域类型={restoredRegionType}，一致={regionTypeRestored}。");

        return !regionTypeRestored && exitCode == 0 ? 5 : exitCode;
    }

    private static bool TryUpdateVisualPortal(
        WindowRegionController controller,
        DwmPortalOverlay visualOverlay,
        NativeMethods.Point screenPoint,
        out string? error)
    {
        if (visualOverlay.IsVisible)
        {
            return visualOverlay.TryUpdate(screenPoint, out error);
        }

        var sourceChild = NativeMethods.WindowFromPoint(screenPoint);
        var sourceWindow = sourceChild == nint.Zero
            ? nint.Zero
            : NativeMethods.GetAncestor(sourceChild, NativeMethods.GaRoot);

        if (sourceWindow == nint.Zero || sourceWindow == controller.ActiveWindow)
        {
            error = "没有识别到宿主窗口下方的可视源窗口。";
            return false;
        }

        var shown = visualOverlay.TryShow(
            sourceWindow,
            controller.ActiveWindow,
            screenPoint,
            out error);
        if (shown)
        {
            Console.WriteLine(
                $"多层合成已启用：可渲染层数={visualOverlay.VisibleLayerCount}；" +
                visualOverlay.CompatibilitySummary);
        }

        return shown;
    }

    private static void RegisterEmergencyRestoration(
        WindowRegionController controller,
        DwmPortalOverlay visualOverlay)
    {
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            visualOverlay.Hide();
            controller.Restore();
            _exitRequested = true;
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            visualOverlay.Hide();
            controller.Restore();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            visualOverlay.Hide();
            controller.Restore();
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            WindowPortal - Windows 圆形窗口打孔技术验证

            用法：
              WindowPortal [--radius <像素>] [--poll-ms <毫秒>]
              WindowPortal --probe-hwnd <句柄> [--probe-duration-ms <毫秒>] [--radius <像素>]
              WindowPortal --self-test
              WindowPortal --list-windows
              WindowPortal --compatibility-report
              WindowPortal --version
              WindowPortal --inspect-hwnd <句柄> [--inspect-point <屏幕X> <屏幕Y>]

            交互控制：
              按住 F8              创建并移动圆洞
              松开 F8              恢复目标窗口
              Esc / Ctrl+Shift+Q   恢复并退出

            参数：
              --radius              圆洞半径，默认 180，范围 32..2000
              --poll-ms             鼠标轮询间隔，默认 16，范围 4..1000
              --probe-hwnd          对指定十进制或 0x 十六进制 HWND 做短暂探测
              --probe-duration-ms   探测持续时间，默认 1500
              --self-test           运行无需桌面窗口的纯逻辑自检
              --list-windows        列出可见顶层窗口、进程、类名和 HWND
              --compatibility-report 只读评估当前窗口的视觉、交互与安全兼容性
              --version             输出语义化版本号
              --inspect-hwnd        输出目标窗口的父子、所有者和 Z-order 诊断
              --inspect-point       指定诊断使用的屏幕坐标；默认使用窗口中心
            """);
    }
}
