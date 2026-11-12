using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 视频卡片: 入场淡入上浮动画 + 悬停放大 + 点击播放。
///
/// 版式(2026-09-26 用户改版): 卡片**只含封面**(四角圆角), 标题/UP 主/日期挪到封面下方。
/// 于是两道动画分挂两处:
///   · 入场淡入上浮 → 最外层 Root(封面和文字一起进场);
///   · 悬停放大     → 封面 CoverCard(放大文字会让下面两行互相挤压, 没有意义)。
/// 两者都是 RenderTransform, 不参与布局, 缩放不会把周围的卡片挤开。
/// </summary>
public partial class VideoCard : UserControl
{
    private TranslateTransform _trans = null!;
    private ScaleTransform _coverScale = null!;
    private bool _entered;

    /// <summary>
    /// 本次左键手势是否**从这张卡片上按下**。
    ///
    /// ★★★ 为什么必须有它(2026-11-06 修 issue #50):
    ///   卡片的打开动作挂在 MouseLeftButtonUp 上, 而 WPF 的 Up 是**按抬起时的坐标重新命中测试**
    ///   派发的, 不要求同一个元素先收到 Down。窗口最大化/页面重排之后, 同一个屏幕坐标可能
    ///   落到另一张卡片的封面上, 它会收到一个"没有配对 Down"的 Up 并直接开始播放。
    ///   有了这道闸, 只有"在这张卡片上按下、也在这张卡片上抬起"才算点击。
    ///   ★ 播放器视频区已经在用同一套判据(见 PlayerWindow.OnVideoAreaMouseLeftUp 的
    ///     _videoGestureActive), 这里是列表侧的对应实现。
    /// </summary>
    private bool _pressInside;

    /// <summary>
    /// 已经"亮过相"的视频条目。
    ///
    /// ★ 为什么需要它(2026-10-02 修"切 tab 卡片闪几下"): 入场动画(360ms 淡入+上浮)挂在
    ///   <see cref="OnLoaded"/> 上, 而切 tab 会把**整墙卡片销毁再重建** —— 每个 VideoCard 实例
    ///   都是新的, `_entered` 也会重置, 于是每次切 tab 一百多张卡同时重播一遍 360ms 入场动画,
    ///   和封面的异步回填叠在一起, 就是用户报的"切 tab 闪几下"。
    ///   但条目本身(VideoItem)在切 tab 时是**同一份对象**(ObservableCollection 没换)——
    ///   用 ConditionalWeakTable 记"这条已经亮过相", 换 tab 回来就直接落位、不再重播。
    ///   ★ ConditionalWeakTable 的键是弱引用: 刷新换了一批新 VideoItem 时, 旧条目被 GC、
    ///   表项自动清掉, 新一批照常播入场动画(那才是真正的"新内容第一次亮相")。
    /// </summary>
    private static readonly ConditionalWeakTable<VideoItem, object?> _seenItems = new();

    /// <summary>
    /// 页面可以往卡片右键菜单里追加自己的菜单项(如收藏页的"移出收藏")。
    /// 用 ObservableCollection 而不是普通 List: 菜单项可能在页面加载后动态增删,
    /// 集合同知能让菜单自动刷新。
    /// </summary>
    public static readonly DependencyProperty ExtraMenuItemsProperty = DependencyProperty.Register(
        nameof(ExtraMenuItems), typeof(System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>),
        typeof(VideoCard), new PropertyMetadata(null, OnExtraMenuItemsChanged));

    public System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>? ExtraMenuItems
    {
        get => (System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>?)GetValue(ExtraMenuItemsProperty);
        set => SetValue(ExtraMenuItemsProperty, value);
    }

    private static void OnExtraMenuItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not VideoCard card) return;
        card.RebuildExtraMenuItems(
            e.NewValue as System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>);
    }

    // 当前插进 ContextMenu 的"额外项壳", 重建时先整体摘掉
    private readonly List<MenuItem> _extraMenuShells = new();
    private System.Collections.Specialized.INotifyCollectionChanged? _extraSource;

    /// <summary>
    /// 卡片右键菜单。**第一次右键时才创建**(2026-11-09 内存优化)。
    ///
    /// ★ 为什么改成懒建: 以前菜单写在 XAML 的 &lt;UserControl.ContextMenu&gt; 里, 每张卡片
    ///   实例化时就把整棵菜单树(约 10 个 MenuItem + 图标 TextBlock + 分隔线)一起建好。
    ///   卡片墙零虚拟化 + 无限滚动, 滚几百条就是几百套完整菜单 —— 而其中绝大多数卡片
    ///   用户**一次右键都不会点**。这笔常驻内存完全是浪费。
    ///   现在只在真的弹出右键菜单时建一次, 之后复用。
    /// </summary>
    private ContextMenu? _menu;

    private Separator? _extraSeparator;
    private MenuItem? _downloadVideoMenu;
    private MenuItem? _watchLaterMenu;

    /// <summary>
    /// 把页面给的菜单项定义插进卡片右键菜单。
    /// ★ 必须在 code-behind 里动态插, 不能用 XAML 里嵌 ItemsControl + ItemTemplate 的写法:
    ///   ContextMenu 是 MenuBase, 会把 ItemsControl **本身**当做一个 item 包进 MenuItem 壳,
    ///   集合为空的页面(首页等)菜单底部就会多出一个 14px 高、可悬停发灰的空块(探针渲染实锤)。
    /// CommandParameter 绑到卡片 DataContext(= 被右键的那条 VideoItem), 和旧模板行为一致;
    /// 绑定 Source 直接指向卡片本身 —— 菜单弹出在别处, AncestorType 解析不到卡片会失效。
    /// </summary>
    private void RebuildExtraMenuItems(System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>? items)
    {
        if (_extraSource != null)
            _extraSource.CollectionChanged -= OnExtraSourceChanged;
        _extraSource = items;

        // 菜单还没建(这张卡从没被右键过): 只记住来源, 第一次弹菜单时会按它建。
        if (_menu == null) return;

        ApplyExtraMenuItems(items);
    }

    /// <summary>把 <paramref name="items"/> 对应的菜单壳插到已建好的菜单末尾。</summary>
    private void ApplyExtraMenuItems(System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>? items)
    {
        var menu = _menu;
        if (menu == null) return;

        foreach (var shell in _extraMenuShells)
            menu.Items.Remove(shell);
        _extraMenuShells.Clear();

        if (_extraSeparator == null)
        {
            _extraSeparator = new Separator { Visibility = Visibility.Collapsed };
            menu.Items.Add(_extraSeparator);
        }

        if (items == null || items.Count == 0)
        {
            // 没有额外项时藏起分隔线, 否则菜单底部会多出一条悬空的分隔
            _extraSeparator.Visibility = Visibility.Collapsed;
            return;
        }

        _extraSeparator.Visibility = Visibility.Visible;
        foreach (var def in items)
        {
            var shell = new MenuItem();
            shell.SetBinding(MenuItem.HeaderProperty, new System.Windows.Data.Binding(nameof(BiliDesk.Models.CardMenuItem.Header)) { Source = def });
            shell.SetBinding(MenuItem.CommandProperty, new System.Windows.Data.Binding(nameof(BiliDesk.Models.CardMenuItem.Command)) { Source = def });
            shell.SetBinding(MenuItem.CommandParameterProperty, new System.Windows.Data.Binding("DataContext") { Source = this });
            var icon = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BiliDesk.Models.CardMenuItem.Glyph)) { Source = def });
            shell.Icon = icon;
            menu.Items.Add(shell);
            _extraMenuShells.Add(shell);
        }

        // 定义集合运行时增删也要跟着刷新 —— 增删频率低, 整组重建最简单可靠
        items.CollectionChanged += OnExtraSourceChanged;
    }

    /// <summary>第一次需要右键菜单时构造一次(见 <see cref="_menu"/> 的说明)。</summary>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("预览封面", "\uE8B9", OnPreviewCover));
        menu.Items.Add(MakeMenuItem("下载封面到本地", "\uE896", OnDownloadCover));
        menu.Items.Add(new Separator());
        _downloadVideoMenu = MakeMenuItem("下载视频到本地", "\uE896", OnDownloadVideo,
            "可选清晰度与保存位置");
        menu.Items.Add(_downloadVideoMenu);
        menu.Items.Add(new Separator());
        _watchLaterMenu = MakeMenuItem("加入稍后再看", "\uE7C1", OnAddWatchLater);
        menu.Items.Add(_watchLaterMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("屏蔽此 UP 主", "\uE77B", OnBlockUp,
            "所有列表里都不再显示这个 UP 主"));
        menu.Items.Add(MakeMenuItem("屏蔽此标题", "\uE7BA", OnBlockTitle,
            "只隐藏标题完全相同的这一条"));
        _extraSeparator = new Separator { Visibility = Visibility.Collapsed };
        menu.Items.Add(_extraSeparator);

        ApplyExtraMenuItems(_extraSource as System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>);
        return menu;
    }

    private static MenuItem MakeMenuItem(string header, string glyph, RoutedEventHandler onClick, string? toolTip = null)
    {
        var item = new MenuItem { Header = header };
        if (!string.IsNullOrEmpty(toolTip)) item.ToolTip = toolTip;
        item.Icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        item.Click += onClick;
        return item;
    }

    /// <summary>
    /// 右键菜单即将弹出: 首次建菜单, 并按当前这条视频(直播/普通)修正两个条目的显隐。
    /// ★ 在事件里把 ContextMenu 挂上, 让 WPF 自己打开 —— 不要 e.Handled + 手动 IsOpen:
    ///   后者会被 ContextMenuService 的打开流程覆盖, 表现为"右键没反应"。
    /// </summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _menu ??= BuildMenu();

        var isLive = DataContext is VideoItem { IsLive: true };
        if (_downloadVideoMenu != null)
            _downloadVideoMenu.Visibility = isLive ? Visibility.Collapsed : Visibility.Visible;
        if (_watchLaterMenu != null)
            _watchLaterMenu.Visibility = isLive ? Visibility.Collapsed : Visibility.Visible;

        ContextMenu = _menu;
    }

    private void OnExtraSourceChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RebuildExtraMenuItems(sender as System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>);
    }

    /// <summary>
    /// 是否处于"可选中"模式(列表编辑模式)。
    /// 打开后: 封面左上角出现选中圆标, 且**点击卡片变成切换选中**而不是播放。
    /// 默认 false —— 卡片在首页/搜索/收藏等所有地方都在用, 这个开关必须是显式打开的。
    /// </summary>
    public static readonly DependencyProperty SelectableProperty = DependencyProperty.Register(
        nameof(Selectable), typeof(bool), typeof(VideoCard), new PropertyMetadata(false));

    public bool Selectable
    {
        get => (bool)GetValue(SelectableProperty);
        set => SetValue(SelectableProperty, value);
    }

    public VideoCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // 右键菜单改成"第一次右键时才建"(见 _menu 的说明)。
        ContextMenuOpening += OnContextMenuOpening;
        // 卡片被复用/换数据时清掉手势标记: 列表容器回收时旧的一次 Down 可能没有配对的 Up,
        // 不清的话下一条视频会带着上一张卡片的"已按下"状态, 一个漏进来的 Up 就能误播。
        DataContextChanged += (_, _) => _pressInside = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_entered) return;
        _entered = true;

        // 「稍后再看」按钮状态: 订阅全局集合 + 首次需要时拉一次服务端列表
        HookLaterState();
        _ = WatchLaterState.EnsureLoadedAsync();
        RefreshLaterState();

        // 入场位移挂最外层(封面与文字一起上浮); 悬停缩放单独挂封面, 两者互不覆盖
        _trans = new TranslateTransform(0, 18);
        _coverScale = new ScaleTransform(1, 1);
        Root.RenderTransform = _trans;
        CoverCard.RenderTransform = _coverScale;
        CoverCard.RenderTransformOrigin = new Point(0.5, 0.5);

        // ★ 切 tab 重建的卡片**不播入场动画**(见 _seenItems 的说明): 直接落位。
        if (DataContext is VideoItem item && _seenItems.TryGetValue(item, out _))
        {
            _trans.Y = 0;
            Opacity = 1;
            return;
        }
        if (DataContext is VideoItem item2) _seenItems.Add(item2, null);

        // 入场: 只做一次**很快**的淡入(不再上浮)。原来 360ms 淡入 + 18px 上浮, 首屏/切 tab/进分区
        // 一次生成几十上百张卡同时播, 合成器一帧要混合几十个动画层 —— 用户报的"闪几下 / 第一秒卡顿"
        // 有一半来自这里。砍到 160ms 纯淡入: 观感仍是"柔和出现", 但动画负担降到原来的 1/4 以下
        // (时长减半 + 去掉位移那条动画通道)。
        Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        _trans.Y = 0;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        AnimateCoverScale(1.04);
        ShowLaterBar(true);
    }

    /// <summary>
    /// 鼠标在卡片上**真的移动**过之后再预热 LibVLC(见 VlcCore.PrewarmOnDemand)。
    ///
    /// ★ 为什么不用 MouseEnter: 窗口刚出现时如果鼠标正好停在某张卡上, WPF 会补一个
    ///   MouseEnter —— 那不是用户想看的信号, 却会立刻把 LibVLC 的 ~28MB 常驻内存拉起来。
    ///   MouseMove 只由真实移动触发, 是更可靠的"意图"判据。
    /// </summary>
    private void OnHoverMove(object sender, MouseEventArgs e)
    {
        if (_prewarmRequested) return;
        _prewarmRequested = true;
        VlcCore.PrewarmOnDemand();
    }

    private bool _prewarmRequested;

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // 按下后拖出卡片, 这一次抬起不再算点击(否则松手时如果恰好又滑回封面会误播)
        _pressInside = false;
        AnimateCoverScale(1);
        ShowLaterBar(false);
    }

    // ------------------------------------------------------------ 「稍后再看」状态

    private bool _laterHooked;

    /// <summary>订阅全局「稍后再看」集合(卡片被虚拟化回收时在 Unloaded 里摘掉)。</summary>
    private void HookLaterState()
    {
        if (_laterHooked) return;
        _laterHooked = true;
        WatchLaterState.Changed += OnLaterStateChanged;
        DataContextChanged += OnLaterDataContextChanged;
        Unloaded += OnLaterUnloaded;
    }

    private void OnLaterUnloaded(object sender, RoutedEventArgs e)
    {
        WatchLaterState.Changed -= OnLaterStateChanged;
        DataContextChanged -= OnLaterDataContextChanged;
        Unloaded -= OnLaterUnloaded;
        _laterHooked = false;
    }

    /// <summary>全局集合变了(任一卡片增删/整表重载): 所有卡片同步图标。</summary>
    private void OnLaterStateChanged() => RefreshLaterState();

    private void OnLaterDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshLaterState();

    /// <summary>已加入 = 对勾 + 强调色底; 未加入 = 旗帜图标 + 半透明黑底。</summary>
    private void RefreshLaterState()
    {
        var item = DataContext as VideoItem;
        var inList = item != null && WatchLaterState.Contains(item.Aid);

        if (LaterIcon != null) LaterIcon.Text = inList ? "\uE73E" : "\uE7C1";
        if (LaterLabelText != null) LaterLabelText.Text = inList ? "从稍后再看移除" : "稍后再看";
        if (LaterButton != null)
        {
            LaterButton.Background = inList
                ? (TryFindResource("AccentBrush") as Brush ?? LaterNormalBrush)
                : LaterNormalBrush;
        }
    }

    /// <summary>未加入时的底色(半透明黑), 与 XAML 初始值一致。</summary>
    private static readonly Brush LaterNormalBrush = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0));

    /// <summary>左键在卡片上按下: 记下"这一次手势是我的"。Up 命中测试换了目标也不会误认。</summary>
    private void OnCardPress(object sender, MouseButtonEventArgs e) => _pressInside = true;

    /// <summary>
    /// 消费一次有效点击。返回 false = 这个 Up 没有配对的 Down(标题栏/浮层/最大化重排漏进来的),
    /// 调用方直接丢弃。
    /// </summary>
    private bool TakePress()
    {
        if (!_pressInside) return false;
        _pressInside = false;
        return true;
    }

    /// <summary>悬停放大(作用在封面卡片上, 用户要求"放大效果不要删")</summary>
    private void AnimateCoverScale(double zoom)
    {
        if (_coverScale == null) return;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var ms = TimeSpan.FromMilliseconds(180);
        _coverScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_coverScale.ScaleX, zoom, ms) { EasingFunction = ease });
        _coverScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(_coverScale.ScaleY, zoom, ms) { EasingFunction = ease });
    }

    // ------------------------------------------------------------ 右上角「稍后再看」悬浮按钮

    /// <summary>
    /// 悬停封面时淡入右上角的「稍后再看」按钮(2026-11-12 用户要求, 参考 B 站客户端)。
    /// 直播卡片没有这个动作, 直接不显示; 隐藏时同时收起文字标签, 避免下次进来残留。
    /// </summary>
    private void ShowLaterBar(bool show)
    {
        if (show && DataContext is VideoItem { IsLive: true }) show = false;

        LaterBar.IsHitTestVisible = show;
        LaterBar.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        if (!show) ShowLaterLabel(false);
    }

    /// <summary>悬停按钮时展开「稍后再看」文字(与 B 站客户端的悬停提示一致)。</summary>
    private void ShowLaterLabel(bool show)
        => LaterLabel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(120)));

    private void OnLaterButtonEnter(object sender, MouseEventArgs e) => ShowLaterLabel(true);

    private void OnLaterButtonLeave(object sender, MouseEventArgs e) => ShowLaterLabel(false);

    /// <summary>
    /// 点悬浮按钮 = 加入稍后再看。
    /// ★ 必须 e.Handled = true: 按钮在 CoverCard 的 Grid 里, 不拦的话 Up 会冒泡到封面,
    ///   被 OnClick 当成"点开视频"(用户点的明明是稍后再看)。
    /// </summary>
    private void OnAddWatchLaterClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleWatchLaterAsync().Forget();
    }

    /// <summary>打开这条视频。封面与标题是两个入口, 逻辑共用一份。</summary>
    private void OpenVideo()
    {
        if (DataContext is not VideoItem v) return;

        // 编辑模式下点击 = 勾选/取消勾选, 不再播放。
        // 这里直接改 IsSelected 而不是绑一个 CheckBox: 卡片的点击目标就是整卡,
        // 没必要再叠一个只有右下角能点的小方块。
        if (Selectable)
        {
            v.IsSelected = !v.IsSelected;
            return;
        }

        // 直播间走另一条路: 拉直播流地址后交给播放器, 没有弹幕/点赞/进度上报
        if (v.IsLive)
        {
            Svc.Player.PlayLive(v.RoomId, v.Title);
            return;
        }

        Svc.Player.PlayVideo(v.Bvid, v.Title);
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;   // 没有配对 Down 的 Up, 丢弃(见 _pressInside)
        OpenVideo();
    }

    /// <summary>点封面下方的标题 → 与点封面一样打开视频</summary>
    private void OnTitleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;
        OpenVideo();
    }

    /// <summary>
    /// 点封面下方的 UP 主名 → 进 TA 的个人主页。
    /// 走的是和"播放器里点 UP 主头像 / 评论头像"完全相同的一条路(NavigationDispatcher):
    /// 在主窗口内容区盖一层带返回按钮的主页, 不另开独立窗口。
    ///
    /// ★ 列表没给 OwnerMid 时**先补查一次视频详情**(与 OnAddWatchLater 同款), 而不是直接弹提示:
    ///   提示等于"这条路走不通", 而详情接口几乎总能给出 owner.mid, 用户点一下就进去了。
    /// ★ async 方法 + Forget: await 期间卡片可能被虚拟化回收, 异常不冒全局弹框。
    /// </summary>
    private void OnAuthorClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;
        OpenAuthorSpaceAsync().Forget();
    }

    private async System.Threading.Tasks.Task OpenAuthorSpaceAsync()
    {
        if (DataContext is not VideoItem v) return;

        var mid = v.OwnerMid;
        if (mid <= 0 && !string.IsNullOrEmpty(v.Bvid))
        {
            var (_, _, vd) = await Svc.Api.GetVideoAsync(v.Bvid);
            mid = vd?.OwnerMid ?? 0;
        }
        if (mid <= 0)
        {
            // 连详情都拿不到(例如动态里的部分转发), 给个明确反馈而不是毫无反应
            Svc.Toast.Show("没拿到 UP 主的 UID, 打不开主页");
            return;
        }
        new NavigationDispatcher(Window.GetWindow(this)).OpenUserSpace(mid.ToString());
    }

    // ------------------------------------------------------------ 右键菜单

    private VideoItem? Current => DataContext as VideoItem;

    // "查看封面大图"曾按用户要求移除(2026-09-26), 2026-10-31 预览窗重写后按新要求加回
    // (OnPreviewCover); MediaActions.ShowImage 同时被头像/私信图片预览使用。

    private void OnDownloadCover(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;
        MediaActions.SaveImageAsync(v.Cover, v.Title).Forget();
    }

    /// <summary>预览封面大图(2026-10-31 加回): 与"下载封面"同一个 Cover URL。</summary>
    private void OnPreviewCover(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;
        MediaActions.ShowImage(v.Cover, v.Title, Window.GetWindow(this));
    }

    private void OnDownloadVideo(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        // 列表页的卡片没有 cid, 由 DownloadActions 补查详情后弹选择窗
        // (清晰度 + 保存位置, 用户 2026-11-14 要求)
        DownloadActions.ShowAndDownloadAsync(
            v.Bvid, 0, v.Title, QualityPreference.RequestQn(Svc.Settings.PreferredQualityQn),
            Window.GetWindow(this)).Forget();
    }

    /// <summary>
    /// 一键屏蔽这个 UP 主(精确同名 + UID)。规则是全局的, 所有已接入屏蔽的列表立刻生效。
    /// </summary>
    private void OnBlockUp(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;

        var author = (v.Author ?? "").Trim();
        if (author.Length == 0 && v.OwnerMid <= 0)
        {
            Svc.Toast.Show("这条没有 UP 主信息，屏蔽不了");
            return;
        }

        FilterService.Instance.BlockUp(author, v.OwnerMid);
        var label = author.Length > 0 ? author : $"UID {v.OwnerMid}";
        Svc.Toast.Show($"已屏蔽 UP 主「{label}」，可在首页「屏蔽」面板撤销");
    }

    /// <summary>
    /// 一键屏蔽这个标题(**完全相同**才隐藏)。只挡这一条, 同 UP 的其它视频不受影响。
    /// </summary>
    private void OnBlockTitle(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;

        var title = (v.Title ?? "").Trim();
        if (title.Length == 0) return;

        FilterService.Instance.BlockTitle(title);
        Svc.Toast.Show("已屏蔽这条视频，可在首页「屏蔽」面板撤销");
    }

    /// <summary>
    /// 把这条视频加进 B 站账号的「稍后再看」。
    ///
    /// 接口只认 avid, 而搜索/推荐这类列表不下发 avid(见 VideoItem.Aid 的说明),
    /// 所以先看列表有没有给, 没有就补查一次详情 —— 否则这些页面上右键点了会一直报"缺 avid"。
    /// 服务端对"已经在列表里"再调一次是幂等的(code 0), 所以不需要先去查列表。
    ///
    /// ★ 用 async 方法体 + Forget 而不是 async void: await 期间卡片可能已被虚拟化回收
    ///   (DataContext 换人), 异常也不再冒全局弹框。
    /// </summary>
    private void OnAddWatchLater(object sender, RoutedEventArgs e) => AddWatchLaterAsync().Forget();

    private async System.Threading.Tasks.Task AddWatchLaterAsync()
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("稍后再看需要先登录");
            return;
        }

        var aid = await ResolveAidAsync(v);
        if (aid <= 0)
        {
            Svc.Toast.Show("没拿到这条视频的 avid, 加不了");
            return;
        }

        var (ok, err) = await Svc.Api.AddWatchLaterAsync(aid);
        if (ok) WatchLaterState.Set(aid, true);
        Svc.Toast.Show(ok ? "已加入稍后再看" : "加入失败: " + (err ?? "未知错误"));
    }

    /// <summary>
    /// 悬浮按钮: 在「加入」和「移除」之间切换(2026-11-12 用户要求)。
    /// 状态以 <see cref="WatchLaterState"/> 为准; 成功后本地立即反映并广播给所有卡片,
    /// 服务端列表才是持久化来源 —— 下次启动重新拉一次, 与账号完全一致。
    /// </summary>
    private async System.Threading.Tasks.Task ToggleWatchLaterAsync()
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("稍后再看需要先登录");
            return;
        }

        var aid = await ResolveAidAsync(v);
        if (aid <= 0)
        {
            Svc.Toast.Show("没拿到这条视频的 avid, 操作不了");
            return;
        }

        if (WatchLaterState.Contains(aid))
        {
            var (ok, err) = await Svc.Api.RemoveWatchLaterAsync(aid);
            if (ok)
            {
                WatchLaterState.Set(aid, false);
                Svc.Toast.Show("已从稍后再看移除");
            }
            else
            {
                Svc.Toast.Show("移除失败: " + (err ?? "未知错误"));
            }
        }
        else
        {
            var (ok, err) = await Svc.Api.AddWatchLaterAsync(aid);
            if (ok)
            {
                WatchLaterState.Set(aid, true);
                Svc.Toast.Show("已加入稍后再看");
            }
            else
            {
                Svc.Toast.Show("加入失败: " + (err ?? "未知错误"));
            }
        }
    }

    /// <summary>列表没下发 avid(搜索/推荐等页面)时补查一次视频详情。</summary>
    private static async System.Threading.Tasks.Task<long> ResolveAidAsync(VideoItem v)
    {
        if (v.Aid > 0) return v.Aid;
        if (string.IsNullOrEmpty(v.Bvid)) return 0;
        var (_, _, vd) = await Svc.Api.GetVideoAsync(v.Bvid);
        return vd?.Aid ?? 0;
    }
}