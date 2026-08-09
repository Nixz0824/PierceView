namespace WindowPortal;

internal static class SelfTests
{
    internal static int Run()
    {
        var failures = new List<string>();

        Check(
            "默认参数",
            () =>
            {
                var options = PortalOptions.Parse([]);
                return options.Radius == 180 && options.PollMilliseconds == 16;
            },
            failures);

        Check(
            "参数覆盖",
            () =>
            {
                var options = PortalOptions.Parse(["--radius", "240", "--poll-ms", "8"]);
                return options.Radius == 240 && options.PollMilliseconds == 8;
            },
            failures);

        Check(
            "十六进制窗口句柄",
            () => PortalOptions.ParseWindowHandle("0x1234") == (nint)0x1234,
            failures);

        Check(
            "坐标转换",
            () =>
            {
                var local = WindowRegionController.ToWindowCoordinates(
                    new NativeMethods.Rect(100, 200, 1100, 1000),
                    new NativeMethods.Point(600, 600));
                return local == new NativeMethods.Point(500, 400);
            },
            failures);

        Check(
            "圆形边界",
            () =>
            {
                var bounds = WindowRegionController.CreateHoleBounds(new NativeMethods.Point(500, 400), 180);
                return bounds == new NativeMethods.Rect(320, 220, 681, 581);
            },
            failures);

        Check(
            "非法半径被拒绝",
            () => Throws<ArgumentException>(() => PortalOptions.Parse(["--radius", "0"])),
            failures);

        Check(
            "标准 DWM 窗口允许合成与交互",
            () =>
            {
                var decision = CompatibilityPolicy.EvaluateProcessNameForTests("notepad");
                return decision.IsSupported &&
                    decision.AllowVisualPreview &&
                    decision.AllowInteraction;
            },
            failures);

        Check(
            "反作弊相关客户端默认受保护",
            () =>
            {
                var decision = CompatibilityPolicy.EvaluateProcessNameForTests("LeagueClientUx");
                return decision.Kind == WindowCompatibilityKind.Protected &&
                    !decision.AllowVisualPreview &&
                    !decision.AllowInteraction;
            },
            failures);

        Check(
            "带空格的 Riot Client 进程名仍受保护",
            () =>
            {
                var decision = CompatibilityPolicy.EvaluateProcessNameForTests("Riot Client");
                return decision.Kind == WindowCompatibilityKind.Protected &&
                    !decision.AllowVisualPreview &&
                    !decision.AllowInteraction;
            },
            failures);

        Check(
            "无重定向表面只禁用视觉预览",
            () =>
            {
                var decision = CompatibilityPolicy.EvaluateProcessNameForTests(
                    "CustomRenderer",
                    0x00200000);
                return decision.Kind == WindowCompatibilityKind.VisualUnsupported &&
                    !decision.AllowVisualPreview &&
                    decision.AllowInteraction;
            },
            failures);

        if (failures.Count == 0)
        {
            Console.WriteLine("自检通过：10/10。");
            return 0;
        }

        Console.Error.WriteLine($"自检失败：{failures.Count} 项。");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"- {failure}");
        }

        return 1;
    }

    private static void Check(string name, Func<bool> assertion, ICollection<string> failures)
    {
        try
        {
            if (!assertion())
            {
                failures.Add(name);
            }
        }
        catch (Exception exception)
        {
            failures.Add($"{name}：{exception.Message}");
        }
    }

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
