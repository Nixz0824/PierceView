# WindowPortal 0.7 测试计划

## 测试层级

| 层级 | 资产 | 目标 |
|---|---|---|
| 纯逻辑 | `WindowPortal.exe --self-test` | 参数、坐标、兼容策略与反作弊名称归一化 |
| 静态安全 | `tests/security-static-audit.ps1` | 禁止能力、清单权限和必需恢复机制 |
| 单层交互 | `tests/nonactivating-click-probe.ps1` | 真实 Click、前台保持、样式恢复、对抗性 `SetForegroundWindow` |
| 深层提升 | `tests/constrained-zorder-promotion-probe.ps1` | `宿主 → -1 → -2` 交换为 `宿主 → -2 → -1` |
| 三层视觉/运动 | `tests/multilayer-visual-probe.ps1` | 红/绿/蓝三层同时出现、所有层位置同步、性能预算、截图证据 |
| 诊断 | `--compatibility-report` | 实机窗口分类；只读且不安装 hook |
| 发布物 | 发布目录 EXE 重跑上述测试 | 防止“开发构建通过、单文件发布失败” |

## P0 用例

1. F8 启用/松开恢复 1,000 次，region 类型和样式无残留。
2. 鼠标静止 10 秒，DWM 更新次数不增加。
3. 连续移动 30 帧，所有可见 portal layer HWND 的 bounds 始终完全一致。
4. 三窗口非重叠切片中，三个采样点分别匹配红、蓝、绿预期颜色。
5. 深层窗口主动 `BringToFront`、`Activate`、`SetForegroundWindow` 后，宿主前台保持。
6. League/Riot/EAC/BattlEye 名称均输出 `Protected`。
7. 正常退出、Esc、Ctrl+C 后恢复。

## 性能门槛

- 自动门槛：平均 < 25 ms，最慢 < 150 ms，作为跨机器宽松回归线。
- 产品目标：平均 < 20 ms，P95 < 33 ms；性能报告必须同时记录 CPU、GPU、刷新率、缩放和窗口数量。
- 任何持续黑帧、两个不同位置的 portal forms 或闪烁超过一帧视为 P0 缺陷。

## 系统与硬件矩阵（正式 1.0 前必须完成）

- Windows 10 22H2；Windows 11 23H2、24H2、25H2 或发布时仍受支持版本。
- 100%、125%、150%、200% DPI；单屏、双屏、异构 DPI。
- Intel、AMD、NVIDIA；混合显卡笔记本；60/120/144/240 Hz。
- 标准 Win32、WPF、WinUI 3、UWP、Chromium/Electron、浏览器、文件管理器。
- 管理员权限匹配/不匹配；虚拟桌面；最小化/恢复；窗口重建。

## 不执行的危险测试

- 不在真实在线游戏账号上尝试绕过反作弊或验证“不会封禁”。
- 不关闭安全软件、不添加全局白名单。
- 不使用强制终止作为常规恢复证明；该路径单独记录为 watchdog 未完成风险。
