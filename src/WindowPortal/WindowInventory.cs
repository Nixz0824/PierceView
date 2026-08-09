using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace WindowPortal;

internal static class WindowInventory
{
	private sealed record WindowEntry(nint Handle, bool IsForeground, uint ProcessId, string ProcessName, string ClassName, string Title, NativeMethods.Rect Bounds);

	internal static int PrintVisibleWindows()
	{
		List<WindowEntry> entries = new List<WindowEntry>();
		nint foregroundWindow = NativeMethods.GetForegroundWindow();
		if (!NativeMethods.EnumWindows(delegate(nint window, nint unusedParameter)
		{
			if (!NativeMethods.IsWindowVisible(window))
			{
				return true;
			}
			NativeMethods.GetWindowThreadProcessId(window, out var processId);
			NativeMethods.GetWindowRect(window, out var rect);
			entries.Add(new WindowEntry(window, window == foregroundWindow, processId, GetProcessName(processId), NativeMethods.GetWindowClassName(window), NativeMethods.GetWindowTitle(window), rect));
			return true;
		}, IntPtr.Zero))
		{
			Console.Error.WriteLine("无法枚举顶层窗口。");
			return 1;
		}
		foreach (WindowEntry item in from entry in entries
			orderby entry.ProcessName, entry.Handle
			select entry)
		{
			Console.WriteLine($"HWND=0x{item.Handle:X} FOREGROUND={item.IsForeground} BOUNDS={item.Bounds.Left},{item.Bounds.Top},{item.Bounds.Right},{item.Bounds.Bottom} PID={item.ProcessId} PROCESS={item.ProcessName} CLASS={item.ClassName} TITLE={item.Title}");
		}
		return 0;
	}

	private static string GetProcessName(uint processId)
	{
		try
		{
			return Process.GetProcessById(checked((int)processId)).ProcessName;
		}
		catch
		{
			return "（无法读取）";
		}
	}
}
