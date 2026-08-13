using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPortal;

/// <summary>
/// Keeps all captured sources non-activating and behind the protected host.
/// A selected captured source may be promoted only to the slot directly behind
/// the host; it can never cross the host into the desktop foreground.
/// </summary>
internal sealed class ForegroundZOrderGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectReorder = 0x8004;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int RecoveryFollowUpPasses = 8;
    private const int RecoveryFollowUpMilliseconds = 2;

    private readonly NativeMethods.WinEventCallback _eventCallback;
    private readonly NonActivatingWindowGuard _interactionGuard = new();
    private readonly object _recoveryGate = new();
    private readonly ManualResetEventSlim _recoveryIdle = new(initialState: true);
    private nint _eventHook;
    private nint _reorderEventHook;
    private nint _protectedWindow;
    private nint _sourceWindow;
    private nint[] _sourceWindows = [];
    private uint[] _sourceProcessIds = [];
    private bool _restoringForeground;
    private nint _pendingForegroundWindow;
    private int _recoveryQueued;

    internal ForegroundZOrderGuard()
    {
        _eventCallback = OnWinEvent;
    }

    internal int RecoveryCount { get; private set; }

    internal int ImmediateClampCount { get; private set; }

    internal int PromotionCount { get; private set; }

    internal bool TryEnable(
        nint sourceWindow,
        nint protectedWindow,
        NativeMethods.Point portalCenter,
        int portalRadius,
        out string? error)
    {
        return TryEnable(
            [sourceWindow],
            protectedWindow,
            portalCenter,
            portalRadius,
            out error);
    }

    internal bool TryEnable(
        IReadOnlyList<nint> sourceWindows,
        nint protectedWindow,
        NativeMethods.Point portalCenter,
        int portalRadius,
        out string? error)
    {
        _ = portalCenter;
        _ = portalRadius;
        Restore();
        RecoveryCount = 0;
        ImmediateClampCount = 0;
        PromotionCount = 0;

        var distinctSources = sourceWindows
            .Where(window => window != nint.Zero)
            .Distinct()
            .ToArray();
        if (distinctSources.Length == 0 ||
            distinctSources.Any(window => !NativeMethods.IsWindow(window)) ||
            !NativeMethods.IsWindow(protectedWindow))
        {
            error = "无法建立多层窗口守卫：来源窗口或宿主窗口不可用。";
            return false;
        }

        var processIds = new List<uint>(distinctSources.Length);
        foreach (var source in distinctSources)
        {
            if (!_interactionGuard.TryAdd(source, out error))
            {
                Restore();
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(source, out var processId);
            if (processId == 0)
            {
                error = "无法识别透视来源窗口的进程。";
                Restore();
                return false;
            }

            processIds.Add(processId);
        }

        _protectedWindow = protectedWindow;
        _sourceWindows = distinctSources;
        _sourceProcessIds = processIds.Distinct().ToArray();
        _sourceWindow = distinctSources[0];
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

        _reorderEventHook = NativeMethods.SetWinEventHook(
            EventObjectReorder,
            EventObjectReorder,
            nint.Zero,
            _eventCallback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (_reorderEventHook == nint.Zero)
        {
            var hookError = Marshal.GetLastPInvokeError();
            error = Win32Error("无法监听后台窗口层级变化", hookError);
            Restore();
            return false;
        }

        error = null;
        return true;
    }

    internal bool TryPromoteSource(nint sourceWindow, out string? error)
    {
        var root = NativeMethods.GetAncestor(sourceWindow, NativeMethods.GaRoot);
        if (root == nint.Zero)
        {
            root = sourceWindow;
        }

        lock (_recoveryGate)
        {
            if (_eventHook == nint.Zero || !NativeMethods.IsWindow(_protectedWindow))
            {
                error = "多层窗口守卫尚未启用。";
                return false;
            }

            if (!_sourceWindows.Contains(root) || !NativeMethods.IsWindow(root))
            {
                error = "鼠标命中的窗口不属于本次 F8 会话锁定的前四层来源。";
                return false;
            }

            _sourceWindow = root;
            if (GetFirstVisibleWindowBehindHost() != root)
            {
                if (!NativeMethods.SetWindowPos(
                        root,
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
                    error = Win32Error(
                        "无法把选中的深层窗口移动到宿主后方",
                        Marshal.GetLastPInvokeError());
                    return false;
                }

                PromotionCount++;
            }
        }

        // The native mouse-down may be followed by several activation attempts
        // from the target application. Start an immediate + short follow-up
        // watch even when the selected source was already the current -1.
        TryClampImmediately(root);
        QueueProtectedPositionRecovery(root);
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
            TryClampImmediately(foreground);
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

        var reorderEventHook = _reorderEventHook;
        _reorderEventHook = nint.Zero;
        if (reorderEventHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(reorderEventHook);
        }

        _ = _recoveryIdle.Wait(500);
        lock (_recoveryGate)
        {
            _interactionGuard.Restore();
            _protectedWindow = nint.Zero;
            _sourceWindow = nint.Zero;
            _sourceWindows = [];
            _sourceProcessIds = [];
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
            if ((hook == _eventHook || hook == _reorderEventHook) &&
                BelongsToSourceApplication(window))
            {
                // WINEVENT_OUTOFCONTEXT is already delivered asynchronously.
                // Recover on this callback turn whenever the gate is free so a
                // 260 Hz compositor does not get an 8 ms window to show the
                // source above the protected host. The worker below remains as
                // a non-blocking fallback for repeated activation attempts.
                TryClampImmediately(window);
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
            // Pass zero runs without a forced sleep. Follow-up passes then watch
            // the short activation burst at sub-frame intervals. This preserves
            // the old eventual recovery guarantee without guaranteeing one or
            // two visible frames of foreground exposure on high-refresh panels.
            for (var pass = 0; pass < RecoveryFollowUpPasses; pass++)
            {
                if (pass > 0)
                {
                    Thread.Sleep(RecoveryFollowUpMilliseconds);
                }

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

    private void TryClampImmediately(nint preferredWindow)
    {
        if (_eventHook == nint.Zero || _restoringForeground ||
            !Monitor.TryEnter(_recoveryGate))
        {
            return;
        }

        try
        {
            if (_eventHook == nint.Zero || _restoringForeground)
            {
                return;
            }

            var foregroundIsSource = BelongsToSourceApplication(
                NativeMethods.GetForegroundWindow());
            if (!foregroundIsSource && !IsSourceApplicationAboveHost())
            {
                return;
            }

            var clampWindow = BelongsToSourceApplication(preferredWindow)
                ? preferredWindow
                : _sourceWindow;
            var rootOwner = NativeMethods.GetAncestor(
                clampWindow,
                NativeMethods.GaRootOwner);
            if (rootOwner != nint.Zero && rootOwner != _protectedWindow)
            {
                MoveBehindProtectedWindow(rootOwner);
            }

            MoveBehindProtectedWindow(_sourceWindow);
            ImmediateClampCount++;
        }
        finally
        {
            Monitor.Exit(_recoveryGate);
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
        return processId != 0 && _sourceProcessIds.Contains(processId);
    }

    private nint GetFirstVisibleWindowBehindHost()
    {
        for (var window = NativeMethods.GetWindow(
                 _protectedWindow,
                 NativeMethods.GwHwndNext);
             window != nint.Zero;
             window = NativeMethods.GetWindow(window, NativeMethods.GwHwndNext))
        {
            if (NativeMethods.IsWindowVisible(window))
            {
                return window;
            }
        }

        return nint.Zero;
    }

    private static string Win32Error(string message, int error) =>
        error == 0
            ? message
            : $"{message}：{new Win32Exception(error).Message}（{error}）";
}
