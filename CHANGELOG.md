# Changelog

本项目采用 [Semantic Versioning](https://semver.org/)。用户可见变更记录如下。

This project follows [Semantic Versioning](https://semver.org/). User-facing changes are listed below.

## [Unreleased]

### Documentation

- （暂无）

## [2.1.0-cpu-alpha.3] - 2026-08-11

### Fixed

- 修复 CPU late-latch 视觉坐标与物理交互孔仍可能相差一个抓帧周期的问题：视觉层先完成提交，再把实际提交的最终坐标同步给交互孔，避免旧孔在当前位置附近短暂露出。
- Fix the CPU late-latch visual center and physical input aperture drifting by one capture interval. The visual layer now commits first and hands its actual final center to the aperture, preventing the old hole from briefly appearing near the current position.
- 每次运行循环只提交一张分层画面；需要新抓帧时不再先提交缓存旧帧再提交新帧，减少高刷新率屏幕上透视窗内部一两帧陈旧画面的闪现。
- Submit only one layered frame per runtime tick. When a fresh capture is due, do not present a cached stale frame immediately before the new one, reducing one- or two-frame stale flashes inside the portal on high-refresh displays.

### Performance

- CPU 内容抓取目标从约 60Hz 提高到约 120Hz；4ms 形状跟随节奏不变。物理交互孔半径从 32 像素缩至 4 像素，鼠标中心仍保留原生点击、滚动与拖放命中，同时显著缩小独立物理孔可能产生的可见面积。
- Raise the CPU content-capture target from roughly 60Hz to roughly 120Hz while retaining the 4ms shape-follow cadence. Reduce the physical input-aperture radius from 32 px to 4 px so native click, scroll, and drag hit-testing remains at the pointer center while the independently visible physical area becomes much smaller.

### Tests

- 视觉冒烟新增“最终提交坐标等于 late-latch 鼠标坐标”及“单次更新只发生一次 `UpdateLayeredWindow` 提交”断言，并把旧位置恢复采样改到物理孔圆心。
- Add visual-smoke assertions that the committed center equals the late-latched pointer and that one update performs exactly one `UpdateLayeredWindow` presentation; move stale-position sampling to the physical aperture center.

## [2.1.0-cpu-alpha.2] - 2026-08-11

### Fixed

- 修复 CPU 稳定画布版本移动时仍可能在旧路径闪现小型透视区域：物理交互孔每次移动后改为请求 Windows 立即重绘重新覆盖的位置，不改动已经稳定的 CPU 抓帧、缓存跟随和 alpha 合成路径。
- Fix the small portal remnant that could still flash along the old movement path in the CPU stable-canvas build. Each physical input-aperture move now requests an immediate repaint of the re-covered area without changing the stabilized CPU capture, cached-follow, or alpha-composition path.

### Tests

- 视觉冒烟的交互孔辅助逻辑改为直接复用生产 region 更新路径，并让稳定画布旧位置测试同时移动视觉 alpha 与物理交互孔，避免测试使用重绘而正式代码未使用的漏检。
- Make the visual-smoke aperture helper reuse the production region-update path, and move both the visual alpha and physical input aperture in the stable-canvas stale-position test, preventing a test/production redraw mismatch from escaping again.

## [2.1.0-cpu-alpha.1] - 2026-08-11

### Fixed

- CPU 透视改用比形状四周各大 96 像素的稳定分层画布：安全范围内只移动画布内部的 alpha 形状，不再逐像素移动原生显示 HWND；跨界时才同步更新 DWM 来源与显示位置，减少移动时的整体抖动、重影和旧路径残留。
- Move the CPU portal to a stable layered canvas with a 96 px margin around the shape. Motion inside the margin moves only the alpha shape inside the canvas rather than the native display HWND; DWM source and display position update together only after crossing the margin, reducing whole-frame jitter, ghosting, and stale-path remnants.
- DWM 来源跨越安全边界时执行一次同步，避免新画布坐标配上旧缩略图内容；边界内不执行全局 DWM flush。
- Synchronize DWM once when the source crosses the safe-canvas boundary so new canvas coordinates cannot be paired with old thumbnail content; no global DWM flush occurs for motion inside the margin.

### Performance

- 鼠标位置变化先复用最近的原始抓帧即时提交，后台内容抓取维持约 60Hz；F8 会话使用约 4ms 的高精度等待节奏，使形状跟随不再完全受 `PrintWindow` 延迟限制。
- Reuse the latest raw frame for immediate pointer-position submissions while refreshing background content at roughly 60Hz. A roughly 4ms high-resolution wait cadence keeps shape tracking from being fully gated by `PrintWindow` latency.
- alpha 合成直接写入会话复用的 DIB，移除最终尺寸位图克隆与一次额外像素复制。
- Write alpha composition directly into the session-reused DIB, removing the final-sized bitmap clone and one extra pixel copy.

### Tests

- 视觉冒烟新增稳定画布重定位、缓存即时提交和旧位置清除断言；真实鼠标/Hover 坐标采样为 `0/384` 异常，四种形状无黑帧或形状异常，最终换帧平均约 `5.17–5.28ms`、最慢 `9.12ms`。
- Visual smoke now asserts stable-canvas relocation, immediate cached presentation, and stale-position cleanup. Real-pointer/hover alignment reports `0/384` mismatches; all four shapes have no black or invalid frames, with final average updates around `5.17–5.28ms` and a `9.12ms` maximum.

## [2.1.0-alpha.5] - 2026-08-11

### Fixed

- 修复移动透视时偶发“停一帧再跳动”的重影：`PrintWindow` 抓取完成后重新读取最新鼠标位置，并在已捕获的 64 像素安全边界内重新裁剪后再提交，避免使用抓帧前的旧坐标。
- Fix intermittent hold-then-jump ghosting during portal motion by reading the latest pointer position after `PrintWindow`, then recropping inside the captured 64 px safety margin before presentation instead of using the pre-capture position.
- 显示层只在首帧执行一次顶层/可见性 `SetWindowPos`，后续位置由同一次 `UpdateLayeredWindow` 整帧提交，减少重复窗口位置事务。
- Perform the topmost/visibility `SetWindowPos` transaction only on the first frame; subsequent positions are submitted atomically with the frame through `UpdateLayeredWindow`.
- 修复覆盖层释放时过早设置 disposed 状态、导致消息线程未能正常关闭的问题，避免暂停、改设置或测试后残留后台线程。
- Fix overlay disposal setting its disposed state too early to close the message loop, preventing background overlay threads from lingering after pause, settings changes, or tests.

### Performance

- 在一次 F8 会话内复用屏幕 DC、内存 DC、DIB section 与 alpha 像素缓冲，并使用原生内存复制替代逐行临时数组，移除主要的逐帧 GDI 分配。
- Reuse the screen DC, memory DC, DIB section, and alpha pixel buffer for the F8 session, and replace temporary row arrays with native memory copies to remove major per-frame GDI allocations.

### Tests

- 视觉冒烟新增提交前鼠标位置延迟锁定与换帧预算统计。最终回归中圆形平均 8.30–10.06ms、矩形平均 12.34–12.59ms；192 帧中 191 帧不超过 16.67ms，唯一超出帧为 16.69ms。延迟锁定边缘采样 0/5 异常，形状异常与过黑帧均为 0。
- Visual smoke now covers pre-present pointer late-latching and frame-budget metrics. In the final run, circles average 8.30–10.06ms and rectangles 12.34–12.59ms; 191 of 192 frames stay within 16.67ms, with the single outlier at 16.69ms. Late-latch edge samples report 0/5 mismatches with no shape or black-frame failures.

## [2.1.0-alpha.4] - 2026-08-11

### Fixed

- 屏外单张 DWM 缩略图增加 64 像素安全边界；边界内移动只改变 CPU 裁剪坐标，不再逐像素调用 `DwmUpdateThumbnailProperties`，使内容坐标与 `UpdateLayeredWindow` 覆盖层位置保持同帧，降低移动时的重影与抖动。
- Add a 64 px safety margin to the single off-screen DWM thumbnail. Motion inside the margin changes only the CPU crop instead of calling `DwmUpdateThumbnailProperties` per pixel, keeping content coordinates and the `UpdateLayeredWindow` overlay position in the same frame to reduce motion ghosting and jitter.
- 捕获缓冲位图在 F8 会话内复用，仅复制最终圆形/矩形尺寸的区域进入 alpha 合成，避免每帧重复分配大尺寸捕获缓冲。
- Reuse the capture buffer for the F8 session and copy only the final circle/rectangle area into alpha composition, avoiding a new oversized capture allocation on every frame.

### Tests

- 高对比 128 像素往返测试为 0/64 错位帧且只重定位 1 次 DWM 来源；真实鼠标、后台 Hover 重绘与 240 像素往返测试为 0/384 错位采样、9 次来源重定位。
- The high-contrast 128 px sweep reports 0/64 misaligned frames with one DWM source reposition; the real-pointer, background-hover, 240 px sweep reports 0/384 misaligned samples with nine source repositions.

## [2.1.0-alpha.3] - 2026-08-11

### Added

- 圆形新增与矩形共用的 0–80 像素边缘羽化。圆形半径继续表示完全清晰的内圆，羽化带只向外扩展；默认 `180 + 24` 像素视觉外半径不会缩小原清晰范围。
- Circles now share the 0–80 px edge-feather setting with rectangles. The circle radius remains the fully clear inner area and feathering expands only outward; the default visual outer radius is `180 + 24` px without shrinking the original clear view.

### Fixed

- 将宿主窗口的物理穿透 Region 缩为鼠标中心附近的 32 像素交互孔，完整圆形/矩形继续由分层视觉窗绘制；降低两个系统窗口分步移动时旧位置短暂露出、形成双形状或轻微抖动的概率。
- Reduce the host's physical pass-through Region to a 32 px input aperture around the pointer while the layered visual still draws the full circle/rectangle. This minimizes stale full-size shapes and slight jitter when the two native windows move in separate transactions.
- 每帧先移动交互孔，再提交视觉帧，使真实点击、滚动和拖放命中更贴近当前鼠标位置。
- Move the input aperture before presenting each visual frame so real click, scroll, and drag hit-testing stays closer to the current pointer position.

### Tests

- 视觉冒烟新增真实系统鼠标移动、后台文字/按钮/图像 Hover 重绘与六点内容坐标对齐；继续覆盖静止刷新、四种形状、羽化 alpha、黑帧和形状异常。
- Visual smoke now moves the real system pointer, repaints background text/button/image hover states, and checks six-point content alignment while retaining stationary-refresh, four-shape, feather-alpha, black-frame, and shape-regression coverage.

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
