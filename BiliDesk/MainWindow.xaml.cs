using System;
using System.Collections.Generic;
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

        // 分区面板: 3 列数据(来源见 PartitionCatalog 的生成说明)
        PartitionCol0.ItemsSource = PartitionCatalog.Columns[0];
        PartitionCol1.ItemsSource = PartitionCatalog.Columns[1];
        PartitionCol2.ItemsSource = PartitionCatalog.Columns[2];

        Loaded += OnLoaded;
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
            // 第一次收起时给个托盘气泡, 让用户知道程序没被关掉
            TrayService.Instance.NotifyMinimizedToTray();
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
        // 先放掉上一个内嵌页: 同一时刻只留一个, 免得旧的 UP 资料被接着显示
        EmbeddedContent.Content = null;

        EmbeddedTitle.Text = title;
        EmbeddedContent.Content = page;
        PageHost.Visibility = Visibility.Collapsed;
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
        PageHost.Visibility = Visibility.Visible;
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

    /// <summary>
    /// 切页过渡(三段式淡出→换页→淡入, 画在纯色幕布 NavVeil 上)。
    /// 逻辑与理由见 <see cref="PageTransition"/> 与 MainWindow.xaml 里 NavVeil 那段注释。
    /// </summary>
    private PageTransition? _navTransition;

    private PageTransition NavTransition => _navTransition ??= new PageTransition(NavVeil);

    /// <summary>
    /// 切换页面。
    ///
    /// ★★★ 整页淡入淡出**重做**(2026-10-03, 用户要求"既保证有动画又要保证流畅"):
    ///   动画保留(观感仍是"整页淡出 → 整页淡入"), 但**画在一块纯色幕布 NavVeil 上**,
    ///   而 `PageHost` 全程保持 `Opacity=1`。承载体的选择理由见 MainWindow.xaml 里 NavVeil 那段。
    ///
    ///   ★★ 先说实测, 别再把这段写成"幕布修好了卡顿"(初版注释这么写, 被探针推翻):
    ///     探针 `%TEMP%\bd-probe-navframes` 用真实窗口量合成帧间隔, 325 张卡的页面 ——
    ///       · 子树静止: 动画 PageHost.Opacity 与动画幕布**一样**(p50 ~7ms / p95 ~14ms / 掉帧 0);
    ///       · 子树在变(复刻 DeferredFill 批量加卡): 两者也一样(~127ms 一次长帧, 那是**卡片批量
    ///         重排**的账, 与透明度动画无关)。
    ///     ⇒ **动画载体不是卡顿来源**。"给容器挂 Opacity<1 就会每帧重栅格化"这个说法在本机
    ///       量不出差异, 不要当成已证事实去引用。
    ///
    ///   ★ 真正的收益在**建页时机**(这条有实测):
    ///     `PageOf` 首次要解析整棵 XAML, Release 口径实测首页 **~360ms**(`bd-probe-navcost` A 段;
    ///     其余页面 7~23ms)。旧写法把它放在 ShowPage 最前面 ⇒ 那 360ms 卡在"点击 → 动画开始"之间,
    ///     用户看到的是"点了没反应"。现在它落在**幕布已全不透明**之后, 用户只看到"一块背景色"。
    ///     附带好处: 淡出期间改点别的导航项时, 这一拍被代数闸取消 ⇒ 页面根本不会被建。
    ///
    ///   ★ 三段式(节奏与旧写法相同, 只是承载体换了):
    ///       ① 幕布 0→1(90ms): 旧页面被"同一底色"渐渐盖住 —— 看起来就是页面在淡出;
    ///       ② 幕布全不透明时换页 + 建页: 切换被完全遮住, 用户看不到任何跳变;
    ///       ③ 幕布 1→0(170ms): 新页面淡入。
    ///
    ///   ★ 别再改回动画 `PageHost.Opacity`(没有收益, 还多担一层"整页半透明合成"的风险),
    ///   也别在过渡中间加 `PageHost.UpdateLayout()`(那会把整墙测量同步堆在点击这一拍)。
    /// </summary>
    private void ShowPage(PageKey key, bool animate)
    {
        // 目标就是当前页: 直接返回, 免得"点当前导航项"白播一段动画。
        // ★ 但过渡**在途**时不能早退: 那一刻 PageHost 上还挂着旧页面, 而幕布后面的换页已经排上队,
        //   早退会让那次换页照常发生 —— 用户点了"首页"却停在别的页上。这种情况要走一次完整过渡。
        if (!NavTransition.InFlight && _pages.TryGetValue(key, out var showing)
            && ReferenceEquals(PageHost.Content, showing)) return;

        // 首次显示(窗口刚出来)不播过渡: 那时还没有"上一个页面"可淡出。
        if (!animate) { NavTransition.Jump(() => SwitchPageNow(PageOf(key))); return; }

        // ★ 建页推迟到 swap 里(幕布已全不透明): 见上面"真正的收益在**建页时机**"。
        NavTransition.Run(() =>
        {
            PageHost.Content = PageOf(key);
            MemoryTrim.RequestTrim();
        });
    }

    /// <summary>
    /// 立刻换页的收尾: 兜底清掉 `PageHost` 上可能残留的整页透明度动画(旧版本写法留下的),
    /// 并请求一次内存回收 —— 旧页面刚离开可视树, 它的卡片容器/封面位图全变成了等 GC 的垃圾。
    /// 不主动收的话, 任务管理器里的数字就是"点遍所有页面后一直不降"的观感。
    /// </summary>
    private void SwitchPageNow(FrameworkElement page)
    {
        PageHost.Content = page;
        PageHost.BeginAnimation(OpacityProperty, null);
        PageHost.Opacity = 1;
        MemoryTrim.RequestTrim();
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

    // ------------------------------------------------------------ 分区面板(点 logo 展开)

    private bool _partitionOpen;

    private void OnBrandLogoClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenPartition();
    }

    /// <summary>
    /// 展开分区面板。动画"从 logo(左上角)长出": 面板以左上角为锚点缩放放大 + 淡入。
    /// RenderTransformOrigin=0,0 意味着缩放锚点就是面板左上角 —— 那里正是 logo 的位置。
    /// </summary>
    private void OpenPartition()
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

    // ------------------------------------------------------------ Toast

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        // 复用同一个定时器而不是每条提示都 new 一个: 下载进度这类高频提示下,
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
        var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
        a.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, a);
    }
}