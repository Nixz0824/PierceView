using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WindowPortal;

/// <summary>
/// DWM 单层透视预览。圆形沿用 1.0.5 稳定路径，2.x 在同管线增加圆角硬边/羽化矩形。
/// 不用条带（条带重影）、不用「全幅+Region/双缓冲换帧」（易变方、闪圆）。
/// 流程：屏外捕获窗上挂单张 DWM 缩略图 → 抓成位图 → CPU 形状蒙版预乘 alpha
/// → UpdateLayeredWindow 一次提交整帧。形状与内容同帧，避免叠影与换帧闪烁。
/// </summary>
internal sealed class DwmPortalOverlay : IDisposable
{
	internal static void ApplyPremultipliedAlphaForTesting(
		Bitmap frame,
		PortalGeometry geometry) =>
		PortalFrameComposer.ApplyPremultipliedAlpha(
			frame,
			PortalFrameComposer.CreateAlphaMask(geometry));

	private sealed class PortalOverlayManager : Form
	{
		private readonly PortalGeometry _geometry;

		private readonly bool _enableForegroundGuard;

		private readonly byte[] _alphaMask;

		private CaptureSurface? _capture;

		private LayeredPortalForm? _display;

		private readonly ForegroundZOrderGuard _foregroundGuard = new();

		private readonly System.Windows.Forms.Timer _zOrderGuardTimer;

		private bool _portalVisible;

		private bool _firstFrameFlushed;

		internal bool PortalVisible => _portalVisible && _display is not null;

		internal nint SourceWindow { get; private set; }

		internal int ForegroundRecoveryCount => _foregroundGuard.RecoveryCount;

		internal int BackgroundPromotionCount => _foregroundGuard.PromotionCount;

		internal PortalOverlayManager(PortalGeometry geometry, bool enableForegroundGuard)
		{
			_geometry = geometry;
			_enableForegroundGuard = enableForegroundGuard;
			_alphaMask = PortalFrameComposer.CreateAlphaMask(geometry);
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			Opacity = 0;
			Size = new Size(1, 1);
			_zOrderGuardTimer = new System.Windows.Forms.Timer
			{
				Interval = 33
			};
			_zOrderGuardTimer.Tick += (_, _) => _foregroundGuard.EnsurePreserved();
		}

		internal bool TryShowPortal(
			nint sourceWindow,
			nint protectedWindow,
			NativeMethods.Point screenCenter,
			out string? error)
		{
			HidePortal();
			SourceWindow = sourceWindow;
			if (_enableForegroundGuard &&
			    !_foregroundGuard.TryEnable(sourceWindow, protectedWindow, screenCenter, _geometry.GuardRadius, out error))
			{
				HidePortal();
				return false;
			}

			_capture = new CaptureSurface(_geometry);
			_display = new LayeredPortalForm(_geometry);
			if (!_capture.TryRegisterSource(sourceWindow, out error))
			{
				HidePortal();
				return false;
			}

			if (!NativeMethods.GetWindowRect(SourceWindow, out var rect))
			{
				error = "无法读取视觉穿透源窗口的位置。";
				HidePortal();
				return false;
			}

			if (!TryPresentFrame(rect, screenCenter, out error))
			{
				HidePortal();
				return false;
			}

			_portalVisible = true;
			if (_enableForegroundGuard)
			{
				_zOrderGuardTimer.Start();
			}
			error = null;
			return true;
		}

		internal bool TryUpdatePortal(
			NativeMethods.Point screenCenter,
			out string? error)
		{
			if (_enableForegroundGuard)
			{
				_foregroundGuard.UpdatePortalGeometry(screenCenter, _geometry.GuardRadius);
			}

			if (!_portalVisible ||
			    _capture is null ||
			    _display is null ||
			    !NativeMethods.GetWindowRect(SourceWindow, out var rect))
			{
				error = "视觉穿透源窗口已经不可用。";
				return false;
			}

			if (!TryPresentFrame(rect, screenCenter, out error))
			{
				return false;
			}

			error = null;
			return true;
		}

		internal void HidePortal()
		{
			_zOrderGuardTimer.Stop();
			_display?.HidePortal();
			_capture?.Dispose();
			_display?.Dispose();
			_foregroundGuard.Restore();
			_capture = null;
			_display = null;
			_portalVisible = false;
			_firstFrameFlushed = false;
			SourceWindow = nint.Zero;
		}

		protected override void Dispose(bool disposing)
		{
			HidePortal();
			if (disposing)
			{
				_zOrderGuardTimer.Dispose();
			}

			base.Dispose(disposing);
		}

		private bool TryPresentFrame(
			NativeMethods.Rect sourceWindowRect,
			NativeMethods.Point screenCenter,
			out string? error)
		{
			if (_capture is null || _display is null)
			{
				error = "视觉穿透表面尚未就绪。";
				return false;
			}

			if (!_capture.TryUpdateSource(sourceWindowRect, screenCenter, out error))
			{
				return false;
			}

			// 仅首帧 flush，帮助缩略图落到捕获面；移动中不再全局 flush，避免浏览器栏抖动
			if (!_firstFrameFlushed)
			{
				_ = NativeMethods.DwmFlush();
				_firstFrameFlushed = true;
			}

			if (!_capture.TryGrabFrame(out var frame, out error))
			{
				return false;
			}

			using (frame)
			{
				PortalFrameComposer.ApplyPremultipliedAlpha(frame, _alphaMask);
				var bounds = _geometry.CreateFrameBounds(screenCenter);
				if (!_display.TryPresent(frame, bounds.Left, bounds.Top, out error))
				{
					return false;
				}
			}

			error = null;
			return true;
		}
	}

	/// <summary>
	/// 屏外捕获窗：只承载一张全幅 DWM 缩略图，不直接给用户看。
	/// </summary>
	private sealed class CaptureSurface : IDisposable
	{
		private sealed class CaptureHostForm : Form
		{
			protected override bool ShowWithoutActivation => true;
		}

		private const int Offscreen = -32000;

		private readonly PortalGeometry _geometry;

		private readonly int _width;

		private readonly int _height;

		private readonly CaptureHostForm _host;

		private nint _thumbnail;

		private NativeMethods.Rect? _lastSource;

		internal CaptureSurface(PortalGeometry geometry)
		{
			_geometry = geometry;
			_width = geometry.FrameWidth;
			_height = geometry.FrameHeight;
			_host = new CaptureHostForm
			{
				FormBorderStyle = FormBorderStyle.None,
				ShowInTaskbar = false,
				StartPosition = FormStartPosition.Manual,
				AutoScaleMode = AutoScaleMode.None,
				BackColor = Color.Black,
				TopMost = false,
				Bounds = new Rectangle(Offscreen, Offscreen, _width, _height)
			};
			// 强制创建句柄并显示在屏外，DWM 缩略图需要目标窗可合成
			_ = _host.Handle;
			_ = NativeMethods.SetWindowPos(
				_host.Handle,
				nint.Zero,
				Offscreen,
				Offscreen,
				_width,
				_height,
				NativeMethods.SwpNoActivate |
				NativeMethods.SwpNoOwnerZOrder |
				NativeMethods.SwpNoZOrder |
				NativeMethods.SwpShowWindow);
		}

		internal bool TryRegisterSource(nint sourceWindow, out string? error)
		{
			var hr = NativeMethods.DwmRegisterThumbnail(_host.Handle, sourceWindow, out _thumbnail);
			if (hr != 0 || _thumbnail == nint.Zero)
			{
				error = $"DwmRegisterThumbnail 失败：0x{hr:X8}。";
				_thumbnail = nint.Zero;
				return false;
			}

			error = null;
			return true;
		}

		internal bool TryUpdateSource(
			NativeMethods.Rect sourceWindowRect,
			NativeMethods.Point screenCenter,
			out string? error)
		{
			if (_thumbnail == nint.Zero)
			{
				error = "DWM 缩略图尚未注册。";
				return false;
			}

			var frame = _geometry.CreateFrameBounds(screenCenter);
			var source = new NativeMethods.Rect(
				frame.Left - sourceWindowRect.Left,
				frame.Top - sourceWindowRect.Top,
				frame.Right - sourceWindowRect.Left,
				frame.Bottom - sourceWindowRect.Top);
			if (_lastSource == source)
			{
				error = null;
				return true;
			}

			var properties = new NativeMethods.DwmThumbnailProperties
			{
				Flags = NativeMethods.DwmTnpRectDestination |
				        NativeMethods.DwmTnpRectSource |
				        NativeMethods.DwmTnpOpacity |
				        NativeMethods.DwmTnpVisible |
				        NativeMethods.DwmTnpSourceClientAreaOnly,
				Destination = new NativeMethods.Rect(0, 0, _width, _height),
				Source = source,
				Opacity = byte.MaxValue,
				Visible = true,
				SourceClientAreaOnly = false
			};

			var hr = NativeMethods.DwmUpdateThumbnailProperties(_thumbnail, ref properties);
			if (hr != 0)
			{
				error = $"DwmUpdateThumbnailProperties 失败：0x{hr:X8}。";
				return false;
			}

			_lastSource = source;
			error = null;
			return true;
		}

		internal bool TryGrabFrame(out Bitmap frame, out string? error)
		{
			frame = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
			var ok = false;
			try
			{
				using (var graphics = Graphics.FromImage(frame))
				{
					var hdc = graphics.GetHdc();
					try
					{
						// 优先完整内容打印（含 DWM 合成）
						ok = NativeMethods.PrintWindow(_host.Handle, hdc, NativeMethods.PwRenderFullContent);
						if (!ok)
						{
							ok = NativeMethods.PrintWindow(_host.Handle, hdc, 0);
						}

						if (!ok)
						{
							var windowDc = NativeMethods.GetDC(_host.Handle);
							if (windowDc != nint.Zero)
							{
								ok = NativeMethods.BitBlt(
									hdc,
									0,
									0,
									_width,
									_height,
									windowDc,
									0,
									0,
									NativeMethods.SrcCopy);
								_ = NativeMethods.ReleaseDC(_host.Handle, windowDc);
							}
						}
					}
					finally
					{
						graphics.ReleaseHdc(hdc);
					}
				}

				if (!ok)
				{
					error = "无法从 DWM 捕获面抓取帧：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
					frame.Dispose();
					frame = null!;
					return false;
				}

				// 全黑帧通常表示捕获失败（缩略图未合成到可抓取表面）
				if (IsAlmostBlack(frame))
				{
					error = "DWM 捕获帧几乎全黑，来源窗口可能无法提供缩略图。";
					frame.Dispose();
					frame = null!;
					return false;
				}

				error = null;
				return true;
			}
			catch
			{
				frame.Dispose();
				throw;
			}
		}

		private static bool IsAlmostBlack(Bitmap frame)
		{
			// 抽样若干点，避免每帧全图扫描
			var hits = 0;
			var samples = 0;
			for (var y = 4; y < frame.Height; y += Math.Max(8, frame.Height / 8))
			{
				for (var x = 4; x < frame.Width; x += Math.Max(8, frame.Width / 8))
				{
					samples++;
					var c = frame.GetPixel(x, y);
					if (c.R > 16 || c.G > 16 || c.B > 16)
					{
						hits++;
					}
				}
			}

			return samples > 0 && hits * 20 < samples;
		}

		public void Dispose()
		{
			if (_thumbnail != nint.Zero)
			{
				_ = NativeMethods.DwmUnregisterThumbnail(_thumbnail);
				_thumbnail = nint.Zero;
			}
			_lastSource = null;

			_host.Close();
			_host.Dispose();
		}
	}

	/// <summary>
	/// 分层显示窗：UpdateLayeredWindow 提交带圆 alpha 的整帧。
	/// </summary>
	private sealed class LayeredPortalForm : Form
	{
		private readonly int _width;

		private readonly int _height;

		protected override bool ShowWithoutActivation => true;

		protected override CreateParams CreateParams
		{
			get
			{
				var createParams = base.CreateParams;
				// WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST
				createParams.ExStyle |= unchecked((int)0x00080000) | 0x00000020 | 0x00000080 | 0x08000000 | 0x00000008;
				return createParams;
			}
		}

		internal LayeredPortalForm(PortalGeometry geometry)
		{
			_width = geometry.FrameWidth;
			_height = geometry.FrameHeight;
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			AutoScaleMode = AutoScaleMode.None;
			BackColor = Color.Black;
			TopMost = true;
			Bounds = new Rectangle(-32000, -32000, _width, _height);
			_ = Handle;
		}

		internal bool TryPresent(Bitmap frame, int left, int top, out string? error)
		{
			if (frame.Width != _width || frame.Height != _height)
			{
				error = "捕获帧尺寸与透视区域不一致。";
				return false;
			}

			var screenDc = NativeMethods.GetDC(nint.Zero);
			if (screenDc == nint.Zero)
			{
				error = "无法获取屏幕 DC。";
				return false;
			}

			var memDc = NativeMethods.CreateCompatibleDC(screenDc);
			if (memDc == nint.Zero)
			{
				_ = NativeMethods.ReleaseDC(nint.Zero, screenDc);
				error = "无法创建兼容 DC。";
				return false;
			}

			// GetHbitmap 会丢掉 alpha；用 DIB section 保留预乘 ARGB
			if (!TryCreatePremultipliedDib(frame, out var hBitmap, out var dibBits, out error))
			{
				_ = NativeMethods.DeleteDC(memDc);
				_ = NativeMethods.ReleaseDC(nint.Zero, screenDc);
				return false;
			}

			var old = NativeMethods.SelectObject(memDc, hBitmap);
			try
			{
				var dst = new NativeMethods.Point(left, top);
				var size = new NativeMethods.Size(_width, _height);
				var src = new NativeMethods.Point(0, 0);
				var blend = new NativeMethods.BlendFunction
				{
					BlendOp = NativeMethods.AcSrcOver,
					BlendFlags = 0,
					SourceConstantAlpha = 255,
					AlphaFormat = NativeMethods.AcSrcAlpha
				};

				if (!NativeMethods.UpdateLayeredWindow(
					    Handle,
					    screenDc,
					    ref dst,
					    ref size,
					    memDc,
					    ref src,
					    0,
					    ref blend,
					    NativeMethods.UlwAlpha))
				{
					error = "UpdateLayeredWindow 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
					return false;
				}

				// 确保盖在色块窗之上
				_ = NativeMethods.SetWindowPos(
					Handle,
					NativeMethods.HwndTopMost,
					left,
					top,
					0,
					0,
					NativeMethods.SwpNoSize |
					NativeMethods.SwpNoActivate |
					NativeMethods.SwpNoOwnerZOrder |
					NativeMethods.SwpShowWindow);

				error = null;
				return true;
			}
			finally
			{
				_ = NativeMethods.SelectObject(memDc, old);
				_ = NativeMethods.DeleteObject(hBitmap);
				_ = NativeMethods.DeleteDC(memDc);
				_ = NativeMethods.ReleaseDC(nint.Zero, screenDc);
				_ = dibBits; // bits owned by hBitmap
			}
		}

		private static bool TryCreatePremultipliedDib(
			Bitmap frame,
			out nint hBitmap,
			out nint bits,
			out string? error)
		{
			hBitmap = nint.Zero;
			bits = nint.Zero;
			var header = new NativeMethods.BitmapInfoHeader
			{
				BiSize = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
				BiWidth = frame.Width,
				// 负高度 = 顶向下 DIB，与 Bitmap 扫描行一致
				BiHeight = -frame.Height,
				BiPlanes = 1,
				BiBitCount = 32,
				BiCompression = 0
			};

			var screenDc = NativeMethods.GetDC(nint.Zero);
			hBitmap = NativeMethods.CreateDIBSection(
				screenDc,
				ref header,
				0,
				out bits,
				nint.Zero,
				0);
			_ = NativeMethods.ReleaseDC(nint.Zero, screenDc);

			if (hBitmap == nint.Zero || bits == nint.Zero)
			{
				error = "CreateDIBSection 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
				return false;
			}

			var data = frame.LockBits(
				new Rectangle(0, 0, frame.Width, frame.Height),
				ImageLockMode.ReadOnly,
				PixelFormat.Format32bppArgb);
			try
			{
				var srcStride = Math.Abs(data.Stride);
				var dstStride = frame.Width * 4;
				var row = new byte[dstStride];
				for (var y = 0; y < frame.Height; y++)
				{
					Marshal.Copy(data.Scan0 + (y * srcStride), row, 0, dstStride);
					Marshal.Copy(row, 0, bits + (y * dstStride), dstStride);
				}
			}
			finally
			{
				frame.UnlockBits(data);
			}

			error = null;
			return true;
		}

		internal void HidePortal()
		{
			if (!IsHandleCreated)
			{
				return;
			}

			_ = NativeMethods.SetWindowPos(
				Handle,
				nint.Zero,
				-32000,
				-32000,
				0,
				0,
				NativeMethods.SwpHideWindow |
				NativeMethods.SwpNoActivate |
				NativeMethods.SwpNoSize |
				NativeMethods.SwpNoZOrder |
				NativeMethods.SwpNoOwnerZOrder);
		}

		protected override void WndProc(ref Message message)
		{
			const int wmNcHitTest = 0x0084;
			const int wmMouseActivate = 0x0021;
			if (message.Msg == wmNcHitTest)
			{
				message.Result = new nint(-1); // HTTRANSPARENT
				return;
			}

			if (message.Msg == wmMouseActivate)
			{
				message.Result = new nint(3); // MA_NOACTIVATE
				return;
			}

			base.WndProc(ref message);
		}
	}

	private static class PortalFrameComposer
	{
		internal static byte[] CreateAlphaMask(PortalGeometry geometry)
		{
			var mask = new byte[checked(geometry.FrameWidth * geometry.FrameHeight)];
			if (geometry.Shape == PortalShape.Circle)
			{
				var radius = geometry.Radius;
				var radiusSquared = (long)radius * radius;
				var circleFeatherWidth = geometry.EffectiveFeatherWidth;
				for (var y = 0; y < geometry.FrameHeight; y++)
				{
					var dy = y - radius;
					for (var x = 0; x < geometry.FrameWidth; x++)
					{
						var dx = x - radius;
						var distanceSquared = ((long)dx * dx) + ((long)dy * dy);
						mask[(y * geometry.FrameWidth) + x] = circleFeatherWidth <= 0
							? distanceSquared > radiusSquared
								? (byte)0
								: byte.MaxValue
							: CreateFeatheredAlpha(
								Math.Sqrt(distanceSquared) - radius,
								circleFeatherWidth);
					}
				}

				return mask;
			}

			var cornerRadius = geometry.EffectiveCornerRadius;
			var featherWidth = geometry.EffectiveFeatherWidth;
			var centerX = (geometry.FrameWidth - 1) / 2d;
			var centerY = (geometry.FrameHeight - 1) / 2d;
			var straightHalfWidth = centerX - cornerRadius;
			var straightHalfHeight = centerY - cornerRadius;
			for (var y = 0; y < geometry.FrameHeight; y++)
			{
				for (var x = 0; x < geometry.FrameWidth; x++)
				{
					var qx = Math.Abs(x - centerX) - straightHalfWidth;
					var qy = Math.Abs(y - centerY) - straightHalfHeight;
					var outsideX = Math.Max(qx, 0d);
					var outsideY = Math.Max(qy, 0d);
					var signedDistance =
						Math.Sqrt((outsideX * outsideX) + (outsideY * outsideY)) +
						Math.Min(Math.Max(qx, qy), 0d) -
						cornerRadius;
					mask[(y * geometry.FrameWidth) + x] =
						CreateFeatheredAlpha(signedDistance, featherWidth);
				}
			}

			return mask;
		}

		private static byte CreateFeatheredAlpha(
			double signedDistance,
			int featherWidth)
		{
			if (featherWidth <= 0)
			{
				return signedDistance <= 0d ? byte.MaxValue : (byte)0;
			}

			var inwardDistance = -signedDistance;
			if (inwardDistance <= 0d)
			{
				return 0;
			}

			if (inwardDistance >= featherWidth)
			{
				return byte.MaxValue;
			}

			return (byte)Math.Clamp(
				(int)Math.Round(
					(inwardDistance / featherWidth) * byte.MaxValue,
					MidpointRounding.AwayFromZero),
				0,
				byte.MaxValue);
		}

		internal static void ApplyPremultipliedAlpha(Bitmap frame, byte[] alphaMask)
		{
			if (alphaMask.Length != checked(frame.Width * frame.Height))
			{
				throw new ArgumentException("Alpha mask dimensions do not match the frame.", nameof(alphaMask));
			}

			var data = frame.LockBits(
				new Rectangle(0, 0, frame.Width, frame.Height),
				ImageLockMode.ReadWrite,
				PixelFormat.Format32bppArgb);
			try
			{
				var stride = Math.Abs(data.Stride);
				var buffer = new byte[stride * frame.Height];
				Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

				for (var y = 0; y < frame.Height; y++)
				{
					var row = y * stride;
					for (var x = 0; x < frame.Width; x++)
					{
						var i = row + (x * 4);
						var alpha = alphaMask[(y * frame.Width) + x];

						if (alpha == 0)
						{
							buffer[i] = 0;
							buffer[i + 1] = 0;
							buffer[i + 2] = 0;
							buffer[i + 3] = 0;
							continue;
						}

						// UpdateLayeredWindow 要求颜色通道与 alpha 预乘。
						var b = buffer[i];
						var g = buffer[i + 1];
						var r = buffer[i + 2];
						buffer[i] = alpha == byte.MaxValue
							? b
							: (byte)((b * alpha + 127) / 255);
						buffer[i + 1] = alpha == byte.MaxValue
							? g
							: (byte)((g * alpha + 127) / 255);
						buffer[i + 2] = alpha == byte.MaxValue
							? r
							: (byte)((r * alpha + 127) / 255);
						buffer[i + 3] = alpha;
					}
				}

				Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
			}
			finally
			{
				frame.UnlockBits(data);
			}
		}
	}

	private readonly PortalGeometry _geometry;

	private readonly bool _enableForegroundGuard;

	private readonly Thread _thread;

	private readonly ManualResetEventSlim _ready = new(initialState: false);

	private PortalOverlayManager? _manager;

	private ApplicationContext? _applicationContext;

	private Exception? _startupException;

	private bool _disposed;

	internal bool IsVisible => Invoke(static manager => manager.PortalVisible);

	internal nint SourceWindow => Invoke(static manager => manager.SourceWindow);

	internal int ForegroundRecoveryCount => Invoke(static manager => manager.ForegroundRecoveryCount);

	internal int BackgroundPromotionCount => Invoke(static manager => manager.BackgroundPromotionCount);

	internal DwmPortalOverlay(int radius)
		: this(PortalGeometry.Circle(radius), enableForegroundGuard: true)
	{
	}

	internal DwmPortalOverlay(PortalGeometry geometry)
		: this(geometry, enableForegroundGuard: true)
	{
	}

	/// <summary>
	/// 仅供同进程视觉夹具关闭前台守卫；生产构造路径始终传入 true。
	/// </summary>
	internal DwmPortalOverlay(PortalGeometry geometry, bool enableForegroundGuard)
	{
		_geometry = geometry;
		_enableForegroundGuard = enableForegroundGuard;
		_thread = new Thread(RunMessageLoop)
		{
			IsBackground = true,
			Name = "PierceView DWM overlay"
		};
		_thread.SetApartmentState(ApartmentState.STA);
		_thread.Start();
		_ready.Wait();
		if (_startupException is not null)
		{
			throw new InvalidOperationException("无法启动视觉穿透覆盖层。", _startupException);
		}
	}

	internal bool TryShow(nint sourceWindow, nint protectedWindow, NativeMethods.Point screenCenter, out string? error)
	{
		if (sourceWindow == nint.Zero || !NativeMethods.IsWindow(sourceWindow))
		{
			error = "透视区域下方没有可用于视觉预览的窗口。";
			return false;
		}

		try
		{
			var result = Invoke(manager =>
				manager.TryShowPortal(sourceWindow, protectedWindow, screenCenter, out var detail)
					? (true, detail)
					: (false, detail));
			error = result.Item2;
			return result.Item1;
		}
		catch (Exception ex)
		{
			error = "无法显示视觉穿透覆盖层：" + ex.Message;
			return false;
		}
	}

	internal bool TryUpdate(NativeMethods.Point screenCenter, out string? error)
	{
		try
		{
			var result = Invoke(manager =>
				manager.TryUpdatePortal(screenCenter, out var detail)
					? (true, detail)
					: (false, detail));
			error = result.Item2;
			return result.Item1;
		}
		catch (Exception ex)
		{
			error = "无法更新视觉穿透覆盖层：" + ex.Message;
			return false;
		}
	}

	internal void Hide()
	{
		if (_disposed || _manager is null)
		{
			return;
		}

		try
		{
			_ = Invoke(manager =>
			{
				manager.HidePortal();
				return true;
			});
		}
		catch
		{
			// ignore
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			Hide();
			_ = Invoke(manager =>
			{
				manager.Close();
				return true;
			});
		}
		catch
		{
			// ignore
		}

		_ = _thread.Join(2000);
		_ready.Dispose();
		GC.SuppressFinalize(this);
	}

	private void RunMessageLoop()
	{
		try
		{
			_manager = new PortalOverlayManager(_geometry, _enableForegroundGuard);
			_applicationContext = new ApplicationContext(_manager);
			_ready.Set();
			Application.Run(_applicationContext);
		}
		catch (Exception ex)
		{
			_startupException = ex;
			_ready.Set();
		}
	}

	private T Invoke<T>(Func<PortalOverlayManager, T> action)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var manager = _manager ?? throw new InvalidOperationException("视觉穿透覆盖层尚未就绪。");
		if (manager.IsDisposed)
		{
			throw new ObjectDisposedException(nameof(PortalOverlayManager));
		}

		if (manager.InvokeRequired)
		{
			return (T)manager.Invoke(action, manager)!;
		}

		return action(manager);
	}
}
