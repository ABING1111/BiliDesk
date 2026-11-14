using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using BiliDesk.Views;
using BiliDesk.Views.Pages;

namespace BiliDesk;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm = App.MainVm;

    /// <summary>
    /// 已建好的页面实例(切换时保留状态)。
    ///
    /// ★ **用到才建**(2026-10-01): 以前构造窗口时把 11 个页面全 `new` 一遍, 实测这一笔要
    ///   **三百多毫秒**(首页单独就占 276ms —— 它 XAML 最重), 全部压在启动那一下、而用户
    ///   首屏只会看到首页。改成 `PageOf(key)` 惰性建: 启动只付首页那一笔, 其余摊到"用户真的
    ///   点进那个页面"的时刻。
    ///
    /// 安全前提(都成立才敢这么改):
    ///   · 每个页面都在自己的 `Loaded` 里拉数据, 构造时**不**发请求 ⇒ 晚建不会漏加载;
    ///   · 每个页面本来就是"实例长期缓存、状态保留" ⇒ 晚建不改变语义, 只是把"什么时候建"推后;
    ///   · `_pages` 只在本类里读写(见 PageOf), 没有别处遍历它。
    /// </summary>
    private readonly Dictionary<PageKey, FrameworkElement> _pages = new();

    private DispatcherTimer? _toastTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // DataContext 在构造时就赋好了, 导航栏的 RadioButton 绑的就是 Current,
        // 所以高亮会停在启动项上(启动固定进首页)。

        _vm.PageChanged += OnPageChanged;
        Svc.Toast.Shown += ShowToast;
        // 鼠标侧键(拇指键) = 返回。挂在窗口的隧道路由上, 不会被页面里的控件吃掉。
        PreviewMouseDown += OnPreviewMouseDown;

        // 分区面板: 3 列数据, 每列 10 个分区(来源与生成说明见 PartitionCatalog)
        PartitionCol0.ItemsSource = PartitionCatalog.Columns[0];
        PartitionCol1.ItemsSource = PartitionCatalog.Columns[1];
        PartitionCol2.ItemsSource = PartitionCatalog.Columns[2];

        Loaded += OnLoaded;

        // ★★ 失焦后的空闲回收(2026-11-09 内存优化): 用户切到别的窗口/最小化后,
        //   等 5 秒确认不是"快速 Alt-Tab 回来", 再做一次阻塞式 GC + 交还工作集。
        //   为什么需要: 任务管理器里的"内存"是工作集, .NET 回收对象后不会主动把空闲页
        //   还给系统 —— 不显式 EmptyWorkingSet, 数字会一直停在峰值附近, 这就是用户看到的
        //   "不看视频也 200~300MB、滑几下 400MB 不回落"。
        //   为什么是失焦而不是前台空闲: 前台时阻塞 GC 会让界面卡一下, 不值当; 后台时
        //   用户看不见, 代价为零。
        Deactivated += OnDeactivatedTrim;
        Activated += OnActivatedCancelTrim;

        // ★★ 前台长时间无输入也回收一次(2026-11-09): 用户开着窗口但不操作(看标题/离开座位),
        //   工作集同样会停在 200~300MB。这里按"最后一次鼠标/滚轮/键盘输入"计时, 45 秒没动
        //   就做一次与失焦相同的空闲回收。
        //   · 有输入就重置 ⇒ 正常浏览时永远不会触发;
        //   · 播放器开着时 RequestIdleRelease 内部会避让(见 VlcCore.HasLivePlayers);
        //   · 回收只是把空闲页交还系统, 不丢数据 —— 下次操作最多多一次软缺页。
        PreviewMouseDown += OnUserActivity;
        PreviewMouseWheel += OnUserActivity;
        PreviewKeyDown += OnUserActivity;
        // F5 = 刷新当前页面(2026-11-12 用户要求"所有的刷新绑定到 F5")
        PreviewKeyDown += OnRefreshKey;
        // Esc 关闭动态详情 / 评论输入面板
        PreviewKeyDown += OnDynamicDetailKeyDown;
        Loaded += (_, _) => StartForegroundIdleTimer();
        Closed += (_, _) => _foregroundIdleTimer?.Stop();
    }

    // ------------------------------------------------------------ 前台空闲回收

    /// <summary>最后一次用户输入的时刻(<see cref="Environment.TickCount64"/>)。</summary>
    private long _lastInputTick = Environment.TickCount64;

    /// <summary>前台无输入多久之后回收。见构造函数里的说明。</summary>
    private const int ForegroundIdleMs = 45_000;

    private DispatcherTimer? _foregroundIdleTimer;

    private void OnUserActivity(object sender, InputEventArgs e)
        => _lastInputTick = Environment.TickCount64;

    // ------------------------------------------------------------ F5 刷新

    /// <summary>
    /// F5 = 刷新当前页面(2026-11-12 用户要求"所有的刷新绑定到 F5")。
    /// 优先走页面自己的 <see cref="IRefreshable"/>: 首页/关注/分区/我的页的刷新带
    /// "滚回顶部、短内容补页"等视图动作, 必须复用它们原来那条路径;
    /// 只绑了命令的页面(历史/收藏/稍后再看/缓存/消息/UP 主页)退化为执行刷新命令。
    /// 内嵌页(UP 主主页等)盖在 PageLayer 上时, 先刷它。
    /// </summary>
    private void OnRefreshKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5) return;
        e.Handled = true;

        if (EmbeddedHost.Visibility == Visibility.Visible)
        {
            if (EmbeddedContent.Content is IRefreshable embeddedPage)
            {
                embeddedPage.Refresh();
                return;
            }
            TryRefreshByCommand(EmbeddedContent.Content as FrameworkElement);
            return;
        }

        if (_currentPage is IRefreshable page)
        {
            page.Refresh();
            return;
        }
        TryRefreshByCommand(_currentPage);
    }

    /// <summary>页面没有实现 <see cref="IRefreshable"/> 时: 直接执行 ViewModel 上的刷新命令。</summary>
    private static void TryRefreshByCommand(FrameworkElement? page)
    {
        var vm = page?.DataContext;
        if (vm == null) return;
        foreach (var name in new[] { "RefreshCommand", "ReloadCommand" })
        {
            if (vm.GetType().GetProperty(name)?.GetValue(vm) is not ICommand cmd) continue;
            if (cmd.CanExecute(null)) cmd.Execute(null);
            return;
        }
    }

    private void StartForegroundIdleTimer()
    {
        if (_foregroundIdleTimer != null) return;
        _foregroundIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _foregroundIdleTimer.Tick += (_, _) =>
        {
            // 后台/最小化由 Deactivated 那条路负责, 这里只处理"窗口在前台但没人动"
            if (!IsActive || !IsVisible || WindowState != WindowState.Normal) return;
            if (Environment.TickCount64 - _lastInputTick < ForegroundIdleMs) return;
            if (VlcCore.HasLivePlayers) return;
            MemoryTrim.RequestIdleRelease();
        };
        _foregroundIdleTimer.Start();
    }

    /// <summary>失焦多久之后回收。见构造函数里的说明。</summary>
    private const int IdleTrimDelayMs = 5000;

    private DispatcherTimer? _idleTrimTimer;

    private void OnDeactivatedTrim(object? sender, EventArgs e)
    {
        _idleTrimTimer ??= CreateIdleTrimTimer();
        _idleTrimTimer.Stop();
        _idleTrimTimer.Start();
    }

    private void OnActivatedCancelTrim(object? sender, EventArgs e) => _idleTrimTimer?.Stop();

    private DispatcherTimer CreateIdleTrimTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(IdleTrimDelayMs),
        };
        t.Tick += (_, _) =>
        {
            t.Stop();
            // 期间可能又激活了(Alt-Tab 回来), 那就不要动
            if (IsActive) return;
            // 收进托盘(Hide(), 非最小化)时由 OnClosing 那条路负责; 最小化要继续往下面走 ——
            // 最小化窗口的 IsVisible 是 false, 不能把它当成"隐藏到托盘"跳过。
            if (!IsVisible && WindowState != WindowState.Minimized) return;
            // 正在看视频时不要做阻塞式 GC/交还工作集 —— 会打断播放/渲染。
            if (VlcCore.HasLivePlayers) return;
            MemoryTrim.RequestIdleRelease();
        };
        return t;
    }

    /// <summary>
    /// 取某个页面的实例, 没有就现建一个并缓存(见 _pages 的说明)。
    ///
    /// 页面的 DataContext 必须跟着实例一起建: 各页 VM 的构造里会订阅服务(搜索历史变更、
    /// 推荐算法变更…), 那些是"页面活着就一直听着"的, 分开建会让订阅时机变得难以推理。
    /// </summary>
    private FrameworkElement PageOf(PageKey key)
    {
        if (_pages.TryGetValue(key, out var cached)) return cached;

        FrameworkElement page = key switch
        {
            PageKey.Home => new HomePage { DataContext = new HomeViewModel() },
            PageKey.Follow => new FollowPage { DataContext = new FollowViewModel() },
            PageKey.Search => new SearchPage { DataContext = new SearchViewModel() },
            PageKey.History => new HistoryPage { DataContext = new HistoryViewModel() },
            PageKey.Favorites => new FavoritesPage { DataContext = new FavoritesViewModel() },
            PageKey.Settings => new SettingsPage { DataContext = new SettingsViewModel() },
            // 我的页(2026-09-26 新增): 个人资料 + 快捷入口 + 收藏夹卡片
            PageKey.Mine => new MinePage { DataContext = new MineViewModel() },
            // 这两个页面不在左侧导航栏上: 消息入口在首页右上角工具列, 离线缓存入口在「我的」页
            PageKey.Cache => new CachePage { DataContext = new CacheViewModel() },
            PageKey.Messages => new MessagesPage { DataContext = new MessagesViewModel() },
            // 分区页(也不在导航栏上): 入口是左上角 logo 的分区面板
            PageKey.Region => new RegionPage { DataContext = new RegionViewModel() },
            // 稍后再看(也不在导航栏上): 入口是「我的」页的快捷入口
            _ => new WatchLaterPage { DataContext = new WatchLaterViewModel() }
        };

        _pages[key] = page;
        return page;
    }

    /// <summary>
    /// 鼠标侧键(XButton1/XButton2) = 返回上一层。
    ///
    /// 主窗口的"上一层"只有一个: **内嵌页**(UP 主主页那种盖在内容区上的页面)。
    /// 没有内嵌页时什么都不做 —— 不擅自把用户弹回首页: 在设置页/历史页按一下侧键
    /// 就被踢走, 那是"误触"而不是"返回"。
    ///
    /// 两个侧键都当返回: 各家鼠标把拇指键映射成哪个 XButton 并不统一, 本应用也没有
    /// "前进"这个语义可用(播放器那边同样处理)。
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1 && e.ChangedButton != MouseButton.XButton2) return;
        // 分区面板开着时, 侧键先把它收起来(优先级高于内嵌页, 因为面板压在最上层)
        if (PartitionPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            HidePartition();
            return;
        }
        if (EmbeddedHost.Visibility != Visibility.Visible) return;
        e.Handled = true;
        HideEmbedded();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 首次显示不播入场动画: 窗口刚出来, 再叠一层淡入会很怪。
        ShowPage(_vm.Current, animate: false);
        _ = (PageOf(PageKey.Home) as HomePage)?.Init();
    }

    /// <summary>
    /// 关闭键的行为。
    ///
    /// 默认(**关闭时最小化到托盘** 打开): 取消这次关闭, 把窗口藏进托盘, 进程继续跑 ——
    /// 双击托盘图标恢复, 退出走托盘右键菜单。设置里关掉这个开关就恢复成"关掉即退出"。
    ///
    /// 两种情况必须放行, 否则会卡死退出流程:
    ///   1. App.IsExiting(托盘菜单点「退出」/ 系统关机注销) —— 那是明确要退, 不能再拦;
    ///   2. 免责声明等流程里的显式 Shutdown。
    ///
    /// ShutdownMode 保持 OnMainWindowClose 不变: 关闭键的语义由这里决定,
    /// 而"真的退出"那条路走 Application.Shutdown(), 两条线互不干扰。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.IsExiting && Svc.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            // ★ 收进托盘 = 用户明确不看了: 立刻做一次"GC + 交还工作集"。
            //   窗口已经隐藏, 阻塞几十毫秒不会被看到; 否则托盘里的进程会一直占着
            //   一屏封面/卡片的工作集, 任务管理器里显得"关不掉"。
            //   正在播放视频时跳过 —— 阻塞式 GC 会让画面/声音抖一下。
            if (!VlcCore.HasLivePlayers) MemoryTrim.RequestIdleRelease();
            return;
        }
        base.OnClosing(e);
    }

    private void OnPageChanged()
    {
        // 任何一次导航都意味着"离开内嵌页" —— 点导航栏里的任意一项(包括当前项)都会回到导航页,
        // 所以这里先无条件关掉它, 不要去猜用户想不想要那个覆盖层留着。
        HideEmbedded();
        HidePartition();
        ShowPage(_vm.Current, animate: true);
    }

    /// <summary>
    /// 左侧导航栏的点击。
    /// 用 Click + Tag 而不是直接绑 Command: RadioButton 点自己时不会再触发 Checked(已经是选中态),
    /// 而"点当前项"是有意义的 —— 它要负责把内嵌的 UP 主主页收起来。Click 每次都会来。
    /// </summary>
    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PageKey key }) _vm.Navigate(key);
    }

    // ------------------------------------------------------------ 内嵌页(UP 主主页等)

    /// <summary>
    /// 在主窗口内容区里打开一个页面: 盖在导航页之上, 自带一条"返回"头。
    ///
    /// 用于 UP 主主页这类"从当前页面点进去、又要能退回来"的页面。
    /// 以前这里是 new 一个独立 Window —— 观感像换了另一个程序, 关掉时还容易连主界面一起关,
    /// 而且那个窗口不在导航体系里, 点哪都回不到刚才的位置。
    /// </summary>
    public void ShowEmbeddedPage(FrameworkElement page, string title)
    {
        // 切页过渡还在跑时先把状态落定: 内嵌页会把整个 PageLayer 藏起来,
        // 若此时有页面停在"内容半透明"的中途, 回来时会看到它没完全显示。
        if (NavTransition.InFlight) NavTransition.FinishNow();

        // 先放掉上一个内嵌页: 同一时刻只留一个, 免得旧的 UP 资料被接着显示
        EmbeddedContent.Content = null;

        EmbeddedTitle.Text = title;
        EmbeddedContent.Content = page;
        // 页面层整体藏起来(显示中的那一页保持原状, 收起内嵌页后立刻还原, 不必重新布局)
        PageLayer.Visibility = Visibility.Collapsed;
        EmbeddedHost.Visibility = Visibility.Visible;

        // 淡入 + 轻微上浮, 和切换导航页的入场观感保持一致
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        EmbeddedHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        page.RenderTransform = new TranslateTransform(0, 12);
        ((TranslateTransform)page.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });

        // 触发源可能在别的窗口上(例如在播放器里点了 UP 主头像)。
        // 那样页面虽然已经内嵌到主窗口, 用户却看不到 —— 所以这里主动把主窗口提到前面。
        // 只在"没最小化"的前提下 Activate, 不去动别人的窗口状态。
        if (!IsActive)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }

    /// <summary>关掉内嵌页, 露出之前的导航页</summary>
    public void HideEmbedded()
    {
        if (EmbeddedHost.Visibility != Visibility.Visible) return;
        // 先清掉淡入动画: 不然下次显示时 Opacity 的动画值还挂着, 会跳过入场动画直接跳满
        EmbeddedHost.BeginAnimation(OpacityProperty, null);
        EmbeddedHost.Opacity = 0;
        EmbeddedHost.Visibility = Visibility.Collapsed;
        // 释放页面对象: 下次点进来重新 new 一个, 顺带重新拉一次数据(而不是显示上次的旧资料)
        EmbeddedContent.Content = null;
        PageLayer.Visibility = Visibility.Visible;
    }

    private void OnEmbeddedBackClick(object sender, RoutedEventArgs e) => HideEmbedded();

    /// <summary>
    /// 切到「搜索」页并立刻用给定关键词搜一次(首页右上角搜索卡用)。
    ///
    /// 刻意不新开窗口: 搜索页本来就在左侧导航栏里, 走同一套切页逻辑 ——
    /// 用户之后从搜索页点别的导航项时, 位置关系仍然是熟悉的那一套。
    /// </summary>
    public void NavigateToSearch(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;

        if (PageOf(PageKey.Search) is SearchPage page && page.DataContext is SearchViewModel vm)
        {
            // setter 会把"搜索历史条"收起来, SearchAsync 会把它记进搜索历史
            vm.Keyword = keyword.Trim();
            _ = vm.SearchAsync();
        }

        _vm.Navigate(PageKey.Search);

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 切到某个分区页并加载数据。入口: 左上角 logo 的分区面板。
    /// 与 NavigateToSearch 同一套路 —— 不新开窗口, 走导航栏那套切页。
    /// 返回 Task 而不是 async void: 调用方(分区面板点击)不关心结果, 但异常必须能顺着
    /// Task 走, 而不是变成进程级未处理异常。
    ///
    /// ★★ 顺序必须是"**先切页, 再拉数据**"(2026-10-02 修, 用户报"第一次进分区页卡一秒才加载"):
    ///   原来的写法是 `await page.OpenAsync(...)` 之后才 `_vm.Navigate(PageKey.Region)` ——
    ///   于是整个网络请求(首次进某分区实测 ~1s)期间, 界面**还停在首页**、分区页压根没上屏,
    ///   用户看到的就是"点了没反应 / 卡了一秒", 然后页面突然跳出来。而分区页上那个
    ///   「正在加载…」转圈绑的是 `RegionViewModel.Loading`, 页面不上屏它永远没机会显示。
    ///   切开之后: 切页是同步的(立即上屏, 转圈开始转), 数据在后台填。
    ///   ★ 注意别把 await 挪回来"顺手"等待 —— 那样等于把上面这个 bug 再写一遍。
    /// </summary>
    public async Task NavigateToRegion(string name, int tid)
    {
        // 先去分区页(同步上屏) —— 转圈立刻可见, 不再有"点了卡一秒"的空白期
        _vm.Navigate(PageKey.Region);
        if (PageOf(PageKey.Region) is RegionPage page) await page.OpenAsync(name, tid);

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------ 切页过渡

    /// <summary>切页过渡。逻辑与理由见 <see cref="PageTransition"/>。</summary>
    private PageTransition? _navTransition;

    private PageTransition NavTransition => _navTransition ??= new PageTransition();

    /// <summary>当前可见页面。页面对象常驻 <see cref="PageLayer"/>，切页只切 Visibility。</summary>
    private FrameworkElement? _currentPage;

    /// <summary>
    /// 切换页面。
    ///
    /// ★★★ 2026-11-07 重做: 页面**不再摘出可视树**，全部常驻 PageLayer。
    ///   旧实现每次切页都把 ContentControl.Content 换掉，旧页一离开可视树就丢了布局状态；
    ///   首页是零虚拟化卡片墙，重新挂回时要重新测量/排布整棵树 —— 本机探针实测
    ///   430 张卡 89ms、830 张 176ms、1530 张 278ms，慢机器上就是用户报的"白屏一秒"。
    ///   现在旧页只是 Visibility.Collapsed（不渲染、不参与布局，但**布局状态保留**），
    ///   探针实测 830 张卡 Collapsed→Visible 的 UpdateLayout 只要 11ms。
    ///
    /// ★ 新页一定先在**旧页仍然完整可见**的时候完成布局，然后再播过渡；
    ///   所以无论页面多重建、第一次建页要多久，用户看到的都是旧页，不会出现白底。
    ///
    /// ★★ 2026-11-07 第四次: 过渡改为 fade-through —— **先淡出旧页内容、再换页、再淡入新页内容**。
    ///   上一版是交叉淡化(新页不透明待在下面、旧页淡出), 那 200ms 里两页内容按比例叠在
    ///   同一个位置, 用户看到的就是"第一张页面和第二张页面叠在一起"。现在任何一帧都只有
    ///   一页的内容可见; 页面底色(半透明)全程不动, 所以也不会露出窗口底色闪白。
    ///   细节见 <see cref="PageTransition"/>。
    /// </summary>
    private void ShowPage(PageKey key, bool animate)
    {
        // 切页时详情遮罩必须收掉, 否则会盖在新页面上
        if (_dynamicDetailOpen) CloseDynamicDetail(immediate: true);

        // 在途过渡先立即落定, 再建页/换页: 否则"建一个很重的页面"会卡住正在跑的动画
        // (首次建页要解析整棵 XAML, 首页实测 ~360ms)。
        if (NavTransition.InFlight) NavTransition.FinishNow();

        var target = PageOf(key);

        // 首次显示(窗口刚出来)不播过渡: 那时还没有"上一个页面"可淡出。
        if (!animate)
        {
            NavTransition.Cancel();
            ShowImmediately(target);
            return;
        }

        // 目标就是当前页: 直接返回, 免得"点当前导航项"白播一段动画。
        if (ReferenceEquals(_currentPage, target)) return;

        ShowAnimated(target);
    }

    /// <summary>不播过渡、立即显示某一页(首次显示 / 过渡被打断时的兜底)。</summary>
    private void ShowImmediately(FrameworkElement page)
    {
        AttachPage(page);
        foreach (var child in PageLayer.Children.OfType<FrameworkElement>())
        {
            if (!ReferenceEquals(child, page) && child.Visibility != Visibility.Collapsed)
                child.Visibility = Visibility.Collapsed;
        }

        // 页面本身与它的内容都复位成不透明 —— 上一次过渡被打断时内容可能停在 0。
        PageTransition.ResetVisual(page);
        page.Visibility = Visibility.Visible;
        page.IsHitTestVisible = true;
        Panel.SetZIndex(page, 1);
        _currentPage = page;
        MemoryTrim.RequestTrim();
    }

    /// <summary>带 Win11 Fluent 过渡地切到某一页。见 <see cref="ShowPage"/> 的说明。</summary>
    private void ShowAnimated(FrameworkElement target)
    {
        AttachPage(target);

        var outgoing = _currentPage;
        if (outgoing == null || ReferenceEquals(outgoing, target))
        {
            ShowImmediately(target);
            return;
        }

        // ★ z 序必须在目标页 Visible **之前**摆好: 页面是按创建顺序加进 Children 的,
        //   新页面天然排在后面(更靠上)。若先 Visible 再设 ZIndex, 它会在 UpdateLayout 那一拍
        //   直接盖住旧页 —— 用户会看到一眼"还没排好的新页面"。
        Panel.SetZIndex(target, 1);
        Panel.SetZIndex(outgoing, 2);

        // 目标页先完成布局: 此刻旧页仍然完整地盖在上面, 这里花多少时间用户都看不到白底。
        // (之前显示过的页面是 Collapsed 回来的, 布局状态还在, 这一句通常只要十几毫秒。)
        // ★ 必须在 Visible **之前**把它整页置为不可见: 页面底色是半透明的, 以不透明状态
        //   上屏哪怕一帧, 也会和旧页底色叠成更深的一档(见 PageTransition.PrepareIncoming)。
        PageTransition.PrepareIncoming(target);
        target.Visibility = Visibility.Visible;
        target.IsHitTestVisible = false;
        target.UpdateLayout();

        // 旧页保持可见, 内容淡出; 中点换页; 新页内容再淡入。
        outgoing.Visibility = Visibility.Visible;
        outgoing.IsHitTestVisible = false;

        NavTransition.Run(outgoing, target,
            swap: () =>
            {
                // 中点: 两页内容都是 0, 此刻收起旧页、把新页提到上面, 看不到任何跳变。
                outgoing.Visibility = Visibility.Collapsed;
                Panel.SetZIndex(target, 2);
                Panel.SetZIndex(outgoing, 0);
                _currentPage = target;
            },
            done: () =>
            {
                target.IsHitTestVisible = true;
                MemoryTrim.RequestTrim();
            });
    }

    /// <summary>
    /// 把页面挂进 PageLayer(只挂一次)。页面常驻, 切页只改 Visibility —— 布局状态必须留住。
    /// 初始为 Collapsed + Opacity 0: 既不会在切页动画里抢先露出来, 也不参与布局。
    /// </summary>
    private void AttachPage(FrameworkElement page)
    {
        if (ReferenceEquals(page.Parent, PageLayer)) return;
        page.Visibility = Visibility.Collapsed;
        page.Opacity = 0;
        page.IsHitTestVisible = false;
        Panel.SetZIndex(page, 0);
        PageLayer.Children.Add(page);
    }

    // ------------------------------------------------------------ 账户区

    /// <summary>
    /// 左下角的头像账户区已撤(2026-09-26 用户要求): 登录入口收进「我的」页。
    /// 未登录时的登录窗口改由 MinePage 的"去登录"按钮调 OpenLoginWindow()。
    /// </summary>

    /// <summary>
    /// 从「我的」页点某个收藏夹卡片: 切到收藏页并直接选中那个夹。
    /// 与 NavigateToRegion 同一套路 —— 不新开窗口, 走导航栏那套切页。
    /// </summary>
    public void OpenFavFolder(FavFolder folder)
    {
        if (folder == null) return;
        if (PageOf(PageKey.Favorites) is FavoritesPage page && page.DataContext is FavoritesViewModel vm)
        {
            if (vm.Folders.Count > 0)
            {
                // 已经加载过: 直接选中(尽量用集合里的同一个实例, 保证 chip 高亮对得上)
                var target = vm.Folders.FirstOrDefault(f => f.Id == folder.Id) ?? folder;
                _ = vm.SelectFolderAsync(target);
            }
            else
            {
                // 首次还没加载: 挂到预留位, 让页面的 LoadAsync 完成后选它
                vm.PendingFolder = folder;
            }
        }
        _vm.Navigate(PageKey.Favorites);

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------ 分区面板(点左上角 logo 展开)

    private bool _partitionOpen;

    /// <summary>
    /// 左上角 logo: 点它展开分区面板(2026-10-13 用户要求"改回左上角 logo 点击后显示分区,
    /// 一切与以前保持一致" —— 这一版与 2026-10-12 之前完全一致)。
    /// ★ 用 MouseLeftButtonUp 而不是 Click: logo 是 Border, 没有 Click 事件。
    /// </summary>
    private void OnBrandLogoClick(object sender, MouseButtonEventArgs e) => OpenPartition();

    /// <summary>
    /// 展开分区面板(2026-10-12: 入口从"左上角 logo"搬到了**首页右下角工具栏**)。
    ///
    /// ★ 面板**仍然留在窗口左上角**展开 —— 入口搬了, 但展开位置没跟着搬:
    ///   分区是"导航级"的菜单, 从右下角按钮长出一张贴着左上的大面板会很别扭;
    ///   而左上角正好是导航栏的位置, 面板从那儿长出来仍然贴着它。
    ///   (真正会读代码的人会问"为什么不跟着入口走", 所以在这里写明。)
    ///
    /// RenderTransformOrigin=0,0 意味着缩放锚点是面板左上角, 动画看起来就是"从左上角长出"。
    /// </summary>
    public void OpenPartition()
    {
        if (_partitionOpen) return;
        _partitionOpen = true;

        PartitionScrim.Visibility = Visibility.Visible;
        PartitionPanel.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PartitionScrim.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        PartitionPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        PartitionScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        PartitionScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    /// <summary>收起分区面板(带缩放回缩 + 淡出动画)</summary>
    private void ClosePartition()
    {
        if (!_partitionOpen) return;
        _partitionOpen = false;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            PartitionScrim.Visibility = Visibility.Collapsed;
            PartitionPanel.Visibility = Visibility.Collapsed;
        };
        PartitionScrim.BeginAnimation(OpacityProperty, fade);
        PartitionPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        PartitionScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.9, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        PartitionScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.9, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    /// <summary>立刻收起、不播动画 —— 导航切页 / 鼠标侧键返回这类"打断"场景用</summary>
    private void HidePartition()
    {
        if (!_partitionOpen) return;
        _partitionOpen = false;

        PartitionScrim.BeginAnimation(OpacityProperty, null);
        PartitionPanel.BeginAnimation(OpacityProperty, null);
        PartitionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PartitionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PartitionScrim.Opacity = 0;
        PartitionScrim.Visibility = Visibility.Collapsed;
        PartitionPanel.Opacity = 0;
        PartitionPanel.Visibility = Visibility.Collapsed;
        PartitionScale.ScaleX = 1;
        PartitionScale.ScaleY = 1;
    }

    private void OnPartitionScrimClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ClosePartition();
    }

    private void OnPartitionItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PartitionItem item }) return;
        e.Handled = true;
        ClosePartition();
        // 原生分区页(与首页同一套卡片墙), 不再落网页版
        _ = NavigateToRegion(item.Name, item.Tid);
    }

    // ------------------------------------------------------------ 动态详情(应用级遮罩)

    private bool _dynamicDetailOpen;
    private int _dynamicDetailAnimGen;
    private Point _dynamicDetailOrigin = new(0.5, 0.5);

    /// <summary>
    /// 打开动态详情。
    /// ★ 遮罩挂在 MainWindow 根 Grid 上(而不是 FollowPage 里), 所以左侧导航栏也会一起变暗;
    ///   面板居中、底色恒不透明(亚克力下也读得清)。
    /// ★ source = 触发它的动态卡: 打开时把缩放原点放到卡片中心, 动画看起来就是"从卡片长出来";
    ///   关闭时从同一个原点缩回去。source 为 null(或卡片已被虚拟化回收)时退化为居中缩放。
    /// </summary>
    public void ShowDynamicDetail(DynamicItem item, bool focusComments, FrameworkElement? source = null)
    {
        if (item == null) return;

        _dynamicDetailOpen = true;
        var gen = ++_dynamicDetailAnimGen;

        DynamicDetailHost.SetItem(item, focusComments);

        // 先取消上一轮关闭动画, 并把视觉状态复位到"隐藏但可见"的起点
        StopDynamicDetailAnimations();
        DynamicDetailOverlay.Visibility = Visibility.Visible;
        DynamicDetailOverlay.Opacity = 0;
        DynamicDetailPanel.Opacity = 0;
        DynamicDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DynamicDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DynamicDetailScale.ScaleX = 1;
        DynamicDetailScale.ScaleY = 1;

        // 等布局完成后才知道面板真实位置/尺寸, 才能把缩放原点算成卡片在面板里的相对坐标
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_dynamicDetailOpen || gen != _dynamicDetailAnimGen) return;

            _dynamicDetailOrigin = ComputeDynamicDetailOrigin(source);
            DynamicDetailPanel.RenderTransformOrigin = _dynamicDetailOrigin;
            DynamicDetailScale.ScaleX = 0.16;
            DynamicDetailScale.ScaleY = 0.16;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            DynamicDetailOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            DynamicDetailPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            var scale = new DoubleAnimation(0.16, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
            DynamicDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            DynamicDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }));
    }

    /// <summary>关闭动态详情。immediate=true 用于切页等打断场景(不播动画)。</summary>
    public void CloseDynamicDetail(bool immediate = false)
    {
        if (!_dynamicDetailOpen) return;
        _dynamicDetailOpen = false;
        var gen = ++_dynamicDetailAnimGen;

        if (immediate)
        {
            StopDynamicDetailAnimations();
            DynamicDetailOverlay.Visibility = Visibility.Collapsed;
            DynamicDetailOverlay.Opacity = 0;
            DynamicDetailPanel.Opacity = 1;
            DynamicDetailScale.ScaleX = 1;
            DynamicDetailScale.ScaleY = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (gen != _dynamicDetailAnimGen || _dynamicDetailOpen) return;
            StopDynamicDetailAnimations();
            DynamicDetailOverlay.Visibility = Visibility.Collapsed;
            DynamicDetailOverlay.Opacity = 0;
            DynamicDetailPanel.Opacity = 1;   // 复位, 下次打开从 0 淡入
            DynamicDetailScale.ScaleX = 1;
            DynamicDetailScale.ScaleY = 1;
        };

        DynamicDetailOverlay.BeginAnimation(OpacityProperty, fade);
        DynamicDetailPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        var scale = new DoubleAnimation(1, 0.16, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        DynamicDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        DynamicDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void StopDynamicDetailAnimations()
    {
        DynamicDetailOverlay.BeginAnimation(OpacityProperty, null);
        DynamicDetailPanel.BeginAnimation(OpacityProperty, null);
        DynamicDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DynamicDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>
    /// 计算"卡片中心"在详情面板内的相对坐标(RenderTransformOrigin)。
    /// ★ 用 RootGrid 当共同祖先: 卡片在 PageLayer 里、面板在根 Grid 上, 二者不是父子关系,
    ///   直接 TransformToAncestor(面板) 会抛异常。
    /// </summary>
    private Point ComputeDynamicDetailOrigin(FrameworkElement? source)
    {
        try
        {
            if (source == null || !source.IsLoaded) return new Point(0.5, 0.5);
            var panelW = DynamicDetailPanel.ActualWidth;
            var panelH = DynamicDetailPanel.ActualHeight;
            if (panelW <= 0 || panelH <= 0) return new Point(0.5, 0.5);

            var sourceCenter = source.TransformToAncestor(RootGrid)
                .Transform(new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            var panelTopLeft = DynamicDetailPanel.TransformToAncestor(RootGrid)
                .Transform(new Point(0, 0));

            var ox = (sourceCenter.X - panelTopLeft.X) / panelW;
            var oy = (sourceCenter.Y - panelTopLeft.Y) / panelH;
            // 卡片在列表很左边/很下边时原点可能超出面板, 留一点余量即可, 不要拉得太夸张
            ox = Math.Clamp(ox, -0.4, 1.4);
            oy = Math.Clamp(oy, -0.4, 1.4);
            return new Point(ox, oy);
        }
        catch
        {
            return new Point(0.5, 0.5);
        }
    }

    private void OnDynamicDetailScrimClick(object sender, MouseButtonEventArgs e)
    {
        if (DynamicDetailPanel != null && DynamicDetailPanel.IsMouseOver) return;
        CloseDynamicDetail();
        e.Handled = true;
    }

    private void OnDynamicDetailCloseClick(object sender, RoutedEventArgs e) => CloseDynamicDetail();

    private void OnDynamicDetailKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_dynamicDetailOpen) return;
        if (DynamicDetailHost.IsComposerOpen)
        {
            DynamicDetailHost.CloseComposer();
        }
        else
        {
            CloseDynamicDetail();
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------ Toast

    private void ShowToast(string message)
    {
        // ★ 2026-11-04 (issue #38): 连续/并发的提示不能每次都从透明度 0 重播淡入 ——
        //   下载进度以前 1~2 秒刷一条, 每次都"透明 → 不透明", 看起来就是每 1% 闪一下。
        //   已经显示(含正在淡出)时只换文字并保持不透明; 只有从隐藏状态出现才做一次淡入。
        var appearing = Toast.Visibility != Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, null);   // 撤掉可能还没跑完的淡出
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;

        if (appearing)
        {
            // 基值 = 终值 + FillBehavior.Stop: 动画结束落回 1, 不会停在动画值上。
            Toast.Opacity = 1;
            Toast.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                { FillBehavior = FillBehavior.Stop });
        }
        else
        {
            Toast.Opacity = 1;
        }

        // 复用同一个定时器而不是每条提示都 new 一个: 短时间连发多条提示时,
        // 每次 new 都会留下一个短命对象和一个挂在它身上的 Tick 委托(得等 GC 才解绑)。
        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _toastTimer.Tick += OnToastTick;
        }
        _toastTimer.Stop();   // 重排: 新提示的存活时间从这一刻重新算
        _toastTimer.Start();
    }

    private void OnToastTick(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        // 基值先归 0: 淡出结束(Stop)时直接落到 0, 不会闪回不透明。
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 0;
        var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
        { FillBehavior = FillBehavior.Stop };
        a.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, a);
    }
}
