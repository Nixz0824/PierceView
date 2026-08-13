# 寸镜介绍页 v7 / PierceView landing v7

## 布局对齐

顶栏与左右介绍卡共用 `--shell-pad` + `--shell-max`：

- 左：Logo 左缘 = 中文卡左缘  
- 右：GitHub 组右缘 = 英文卡右缘  

## 滚动行为

- **刷新始终停在顶部**（`scrollRestoration = manual` + 强制 `scrollTo(0)`）  
- 在演示阶段：向下滚轮约一截 → **要点整卡飞入**（不是滚一点出一点）  
- 再向上 → 回到演示  
- 要点出现后再向下 → 进入底部留言/统计  

## F8 演示（对齐产品主路径）

- 按住 `F8`/`空格`：圆角矩形透视框 / rounded rectangular portal
- 框内：**观察、选中文字、点击按钮、滚动长页面** / view, select, click, and scroll
- **仅图片**可拖到投放区，松开 F8 投放  

页面演示仍使用一张可交互参考页，产品文字与下载入口已同步到 2.3 GPU 版本的“最多四层、深层提升、输入同步与动态补位”。The interactive page remains a simple one-page demo, while product copy and downloads describe the 2.3 GPU Edition's four-layer reconstruction, deep-window promotion, input synchronization, and dynamic backfill.

## 预览

```powershell
cd landing
python -m http.server 8765
```

http://127.0.0.1:8765/ （Ctrl+F5）

## GitHub Pages

推送到 `main` 且改动 `landing/**` 时，工作流 `.github/workflows/pages.yml` 会部署本目录。

在线地址（仓库启用 Pages 后）：

`https://nixz0824.github.io/PierceView/`
