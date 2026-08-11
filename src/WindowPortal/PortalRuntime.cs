using System.Diagnostics;

namespace WindowPortal;

internal sealed class PortalRuntime : IDisposable
{
    private readonly object _sync = new();
    private Thread? _thread;
    private volatile bool _stopRequested;
    private bool _disposed;

    internal event Action<string>? ErrorOccurred;

    internal void Start(PortalGeometry geometry, int pollMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _stopRequested = false;
            _thread = new Thread(() => Run(geometry, pollMilliseconds))
            {
                IsBackground = true,
                Name = "PierceView single-layer runtime"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    internal bool Restart(PortalGeometry geometry, int pollMilliseconds)
    {
        if (!Stop())
        {
            return false;
        }

        Start(geometry, pollMilliseconds);
        return true;
    }

    internal bool Stop(int timeoutMilliseconds = 3000)
    {
        Thread? thread;
        lock (_sync)
        {
            _stopRequested = true;
            thread = _thread;
        }

        if (thread is null || !thread.IsAlive || thread == Thread.CurrentThread)
        {
            return true;
        }

        return thread.Join(timeoutMilliseconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = Stop();
        GC.SuppressFinalize(this);
    }

    private void Run(PortalGeometry geometry, int pollMilliseconds)
    {
        using var controller = new WindowRegionController(geometry);
        using var visualOverlay = new AdaptivePortalOverlay(geometry);
        using var highResolutionWaiter = new HighResolutionWaiter();
        var wasActivationHeld = false;
        var visualWarningShown = false;
        try
        {
            while (!_stopRequested)
            {
                var loopStartedAt = Stopwatch.GetTimestamp();
                var activationHeld = NativeMethods.IsKeyDown(NativeMethods.VkF8);

                if (activationHeld && !wasActivationHeld)
                {
                    visualWarningShown = false;
                    if (!controller.TryBeginAtCursor(out var message))
                    {
                        ErrorOccurred?.Invoke(message);
                    }
                }

                if (activationHeld && controller.IsActive)
                {
                    if (!NativeMethods.GetCursorPos(out var cursor))
                    {
                        if (!visualWarningShown)
                        {
                            ErrorOccurred?.Invoke("无法读取鼠标位置。");
                            visualWarningShown = true;
                        }
                    }
                    else if (visualOverlay.IsVisible)
                    {
                        // 视觉层会在 PrintWindow 结束后 late-latch 到更新的鼠标坐标。
                        // 先完成唯一一次视觉提交，再把它实际提交的坐标交给物理交互孔，
                        // 避免两者分别停在抓帧前/后的两个位置。
                        if (!visualOverlay.TryUpdate(cursor, out var visualError))
                        {
                            if (!visualWarningShown)
                            {
                                ErrorOccurred?.Invoke(visualError ?? "视觉来源暂不可用。");
                                visualWarningShown = true;
                            }
                        }
                        else
                        {
                            var committedCenter = visualOverlay.LastPresentedCenter ?? cursor;
                            if (!controller.Update(committedCenter, out var regionError))
                            {
                                visualOverlay.Hide();
                                ErrorOccurred?.Invoke(regionError ?? "无法移动透视区域。");
                            }
                        }
                    }
                    else if (!controller.Update(cursor, out var regionError))
                    {
                        visualOverlay.Hide();
                        ErrorOccurred?.Invoke(regionError ?? "无法创建透视区域。");
                    }
                    else if (!TryUpdateVisualPortal(
                                 controller,
                                 visualOverlay,
                                 cursor,
                                 out var visualError))
                    {
                        if (!visualWarningShown)
                        {
                            ErrorOccurred?.Invoke(visualError ?? "视觉来源暂不可用。");
                            visualWarningShown = true;
                        }
                    }
                    else
                    {
                        var committedCenter = visualOverlay.LastPresentedCenter ?? cursor;
                        if (!controller.Update(committedCenter, out var syncRegionError))
                        {
                            visualOverlay.Hide();
                            ErrorOccurred?.Invoke(syncRegionError ?? "无法同步透视交互区域。");
                        }
                    }
                }

                if (!activationHeld && wasActivationHeld && controller.IsActive)
                {
                    visualOverlay.Hide();
                    controller.Restore();
                }

                wasActivationHeld = activationHeld;
                var targetLoopMilliseconds =
                    activationHeld
                        ? Math.Min(
                            pollMilliseconds,
                            visualOverlay.IsGpuActive ? 2 : 4)
                        : pollMilliseconds;
                var remainingMilliseconds =
                    targetLoopMilliseconds -
                    Stopwatch.GetElapsedTime(loopStartedAt).TotalMilliseconds;
                if (remainingMilliseconds >= 1)
                {
                    if (activationHeld)
                    {
                        highResolutionWaiter.Wait(remainingMilliseconds);
                    }
                    else
                    {
                        Thread.Sleep((int)Math.Floor(remainingMilliseconds));
                    }
                }
                else
                {
                    Thread.Yield();
                }
            }
        }
        catch (Exception exception)
        {
            ErrorOccurred?.Invoke(exception.Message);
        }
        finally
        {
            visualOverlay.Hide();
            controller.Restore();
        }
    }

    private static bool TryUpdateVisualPortal(
        WindowRegionController controller,
        AdaptivePortalOverlay visualOverlay,
        NativeMethods.Point screenPoint,
        out string? error)
    {
        if (visualOverlay.IsVisible)
        {
            return visualOverlay.TryUpdate(screenPoint, out error);
        }

        var sourceChild = NativeMethods.WindowFromPoint(screenPoint);
        var sourceWindow = sourceChild == nint.Zero
            ? nint.Zero
            : NativeMethods.GetAncestor(sourceChild, NativeMethods.GaRoot);
        if (sourceWindow == nint.Zero || sourceWindow == controller.ActiveWindow)
        {
            error = "没有识别到宿主窗口下方的单层视觉来源。";
            return false;
        }

        return visualOverlay.TryShow(
            sourceWindow,
            controller.ActiveWindow,
            screenPoint,
            out error);
    }
}
