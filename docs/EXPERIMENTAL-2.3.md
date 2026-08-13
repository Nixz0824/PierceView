# 寸镜 / PierceView 2.3 多层开发记录

本页保留从 `2.2.0` 到正式 `2.3.0 GPU Edition` 的多层开发范围与技术决策。用户使用说明请优先阅读 [README](../README.md)、[架构](ARCHITECTURE.md)和[兼容性](COMPATIBILITY.md)。

This page preserves the multi-window development scope and technical decisions that led from `2.2.0` to the public `2.3.0 GPU Edition`. Users should prefer the [README](../README.md), [Architecture](ARCHITECTURE.md), and [Compatibility](COMPATIBILITY.md) documents.

## 本轮范围 / Scope

- 只使用一块固定的圆角羽化矩形。Use one fixed rounded and feathered rectangle.
- 按宿主窗口后的真实 Z-order，最多识别并同时显示 `-1`、`-2`、`-3`、`-4` 四层；超过 `-4` 不识别。Resolve and simultaneously display at most layers `-1` through `-4` behind the host in real Z-order; ignore anything deeper than `-4`.
- 按每个窗口的真实屏幕边界重建遮挡：浅层覆盖处显示浅层，未覆盖处继续显示更深层，不把四张完整截图半透明叠加。Reconstruct occlusion from real screen bounds: shallower windows win where they cover, while uncovered pixels reveal deeper layers. Full-window screenshots are not alpha-stacked.
- 每层使用独立 WGC 会话和常驻 D3D11 纹理，但最终共用一张固定 DirectComposition 显示层，每次更新只提交一次。Each layer has an independent WGC session and persistent D3D11 texture, while all layers share one fixed DirectComposition display surface and one present per update.
- 点击矩形中可见的深层窗口时，只把它提升到宿主正后方的新 `-1`；宿主继续保持前台，其余后台来源按原相对顺序后移。Clicking a visible deep window promotes it only to the new `-1` slot behind the host; the host remains foreground and other sources shift back without changing their relative order.
- 前台窗口事件到达时立即把来源钳制回宿主后方，并以 2 ms 间隔短暂复查，避免高刷新率屏幕在后台应用主动激活时看到整窗闪现。Clamp a source behind the host immediately when a foreground event arrives, then recheck briefly at 2 ms intervals to prevent whole-window flashes when background apps activate on high-refresh displays.
- F8 会话期间为宿主建立可恢复的临时置顶屏障，并把透明透视显示层绑定为宿主拥有窗口；后台来源即使主动激活也不能在合成器中覆盖宿主，松开 F8 后恢复宿主原始置顶状态。Create a restorable temporary topmost barrier for the host during the F8 session and bind the transparent portal surface as an owned window. A background source cannot cover the host even if it activates, and the host's original topmost state is restored on release.
- 持续校验“视觉新 `-1`”与 Windows 真实输入层级；若来源应用自行改变后台顺序，会在不激活窗口的前提下恢复所选来源，使后续点击、滚轮和拖放继续命中新 `-1`。Continuously validate the visual `-1` against Windows' physical input order. If a source app changes background ordering, restore the selected source without activation so subsequent click, wheel, and drag input still reaches the new `-1`.
- 深层交换期间短暂冻结最后一张完整有效合成帧，优先等待被提升来源的新 WGC 帧后再一次切换；最长等待 8 ms，避免静态窗口停住，同时减少独立捕获源的新旧帧中间态造成的偶发频闪。Briefly freeze the last complete composition during a deep-layer exchange and preferably switch once after the promoted source produces a fresh WGC frame. The hold is capped at 8 ms so static windows cannot stall while mixed old/new states across independent capture sources are less likely to flash.
- F8 按住期间每 75 ms 校验已捕获来源；关闭或最小化一层后，从当前最深有效来源继续向后补齐到最多四层。正常会话不会主动改动仍有效的来源名单。仍有效的捕获与纹理继续复用，新来源首帧到达前保留最后一张完整画面。Validate captured sources every 75 ms while F8 is held. If one closes or minimizes, continue behind the deepest valid source to backfill up to four layers. Normal sessions do not proactively alter a still-valid source set. Reuse valid captures and textures, and retain the last complete composition until a replacement publishes its first frame.
- 动态补位同步更新非激活样式、前台守卫和真实输入顺序；协调器不因普通 Z-order 变化重排仍有效的来源，因此不会覆盖深层点击已经建立的有效 `-1`。Synchronize non-activating styles, foreground protection, and real input order during backfill. Ordinary Z-order changes do not reorder still-valid captures, so reconciliation cannot overwrite the `-1` established by deep-window promotion.
- 单路抓帧异常或一次动态补位失败只暂停该次更新：固定 DirectComposition 表面继续显示最后一张完整合成画面，失败来源或新补位在后续循环恢复，不让宿主应用短暂露出。A single-source frame error or one failed backfill only skips that update: the fixed DirectComposition surface keeps the last complete composition visible while the source or replacement recovers later, preventing the host application from briefly showing through.
- 普通顶层来源在真实窗口边界内按不透明表面合成，并在 GPU 着色器中消除 WGC 暂态 alpha；DirectComposition 同时裁剪到实际透视框范围，减少宿主内容经透明像素泄漏的机会。Ordinary top-level sources are composed as opaque within their real window bounds after removing transient WGC alpha in the GPU shader. DirectComposition is also clipped to the portal frame, reducing any path for host content to leak through transparent pixels.
- 唯一的 GPU 显示 HWND 会持续核对其真实桌面层级，并在落到宿主窗口后方时以不激活窗口的方式立即复位；探针以 2 ms 间隔记录显示层低于宿主的瞬态帧。The single GPU display HWND continuously verifies its real desktop Z-order and is restored without activation whenever it falls behind the protected host. The probe records transient display-below-host samples at 2 ms intervals.
- 来源激活守卫按本次捕获的窗口家族匹配，不再按整个进程匹配；因此文件资源管理器与任务栏、桌面共享 `explorer.exe` 时不会发生错误层级恢复。Source activation protection matches only the captured window family rather than the whole process, preventing false Z-order recovery when File Explorer shares `explorer.exe` with the taskbar and desktop.

## 暂未包含 / Not included yet

- 点击不可见、完全被浅层遮住的窗口。Clicking a completely occluded window that is not actually visible.
- 抖音桌面版等只向顶层 WGC 返回透明空帧、且把实际内容放在不可独立捕获的 D3D 子窗口中的应用。Apps such as Douyin Desktop that return only a transparent top-level WGC frame while placing real content in a D3D child window that cannot be captured independently.

正式 2.3.0 已完成多层视觉、移动稳定性、深层提升、真实输入同步、动态补位和文件资源管理器稳定性验证。真实点击、滚轮和拖放仍由 Windows 当前实际命中的最前一层接收；多层 GPU 会话失败时会安全回退到 2.1.0 单层 CPU 管线。

The final 2.3.0 release covers multi-window visuals, motion stability, deep-window promotion, physical input synchronization, dynamic backfill, and File Explorer stability. Native click, wheel, and drag input still goes to the frontmost window actually hit by Windows. A multi-source GPU failure safely falls back to the 2.1.0 single-layer CPU renderer.
