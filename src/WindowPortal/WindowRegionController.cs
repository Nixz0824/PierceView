using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowPortal;

internal sealed class WindowRegionController : IDisposable
{
	private sealed class RegionWindowState(nint window, nint originalRegion, bool hadOriginalRegion)
	{
		internal nint Window { get; } = window;

		internal nint OriginalRegion { get; set; } = originalRegion;

		internal bool HadOriginalRegion { get; } = hadOriginalRegion;

		internal NativeMethods.Rect? LastWindowRect { get; set; }
	}

	private static readonly HashSet<string> ProtectedShellWindowClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

	private readonly PortalGeometry _geometry;

	private readonly List<RegionWindowState> _windows = new List<RegionWindowState>();

	private nint _rootWindow;

	private NativeMethods.Point? _lastCursor;

	internal bool IsActive => _rootWindow != IntPtr.Zero;

	internal nint ActiveWindow => _rootWindow;

	internal int ActiveLayerCount => _windows.Count;

	internal string ActiveDescription
	{
		get
		{
			if (!IsActive)
			{
				return "（无）";
			}
			return $"{NativeMethods.GetWindowTitle(_rootWindow)} [{NativeMethods.GetWindowClassName(_rootWindow)}] HWND=0x{_rootWindow:X}，裁剪层数={ActiveLayerCount}";
		}
	}

	internal WindowRegionController(int radius)
		: this(PortalGeometry.Circle(radius))
	{
	}

	internal WindowRegionController(PortalGeometry geometry)
	{
		_geometry = geometry;
	}

	internal bool TryBeginAtCursor(out string message)
	{
		if (!NativeMethods.GetCursorPos(out var point))
		{
			message = LastWin32Error("无法读取鼠标位置");
			return false;
		}
		nint num = NativeMethods.WindowFromPoint(point);
		nint num2 = ((num == IntPtr.Zero) ? IntPtr.Zero : NativeMethods.GetAncestor(num, 2u));
		if (num2 == IntPtr.Zero)
		{
			message = "鼠标下没有可操作的顶层窗口。";
			return false;
		}
		return TryBegin(num2, out message);
	}

	internal bool TryBegin(nint window, out string message)
	{
		Restore();
		if (!ValidateRootWindow(window, out message))
		{
			return false;
		}
		if (!TryCaptureWindowState(window, out RegionWindowState state, out message))
		{
			return false;
		}
		_rootWindow = window;
		_windows.Add(state);
		_lastCursor = null;
		string value = string.Join(", ", _windows.Select((RegionWindowState regionWindowState) => NativeMethods.GetWindowClassName(regionWindowState.Window)).Distinct());
		message = $"已锁定：{ActiveDescription}（{value}）";
		return true;
	}

	internal bool UpdateAtCursor(out string? error)
	{
		if (!NativeMethods.GetCursorPos(out var point))
		{
			error = LastWin32Error("无法读取鼠标位置");
			return false;
		}
		return Update(point, out error);
	}

	internal bool Update(NativeMethods.Point screenPoint, out string? error)
	{
		error = null;
		if (!IsActive)
		{
			error = "当前没有锁定窗口。";
			return false;
		}
		if (!NativeMethods.IsWindow(_rootWindow))
		{
			error = "目标窗口已经关闭。";
			ResetState(deleteOriginalRegions: true);
			return false;
		}
		Dictionary<nint, NativeMethods.Rect> dictionary = new Dictionary<nint, NativeMethods.Rect>();
		bool flag = _lastCursor != screenPoint;
		foreach (RegionWindowState window in _windows)
		{
			if (!NativeMethods.IsWindow(window.Window) || !NativeMethods.GetWindowRect(window.Window, out var rect) || rect.Width <= 1 || rect.Height <= 1)
			{
				error = "ChatGPT 的渲染窗口在裁剪期间被重建，已执行安全恢复。";
				Restore();
				return false;
			}
			dictionary[window.Window] = rect;
			flag |= window.LastWindowRect != rect;
		}
		if (!flag)
		{
			return true;
		}
		foreach (RegionWindowState item in _windows.OrderBy((RegionWindowState state) => (state.Window == _rootWindow) ? 1 : 0))
		{
			NativeMethods.Rect rect2 = dictionary[item.Window];
			if (!ApplyHole(item.Window, rect2, screenPoint, out error))
			{
				Restore();
				return false;
			}
			item.LastWindowRect = rect2;
		}
		_lastCursor = screenPoint;
		return true;
	}

	internal RegionInspection InspectCurrentHole(NativeMethods.Point screenPoint)
	{
		if (!IsActive || !NativeMethods.GetWindowRect(_rootWindow, out var rect))
		{
			return new RegionInspection(0, CenterExcluded: false, "目标窗口不可用。");
		}
		nint num = NativeMethods.CreateRectRgn(0, 0, 0, 0);
		if (num == IntPtr.Zero)
		{
			return new RegionInspection(0, CenterExcluded: false, LastWin32Error("无法创建检查区域"));
		}
		try
		{
			int windowRgn = NativeMethods.GetWindowRgn(_rootWindow, num);
			if (windowRgn == 0)
			{
				return new RegionInspection(windowRgn, CenterExcluded: false, "目标窗口没有可读取的显式区域。");
			}
			NativeMethods.Point point = ToWindowCoordinates(rect, screenPoint);
			bool flag = !NativeMethods.PtInRegion(num, point.X, point.Y);
			string detail = (flag ? $"透视中心已从目标窗口区域中排除；同步裁剪层数={ActiveLayerCount}。" : "透视中心仍在目标窗口区域内。");
			return new RegionInspection(windowRgn, flag, detail);
		}
		finally
		{
			NativeMethods.DeleteObject(num);
		}
	}

	internal static int ReadWindowRegionType(nint window)
	{
		nint num = NativeMethods.CreateRectRgn(0, 0, 0, 0);
		if (num == IntPtr.Zero)
		{
			return 0;
		}
		try
		{
			return NativeMethods.GetWindowRgn(window, num);
		}
		finally
		{
			NativeMethods.DeleteObject(num);
		}
	}

	internal void Restore()
	{
		if (!IsActive)
		{
			return;
		}
		nint rootWindow = _rootWindow;
		RegionWindowState[] source = _windows.ToArray();
		_rootWindow = IntPtr.Zero;
		_windows.Clear();
		_lastCursor = null;
		foreach (RegionWindowState item in source.OrderBy((RegionWindowState state) => (state.Window == rootWindow) ? 1 : 0))
		{
			RestoreWindowState(item);
		}
	}

	public void Dispose()
	{
		Restore();
		GC.SuppressFinalize(this);
	}

	internal static NativeMethods.Point ToWindowCoordinates(NativeMethods.Rect windowRect, NativeMethods.Point screenPoint)
	{
		return new NativeMethods.Point(screenPoint.X - windowRect.Left, screenPoint.Y - windowRect.Top);
	}

	internal static NativeMethods.Rect CreateHoleBounds(NativeMethods.Point center, int radius)
	{
		return new NativeMethods.Rect(center.X - radius, center.Y - radius, center.X + radius + 1, center.Y + radius + 1);
	}

	private bool ValidateRootWindow(nint window, out string message)
	{
		if (!NativeMethods.IsWindow(window) || !NativeMethods.IsWindowVisible(window))
		{
			message = $"窗口 0x{window:X} 不存在或不可见。";
			return false;
		}
		string windowClassName = NativeMethods.GetWindowClassName(window);
		if (window == NativeMethods.GetDesktopWindow() || window == NativeMethods.GetShellWindow() || ProtectedShellWindowClasses.Contains(windowClassName))
		{
			message = "为保护桌面和任务栏，原型不会操作系统 Shell 窗口 [" + windowClassName + "]。";
			return false;
		}
		NativeMethods.GetWindowThreadProcessId(window, out var processId);
		if (processId == Environment.ProcessId)
		{
			message = "为避免锁住控制窗口，原型不会操作自身窗口。";
			return false;
		}
		if (!NativeMethods.GetWindowRect(window, out var rect) || rect.Width <= 1 || rect.Height <= 1)
		{
			message = LastWin32Error("无法读取目标窗口尺寸");
			return false;
		}
		message = string.Empty;
		return true;
	}

	private static bool TryCaptureWindowState(nint window, out RegionWindowState state, out string message)
	{
		nint num = NativeMethods.CreateRectRgn(0, 0, 0, 0);
		if (num == IntPtr.Zero)
		{
			state = null!;
			message = LastWin32Error("无法创建用于保存原始窗口区域的对象");
			return false;
		}
		int windowRgn = NativeMethods.GetWindowRgn(window, num);
		if (windowRgn == 0)
		{
			NativeMethods.DeleteObject(num);
			num = IntPtr.Zero;
		}
		state = new RegionWindowState(window, num, windowRgn != 0);
		message = string.Empty;
		return true;
	}

	private bool ApplyHole(nint window, NativeMethods.Rect windowRect, NativeMethods.Point screenPoint, out string? error)
	{
		return ApplyHoleCore(window, windowRect, screenPoint, _geometry, out error);
	}

	// Visual smoke uses the same region-update path as production so a test-only
	// redraw flag cannot silently diverge from the behavior users actually run.
	internal static bool TryApplyHoleForVisualTest(
		nint window,
		NativeMethods.Rect windowRect,
		NativeMethods.Point screenPoint,
		PortalGeometry geometry,
		out string? error)
	{
		return ApplyHoleCore(window, windowRect, screenPoint, geometry, out error);
	}

	private static bool ApplyHoleCore(
		nint window,
		NativeMethods.Rect windowRect,
		NativeMethods.Point screenPoint,
		PortalGeometry geometry,
		out string? error)
	{
		var center = ToWindowCoordinates(windowRect, screenPoint);
		// 视觉形状由独立的分层窗完整绘制。窗口 Region 只保留鼠标中心附近的
		// 小型交互孔，避免两个系统窗口换位不同步时短暂露出第二个圆/矩形。
		var rect = CreateHoleBounds(center, geometry.EffectiveInteractionRadius);
		nint num = NativeMethods.CreateRectRgn(0, 0, windowRect.Width, windowRect.Height);
		nint num2 = NativeMethods.CreateEllipticRgn(
			rect.Left,
			rect.Top,
			rect.Right,
			rect.Bottom);
		if (num == IntPtr.Zero || num2 == IntPtr.Zero)
		{
			DeleteIfOwned(num);
			DeleteIfOwned(num2);
			error = LastWin32Error("无法创建透视窗口区域");
			return false;
		}
		int num3 = NativeMethods.CombineRgn(num, num, num2, 4);
		NativeMethods.DeleteObject(num2);
		if (num3 == 0)
		{
			NativeMethods.DeleteObject(num);
			error = LastWin32Error("无法从窗口区域中减去透视区域");
			return false;
		}
		// 交互孔移动后必须让 Windows 立即重绘重新覆盖的旧位置。若关闭重绘，
		// DWM 可能短暂保留旧孔露出的像素，看起来就像透视窗在移动路径上闪现。
		if (NativeMethods.SetWindowRgn(window, num, redraw: true) == 0)
		{
			NativeMethods.DeleteObject(num);
			error = LastWin32Error($"Windows 拒绝修改渲染窗口 0x{window:X}；如果目标程序以管理员身份运行，请用相同权限启动本工具");
			return false;
		}
		error = null;
		return true;
	}

	private static void RestoreWindowState(RegionWindowState state)
	{
		nint originalRegion = state.OriginalRegion;
		state.OriginalRegion = IntPtr.Zero;
		if (!NativeMethods.IsWindow(state.Window))
		{
			DeleteIfOwned(originalRegion);
		}
		else if (state.HadOriginalRegion)
		{
			if (NativeMethods.SetWindowRgn(state.Window, originalRegion, redraw: true) == 0)
			{
				DeleteIfOwned(originalRegion);
			}
		}
		else
		{
			NativeMethods.SetWindowRgn(state.Window, IntPtr.Zero, redraw: true);
		}
	}

	private void ResetState(bool deleteOriginalRegions)
	{
		if (deleteOriginalRegions)
		{
			foreach (RegionWindowState window in _windows)
			{
				DeleteIfOwned(window.OriginalRegion);
				window.OriginalRegion = IntPtr.Zero;
			}
		}
		_rootWindow = IntPtr.Zero;
		_windows.Clear();
		_lastCursor = null;
	}

	private static void DeleteIfOwned(nint region)
	{
		if (region != IntPtr.Zero)
		{
			NativeMethods.DeleteObject(region);
		}
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
