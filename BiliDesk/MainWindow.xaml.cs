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
    private readonly Dictionary<PageKey, FrameworkElement> _pages = new();
    private DispatcherTimer? _toastTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // 启动页: 从设置里读(默认首页)。
        // 赋值放在订阅 PageChanged **之前** —— 否则窗口还没显示就先跑一遍"切页动画",
        // 而且那次切换会把下面 OnLoaded 的 `animate: false` 顶掉。
        // 导航栏的 RadioButton 绑的就是 Current, 所以高亮也会正确停在启动项上。
        _vm.Current = MainViewModel.ParseStartupPage(Svc.Settings.StartupPage);

        // 页面实例(缓存在内存中, 切换时保留状态)
        _pages[PageKey.Home] = new HomePage { DataContext = new HomeViewModel() }; // WPF 原生首页(推荐/热门/排行榜)
        _pages[PageKey.Follow] = new FollowPage { DataContext = new FollowViewModel() };
        _pages[PageKey.Search] = new SearchPage { DataContext = new SearchViewModel() };
        _pages[PageKey.History] = new HistoryPage { DataContext = new HistoryViewModel() };
        _pages[PageKey.Favorites] = new FavoritesPage { DataContext = new FavoritesViewModel() };
        _pages[PageKey.Settings] = new SettingsPage { DataContext = new SettingsViewModel() };
        // 我的页(2026-09-26 新增): 个人资料 + 快捷入口 + 收藏夹卡片
        _pages[PageKey.Mine] = new MinePage { DataContext = new MineViewModel() };
        // 这两个页面不在左侧导航栏上: 消息入口在首页右上角工具列, 离线缓存入口在「我的」页
        _pages[PageKey.Cache] = new CachePage { DataContext = new CacheViewModel() };
        _pages[PageKey.Messages] = new MessagesPage { DataContext = new MessagesViewModel() };
        // 分区页(也不在导航栏上): 入口是左上角 logo 的分区面板
        _pages[PageKey.Region] = new RegionPage { DataContext = new RegionViewModel() };
        // 稍后再看(也不在导航栏上): 入口是「我的」页的快捷入口
        _pages[PageKey.WatchLater] = new WatchLaterPage { DataContext = new WatchLaterViewModel() };

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
        // 这里传的是"设置里的启动页", 不再是写死的首页。
        ShowPage(_vm.Current, animate: false);
        _ = (_pages[PageKey.Home] as HomePage)?.Init();
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

        if (_pages[PageKey.Search] is SearchPage page && page.DataContext is SearchViewModel vm)
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
    /// </summary>
    public async Task NavigateToRegion(string name, int tid)
    {
        if (_pages[PageKey.Region] is RegionPage page) await page.OpenAsync(name, tid);
        _vm.Navigate(PageKey.Region);

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>切换页面(带淡出 + 上浮入场动画)</summary>
    private void ShowPage(PageKey key, bool animate)
    {
        var page = _pages[key];
        if (ReferenceEquals(PageHost.Content, page)) return;

        if (!animate)
        {
            PageHost.Content = page;
            // 旧页面刚离开可视树: 它的卡片容器/封面位图全变成了等 GC 的垃圾。
            // 不主动收的话, 任务管理器里的数字就是"点遍所有页面后一直不降"的观感。
            MemoryTrim.RequestTrim();
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
        fadeOut.Completed += (_, _) =>
        {
            if (!ReferenceEquals(PageHost.Content, page))
                PageHost.Content = page;

            // ★★ 先把这次挂载引发的**布局**跑完, 再开始动画。
            //
            // 为什么: 首页/历史页那种"零虚拟化卡片墙"一挂进可视树就要重新测量+排布整墙卡片,
            // 是一整块同步耗时(几十毫秒起)。原来的写法是"挂载 + 立刻 BeginAnimation",
            // 于是那一下布局正好压在动画的第一帧上 —— 用户看到的就是"**切过去卡顿一下**"。
            // 这里强制先跑完布局: 布局的耗时一次付清, 之后的动画帧是纯合成(不再夹着布局)。
            // 代价是动画晚一帧开始, 但那一下本来就被 fadeOut 的黑色/底色盖着, 看不出来。
            PageHost.UpdateLayout();

            page.RenderTransform = new TranslateTransform(0, 10);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            // 时长也收短了(260 → 170): 整页透明度动画是在重绘整页, 页面越重越贵。
            // 用户要的是"别卡", 不是"看一段动画"。
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
            var rise = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };
            PageHost.BeginAnimation(OpacityProperty, fadeIn);
            ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.YProperty, rise);
            MemoryTrim.RequestTrim();
        };
        PageHost.BeginAnimation(OpacityProperty, fadeOut);
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
        if (_pages[PageKey.Favorites] is FavoritesPage page && page.DataContext is FavoritesViewModel vm)
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