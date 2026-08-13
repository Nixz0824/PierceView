using System.Globalization;

namespace WindowPortal;

internal static class Localizer
{
    internal const string Chinese = "zh-CN";
    internal const string English = "en-US";

    internal static string DefaultLanguage =>
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;

    internal static string NormalizeLanguage(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;

    internal static UiText Get(string? language) =>
        NormalizeLanguage(language) == English ? UiText.English : UiText.Chinese;
}

internal sealed record UiText(
    string SettingsTitle,
    string PortalShapeLabel,
    string CircleShape,
    string RectangleShape,
    string RadiusLabel,
    string RadiusHint,
    string RectangleSizeLabel,
    string RectangleSizeHint,
    string FeatherWidthLabel,
    string FeatherWidthHint,
    string LanguageLabel,
    string ChineseLanguage,
    string EnglishLanguage,
    string Save,
    string Cancel,
    string TrayStart,
    string TrayPause,
    string TraySettings,
    string TrayHelp,
    string TrayExit,
    string TrayRunning,
    string TrayPaused,
    string FirstRunTitle,
    string FirstRunBody,
    string HelpTitle,
    string HelpBody,
    string PreviewUnavailableTitle,
    string PreviewUnavailableBody,
    string AlreadyRunning,
    string SettingsSaveFailed)
{
    internal static UiText Chinese { get; } = new(
        "寸镜 / PierceView 设置",
        "透视形状（2.3 实验版）",
        "圆形",
        "圆角矩形（固定）",
        "圆形清晰区半径",
        "清晰区 64–400 像素；羽化带向外扩展，默认 180。",
        "矩形尺寸",
        "宽 160–1000，高 120–800 像素。",
        "边缘羽化",
        "圆形和矩形共用；0 为硬边，最大 80 像素。矩形始终保留自动圆角。",
        "界面语言",
        "简体中文",
        "English",
        "保存",
        "取消",
        "启动透视",
        "暂停透视",
        "设置",
        "帮助",
        "退出",
        "寸镜 PierceView — 透视已启动",
        "寸镜 PierceView — 透视已暂停",
        "寸镜已启用",
        "把鼠标移到目标应用上，按住 F8 开始透视。松开 F8 即可恢复。",
        "寸镜 / PierceView 帮助",
        "按住 F8：在固定圆角矩形中同时查看当前窗口后方最多四层普通应用（-1 至 -4）；超过 -4 不识别。\n" +
        "松开 F8：立即关闭透视并恢复当前窗口。\n\n" +
        "本 alpha 只验证多层视觉与性能：真实点击、滚轮和拖放仍只按 Windows 当前命中的最前一层工作，深层点击排序尚未加入。若 GPU 多层捕获失败，会安全回退为单层 CPU 透视。\n\n" +
        "矩形支持自动圆角和羽化；鼠标中心仍位于完全穿透区域。游戏、反作弊、受保护视频、系统界面和无重定向窗口可能只有点击、没有画面。启动游戏前请从托盘完全退出寸镜。",
        "暂时无法透视",
        "请确认鼠标下方存在普通桌面应用，并避开游戏、受保护内容和系统界面。",
        "寸镜 / PierceView 已经在运行。请从系统托盘打开设置。",
        "无法保存设置，请检查当前用户目录的写入权限。请注意：本次设置没有保存。" );

    internal static UiText English { get; } = new(
        "PierceView Settings",
        "Portal shape (2.3 experimental)",
        "Circle",
        "Rounded rectangle (fixed)",
        "Clear circle radius",
        "Clear area 64–400 px; feathering expands outward. Default: 180.",
        "Rectangle size",
        "Width 160–1000, height 120–800 pixels.",
        "Edge feather",
        "Shared by circle and rectangle; 0 is hard-edged, up to 80 px. Rectangles remain automatically rounded.",
        "Language",
        "简体中文",
        "English",
        "Save",
        "Cancel",
        "Start portal",
        "Pause portal",
        "Settings",
        "Help",
        "Exit",
        "PierceView — Portal enabled",
        "PierceView — Portal paused",
        "PierceView enabled",
        "Move the cursor over an app, then hold F8 to open the portal. Release F8 to restore.",
        "PierceView Help",
        "Hold F8: simultaneously view up to four ordinary windows behind the current window (-1 through -4) in one fixed rounded rectangle. Layers beyond -4 are ignored.\n" +
        "Release F8: close the portal and restore the current window immediately.\n\n" +
        "This alpha validates multi-layer visuals and performance only. Native click, wheel, and drag still follow the frontmost window Windows currently hits; deep-layer click reordering is not included yet. If multi-source GPU capture fails, PierceView safely falls back to the single-layer CPU renderer.\n\n" +
        "The rectangle supports automatic rounded corners and feathering while keeping the pointer center fully open. Games, anti-cheat software, protected video, system UI, and no-redirection windows may accept clicks without showing an image. Fully exit PierceView before starting a game.",
        "Portal unavailable",
        "Make sure a standard desktop app is under the cursor. Avoid games, protected content, and system UI.",
        "PierceView is already running. Open it from the system tray.",
        "Could not save settings. Check write access to your user profile. This change was not saved." );
}
