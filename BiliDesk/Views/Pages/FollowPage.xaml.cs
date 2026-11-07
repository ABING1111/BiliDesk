using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「动态」页: 左栏 = 搜索 + 全部动态 + 关注 UP 列表(虚拟化 ListBox),
/// 右栏 = 选中对象的内容(默认动态卡也是虚拟化 ListBox, 卡片墙带上限)。
///
/// 2026-11-06 关注 2790+ 的性能重做:
///   · 左栏不再用 ScrollViewer + StackPanel 一次性创建所有行;
///   · 左右加载状态分开, 左栏翻页不再让右栏转圈;
///   · 滚动到底的自动翻页统一用 ScrollChanged 的 Extent/Viewport 像素值。
/// </summary>
public partial class FollowPage : UserControl
{
    /// <summary>
    /// 自动翻页阈值(单位 = **条目**, 不是像素)。
    /// ListBox 用默认的 item-based 虚拟化(CanContentScroll=True), 滚动距离按行数算;
    /// 设成像素值会在 50 条一页的左栏上永远触发/永远不触发。
    /// </summary>
    private const double UserAutoLoadThreshold = 8;
    private const double FeedAutoLoadThreshold = 4;

    /// <summary>看板轮询间隔: "关注的主播开播了"要能自己冒出来, 不能靠用户去点刷新。</summary>
    private static readonly TimeSpan LiveBoardPollInterval = TimeSpan.FromSeconds(60);

    private DispatcherTimer? _liveBoardTimer;

    /// <summary>
    /// 左栏本次左键手势是否从页面内按下(与 VideoCard 的 _pressInside 同款)。
    /// 窗口最大化/页面重排后, 一个没有配对 Down 的 Up 可能落到某一行上;
    /// 没有这道闸会误触发"选中 UP"。
    /// </summary>
    private bool _leftPressSeen;

    /// <summary>
    /// 「全部动态」底衬与 UP 行对齐的同步回调。
    /// ★ 必须是**命名方法**: OnLoaded 会在页面每次重新可见时触发(页面实例被 MainWindow
    ///   缓存), 以前这里用 lambda 订阅、从不退订, 每进一次页面就叠一层处理器。
    /// </summary>
    private void OnUsersListSizeChanged(object sender, SizeChangedEventArgs e) => SyncAllDynamicEntry();

    public FollowPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // 屏蔽把动态流筛空后补下一页(否则会停在一片空白上)
        FilterService.Instance.Changed += OnFilterApplied;

        // 搜索框占位符显隐(2026-11-07): 由 TextChanged **直接**控制, 不走绑定。
        // ★ 为什么不用绑定: 先后试过 UserSearchText(string)+InvStrToVis 与
        //   HasUserSearch(bool)+InverseBool 两条路线, 源码与 BAML 均正确, 用户实测仍复现
        //   "输入文字后占位符不消失"。绑定失败是静默的(只写 Output 窗口), 与其继续猜,
        //   不如换成与首页搜索框同款(RefreshSearchPlaceholder)的确定性方案:
        //   TextChanged 是输入/删除/粘贴/清空/代码赋值**必然**会走的事件, 零依赖。
        // ★ 判据用 IsNullOrWhiteSpace: 纯空格不算"有内容"(与 VM.HasUserSearch 同语义)。
        UserSearchBox.TextChanged += (_, _) => RefreshUserSearchPlaceholder();
        RefreshUserSearchPlaceholder();   // 初始状态(页面被缓存复用时输入框可能已有文字)
    }

    /// <summary>
    /// 同步左栏搜索框占位符的显隐: 有非空白内容就藏, 否则显示。
    /// 与首页 <c>RefreshSearchPlaceholder</c> 同一套思路 —— 只是这里不需要淡入淡出,
    /// 小输入框里瞬间切换更干脆。
    /// </summary>
    private void RefreshUserSearchPlaceholder()
    {
        if (UserSearchPlaceholder == null) return;
        var empty = string.IsNullOrWhiteSpace(UserSearchBox.Text);
        UserSearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm)
        {
            // 动态流与"关注的主播正在直播"都纳入全局屏蔽(与首页/收藏/历史同一套条件)
            FilterService.Instance.Attach(vm.FeedItems);
            FilterService.Instance.Attach(vm.WallItems);
            FilterService.Instance.Attach(vm.LiveNow);

            // 看板默认折叠: 页面实例会被 MainWindow 缓存, 每次进入都归位到收起。
            vm.LiveBoardOpen = false;
            UpdateLayoutChips(vm);
            // 占位符显隐再同步一次: 页面被缓存复用, VM 侧可能在页面不可见时清过搜索词
            // (ClearUserSearch), 回来时输入框文字已变但 TextChanged 未必触发过。
            RefreshUserSearchPlaceholder();
            // 「全部动态」底衬要与 UP 行对齐: 在列表布局完成后量一次。
            // 先 -= 再 += : 页面实例被缓存复用, Loaded 每次进入都会响, 不能叠处理器。
            UsersList.SizeChanged -= OnUsersListSizeChanged;
            UsersList.SizeChanged += OnUsersListSizeChanged;
            await vm.InitAsync();
            // ★ 延迟到 Loaded 优先级: InitAsync 返回时 Users 刚填充、虚拟化列表还没走完
            //   布局, 容器尚未生成, 立刻量是空跑; 排一个布局完成后的事件再量才量得到。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncAllDynamicEntry));
            StartLivePolling();
        }
    }

    /// <summary>屏蔽条件落地后, 等布局量完再判断要不要补一页动态</summary>
    private void OnFilterApplied()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
        {
            if (!IsLoaded || DataContext is not FollowViewModel vm) return;
            var sv = GetVisibleFeedScroll();
            if (sv == null) return;
            var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
            var contentShort = sv.ScrollableHeight <= 0;
            if (distanceToBottom > FeedAutoLoadThreshold && !contentShort) return;
            await vm.LoadMoreFeedAsync();
        }));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面实例不会跟着导航销毁(MainWindow 缓存), 定时器必须自己停。
        _liveBoardTimer?.Stop();
    }

    private void StartLivePolling()
    {
        _liveBoardTimer ??= new DispatcherTimer { Interval = LiveBoardPollInterval };
        _liveBoardTimer.Tick -= OnLiveBoardTick;
        _liveBoardTimer.Tick += OnLiveBoardTick;
        _liveBoardTimer.Start();
    }

    private void OnLiveBoardTick(object? sender, EventArgs e)
    {
        if (!IsPageActive()) return;
        if (DataContext is FollowViewModel vm) _ = vm.RefreshLiveBoardAsync(showLoading: false);
    }

    /// <summary>最小化到托盘 / 切到别的页面时不再轮询; 重新可见后下一拍自然恢复。</summary>
    private bool IsPageActive()
    {
        var win = Window.GetWindow(this);
        return IsVisible && win != null && win.IsVisible &&
               win.WindowState != WindowState.Minimized;
    }

    // ------------------------------------------------------------ 右栏排版切换

    private void OnLayoutCardsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm) { vm.UseCardWall = false; UpdateLayoutChips(vm); }
    }

    private void OnLayoutWallClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm) { vm.UseCardWall = true; UpdateLayoutChips(vm); }
    }

    /// <summary>
    /// 同步两枚 chip 的选中态。★ 只在这里写 Tag(与播放页评论排序同一套约定: 字符串 "on"/"off")。
    /// </summary>
    private void UpdateLayoutChips(FollowViewModel vm)
    {
        if (BtnLayoutCards == null || BtnLayoutWall == null) return;
        BtnLayoutCards.Tag = vm.UseCardWall ? "off" : "on";
        BtnLayoutWall.Tag = vm.UseCardWall ? "on" : "off";
    }

    // ------------------------------------------------------------ 选择

    /// <summary>
    /// 记录"左栏有一次真实按下"。配合 Up 的 TakeLeftPress 使用, 防止窗口最大化重排后
    /// 一个没有配对 Down 的 Up 误选中 UP。
    /// </summary>
    private void OnLeftColumnPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) _leftPressSeen = true;
    }

    /// <summary>Up 冒泡到左栏时清掉标记(有效点击已在行处理器里消费)。</summary>
    private void OnLeftColumnUp(object sender, MouseButtonEventArgs e) => _leftPressSeen = false;

    private bool TakeLeftPress()
    {
        if (!_leftPressSeen) return false;
        _leftPressSeen = false;
        return true;
    }

    /// <summary>点「全部动态」: 右栏显示关注 UP 近期的视频投稿</summary>
    private void OnSelectAll(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakeLeftPress()) return;
        if (DataContext is FollowViewModel vm) _ = vm.SelectAllAsync();
    }

    /// <summary>点某个 UP: 右栏只显示 TA 的投稿视频</summary>
    private void OnSelectUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakeLeftPress()) return;
        if (((FrameworkElement)sender).DataContext is not FollowUser u) return;
        if (DataContext is FollowViewModel vm) _ = vm.SelectUpAsync(u);
    }

    private void OnClearUserSearch(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm) vm.ClearUserSearchCommand.Execute(null);
    }

    /// <summary>
    /// 让「全部动态」入口的底衬与下面 UP 行的底衬**像素级对齐**(2026-11-06)。
    ///
    /// ★ 为什么需要代码来同步, 而不是写死一个 Margin:
    ///   「全部动态」在 UsersList **外面**, UP 行在 UsersList **里面**。
    ///   ListBox 的内容区宽度要扣掉垂直滚动条(实测约 10px), 所以:
    ///     · UP 行底衬的右缘 = 列表右缘 - 滚动条宽;
    ///     · 「全部动态」底衬的右缘 = 列表右缘(不受滚动条影响)。
    ///   两块粉色底衬因此长短不一 —— 用户看着就是"上下两行没对齐"。
    ///   而且滚动条出没出现会变(内容不足时没有), 写死的数值必然对不上。
    /// ⇒ 这里直接量出**第一行 UP 底衬**的左缘与宽度, 套到「全部动态」上, 两者就完全重合。
    /// </summary>
    private void SyncAllDynamicEntry()
    {
        if (AllDynamicEntry == null || UsersList == null) return;
        var container = UsersList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        if (container == null) return;

        // 第一行 UP 的底衬(圆角 10 的那个 Border)
        var rowBorder = FindRowBorder(container);
        if (rowBorder == null) return;

        var p = rowBorder.TransformToAncestor(UsersList).Transform(new Point(0, 0));
        var width = rowBorder.ActualWidth;
        if (width <= 0) return;

        // 「全部动态」的坐标是相对左栏 Grid 的, UP 行底衬是相对 UsersList 的;
        // UsersList 相对左栏 Grid 有 Margin="8,0,8,0" 的左内边距 8。
        var listOffset = UsersList.TransformToAncestor(AllDynamicEntry.Parent as FrameworkElement ?? AllDynamicEntry)
            .Transform(new Point(0, 0)).X;

        AllDynamicEntry.Margin = new Thickness(listOffset + p.X, 4, 0, 6);
        AllDynamicEntry.Width = width;
    }

    /// <summary>在容器里找行底衬(圆角 10、且是那一行的直接内容)。</summary>
    private static Border? FindRowBorder(FrameworkElement container)
    {
        Border? found = null;
        WalkVisual(container, d =>
        {
            if (found != null) return;
            if (d is Border b && b.CornerRadius.TopLeft == 10 && b.ActualWidth > 0) found = b;
        });
        return found;
    }

    private static void WalkVisual(DependencyObject root, Action<DependencyObject> visit)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            visit(child);
            WalkVisual(child, visit);
        }
    }

    /// <summary>右下角工具列「刷新」: 刷新后把右栏滚回顶部(与首页刷新同一套体验)。</summary>
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FollowViewModel vm) return;
        await vm.RefreshAsync();
        var sv = GetVisibleFeedScroll();
        if (sv != null) sv.ScrollToTop();
    }

    // ------------------------------------------------------------ 关注直播看板

    private void OnToggleLiveBoard(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FollowViewModel vm) vm.LiveBoardOpen = !vm.LiveBoardOpen;
    }

    private void OnLiveItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (((FrameworkElement)sender).DataContext is not VideoItem item) return;
        if (DataContext is FollowViewModel vm) vm.OpenLive(item);
    }

    // ------------------------------------------------------------ 滚动加载

    /// <summary>
    /// 取滚动事件里的 ScrollViewer。ListBox 的 ScrollChanged 是附加事件,
    /// OriginalSource 通常就是内部 ScrollViewer; 取不到时再从 ListBox 里找。
    /// </summary>
    private static ScrollViewer? ScrollViewerOf(RoutedEventArgs e, object? sender)
    {
        if (e.OriginalSource is ScrollViewer sv) return sv;
        return sender is DependencyObject d ? FindChild<ScrollViewer>(d) : null;
    }

    /// <summary>右栏: 滚动到底自动补下一页(全部动态 = 游标; 单 UP = 页码)</summary>
    private void FeedScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not FollowViewModel vm) return;

        var sv = ScrollViewerOf(e, sender);
        if (sv != null)
        {
            // ★ 记录"用户当前是否停在列表底部"(2026-11-06):
            //   「已经到底啦」只在真正滑到底时出现, 往上滚就消失 ——
            //   它是"到底了"的瞬时反馈, 不该变成常驻横幅。
            var atBottom = sv.ScrollableHeight <= 0 ||
                           sv.ScrollableHeight - sv.VerticalOffset <= 1;
            vm.SetAtFeedBottom(atBottom);
        }

        // 卡片墙有 300 条上限: 到上限后不再自动翻页, 底部会提示切换动态卡。
        if (vm.UseCardWall && vm.WallItems.Count >= 300) return;
        if (vm.LoadingMore || vm.LoadMoreError || !vm.HasMore) return;

        if (sv == null || sv.ScrollableHeight <= 0) return;
        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > FeedAutoLoadThreshold) return;

        // ★★ 2026-11-06 用户报"右栏最下面被一个长方形画幅遮挡住了": 那是**被裁掉的半张卡** ——
        //   ListBox 滚到底时按**像素**停, 最后一张动态卡只露出顶部一行(头像+标题),
        //   而那张卡的白色卡片底色横贯整个右栏宽度, 看着就像"下面压着一个长方形画幅"。
        //   这里在触底时把最后一项**完整滚进视口**(按项对齐), 半张卡的现象就没了。
        ScrollLastFeedItemIntoView(sv);
        _ = vm.LoadMoreFeedAsync();
    }

    /// <summary>
    /// 把列表最后一项完整滚进视口(按**项**对齐而不是按像素)。
    ///
    /// ★ 为什么必须显式做: ListBox 的 ScrollViewer 滚到底是按像素停的, 最后一个
    ///   容器的高度往往超过剩余空间, 于是它被裁掉一截 —— 动态卡被裁掉的那截正好是
    ///   它的卡片底色, 横贯整宽, 看着像一块"长方形画幅"。
    /// ★ 只在真的到底时做: 平时滚动不该被这个打断。
    /// </summary>
    private void ScrollLastFeedItemIntoView(ScrollViewer sv)
    {
        var list = (DataContext as FollowViewModel)?.UseCardWall == true
            ? (DependencyObject?)FeedWallList
            : FeedList;
        if (list is not ListBox lb || lb.Items.Count == 0) return;

        var last = lb.Items[lb.Items.Count - 1];
        if (last == null) return;
        try
        {
            lb.ScrollIntoView(last);
        }
        catch
        {
            // ScrollIntoView 在容器尚未布局时可能抛异常 —— 忽略即可(下一次滚动会再试)。
        }
    }

    /// <summary>左栏: UP 列表滚到底自动补(一页 50 个)。搜索结果显示时不平铺翻页。</summary>
    private void UpListScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not FollowViewModel vm) return;
        if (vm.HasUserSearch || vm.LoadingMoreUsers || vm.HasUsersLoadError || !vm.HasMoreUsers) return;
        if (vm.Users.Count == 0) return;

        var sv = ScrollViewerOf(e, sender);
        if (sv == null || sv.ScrollableHeight <= 0) return;
        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > UserAutoLoadThreshold) return;
        _ = vm.LoadMoreAsync();
    }

    private ScrollViewer? GetVisibleFeedScroll()
    {
        var vm = DataContext as FollowViewModel;
        var list = vm?.UseCardWall == true ? (DependencyObject?)FeedWallList : FeedList;
        return list == null ? null : FindChild<ScrollViewer>(list);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var nested = FindChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    // ------------------------------------------------------------ 左栏条目的右键操作

    private void OnVisitClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
        }
    }

    /// <summary>取消关注该 UP 主, 成功后从列表移除(移除的是选中项时自动回到全部动态)</summary>
    private async void OnUnfollowClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.DataContext is not FollowUser u) return;
        if (DataContext is not FollowViewModel vm) return;

        var (ok, err) = await Svc.Api.FollowUpAsync(u.Mid, follow: false);
        if (ok)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => vm.RemoveUser(u));
            Svc.Toast.Show($"已取消关注 {u.Name}");
        }
        else
        {
            Svc.Toast.Show("取消关注失败: " + (err ?? "未知错误"));
        }
    }

    /// <summary>右键头像 → 预览大图</summary>
    private void OnViewAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            MediaActions.ShowImage(u.Face, u.Name + " 的头像", Window.GetWindow(this));
        }
    }

    /// <summary>右键头像 → 下载到本地</summary>
    private async void OnDownloadAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            await MediaActions.SaveImageAsync(u.Face, u.Name + "_头像");
        }
    }
}
