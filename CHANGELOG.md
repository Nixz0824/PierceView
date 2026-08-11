# Changelog

本项目采用 [Semantic Versioning](https://semver.org/)。用户可见变更记录如下。

This project follows [Semantic Versioning](https://semver.org/). User-facing changes are listed below.

## [Unreleased]

### Documentation

- （暂无）

## [2.1.0-alpha.2] - 2026-08-11

### Fixed

- 修复按住 F8 且鼠标静止时透视画面停在旧帧：现在按轮询节奏持续抓取并提交来源画面，只有未变化的 DWM 裁剪参数会跳过重复设置。
- Fix the portal freezing on an old frame while F8 is held and the pointer stays still. Source frames are now continuously captured and presented; only unchanged DWM crop properties skip redundant updates.
- 前台/Z-order 恢复改为独立异步工作，不再在 DWM 覆盖层消息线程中同步执行，降低后台点击时的画面停顿。
- Foreground/Z-order recovery now runs as independent asynchronous work instead of blocking the DWM overlay message thread, reducing click-time stalls.
- 矩形使用真正的圆角 alpha 蒙版与匹配的圆角物理缺口；硬边模式保留圆角，羽化沿圆角轮廓平滑向内过渡。
- Rectangles now use a true rounded alpha mask and matching rounded physical hole. Hard-edge mode keeps rounded corners; feathering follows the rounded contour inward.

### Performance

- 圆形/圆角矩形 alpha 蒙版在启用时预计算一次，避免每帧重复计算形状距离。
- Circle and rounded-rectangle alpha masks are precomputed once on activation instead of recalculating shape distances every frame.

## [2.1.0-alpha.1] - 2026-08-11

### Added

- 矩形模式新增 0–80 像素边缘羽化，默认 24 像素；0 保留 2.0 的硬边效果，较小矩形会自动限制最大值。
- Rectangle mode adds 0–80 px edge feathering, defaulting to 24 px. Set it to 0 for the 2.0 hard edge; smaller rectangles limit the maximum automatically.

### Changed

- 羽化带从当前层像素线性过渡到后台画面；物理穿透区域只使用完全不透明的内矩形，鼠标中心始终位于完整穿透区。
- The feather band linearly transitions from foreground pixels to the background image. The physical hit-through hole uses only the fully opaque inner rectangle, keeping the pointer center in the fully open area.

### Tests

- 视觉冒烟同时覆盖圆形、硬边矩形与羽化矩形，并校验外沿透明、过渡带预乘 alpha、内沿不透明及连续移动黑帧。
- Visual smoke now covers circle, hard rectangle, and feathered rectangle, checking transparent outer edge, premultiplied-alpha transition, opaque inner edge, and moving black frames.

## [2.0.0-alpha.1] - 2026-08-11

### Added

- 新增可选的单层硬边矩形透视，默认尺寸为 420×280，可在设置中调整宽高。
- Add an optional single-layer hard-edged rectangular portal, defaulting to 420×280 with configurable width and height.
- 设置新增透视形状选择；新安装默认矩形，已有 1.x 配置继续使用圆形兼容模式。
- Add portal-shape selection. New installs default to rectangle; existing 1.x settings remain on the compatible circle mode.

### Compatibility

- 圆形模式完整保留 1.0.6 的单张 DWM 捕获、预乘 alpha 与整帧 `UpdateLayeredWindow` 合成路径。
- Circle mode preserves the 1.0.6 single-DWM-capture, premultiplied-alpha, full-frame `UpdateLayeredWindow` pipeline.

## [1.0.6] - 2026-08-10

### Changed

- 就绪提示改为**每次启用**都弹出托盘气泡（程序启动启用、以及托盘「启动透视」从暂停恢复时），不再仅首次运行一次。
- Ready tip balloon now shows on **every enable** (app launch and tray “Start portal” after pause), not only the first run.

## [1.0.5] - 2026-08-10

### Fixed

- 重做透视合成：屏外单张 DWM 缩略图捕获 → **圆形预乘 alpha 蒙版** → `UpdateLayeredWindow` 整帧提交。避免「全幅+Region 变方/闪圆」与「条带重影」「双缓冲换帧闪烁」。
- Rebuild portal compositing: off-screen single DWM thumbnail capture → **circular premultiplied alpha mask** → one `UpdateLayeredWindow` present. Avoids square/region flash, band ghosting, and dual-buffer flicker.
- 新增 `--visual-smoke` 自动视觉冒烟（自建红/绿窗采样圆角），减少纯手测回归。
- Add `--visual-smoke` automated visual smoke (red/green fixture pixel checks).

## [1.0.4] - 2026-08-10

### Fixed

- 明显减轻移动时圆内「重影/拖影」：去掉多条带非原子更新，改为**每缓冲单张全幅 DWM 缩略图**；移动时在隐藏缓冲上整帧准备后**原子换显**，避免「改源再移窗」错帧；换帧后强制隐藏并停放旧窗；每帧强制 `Form.Region` 保持圆形。
- Greatly reduce in-portal ghosting while moving: one full DWM thumbnail per buffer (no multi-band tear), prepare the hidden buffer then atomically present, force-hide/park the previous frame, and re-apply circular `Form.Region` each update.

## [1.0.3] - 2026-08-10

### Fixed

- 修复 1.0.2 透视变成「方框+圆」叠影、随后只剩矩形的问题：取消双窗换帧（避免两窗叠显），恢复**条带缩略图保证圆内容**，并用每帧强制的 `Form.Region` 椭圆裁掉条带外黑底；移动路径去掉 `DwmFlush` 以减轻浏览器标题栏/地址栏抖动。
- Fix 1.0.2 portal becoming a rectangle/circle stack then a plain rectangle: drop dual-window swap, restore **band thumbnails for circular content**, re-apply `Form.Region` every update to clip black outside the circle; skip move-path `DwmFlush` to reduce chrome/title-bar jitter.

## [1.0.2] - 2026-08-10

### Fixed

- 减轻按住 F8 **移动时圆内画面重影/抖动**：改用**单张全幅 DWM 缩略图**（圆由 `SetWindowRgn` 裁剪，不再用多条带非原子更新），并恢复**双缓冲准备后原子换帧**，避免“改源再移窗”的错帧拖影；仍不使用 `TransparencyKey`。
- Reduce **in-portal ghosting/jitter while moving** under F8: one full-frame DWM thumbnail (circle via `SetWindowRgn`, no multi-band partial updates) and dual-buffer prepare-then-atomic swap so position and content stay aligned; still no `TransparencyKey`.

## [1.0.1] - 2026-08-10

### Fixed

- 降低按住 F8 移动透视圆时的画面频闪：移动路径改为单缓冲原位更新（不再每帧显隐换帧），并用圆形 `SetWindowRgn` 替代 `TransparencyKey` 色键。
- Reduce portal flicker while moving under F8: in-place single-buffer updates (no per-frame show/hide swap) and circular `SetWindowRgn` instead of `TransparencyKey`.

## [1.0.0] - 2026-08-10

首个公开版本。单层圆形透视托盘工具，可直接下载使用。

First public release. A single-layer circular portal tray utility ready to download and use.

### Packaging

- 正式发布包命名为 `PierceView-v1.0.0-win-x64.zip`（不再使用 `rc` 后缀）。
- Official package name is `PierceView-v1.0.0-win-x64.zip` (no `rc` suffix).
- 新增互动介绍页 `landing/`（按住 F8 演示透视与图片拖放）。
- Added interactive product site under `landing/`.

### Documentation

- 公开仓库整理为用户向首页；采用 PolyForm Noncommercial 1.0.0；品牌收敛为最终 Logo。
- Public-facing README cleanup; PolyForm Noncommercial 1.0.0; final logo only.

### Features

- 纯系统托盘小工具：普通启动无主窗口；托盘提供启动/暂停、设置、帮助、退出。
- Tray-only UX: no main window on normal launch; Start/Pause, Settings, Help, and Exit in the tray.
- 按住 `F8` 开启圆形透视：查看、滚动或点击紧贴当前窗口后的一层普通窗口；松开即恢复。
- Hold `F8` for a circular portal: view, scroll, or click the ordinary window directly behind the host; release to restore.
- 设置仅含透视圆半径与简体中文/English；配置保存在 `%LOCALAPPDATA%\PierceView\settings.json`。
- Settings cover only portal radius and Simplified Chinese / English; stored under `%LOCALAPPDATA%\PierceView\settings.json`.
- 兼容应用之间支持 Windows 原生拖放工作流（两端格式与权限均支持时）。
- Native Windows drag-and-drop workflows when both apps and privilege levels allow it.

### Safety & scope

- 普通用户权限运行；不注入进程、不读写其他进程内存、不模拟输入、不联网、不上传遥测、不安装驱动或全局低级键鼠钩子、不设置自启动。
- Runs at standard user rights; no injection, foreign process memory access, synthesized input, network, telemetry, drivers, global low-level hooks, or startup registration.
- 启动游戏、游戏启动器或反作弊服务前，请从托盘完全退出寸镜。
- Fully exit PierceView before starting games, launchers, or anti-cheat services.

### Known limitations

- 圆形边缘可能有轻微阶梯；个别 GPU/驱动或来源窗口可能出现短暂黑帧。
- Mild circular-edge stair-stepping; occasional black frames on some GPU/driver or source windows.
- 无重定向、受保护或特殊 GPU 表面可能只有点击、没有画面。
- Some no-redirection, protected, or special GPU surfaces may accept clicks without a visual.
- 发布包尚未 Authenticode 签名；Windows 可能提示“未知发布者”。
- Builds are not Authenticode-signed; Windows may show “Unknown publisher.”

## Earlier pre-release history

以下为整理公开仓库前的预发布演进摘要，仅供参考；不构成对 1.0.0 的额外功能承诺。

Brief pre-release history before the public-repo cleanup. For reference only; it does not add feature promises beyond 1.0.0.

### 0.7.x

- 修复颜色键与场景切换相关的闪烁、黑帧与双圆问题；增强视觉探针与发布物扫描记录。
- Fixed TransparencyKey-related flicker/black frames/double-circle issues; strengthened visual probes and release scanning notes.

### 0.5.x – 0.6.x

- DWM 条带圆预览、前台恢复守卫，以及后续预览交互实验。
- DWM strip circular preview, foreground restore guards, and later interaction experiments.
