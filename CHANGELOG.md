# Changelog

本项目采用 [Semantic Versioning](https://semver.org/)；0.x 版本表示接口与行为仍可能变化。

## [0.7.1] - 2026-08-10

### Fixed

- 移除 `TransparencyKey` 颜色键，改为 alpha-only layered portal，修复圆形、矩形和圆形＋矩形随机交替，以及部分来源区域退化为纯色的问题。
- 场景来源连续稳定三帧后才重建；点击引起的受限 Z-order 变化仍立即更新，减少鼠标跨窗口边界时的反复销毁与重建。
- 新场景先在上一张成功画面上完成 DWM 预热，再撤下旧场景；旧、新场景交接前强制对齐同一圆心，消除空窗黑帧和双圆位置分裂。
- 多层遮挡改为使用 portal HWND 的真实 Z-order，不再从深层 region 重复减去浅层矩形，消除异步更新时出现的黑色裁剪缝。
- 单帧 DWM prepare 失败时继续移动并保留上一张成功画面；只有 HWND 移动本身失败才撤下失效场景。
- portal 移动不再逐帧重复设置尺寸；每帧校验并修复丢失或异常的窗口 region。

### Verification

- 增强三层视觉探针，逐帧检查窗口是否消失、region、位置同步、颜色键、layered alpha 和整圆内容覆盖率。
- Release 构建 0 警告、0 错误；纯逻辑自检 10/10、静态安全审计、格式检查、非激活点击和受限 Z-order 提升均通过。
- 最终 `win-x64` 单文件发布物通过相同 GUI 回归与 Microsoft Defender 扫描；SHA-256 为 `6263D0AB17763D6F2B9EC2FF16EA8FF8D796D23D17E7CAE7DA537E3072DE6B6C`。

### Known issues

- 仍属于受控技术预览；DWM 不提供可捕获表面的窗口、DRM 内容、独占全屏、硬件 overlay 和受保护游戏不会获得视觉穿透保证。
- 未签名构建可能触发 SmartScreen 未知发布者提示；启动受保护游戏前应退出 QL eye。

## [0.7.0] - 2026-08-09

### Added

- 单圆、多窗口 DWM 合成器，最多同时显示 -1、-2、-3 的可见区域。
- `CompatibilityPolicy`，默认排除 League/Riot/Vanguard、EAC、BattlEye、FACEIT 和安全桌面相关窗口。
- 只读 `--compatibility-report`。
- 三层视觉/运动自动测试和静态安全审计。
- PRD、市场调研、架构、兼容性、安全、测试与发布文档。
- 语义化版本元数据与 `VERSION` 文件。

### Changed

- 移除逐帧双缓冲圆窗交换和 3px DWM 条带。
- 每层从约 121 个 DWM thumbnails 降为一个；所有图层通过一次 DeferWindowPos 同步移动。
- 深层窗口只提升到宿主正下方，视觉来源按 Z-order 动态重建。

### Security

- 明确禁止游戏/反作弊进程的 DWM 注册、扩展样式和 Z-order 修改。
- 清单保持 `asInvoker` 与 `uiAccess=false`。

### Verification

- Release 构建 0 警告、0 错误；纯逻辑自检 10/10，静态安全审计和格式检查通过。
- 开发构建与最终单文件发布物均通过三层视觉/同步移动、不激活点击和受限 Z-order 提升测试。
- 最终发布物三层移动探针平均 8.17 ms、最慢 40.85 ms，100 次位置采样未出现不同步 portal bounds。
- Microsoft Defender 单文件扫描未检出威胁；SHA-256 为 `07EEF5F43075F42D610CC9525785248BA02831989BDD91A7EBDDF17116698865`。

### Known issues

- 未签名构建可能触发 SmartScreen 未知发布者提示。
- 强制终止不能保证恢复；独立 watchdog 计划在 0.8 实现。
- 顶层矩形遮挡近似对 per-pixel layered window 可能有边缘误差。

## [0.6.0] - 2026-08-09

- 加入 `WH_MOUSE_LL` 受限 Z-order 提升：点击 -2/-3 时只提升到宿主之后。
- 多后台窗口 `WS_EX_NOACTIVATE` 恢复与前台守卫。

## [0.5.0] - 2026-08-09

- DWM 条带双缓冲视觉圆、静止帧跳过、前台恢复守卫。
