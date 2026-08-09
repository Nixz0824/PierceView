# QL eye 0.7.1 技术预览

QL eye 是“背景透明计划”的产品名称；当前可执行模块仍名为 `WindowPortal.exe`。它在 Windows 顶层窗口上动态设置一个带圆形缺口的窗口区域，使缺口内真实显示并可点击后方窗口。

> 当前版本是兼容性技术验证，不是可公开发布的成品。它不会注入或修改 ChatGPT 文件，但会在运行期间临时修改目标窗口的 Win32 region。

## 环境要求

- Windows 10/11
- .NET 8 SDK 或更高版本

## 构建

```powershell
dotnet build .\src\WindowPortal\WindowPortal.csproj
```

## 运行

```powershell
dotnet run --project .\src\WindowPortal\WindowPortal.csproj
```

已经发布的视觉穿透版本位于：

```text
artifacts\QL-eye-v0.7.1\WindowPortal.exe
```

可以直接双击运行，不需要先执行构建命令；当前发布版本依赖本机已安装的 .NET 8 Runtime。

运行后：

- 把鼠标放在目标应用窗口上，按住 `F8`：锁定该窗口并创建圆形缺口。
- 圆洞在同一位置按实际 Z-order 合成最多三个后台应用的可见区域，不再逐层切换单一来源。
- 按住 `F8` 移动鼠标：缺口跟随鼠标。
- 圆洞内的真实鼠标点击会送到命中的后台应用；命中 -2、-3 等更深窗口时，该窗口只提升到 ChatGPT 正下方并成为新的 -1，原 -1 顺延，ChatGPT 始终保留在前台。
- 每次受限提升后，三层场景按新的 Z-order 自动重建。
- 如果任一已交互应用在点击处理器中主动调用 `SetForegroundWindow`，系统前台事件守卫会立刻恢复 ChatGPT，并把刚点击的窗口放回 ChatGPT 后面。
- 松开 `F8`：立即恢复目标窗口。
- 按 `Esc` 或 `Ctrl+Shift+Q`：恢复目标窗口并退出。
- 默认半径为 180 像素，可用 `--radius 240` 修改。

示例：

```powershell
dotnet run --project .\src\WindowPortal\WindowPortal.csproj -- --radius 220
```

## 自动验证

纯逻辑自检：

```powershell
dotnet run --project .\src\WindowPortal\WindowPortal.csproj -- --self-test
```

列出当前可见顶层窗口及 HWND：

```powershell
dotnet run --project .\src\WindowPortal\WindowPortal.csproj -- --list-windows
```

生成不修改任何窗口的只读兼容性报告：

```powershell
dotnet run --project .\src\WindowPortal\WindowPortal.csproj -- --compatibility-report
```

对指定窗口句柄执行短暂探测：

```powershell
dotnet run --project .\src\WindowPortal\WindowPortal.csproj -- --probe-hwnd 0x123456 --probe-duration-ms 1500
```

探测模式会在目标窗口中心创建圆形缺口，连续移动 30 帧并输出平均/最慢换帧耗时，随后确认圆心已被排除并自动恢复。

仓库还包含一个独立的 WinForms 测试窗口 `tests/WindowPortal.TestTarget`。它用于排除记事本等现代打包应用的启动进程重定向问题，不属于最终工具。

## 当前验证结果

- Release 构建：0 警告、0 错误。
- 纯逻辑自检：10/10 通过。
- 静态安全审计：无进程注入、进程内存访问、合成输入、联网和持久化能力。
- 独立测试窗口：中心圆洞、移动圆洞和恢复核对全部通过。
- 当前机器的 ChatGPT 主窗口（`Chrome_WidgetWin_1`）：圆洞生效，移动后旧圆心恢复，原始 region 类型 `2`，恢复后仍为 `2`。
- 0.7.1 合成架构：每层一个持久 DWM thumbnail，最多三层；依靠 portal HWND 的真实 Z-order 完成多层遮挡，场景切换使用同圆心预热交接。
- 增强视觉探针逐帧检查圆形 region、同步位置、颜色键、layered alpha、portal 消失和整圆内容覆盖率；发布物实测数据见 0.7.1 报告。
- 静止鼠标且来源窗口几何未变化时不会提交重复 DWM 更新。
- 对抗性非激活点击验证：后台测试程序收到 Click 后故意执行 `BringToFront`、`Activate` 和 `SetForegroundWindow`；焦点守卫触发回滚，ChatGPT 前台 HWND 与可见 Z-order 均保持不变，退出后扩展窗口样式恢复。
- 三层 Z-order 验证：初始顺序为 `ChatGPT → 小型 -1 → 大型 -2`；点击只被 -2 覆盖的圆洞区域后，真实 Click 成功，顺序变为 `ChatGPT → 原 -2 → 原 -1`，ChatGPT 前台 HWND 不变，两个后台窗口的临时扩展样式均在退出后恢复。
- 最终 EXE 经 Microsoft Defender 引擎 `1.1.26070.7`、病毒库 `1.457.75.0` 自定义扫描未检出威胁；文件尚未进行 Authenticode 签名，仍可能出现 SmartScreen 未知发布者提示。

## 安全边界

- 只操作按下 `F8` 瞬间鼠标所在的顶层窗口，不注入目标进程。
- 主动拒绝修改桌面、任务栏及常见 Windows Shell 窗口。
- 默认拒绝 League/Riot/Vanguard、EAC、BattlEye、FACEIT 等游戏与反作弊相关进程；请在启动游戏前退出 WindowPortal。
- 保存目标窗口原有 region，并在松键、正常退出、`Ctrl+C` 和常见未处理异常路径中恢复。
- 保存所有被点击后台窗口的原有扩展样式；穿透期间临时启用 `WS_EX_NOACTIVATE`，松开 `F8` 或退出时统一恢复。
- F8 portal 激活期间会临时安装 `WH_MOUSE_LL` 低级鼠标 hook，用于识别真实点击和受限层级提升；它不合成输入，但仍可能被游戏反作弊视为敏感，因此受保护游戏默认拒绝且启动游戏前应退出本工具。
- 如果目标应用以管理员权限运行，本工具通常也需要相同权限。
- 不要用任务管理器强制结束正在挖洞的工具；应先松开 `F8`，再按退出快捷键。即使恢复失败，关闭并重新打开目标应用通常也会重建完整窗口。
- 某些采用特殊合成、受保护内容或主动重设 region 的应用可能不兼容。
- DRM/受保护视频可能拒绝 DWM 缩略图；视觉源窗口会随受限层级提升动态切换。
- “保持前台层级”和“让后台应用获得键盘焦点”彼此冲突，因此当前非激活交互支持鼠标点击、拖动与滚轮；键盘仍输入到前台应用。

## 产品文档

- [产品需求文档](docs/PRD.md)
- [国内外市场调研](docs/MARKET_RESEARCH.md)
- [架构说明](docs/ARCHITECTURE.md)
- [兼容性说明](docs/COMPATIBILITY.md)
- [安全模型](docs/SECURITY.md)
- [测试计划](docs/TEST_PLAN.md)
- [0.7.1 实测报告](docs/TEST_RESULTS_0.7.1.md)
- [0.7.0 实测报告](docs/TEST_RESULTS_0.7.0.md)
- [发布检查清单](docs/RELEASE_CHECKLIST.md)
- [版本变更记录](CHANGELOG.md)
