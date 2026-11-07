using System;
using System.Threading.Tasks;
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
    /// 左栏 UP 列表的自动翻页阈值(单位 = **条目**, 不是像素)。
    /// 这个列表仍是默认的 item-based 虚拟化(CanContentScroll=True), 滚动距离按行数算;
    /// 设成像素值会在 50 条一页的左栏上永远触发/永远不触发。
    /// </summary>
    private const double UserAutoLoadThreshold = 8;

    /// <summary>
    /// 右栏动态流的自动翻页阈值(单位 = **像素**)。
    /// ★ 2026-11-07 起两条分支都是像素滚: 动态卡列表设了 ScrollUnit=Pixel(为了让全局
    ///   滚轮平滑生效), 卡片墙本来就是 CardWallPanel 的像素滚动 —— 统一用像素。
    ///   800px 大致等于原来"还差三四张卡就开始预取"的时机。
    /// </summary>
    private const double FeedAutoLoadThreshold = 800;

    /// <summary>
    /// "把最后一张卡按项对齐"的触发距离(像素): 只有真的触底才做。
    /// ★ 预取可以提前(见上), 对齐不行 —— 提前做会把画面往下拽一下。
    /// </summary>
    private const double FeedBottomAlignThreshold = 4;

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

    /// <summary>
    /// 「全部动态」入口是否已量准尺寸。量准前该入口保持透明 —— 它的宽度是
    /// code-behind 量出来才设置的, XAML 初始是"贴内容的窄条"(约为最终宽度的一半),
    /// 若直接亮出来, 用户会看到"先半截、量完闪成全宽"的跳变(2026-11-07 用户实测仍复现)。
    /// </summary>
    private bool _allEntrySized;

    /// <summary>
    /// 正在把最后一张卡完整滚进视口。
    ///
    /// ★★ 为什么需要这道闸(2026-11-07 修"卡片墙连续下滑后崩溃"):
    ///   <see cref="ScrollLastFeedItemIntoView"/> 调 ListBox.ScrollIntoView 会**同步**引发
    ///   ScrollChanged; 而 ScrollChanged 的处理里, 只要"离底 ≤ 4px"又会再调一次对齐。
    ///   在某几种布局状态下(容器刚生成/被重建, 或滚到底后 Extent 又变了一点),
    ///   ScrollIntoView 不移动偏移但照样触发一次 ScrollChanged —— 于是自己调自己,
    ///   一路递归到栈溢出。StackOverflowException 是**不可捕获**的, 表现就是进程直接消失,
    ///   errors.txt 里一个字都没有。有了这个标记, 重入的那一次直接返回。
    /// </summary>
    private bool _aligningFeedBottom;

    /// <summary>
    /// 下一次允许做"最后一项按项对齐"的时间戳(TickCount64)。
    /// ScrollChanged 在布局变化时会连成一串, 300ms 冷却是给动态卡那条路用的第二道闸;
    /// 卡片墙根本不走对齐(见 FeedScroll_OnScrollChanged)。
    /// </summary>
    private long _nextFeedBottomAlignAt;

    /// <summary>
    /// 页面侧再兜一道"自动翻页在途"的闸。
    /// vm.LoadingMore 负责正常情况; 这里防的是滚动事件的连锁触发(一批内容落地后
    /// 尺寸变化又冒出一串 ScrollChanged), 不靠它兜底就会在底部反复发起同一页。
    /// </summary>
    private bool _feedAutoLoadBusy;

    /// <summary>
    /// 连续"内容还不够一屏"的自动补页次数。
    /// ★ 只在**用户没有滚动**时累积(见 FeedScroll_OnScrollChanged 里的 VerticalChange 判断),
    ///   用户一滚动就清零。上限用于防止"接口一直回空页/被屏蔽筛空"时页面自己无限补页。
    /// </summary>
    private int _shortContentFills;

    /// <summary>"内容不足一屏"补页的连续上限(与首页 TriggerAutoLoad 的防死循环思路一致)。</summary>
    private const int MaxShortContentFills = 3;

    public FollowPage()
    {
        InitializeComponent();
        // ★ 页面常驻 PageLayer 后, 导航不再触发 Unloaded/Loaded(只是 Visibility 切换),
        //   所以"进入/离开页面"要挂在 IsVisibleChanged 上 —— 定时器才会在离开页面时停掉。
        IsVisibleChanged += OnPageVisibleChanged;
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

    /// <summary>
    /// 页面可见性变化(= 切页, 见构造函数里的说明)。可见时走原来 OnLoaded 的流程,
    /// 不可见时停掉直播轮询定时器 —— 页面常驻后这是唯一可靠的"离开页面"信号。
    /// </summary>
    private async void OnPageVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) await OnEnteredPage();
        else _liveBoardTimer?.Stop();
    }

    private async Task OnEnteredPage()
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
            _shortContentFills = 0;
            // ★ 延迟到 Loaded 优先级: InitAsync 返回时 Users 刚填充、虚拟化列表还没走完
            //   布局, 容器尚未生成, 立刻量是空跑; 排一个布局完成后的事件再量才量得到。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncAllDynamicEntry));
            // ★ 首屏/刷新后如果内容还不够一屏, 列表根本不会产生滚动, ScrollChanged
            //   也就永远等不到 —— 这里主动补一次(有 MaxShortContentFills 上限)。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAutoFillShortContent));
            // ★ 兜底: 列表为空(未登录/加载失败/一个关注都没有)时没有 UP 行可量,
            //   入口会永远停在透明态 —— 排一个低优先级检查, 量不准就按 XAML 默认样子亮出来,
            //   宁可显示窄条也不要什么都没有。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (AllDynamicEntry == null || _allEntrySized) return;
                _allEntrySized = true;
                AllDynamicEntry.Opacity = 1;
            }));
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
            await LoadMoreFeedGuardedAsync();
        }));
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
        if (DataContext is FollowViewModel vm)
        {
            vm.UseCardWall = false;
            UpdateLayoutChips(vm);
            _shortContentFills = 0;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAutoFillShortContent));
        }
    }

    private void OnLayoutWallClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm)
        {
            vm.UseCardWall = true;
            UpdateLayoutChips(vm);
            _shortContentFills = 0;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAutoFillShortContent));
        }
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
        if (DataContext is FollowViewModel vm) _ = AfterSelectionChangedAsync(vm.SelectAllAsync());
    }

    /// <summary>点某个 UP: 右栏只显示 TA 的投稿视频</summary>
    private void OnSelectUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakeLeftPress()) return;
        if (((FrameworkElement)sender).DataContext is not FollowUser u) return;
        if (DataContext is FollowViewModel vm) _ = AfterSelectionChangedAsync(vm.SelectUpAsync(u));
    }

    /// <summary>
    /// 换选择(全部动态 / 某个 UP)之后的收尾: 重置"内容不足一屏"的补页额度,
    /// 并在新内容布局完成后检查一次够不够一屏(否则卡片墙会停在一页上, 用户滚不动也加载不了)。
    /// </summary>
    private async Task AfterSelectionChangedAsync(Task selectionTask)
    {
        _shortContentFills = 0;
        await selectionTask;
        if (!IsLoaded) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAutoFillShortContent));
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

        // ★ 尺寸量准的这一拍才让入口可见(同一帧生效, 窄条状态从未被渲染过)。
        if (!_allEntrySized)
        {
            _allEntrySized = true;
            AllDynamicEntry.Opacity = 1;
        }
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
        _shortContentFills = 0;
        // 刷新后只有一页时内容常常不够一屏: 不主动补一次的话既没有滚动条也没有 ScrollChanged,
        // 右栏会一直停在这一页(用户感受就是"刷新之后滑不动了")。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAutoFillShortContent));
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
        if (!IsLoaded || DataContext is not FollowViewModel vm) return;

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

        // 用户自己滚动过 ⇒「内容不足一屏」的连续补页额度重新开始。
        if (Math.Abs(e.VerticalChange) > 0.5) _shortContentFills = 0;

        // ★★ 触底对齐中的重入必须立刻返回(2026-11-07 修连续下滑崩溃):
        //   ScrollIntoView 会同步引发 ScrollChanged; 若此时仍在"离底 ≤ 4px"，
        //   下面又会调一次 ScrollIntoView —— 自己调自己, 栈溢出直接杀进程。
        if (_aligningFeedBottom) return;

        // 页面已经切走(实例仍被 MainWindow 缓存)时不要再翻页。
        if (!IsPageActive()) return;

        // 卡片墙有 300 条上限: 到上限后不再自动翻页, 底部会提示切换动态卡。
        if (vm.UseCardWall && vm.WallItems.Count >= 300) return;
        if (vm.LoadingMore || vm.LoadMoreError || !vm.HasMore) return;

        if (sv == null) return;

        // 内容还不够一屏: 没有滚动条、永远滚不到底, ScrollChanged 也不会再来了 ——
        // 主动补一次(由 _shortContentFills 限流, 防止接口一直回空页时无限自补)。
        if (sv.ScrollableHeight <= 0)
        {
            TryAutoFillShortContent();
            return;
        }

        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > FeedAutoLoadThreshold) return;

        // ★★ 2026-11-06 用户报"右栏最下面被一个长方形画幅遮挡住了": 那是**被裁掉的半张卡** ——
        //   ListBox 滚到底时按**像素**停, 最后一张动态卡只露出顶部一行(头像+标题),
        //   而那张卡的白色卡片底色横贯整个右栏宽度, 看着就像"下面压着一个长方形画幅"。
        //   这里在触底时把最后一项**完整滚进视口**(按项对齐), 半张卡的现象就没了。
        // ★ 2026-11-07: 预取阈值改成 800px(见 FeedAutoLoadThreshold)后, 对齐必须单独
        //   用更小的 FeedBottomAlignThreshold —— 否则离底还差 800px 就把画面拽到最后一张卡。
        // ★★ 卡片墙**不需要**这一步: 它是多列网格, 最后一行卡片本身就不高, 滚到底时
        //   整行都在视口里。而 ScrollIntoView 在非虚拟化面板 + 连续滚动下会同步引发
        //   ScrollChanged, 是"自己调自己"的唯一入口 —— 这一档直接跳过, 从源头断掉。
        //   动态卡(单列, 卡片可能比视口还高)才需要按项对齐, 并且带 300ms 冷却。
        if (distanceToBottom <= FeedBottomAlignThreshold
            && !vm.UseCardWall
            && Environment.TickCount64 >= _nextFeedBottomAlignAt)
        {
            _nextFeedBottomAlignAt = Environment.TickCount64 + 300;
            ScrollLastFeedItemIntoView(sv);
        }
        _ = LoadMoreFeedGuardedAsync();
    }

    /// <summary>
    /// 内容明显不足一屏时的主动补页。
    ///
    /// ★ 只在"用户没有滚动"的时机调用(页面加载完 / 刷新后 / 切选择后 / 一批内容落地后):
    ///   这些时候如果没有滚动条, ScrollChanged 不会再来, 不主动补就会永远停在一页上。
    /// ★ 连补次数有上限(<see cref="MaxShortContentFills"/>), 用户一滚动就清零 ——
    ///   既不无限自补, 也不会把用户手动滚出来的翻页额度耗掉。
    /// </summary>
    private void TryAutoFillShortContent()
    {
        if (!IsLoaded || !IsPageActive()) return;
        if (DataContext is not FollowViewModel vm) return;
        if (_shortContentFills >= MaxShortContentFills) return;
        if (vm.Loading || vm.LoadingMore || vm.LoadMoreError || !vm.HasMore) return;
        if (vm.UseCardWall && vm.WallItems.Count >= 300) return;

        var sv = GetVisibleFeedScroll();
        if (sv == null) return;

        // 内容明显够长(超过 0.8 个视口)就不补 —— 这是"够不够一屏"的判据, 不是预取阈值。
        if (sv.ScrollableHeight >= Math.Max(1, sv.ViewportHeight) * 0.8) return;

        _shortContentFills++;
        _ = LoadMoreFeedGuardedAsync();
    }

    /// <summary>
    /// 页面侧的"加载更多"唯一入口: 同一时刻只允许一条在途,
    /// 完成后再排一次"内容是否还不够一屏"的检查(用 Loaded 优先级, 等新内容布局完)。
    /// </summary>
    private async Task LoadMoreFeedGuardedAsync()
    {
        if (_feedAutoLoadBusy || DataContext is not FollowViewModel vm) return;

        _feedAutoLoadBusy = true;
        try
        {
            await vm.LoadMoreFeedAsync();
        }
        finally
        {
            _feedAutoLoadBusy = false;
        }

        if (!IsLoaded) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAutoFillShortContent));
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
        // ★ 重入保护(2026-11-07): ScrollIntoView 会同步发 ScrollChanged, 而 ScrollChanged
        //   里"离底 ≤ 4px"又可能再调回来 —— 没有这道闸就是无限递归 → StackOverflow。
        if (_aligningFeedBottom) return;

        var list = (DataContext as FollowViewModel)?.UseCardWall == true
            ? (DependencyObject?)FeedWallList
            : FeedList;
        if (list is not ListBox lb || lb.Items.Count == 0) return;

        var last = lb.Items[lb.Items.Count - 1];
        if (last == null) return;

        _aligningFeedBottom = true;
        try
        {
            lb.ScrollIntoView(last);
        }
        catch
        {
            // ScrollIntoView 在容器尚未布局时可能抛异常 —— 忽略即可(下一次滚动会再试)。
        }
        finally
        {
            _aligningFeedBottom = false;
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

    /// <summary>右键 UP 行 → 预览大图</summary>
    private void OnViewAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            MediaActions.ShowImage(u.Face, u.Name + " 的头像", Window.GetWindow(this));
        }
    }

    /// <summary>右键 UP 行 → 下载到本地</summary>
    private async void OnDownloadAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            await MediaActions.SaveImageAsync(u.Face, u.Name + "_头像");
        }
    }
}
