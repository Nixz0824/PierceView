# 寸镜 / PierceView 2.1 架构说明

Architecture notes for the **2.1 alpha** single-layer feathered-circle/rounded-rectangle portal. This page describes the current local development build—not internal product roadmaps.

本页说明当前本地开发的 **2.1 alpha** 单层羽化圆形/圆角矩形透视如何工作，不包含内部产品路线。

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
  └─ DwmPortalOverlay：复制固定的后方一层来源
       copy the fixed one-layer-behind source
       ├─ 屏外 DWM 捕获面（单张缩略图）→ 预计算形状 alpha 蒙版 → UpdateLayeredWindow 整帧提交
       │  off-screen DWM capture → shape alpha mask → layered present
       ├─ NonActivatingWindowGuard：临时 WS_EX_NOACTIVATE
       └─ ForegroundZOrderGuard：WinEvent 恢复宿主前台
```

## 托盘生命周期 / Tray lifecycle

`Program` 只在普通启动时创建单实例互斥量和 `PierceViewApplicationContext`。应用上下文拥有托盘图标、菜单、设置窗口和运行时；退出时先停止运行时、恢复窗口，再移除托盘图标。命令行自检、窗口清单和探针模式不启动托盘。

On normal launch, `Program` creates a single-instance mutex and `PierceViewApplicationContext`. The context owns the tray icon, menu, settings form, and runtime. On exit it stops the runtime, restores windows, then removes the tray icon. CLI self-tests, window listing, and probe modes do not start the tray.

## 单层透视数据流 / Single-layer data flow

1. F8 首次按下时，`WindowRegionController` 锁定鼠标下的宿主顶层 HWND，并保存其原始 region。On first F8 press, lock the top-level host HWND under the pointer and save its original region.
2. 在宿主 region 的鼠标中心减去一个小型圆形交互孔后，`WindowFromPoint` 得到该位置当前暴露的后方一层顶层窗口。视觉区域仍由独立覆盖层完整绘制。After subtracting a small circular input aperture around the pointer, resolve the one top-level window now exposed behind the host. The separate layered overlay still draws the full visual portal.
3. 本次 F8 会话固定使用这个来源，不枚举或切换更深窗口。That source stays fixed for the hold session; deeper windows are not scanned or switched to.
4. `DwmPortalOverlay` 用 DWM thumbnail 把来源窗口对应区域画到所选形状的预览窗。DWM thumbnails paint the matching source region into the selected portal shape.
5. 预览在屏外窗维护单张比透视形状四周各大 96 像素的 DWM 缩略图，并把同尺寸分层显示窗作为稳定画布。边界内移动不会改变 DWM 来源或原生显示 HWND 的位置，只把预计算 alpha 形状移动到复用的原始抓帧中并立即提交；越过边界才同步刷新来源映射和画布位置。`PrintWindow` 完成后再次读取鼠标坐标，在同一捕获缓冲内延迟锁定。合成结果直接写入会话复用的 DIB，省去最终尺寸位图克隆和额外像素复制。Keep one off-screen DWM thumbnail and same-sized layered display canvas with a 96 px margin around the portal. Motion inside the margin changes neither the DWM source nor the native display HWND position; it moves the precomputed alpha shape over the reused raw frame and submits immediately. Only crossing the margin refreshes both source mapping and canvas position. After `PrintWindow`, read the pointer again and late-latch inside the same capture buffer. Composition writes directly into the session-reused DIB, avoiding the final-sized bitmap clone and an extra pixel copy.
6. 圆形设置半径代表完全清晰的内圆，羽化带向外扩展。矩形圆角半径随较短边自动计算，羽化沿圆角轮廓向内达到完全不透明。物理交互孔固定限制在鼠标中心附近，避免大尺寸 Region 与视觉层分两次移动时出现第二个圆/矩形，同时保留真实点击、滚动与拖放。The circle setting defines the fully clear inner radius and feathering expands outward. Rectangle corner radius follows the shorter side, with feathering becoming fully opaque inward along the rounded contour. The physical input aperture stays small and centered on the pointer, preventing a second circle/rectangle when the Region and visual layer move in separate transactions while preserving real click, scroll, and drag input.
7. F8 按住期间，运行线程使用高精度可等待定时器以约 4ms 目标间隔轮询。鼠标位置变化可立即复用最近的原始抓帧重绘，后台内容捕获限制在约 60Hz；这样把“形状跟随”与较慢的 `PrintWindow` 内容刷新解耦。While F8 is held, the runtime uses a high-resolution waitable timer with an approximately 4ms target interval. Pointer changes can immediately redraw from the latest raw frame, while background content capture is limited to roughly 60Hz, decoupling shape tracking from the slower `PrintWindow` refresh.
8. 松开 F8 时先隐藏预览，再恢复宿主 region 和来源扩展样式。On release, hide the preview, then restore the host region and source extended styles.

## 交互与前台保护 / Input and foreground protection

寸镜不转发或合成鼠标事件。跟随鼠标中心的小型交互孔让 Windows 自己把真实鼠标事件送给下面的窗口；完整圆形或矩形只属于视觉层。透视期间，来源窗口临时增加 `WS_EX_NOACTIVATE`；若来源仍主动争夺前台，WinEvent 守卫在独立后台工作中把它放回宿主之后并恢复宿主前台，避免同步阻塞 DWM 抓帧。

PierceView does not synthesize mouse input. A small aperture following the pointer center lets Windows deliver real pointer events to the window beneath; the full circle or rectangle belongs only to the visual layer. During a session the source temporarily gains `WS_EX_NOACTIVATE`; if it still steals focus, a WinEvent guard restores host order and foreground on independent background work so DWM frame capture is not synchronously blocked.

1.0 没有全局低级鼠标钩子，也没有深层命中识别或后台窗口提升算法。

Version 1.0 has no global low-level mouse hook, no deep hit-testing, and no background window promotion algorithm.

## 线程与恢复 / Threads and restore

- UI 线程：托盘和设置。UI thread: tray and settings.
- 运行线程：F8、物理 region、单层会话状态。Runtime thread: F8, physical region, session state.
- DWM STA 线程：预览窗口和 DWM thumbnail。DWM STA thread: preview windows and thumbnails.

正常松键、暂停、设置重启运行时、托盘退出和普通进程退出都会执行同一恢复路径。强制结束进程无法保证托管清理，因此不要在 F8 按住时用任务管理器强制结束。

Release, pause, settings-driven restart, tray exit, and normal process exit share one restore path. Killing the process from Task Manager while F8 is held cannot guarantee cleanup.

## 已知技术边界 / Technical boundaries

圆形和圆角矩形都由分层位图 alpha 蒙版保证。小型物理交互孔使用 `SetWindowRgn(..., redraw: true)`，确保移动后重新覆盖的旧位置立即进入 Windows 重绘流程。`--visual-smoke` 会通过生产使用的同一 region 更新路径，覆盖静止刷新、真实鼠标移动、后台 Hover 重绘、内容坐标对齐、稳定画布重定位次数、缓存即时提交、旧位置恢复、提交前鼠标延迟锁定、换帧预算、羽化 alpha、黑帧和形状回归。个别来源窗口若 DWM 缩略图/PrintWindow 抓取失败，预览可能不可用。更激进的合成方式不在本页承诺范围内。

Both circles and rounded rectangles are guaranteed by layered bitmap alpha. The small physical input aperture uses `SetWindowRgn(..., redraw: true)` so the old position re-covered after motion immediately enters Windows' repaint path. Through the same region-update path used in production, `--visual-smoke` covers stationary refresh, real-pointer motion, background hover repaint, content alignment, stable-canvas relocation counts, immediate cached presentation, old-position restoration, pre-present pointer late-latching, frame-budget metrics, feather alpha, black frames, and shape regressions. Some source windows may fail DWM thumbnail/PrintWindow capture. More aggressive compositing approaches are outside this document’s promises.
