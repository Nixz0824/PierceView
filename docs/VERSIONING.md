# 版本管理规范

## 版本号

采用 Semantic Versioning：`MAJOR.MINOR.PATCH`。

- 0.x：技术预览，行为和兼容策略可以变化。
- PATCH：修复，不增加新的用户级能力。
- MINOR：新增兼容能力、交互或重要架构变化。
- 1.0：完成代码签名、watchdog、安装/更新、硬件矩阵和公开支持流程。

版本必须同时更新：

1. 根目录 `VERSION`。
2. `WindowPortal.csproj` 的 Version、AssemblyVersion、FileVersion 和 InformationalVersion。
3. `CHANGELOG.md`。
4. 发布目录 `artifacts/QL-eye-vMAJOR.MINOR.PATCH` 与 Git tag `vMAJOR.MINOR.PATCH`。

`WindowPortal.exe --version` 必须与 `VERSION` 完全一致，CI 会自动验证。

## Git 流程

- `main`：始终可构建。
- `feature/<name>`：功能开发。
- `fix/<name>`：缺陷修复。
- 合并前必须通过非 GUI CI；窗口交互与视觉测试在 Windows 实机完成并保存日志。
- 发布提交使用 `release: WindowPortal x.y.z`，随后创建带注释 tag。

## 发布物

- Git 不跟踪 `bin/`、`obj/`、`artifacts/` 和包含桌面截图的 diagnostics。
- 每个发布版保留单文件 EXE、SHA256、测试结果摘要和已知问题。
- 0.9 起必须提供 Authenticode 签名和 SBOM。
