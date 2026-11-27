using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using BiliDesk.Views.Controls;

namespace BiliDesk.Views.Pages;

public partial class UserSpacePage : UserControl
{
    /// <summary>当前订阅的 VM(DataContext 可能被换掉, 必须自己记一份以便退订事件)</summary>
    private UserSpaceViewModel? _vm;

    /// <summary>顶部信息区的自动收起/展开(合集详情页共用同一套逻辑, 见 Helpers/HeaderAutoHide)</summary>
    private readonly HeaderAutoHideController _headerAutoHide;

    public UserSpacePage()
    {
        InitializeComponent();
        _headerAutoHide = new HeaderAutoHideController(HeaderHost);
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    public UserSpacePage(long mid) : this()
    {
        DataContext = new UserSpaceViewModel();
        if (DataContext is UserSpaceViewModel vm) _ = vm.InitAsync(mid);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserSpaceViewModel vm)
        {
            AttachVm(vm);
            if (vm.Mid > 0) await vm.InitAsync(vm.Mid);
            else if (Tag is long mid) await vm.InitAsync(mid);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AttachVm(e.NewValue as UserSpaceViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => AttachVm(null);

    /// <summary>
    /// 订阅 VM 的事件 / 把投稿列表纳入全局屏蔽。
    /// ★ FilterService 只是在集合的默认视图上装一个 Filter 谓词, 翻页时 Clear+Add
    ///   会自动重新过滤, 不需要在页面里再做"补页"。
    /// </summary>
    private void AttachVm(UserSpaceViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null)
        {
            _vm.ScrollToTopRequested -= OnScrollToTopRequested;
            _vm.ImageSearchChanged -= OnImageSearchChanged;
        }
        _vm = vm;
        if (vm == null) return;
        vm.ScrollToTopRequested += OnScrollToTopRequested;
        vm.ImageSearchChanged += OnImageSearchChanged;
        FilterService.Instance.Attach(vm.Videos, isActive: () => IsVisible);
        // 图文的搜索是页内筛选: 与全局屏蔽取"与", 谓词读的是 VM 里实时的词条
        FilterService.Instance.Attach(vm.ImageDynamics, extra: vm.MatchesImageSearch,
            isActive: () => IsVisible);
        UpdateTabs(vm);
        ResetHeaderState();
    }

    private void UpdateTabs(UserSpaceViewModel vm)
    {
        TabVideos.Tag = vm.IsVideosTab ? "on" : "off";
        TabImages.Tag = vm.IsImagesTab ? "on" : "off";
        TabCollections.Tag = vm.IsCollectionsTab ? "on" : "off";
        QueueTabIndicatorUpdate(animate: true);
    }

    // ------------------------------------------------------------ Tab 共享下划线
    //
    // 2026-11-28 用户要求"tab 切换底下的横线加上动画"。
    // 与首页 / 播放器简介评论 Tab 同一套做法: 线上只有**一条**共享 Border,
    // 切 Tab 时把它的 X 与 Width 一起缓动到新标签 —— 而不是每个按钮各画一条、
    // 由 Tag 驱动瞬间换位(那样是硬切, 没有"滑过去"的观感)。

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
    /// 标签文字宽度变了(如订阅数从无到有、选中态加粗): 下划线要跟着对齐。
    /// 用动画而不是硬切 —— 与切换 Tab 的观感一致。
    /// </summary>
    private void OnTabRowSizeChanged(object sender, SizeChangedEventArgs e)
        => QueueTabIndicatorUpdate(animate: true);

    /// <summary>
    /// 把更新排到本轮布局之后: 选中态会把字号改成 SemiBold, 事件触发时量到的还是旧宽度,
    /// 立刻摆线会短一截(与播放器 UpdateContentTabIndicator 同一条经验)。
    /// </summary>
    private void QueueTabIndicatorUpdate(bool animate)
    {
        if (!IsLoaded || !TabRow.IsLoaded)
        {
            _tabIndicatorPlaced = false;
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => MoveTabIndicator(animate)));
    }

    /// <summary>
    /// 把共享下划线挪到当前 Tab 下面。
    /// ★ 位置取标签的实测坐标(TransformPoint): 三个标签文字长短不同, 靠固定间距推不出来。
    /// ★ 左右各内缩 6px, 与原来模板里的 Margin="6,2,6,0" 对齐, 静止时观感不变。
    /// </summary>
    private void MoveTabIndicator(bool animate)
    {
        var target = TabCollections.Tag as string == "on" ? TabCollections
                   : TabImages.Tag as string == "on" ? TabImages
                   : TabVideos;
        if (target == null || target.ActualWidth <= 0 || TabRow.ActualWidth <= 0)
        {
            _tabIndicatorPlaced = false;
            return;
        }

        const double inset = 6;
        var to = target.TranslatePoint(new Point(0, 0), TabRow).X + inset;
        var width = Math.Max(0, target.ActualWidth - inset * 2);

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

        // 起点取横条**当前**值: 动画没跑完就连点另一项时, 从半路接着滑, 不会先跳回旧位置。
        var duration = new Duration(TimeSpan.FromMilliseconds(TabIndicatorMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(TabIndicatorShift.X, to, duration) { EasingFunction = ease });
        TabIndicator.BeginAnimation(WidthProperty,
            new DoubleAnimation(TabIndicator.ActualWidth, width, duration) { EasingFunction = ease });
    }

    private void OnVideosTabClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.ShowVideosTab();
        UpdateTabs(_vm);
        ResetHeaderState();
    }

    private void OnImagesTabClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        ResetHeaderState();
        _ = ShowImagesAsync();
    }

    private async System.Threading.Tasks.Task ShowImagesAsync()
    {
        if (_vm == null) return;
        var task = _vm.ShowImagesTabAsync();
        UpdateTabs(_vm);
        await task;
        UpdateTabs(_vm);
        // 首屏可能装不满一屏(没有滚动条 ⇒ 永远等不到 ScrollChanged): 主动补到够一屏
        _imagesAutoFillGuard = 0;
        ScheduleImagesAutoLoad();
    }

    private void OnImagesRetryClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _ = ShowImagesAsync();
    }

    // ------------------------------------------------------------ 合集和系列 Tab

    private void OnCollectionsTabClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        ResetHeaderState();
        _ = ShowCollectionsAsync();
    }

    private async System.Threading.Tasks.Task ShowCollectionsAsync()
    {
        if (_vm == null) return;
        var task = _vm.ShowCollectionsTabAsync();
        UpdateTabs(_vm);
        await task;
        UpdateTabs(_vm);
    }

    private void OnCollectionsRetryClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _ = ShowCollectionsAsync();
    }

    /// <summary>点合集/系列卡片 → 打开详情页(合集/系列共用同一个页面, 由 item.Kind 决定接口)。</summary>
    private void OnCollectionCardOpenRequested(object? sender, EventArgs e)
    {
        if (sender is not SpaceCollectionCard card) return;
        if (card.DataContext is not SpaceCollectionItem item) return;
        Svc.Navigate?.OpenSpaceCollection(item, _vm?.Name ?? "");
    }

    private void OnCollectionsScrollChanged(object sender, ScrollChangedEventArgs e)
        => _headerAutoHide.HandleScroll(e.VerticalOffset, e.ExtentHeight, e.ViewportHeight);

    private void OnDynamicCardDetailRequested(object sender, DynamicDetailRequestedEventArgs e)
    {
        if (e.Item == null) return;
        SvcWindow.Main?.ShowDynamicDetail(e.Item, e.FocusComments, e.Source);
    }

    // ------------------------------------------------------------ 滚动时自动收起 / 展开头部
    // 判定与动画在 Helpers/HeaderAutoHide(合集详情页共用同一套阈值, 见那里关于"卡片上下跳"的说明)

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        => _headerAutoHide.HandleScroll(e.VerticalOffset, e.ExtentHeight, e.ViewportHeight);

    private void OnImageListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _headerAutoHide.HandleScroll(e.VerticalOffset, e.ExtentHeight, e.ViewportHeight);
        ScheduleImagesAutoLoad();
    }

    /// <summary>内容不满一屏时连续补页的额度(防止接口一直回空页时无限自补)。</summary>
    private int _imagesAutoFillGuard;

    /// <summary>
    /// 图文 Tab 的无限滚动: 滚到接近底部、或内容还装不满一屏时自动补下一页。
    /// ★ 用 Dispatcher 排到布局之后: ScrollChanged 里 Extent/Viewport 可能还没落定,
    ///   当场判距离会误判成"已经到底"。
    /// </summary>
    private void ScheduleImagesAutoLoad()
    {
        if (_imagesAutoLoadPending) return;
        _imagesAutoLoadPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _imagesAutoLoadPending = false;
            TryAutoLoadImages();
        }), DispatcherPriority.Background);
    }

    private bool _imagesAutoLoadPending;

    private void TryAutoLoadImages()
    {
        if (DataContext is not UserSpaceViewModel vm || !vm.IsImagesTab) return;
        if (vm.ImagesLoading || vm.ImagesLoadingMore || !vm.ImagesHasMore) return;
        if (ImageList == null) return;

        var sv = FindScrollViewer(ImageList);
        if (sv == null) return;

        // 内容还不够一屏: 没有滚动条, 也就不会再有 ScrollChanged —— 主动补
        if (sv.ScrollableHeight <= 0)
        {
            // ★ 搜索态把自动补页额度收紧: 没命中时一路扫下去代价不小,
            //   补 3 轮还没结果就交给空态里的「继续查找」让用户自己决定。
            var cap = vm.HasSearch ? 3 : 6;
            if (_imagesAutoFillGuard >= cap) return;
            _imagesAutoFillGuard++;
            _ = LoadMoreImagesAndRecheckAsync(vm);
            return;
        }

        var distance = sv.ScrollableHeight - sv.VerticalOffset;
        if (distance > 600) return;
        _imagesAutoFillGuard = 0;
        _ = LoadMoreImagesAndRecheckAsync(vm);
    }

    private async System.Threading.Tasks.Task LoadMoreImagesAndRecheckAsync(UserSpaceViewModel vm)
    {
        await vm.LoadMoreImagesAsync();
        ScheduleImagesAutoLoad();
    }

    /// <summary>
    /// 图文搜索词变了: 重算补页额度并继续往后翻。
    /// ★ 搜索是本地筛选, 命中太少时列表可能"不满一屏"甚至空掉 —— 没有滚动条就不会有
    ///   ScrollChanged, 必须主动补(与首次进 Tab 同一条路)。
    /// </summary>
    private void OnImageSearchChanged()
    {
        _imagesAutoFillGuard = 0;
        ScheduleImagesAutoLoad();
    }

    /// <summary>图文搜索空态里的「继续查找」: 清掉自动补页额度, 再往后加载一页。</summary>
    private void OnImageSearchLoadMoreClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _imagesAutoFillGuard = 0;
        _ = LoadMoreImagesAndRecheckAsync(_vm);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var nested = FindScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>切 Tab / 换 UP / 重新进场时把头部恢复展开, 否则新内容一进来就被裁掉一截。</summary>
    private void ResetHeaderState() => _headerAutoHide.Reset();

    /// <summary>翻页成功后把列表滚回顶部 —— 否则用户点了第 2 页, 眼前还停在上一页的底部。</summary>
    private void OnScrollToTopRequested()
        => Dispatcher.BeginInvoke(new Action(() => ListScroll?.ScrollToTop()),
            DispatcherPriority.Loaded);

    // ------------------------------------------------------------ 统计数字可点(2026-11-18, issue #90)

    /// <summary>「关注数」→ 「我的空间」·关注(看的是**这个 UP** 的关注列表)。</summary>
    private void OnOpenFollowingsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserSpaceViewModel vm || vm.Mid <= 0) return;
        Svc.Navigate?.OpenUserCenter(vm.Mid, vm.Name, vm.Face, vm.IsSelf,
            ViewModels.UserCenterTab.Followings);
    }

    /// <summary>「粉丝数」→ 「我的空间」·粉丝。</summary>
    private void OnOpenFollowersClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserSpaceViewModel vm || vm.Mid <= 0) return;
        Svc.Navigate?.OpenUserCenter(vm.Mid, vm.Name, vm.Face, vm.IsSelf,
            ViewModels.UserCenterTab.Followers);
    }

    // ------------------------------------------------------------ 头像右键菜单

    private void OnViewAvatar(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserSpaceViewModel vm) return;
        MediaActions.ShowImage(vm.Face, vm.Name + " 的头像", Window.GetWindow(this));
    }

    private async void OnDownloadAvatar(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserSpaceViewModel vm) return;
        await MediaActions.SaveImageAsync(vm.Face, vm.Name + "_头像");
    }
}
