using System.Runtime.InteropServices;

namespace WindowPortal;

internal readonly record struct MultilayerWindowSource(
    nint Handle,
    NativeMethods.Rect Bounds);

/// <summary>
/// Resolves the first four capturable top-level windows behind the protected
/// host. The returned order is front-to-back and remains fixed for one F8
/// session; normal pointer movement only changes the crop position.
/// </summary>
internal static class MultilayerWindowResolver
{
    internal const int MaximumLayerCount = 4;

    internal static IReadOnlyList<MultilayerWindowSource> Resolve(
        nint protectedWindow,
        NativeMethods.Rect portalBounds)
    {
        if (protectedWindow == nint.Zero ||
            !NativeMethods.IsWindow(protectedWindow) ||
            portalBounds.Width <= 0 ||
            portalBounds.Height <= 0)
        {
            return Array.Empty<MultilayerWindowSource>();
        }

        var candidates = new List<WindowCandidate>();
        for (var window = NativeMethods.GetWindow(
                 protectedWindow,
                 NativeMethods.GwHwndNext);
             window != nint.Zero && candidates.Count < MaximumLayerCount;
             window = NativeMethods.GetWindow(window, NativeMethods.GwHwndNext))
        {
            if (!TryCreateCandidate(window, out var candidate) ||
                !Intersects(candidate.Bounds, portalBounds))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        return SelectCandidates(candidates, portalBounds, MaximumLayerCount)
            .Select(candidate => new MultilayerWindowSource(
                candidate.Handle,
                candidate.Bounds))
            .ToArray();
    }

    internal static IReadOnlyList<WindowCandidate> SelectCandidates(
        IEnumerable<WindowCandidate> candidates,
        NativeMethods.Rect portalBounds,
        int maximumLayerCount = MaximumLayerCount)
    {
        if (maximumLayerCount <= 0 ||
            portalBounds.Width <= 0 ||
            portalBounds.Height <= 0)
        {
            return Array.Empty<WindowCandidate>();
        }

        return candidates
            .Where(candidate =>
                candidate.IsVisible &&
                !candidate.IsMinimized &&
                !candidate.IsCloaked &&
                !candidate.IsToolWindow &&
                !candidate.IsChildWindow &&
                candidate.Handle != nint.Zero &&
                candidate.Bounds.Width > 1 &&
                candidate.Bounds.Height > 1 &&
                Intersects(candidate.Bounds, portalBounds))
            .Take(Math.Min(maximumLayerCount, MaximumLayerCount))
            .ToArray();
    }

    internal static bool Intersects(
        NativeMethods.Rect first,
        NativeMethods.Rect second) =>
        first.Left < second.Right &&
        first.Right > second.Left &&
        first.Top < second.Bottom &&
        first.Bottom > second.Top;

    private static bool TryCreateCandidate(
        nint window,
        out WindowCandidate candidate)
    {
        candidate = default;
        if (!NativeMethods.IsWindow(window) ||
            !NativeMethods.IsWindowVisible(window) ||
            NativeMethods.IsIconic(window) ||
            NativeMethods.GetAncestor(window, NativeMethods.GaRoot) != window)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId)
        {
            return false;
        }

        var style = NativeMethods.GetWindowLongPtr(
            window,
            NativeMethods.GwlStyle).ToInt64();
        var extendedStyle = NativeMethods.GetWindowLongPtr(
            window,
            NativeMethods.GwlExStyle).ToInt64();
        var isChild = (style & NativeMethods.WsChild) != 0;
        var isToolWindow = (extendedStyle & NativeMethods.WsExToolWindow) != 0;
        var isCloaked = false;
        var cloakResult = NativeMethods.DwmGetWindowAttribute(
            window,
            NativeMethods.DwmwaCloaked,
            out int cloakValue,
            Marshal.SizeOf<int>());
        if (cloakResult == 0)
        {
            isCloaked = cloakValue != 0;
        }

        if (!TryGetWindowBounds(window, out var bounds))
        {
            return false;
        }

        candidate = new WindowCandidate(
            window,
            bounds,
            IsVisible: true,
            IsMinimized: false,
            isCloaked,
            isToolWindow,
            isChild);
        return true;
    }

    internal static bool TryGetWindowBounds(
        nint window,
        out NativeMethods.Rect bounds)
    {
        var extendedFrameResult = NativeMethods.DwmGetWindowAttribute(
            window,
            NativeMethods.DwmwaExtendedFrameBounds,
            out bounds,
            Marshal.SizeOf<NativeMethods.Rect>());
        if (extendedFrameResult == 0 && bounds.Width > 1 && bounds.Height > 1)
        {
            return true;
        }

        return NativeMethods.GetWindowRect(window, out bounds) &&
               bounds.Width > 1 &&
               bounds.Height > 1;
    }

    internal readonly record struct WindowCandidate(
        nint Handle,
        NativeMethods.Rect Bounds,
        bool IsVisible,
        bool IsMinimized,
        bool IsCloaked,
        bool IsToolWindow,
        bool IsChildWindow);
}
