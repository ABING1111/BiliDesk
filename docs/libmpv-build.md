# libmpv 构建来源与许可

播放内核使用 shinchiro 的 Windows 构建（x86-64 基线版，非 v3）。

| 项 | 值 |
| --- | --- |
| 包名 | `mpv-dev-x86_64-20261111-git-bcde6b93cc` |
| mpv 版本 | `v0.42.0-38-gbcde6b93c` |
| FFmpeg 版本 | `N-128766-gcd4e96eb3` |
| 架构 | x86-64 基线（PE machine `0x8664`，未启用 x86-64-v3 指令集） |
| 运行时文件 | `libmpv-2.dll`（121,789,952 字节） |
| SHA-256 | `88504EE5560942A955405E99DFB5971368E4CF851321F36565BF2A667271DB1B` |
| 本地放置 | `BiliDesk\libmpv\win-x64\libmpv-2.dll`（已 gitignore，不入库） |

## 许可

- mpv 本体为 **GPLv2+**；该构建包含 GPL 组件（FFmpeg 等），**不是 LGPL 构建**。
- 本项目（BiliDesk）为 GPLv3，与 GPLv2+ 兼容；分发安装包时需要：
  1. 随包附带 GPLv2 许可证文本；
  2. 提供 mpv / FFmpeg 对应源码的获取方式（上游仓库或构建方源码包）。
- 安装器/发布脚本在打包前应校验上面的 SHA-256。

## 来源

- 构建发布：shinchiro 的 `mpv-dev-x86_64-*`（SourceForge: mpv-player-windows）
- mpv 源码：https://github.com/mpv-player/mpv （commit `bcde6b93cc`）
- FFmpeg 源码：https://ffmpeg.org