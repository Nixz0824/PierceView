# 寸镜 / PierceView 1.0.0-rc.1

![寸镜 PierceView Logo](assets/brand/ql-eye-logo-concept-v5-content-continuity-bw.png)

寸镜（PierceView）是一个以效率为核心的 Windows 系统托盘小工具。工作时经常同时打开多个应用和网页，但很多次 `Alt+Tab` 只是为了看一眼后台的一小块关键信息，切过去再切回来反而打断思路。寸镜启动后不显示主窗口；只要按住 `F8`，鼠标附近就会出现一个圆形透视区域，让你直接查看当前窗口后面紧贴的一层普通桌面应用，松开即恢复。

透视不只是“看”。你可以直接滚动、点击后台应用或网页；在两端都支持 Windows 原生拖放时，还可以在后台选中文字、图片或文件并保持拖动，松开 `F8` 后把它带到当前应用，在合适位置放手完成跨应用复制。具体能否复制由两端应用的拖放实现和权限决定。

当然，在等待 AI 处理任务或回复的间隙，寸镜也能让你不用切走当前页面，就轻松看一眼后台内容——包括偶尔摸个鱼。

当前版本以已验证过的 V6 单层渲染内核为基础。1.0 冻结渲染功能，不加入多层合成、深层窗口识别或后台层级重排。

## 使用

双击 `PierceView.exe` 后，从 Windows 通知区域找到寸镜图标。

- 按住 `F8`：开启并移动透视圆。
- 松开 `F8`：关闭透视并恢复宿主窗口。
- 双击托盘图标：打开设置。
- 右键托盘图标：启动/暂停透视、设置、帮助、退出。

设置只有两项：透视圆半径、中文/英文。配置保存在 `%LOCALAPPDATA%\PierceView\settings.json`。

## 重要限制

- 1.0 只识别当前窗口后方的一层；不会继续寻找 -2、-3 或 -4。
- 普通 Win32、WinForms、WPF、浏览器和常规 Electron 窗口通常兼容较好。
- 游戏、反作弊、DRM 视频、UAC 安全桌面、独占全屏、部分 GPU 自绘窗口和无重定向窗口可能只有点击、没有视觉画面，或者完全不可用。
- 启动任何游戏、游戏启动器或反作弊服务前，请从托盘完全退出寸镜。此说明不限于网游。
- 工具不注入进程、不读写其他进程内存、不模拟输入、不联网、不安装全局低级鼠标钩子，也不会自动随 Windows 启动。
- 未签名的候选版可能出现 Windows SmartScreen“未知发布者”提示。

完整说明见[兼容性文档](docs/COMPATIBILITY.md)和[安全模型](docs/SECURITY.md)。

## 构建与测试

```powershell
dotnet build .\src\WindowPortal\WindowPortal.csproj -c Release
pwsh -File .\tests\run-non-gui-tests.ps1
pwsh -File .\tests\tray-smoke-test.ps1
```

诊断命令建议通过 DLL 运行，以便 GUI 子系统构建仍能稳定输出日志：

```powershell
dotnet .\src\WindowPortal\bin\Release\net8.0-windows\PierceView.dll --self-test
dotnet .\src\WindowPortal\bin\Release\net8.0-windows\PierceView.dll --version
dotnet .\src\WindowPortal\bin\Release\net8.0-windows\PierceView.dll --list-windows
```

## 当前候选版状态

- Release 构建：0 警告、0 错误。
- 纯逻辑自检：12/12 通过，包含 Logo 资源和“仅两个设置项”的窗口结构检查。
- 托盘冒烟测试：无主窗口、自动正常退出、无残留进程。
- 静态安全审计：无注入、进程内存访问、合成输入、联网、自启动持久化和全局低级鼠标钩子。
- 最终自包含 EXE：Microsoft Defender 扫描威胁数 0；SHA256 `41EE0FD2DFB4B437C80D2FD9E89EB0C17F173B4769436E98E5BF8CA373F65A6D`。
- 最终视觉流畅度、目标应用兼容性和长时间稳定性仍需用户在真实桌面上验收，因此当前标记为 `1.0.0-rc.1`，不是正式 `1.0.0`。

## 文档

- [PRD](docs/PRD.md)
- [产品路线图](docs/ROADMAP.md)
- [市场调研](docs/MARKET_RESEARCH.md)
- [架构](docs/ARCHITECTURE.md)
- [兼容性](docs/COMPATIBILITY.md)
- [安全模型](docs/SECURITY.md)
- [测试计划](docs/TEST_PLAN.md)
- [1.0.0-rc.1 测试结果](docs/TEST_RESULTS_1.0.0-rc.1.md)
- [发布检查清单](docs/RELEASE_CHECKLIST.md)
- [版本管理](docs/VERSIONING.md)
- [变更记录](CHANGELOG.md)

0.7.x 文档保留为历史实验记录，不代表 1.0 的功能承诺。
