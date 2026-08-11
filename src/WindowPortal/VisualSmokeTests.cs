using System.Drawing;
using System.Drawing.Imaging;

namespace WindowPortal;

/// <summary>
/// 自动视觉冒烟：自建红/绿色块窗，只驱动 DWM 透视层（不挖自身窗口洞），
/// 采样外接矩形四角与圆心，拦住「变方 / 过黑闪帧」回归。
/// </summary>
internal static class VisualSmokeTests
{
	internal static int Run(int radius = 120)
	{
		radius = Math.Clamp(radius, 64, 200);
		var failures = new List<string>();
		Exception? threadError = null;
		var done = new ManualResetEventSlim(false);

		var thread = new Thread(() =>
		{
			try
			{
				Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
				Application.EnableVisualStyles();
				RunCore(radius, failures);
			}
			catch (Exception ex)
			{
				threadError = ex;
			}
			finally
			{
				done.Set();
			}
		})
		{
			IsBackground = false,
			Name = "PierceView visual smoke"
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		if (!done.Wait(90000))
		{
			Console.Error.WriteLine("视觉冒烟超时。");
			return 9;
		}

		if (threadError is not null)
		{
			Console.Error.WriteLine("视觉冒烟异常：" + threadError);
			return 9;
		}

		foreach (var failure in failures)
		{
			Console.Error.WriteLine("失败：" + failure);
		}

		if (failures.Count == 0)
		{
			Console.WriteLine($"视觉冒烟通过（radius={radius}）。");
			return 0;
		}

		Console.Error.WriteLine($"视觉冒烟失败：{failures.Count} 项。");
		return 8;
	}

	private static void RunCore(int radius, List<string> failures)
	{
		var frontColor = Color.FromArgb(255, 220, 40, 40);
		var backColor = Color.FromArgb(255, 40, 200, 80);
		RunShape(
			"硬边矩形模式",
			PortalGeometry.Rectangle(
				UserSettings.DefaultRectangleWidth,
				UserSettings.DefaultRectangleHeight),
			frontColor,
			backColor,
			(Bitmap shot, PortalGeometry _, out string detail) =>
				LooksHardRectangle(shot, backColor, frontColor, out detail),
			failures);
		RunShape(
			"圆形兼容模式",
			PortalGeometry.Circle(radius),
			frontColor,
			backColor,
			(Bitmap shot, PortalGeometry geometry, out string detail) =>
				LooksCircular(shot, geometry.Radius, backColor, frontColor, out detail),
			failures);
	}

	private delegate bool FrameValidator(
		Bitmap shot,
		PortalGeometry geometry,
		out string detail);

	private static void RunShape(
		string name,
		PortalGeometry geometry,
		Color frontColor,
		Color backColor,
		FrameValidator validateFrame,
		List<string> failures)
	{
		using var back = CreateColorWindow($"PierceView Smoke Back - {name}", backColor, 120, 120, 720, 520);
		using var front = CreateColorWindow($"PierceView Smoke Front - {name}", frontColor, 160, 160, 640, 460);
		back.Show();
		front.Show();
		Application.DoEvents();
		Thread.Sleep(250);
		var center = new NativeMethods.Point(front.Left + front.Width / 2, front.Top + front.Height / 2);
		using var overlay = new DwmPortalOverlay(geometry);

		// 不调用 WindowRegionController：产品禁止锁自身进程窗口；形状判定只依赖分层圆 alpha。
		if (!overlay.TryShow(back.Handle, front.Handle, center, out var showError))
		{
			failures.Add($"{name}显示透视失败：{showError}");
			return;
		}

		Thread.Sleep(100);
		Application.DoEvents();

		var blackishFrames = 0;
		var invalidShapeFrames = 0;
		var samples = 0;
		const int moveCount = 48;

		for (var i = 0; i < moveCount; i++)
		{
			var t = i / (double)Math.Max(1, moveCount - 1);
			var point = new NativeMethods.Point(
				center.X + (int)(180 * Math.Sin(t * Math.PI * 2)),
				center.Y + (int)(70 * Math.Cos(t * Math.PI * 2)));

			if (!overlay.TryUpdate(point, out var updateError))
			{
				failures.Add($"移动第 {i} 帧失败：{updateError}");
				break;
			}

			Application.DoEvents();
			Thread.Sleep(16);

			var bounds = geometry.CreateFrameBounds(point);
			using var shot = CaptureScreenRect(
				bounds.Left,
				bounds.Top,
				geometry.FrameWidth,
				geometry.FrameHeight);

			samples++;
			if (!validateFrame(shot, geometry, out var shapeDetail))
			{
				invalidShapeFrames++;
				if (invalidShapeFrames <= 4)
				{
					Console.WriteLine($"{name}形状告警 frame={i}: {shapeDetail}");
				}
			}

			if (LooksMostlyBlack(shot))
			{
				blackishFrames++;
			}
		}

		overlay.Hide();
		front.Close();
		back.Close();
		Application.DoEvents();

		Console.WriteLine(
			$"{name}：采样帧={samples}，形状异常={invalidShapeFrames}，疑似过黑={blackishFrames}。");

		if (samples < moveCount / 2)
		{
			failures.Add($"{name}有效采样过少。");
		}

		if (invalidShapeFrames > samples / 5)
		{
			failures.Add($"{name}形状异常帧过多：{invalidShapeFrames}/{samples}。");
		}

		if (blackishFrames > samples / 4)
		{
			failures.Add($"{name}过黑帧过多：{blackishFrames}/{samples}（可能闪黑或捕获失败）。");
		}
	}

	private static Form CreateColorWindow(string title, Color color, int x, int y, int w, int h)
	{
		return new Form
		{
			Text = title,
			FormBorderStyle = FormBorderStyle.None,
			StartPosition = FormStartPosition.Manual,
			Bounds = new Rectangle(x, y, w, h),
			BackColor = color,
			TopMost = true,
			ShowInTaskbar = false
		};
	}

	private static Bitmap CaptureScreenRect(int left, int top, int width, int height)
	{
		var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
		using var g = Graphics.FromImage(bmp);
		g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
		return bmp;
	}

	private static bool LooksMostlyBlack(Bitmap shot)
	{
		var dark = 0;
		var total = 0;
		for (var y = 2; y < shot.Height; y += 10)
		{
			for (var x = 2; x < shot.Width; x += 10)
			{
				total++;
				var c = shot.GetPixel(x, y);
				if (c.R < 24 && c.G < 24 && c.B < 24)
				{
					dark++;
				}
			}
		}

		return total > 0 && dark * 100 >= total * 85;
	}

	/// <summary>
	/// 圆 + 透明角：四角应更像前景红；圆心应更像背景绿。
	/// 方框不透明：四角也会变绿。
	/// </summary>
	private static bool LooksCircular(
		Bitmap shot,
		int radius,
		Color backColor,
		Color frontColor,
		out string detail)
	{
		var corners = new[]
		{
			shot.GetPixel(2, 2),
			shot.GetPixel(shot.Width - 3, 2),
			shot.GetPixel(2, shot.Height - 3),
			shot.GetPixel(shot.Width - 3, shot.Height - 3)
		};
		var center = shot.GetPixel(radius, radius);

		var cornerLikeBack = corners.Count(c => ColorDistance(c, backColor) < ColorDistance(c, frontColor));
		var centerLikeBack = ColorDistance(center, backColor) + 30 < ColorDistance(center, frontColor);

		detail =
			$"cornersBackish={cornerLikeBack}/4, centerBackish={centerLikeBack}, " +
			$"cRGB=({center.R},{center.G},{center.B}), " +
			$"tlRGB=({corners[0].R},{corners[0].G},{corners[0].B})";

		if (cornerLikeBack >= 3)
		{
			return false;
		}

		if (!centerLikeBack && ColorDistance(center, frontColor) < 50)
		{
			return false;
		}

		return true;
	}

	/// <summary>
	/// 硬边矩形：四角与中心都应更像背景绿，不应残留前景红或透明圆角。
	/// </summary>
	private static bool LooksHardRectangle(
		Bitmap shot,
		Color backColor,
		Color frontColor,
		out string detail)
	{
		var samples = new[]
		{
			shot.GetPixel(2, 2),
			shot.GetPixel(shot.Width - 3, 2),
			shot.GetPixel(2, shot.Height - 3),
			shot.GetPixel(shot.Width - 3, shot.Height - 3),
			shot.GetPixel(shot.Width / 2, shot.Height / 2)
		};
		var backgroundLike = samples.Count(
			color => ColorDistance(color, backColor) + 30 < ColorDistance(color, frontColor));
		detail =
			$"backgroundLike={backgroundLike}/5, " +
			$"centerRGB=({samples[4].R},{samples[4].G},{samples[4].B}), " +
			$"tlRGB=({samples[0].R},{samples[0].G},{samples[0].B})";
		return backgroundLike >= 4;
	}

	private static int ColorDistance(Color a, Color b)
	{
		var dr = a.R - b.R;
		var dg = a.G - b.G;
		var db = a.B - b.B;
		return (dr * dr) + (dg * dg) + (db * db);
	}
}
