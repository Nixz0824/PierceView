using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPortal;

internal sealed class NonActivatingWindowGuard : IDisposable
{
    private const long WsExNoActivate = 0x08000000;
    private readonly Dictionary<nint, GuardedWindowState> _windows = [];

    internal int Count => _windows.Count;

    internal bool Contains(nint window) => _windows.ContainsKey(window);

    internal bool TryEnable(nint window, out string? error)
    {
        Restore();
        return TryAdd(window, out error);
    }

    internal bool TryAdd(nint window, out string? error)
    {
        if (window == nint.Zero || !NativeMethods.IsWindow(window))
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
        var originalStyle = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle);
        var readStyleError = Marshal.GetLastPInvokeError();
        if (originalStyle == nint.Zero && readStyleError != 0)
        {
            error = Win32Error("无法读取后台窗口的扩展样式", readStyleError);
            return false;
        }

        var changedStyle = (originalStyle.ToInt64() & WsExNoActivate) == 0;
        if (changedStyle)
        {
            var guardedStyle = new nint(originalStyle.ToInt64() | WsExNoActivate);
            Marshal.SetLastPInvokeError(0);
            var previousStyle = NativeMethods.SetWindowLongPtr(
                window,
                NativeMethods.GwlExStyle,
                guardedStyle);
            var setStyleError = Marshal.GetLastPInvokeError();
            if (previousStyle == nint.Zero && setStyleError != 0)
            {
                error = Win32Error("无法阻止后台窗口在点击时置前", setStyleError);
                return false;
            }

            if (!RefreshWindowStyle(window))
            {
                var refreshError = Marshal.GetLastPInvokeError();
                _ = NativeMethods.SetWindowLongPtr(
                    window,
                    NativeMethods.GwlExStyle,
                    originalStyle);
                _ = RefreshWindowStyle(window);
                error = Win32Error("无法刷新后台窗口的非激活样式", refreshError);
                return false;
            }
        }

        _windows.Add(
            window,
            new GuardedWindowState(processId, originalStyle, changedStyle));
        error = null;
        return true;
    }

    internal void Restore()
    {
        var states = _windows.ToArray();
        _windows.Clear();

        foreach (var (window, state) in states)
        {
            if (!state.ChangedStyle || !IsSameWindow(window, state.ProcessId))
            {
                continue;
            }

            _ = NativeMethods.SetWindowLongPtr(
                window,
                NativeMethods.GwlExStyle,
                state.OriginalExtendedStyle);
            _ = RefreshWindowStyle(window);
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

        NativeMethods.GetWindowThreadProcessId(window, out var currentProcessId);
        return currentProcessId == expectedProcessId;
    }

    private static bool RefreshWindowStyle(nint window) =>
        NativeMethods.SetWindowPos(
            window,
            nint.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpFrameChanged);

    private static string Win32Error(string message, int error) =>
        error == 0 ? message : $"{message}：{new Win32Exception(error).Message}（{error}）";

    private readonly record struct GuardedWindowState(
        uint ProcessId,
        nint OriginalExtendedStyle,
        bool ChangedStyle);
}
