# 寸镜 / PierceView 版本管理规范

## 版本号

采用 Semantic Versioning：`MAJOR.MINOR.PATCH`，候选版使用 `-rc.N`。

- `1.0.0-rc.N`：V6 单层圆形版候选包，等待真实桌面验收。
- `1.0.x`：只修缺陷、兼容性、恢复、文案和发布工程，不新增渲染能力。
- `2.0.0`：单层矩形 GPU 硬边透视。
- `2.1.0`：矩形 Alpha 羽化。
- `2.5.0`：最多 -4 层和宿主后方的层级交换。

每次版本变更同时更新：

1. 根目录 `VERSION`。
2. `src/WindowPortal/WindowPortal.csproj` 的 Version、AssemblyVersion、FileVersion、InformationalVersion。
3. `CHANGELOG.md`。
4. 对应测试结果文档。
5. 发布目录 `artifacts/QL-eye-v<version>` 和 Git tag `v<version>`。

`dotnet PierceView.dll --version` 必须输出 `PierceView <VERSION>`。自动测试通过后再生成发布物，发布物生成后再记录 SHA256。

## Git 流程

- `main`：已验证的稳定线。
- `release/1.0-v6`：当前 1.0 单层候选线。
- `feature/<name>`：明确范围的新能力。
- `fix/<name>`：不改变产品范围的修复。
- 多层实验保留在 `fix/v6-renderer-4-layer`，不得直接合并回 1.0。

合并前必须通过非 GUI 测试；窗口视觉和点击测试只能在 Windows 实机完成。发布提交使用 `release: PierceView x.y.z`，随后创建带注释 tag。

## 发布物

- Git 不跟踪 `bin/`、`obj/`、临时诊断截图和本机日志。
- 每个候选版保留 EXE、必要运行文件、SHA256、测试结果、已知问题和回退版本。
- RC 不标记为正式上线；只有用户验收清单完成后才改为 `1.0.0`。
- 公开分发前建议提供 Authenticode 签名和 SBOM；个人本机测试可先使用明确标注的未签名 RC。
