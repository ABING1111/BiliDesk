using Microsoft.Web.WebView2.Wpf;

namespace BiliDesk.Services;

/// <summary>主窗口引用(供退出登录时清 Cookie 等场景使用)</summary>
public static class SvcWindow
{
    public static global::BiliDesk.MainWindow Main { get; set; } = null!;
    public static WebView2 HiddenWebView => Main.HiddenWebView;
}