# WindowPortal 0.7 架构说明

## 核心数据流

```text
F8 + 鼠标坐标
      |
      +--> WindowRegionController：从宿主 HWND region 中减去圆
      |
      +--> PortalOverlayManager：枚举宿主之后最多三层顶层窗口
                 |
                 +--> CompatibilityPolicy：允许 / 无视觉表面 / 受保护 / 忽略
                 |
                 +--> PortalScene：每个可渲染层注册一个 DWM thumbnail
                            |
                            +--> 椭圆 ∩ 当前层矩形 - 所有浅层矩形
                            +--> 所有图层一次 DeferWindowPos 同步移动
      |
鼠标按下 --> WH_MOUSE_LL --> ForegroundZOrderGuard
                            +--> WS_EX_NOACTIVATE
                            +--> SetWindowPos(target, host, SWP_NOACTIVATE)
                            +--> WinEvent + 10ms guard timer 恢复宿主前台
```

## 从 v6 到 v7 的关键变化

v6 为获得圆边缘而把每个画面拆成 3 px 水平条带。半径 180 时，单个缓冲约有 121 个 DWM 缩略图，并用前后两个顶层窗逐帧隐藏/显示。这会造成窗口位置和 DWM 提交不同步，出现双圆、频闪和偶发黑帧。

v7 使用一个持久场景：

- 每层只注册一个 DWM thumbnail。
- 使用目标窗口 region 形成圆形和层级裁剪。
- 三个层窗在同一次 `BeginDeferWindowPos` / `EndDeferWindowPos` 中移动。
- 鼠标静止且来源窗口几何未变化时完全跳过更新。
- 只有来源 HWND 列表改变时才重建隐藏场景并原子替换。

## 多层可见区域算法

对第 `i` 层，窗口 region 近似为：

```text
circle
∩ bounds(layer[i])
- bounds(layer[0])
- ...
- bounds(layer[i-1])
```

因此 -1、-2、-3 的图像不会互相覆盖错误区域。当前使用顶层窗口矩形近似遮挡；复杂的 per-pixel layered window、圆角和透明子区域是 0.8 的精细化方向。

## 线程模型

- 主线程：F8/退出键轮询、宿主 region 更新和诊断模式。
- STA 合成线程：WinForms 消息循环、DWM 目标窗、鼠标钩子与 WinEvent 回调。
- 不进入目标进程，不创建远程线程，不读取目标进程内存。

## 失败恢复

- 正常松开 F8：隐藏/销毁 DWM 场景，卸载 hooks，恢复所有后台扩展样式，最后恢复宿主 region。
- Ctrl+C、正常进程退出、未处理异常：执行同一恢复路径。
- 强制终止（Task Manager End task / `taskkill /F`）无法保证托管 finally 或 ProcessExit 执行；0.8 需要独立 watchdog 恢复默认 region 与样式。
