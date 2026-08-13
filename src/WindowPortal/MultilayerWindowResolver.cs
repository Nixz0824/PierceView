using System.Runtime.InteropServices;

namespace WindowPortal;

internal readonly record struct MultilayerWindowSource(
    nint Handle,
    NativeMethods.Rect Bounds);

/// <summary>
/// Resolves the first four capturable top-level windows behind the protected
/// host. The returned order is front-to-back. GPU sessions periodically
/// resolve it again so closed or newly eligible windows can be removed or
/// backfilled without rebuilding captures that are still valid.
/// </summary>
internal static class MultilayerWindowResolver
{
    internal const int MaximumLayerCount = 4;

    internal static IReadOnlyList<MultilayerWindowSource> Resolve(
        nint protectedWindow,
        NativeMethods.Rect portalBounds,
        IReadOnlySet<nint>? excludedWindows = null)
    {
        return ResolveAfterWindow(
            protectedWindow,
            portalBounds,
            excludedWindows);
    }

    internal static IReadOnlyList<MultilayerWindowSource> ResolveAfterWindow(
        nint frontWindow,
        NativeMethods.Rect portalBounds,
        IReadOnlySet<nint>? excludedWindows = null)
    {
        if (frontWindow == nint.Zero ||
            !NativeMethods.IsWindow(frontWindow) ||
            portalBounds.Width <= 0 ||
            portalBounds.Height <= 0)
        {
            return Array.Empty<MultilayerWindowSource>();
        }

        var candidates = new List<WindowCandidate>();
        for (var window = NativeMethods.GetWindow(
                 frontWindow,
                 NativeMethods.GwHwndNext);
             window != nint.Zero && candidates.Count < MaximumLayerCount;
             window = NativeMethods.GetWindow(window, NativeMethods.GwHwndNext))
        {
            if (excludedWindows?.Contains(window) == true)
            {
                continue;
            }

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

    internal static IReadOnlyList<T> PromoteToFront<T>(
        IReadOnlyList<T> frontToBack,
        T selected)
    {
        var selectedIndex = -1;
        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < frontToBack.Count; index++)
        {
            if (comparer.Equals(frontToBack[index], selected))
            {
                selectedIndex = index;
                break;
            }
        }
        if (selectedIndex <= 0)
        {
            return frontToBack.ToArray();
        }

        var reordered = frontToBack.ToList();
        reordered.RemoveAt(selectedIndex);
        reordered.Insert(0, selected);
        return reordered;
    }

    internal static IReadOnlyList<nint> ReconcileInvalidSources(
        IReadOnlyList<nint> currentFrontToBack,
        IReadOnlySet<nint> invalidCurrentSources,
        IEnumerable<nint> resolvedFrontToBack,
        int maximumLayerCount = MaximumLayerCount)
    {
        var limit = Math.Min(
            Math.Max(maximumLayerCount, 0),
            MaximumLayerCount);
        if (limit == 0)
        {
            return Array.Empty<nint>();
        }

        var reconciled = new List<nint>(limit);
        foreach (var current in currentFrontToBack)
        {
            if (current != nint.Zero &&
                !invalidCurrentSources.Contains(current) &&
                !reconciled.Contains(current))
            {
                reconciled.Add(current);
                if (reconciled.Count == limit)
                {
                    return reconciled;
                }
            }
        }

        foreach (var resolved in resolvedFrontToBack)
        {
            if (resolved != nint.Zero && !reconciled.Contains(resolved))
            {
                reconciled.Add(resolved);
                if (reconciled.Count == limit)
                {
                    break;
                }
            }
        }

        return reconciled;
    }

    internal static bool IsEligibleSessionSource(nint window)
    {
        if (window == nint.Zero ||
            !NativeMethods.IsWindow(window) ||
            !NativeMethods.IsWindowVisible(window) ||
            NativeMethods.IsIconic(window) ||
            NativeMethods.GetAncestor(window, NativeMethods.GaRoot) != window)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        var cloakResult = NativeMethods.DwmGetWindowAttribute(
            window,
            NativeMethods.DwmwaCloaked,
            out int cloakValue,
            Marshal.SizeOf<int>());
        return cloakResult != 0 || cloakValue == 0;
    }

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
