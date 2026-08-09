# 寸镜 / PierceView 同类产品市场调研

- 文档版本：1.0
- 更新日期：2026-08-10
- 定位：个人经验项目与 Windows 托盘小工具，不以融资或大规模商业化为前提
- 方法：只采用产品官网、官方文档或官方代码仓库可验证的公开能力，不推断下载量和收入

## 1. 品类判断

寸镜位于窗口透明、画中画/窗口复制、局部预览和窗口管理四类工具的交叉处。1.0 的核心并不是“透明度调节”，而是按住快捷键，在当前窗口内临时形成一个可交互的单层圆形入口。

## 2. 同类产品矩阵

| 产品 | 平台/形态 | 官方可验证能力 | 与 PierceView 1.0 的主要差异 |
|---|---|---|---|
| Microsoft PowerToys Crop And Lock | Windows / 免费 | 创建应用局部 live thumbnail；Thumbnail 与 Reparent 两种模式 | 生成独立裁剪窗口，不在当前窗口内随鼠标形成临时圆形缺口 |
| OnTopReplica | Windows / 开源 | 单窗口实时副本、局部区域、透明度、click forwarding/click-through | 固定单源浮窗；寸镜把入口放在宿主窗口内部并按住即用 |
| WindowTop | Windows / 商业 | Always on Top、Opacity、PiP/Crop、Glass Mode | 面向完整窗口和 PiP，功能更广；不是临时局部入口 |
| Actual Window Manager | Windows / 商业 | 透明度、置顶、窗口规则、虚拟桌面 | 综合窗口管理器，寸镜则刻意保持单功能托盘形态 |
| Glass2k | Windows / 免费旧工具 | 快捷键调节整个窗口透明度、记忆设置、置顶 | 修改整个窗口透明度，没有局部视觉复制和后台点击保护 |
| Snipaste | 中国 / 多平台 | 截图和贴图（pin） | 核心是静态/贴图工作流，不是后台应用实时交互 |
| Quicker | 中国 / Windows | 动作、面板和自动化扩展 | 通用自动化平台，产品复杂度和目标均不同 |
| AquaSnap | Windows / 商业 | docking、snapping、tiling | 解决窗口布局，不解决局部透视 |
| ZoomIt | Windows / Microsoft | 屏幕缩放、标注、演示 | 放大当前屏幕，不显示后台窗口 |

## 3. 来源

- [PowerToys Crop And Lock](https://learn.microsoft.com/en-us/windows/powertoys/crop-and-lock)
- [Microsoft DWM Thumbnail Overview](https://learn.microsoft.com/en-us/windows/win32/dwm/thumbnail-ovw)
- [OnTopReplica 官方仓库](https://github.com/LorenzCK/OnTopReplica)
- [WindowTop 官网](https://windowtop.info/)
- [Actual Window Manager 官网](https://www.actualtools.com/windowmanager/features/)
- [Glass2k 官网](https://chime.tv/products/glass2k.shtml)
- [Snipaste 官方文档](https://docs.snipaste.com/)
- [Quicker 官网](https://getquicker.net/)
- [AquaSnap 官网](https://www.nurgo-software.com/products/aquasnap)
- [ZoomIt 官方页面](https://learn.microsoft.com/en-us/sysinternals/downloads/zoomit)

## 4. 用户价值与差异化

- 按住即用、松开即恢复，适合短时查看，不改变长期桌面布局。
- 圆形局部入口比整窗透明更聚焦，也不要求先布置一个 PiP 浮窗。
- 点击使用 Windows 真实鼠标路径，不依赖 OCR、截图识别或远程控制。
- 托盘、两项设置和一页帮助即可完成日常操作，学习成本低。

1.0 的差异化建立在稳定的单层体验上，不应借尚未成熟的多层实验抬高承诺。

## 5. 产品判断

- 适合对象：在 AI 对话、IDE、浏览器、文件管理器和参考资料之间频繁核对的 Windows 用户。
- 合理形态：免费、轻量、便携式托盘工具；先积累工程、测试、版本和发布经验。
- 主要风险：DWM/GPU 兼容性、窗口权限、视觉稳定性、未签名 SmartScreen 与游戏/反作弊冲突，而不是菜单或设置本身。
- 不建议投入：前后端、账号、云同步、复杂规则、主题商城和商业支付。
- 后续价值：矩形 GPU 透视、Alpha 羽化、最多四层与受限重排，但必须按路线图拆版本验证。

结论：寸镜有清晰而小众的交互差异，但当前最优策略是把 1.0 做成可信赖的小工具和完整工程样本，而不是扩张为综合桌面平台。
