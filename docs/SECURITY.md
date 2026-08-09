# WindowPortal 安全模型

## 当前安全结论

0.7 的设计比典型游戏 overlay 更保守：不注入、不读取进程内存、不创建远程线程、不安装驱动、不模拟输入、不联网、不自启动。它仍会在 F8 会话期间使用全局低级鼠标 hook、DWM thumbnails、`SetWindowRgn`、`SetWindowLongPtr` 和 `SetWindowPos`，因此未经代码签名的构建仍可能触发信誉型安全提示，某些反作弊也可能把这些行为视为不兼容。

## 权限与 API 边界

- 清单：`requestedExecutionLevel=asInvoker`，`uiAccess=false`。
- 允许：窗口枚举、DWM 缩略图、窗口 region、扩展样式、Z-order、前台 WinEvent、`WH_MOUSE_LL`。
- 禁止且静态测试会检查：`Read/WriteProcessMemory`、`VirtualAllocEx`、`CreateRemoteThread`、DLL 注入、键盘 hook、合成输入、网络客户端、注册表自启动、服务和计划任务。

## 威胁模型

1. 恶意数据采集：当前无网络与磁盘遥测；鼠标 hook 不记录坐标历史，只在按下时计算目标窗口。
2. 窗口残留：正常退出路径恢复；强制终止仍是已知风险，0.8 需要 watchdog。
3. 权限提升：应用不请求管理员权限，不绕过 UIPI。
4. 反作弊冲突：默认进程排除；不对真实在线游戏执行自动化修改测试。
5. 供应链与信誉：当前单文件 EXE 未签名；正式发布必须 Authenticode 签名、保留 SHA256、生成 SBOM 并通过 Defender/多引擎扫描。

## 安全软件与反作弊说明

- “本机 Defender 未报毒”只能证明该版本、该签名和该规则库下没有命中，不能证明所有安全软件永不告警。
- “一次运行没有被游戏封禁”不是有效安全证明；反作弊规则可延迟执行、服务器侧变化且通常不公开。
- 0.7 明确不支持在受保护游戏/客户端上使用。用户应在启动游戏前退出 WindowPortal，而不是只松开 F8。
- 未签名构建可能被 Windows SmartScreen 显示“未知发布者”；这是信誉/签名问题，不等同于恶意代码检测。

## 报告与响应

若发生安全软件告警，应记录：产品版本、SHA256、安全软件与规则版本、告警名称、命中路径、是否在 F8 活动期。确认前停止分发该构建；不得要求用户关闭安全软件或给整个目录加白名单。
