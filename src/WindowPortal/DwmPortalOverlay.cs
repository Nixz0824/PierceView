using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WindowPortal;

/// <summary>
/// DWM 圆形透视预览。移动时采用单缓冲原位更新（不每帧显隐换帧），
/// 并用椭圆 SetWindowRgn 代替 TransparencyKey，减轻移动频闪。
/// </summary>
internal sealed class DwmPortalOverlay : IDisposable
{
	private sealed class PortalOverlayManager : Form
	{
		private PortalPreviewForm? _preview;

		private readonly ForegroundZOrderGuard _foregroundGuard = new();

		private readonly System.Windows.Forms.Timer _zOrderGuardTimer;

		private NativeMethods.Point? _lastScreenCenter;

		private NativeMethods.Rect? _lastSourceRect;

		private bool _portalVisible;

		internal bool PortalVisible => _portalVisible && _preview is not null;

		internal nint SourceWindow { get; private set; }

		internal int ForegroundRecoveryCount => _foregroundGuard.RecoveryCount;

		internal int BackgroundPromotionCount => _foregroundGuard.PromotionCount;

		internal PortalOverlayManager()
		{
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			Opacity = 0;
			Size = new Size(1, 1);
			_zOrderGuardTimer = new System.Windows.Forms.Timer
			{
				// 不必 10ms 狂刷 Z-order；过频会加重合成抖动
				Interval = 33
			};
			_zOrderGuardTimer.Tick += (_, _) => _foregroundGuard.EnsurePreserved();
		}

		internal bool TryShowPortal(
			nint sourceWindow,
			nint protectedWindow,
			NativeMethods.Point screenCenter,
			int radius,
			out string? error)
		{
			HidePortal();
			SourceWindow = sourceWindow;
			if (!_foregroundGuard.TryEnable(sourceWindow, protectedWindow, screenCenter, radius, out error))
			{
				HidePortal();
				return false;
			}

			_preview = new PortalPreviewForm(radius);
			if (!_preview.TryRegisterSource(sourceWindow, out error))
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

			if (!_preview.TryPrepare(rect, screenCenter, radius, moveWindow: true, out error) ||
			    !TryShowPreview(_preview, out error))
			{
				HidePortal();
				return false;
			}

			// 首帧同步一次即可，移动路径不再每帧 DwmFlush
			_ = NativeMethods.DwmFlush();
			_portalVisible = true;
			_zOrderGuardTimer.Start();
			_lastScreenCenter = screenCenter;
			_lastSourceRect = rect;
			error = null;
			return true;
		}

		internal bool TryUpdatePortal(
			NativeMethods.Point screenCenter,
			int radius,
			out string? error)
		{
			_foregroundGuard.UpdatePortalGeometry(screenCenter, radius);

			if (!_portalVisible || _preview is null ||
			    !NativeMethods.GetWindowRect(SourceWindow, out var rect))
			{
				error = "视觉穿透源窗口已经不可用。";
				return false;
			}

			// 位置与源窗口矩形未变：跳过（避免无意义 DWM 更新）
			if (_lastScreenCenter is { } lastCenter &&
			    lastCenter == screenCenter &&
			    _lastSourceRect == rect)
			{
				error = null;
				return true;
			}

			// 单缓冲原位更新：不隐藏/显示第二窗，不每帧换帧
			if (!_preview.TryPrepare(rect, screenCenter, radius, moveWindow: true, out error))
			{
				return false;
			}

			_lastScreenCenter = screenCenter;
			_lastSourceRect = rect;
			error = null;
			return true;
		}

		internal void HidePortal()
		{
			_zOrderGuardTimer.Stop();
			if (_portalVisible && _preview is not null)
			{
				_ = TryHidePreview(_preview, out _);
			}

			_portalVisible = false;
			_preview?.Dispose();
			_foregroundGuard.Restore();
			_preview = null;
			_lastScreenCenter = null;
			_lastSourceRect = null;
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

		private static bool TryShowPreview(PortalPreviewForm preview, out string? error)
		{
			// SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_NOOWNERZORDER
			const uint flags = NativeMethods.SwpShowWindow |
			                   NativeMethods.SwpNoActivate |
			                   NativeMethods.SwpNoMove |
			                   NativeMethods.SwpNoSize |
			                   NativeMethods.SwpNoOwnerZOrder;
			if (!NativeMethods.SetWindowPos(
				    preview.Handle,
				    NativeMethods.HwndTopMost,
				    0,
				    0,
				    0,
				    0,
				    flags))
			{
				error = LastWin32Error("无法显示视觉圆");
				return false;
			}

			error = null;
			return true;
		}

		private static bool TryHidePreview(PortalPreviewForm preview, out string? error)
		{
			const uint flags = NativeMethods.SwpHideWindow |
			                   NativeMethods.SwpNoActivate |
			                   NativeMethods.SwpNoMove |
			                   NativeMethods.SwpNoSize |
			                   NativeMethods.SwpNoOwnerZOrder |
			                   NativeMethods.SwpNoZOrder;
			if (!NativeMethods.SetWindowPos(preview.Handle, nint.Zero, 0, 0, 0, 0, flags))
			{
				error = LastWin32Error("无法隐藏视觉圆");
				return false;
			}

			error = null;
			return true;
		}

		private static string LastWin32Error(string message)
		{
			var code = Marshal.GetLastWin32Error();
			return code != 0
				? $"{message}：{new Win32Exception(code).Message}（{code}）"
				: message;
		}
	}

	private sealed class PortalPreviewForm : Form
	{
		// 4px 条带：比 3px 略减缩略图数量，移动时 DWM 更新更轻
		private const int BandHeight = 4;

		private const int WmNcHitTest = 0x0084;

		private const int WmMouseActivate = 0x0021;

		private static readonly nint HtTransparent = new(-1);

		private static readonly nint MaNoActivate = new(3);

		private readonly int _radius;

		private readonly int _diameter;

		private readonly List<nint> _thumbnails = [];

		private bool _thumbnailLayoutConfigured;

		private bool _circularRegionApplied;

		private int _lastX = int.MinValue;

		private int _lastY = int.MinValue;

		protected override bool ShowWithoutActivation => true;

		protected override CreateParams CreateParams
		{
			get
			{
				var createParams = base.CreateParams;
				// WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST 由 TopMost 等处理
				createParams.ExStyle |= 0x00000020 | 0x00000080 | 0x08000000;
				return createParams;
			}
		}

		internal PortalPreviewForm(int radius)
		{
			_radius = radius;
			_diameter = checked(radius * 2 + 1);
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			AutoScaleMode = AutoScaleMode.None;
			// 不用 TransparencyKey：色键在移动/重绘时极易整窗闪一下
			BackColor = Color.Black;
			TopMost = true;
			// 先放到屏外，避免创建瞬间闪一下
			Bounds = new Rectangle(-32000, -32000, _diameter, _diameter);
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			ApplyCircularRegion();
		}

		internal bool TryRegisterSource(nint sourceWindow, out string? error)
		{
			var bandCount = (_diameter + BandHeight - 1) / BandHeight;
			for (var i = 0; i < bandCount; i++)
			{
				var hr = NativeMethods.DwmRegisterThumbnail(Handle, sourceWindow, out var thumbnail);
				if (hr != 0 || thumbnail == nint.Zero)
				{
					error = $"DwmRegisterThumbnail 失败：0x{hr:X8}。";
					UnregisterThumbnails();
					return false;
				}

				_thumbnails.Add(thumbnail);
			}

			error = null;
			return true;
		}

		/// <param name="moveWindow">
		/// 是否移动预览窗。首次显示与光标移动都需要为 true。
		/// </param>
		internal bool TryPrepare(
			NativeMethods.Rect sourceWindowRect,
			NativeMethods.Point screenCenter,
			int radius,
			bool moveWindow,
			out string? error)
		{
			if (_thumbnails.Count == 0)
			{
				error = "DWM 圆形缩略图尚未注册。";
				return false;
			}

			ApplyCircularRegion();

			var left = screenCenter.X - radius;
			var top = screenCenter.Y - radius;

			// 先更新 DWM 源矩形（内容对齐新圆心），再移动窗口，减少“错位一帧”
			for (var i = 0; i < _thumbnails.Count; i++)
			{
				var band = CreateBandGeometry(i);
				var sourceLeft = screenCenter.X - band.HalfWidth;
				var sourceTop = top + band.LocalTop;
				var destLeft = _radius - band.HalfWidth;

				// 首次：写全套属性；之后移动：只改 Source（Destination 在客户区固定）
				var flags = _thumbnailLayoutConfigured
					? NativeMethods.DwmTnpRectSource
					: NativeMethods.DwmTnpRectDestination |
					  NativeMethods.DwmTnpRectSource |
					  NativeMethods.DwmTnpOpacity |
					  NativeMethods.DwmTnpVisible |
					  NativeMethods.DwmTnpSourceClientAreaOnly;

				var properties = new NativeMethods.DwmThumbnailProperties
				{
					Flags = flags,
					Destination = new NativeMethods.Rect(
						destLeft,
						band.LocalTop,
						destLeft + band.Width,
						band.LocalTop + band.Height),
					Source = new NativeMethods.Rect(
						sourceLeft - sourceWindowRect.Left,
						sourceTop - sourceWindowRect.Top,
						sourceLeft - sourceWindowRect.Left + band.Width,
						sourceTop - sourceWindowRect.Top + band.Height),
					Opacity = byte.MaxValue,
					Visible = true,
					SourceClientAreaOnly = false
				};

				var hr = NativeMethods.DwmUpdateThumbnailProperties(_thumbnails[i], ref properties);
				if (hr != 0)
				{
					error = $"DwmUpdateThumbnailProperties 失败：0x{hr:X8}。";
					return false;
				}
			}

			_thumbnailLayoutConfigured = true;

			if (moveWindow && (_lastX != left || _lastY != top))
			{
				// 原位移动：不改 Z、不激活、不改尺寸，避免 Defer 显隐换帧
				const uint moveFlags =
					NativeMethods.SwpNoSize |
					NativeMethods.SwpNoZOrder |
					NativeMethods.SwpNoActivate |
					NativeMethods.SwpNoOwnerZOrder;
				if (!NativeMethods.SetWindowPos(Handle, nint.Zero, left, top, 0, 0, moveFlags))
				{
					error = "无法移动视觉圆：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
					return false;
				}

				_lastX = left;
				_lastY = top;
			}

			error = null;
			return true;
		}

		private void ApplyCircularRegion()
		{
			if (_circularRegionApplied || !IsHandleCreated)
			{
				return;
			}

			// CreateEllipticRgn 右/下边界是开区间
			var region = NativeMethods.CreateEllipticRgn(0, 0, _diameter, _diameter);
			if (region == nint.Zero)
			{
				return;
			}

			// 成功时系统接管 region；失败则自行删除
			if (NativeMethods.SetWindowRgn(Handle, region, true) == 0)
			{
				_ = NativeMethods.DeleteObject(region);
				return;
			}

			_circularRegionApplied = true;
		}

		private PortalBandGeometry CreateBandGeometry(int index)
		{
			var localTop = index * BandHeight;
			var height = Math.Min(BandHeight, _diameter - localTop);
			var midY = localTop + height / 2.0 - _radius;
			var halfWidth = Math.Max(1, (int)Math.Floor(Math.Sqrt(Math.Max(0.0, (_radius * _radius) - midY * midY))));
			return new PortalBandGeometry(localTop, height, halfWidth);
		}

		protected override void WndProc(ref Message message)
		{
			if (message.Msg == WmNcHitTest)
			{
				message.Result = HtTransparent;
				return;
			}

			if (message.Msg == WmMouseActivate)
			{
				message.Result = MaNoActivate;
				return;
			}

			base.WndProc(ref message);
		}

		protected override void Dispose(bool disposing)
		{
			Hide();
			UnregisterThumbnails();
			base.Dispose(disposing);
		}

		private void UnregisterThumbnails()
		{
			foreach (var thumbnail in _thumbnails)
			{
				_ = NativeMethods.DwmUnregisterThumbnail(thumbnail);
			}

			_thumbnails.Clear();
			_thumbnailLayoutConfigured = false;
		}
	}

	private readonly record struct PortalBandGeometry(int LocalTop, int Height, int HalfWidth)
	{
		internal int Width => HalfWidth * 2 + 1;
	}

	private readonly int _radius;

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
	{
		_radius = radius;
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
			error = "圆洞下方没有可用于视觉预览的窗口。";
			return false;
		}

		try
		{
			var result = Invoke(manager =>
				manager.TryShowPortal(sourceWindow, protectedWindow, screenCenter, _radius, out var detail)
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
				manager.TryUpdatePortal(screenCenter, _radius, out var detail)
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
			// 关闭路径忽略
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

		if (!_thread.Join(2000))
		{
			// best-effort
		}

		_ready.Dispose();
		GC.SuppressFinalize(this);
	}

	private void RunMessageLoop()
	{
		try
		{
			_manager = new PortalOverlayManager();
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
