namespace WindowPortal;

internal static class SelfTests
{
    internal static int Run()
    {
        var failures = new List<string>();

        Check("默认参数", () =>
        {
            var options = PortalOptions.Parse([]);
            return options.Radius == 180 && options.PollMilliseconds == 16;
        }, failures);

        Check("参数覆盖", () =>
        {
            var options = PortalOptions.Parse(
                ["--radius", "240", "--poll-ms", "8"]);
            return options.Radius == 240 && options.PollMilliseconds == 8;
        }, failures);

        Check(
            "十六进制窗口句柄",
            () => PortalOptions.ParseWindowHandle("0x1234") == 0x1234,
            failures);

        Check(
            "坐标转换",
            () => WindowRegionController.ToWindowCoordinates(
                    new NativeMethods.Rect(100, 200, 1100, 1000),
                    new NativeMethods.Point(600, 600)) ==
                new NativeMethods.Point(500, 400),
            failures);

        Check(
            "圆形边界",
            () => WindowRegionController.CreateHoleBounds(
                    new NativeMethods.Point(500, 400),
                    180) ==
                new NativeMethods.Rect(320, 220, 681, 581),
            failures);

        Check(
            "非法半径被拒绝",
            () => Throws<ArgumentException>(
                () => PortalOptions.Parse(["--radius", "0"])),
            failures);

        Check("设置规范化", () =>
        {
            var normalized = new UserSettings(9999, "invalid").Normalize();
            return normalized.Radius == UserSettings.MaximumRadius &&
                   normalized.Language == Localizer.Chinese;
        }, failures);

        Check(
            "中英文切换",
            () => Localizer.Get(Localizer.Chinese).HelpBody.Contains("F8") &&
                  Localizer.Get(Localizer.English).HelpBody.Contains("F8") &&
                  Localizer.NormalizeLanguage("EN-us") == Localizer.English,
            failures);

        Check("托盘冒烟测试参数", () =>
        {
            var options = PortalOptions.Parse(["--tray-smoke-test-ms", "750"]);
            return options.TraySmokeTestMilliseconds == 750;
        }, failures);

        Check("品牌资源与最小设置窗口", () =>
        {
            using var logo = BrandResources.LoadLogoBitmap();
            using var icon = BrandResources.LoadApplicationIcon();
            using var form = new SettingsForm(
                UserSettings.CreateDefault(),
                icon,
                _ => true);
            var controls = EnumerateControls(form).ToArray();
            return logo is { Width: > 0, Height: > 0 } &&
                   form.Text.Contains("寸镜", StringComparison.Ordinal) &&
                   controls.OfType<NumericUpDown>().Count() == 1 &&
                   controls.OfType<ComboBox>().Count() == 1 &&
                   controls.OfType<Button>().Count() == 2;
        }, failures);

        Check("设置保存与读取", () => WithTemporaryStore((store, _) =>
        {
            var expected = new UserSettings(230, Localizer.English);
            store.Save(expected);
            return store.Exists && store.Load() == expected;
        }), failures);

        Check("损坏设置安全回退", () => WithTemporaryStore((store, path) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{not-json");
            return store.Load() == UserSettings.CreateDefault();
        }), failures);

        if (failures.Count == 0)
        {
            Console.WriteLine("自检通过：12/12。");
            return 0;
        }

        Console.Error.WriteLine($"自检失败：{failures.Count} 项。");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("- " + failure);
        }

        return 1;
    }

    private static bool WithTemporaryStore(
        Func<UserSettingsStore, string, bool> assertion)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PierceView.SelfTest",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            return assertion(new UserSettingsStore(path), path);
        }
        finally
        {
            TryDelete(Path.Combine(directory, "settings.json.tmp"));
            TryDelete(path);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent is not null &&
                Directory.Exists(parent) &&
                !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<Control> EnumerateControls(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var descendant in EnumerateControls(control))
            {
                yield return descendant;
            }
        }
    }

    private static void Check(
        string name,
        Func<bool> assertion,
        ICollection<string> failures)
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
            failures.Add(name + "：" + exception.Message);
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
