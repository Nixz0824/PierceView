# 寸镜 / PierceView 2.3 多层实验版

`2.3.0-multilayer-alpha.3` 是从公开稳定版 `2.2.0` 独立分支开发的本地测试版本，不替代 `2.2.0 GPU Edition` 或 `2.1.0 CPU Edition`。

`2.3.0-multilayer-alpha.3` is a local test build developed on a separate branch from the public stable `2.2.0`. It does not replace the `2.2.0 GPU Edition` or `2.1.0 CPU Edition`.

## 本轮范围 / Scope

- 只使用一块固定的圆角羽化矩形。Use one fixed rounded and feathered rectangle.
- 按宿主窗口后的真实 Z-order，最多识别并同时显示 `-1`、`-2`、`-3`、`-4` 四层；超过 `-4` 不识别。Resolve and simultaneously display at most layers `-1` through `-4` behind the host in real Z-order; ignore anything deeper than `-4`.
- 按每个窗口的真实屏幕边界重建遮挡：浅层覆盖处显示浅层，未覆盖处继续显示更深层，不把四张完整截图半透明叠加。Reconstruct occlusion from real screen bounds: shallower windows win where they cover, while uncovered pixels reveal deeper layers. Full-window screenshots are not alpha-stacked.
- 每层使用独立 WGC 会话和常驻 D3D11 纹理，但最终共用一张固定 DirectComposition 显示层，每次更新只提交一次。Each layer has an independent WGC session and persistent D3D11 texture, while all layers share one fixed DirectComposition display surface and one present per update.
- 点击矩形中可见的深层窗口时，只把它提升到宿主正后方的新 `-1`；宿主继续保持前台，其余后台来源按原相对顺序后移。Clicking a visible deep window promotes it only to the new `-1` slot behind the host; the host remains foreground and other sources shift back without changing their relative order.
- 前台窗口事件到达时立即把来源钳制回宿主后方，并以 2 ms 间隔短暂复查，避免高刷新率屏幕在后台应用主动激活时看到整窗闪现。Clamp a source behind the host immediately when a foreground event arrives, then recheck briefly at 2 ms intervals to prevent whole-window flashes when background apps activate on high-refresh displays.

## 暂未包含 / Not included yet

- 点击不可见、完全被浅层遮住的窗口。Clicking a completely occluded window that is not actually visible.

因此，本 alpha 主要用于验证多层视觉、移动稳定性和性能。真实点击、滚轮和拖放仍由 Windows 当前实际命中的最前一层接收。多层 GPU 会话失败时会安全回退到 2.1.0 单层 CPU 管线。

This alpha primarily validates multi-layer visuals, motion stability, and performance. Native click, wheel, and drag input still goes to the frontmost window actually hit by Windows. A multi-source GPU failure safely falls back to the 2.1.0 single-layer CPU renderer.
