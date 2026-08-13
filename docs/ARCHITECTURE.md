# 寸镜 / PierceView 2.2 架构说明

Architecture notes for the **2.2.0 GPU Edition** single-layer feathered-circle/rounded-rectangle portal and its automatic CPU fallback. This page describes the public renderer—not internal product roadmaps.

本页说明公开发布的 **2.2.0 GPU 版本**单层羽化圆形/圆角矩形透视及其自动 CPU 回退如何工作，不包含内部产品路线。

## 总体结构 / Overview

```text
托盘 UI 线程 / Tray UI thread
  ├─ NotifyIcon：启动/暂停、设置、帮助、退出
  │  Start/Pause, Settings, Help, Exit
  ├─ SettingsForm：形状、半径/矩形尺寸、羽化、语言 / shape, size, feather, language
  └─ PortalRuntime：启动/停止工作线程 / start/stop worker

单层运行线程 / Single-layer runtime thread
  ├─ GetAsyncKeyState(F8) + 鼠标坐标 / pointer position
  ├─ WindowRegionController：在鼠标中心维护小型交互孔
  │  maintain a small input aperture around the pointer
  └─ AdaptivePortalOverlay：优先 GPU，失败时回退 CPU
       prefer GPU, fall back to CPU on failure
       ├─ GpuPortalOverlay：WGC(HWND) → D3D11 常驻纹理 → HLSL 裁剪/形状/羽化 → DirectComposition
       └─ DwmPortalOverlay：屏外 DWM → PrintWindow/BitBlt → CPU alpha → UpdateLayeredWindow
```

## 托盘生命周期 / Tray lifecycle

`Program` 只在普通启动时创建单实例互斥量和 `PierceViewApplicationContext`。应用上下文拥有托盘图标、菜单、设置窗口和运行时；退出时先停止运行时、恢复窗口，再移除托盘图标。命令行自检、窗口清单和探针模式不启动托盘。

On normal launch, `Program` creates a single-instance mutex and `PierceViewApplicationContext`. The context owns the tray icon, menu, settings form, and runtime. On exit it stops the runtime, restores windows, then removes the tray icon. CLI self-tests, window listing, and probe modes do not start the tray.

## 单层透视数据流 / Single-layer data flow

1. F8 首次按下时，`WindowRegionController` 锁定鼠标下的宿主顶层 HWND，并保存其原始 region。On first F8 press, lock the top-level host HWND under the pointer and save its original region.
2. 在宿主 region 的鼠标中心减去一个小型圆形交互孔后，`WindowFromPoint` 得到该位置当前暴露的后方一层顶层窗口。视觉区域仍由独立覆盖层完整绘制。After subtracting a small circular input aperture around the pointer, resolve the one top-level window now exposed behind the host. The separate layered overlay still draws the full visual portal.
3. 本次 F8 会话固定使用这个来源，不枚举或切换更深窗口。That source stays fixed for the hold session; deeper windows are not scanned or switched to.
4. `AdaptivePortalOverlay` 优先建立 WGC/D3D11 会话。WGC 只在来源内容更新时产生新帧，新帧会整帧拷贝到可作为 shader resource 的常驻默认纹理。鼠标移动不等待新捕获帧，而是直接用最新纹理重新计算裁剪坐标。`AdaptivePortalOverlay` prefers a WGC/D3D11 session. WGC produces a new frame only when source content changes; each frame is copied into a persistent default texture usable as a shader resource. Pointer motion does not wait for another capture frame and instead recalculates the crop against the latest texture.
5. 两缓冲 BGRA 交换链与顶层 HWND 在一次 F8 会话内固定覆盖完整 Windows 虚拟屏幕，只在首帧定位一次；鼠标移动只更新 viewport、来源采样坐标和形状中心。每轮先清透明整张后备缓冲，再让全屏三角形 pixel shader 仅在当前透视大小的 viewport 内执行一比一采样、圆形/圆角矩形 signed-distance mask 和羽化预乘 alpha，从机制上移除旧坐标 HWND 合成新帧的机会。The two-buffer BGRA swap chain and top-level HWND stay fixed across the complete Windows virtual screen for the full F8 session and are placed only on the first frame. Pointer motion updates only the viewport, source sampling coordinates, and portal center. Each update clears the full back buffer to transparent, then runs the fullscreen-triangle pixel shader only inside a portal-sized viewport for one-to-one sampling, circle/rounded-rectangle signed-distance masking, and premultiplied-alpha feathering. No moving HWND remains where a new frame could be composed at an old coordinate.
6. 交换链通过 DirectComposition 附着到顶层窗口，最大帧延迟为 1，并使用 `FlipDiscard`。GPU 窗口组合 `WS_EX_LAYERED` 与 `WS_EX_TRANSPARENT`，使跨线程/进程的系统命中查找跳过该窗；同时对 `WM_NCHITTEST` 返回 `HTTRANSPARENT`，对 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。GPU 活动时，运行线程使用高精度可等待定时器以约 2ms 目标间隔轮询，只有鼠标像素位置或 WGC 帧序号改变时才重新提交。The swap chain is attached to a top-level HWND through DirectComposition with maximum frame latency set to one and uses `FlipDiscard`. The GPU window combines `WS_EX_LAYERED` and `WS_EX_TRANSPARENT` so system hit-testing skips it across thread/process boundaries, while still returning `HTTRANSPARENT` for `WM_NCHITTEST` and `MA_NOACTIVATE` for `WM_MOUSEACTIVATE`. While GPU rendering is active, the runtime polls through a high-resolution waitable timer at an approximately 2 ms target interval and presents only when the pointer pixel or WGC frame serial changes.
7. 若 GPU 初始化、WGC 会话或帧处理失败，当次会话切换到 2.1.0 CPU 终版路径：固定虚拟屏幕显示层 + 屏外单张 DWM 缩略图 + 96 像素捕获边界 + `PrintWindow/BitBlt` + CPU 预乘 alpha + `UpdateLayeredWindow`。If GPU initialization, the WGC session, or frame handling fails, the session switches to the 2.1.0 CPU final path: a fixed virtual-screen display surface + one off-screen DWM thumbnail + 96 px capture margin + `PrintWindow/BitBlt` + CPU premultiplied alpha + `UpdateLayeredWindow`.
8. 圆形设置半径代表完全清晰的内圆，羽化带向外扩展。矩形圆角半径随较短边自动计算。物理交互孔使用 32 像素半径与 16 像素锚定阈值；视觉层先提交，再把最终坐标交给交互孔，从而保留真实点击、滚动与拖放并避免前后台交替命中。The circle setting defines the fully clear inner radius and feathering expands outward; rectangle corner radius follows the shorter side. The physical input aperture uses a 32 px radius and 16 px anchoring threshold. The visual layer presents first and hands its final coordinate to the aperture, preserving native click, wheel, and drag while avoiding alternating foreground/background hits.
9. GPU 路径由 WGC 事件驱动来源纹理更新，由运行线程驱动鼠标裁剪；CPU 回退路径仍在 F8 按住期间持续抓取和提交。The GPU path updates its source texture from WGC events and pointer cropping from the runtime thread; the CPU fallback continues to capture and present throughout the F8 hold.
10. 松开 F8 时先隐藏预览，再恢复宿主 region 和来源扩展样式。On release, hide the preview, then restore the host region and source extended styles.

## 交互与前台保护 / Input and foreground protection

寸镜不转发或合成鼠标事件。跟随鼠标中心的小型交互孔让 Windows 自己把真实鼠标事件送给下面的窗口；完整圆形或矩形只属于视觉层。GPU 视觉窗使用系统级分层透明命中跳过，宿主交互孔移动后重绘刚恢复覆盖的旧位置。透视期间，来源窗口临时增加 `WS_EX_NOACTIVATE`；若来源仍主动争夺前台，WinEvent 守卫监听前台切换与窗口重排，在来源开始越过宿主时立即把它钳制回宿主后方，再由独立后台工作以 2 ms 间隔短暂复查并在必要时恢复宿主前台。快速钳制不进入 DWM/WGC 抓帧或 GPU 合成线程。

PierceView does not synthesize mouse input. A small aperture following the pointer center lets Windows deliver real pointer events to the window beneath; the full circle or rectangle belongs only to the visual layer. The GPU visual window uses system-level layered transparent hit-test skipping, and the host redraws the old location after its aperture moves. During a session the source temporarily gains `WS_EX_NOACTIVATE`; if it still steals focus, the WinEvent guard watches both foreground changes and window reordering, clamps the source behind the host as it starts crossing the host, then performs brief 2 ms follow-up checks on independent background work and restores host foreground when needed. The fast clamp never runs on the DWM/WGC capture or GPU composition thread.

1.0 没有全局低级鼠标钩子，也没有深层命中识别或后台窗口提升算法。

Version 1.0 has no global low-level mouse hook, no deep hit-testing, and no background window promotion algorithm.

## 线程与恢复 / Threads and restore

- UI 线程：托盘和设置。UI thread: tray and settings.
- 运行 STA 线程：F8、物理 region、GPU 裁剪/提交与单层会话状态。Runtime STA thread: F8, physical region, GPU crop/present, and session state.
- WGC FreeThreaded 回调：将最新捕获帧拷贝到常驻纹理。WGC FreeThreaded callback: copy the newest captured frame into the persistent texture.
- DWM STA 线程：预览窗口和 DWM thumbnail。DWM STA thread: preview windows and thumbnails.

正常松键、暂停、设置重启运行时、托盘退出和普通进程退出都会执行同一恢复路径。强制结束进程无法保证托管清理，因此不要在 F8 按住时用任务管理器强制结束。

Release, pause, settings-driven restart, tray exit, and normal process exit share one restore path. Killing the process from Task Manager while F8 is held cannot guarantee cleanup.

## 已知技术边界 / Technical boundaries

GPU 形状由 HLSL signed-distance mask 保证，CPU 回退形状由分层位图 alpha 蒙版保证。`--gpu-probe` 验证 WGC/D3D11/DirectComposition/HLSL，`--gpu-portal-smoke-hwnd` 验证常驻纹理、高精度移动裁剪、输入命中透明、动态来源持续刷新与一次会话只定位一次显示 HWND，`--visual-smoke` 继续覆盖 CPU 管线的黑帧、形状和坐标回归。受保护内容、某些游戏/反作弊窗口、无重定向表面或驱动限制可能让 WGC 只返回空白/静态帧；这些情况不通过注入、驱动或绕过保护来解决。

GPU shapes are produced by HLSL signed-distance masks, while CPU fallback shapes use layered-bitmap alpha masks. `--gpu-probe` verifies WGC/D3D11/DirectComposition/HLSL; `--gpu-portal-smoke-hwnd` covers persistent textures, high-resolution motion cropping, transparent input hit-testing, continuous dynamic-source refresh, and exactly one display-HWND placement per session; `--visual-smoke` retains CPU black-frame, shape, and alignment regressions. Protected content, some game/anti-cheat windows, non-redirected surfaces, or driver restrictions may make WGC return blank or static frames; PierceView does not use injection, drivers, or protection bypasses to work around them.

32 像素锚定物理交互孔继续使用 `SetWindowRgn(..., redraw: true)` 并跟随视觉层实际提交的最终坐标。独立窗口测试断言滚轮只到后台、前台滚轮计数为零，同时覆盖点击、前台保持、Z-order 与样式恢复。

The 32 px anchored physical input aperture continues to use `SetWindowRgn(..., redraw: true)` and follows the visual layer's committed center. The independent-window probe asserts background-only wheel delivery with zero foreground wheel input, alongside click delivery, foreground preservation, Z-order, and style restoration.
