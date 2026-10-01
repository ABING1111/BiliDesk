using System.Windows;
using System.Windows.Controls;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「稍后再看」页(2026-09-28 新增): 卡片墙 + 编辑模式(批量移出/清空)。
/// 页面实例被 MainWindow 缓存, 每次切进来 Loaded 都重拉一次 —— 清单会被别的端改,
/// 而且本页只有一次请求, 重拉没有成本压力。
/// </summary>
public partial class WatchLaterPage : UserControl
{
    private WatchLaterViewModel? Vm => DataContext as WatchLaterViewModel;

    public WatchLaterPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (Vm != null) await Vm.LoadAsync();
        };
    }

    /// <summary>
    /// 滚到接近底部(或内容不足一屏)就补下一页。
    ///
    /// ★ 补页进行中必须暂停判定: 每补一批内容都在长, 这时"内容不够一屏"是假象 ——
    ///   不挡住的话几次 ScrollChanged 就把剩下所有页一口气全铺出来, 分页等于白做
    ///   (与首页分帧填充时踩的是同一个坑)。
    /// </summary>
    private int _autoFillCount;

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Vm is not { HasMore: true } vm) return;
        if (vm.IsFilling) return;
        if (_autoFillCount > 5) return;   // 防止"补了但布局还没长"造成的死循环

        if (sender is not ScrollViewer sv) return;
        var nearBottom = sv.ScrollableHeight - sv.VerticalOffset < 400;
        var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
        if (!nearBottom && !contentShort) return;

        _autoFillCount++;
        try { vm.ShowNextPage(); }
        finally { _autoFillCount--; }
    }

    /// <summary>返回「我的」页(入口在那里)</summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Mine);
}
