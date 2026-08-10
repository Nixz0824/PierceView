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
    string RadiusLabel,
    string RadiusHint,
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
        "透视圆半径",
        "范围 64–400 像素，建议保持 180。",
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
        "按住 F8：不用来回 Alt+Tab，直接查看、滚动或点击当前窗口后面的一层普通应用。\n" +
        "松开 F8：立即关闭透视并恢复当前窗口。\n\n" +
        "在支持 Windows 原生拖放的应用中，可以从后台选中文字、图片或文件并保持拖动，松开 F8 后把它拖到当前应用的合适位置再放手。是否可复制取决于两端应用的拖放支持。\n\n" +
        "1.0 只支持紧贴当前窗口后方的一层。游戏、反作弊、受保护视频、系统界面和无重定向窗口可能只有点击、没有画面。启动游戏前请从托盘完全退出寸镜。",
        "暂时无法透视",
        "请确认鼠标下方存在普通桌面应用，并避开游戏、受保护内容和系统界面。",
        "寸镜 / PierceView 已经在运行。请从系统托盘打开设置。",
        "无法保存设置，请检查当前用户目录的写入权限。请注意：本次设置没有保存。" );

    internal static UiText English { get; } = new(
        "PierceView Settings",
        "Portal radius",
        "64–400 pixels. 180 is recommended.",
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
        "Hold F8: skip repetitive Alt+Tab switching and directly view, scroll, or click one ordinary app behind the current window.\n" +
        "Release F8: close the portal and restore the current window immediately.\n\n" +
        "When both apps support native Windows drag-and-drop, you can start dragging text, an image, or a file in the background app, release F8, then drop it into the current app. Actual copy behavior depends on both apps.\n\n" +
        "Version 1.0 supports only the layer directly behind the current window. Games, anti-cheat software, protected video, system UI, and no-redirection windows may accept clicks without showing an image. Fully exit PierceView before starting a game.",
        "Portal unavailable",
        "Make sure a standard desktop app is under the cursor. Avoid games, protected content, and system UI.",
        "PierceView is already running. Open it from the system tray.",
        "Could not save settings. Check write access to your user profile. This change was not saved." );
}
