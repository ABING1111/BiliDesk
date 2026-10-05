# BiliDesk

哔哩哔哩第三方桌面客户端（Windows / WPF / .NET 8）。

![程序主界面](docs/screenshot-home.jpg)

> 非官方、非商业的开源项目，与哔哩哔哩官方无任何关联。视频内容与版权归哔哩哔哩所有。

## 下载

前往 [Releases](https://github.com/ABING1111/BiliDesk/releases/latest) 下载最新版本：

- **便携版 zip** —— 自包含，解压即用，不需要安装 .NET 运行时
- **安装器** —— 需要 .NET 8 桌面运行时（安装器会自动检测并引导下载）

## 功能

- 视频播放：LibVLC 内核，DASH 音视频双流，最高 4K，HEVC 优先选流
- 播放设置：默认画质可预选（该清晰度不可用时自动降级），线路自动测速/手动指定，免登录 1080P
- 播放器视图：视频可「铺满窗口」（收起右侧信息栏），另有全屏（F / 双击视频）
- 弹幕：滚动/顶部/底部全类型渲染，智能屏蔽、关键词黑名单、彩色开关、显示区域调节
- 短视频本地合流：≤3 分钟的视频下载 1080P DASH 后本地合流为 MP4 缓存播放
- SponsorBlock 跳过赞助片段（默认关闭，需用户手动开启）
- 登录（扫码）、历史/收藏/稍后再看/动态/消息、分区浏览、搜索
- 亮色/暗色/跟随系统三态主题，托盘常驻，自动检查更新

## 构建

需要 Windows 10+ 和 .NET 8 SDK（x64）。

```powershell
dotnet build BiliDesk/BiliDesk.csproj -c Release
```

LibVLC 原生库由 NuGet 包（`VideoLAN.LibVLC.Windows`）自动部署，无需手动安装。
完整发布（便携版 zip + Inno 安装器载荷）用仓库根目录的 `发布.ps1`。

## 许可

[GPL-3.0](LICENSE)
