using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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

public partial class HomePage : UserControl, IRefreshable
{
    private HomeViewModel? Vm => DataContext as HomeViewModel;
    /// <summary>
    /// 自动加载的预取阈值下限(DIP)。实际阈值按视口高度动态放大, 见 <see cref="AutoLoadThresholdFor"/>。
    ///
    /// ★★ 2026-11-14 从固定 240 改成"约 2 屏"(用户报"滑到未加载内容会顿 1 秒, 滑已加载的不顿"):
    ///   240px ≈ 一行卡片, 几乎滑到最底才发请求 —— AutoLoadThrottle 的稳定期/冷却 + 接口
    ///   200~500ms(撞上 -412 限流退避还有 1.5s)全部叠在"用户已经滚到底、滚不动"的那段等待上。
    ///   提前两屏预取, 网络等待与用户继续滚动的时间重叠, 到底时新内容已就位。
    ///   (FollowPage 的动态流早已用 800px 固定预取, 这里按视口算, 大小窗口都合适。)
    /// </summary>
    private const double AutoLoadThresholdMin = 480;

    /// <summary>预取阈值 = max(下限, 2 × 视口高)。视口量不出来时退回下限。</summary>
    private static double AutoLoadThresholdFor(ScrollViewer sv)
        => Math.Max(AutoLoadThresholdMin, sv.ViewportHeight * 2.0);

    /// <summary>
    /// 连续自动补页的闸门(见 <see cref="AutoFillGuard"/>)。
    ///
    /// ★★★ 2026-11-28: 原来这里是 `_autoFillCount++ / finally { _autoFillCount--; }` ——
    ///   await 一结束就减回去, 计数器只等于"在飞请求数", **永远到不了上限 5**, 连续补页
    ///   没有刹车。改成只增不减, 由用户滚动 / 切 tab 来重置(与搜索页同一套)。
    /// </summary>
    private readonly AutoFillGuard _autoFillGuard = new();

    /// <summary>自动加载的公共闸门: 缩放/布局变化时不立即翻页(见 <see cref="AutoLoadThrottle"/>)。</summary>
    private readonly AutoLoadThrottle _autoLoadThrottle = new();

    /// <summary>
    /// 点深浅色按钮时的鼠标位置(相对本页坐标系)。
    /// 圆形扩散动画需要它当圆心; Click 事件本身不带坐标, 所以在这里提前记下来。
    /// </summary>
    private Point? _themeClickPoint;

    /// <summary>
    /// 筛选弹层最近一次"被筛选按钮点击收起"的时刻(Environment.TickCount64)。
    /// 判定逻辑复用 <see cref="PopupToggle"/>: StaysOpen=False 的弹层会在 MouseDown
    /// 阶段先自行收起, 只判 IsOpen 取反会变成"永远打开"。
    /// </summary>
    private long? _filterPopupClosedAt;

    public HomePage()
    {
        InitializeComponent();
        PopularTab.Checked += OnPopularChecked;
        RecommendTab.Checked += OnRecommendChecked;
        MovieTab.Checked += OnMovieChecked;
        LiveTab.Checked += OnLiveChecked;
        BangumiTab.Checked += OnBangumiChecked;
        // 页面被重新挂载到可视树时刷新问候语(应用长时间挂在后台再切回来, 文案要跟着时段变)
        // ★ 页面常驻 PageLayer 后, 切页只改 Visibility —— 用 IsVisibleChanged 收"进入页面"信号;
        //   Loaded 仍然保留: 它负责**首次**挂载(那一次布局相关的初始化必须做)。
        Loaded += OnPageFirstLoaded;
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                // 离开首页(切页 / 被内嵌页盖住): 停掉消息未读轮询, 不在后台空转
                Vm?.StopMessageUnreadPolling();
                return;
            }

            // 回到首页: 消息未读可能已经变了(刚看完消息), 立刻拉一次并接着轮询
            Vm?.StartMessageUnreadPolling();
            Vm?.RefreshGreeting();
            RefreshThemeIcon();
            RefreshFilterButton();
            AttachFilterViews();
            // 从内嵌页(番剧详情)退回来时, 期间追过的番要立刻出现在「我的追番」里
            // (内嵌页关掉不会触发 tab 的 Checked, 见 HomeViewModel.RefreshBangumiIfFollowChanged)
            Vm?.RefreshBangumiIfFollowChanged();
            // 本页可能停在下滑位置被切走再切回来: 「刷新 / 回到顶部」的当前状态要按偏移重算一次
            UpdateTopRefresh(ScrollOf(CurrentTabFromVm() ?? HomeTabKind.Recommend));
        };
        // 主题可能被设置页改掉, 这里跟着更新图标。
        // 只在构造函数里订阅一次、不解绑: 本页实例被 MainWindow 长期缓存, 生命周期等同于应用,
        // 不存在泄漏; 反过来写成 Loaded/Unloaded 成对订阅反而会在"切走一次"之后永久失效。
        Svc.Theme.ThemeChanged += RefreshThemeIcon;
        // 筛选条件变化时让筛选按钮自己显示"已启用"状态
        FilterService.Instance.PropertyChanged += OnFilterChanged;
        // 屏蔽把一屏内容筛空后要补下一页, 否则底部会留一大片空白(刷新不会触发滚动事件)
        FilterService.Instance.Changed += OnFilterApplied;
        // 窗口缩放时若搜索卡正开着, 要让它重新对中(RenderTransform 是绝对位移, 不跟布局走)
        SizeChanged += (_, _) =>
        {
            RelayoutSearchCard();
            // ★ 窗口尺寸变了 ⇒ 标签位置变了 ⇒ 下划线要重新摆一次(它靠实测坐标定位)。
            //   不重摆的话, 窗口一缩放线就停在旧位置上了。
            if (_tabIndicatorPlaced) MoveTabIndicator(CurrentTabFromVm() ?? HomeTabKind.Recommend, animate: false);
        };
        // 输入任何文字时占位提示要立刻渐隐(聚焦淡出在 GotFocus 里, 这里管"打了字又删光")
        SearchKeywordBox.TextChanged += (_, _) => RefreshSearchPlaceholder();
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
        // 筛选弹层: 点一下展开, 再点一下收起(见 HookFilterPopupToggle 的说明)
        HookFilterPopupToggle();
        // 默认选中「推荐」(个性化内容优先), 触发一次初始加载
        RecommendTab.IsChecked = true;
        RefreshThemeIcon();
    }

    // ------------------------------------------------------------ 加大的窗口拖动区

    /// <summary>
    /// 首次挂载的初始化(只跑一次): 分帧填充器、搜索卡订阅、tab 下划线的首次摆放。
    /// 之后的"切页回来"由 IsVisibleChanged 处理(见构造函数), 不会重复跑这些。
    /// </summary>
    private void OnPageFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageFirstLoaded;
        // 构造函数里 RecommendTab.IsChecked=true 触发 ShowTab 时 DataContext 还没赋, EnsureFill
        // 早退了 —— 这里(DataContext 已就绪)补上当前 tab 的分帧填充器, 让首次推荐流也走分帧。
        if (CurrentTabFromVm() is { } cur) EnsureFill(cur);
        // DataContext 是外面赋进来的, 构造函数里还拿不到 VM, 所以订阅放在这里
        HookSearchCard();
        // 分段标签的下划线要等布局出来才摆得对(首次布局前 ActualWidth 都是 0);
        // 派到 Loaded 优先级 = 排在本次布局之后, 且首次摆放不带动画
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // 首次摆放(不带动画)。布局前 ActualWidth 还是 0, 所以必须等布局跑完。
            MoveTabIndicator(CurrentTabFromVm() ?? HomeTabKind.Recommend, animate: false);
        }));
    }

    /// <summary>
    /// 双击的第二下已经按下, 等抬起后再切窗口状态(见 <see cref="OnDragAreaMouseLeftButtonUp"/>)。
    /// </summary>
    private bool _maximizeOnUp;

    /// <summary>记下双击第二下按下的时刻, 防止 Up 丢失后标记一直挂着(见 Up 处理器里的兜底)。</summary>
    private int _maximizeArmedAt;

    /// <summary>
    /// 首页顶部那层"空闲拖动区"的按下处理: 长按左键拖动窗口、双击最大化/还原。
    ///
    /// ★ 为什么自己实现而不用 WindowChrome 的 CaptionHeight:
    ///   那是**窗口级**设置, 调大以后每个页面顶部都会变成标题栏 —— 而搜索页/历史页顶部
    ///   正好是输入框, 被吃掉上沿就点不动了(项目笔记里记着这条)。首页顶部是真空的,
    ///   所以只在首页补这一层。
    ///
    /// ★ 为什么 DragMove 要 try/catch: `Window.DragMove()` 内部走的是 Win32 的模态拖动循环,
    ///   它在"鼠标已经松开"或"拖动被系统打断(比如别的窗口抢了捕获)"时会抛
    ///   InvalidOperationException。这不是异常路径, 是正常竞态 —— 双击的第二下、
    ///   快速点一下就松开, 都很容易踩到。吞掉即可, 用户感知不到。
    ///
    /// ★ 双击判定: WPF 的 MouseButtonEventArgs.ClickCount 由系统按
    ///   `GetDoubleClickTime()` 与双击距离阈值算好, 直接用, 不要自己数时间戳。
    ///
    /// ★★★ 双击的窗口状态切换**必须放到 MouseUp 里**(2026-11-06 修 issue #50):
    ///   以前是在第二次 Down 里直接改 WindowState, 而窗口最大化会让整页在**第二次 Up 之前**
    ///   重排完 —— Up 是按抬起时的坐标重新命中测试的, 最大化前还在"标题空白区"的同一个屏幕坐标,
    ///   最大化后已经落到第一行视频卡片的封面上, 卡片就把这一下当成正常点击开始播放。
    ///   Down 里现在只置标记; Up 到达时窗口还没动, 命中测试仍落在本层, 状态切换在处理器内部
    ///   才发生, 后面不会再有鼠标消息漏给卡片。
    /// </summary>
    private void OnDragAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (win == null) return;

        if (e.ClickCount == 2)
        {
            // 只记下"待最大化", 真正的状态切换留给 Up —— 见上面 issue #50 的说明。
            // ★ 用 == 而不是 >=: 三连击的第三下(ClickCount=3)不该再把窗口切回去。
            _maximizeOnUp = true;
            _maximizeArmedAt = Environment.TickCount;
            e.Handled = true;
            return;   // ★ 这里不能再 DragMove, 否则第二次点击会先把窗口拖走
        }

        _maximizeOnUp = false;

        // 单击拖动。★ 必须判左键按下状态: 双击的第一下也会走到这里,
        //   若不判就会先 DragMove 把窗口拖走一小段, 双击的位置跟着漂。
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { win.DragMove(); }
        catch (InvalidOperationException) { /* 鼠标已松开/拖动被打断, 正常竞态 */ }
    }

    /// <summary>
    /// 双击的第二下抬起: 在这里才切最大化/还原。
    ///
    /// ★ 只要 Up 还在拖动区上, 命中测试拿到的就是本层(窗口此刻还没改状态), 这一下被吞掉,
    ///   视频卡片收不到任何鼠标事件 —— 这正是 issue #50 的修法。
    /// </summary>
    private void OnDragAreaMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_maximizeOnUp) return;
        _maximizeOnUp = false;

        // 兜底: 第二下按下后若把鼠标移出拖动区再松开, Up 会落到别处, 标记就永远挂着。
        // 超过 3s 的 Up 不认, 避免下一次别处漏进来的 Up 突然把窗口最大化。
        if (unchecked(Environment.TickCount - _maximizeArmedAt) > 3000) return;

        var win = Window.GetWindow(this);
        if (win == null) return;

        win.WindowState = win.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        e.Handled = true;
    }

    // ------------------------------------------------------------ 屏蔽

    /// <summary>
    /// 把首页的三个列表纳入全局屏蔽。
    /// 只挂一次(FilterService 内部会去重), 放在 Loaded 里是因为 DataContext 要等外部赋值。
    /// </summary>
    private void AttachFilterViews()
    {
        if (Vm == null) return;
        // ★ 2026-11-20: 传"这一面墙现在是否真的在显示" —— 四个 tab 常驻, 但一次只显示一个。
        //   隐藏 tab 的墙有几百个卡片容器, 规则一变就 Refresh 会同步销毁/重建整墙;
        //   现在它只记账, 切到那个 tab 时再由 FilterService.RequestPendingRefresh 补刷。
        FilterService.Instance.Attach(Vm.RecommendItems,
            isActive: () => IsVisible && Vm?.IsRecommendTab == true);
        FilterService.Instance.Attach(Vm.PopularItems,
            isActive: () => IsVisible && Vm?.IsPopularTab == true);
        FilterService.Instance.Attach(Vm.LiveItems,
            isActive: () => IsVisible && Vm?.IsLiveTab == true);
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

    /// <summary>
    /// 筛选弹层「点一下展开, 再点一下收起」的接线。
    ///
    /// ★ StaysOpen=False 的 WPF 弹层在用户点入口按钮时, 会在 **MouseDown 阶段先自动收起**
    ///   (对弹层而言那是"外部点击"), 等 Click 到达时 IsOpen 恒为 false ——
    ///   只写 `IsOpen = !IsOpen` 就会永远走"展开"分支, 这正是"再次点击不关闭"的原因。
    /// ★ 两层防线(与播放器弹层同一套):
    ///   ① PreviewMouseLeftButtonDown 到达时弹层还开着 → 直接收起并吞掉这一下;
    ///   ② WPF 已经先收起 → 用 IsOpen 变更回调记下"光标正压在按钮上"的收起时刻,
    ///      Click 时由 <see cref="PopupToggle.Decide"/> 认出这是同一次点击, 保持收起。
    /// ★ 用 GetCursorPos + PointToScreen(屏幕物理像素)而不是 IsMouseOver:
    ///   弹层握着鼠标捕获时, 入口按钮的 IsMouseOver 未必及时更新。
    /// </summary>
    private void HookFilterPopupToggle()
    {
        // HomePage 实例被 MainWindow 长期缓存(生命周期等同于应用), 这里的订阅无需退订。
        var descriptor = DependencyPropertyDescriptor.FromProperty(
            Popup.IsOpenProperty, typeof(Popup));
        EventHandler onIsOpenChanged = (_, _) =>
        {
            if (FilterPopup.IsOpen) return;
            // 点别处 / 点面板里的按钮 / Esc 引起的收起不记, 否则下次点筛选会被误吞一下。
            if (IsCursorOverElement(BtnFilter)) _filterPopupClosedAt = Environment.TickCount64;
        };
        descriptor.AddValueChanged(FilterPopup, onIsOpenChanged);

        BtnFilter.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!FilterPopup.IsOpen) return;
            FilterPopup.IsOpen = false;
            // 这一下已经被我们吞掉, 不会再走 Click —— 清掉刚记下的收起时刻,
            // 让用户紧接着再点一次时能立刻重新展开(不被 350ms 抑制窗口误吞)。
            _filterPopupClosedAt = null;
            e.Handled = true;
        };
    }

    /// <summary>
    /// 光标此刻是否落在元素范围内(GetCursorPos + PointToScreen, 都是屏幕物理像素)。
    /// 与播放器弹层里的同名判定同一套理由: 收起回调里 WPF 的命中测试未必已刷新。
    /// </summary>
    private static bool IsCursorOverElement(FrameworkElement element)
    {
        try
        {
            if (!GetCursorPos(out var pt)) return false;
            var tl = element.PointToScreen(new Point(0, 0));
            var br = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return pt.X >= tl.X && pt.X <= br.X && pt.Y >= tl.Y && pt.Y <= br.Y;
        }
        catch { return false; /* 元素已脱离可视树: 不记时刻即可 */ }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        var action = PopupToggle.Decide(
            FilterPopup.IsOpen,
            _filterPopupClosedAt,
            Environment.TickCount64);

        switch (action)
        {
            case PopupToggle.Action.Close:
                FilterPopup.IsOpen = false;
                break;
            case PopupToggle.Action.StayClosed:
                // 这一下正是"再点一下收起": WPF 已在 MouseDown 阶段收起, 保持收起不重开
                break;
            default:
                FilterPopup.IsOpen = true;
                FilterTitleBox.Focus();
                break;
        }
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        FilterService.Instance.Clear();
        RefreshFilterButton();
    }

    /// <summary>面板里点某条规则的 ×: 只撤销这一条(含它带来的 UID 规则)</summary>
    private void OnRemoveFilterRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FilterRule rule })
            FilterService.Instance.RemoveRule(rule);
    }

    // ------------------------------------------------------------ 关键词标签(issue #98)

    /// <summary>添加关键词后清空输入框并留在原地, 方便连着加几个词。</summary>
    private void AddFilterKeywords(FilterRuleKind kind, TextBox box)
    {
        var raw = box.Text;
        if (string.IsNullOrWhiteSpace(raw)) { box.Focus(); return; }

        FilterService.Instance.AddKeywords(kind, raw);
        box.Clear();
        box.Focus();
    }

    /// <summary>回车 = 添加。不用为加一个词先把鼠标挪到「添加」按钮上。</summary>
    private void OnFilterTitleBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddFilterKeywords(FilterRuleKind.TitleKeyword, FilterTitleBox);
    }

    private void OnFilterUpBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddFilterKeywords(FilterRuleKind.UpKeyword, FilterUpBox);
    }

    private void OnAddTitleKeywordClick(object sender, RoutedEventArgs e)
        => AddFilterKeywords(FilterRuleKind.TitleKeyword, FilterTitleBox);

    private void OnAddUpKeywordClick(object sender, RoutedEventArgs e)
        => AddFilterKeywords(FilterRuleKind.UpKeyword, FilterUpBox);

    /// <summary>导入期间不要再点第二次(接口要翻页, 连点会叠出两轮请求)。</summary>
    private bool _importingBlacklist;

    /// <summary>
    /// 把当前登录账号的 B 站黑名单导入「已屏蔽的 UP 主」(issue #98)。
    /// ★ 幂等: 已存在的规则不重复加, 重复点击只会得到"都已经在列表里"。
    /// ★ 走 <see cref="FilterService.BlockUps"/> 批量入口: 500 条逐条加会刷 500 次全列表。
    /// </summary>
    private async void OnImportBlacklistClick(object sender, RoutedEventArgs e)
    {
        if (_importingBlacklist) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再导入 B 站黑名单");
            return;
        }

        _importingBlacklist = true;
        try
        {
            var (ok, err, data) = await Svc.Api.GetBlacklistAsync();
            if (!ok)
            {
                Svc.Toast.Show(err ?? "获取黑名单失败");
                return;
            }
            if (data.Count == 0)
            {
                Svc.Toast.Show("B 站黑名单是空的");
                return;
            }

            var added = FilterService.Instance.BlockUps(
                data.Select(x => new BlockedUpEntry { Mid = x.Mid, Name = x.Name }));
            RefreshFilterButton();
            Svc.Toast.Show(added > 0
                ? $"已从 B 站黑名单导入 {added} 个 UP 主"
                : "黑名单里的 UP 主都已经在屏蔽列表里了");
        }
        finally
        {
            _importingBlacklist = false;
        }
    }

    /// <summary>
    /// 屏蔽条件刚落到列表上。等这次布局跑完再判断"当前页内容够不够一屏",
    /// 不够就补下一页 —— 否则一键屏蔽十几个 UP 之后, 页面会停在一大片空白上。
    ///
    /// ★ 布局回调里若分帧填充还在进行, TriggerAutoLoad 的 fill.IsActive 闸门会先挡住 ——
    ///   这是对的: 屏蔽 Refresh 让 DeferredFill 回到填充态, 不能拿"半途的高度"判"不够一屏"。
    ///   等它填完抛出的 ScrollChanged 会再次走到补页判断, 那一拍才是真的"还缺内容"。
    /// </summary>
    private void OnFilterApplied()
    {
        if (!IsLoaded || !IsVisible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded || !IsVisible || DataContext is not HomeViewModel vm) return;
            if (CurrentTabFromVm() is not { } tab) return;
            if (ScrollOf(tab) is not { } sv) return;
            _ = TriggerAutoLoad(sv, vm);
        }));
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
        if (ThemeIcon == null || BtnTheme == null) return;
        var dark = Svc.Theme.IsDark;
        // 图标显示"点了会切到哪种": 深色时给太阳, 浅色时给月亮。
        ThemeIcon.Data = (Geometry)FindResource(dark ? "SunIconGeometry" : "MoonIconGeometry");
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

    private void OnMovieChecked(object sender, RoutedEventArgs e)
    {
        ShowTab(HomeTabKind.Movie);
        if (DataContext is HomeViewModel vm) _ = vm.SwitchToMovieAsync();
    }

    private void OnLiveChecked(object sender, RoutedEventArgs e)
    {
        ShowTab(HomeTabKind.Live);
        if (DataContext is HomeViewModel vm) _ = vm.SwitchToLiveAsync();
    }

    private void OnBangumiChecked(object sender, RoutedEventArgs e)
    {
        ShowTab(HomeTabKind.Bangumi);
        if (DataContext is HomeViewModel vm) _ = vm.SwitchToBangumiAsync();
    }

    // ------------------------------------------------------------ 五个 tab 的列表(常驻, 不再摘挂)

    private enum HomeTabKind { Recommend, Popular, Movie, Live, Bangumi }

    /// <summary>
    /// 每个 tab 一个 DeferredFill, **建一次、永驻**。
    ///
    /// ★★★ 这是 2026-10-02 针对"切 tab 卡顿 / 从侧栏切回首页卡顿 / 切 tab 闪几下"的重构:
    ///   旧实现是"同一时刻只有一个 tab 挂着列表, 切 tab = 摘掉旧的 + 分帧重建新的"。
    ///   但四个列表都是**零虚拟化**的 ItemsControl + CardWallPanel —— 摘挂一次就等于
    ///   把几十上百张 VideoCard 容器整个销毁再重建, 每张卡还要重新触发 Cover 绑定 / 入场动画。
    ///   这套"省内存"的摘挂在三次用户反馈(卡顿/闪)面前是**负资产**: 它省下的内存(几十 MB 封面位图)
    ///   远不如它造成的每次切换都要重建整墙的观感代价。
    ///
    ///   新做法: 四个列表**常驻**, 切 tab 只靠 XAML 里 `Visibility="{Binding IsXxxTab}"` 切换显隐,
    ///   `ShowTab` 不再碰 ItemsSource。DeferredFill 只负责"数据第一次到位时分帧填充"(避免同步生成
    ///   95 张卡), 填完就停在原地, 切走切回来都不会再重建。
    ///   ★ 内存代价: 四个 tab 的卡片墙都活着, 封面位图常驻。CoverLoader 的 32MB LRU 缓存兜住一部分,
    ///     但四墙全点一遍后的常驻内存会比"摘挂版"高。这是**刻意**取舍 —— 用户三次报卡顿, 流畅优先。
    ///     若以后内存吃紧, 再考虑"离开首页页时才释放非当前 tab"这种页面级治理, 别在 tab 切换里做。
    /// </summary>
    private readonly Dictionary<HomeTabKind, DeferredFill> _fills = new();

    private ItemsControl? ListOf(HomeTabKind t) => t switch
    {
        HomeTabKind.Recommend => RecommendList,
        HomeTabKind.Popular => PopularList,
        HomeTabKind.Live => LiveList,
        // 影视页是**多段栏目**(我的追剧 + 正在热播 + 四个热播榜), 没有"一个列表"可填;
        // 追番页同样有两段分区(我的追番 / 番剧推荐)。返回 null 让 EnsureFill 直接返回 ——
        // 不给哪一段挂填充器, 各段就都按普通 ItemsControl 生成。代价是切到该页时一次
        // 生成较多海报卡; 可以接受, 因为海报卡标题走 TextTrimming(不换行), 没有
        // VideoCard 那种"换行排版"的昂贵测量 —— 那才是 CardWallPanel 要把全墙重测摊开的主因。
        _ => null,
    };

    private ScrollViewer? ScrollOf(HomeTabKind t) => t switch
    {
        HomeTabKind.Recommend => RecommendScroll,
        HomeTabKind.Popular => PopularScroll,
        HomeTabKind.Movie => MovieScroll,
        HomeTabKind.Live => LiveScroll,
        HomeTabKind.Bangumi => BangumiScroll,
        _ => null,
    };

    private System.Collections.IEnumerable? ItemsOf(HomeTabKind t, HomeViewModel vm) => t switch
    {
        HomeTabKind.Recommend => vm.RecommendItems,
        HomeTabKind.Popular => vm.PopularItems,
        HomeTabKind.Live => vm.LiveItems,
        _ => null,
    };

    /// <summary>每批补多少张卡片: 首屏一批(约 4~5 列 × 5 行), 之后每帧一批。</summary>
    private const int FillChunkSize = 24;

    /// <summary>
    /// 切换 tab: **只切显隐 + 摆下划线**, 不重建列表(见 _fills 的说明)。
    /// 列表的显隐由 `Visibility="{Binding IsXxxTab}"` 在 VM 的 SetTab 里驱动, 这里不需要也不该碰。
    /// </summary>
    private void ShowTab(HomeTabKind tab)
    {
        EnsureFill(tab);
        // ★ 换 tab = 换了一整批内容: 连续补页额度重开, 否则在 A tab 上补满 5 次后,
        //   切到 B tab 就再也补不动了(两个 tab 的滚动事件共用一个闸门)。
        _autoFillGuard.Reset();
        // 切 tab 后「刷新 / 回到顶部」要跟着新列表的滚动位置走(那个 tab 可能本来就停在顶部)
        UpdateTopRefresh(ScrollOf(tab));
        // 分段标签的下划线跟着选中项走
        MoveTabIndicator(tab);
        // 这个 tab 在别的页面显示期间可能被新的屏蔽规则标成了"待刷"(见 FilterService 的 isActive):
        // 排到 Background 补刷 —— VM 的 tab 标志在这之后由 SwitchToXxxAsync 同步写好。
        FilterService.Instance.RequestPendingRefresh();
        // ★ 2026-10-12 用户要求"彻底移除浮动动画" —— 切 tab 现在**没有任何转场动画**,
        //   只切显隐与下划线。早先的"闪白幕布"与"卡片上浮"都已删除。
    }
    /// <summary>
    /// 保证某个 tab 的列表已经挂上分帧填充器。**每个 tab 只建一次**, 之后永远复用 ——
    /// 这就是"切 tab 不重建"的关键(旧版在 ShowTab 里每次都 Attach/Detach)。
    /// </summary>
    private void EnsureFill(HomeTabKind tab)
    {
        if (_fills.ContainsKey(tab)) return;
        if (DataContext is not HomeViewModel vm) return;
        var list = ListOf(tab);
        if (list == null) return;
        var src = ItemsOf(tab, vm);
        if (src == null) return;

        // 分帧挂载: 首批在下一帧(Background 优先级, 点击的布局/渲染先走), 其余按帧补。
        // 为什么必须这样: 三个列表都是 ItemsControl + WrapPanel(外层还有自己的 ScrollViewer),
        // **没有任何虚拟化** —— ItemsSource 一挂, 几十上百张 VideoCard 容器在一次布局里同步生成,
        // 表现就是"点热门/直播入口卡一下"。把生成时机接管成分帧。
        // 注意 XAML 里三份 ItemsSource 是初始绑定好的: 折叠状态的 ItemsControl 没被测量过,
        // 容器其实还没生成, 所以这里把绑定替换成代理视图没有浪费。
        var fill = new DeferredFill(list, src, FillChunkSize);
        _fills[tab] = fill;
        fill.Start();
    }

    // ------------------------------------------------------------ 分段标签的选中下划线
    //
    // 参考图: 选中的标签字变粉, 它下面有一根短横线; 换标签时那根线要**滑过去**。
    // 只动画一个属性就够 —— 线的宽度是固定的(见 HomePage.xaml 里的 TabIndicator), 所以只要把 X
    // 从旧位置挪到新位置; 不动画宽度也就不会"中途忽胖忽瘦"。
    // 位置必须实测(TransformToAncestor): 标签文字长短不同(推荐/热门 vs 影视), 宽度不一样,
    // 靠固定间距是推不出来的。

    /// <summary>下划线滑过去的时长。比搜索卡那两段短 —— 它只是个小指示器, 不该抢戏。</summary>
    private const int TabIndicatorMs = 220;

    /// <summary>下划线还没摆过位。首次布局之前 ActualWidth 都是 0, 那时摆了也是错的。</summary>
    private bool _tabIndicatorPlaced;

    private RadioButton? TabButtonOf(HomeTabKind tab) => tab switch
    {
        HomeTabKind.Recommend => RecommendTab,
        HomeTabKind.Popular => PopularTab,
        HomeTabKind.Movie => MovieTab,
        HomeTabKind.Live => LiveTab,
        HomeTabKind.Bangumi => BangumiTab,
        _ => null
    };

    /// <summary>
    /// 把选中下划线挪到某个标签下面。animate=false 用于首次摆放(不该有"滑进来"的动画)。
    ///
    /// ★ 下划线的宽度是**固定的 20px**(见 HomePage.xaml), 所以这里只需要动 X 一个属性 ——
    ///   这正是"改回下划线"最大的好处: 药丸版本要同时动"位置 + 宽度", 而那是 WPF 里
    ///   最容易出错的组合(宽度动画的起点会读到被动画接管的旧值, 表现为"方块从最左边飞来";
    ///   改用 ScaleX 又有缩放锚点偏移的坑, 5 个标签里只有宽度变化的那两档会偏)。
    ///   宽度固定 ⇒ 不存在"位置与宽度不同步"这件事, 一条 DoubleAnimation 就够了。
    ///
    /// ★ 位置必须**实测**(TransformToAncestor): 五个标签文字长短不同(推荐/热门/影视/直播/追番),
    ///   宽度不一样, 靠固定间距推不出来。
    /// ★ 线要**在文字下面居中**: 所以 X 要在标签左边距上再加 (标签宽 − 线宽)/2。
    ///   少加这一项, 线会贴到标签左边缘, 看起来像"没对齐"。
    /// </summary>
    private void MoveTabIndicator(HomeTabKind tab, bool animate = true)
    {
        var target = TabButtonOf(tab);
        if (target == null || HomeTabs.ActualWidth <= 0 || target.ActualWidth <= 0)
        {
            // 布局还没就绪: 记下来, Loaded 里会再摆一次
            _tabIndicatorPlaced = false;
            return;
        }

        // 标签左边距(相对标签行) + 居中偏移。下划线是 TabsRow 的子元素、标签行的兄弟,
        // 两者都没有 Margin, 所以坐标原点一致, 直接可用。
        var left = target.TransformToAncestor(HomeTabs).Transform(new Point(0, 0)).X
                   + (target.ActualWidth - TabIndicator.Width) / 2;

        if (!animate || !_tabIndicatorPlaced)
        {
            ClearIndicatorAnimations();
            TabIndicatorTrans.X = left;
            _tabIndicatorPlaced = true;
            return;
        }

        // ★★ 顺序很关键: **先读当前 X, 再清动画**。
        //   反过来先清动画, X 会立刻回落到"上次动画开始前的本地值"(0),
        //   于是新动画从 0 起步 —— 表现就是"线先跳到最左边再滑回来"(实测从
        //   影视切到别的档时会看到线从左上角飞过来, 因为 0 就是最左)。
        //   先读则拿到的是"上一次动画的当前值", 正是我们要的连续起点。
        var from = TabIndicatorTrans.X;
        ClearIndicatorAnimations();
        TabIndicatorTrans.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(from, left, TimeSpan.FromMilliseconds(TabIndicatorMs))
            {
                // ★ EaseOut: 起步快、收尾缓。位移动画用缓出比线性"更有质感" ——
                //   线性看起来像生硬地平移, 缓出像是滑过去停在目标上。
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>清掉下划线上残留的位移动画</summary>
    private void ClearIndicatorAnimations()
        => TabIndicatorTrans.BeginAnimation(TranslateTransform.XProperty, null);

    /// <summary>当前 VM 认为选中的 tab(显隐由它驱动, 这里只做"读"用途)</summary>
    private HomeTabKind? CurrentTabFromVm()
    {
        if (DataContext is not HomeViewModel vm) return null;
        if (vm.IsPopularTab) return HomeTabKind.Popular;
        if (vm.IsMovieTab) return HomeTabKind.Movie;
        if (vm.IsLiveTab) return HomeTabKind.Live;
        if (vm.IsBangumiTab) return HomeTabKind.Bangumi;
        if (vm.IsRecommendTab) return HomeTabKind.Recommend;
        return null;
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
    }

    /// <summary>错误条里的「重试」和 F5 都走这一条(与右下角「刷新 / 回到顶部」的刷新分支相同)。</summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshCurrentTabAsync();

    /// <summary>F5: 刷新当前 Tab(与右下角「刷新 / 回到顶部」按钮同一条路径, 刷完滚回顶部)。</summary>
    public void Refresh() => _ = RefreshCurrentTabAsync();

    /// <summary>
    /// 「分区」入口(2026-10-12 用户要求"放左下角", 入口与展开位置一致)。
    ///
    /// ★ 按钮在 **MainWindow 的左侧导航栏底部**(XAML 里), 这里只负责接收点击:
    ///   它属于**窗口级**功能(分区面板盖住整个窗口、含导航栏), 而不是首页的内容,
    ///   所以按钮不该放在 HomePage 的右下角工具栏里。
    /// ★ 用 <see cref="SvcWindow.Main"/> 而不是"往上找父窗口":
    ///   MainWindow 把页面挂在 PageLayer 里时, 父级链上未必有 MainWindow 那个类型;
    ///   SvcWindow 是 App 启动时登记的全局引用, 哪儿都能拿到。
    /// ★ 主窗口不可用时(启动早期/探针环境)静默跳过: 分区面板是导航增强, 缺了不该抛。
    /// </summary>
    public void OpenPartitionFromNav() => SvcWindow.Main?.OpenPartition();

    /// <summary>通用: 滚动到底部 / 内容不够一屏时, 自动加载更多</summary>
    private async Task TriggerAutoLoad(ScrollViewer sv, HomeViewModel vm)
    {
        // ★ 连续补页上限(见 AutoFillGuard): 补了还是不满一屏时不能无限自补。
        if (!_autoFillGuard.CanFill) return;
        if (vm.LoadingMore || vm.Loading) return;

        // ★ 防"延迟检查跑到别的 tab 上"(2026-11-09): AutoLoadThrottle 会把布局变化的判断
        //   推迟 300ms, 这期间用户可能已经切了 tab。那种情况下不能用**旧 tab 的滚动位置**
        //   去决定要不要给**新 tab** 加载。
        if (CurrentTabFromVm() is { } activeTab && !ReferenceEquals(ScrollOf(activeTab), sv)) return;

        // 分帧填充进行中: 每补一批 ScrollableHeight 都在长, 这时判"内容不够一屏"是错的,
        // 会带着空列表去拉下一页; 等填充完(补帧引发的 ScrollChanged 会再来)再判。
        // ★ 四个 tab 各有各的 fill, 这里要判"当前 tab 那个"还在不在补帧(不是某个全局的)。
        if (CurrentTabFromVm() is { } tab && _fills.TryGetValue(tab, out var f) && f.IsActive) return;

        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
        if (distanceToBottom > AutoLoadThresholdFor(sv) && !contentShort) return;

        // ★ 冷却闸门: 缩放/布局变化会连着发很多 ScrollChanged, 稳定后也可能连续判中。
        //   没有它的话一次拖窗口能触发好几页(见 AutoLoadThrottle)。
        if (!_autoLoadThrottle.TryEnter()) return;

        // ★ 只累加、不在 finally 里回退 —— 回退就等于没有上限(见 AutoFillGuard)。
        _autoFillGuard.OnFilled();
        await vm.LoadMoreAsync();
    }

    private void PopularScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateTopRefresh((ScrollViewer)sender);
        if (DataContext is not HomeViewModel vm) return;
        var sv = (ScrollViewer)sender;
        // ★ 必须先把布局变化那一类挡掉: 卡片墙补锚点自己调的 ScrollToVerticalOffset 也带
        //   VerticalChange, 那是程序化滚动, 不能拿它给连续补页"充值"(见 AutoFillGuard)。
        if (_autoLoadThrottle.ShouldSkipLayoutChange(e, () => _ = TriggerAutoLoad(sv, vm))) return;
        if (Math.Abs(e.VerticalChange) > 0.5) _autoFillGuard.Reset();
        _ = TriggerAutoLoad(sv, vm);
    }

    private void RecommendScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateTopRefresh((ScrollViewer)sender);
        if (DataContext is not HomeViewModel vm) return;
        var sv = (ScrollViewer)sender;
        if (_autoLoadThrottle.ShouldSkipLayoutChange(e, () => _ = TriggerAutoLoad(sv, vm))) return;
        if (Math.Abs(e.VerticalChange) > 0.5) _autoFillGuard.Reset();
        _ = TriggerAutoLoad(sv, vm);
    }

    private void LiveScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateTopRefresh((ScrollViewer)sender);
        if (DataContext is not HomeViewModel vm) return;
        var sv = (ScrollViewer)sender;
        if (_autoLoadThrottle.ShouldSkipLayoutChange(e, () => _ = TriggerAutoLoad(sv, vm))) return;
        if (Math.Abs(e.VerticalChange) > 0.5) _autoFillGuard.Reset();
        _ = TriggerAutoLoad(sv, vm);
    }

    /// <summary>点「我关注的主播」头像 → 直接进直播间(与直播卡片同一个入口)</summary>
    private void OnFollowingLiveClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoItem v) return;
        if (v.RoomId <= 0) return;
        Svc.Player.PlayLive(v.RoomId, v.Title);
        e.Handled = true;
    }

    /// <summary>影视页不自动翻页(栏目一次给完), 但仍然要让「刷新 / 回到顶部」跟着滚动位置切换</summary>
    private void MovieScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateTopRefresh((ScrollViewer)sender);
    /// <summary>
    /// 追番页与其它滚动列表同款: 滚到底自动翻下一批推荐。
    /// TriggerAutoLoad 内部会判当前 tab 是不是追番; 不是的话 LoadMoreAsync 也什么都不做
    /// (它自己按 IsBangumiTab 分流), 所以这里不需要额外判。
    ///
    /// ★ 这里**额外**要告诉 VM "用户当前看得见哪一段"(仍在用): 「我的追番」是固定内容,
    ///   不翻页; 停在它上面时加载目标要顺延到「番剧推荐」(见 LoadMoreAsync 的追番分支)。
    /// ★ 2026-11-12 合并国创后, 原先那套"只往看得见的那栏追加"的规则已不需要 ——
    ///   现在只有一栏会翻页, 追加位置天然在视口下方。
    /// </summary>
    private void BangumiScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateTopRefresh((ScrollViewer)sender);
        if (DataContext is not HomeViewModel vm) return;
        var sv = (ScrollViewer)sender;
        SyncBangumiActiveSection(sv, vm);
        if (_autoLoadThrottle.ShouldSkipLayoutChange(e, () => _ = TriggerAutoLoad(sv, vm))) return;
        if (Math.Abs(e.VerticalChange) > 0.5) _autoFillGuard.Reset();
        _ = TriggerAutoLoad(sv, vm);
    }

    /// <summary>
    /// 用**视口底部**落在哪一段来判定"用户当前在看哪一段"。
    ///
    /// ★ 2026-11-12 合并国创之后这里只剩两段(「我的追番」/「番剧推荐」), 判段因此大幅简化 ——
    ///   原先那个 GuochuangRecommendList 的探测和"向上回看"的三段循环都删掉了。
    /// ★ 为什么取底部而不是中线: 自动加载发生在"滚到底"这一刻, 底部所在的那一段正是用户
    ///   马上要继续看的。取中线的话, 一屏里同时露出两段时会判到靠上的那一段。
    /// ★ 集合空时对应 ItemsControl 是 Collapsed(ActualHeight=0), span 返回空区间自动跳过,
    ///   不会误判成"看的是这一段"。
    /// </summary>
    private void SyncBangumiActiveSection(ScrollViewer sv, HomeViewModel vm)
    {
        if (sv.ActualHeight <= 0) return;
        var probeY = sv.ViewportHeight - 1;

        // 段的可见区间(相对视口): [top, bottom)。
        // ★ 元组字段名必须在 lambda 的返回类型上显式声明, 否则编译器推成普通 (double,double),
        //   下面访问 .top/.bottom 直接编译不过。
        var span = (FrameworkElement? el) =>
        {
            if (el == null || el.ActualHeight <= 0) return (top: double.MinValue, bottom: double.MinValue);
            var top = el.TranslatePoint(new Point(0, 0), sv).Y;
            return (top: top, bottom: top + el.ActualHeight);
        };
        var containsBottom = ((double top, double bottom) s) =>
            s.top <= probeY && probeY < s.bottom;

        var my = span(MyBangumiList);
        var rec = span(BangumiRecommendList);

        HomeViewModel.BangumiSection section;
        if (containsBottom(my)) section = HomeViewModel.BangumiSection.My;
        else if (containsBottom(rec)) section = HomeViewModel.BangumiSection.Recommend;
        else
        {
            // 底部落在两段之间的空隙 / 已越过最后一个列表的末尾:
            // 向上回看 —— 底部之上、离得最近的那段就是用户正在看末尾的段。
            var best = HomeViewModel.BangumiSection.None;
            var bestTop = double.MinValue;
            foreach (var (s, sec) in new[] { (my, HomeViewModel.BangumiSection.My),
                                             (rec, HomeViewModel.BangumiSection.Recommend) })
            {
                if (s.top > bestTop && s.top <= probeY) { bestTop = s.top; best = sec; }
            }
            if (best == HomeViewModel.BangumiSection.None)
                return;   // 两段一个都不可见(内容不足一屏/正在布局): 保持上一次的判定, 别来回抖
            section = best;
        }

        vm.ActiveBangumiSection = section;
    }

    // ------------------------------------------------------------ 回到顶部 / 刷新

    /// <summary>
    /// 把当前 tab 的滚动偏移交给共用的 TopRefreshButton:
    /// 顶部显示刷新, 下滑超过阈值显示回到顶部(阈值/回差在控件内部)。
    /// </summary>
    private void UpdateTopRefresh(ScrollViewer? sv) => BtnTopRefresh.UpdateForOffset(sv);

    /// <summary>
    /// 二合一按钮的点击:
    ///   · 处于"回到顶部"状态 → 把**当前 tab 的**列表滚回顶部;
    ///   · 处于"刷新"状态 → 走原来的刷新路径(含刷完滚回顶部)。
    /// 必须按 tab 取, 不能拿 sender 之外的那个 —— 五个 ScrollViewer 常驻同一棵树, 切走的只是被折叠。
    /// </summary>
    private void OnTopRefreshClick(object sender, RoutedEventArgs e)
    {
        if (BtnTopRefresh.ShowsBackToTop)
        {
            var tab = CurrentTabFromVm() ?? HomeTabKind.Recommend;
            ScrollOf(tab)?.ScrollToTop();
            return;
        }

        _ = RefreshCurrentTabAsync();
    }

    // ------------------------------------------------------------ 顶部居中搜索卡
    //
    // 2026-10-03 用户要求"搜索框固定在首页正上方的中间位置, 保留展开动画":
    //   · 收起态 = 页面水平正中那条灰的搜索框(XAML 里 Center 对齐 + 顶部 16);
    //   · 展开 = 原地往下长(宽度 440→520、高度 48→卡片高), 露出 搜索历史 / 热搜。
    //   居中对齐下宽度变化是"两边对称地长", **中心天然不动** —— 旧版"右对齐 + 平移补偿"
    //   那套(PanXFor / SearchCardTrans.X 关键帧)整体废弃, SearchCardTrans 恒为 0。
    //
    // 为什么展开仍用关键帧而不是"跑完一步再 BeginAnimation 下一步": 同一个属性上第二次
    // BeginAnimation 会**立刻**把值顶到新动画的 From(BeginTime 只推迟开始, 不推迟生效),
    // 前一段会被截断 —— 这条在滚动弹幕上踩过, 见 PlayerWindow 里弹幕的注释。

    private const double SearchBoxW = 440;
    private const double SearchBoxH = 48;
    private const double SearchCardW = 520;
    /// <summary>内容四周的余量: 免得"刚好放满"因为 1px 取整就冒出滚动条</summary>
    private const double SearchCardSlack = 8;

    /// <summary>搜索框与内容框之间的间隙(2026-09-26 起两个框是分开的, XAML 里内容框的 Margin 上边距同值)</summary>
    private const double SearchContentGap = 10;

    /// <summary>搜索框聚焦时描边淡入的最终不透明度(2026-09-26 起是描边、不再是光晕)</summary>
    private const double SearchRingOpacity = 1.0;

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
        // 展开即视为"选中了搜索框": 占位提示渐隐
        RefreshSearchPlaceholder();
        // 热搜是懒加载的: 现在就去拉, 等卡片展开完基本已经就位
        _ = SearchVm?.EnsureHotSearchAsync();
        // 高度在变, 这段别让 Auto 闪出滚动条(动画结束再切回 Auto)
        SearchCardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var w1 = SearchCardW;
        var h1 = ComputeOpenHeight();
        var expand = SearchExpandMs;

        // 居中对齐: 中心天然不动, 只有宽度/高度在长(顶部固定, 卡片往下长高)。
        // SearchCardTrans 恒为 0, 不再有 X 平移段。
        SearchCard.BeginAnimation(FrameworkElement.WidthProperty, Keys(
            K(0, SearchBoxW), K(expand, w1, ease)));

        var height = Keys(K(0, SearchBoxH), K(expand, h1, ease));
        height.Completed += (_, _) => OnSearchOpened();
        SearchCard.BeginAnimation(FrameworkElement.HeightProperty, height);

        // 内容框随展开淡入 —— 底色硬切在动画里很跳。搜索框自己不用淡入: 它一直都在,
        // 展开只是"旁边多长出一个框"。
        FadeIn(SearchContentBox, 0, expand);
        // 框里的内容等长开一点再露, 看着像"被框一点点让出来"
        FadeIn(SearchCardScroll, (int)(expand * 0.35), (int)(expand * 0.65));
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

        // 拿当前值当起点: 动画被打断时就从这一刻的尺寸收回去, 不跳
        var w0 = SearchCard.Width > 0 ? SearchCard.Width : SearchBoxW;
        var h0 = SearchCard.Height > 0 ? SearchCard.Height : SearchBoxH;

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

        // 居中对齐: 收起只有尺寸在缩, 没有位移
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
    /// 关键帧的起点就不再是收起值, 窗口缩放后卡片也回不到收起尺寸。
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
        SearchCardTrans.X = 0;   // 居中对齐后恒为 0(保留元素本身, 动画代码仍会碰它)
        SearchContentBox.Opacity = 0;
        SearchCardScroll.Opacity = 0;
        SearchCardBgHover.Opacity = 0;
        SearchInputRing.Opacity = 0;
        SearchCardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        SearchScrim.Visibility = Visibility.Collapsed;
        // 卡片收起后占位提示要回来(淡入)
        RefreshSearchPlaceholder();
    }

    /// <summary>
    /// 窗口尺寸变了: 卡片开着也不需要补偿 —— 居中对齐跟着布局走,
    /// 中心永远在页面正中(保留空实现是给 SizeChanged 的订阅留个语义清晰的落点)。
    /// </summary>
    private void RelayoutSearchCard()
    {
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
    {
        FadeSearchRing(true);
        RefreshSearchPlaceholder();
    }

    private void OnSearchKeywordLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FadeSearchRing(false);
        RefreshSearchPlaceholder();
    }

    private void FadeSearchRing(bool on)
        => SearchInputRing.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchInputRing.Opacity, on ? SearchRingOpacity : 0,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

    // ---------------- 占位提示("搜索你感兴趣的视频") ----------------
    // 2026-10-03 用户要求: 搜索框里有这句提示, 选中(聚焦)时渐隐消失。
    // 显隐三条件: 有文字 / 聚焦中 / 卡片展开中 —— 任一成立就藏。
    // ★ 淡出而不是瞬间消失: 与搜索卡其它淡入淡出是同一套语言。

    /// <summary>占位提示渐隐/渐显的目标透明度。0 = 藏, 1 = 显。</summary>
    private const double PlaceholderVisibleOpacity = 1.0;
    private const int PlaceholderFadeMs = 160;

    private void RefreshSearchPlaceholder()
    {
        var visible = string.IsNullOrEmpty(SearchKeywordBox.Text)
                      && !SearchKeywordBox.IsKeyboardFocusWithin
                      && !_searchOpen;
        SearchPlaceholder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchPlaceholder.Opacity, visible ? PlaceholderVisibleOpacity : 0,
                TimeSpan.FromMilliseconds(PlaceholderFadeMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

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
/// 为什么需要它: 首页几个列表是 ItemsControl + WrapPanel(外面还套了自己的 ScrollViewer),
/// 这种结构**没有虚拟化**, 挂上几百条数据就同步生成几百张 VideoCard —— 用户点
/// 热门/直播入口时"卡一下"的直接来源。分帧之后点击瞬间只生成一批(约 24 张),
/// 其余的插在渲染间隙里补, 眼睛看到的是"立刻出来了、多滚几下逐渐铺满", 而不是"卡死一下再全出来"。
///
/// 实现要点:
/// - ★ 数据源是**默认 CollectionView**(而不是原始集合): 全局屏蔽(FilterService)装在视图的
///   Filter 谓词上, 直接按索引复制原始集合会把被屏蔽的内容原样搬进界面 —— 那正是
///   "右键屏蔽了、右下角也显示已屏蔽, 首页却还在推荐"的根(2026-11-06 用户报)。
///   从视图读, 拿到的天然就是过滤后的可见项; 屏蔽条件变化时 FilterService 会 Refresh 视图,
///   视图发 Reset, 下面的重建逻辑正好接住。
/// - 读数据源用 **索引快照** 而不是枚举器: 填充过程中数据源还可能被改(直播 tab 切回会
///   Clear+重灌、滚动加载会 Add、屏蔽刷新会 Reset), 枚举器一遇到修改就抛异常, 索引不会。
/// - 填充中的 Add 不直接进视图(交给游标自然扫到, 保证顺序); 填充完的 Add 进**待补队列**,
///   由同一个泵按小批(8 张/帧)分帧补 —— 2026-11-14 用户报"滑到未加载内容顿 1 秒"的另一半:
///   原来加载更多的 20~30 条在一个回调里逐条 Add, 一次布局同步生成 30 张 VideoCard
///   (标题换行排版最贵), 就是"新卡片出现瞬间整界面冻结"的来源。首屏 24 张/批在页面
///   刚出现时可接受, 滚动中的追加必须更小批, 帧间隔才贴得住 60fps。
/// - Reset/Remove/Replace/Move 一律全量重同步: 这些只发生在"刷新/屏蔽变化"语义里, 简单正确优先。
/// </summary>
internal sealed class DeferredFill
{
    private readonly ItemsControl _list;
    private readonly IList _source;
    private readonly ICollectionView? _viewSource;
    private readonly ObservableCollection<object> _view = new();
    private readonly int _chunk;

    /// <summary>"加载更多"追加时的**初始**每帧批量; 之后按帧间隔自适应(见 <see cref="_appendBatch"/>)。</summary>
    private const int AppendBatchStart = 6;
    private const int AppendBatchMin = 2;
    private const int AppendBatchMax = 16;

    /// <summary>
    /// 当前每帧追加几张。**按帧间隔自适应**(见 <see cref="OnPumpTiming"/>) ——
    /// 固定值在 Debug 构建/弱机器上会偏大(一帧插入的容器更贵), 在强机器上又偏小(追加拖太久)。
    /// </summary>
    private int _appendBatch = AppendBatchStart;

    /// <summary>上一次泵回调的时刻, 用来量"上一帧实际花了多久"(含布局/渲染, 比只量 Add 准)。</summary>
    private long _lastPumpTick;

    /// <summary>已填完后收到的追加条目(按到达顺序), 由 <see cref="FillChunk"/> 分帧搬进视图。</summary>
    private readonly Queue<object> _appends = new();

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
        // 默认视图拿不到时(极少数集合)存 null —— FillChunk 会按"没有视图"处理,
        // 行为与旧版(直接读原始集合)一致, 不至于让填充整个失效。
        _viewSource = CollectionViewSource.GetDefaultView(source);
        _chunk = chunk;
    }

    public ItemsControl List => _list;
    public ObservableCollection<object> View => _view;

    /// <summary>
    /// 还没填完 / 还有待补的追加 / 没被丢弃(此间自动加载要暂停, 否则会拿"内容不够一屏"的假象去拉下一页)。
    /// ★ 待补队列也算"进行中": 追加分帧期间内容高度一格格在长, 判据同样不可信。
    /// </summary>
    public bool IsActive => (!_completed || _appends.Count > 0) && !_disposed;

    public void Start()
    {
        _list.ItemsSource = _view;
        // 订阅**视图**而不是原始集合: 屏蔽 Refresh 发的 Reset 只在视图上, 原始集合收不到。
        // 视图的 CollectionChanged 会把原始集合自己的 Add/Reset 一起带过来, 不会漏。
        if (_viewSource is INotifyCollectionChanged ncc) ncc.CollectionChanged += OnSourceChanged;
        Pump();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Abort();
        if (_viewSource is INotifyCollectionChanged ncc) ncc.CollectionChanged -= OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                // 填充中: 不动, 游标会自然扫到(直接塞会乱序 —— 游标后面还有没扫完的旧数据);
                // 已填完: 普通的滚动加载追加 —— 进队列由泵分帧补(见类注释, 不再当场逐条 Add)。
                if (_completed && e.NewItems != null)
                {
                    foreach (var item in e.NewItems) _appends.Enqueue(item!);
                    Pump();
                }
                break;
            case NotifyCollectionChangedAction.Reset:
                _index = 0;
                _completed = false;
                _appends.Clear();          // 整批换掉: 旧追加不再有意义, 快照游标会重新扫到
                _rebuildPending = true;    // 换列表整段推迟到下一拍, 见 _rebuildPending 的说明
                Pump();
                break;
            // 列表里没有删除/替换/移动的语义, 兜底走全量重同步
            default:
                _index = 0;
                _completed = false;
                _appends.Clear();
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
    /// 把数据源(过滤后的视图)的下一批补进视图, 返回补了几条(Reset 的"先清后填"也在这里合并完成)。
    ///
    /// ★★ 关键点 1: `_view.Clear()` 与随后的第一批填充必须在**同一个调用**里落地。
    ///   本类原来的 Reset 分支是"当场 Clear + 排一帧再填", 中间隔了一次布局/渲染的机会 ——
    ///   ScrollViewer 会看到内容高度塌到 0 并把滚动偏移夹小(探针实测 700 → 140),
    ///   用户的位置就再也回不去了, 表现是"点刷新后页面自己滑走"。
    ///
    /// ★★ 关键点 2: 每次进来先对视图做一份**索引快照**再按游标切块。
    ///   不能直接对视图枚举着数 —— 填充进行中屏蔽条件可能再变(Refresh → Reset),
    ///   视图枚举器遇到修改会抛异常; 快照只在这一次调用内存活, 天然免疫中途变化。
    /// </summary>
    private void FillChunk()
    {
        if (_disposed) return;

        // ★★★ 2026-11-14(用户报"往下滑到未加载内容卡 1 秒"): 追加阶段**不再建数据源快照**。
        //   旧写法每进一次 FillChunk 都要把整个过滤视图枚举一遍(每个条目都要过一遍屏蔽谓词),
        //   而追加时每帧都会进来 —— 墙滚到几千张之后, 每追加一页就白跑好几次全表过滤。
        if (_completed && !_rebuildPending)
        {
            DrainAppends();
            return;
        }

        // 过滤后的可见项快照。视图存在就用视图(屏蔽生效的关键); 拿不到时退回原始集合。
        // ★ 2026-11-20: 快照提到重建分支之前 —— 屏蔽 Refresh 发的 Reset 要先拿它和当前画面
        //   做增量对账, 对不上才退回"整批清空 + 分帧重填"。
        IList snapshot;
        if (_viewSource != null)
        {
            var visible = new List<object>();
            foreach (var item in _viewSource) visible.Add(item!);
            snapshot = visible;
        }
        else
        {
            snapshot = _source;
        }

        if (_rebuildPending)
        {
            _rebuildPending = false;
            // ★★★ 2026-11-20(用户报"屏蔽/取消屏蔽卡 1 秒再刷新"):
            //   屏蔽规则变化只是"可见集合少了几条 / 多回几条"。旧写法无条件 `_view.Clear()`
            //   再按 24 张/帧重填 —— 首页几面墙各有上百张卡, Clear 会在同一拍里把它们
            //   全部销毁, 重填又要几百毫秒, 用户看到的就是"点完屏蔽先卡 1 秒"。
            //   现在先尝试**增量对账**: 只摘掉被屏蔽的卡、插回恢复可见的卡, 其余容器原地不动。
            //   差得太多(刷新/换 tab)才退回整批重填。
            if (TryReconcile(snapshot))
            {
                Progress?.Invoke(this, EventArgs.Empty);
                return;
            }
            _view.Clear();
        }

        var added = 0;
        while (added < _chunk && _index < snapshot.Count)
        {
            _view.Add(snapshot[_index]!);
            _index++;
            added++;
        }
        if (_index >= snapshot.Count) _completed = true;

        // ★ 已填完后的追加(滚动加载更多)跟着搬一小批 —— 首批与"数据到位"同帧落地,
        //   用户到底时立刻有反馈; 其余的由泵按帧补完(见 DrainAppends)。
        if (_completed) added += DrainAppends();

        if (added > 0) Progress?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把界面集合 <see cref="_view"/> 与新可见快照做增量对账，返回 true 表示已完成对账
    /// （调用方不要再清空重填）。
    ///
    /// ★ 只用于"屏蔽规则变化"这种小改动: 两批内容大半相同、且保留下来的条目顺序一致。
    ///   刷新 / 换 tab / 换 UP 是整批换掉 —— 那种情况交给原来的"清空 + 分帧重填"。
    /// ★ 恢复可见的条目一次补太多会重新制造卡顿, 超过上限也走整批重填(分帧摊开)。
    /// </summary>
    private bool TryReconcile(IList snapshot)
    {
        if (_view.Count == 0 || snapshot.Count == 0) return false;

        var wanted = new HashSet<object>();
        foreach (var item in snapshot) wanted.Add(item!);

        var survivors = 0;
        foreach (var item in _view)
            if (wanted.Contains(item)) survivors++;

        // 保留下来不到一半 ⇒ 是整批数据被换掉, 不是屏蔽增删
        if (survivors * 2 < Math.Min(_view.Count, snapshot.Count)) return false;

        // 留下的条目在新快照里必须保持同序 —— 顺序全变说明换了一批内容, 整批重填更稳
        // （马上要被摘掉的条目不在快照里, 不参与同序检查）
        var j = 0;
        foreach (var item in _view)
        {
            if (!wanted.Contains(item)) continue;
            while (j < snapshot.Count && !ReferenceEquals(snapshot[j], item)) j++;
            if (j >= snapshot.Count) return false;
            j++;
        }

        // 一次性补回太多(取消一大批屏蔽)会重新制造一次长冻结 ⇒ 交给分帧重填
        const int maxInserts = 6;
        var missing = snapshot.Count - survivors;
        if (missing > maxInserts) return false;

        // 1) 摘掉被屏蔽/已不在快照里的条目(只销毁这几张卡的容器)
        for (var i = _view.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_view[i])) _view.RemoveAt(i);

        // 2) 把恢复可见的条目插回原位
        j = 0;
        foreach (var item in snapshot)
        {
            if (j < _view.Count && ReferenceEquals(_view[j], item))
            {
                j++;
                continue;
            }
            _view.Insert(j, item!);
            j++;
        }

        _appends.Clear();
        _index = snapshot.Count;
        _completed = true;
        return true;
    }

    /// <summary>
    /// 把待补队列按**这一帧的批量**搬进视图, 返回搬了几条。
    ///
    /// ★ 追加的卡片不播入场淡入(见 <c>VideoCard.MarkNoEntranceAnimation</c>):
    ///   一页 20~30 张同时起 160ms 的 Opacity 动画, 合成器要同时混合这么多动画层,
    ///   那一下的卡顿有一半来自这里 —— 而"滚动中追加上来的卡片"本来就不该再演一次入场。
    /// </summary>
    private int DrainAppends()
    {
        if (_appends.Count == 0) return 0;

        var added = 0;
        while (_appends.Count > 0 && added < _appendBatch)
        {
            var item = _appends.Dequeue();
            BiliDesk.Views.Controls.VideoCard.MarkNoEntranceAnimation(item);
            _view.Add(item);
            added++;
        }
        return added;
    }

    private void Pump()
    {
        // 有存货 = 游标还没扫完 或 还有追加待补(见 IsActive: 追加也算"填充进行中")
        if (_disposed || (_completed && _appends.Count == 0) || _pending != null) return;
        // Background 优先级: 让输入和渲染先走 —— 每一批都插在"用户还能操作"的间隙里
        _pending = _list.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _pending = null;
            var now = Environment.TickCount64;
            OnPumpTiming(now);
            _lastPumpTick = now;
            FillChunk();
            if (!_completed || _appends.Count > 0) Pump();   // 还有存货, 继续排下一帧
        });
    }

    /// <summary>
    /// 按**帧间隔**自适应追加批量。
    ///
    /// ★ 为什么量帧间隔而不是量 FillChunk 自己花了多久: 插入容器的真正开销在**布局/渲染**里
    ///   (标题换行排版、模板实例化), 不在 <c>_view.Add</c> 那一句上。泵运行在 Background
    ///   优先级 = 渲染之后, 于是两次泵之间的间隔就近似"上一帧整帧花了多久"。
    /// ★ 间隔偏长(>40ms) ⇒ 这机器/这个构建撑不住当前批量, 对半砍; 很宽裕(<20ms) ⇒ 慢慢加回来。
    ///   Debug 构建下会自动收敛到 2~4 张/帧, Release 下会爬回 10+。
    /// </summary>
    private void OnPumpTiming(long now)
    {
        if (_lastPumpTick == 0) return;
        var delta = now - _lastPumpTick;
        if (delta > 40) _appendBatch = Math.Max(AppendBatchMin, _appendBatch / 2);
        else if (delta < 20) _appendBatch = Math.Min(AppendBatchMax, _appendBatch + 1);
    }
}
