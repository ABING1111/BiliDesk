using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public UserSpacePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    public UserSpacePage(long mid)
    {
        InitializeComponent();
        DataContext = new UserSpaceViewModel();
        if (DataContext is UserSpaceViewModel vm) _ = vm.InitAsync(mid);
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
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
        if (_vm != null) _vm.ScrollToTopRequested -= OnScrollToTopRequested;
        _vm = vm;
        if (vm == null) return;
        vm.ScrollToTopRequested += OnScrollToTopRequested;
        FilterService.Instance.Attach(vm.Videos, isActive: () => IsVisible);
        FilterService.Instance.Attach(vm.ImageDynamics, isActive: () => IsVisible);
        UpdateTabs(vm);
        ResetHeaderState();
    }

    private void UpdateTabs(UserSpaceViewModel vm)
    {
        TabVideos.Tag = vm.IsVideosTab ? "on" : "off";
        TabImages.Tag = vm.IsImagesTab ? "on" : "off";
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

    private void OnDynamicCardDetailRequested(object sender, DynamicDetailRequestedEventArgs e)
    {
        if (e.Item == null) return;
        SvcWindow.Main?.ShowDynamicDetail(e.Item, e.FocusComments, e.Source);
    }

    // ------------------------------------------------------------ 滚动时自动收起 / 展开头部

    /// <summary>
    /// 顶部信息区(头像/简介/数据/Tab)的展开高度。首次量到就记住,
    /// 收起时 Height 归 0, 展开时还原成这个值。
    /// </summary>
    private double _headerExpandedHeight = double.NaN;

    /// <summary>头部当前是否已收起</summary>
    private bool _headerCollapsed;

    /// <summary>
    /// 收起头部**至少要先滚过**"头部高度 + 这么多"。
    ///
    /// ★★★ 2026-11-19 修"往下滚动卡片上下跳然后被收起"(1.7.6 之前的版本不弹):
    ///   光看"可滚高度够不够"挡不住弹跳。真正的条件在**偏移轴**上:
    ///   触发收起要滚过 ~211px, 触发展开只要回到 8px 以内 —— 两条判据在偏移轴上
    ///   相隔两百多像素, 物理上不可能在同一个位置来回触发。
    ///   反过来说, 只要允许"滚 9px 就收起", 收起会把视口撑大、把偏移夹回顶部附近,
    ///   于是立刻满足"回到顶部 ⇒ 展开", 而展开又把可滚高度长回去 —— 卡片跟着一帧一帧
    ///   上下弹, 这就是用户看到的抽搐。
    /// </summary>
    private const double HeaderCollapseSlack = 40;

    /// <summary>
    /// 收起之后**至少还要能滚出这么多**才允许收起。
    ///
    /// ★★★ 2026-11-19 另一道保险(探针 win=714/718 实测反向 4~7 次):
    ///   收起头部会让 ScrollViewer 的视口变大 H, 可滚高度随之少 H, 用户还会被夹回
    ///   offset ≈ (可滚 − H)。这个余量一旦落进 <see cref="HeaderExpandAtTop"/>(8px) 里,
    ///   收起立刻又满足"回到顶部 ⇒ 展开" —— 卡片墙跟着反复重排, 用户看到的就是抽搐。
    ///   ⇒ 余量必须**明显大于** HeaderExpandAtTop, 把这两条判据彻底分开。
    /// </summary>
    private const double MinScrollAfterCollapse = 32;

    /// <summary>
    /// 顶部信息区只在**滚到最顶端**时才展示(2026-11-14 用户要求):
    /// 一旦往下滚开(超过 <see cref="HeaderExpandAtTop"/>), 立刻收起;
    /// 只有滑回顶部那一点点范围内才重新展开。中间任何位置都保持收起。
    /// </summary>
    private const double HeaderExpandAtTop = 8;

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        => HandleHeaderAutoHide(e.VerticalOffset, e.ExtentHeight, e.ViewportHeight);

    private void OnImageListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        HandleHeaderAutoHide(e.VerticalOffset, e.ExtentHeight, e.ViewportHeight);
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
            if (_imagesAutoFillGuard >= 6) return;
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

    /// <summary>
    /// 顶部信息区只在**滚到最顶端**时展示:
    ///   偏移 ≤ <see cref="HeaderExpandAtTop"/> → 展开;
    ///   其余位置(只要还能滚) → 收起。
    /// ★ 不再按"上滑/下滑方向"判断(用户 2026-11-14 明确要求"滑到最顶端的时候再展示"),
    ///   所以上滑到中间位置也是收起的, 只有真正回到顶部才重新出现。
    ///
    /// ★★★ 2026-11-19 修"往下滑以后再上滑, tab 呼不出来":
    ///   头部在 Grid 行 0、**不在** ScrollViewer 里, 所以把它收起会让 ScrollViewer 的
    ///   **视口变大**, 可滚高度反而**变小**。老代码在展开之前先判 `extent - viewport <= 1`
    ///   ("内容不够一屏就不折腾"), 结果内容只比一屏多一点点时(探针实测: 12 条投稿,
    ///   展开时头部 171px、可滚 9px), 一收起可滚就变成 0 —— 页面不再可滚、不会再有
    ///   ScrollChanged, 那句 return 就把头部钉死在收起状态, 怎么滑都回不来。
    ///   ⇒ 展开这条路径必须**无条件**执行; "够不够一屏"只用来决定**要不要收起**。
    /// </summary>
    private void HandleHeaderAutoHide(double offset, double extent, double viewport)
    {
        if (HeaderHost == null) return;

        // 第一次滚动时量出展开高度(此后收起/展开都用它还原)。
        // ★ 量到 0 说明这一帧还没布局完, 保持 NaN 下次再量 —— 直接把 0 记下来会让
        //   头部永远收不起来(SetHeaderCollapsed 里 _headerExpandedHeight <= 0 会 early return)。
        if (double.IsNaN(_headerExpandedHeight))
        {
            var measured = HeaderHost.ActualHeight;
            if (measured <= 0) return;
            _headerExpandedHeight = measured;
        }

        // ---- 回到顶部: 无条件展开 ----
        if (offset <= HeaderExpandAtTop)
        {
            SetHeaderCollapsed(false);
            return;
        }

        // ---- 往下滚开了: 两道门槛都过了才收起 ----
        // ① 偏移轴: 已经滚过"头部高度 + 余量"。没滚过去就收起, 等于在屏幕中间把内容
        //    猛地往上抽一截, 接着又被夹回顶部、又展开 —— 卡片就上下弹个不停。
        if (offset <= _headerExpandedHeight + HeaderCollapseSlack) return;

        // ② 可滚高度: 收起之后仍然有得滚, 否则视口变大后整页刚好装下, 立刻又满足"回到顶部"。
        if (extent - viewport <= _headerExpandedHeight + MinScrollAfterCollapse) return;

        SetHeaderCollapsed(true);
    }

    private void SetHeaderCollapsed(bool collapse)
    {
        if (_headerCollapsed == collapse) return;
        _headerCollapsed = collapse;
        if (double.IsNaN(_headerExpandedHeight) || _headerExpandedHeight <= 0) return;

        var target = collapse ? 0 : _headerExpandedHeight;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(
            HeaderHost.ActualHeight, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        // 动画结束后把基准值落到目标值, 下次从正确高度起算
        anim.Completed += (_, _) =>
        {
            HeaderHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            HeaderHost.Height = target;
        };
        HeaderHost.BeginAnimation(FrameworkElement.HeightProperty, anim);
    }

    /// <summary>切 Tab / 换 UP 时把头部恢复展开, 否则新内容一进来就被裁掉一截。</summary>
    private void ResetHeaderState()
    {
        _headerCollapsed = false;
        if (HeaderHost == null) return;
        HeaderHost.BeginAnimation(FrameworkElement.HeightProperty, null);
        HeaderHost.Height = double.NaN;   // Auto
        _headerExpandedHeight = double.NaN;
    }

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
