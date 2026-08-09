using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPortal;

/// <summary>
/// Keeps the single visible source behind the protected host without scanning,
/// promoting, or switching to deeper windows. PierceView 1.0 deliberately has no
/// low-level mouse hook.
/// </summary>
internal sealed class ForegroundZOrderGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private readonly NativeMethods.WinEventCallback _eventCallback;
    private readonly NonActivatingWindowGuard _interactionGuard = new();
    private nint _eventHook;
    private nint _protectedWindow;
    private nint _sourceWindow;
    private uint _sourceProcessId;
    private bool _restoringForeground;

    internal ForegroundZOrderGuard()
    {
        _eventCallback = OnWinEvent;
    }

    internal int RecoveryCount { get; private set; }

    internal int PromotionCount => 0;

    internal bool TryEnable(
        nint sourceWindow,
        nint protectedWindow,
        NativeMethods.Point portalCenter,
        int portalRadius,
        out string? error)
    {
        _ = portalCenter;
        _ = portalRadius;
        Restore();
        RecoveryCount = 0;

        if (!NativeMethods.IsWindow(sourceWindow) || !NativeMethods.IsWindow(protectedWindow))
        {
            error = "无法建立单层窗口守卫：来源窗口或宿主窗口不可用。";
            return false;
        }

        if (!_interactionGuard.TryAdd(sourceWindow, out error))
        {
            Restore();
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(sourceWindow, out _sourceProcessId);
        if (_sourceProcessId == 0)
        {
            error = "无法识别透视来源窗口的进程。";
            Restore();
            return false;
        }

        _protectedWindow = protectedWindow;
        _sourceWindow = sourceWindow;
        _eventHook = NativeMethods.SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _eventCallback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (_eventHook == nint.Zero)
        {
            var hookError = Marshal.GetLastPInvokeError();
            error = Win32Error("无法监听前台窗口变化", hookError);
            Restore();
            return false;
        }

        error = null;
        return true;
    }

    internal void UpdatePortalGeometry(NativeMethods.Point center, int radius)
    {
        _ = center;
        _ = radius;
    }

    internal void EnsurePreserved()
    {
        if (_eventHook == nint.Zero || _restoringForeground)
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (BelongsToSourceApplication(foreground) || IsSourceApplicationAboveHost())
        {
            RestoreProtectedPosition(foreground);
        }
    }

    internal void Restore()
    {
        var eventHook = _eventHook;
        _eventHook = nint.Zero;
        if (eventHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(eventHook);
        }

        _interactionGuard.Restore();
        _protectedWindow = nint.Zero;
        _sourceWindow = nint.Zero;
        _sourceProcessId = 0;
        _restoringForeground = false;
    }

    public void Dispose()
    {
        Restore();
        _interactionGuard.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        _ = eventType;
        _ = objectId;
        _ = childId;
        _ = eventThread;
        _ = eventTime;
        try
        {
            if (hook == _eventHook && BelongsToSourceApplication(window))
            {
                RestoreProtectedPosition(window);
            }
        }
        catch
        {
            // WinEvent callbacks must never escape into user32.
        }
    }

    private void RestoreProtectedPosition(nint foregroundBackgroundWindow)
    {
        if (_restoringForeground || !NativeMethods.IsWindow(_protectedWindow))
        {
            return;
        }

        _restoringForeground = true;
        RecoveryCount++;
        try
        {
            var rootOwner = NativeMethods.GetAncestor(
                foregroundBackgroundWindow,
                NativeMethods.GaRootOwner);
            if (rootOwner != nint.Zero && rootOwner != _protectedWindow)
            {
                MoveBehindProtectedWindow(rootOwner);
            }

            MoveBehindProtectedWindow(_sourceWindow);
            ForceProtectedWindowForeground();
            MoveBehindProtectedWindow(_sourceWindow);
        }
        finally
        {
            _restoringForeground = false;
        }
    }

    private void MoveBehindProtectedWindow(nint window)
    {
        if (window == nint.Zero || window == _protectedWindow || !NativeMethods.IsWindow(window))
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            window,
            _protectedWindow,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoOwnerZOrder);
    }

    private void ForceProtectedWindowForeground()
    {
        var currentThread = NativeMethods.GetCurrentThreadId();
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == nint.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var protectedThread = NativeMethods.GetWindowThreadProcessId(_protectedWindow, out _);
        var attachedToForeground = false;
        var attachedToProtected = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedToForeground = NativeMethods.AttachThreadInput(
                    currentThread,
                    foregroundThread,
                    attach: true);
            }

            if (protectedThread != 0 &&
                protectedThread != currentThread &&
                protectedThread != foregroundThread)
            {
                attachedToProtected = NativeMethods.AttachThreadInput(
                    currentThread,
                    protectedThread,
                    attach: true);
            }

            _ = NativeMethods.BringWindowToTop(_protectedWindow);
            _ = NativeMethods.SetForegroundWindow(_protectedWindow);
        }
        finally
        {
            if (attachedToProtected)
            {
                _ = NativeMethods.AttachThreadInput(
                    currentThread,
                    protectedThread,
                    attach: false);
            }

            if (attachedToForeground)
            {
                _ = NativeMethods.AttachThreadInput(
                    currentThread,
                    foregroundThread,
                    attach: false);
            }
        }
    }

    private bool IsSourceApplicationAboveHost()
    {
        for (var window = NativeMethods.GetWindow(
                 _protectedWindow,
                 NativeMethods.GwHwndPrevious);
             window != nint.Zero;
             window = NativeMethods.GetWindow(window, NativeMethods.GwHwndPrevious))
        {
            if (NativeMethods.IsWindowVisible(window) && BelongsToSourceApplication(window))
            {
                return true;
            }
        }

        return false;
    }

    private bool BelongsToSourceApplication(nint window)
    {
        if (window == nint.Zero || window == _protectedWindow || !NativeMethods.IsWindow(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId != 0 && processId == _sourceProcessId;
    }

    private static string Win32Error(string message, int error) =>
        error == 0
            ? message
            : $"{message}：{new Win32Exception(error).Message}（{error}）";
}
