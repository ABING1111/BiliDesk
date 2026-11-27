using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「我的订阅」页(2026-11-27 新增): 合集 / 追番 / 追剧 三个 Tab(合集是默认页)。
///
/// ★ Tab 选中态**只在这里写 IsChecked**(与我的空间页同一条经验):
///   分散着改必然出现“两个都亮”或“两个都不亮”; VM 的 Tab 变了也由这里回写。
/// ★ 追番/追剧是页码分页, 合集是滚动加载 —— 由 VM 的 ShowPager / LoadMoreCollectionsAsync 分开处理。
/// ★ 页面被 MainWindow 缓存: 首次 Loaded 才 InitAsync; 之后每次回来比对追番版本号,
///   期间在内嵌番剧页追/退过番才重拉(与首页追番 tab 同一套判据)。
/// </summary>
public partial class SubscriptionPage : UserControl, IRefreshable
{
    /// <summary>滚到底自动加载下一页的阈值(像素)。</summary>
    private const double AutoLoadThreshold = 600;

    /// <summary>
    /// 合集第一屏不足一屏时的主动补页上限。
    /// ★ 没有滚动条 ⇒ ScrollChanged 不会来, 不主动补就会停在第一页(与我的空间页同一道闸)。
    /// </summary>
    private const int MaxCollectionAutoFills = 3;

    private SubscriptionViewModel? _vm;
    private bool _initialized;
    private bool _syncingTab;
    private int _followVersion = -1;
    private int _collectionAutoFills;

    private SubscriptionViewModel? Vm => DataContext as SubscriptionViewModel;

    public SubscriptionPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => AttachVm(e.NewValue as SubscriptionViewModel);
        Loaded += OnLoaded;
        Unloaded += (_, _) => AttachVm(null);
        // 页面常驻 PageLayer、切回来时不一定再发 ScrollChanged —— 按当前滚动区补一次工具列状态
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) BtnTopRefresh.UpdateForOffset(CurrentScroll);
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachVm(Vm);
        if (Vm == null) return;

        if (!_initialized)
        {
            _initialized = true;
            _followVersion = BangumiFollowSignal.Version;
            await Vm.InitAsync();
            RequestCollectionAutoFill();
            return;
        }

        // 从内嵌番剧详情退回来时, 期间追/退过的番要立刻反映到当前 tab。
        if (_followVersion != BangumiFollowSignal.Version)
        {
            _followVersion = BangumiFollowSignal.Version;
            await Vm.RefreshAsync();
        }
    }

    // ------------------------------------------------------------ Tab 选中态

    private void AttachVm(SubscriptionViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null)
        {
            _vm.TabChanged -= OnVmTabChanged;
            _vm.ScrollToTopRequested -= OnScrollToTop;
        }
        _vm = vm;
        if (vm == null) return;
        vm.TabChanged += OnVmTabChanged;
        vm.ScrollToTopRequested += OnScrollToTop;
        // 页面可能是复用的, 先把选中态对齐到 VM 当前 Tab
        SetCheckedTab(vm.Tab);
    }

    private void OnVmTabChanged(SubscriptionTab tab) => SetCheckedTab(tab);

    private void SetCheckedTab(SubscriptionTab tab)
    {
        _syncingTab = true;
        try
        {
            TabBangumi.IsChecked = tab == SubscriptionTab.Bangumi;
            TabTeleplay.IsChecked = tab == SubscriptionTab.Teleplay;
            TabCollections.IsChecked = tab == SubscriptionTab.Collections;
        }
        finally
        {
            _syncingTab = false;
        }
        QueueTabIndicatorUpdate(animate: true);
    }

    // ------------------------------------------------------------ Tab 共享下划线
    //
    // 2026-11-28 用户要求"tab 切换底下的横线加上动画"。
    // 做法与首页 / 播放器简介评论 Tab 一致: 整行只有一条共享横条, 切 Tab 时把它的
    // X 与 Width 一起缓动到新标签 —— 不再让每个 Tab 模板各自瞬间点亮一条(那是硬切)。

    /// <summary>下划线滑动时长: 与首页 / 播放器一致。</summary>
    private const int TabIndicatorMs = 220;

    /// <summary>下划线还没有按真实布局摆过位。</summary>
    private bool _tabIndicatorPlaced;

    private void OnTabRowLoaded(object sender, RoutedEventArgs e)
        => QueueTabIndicatorUpdate(animate: false);

    private void OnTabRowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) QueueTabIndicatorUpdate(animate: false);
    }

    /// <summary>
    /// 标签文字宽度变了(如追番/追剧条数从无到有、选中态加粗): 下划线要跟着对齐。
    /// 用动画而不是硬切 —— 与切换 Tab 的观感一致。
    /// </summary>
    private void OnTabRowSizeChanged(object sender, SizeChangedEventArgs e)
        => QueueTabIndicatorUpdate(animate: true);

    /// <summary>
    /// 把更新排到本轮布局之后: 选中态会把字号改成 SemiBold, 且 Tab 文案里带条数
    /// (如「追番 12」), 事件触发时量到的可能还是旧宽度; 排到 Loaded 优先级后再摆才准。
    /// </summary>
    private void QueueTabIndicatorUpdate(bool animate)
    {
        if (!IsLoaded || !TabRow.IsLoaded)
        {
            _tabIndicatorPlaced = false;
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => UpdateTabIndicator(animate)));
    }

    private void UpdateTabIndicator(bool animate)
    {
        var target = TabCollections.IsChecked == true ? TabCollections
                   : TabBangumi.IsChecked == true ? TabBangumi
                   : TabTeleplay.IsChecked == true ? TabTeleplay
                   : null;
        if (target == null || target.ActualWidth <= 0 || TabRow.ActualWidth <= 0)
        {
            _tabIndicatorPlaced = false;
            return;
        }

        var to = target.TranslatePoint(new Point(0, 0), TabRow).X;
        var width = target.ActualWidth;

        if (!animate || !_tabIndicatorPlaced)
        {
            // 先清动画再写基础值: 残留的 HoldEnd 动画会让直接赋值不生效
            TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicator.BeginAnimation(WidthProperty, null);
            TabIndicatorShift.X = to;
            TabIndicator.Width = width;
            _tabIndicatorPlaced = true;
            return;
        }

        // 起点取横条**当前**值: 切到一半又点别的 Tab 时, 从半路接着滑, 不会先跳回旧位置。
        var duration = new Duration(TimeSpan.FromMilliseconds(TabIndicatorMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(TabIndicatorShift.X, to, duration) { EasingFunction = ease });
        TabIndicator.BeginAnimation(WidthProperty,
            new DoubleAnimation(TabIndicator.ActualWidth, width, duration) { EasingFunction = ease });
    }

    private void OnTabBangumiChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTab) return;
        _ = SwitchTabAsync(SubscriptionTab.Bangumi);
    }

    private void OnTabTeleplayChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTab) return;
        _ = SwitchTabAsync(SubscriptionTab.Teleplay);
    }

    private void OnTabCollectionsChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTab) return;
        _ = SwitchTabAsync(SubscriptionTab.Collections);
    }

    private async Task SwitchTabAsync(SubscriptionTab tab)
    {
        if (Vm == null) return;
        _collectionAutoFills = 0;
        await Vm.SwitchTabAsync(tab);
        if (tab == SubscriptionTab.Collections) RequestCollectionAutoFill();
        // 换 tab = 换 ScrollViewer, 工具列的二合一按钮按新滚动区的偏移重置
        BtnTopRefresh.UpdateForOffset(CurrentScroll);
    }

    // ------------------------------------------------------------ 滚动 / 加载

    private ScrollViewer? CurrentScroll => Vm?.Tab switch
    {
        SubscriptionTab.Bangumi => BangumiScroll,
        SubscriptionTab.Teleplay => TeleplayScroll,
        _ => CollectionsScroll,
    };

    private void OnScrollToTop() => CurrentScroll?.ScrollToTop();

    /// <summary>追番 / 追剧列表滚动: 只用来更新工具列的「刷新 ⇄ 回到顶部」状态。</summary>
    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer sv) BtnTopRefresh.UpdateForOffset(sv);
    }

    /// <summary>工具列二合一按钮: 顶部 = 刷新当前 tab, 下滑 = 回到顶部。</summary>
    private void OnTopRefreshClick(object sender, RoutedEventArgs e)
    {
        if (BtnTopRefresh.ShowsBackToTop)
        {
            CurrentScroll?.ScrollToTop();
            return;
        }

        Refresh();
    }

    /// <summary>合集 tab 滚到底追加下一页; 内容不足一屏时主动补。</summary>
    private void OnCollectionsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer) BtnTopRefresh.UpdateForOffset(scrollViewer);
        if (!IsLoaded || Vm == null || !Vm.IsCollectionsTab) return;

        // 缩放/分帧填充会连续发 ScrollChanged, 这一拍的高度不是稳定版式, 先跳过
        if (CardWall.IsResizing) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) return;

        var sv = e.OriginalSource as ScrollViewer;
        if (sv == null) return;

        if (sv.ScrollableHeight <= 0)
        {
            TryFillCollections();
            return;
        }
        if (sv.ScrollableHeight - sv.VerticalOffset <= AutoLoadThreshold)
            _ = Vm.LoadMoreCollectionsAsync();
    }

    private void RequestCollectionAutoFill()
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryFillCollections));

    private void TryFillCollections()
    {
        if (!IsLoaded || Vm == null || !Vm.IsCollectionsTab) return;
        if (CardWall.IsResizing) return;
        if (CollectionsScroll.ScrollableHeight > 0) return;
        if (_collectionAutoFills >= MaxCollectionAutoFills) return;
        _collectionAutoFills++;
        _ = Vm.LoadMoreCollectionsAsync();
    }

    // ------------------------------------------------------------ 卡片 / 导航

    private void OnCollectionCardOpenRequested(object sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpaceCollectionItem item })
            Svc.Navigate?.OpenSpaceCollection(item);
    }

    /// <summary>
    /// 返回"上一级": 目标与鼠标侧键 / Esc 一致 —— 有来源页回来源页, 没有才回「我的」,
    /// 见 MainViewModel.ResolveBackTarget。
    /// </summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(App.MainVm.ResolveBackTarget(PageKey.Subscription));

    public void Refresh()
    {
        _collectionAutoFills = 0;
        _ = Vm?.RefreshAsync();
    }
}
