using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowPortal;

/// <summary>
/// Uses the Windows high-resolution waitable timer when available so the CPU
/// portal can move a cached frame between slower PrintWindow captures without
/// changing the system-wide timer resolution.
/// </summary>
internal sealed class HighResolutionWaiter : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = 0xFFFFFFFF;
    private readonly SafeWaitHandle? timer;

    internal HighResolutionWaiter()
    {
        var handle = CreateWaitableTimerEx(
            nint.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);
        if (handle != nint.Zero)
        {
            timer = new SafeWaitHandle(handle, ownsHandle: true);
        }
    }

    internal bool IsHighResolution => timer is { IsInvalid: false, IsClosed: false };

    internal void Wait(double milliseconds)
    {
        if (milliseconds <= 0)
        {
            Thread.Yield();
            return;
        }

        if (!IsHighResolution)
        {
            Thread.Sleep(Math.Max(1, (int)Math.Ceiling(milliseconds)));
            return;
        }

        var dueTime = -Math.Max(1L, (long)Math.Ceiling(milliseconds * 10_000d));
        if (!SetWaitableTimer(
                timer!.DangerousGetHandle(),
                ref dueTime,
                0,
                nint.Zero,
                nint.Zero,
                false))
        {
            Thread.Sleep(Math.Max(1, (int)Math.Ceiling(milliseconds)));
            return;
        }

        _ = WaitForSingleObject(timer.DangerousGetHandle(), Infinite);
    }

    public void Dispose() => timer?.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerEx(
        nint timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        nint timer,
        ref long dueTime,
        int period,
        nint completionRoutine,
        nint completionRoutineArgument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
