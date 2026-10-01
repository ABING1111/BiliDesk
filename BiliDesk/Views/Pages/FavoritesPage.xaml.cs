using System.Windows;
using System.Windows.Controls;
using BiliDesk.Helpers;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 收藏页: 只展示 B 站账号的云端收藏夹。
/// 本机收藏夹(本地 JSON)已整体移除 —— 两套互不相通的数据源只会让人困惑,
/// 播放页的"收藏"现在也直接保存到云端。
/// </summary>
public partial class FavoritesPage : UserControl
{
    private FavoritesViewModel? Vm => DataContext as FavoritesViewModel;

    /// <summary>懒加载: 只在首次显示时拉一次, 避免每次切页都重新请求</summary>
    private bool _loadedOnce;

    public FavoritesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // 纳入全局屏蔽(命中关键词的内容会被隐藏), 见 FilterService
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is FavoritesViewModel vm) FilterService.Instance.Attach(vm.Items);
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        // 已经拿到内容就不再重复请求(页面会被主窗口反复挂载/卸载);
        // 但"上次没登录 / 请求失败"这两种情况要再给一次机会, 否则用户登录后回到这一页
        // 只能看到旧错误提示, 必须手动点刷新。
        if (_loadedOnce && (Vm.Folders.Count > 0 || Vm.Items.Count > 0)) return;
        _loadedOnce = true;
        await Vm.LoadAsync();
    }

    /// <summary>右上角返回按钮: 历史/收藏/缓存的入口都在「我的」页, 统一回那里</summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Mine);

    /// <summary>列表滚动到底部自动加载下一页</summary>
    private async void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight > 0 && e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 120
            && Vm is { HasMore: true } vm)
        {
            await vm.LoadMoreAsync();
        }
    }
}
