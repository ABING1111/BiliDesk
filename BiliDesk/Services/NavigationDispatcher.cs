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
    /// 打开「我的空间」页(动态 / 关注 / 粉丝三个 Tab)。
    ///
    /// 与 <see cref="OpenUserSpace"/> / <see cref="OpenBangumi"/> 同款做法:
    /// 在**主窗口内容区里内嵌打开**(盖一层带返回按钮的页面), 而不是另开独立窗口 ——
    /// 用户是从某个页面点进来的, 要能"退回去", 另开窗口会让人失去方位感。
    ///
    /// ★ 这个页面**看的是谁的空间由 mid 决定**, 不是"一定是我的":
    ///   「我的」页点三个数字 → 传自己的 mid;
    ///   UP 主主页点关注数/粉丝数 → 传那个 UP 的 mid。两处共用同一个页面。
    ///
    /// ★ <paramref name="isSelf"/> 由调用方判定 —— 调用方本来就知道自己的 mid,
    ///   不必在这里多打一次 nav 请求(那一次往返不划算)。
    /// </summary>
    public void OpenUserCenter(long mid, string name = "", string face = "", bool isSelf = false,
                               ViewModels.UserCenterTab tab = ViewModels.UserCenterTab.Dynamics,
                               string? title = null)
    {
        if (mid <= 0) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var page = new UserCenterPage(mid, name, face, isSelf, tab);

            var main = SvcWindow.Main;
            if (main != null && main.IsLoaded)
            {
                main.ShowEmbeddedPage(page, title ?? (name.Length > 0 ? name + " 的空间" : "我的空间"));
                return;
            }

            var host = new Window
            {
                Title = title ?? "我的空间",
                Width = 980,
                Height = 720,
                Background = ThemeBackground(),
                Content = page
            };
            host.Owner = _owner;
            host.Show();
        });
    }

    /// <summary>
    /// 打开番剧选集页。
    ///
    /// 与 <see cref="OpenUserSpace"/> 同款做法: 在**主窗口内容区里内嵌打开**(盖一层带返回
    /// 按钮的页面), 而不是另开独立窗口 —— 追番页点进来就是"在当前上下文里看这一季的选集",
    /// 另开窗口会让人失去"我正在看追番列表"的方位感。
    ///
    /// 主窗口不可用时(应用正在退出的边缘场景)退回新开窗口, 与 OpenUserSpace 一致。
    /// </summary>
    public void OpenBangumi(BiliDesk.Models.BangumiItem item)
    {
        if (item == null || item.SeasonId <= 0) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var page = new BangumiPage(item);

            var main = SvcWindow.Main;
            if (main != null && main.IsLoaded)
            {
                main.ShowEmbeddedPage(page, item.Title);
                return;
            }

            var host = new Window
            {
                Title = item.Title,
                Width = 980,
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