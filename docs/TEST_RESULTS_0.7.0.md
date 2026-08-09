# WindowPortal 0.7.0 实测报告

## 结论

WindowPortal 0.7.0 在本机开发构建和最终 `win-x64` 单文件发布物上均通过当前自动化验收：三层画面同帧可见、所有视觉层同步移动、后台点击可送达、深层窗口只提升到宿主后方、退出后窗口样式与 region 恢复。发布物通过 Microsoft Defender 单文件扫描。

本结论只支持发布为技术预览，不支持标记为稳定 1.0，也不构成“所有安全软件或游戏反作弊均兼容”的保证。

## 测试环境

| 项目 | 实测值 |
|---|---|
| 日期 | 2026-08-10（Asia/Shanghai） |
| 操作系统 | Microsoft Windows 11 专业版，10.0.26200，64 位 |
| 内存 | 47.2 GB |
| 独立显卡 | NVIDIA GeForce RTX 5070，驱动 32.0.16.1088 |
| 核显 | AMD Radeon(TM) Graphics，驱动 32.0.21043.5001 |
| 主显示模式 | 2560 × 1440，系统报告 259 Hz |
| DPI | 120 DPI（125%） |
| .NET SDK | 10.0.302 |
| 宿主 | ChatGPT，`Chrome_WidgetWin_1` |
| 发布形式 | `net8.0-windows`、`win-x64`、framework-dependent、单文件 |

## 构建与纯逻辑检查

| 检查 | 结果 |
|---|---|
| Release 构建 | PASS，0 警告、0 错误 |
| `dotnet format --verify-no-changes` | PASS |
| `WindowPortal.exe --version` | `WindowPortal 0.7.0` |
| `WindowPortal.exe --self-test` | PASS，10/10 |
| 静态安全审计 | PASS |
| 版本一致性 | PASS，`VERSION`、项目元数据与 CHANGELOG 均为 0.7.0 |

静态审计确认产品源代码不包含进程注入、进程内存读写、合成输入、联网或持久化能力；清单为 `asInvoker`、`uiAccess=false`。产品在 F8 portal 激活期间会安装 `WH_MOUSE_LL` 低级鼠标 hook，用于识别真实点击并执行受限 Z-order 提升，结束透视后卸载。

## GUI 自动化结果

下表数据来自最终发布物 `artifacts\WindowPortal-v7\WindowPortal.exe`；相同用例也已对 Release 开发构建执行并通过。

| 用例 | 关键证据 | 结果 |
|---|---|---|
| 三层视觉与运动 | 红/蓝/绿采样像素分别为 `217,74,74`、`53,107,214`、`53,167,101`；可渲染层数 3 | PASS |
| 单圆同步 | 100 次采样中可见 portal HWND 的最大不同 bounds 数为 1 | PASS |
| 性能预算 | 30 帧平均 8.17 ms，最慢 40.85 ms；门槛为平均 < 25 ms、最慢 < 150 ms | PASS |
| 受限层级提升 | 点击 -2 后顺序从 `ChatGPT → -1 → -2` 变为 `ChatGPT → 原 -2 → 原 -1`；提升计数 1 | PASS |
| 深层点击 | 原 -2 收到 Click 1 次，原 -1 未收到 Click | PASS |
| 前景保持 | 点击后前景 HWND 仍为 ChatGPT；焦点守卫回滚 2 次 | PASS |
| 单层不激活点击 | 后台目标收到 Click 1 次，宿主前景和 Z-order 不变 | PASS |
| 样式恢复 | `WS_EX_NOACTIVATE` 在测试期间生效，结束后恢复原始扩展样式 | PASS |
| region 恢复 | 原始和结束后的 region 类型一致 | PASS |

截图证据保存在本机 `artifacts\diagnostics\multilayer-visual-probe.png`。该目录被 Git 忽略，避免提交可能包含私人桌面内容的诊断图片。

## 安全与发布物检查

| 检查 | 结果 |
|---|---|
| Microsoft Defender | `NO_THREAT_DETECTED` |
| Defender 引擎 | 1.1.26070.7 |
| Defender 病毒库 | 1.457.75.0，更新于 2026-08-09 08:09:12 |
| 实时保护 | 已启用 |
| Authenticode | `NotSigned` |
| 文件大小 | 242,274 bytes |
| SHA-256 | `07EEF5F43075F42D610CC9525785248BA02831989BDD91A7EBDDF17116698865` |

Defender 的单次无检测结果只代表本机当时的引擎和规则。未签名文件仍可能触发 SmartScreen 或第三方安全软件的信誉提示。

## 游戏与反作弊边界

只读兼容性报告已把 `League of Legends`、`LeagueClientUx` 和带空格的 `Riot Client` 识别为 `Protected`。WindowPortal 默认不对这些窗口注册 DWM thumbnail，也不修改其样式或 Z-order。

本轮没有在真实游戏、Vanguard、EAC、BattlEye 或 FACEIT 会话中执行动态兼容性测试，也不应使用真实账号尝试证明“不会封禁”。低级鼠标 hook 和 overlay 类行为可能被不同反作弊策略视为敏感；正确的产品策略是默认拒绝并要求用户在启动游戏前退出 WindowPortal，而不是尝试绕过。

## 尚未覆盖的发布门槛

- Windows 10/11 多版本、100%/150%/200% DPI、多显示器异构 DPI和 Intel/AMD/NVIDIA 矩阵。
- 60/120/144/240 Hz 长时间主观频闪测试、P95/P99 帧时间和 GPU/CPU 占用采样。
- DRM/HDCP、Independent Flip、硬件 overlay、独占全屏和 `WS_EX_NOREDIRECTIONBITMAP` 的完整实机矩阵。
- 强制终止后的独立 watchdog 恢复。
- Authenticode 代码签名、SBOM、依赖漏洞扫描和独立多引擎恶意软件扫描。
- 安装、更新、回滚、崩溃收集、隐私与支持流程。

因此 0.7.0 的发布建议为“受控技术预览”；完成上述矩阵和发布工程后再评估 0.9/1.0。
