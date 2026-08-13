using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace WindowPortal;

internal static class Program
{
    private const string SingleInstanceName =
        "Local\\PierceView.SingleInstance.2D9EE2AF-9F96-4C79-9F48-12E087B8A1A4";

    [STAThread]
    private static int Main(string[] args)
    {
        ConfigureRedirectedConsole();
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
            Console.WriteLine($"PierceView {GetProductVersion()}");
            return 0;
        }

        if (options.SelfTest)
        {
            return SelfTests.Run();
        }

        if (options.VisualSmoke)
        {
            return VisualSmokeTests.Run(options.Radius);
        }

        if (options.GpuProbe)
        {
            return GpuCapabilityProbe.Run();
        }

        if (options.GpuSmokeWindow is { } gpuSmokeWindow)
        {
            return GpuCaptureSmokeTests.Run(
                gpuSmokeWindow,
                options.ProbeDurationMilliseconds);
        }

        if (options.GpuPortalSmokeWindow is { } gpuPortalSmokeWindow)
        {
            return GpuPortalSmokeTests.Run(
                gpuPortalSmokeWindow,
                options.ProbeDurationMilliseconds);
        }

        if (options.ListWindows)
        {
            return WindowInventory.PrintVisibleWindows();
        }

        if (options.InspectWindow is { } inspectWindow)
        {
            return WindowHierarchyInspector.Inspect(inspectWindow, options.InspectPoint);
        }

        if (options.ProbeWindow is { } probeWindow)
        {
            return RunProbeMode(options, probeWindow);
        }

        return RunTrayApplication(options);
    }

    private static int RunTrayApplication(PortalOptions options)
    {
        var settingsPath = Environment.GetEnvironmentVariable("PIERCEVIEW_SETTINGS_PATH");
        var settingsStore = new UserSettingsStore(
            string.IsNullOrWhiteSpace(settingsPath) ? null : settingsPath);
        var firstRun = !settingsStore.Exists;
        var settings = settingsStore.Load();
        if (options.RadiusWasSpecified)
        {
            settings = settings with { Radius = options.Radius };
        }

        using var mutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceName,
            out var ownsMutex);
        if (!ownsMutex)
        {
            var text = Localizer.Get(settings.Language);
            MessageBox.Show(
                text.AlreadyRunning,
                "寸镜 / PierceView",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        try
        {
            _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var context = new PierceViewApplicationContext(
                settingsStore,
                settings,
                firstRun,
                options.PollMilliseconds,
                options.TraySmokeTestMilliseconds);
            Application.Run(context);
            return 0;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static int RunProbeMode(PortalOptions options, nint probeWindow)
    {
        using var controller = new WindowRegionController(options.Radius);
        using var visualOverlay = new AdaptivePortalOverlay(
            PortalGeometry.Circle(options.Radius));
        RegisterEmergencyRestoration(controller, visualOverlay);
        return RunProbe(
            controller,
            visualOverlay,
            probeWindow,
            options.ProbeDurationMilliseconds,
            options.Radius);
    }

    private static int RunProbe(
        WindowRegionController controller,
        AdaptivePortalOverlay visualOverlay,
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
        var result = 0;
        try
        {
            if (!NativeMethods.GetWindowRect(window, out var rect))
            {
                Console.Error.WriteLine("无法读取探测窗口尺寸。");
                return 3;
            }

            var center = new NativeMethods.Point(
                rect.Left + rect.Width / 2,
                rect.Top + rect.Height / 2);
            var destination = new NativeMethods.Point(
                rect.Left + rect.Width * 3 / 4,
                rect.Top + rect.Height / 2);

            if (!controller.Update(center, out var regionError))
            {
                Console.Error.WriteLine(regionError);
                return 3;
            }

            if (!TryUpdateVisualPortal(controller, visualOverlay, center, out var visualError))
            {
                Console.Error.WriteLine("视觉穿透探测失败：" + visualError);
                result = 6;
            }

            var centerInspection = controller.InspectCurrentHole(center);
            Console.WriteLine(
                $"中心探测：区域类型={centerInspection.RegionType}，" +
                $"圆心排除={centerInspection.CenterExcluded}：{centerInspection.Detail}");
            if (!centerInspection.CenterExcluded)
            {
                result = 4;
            }

            Thread.Sleep(500);

            if (result == 0)
            {
                var frameTimes = new List<double>(30);
                for (var frame = 1; frame <= 30; frame++)
                {
                    var point = new NativeMethods.Point(
                        center.X + (destination.X - center.X) * frame / 30,
                        center.Y + (destination.Y - center.Y) * frame / 30);
                    var frameStartedAt = Stopwatch.GetTimestamp();
                    if (!visualOverlay.TryUpdate(point, out var updateError))
                    {
                        Console.Error.WriteLine("视觉穿透移动失败：" + updateError);
                        result = 6;
                        break;
                    }

                    if (!controller.Update(point, out var moveError))
                    {
                        Console.Error.WriteLine(moveError);
                        result = 3;
                        break;
                    }

                    frameTimes.Add(Stopwatch.GetElapsedTime(frameStartedAt).TotalMilliseconds);
                    Thread.Sleep(8);
                }

                if (frameTimes.Count > 0)
                {
                    Console.WriteLine(
                        $"连续换帧：{frameTimes.Count} 帧，" +
                        $"平均={frameTimes.Average():F2}ms，最慢={frameTimes.Max():F2}ms。");
                    Console.WriteLine(
                        $"视觉后端={visualOverlay.ActiveBackendName}，" +
                        $"顶层显示定位次数={visualOverlay.VisualPlacementCount}。");
                }
            }

            if (result == 0)
            {
                var movedInspection = controller.InspectCurrentHole(destination);
                Console.WriteLine(
                    $"移动探测：区域类型={movedInspection.RegionType}，" +
                    $"新圆心排除={movedInspection.CenterExcluded}：{movedInspection.Detail}");
                if (!movedInspection.CenterExcluded)
                {
                    result = 4;
                }

                if (Math.Abs(destination.X - center.X) > radius + 2)
                {
                    var oldCenterRestored = !controller.InspectCurrentHole(center).CenterExcluded;
                    Console.WriteLine($"旧圆心重新纳入窗口区域={oldCenterRestored}。");
                    if (!oldCenterRestored)
                    {
                        result = 4;
                    }
                }

                var hit = NativeMethods.WindowFromPoint(destination);
                var hitRoot = hit == nint.Zero
                    ? nint.Zero
                    : NativeMethods.GetAncestor(hit, NativeMethods.GaRoot);
                NativeMethods.GetWindowThreadProcessId(hitRoot, out var processId);
                var hitBackground = hitRoot != nint.Zero && hitRoot != window;
                Console.WriteLine(
                    $"穿透命中：HWND=0x{hitRoot:X}，进程={processId}，" +
                    $"已排除目标窗口={hitBackground}。");
                if (!hitBackground)
                {
                    result = 7;
                }
            }

            if (result == 0)
            {
                Thread.Sleep(durationMilliseconds);
                Console.WriteLine($"前台焦点守卫：回滚次数={visualOverlay.ForegroundRecoveryCount}。");
            }
        }
        finally
        {
            visualOverlay.Hide();
            controller.Restore();
            Console.WriteLine("探测结束，目标窗口已恢复。");
        }

        var restoredRegionType = WindowRegionController.ReadWindowRegionType(window);
        var restored = restoredRegionType == originalRegionType;
        Console.WriteLine(
            $"恢复核对：原始区域类型={originalRegionType}，" +
            $"恢复后区域类型={restoredRegionType}，一致={restored}。");
        return restored || result != 0 ? result : 5;
    }

    private static bool TryUpdateVisualPortal(
        WindowRegionController controller,
        AdaptivePortalOverlay visualOverlay,
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
            error = "没有识别到宿主窗口下方的单层视觉来源。";
            return false;
        }

        return visualOverlay.TryShow(
            sourceWindow,
            controller.ActiveWindow,
            screenPoint,
            out error);
    }

    private static void RegisterEmergencyRestoration(
        WindowRegionController controller,
        AdaptivePortalOverlay visualOverlay)
    {
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            visualOverlay.Hide();
            controller.Restore();
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

    private static string GetProductVersion()
    {
        var attribute = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attribute?.InformationalVersion ?? "unknown";
    }

    private static void ConfigureRedirectedConsole()
    {
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var standardOutput = Console.OpenStandardOutput();
            if (!ReferenceEquals(standardOutput, Stream.Null))
            {
                Console.SetOut(new StreamWriter(standardOutput, utf8) { AutoFlush = true });
            }

            var standardError = Console.OpenStandardError();
            if (!ReferenceEquals(standardError, Stream.Null))
            {
                Console.SetError(new StreamWriter(standardError, utf8) { AutoFlush = true });
            }
        }
        catch (IOException)
        {
            // A normal GUI-subsystem launch has no console streams.
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            "寸镜 / PierceView - Windows 单层窗口透视托盘工具\n\n" +
            "用法：\n" +
            "  PierceView [--radius <像素>] [--poll-ms <毫秒>]\n" +
            "  PierceView --probe-hwnd <句柄> [--probe-duration-ms <毫秒>] [--radius <像素>]\n" +
            "  PierceView --self-test\n" +
            "  PierceView --visual-smoke [--radius <像素>]\n" +
            "  PierceView --gpu-probe\n" +
            "  PierceView --gpu-smoke-hwnd <句柄> [--probe-duration-ms <毫秒>]\n" +
            "  PierceView --gpu-portal-smoke-hwnd <句柄> [--probe-duration-ms <毫秒>]\n" +
            "  PierceView --list-windows\n" +
            "  PierceView --inspect-hwnd <句柄> [--inspect-point <屏幕X> <屏幕Y>]\n" +
            "  PierceView --version\n\n" +
            "普通运行：\n" +
            "  启动后只进入系统托盘。按住 F8 开启透视，松开恢复。\n" +
            "  从托盘菜单启动/暂停、设置、查看帮助或退出。\n\n" +
            "参数：\n" +
            "  --radius              圆半径，默认 180，范围 64..400\n" +
            "  --poll-ms             鼠标轮询间隔，默认 16，范围 8..100\n" +
            "  --probe-hwnd          对指定十进制或 0x 十六进制 HWND 做短暂探测\n" +
            "  --probe-duration-ms   探测持续时间，默认 1500\n" +
            "  --self-test           运行无需桌面窗口的纯逻辑自检\n" +
            "  --visual-smoke        自动视觉冒烟（采样圆形、矩形、羽化与闪黑回归）\n" +
            "  --gpu-probe           检测 WGC、硬件 D3D11、DirectComposition 与当前刷新率\n" +
            "  --gpu-smoke-hwnd      将指定 HWND 的 WGC 帧显示在 GPU 硬边矩形测试窗\n" +
            "  --gpu-portal-smoke-hwnd  验证 GPU 常驻纹理、移动裁剪、圆角与羽化\n" +
            "  --list-windows        列出可见顶层窗口、进程、类名和 HWND\n" +
            "  --inspect-hwnd        输出目标窗口的父子、所有者和 Z-order 诊断\n" +
            "  --inspect-point       指定诊断坐标；默认使用窗口中心");
    }
}
