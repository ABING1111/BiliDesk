using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「我的」页: 资料 + 统计 + 快捷入口 + 收藏夹卡片。
/// 页面实例被 MainWindow 缓存, 每次切进来 Loaded 都会重拉一次数据 ——
/// 硬币/经验/粉丝这类数字过期了看着难受, 而且本页请求量小, 重拉没有成本压力。
/// ★ 2026-11-27: 快捷入口第三枚改成「我的订阅」(进订阅页); 下方收藏夹区块保持原样。
/// </summary>
public partial class MinePage : UserControl, IRefreshable
{
    private MineViewModel? Vm => DataContext as MineViewModel;

    public MinePage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (Vm != null) await Vm.LoadAsync();
        };
        SizeChanged += (_, _) => UpdateQuickEntryPlacement();
    }

    // ---- 快捷入口的落位: 一行 / 两行 / 落到资料头下方 ----

    /// <summary>快捷入口与资料块之间的最小水平间距(= 面板的左外边距)</summary>
    private const double QuickEntryGap = 24;

    /// <summary>快捷入口落到资料头下方时, 与资料头的间距</summary>
    private const double QuickEntryDropGap = 22;

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => UpdateQuickEntryPlacement();

    private void OnQuickGridSizeChanged(object sender, SizeChangedEventArgs e) => UpdateQuickEntryPlacement();

    /// <summary>
    /// 决定四枚快捷入口放哪儿(2026-11-27, 用户要求"变窄时先折成两行, 实在没位置再放底下"):
    ///   ① 右侧放得下一行            → 同一行, 贴最右;
    ///   ② 放不下一行、放得下两行    → 折成两行, 仍在右侧(两行都靠右, 见 RightWrapPanel);
    ///   ③ 连两行都放不下            → 落到资料头下方, 从头像的起始线向右排。
    ///
    /// ★ 按**实际剩余宽度**算, 不按窗口档位: 档位只有三档、还是按窗口宽度算的 ——
    ///   125% 缩放下一台 1565px 的窗口只有 1252 DIP, 右侧明明放得下, 按档位却判成"窄"。
    /// ★ 只在结果变了的时候写属性: 本方法由 SizeChanged 触发, 而它改的属性(行/列/高度)
    ///   又会让 HeaderGrid 改尺寸。结果不变时什么都不写, 回路自然停下。
    /// ★ 子元素的 DesiredSize 要第一次布局之后才有值; 那之前直接返回,
    ///   等随后必然到来的那一次 SizeChanged 再算。
    /// </summary>
    private void UpdateQuickEntryPlacement()
    {
        var count = QuickGrid.Children.Count;
        if (HeaderGrid.ActualWidth <= 0 || count == 0) return;

        var widths = new double[count];
        for (var i = 0; i < count; i++)
        {
            // DesiredSize 含 Margin —— 胶囊之间的间距因此自动算进来
            var w = QuickGrid.Children[i].DesiredSize.Width;
            if (w <= 0) return;
            widths[i] = w;
        }

        // "均分成两行"所需的宽度: 前一半一行、后一半一行(四枚即 2 + 2), 取较宽的那一行。
        // ★ 必须和面板真实的折法一致 —— 面板是均分(BalanceLines), 这里就不能按"任意切分点里最省的"
        //   来算: 那样判得偏松, 会出现"判定说放得下、面板却因某一行超宽退回贪心"的 3+1。
        var half = (count + 1) / 2;
        double first = 0, second = 0;
        for (var i = 0; i < half; i++) first += widths[i];
        for (var i = half; i < count; i++) second += widths[i];
        var twoLineNeed = System.Math.Max(first, second);

        var available = HeaderGrid.ActualWidth - ProfileBlock.ActualWidth - QuickEntryGap;
        var stayRight = available >= twoLineNeed;

        var newCol = stayRight ? 1 : 0;
        var newRow = stayRight ? 0 : 2;
        if (Grid.GetColumn(QuickGrid) != newCol) Grid.SetColumn(QuickGrid, newCol);
        if (Grid.GetRow(QuickGrid) != newRow) Grid.SetRow(QuickGrid, newRow);

        var newAlign = stayRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        if (QuickGrid.HorizontalAlignment != newAlign) QuickGrid.HorizontalAlignment = newAlign;

        // 落到下方时从头像的起始线起排(去掉左间距); 留在右侧时补回左间距, 右边的 -12 抵掉最后一枚的右间距
        var newMargin = stayRight ? new Thickness(QuickEntryGap, 0, -12, 0) : new Thickness(0);
        if (QuickGrid.Margin != newMargin) QuickGrid.Margin = newMargin;

        // 落到下方时限宽 = 页首宽度: 它在 Auto 列里, 不限宽就拿到无限宽、永远不折行
        var newMaxWidth = stayRight ? double.PositiveInfinity : HeaderGrid.ActualWidth;
        if (Math.Abs(QuickGrid.MaxWidth - newMaxWidth) > 0.5) QuickGrid.MaxWidth = newMaxWidth;

        var newGap = stayRight ? 0 : QuickEntryDropGap;
        if (Math.Abs(HeaderGap.Height - newGap) > 0.5) HeaderGap.Height = newGap;
    }

    // ---- 快捷入口(直接走主导航, 与首页右上浮层同一套) ----

    private void OnOpenCacheClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Cache);

    private void OnOpenHistoryClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.History);

    /// <summary>快捷入口「我的订阅」: 进订阅页(追番/追剧/订阅的合集)。</summary>
    private void OnOpenSubscriptionClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Subscription);

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

    /// <summary>F5: 刷新收藏夹(与右上角刷新按钮同一条路径, 强制拉取)。</summary>
    public void Refresh() => OnRefreshFoldersClick(this, new RoutedEventArgs());

    /// <summary>点收藏夹卡片 → 收藏页并选中那个夹(经 MainWindow.OpenFavFolder 落地)</summary>
    private void OnFolderClick(object sender, MouseButtonEventArgs e)
    {
        // 点在卡片右上角的「删除收藏夹」上时不要顺带打开收藏页:
        // Button 通常会吞掉鼠标事件, 但这里挂的是 MouseLeftButtonUp 手势, 再兜一层更稳。
        if (VisualTreeUtil.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;

        if (sender is FrameworkElement { DataContext: FavFolder folder })
            SvcWindow.Main?.OpenFavFolder(folder);
    }

    /// <summary>
    /// 卡片右上角的「删除收藏夹」(2026-11-19): 交给 VM 命令(确认 → 接口 → 重拉卡片)。
    /// </summary>
    private void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: FavFolder folder } &&
            Vm?.DeleteFolderCommand is { } cmd && cmd.CanExecute(folder))
        {
            cmd.Execute(folder);
        }
    }
}
