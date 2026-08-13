using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPortal;

/// <summary>
/// Temporarily makes a PierceView visual surface an owned window of the
/// protected host. Windows then keeps the transparent visual above its host
/// while the host occupies the topmost band for one F8 session.
/// </summary>
internal sealed class WindowOwnerGuard : IDisposable
{
    private nint window;
    private nint originalOwner;
    private uint processId;
    private bool active;

    internal bool TryEnable(
        nint visualWindow,
        nint protectedWindow,
        out string? error)
    {
        Restore();
        if (!NativeMethods.IsWindow(visualWindow) ||
            !NativeMethods.IsWindow(protectedWindow) ||
            visualWindow == protectedWindow)
        {
            error = "无法建立透视显示层与宿主的临时拥有关系。";
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(visualWindow, out processId);
        if (processId == 0)
        {
            error = "无法识别透视显示层进程。";
            return false;
        }

        originalOwner = NativeMethods.GetWindow(
            visualWindow,
            NativeMethods.GwOwner);
        Marshal.SetLastPInvokeError(0);
        var previousOwner = NativeMethods.SetWindowLongPtr(
            visualWindow,
            NativeMethods.GwlHwndParent,
            protectedWindow);
        var ownerError = Marshal.GetLastPInvokeError();
        if (previousOwner == nint.Zero && ownerError != 0)
        {
            processId = 0;
            originalOwner = nint.Zero;
            error = Win32Error("无法绑定透视显示层与宿主", ownerError);
            return false;
        }

        window = visualWindow;
        active = true;
        error = null;
        return true;
    }

    internal void Restore()
    {
        if (active && IsSameWindow(window, processId))
        {
            Marshal.SetLastPInvokeError(0);
            _ = NativeMethods.SetWindowLongPtr(
                window,
                NativeMethods.GwlHwndParent,
                originalOwner);
        }

        active = false;
        window = nint.Zero;
        originalOwner = nint.Zero;
        processId = 0;
    }

    public void Dispose()
    {
        Restore();
        GC.SuppressFinalize(this);
    }

    private static bool IsSameWindow(nint candidate, uint expectedProcessId)
    {
        if (!NativeMethods.IsWindow(candidate))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(candidate, out var actualProcessId);
        return actualProcessId == expectedProcessId;
    }

    private static string Win32Error(string message, int error) =>
        error == 0
            ? message
            : $"{message}：{new Win32Exception(error).Message}（{error}）";
}
