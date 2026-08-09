# 寸镜 / PierceView 1.0 安全模型

## 报告安全问题 / Reporting a security issue

请不要在公开 Issue 中发布利用细节、令牌、私人桌面内容或可执行样本。优先使用 GitHub 仓库 **Security → Report a vulnerability** 提交私密报告；如果该入口尚未启用，只创建一条不含敏感细节的 Issue，请求建立私密沟通渠道。

Do not post exploit details, tokens, private desktop content, or executable samples in a public issue. Prefer **Security → Report a vulnerability** in the GitHub repository. If private reporting is not enabled yet, open an issue only to request a private contact channel and omit all sensitive details.

报告应包含受影响版本与 SHA256、Windows 版本、最小复现条件、潜在影响，以及问题是否需要游戏或安全软件参与。请勿使用真实游戏账号进行高风险验证。

Include the affected version and SHA256, Windows version, minimal conditions, potential impact, and whether games or security software are involved. Do not use a real game account for risky verification.

## 当前结论

寸镜是本地、普通用户权限的窗口工具。它不注入 DLL、不读取或写入其他进程内存、不创建远程线程、不安装驱动、不模拟输入、不联网、不采集遥测、不设置 Windows 自启动，也不安装全局低级键盘或鼠标钩子。

这不等于“不会与任何安全软件冲突”。寸镜会临时修改窗口 region、扩展样式和 Z-order，并创建 DWM thumbnail；未签名 EXE 还可能触发 SmartScreen 信誉提示。

## 使用的系统能力

- `GetAsyncKeyState(F8)`：轮询 F8 当前状态，不注册键盘钩子。
- `SetWindowRgn`：为宿主窗口创建并移动圆形缺口。
- DWM thumbnail API：复制一个后台窗口的可用桌面表面。
- `SetWindowLongPtr(WS_EX_NOACTIVATE)`：临时避免来源窗口因鼠标操作激活。
- `SetWindowPos`、`SetForegroundWindow`、WinEvent：当来源争夺前台时恢复宿主顺序。
- `%LOCALAPPDATA%\PierceView\settings.json`：仅保存半径和语言。

应用清单固定为 `asInvoker`、`uiAccess=false`，不会自动请求管理员权限。

## 明确不存在的能力

- 进程注入、内存扫描、DLL 加载、远程线程。
- 合成键盘或鼠标输入。
- `WH_MOUSE_LL` / `WH_KEYBOARD_LL` 全局低级钩子。
- 网络请求、上传、自动更新和遥测。
- 注册表 Run 项、计划任务、服务或其他自启动持久化。
- 绕过 DRM、反作弊、UAC 或安全桌面。

## 游戏与反作弊

寸镜 1.0 已移除多层实验版使用过的低级鼠标钩子，但窗口覆盖、DWM 复制和 Z-order 操作仍可能与某些游戏或反作弊策略冲突。无法通过普通功能测试证明“绝不触发封禁”，也不应使用真实游戏账号做破坏性验证。

安全使用规则：在启动任何游戏、游戏启动器或反作弊服务前完全退出寸镜。此规则同时适用于网游和带反作弊的单机游戏。

## 恢复边界

松开 F8、暂停、保存新半径、托盘退出和普通进程结束会恢复已保存的窗口 region 与扩展样式。任务管理器强制结束、电源中断或进程崩溃不能保证托管清理代码运行；不要在 F8 按住时强制终止。

## 发布安全门槛

- 每个候选包运行静态能力审计。
- 对最终发布 EXE 记录 SHA256。
- 正式公开发布前建议完成 Authenticode 签名和 Defender 扫描。
- 任何安全软件或反作弊误报都应先停止分发、保留样本和版本哈希，再与厂商核实；不得指导用户绕过检测。
