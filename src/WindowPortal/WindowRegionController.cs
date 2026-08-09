using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPortal;

internal sealed class WindowRegionController : IDisposable
{
    private static readonly HashSet<string> ProtectedShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    };

    private readonly int _radius;
    private readonly List<RegionWindowState> _windows = [];
    private nint _rootWindow;
    private NativeMethods.Point? _lastCursor;

    internal WindowRegionController(int radius)
    {
        _radius = radius;
    }

    internal bool IsActive => _rootWindow != nint.Zero;

    internal nint ActiveWindow => _rootWindow;

    internal int ActiveLayerCount => _windows.Count;

    internal string ActiveDescription => IsActive
        ? $"{NativeMethods.GetWindowTitle(_rootWindow)} [{NativeMethods.GetWindowClassName(_rootWindow)}] " +
          $"HWND=0x{_rootWindow:X}，裁剪层数={ActiveLayerCount}"
        : "（无）";

    internal bool TryBeginAtCursor(out string message)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            message = LastWin32Error("无法读取鼠标位置");
            return false;
        }

        var childWindow = NativeMethods.WindowFromPoint(cursor);
        var rootWindow = childWindow == nint.Zero
            ? nint.Zero
            : NativeMethods.GetAncestor(childWindow, NativeMethods.GaRoot);

        if (rootWindow == nint.Zero)
        {
            message = "鼠标下没有可操作的顶层窗口。";
            return false;
        }

        return TryBegin(rootWindow, out message);
    }

    internal bool TryBegin(nint window, out string message)
    {
        Restore();

        if (!ValidateRootWindow(window, out message))
        {
            return false;
        }

        if (!TryCaptureWindowState(window, out var rootState, out message))
        {
            return false;
        }

        _rootWindow = window;
        _windows.Add(rootState);

        _lastCursor = null;
        var layerClasses = string.Join(
            ", ",
            _windows.Select(state => NativeMethods.GetWindowClassName(state.Window)).Distinct());
        message = $"已锁定：{ActiveDescription}（{layerClasses}）";
        return true;
    }

    internal bool UpdateAtCursor(out string? error)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            error = LastWin32Error("无法读取鼠标位置");
            return false;
        }

        return Update(cursor, out error);
    }

    internal bool Update(NativeMethods.Point screenPoint, out string? error)
    {
        error = null;

        if (!IsActive)
        {
            error = "当前没有锁定窗口。";
            return false;
        }

        if (!NativeMethods.IsWindow(_rootWindow))
        {
            error = "目标窗口已经关闭。";
            ResetState(deleteOriginalRegions: true);
            return false;
        }

        var windowRects = new Dictionary<nint, NativeMethods.Rect>();
        var needsUpdate = _lastCursor != screenPoint;
        foreach (var state in _windows)
        {
            if (!NativeMethods.IsWindow(state.Window) ||
                !NativeMethods.GetWindowRect(state.Window, out var rect) ||
                rect.Width <= 1 ||
                rect.Height <= 1)
            {
                error = "宿主应用的渲染窗口在裁剪期间被重建，已执行安全恢复。";
                Restore();
                return false;
            }

            windowRects[state.Window] = rect;
            needsUpdate |= state.LastWindowRect != rect;
        }

        if (!needsUpdate)
        {
            return true;
        }

        // 先裁剪 Chromium/D3D 子渲染层，最后裁剪顶层 HWND。
        foreach (var state in _windows.OrderBy(state => state.Window == _rootWindow ? 1 : 0))
        {
            var windowRect = windowRects[state.Window];
            if (!ApplyHole(state.Window, windowRect, screenPoint, out error))
            {
                Restore();
                return false;
            }

            state.LastWindowRect = windowRect;
        }

        _lastCursor = screenPoint;
        return true;
    }

    internal RegionInspection InspectCurrentHole(NativeMethods.Point screenPoint)
    {
        if (!IsActive || !NativeMethods.GetWindowRect(_rootWindow, out var windowRect))
        {
            return new RegionInspection(NativeMethods.ErrorRegion, false, "目标窗口不可用。");
        }

        var copy = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (copy == nint.Zero)
        {
            return new RegionInspection(NativeMethods.ErrorRegion, false, LastWin32Error("无法创建检查区域"));
        }

        try
        {
            var regionType = NativeMethods.GetWindowRgn(_rootWindow, copy);
            if (regionType == NativeMethods.ErrorRegion)
            {
                return new RegionInspection(regionType, false, "目标窗口没有可读取的显式区域。");
            }

            var localCenter = ToWindowCoordinates(windowRect, screenPoint);
            var centerExcluded = !NativeMethods.PtInRegion(copy, localCenter.X, localCenter.Y);
            var detail = centerExcluded
                ? $"圆心已从目标窗口区域中排除；同步裁剪层数={ActiveLayerCount}。"
                : "圆心仍在目标窗口区域内。";
            return new RegionInspection(regionType, centerExcluded, detail);
        }
        finally
        {
            NativeMethods.DeleteObject(copy);
        }
    }

    internal static int ReadWindowRegionType(nint window)
    {
        var copy = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (copy == nint.Zero)
        {
            return NativeMethods.ErrorRegion;
        }

        try
        {
            return NativeMethods.GetWindowRgn(window, copy);
        }
        finally
        {
            NativeMethods.DeleteObject(copy);
        }
    }

    internal void Restore()
    {
        if (!IsActive)
        {
            return;
        }

        var rootWindow = _rootWindow;
        var states = _windows.ToArray();

        // 先清理本地活动标记，避免恢复失败或异常处理时二次转移 region 句柄。
        _rootWindow = nint.Zero;
        _windows.Clear();
        _lastCursor = null;

        // 先恢复子渲染层，最后恢复顶层窗口。
        foreach (var state in states.OrderBy(state => state.Window == rootWindow ? 1 : 0))
        {
            RestoreWindowState(state);
        }
    }

    public void Dispose()
    {
        Restore();
        GC.SuppressFinalize(this);
    }

    internal static NativeMethods.Point ToWindowCoordinates(
        NativeMethods.Rect windowRect,
        NativeMethods.Point screenPoint) =>
        new(screenPoint.X - windowRect.Left, screenPoint.Y - windowRect.Top);

    internal static NativeMethods.Rect CreateHoleBounds(NativeMethods.Point center, int radius) =>
        new(center.X - radius, center.Y - radius, center.X + radius + 1, center.Y + radius + 1);

    private bool ValidateRootWindow(nint window, out string message)
    {
        if (!NativeMethods.IsWindow(window) || !NativeMethods.IsWindowVisible(window))
        {
            message = $"窗口 0x{window:X} 不存在或不可见。";
            return false;
        }

        var windowClassName = NativeMethods.GetWindowClassName(window);
        if (window == NativeMethods.GetDesktopWindow() ||
            window == NativeMethods.GetShellWindow() ||
            ProtectedShellWindowClasses.Contains(windowClassName))
        {
            message = $"为保护桌面和任务栏，原型不会操作系统 Shell 窗口 [{windowClassName}]。";
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId)
        {
            message = "为避免锁住控制窗口，原型不会操作自身窗口。";
            return false;
        }

        var compatibility = CompatibilityPolicy.Evaluate(window);
        if (compatibility.Kind is WindowCompatibilityKind.Protected or
            WindowCompatibilityKind.Ignored)
        {
            message =
                $"安全策略拒绝把 {compatibility.ProcessName} 作为宿主窗口：{compatibility.Reason}";
            return false;
        }

        if (!NativeMethods.GetWindowRect(window, out var rect) || rect.Width <= 1 || rect.Height <= 1)
        {
            message = LastWin32Error("无法读取目标窗口尺寸");
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryCaptureWindowState(
        nint window,
        out RegionWindowState state,
        out string message)
    {
        var originalRegion = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (originalRegion == nint.Zero)
        {
            state = null!;
            message = LastWin32Error("无法创建用于保存原始窗口区域的对象");
            return false;
        }

        var originalRegionType = NativeMethods.GetWindowRgn(window, originalRegion);
        if (originalRegionType == NativeMethods.ErrorRegion)
        {
            NativeMethods.DeleteObject(originalRegion);
            originalRegion = nint.Zero;
        }

        state = new RegionWindowState(
            window,
            originalRegion,
            originalRegionType != NativeMethods.ErrorRegion);
        message = string.Empty;
        return true;
    }

    private bool ApplyHole(
        nint window,
        NativeMethods.Rect windowRect,
        NativeMethods.Point screenPoint,
        out string? error)
    {
        var localCenter = ToWindowCoordinates(windowRect, screenPoint);
        var hole = CreateHoleBounds(localCenter, _radius);
        var frameRegion = NativeMethods.CreateRectRgn(0, 0, windowRect.Width, windowRect.Height);
        var holeRegion = NativeMethods.CreateEllipticRgn(hole.Left, hole.Top, hole.Right, hole.Bottom);

        if (frameRegion == nint.Zero || holeRegion == nint.Zero)
        {
            DeleteIfOwned(frameRegion);
            DeleteIfOwned(holeRegion);
            error = LastWin32Error("无法创建圆形窗口区域");
            return false;
        }

        var combineResult = NativeMethods.CombineRgn(
            frameRegion,
            frameRegion,
            holeRegion,
            NativeMethods.RgnDiff);
        NativeMethods.DeleteObject(holeRegion);

        if (combineResult == NativeMethods.ErrorRegion)
        {
            NativeMethods.DeleteObject(frameRegion);
            error = LastWin32Error("无法从窗口区域中减去圆形区域");
            return false;
        }

        // 高频跟随时强制重绘整个 Chromium 窗口会产生闪烁；区域本身的命中测试会立即生效。
        if (NativeMethods.SetWindowRgn(window, frameRegion, redraw: false) == 0)
        {
            NativeMethods.DeleteObject(frameRegion);
            error = LastWin32Error(
                $"Windows 拒绝修改渲染窗口 0x{window:X}；如果目标程序以管理员身份运行，请用相同权限启动本工具");
            return false;
        }

        // SetWindowRgn 成功后，frameRegion 的所有权已转交给 Windows。
        error = null;
        return true;
    }

    private static void RestoreWindowState(RegionWindowState state)
    {
        var originalRegion = state.OriginalRegion;
        state.OriginalRegion = nint.Zero;

        if (!NativeMethods.IsWindow(state.Window))
        {
            DeleteIfOwned(originalRegion);
            return;
        }

        if (state.HadOriginalRegion)
        {
            if (NativeMethods.SetWindowRgn(state.Window, originalRegion, redraw: true) == 0)
            {
                DeleteIfOwned(originalRegion);
            }

            return;
        }

        _ = NativeMethods.SetWindowRgn(state.Window, nint.Zero, redraw: true);
    }

    private void ResetState(bool deleteOriginalRegions)
    {
        if (deleteOriginalRegions)
        {
            foreach (var state in _windows)
            {
                DeleteIfOwned(state.OriginalRegion);
                state.OriginalRegion = nint.Zero;
            }
        }

        _rootWindow = nint.Zero;
        _windows.Clear();
        _lastCursor = null;
    }

    private static void DeleteIfOwned(nint region)
    {
        if (region != nint.Zero)
        {
            NativeMethods.DeleteObject(region);
        }
    }

    private static string LastWin32Error(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return error == 0 ? message : $"{message}：{new Win32Exception(error).Message}（{error}）";
    }

    private sealed class RegionWindowState(
        nint window,
        nint originalRegion,
        bool hadOriginalRegion)
    {
        internal nint Window { get; } = window;
        internal nint OriginalRegion { get; set; } = originalRegion;
        internal bool HadOriginalRegion { get; } = hadOriginalRegion;
        internal NativeMethods.Rect? LastWindowRect { get; set; }
    }
}

internal readonly record struct RegionInspection(int RegionType, bool CenterExcluded, string Detail);
