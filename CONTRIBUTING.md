# 参与寸镜 / Contributing to PierceView

感谢你帮助寸镜变得更稳定。1.0 已冻结为 V6 单层圆形版本：欢迎提交缺陷、兼容性报告、文档改进和测试补充；多层、矩形与羽化功能请按路线图讨论，不会直接加入 1.0。

Thank you for helping PierceView become more reliable. Version 1.0 is frozen on the V6 single-layer circular core. Bug reports, compatibility findings, documentation improvements, and tests are welcome. Multi-layer, rectangular, and feathered portals follow the roadmap and will not be added directly to 1.0.

## 报告问题 / Report an issue

请尽量提供以下信息：

Please include as much of the following as possible:

- 寸镜版本与文件 SHA256。PierceView version and file SHA256.
- Windows 版本、显示缩放比例，以及是否使用多显示器。Windows version, display scaling, and multi-monitor setup.
- 当前窗口与后台来源窗口的应用类型和版本。Foreground and background app types and versions.
- 最短复现步骤、预期结果与实际结果。Minimal reproduction steps, expected result, and actual result.
- 问题属于视觉、点击、滚动、拖放、恢复、托盘还是设置。Whether the problem concerns visuals, click, scroll, drag-and-drop, restore, tray, or settings.

请不要上传包含私人聊天、文件名、账号、浏览记录、通知、令牌或本机绝对路径的截图和日志。必要时先裁剪并涂抹；不要使用真实游戏账号测试反作弊冲突。

Do not upload screenshots or logs containing private chats, filenames, accounts, browsing history, notifications, tokens, or absolute local paths. Crop and redact first when evidence is necessary. Do not use a real game account to test anti-cheat conflicts.

## 本地验证 / Local verification

需要 Windows 10/11 与 .NET 8 SDK。Requires Windows 10/11 and the .NET 8 SDK.

```powershell
dotnet build .\src\WindowPortal\WindowPortal.csproj -c Release
pwsh -File .\tests\run-non-gui-tests.ps1
pwsh -File .\tests\tray-smoke-test.ps1
```

涉及窗口行为的修改还应运行相关独立窗口探针，并在提交中说明人工验证环境。Changes affecting window behavior should also run the relevant independent-window probes and document the manual verification environment.

## 提交原则 / Pull request principles

- 保持托盘小工具定位，不引入账号、云服务、复杂前后端或无关依赖。Keep the tray-utility scope; do not add accounts, cloud services, complex frontends/backends, or unrelated dependencies.
- 1.0.x 只接受稳定性、兼容性、恢复、文案与发布工程改进。Version 1.0.x accepts only stability, compatibility, restore, copy, and release-engineering improvements.
- 不绕过 DRM、UAC、反作弊或其他保护机制。Do not bypass DRM, UAC, anti-cheat, or other protection mechanisms.
- 用户可见文案和仓库主页素材保持简体中文与 English 对照。Keep user-facing copy and repository-homepage assets bilingual in Simplified Chinese and English.
- 更新行为时同步更新测试、CHANGELOG 与相关文档。Update tests, CHANGELOG, and related documentation when behavior changes.

提交即表示你有权贡献其中内容；正式许可条款将在仓库加入 `LICENSE` 后确定。By contributing, you confirm that you have the right to submit the material. Formal contribution licensing will be defined when the repository adds a `LICENSE` file.
