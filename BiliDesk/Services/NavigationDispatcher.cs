using System;
using System.Windows;
using BiliDesk.Utils;
using BiliDesk.Views;
using BiliDesk.Views.Pages;

namespace BiliDesk.Services;

/// <summary>导航调度: 拦截 WebView2 跳转, 把 B 站原生页面(视频/UP主空间/搜索) 路由到 WPF 原生窗口/页面</summary>
public class NavigationDispatcher
{
    private readonly Window _owner;

    public NavigationDispatcher(Window owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// 处理一个 URL: 根据路径决定打开原生播放器/UP主页/搜索, 或交给 WebView2 继续浏览。
    /// 注: 当前版本已改为"不自动拦截 WebView2 内部跳转"(见 BrowserWindow 说明),
    /// 因此本方法暂时没有调用方, 作为后续重新接线时的统一入口保留。
    /// </summary>
    public void HandleUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        var bvid = UrlParser.ExtractBvid(url);
        if (!string.IsNullOrEmpty(bvid))
        {
            OpenVideoPlayer(bvid);
            return;
        }

        var uid = UrlParser.ExtractUid(url);
        if (!string.IsNullOrEmpty(uid))
        {
            OpenUserSpace(uid);
            return;
        }

        var keyword = UrlParser.ExtractKeyword(url);
        if (!string.IsNullOrEmpty(keyword))
        {
            OpenSearch(keyword);
            return;
        }

        // 其他 URL 交给 WebView2 继续
        // 由调用方决定
    }

    /// <summary>打开原生视频播放器(LibVLC)</summary>
    public void OpenVideoPlayer(string bvid)
    {
        // 统一走 PlayerService: 复用同一个播放器窗口, 且不会复用到"正在关闭中"的窗口。
        // 之前这里每次 new 一个 PlayerWindow, 与 PlayerService 的复用逻辑并存,
        // 是"播放后立刻退出、再点视频又立刻退出 / 偶发 0xc0000005"的诱因之一。
        Svc.Player.PlayVideo(bvid);
    }

    /// <summary>用 WebView2 打开任意网页(用户主动触发的回退入口, 非主流程)</summary>
    public void OpenWebViewWindow(string url, string? title = null)
    {
        if (string.IsNullOrEmpty(url)) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var win = new BrowserWindow(url, title ?? url);
            win.Owner = _owner;
            win.Show();
        });
    }

    /// <summary>
    /// 打开 UP 主主页。
    ///
    /// 优先在**主窗口内容区里内嵌打开**(盖一层带返回按钮的页面), 而不是另开一个独立窗口:
    /// 独立窗口观感像换了个程序、关掉时容易连主界面一起关, 而且不在导航体系里。
    /// 只有主窗口不可用时才退回"新开窗口"这条老路(例如应用正在退出的边缘场景)。
    /// </summary>
    public void OpenUserSpace(string uid)
    {
        if (!long.TryParse(uid, out var mid) || mid <= 0) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var page = new UserSpacePage(mid);

            var main = SvcWindow.Main;
            if (main != null && main.IsLoaded)
            {
                main.ShowEmbeddedPage(page, "UP 主主页");
                return;
            }

            var host = new Window
            {
                Title = "UP 主主页",
                Width = 980,
                Height = 720,
                Background = ThemeBackground(),
                Content = page
            };
            host.Owner = _owner;
            host.Show();
        });
    }

    /// <summary>打开搜索结果(原生 WPF Window 承载)</summary>
    public void OpenSearch(string? keyword)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var vm = new ViewModels.SearchViewModel();
            var page = new SearchPage { DataContext = vm };
            if (!string.IsNullOrEmpty(keyword))
            {
                vm.Keyword = keyword;
                _ = vm.SearchAsync();
            }
            var host = new Window
            {
                Title = "搜索 - " + (keyword ?? ""),
                Width = 1100,
                Height = 720,
                Background = ThemeBackground(),
                Content = page
            };
            host.Owner = _owner;
            host.Show();
        });
    }

    /// <summary>
    /// 取当前主题对应的窗口底色。
    /// 这两个宿主窗口原先写死 WindowSolidLightBrush, 深色模式下会闪出一块白色底。
    /// </summary>
    private static System.Windows.Media.Brush ThemeBackground()
    {
        var key = Helpers.ThemeService.Instance.IsDark ? "WindowSolidDarkBrush" : "WindowSolidLightBrush";
        return (System.Windows.Media.Brush)Application.Current.Resources[key];
    }
}