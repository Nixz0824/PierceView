using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WindowPortal;

/// <summary>
/// DWM 单层透视预览。圆形沿用 1.0.5 稳定路径，2.x 在同管线增加圆角硬边/羽化矩形。
/// 不用条带（条带重影）、不用「全幅+Region/双缓冲换帧」（易变方、闪圆）。
/// 流程：屏外捕获窗上挂单张带安全边界的 DWM 缩略图 → CPU 对齐裁剪 → 形状蒙版预乘 alpha
/// → UpdateLayeredWindow 一次提交整帧。形状与内容同帧，避免叠影与换帧闪烁。
/// </summary>
internal sealed class DwmPortalOverlay : IDisposable
{
	internal const int StableCanvasMargin = 96;

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

		private readonly bool _lateLatchToCursor;

		private CaptureSurface? _capture;

		private LayeredPortalForm? _display;

		private readonly ForegroundZOrderGuard _foregroundGuard = new();

		private readonly System.Windows.Forms.Timer _zOrderGuardTimer;

		private bool _portalVisible;

		private bool _firstFrameFlushed;

		private long _lastCaptureTimestamp;

		private NativeMethods.Point? _lastPresentedCenter;

		internal bool PortalVisible => _portalVisible && _display is not null;

		internal nint SourceWindow { get; private set; }

		internal int ForegroundRecoveryCount => _foregroundGuard.RecoveryCount;

		internal int BackgroundPromotionCount => _foregroundGuard.PromotionCount;

		internal int CaptureSourceUpdateCount => _capture?.SourceUpdateCount ?? 0;

		internal int DisplayRelocationCount => _display?.RelocationCount ?? 0;

		internal int CachedPresentationCount { get; private set; }

		internal PortalOverlayManager(
			PortalGeometry geometry,
			bool enableForegroundGuard,
			bool lateLatchToCursor)
		{
			_geometry = geometry;
			_enableForegroundGuard = enableForegroundGuard;
			_lateLatchToCursor = lateLatchToCursor;
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
			_display = new LayeredPortalForm(
				_geometry,
				_capture.CanvasWidth,
				_capture.CanvasHeight);
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
			_lastCaptureTimestamp = 0;
			_lastPresentedCenter = null;
			CachedPresentationCount = 0;
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

			var targetCenter = screenCenter;
			if (_lateLatchToCursor &&
			    _capture.HasCapturedFrame &&
			    NativeMethods.GetCursorPos(out var latestCenter))
			{
				targetCenter = latestCenter;
			}

			// 鼠标仍在当前安全画布内时，先用上一张原始抓帧立即重绘形状。
			// 这样鼠标移动不必先等待 PrintWindow，视觉位置与物理交互孔更接近同一时刻。
			if (_capture.TryGetCachedFrame(
				    sourceWindowRect,
				    targetCenter,
				    out var cachedFrame,
				    out var cachedCanvasBounds,
				    out var cachedPortalOffset) &&
			    _lastPresentedCenter != targetCenter)
			{
				if (!_display.TryPresent(
					    cachedFrame,
					    cachedPortalOffset.X,
					    cachedPortalOffset.Y,
					    cachedCanvasBounds.Left,
					    cachedCanvasBounds.Top,
					    out error))
				{
					return false;
				}

				_lastPresentedCenter = targetCenter;
				CachedPresentationCount++;
			}

			if (!_capture.TryUpdateSource(sourceWindowRect, targetCenter, out error))
			{
				return false;
			}

			// 首帧和安全画布跨界时等待新的 DWM 映射落地。安全画布内移动不 flush，
			// 因此不会恢复早期每像素刷新造成的浏览器标题栏抖动。
			if (!_firstFrameFlushed || _capture.SourceMappingChanged)
			{
				_ = NativeMethods.DwmFlush();
				_firstFrameFlushed = true;
			}

			// 鼠标移动可复用缓存画布快速重绘；后台内容抓取维持约 60Hz。
			// 发生画布重定位或首次显示时仍立即抓取，不能沿用旧坐标映射。
			var captureElapsed = _lastCaptureTimestamp == 0
				? TimeSpan.MaxValue
				: Stopwatch.GetElapsedTime(_lastCaptureTimestamp);
			if (_capture.HasCapturedFrame &&
			    !_capture.SourceMappingChanged &&
			    captureElapsed < TimeSpan.FromMilliseconds(16))
			{
				if (_enableForegroundGuard &&
				    _lastPresentedCenter is { } cachedCenter &&
				    cachedCenter != screenCenter)
				{
					_foregroundGuard.UpdatePortalGeometry(cachedCenter, _geometry.GuardRadius);
				}

				error = null;
				return true;
			}

			var captureStartedAt = Stopwatch.GetTimestamp();
			if (!_capture.TryGrabFrame(
				    sourceWindowRect,
				    targetCenter,
				    _lateLatchToCursor,
				    out var frame,
				    out var presentedCenter,
				    out var canvasBounds,
				    out var portalOffset,
				    out error))
			{
				return false;
			}

			if (!_display.TryPresent(
				    frame,
				    portalOffset.X,
				    portalOffset.Y,
				    canvasBounds.Left,
				    canvasBounds.Top,
				    out error))
			{
				return false;
			}

			_lastCaptureTimestamp = captureStartedAt;
			_lastPresentedCenter = presentedCenter;

			if (_enableForegroundGuard && presentedCenter != screenCenter)
			{
				_foregroundGuard.UpdatePortalGeometry(presentedCenter, _geometry.GuardRadius);
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

		// 鼠标在该边界内移动时只改变 CPU 裁剪位置，不重设 DWM 来源。
		// 这样可以避免 DWM 来源矩形与顶层显示窗分别落在相邻两帧。
		private readonly PortalGeometry _geometry;

		private readonly int _frameWidth;

		private readonly int _frameHeight;

		private readonly int _captureWidth;

		private readonly int _captureHeight;

		private readonly CaptureHostForm _host;

		private readonly Bitmap _captureBuffer;

		private nint _thumbnail;

		private NativeMethods.Rect? _bufferSource;

		private int _sourceWindowWidth;

		private int _sourceWindowHeight;

		private int _cropX;

		private int _cropY;

		private bool _hasCapturedFrame;

		internal int CanvasWidth => _captureWidth;

		internal int CanvasHeight => _captureHeight;

		internal bool HasCapturedFrame => _hasCapturedFrame;

		internal bool SourceMappingChanged { get; private set; }

		internal int SourceUpdateCount { get; private set; }

		internal CaptureSurface(PortalGeometry geometry)
		{
			_geometry = geometry;
			_frameWidth = geometry.FrameWidth;
			_frameHeight = geometry.FrameHeight;
			_captureWidth = checked(_frameWidth + (StableCanvasMargin * 2));
			_captureHeight = checked(_frameHeight + (StableCanvasMargin * 2));
			_captureBuffer = new Bitmap(
				_captureWidth,
				_captureHeight,
				PixelFormat.Format32bppArgb);
			_host = new CaptureHostForm
			{
				FormBorderStyle = FormBorderStyle.None,
				ShowInTaskbar = false,
				StartPosition = FormStartPosition.Manual,
				AutoScaleMode = AutoScaleMode.None,
				BackColor = Color.Black,
				TopMost = false,
				Bounds = new Rectangle(Offscreen, Offscreen, _captureWidth, _captureHeight)
			};
			// 强制创建句柄并显示在屏外，DWM 缩略图需要目标窗可合成
			_ = _host.Handle;
			_ = NativeMethods.SetWindowPos(
				_host.Handle,
				nint.Zero,
				Offscreen,
				Offscreen,
				_captureWidth,
				_captureHeight,
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
			var requestedSource = new NativeMethods.Rect(
				frame.Left - sourceWindowRect.Left,
				frame.Top - sourceWindowRect.Top,
				frame.Right - sourceWindowRect.Left,
				frame.Bottom - sourceWindowRect.Top);
			var sourceSizeChanged =
				_sourceWindowWidth != sourceWindowRect.Width ||
				_sourceWindowHeight != sourceWindowRect.Height;
			if (!sourceSizeChanged &&
			    _bufferSource is { } currentBuffer &&
			    Contains(currentBuffer, requestedSource))
			{
				_cropX = requestedSource.Left - currentBuffer.Left;
				_cropY = requestedSource.Top - currentBuffer.Top;
				error = null;
				return true;
			}

			var bufferSource = new NativeMethods.Rect(
				requestedSource.Left - StableCanvasMargin,
				requestedSource.Top - StableCanvasMargin,
				requestedSource.Right + StableCanvasMargin,
				requestedSource.Bottom + StableCanvasMargin);
			var sourceBounds = new NativeMethods.Rect(
				0,
				0,
				sourceWindowRect.Width,
				sourceWindowRect.Height);
			var clippedSource = Intersect(bufferSource, sourceBounds);
			if (clippedSource.Width <= 0 || clippedSource.Height <= 0)
			{
				error = "透视区域已经移出视觉来源窗口。";
				return false;
			}

			var destination = new NativeMethods.Rect(
				clippedSource.Left - bufferSource.Left,
				clippedSource.Top - bufferSource.Top,
				clippedSource.Right - bufferSource.Left,
				clippedSource.Bottom - bufferSource.Top);

			var properties = new NativeMethods.DwmThumbnailProperties
			{
				Flags = NativeMethods.DwmTnpRectDestination |
				        NativeMethods.DwmTnpRectSource |
				        NativeMethods.DwmTnpOpacity |
				        NativeMethods.DwmTnpVisible |
				        NativeMethods.DwmTnpSourceClientAreaOnly,
				Destination = destination,
				Source = clippedSource,
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

			_bufferSource = bufferSource;
			_sourceWindowWidth = sourceWindowRect.Width;
			_sourceWindowHeight = sourceWindowRect.Height;
			_cropX = requestedSource.Left - bufferSource.Left;
			_cropY = requestedSource.Top - bufferSource.Top;
			SourceUpdateCount++;
			SourceMappingChanged = true;
			_hasCapturedFrame = false;
			error = null;
			return true;
		}

		internal bool TryGetCachedFrame(
			NativeMethods.Rect sourceWindowRect,
			NativeMethods.Point screenCenter,
			out Bitmap frame,
			out NativeMethods.Rect canvasBounds,
			out NativeMethods.Point portalOffset)
		{
			frame = null!;
			canvasBounds = default;
			portalOffset = default;
			if (!_hasCapturedFrame ||
			    SourceMappingChanged ||
			    !TryAlignCapturedCrop(sourceWindowRect, screenCenter) ||
			    !TryGetCanvasLayout(sourceWindowRect, out canvasBounds, out portalOffset))
			{
				return false;
			}

			frame = _captureBuffer;
			return true;
		}

		internal bool TryGrabFrame(
			NativeMethods.Rect sourceWindowRect,
			NativeMethods.Point requestedCenter,
			bool lateLatchToCursor,
			out Bitmap frame,
			out NativeMethods.Point presentedCenter,
			out NativeMethods.Rect canvasBounds,
			out NativeMethods.Point portalOffset,
			out string? error)
		{
			frame = null!;
			presentedCenter = requestedCenter;
			canvasBounds = default;
			portalOffset = default;
			var ok = false;
			try
			{
				using (var graphics = Graphics.FromImage(_captureBuffer))
				{
					graphics.Clear(Color.Black);
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
									_captureWidth,
									_captureHeight,
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
					return false;
				}

				// PrintWindow 是本帧最慢的一段。抓取完成后再读取一次鼠标，
				// 只在已经捕获的安全边界内重新裁剪，避免把 10~20ms 前的旧坐标提交到屏幕。
				if (lateLatchToCursor &&
				    NativeMethods.GetCursorPos(out var latestCenter) &&
				    TryAlignCapturedCrop(sourceWindowRect, latestCenter))
				{
					presentedCenter = latestCenter;
				}

				if (_cropX < 0 ||
				    _cropY < 0 ||
				    _cropX + _frameWidth > _captureWidth ||
				    _cropY + _frameHeight > _captureHeight)
				{
					error = "DWM 安全边界裁剪位置超出捕获面。";
					return false;
				}

				// 全黑帧通常表示捕获失败（缩略图未合成到可抓取表面）
				if (IsAlmostBlack(
					    _captureBuffer,
					    new Rectangle(_cropX, _cropY, _frameWidth, _frameHeight)))
				{
					error = "DWM 捕获帧几乎全黑，来源窗口可能无法提供缩略图。";
					return false;
				}

				if (!TryGetCanvasLayout(sourceWindowRect, out canvasBounds, out portalOffset))
				{
					error = "DWM 稳定画布坐标尚未就绪。";
					return false;
				}

				frame = _captureBuffer;
				_hasCapturedFrame = true;
				SourceMappingChanged = false;
				error = null;
				return true;
			}
			catch
			{
				throw;
			}
		}

		private bool TryGetCanvasLayout(
			NativeMethods.Rect sourceWindowRect,
			out NativeMethods.Rect canvasBounds,
			out NativeMethods.Point portalOffset)
		{
			if (_bufferSource is not { } bufferSource)
			{
				canvasBounds = default;
				portalOffset = default;
				return false;
			}

			canvasBounds = new NativeMethods.Rect(
				sourceWindowRect.Left + bufferSource.Left,
				sourceWindowRect.Top + bufferSource.Top,
				sourceWindowRect.Left + bufferSource.Right,
				sourceWindowRect.Top + bufferSource.Bottom);
			portalOffset = new NativeMethods.Point(_cropX, _cropY);
			return true;
		}

		private bool TryAlignCapturedCrop(
			NativeMethods.Rect sourceWindowRect,
			NativeMethods.Point screenCenter)
		{
			if (_bufferSource is not { } currentBuffer ||
			    _sourceWindowWidth != sourceWindowRect.Width ||
			    _sourceWindowHeight != sourceWindowRect.Height)
			{
				return false;
			}

			var frame = _geometry.CreateFrameBounds(screenCenter);
			var requestedSource = new NativeMethods.Rect(
				frame.Left - sourceWindowRect.Left,
				frame.Top - sourceWindowRect.Top,
				frame.Right - sourceWindowRect.Left,
				frame.Bottom - sourceWindowRect.Top);
			if (!Contains(currentBuffer, requestedSource))
			{
				return false;
			}

			_cropX = requestedSource.Left - currentBuffer.Left;
			_cropY = requestedSource.Top - currentBuffer.Top;
			return true;
		}

		private static bool Contains(
			NativeMethods.Rect container,
			NativeMethods.Rect candidate) =>
			candidate.Left >= container.Left &&
			candidate.Top >= container.Top &&
			candidate.Right <= container.Right &&
			candidate.Bottom <= container.Bottom;

		private static NativeMethods.Rect Intersect(
			NativeMethods.Rect first,
			NativeMethods.Rect second) =>
			new(
				Math.Max(first.Left, second.Left),
				Math.Max(first.Top, second.Top),
				Math.Min(first.Right, second.Right),
				Math.Min(first.Bottom, second.Bottom));

		private static bool IsAlmostBlack(Bitmap frame, Rectangle bounds)
		{
			// 抽样若干点，避免每帧全图扫描
			var hits = 0;
			var samples = 0;
			for (var y = bounds.Top + 4;
			     y < bounds.Bottom;
			     y += Math.Max(8, bounds.Height / 8))
			{
				for (var x = bounds.Left + 4;
				     x < bounds.Right;
				     x += Math.Max(8, bounds.Width / 8))
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
			_bufferSource = null;
			_captureBuffer.Dispose();

			_host.Close();
			_host.Dispose();
		}
	}

	/// <summary>
	/// 分层显示窗：UpdateLayeredWindow 提交带圆 alpha 的整帧。
	/// </summary>
	private sealed class LayeredPortalForm : Form
	{
		private readonly int _portalWidth;

		private readonly int _portalHeight;

		private readonly byte[] _alphaMask;

		private readonly byte[] _pixelBuffer;

		private readonly int _width;

		private readonly int _height;

		private nint _screenDc;

		private nint _memoryDc;

		private nint _dibSection;

		private nint _dibBits;

		private nint _previousBitmap;

		private bool _shown;

		private int? _lastLeft;

		private int? _lastTop;

		internal int RelocationCount { get; private set; }

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

		internal LayeredPortalForm(
			PortalGeometry geometry,
			int canvasWidth,
			int canvasHeight)
		{
			_portalWidth = geometry.FrameWidth;
			_portalHeight = geometry.FrameHeight;
			_alphaMask = PortalFrameComposer.CreateAlphaMask(geometry);
			_width = canvasWidth;
			_height = canvasHeight;
			_pixelBuffer = new byte[checked(_width * _height * 4)];
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			AutoScaleMode = AutoScaleMode.None;
			BackColor = Color.Black;
			TopMost = true;
			Bounds = new Rectangle(-32000, -32000, _width, _height);
			_ = Handle;
		}

		internal bool TryPresent(
			Bitmap frame,
			int portalLeft,
			int portalTop,
			int left,
			int top,
			out string? error)
		{
			if (frame.Width != _width || frame.Height != _height)
			{
				error = "捕获帧尺寸与透视区域不一致。";
				return false;
			}

			if (!TryEnsureSurface(out error))
			{
				return false;
			}

			PortalFrameComposer.CopyPositionedPremultipliedPixels(
				frame,
				_dibBits,
				_alphaMask,
				_portalWidth,
				_portalHeight,
				portalLeft,
				portalTop,
				_pixelBuffer);
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
				    _screenDc,
				    ref dst,
				    ref size,
				    _memoryDc,
				    ref src,
				    0,
				    ref blend,
				    NativeMethods.UlwAlpha))
			{
				error = "UpdateLayeredWindow 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
				return false;
			}

			if (_lastLeft != left || _lastTop != top)
			{
				RelocationCount++;
				_lastLeft = left;
				_lastTop = top;
			}

			if (!_shown)
			{
				// UpdateLayeredWindow 已经提交位置；首次显示时只做一次顶层/可见性事务。
				_ = NativeMethods.SetWindowPos(
					Handle,
					NativeMethods.HwndTopMost,
					0,
					0,
					0,
					0,
					NativeMethods.SwpNoMove |
					NativeMethods.SwpNoSize |
					NativeMethods.SwpNoActivate |
					NativeMethods.SwpNoOwnerZOrder |
					NativeMethods.SwpShowWindow);
				_shown = true;
			}

			error = null;
			return true;
		}

		private bool TryEnsureSurface(out string? error)
		{
			if (_dibSection != nint.Zero)
			{
				error = null;
				return true;
			}

			_screenDc = NativeMethods.GetDC(nint.Zero);
			if (_screenDc == nint.Zero)
			{
				error = "无法获取屏幕 DC。";
				return false;
			}

			_memoryDc = NativeMethods.CreateCompatibleDC(_screenDc);
			if (_memoryDc == nint.Zero)
			{
				ReleaseSurface();
				error = "无法创建兼容 DC。";
				return false;
			}

			var header = new NativeMethods.BitmapInfoHeader
			{
				BiSize = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
				BiWidth = _width,
				// 负高度 = 顶向下 DIB，与 Bitmap 扫描行一致
				BiHeight = -_height,
				BiPlanes = 1,
				BiBitCount = 32,
				BiCompression = 0
			};

			// GetHbitmap 会丢掉 alpha；会话内复用同一张 DIB section 保留预乘 ARGB。
			_dibSection = NativeMethods.CreateDIBSection(
				_screenDc,
				ref header,
				0,
				out _dibBits,
				nint.Zero,
				0);

			if (_dibSection == nint.Zero || _dibBits == nint.Zero)
			{
				ReleaseSurface();
				error = "CreateDIBSection 失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
				return false;
			}

			_previousBitmap = NativeMethods.SelectObject(_memoryDc, _dibSection);
			if (_previousBitmap == nint.Zero || _previousBitmap == new nint(-1))
			{
				ReleaseSurface();
				error = "无法把 DIB section 绑定到内存 DC。";
				return false;
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
			_shown = false;
			_lastLeft = null;
			_lastTop = null;
		}

		protected override void Dispose(bool disposing)
		{
			ReleaseSurface();
			base.Dispose(disposing);
		}

		private void ReleaseSurface()
		{
			if (_memoryDc != nint.Zero &&
			    _previousBitmap != nint.Zero &&
			    _previousBitmap != new nint(-1))
			{
				_ = NativeMethods.SelectObject(_memoryDc, _previousBitmap);
			}

			_previousBitmap = nint.Zero;
			if (_dibSection != nint.Zero)
			{
				_ = NativeMethods.DeleteObject(_dibSection);
				_dibSection = nint.Zero;
				_dibBits = nint.Zero;
			}

			if (_memoryDc != nint.Zero)
			{
				_ = NativeMethods.DeleteDC(_memoryDc);
				_memoryDc = nint.Zero;
			}

			if (_screenDc != nint.Zero)
			{
				_ = NativeMethods.ReleaseDC(nint.Zero, _screenDc);
				_screenDc = nint.Zero;
			}
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

		internal static void ApplyPremultipliedAlpha(
			Bitmap frame,
			byte[] alphaMask,
			byte[]? reusableBuffer = null)
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
				var requiredLength = checked(stride * frame.Height);
				var buffer = reusableBuffer is { Length: var length } && length >= requiredLength
					? reusableBuffer
					: new byte[requiredLength];
				CopyBitmapToBuffer(frame, data, buffer, stride);
				ApplyPositionedMask(
					buffer,
					stride,
					frame.Width,
					frame.Height,
					alphaMask,
					frame.Width,
					frame.Height,
					0,
					0);
				CopyBufferToBitmap(frame, data, buffer, stride);
			}
			finally
			{
				frame.UnlockBits(data);
			}
		}

		internal static void CopyPositionedPremultipliedPixels(
			Bitmap frame,
			nint destination,
			byte[] alphaMask,
			int portalWidth,
			int portalHeight,
			int portalLeft,
			int portalTop,
			byte[] reusableBuffer)
		{
			if (destination == nint.Zero)
			{
				throw new ArgumentException("Destination DIB is not initialized.", nameof(destination));
			}

			if (alphaMask.Length != checked(portalWidth * portalHeight))
			{
				throw new ArgumentException("Alpha mask dimensions do not match the portal.", nameof(alphaMask));
			}

			var data = frame.LockBits(
				new Rectangle(0, 0, frame.Width, frame.Height),
				ImageLockMode.ReadOnly,
				PixelFormat.Format32bppArgb);
			try
			{
				var stride = Math.Abs(data.Stride);
				var requiredLength = checked(stride * frame.Height);
				if (reusableBuffer.Length < requiredLength)
				{
					throw new ArgumentException("Reusable buffer is smaller than the frame.", nameof(reusableBuffer));
				}

				CopyBitmapToBuffer(frame, data, reusableBuffer, stride);
				ApplyPositionedMask(
					reusableBuffer,
					stride,
					frame.Width,
					frame.Height,
					alphaMask,
					portalWidth,
					portalHeight,
					portalLeft,
					portalTop);

				var destinationStride = checked(frame.Width * 4);
				if (stride == destinationStride)
				{
					Marshal.Copy(reusableBuffer, 0, destination, checked(destinationStride * frame.Height));
					return;
				}

				for (var y = 0; y < frame.Height; y++)
				{
					Marshal.Copy(
						reusableBuffer,
						y * stride,
						destination + (y * destinationStride),
						destinationStride);
				}
			}
			finally
			{
				frame.UnlockBits(data);
			}
		}

		private static void ApplyPositionedMask(
			byte[] buffer,
			int stride,
			int canvasWidth,
			int canvasHeight,
			byte[] alphaMask,
			int portalWidth,
			int portalHeight,
			int portalLeft,
			int portalTop)
		{
			if (portalLeft < 0 ||
			    portalTop < 0 ||
			    portalLeft + portalWidth > canvasWidth ||
			    portalTop + portalHeight > canvasHeight)
			{
				throw new ArgumentOutOfRangeException(nameof(portalLeft), "Portal is outside the stable canvas.");
			}

			var portalBottom = portalTop + portalHeight;
			var leftByteCount = portalLeft * 4;
			var rightByteStart = (portalLeft + portalWidth) * 4;
			for (var y = 0; y < canvasHeight; y++)
			{
				var row = y * stride;
				if (y < portalTop || y >= portalBottom)
				{
					Array.Clear(buffer, row, stride);
					continue;
				}

				if (leftByteCount > 0)
				{
					Array.Clear(buffer, row, leftByteCount);
				}

				var maskRow = (y - portalTop) * portalWidth;
				for (var x = 0; x < portalWidth; x++)
				{
					var i = row + ((portalLeft + x) * 4);
					var alpha = alphaMask[maskRow + x];
					if (alpha == 0)
					{
						buffer[i] = 0;
						buffer[i + 1] = 0;
						buffer[i + 2] = 0;
						buffer[i + 3] = 0;
						continue;
					}

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

				if (rightByteStart < stride)
				{
					Array.Clear(buffer, row + rightByteStart, stride - rightByteStart);
				}
			}
		}

		private static void CopyBitmapToBuffer(
			Bitmap frame,
			BitmapData data,
			byte[] buffer,
			int stride)
		{
			var rowBytes = checked(frame.Width * 4);
			for (var y = 0; y < frame.Height; y++)
			{
				var sourceRow = data.Stride >= 0
					? data.Scan0 + (y * data.Stride)
					: data.Scan0 + ((frame.Height - 1 - y) * -data.Stride);
				Marshal.Copy(sourceRow, buffer, y * stride, rowBytes);
			}
		}

		private static void CopyBufferToBitmap(
			Bitmap frame,
			BitmapData data,
			byte[] buffer,
			int stride)
		{
			var rowBytes = checked(frame.Width * 4);
			for (var y = 0; y < frame.Height; y++)
			{
				var destinationRow = data.Stride >= 0
					? data.Scan0 + (y * data.Stride)
					: data.Scan0 + ((frame.Height - 1 - y) * -data.Stride);
				Marshal.Copy(buffer, y * stride, destinationRow, rowBytes);
			}
		}
	}

	private readonly PortalGeometry _geometry;

	private readonly bool _enableForegroundGuard;

	private readonly bool _lateLatchToCursor;

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

	internal int CaptureSourceUpdateCount => Invoke(static manager => manager.CaptureSourceUpdateCount);

	internal int DisplayRelocationCount => Invoke(static manager => manager.DisplayRelocationCount);

	internal int CachedPresentationCount => Invoke(static manager => manager.CachedPresentationCount);

	internal DwmPortalOverlay(int radius)
		: this(
			PortalGeometry.Circle(radius),
			enableForegroundGuard: true,
			lateLatchToCursor: false)
	{
	}

	internal DwmPortalOverlay(PortalGeometry geometry)
		: this(
			geometry,
			enableForegroundGuard: true,
			lateLatchToCursor: false)
	{
	}

	/// <summary>
	/// 仅供同进程视觉夹具关闭前台守卫；生产构造路径始终传入 true。
	/// </summary>
	internal DwmPortalOverlay(PortalGeometry geometry, bool enableForegroundGuard)
		: this(geometry, enableForegroundGuard, lateLatchToCursor: false)
	{
	}

	internal DwmPortalOverlay(
		PortalGeometry geometry,
		bool enableForegroundGuard,
		bool lateLatchToCursor)
	{
		_geometry = geometry;
		_enableForegroundGuard = enableForegroundGuard;
		_lateLatchToCursor = lateLatchToCursor;
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

		_disposed = true;
		_ = _thread.Join(2000);
		_ready.Dispose();
		GC.SuppressFinalize(this);
	}

	private void RunMessageLoop()
	{
		try
		{
			_manager = new PortalOverlayManager(
				_geometry,
				_enableForegroundGuard,
				_lateLatchToCursor);
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
