# 寸镜 / PierceView 1.0 架构说明

Architecture notes for the public **1.0** single-layer circular portal. This page describes how the shipping build works—not internal product roadmaps.

本页说明当前公开发布的 **1.0** 单层圆形透视如何工作，不包含内部产品路线。

## 总体结构 / Overview

```text
托盘 UI 线程 / Tray UI thread
  ├─ NotifyIcon：启动/暂停、设置、帮助、退出
  │  Start/Pause, Settings, Help, Exit
  ├─ SettingsForm：半径、语言 / radius, language
  └─ PortalRuntime：启动/停止工作线程 / start/stop worker

单层运行线程 / Single-layer runtime thread
  ├─ GetAsyncKeyState(F8) + 鼠标坐标 / pointer position
  ├─ WindowRegionController：在宿主 region 中减去圆
  │  subtract a circle from the host window region
  └─ DwmPortalOverlay：复制固定的后方一层来源
       copy the fixed one-layer-behind source
       ├─ 屏外 DWM 捕获面（单张缩略图）→ 圆 alpha 蒙版 → UpdateLayeredWindow 整帧提交
       │  off-screen DWM capture → circular alpha mask → layered present
       ├─ NonActivatingWindowGuard：临时 WS_EX_NOACTIVATE
       └─ ForegroundZOrderGuard：WinEvent 恢复宿主前台
```

## 托盘生命周期 / Tray lifecycle

`Program` 只在普通启动时创建单实例互斥量和 `PierceViewApplicationContext`。应用上下文拥有托盘图标、菜单、设置窗口和运行时；退出时先停止运行时、恢复窗口，再移除托盘图标。命令行自检、窗口清单和探针模式不启动托盘。

On normal launch, `Program` creates a single-instance mutex and `PierceViewApplicationContext`. The context owns the tray icon, menu, settings form, and runtime. On exit it stops the runtime, restores windows, then removes the tray icon. CLI self-tests, window listing, and probe modes do not start the tray.

## 单层透视数据流 / Single-layer data flow

1. F8 首次按下时，`WindowRegionController` 锁定鼠标下的宿主顶层 HWND，并保存其原始 region。On first F8 press, lock the top-level host HWND under the pointer and save its original region.
2. 在宿主 region 中减去圆后，`WindowFromPoint` 得到该位置当前暴露的后方一层顶层窗口。After subtracting the circle, resolve the one top-level window now exposed behind the host.
3. 本次 F8 会话固定使用这个来源，不枚举或切换更深窗口。That source stays fixed for the hold session; deeper windows are not scanned or switched to.
4. `DwmPortalOverlay` 用 DWM thumbnail 把来源窗口对应区域画到圆形预览窗。DWM thumbnails paint the matching source region into the circular preview.
5. 预览在屏外窗更新单张 DWM 缩略图，抓帧后做圆形预乘 alpha，再用 `UpdateLayeredWindow` 一次提交（1.0.5）。形状不依赖 `SetWindowRgn` 是否裁切 DWM。Capture one DWM thumbnail off-screen, apply circular premultiplied alpha, present with `UpdateLayeredWindow` (1.0.5).
6. 松开 F8 时先隐藏预览，再恢复宿主 region 和来源扩展样式。On release, hide the preview, then restore the host region and source extended styles.

## 交互与前台保护 / Input and foreground protection

寸镜不转发或合成鼠标事件。圆形缺口让 Windows 自己把真实鼠标事件送给下面的窗口。透视期间，来源窗口临时增加 `WS_EX_NOACTIVATE`；若来源仍主动争夺前台，WinEvent 守卫把它放回宿主之后并恢复宿主前台。

PierceView does not synthesize mouse input. The circular hole lets Windows deliver real pointer events to the window beneath. During a session the source temporarily gains `WS_EX_NOACTIVATE`; if it still steals focus, a WinEvent guard restores the host order and foreground.

1.0 没有全局低级鼠标钩子，也没有深层命中识别或后台窗口提升算法。

Version 1.0 has no global low-level mouse hook, no deep hit-testing, and no background window promotion algorithm.

## 线程与恢复 / Threads and restore

- UI 线程：托盘和设置。UI thread: tray and settings.
- 运行线程：F8、物理 region、单层会话状态。Runtime thread: F8, physical region, session state.
- DWM STA 线程：预览窗口和 DWM thumbnail。DWM STA thread: preview windows and thumbnails.

正常松键、暂停、设置重启运行时、托盘退出和普通进程退出都会执行同一恢复路径。强制结束进程无法保证托管清理，因此不要在 F8 按住时用任务管理器强制结束。

Release, pause, settings-driven restart, tray exit, and normal process exit share one restore path. Killing the process from Task Manager while F8 is held cannot guarantee cleanup.

## 已知技术边界 / Technical boundaries

圆形由分层位图 alpha 蒙版保证。可用 `--visual-smoke` 做自动色块采样回归。个别来源窗口若 DWM 缩略图/PrintWindow 抓取失败，预览可能不可用。更激进的合成方式不在本页承诺范围内。

The circle is guaranteed by layered bitmap alpha. `--visual-smoke` runs automated fixture sampling. Some source windows may fail DWM thumbnail/PrintWindow capture. More aggressive compositing approaches are outside this document’s promises.
