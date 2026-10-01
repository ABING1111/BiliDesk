using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 离线缓存页: 管理下载目录里的视频文件(在应用内播放 / 打开所在位置 / 删除)。
/// </summary>
public partial class CachePage : UserControl
{
    private CacheViewModel? Vm => DataContext as CacheViewModel;

    public CachePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        // 每次进入都重扫一遍: 用户很可能刚在别处下载完/删掉了文件, 缓存列表必须反映磁盘现状。
        // 防重入交给 VM 自己的 Loading 哨兵。
        await Vm.LoadAsync();
    }

    /// <summary>右上角返回按钮: 历史/收藏/缓存的入口都在「我的」页, 统一回那里(旧的"返回首页"按钮已删)</summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Mine);

    /// <summary>点空白处也能播(整行可点), 但按到按钮上时不重复触发</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d && VisualTreeUtil.FindAncestor<Button>(d) != null) return;
        if ((sender as FrameworkElement)?.DataContext is CacheEntry entry)
            Vm?.PlayCommand.Execute(entry);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CacheEntry entry)
            Vm?.PlayCommand.Execute(entry);
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CacheEntry entry)
            Vm?.RevealCommand.Execute(entry);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CacheEntry entry)
            Vm?.DeleteCommand.Execute(entry);
    }
}
