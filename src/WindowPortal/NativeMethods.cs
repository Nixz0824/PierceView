using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowPortal;

internal static class NativeMethods
{
	internal delegate bool EnumWindowsCallback(nint window, nint parameter);

	internal delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime);

	internal readonly record struct Point(int X, int Y);

	internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
	{
		internal int Width => Right - Left;

		internal int Height => Bottom - Top;

		internal bool Contains(Point point)
		{
			if (point.X >= Left && point.X < Right && point.Y >= Top)
			{
				return point.Y < Bottom;
			}
			return false;
		}
	}

	internal struct DwmThumbnailProperties
	{
		internal uint Flags;

		internal Rect Destination;

		internal Rect Source;

		internal byte Opacity;

		[MarshalAs(UnmanagedType.Bool)]
		internal bool Visible;

		[MarshalAs(UnmanagedType.Bool)]
		internal bool SourceClientAreaOnly;
	}

	internal const int ErrorRegion = 0;

	internal const int NullRegion = 1;

	internal const int SimpleRegion = 2;

	internal const int ComplexRegion = 3;

	internal const int RgnDiff = 4;

	internal const uint GaRoot = 2u;

	internal const uint GaRootOwner = 3u;

	internal const uint GwOwner = 4u;

	internal const uint GwHwndNext = 2u;

	internal const uint GwHwndPrevious = 3u;

	internal const int GwlStyle = -16;

	internal const int GwlExStyle = -20;

	internal const uint DwmwaCloaked = 14u;

	internal const uint DwmTnpRectDestination = 1u;

	internal const uint DwmTnpRectSource = 2u;

	internal const uint DwmTnpOpacity = 4u;

	internal const uint DwmTnpVisible = 8u;

	internal const uint DwmTnpSourceClientAreaOnly = 16u;

	internal const uint SwpNoSize = 1u;

	internal const uint SwpNoMove = 2u;

	internal const uint SwpNoZOrder = 4u;

	internal const uint SwpNoActivate = 16u;

	internal const uint SwpFrameChanged = 32u;

	internal const uint SwpShowWindow = 64u;

	internal const uint SwpHideWindow = 128u;

	internal const uint SwpNoOwnerZOrder = 512u;

	internal static readonly nint HwndTopMost = new IntPtr(-1);

	internal const int VkF8 = 119;

	private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

	[DllImport("user32.dll")]
	internal static extern short GetAsyncKeyState(int virtualKey);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetCursorPos(out Point point);

	[DllImport("user32.dll")]
	internal static extern nint WindowFromPoint(Point point);

	[DllImport("user32.dll")]
	internal static extern nint GetAncestor(nint window, uint flags);

	[DllImport("user32.dll")]
	internal static extern nint GetParent(nint window);

	[DllImport("user32.dll")]
	internal static extern nint GetWindow(nint window, uint command);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool EnumChildWindows(nint parent, EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool IsWindowEnabled(nint window);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
	internal static extern nint GetWindowLongPtr(nint window, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	internal static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

	[DllImport("user32.dll")]
	internal static extern nint GetDesktopWindow();

	[DllImport("user32.dll")]
	internal static extern nint GetShellWindow();

	[DllImport("user32.dll")]
	internal static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool SetForegroundWindow(nint window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool BringWindowToTop(nint window);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool AttachThreadInput(uint firstThread, uint secondThread, [MarshalAs(UnmanagedType.Bool)] bool attach);

	[DllImport("kernel32.dll")]
	internal static extern uint GetCurrentThreadId();

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern nint SetWinEventHook(uint eventMinimum, uint eventMaximum, nint eventHookModule, WinEventCallback callback, uint processId, uint threadId, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool UnhookWinEvent(nint hook);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetWindowRect(nint window, out Rect rect);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern nint BeginDeferWindowPos(int windowCount);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern nint DeferWindowPos(nint deferredWindowPosition, nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool EndDeferWindowPos(nint deferredWindowPosition);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern int GetWindowRgn(nint window, nint region);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool IsWindow(nint window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern int GetWindowTextLength(nint window);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	internal static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern nint CreateEllipticRgn(int left, int top, int right, int bottom);

	[DllImport("gdi32.dll", SetLastError = true)]
	internal static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool DeleteObject(nint value);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool PtInRegion(nint region, int x, int y);

	[DllImport("dwmapi.dll")]
	internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int valueSize);

	[DllImport("dwmapi.dll")]
	internal static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

	[DllImport("dwmapi.dll")]
	internal static extern int DwmUnregisterThumbnail(nint thumbnail);

	[DllImport("dwmapi.dll")]
	internal static extern int DwmFlush();

	[DllImport("dwmapi.dll")]
	internal static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);

	internal static void TryEnablePerMonitorDpiAwareness()
	{
		SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
	}

	internal static bool IsKeyDown(int virtualKey)
	{
		return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
	}

	internal static string GetWindowTitle(nint window)
	{
		StringBuilder stringBuilder = new StringBuilder(Math.Max(GetWindowTextLength(window) + 1, 2));
		GetWindowText(window, stringBuilder, stringBuilder.Capacity);
		if (!string.IsNullOrWhiteSpace(stringBuilder.ToString()))
		{
			return stringBuilder.ToString();
		}
		return "（无标题）";
	}

	internal static string GetWindowClassName(nint window)
	{
		StringBuilder stringBuilder = new StringBuilder(256);
		GetClassName(window, stringBuilder, stringBuilder.Capacity);
		if (!string.IsNullOrWhiteSpace(stringBuilder.ToString()))
		{
			return stringBuilder.ToString();
		}
		return "（未知类名）";
	}
}
