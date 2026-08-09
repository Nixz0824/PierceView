# WindowPortal 兼容性说明

## 支持良好的类型

- 标准 Win32/GDI 窗口。
- WinForms、WPF、普通桌面应用。
- 大多数处于 DWM 合成模式的 Chromium/Electron 页面，包括普通网页、Codex/ChatGPT、文件管理器和常规工具窗。
- 非独占全屏、未启用受保护内容的浏览器页面。

## 无效果、黑屏或只显示部分画面的类型

| 类型 | 原因 | v7 行为 |
|---|---|---|
| League of Legends、Valorant、Riot/Vanguard、EAC、BattlEye、FACEIT 等 | 游戏/反作弊可能监视全局 hooks、窗口样式和覆盖层；兼容性与账号风险不可通过普通功能测试证明 | 默认禁止 DWM 注册、样式修改和 Z-order 操作 |
| UAC、登录、锁屏、Ctrl+Alt+Del、安全凭据页面 | 位于安全桌面或受系统访问控制保护 | 无法访问且明确不尝试绕过 |
| Netflix 等 DRM/HDCP/Widevine 受保护视频 | DWM/截图 API 会得到黑色或拒绝受保护表面 | 浏览器 UI 可能显示，视频区域可能为黑色 |
| 独占全屏游戏、Independent Flip、硬件 overlay | 画面可能绕过普通 DWM 重定向 | 不保证视觉预览；建议先切换无边框窗口模式，但受保护游戏仍默认排除 |
| `WS_EX_NOREDIRECTIONBITMAP` 窗口 | 没有 DWM thumbnail 可读取的重定向位图 | 兼容性报告显示 `VisualUnsupported`；不伪造更深层画面 |
| 其他虚拟桌面的 cloaked 窗口、最小化窗口 | DWM 暂停或隐藏其正常表面 | 不参与当前三层合成 |
| 管理员权限高于 WindowPortal 的窗口 | UIPI/权限边界可能拒绝 region、样式或 Z-order 修改 | 保持 asInvoker；不自动提权，必要时由用户用相同权限启动 |
| per-pixel layered、透明覆盖层、特殊圆角窗口 | 当前遮挡算法以顶层矩形近似 | 可能出现少量边缘误遮挡，计划在 0.8 使用窗口 region/扩展 frame 精细裁剪 |

## 为什么 LoL 客户端看不到

本机只读报告已经识别到：

- `League of Legends`：`Protected`，视觉与交互均关闭。
- `LeagueClientUx`：`Protected`，视觉与交互均关闭。
- `Riot Client`：忽略空格后仍匹配 Riot 保护规则。

这是主动的安全降级，不是计划绕过的 bug。即使某个版本的 LeagueClientUx 能被 DWM 注册，也不能据此推断 Vanguard 或未来反作弊版本允许窗口 overlay/hook。对真实账号进行“是否会封禁”的实验没有可接受的完备性和可逆性。

## 用户自助诊断

```powershell
WindowPortal.exe --compatibility-report
```

该模式仅枚举可见窗口和样式，不安装鼠标 hook，不修改 region、Z-order 或窗口样式。
