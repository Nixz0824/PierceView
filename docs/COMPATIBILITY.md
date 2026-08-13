# 寸镜 / PierceView 2.3 兼容性说明

Compatibility notes for **PierceView 2.3 GPU Edition**. English follows each Chinese section.

## 支持范围 / Supported scope

寸镜 2.3 优先使用 Windows Graphics Capture（WGC）捕获最多四个后台顶层窗口，并用 D3D11/DirectComposition 重建遮挡；不可用或失败时自动回退到 2.1.0 单层 DWM/CPU 管线。真实交互仍依赖 Win32 窗口 region。以下普通窗口通常兼容较好：

- 传统 Win32、WinForms、WPF 应用。
- 常规浏览器页面，包括普通 Chromium/Edge/Chrome 页面。
- 常规 Electron 桌面应用。
- 未最小化、未被系统 cloaked、与寸镜权限等级相同的顶层窗口。

PierceView 2.3 uses WGC for at most four background top-level windows and reconstructs their occlusion with D3D11/DirectComposition. If that path is unavailable or fails, it falls back to the single-layer 2.1.0 DWM/CPU renderer. Native interaction still relies on a Win32 window region. These ordinary windows usually work best:

- Traditional Win32, WinForms, and WPF applications.
- Ordinary Chromium, Edge, and Chrome pages.
- Ordinary Electron desktop applications.
- Non-minimized, non-cloaked top-level windows running at the same integrity level as PierceView.

“通常兼容”不是全量保证；同一应用的登录页、视频页、GPU 画布和普通文本页可能使用不同渲染路径。

“Usually supported” is not a blanket guarantee. Login pages, video pages, GPU canvases, and ordinary text pages inside the same app may use different rendering paths.

## 2.3 层级规则 / Layer rules

每次按下 F8 时，寸镜按宿主后方的真实 Z-order 最多识别 `-1` 至 `-4`，并在圆角羽化矩形中按真实屏幕边界重建遮挡：

- 超过 `-4` 的窗口不识别。
- 浅层窗口覆盖的位置显示浅层，未覆盖的位置可继续看到更深层；不是半透明叠图。
- 点击透视框内可见的深层窗口，只把它变为宿主后方的新 `-1`，不会越过宿主到桌面最前面。
- 点击、滚轮和拖放随真实 `-1` 同步；完全被浅层遮住、在透视框内不可见的窗口不能直接提升。
- F8 会话中关闭或最小化来源后，会从更深候选自动补位，但总数仍不超过四层。

On each F8 hold, PierceView resolves at most layers `-1` through `-4` behind the host and reconstructs their real screen-space occlusion inside one rounded feathered rectangle:

- Windows deeper than `-4` are ignored.
- Shallower windows win where they cover; uncovered pixels may reveal deeper windows. This is not translucent image stacking.
- Clicking a visible deeper window moves it only to the new `-1` slot behind the host, never above the host to the desktop foreground.
- Click, wheel, and drag follow the physical `-1`. A fully occluded window that is not visible in the portal cannot be promoted directly.
- Closing or minimizing a source during the F8 session backfills from deeper candidates, while the source count remains capped at four.

## 可能只有点击、没有画面的类型 / Input without a visual

| 类型 / Type | 简单例子 / Example | 原因 / Reason |
|---|---|---|
| 无重定向窗口 / No-redirection window | 显式使用 `WS_EX_NOREDIRECTIONBITMAP` 的窗口；部分 DirectComposition 自绘壳、桌面小组件或录屏叠加层 / windows explicitly using `WS_EX_NOREDIRECTIONBITMAP`, some custom DirectComposition shells, widgets, or capture overlays | WGC 或 DWM 可能无法提供可复制的常规窗口画面 / WGC or DWM may not expose a copyable ordinary window surface |
| 受保护视频 / Protected video | DRM/HDCP 视频、受保护媒体播放器画面 / DRM or HDCP video and protected media players | 系统有意禁止复制受保护表面 / Windows intentionally blocks copying |
| GPU 独立表面 / Independent GPU surface | 硬件 overlay、DirectX/Vulkan 自绘视图、独占全屏 / hardware overlays, custom DirectX or Vulkan views, exclusive fullscreen | 画面未进入普通 DWM 捕获路径 / content may bypass the ordinary DWM capture path |
| 独立 D3D 子表面 / Independent D3D child surface | 抖音桌面版等使用独立 `Intermediate D3D Window` 的应用 / apps such as Douyin Desktop using an independent `Intermediate D3D Window` | 顶层 HWND 可能只返回透明帧，内容子窗口又不接受独立捕获 / the top-level HWND may return only transparency while the child surface rejects standalone capture |
| 游戏与反作弊 / Games and anti-cheat | League of Legends、Riot Client、Vanguard、EAC、BattlEye、FACEIT | 渲染或安全策略可能阻止捕获或把窗口工具视为不兼容 / rendering or security policy may block capture or reject window tools |
| 系统安全界面 / Secure system UI | UAC 安全桌面、锁屏、部分 Shell 界面 / UAC secure desktop, lock screen, some Shell UI | 会话、桌面或权限隔离 / session, desktop, or privilege isolation |
| 最小化/隐藏窗口 / Minimized or hidden window | 最小化应用、其他虚拟桌面的 cloaked 窗口 / minimized apps or cloaked windows on another virtual desktop | DWM 可能暂停或替换可复制表面 / DWM may pause or replace the copyable surface |
| 权限更高的窗口 / Elevated window | 管理员工具，而寸镜为普通权限 / an administrator app while PierceView is unelevated | UIPI/权限边界可能拒绝 region、样式或 Z-order 操作 / UIPI may reject region, style, or Z-order changes |

“无重定向窗口”是窗口实现方式，不是固定应用名单；应用升级后也可能改变，因此寸镜不按品牌武断判定。

A “no-redirection window” describes an implementation technique, not a permanent app list. Rendering paths can change between app releases, so PierceView does not classify compatibility by brand alone.

## 游戏说明 / Games

风险不只涉及网游。单机游戏也可能带 EAC/BattlEye 等反作弊，游戏启动器和客户端本身也可能使用受保护或特殊 GPU 界面。寸镜 2.3 不尝试判断游戏是否安全，也不绕过任何保护。

统一规则：启动游戏、游戏启动器或反作弊服务前，从托盘完全退出寸镜，而不是只暂停或松开 F8。

The risk is not limited to online games. Single-player titles may also ship anti-cheat, and launchers can use protected or unusual GPU surfaces. PierceView neither decides whether a game is safe nor bypasses protection. Fully exit PierceView from the tray before starting any game, launcher, or anti-cheat service; pausing or releasing F8 is not enough.

跨应用拖放同样属于兼容能力：源应用必须允许开始 OLE/HTML5 拖放，目标应用必须接受相应数据格式，且两边权限等级应一致。某些浏览器图片、受保护内容或管理员窗口不会允许拖出或拖入，这不代表透视本身失效。

Cross-app drag-and-drop is also compatibility-dependent: the source must start OLE/HTML5 drag, the target must accept the same data format, and both apps should run at the same privilege level. Some browser images, protected content, or elevated windows cannot be dragged even when the visual portal works.

## 常见现象与判断 / Common symptoms

- 透视框为纯色、黑色，但点击有效：物理 region 已成功，视觉来源不可复制。Solid or black portal with working clicks: the physical region succeeded, but the visual source cannot be copied.
- 抖音桌面版只有点击、没有画面：2.3 开发测试中其顶层窗口未设置 `WDA_EXCLUDEFROMCAPTURE`，但 WGC 仅返回透明空帧；内部 `Intermediate D3D Window` 又拒绝独立 HWND 捕获。这属于应用渲染路径不兼容，不是刷新率或寸镜性能不足。Douyin Desktop accepts clicks but has no visual: its top-level WGC capture returned a transparent frame while its independent D3D child surface rejected standalone HWND capture. This is a rendering-path limitation, not a refresh-rate or performance issue.
- 只显示最多四层：符合 2.3 产品上限，超过 `-4` 不识别。Only four layers appear: this is the intentional 2.3 limit; anything beyond `-4` is ignored.
- 透视框不出现：宿主可能是 Shell/安全窗口、权限不匹配，或无法修改 region。No portal: the host may be a Shell/security surface, have a different privilege level, or reject region changes.
- 点击后来源整体闪到桌面最前方：来源可能主动争夺前台；2.3 使用临时置顶屏障阻止普通来源覆盖宿主。若特定置顶应用仍可闪现，请记录应用名称、版本及其是否启用“窗口置顶”并反馈。A source flashes to the desktop foreground after a click: the source may be actively taking foreground. Report the app/version and whether its always-on-top option is enabled.
- 来源最小化后画面停止：Windows 可能不再提供稳定实时表面。A minimized source stops updating: Windows may no longer provide a stable real-time capture surface.

遇到问题时，先在记事本、文件资源管理器或普通浏览器页面之间验证；若基础窗口正常而特定应用失败，应按该应用不兼容处理，不要尝试关闭其安全机制。

When troubleshooting, first test with Notepad, File Explorer, or ordinary browser pages. If those work and one particular app fails, treat it as an app-specific incompatibility and do not disable that app's security mechanisms.
