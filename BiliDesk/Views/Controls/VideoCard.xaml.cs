using System;
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
    /// 把页面给的菜单项定义插进卡片右键菜单。
    /// ★ 必须在 code-behind 里动态插, 不能用 XAML 里嵌 ItemsControl + ItemTemplate 的写法:
    ///   ContextMenu 是 MenuBase, 会把 ItemsControl **本身**当做一个 item 包进 MenuItem 壳,
    ///   集合为空的页面(首页等)菜单底部就会多出一个 14px 高、可悬停发灰的空块(探针渲染实锤)。
    /// CommandParameter 绑到卡片 DataContext(= 被右键的那条 VideoItem), 和旧模板行为一致;
    /// 绑定 Source 直接指向卡片本身 —— 菜单弹出在别处, AncestorType 解析不到卡片会失效。
    /// </summary>
    private void RebuildExtraMenuItems(System.Collections.ObjectModel.ObservableCollection<BiliDesk.Models.CardMenuItem>? items)
    {
        var menu = ContextMenu;
        if (menu == null) return;

        if (_extraSource != null)
            _extraSource.CollectionChanged -= OnExtraSourceChanged;
        _extraSource = items;

        foreach (var shell in _extraMenuShells)
            menu.Items.Remove(shell);
        _extraMenuShells.Clear();

        if (items == null || items.Count == 0)
        {
            // 没有额外项时藏起分隔线, 否则菜单底部会多出一条悬空的分隔
            ExtraSeparator.Visibility = Visibility.Collapsed;
            return;
        }

        ExtraSeparator.Visibility = Visibility.Visible;
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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_entered) return;
        _entered = true;

        // 入场位移挂最外层(封面与文字一起上浮); 悬停缩放单独挂封面, 两者互不覆盖
        _trans = new TranslateTransform(0, 18);
        _coverScale = new ScaleTransform(1, 1);
        Root.RenderTransform = _trans;
        CoverCard.RenderTransform = _coverScale;
        CoverCard.RenderTransformOrigin = new Point(0.5, 0.5);

        Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
        _trans.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => AnimateCoverScale(1.04);

    private void OnMouseLeave(object sender, MouseEventArgs e) => AnimateCoverScale(1);

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
        OpenVideo();
    }

    /// <summary>点封面下方的标题 → 与点封面一样打开视频</summary>
    private void OnTitleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        OpenVideo();
    }

    /// <summary>
    /// 点封面下方的 UP 主名 → 进 TA 的个人主页。
    /// 走的是和"播放器里点 UP 主头像 / 评论头像"完全相同的一条路(NavigationDispatcher):
    /// 在主窗口内容区盖一层带返回按钮的主页, 不另开独立窗口。
    /// </summary>
    private void OnAuthorClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not VideoItem v) return;
        if (v.OwnerMid <= 0)
        {
            // 少数接口不给 owner.mid(例如动态里的部分转发), 给个明确反馈而不是毫无反应
            Svc.Toast.Show("没拿到 UP 主的 UID, 打不开主页");
            return;
        }
        new NavigationDispatcher(Window.GetWindow(this)).OpenUserSpace(v.OwnerMid.ToString());
    }

    // ------------------------------------------------------------ 右键菜单

    private VideoItem? Current => DataContext as VideoItem;

    // "查看封面大图"(OnViewCover)已按用户要求整体移除(2026-09-26);
    // MediaActions.ShowImage 仍被头像/私信图片预览使用, ImagePreviewWindow 保留。

    private async void OnDownloadCover(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;
        await MediaActions.SaveImageAsync(v.Cover, v.Title);
    }

    private async void OnDownloadVideo(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        await VideoDownloader.DownloadAsync(v.Bvid, v.Title, Window.GetWindow(this));
    }

    /// <summary>
    /// 把这条视频加进 B 站账号的「稍后再看」。
    ///
    /// 接口只认 avid, 而搜索/推荐这类列表不下发 avid(见 VideoItem.Aid 的说明),
    /// 所以先看列表有没有给, 没有就补查一次详情 —— 否则这些页面上右键点了会一直报"缺 avid"。
    /// 服务端对"已经在列表里"再调一次是幂等的(code 0), 所以不需要先去查列表。
    /// </summary>
    private async void OnAddWatchLater(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("稍后再看需要先登录");
            return;
        }

        var aid = v.Aid;
        if (aid <= 0)
        {
            var (_, _, vd) = await Svc.Api.GetVideoAsync(v.Bvid);
            aid = vd?.Aid ?? 0;
        }
        if (aid <= 0)
        {
            Svc.Toast.Show("没拿到这条视频的 avid, 加不了");
            return;
        }

        var (ok, err) = await Svc.Api.AddWatchLaterAsync(aid);
        Svc.Toast.Show(ok ? "已加入稍后再看" : "加入失败: " + (err ?? "未知错误"));
    }
}