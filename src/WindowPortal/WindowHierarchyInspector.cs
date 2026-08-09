using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace WindowPortal;

internal static class WindowHierarchyInspector
{
	private sealed record WindowSnapshot(nint Handle, int ZOrder, uint ProcessId, string ProcessName, string ClassName, string Title, NativeMethods.Rect Bounds, bool Visible, bool Cloaked, nint Parent, nint Owner, long Style, long ExStyle);

	internal static int Inspect(nint targetWindow, NativeMethods.Point? requestedPoint)
	{
		if (!NativeMethods.IsWindow(targetWindow) || !NativeMethods.GetWindowRect(targetWindow, out var rect))
		{
			Console.Error.WriteLine($"无法检查窗口 0x{targetWindow:X}。");
			return 1;
		}
		NativeMethods.Point point = requestedPoint ?? new NativeMethods.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
		NativeMethods.GetWindowThreadProcessId(targetWindow, out var targetProcessId);
		nint ancestor = NativeMethods.GetAncestor(targetWindow, 2u);
		nint ancestor2 = NativeMethods.GetAncestor(targetWindow, 3u);
		nint parent = NativeMethods.GetParent(targetWindow);
		nint window = NativeMethods.GetWindow(targetWindow, 4u);
		nint window2 = NativeMethods.WindowFromPoint(point);
		Console.WriteLine($"检查点：{point.X},{point.Y}");
		PrintRelation("TARGET", targetWindow);
		PrintRelation("WINDOW_FROM_POINT", window2);
		PrintRelation("PARENT", parent);
		PrintRelation("OWNER", window);
		PrintRelation("ROOT", ancestor);
		PrintRelation("ROOT_OWNER", ancestor2);
		PrintRegionMembership("TARGET_REGION_AT_POINT", targetWindow, point);
		PrintRegionMembership("WINDOW_FROM_POINT_REGION_AT_POINT", window2, point);
		List<WindowSnapshot> source = EnumerateTopLevelWindows();
		Console.WriteLine();
		Console.WriteLine($"同进程顶层窗口（PID={targetProcessId}，包括隐藏窗口）：");
		foreach (WindowSnapshot item in source.Where((WindowSnapshot item) => item.ProcessId == targetProcessId))
		{
			PrintSnapshot(item);
		}
		Console.WriteLine();
		Console.WriteLine("检查点上的子窗口链候选：");
		int childCount = 0;
		NativeMethods.EnumWindowsCallback callback = delegate(nint window3, nint unusedParameter)
		{
			WindowSnapshot windowSnapshot = CreateSnapshot(window3, -1);
			if (windowSnapshot.Bounds.Contains(point))
			{
				PrintSnapshot(windowSnapshot);
				childCount++;
			}
			return true;
		};
		NativeMethods.EnumChildWindows(targetWindow, callback, IntPtr.Zero);
		if (childCount == 0)
		{
			Console.WriteLine("（没有覆盖检查点的子 HWND）");
		}
		Console.WriteLine();
		Console.WriteLine("检查点处的顶层 Z-order（从上到下）：");
		foreach (WindowSnapshot item2 in source.Where((WindowSnapshot item) => item.Bounds.Contains(point)))
		{
			PrintSnapshot(item2);
		}
		return 0;
	}

	private static List<WindowSnapshot> EnumerateTopLevelWindows()
	{
		List<WindowSnapshot> snapshots = new List<WindowSnapshot>();
		int zOrder = 0;
		NativeMethods.EnumWindows(delegate(nint window, nint unusedParameter)
		{
			snapshots.Add(CreateSnapshot(window, zOrder));
			zOrder++;
			return true;
		}, IntPtr.Zero);
		return snapshots;
	}

	private static WindowSnapshot CreateSnapshot(nint window, int zOrder)
	{
		NativeMethods.GetWindowThreadProcessId(window, out var processId);
		NativeMethods.GetWindowRect(window, out var rect);
		int value = 0;
		NativeMethods.DwmGetWindowAttribute(window, 14u, out value, 4);
		return new WindowSnapshot(window, zOrder, processId, GetProcessName(processId), NativeMethods.GetWindowClassName(window), NativeMethods.GetWindowTitle(window), rect, NativeMethods.IsWindowVisible(window), value != 0, NativeMethods.GetParent(window), NativeMethods.GetWindow(window, 4u), ((IntPtr)NativeMethods.GetWindowLongPtr(window, -16)).ToInt64(), ((IntPtr)NativeMethods.GetWindowLongPtr(window, -20)).ToInt64());
	}

	private static void PrintRelation(string label, nint window)
	{
		if (window == IntPtr.Zero)
		{
			Console.WriteLine(label + "=NULL");
			return;
		}
		Console.Write(label + ": ");
		PrintSnapshot(CreateSnapshot(window, -1));
	}

	private static void PrintRegionMembership(string label, nint window, NativeMethods.Point screenPoint)
	{
		if (window == IntPtr.Zero || !NativeMethods.GetWindowRect(window, out var rect))
		{
			Console.WriteLine(label + "=不可用");
			return;
		}
		nint num = NativeMethods.CreateRectRgn(0, 0, 0, 0);
		if (num == IntPtr.Zero)
		{
			Console.WriteLine(label + "=无法创建检查 region");
			return;
		}
		try
		{
			int windowRgn = NativeMethods.GetWindowRgn(window, num);
			NativeMethods.Point point = new NativeMethods.Point(screenPoint.X - rect.Left, screenPoint.Y - rect.Top);
			bool value = ((windowRgn == 0) ? rect.Contains(screenPoint) : NativeMethods.PtInRegion(num, point.X, point.Y));
			Console.WriteLine($"{label}: REGION_TYPE={windowRgn} POINT_INSIDE={value}");
		}
		finally
		{
			NativeMethods.DeleteObject(num);
		}
	}

	private static void PrintSnapshot(WindowSnapshot snapshot)
	{
		Console.WriteLine($"Z={snapshot.ZOrder} HWND=0x{snapshot.Handle:X} PID={snapshot.ProcessId} PROCESS={snapshot.ProcessName} VISIBLE={snapshot.Visible} CLOAKED={snapshot.Cloaked} BOUNDS={snapshot.Bounds.Left},{snapshot.Bounds.Top},{snapshot.Bounds.Right},{snapshot.Bounds.Bottom} PARENT=0x{snapshot.Parent:X} OWNER=0x{snapshot.Owner:X} STYLE=0x{snapshot.Style:X} EXSTYLE=0x{snapshot.ExStyle:X} CLASS={snapshot.ClassName} TITLE={snapshot.Title}");
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
