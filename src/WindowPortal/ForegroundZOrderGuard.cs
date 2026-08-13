using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPortal;

/// <summary>
/// Keeps the single visible source behind the protected host without scanning,
/// promoting, or switching to deeper windows. Recovery is queued away from the
/// DWM render thread so a click cannot synchronously block frame capture.
/// </summary>
internal sealed class ForegroundZOrderGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int RecoverySettlePasses = 4;
    private const int RecoverySettleMilliseconds = 8;

    private readonly NativeMethods.WinEventCallback _eventCallback;
    private readonly NonActivatingWindowGuard _interactionGuard = new();
    private readonly object _recoveryGate = new();
    private readonly ManualResetEventSlim _recoveryIdle = new(initialState: true);
    private nint _eventHook;
    private nint _protectedWindow;
    private nint _sourceWindow;
    private uint _sourceProcessId;
    private bool _restoringForeground;
    private nint _pendingForegroundWindow;
    private int _recoveryQueued;

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
            QueueProtectedPositionRecovery(foreground);
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

        _ = _recoveryIdle.Wait(500);
        lock (_recoveryGate)
        {
            _interactionGuard.Restore();
            _protectedWindow = nint.Zero;
            _sourceWindow = nint.Zero;
            _sourceProcessId = 0;
            _pendingForegroundWindow = nint.Zero;
            _restoringForeground = false;
        }
    }

    public void Dispose()
    {
        Restore();
        _interactionGuard.Dispose();
        if (_recoveryIdle.Wait(2000))
        {
            _recoveryIdle.Dispose();
        }
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
                QueueProtectedPositionRecovery(window);
            }
        }
        catch
        {
            // WinEvent callbacks must never escape into user32.
        }
    }

    private void QueueProtectedPositionRecovery(nint foregroundBackgroundWindow)
    {
        if (_eventHook == nint.Zero)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _pendingForegroundWindow, foregroundBackgroundWindow);
        if (Interlocked.CompareExchange(ref _recoveryQueued, 1, 0) != 0)
        {
            return;
        }

        _recoveryIdle.Reset();
        if (!ThreadPool.QueueUserWorkItem(_ => RunQueuedRecovery()))
        {
            Volatile.Write(ref _recoveryQueued, 0);
            _recoveryIdle.Set();
        }
    }

    private void RunQueuedRecovery()
    {
        try
        {
            // A single native click can run Activate/BringWindowToTop/
            // SetForegroundWindow back-to-back. Recovering on the first event can
            // therefore land before the source application completes its own
            // activation sequence. Settle and recheck a few times on this worker
            // thread so the final state, not an intermediate event, wins.
            for (var pass = 0; pass < RecoverySettlePasses; pass++)
            {
                Thread.Sleep(RecoverySettleMilliseconds);
                lock (_recoveryGate)
                {
                    if (_eventHook == nint.Zero)
                    {
                        return;
                    }

                    var requestedWindow = Interlocked.Exchange(
                        ref _pendingForegroundWindow,
                        nint.Zero);
                    var foreground = NativeMethods.GetForegroundWindow();
                    var foregroundIsSource = BelongsToSourceApplication(foreground);
                    if (!foregroundIsSource && !IsSourceApplicationAboveHost())
                    {
                        continue;
                    }

                    var recoveryWindow = BelongsToSourceApplication(requestedWindow)
                        ? requestedWindow
                        : foregroundIsSource
                            ? foreground
                            : _sourceWindow;
                    RestoreProtectedPosition(recoveryWindow);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _recoveryQueued, 0);
            var pendingWindow = Volatile.Read(ref _pendingForegroundWindow);
            if (_eventHook != nint.Zero && pendingWindow != nint.Zero)
            {
                QueueProtectedPositionRecovery(pendingWindow);
            }
            else
            {
                _recoveryIdle.Set();
            }
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
