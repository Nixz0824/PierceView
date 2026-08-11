using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowPortal;

/// <summary>
/// 自动视觉冒烟：自建颜色/坐标图案窗，覆盖静止刷新、真实鼠标移动、
/// 后台控件 Hover 重绘、内容对齐、形状边缘、羽化带与过黑闪帧。
/// </summary>
internal static class VisualSmokeTests
{
	private sealed class PatternWindow : Form
	{
		private const int CellSize = 8;

		private static readonly Color[] Palette =
		[
			Color.FromArgb(255, 20, 80, 220),
			Color.FromArgb(255, 20, 190, 80),
			Color.FromArgb(255, 240, 180, 20),
			Color.FromArgb(255, 210, 30, 170),
			Color.FromArgb(255, 20, 190, 210),
			Color.FromArgb(255, 235, 80, 25),
			Color.FromArgb(255, 235, 235, 235),
			Color.FromArgb(255, 35, 35, 45)
		];

		private readonly Bitmap _pattern;

		private readonly Bitmap? _hoverImage;

		internal int HoverRepaintCount { get; private set; }

		internal PatternWindow(
			string title,
			int x,
			int y,
			int width,
			int height,
			bool addHoverControls = false)
		{
			Text = title;
			FormBorderStyle = FormBorderStyle.None;
			StartPosition = FormStartPosition.Manual;
			Bounds = new Rectangle(x, y, width, height);
			TopMost = false;
			ShowInTaskbar = false;
			_pattern = new Bitmap(width, height, PixelFormat.Format32bppArgb);
			using var graphics = Graphics.FromImage(_pattern);
			for (var cellY = 0; cellY * CellSize < height; cellY++)
			{
				for (var cellX = 0; cellX * CellSize < width; cellX++)
				{
					using var brush = new SolidBrush(Palette[PaletteIndex(cellX, cellY)]);
					graphics.FillRectangle(
						brush,
						cellX * CellSize,
						cellY * CellSize,
						CellSize,
						CellSize);
				}
			}

			if (addHoverControls)
			{
				var hoverY = Math.Max(24, (height / 2) - 28);
				var link = new LinkLabel
				{
					Text = "Hover text",
					TextAlign = ContentAlignment.MiddleCenter,
					BackColor = Color.White,
					LinkColor = Color.FromArgb(20, 80, 220),
					Bounds = new Rectangle((width / 2) - 150, hoverY, 100, 56)
				};
				var button = new Button
				{
					Text = "Hover button",
					FlatStyle = FlatStyle.System,
					Bounds = new Rectangle((width / 2) - 45, hoverY, 110, 56)
				};
				_hoverImage = new Bitmap(80, 56, PixelFormat.Format32bppArgb);
				using (var imageGraphics = Graphics.FromImage(_hoverImage))
				{
					imageGraphics.Clear(Color.FromArgb(245, 190, 30));
					using var pen = new Pen(Color.FromArgb(30, 60, 180), 4);
					imageGraphics.DrawEllipse(pen, 14, 8, 48, 40);
				}

				var image = new PictureBox
				{
					Image = _hoverImage,
					SizeMode = PictureBoxSizeMode.StretchImage,
					Bounds = new Rectangle((width / 2) + 70, hoverY, 80, 56)
				};
				foreach (Control control in new Control[] { link, button, image })
				{
					control.MouseEnter += (_, _) => RegisterHoverActivity(control, entered: true);
					control.MouseLeave += (_, _) => RegisterHoverActivity(control, entered: false);
					control.MouseMove += (_, _) => RegisterHoverActivity(control, entered: true);
					Controls.Add(control);
				}
			}
		}

		internal int ExpectedPaletteIndex(NativeMethods.Point screenPoint)
		{
			var localX = Math.Clamp(screenPoint.X - Left, 0, ClientSize.Width - 1);
			var localY = Math.Clamp(screenPoint.Y - Top, 0, ClientSize.Height - 1);
			return PaletteIndex(localX / CellSize, localY / CellSize);
		}

		internal static int ClosestPaletteIndex(Color color)
		{
			var closest = 0;
			var closestDistance = int.MaxValue;
			for (var index = 0; index < Palette.Length; index++)
			{
				var distance = ColorDistance(color, Palette[index]);
				if (distance < closestDistance)
				{
					closest = index;
					closestDistance = distance;
				}
			}

			return closest;
		}

		internal void ForceHoverRepaint(NativeMethods.Point screenPoint)
		{
			var localPoint = new Point(screenPoint.X - Left, screenPoint.Y - Top);
			HoverRepaintCount++;
			foreach (Control control in Controls)
			{
				var entered = control.Bounds.Contains(localPoint);
				if (control is not Button)
				{
					control.BackColor = entered
						? Color.FromArgb(220, 235, 255)
						: Color.White;
				}

				control.Invalidate();
			}

			Update();
		}

		protected override void OnPaintBackground(PaintEventArgs eventArgs)
		{
			eventArgs.Graphics.DrawImageUnscaled(_pattern, 0, 0);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_hoverImage?.Dispose();
				_pattern.Dispose();
			}

			base.Dispose(disposing);
		}

		private static int PaletteIndex(int cellX, int cellY) =>
			Math.Abs((cellX * 3) + (cellY * 5)) % Palette.Length;

		private void RegisterHoverActivity(Control control, bool entered)
		{
			HoverRepaintCount++;
			if (control is not Button)
			{
				control.BackColor = entered
					? Color.FromArgb(220, 235, 255)
					: Color.White;
			}

			control.Invalidate();
		}
	}

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
		var featheredGeometry = PortalGeometry.Rectangle(
			UserSettings.DefaultRectangleWidth,
			UserSettings.DefaultRectangleHeight,
			UserSettings.DefaultFeatherWidth);
		var hardRoundedGeometry = PortalGeometry.Rectangle(
			UserSettings.DefaultRectangleWidth,
			UserSettings.DefaultRectangleHeight);
		var featheredCircleGeometry = PortalGeometry.Circle(
			radius + UserSettings.DefaultFeatherWidth,
			UserSettings.DefaultFeatherWidth);
		if (!ValidateFeatherMask(featheredCircleGeometry, backColor, out var circleMaskDetail))
		{
			failures.Add("圆形羽化 alpha 蒙版异常：" + circleMaskDetail);
		}
		else
		{
			Console.WriteLine("圆形羽化 alpha 蒙版：" + circleMaskDetail);
		}

		if (!ValidateHardRoundedMask(hardRoundedGeometry, backColor, out var hardMaskDetail))
		{
			failures.Add("硬边圆角 alpha 蒙版异常：" + hardMaskDetail);
		}
		else
		{
			Console.WriteLine("硬边圆角 alpha 蒙版：" + hardMaskDetail);
		}

		if (!ValidateFeatherMask(featheredGeometry, backColor, out var maskDetail))
		{
			failures.Add("羽化 alpha 蒙版异常：" + maskDetail);
		}
		else
		{
			Console.WriteLine("羽化 alpha 蒙版：" + maskDetail);
		}

		RunStationaryRefreshTest(hardRoundedGeometry, frontColor, backColor, failures);
		RunPatternAlignmentTest(hardRoundedGeometry, frontColor, failures);
		RunLateLatchAlignmentTest(hardRoundedGeometry, frontColor, failures);
		RunRealCursorHoverAlignmentTest(hardRoundedGeometry, frontColor, failures);
		RunStableCanvasTrailTest(frontColor, backColor, failures);

		RunShape(
			"圆形硬边模式",
			PortalGeometry.Circle(radius),
			frontColor,
			backColor,
			(Bitmap shot, PortalGeometry geometry, out string detail) =>
				LooksCircular(shot, geometry.Radius, backColor, frontColor, out detail),
			failures);
		RunShape(
			"圆形羽化模式",
			featheredCircleGeometry,
			frontColor,
			backColor,
			(Bitmap shot, PortalGeometry geometry, out string detail) =>
				LooksCircular(shot, geometry.Radius, backColor, frontColor, out detail),
			failures);
		RunShape(
			"硬边圆角矩形模式",
			hardRoundedGeometry,
			frontColor,
			backColor,
			(Bitmap shot, PortalGeometry _, out string detail) =>
				LooksHardRoundedRectangleContent(shot, backColor, frontColor, out detail),
			failures);
		RunShape(
			"羽化矩形模式",
			featheredGeometry,
			frontColor,
			backColor,
			(Bitmap shot, PortalGeometry geometry, out string detail) =>
				LooksFeatheredRectangleContent(shot, geometry, backColor, frontColor, out detail),
			failures);
	}

	private static void RunLateLatchAlignmentTest(
		PortalGeometry geometry,
		Color frontColor,
		List<string> failures)
	{
		using var back = new PatternWindow(
			"PierceView Smoke Back - Late Latch",
			120,
			120,
			720,
			520);
		using var front = CreateColorWindow(
			"PierceView Smoke Front - Late Latch",
			frontColor,
			160,
			160,
			640,
			460);
		NativeMethods.GetCursorPos(out var originalCursor);
		back.Show();
		front.Show();
		front.BringToFront();
		front.Activate();
		Application.DoEvents();
		Thread.Sleep(200);
		var requestedCenter = new NativeMethods.Point(
			front.Left + (front.Width / 2) - 32,
			front.Top + (front.Height / 2));
		var latestCenter = new NativeMethods.Point(requestedCenter.X + 56, requestedCenter.Y);
		using var overlay = new DwmPortalOverlay(
			geometry,
			enableForegroundGuard: false,
			lateLatchToCursor: true);
		try
		{
			_ = NativeMethods.SetCursorPos(requestedCenter.X, requestedCenter.Y);
			Application.DoEvents();
			if (!overlay.TryShow(back.Handle, front.Handle, requestedCenter, out var showError))
			{
				failures.Add("延迟锁定测试无法显示透视：" + showError);
				return;
			}

			_ = NativeMethods.SetCursorPos(latestCenter.X, latestCenter.Y);
			Thread.Sleep(20);
			var presentationsBeforeUpdate = overlay.DisplayPresentationCount;
			if (!overlay.TryUpdate(requestedCenter, out var updateError))
			{
				failures.Add("延迟锁定测试无法更新透视：" + updateError);
				return;
			}
			var presentationsAfterUpdate = overlay.DisplayPresentationCount;
			var committedCenter = overlay.LastPresentedCenter;

			Application.DoEvents();
			Thread.Sleep(20);
			var bounds = geometry.CreateFrameBounds(latestCenter);
			using var shot = CaptureScreenRect(
				bounds.Left,
				bounds.Top,
				geometry.FrameWidth,
				geometry.FrameHeight);
			var sampleX = geometry.FrameWidth - geometry.EffectiveFeatherWidth - 6;
			var mismatches = 0;
			var sampleOffsetsY = new[] { -72, -36, 0, 36, 72 };
			foreach (var offsetY in sampleOffsetsY)
			{
				var sourcePoint = new NativeMethods.Point(
					bounds.Left + sampleX,
					latestCenter.Y + offsetY);
				var observed = PatternWindow.ClosestPaletteIndex(
					shot.GetPixel(sampleX, (geometry.FrameHeight / 2) + offsetY));
				var expected = back.ExpectedPaletteIndex(sourcePoint);
				if (observed != expected)
				{
					mismatches++;
				}
			}

			Console.WriteLine(
				$"提交前鼠标延迟锁定：偏移=56px，边缘采样异常={mismatches}/{sampleOffsetsY.Length}，" +
				$"最终坐标一致={committedCenter == latestCenter}，" +
				$"本轮提交={presentationsAfterUpdate - presentationsBeforeUpdate}。");
			if (mismatches > 0)
			{
				failures.Add(
					$"提交前未使用最新鼠标位置：边缘采样异常 {mismatches}/{sampleOffsetsY.Length}。");
			}

			if (committedCenter != latestCenter)
			{
				failures.Add(
					$"视觉层未公开实际提交坐标：expected={latestCenter}, actual={committedCenter}。");
			}

			if (presentationsAfterUpdate - presentationsBeforeUpdate != 1)
			{
				failures.Add(
					$"一次更新发生多次分层窗提交：" +
					$"{presentationsBeforeUpdate}->{presentationsAfterUpdate}。");
			}
		}
		finally
		{
			overlay.Hide();
			_ = NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
			front.Close();
			back.Close();
			Application.DoEvents();
		}
	}

	private static void RunRealCursorHoverAlignmentTest(
		PortalGeometry geometry,
		Color frontColor,
		List<string> failures)
	{
		using var back = new PatternWindow(
			"PierceView Smoke Back - Real Cursor Hover",
			120,
			120,
			720,
			520,
			addHoverControls: true);
		using var front = CreateColorWindow(
			"PierceView Smoke Front - Real Cursor Hover",
			frontColor,
			160,
			160,
			640,
			460);
		NativeMethods.GetCursorPos(out var originalCursor);
		back.Show();
		front.Show();
		front.BringToFront();
		front.Activate();
		Application.DoEvents();
		Thread.Sleep(200);
		var origin = new NativeMethods.Point(
			front.Left + (front.Width / 2),
			front.Top + (front.Height / 2));
		using var overlay = new DwmPortalOverlay(geometry, enableForegroundGuard: false);
		try
		{
			if (!TryApplyTestHole(front, origin, geometry, out var holeError))
			{
				failures.Add("真实鼠标 Hover 测试无法创建窗口缺口：" + holeError);
				return;
			}

			if (!NativeMethods.SetCursorPos(origin.X, origin.Y))
			{
				failures.Add("真实鼠标 Hover 测试无法移动系统鼠标。");
				return;
			}

			Application.DoEvents();
			Thread.Sleep(80);
			if (!overlay.TryShow(back.Handle, front.Handle, origin, out var showError))
			{
				failures.Add("真实鼠标 Hover 测试无法显示透视：" + showError);
				return;
			}

			var mismatchedFrames = 0;
			var mismatchedSamples = 0;
			var samples = 0;
			var successfulUpdates = 0;
			var presentationsBeforeMotion = overlay.DisplayPresentationCount;
			const int frameCount = 64;
			var sampleOffsets = new[]
			{
				new NativeMethods.Point(-96, -96),
				new NativeMethods.Point(0, -96),
				new NativeMethods.Point(96, -96),
				new NativeMethods.Point(-96, 96),
				new NativeMethods.Point(0, 96),
				new NativeMethods.Point(96, 96)
			};
			for (var frame = 0; frame < frameCount; frame++)
			{
				var phase = frame % 32;
				var offset = phase < 16
					? -120 + (phase * 8)
					: 120 - ((phase - 16) * 8);
				var point = new NativeMethods.Point(origin.X + offset, origin.Y);
				if (!TryApplyTestHole(front, point, geometry, out var moveHoleError))
				{
					failures.Add("真实鼠标 Hover 测试无法移动交互孔：" + moveHoleError);
					break;
				}

				if (!NativeMethods.SetCursorPos(point.X, point.Y))
				{
					failures.Add("真实鼠标 Hover 测试中途无法移动系统鼠标。");
					break;
				}

				Application.DoEvents();
				back.ForceHoverRepaint(point);
				Thread.Sleep(4);
				if (!overlay.TryUpdate(point, out var updateError))
				{
					failures.Add("真实鼠标 Hover 对齐更新失败：" + updateError);
					break;
				}
				successfulUpdates++;

				Application.DoEvents();
				Thread.Sleep(12);
				var bounds = geometry.CreateFrameBounds(point);
				using var shot = CaptureScreenRect(
					bounds.Left,
					bounds.Top,
					geometry.FrameWidth,
					geometry.FrameHeight);
				var frameMismatch = false;
				foreach (var sampleOffset in sampleOffsets)
				{
					var sourcePoint = new NativeMethods.Point(
						point.X + sampleOffset.X,
						point.Y + sampleOffset.Y);
					var observedColor = shot.GetPixel(
						(geometry.FrameWidth / 2) + sampleOffset.X,
						(geometry.FrameHeight / 2) + sampleOffset.Y);
					var observed = PatternWindow.ClosestPaletteIndex(observedColor);
					var expected = back.ExpectedPaletteIndex(sourcePoint);
					samples++;
					if (observed != expected)
					{
						mismatchedSamples++;
						frameMismatch = true;
					}
				}

				if (frameMismatch)
				{
					mismatchedFrames++;
				}
			}

			var sourceUpdates = overlay.CaptureSourceUpdateCount;
			var displayRelocations = overlay.DisplayRelocationCount;
			var cachedPresentations = overlay.CachedPresentationCount;
			var presentationsDuringMotion =
				overlay.DisplayPresentationCount - presentationsBeforeMotion;
			Console.WriteLine(
				$"真实鼠标 Hover 对齐：异常帧={mismatchedFrames}/{frameCount}，" +
				$"异常采样={mismatchedSamples}/{samples}，Hover 重绘={back.HoverRepaintCount}，" +
				$"DWM 来源重定位={sourceUpdates}，显示画布重定位={displayRelocations}，" +
				$"缓存即时提交={cachedPresentations}，单轮提交={presentationsDuringMotion}/{successfulUpdates}。");
			if (back.HoverRepaintCount < 4)
			{
				failures.Add("后台 Hover 重绘次数不足，测试场景无效。");
			}

			if (samples < frameCount * sampleOffsets.Length / 2 ||
			    mismatchedFrames > Math.Max(2, frameCount / 16))
			{
				failures.Add(
					$"真实鼠标经过文字/图像控件时出现过多坐标错位：" +
					$"{mismatchedFrames}/{frameCount} 帧，{mismatchedSamples}/{samples} 个采样。");
			}

			if (sourceUpdates > 12)
			{
				failures.Add($"真实鼠标移动时 DWM 来源重定位过多：{sourceUpdates} 次。");
			}

			if (displayRelocations != 1)
			{
				failures.Add(
					$"固定虚拟屏幕显示窗发生了额外移动：" +
					$"DWM 来源 {sourceUpdates} 次，显示窗 {displayRelocations} 次。");
			}

			if (presentationsDuringMotion != successfulUpdates)
			{
				failures.Add(
					$"真实鼠标更新未保持每轮一次提交：" +
					$"{presentationsDuringMotion}/{successfulUpdates}。");
			}
		}
		finally
		{
			overlay.Hide();
			_ = NativeMethods.SetWindowRgn(front.Handle, nint.Zero, redraw: true);
			_ = NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
			front.Close();
			back.Close();
			Application.DoEvents();
		}
	}

	private static void RunPatternAlignmentTest(
		PortalGeometry geometry,
		Color frontColor,
		List<string> failures)
	{
		using var back = new PatternWindow(
			"PierceView Smoke Back - Pattern Alignment",
			120,
			120,
			720,
			520);
		using var front = CreateColorWindow(
			"PierceView Smoke Front - Pattern Alignment",
			frontColor,
			160,
			160,
			640,
			460);
		back.Show();
		front.Show();
		front.BringToFront();
		front.Activate();
		Application.DoEvents();
		Thread.Sleep(200);
		var origin = new NativeMethods.Point(
			front.Left + (front.Width / 2),
			front.Top + (front.Height / 2));
		using var overlay = new DwmPortalOverlay(geometry, enableForegroundGuard: false);
		if (!overlay.TryShow(back.Handle, front.Handle, origin, out var showError))
		{
			failures.Add("高对比内容对齐测试无法显示透视：" + showError);
			return;
		}

		Thread.Sleep(100);
		Application.DoEvents();
		var mismatches = 0;
		var samples = 0;
		var successfulUpdates = 0;
		var presentationsBeforeMotion = overlay.DisplayPresentationCount;
		const int frameCount = 64;
		for (var frame = 0; frame < frameCount; frame++)
		{
			var phase = frame % 32;
			var offset = phase < 16
				? -64 + (phase * 8)
				: 64 - ((phase - 16) * 8);
			var point = new NativeMethods.Point(origin.X + offset, origin.Y);
			if (!overlay.TryUpdate(point, out var updateError))
			{
				failures.Add("高对比内容对齐更新失败：" + updateError);
				break;
			}
			successfulUpdates++;

			Application.DoEvents();
			using var shot = CaptureScreenRect(point.X, point.Y, 1, 1);
			var observed = PatternWindow.ClosestPaletteIndex(shot.GetPixel(0, 0));
			var expected = back.ExpectedPaletteIndex(point);
			samples++;
			if (observed != expected)
			{
				mismatches++;
			}

			Thread.Sleep(16);
		}

		var sourceUpdates = overlay.CaptureSourceUpdateCount;
		var displayRelocations = overlay.DisplayRelocationCount;
		var cachedPresentations = overlay.CachedPresentationCount;
		var presentationsDuringMotion =
			overlay.DisplayPresentationCount - presentationsBeforeMotion;
		overlay.Hide();
		front.Close();
		back.Close();
		Application.DoEvents();
		Console.WriteLine(
			$"高对比内容坐标对齐：错位帧={mismatches}/{samples}，" +
			$"DWM 来源重定位={sourceUpdates}，显示画布重定位={displayRelocations}，" +
			$"缓存即时提交={cachedPresentations}，单轮提交={presentationsDuringMotion}/{successfulUpdates}。");
		if (samples < frameCount / 2 || mismatches > Math.Max(2, samples / 16))
		{
			failures.Add($"高对比文字/图像区域错位帧过多：{mismatches}/{samples}。");
		}

		if (sourceUpdates > 3)
		{
			failures.Add($"安全边界内移动仍重复重设 DWM 来源：{sourceUpdates} 次。");
		}

		if (displayRelocations > 1)
		{
			failures.Add($"安全边界内移动仍重复移动显示画布：{displayRelocations} 次。");
		}

		if (presentationsDuringMotion != successfulUpdates)
		{
			failures.Add(
				$"高对比移动未保持每轮一次提交：" +
				$"{presentationsDuringMotion}/{successfulUpdates}。");
		}
	}

	private static void RunStableCanvasTrailTest(
		Color frontColor,
		Color backColor,
		List<string> failures)
	{
		var geometry = PortalGeometry.Circle(28, 4);
		using var back = CreateColorWindow(
			"PierceView Smoke Back - Stable Canvas Trail",
			backColor,
			120,
			120,
			720,
			520);
		using var front = CreateColorWindow(
			"PierceView Smoke Front - Stable Canvas Trail",
			frontColor,
			160,
			160,
			640,
			460);
		back.TopMost = false;
		back.Show();
		front.Show();
		front.BringToFront();
		front.Activate();
		Application.DoEvents();
		Thread.Sleep(160);
		var origin = new NativeMethods.Point(
			front.Left + (front.Width / 2) - 40,
			front.Top + (front.Height / 2));
		var moved = new NativeMethods.Point(origin.X + 72, origin.Y);
		using var overlay = new DwmPortalOverlay(geometry, enableForegroundGuard: false);
		if (!TryApplyTestHole(front, origin, geometry, out var initialHoleError))
		{
			failures.Add("稳定画布残留测试无法创建初始交互孔：" + initialHoleError);
			return;
		}

		if (!overlay.TryShow(back.Handle, front.Handle, origin, out var showError))
		{
			failures.Add("稳定画布残留测试无法显示透视：" + showError);
			return;
		}

		Application.DoEvents();
		Thread.Sleep(20);
		var relocationsBeforeMove = overlay.DisplayRelocationCount;
		var presentationsBeforeMove = overlay.DisplayPresentationCount;
		if (!overlay.TryUpdate(moved, out var updateError))
		{
			failures.Add("稳定画布残留测试无法移动透视：" + updateError);
			return;
		}

		// Match production ordering: commit the visual frame first, then hand the
		// committed center to the physical input aperture.
		if (!TryApplyTestHole(front, moved, geometry, out var moveHoleError))
		{
			failures.Add("稳定画布残留测试无法移动交互孔：" + moveHoleError);
			return;
		}

		Application.DoEvents();
		Thread.Sleep(12);
		using var oldShot = CaptureScreenRect(origin.X, origin.Y, 1, 1);
		using var newShot = CaptureScreenRect(moved.X, moved.Y, 1, 1);
		var oldPixel = oldShot.GetPixel(0, 0);
		var newPixel = newShot.GetPixel(0, 0);
		var relocationsAfterMove = overlay.DisplayRelocationCount;
		var cachedPresentations = overlay.CachedPresentationCount;
		var presentationsAfterMove = overlay.DisplayPresentationCount;
		var oldCleared = ColorDistance(oldPixel, frontColor) + 400 <
		                 ColorDistance(oldPixel, backColor);
		var newVisible = ColorDistance(newPixel, backColor) + 400 <
		                 ColorDistance(newPixel, frontColor);
		Console.WriteLine(
			$"CPU 稳定画布残留：旧位置已清除={oldCleared}，新位置可见={newVisible}，" +
			$"显示画布重定位={relocationsBeforeMove}->{relocationsAfterMove}，" +
			$"缓存即时提交={cachedPresentations}，" +
			$"本轮提交={presentationsAfterMove - presentationsBeforeMove}。");

		if (!oldCleared || !newVisible)
		{
			failures.Add(
				$"CPU 稳定画布移动后残留异常：old={oldPixel}, new={newPixel}。");
		}

		if (relocationsAfterMove != relocationsBeforeMove)
		{
			failures.Add(
				$"安全边界内移动不应重定位显示画布：" +
				$"{relocationsBeforeMove}->{relocationsAfterMove}。");
		}

		if (presentationsAfterMove - presentationsBeforeMove != 1)
		{
			failures.Add(
				$"稳定画布移动未保持单轮一次提交：" +
				$"{presentationsBeforeMove}->{presentationsAfterMove}。");
		}
	}

	private static void RunStationaryRefreshTest(
		PortalGeometry geometry,
		Color frontColor,
		Color initialBackColor,
		List<string> failures)
	{
		var updatedBackColor = Color.FromArgb(255, 40, 100, 220);
		using var back = CreateColorWindow(
			"PierceView Smoke Back - Stationary Refresh",
			initialBackColor,
			120,
			120,
			720,
			520);
		using var front = CreateColorWindow(
			"PierceView Smoke Front - Stationary Refresh",
			frontColor,
			160,
			160,
			640,
			460);
		back.TopMost = false;
		back.Show();
		front.Show();
		front.BringToFront();
		front.Activate();
		Application.DoEvents();
		Thread.Sleep(200);
		var center = new NativeMethods.Point(
			front.Left + (front.Width / 2),
			front.Top + (front.Height / 2));
		using var overlay = new DwmPortalOverlay(geometry, enableForegroundGuard: false);
		if (!overlay.TryShow(back.Handle, front.Handle, center, out var showError))
		{
			failures.Add("静止刷新测试无法显示透视：" + showError);
			return;
		}

		Thread.Sleep(100);
		Application.DoEvents();
		back.BackColor = updatedBackColor;
		back.Refresh();
		Application.DoEvents();
		var refreshed = false;
		var refreshFrames = 0;
		for (var frame = 0; frame < 24; frame++)
		{
			if (!overlay.TryUpdate(center, out var updateError))
			{
				failures.Add("静止刷新测试更新失败：" + updateError);
				break;
			}

			refreshFrames++;
			Application.DoEvents();
			Thread.Sleep(16);
			using var shot = CaptureScreenRect(center.X, center.Y, 1, 1);
			var sample = shot.GetPixel(0, 0);
			if (ColorDistance(sample, updatedBackColor) + 400 <
			    ColorDistance(sample, initialBackColor))
			{
				refreshed = true;
				break;
			}
		}

		overlay.Hide();
		front.Close();
		back.Close();
		Application.DoEvents();
		Console.WriteLine($"静止持续刷新：更新成功={refreshed}，等待帧数={refreshFrames}。");
		if (!refreshed)
		{
			failures.Add("鼠标静止时后台内容变化未刷新到透视画面。");
		}
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
		back.TopMost = false;
		back.Show();
		front.Show();
		front.BringToFront();
		front.Activate();
		Application.DoEvents();
		Thread.Sleep(250);
		var center = new NativeMethods.Point(front.Left + front.Width / 2, front.Top + front.Height / 2);
		// 测试色块窗与覆盖层同属本进程；关闭前台守卫，避免它把测试覆盖层误判为来源应用窗口。
		using var overlay = new DwmPortalOverlay(geometry, enableForegroundGuard: false);

		// 不调用 WindowRegionController：产品禁止锁自身进程窗口；形状判定只依赖分层位图 alpha。
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
		var frameTimes = new List<double>(moveCount);
		var horizontalAmplitude = Math.Min(
			180,
			Math.Max(0, ((front.Width - geometry.FrameWidth) / 2) - 8));
		var verticalAmplitude = Math.Min(
			70,
			Math.Max(0, ((front.Height - geometry.FrameHeight) / 2) - 8));
		for (var i = 0; i < moveCount; i++)
		{
			var t = i / (double)Math.Max(1, moveCount - 1);
			var point = new NativeMethods.Point(
				center.X + (int)(horizontalAmplitude * Math.Sin(t * Math.PI * 2)),
				center.Y + (int)(verticalAmplitude * Math.Cos(t * Math.PI * 2)));

			var frameStartedAt = Stopwatch.GetTimestamp();
			if (!overlay.TryUpdate(point, out var updateError))
			{
				failures.Add($"移动第 {i} 帧失败：{updateError}");
				break;
			}
			frameTimes.Add(Stopwatch.GetElapsedTime(frameStartedAt).TotalMilliseconds);

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
		if (frameTimes.Count > 0)
		{
			var sortedTimes = frameTimes.Order().ToArray();
			var percentile95 = sortedTimes[Math.Min(
				sortedTimes.Length - 1,
				(int)Math.Ceiling(sortedTimes.Length * 0.95) - 1)];
			var overBudget = frameTimes.Count(milliseconds => milliseconds > (1000d / 60d));
			Console.WriteLine(
				$"{name}换帧：平均={frameTimes.Average():F2}ms，" +
				$"P95={percentile95:F2}ms，最慢={frameTimes.Max():F2}ms，" +
				$"超过16.67ms={overBudget}/{frameTimes.Count}。");
		}

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

	private static bool TryApplyTestHole(
		Form window,
		NativeMethods.Point screenCenter,
		PortalGeometry geometry,
		out string? error)
	{
		if (!NativeMethods.GetWindowRect(window.Handle, out var windowRect))
		{
			error = "无法读取测试窗口位置。";
			return false;
		}

		return WindowRegionController.TryApplyHoleForVisualTest(
			window.Handle,
			windowRect,
			screenCenter,
			geometry,
			out error);
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
	/// 屏幕移动采样检查硬边圆角矩形的不透明边中点与中心；圆角 alpha 由原始位图测试精确检查。
	/// </summary>
	private static bool LooksHardRoundedRectangleContent(
		Bitmap shot,
		Color backColor,
		Color frontColor,
		out string detail)
	{
		var edge = shot.GetPixel(shot.Width / 2, 2);
		var center = shot.GetPixel(shot.Width / 2, shot.Height / 2);
		var edgeLikeBack =
			ColorDistance(edge, backColor) + 30 < ColorDistance(edge, frontColor);
		var centerLikeBack =
			ColorDistance(center, backColor) + 30 < ColorDistance(center, frontColor);
		detail =
			$"edgeBack={edgeLikeBack}, centerBack={centerLikeBack}, " +
			$"edgeRGB=({edge.R},{edge.G},{edge.B}), " +
			$"centerRGB=({center.R},{center.G},{center.B})";
		return edgeLikeBack && centerLikeBack;
	}

	/// <summary>
	/// 屏幕移动采样只检查羽化矩形的完全不透明内区；alpha 梯度由原始位图测试精确检查。
	/// </summary>
	private static bool LooksFeatheredRectangleContent(
		Bitmap shot,
		PortalGeometry geometry,
		Color backColor,
		Color frontColor,
		out string detail)
	{
		var feather = geometry.EffectiveFeatherWidth;
		if (feather <= 1)
		{
			detail = "feather width is too small";
			return false;
		}

		var y = shot.Height / 2;
		var midpointX = feather / 2;
		var outer = shot.GetPixel(0, y);
		var midpoint = shot.GetPixel(midpointX, y);
		var inner = shot.GetPixel(Math.Min(shot.Width - 1, feather + 2), y);
		var center = shot.GetPixel(shot.Width / 2, y);
		var innerLikeBack =
			ColorDistance(inner, backColor) + 400 < ColorDistance(inner, frontColor);
		var centerLikeBack =
			ColorDistance(center, backColor) + 400 < ColorDistance(center, frontColor);

		detail =
			$"innerBack={innerLikeBack}, centerBack={centerLikeBack}, " +
			$"outerRGB=({outer.R},{outer.G},{outer.B}), " +
			$"midRGB=({midpoint.R},{midpoint.G},{midpoint.B}), " +
			$"innerRGB=({inner.R},{inner.G},{inner.B})";
		return innerLikeBack && centerLikeBack;
	}

	private static bool ValidateFeatherMask(
		PortalGeometry geometry,
		Color sourceColor,
		out string detail)
	{
		using var frame = new Bitmap(
			geometry.FrameWidth,
			geometry.FrameHeight,
			PixelFormat.Format32bppArgb);
		using (var graphics = Graphics.FromImage(frame))
		{
			graphics.Clear(sourceColor);
		}

		DwmPortalOverlay.ApplyPremultipliedAlphaForTesting(frame, geometry);
		var feather = geometry.EffectiveFeatherWidth;
		var midpointX = feather / 2;
		var y = frame.Height / 2;
		var pixels = ReadRawPixels(
			frame,
			(0, 0),
			(0, y),
			(midpointX, y),
			(Math.Min(frame.Width - 1, feather + 2), y));
		var corner = pixels[0];
		var outer = pixels[1];
		var midpoint = pixels[2];
		var inner = pixels[3];
		var expectedAlpha = (midpointX * 255 + (feather / 2)) / feather;
		var expectedMidR = (sourceColor.R * expectedAlpha + 127) / 255;
		var expectedMidG = (sourceColor.G * expectedAlpha + 127) / 255;
		var expectedMidB = (sourceColor.B * expectedAlpha + 127) / 255;
		var cornerTransparent = corner == new RawPixel(0, 0, 0, 0);
		var outerTransparent = outer == new RawPixel(0, 0, 0, 0);
		var midpointPremultiplied =
			Math.Abs(midpoint.A - expectedAlpha) <= 1 &&
			Math.Abs(midpoint.R - expectedMidR) <= 1 &&
			Math.Abs(midpoint.G - expectedMidG) <= 1 &&
			Math.Abs(midpoint.B - expectedMidB) <= 1;
		var innerOpaque = inner == new RawPixel(
			sourceColor.B,
			sourceColor.G,
			sourceColor.R,
			255);
		detail =
			$"cornerTransparent={cornerTransparent}, outerTransparent={outerTransparent}, " +
			$"midPremultiplied={midpointPremultiplied}, " +
			$"innerOpaque={innerOpaque}, midBGRA=({midpoint.B},{midpoint.G},{midpoint.R},{midpoint.A})";
		return cornerTransparent && outerTransparent && midpointPremultiplied && innerOpaque;
	}

	private static bool ValidateHardRoundedMask(
		PortalGeometry geometry,
		Color sourceColor,
		out string detail)
	{
		using var frame = new Bitmap(
			geometry.FrameWidth,
			geometry.FrameHeight,
			PixelFormat.Format32bppArgb);
		using (var graphics = Graphics.FromImage(frame))
		{
			graphics.Clear(sourceColor);
		}

		DwmPortalOverlay.ApplyPremultipliedAlphaForTesting(frame, geometry);
		var pixels = ReadRawPixels(
			frame,
			(0, 0),
			(frame.Width / 2, 0),
			(frame.Width / 2, frame.Height / 2));
		var transparent = new RawPixel(0, 0, 0, 0);
		var opaque = new RawPixel(
			sourceColor.B,
			sourceColor.G,
			sourceColor.R,
			255);
		var cornerTransparent = pixels[0] == transparent;
		var edgeOpaque = pixels[1] == opaque;
		var centerOpaque = pixels[2] == opaque;
		detail =
			$"cornerTransparent={cornerTransparent}, edgeOpaque={edgeOpaque}, " +
			$"centerOpaque={centerOpaque}, radius={geometry.EffectiveCornerRadius}";
		return cornerTransparent && edgeOpaque && centerOpaque;
	}

	private static RawPixel[] ReadRawPixels(
		Bitmap frame,
		params (int X, int Y)[] points)
	{
		var data = frame.LockBits(
			new Rectangle(0, 0, frame.Width, frame.Height),
			ImageLockMode.ReadOnly,
			PixelFormat.Format32bppArgb);
		try
		{
			var stride = Math.Abs(data.Stride);
			return points.Select(point =>
			{
				var address = data.Scan0 + (point.Y * stride) + (point.X * 4);
				return new RawPixel(
					Marshal.ReadByte(address),
					Marshal.ReadByte(address + 1),
					Marshal.ReadByte(address + 2),
					Marshal.ReadByte(address + 3));
			}).ToArray();
		}
		finally
		{
			frame.UnlockBits(data);
		}
	}

	private readonly record struct RawPixel(byte B, byte G, byte R, byte A);

	private static int ColorDistance(Color a, Color b)
	{
		var dr = a.R - b.R;
		var dg = a.G - b.G;
		var db = a.B - b.B;
		return (dr * dr) + (dg * dg) + (db * db);
	}
}
