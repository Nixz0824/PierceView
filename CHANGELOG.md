# Changelog

本项目采用 [Semantic Versioning](https://semver.org/)。用户可见变更记录如下。

This project follows [Semantic Versioning](https://semver.org/). User-facing changes are listed below.

## [Unreleased]

### Documentation

- （暂无）

## [1.0.0] - 2026-08-10

首个公开版本。单层圆形透视托盘工具，可直接下载使用。

First public release. A single-layer circular portal tray utility ready to download and use.

### Packaging

- 正式发布包命名为 `PierceView-v1.0.0-win-x64.zip`（不再使用 `rc` 后缀）。
- Official package name is `PierceView-v1.0.0-win-x64.zip` (no `rc` suffix).
- 新增互动介绍页 `landing/`（按住 F8 演示透视与图片拖放）。
- Added interactive product site under `landing/`.

### Documentation

- 公开仓库整理为用户向首页；采用 PolyForm Noncommercial 1.0.0；品牌收敛为最终 Logo。
- Public-facing README cleanup; PolyForm Noncommercial 1.0.0; final logo only.

### Features

- 纯系统托盘小工具：普通启动无主窗口；托盘提供启动/暂停、设置、帮助、退出。
- Tray-only UX: no main window on normal launch; Start/Pause, Settings, Help, and Exit in the tray.
- 按住 `F8` 开启圆形透视：查看、滚动或点击紧贴当前窗口后的一层普通窗口；松开即恢复。
- Hold `F8` for a circular portal: view, scroll, or click the ordinary window directly behind the host; release to restore.
- 设置仅含透视圆半径与简体中文/English；配置保存在 `%LOCALAPPDATA%\PierceView\settings.json`。
- Settings cover only portal radius and Simplified Chinese / English; stored under `%LOCALAPPDATA%\PierceView\settings.json`.
- 兼容应用之间支持 Windows 原生拖放工作流（两端格式与权限均支持时）。
- Native Windows drag-and-drop workflows when both apps and privilege levels allow it.

### Safety & scope

- 普通用户权限运行；不注入进程、不读写其他进程内存、不模拟输入、不联网、不上传遥测、不安装驱动或全局低级键鼠钩子、不设置自启动。
- Runs at standard user rights; no injection, foreign process memory access, synthesized input, network, telemetry, drivers, global low-level hooks, or startup registration.
- 启动游戏、游戏启动器或反作弊服务前，请从托盘完全退出寸镜。
- Fully exit PierceView before starting games, launchers, or anti-cheat services.

### Known limitations

- 圆形边缘可能有轻微阶梯；个别 GPU/驱动或来源窗口可能出现短暂黑帧。
- Mild circular-edge stair-stepping; occasional black frames on some GPU/driver or source windows.
- 无重定向、受保护或特殊 GPU 表面可能只有点击、没有画面。
- Some no-redirection, protected, or special GPU surfaces may accept clicks without a visual.
- 发布包尚未 Authenticode 签名；Windows 可能提示“未知发布者”。
- Builds are not Authenticode-signed; Windows may show “Unknown publisher.”

## Earlier pre-release history

以下为整理公开仓库前的预发布演进摘要，仅供参考；不构成对 1.0.0 的额外功能承诺。

Brief pre-release history before the public-repo cleanup. For reference only; it does not add feature promises beyond 1.0.0.

### 0.7.x

- 修复颜色键与场景切换相关的闪烁、黑帧与双圆问题；增强视觉探针与发布物扫描记录。
- Fixed TransparencyKey-related flicker/black frames/double-circle issues; strengthened visual probes and release scanning notes.

### 0.5.x – 0.6.x

- DWM 条带圆预览、前台恢复守卫，以及后续预览交互实验。
- DWM strip circular preview, foreground restore guards, and later interaction experiments.
