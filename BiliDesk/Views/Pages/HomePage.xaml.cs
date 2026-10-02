using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

public partial class HomePage : UserControl
{
    private HomeViewModel? Vm => DataContext as HomeViewModel;
    private const double AutoLoadThreshold = 240;
    // 防止连续自动填充死循环
    private int _autoFillCount;

    /// <summary>
    /// 点深浅色按钮时的鼠标位置(相对本页坐标系)。
    /// 圆形扩散动画需要它当圆心; Click 事件本身不带坐标, 所以在这里提前记下来。
    /// </summary>
    private Point? _themeClickPoint;

    public HomePage()
    {
        InitializeComponent();
        PopularTab.Checked += OnPopularChecked;
        RecommendTab.Checked += OnRecommendChecked;
        RankingTab.Checked += OnRankingChecked;
        LiveTab.Checked += OnLiveChecked;
        // 页面被重新挂载到可视树时刷新问候语(应用长时间挂在后台再切回来, 文案要跟着时段变)
        Loaded += (_, _) =>
        {
            Vm?.RefreshGreeting();
            RefreshThemeIcon();
            RefreshFilterButton();
            AttachFilterViews();
            // 首次进入 / 从其它页面回来: 把非当前 tab 的列表容器摘掉(它们握着几百张
            // 已解码的封面位图), 内存治理的另一半在 ShowTab / MemoryTrim。
            EnsureAttachedTab();
            // 本页可能停在下滑位置被切走再切回来: 「回到顶部」的显隐要按当前偏移重算一次
            UpdateBackToTop(ScrollOf(CurrentTabFromVm() ?? HomeTabKind.Recommend));
            // DataContext 是外面赋进来的, 构造函数里还拿不到 VM, 所以订阅放在这里
            HookSearchCard();
            // 分段标签的下划线要等布局出来才摆得对(首次布局前 ActualWidth 都是 0);
            // 派到 Loaded 优先级 = 排在本次布局之后, 且首次摆放不带动画
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(
                () => MoveTabIndicator(CurrentTabFromVm() ?? HomeTabKind.Recommend, animate: false)));
        };
        // 主题可能被设置页改掉, 这里跟着更新图标。
        // 只在构造函数里订阅一次、不解绑: 本页实例被 MainWindow 长期缓存, 生命周期等同于应用,
        // 不存在泄漏; 反过来写成 Loaded/Unloaded 成对订阅反而会在"切走一次"之后永久失效。
        Svc.Theme.ThemeChanged += RefreshThemeIcon;
        // 筛选条件变化时让筛选按钮自己显示"已启用"状态
        FilterService.Instance.PropertyChanged += OnFilterChanged;
        // 窗口缩放时若搜索卡正开着, 要让它重新对中(RenderTransform 是绝对位移, 不跟布局走)
        SizeChanged += (_, _) => RelayoutSearchCard();
        // 屏蔽面板改到工具列左边后(Placement=Left), 垂直方向要自己底对齐: 面板(≈340px)比
        // 工具列(≈214px)高, 默认顶对齐会顺着往窗口下缘外长出去。Opened 时面板的高度才可信
        // (先量 Child, 兜底; ActualHeight 在布局后也可用), 按"面板底缘 ≈ 工具列底缘 - 2px"算。
        FilterPopup.Opened += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                var h = FilterPopup.ActualHeight;
                if (h <= 0 && FilterPopup.Child is FrameworkElement fe)
                {
                    fe.Measure(new Size(fe.Width > 0 ? fe.Width : 300, double.PositiveInfinity));
                    h = fe.DesiredSize.Height;
                }
                if (h > 0) FilterPopup.VerticalOffset = ToolBar.ActualHeight - h - 2;
            }));
        // 默认选中「推荐」(个性化内容优先), 触发一次初始加载
        RecommendTab.IsChecked = true;
        RefreshThemeIcon();
    }

    // ------------------------------------------------------------ 屏蔽

    /// <summary>
    /// 把首页的三个列表纳入全局屏蔽。
    /// 只挂一次(FilterService 内部会去重), 放在 Loaded 里是因为 DataContext 要等外部赋值。
    /// </summary>
    private void AttachFilterViews()
    {
        if (Vm == null) return;
        FilterService.Instance.Attach(Vm.RecommendItems);
        FilterService.Instance.Attach(Vm.PopularItems);
        FilterService.Instance.Attach(Vm.RankingItems);
        FilterService.Instance.Attach(Vm.LiveItems);
    }

    private void OnFilterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterService.HasFilter)) RefreshFilterButton();
    }

    /// <summary>有屏蔽条件时按钮变强调色, 让用户知道"现在看到的列表是被挡过内容的"</summary>
    private void RefreshFilterButton()
    {
        if (BtnFilter == null) return;
        var on = FilterService.Instance.HasFilter;
        BtnFilter.Foreground = on
            ? (System.Windows.Media.Brush)FindResource("AccentTextBrush")
            : (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        BtnFilter.ToolTip = on ? "屏蔽内容(已启用): " + FilterService.Instance.Description
                               : "屏蔽内容(标题 / UP 主)";
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        FilterPopup.IsOpen = !FilterPopup.IsOpen;
        if (FilterPopup.IsOpen) FilterTitleBox.Focus();
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        FilterService.Instance.Clear();
        RefreshFilterButton();
    }

    // ------------------------------------------------------------ 其它入口

    // 首页右上角的"离线缓存"入口已删(2026-09-26 用户要求): 收进「我的」页了。

    private void OnMessagesClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Messages);

    // ------------------------------------------------------------ 深浅色切换

    private void OnThemeButtonMouseDown(object sender, MouseButtonEventArgs e)
        => _themeClickPoint = e.GetPosition(this);

    /// <summary>
    /// 切换深浅色, 并以点击位置为圆心播放"圆形扩散"过渡。
    ///
    /// 圆心要从本页坐标系换算到**主窗口内容区**的坐标系 —— 快照和覆盖层是按窗口根面板
    /// 建立的, 直接用页面内坐标会让扩散中心偏移(偏移量正好是左侧导航栏宽度)。
    /// </summary>
    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        Point? origin = null;
        if (_themeClickPoint is { } p && win?.Content is FrameworkElement root)
        {
            try { origin = TranslatePoint(p, root); }
            catch { origin = null; /* 不在同一可视树里时退回窗口中心 */ }
        }
        _themeClickPoint = null;

        ThemeTransition.ToggleLightDark(win, origin);
    }

    /// <summary>
    /// 图标显示"点了会切到哪种": 当前是深色就显示太阳(切浅色), 反之显示月亮。
    /// 同时把提示文案一起更新, 免得图标语义含糊时用户猜。
    /// </summary>
    private void RefreshThemeIcon()
    {
        if (ThemeGlyph == null || BtnTheme == null) return;
        var dark = Svc.Theme.IsDark;
        // E706 = 太阳(Brightness) / E708 = 月亮(QuietHours)
        ThemeGlyph.Text = dark ? "\uE706" : "\uE708";
        BtnTheme.ToolTip = dark ? "切换到浅色主题" : "切换到深色主题";
    }

    /// <summary>由主窗口在首次显示时调用</summary>
    public async Task Init()
    {
        if (Vm != null) await Vm.InitAsync();
    }

    private void OnPopularChecked(object sender, RoutedEventArgs e)
    {
        ShowTab(HomeTabKind.Popular);
        if (DataContext is HomeViewModel vm) _ = vm.SwitchToPopularAsync();
    }

    private void OnRecommendChecked(object sender, RoutedEventArgs e)
    {
        ShowTab(HomeTabKind.Recommend);
        if (DataContext is HomeViewModel vm) _ = vm.SwitchToRecommendAsync();
    }

    private void OnRankingChecked(object sender, RoutedEventArgs e)
    {
        ShowTab(HomeTabKind.Ranking);
        if (DataContext is HomeViewModel vm) _ = vm.SwitchToRankingAsync();
    }

    private void OnLiveChecked(object sender, RoutedEventArgs e)
    {
        ShowTab(HomeTabKind.Live);
        if (DataContext is HomeViewModel vm) _ = vm.SwitchToLiveAsync();
    }

    // ------------------------------------------------------------ 非当前 tab 的容器释放(内存治理)

    private enum HomeTabKind { Recommend, Popular, Ranking, Live }

    /// <summary>当前实际挂载着列表的 tab(只有一个; 其余的 ItemsSource 都被摘掉)</summary>
    private HomeTabKind? _attachedTab;

    /// <summary>被摘下时各 tab 的滚动位置, 挂回去时恢复 —— 摘挂不能牺牲"切回来还在原地"的体验</summary>
    private readonly Dictionary<HomeTabKind, double> _savedOffsets = new();

    private ItemsControl? ListOf(HomeTabKind t) => t switch
    {
        HomeTabKind.Recommend => RecommendList,
        HomeTabKind.Popular => PopularList,
        HomeTabKind.Ranking => RankingList,
        _ => LiveList,
    };

    private ScrollViewer? ScrollOf(HomeTabKind t) => t switch
    {
        HomeTabKind.Recommend => RecommendScroll,
        HomeTabKind.Popular => PopularScroll,
        HomeTabKind.Ranking => RankingScroll,
        _ => LiveScroll,
    };

    private System.Collections.IEnumerable? ItemsOf(HomeTabKind t, HomeViewModel vm) => t switch
    {
        HomeTabKind.Recommend => vm.RecommendItems,
        HomeTabKind.Popular => vm.PopularItems,
        HomeTabKind.Ranking => vm.RankingItems,
        _ => vm.LiveItems,
    };

    /// <summary>
    /// 切换 tab 时只保留当前 tab 的列表容器, 摘掉其它三个的 ItemsSource。
    ///
    /// 为什么: 四个 tab 的 ScrollViewer 常驻同一棵可视树, 切走只是 Collapsed ——
    /// 里面的几百张卡片(以及它们握着的已解码封面位图)被 UI 钉住永不释放,
    /// 这是"点过热门/排行榜/直播后内存 +100MB 且切回推荐也不回落"的直接原因。
    /// 摘掉 ItemsSource 后容器与位图引用一起变成垃圾; CoverLoader 的 32MB 缓存
    /// 会兜住"切回来"时的大部分封面, 重挂载是毫秒级的。
    /// </summary>
    private void ShowTab(HomeTabKind tab)
    {
        if (_attachedTab == tab) return;
        if (_attachedTab is { } old) DetachTab(old);
        // 先记新 tab 再挂载: AttachTab 里的滚动位置恢复要拿它做判断
        _attachedTab = tab;
        AttachTab(tab);
        // 切 tab 后「回到顶部」要跟着新列表的滚动位置走(那个 tab 可能本来就停在顶部)
        UpdateBackToTop(ScrollOf(tab));
        // 刚释放了一批卡片容器与封面位图: 安排一次延迟合并的回收,
        // 让任务管理器里的数字真的落下来(否则引用已是垃圾但 GC 不跑就永远占着)。
        MemoryTrim.RequestTrim();
        // 分段标签的下划线跟着选中项走
        MoveTabIndicator(tab);
    }

    // ------------------------------------------------------------ 分段标签的选中下划线
    //
    // 参考图: 选中的标签字变粉, 它下面有一根短横线; 换标签时那根线要**滑过去**。
    // 只动画一个属性就够 —— 线的宽度是固定的(见 HomePage.xaml 里的 TabIndicator), 所以只要把 X
    // 从旧位置挪到新位置; 不动画宽度也就不会"中途忽胖忽瘦"。
    // 位置必须实测(TransformToAncestor): 四个标签文字长短不同(推荐/热门 vs 排行榜), 宽度不一样,
    // 靠固定间距是推不出来的。

    /// <summary>下划线滑过去的时长。比搜索卡那两段短 —— 它只是个小指示器, 不该抢戏。</summary>
    private const int TabIndicatorMs = 220;

    /// <summary>下划线还没摆过位。首次布局之前 ActualWidth 都是 0, 那时摆了也是错的。</summary>
    private bool _tabIndicatorPlaced;

    private RadioButton? TabButtonOf(HomeTabKind tab) => tab switch
    {
        HomeTabKind.Recommend => RecommendTab,
        HomeTabKind.Popular => PopularTab,
        HomeTabKind.Ranking => RankingTab,
        _ => LiveTab,
    };

    /// <summary>把选中下划线挪到某个标签下面。animate=false 用于首次摆放(不该有"滑进来"的动画)。</summary>
    private void MoveTabIndicator(HomeTabKind tab, bool animate = true)
    {
        var target = TabButtonOf(tab);
        if (target == null || HomeTabs.ActualWidth <= 0 || target.ActualWidth <= 0)
        {
            // 布局还没就绪: 记下来, Loaded 里会再摆一次
            _tabIndicatorPlaced = false;
            return;
        }

        var x = target.TransformToAncestor(HomeTabs).Transform(new Point(0, 0)).X
                + (target.ActualWidth - TabIndicator.Width) / 2;

        if (!animate || !_tabIndicatorPlaced)
        {
            // 先清动画再落值: 否则会被上一次残留的动画值顶掉
            TabIndicatorTrans.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorTrans.X = x;
            _tabIndicatorPlaced = true;
            return;
        }

        TabIndicatorTrans.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(TabIndicatorTrans.X, x, TimeSpan.FromMilliseconds(TabIndicatorMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>页面从可视树回来时调用: 保证"当前 tab"确实挂载着(其它的保持摘除)</summary>
    private void EnsureAttachedTab()
    {
        var current = CurrentTabFromVm();
        if (current == null) return;
        if (_attachedTab == current) return;
        // 页面切走再切回来: _attachedTab 记录的还是离开时的 tab, 但四个列表的 ItemsSource
        // 都还在(XAML 初始绑定) —— 此时只需要把"非当前"的三个摘掉, 当前的别动(避免闪烁)。
        foreach (HomeTabKind t in Enum.GetValues<HomeTabKind>())
            if (t != current && ListOf(t)?.ItemsSource != null) DetachTab(t);
        _attachedTab = current;
    }

    private HomeTabKind? CurrentTabFromVm()
    {
        if (DataContext is not HomeViewModel vm) return null;
        if (vm.IsPopularTab) return HomeTabKind.Popular;
        if (vm.IsRankingTab) return HomeTabKind.Ranking;
        if (vm.IsLiveTab) return HomeTabKind.Live;
        if (vm.IsRecommendTab) return HomeTabKind.Recommend;
        return null;
    }

    private void DetachTab(HomeTabKind tab)
    {
        // 先记滚动位置: 挂回去时恢复, 否则"切回来回到顶部"是可感知的体验倒退
        if (ScrollOf(tab) is { } sv) _savedOffsets[tab] = sv.VerticalOffset;
        var list = ListOf(tab);
        if (list == null) return;
        // 摘 ItemsSource 前先停掉它的分帧填充: 否则还在排队的补帧会往一个已摘除的列表里灌数据
        if (_fill != null && ReferenceEquals(_fill.List, list)) { _fill.Dispose(); _fill = null; }
        list.ItemsSource = null;
    }

    /// <summary>正在进行的分帧填充(同一时刻只有当前 tab 的列表挂着数据, 所以只有一个)</summary>
    private DeferredFill? _fill;

    /// <summary>每批补多少张卡片: 首屏一批(约 4~5 列 × 5 行), 之后每帧一批。</summary>
    private const int FillChunkSize = 24;

    private void AttachTab(HomeTabKind tab)
    {
        if (DataContext is not HomeViewModel vm) return;
        var list = ListOf(tab);
        if (list == null) return;
        var src = ItemsOf(tab, vm);
        if (src == null) return;

        // 统一走"分帧挂载": 首批在下一帧(Background 优先级, 点击的布局/渲染先走), 其余按帧补。
        // 为什么必须这样: 四个列表都是 ItemsControl + WrapPanel(外层还有自己的 ScrollViewer),
        // **没有任何虚拟化** —— ItemsSource 一挂, 几百张 VideoCard 容器在一次布局里同步生成,
        // 表现就是"点热门/排行榜/直播入口卡一下"。
        // 注意 XAML 里四份 ItemsSource 是初始绑定好的: 折叠状态的 ItemsControl 没被测量过,
        // 容器其实还没生成, 所以这里把绑定替换成代理视图没有浪费, 生成时机反而被我们接管了。
        _fill?.Dispose();
        _fill = new DeferredFill(list, src, FillChunkSize);
        _fill.Progress += (_, _) => TryRestoreScroll(tab);
        _fill.Start();
        TryRestoreScroll(tab);   // 首批挂完若已覆盖保存的偏移, 当场恢复, 不用等补帧
    }

    /// <summary>
    /// 补帧过程中逐步尝试恢复滚动位置。
    /// 不能只在挂载时恢复一次: 首批之后 ScrollableHeight 还很小, 深一点的偏移会被判成"越界"跳过。
    /// 也不能每帧都无条件恢复: 用户可能已经在滚了 —— 恢复过一次就从待恢复表里移除。
    /// </summary>
    private void TryRestoreScroll(HomeTabKind tab)
    {
        if (_attachedTab != tab) return;
        if (!_savedOffsets.TryGetValue(tab, out var offset) || offset <= 0) return;
        if (ScrollOf(tab) is { } sv && offset <= sv.ScrollableHeight)
        {
            sv.ScrollToVerticalOffset(offset);
            _savedOffsets.Remove(tab);
        }
    }

    /// <summary>
    /// 手动刷新(当前 tab)。
    ///
    /// ★ 刷新完把当前 tab 滚回顶部(2026-10-01)。刷新换的是**整份列表**: 新的一批可能比旧的短
    ///   (推荐接口一次只给 10~30 条, 而旧列表可能已经滚了几页), 此时 ScrollViewer 会把越界的
    ///   偏移夹回来, 夹多少取决于新内容有多高 —— 用户看到的就是"点完刷新页面自己滑了一段"。
    ///   回到顶部是刷新语义下唯一确定的落点(和下拉刷新一致), 也就没有"滑"这回事了。
    /// </summary>
    private async Task RefreshCurrentTabAsync()
    {
        if (Vm == null) return;
        await Vm.RefreshAsync();
        var tab = CurrentTabFromVm() ?? HomeTabKind.Recommend;
        ScrollOf(tab)?.ScrollToTop();
        _savedOffsets.Remove(tab);
    }

    /// <summary>工具栏刷新键 + 错误条里的「重试」都走这一条</summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshCurrentTabAsync();

    /// <summary>通用: 滚动到底部 / 内容不够一屏时, 自动加载更多</summary>
    private async Task TriggerAutoLoad(ScrollViewer sv, HomeViewModel vm)
    {
        if (_autoFillCount > 5) return; // 防止死循环
        if (vm.LoadingMore || vm.Loading) return;
        // 分帧填充进行中: 每补一批 ScrollableHeight 都在长, 这时判"内容不够一屏"是错的,
        // 会带着空列表去拉下一页; 等填充完(补帧引发的 ScrollChanged 会再来)再判。
        if (_fill is { IsActive: true }) return;

        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
        if (distanceToBottom > AutoLoadThreshold && !contentShort) return;

        _autoFillCount++;
        try { await vm.LoadMoreAsync(); }
        finally { _autoFillCount--; }
    }

    private void PopularScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateBackToTop((ScrollViewer)sender);
        if (DataContext is not HomeViewModel vm) return;
        _ = TriggerAutoLoad((ScrollViewer)sender, vm);
    }

    private void RecommendScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateBackToTop((ScrollViewer)sender);
        if (DataContext is not HomeViewModel vm) return;
        _ = TriggerAutoLoad((ScrollViewer)sender, vm);
    }

    private void LiveScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateBackToTop((ScrollViewer)sender);
        if (DataContext is not HomeViewModel vm) return;
        _ = TriggerAutoLoad((ScrollViewer)sender, vm);
    }

    /// <summary>排行榜不自动翻页(它是一次给完的), 但仍然要让「回到顶部」跟着滚动位置亮灭</summary>
    private void RankingScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateBackToTop((ScrollViewer)sender);

    // ------------------------------------------------------------ 回到顶部

    /// <summary>
    /// 下滑超过这个距离才把「回到顶部」亮出来。
    /// 阈值别太小: 轻轻滚一下按钮就闪出来、回滚一点又消失, 看着像故障。
    /// </summary>
    private const double BackToTopThreshold = 400;

    /// <summary>「回到顶部」当前是否亮着(避免每次滚动都重复起动画)</summary>
    private bool _backToTopShown;

    /// <summary>
    /// 按当前滚动位置点亮/熄灭「回到顶部」。
    ///
    /// 只动 <c>Opacity</c> 与 <c>IsHitTestVisible</c>, **不用 Visibility** ——
    /// 它在右下角那个 StackPanel 里与另外四个按钮同排, 用 Collapsed 会让它们每过一次阈值就上下跳。
    /// 淡入淡出顺带和界面其它地方的过渡语言一致。
    /// </summary>
    private void UpdateBackToTop(ScrollViewer? sv)
    {
        var show = sv is { VerticalOffset: > BackToTopThreshold };
        if (show == _backToTopShown) return;
        _backToTopShown = show;

        // 只切按钮自己的 Opacity 与命中测试 —— 它在工具列最上面, 用 Visibility 的话
        // 下面四个按钮每滚过一次阈值就会上下跳一下。
        BtnTop.IsHitTestVisible = show;
        BtnTop.BeginAnimation(OpacityProperty,
            new DoubleAnimation(BtnTop.Opacity, show ? 1 : 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>
    /// 把**当前 tab 的**列表滚回顶部。
    /// 必须按 tab 取, 不能拿 sender 之外的那个 —— 四个 ScrollViewer 常驻同一棵树, 切走的只是被折叠。
    /// </summary>
    private void OnBackToTopClick(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTabFromVm() ?? HomeTabKind.Recommend;
        ScrollOf(tab)?.ScrollToTop();
    }

    // ------------------------------------------------------------ 右上角搜索卡
    //
    // 一套动画, 两个阶段:
    //   ① 向左平移 —— 把卡片(此刻就是那条灰的搜索框)从右上角滑到页面**水平正中**;
    //   ② 展开 —— 宽度 440→520、高度 48→卡片高, 露出下面的 搜索历史 / 热搜。
    //   **顶部全程不动**(2026-09-26 用户要求"向左平移到上方正中间"): 卡片长高是往下长,
    //   所以没有 Y 方向的位移、也没有 Y 的补偿 —— 只有 X 一个方向在动。
    //
    // ★ 展开时"横向中心钉住不动"的补偿量是这么来的(卡片右对齐):
    //     渲染中心X = 可用宽 - 右边距 - W/2 + X   →  W 每长 1, 中心就左移 0.5 ⇒ X 要补 +ΔW/2
    //   代码里不显式写这个补偿量, 而是各算一次 PanXFor(收起宽) / PanXFor(展开宽) 当两段终点 ——
    //   两者之差恰好就是 ΔW/2。把 X/Width 的**关键帧时间点对齐**, 补偿就天然同步: 补偿量只是
    //   宽度的线性函数, 跟缓动怎么走无关(两边缓动相同, 但即便不同也严格成立)。
    //   ⚠ 别"顺手再补一次 ΔW/2": 那是重复补偿, 卡片会整体偏出页面正中半个宽度差(踩过)。
    //
    // 为什么两段都用关键帧、而不是"跑完平移再 BeginAnimation 展开": 同一个属性上第二次
    // BeginAnimation 会**立刻**把值顶到新动画的 From(BeginTime 只推迟开始, 不推迟生效),
    // 第一段会被截断 —— 这条在滚动弹幕上踩过, 见 PlayerWindow 里弹幕的注释。

    private const double SearchBoxW = 440;
    private const double SearchBoxH = 48;
    /// <summary>
    /// 收起态右边距。= 142(窗口那三个自绘按钮在本页坐标里的跨度) + 10 间隙。
    /// 窗口按钮是 MainWindow 里 `Margin="0,4,4,0"` 右对齐的 3×46 = 138 宽, 在本页坐标里从右边
    /// 142 起贴到页右缘; 而搜索框跟它处在**同一条水平线**上(与首页标题、左侧 logo 对齐),
    /// 纵向躲不开, 只能横向让开。改 XAML 里的 Margin 时这里要同步(算平移量要用)。
    /// </summary>
    private const double SearchBoxRight = 152;
    private const double SearchCardW = 520;
    /// <summary>内容四周的余量: 免得"刚好放满"因为 1px 取整就冒出滚动条</summary>
    private const double SearchCardSlack = 8;

    /// <summary>搜索框与内容框之间的间隙(2026-09-26 起两个框是分开的, XAML 里内容框的 Margin 上边距同值)</summary>
    private const double SearchContentGap = 10;

    /// <summary>搜索框聚焦时描边淡入的最终不透明度(2026-09-26 起是描边、不再是光晕)</summary>
    private const double SearchRingOpacity = 1.0;

    /// <summary>平移段时长。位移有好几百像素, 太短会"闪一下就到位"</summary>
    private const int SearchPanMs = 300;
    /// <summary>展开段时长(紧接平移之后)</summary>
    private const int SearchExpandMs = 340;
    /// <summary>收起时先让内容淡出, 这段之后尺寸才开始缩 —— 反过来能看见内容被挤扁</summary>
    private const int SearchFadeOutMs = 90;
    private const int SearchCollapseMs = 300;
    /// <summary>内容量变化后, 已展开卡片改高度的时长</summary>
    private const int SearchResizeMs = 220;

    /// <summary>卡片处于"打开"状态(含正在展开的那一段)</summary>
    private bool _searchOpen;
    /// <summary>正在收起: 这时不接受"再打开", 免得从半路折返出怪动作</summary>
    private bool _searchClosing;
    /// <summary>展开动画已结束、输入框可以正常点了(动画期间点它只当"打开")</summary>
    private bool _searchInputReady;
    /// <summary>已订阅 SearchRequested 的那个 VM(DataContext 可能被换, 所以记对象本身)</summary>
    private HomeViewModel? _searchHookedVm;
    /// <summary>展开动画途中内容量变了: 记下来, 等动画收尾再重新量一次高度</summary>
    private bool _searchNeedRemeasure;

    private HomeViewModel? SearchVm => DataContext as HomeViewModel;

    /// <summary>
    /// 展开态应有的高度 = 输入行 + 内容自然高度。
    ///
    /// 为什么不写死一个数: 搜索历史可能只有两条、也可能展开成 20 条, 热搜还会多出"取不到"
    /// 的一行说明 —— 写死高度不是底部空一大块, 就是内容被裁掉。
    /// 好在 ScrollViewer 即使在 0 高的行里也会拿无限高度去量内容, ExtentHeight 随时可读。
    ///
    /// ★ 量之前必须把宽度摆到展开值: 收起态宽 440、展开态 520, 标签的换行结果不一样
    ///   (实测同一批词条能差整整一行), 按收起态量出来的高度会偏大一截。
    /// </summary>
    private double ComputeOpenHeight()
    {
        // 用局部变量判"要不要临时改宽", 而不是拿 SearchCard.Width 直接比 —— 动画档位下它未必等于
        // 本地值, 但这里只在"没在动"的时候被调用(展开前 / 展开完后 / 内容变化后)。
        var needRemeasure = Math.Abs(SearchCard.Width - SearchCardW) > 0.5;
        if (needRemeasure)
        {
            SearchCard.Width = SearchCardW;
            SearchCard.UpdateLayout();
        }

        var content = SearchCardScroll.ExtentHeight;

        if (needRemeasure)
        {
            SearchCard.Width = SearchBoxW;
            SearchCard.UpdateLayout();
        }

        // 兜底: 正常情况下布局早就跑过了, 这个分支不会走到
        if (content <= 0) return SearchBoxH + 320;

        // 上限留出上下各 48 的呼吸空间; 真超了就靠内容区自己的滚动条兜住
        var max = Math.Max(SearchBoxH + 120, ActualHeight - 96);
        // 展开高度 = 输入行 + 两框之间的间隙 + 内容框内部高度 + 余量。
        // 内容框自己那 1px 上下边框会吃掉 2px, 由 SearchCardSlack 兜住(它本来就是留给取整误差的)。
        return Math.Min(max, SearchBoxH + SearchContentGap + content + SearchCardSlack);
    }

    /// <summary>
    /// 内容量变了(热搜到货 / 历史展开收起 / 历史被清空 / 加载占位行出现消失) → 卡片高度要重算。
    /// </summary>
    private void OnSearchVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("HasHot" or "HistoryExpanded" or "HasSearchHistory" or "HotError" or "HotLoading"))
            return;
        if (!_searchOpen) return;
        ScheduleOpenHeightSync();
    }

    /// <summary>
    /// 安排一次"重新量高度并调卡片"。**必须延后**, 而且有两个独立的理由, 都是实测踩出来的:
    ///   1. 属性通知发在"数据刚变、容器还没重建"那一刻, 当场量到的 ExtentHeight 还是旧值
    ///      (表现: 点「展开更多」后卡片没长够, 底部溢出 62px);
    ///   2. 展开动画的 Completed 也跑在布局之前, 在那里量同样是旧值
    ///      (表现: 展开高度一直停在"带‘正在获取热搜…’占位行"的那个偏大值, 底部白 42px)。
    /// 于是统一派到 Loaded 优先级(排在本次布局之后), 再补一次 UpdateLayout 兜底。
    /// 展开动画还没结束就先记账, 由 OnSearchOpened 收尾补一次 —— 动画中间插一脚会把动画截断。
    /// </summary>
    private void ScheduleOpenHeightSync()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_searchOpen) return;
            if (!_searchInputReady) { _searchNeedRemeasure = true; return; }
            UpdateLayout();
            ResizeOpenCard(ComputeOpenHeight());
        }));
    }

    /// <summary>
    /// 已展开的卡片改高度 —— 顶部固定, 只往下长, 所以**不用补偿位移**
    /// (之前是"中心钉在页面正中", 长高要同步上移 ΔH/2; 现在顶部不动, 那一半补偿就没了)。
    /// </summary>
    private void ResizeOpenCard(double newHeight)
    {
        var h0 = SearchCard.Height;
        if (double.IsNaN(h0) || h0 <= 0 || Math.Abs(newHeight - h0) < 1) return;

        SearchCard.BeginAnimation(FrameworkElement.HeightProperty,
            new DoubleAnimation(h0, newHeight, TimeSpan.FromMilliseconds(SearchResizeMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>
    /// 订阅 VM 的"用户选定了要搜的词", 顺带把上次离开本页时的卡片状态收干净。
    /// 导航切页只是把本页从可视树里摘掉, 状态会留着 —— 不在这里收, 切回来卡片还开着。
    /// </summary>
    private void HookSearchCard()
    {
        var vm = SearchVm;
        if (!ReferenceEquals(_searchHookedVm, vm))
        {
            if (_searchHookedVm != null)
            {
                _searchHookedVm.SearchRequested -= OnSearchRequested;
                _searchHookedVm.PropertyChanged -= OnSearchVmPropertyChanged;
            }
            _searchHookedVm = vm;
            if (_searchHookedVm != null)
            {
                _searchHookedVm.SearchRequested += OnSearchRequested;
                _searchHookedVm.PropertyChanged += OnSearchVmPropertyChanged;
            }
        }

        if (_searchOpen || _searchClosing) ResetSearchCard();

        // 顺手把热搜拉起来(有 10 分钟缓存, 每次回首页再调也只是读缓存):
        // 这样首次点开搜索卡时榜单已经就位, 不会先冒出一行"正在获取热搜…"、等数据到了再把卡片撑高一下。
        if (vm != null) _ = vm.EnsureHotSearchAsync();
    }

    private void OnSearchRequested(string keyword)
    {
        // 先收卡片再跳: 跳过去之后本页就被摘出可视树, 动画留在半路很难看
        ResetSearchCard();
        SvcWindow.Main?.NavigateToSearch(keyword);
    }

    // ---------------- 打开 / 收起 ----------------

    /// <summary>点右上角搜索框: 先平移到页面正中, 再原地展开成卡片</summary>
    private void OpenSearchOverlay()
    {
        if (_searchOpen || _searchClosing) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        _searchOpen = true;
        // 遮罩先出来: 展开这一段里点页面任何地方都算"点外面"
        SearchScrim.Visibility = Visibility.Visible;
        // 热搜是懒加载的: 现在就去拉, 等卡片展开完基本已经就位
        _ = SearchVm?.EnsureHotSearchAsync();
        // 高度在变, 这段别让 Auto 闪出滚动条(动画结束再切回 Auto)
        SearchCardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var w1 = SearchCardW;
        var h1 = ComputeOpenHeight();
        // 平移段终点 = "把收起宽摆到页面水平正中"; 展开段终点 = "把展开宽摆到页面水平正中"。
        // 两者之差正好是 ΔW/2 —— 就是宽度增长会让中心跑掉的那一半, 于是展开全程横向中心都不动。
        var tx = PanXFor(SearchBoxW);
        var tx1 = PanXFor(w1);
        var pan = SearchPanMs;
        var end = SearchPanMs + SearchExpandMs;

        // 只有 X 在动: 顶部固定, 卡片是往下长高的
        SearchCardTrans.BeginAnimation(TranslateTransform.XProperty, Keys(
            K(0, 0), K(pan, tx, ease), K(end, tx1, ease)));

        // 平移段宽度/高度不动: 中间那个关键帧要显式给原值, 否则会从 0 一路线性长到展开值
        SearchCard.BeginAnimation(FrameworkElement.WidthProperty, Keys(
            K(0, SearchBoxW), K(pan, SearchBoxW), K(end, w1, ease)));

        var height = Keys(K(0, SearchBoxH), K(pan, SearchBoxH), K(end, h1, ease));
        height.Completed += (_, _) => OnSearchOpened();
        SearchCard.BeginAnimation(FrameworkElement.HeightProperty, height);

        // 内容框随展开淡入 —— 底色硬切在动画里很跳。搜索框自己不用淡入: 它一直都在,
        // 展开只是"旁边多长出一个框"。
        FadeIn(SearchContentBox, pan, SearchExpandMs);
        // 框里的内容等长开一点再露, 看着像"被框一点点让出来"
        FadeIn(SearchCardScroll, pan + (int)(SearchExpandMs * 0.35), (int)(SearchExpandMs * 0.65));
    }

    private void OnSearchOpened()
    {
        _searchInputReady = true;
        SearchCardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // 展开途中热搜到货之类让内容变多了: 这时才补一次高度(动画期间插一脚会把动画截断)
        if (_searchNeedRemeasure)
        {
            _searchNeedRemeasure = false;
            ScheduleOpenHeightSync();
        }
        // 点搜索框的意图就是要打字, 展开完直接把焦点交过去
        SearchKeywordBox.Focus();
    }

    /// <summary>收起: 内容先淡出 → 缩回灰条 → 滑回右上角。任何时刻都能调(半路也收得回去)</summary>
    private void CloseSearchOverlay()
    {
        if (!_searchOpen) return;
        _searchOpen = false;
        _searchInputReady = false;
        _searchClosing = true;

        // 拿当前值当起点: 动画被打断时就从这一刻的尺寸/位置收回去, 不跳
        var w0 = SearchCard.Width > 0 ? SearchCard.Width : SearchBoxW;
        var h0 = SearchCard.Height > 0 ? SearchCard.Height : SearchBoxH;
        var x0 = SearchCardTrans.X;

        var fade = TimeSpan.FromMilliseconds(SearchFadeOutMs);
        // From **必须显式给当前值**: 不写 From 时动画从属性**基值**起步, 而基值仍是收起态的 0,
        // 内容会先"啪"地跳成全不透明再淡出。
        SearchCardScroll.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchCardScroll.Opacity, 0, fade));

        var hold = SearchFadeOutMs;
        var end = SearchFadeOutMs + SearchCollapseMs;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeBack = new TimeSpan(0, 0, 0, 0, SearchCollapseMs);

        SearchContentBox.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchContentBox.Opacity, 0, fadeBack) { BeginTime = fade });

        SearchCardTrans.BeginAnimation(TranslateTransform.XProperty, Keys(
            K(0, x0), K(hold, x0), K(end, 0, ease)));
        SearchCard.BeginAnimation(FrameworkElement.WidthProperty, Keys(
            K(0, w0), K(hold, w0), K(end, SearchBoxW, ease)));

        var height = Keys(K(0, h0), K(hold, h0), K(end, SearchBoxH, ease));
        height.Completed += (_, _) => ResetSearchCard();
        SearchCard.BeginAnimation(FrameworkElement.HeightProperty, height);
    }

    /// <summary>
    /// 恢复成收起态并清掉全部动画。
    ///
    /// 必须清动画: 关键帧动画是 HoldEnd 的, 不清的话属性被动画值一直占着 —— 下次打开时
    /// 关键帧的起点就不再是收起值, 窗口缩放后卡片也回不到右上角。
    /// </summary>
    private void ResetSearchCard()
    {
        _searchOpen = false;
        _searchClosing = false;
        _searchInputReady = false;
        _searchNeedRemeasure = false;

        SearchCard.BeginAnimation(FrameworkElement.WidthProperty, null);
        SearchCard.BeginAnimation(FrameworkElement.HeightProperty, null);
        SearchCardTrans.BeginAnimation(TranslateTransform.XProperty, null);
        SearchContentBox.BeginAnimation(OpacityProperty, null);
        SearchCardScroll.BeginAnimation(OpacityProperty, null);
        SearchCardBgHover.BeginAnimation(OpacityProperty, null);
        SearchInputRing.BeginAnimation(OpacityProperty, null);

        // 先清动画再落本地值, 否则会被残留的动画值顶掉
        SearchCard.Width = SearchBoxW;
        SearchCard.Height = SearchBoxH;
        SearchCardTrans.X = 0;
        SearchContentBox.Opacity = 0;
        SearchCardScroll.Opacity = 0;
        SearchCardBgHover.Opacity = 0;
        SearchInputRing.Opacity = 0;
        SearchCardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        SearchScrim.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 让"宽度为 w 的卡片"横向居中于页面所需的 X 位移。
    /// 卡片是右对齐的, 所以布局左边界 = 可用宽 - 右边距 - w;
    /// 顶部固定(竖向不需要位移), 于是整个动画只有这一个量。
    /// </summary>
    private double PanXFor(double w)
        => ActualWidth / 2 - (ActualWidth - SearchBoxRight - w / 2);

    /// <summary>窗口尺寸变了: 卡片开着就按新位置重新居中(不重播动画, 直接落到新位置)</summary>
    private void RelayoutSearchCard()
    {
        // _searchInputReady 为真 = 已展开完且没在收 —— 动画中途不动它, 否则会把动画打断
        if (!_searchInputReady) return;

        SearchCardTrans.BeginAnimation(TranslateTransform.XProperty, null);
        SearchCardTrans.X = PanXFor(SearchCard.Width);
    }

    // ---------------- 输入框 / 遮罩的交互 ----------------

    /// <summary>
    /// 收起态: 点卡片(连输入框一起)只当"打开", 不把光标落进输入框 —— 点这个条的意图是要
    /// 搜索面板, 而不是在这里盲打。展开动画结束时才由 OnSearchOpened 把焦点交过去。
    /// 展开态不拦: 那时点输入框就该正常定位光标、选中文字。
    /// </summary>
    private void OnSearchCardPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_searchInputReady) return;
        e.Handled = true;
        OpenSearchOverlay();
    }

    /// <summary>
    /// 聚焦 / 失焦: 搜索框那圈**粉色描边**淡入淡出。
    /// (2026-09-26 需求一路在变: 先"描边做成光晕", 又"光晕不明显"加强, 最终定回"去除光晕、增加粉色描边" ——
    ///  现在就是最简单的一圈 2px 描边。)
    ///
    /// 为什么用动画而不是样式触发器: 直接切会"啪"地亮起来, 淡入才和卡片其它淡入淡出一套语言。
    /// From 取当前值, 所以淡出途中再聚焦也不会跳。
    /// </summary>
    private void OnSearchKeywordGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        => FadeSearchRing(true);

    private void OnSearchKeywordLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => FadeSearchRing(false);

    private void FadeSearchRing(bool on)
        => SearchInputRing.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchInputRing.Opacity, on ? SearchRingOpacity : 0,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

    /// <summary>悬停加深。2026-09-26 起这层只盖住搜索框自己(事件挂在输入行上), 所以两种状态都要亮。</summary>
    private void OnSearchCardMouseEnter(object sender, MouseEventArgs e)
        => SearchCardBgHover.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchCardBgHover.Opacity, 1, TimeSpan.FromMilliseconds(120)));

    private void OnSearchCardMouseLeave(object sender, MouseEventArgs e)
    {
        SearchCardBgHover.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchCardBgHover.Opacity, 0, TimeSpan.FromMilliseconds(140)));
    }

    private void OnSearchScrimClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseSearchOverlay();
    }

    /// <summary>Esc 关掉卡片。挂在卡片上: PreviewKeyDown 是从根往焦点元素隧道的, 焦点在不在输入框都能收到。</summary>
    private void OnSearchCardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseSearchOverlay();
    }

    private void OnSearchKeywordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SearchVm?.RunSearchCommand.Execute(null);
    }

    private void OnSearchGoClick(object sender, RoutedEventArgs e)
    {
        // 收起态点放大镜 = 打开面板(正常流程里这次点击已被卡片自己拦下, 这里只是兜底);
        // 展开态 = 用当前词搜一次。
        if (!_searchInputReady) OpenSearchOverlay();
        else SearchVm?.RunSearchCommand.Execute(null);
    }

    private void OnHotSearchRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HotSearchItem item }) return;
        e.Handled = true;
        SearchVm?.UseHotSearchCommand.Execute(item);
    }

    // ---------------- 动画小工具 ----------------

    /// <summary>一帧关键帧。给了缓动, 就作用在"上一帧 → 这一帧"这一段上。</summary>
    private static EasingDoubleKeyFrame K(int ms, double value, IEasingFunction? ease = null)
    {
        var frame = new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)));
        if (ease != null) frame.EasingFunction = ease;
        return frame;
    }

    private static DoubleAnimationUsingKeyFrames Keys(params DoubleKeyFrame[] frames)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        foreach (var f in frames) anim.KeyFrames.Add(f);
        return anim;
    }

    /// <summary>从全透明淡入到 1。每次新建动画实例 —— 同一个实例挂到两处容易出怪问题。</summary>
    private static void FadeIn(UIElement target, int beginMs, int durationMs)
    {
        target.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}

/// <summary>
/// 分帧挂载器: 把"ItemsSource 一挂就在一次布局里同步生成全部卡片容器"拆成
/// 首屏一批 + 之后每帧一批。
///
/// 为什么需要它: 首页四个列表是 ItemsControl + WrapPanel(外面还套了自己的 ScrollViewer),
/// 这种结构**没有虚拟化**, 挂上几百条数据就同步生成几百张 VideoCard —— 用户点
/// 热门/排行榜/直播入口时"卡一下"的直接来源。分帧之后点击瞬间只生成一批(约 24 张),
/// 其余的插在渲染间隙里补, 眼睛看到的是"立刻出来了、多滚几下逐渐铺满", 而不是"卡死一下再全出来"。
///
/// 实现要点:
/// - 读源集合用 **索引** 而不是枚举器: 填充过程中源集合还可能被改(直播 tab 切回会 Clear+重灌、
///   滚动加载会 Add), ObservableCollection 的枚举器一遇到修改就抛异常, 索引不会。
/// - 填充中的 Add 不直接进视图(交给游标自然扫到, 保证顺序); 填充完的 Add 直接补
///   (就是普通的滚动加载, 一次 20 条, 逐个生成容器的负担可以接受)。
/// - Reset/Remove/Replace/Move 一律全量重同步: 这些只发生在"刷新"语义里, 简单正确优先。
/// </summary>
internal sealed class DeferredFill
{
    private readonly ItemsControl _list;
    private readonly IList _source;
    private readonly ObservableCollection<object> _view = new();
    private readonly int _chunk;
    private DispatcherOperation? _pending;
    private int _index;
    private bool _completed;
    private bool _disposed;

    /// <summary>每补完一批(含最后一批)触发一次; 页面用它逐步尝试恢复滚动位置</summary>
    public event EventHandler? Progress;

    public DeferredFill(ItemsControl list, IEnumerable source, int chunk)
    {
        _list = list;
        _source = (IList)source;
        _chunk = chunk;
    }

    public ItemsControl List => _list;
    public ObservableCollection<object> View => _view;

    /// <summary>还没填完且没被丢弃(此间自动加载要暂停, 否则会拿"内容不够一屏"的假象去拉下一页)</summary>
    public bool IsActive => !_completed && !_disposed;

    public void Start()
    {
        _list.ItemsSource = _view;
        if (_source is INotifyCollectionChanged ncc) ncc.CollectionChanged += OnSourceChanged;
        Pump();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Abort();
        if (_source is INotifyCollectionChanged ncc) ncc.CollectionChanged -= OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                // 填充中: 不动, 游标会自然扫到(直接塞会乱序 —— 游标后面还有没扫完的旧数据);
                // 已填完: 这就是普通的滚动加载/刷新追加, 直接补进视图
                if (_completed && e.NewItems != null)
                    foreach (var item in e.NewItems) _view.Add(item!);
                break;
            case NotifyCollectionChangedAction.Reset:
                _index = 0;
                _completed = false;
                _rebuildPending = true;    // 换列表整段推迟到下一拍, 见 _rebuildPending 的说明
                Pump();
                break;
            // 列表里没有删除/替换/移动的语义, 兜底走全量重同步
            default:
                _index = 0;
                _completed = false;
                _rebuildPending = true;
                Pump();
                break;
        }
    }

    /// <summary>
    /// "源列表被整体换掉了, 但视图还没换"。
    ///
    /// ★★ 为什么不能在这一拍就 Clear + 重填(2026-10-01, "推荐页点刷新后页面自己滑动" 的根):
    ///   刷新走的是 `RecommendItems.Clear()` → 再逐条 Add。Clear 发 Reset 时**源还是空的**,
    ///   此时若立刻 `_view.Clear()`, 视图就真的空了一拍 —— 视图一空, ScrollViewer 的内容高度
    ///   塌到 0, 当前滚动偏移被夹成 0/一小截(探针实测 700 → 140), 滚动条也消失一次;
    ///   等下一拍数据填回来, 用户的位置已经回不去了(而且夹小之后每一次补页都会把偏移再推一下,
    ///   看起来就是"页面自己在往下滑")。
    ///   推迟到下一拍: Clear 与第一批填充落在**同一个 dispatcher 回调**里, 中间没有布局/渲染,
    ///   内容高度根本不会塌 —— 列表从旧内容直接换成新内容。
    /// </summary>
    private bool _rebuildPending;

    /// <summary>
    /// 把源列表的下一批补进视图, 返回补了几条(Reset 的"先清后填"也在这里合并完成)。
    ///
    /// ★★ 关键点: `_view.Clear()` 与随后的第一批填充必须在**同一个调用**里落地。
    ///   本类原来的 Reset 分支是"当场 Clear + 排一帧再填", 中间隔了一次布局/渲染的机会 ——
    ///   ScrollViewer 会看到内容高度塌到 0 并把滚动偏移夹小(探针实测 700 → 140),
    ///   用户的位置就再也回不去了, 表现是"点刷新后页面自己滑走"。
    /// </summary>
    private void FillChunk()
    {
        if (_disposed) return;

        if (_rebuildPending)
        {
            _rebuildPending = false;
            _view.Clear();
        }

        var added = 0;
        while (added < _chunk && _index < _source.Count)
        {
            _view.Add(_source[_index]!);
            _index++;
            added++;
        }
        if (_index >= _source.Count) _completed = true;
        if (added > 0) Progress?.Invoke(this, EventArgs.Empty);
    }

    private void Pump()
    {
        if (_disposed || _completed || _pending != null) return;
        // Background 优先级: 让输入和渲染先走 —— 每一批都插在"用户还能操作"的间隙里
        _pending = _list.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _pending = null;
            FillChunk();
            if (!_completed) Pump();   // 还有存货, 继续排下一帧
        });
    }
}