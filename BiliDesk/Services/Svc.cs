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
    public static ApiClient Api { get; } = new();
    public static WebView2Service WebView2 { get; } = new();
    public static PlayerService Player { get; } = new();
    /// <summary>赞助片段标注数据源(SponsorBlock 兼容服务端)</summary>
    public static SponsorBlockService SponsorBlock => SponsorBlockService.Instance;
    public static HistorySyncService HistorySync => HistorySyncService.Instance;
    public static NavigationDispatcher? Navigate { get; set; }
}