using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「我的」页: 资料 + 统计 + 快捷入口 + 收藏夹卡片。
/// 页面实例被 MainWindow 缓存, 每次切进来 Loaded 都会重拉一次数据 ——
/// 硬币/经验/粉丝这类数字过期了看着难受, 而且本页请求量小, 重拉没有成本压力。
///
/// 2026-10-13: 动态/关注/粉丝三项统计改成可点(以前是纯展示), 各进各的二级页,
/// 与「我的收藏」快捷入口同级。理由: 那三个数字摆在那儿却点不动, 用户想看的
/// 就是"我关注了谁 / 谁关注了我 / 关注的 UP 更新了啥", 现在有一条明确的路过去。
/// </summary>
public partial class MinePage : UserControl
{
    private MineViewModel? Vm => DataContext as MineViewModel;

    public MinePage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (Vm != null) await Vm.LoadAsync();
        };
    }

    // ---- 快捷入口(直接走主导航, 与首页右上浮层同一套) ----

    private void OnOpenCacheClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Cache);

    private void OnOpenHistoryClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.History);

    private void OnOpenFavoritesClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Favorites);

    private void OnOpenWatchLaterClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.WatchLater);

    /// <summary>刷新收藏夹。★ 必须 force —— 收藏夹默认只在第一次进本页时拉一次
    /// (见 MineViewModel._foldersLoaded 的说明), 不强制刷新的话这个按钮会"点了没反应"。</summary>
    private void OnRefreshFoldersClick(object sender, RoutedEventArgs e)
    {
        if (Vm != null) _ = Vm.LoadFoldersAsync(force: true);
    }

    // ---- 三项统计(2026-10-13 改成可点, 与「收藏」同级) ----

    /// <summary>动态 → 动态页(关注 UP 的近期投稿, 见 FollowPage)</summary>
    private void OnOpenDynamicClick(object sender, MouseButtonEventArgs e)
        => App.MainVm.Navigate(PageKey.Follow);

    /// <summary>关注 → 关系页(关注模式)</summary>
    private void OnOpenFollowingClick(object sender, MouseButtonEventArgs e)
        => SvcWindow.Main?.NavigateToRelation(RelationMode.Following);

    /// <summary>粉丝 → 关系页(粉丝模式)</summary>
    private void OnOpenFansClick(object sender, MouseButtonEventArgs e)
        => SvcWindow.Main?.NavigateToRelation(RelationMode.Fans);

    /// <summary>点收藏夹卡片 → 收藏页并选中那个夹(经 MainWindow.OpenFavFolder 落地)</summary>
    private void OnFolderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FavFolder folder })
            SvcWindow.Main?.OpenFavFolder(folder);
    }
}
