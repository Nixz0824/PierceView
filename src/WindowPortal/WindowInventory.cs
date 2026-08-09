using System.Diagnostics;

namespace WindowPortal;

internal static class WindowInventory
{
    internal static int PrintVisibleWindows()
    {
        var entries = new List<WindowEntry>();
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        NativeMethods.EnumWindowsCallback callback = (window, unusedParameter) =>
        {
            if (!NativeMethods.IsWindowVisible(window))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            _ = NativeMethods.GetWindowRect(window, out var rect);
            entries.Add(new WindowEntry(
                window,
                window == foregroundWindow,
                processId,
                GetProcessName(processId),
                NativeMethods.GetWindowClassName(window),
                NativeMethods.GetWindowTitle(window),
                rect));
            return true;
        };

        if (!NativeMethods.EnumWindows(callback, nint.Zero))
        {
            Console.Error.WriteLine("无法枚举顶层窗口。");
            return 1;
        }

        foreach (var entry in entries.OrderBy(entry => entry.ProcessName).ThenBy(entry => entry.Handle))
        {
            Console.WriteLine(
                $"HWND=0x{entry.Handle:X} FOREGROUND={entry.IsForeground} BOUNDS={entry.Bounds.Left},{entry.Bounds.Top},{entry.Bounds.Right},{entry.Bounds.Bottom} " +
                $"PID={entry.ProcessId} PROCESS={entry.ProcessName} CLASS={entry.ClassName} TITLE={entry.Title}");
        }

        return 0;
    }

    internal static int PrintCompatibilityReport()
    {
        var entries = new List<(nint Window, WindowCompatibilityDecision Decision)>();
        NativeMethods.EnumWindowsCallback callback = (window, unusedParameter) =>
        {
            if (NativeMethods.IsWindowVisible(window))
            {
                entries.Add((window, CompatibilityPolicy.Evaluate(window)));
            }

            return true;
        };

        if (!NativeMethods.EnumWindows(callback, nint.Zero))
        {
            Console.Error.WriteLine("无法枚举顶层窗口。以普通用户权限运行时只会生成只读报告。");
            return 1;
        }

        foreach (var (window, decision) in entries
                     .Where(entry => entry.Decision.Kind != WindowCompatibilityKind.Ignored)
                     .OrderBy(entry => entry.Decision.Kind)
                     .ThenBy(entry => entry.Decision.ProcessName))
        {
            Console.WriteLine(
                $"HWND=0x{window:X} PROCESS={decision.ProcessName} " +
                $"RESULT={decision.Kind} VISUAL={decision.AllowVisualPreview} " +
                $"INTERACTION={decision.AllowInteraction} REASON={decision.Reason}");
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

    private sealed record WindowEntry(
        nint Handle,
        bool IsForeground,
        uint ProcessId,
        string ProcessName,
        string ClassName,
        string Title,
        NativeMethods.Rect Bounds);
}
