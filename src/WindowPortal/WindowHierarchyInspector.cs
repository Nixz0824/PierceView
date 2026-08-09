using System.Diagnostics;

namespace WindowPortal;

internal static class WindowHierarchyInspector
{
    internal static int Inspect(nint targetWindow, NativeMethods.Point? requestedPoint)
    {
        if (!NativeMethods.IsWindow(targetWindow) || !NativeMethods.GetWindowRect(targetWindow, out var targetRect))
        {
            Console.Error.WriteLine($"无法检查窗口 0x{targetWindow:X}。");
            return 1;
        }

        var point = requestedPoint ?? new NativeMethods.Point(
            targetRect.Left + (targetRect.Width / 2),
            targetRect.Top + (targetRect.Height / 2));

        NativeMethods.GetWindowThreadProcessId(targetWindow, out var targetProcessId);
        var root = NativeMethods.GetAncestor(targetWindow, NativeMethods.GaRoot);
        var rootOwner = NativeMethods.GetAncestor(targetWindow, NativeMethods.GaRootOwner);
        var parent = NativeMethods.GetParent(targetWindow);
        var owner = NativeMethods.GetWindow(targetWindow, NativeMethods.GwOwner);
        var windowAtPoint = NativeMethods.WindowFromPoint(point);

        Console.WriteLine($"检查点：{point.X},{point.Y}");
        PrintRelation("TARGET", targetWindow);
        PrintRelation("WINDOW_FROM_POINT", windowAtPoint);
        PrintRelation("PARENT", parent);
        PrintRelation("OWNER", owner);
        PrintRelation("ROOT", root);
        PrintRelation("ROOT_OWNER", rootOwner);
        PrintRegionMembership("TARGET_REGION_AT_POINT", targetWindow, point);
        PrintRegionMembership("WINDOW_FROM_POINT_REGION_AT_POINT", windowAtPoint, point);

        var topLevelWindows = EnumerateTopLevelWindows();
        Console.WriteLine();
        Console.WriteLine($"同进程顶层窗口（PID={targetProcessId}，包括隐藏窗口）：");
        foreach (var snapshot in topLevelWindows.Where(item => item.ProcessId == targetProcessId))
        {
            PrintSnapshot(snapshot);
        }

        Console.WriteLine();
        Console.WriteLine("检查点上的子窗口链候选：");
        var childCount = 0;
        NativeMethods.EnumWindowsCallback childCallback = (window, unusedParameter) =>
        {
            var snapshot = CreateSnapshot(window, -1);
            if (snapshot.Bounds.Contains(point))
            {
                PrintSnapshot(snapshot);
                childCount++;
            }

            return true;
        };
        _ = NativeMethods.EnumChildWindows(targetWindow, childCallback, nint.Zero);
        if (childCount == 0)
        {
            Console.WriteLine("（没有覆盖检查点的子 HWND）");
        }

        Console.WriteLine();
        Console.WriteLine("检查点处的顶层 Z-order（从上到下）：");
        foreach (var snapshot in topLevelWindows.Where(item => item.Bounds.Contains(point)))
        {
            PrintSnapshot(snapshot);
        }

        return 0;
    }

    private static List<WindowSnapshot> EnumerateTopLevelWindows()
    {
        var snapshots = new List<WindowSnapshot>();
        var zOrder = 0;
        NativeMethods.EnumWindowsCallback callback = (window, unusedParameter) =>
        {
            snapshots.Add(CreateSnapshot(window, zOrder));
            zOrder++;
            return true;
        };

        _ = NativeMethods.EnumWindows(callback, nint.Zero);
        return snapshots;
    }

    private static WindowSnapshot CreateSnapshot(nint window, int zOrder)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        _ = NativeMethods.GetWindowRect(window, out var rect);
        var cloaked = 0;
        _ = NativeMethods.DwmGetWindowAttribute(
            window,
            NativeMethods.DwmwaCloaked,
            out cloaked,
            sizeof(int));

        return new WindowSnapshot(
            window,
            zOrder,
            processId,
            GetProcessName(processId),
            NativeMethods.GetWindowClassName(window),
            NativeMethods.GetWindowTitle(window),
            rect,
            NativeMethods.IsWindowVisible(window),
            cloaked != 0,
            NativeMethods.GetParent(window),
            NativeMethods.GetWindow(window, NativeMethods.GwOwner),
            NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle).ToInt64(),
            NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64());
    }

    private static void PrintRelation(string label, nint window)
    {
        if (window == nint.Zero)
        {
            Console.WriteLine($"{label}=NULL");
            return;
        }

        Console.Write($"{label}: ");
        PrintSnapshot(CreateSnapshot(window, -1));
    }

    private static void PrintRegionMembership(string label, nint window, NativeMethods.Point screenPoint)
    {
        if (window == nint.Zero || !NativeMethods.GetWindowRect(window, out var rect))
        {
            Console.WriteLine($"{label}=不可用");
            return;
        }

        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (region == nint.Zero)
        {
            Console.WriteLine($"{label}=无法创建检查 region");
            return;
        }

        try
        {
            var regionType = NativeMethods.GetWindowRgn(window, region);
            var localPoint = new NativeMethods.Point(screenPoint.X - rect.Left, screenPoint.Y - rect.Top);
            var pointInside = regionType == NativeMethods.ErrorRegion
                ? rect.Contains(screenPoint)
                : NativeMethods.PtInRegion(region, localPoint.X, localPoint.Y);
            Console.WriteLine($"{label}: REGION_TYPE={regionType} POINT_INSIDE={pointInside}");
        }
        finally
        {
            NativeMethods.DeleteObject(region);
        }
    }

    private static void PrintSnapshot(WindowSnapshot snapshot)
    {
        Console.WriteLine(
            $"Z={snapshot.ZOrder} HWND=0x{snapshot.Handle:X} PID={snapshot.ProcessId} PROCESS={snapshot.ProcessName} " +
            $"VISIBLE={snapshot.Visible} CLOAKED={snapshot.Cloaked} BOUNDS={snapshot.Bounds.Left},{snapshot.Bounds.Top},{snapshot.Bounds.Right},{snapshot.Bounds.Bottom} " +
            $"PARENT=0x{snapshot.Parent:X} OWNER=0x{snapshot.Owner:X} STYLE=0x{snapshot.Style:X} EXSTYLE=0x{snapshot.ExStyle:X} " +
            $"CLASS={snapshot.ClassName} TITLE={snapshot.Title}");
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

    private sealed record WindowSnapshot(
        nint Handle,
        int ZOrder,
        uint ProcessId,
        string ProcessName,
        string ClassName,
        string Title,
        NativeMethods.Rect Bounds,
        bool Visible,
        bool Cloaked,
        nint Parent,
        nint Owner,
        long Style,
        long ExStyle);
}
