# Changelog

本项目采用 [Semantic Versioning](https://semver.org/)。用户可见变更记录如下。

This project follows [Semantic Versioning](https://semver.org/). User-facing changes are listed below.

## [Unreleased]

### 2.3.0-multilayer-alpha.4 (local experimental build)

- 将后台整窗闪现修复从“事件到达后恢复”升级为预防式窗口带隔离：F8 会话期间临时把宿主放入 Windows 置顶窗口带，普通后台来源即使短暂激活也无法在桌面合成器中覆盖宿主。
- Upgrade whole-window flash protection from post-event recovery to preventive window-band isolation. During an F8 session, temporarily place the host in Windows' topmost band so an ordinary background source cannot cover it even if it briefly activates.
- 透视显示层临时绑定为宿主的拥有窗口，继续保持在宿主上方且完全鼠标穿透；松开 F8、暂停、失败回退或退出时恢复显示层拥有关系与宿主原始置顶状态，原本已置顶的宿主不会被取消置顶。
- Temporarily bind the transparent portal surface as an owned window of the host so it stays above the host while remaining input-transparent. Release, pause, fallback, and exit restore both ownership and the host's original topmost state; an already-topmost host remains topmost.

### 2.3.0-multilayer-alpha.3 (local experimental build)

- 修复点击后台应用或其内容时，来源应用整体界面可能在桌面最前方闪现一两帧：同时监听 WinEvent 前台切换与窗口重排，事件到达后立即把来源窗口钳制回宿主后方，不再固定等待 8 ms 后才首次恢复。
- Fix a one-to-two-frame whole-window foreground flash after clicking a background app or its content. Watch both WinEvent foreground changes and window reordering, then immediately clamp the source behind the host instead of waiting a fixed 8 ms before the first recovery.
- 保留异步恢复兜底，并把后续复查改为 2 ms 间隔的短时窗口，以覆盖应用在一次原生点击中连续发起的激活/置前动作；不改四层 WGC 合成、固定 DirectComposition 显示层或原生输入路径。
- Keep asynchronous recovery as a fallback with a short 2 ms follow-up window for repeated activation attempts during one native click. The four-source WGC compositor, fixed DirectComposition surface, and native input path remain unchanged.

### 2.3.0-multilayer-alpha.2 (local experimental build)

- 新增可见深层窗口的受限 Z-order 提升：真实左键按下时识别 Windows 实际命中的已捕获窗口，只把它移动为宿主正后方的新 `-1`，绝不越过宿主到桌面最前面；其他后台来源保持相对顺序后移。
- Add constrained Z-order promotion for visible deep windows. On a real left-button press, resolve the captured window actually hit by Windows and move it only to the new `-1` slot directly behind the host—never above the host—while preserving the relative order of the other background sources.
- GPU 合成来源同步采用相同顺序，提升后无需重建 WGC 会话或移动 DirectComposition 显示 HWND；真实点击、滚轮和拖放继续由 Windows 原生分发。
- Reorder the GPU compositor sources in the same way without rebuilding WGC sessions or moving the DirectComposition display HWND. Windows continues to dispatch native click, wheel, and drag input.
- 四层来源在整个 F8 会话期间都获得可恢复的 `WS_EX_NOACTIVATE` 保护；松开 F8、暂停或退出时恢复每个窗口的原始样式。
- Apply restorable `WS_EX_NOACTIVATE` protection to all four captured sources for the full F8 session, restoring every original style on release, pause, or exit.

### 2.3.0-multilayer-alpha.1 (local experimental build)

- 基于 2.2.0 稳定 GPU 管线新增矩形多窗口遮挡重建：按真实屏幕位置和 Z-order 同时组合宿主后方 `-1` 至 `-4`，超过 `-4` 不识别；最终仍只使用一张固定 DirectComposition 显示层并且每轮只提交一次。
- Add rounded-rectangle multi-window occlusion reconstruction on the stable 2.2.0 GPU pipeline. It composites the real screen positions and Z-order of layers `-1` through `-4`, ignores anything deeper than `-4`, and retains one fixed DirectComposition display surface with one present per update.
- 每个来源使用独立 WGC 会话与常驻 D3D11 纹理，最外层统一应用圆角与羽化；任何多层 GPU 启动或运行失败均安全回退到 2.1.0 单层 CPU 管线。
- Give each source its own WGC session and persistent D3D11 texture, then apply one outer rounded/feathered mask. Any multi-source GPU startup or runtime failure safely falls back to the 2.1.0 single-layer CPU renderer.
- 本 alpha 只验证多层视觉与性能，深层点击重排尚未加入；真实输入仍由 Windows 当前命中的最前一层接收。
- This alpha validates multi-layer visuals and performance only. Deep-layer click reordering is not included; native input still goes to the frontmost window hit by Windows.
- 自动四层测试确认四种来源同时可见、固定显示层位置数为 1、无无效合成帧，100 次固定刷新平均 `0.22ms`、最慢 `0.77ms`（本机 2560×1440 @ 260Hz）。
- Automated four-layer testing confirms all four sources are simultaneously visible, one fixed display position, no invalid composite frames, and 100 stationary updates averaging `0.22ms` with a `0.77ms` maximum (local 2560×1440 @ 260Hz system).

## [2.2.0] - 2026-08-13

### GPU Edition / GPU 版本

- 将用户验证通过的 `2.2.0-gpu-alpha.2` 晋升为首个正式 GPU 版本；保持轻量、免安装的 Windows 托盘工具形态，并完整保留 2.1.0 CPU 稳定管线作为自动回退。
- Promote the user-validated `2.2.0-gpu-alpha.2` to the first stable GPU Edition. It remains a lightweight, portable Windows tray utility and retains the complete 2.1.0 CPU pipeline as an automatic fallback.

### Added

- 新增 `Windows.Graphics.Capture → D3D11 常驻纹理 → HLSL 形状/羽化 → DirectComposition` GPU 管线；鼠标移动直接重裁最新显存纹理，不等待新的窗口抓帧。
- Add a `Windows.Graphics.Capture → persistent D3D11 texture → HLSL shape/feathering → DirectComposition` pipeline. Pointer motion recrops the latest GPU texture without waiting for another window capture.
- 新增 GPU 能力探针、动态来源冒烟和打包态就绪握手，覆盖首帧提交、来源 HWND、透明命中与 `WS_EX_NOACTIVATE`。
- Add GPU capability probing, dynamic-source smoke coverage, and packaged-build readiness checks for first-frame presentation, source HWND, transparent hit-testing, and `WS_EX_NOACTIVATE`.

### Fixed

- GPU 顶层显示窗在一次 F8 会话内固定覆盖 Windows 虚拟屏幕，只在首帧定位一次；鼠标移动只更新透视大小的 viewport、采样坐标和形状中心，移除旧路径透视窗闪现与整体偏移来源。
- Pin the GPU top-level surface across the Windows virtual screen for the entire F8 session and place it only on the first frame. Pointer motion updates only the portal-sized viewport, sample coordinates, and shape center, removing stale-path flashes and whole-surface drift.
- GPU 覆盖窗保持系统级输入穿透，独立前台守卫继续保护当前宿主与 Z-order；真实点击、滚轮和原生拖放入口保持可用，滚轮不会同时控制前台页面。
- Keep system-level input pass-through on the GPU surface and preserve the current host and Z-order with an independent foreground guard. Native click, wheel, and drag entry remains available without scrolling the foreground at the same time.

### Verified

- 2560×1440 @ 260Hz、RTX 5070 正式 EXE 动态测试：4 秒 WGC 新帧 `212`、GPU 提交 `855`、显示层定位 `1` 次，P95 `0.13ms`、P99 `0.24ms`。
- Formal-EXE dynamic test on a 2560×1440 @ 260Hz RTX 5070 system: `212` new WGC frames, `855` GPU presents, one display placement, P95 `0.13ms`, and P99 `0.24ms` over four seconds.
- 自检 `23/23`；最终 EXE 连续 3 轮通过后台点击、后台独占滚轮、前台保持、Z-order、非激活样式与恢复；托盘生命周期通过，Microsoft Defender 为 0 检出。
- Self-tests `23/23`; the final EXE passes three consecutive runs of background click, background-only wheel, foreground preservation, Z-order, non-activating style, and restoration. Tray lifecycle passes and Microsoft Defender reports zero detections.

## [2.2.0-gpu-alpha.2] - 2026-08-13

- `2.2.0-gpu-alpha.2` 为独立交互回归增加本轮宿主绑定与可靠就绪握手：确认 GPU 首帧已提交、实际来源 HWND 正确、`WS_EX_NOACTIVATE` 已应用且点击位置命中预期目标后才发送真实鼠标输入，避免单文件启动期间读取旧日志或桌面前台未稳定造成误报。
- In `2.2.0-gpu-alpha.2`, bind each independent interaction regression to its current host and wait for a reliable readiness handshake: the first GPU frame must be presented, the captured source HWND must match, `WS_EX_NOACTIVATE` must be active, and the click point must hit the expected target before native mouse input is sent. This prevents stale logs and an unsettled desktop during single-file startup from being reported as product failures.

## [2.2.0-gpu-alpha.1] - 2026-08-12

- `2.2.0-gpu-alpha.1` 从公开的 2.1.0 CPU 终版重新建立 GPU 开发线，保留固定 CPU 显示层、单轮单次提交、32 像素交互孔与 16 像素锚定阈值作为回退基线。
- Rebase `2.2.0-gpu-alpha.1` on the public 2.1.0 CPU final build, preserving its fixed CPU display surface, one presentation per update, 32 px input aperture, and 16 px anchoring threshold as the fallback baseline.
- GPU 的 WGC → D3D11 → HLSL → DirectComposition 路径改为一次 F8 会话只定位一次虚拟屏幕顶层 HWND；鼠标移动只更新 portal-sized viewport 和着色器坐标，移除 192 像素小画布的跨界移动与旧位置闪现来源。
- Keep the GPU WGC → D3D11 → HLSL → DirectComposition path on one virtual-screen top-level HWND placement per F8 session. Pointer motion updates only the portal-sized viewport and shader coordinates, removing 192 px small-canvas relocations and their stale-position flash source.
- GPU 覆盖窗保持 `WS_EX_LAYERED | WS_EX_TRANSPARENT`、`HTTRANSPARENT` 与 `MA_NOACTIVATE`；独立前台守卫心跳不再依赖鼠标移动或下一次渲染。
- Retain `WS_EX_LAYERED | WS_EX_TRANSPARENT`, `HTTRANSPARENT`, and `MA_NOACTIVATE` on the GPU overlay, with an independent foreground-guard heartbeat that does not depend on pointer motion or another render tick.
- 260Hz 实机动态来源测试：4 秒 WGC 新帧 `211`、GPU 提交 `860`、顶层显示定位 `1` 次，P95 `0.13ms`、P99 `0.22ms`、最慢 `3.50ms`；后台点击、滚轮独占、前台与 Z-order 保护连续 3 轮通过。
- On the 260Hz test system with a dynamic source: 4 seconds produce `211` new WGC frames, `860` GPU presents, and one top-level display placement; P95 is `0.13ms`, P99 `0.22ms`, and maximum `3.50ms`. Background click, wheel exclusivity, foreground preservation, and Z-order protection pass three consecutive runs.

## [2.1.0] - 2026-08-11

### CPU Edition / CPU 版本

- 将用户验证通过的 `2.1.0-cpu-alpha.4` 固化为正式 CPU 版本；不要求独立显卡，保持轻量、免安装的 Windows 托盘工具形态。
- Promote the user-validated `2.1.0-cpu-alpha.4` build to the final CPU Edition. It requires no discrete GPU and remains a lightweight, portable Windows tray utility.

### Added

- 新增可调羽化的圆形和自动圆角矩形透视；羽化设为 0 时可使用硬边外观，静止鼠标时后台动态内容也会持续刷新。
- Add adjustable feathering for circles and automatically rounded rectangles, with a hard-edge option at zero feathering and continuous background refresh while the pointer is still.

### Fixed

- F8 会话期间固定用户可见分层显示窗，只重映射屏外 DWM 捕获来源，修复移动路径上的透视窗闪现、内部旧帧与整体抖动。
- Pin the user-visible layered surface for the full F8 session and remap only the off-screen DWM capture source, fixing portal flashes along the movement path, stale internal frames, and whole-surface jitter.
- 每轮只提交一张最终画面，并以约 120Hz 为 CPU 抓取目标，提升高刷新率屏幕上的移动稳定性。
- Present one final frame per update and target roughly 120Hz CPU capture for steadier motion on high-refresh displays.
- 使用 32 像素物理交互孔与 16 像素锚定阈值，使后台点击、滚轮和拖放保持可用，同时避免滚轮同时控制前台页面。
- Use a 32 px physical input aperture with a 16 px anchoring threshold, preserving background click, wheel, and drag behavior while preventing the foreground page from scrolling at the same time.
- 维持宿主窗口前台与原有 Z-order，并在 F8 会话结束后恢复临时窗口样式。
- Preserve the host foreground and existing Z-order, then restore temporary window styles when the F8 session ends.

### Verified

- 自检 `19/19`；真实鼠标/Hover 坐标异常 `0/384`；独立窗口回归确认后台滚轮 `Wheel=120`、前台滚轮 `Wheel=0`，后台点击、前台保持、Z-order 与样式恢复均通过。
- Self-tests `19/19`; real-pointer/hover alignment `0/384` mismatches; independent-window regression confirms background `Wheel=120`, foreground `Wheel=0`, with background click delivery, foreground preservation, Z-order, and style restoration all passing.
- Microsoft Defender 扫描为 0 检出；当前发布包未做 Authenticode 代码签名。
- Microsoft Defender reports zero detections; the release remains unsigned with Authenticode.

### Next / 下一版

- 下一版将转向 GPU 加速路径，带来更高效、更顺滑的透视体验。
- The next release will move to a GPU-accelerated path for a faster, smoother portal experience.

## [2.1.0-cpu-alpha.4] - 2026-08-11

### Fixed

- CPU 用户可见分层窗改为在一次 F8 会话内固定覆盖 Windows 虚拟屏幕；96 像素安全边界跨界时只重设屏外 DWM 捕获映射，不再移动顶层显示 HWND，从实现路径上移除旧位置窗口残影来源。
- Keep the user-visible CPU layered surface fixed across the Windows virtual screen for the full F8 session. Crossing the 96 px capture margin now remaps only the off-screen DWM source and never moves the top-level display HWND, removing the native stale-window trail source from the presentation path.
- 物理交互孔恢复为 32 像素并采用 16 像素锚定阈值；滚轮期间的微小鼠标位移保持在同一个原生命中孔内，避免前后台页面交替收到滚轮，同时减少宿主 Region 改写次数。
- Restore a 32 px physical input aperture with a 16 px anchoring threshold. Small wheel-time pointer motion remains inside one native hit-test hole, avoiding alternating foreground/background wheel routing while reducing host Region rewrites.

### Tests

- 新增独立进程滚轮独占回归：后台窗口 `Wheel=120`、前台窗口 `Wheel=0`，并与真实点击、前台保持、Z-order、`WS_EX_NOACTIVATE` 及样式恢复一起验证。
- Add an independent-process wheel exclusivity regression: background `Wheel=120`, foreground `Wheel=0`, alongside native click delivery, foreground preservation, Z-order, `WS_EX_NOACTIVATE`, and style restoration.
- 视觉冒烟验证 DWM 来源可重定位 9 次而顶层显示窗只定位 1 次；真实鼠标/Hover 坐标异常 `0/384`，四种形状无黑帧或形状异常，最慢更新约 `10.03ms`。
- Visual smoke verifies nine DWM source remaps with only one top-level display placement; real-pointer/hover alignment reports `0/384` mismatches, all four shapes have no black or invalid frames, and the slowest update is approximately `10.03ms`.

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
## [2.1.0-alpha.8] - 2026-08-11

### Fixed

- 修复 alpha.7 仍无法真实点击、滚动或拖放的问题：仅返回 `HTTRANSPARENT` 不能可靠穿过其他线程/进程的顶层窗口；GPU 覆盖窗现在组合 `WS_EX_LAYERED` 与 `WS_EX_TRANSPARENT`，使 Windows 系统命中查找直接跳过该窗，同时继续保持不激活。
- Fix alpha.7 still blocking real click, scroll, and drag input. Returning `HTTRANSPARENT` alone cannot reliably cross another top-level window's thread/process boundary; the GPU overlay now combines `WS_EX_LAYERED` and `WS_EX_TRANSPARENT` so Windows system hit-testing skips it while activation remains disabled.
- 修复跨越 192 像素安全边界时旧路径闪现完整透视窗：可见画布会先隐藏并移动，交换链使用 `FlipDiscard` 且每帧清透明，再在新位置显示已提交帧。
- Fix a full portal flashing on the old path when crossing the 192 px safety boundary. A visible canvas is hidden before relocation, the swap chain uses `FlipDiscard` and clears transparent every frame, and only the newly presented frame is shown at the destination.
- 宿主的小型交互孔移动后启用重绘，避免旧孔刚被覆盖的像素被 DWM 短暂保留。
- Redraw the host after moving its small input aperture so DWM does not briefly preserve pixels exposed by the previous aperture.

### Tests

- GPU 冒烟新增真实 `WindowFromPoint` 系统命中跳过检查，并强制执行 3 次跨安全边界重定位；2560×1440 @ 260Hz 实机 P95 0.14ms、P99 0.20ms。
- The GPU smoke now verifies real system-level `WindowFromPoint` skipping and forces three safety-boundary relocations; on the 2560×1440 @ 260Hz test system it reports P95 0.14ms and P99 0.20ms.
- CPU 回退视觉冒烟继续通过：移动/Hover 坐标异常 0/384，四种形状无异常帧或疑似过黑帧。
- The CPU fallback visual smoke still passes with 0/384 motion/hover alignment mismatches and no malformed or suspected-black frames across all four shape modes.

## [2.1.0-alpha.7] - 2026-08-11

### Fixed

- 修复 GPU 覆盖窗拦截鼠标输入：窗口对 `WM_NCHITTEST` 返回 `HTTRANSPARENT`，并以 `MA_NOACTIVATE` 拒绝激活，使宿主的小型交互孔重新获得真实点击、滚动和拖放入口。
- Fix the GPU overlay intercepting pointer input by returning `HTTRANSPARENT` for `WM_NCHITTEST` and refusing activation with `MA_NOACTIVATE`, restoring real click, scroll, and drag entry through the host's small input aperture.
- 修复移动鼠标时透视内容轻微整体偏移：GPU 交换链改为带 192 像素安全余量的稳定画布，安全范围内只在同一次 shader/Present 提交中移动透视中心和采样坐标，不再逐像素移动原生 HWND。
- Fix slight whole-image drift during pointer motion by using a stable GPU swap-chain canvas with a 192 px safety margin. Within that range, the portal center and sampling coordinates move in the same shader/Present submission without pixel-by-pixel native HWND moves.

### Tests

- 自检新增 GPU 稳定画布边界测试；GPU 透视冒烟新增 `HTTRANSPARENT`/`MA_NOACTIVATE` 输入探针和画布重定位计数断言。
- Add a GPU stable-canvas boundary self-test plus `HTTRANSPARENT`/`MA_NOACTIVATE` input probes and a canvas-relocation assertion to the GPU portal smoke test.
- 2560×1440 @ 260Hz、RTX 5070 实机：4 秒调度 1622 次、GPU 提交 717 帧、画布仅首帧重定位 1 次；更新耗时 P95 0.13ms、P99 0.20ms。
- On a 2560×1440 @ 260Hz RTX 5070 system, the 4-second run schedules 1,622 updates, presents 717 GPU frames, and relocates the canvas only once on the first frame; update latency is P95 0.13ms and P99 0.20ms.

## [2.1.0-alpha.6] - 2026-08-11

### Added

- 新增实验性 GPU 单层管线：按 HWND 使用 Windows.Graphics.Capture，将最新完整来源帧常驻在 D3D11 纹理中，再由 HLSL 完成鼠标位置裁剪、圆形/圆角矩形与羽化，通过 DirectComposition 交换链提交。
- Add an experimental single-layer GPU pipeline: capture an HWND with Windows.Graphics.Capture, keep the latest complete source frame in a persistent D3D11 texture, apply pointer cropping plus circle/rounded-rectangle feathering in HLSL, and present through a DirectComposition swap chain.
- 新增 `--gpu-probe`、`--gpu-smoke-hwnd` 与 `--gpu-portal-smoke-hwnd` 诊断入口，分别验证 GPU/WGC 前置条件、最小帧闭环和常驻纹理移动裁剪。
- Add `--gpu-probe`, `--gpu-smoke-hwnd`, and `--gpu-portal-smoke-hwnd` diagnostics for GPU/WGC prerequisites, the minimum frame loop, and persistent-texture motion cropping.

### Changed

- 生产运行时优先 GPU；初始化不可用或会话中捕获失败时，自动回退到 alpha.5 的 DWM thumbnail → PrintWindow/BitBlt → CPU alpha → UpdateLayeredWindow 管线。
- Prefer the GPU renderer at runtime and automatically fall back to alpha.5's DWM thumbnail → PrintWindow/BitBlt → CPU alpha → UpdateLayeredWindow pipeline when initialization or a capture session fails.
- GPU 活动期使用 Windows 高精度可等待定时器，避免 `Thread.Sleep(2)` 被约 15.6ms 普通定时粒度限制在约 60–64Hz。
- Use a Windows high-resolution waitable timer while the GPU backend is active so `Thread.Sleep(2)` is not quantized to the ordinary ~15.6 ms timer interval and capped near 60–64 Hz.

### Tests

- 2560×1440 @ 260Hz、RTX 5070 实机：GPU 透视冒烟 4 秒调度 1549 次，静态 WGC 帧 1 张可独立生成 714 次不同位置提交；更新耗时 P95 0.81ms、P99 1.14ms、最慢 3.40ms，低于 260Hz 的 3.85ms 单刷新周期。
- On a 2560×1440 @ 260Hz RTX 5070 system, the 4-second GPU portal smoke schedules 1,549 updates and turns one static WGC source frame into 714 distinct-position presents; update latency is P95 0.81ms, P99 1.14ms, and 3.40ms maximum, below the 3.85ms refresh interval at 260Hz.
- 原 DWM/CPU `--visual-smoke --radius 120` 继续通过：圆形/矩形形状异常和疑似过黑均为 0，移动与 Hover 坐标异常采样为 0/384。
- The original DWM/CPU `--visual-smoke --radius 120` still passes with zero circle/rectangle shape or suspected-black failures and 0/384 motion/hover alignment mismatches.
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
