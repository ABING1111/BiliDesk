using Microsoft.Web.WebView2.Wpf;

namespace BiliDesk.Services;

/// <summary>主窗口引用(供退出登录时清 Cookie 等场景使用)</summary>
public static class SvcWindow
{
    /// <summary>
    /// 主窗口。正常运行时在 <c>App.OnStartup</c> 里登记; **可空**是因为"还没登记"是真实存在的状态
    /// (启动早期、以及探针等不跑完整启动流程的宿主) —— 调用方一律用 `is { } x` / `?.` 处理,
    /// 别在这上面直接解引用: 那会变成一句"空引用异常", 而不是"这一步跳过"。
    /// </summary>
    public static global::BiliDesk.MainWindow? Main { get; set; }

    /// <summary>主窗口里那个 1×1 的隐藏 WebView2; 主窗口还没建好时是 null</summary>
    public static WebView2? HiddenWebView => Main?.HiddenWebView;
}
