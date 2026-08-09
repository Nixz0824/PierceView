using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPortal;

internal sealed class ForegroundZOrderGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private readonly NativeMethods.WinEventCallback _eventCallback;
    private readonly NativeMethods.LowLevelMouseCallback _mouseCallback;
    private readonly NonActivatingWindowGuard _interactionGuard = new();
    private readonly HashSet<uint> _guardedProcessIds = [];
    private nint _eventHook;
    private nint _mouseHook;
    private nint _protectedWindow;
    private nint _currentSourceWindow;
    private nint _pendingPromotedWindow;
    private NativeMethods.Point _portalCenter;
    private int _portalRadius;
    private string? _pendingPromotionError;
    private bool _restoringForeground;

    internal int RecoveryCount { get; private set; }

    internal int PromotionCount { get; private set; }

    internal ForegroundZOrderGuard()
    {
        _eventCallback = OnWinEvent;
        _mouseCallback = OnLowLevelMouse;
    }

    internal bool TryEnable(
        nint sourceWindow,
        nint protectedWindow,
        NativeMethods.Point portalCenter,
        int portalRadius,
        out string? error)
    {
        Restore();
        RecoveryCount = 0;
        PromotionCount = 0;

        if (!NativeMethods.IsWindow(sourceWindow) || !NativeMethods.IsWindow(protectedWindow))
        {
            error = "无法建立窗口层级守卫：来源窗口或保护窗口不可用。";
            return false;
        }

        _protectedWindow = protectedWindow;
        _currentSourceWindow = sourceWindow;
        UpdatePortalGeometry(portalCenter, portalRadius);

        var sourceDecision = CompatibilityPolicy.Evaluate(sourceWindow, protectedWindow);
        if (sourceDecision.AllowInteraction && !TryTrackWindow(sourceWindow, out error))
        {
            Restore();
            return false;
        }

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
            Restore();
            error = Win32Error("无法监听系统前台窗口变化", hookError);
            return false;
        }

        var module = NativeMethods.GetModuleHandle(null);
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLowLevel,
            _mouseCallback,
            module,
            0);
        if (_mouseHook == nint.Zero)
        {
            var hookError = Marshal.GetLastPInvokeError();
            Restore();
            error = Win32Error("无法监听圆洞内的鼠标按下事件", hookError);
            return false;
        }

        error = null;
        return true;
    }

    internal void UpdatePortalGeometry(NativeMethods.Point center, int radius)
    {
        _portalCenter = center;
        _portalRadius = radius;
    }

    internal bool TryTakePromotedWindow(out nint window, out string? error)
    {
        window = _pendingPromotedWindow;
        error = _pendingPromotionError;
        _pendingPromotedWindow = nint.Zero;
        _pendingPromotionError = null;
        return window != nint.Zero;
    }

    internal void EnsurePreserved()
    {
        if (_eventHook == nint.Zero || _restoringForeground)
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (BelongsToGuardedApplication(foreground) || IsGuardedApplicationAboveProtectedWindow())
        {
            RestoreProtectedPosition(foreground);
        }
    }

    internal void Restore()
    {
        var mouseHook = _mouseHook;
        var eventHook = _eventHook;
        _mouseHook = nint.Zero;
        _eventHook = nint.Zero;

        if (mouseHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(mouseHook);
        }

        if (eventHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(eventHook);
        }

        _interactionGuard.Restore();
        _guardedProcessIds.Clear();
        _protectedWindow = nint.Zero;
        _currentSourceWindow = nint.Zero;
        _pendingPromotedWindow = nint.Zero;
        _pendingPromotionError = null;
        _portalCenter = default;
        _portalRadius = 0;
        _restoringForeground = false;
    }

    public void Dispose()
    {
        Restore();
        _interactionGuard.Dispose();
        GC.SuppressFinalize(this);
    }

    private nint OnLowLevelMouse(int code, nint message, nint mouseData)
    {
        try
        {
            if (code >= 0 && IsMouseButtonDown(message))
            {
                var hookData = Marshal.PtrToStructure<NativeMethods.MsllHookStruct>(mouseData);
                if (IsInsidePortal(hookData.Point))
                {
                    PromoteWindowAtPoint(hookData.Point);
                }
            }
        }
        catch (Exception exception)
        {
            _pendingPromotionError = $"无法提升圆洞内的后台窗口：{exception.Message}";
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, message, mouseData);
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
        try
        {
            if (hook == _eventHook && BelongsToGuardedApplication(window))
            {
                RestoreProtectedPosition(window);
            }
        }
        catch
        {
            // Never allow a system WinEvent callback failure to escape into user32.
        }
    }

    private void PromoteWindowAtPoint(NativeMethods.Point point)
    {
        if (!NativeMethods.IsWindow(_protectedWindow))
        {
            return;
        }

        var target = FindBackgroundWindowAtPoint(point);
        if (target == nint.Zero)
        {
            return;
        }

        if (!TryTrackWindow(target, out var trackingError))
        {
            if (trackingError is not null)
            {
                _pendingPromotionError = trackingError;
            }

            return;
        }

        if (!NativeMethods.SetWindowPos(
                target,
                _protectedWindow,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoOwnerZOrder))
        {
            _pendingPromotionError = Win32Error(
                "无法把被点击窗口提升到宿主窗口正下方",
                Marshal.GetLastPInvokeError());
            return;
        }

        var sourceChanged = target != _currentSourceWindow;
        _currentSourceWindow = target;
        PromotionCount++;
        if (sourceChanged)
        {
            _pendingPromotedWindow = target;
        }
    }

    private nint FindBackgroundWindowAtPoint(NativeMethods.Point point)
    {
        for (var window = NativeMethods.GetWindow(
                 _protectedWindow,
                 NativeMethods.GwHwndNext);
             window != nint.Zero;
             window = NativeMethods.GetWindow(window, NativeMethods.GwHwndNext))
        {
            var decision = CompatibilityPolicy.Evaluate(window, _protectedWindow);
            if (!decision.IncludeInVisualStack || !ContainsBackgroundPoint(window, point))
            {
                continue;
            }

            if (!decision.AllowInteraction)
            {
                _pendingPromotionError =
                    $"已阻止对 {decision.ProcessName} 的窗口层级修改：{decision.Reason}";
                return nint.Zero;
            }

            return window;
        }

        return nint.Zero;
    }

    private static bool ContainsBackgroundPoint(nint window, NativeMethods.Point point)
    {
        if (!NativeMethods.IsWindowEnabled(window))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(window, out var rect) || !rect.Contains(point))
        {
            return false;
        }

        var region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (region == nint.Zero)
        {
            return true;
        }

        try
        {
            var regionType = NativeMethods.GetWindowRgn(window, region);
            if (regionType == NativeMethods.ErrorRegion)
            {
                return true;
            }

            return NativeMethods.PtInRegion(
                region,
                point.X - rect.Left,
                point.Y - rect.Top);
        }
        finally
        {
            _ = NativeMethods.DeleteObject(region);
        }
    }

    private bool TryTrackWindow(nint window, out string? error)
    {
        var decision = CompatibilityPolicy.Evaluate(window, _protectedWindow);
        if (!decision.AllowInteraction)
        {
            error = $"已阻止对 {decision.ProcessName} 的窗口修改：{decision.Reason}";
            return false;
        }

        if (!_interactionGuard.TryAdd(window, out error))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            error = "无法识别圆洞内后台应用的进程。";
            return false;
        }

        _guardedProcessIds.Add(processId);
        error = null;
        return true;
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
            var foregroundRootOwner = NativeMethods.GetAncestor(
                foregroundBackgroundWindow,
                NativeMethods.GaRootOwner);
            if (foregroundRootOwner != nint.Zero && foregroundRootOwner != _protectedWindow)
            {
                MoveBehindProtectedWindow(foregroundRootOwner);
            }

            MoveBehindProtectedWindow(_currentSourceWindow);
            ForceProtectedWindowForeground();
            MoveBehindProtectedWindow(_currentSourceWindow);
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

    private bool IsGuardedApplicationAboveProtectedWindow()
    {
        for (var window = NativeMethods.GetWindow(
                 _protectedWindow,
                 NativeMethods.GwHwndPrevious);
             window != nint.Zero;
             window = NativeMethods.GetWindow(window, NativeMethods.GwHwndPrevious))
        {
            if (BelongsToGuardedApplication(window) && NativeMethods.IsWindowVisible(window))
            {
                return true;
            }
        }

        return false;
    }

    private bool BelongsToGuardedApplication(nint window)
    {
        if (window == nint.Zero || window == _protectedWindow || !NativeMethods.IsWindow(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId != 0 && _guardedProcessIds.Contains(processId);
    }

    private bool IsInsidePortal(NativeMethods.Point point)
    {
        if (_portalRadius <= 0)
        {
            return false;
        }

        var deltaX = (long)point.X - _portalCenter.X;
        var deltaY = (long)point.Y - _portalCenter.Y;
        var radius = (long)_portalRadius;
        return (deltaX * deltaX) + (deltaY * deltaY) <= radius * radius;
    }

    private static bool IsMouseButtonDown(nint message)
    {
        var value = unchecked((uint)message.ToInt64());
        return value is NativeMethods.WmLeftButtonDown or
            NativeMethods.WmRightButtonDown or
            NativeMethods.WmMiddleButtonDown or
            NativeMethods.WmXButtonDown;
    }

    private static string Win32Error(string message, int error) =>
        error == 0 ? message : $"{message}：{new Win32Exception(error).Message}（{error}）";
}
