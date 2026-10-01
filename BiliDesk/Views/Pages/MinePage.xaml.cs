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

    /// <summary>点收藏夹卡片 → 收藏页并选中那个夹(经 MainWindow.OpenFavFolder 落地)</summary>
    private void OnFolderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FavFolder folder })
            SvcWindow.Main?.OpenFavFolder(folder);
    }
}
