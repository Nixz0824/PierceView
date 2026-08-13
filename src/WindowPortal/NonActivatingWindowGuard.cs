using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace WindowPortal;

internal sealed class NonActivatingWindowGuard : IDisposable
{
	private readonly record struct GuardedWindowState(uint ProcessId, nint OriginalExtendedStyle, bool ChangedStyle);

	private readonly Dictionary<nint, GuardedWindowState> _windows = new Dictionary<nint, GuardedWindowState>();

	internal int Count => _windows.Count;

	internal bool Contains(nint window)
	{
		return _windows.ContainsKey(window);
	}

	internal bool TryEnable(nint window, out string? error)
	{
		Restore();
		return TryAdd(window, out error);
	}

	internal bool TryAdd(nint window, out string? error)
	{
		if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
		{
			error = "后台窗口已经不可用，无法启用非激活交互。";
			return false;
		}
		if (_windows.ContainsKey(window))
		{
			error = null;
			return true;
		}
		NativeMethods.GetWindowThreadProcessId(window, out var processId);
		if (processId == 0)
		{
			error = "无法识别后台窗口的进程。";
			return false;
		}
		Marshal.SetLastPInvokeError(0);
		nint windowLongPtr = NativeMethods.GetWindowLongPtr(window, -20);
		int lastPInvokeError = Marshal.GetLastPInvokeError();
		if (windowLongPtr == IntPtr.Zero && lastPInvokeError != 0)
		{
			error = Win32Error("无法读取后台窗口的扩展样式", lastPInvokeError);
			return false;
		}
		bool flag = (((IntPtr)windowLongPtr).ToInt64() & NativeMethods.WsExNoActivate) == 0;
		if (flag)
		{
			nint newValue = new IntPtr(
				((IntPtr)windowLongPtr).ToInt64() | NativeMethods.WsExNoActivate);
			Marshal.SetLastPInvokeError(0);
			nint num = NativeMethods.SetWindowLongPtr(window, -20, newValue);
			int lastPInvokeError2 = Marshal.GetLastPInvokeError();
			if (num == IntPtr.Zero && lastPInvokeError2 != 0)
			{
				error = Win32Error("无法阻止后台窗口在点击时置前", lastPInvokeError2);
				return false;
			}
			if (!RefreshWindowStyle(window))
			{
				int lastPInvokeError3 = Marshal.GetLastPInvokeError();
				NativeMethods.SetWindowLongPtr(window, -20, windowLongPtr);
				RefreshWindowStyle(window);
				error = Win32Error("无法刷新后台窗口的非激活样式", lastPInvokeError3);
				return false;
			}
		}
		_windows.Add(window, new GuardedWindowState(processId, windowLongPtr, flag));
		error = null;
		return true;
	}

	internal bool TryRemove(nint window)
	{
		if (!_windows.Remove(window, out var guardedWindowState))
		{
			return false;
		}

		RestoreWindow(window, guardedWindowState);
		return true;
	}

	internal void Restore()
	{
		KeyValuePair<nint, GuardedWindowState>[] array = _windows.ToArray();
		_windows.Clear();
		foreach (var keyValuePair in array)
		{
			RestoreWindow(keyValuePair.Key, keyValuePair.Value);
		}
	}

	public void Dispose()
	{
		Restore();
		GC.SuppressFinalize(this);
	}

	private static bool IsSameWindow(nint window, uint expectedProcessId)
	{
		if (!NativeMethods.IsWindow(window))
		{
			return false;
		}
		NativeMethods.GetWindowThreadProcessId(window, out var processId);
		return processId == expectedProcessId;
	}

	private static void RestoreWindow(
		nint window,
		GuardedWindowState guardedWindowState)
	{
		if (!guardedWindowState.ChangedStyle ||
			!IsSameWindow(window, guardedWindowState.ProcessId))
		{
			return;
		}

		NativeMethods.SetWindowLongPtr(
			window,
			NativeMethods.GwlExStyle,
			guardedWindowState.OriginalExtendedStyle);
		RefreshWindowStyle(window);
	}

	private static bool RefreshWindowStyle(nint window)
	{
		return NativeMethods.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 55u);
	}

	private static string Win32Error(string message, int error)
	{
		if (error != 0)
		{
			return $"{message}：{new Win32Exception(error).Message}（{error}）";
		}
		return message;
	}
}
