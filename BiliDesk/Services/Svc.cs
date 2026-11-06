using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk;

/// <summary>全局服务入口(简易服务容器)</summary>
public static class Svc
{
    public static ThemeService Theme => ThemeService.Instance;
    public static SettingsStore Settings => SettingsStore.Instance;
    public static SessionManager Session => SessionManager.Instance;
    public static SearchHistoryService SearchHistory => SearchHistoryService.Instance;
    public static ToastService Toast => ToastService.Instance;
    /// <summary>下载进度中心: 同时下多个视频时合并成一条稳定进度, 见 issue #38</summary>
    public static DownloadProgressService DownloadProgress => DownloadProgressService.Instance;
    public static ApiClient Api { get; } = new();
    /// <summary>扫码登录接口(不带会话 Cookie, 见 PassportService 的说明)</summary>
    public static PassportService Passport => PassportService.Instance;
    public static WebView2Service WebView2 { get; } = new();
    public static PlayerService Player { get; } = new();
    /// <summary>赞助片段标注数据源(SponsorBlock 兼容服务端)</summary>
    public static SponsorBlockService SponsorBlock => SponsorBlockService.Instance;
    /// <summary>视频线路(CDN)测速与选择, 设置页可配</summary>
    public static CdnService Cdn => CdnService.Instance;
    public static HistorySyncService HistorySync => HistorySyncService.Instance;
    /// <summary>本机播放进度记录与自动续播</summary>
    public static PlaybackProgressService PlaybackProgress => PlaybackProgressService.Instance;
    /// <summary>应用级 UP 主动态新消息检测(导航栏红点 / 动态页排序)</summary>
    public static DynamicNotifyService DynamicNotify => DynamicNotifyService.Instance;
    public static NavigationDispatcher? Navigate { get; set; }
}
