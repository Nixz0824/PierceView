using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WindowPortal;

internal sealed class DwmPortalOverlay : IDisposable
{
	private sealed class PortalOverlayManager : Form
	{
		private PortalPreviewForm? _frontPreview;

		private PortalPreviewForm? _backPreview;

		private readonly ForegroundZOrderGuard _foregroundGuard = new ForegroundZOrderGuard();

		private readonly System.Windows.Forms.Timer _zOrderGuardTimer;

		private NativeMethods.Point? _lastScreenCenter;

		private NativeMethods.Rect? _lastSourceRect;

		private bool _portalVisible;

		internal bool PortalVisible
		{
			get
			{
				if (_portalVisible)
				{
					return _frontPreview != null;
				}
				return false;
			}
		}

		internal nint SourceWindow { get; private set; }

		internal int ForegroundRecoveryCount => _foregroundGuard.RecoveryCount;

		internal int BackgroundPromotionCount => _foregroundGuard.PromotionCount;

		internal PortalOverlayManager()
		{
			base.FormBorderStyle = FormBorderStyle.None;
			base.ShowInTaskbar = false;
			base.StartPosition = FormStartPosition.Manual;
			base.Opacity = 0.0;
			base.Size = new Size(1, 1);
			_zOrderGuardTimer = new System.Windows.Forms.Timer
			{
				Interval = 10
			};
			_zOrderGuardTimer.Tick += delegate
			{
				_foregroundGuard.EnsurePreserved();
			};
		}

		internal bool TryShowPortal(nint sourceWindow, nint protectedWindow, NativeMethods.Point screenCenter, int radius, out string? error)
		{
			HidePortal();
			SourceWindow = sourceWindow;
			if (!_foregroundGuard.TryEnable(sourceWindow, protectedWindow, screenCenter, radius, out error))
			{
				HidePortal();
				return false;
			}
			_frontPreview = new PortalPreviewForm(radius);
			_backPreview = new PortalPreviewForm(radius);
			if (!_frontPreview.TryRegisterSource(sourceWindow, out error) || !_backPreview.TryRegisterSource(sourceWindow, out error))
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
			if (!_frontPreview.TryPrepare(rect, screenCenter, radius, out error) || !TrySetPreviewVisibility(null, _frontPreview, out error))
			{
				HidePortal();
				return false;
			}
			NativeMethods.DwmFlush();
			_portalVisible = true;
			_zOrderGuardTimer.Start();
			_lastScreenCenter = screenCenter;
			_lastSourceRect = rect;
			error = null;
			return true;
		}

		internal bool TryUpdatePortal(NativeMethods.Point screenCenter, int radius, out string? error)
		{
			_foregroundGuard.UpdatePortalGeometry(screenCenter, radius);
			_foregroundGuard.EnsurePreserved();
			if (!_portalVisible || _frontPreview == null || _backPreview == null || !NativeMethods.GetWindowRect(SourceWindow, out var rect))
			{
				error = "视觉穿透源窗口已经不可用。";
				return false;
			}
			NativeMethods.Point? lastScreenCenter = _lastScreenCenter;
			if (lastScreenCenter.HasValue && lastScreenCenter.GetValueOrDefault() == screenCenter && _lastSourceRect == rect)
			{
				error = null;
				return true;
			}
			if (!_backPreview.TryPrepare(rect, screenCenter, radius, out error) || !TrySetPreviewVisibility(_frontPreview, _backPreview, out error))
			{
				return false;
			}
			PortalPreviewForm backPreview = _backPreview;
			PortalPreviewForm frontPreview = _frontPreview;
			_frontPreview = backPreview;
			_backPreview = frontPreview;
			_lastScreenCenter = screenCenter;
			_lastSourceRect = rect;
			NativeMethods.DwmFlush();
			error = null;
			return true;
		}

		internal void HidePortal()
		{
			_zOrderGuardTimer.Stop();
			if (_portalVisible && _frontPreview != null)
			{
				TrySetPreviewVisibility(_frontPreview, null, out _);
			}
			_portalVisible = false;
			_frontPreview?.Dispose();
			_backPreview?.Dispose();
			_foregroundGuard.Restore();
			_frontPreview = null;
			_backPreview = null;
			_lastScreenCenter = null;
			_lastSourceRect = null;
			SourceWindow = IntPtr.Zero;
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

		private static bool TrySetPreviewVisibility(PortalPreviewForm? previewToHide, PortalPreviewForm? previewToShow, out string? error)
		{
			nint num = NativeMethods.BeginDeferWindowPos(((previewToHide != null) ? 1 : 0) + ((previewToShow != null) ? 1 : 0));
			if (num == IntPtr.Zero)
			{
				error = LastWin32Error("无法开始视觉圆的原子换帧");
				return false;
			}
			uint num2 = 531u;
			if (previewToHide != null)
			{
				num = NativeMethods.DeferWindowPos(num, previewToHide.Handle, NativeMethods.HwndTopMost, 0, 0, 0, 0, num2 | 0x80);
				if (num == IntPtr.Zero)
				{
					error = LastWin32Error("无法隐藏上一帧视觉圆");
					return false;
				}
			}
			if (previewToShow != null)
			{
				num = NativeMethods.DeferWindowPos(num, previewToShow.Handle, NativeMethods.HwndTopMost, 0, 0, 0, 0, num2 | 0x40);
				if (num == IntPtr.Zero)
				{
					error = LastWin32Error("无法显示下一帧视觉圆");
					return false;
				}
			}
			if (!NativeMethods.EndDeferWindowPos(num))
			{
				error = LastWin32Error("无法提交视觉圆的原子换帧");
				return false;
			}
			error = null;
			return true;
		}

		private static string LastWin32Error(string message)
		{
			int lastWin32Error = Marshal.GetLastWin32Error();
			if (lastWin32Error != 0)
			{
				return $"{message}：{new Win32Exception(lastWin32Error).Message}（{lastWin32Error}）";
			}
			return message;
		}
	}

	private sealed class PortalPreviewForm : Form
	{
		private const int BandHeight = 3;

		private const int WsExTransparent = 32;

		private const int WsExToolWindow = 128;

		private const int WsExNoActivate = 134217728;

		private const int WmNcHitTest = 132;

		private const int WmMouseActivate = 33;

		private static readonly nint HtTransparent = new IntPtr(-1);

		private static readonly nint MaNoActivate = new IntPtr(3);

		private static readonly Color PortalTransparencyColor = Color.FromArgb(255, 1, 0, 255);

		private readonly int _radius;

		private readonly int _diameter;

		private readonly List<nint> _thumbnails = new List<nint>();

		private bool _thumbnailLayoutConfigured;

		protected override bool ShowWithoutActivation => true;

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams createParams = base.CreateParams;
				createParams.ExStyle |= 134217888;
				return createParams;
			}
		}

		internal PortalPreviewForm(int radius)
		{
			_radius = radius;
			_diameter = checked(radius * 2 + 1);
			base.FormBorderStyle = FormBorderStyle.None;
			base.ShowInTaskbar = false;
			base.StartPosition = FormStartPosition.Manual;
			base.AutoScaleMode = AutoScaleMode.None;
			BackColor = PortalTransparencyColor;
			base.TransparencyKey = PortalTransparencyColor;
			base.TopMost = true;
			base.Bounds = new Rectangle(0, 0, _diameter, _diameter);
		}

		internal bool TryRegisterSource(nint sourceWindow, out string? error)
		{
			int num = (_diameter + 3 - 1) / 3;
			for (int i = 0; i < num; i++)
			{
				nint thumbnail;
				int num2 = NativeMethods.DwmRegisterThumbnail(base.Handle, sourceWindow, out thumbnail);
				if (num2 != 0 || thumbnail == IntPtr.Zero)
				{
					error = $"DwmRegisterThumbnail 失败：0x{num2:X8}。";
					UnregisterThumbnails();
					return false;
				}
				_thumbnails.Add(thumbnail);
			}
			error = null;
			return true;
		}

		internal bool TryPrepare(NativeMethods.Rect sourceWindowRect, NativeMethods.Point screenCenter, int radius, out string? error)
		{
			if (_thumbnails.Count == 0)
			{
				error = "DWM 圆形缩略图尚未注册。";
				return false;
			}
			int x = screenCenter.X - radius;
			int num = screenCenter.Y - radius;
			base.Bounds = new Rectangle(x, num, _diameter, _diameter);
			for (int i = 0; i < _thumbnails.Count; i++)
			{
				PortalBandGeometry portalBandGeometry = CreateBandGeometry(i);
				int num2 = screenCenter.X - portalBandGeometry.HalfWidth;
				int num3 = num + portalBandGeometry.LocalTop;
				int num4 = _radius - portalBandGeometry.HalfWidth;
				NativeMethods.DwmThumbnailProperties properties = new NativeMethods.DwmThumbnailProperties
				{
					Flags = (_thumbnailLayoutConfigured ? 2u : 31u),
					Destination = new NativeMethods.Rect(num4, portalBandGeometry.LocalTop, num4 + portalBandGeometry.Width, portalBandGeometry.LocalTop + portalBandGeometry.Height),
					Source = new NativeMethods.Rect(num2 - sourceWindowRect.Left, num3 - sourceWindowRect.Top, num2 - sourceWindowRect.Left + portalBandGeometry.Width, num3 - sourceWindowRect.Top + portalBandGeometry.Height),
					Opacity = byte.MaxValue,
					Visible = true,
					SourceClientAreaOnly = false
				};
				int num5 = NativeMethods.DwmUpdateThumbnailProperties(_thumbnails[i], ref properties);
				if (num5 != 0)
				{
					error = $"DwmUpdateThumbnailProperties 失败：0x{num5:X8}。";
					return false;
				}
			}
			_thumbnailLayoutConfigured = true;
			error = null;
			return true;
		}

		private PortalBandGeometry CreateBandGeometry(int index)
		{
			int num = index * 3;
			int num2 = Math.Min(3, _diameter - num);
			double num3 = (double)num + (double)num2 / 2.0 - (double)_radius;
			double d = Math.Max(0.0, (double)(_radius * _radius) - num3 * num3);
			int halfWidth = Math.Max(1, (int)Math.Floor(Math.Sqrt(d)));
			return new PortalBandGeometry(num, num2, halfWidth);
		}

		protected override void WndProc(ref Message message)
		{
			if (message.Msg == 132)
			{
				message.Result = HtTransparent;
			}
			else if (message.Msg == 33)
			{
				message.Result = MaNoActivate;
			}
			else
			{
				base.WndProc(ref message);
			}
		}

		protected override void Dispose(bool disposing)
		{
			Hide();
			UnregisterThumbnails();
			base.Dispose(disposing);
		}

		private void UnregisterThumbnails()
		{
			foreach (nint thumbnail in _thumbnails)
			{
				NativeMethods.DwmUnregisterThumbnail(thumbnail);
			}
			_thumbnails.Clear();
		}
	}

	private readonly record struct PortalBandGeometry(int LocalTop, int Height, int HalfWidth)
	{
		internal int Width => HalfWidth * 2 + 1;
	}

	private readonly int _radius;

	private readonly Thread _thread;

	private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(initialState: false);

	private PortalOverlayManager? _manager;

	private ApplicationContext? _applicationContext;

	private Exception? _startupException;

	private bool _disposed;

	internal bool IsVisible => Invoke((PortalOverlayManager manager) => manager.PortalVisible);

	internal nint SourceWindow => Invoke((PortalOverlayManager manager) => manager.SourceWindow);

	internal int ForegroundRecoveryCount => Invoke((PortalOverlayManager manager) => manager.ForegroundRecoveryCount);

	internal int BackgroundPromotionCount => Invoke((PortalOverlayManager manager) => manager.BackgroundPromotionCount);

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
		if (_startupException != null)
		{
			throw new InvalidOperationException("无法启动视觉穿透覆盖层。", _startupException);
		}
	}

	internal bool TryShow(nint sourceWindow, nint protectedWindow, NativeMethods.Point screenCenter, out string? error)
	{
		if (sourceWindow == IntPtr.Zero || !NativeMethods.IsWindow(sourceWindow))
		{
			error = "圆洞下方没有可用于视觉预览的窗口。";
			return false;
		}
		try
		{
			(bool Success, string? Detail) tuple = Invoke((PortalOverlayManager manager) => (!manager.TryShowPortal(sourceWindow, protectedWindow, screenCenter, _radius, out string? error2)) ? (Success: false, Detail: error2) : (Success: true, Detail: null));
			error = tuple.Item2;
			return tuple.Item1;
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
			(bool Success, string? Detail) tuple = Invoke((PortalOverlayManager manager) => (!manager.TryUpdatePortal(screenCenter, _radius, out string? error2)) ? (Success: false, Detail: error2) : (Success: true, Detail: null));
			error = tuple.Item2;
			return tuple.Item1;
		}
		catch (Exception ex)
		{
			error = "无法更新视觉穿透覆盖层：" + ex.Message;
			return false;
		}
	}

	internal void Hide()
	{
		if (_disposed || _manager == null)
		{
			return;
		}
		try
		{
			Invoke(delegate(PortalOverlayManager manager)
			{
				manager.HidePortal();
				return true;
			});
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		Hide();
		_disposed = true;
		if (_manager != null && _applicationContext != null)
		{
			try
			{
				_manager.BeginInvoke(delegate
				{
					_applicationContext.ExitThread();
				});
			}
			catch
			{
			}
		}
		if (_thread.IsAlive && Thread.CurrentThread != _thread)
		{
			_thread.Join(TimeSpan.FromSeconds(2.0));
		}
		_ready.Dispose();
		GC.SuppressFinalize(this);
	}

	private T Invoke<T>(Func<PortalOverlayManager, T> operation)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		PortalOverlayManager portalOverlayManager = _manager ?? throw new InvalidOperationException("视觉穿透覆盖层尚未就绪。");
		if (portalOverlayManager.InvokeRequired)
		{
			return (T)portalOverlayManager.Invoke(operation, portalOverlayManager);
		}
		return operation(portalOverlayManager);
	}

	private void RunMessageLoop()
	{
		try
		{
			Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
			_applicationContext = new ApplicationContext();
			_manager = new PortalOverlayManager();
			_ = _manager.Handle;
			_ready.Set();
			Application.Run(_applicationContext);
		}
		catch (Exception startupException)
		{
			_startupException = startupException;
			_ready.Set();
		}
		finally
		{
			_manager?.Dispose();
			_manager = null;
			_applicationContext?.Dispose();
			_applicationContext = null;
		}
	}
}
