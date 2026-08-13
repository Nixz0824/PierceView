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
            return RunProbeMode(
                options,
                probeWindow,
                options.MultilayerProbe || options.PromotionProbe,
                options.PromotionProbe);
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

    private static int RunProbeMode(
        PortalOptions options,
        nint probeWindow,
        bool multilayerProbe,
        bool promotionProbe)
    {
        var geometry = PortalGeometry.Rectangle(
            UserSettings.DefaultRectangleWidth,
            UserSettings.DefaultRectangleHeight,
            UserSettings.DefaultFeatherWidth);
        using var controller = new WindowRegionController(geometry);
        using var visualOverlay = new AdaptivePortalOverlay(
            geometry);
        RegisterEmergencyRestoration(controller, visualOverlay);
        return RunProbe(
            controller,
            visualOverlay,
            geometry,
            probeWindow,
            options.ProbeDurationMilliseconds,
            options.Radius,
            movePortal: !multilayerProbe,
            reportPromotion: promotionProbe);
    }

    private static int RunProbe(
        WindowRegionController controller,
        AdaptivePortalOverlay visualOverlay,
        PortalGeometry geometry,
        nint window,
        int durationMilliseconds,
        int radius,
        bool movePortal,
        bool reportPromotion)
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

            if (!TryUpdateVisualPortal(
                    controller,
                    visualOverlay,
                    geometry,
                    center,
                    out var visualError))
            {
                Console.Error.WriteLine("视觉穿透探测失败：" + visualError);
                result = 6;
            }

            if (result == 0 &&
                !WaitForVisualReadiness(
                    visualOverlay,
                    center,
                    TimeSpan.FromSeconds(3),
                    out visualError))
            {
                Console.Error.WriteLine("视觉穿透就绪失败：" + visualError);
                result = 6;
            }

            Console.WriteLine(
                $"视觉就绪：后端={visualOverlay.ActiveBackendName}，" +
                $"多层合成已启用：可渲染层数={visualOverlay.SourceCount}，" +
                $"首帧已提交={visualOverlay.HasPresentedFrame}，" +
                $"来源HWND=0x{visualOverlay.SourceWindow:X}，" +
                $"来源非激活={visualOverlay.IsSourceNoActivateApplied}。");

            var centerInspection = controller.InspectCurrentHole(center);
            Console.WriteLine(
                $"中心探测：区域类型={centerInspection.RegionType}，" +
                $"圆心排除={centerInspection.CenterExcluded}：{centerInspection.Detail}");
            if (!centerInspection.CenterExcluded)
            {
                result = 4;
            }

            if (movePortal)
            {
                Thread.Sleep(500);
            }

            if (result == 0 && movePortal)
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

            if (result == 0 && movePortal)
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

            if (result == 0 && !movePortal)
            {
                var frameTimes = new List<double>(100);
                var wasPrimaryButtonHeld = false;
                for (var frame = 0; frame < 100; frame++)
                {
                    var updatePoint = reportPromotion &&
                                      NativeMethods.GetCursorPos(out var currentCursor)
                        ? currentCursor
                        : center;
                    var frameStartedAt = Stopwatch.GetTimestamp();
                    if (!visualOverlay.TryUpdate(updatePoint, out var updateError))
                    {
                        Console.Error.WriteLine("多层固定位置刷新失败：" + updateError);
                        result = 6;
                        break;
                    }

                    var committedCenter = visualOverlay.LastPresentedCenter ?? updatePoint;
                    if (!controller.Update(committedCenter, out var followRegionError))
                    {
                        Console.Error.WriteLine("多层固定位置交互孔刷新失败：" + followRegionError);
                        result = 3;
                        break;
                    }

                    var primaryButtonState = NativeMethods.GetAsyncKeyState(
                        NativeMethods.VkLButton);
                    var primaryButtonHeld = (primaryButtonState & 0x8000) != 0;
                    var primaryButtonPressed =
                        (primaryButtonState & 0x0001) != 0 ||
                        (primaryButtonHeld && !wasPrimaryButtonHeld);
                    if (reportPromotion && primaryButtonPressed)
                    {
                        TryPromoteWindowAtPoint(
                            visualOverlay,
                            updatePoint);
                    }

                    wasPrimaryButtonHeld = primaryButtonHeld;

                    frameTimes.Add(
                        Stopwatch.GetElapsedTime(frameStartedAt).TotalMilliseconds);
                    Thread.Sleep(8);
                }

                if (frameTimes.Count > 0)
                {
                    Console.WriteLine(
                        $"固定多层换帧：{frameTimes.Count} 帧，" +
                        $"平均={frameTimes.Average():F2}ms，最慢={frameTimes.Max():F2}ms。");
                }
            }

            if (result == 0)
            {
                var keepAliveDeadline = Stopwatch.GetTimestamp() +
                    (long)(durationMilliseconds / 1000d * Stopwatch.Frequency);
                while (!movePortal &&
                       Stopwatch.GetTimestamp() < keepAliveDeadline)
                {
                    if (!visualOverlay.TryUpdate(center, out var updateError))
                    {
                        Console.Error.WriteLine(
                            "动态来源保持刷新失败：" + updateError);
                        result = 6;
                        break;
                    }

                    var committedCenter = visualOverlay.LastPresentedCenter ?? center;
                    if (!controller.Update(
                            committedCenter,
                            out var keepAliveRegionError))
                    {
                        Console.Error.WriteLine(
                            "动态来源交互孔刷新失败：" + keepAliveRegionError);
                        result = 3;
                        break;
                    }

                    Thread.Sleep(8);
                }

                if (movePortal)
                {
                    Thread.Sleep(durationMilliseconds);
                }

                Console.WriteLine($"前台焦点守卫：回滚次数={visualOverlay.ForegroundRecoveryCount}。");
                Console.WriteLine(
                    $"前台快速钳制：次数={visualOverlay.ImmediateForegroundClampCount}。");
                if (reportPromotion)
                {
                    Console.WriteLine(
                        $"受限层级提升：次数={visualOverlay.BackgroundPromotionCount}。");
                    Console.WriteLine(
                        $"视觉/输入层级同步：{visualOverlay.IsPhysicalSourceOrderSynchronized}，" +
                        $"真实 -1 HWND=0x{visualOverlay.PhysicallySelectedSourceWindow:X}。");
                    Console.WriteLine(
                        $"物理后台顺序恢复：次数={visualOverlay.PhysicalOrderRecoveryCount}。");
                }

                Console.WriteLine(
                    $"动态来源协调：次数={visualOverlay.SourceReconciliationCount}，" +
                    $"新建捕获={visualOverlay.SourceReplacementCount}，" +
                    $"保帧重试={visualOverlay.SourceReconciliationRetryCount}，" +
                    $"已隔离帧异常={visualOverlay.RecoverableCaptureFailureCount}，" +
                    $"已隔离更新异常={visualOverlay.RecoverableUpdateFailureCount}，" +
                    $"显示定位={visualOverlay.VisualPlacementCount}，" +
                    $"显示层级恢复={visualOverlay.DisplayZOrderRecoveryCount}，" +
                    $"显示层位于宿主上方={visualOverlay.IsDisplayAboveProtected}，" +
                    "最终来源=" + string.Join(
                        ',',
                        visualOverlay.SourceWindows.Select(
                            source => $"0x{source:X}")) + "。");
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
        PortalGeometry geometry,
        NativeMethods.Point screenPoint,
        out string? error)
    {
        if (visualOverlay.IsVisible)
        {
            return visualOverlay.TryUpdate(screenPoint, out error);
        }

        var sources = MultilayerWindowResolver.Resolve(
            controller.ActiveWindow,
            geometry.CreateFrameBounds(screenPoint));
        if (sources.Count == 0)
        {
            error = "没有识别到宿主窗口后方与矩形透视区域相交的前四层窗口。";
            return false;
        }

        return visualOverlay.TryShow(
            sources,
            controller.ActiveWindow,
            screenPoint,
            out error);
    }

    private static void TryPromoteWindowAtPoint(
        AdaptivePortalOverlay visualOverlay,
        NativeMethods.Point screenPoint)
    {
        var hitWindow = NativeMethods.WindowFromPoint(screenPoint);
        var hitRoot = hitWindow == nint.Zero
            ? nint.Zero
            : NativeMethods.GetAncestor(hitWindow, NativeMethods.GaRoot);
        if (hitRoot == nint.Zero || !visualOverlay.ContainsSourceWindow(hitRoot))
        {
            return;
        }

        _ = visualOverlay.TryPromoteSource(hitRoot, out _);
    }

    private static bool WaitForVisualReadiness(
        AdaptivePortalOverlay visualOverlay,
        NativeMethods.Point screenPoint,
        TimeSpan timeout,
        out string? error)
    {
        var deadline = Stopwatch.GetTimestamp() +
                       (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (!visualOverlay.TryUpdate(screenPoint, out error))
            {
                return false;
            }

            if (visualOverlay.HasPresentedFrame &&
                visualOverlay.IsSourceNoActivateApplied)
            {
                error = null;
                return true;
            }

            Thread.Sleep(10);
        }

        error =
            $"等待首帧或来源窗口非激活样式超时（后端={visualOverlay.ActiveBackendName}，" +
            $"首帧={visualOverlay.HasPresentedFrame}，" +
            $"来源HWND=0x{visualOverlay.SourceWindow:X}，" +
            $"非激活={visualOverlay.IsSourceNoActivateApplied}）。";
        return false;
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
            "寸镜 / PierceView - Windows 多层窗口透视工具\n\n" +
            "用法：\n" +
            "  PierceView [--radius <像素>] [--poll-ms <毫秒>]\n" +
            "  PierceView --probe-hwnd <句柄> [--probe-duration-ms <毫秒>] [--radius <像素>]\n" +
            "  PierceView --multilayer-probe-hwnd <句柄> [--probe-duration-ms <毫秒>]\n" +
            "  PierceView --promotion-probe-hwnd <句柄> [--probe-duration-ms <毫秒>]\n" +
            "  PierceView --self-test\n" +
            "  PierceView --visual-smoke [--radius <像素>]\n" +
            "  PierceView --gpu-probe\n" +
            "  PierceView --gpu-smoke-hwnd <句柄> [--probe-duration-ms <毫秒>]\n" +
            "  PierceView --gpu-portal-smoke-hwnd <句柄> [--probe-duration-ms <毫秒>]\n" +
            "  PierceView --list-windows\n" +
            "  PierceView --inspect-hwnd <句柄> [--inspect-point <屏幕X> <屏幕Y>]\n" +
            "  PierceView --version\n\n" +
            "普通运行：\n" +
            "  启动后只进入系统托盘。按住 F8 开启矩形透视，松开恢复。\n" +
            "  2.3 GPU 版本最多识别宿主后方 -1 到 -4 四层；超过 -4 不识别。\n" +
            "  从托盘菜单启动/暂停、设置、查看帮助或退出。\n\n" +
            "参数：\n" +
            "  --radius              圆半径，默认 180，范围 64..400\n" +
            "  --poll-ms             鼠标轮询间隔，默认 16，范围 8..100\n" +
            "  --probe-hwnd          对指定十进制或 0x 十六进制 HWND 做短暂探测\n" +
            "  --multilayer-probe-hwnd  固定矩形位置验证最多四层同时合成\n" +
            "  --promotion-probe-hwnd  固定矩形位置验证深层点击受限提升\n" +
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
