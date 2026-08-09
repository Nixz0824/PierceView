using System.Diagnostics;
using System.Collections.Concurrent;

namespace WindowPortal;

internal enum WindowCompatibilityKind
{
    Supported,
    VisualUnsupported,
    Protected,
    Ignored
}

internal readonly record struct WindowCompatibilityDecision(
    WindowCompatibilityKind Kind,
    bool IncludeInVisualStack,
    bool AllowVisualPreview,
    bool AllowInteraction,
    string ProcessName,
    string Reason)
{
    internal bool IsSupported => Kind == WindowCompatibilityKind.Supported;
}

internal static class CompatibilityPolicy
{
    private const long WsExTransparent = 0x00000020;
    private const long WsExNoRedirectionBitmap = 0x00200000;
    private static readonly ConcurrentDictionary<uint, string> ProcessNameCache = new();

    private static readonly HashSet<string> IgnoredShellClasses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "EdgeUiInputTopWndClass",
        "DummyDWMListenerWindow",
        "ThumbnailDeviceHelperWnd"
    };

    private static readonly HashSet<string> IgnoredOverlayProcesses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "NVIDIA Overlay",
        "GameBar",
        "GameBarFTServer",
        "TextInputHost"
    };

    private static readonly string[] ProtectedProcessFragments =
    [
        "LeagueClient",
        "League of Legends",
        "RiotClient",
        "VALORANT",
        "vgc",
        "vgtray",
        "EasyAntiCheat",
        "BEService",
        "BattlEye",
        "FACEIT",
        "EAAntiCheat",
        "ACE-BASE",
        "SGuard",
        "TenProtect",
        "TGuard"
    ];

    private static readonly HashSet<string> ProtectedSystemProcesses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "LogonUI",
        "CredentialUIBroker",
        "consent",
        "SecurityHealthHost",
        "WindowsSecurityHealthService"
    };

    internal static WindowCompatibilityDecision Evaluate(
        nint window,
        nint protectedWindow = default)
    {
        if (window == nint.Zero ||
            window == protectedWindow ||
            !NativeMethods.IsWindow(window) ||
            !NativeMethods.IsWindowVisible(window))
        {
            return Ignore("窗口不可见、不可用或属于当前受保护应用。", "（无）");
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        var processName = TryGetProcessName(processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return Ignore("忽略工具自身窗口。", processName);
        }

        var className = NativeMethods.GetWindowClassName(window);
        if (window == NativeMethods.GetDesktopWindow() ||
            window == NativeMethods.GetShellWindow() ||
            IgnoredShellClasses.Contains(className))
        {
            return Ignore("系统桌面、任务栏或 DWM 辅助窗口不参与合成。", processName);
        }

        if (IgnoredOverlayProcesses.Contains(processName))
        {
            return Ignore("透明系统/游戏覆盖层不参与后台窗口层级计算。", processName);
        }

        var canHostOverlay =
            processName.Contains("Discord", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("GameBar", StringComparison.OrdinalIgnoreCase);
        if (canHostOverlay &&
            NativeMethods.GetWindowTitle(window)
                .Contains("Overlay", StringComparison.OrdinalIgnoreCase))
        {
            return Ignore("透明聊天/游戏覆盖层不参与后台窗口层级计算。", processName);
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(
            window,
            NativeMethods.GwlExStyle).ToInt64();
        if ((extendedStyle & WsExTransparent) != 0)
        {
            return Ignore("WS_EX_TRANSPARENT 覆盖窗会把鼠标和画面继续交给下层。", processName);
        }

        var cloakedResult = NativeMethods.DwmGetWindowAttribute(
            window,
            NativeMethods.DwmwaCloaked,
            out var cloaked,
            sizeof(int));
        if (cloakedResult == 0 && cloaked != 0)
        {
            return Ignore("窗口已被 DWM cloaking（例如位于其他虚拟桌面）。", processName);
        }

        var pureDecision = EvaluateProcessNameForTests(processName, extendedStyle, className);
        return pureDecision;
    }

    internal static WindowCompatibilityDecision EvaluateProcessNameForTests(
        string processName,
        long extendedStyle = 0,
        string className = "")
    {
        var normalizedProcessName = string.IsNullOrWhiteSpace(processName)
            ? "（未知进程）"
            : processName.Trim();
        var comparisonProcessName = NormalizeForComparison(normalizedProcessName);

        if (ProtectedSystemProcesses.Contains(normalizedProcessName) ||
            className.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("Secure", StringComparison.OrdinalIgnoreCase))
        {
            return Protected(
                normalizedProcessName,
                "Windows 安全桌面、凭据或系统安全界面禁止预览与穿透交互。");
        }

        if (ProtectedProcessFragments.Any(fragment =>
                comparisonProcessName.Contains(
                    NormalizeForComparison(fragment),
                    StringComparison.OrdinalIgnoreCase)))
        {
            return Protected(
                normalizedProcessName,
                "检测到游戏、反作弊或安全客户端；为降低封禁和兼容风险，默认禁止预览及窗口修改。");
        }

        if ((extendedStyle & WsExNoRedirectionBitmap) != 0)
        {
            return new WindowCompatibilityDecision(
                WindowCompatibilityKind.VisualUnsupported,
                IncludeInVisualStack: true,
                AllowVisualPreview: false,
                AllowInteraction: true,
                normalizedProcessName,
                "窗口使用 WS_EX_NOREDIRECTIONBITMAP，没有可供 DWM 缩略图读取的重定向表面。");
        }

        return new WindowCompatibilityDecision(
            WindowCompatibilityKind.Supported,
            IncludeInVisualStack: true,
            AllowVisualPreview: true,
            AllowInteraction: true,
            normalizedProcessName,
            "标准 DWM 重定向窗口。");
    }

    private static WindowCompatibilityDecision Protected(string processName, string reason) =>
        new(
            WindowCompatibilityKind.Protected,
            IncludeInVisualStack: true,
            AllowVisualPreview: false,
            AllowInteraction: false,
            processName,
            reason);

    private static WindowCompatibilityDecision Ignore(string reason, string processName) =>
        new(
            WindowCompatibilityKind.Ignored,
            IncludeInVisualStack: false,
            AllowVisualPreview: false,
            AllowInteraction: false,
            processName,
            reason);

    private static string TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return "（未知进程）";
        }

        return ProcessNameCache.GetOrAdd(
            processId,
            static id =>
            {
                try
                {
                    return Process.GetProcessById(checked((int)id)).ProcessName;
                }
                catch
                {
                    return $"PID {id}";
                }
            });
    }

    private static string NormalizeForComparison(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray());
}
