namespace WindowPortal;

internal static class SelfTests
{
    internal static int Run()
    {
        var failures = new List<string>();
        var total = 0;

        Check("默认参数", () =>
        {
            var options = PortalOptions.Parse([]);
            return options.Radius == 180 && options.PollMilliseconds == 16;
        }, failures, ref total);

        Check("参数覆盖", () =>
        {
            var options = PortalOptions.Parse(
                ["--radius", "240", "--poll-ms", "8"]);
            return options.Radius == 240 && options.PollMilliseconds == 8;
        }, failures, ref total);

        Check("GPU 探针独占模式", () =>
        {
            var options = PortalOptions.Parse(["--gpu-probe"]);
            return options.GpuProbe &&
                   Throws<ArgumentException>(() =>
                       PortalOptions.Parse(["--gpu-probe", "--self-test"]));
        }, failures, ref total);

        Check("GPU 闭环测试参数", () =>
        {
            var options = PortalOptions.Parse(
                ["--gpu-smoke-hwnd", "0x1234", "--probe-duration-ms", "900"]);
            return options.GpuSmokeWindow == 0x1234 &&
                   options.ProbeDurationMilliseconds == 900;
        }, failures, ref total);

        Check("GPU 透视渲染测试参数", () =>
        {
            var options = PortalOptions.Parse(
                ["--gpu-portal-smoke-hwnd", "4660"]);
            return options.GpuPortalSmokeWindow == 0x1234;
        }, failures, ref total);

        Check(
            "十六进制窗口句柄",
            () => PortalOptions.ParseWindowHandle("0x1234") == 0x1234,
            failures,
            ref total);

        Check(
            "坐标转换",
            () => WindowRegionController.ToWindowCoordinates(
                    new NativeMethods.Rect(100, 200, 1100, 1000),
                    new NativeMethods.Point(600, 600)) ==
                new NativeMethods.Point(500, 400),
            failures,
            ref total);

        Check(
            "圆形边界",
            () => WindowRegionController.CreateHoleBounds(
                    new NativeMethods.Point(500, 400),
                    180) ==
                new NativeMethods.Rect(320, 220, 681, 581),
            failures,
            ref total);

        Check("圆形与矩形几何", () =>
        {
            var center = new NativeMethods.Point(500, 400);
            var circle = PortalGeometry.Circle(180);
            var featheredCircle = PortalGeometry.Circle(204, 24);
            var rectangle = PortalGeometry.Rectangle(420, 280);
            var featheredRectangle = PortalGeometry.Rectangle(420, 280, 24);
            return circle.CreateFrameBounds(center) ==
                       new NativeMethods.Rect(320, 220, 681, 581) &&
                   circle.CreateHitBounds(center) ==
                       new NativeMethods.Rect(320, 220, 681, 581) &&
                   circle.EffectiveHitRadius == 180 &&
                   circle.EffectiveInteractionRadius == 32 &&
				   circle.InteractionReanchorDistance == 16 &&
                   featheredCircle.CreateFrameBounds(center) ==
                       new NativeMethods.Rect(296, 196, 705, 605) &&
                   featheredCircle.CreateHitBounds(center) ==
                       new NativeMethods.Rect(320, 220, 681, 581) &&
                   featheredCircle.EffectiveHitRadius == 180 &&
                   featheredCircle.EffectiveInteractionRadius == 32 &&
                   rectangle.CreateFrameBounds(center) ==
                       new NativeMethods.Rect(290, 260, 710, 540) &&
                   rectangle.CreateHitBounds(center) ==
                       new NativeMethods.Rect(290, 260, 710, 540) &&
                   rectangle.EffectiveCornerRadius == 46 &&
                   rectangle.EffectiveHitCornerRadius == 46 &&
                   featheredRectangle.CreateFrameBounds(center) ==
                       new NativeMethods.Rect(290, 260, 710, 540) &&
                   featheredRectangle.CreateHitBounds(center) ==
                       new NativeMethods.Rect(314, 284, 686, 516) &&
                   featheredRectangle.EffectiveCornerRadius == 46 &&
                   featheredRectangle.EffectiveHitCornerRadius == 22 &&
                   rectangle.EffectiveInteractionRadius == 32 &&
                   featheredRectangle.EffectiveInteractionRadius == 32;
        }, failures, ref total);

        Check("鼠标中心锚定交互孔", () =>
        {
            var geometry = PortalGeometry.Rectangle(420, 280, 24);
            var center = new NativeMethods.Point(210, 140);
            var bounds = WindowRegionController.CreateHoleBounds(
                center,
                geometry.EffectiveInteractionRadius);
            var region = NativeMethods.CreateEllipticRgn(
                bounds.Left,
                bounds.Top,
                bounds.Right,
                bounds.Bottom);
            if (region == nint.Zero)
            {
                return false;
            }

            try
            {
                return NativeMethods.PtInRegion(region, center.X, center.Y) &&
                       NativeMethods.PtInRegion(
                           region,
                           center.X + geometry.EffectiveInteractionRadius - 2,
                           center.Y) &&
                       !NativeMethods.PtInRegion(
                           region,
                           center.X + geometry.EffectiveInteractionRadius + 2,
                           center.Y);
            }
            finally
            {
                _ = NativeMethods.DeleteObject(region);
            }
        }, failures, ref total);

		Check(
			"交互孔锚定阈值",
			() => !WindowRegionController.ShouldReanchorAperture(
				      new NativeMethods.Point(100, 100),
				      new NativeMethods.Point(115, 100),
				      16) &&
			      WindowRegionController.ShouldReanchorAperture(
				      new NativeMethods.Point(100, 100),
				      new NativeMethods.Point(116, 100),
				      16),
			failures,
			ref total);

        Check(
            "非法半径被拒绝",
            () => Throws<ArgumentException>(
                () => PortalOptions.Parse(["--radius", "0"])),
            failures,
            ref total);

        Check("设置规范化", () =>
        {
            var normalized = new UserSettings(9999, "invalid").Normalize();
            return normalized.Radius == UserSettings.MaximumRadius &&
                   normalized.Language == Localizer.Chinese;
        }, failures, ref total);

        Check("新安装默认矩形", () =>
        {
            var defaults = UserSettings.CreateDefault().Normalize();
            var geometry = defaults.CreateGeometry();
            return defaults.PortalMode == UserSettings.RectangleMode &&
                   geometry == PortalGeometry.Rectangle(
                       UserSettings.DefaultRectangleWidth,
                       UserSettings.DefaultRectangleHeight,
                       UserSettings.DefaultFeatherWidth);
        }, failures, ref total);

        Check("旧设置迁移为圆形", () => WithTemporaryStore((store, path) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"Radius\":230,\"Language\":\"en-US\"}");
            var migrated = store.Load();
            return migrated.PortalMode == UserSettings.CircleMode &&
                   migrated.FeatherWidth == UserSettings.DefaultFeatherWidth &&
                   migrated.CreateGeometry() == PortalGeometry.Circle(254, 24);
        }), failures, ref total);

        Check("2.0 矩形设置获得默认羽化", () => WithTemporaryStore((store, path) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                "{\"Radius\":180,\"Language\":\"zh-CN\",\"PortalMode\":\"rectangle\",\"RectangleWidth\":420,\"RectangleHeight\":280}");
            var migrated = store.Load();
            return migrated.FeatherWidth == UserSettings.DefaultFeatherWidth &&
                   migrated.CreateGeometry() == PortalGeometry.Rectangle(420, 280, 24);
        }), failures, ref total);

        Check("矩形设置规范化", () =>
        {
            var normalized = new UserSettings(
                180,
                Localizer.English,
                UserSettings.RectangleMode,
                1,
                9999,
                80).Normalize();
            return normalized.RectangleWidth == UserSettings.MinimumRectangleWidth &&
                   normalized.RectangleHeight == UserSettings.MaximumRectangleHeight &&
                   normalized.FeatherWidth == 79 &&
                   normalized.CreateGeometry() == PortalGeometry.Rectangle(
                       UserSettings.MinimumRectangleWidth,
                       UserSettings.MaximumRectangleHeight,
                       79);
        }, failures, ref total);

        Check(
            "中英文切换",
            () => Localizer.Get(Localizer.Chinese).HelpBody.Contains("F8") &&
                  Localizer.Get(Localizer.English).HelpBody.Contains("F8") &&
                  Localizer.NormalizeLanguage("EN-us") == Localizer.English,
            failures,
            ref total);

        Check("托盘冒烟测试参数", () =>
        {
            var options = PortalOptions.Parse(["--tray-smoke-test-ms", "750"]);
            return options.TraySmokeTestMilliseconds == 750;
        }, failures, ref total);

        Check("品牌资源与最小设置窗口", () =>
        {
            using var logo = BrandResources.LoadLogoBitmap();
            using var icon = BrandResources.LoadApplicationIcon();
            using var chineseForm = new SettingsForm(
                new UserSettings(UserSettings.DefaultRadius, Localizer.Chinese),
                icon,
                _ => true);
            using var englishForm = new SettingsForm(
                new UserSettings(UserSettings.DefaultRadius, Localizer.English),
                icon,
                _ => true);
            var controls = EnumerateControls(chineseForm).ToArray();
            return logo is { Width: > 0, Height: > 0 } &&
                   chineseForm.Text.Contains("寸镜", StringComparison.Ordinal) &&
                   englishForm.Text.Contains("PierceView", StringComparison.Ordinal) &&
                   controls.OfType<NumericUpDown>().Count() == 4 &&
                   controls.OfType<ComboBox>().Count() == 2 &&
                   controls.OfType<Button>().Count() == 2;
        }, failures, ref total);

        Check("设置保存与读取", () => WithTemporaryStore((store, _) =>
        {
            var expected = new UserSettings(230, Localizer.English);
            store.Save(expected);
            return store.Exists && store.Load() == expected.Normalize();
        }), failures, ref total);

        Check("损坏设置安全回退", () => WithTemporaryStore((store, path) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{not-json");
            return store.Load() == UserSettings.CreateDefault();
        }), failures, ref total);

        if (failures.Count == 0)
        {
            Console.WriteLine($"自检通过：{total}/{total}。");
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
        ICollection<string> failures,
        ref int total)
    {
        total++;
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
