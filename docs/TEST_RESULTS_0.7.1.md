# QL eye 0.7.1 实测报告

## 结论

QL eye 0.7.1 针对 0.7.0 的视觉稳定性回归完成修复，并在开发构建和最终 `win-x64` 单文件发布物上通过当前自动化验收。实测未再出现 portal HWND 消失、region 丢失、颜色键矩形、不同圆心或整圆内容失效；三层画面、后台点击和受限 Z-order 提升保持正常。

本结论支持发布为“受控技术预览”，不等同于所有 Windows、GPU、页面、安全软件或游戏反作弊环境下的稳定版保证。

## 测试环境

| 项目 | 实测值 |
|---|---|
| 日期 | 2026-08-10（Asia/Shanghai） |
| 操作系统 | Microsoft Windows 11 专业版，10.0.26200，64 位 |
| 独立显卡 | NVIDIA GeForce RTX 5070，驱动 32.0.16.1088 |
| 核显 | AMD Radeon(TM) Graphics，驱动 32.0.21043.5001 |
| 主显示模式 | 2560 × 1440，系统报告 259 Hz |
| DPI | 120 DPI（125%） |
| .NET SDK | 10.0.302 |
| 宿主 | ChatGPT，`Chrome_WidgetWin_1` |
| 发布形式 | `net8.0-windows`、`win-x64`、framework-dependent、单文件 |
| 发布路径 | `artifacts\QL-eye-v0.7.1\WindowPortal.exe` |

## 0.7.0 回归与修复证据

| 回归 | 0.7.1 处理 |
|---|---|
| 圆形、矩形、圆形＋矩形交替 | 移除 `TransparencyKey` 和 `LWA_COLORKEY`；portal 仅使用 alpha layered window，并逐帧校验实际 window region |
| 画面间歇消失或变纯色 | 单帧 DWM prepare 失败时保留上一张成功画面；来源变化需连续稳定三帧后才重建 |
| 场景切换黑帧 | 新场景在旧场景仍可见时预热并 `DwmFlush`，确认后才撤下旧场景 |
| 两个不同步圆圈 | 交接前先把旧、新 portal 对齐到相同 bounds；移动使用一次 `DeferWindowPos` 且不重复改尺寸 |
| 多层边界黑缝 | 可渲染层依靠真实 portal HWND Z-order 遮挡，不再从深层 region 重复减去浅层矩形 |
| 不支持来源被深层画面冒充 | 只对不可渲染的浅层来源保留显式遮挡，受保护或不可捕获区域不会被更深层内容伪装 |

## 构建与纯逻辑检查

| 检查 | 结果 |
|---|---|
| Release 构建 | PASS，0 警告、0 错误 |
| `dotnet format --verify-no-changes` | PASS |
| `WindowPortal.exe --version` | `WindowPortal 0.7.1` |
| `WindowPortal.exe --self-test` | PASS，10/10 |
| 静态安全审计 | PASS |
| 版本一致性 | PASS，`VERSION`、项目元数据与 CHANGELOG 均为 0.7.1 |

静态审计确认源代码不包含进程注入、进程内存读写、合成输入、联网或持久化能力；清单为 `asInvoker`、`uiAccess=false`。F8 portal 激活期间使用 `WH_MOUSE_LL` 低级鼠标 hook 识别真实点击，结束透视后卸载。

## 最终发布物 GUI 自动化结果

| 用例 | 关键证据 | 结果 |
|---|---|---|
| 三层视觉 | 红/蓝/绿采样为 `217,74,74`、`53,107,214`、`53,167,101`；可渲染层数 3 | PASS |
| 单圆同步 | 动态采样的最大不同 portal bounds 数为 1 | PASS |
| region 稳定 | `MISSING_PORTAL_REGION_FRAMES=0` | PASS |
| portal 连续性 | `MISSING_PORTAL_WINDOW_FRAMES=0` | PASS |
| 无颜色键矩形 | `COLOR_KEY_PORTAL_WINDOW_COUNT=0` | PASS |
| layered alpha | `INVALID_ALPHA_LAYERED_FRAMES=0` | PASS |
| 整圆内容连续性 | `INVALID_COMPOSITE_FRAMES=0` | PASS |
| 性能预算 | 30 帧平均 11.76 ms、最慢 52.65 ms；门槛为平均 < 25 ms、最慢 < 150 ms | PASS |
| 深层点击 | 原 -2 收到 Click 1 次，原 -1 未收到 Click | PASS |
| 受限层级提升 | 点击后顺序为 `ChatGPT → 原 -2 → 原 -1`；ChatGPT 仍为前景 | PASS |
| 单层非激活点击 | 后台目标收到 Click 1 次，宿主前景与 Z-order 不变 | PASS |
| 样式恢复 | 临时 `WS_EX_NOACTIVATE` 在测试结束后恢复 | PASS |

探针会先做单点高频采样；若单点落在合法黑色窗口边框或黑色页面内容上，会立即抓取整圆并按来源内容覆盖率二次确认，避免把真实黑色 UI 误报为“黑帧”。诊断截图保存在本机忽略目录 `artifacts\diagnostics`，不纳入版本控制。

## 安全与发布物检查

| 检查 | 结果 |
|---|---|
| Microsoft Defender | `THREAT_COUNT=0` |
| Defender 引擎 | 1.1.26070.7 |
| Defender 病毒库 | 1.457.75.0，更新于 2026-08-09 08:09:12 +08:00 |
| 实时保护 | 已启用 |
| Authenticode | `NotSigned` |
| 文件大小 | 244,834 bytes |
| SHA-256 | `6263D0AB17763D6F2B9EC2FF16EA8FF8D796D23D17E7CAE7DA537E3072DE6B6C` |

Defender 的无检测结果只代表本机当时的引擎与规则。文件尚未进行 Authenticode 签名，因此仍可能触发 SmartScreen 或第三方安全软件的信誉提示。

## 页面与应用兼容边界

- 标准 Win32、WinForms、WPF，以及多数普通 Chromium/Electron 页面通常可以显示。
- League/Riot/Vanguard、EAC、BattlEye、FACEIT 等受保护游戏和反作弊相关进程默认排除；本轮没有、也不应使用真实账号做侵入式动态测试。
- UAC、安全桌面、登录/锁屏页面不可访问，QL eye 不尝试绕过。
- DRM/HDCP/Widevine 视频、独占全屏、Independent Flip、硬件 overlay、`WS_EX_NOREDIRECTIONBITMAP`、最小化或 cloaked 窗口可能黑屏、无画面或不可捕获。
- 本机 LoL/Riot 客户端看不到属于主动安全降级；这不是要通过绕过反作弊来修复的问题。

## 尚未覆盖的发布门槛

- Windows 10/11 多版本、100%/150%/200% DPI、多显示器异构 DPI和 Intel/AMD/NVIDIA 矩阵。
- 60/120/144/240 Hz 长时间主观频闪测试、P95/P99 帧时间及 GPU/CPU 占用采样。
- 独立 watchdog、崩溃恢复、安装/更新/回滚、代码签名、SBOM 与依赖漏洞扫描。
- 第三方多引擎安全软件与真实企业终端策略验证。

因此 0.7.1 仍建议作为受控技术预览，由用户保留 0.7.0 或更早版本以便比较和回退。
