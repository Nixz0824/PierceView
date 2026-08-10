# 参与寸镜 / Contributing to PierceView

感谢你帮助寸镜变得更稳定。欢迎提交缺陷、兼容性报告、文档改进和测试补充。1.0 定位为轻量托盘小工具与单层圆形透视；请保持范围克制，避免把无关平台能力塞进本版本。

Thank you for helping PierceView become more reliable. Bug reports, compatibility findings, documentation improvements, and tests are welcome. Version 1.0 is a lightweight tray utility with a single-layer circular portal—please keep the scope tight and avoid unrelated platform features.

## 许可与贡献 / License & contributions

本仓库采用 [PolyForm Noncommercial License 1.0.0](LICENSE)，**禁止商业用途**。提交代码或文档即表示：

This repository uses the [PolyForm Noncommercial License 1.0.0](LICENSE) (**noncommercial only**). By submitting code or documentation, you confirm that:

1. 你有权提交这些内容。You have the right to submit the material.
2. 你同意贡献内容在相同许可条款下成为本项目的一部分。You license your contribution under the same terms as this project.
3. 你不会通过贡献引入需要商业授权或与本许可冲突的依赖。You will not introduce dependencies that require commercial licensing or conflict with these terms.

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
- 1.0.x 优先接受稳定性、兼容性、恢复、文案与发布工程改进。Version 1.0.x prefers stability, compatibility, restore, copy, and release-engineering improvements.
- 不绕过 DRM、UAC、反作弊或其他保护机制。Do not bypass DRM, UAC, anti-cheat, or other protection mechanisms.
- 用户可见文案和仓库主页素材保持简体中文与 English 对照。Keep user-facing copy and repository-homepage assets bilingual in Simplified Chinese and English.
- 更新行为时同步更新测试、CHANGELOG 与相关公开文档。Update tests, CHANGELOG, and related public documentation when behavior changes.
- 不要把内部规划、未公开路线或私人桌面材料提交进仓库。Do not commit internal planning notes, unpublished roadmaps, or private desktop material.
