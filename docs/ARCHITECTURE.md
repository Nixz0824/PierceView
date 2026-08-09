# 寸镜 / PierceView 1.0 架构说明

## 总体结构

```text
托盘 UI 线程
  ├─ NotifyIcon：启动/暂停、设置、帮助、退出
  ├─ SettingsForm：半径、语言
  └─ PortalRuntime：启动/停止工作线程

单层运行线程
  ├─ GetAsyncKeyState(F8) + 鼠标坐标
  ├─ WindowRegionController：在宿主 region 中减去圆
  └─ DwmPortalOverlay：复制固定的 -1 来源
       ├─ 两个预览窗口交替提交
       ├─ NonActivatingWindowGuard：临时 WS_EX_NOACTIVATE
       └─ ForegroundZOrderGuard：WinEvent 恢复宿主前台
```

## 托盘生命周期

`Program` 只在普通启动时创建单实例互斥量和 `PierceViewApplicationContext`。应用上下文拥有托盘图标、菜单、设置窗口和运行时；退出时先停止运行时、恢复窗口，再移除托盘图标。命令行自检、窗口清单和探针模式不启动托盘。

## 单层透视数据流

1. F8 首次按下时，`WindowRegionController` 锁定鼠标下的宿主顶层 HWND，并保存其原始 region。
2. 在宿主 region 中减去圆后，`WindowFromPoint` 得到该位置当前暴露的 -1 顶层窗口。
3. 本次 F8 会话固定使用这个来源，不枚举或切换 -2/-3/-4。
4. `DwmPortalOverlay` 用 DWM thumbnail 把来源窗口对应区域画到圆形预览窗。
5. V6 为近似圆边缘，按 3 px 水平条带注册缩略图，并在前后两个预览窗之间原子换帧。
6. 松开 F8 时先隐藏预览，再恢复宿主 region 和来源扩展样式。

## 交互与前台保护

寸镜不转发或合成鼠标事件。圆形缺口让 Windows 自己把真实鼠标事件送给下面的窗口。透视期间，来源窗口临时增加 `WS_EX_NOACTIVATE`；若来源仍主动争夺前台，WinEvent 守卫把它放回宿主之后并恢复宿主前台。

1.0 没有 `WH_MOUSE_LL`，没有深层命中识别，也没有后台窗口提升算法。

## 线程与恢复

- UI 线程：托盘和设置。
- 运行线程：F8、物理 region、单层会话状态。
- DWM STA 线程：预览窗口和 DWM thumbnail。

正常松键、暂停、设置重启运行时、托盘退出和普通进程退出都会执行同一恢复路径。强制结束进程无法保证托管清理，因此不要在 F8 按住时用任务管理器强制结束。

## 已知技术边界

条带圆是 V6 稳定版的有意保留方案，边缘仍有轻微阶梯。两个预览窗降低撕裂，但无法从原理上保证所有 GPU/驱动组合都没有偶发黑帧。矩形 GPU 合成与 Alpha 羽化属于 2.0/2.1，最多四层与深层重排属于 2.5。
