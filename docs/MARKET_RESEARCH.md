# WindowPortal 国内外同类产品市场调研

- 版本：0.7
- 调研日期：2026-08-09
- 方法：仅采用产品官网、官方文档或官方代码仓库；功能判断以页面可验证描述为准，不使用未经验证的下载量和收入推断。

## 1. 市场定义

WindowPortal 所在领域横跨四类工具：窗口透明、窗口置顶/画中画、局部实时预览、桌面窗口管理。现有产品通常改变整个窗口的透明度，或创建一个单窗口副本；“在当前窗口局部挖洞、实时显示多层后台窗口、把真实鼠标事件送往洞内目标，同时限制后台窗口不得越过当前窗口”的组合仍较少见。

## 2. 竞品矩阵

| 产品 | 市场/平台 | 官方可验证能力 | 与 WindowPortal 的差异 |
|---|---|---|---|
| Microsoft PowerToys Crop And Lock | 全球 / Windows | 创建应用局部的 live thumbnail；支持 Thumbnail 与 Reparent 两种裁剪方式 | 是独立裁剪窗口，不是在当前窗口中形成随鼠标移动的真实穿透区域；不合成 -1/-2/-3 |
| OnTopReplica | 全球 / Windows / 开源 | 实时复制单个窗口、局部区域、透明度、click forwarding、click-through | 单源副本和固定浮窗；不保留“宿主始终在前、后台内部重排”的约束 |
| WindowTop | 全球 / Windows / 商业 | Always on Top、Opacity、PiP/Crop、Glass Mode、Anchors | 面向完整窗口或 PiP；不是局部圆洞和多后台窗口空间合成 |
| Actual Window Manager | 全球 / Windows / 商业 | 透明窗口、置顶、窗口规则、虚拟桌面等综合管理 | 功能面广，但透明度作用于窗口整体，交互模型不同 |
| Glass2k | 全球 / Windows / 免费旧工具 | 快捷键调节整个窗口透明度、记忆设置、置顶 | 技术年代较早；没有局部区域、实时多层画面和安全策略 |
| Snipaste | 中国 / Windows、macOS、Linux | 截图与贴图（pin）工作流 | 贴图以静态内容为主，不是后台应用真实实时交互 |
| Quicker | 中国 / Windows / 自动化平台 | 动作、面板和自动化扩展生态 | 可承载窗口脚本，但不是专用 DWM 多层合成产品 |
| AquaSnap | 全球 / Windows / 商业 | 窗口 docking、snapping、tiling、组织管理 | 解决布局管理，不解决局部透视和后台交互 |
| ZoomIt | 全球 / Windows / Microsoft Sysinternals | 屏幕缩放、标注、演示 | 操作的是屏幕放大与演示画面，不是后台窗口层级 |
| AfloatX | 全球 / macOS / 开源 | macOS 窗口浮动/置顶辅助 | 平台和交互目标均不同，可作为跨平台窗口增强参考 |

## 3. 可验证来源

- [PowerToys Crop And Lock](https://learn.microsoft.com/en-us/windows/powertoys/crop-and-lock)：官方说明其创建 smaller cropped windows，可生成反映原窗口变化的 live thumbnails。
- [PowerToys Always On Top](https://learn.microsoft.com/en-us/windows/powertoys/always-on-top)
- [Microsoft DWM Thumbnail Overview](https://learn.microsoft.com/en-us/windows/win32/dwm/thumbnail-ovw)
- [OnTopReplica 官方仓库](https://github.com/LorenzCK/OnTopReplica)：README 列出 subregion、opacity、click forwarding 与 click-through。
- [WindowTop 官网](https://windowtop.info/)：列出 Always on Top、Opacity、PiP/Crop、Glass Mode 和 Anchors。
- [Actual Window Manager 官网](https://www.actualtools.com/windowmanager/features/)
- [Glass2k 官网](https://chime.tv/products/glass2k.shtml)
- [Snipaste 官方文档](https://docs.snipaste.com/)
- [Quicker 官网](https://getquicker.net/)
- [AquaSnap 官网](https://www.nurgo-software.com/products/aquasnap)
- [ZoomIt 官方页面](https://learn.microsoft.com/en-us/sysinternals/downloads/zoomit)
- [AfloatX 官方仓库](https://github.com/jslegendre/AfloatX)

## 4. 用户机会

核心机会不是“又一个透明度工具”，而是建立一种临时的空间快捷操作：用户不改变主工作窗口布局，只需按住快捷键，就能在鼠标附近观察并操作后方应用。适合 AI 对话/IDE 与浏览器、文件管理器、监控面板、参考资料之间的快速交叉操作。

可形成差异化的能力：

1. 圆形局部而非整个窗口透明。
2. -1/-2/-3 在同一个圆内按实际可见区域同时合成。
3. 真实鼠标事件，不依赖截图 OCR 或远程控制。
4. 后台窗口只在宿主之后重排，不破坏主工作上下文。
5. 对安全桌面、DRM、游戏和反作弊进程采取明确的默认拒绝策略。

## 5. 商业与产品判断

- 早期目标用户：多窗口知识工作者、开发者、AI/IDE 重度用户、需要短时参考后台信息的创作者。
- 推荐形态：免费基础版验证需求；签名安装包、配置界面、规则和企业策略作为产品化阶段能力。
- 最大进入障碍：Windows 合成兼容性、窗口权限边界、杀毒/SmartScreen 信任和反作弊风险，而不是圆形 UI 本身。
- 0.7 仍属于技术预览；在代码签名、崩溃恢复守护进程、硬件/系统矩阵和公开 beta 反馈完成前，不应宣传为生产级或“兼容所有应用”。
