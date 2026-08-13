# 寸镜 / PierceView 2.3 架构说明

Architecture notes for the public **2.3.0 GPU Edition** multi-window portal and its automatic CPU fallback. This page describes the shipped renderer, not internal roadmaps.

本页说明公开发布的 **2.3.0 GPU 版本**多窗口透视、真实输入层级同步及 CPU 自动回退，不包含内部产品路线。

## 总体结构 / Overview

```text
托盘 UI 线程 / Tray UI thread
  ├─ NotifyIcon：启动/暂停、设置、帮助、退出
  ├─ SettingsForm：尺寸、羽化、语言 / size, feather, language
  └─ PortalRuntime：F8 会话 / F8 session
       ├─ WindowRegionController：鼠标中心真实交互孔 / native input aperture
       ├─ MultilayerWindowResolver：宿主后方最多 -1…-4 / at most four sources
       └─ AdaptivePortalOverlay
            ├─ GPU：WGC × 1…4 → D3D11 纹理 → HLSL 遮挡/羽化 → DirectComposition
            └─ CPU 回退：单张 DWM → PrintWindow/BitBlt → UpdateLayeredWindow
```

## 托盘生命周期 / Tray lifecycle

普通启动创建单实例互斥量和 `PierceViewApplicationContext`。应用上下文拥有托盘图标、菜单、设置窗口和运行时；退出时先停止运行时、恢复窗口，再移除托盘图标。命令行自检和探针模式不启动托盘。

Normal launch creates a single-instance mutex and `PierceViewApplicationContext`. The application context owns the tray icon, menu, settings window, and runtime. Exit stops the runtime, restores windows, and then removes the tray icon. CLI self-tests and probes do not start the tray.

## 多层透视数据流 / Multi-window data flow

1. F8 首次按下时，`WindowRegionController` 锁定鼠标下的宿主顶层 HWND，保存其原始 region，并建立一个跟随鼠标中心的小型真实交互孔。On first F8 press, the controller locks the host top-level HWND, saves its original region, and creates a small native input aperture around the pointer.
2. `MultilayerWindowResolver` 从宿主后方真实 Z-order 中选择与矩形透视框相交的最多四个普通顶层窗口；超过 `-4` 的候选明确忽略。The resolver selects at most four ordinary top-level windows intersecting the rectangular portal behind the host; candidates deeper than `-4` are deliberately ignored.
3. 每个来源建立独立 WGC 会话和 D3D11 常驻纹理。新帧只更新对应纹理；鼠标移动直接用最新纹理重新计算采样，不等待来源产生新画面。Each source receives an independent WGC session and persistent D3D11 texture. A new frame updates only its source texture, while pointer motion recrops the latest textures without waiting for another captured frame.
4. HLSL 根据每个窗口的真实屏幕边界和来源顺序重建遮挡：浅层覆盖处显示浅层，未覆盖处继续查找更深层。普通顶层来源边界内按不透明表面处理，避免 WGC 暂态 alpha 泄漏宿主画面。HLSL reconstructs occlusion from real screen bounds and source order. Shallower sources win where they cover, and uncovered pixels continue to deeper sources. Ordinary top-level windows are treated as opaque within their bounds to prevent transient WGC alpha from leaking the host.
5. 所有来源最终共用一张覆盖虚拟屏幕的 DirectComposition 显示 HWND 与双缓冲交换链。一次 F8 会话只定位一次显示层；每轮清空、绘制圆角羽化矩形并整帧提交一次，同时把 DirectComposition 内容裁剪到实际透视框。All sources share one virtual-screen DirectComposition display HWND and double-buffered swap chain. It is placed once per F8 session; each update clears, draws the rounded feathered rectangle, and presents once, with an additional DirectComposition clip at the portal bounds.
6. 单路抓帧异常或一次动态补位竞态不会立即拆毁视觉层。程序保留最后一张完整有效合成并在后续循环重试，减少宿主内容或中间态闪现。A single capture error or one reconciliation race does not tear down the display. PierceView keeps the last complete composition and retries later, reducing host-content and intermediate-state flashes.
7. GPU 初始化或完整会话无法继续时，`AdaptivePortalOverlay` 回退到 2.1.0 单层 CPU 终版：固定显示层、屏外单张 DWM thumbnail、`PrintWindow/BitBlt`、CPU 预乘 alpha 和 `UpdateLayeredWindow`。If GPU initialization or the full session cannot continue, the adaptive overlay falls back to the final 2.1.0 single-layer CPU renderer: fixed display surface, one off-screen DWM thumbnail, `PrintWindow/BitBlt`, CPU premultiplied alpha, and `UpdateLayeredWindow`.
8. 松开 F8、暂停、设置重启运行时或正常退出时，先隐藏透视，再恢复宿主 region、来源扩展样式、宿主置顶状态和显示层拥有关系。Release, pause, settings restart, or normal exit hides the portal before restoring the host region, source styles, host topmost state, and display ownership.

## 深层提升与输入同步 / Promotion and input synchronization

寸镜不合成或转发鼠标事件。物理交互孔让 Windows 把真实点击、滚轮和拖放送给孔下方窗口。按下左键时，运行时识别透视框中实际可见且已捕获的来源；若它不是当前 `-1`，则只把它无激活地移动到宿主正后方，并保持其他后台来源的相对顺序。GPU 合成顺序只有在物理 Z-order 调整成功后才同步。

PierceView does not synthesize or relay mouse input. The physical aperture lets Windows deliver native click, wheel, and drag events to the window beneath it. On left-button press, the runtime identifies the captured source actually visible at that point. If it is not the current `-1`, PierceView moves it without activation only to the slot directly behind the host, preserving the relative order of other sources. GPU composition order changes only after the physical Z-order update succeeds.

深层交换期间会短暂保留最后一张完整画面，优先等待被提升来源产生交换后的 WGC 帧；最长等待 8 ms，避免静态窗口停住。前台与物理层级守卫持续核对选中来源仍是真实 `-1`，因此后续滚轮、点击和拖放不会继续落到旧来源。

During promotion, PierceView briefly holds the last complete image and prefers to wait for a post-promotion WGC frame from the selected source, capped at 8 ms so a static window cannot stall. Foreground and physical-order guards keep validating that the selected source remains the real `-1`, so subsequent wheel, click, and drag input does not fall back to the old source.

## 动态来源协调 / Dynamic source reconciliation

F8 按住期间每 75 ms 检查已捕获来源。窗口真正销毁或最小化时立即移除；普通可见性或根窗口暂态需要连续三次无效才处理。仍有效的 WGC 会话和纹理继续复用，只为新补位来源创建捕获；新来源首帧到达前保留最后一张完整合成。

Captured sources are checked every 75 ms while F8 is held. A destroyed or minimized window is removed immediately, while transient visibility/root-window states require three consecutive invalid probes. Valid WGC sessions and textures are reused, and only a replacement receives a new capture. The previous complete composition remains until the replacement publishes its first frame.

来源名单变化会同步到 `WS_EX_NOACTIVATE`、前台守卫与真实输入顺序。协调器只替换失效来源，不会因普通后台 Z-order 变化覆盖用户已经建立的有效新 `-1`。

Source-list changes are synchronized with `WS_EX_NOACTIVATE`, foreground protection, and physical input order. The reconciler replaces only invalid sources and does not overwrite a valid user-selected `-1` after ordinary background Z-order changes.

## 前台与显示层保护 / Foreground and display protection

F8 会话期间，所有捕获来源临时获得可恢复的 `WS_EX_NOACTIVATE`；宿主临时进入置顶窗口带，唯一的透明显示层作为宿主拥有窗口保持在其上方。WinEvent 守卫按捕获 HWND 及其子窗口/拥有窗口家族匹配，不按整个进程匹配，因此文件资源管理器不会与同属 `explorer.exe` 的任务栏、桌面混淆。

During F8, every captured source receives a restorable `WS_EX_NOACTIVATE` style. The host temporarily enters the topmost band, and the single transparent display surface remains above it as an owned window. The WinEvent guard matches captured HWND families—children and owned windows—not entire processes, so File Explorer is not confused with the taskbar or desktop that also run inside `explorer.exe`.

显示层在正常更新、首次显示和 16 ms 心跳中核对自己是否仍位于宿主上方；只有失序时才以不激活方式插回宿主正上方。会话结束恢复宿主原始置顶状态与显示层拥有关系。

Normal updates, first display, and a 16 ms heartbeat verify that the display surface remains above the host. It is reinserted without activation only when misplaced. Session teardown restores the host's original topmost state and display ownership.

## 线程与恢复 / Threads and recovery

- UI 线程：托盘和设置。UI thread: tray and settings.
- 运行 STA 线程：F8、物理 region、GPU 裁剪/提交、多层会话状态与提升。Runtime STA thread: F8, physical region, GPU crop/present, multi-window state, and promotion.
- WGC FreeThreaded 回调：独立更新各来源常驻纹理。WGC FreeThreaded callbacks: independently update persistent source textures.
- 守卫后台工作：短间隔复查前台、来源物理顺序和显示层顺序。Guard work: short-interval checks of foreground, physical source order, and display order.
- DWM STA 线程：CPU 回退预览窗口与 DWM thumbnail。DWM STA thread: CPU-fallback preview and DWM thumbnail.

正常松键、暂停、设置重启、托盘退出和普通进程退出共享同一恢复路径。强制结束进程无法保证托管清理，因此不要在 F8 按住时用任务管理器强制结束。

Release, pause, settings restart, tray exit, and normal process exit share one recovery path. Killing the process from Task Manager while F8 is held cannot guarantee managed cleanup.

## 验证入口与边界 / Verification and boundaries

`--self-test` 覆盖几何、来源协调、层级决策和窗口家族匹配；`--gpu-probe` 验证 WGC/D3D11/DirectComposition/HLSL；`--gpu-portal-smoke-hwnd` 验证动态来源、固定显示层和性能；`--visual-smoke` 覆盖 CPU 回退。PowerShell 探针进一步覆盖四层遮挡、深层提升与输入同步、非激活点击/滚轮，以及关闭来源后的动态补位。

`--self-test` covers geometry, source reconciliation, order decisions, and captured-window-family matching. `--gpu-probe` verifies WGC/D3D11/DirectComposition/HLSL; `--gpu-portal-smoke-hwnd` covers dynamic capture, the fixed display surface, and performance; `--visual-smoke` covers the CPU fallback. PowerShell probes additionally cover four-layer occlusion, deep-window promotion and input synchronization, non-activating click/wheel behavior, and dynamic backfill after a source closes.

受保护内容、游戏/反作弊、无重定向表面和独立 D3D 子表面可能只提供空白或静态帧。寸镜不使用注入、驱动或保护绕过来处理这些边界。

Protected content, games/anti-cheat, no-redirection surfaces, and independent D3D child surfaces may provide only blank or static frames. PierceView does not use injection, drivers, or protection bypasses to work around these boundaries.
