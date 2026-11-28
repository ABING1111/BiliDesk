using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

public partial class SettingsPage : UserControl
{
    private SettingsViewModel? _vm;
    private bool _subscribed;

    /// <summary>
    /// 顶部吸顶行(胶囊 + 搜索)的折叠吸顶。算法抽在 <see cref="StickyPin"/>(收藏页/历史页共用同一份),
    /// 构造时就要建好 —— 它从行的 XAML Margin 里取水平边距与初始自然顶边。
    /// ★ 2026-11-28 起挂的是**整行**(StickyRow) 而不是胶囊: 搜索圆和胶囊一起吸顶,
    ///   两者必须同步位移, 否则展开的输入框会和胶囊错位。
    /// </summary>
    private readonly StickyPin _sticky;

    /// <summary>上一次被高亮的那条色带(用来判"这次淡出该不该清掉画面")</summary>
    private System.Windows.Shapes.Rectangle? _flashed;

    public SettingsPage()
    {
        InitializeComponent();
        _sticky = new StickyPin(PageScroll, CapsuleSlot, StickyRow, StickyRowShift);
        Loaded += OnLoaded;
        // 页面常驻 PageLayer、只切 Visibility: 切回来时 Loaded 不会再发, 补量一次自然位置。
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) _sticky.SyncDeferred(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as SettingsViewModel;
        if (_vm == null) return;

        _vm.OnNavigated();

        if (!_subscribed)
        {
            _subscribed = true;
            _vm.PropertyChanged += OnVmPropertyChanged;
            // 设置项搜索(2026-11-28): 结果卡片点一行 / 回车 → VM 只负责切 Tab,
            // "滚到那一行 + 高亮"必须由页面做(它才拿得到可视树)。
            _vm.LocateRequested += OnLocateRequested;
            // 调色盘拖动起止: 拖的时候跳过全窗口可视树重解析, 松手补一次
            // (理由见 SettingsViewModel.AccentPickerColor 上的说明)
            //
            // ★ 2026-10-03 重做后调色盘在"通用"Tab 的模板里, 切 Tab 会整个换掉可视树,
            //   所以每次 TabIndex 变化后要重新找一次 Palette 实例(见 OnVmPropertyChanged)。
        }
        _ = LoadAvatarAsync(_vm.UserFace);
        HookPalette();
        // 吸顶行: 等布局落定后量一次它的自然位置(见 StickyPin.Sync)
        _sticky.SyncDeferred();
    }

    /// <summary>
    /// Tab 胶囊吸顶(2026-11-14 用户要求"只折叠吸顶胶囊, 且不要改变胶囊的大小"):
    /// 胶囊是滚动内容里的**唯一实例**, 滚动多少就用 TranslateTransform 反向抵消多少 ——
    /// 视觉上先跟着内容一起上滑, 到距顶端 8px 处钉住; 大标题照常滚走。
    /// ★ 只改 RenderTransform, 不碰布局: 每帧不触发测量/排版。实现在 <see cref="StickyPin"/>。
    /// </summary>
    private void OnSettingsScrollChanged(object sender, ScrollChangedEventArgs e) => _sticky.Update();

    // ==================== 设置项搜索(2026-11-28) ====================

    /// <summary>
    /// 搜索框里按回车 = 跳到第一条命中(结果卡片里每行也可以直接点)。
    /// ★ 用 PreviewKeyDown: 单行 TextBox 不处理 Enter, 但 TabSearchBox 内部的 TextBox
    ///   会把 KeyDown 冒泡出来 —— 挂在这里两条路都能收到。
    /// </summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_vm?.ActivateFirstSearchHit() == true) e.Handled = true;
    }

    /// <summary>
    /// 把某条命中项滚到视野里并高亮一下。
    /// ★ 必须等一帧: VM 刚改 TabIndex, ContentControl 换模板发生在绑定通知之后,
    ///   当场找可视树拿到的是**上一个 Tab** 的行(与 HookPalette 同一个时机, 用 Loaded 优先级)。
    /// </summary>
    private void OnLocateRequested(SettingsSearchEntry hit)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            // 模板刚换完, 先强制排一次版 —— 否则 TransformToAncestor 量到的是旧位置
            PageScroll.UpdateLayout();

            var row = FindSettingsRow(hit.LocateTitle);
            if (row == null) return;   // 找不到就安静放弃(标题被改而索引没跟上, 测试会拦)

            ScrollRowIntoView(row);
            FlashRow(row);

            // 关键词已由 VM 清空; TabSearchBox 只在失焦/Esc/点外面时才自己收回,
            // 这里补一次, 免得跳走之后留着一个空的展开输入框。
            SearchPill.CollapseIfEmpty();
        }));
    }

    /// <summary>
    /// 按行的标题找到"行容器"。
    /// ★ 只在 <see cref="TabContent"/> 的子树里找: 右上角结果卡片里也有同样的标题文字,
    ///   从页面根找会命中浮层里的那一份。
    /// ★ 行容器 = 最近的、Style 是 SettingsRowStyle 的 Panel: 绝大多数行是 Grid,
    ///   少数竖排行是 StackPanel(主题色 / 界面字体 / 弹幕外观…), 两种都覆盖。
    /// </summary>
    private Panel? FindSettingsRow(string title)
    {
        var text = FindTextBlock(TabContent, title);
        if (text == null) return null;

        var rowStyle = TryFindResource("SettingsRowStyle") as Style;
        for (DependencyObject? d = text; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Panel p && ReferenceEquals(p.Style, rowStyle))
                return p;
        return null;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        if (root is TextBlock tb && string.Equals(tb.Text, text, StringComparison.Ordinal)) return tb;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindTextBlock(VisualTreeHelper.GetChild(root, i), text);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// 手动算偏移滚动, 不用 <c>BringIntoView</c>: 后者会把目标顶到视口 0,
    /// 正好被吸顶的胶囊盖住 —— 用户会以为"跳过去什么都没有"。
    /// 这里留出 "胶囊钉住后的下沿 + 一点呼吸空间" 当目标位置。
    ///
    /// ★ 先回顶再量: 切 Tab 前可能停在很靠下的位置, 而 ScrollViewer 在内容变短时会把
    ///   偏移夹回去 —— 直接拿"旧偏移 + 视口内 y"叠加会算歪(两个基准不是同一个)。
    ///   回顶与跳转发生在同一帧里, 用户看不到中间的闪烁。
    /// </summary>
    private void ScrollRowIntoView(Panel row)
    {
        try
        {
            PageScroll.ScrollToVerticalOffset(0);
            PageScroll.UpdateLayout();

            var y = row.TransformToAncestor(PageScroll).Transform(new Point(0, 0)).Y;
            var desiredTop = StickyPin.PinnedTopGap + StickyCapsule.ActualHeight + 16;
            PageScroll.ScrollToVerticalOffset(Math.Max(0, y - desiredTop));
        }
        catch
        {
            // 还没进可视树(理论上不会走到这里): 不滚, 只留高亮
        }
    }

    /// <summary>
    /// 高亮一下命中的行: 在 <see cref="HighlightLayer"/> 上画一条强调色色带,
    /// 先**保持 0.9 秒**, 再在 2.1 秒里淡出(合计约 3 秒)。
    ///
    /// ★ 2026-11-28(用户要求"略微调大提示颜色范围"): 不再直接改行的 Background。
    ///   行元素只有内容框那么大 —— 行间距(SettingsRowStyle 的 Margin 14)和卡片内边距(26)
    ///   都盖不到, 看起来是"一小块"而不是"这一条"。这里按行的**外边距盒**铺满卡片内宽:
    ///   上下各吃掉一半行间距、左右铺到卡片内边, 视觉上是一条完整的色带。
    /// ★ 画在内容流里的浮层而不是改行底色: 随内容一起滚动, 也被 ScrollViewer 正确裁剪。
    /// ★ 2026-11-28(用户要求"高亮时间变长一些"): 原来是 1.1 秒一路淡出, 跳过去时
    ///   视线还没落到目标上颜色就没了。现在分成"保持 + 淡出"两段, 合计约 3 秒 ——
    ///   保持段让用户看清"跳到这里了", 淡出段再慢慢退场, 不显得突兀。
    /// </summary>
    private void FlashRow(Panel row)
    {
        RestoreFlash();

        var accent = Svc.Theme.Accent;
        var brush = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        var rect = new System.Windows.Shapes.Rectangle
        {
            Fill = brush,
            RadiusX = 8,
            RadiusY = 8,
            IsHitTestVisible = false,
        };

        // 尺寸/位置: 先按行自己的坐标铺, 再把行间距与卡片内边距算进去
        var (x, y, w, h) = MeasureHighlightBand(row);
        System.Windows.Controls.Canvas.SetLeft(rect, x);
        System.Windows.Controls.Canvas.SetTop(rect, y);
        rect.Width = w;
        rect.Height = h;

        HighlightLayer.Children.Clear();
        HighlightLayer.Children.Add(rect);
        _flashed = rect;

        // 保持段用 BeginTime 实现: 这段时间里 Fill 仍是上面那个不透明的强调色,
        // 到点才开始淡出 —— 比"一路慢慢淡"更容易被注意到。
        // ★ 淡出用 QuadraticEase/EaseInOut 而不是 CubicEase/EaseOut: EaseOut 在头 20%
        //   就把颜色掉掉大半(实测 1.3s 时 alpha 已从 54 掉到 10), 看起来像"突然没了";
        //   EaseInOut 前后都缓, 中间匀速, 三秒的过程才读得出"慢慢退场"。
        var fade = new ColorAnimation(
            Color.FromArgb(0x00, accent.R, accent.G, accent.B),
            HighlightFadeDuration)
        {
            BeginTime = HighlightHoldDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        // ★ 只撤"这一笔": 连续跳两条时上一条的 Completed 会晚于新一次 FlashRow 到来,
        //   不判身份的话它会把新色带提前清掉(表现为第二条只闪一下就没颜色)。
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(_flashed, rect)) ClearHighlight();
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, fade);
    }

    /// <summary>高亮保持不透明的时长(这段里颜色最醒目)</summary>
    private static readonly TimeSpan HighlightHoldDuration = TimeSpan.FromMilliseconds(900);

    /// <summary>保持段之后淡出到透明的时长</summary>
    private static readonly TimeSpan HighlightFadeDuration = TimeSpan.FromMilliseconds(2100);

    /// <summary>
    /// 算出色带在 <see cref="HighlightLayer"/> 里的矩形。
    ///
    /// 做法: 行 → 卡片 → 内容流(HighlightLayer 的父级, 也就是 ScrollViewer 里的那个 Grid)。
    ///   · 上下: 行的高加上它自己的 Margin.Top/Bottom(把行间距一起染上), 但**不超过卡片边界**
    ///     (卡片首/末行只扩到卡片内边距为止, 不糊到卡片外面);
    ///   · 左右: 卡片的内容区(扣掉卡片 Padding), 再向两侧各扩
    ///     <see cref="HighlightHorizontalBleed"/> —— 探进卡片内边距里, 色带读起来才是
    ///     "这一整条"而不是"文字那一块"(2026-11-28 用户要求"左右扩展一些");
    ///     但**不越过卡片本身**(含圆角), 否则会在卡片外面露出一截方块。
    /// </summary>
    private (double X, double Y, double W, double H) MeasureHighlightBand(Panel row)
    {
        var layer = HighlightLayer;
        var host = layer.Parent as FrameworkElement;
        if (host == null) return (0, 0, 0, 0);

        // 行在内容流坐标里的矩形
        var rowRect = row.TransformToAncestor(host).TransformBounds(
            new Rect(0, 0, row.ActualWidth, row.ActualHeight));

        // 往上找最近的卡片(带 SettingsCardStyle 的 Border), 拿它的内边距当左右基准
        var card = FindCardAncestor(row);
        double left = rowRect.Left - HighlightHorizontalBleed;
        double right = rowRect.Right + HighlightHorizontalBleed;
        double top = rowRect.Top - row.Margin.Top;
        double bottom = rowRect.Bottom + row.Margin.Bottom;

        if (card != null)
        {
            var cardRect = card.TransformToAncestor(host).TransformBounds(
                new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            var pad = card.Padding;
            var innerLeft = cardRect.Left + pad.Left;
            var innerRight = cardRect.Right - pad.Right;

            // 从卡片内容区出发向两侧扩, 但不越过卡片的描边内侧
            left = Math.Max(cardRect.Left + card.BorderThickness.Left,
                            innerLeft - HighlightHorizontalBleed);
            right = Math.Min(cardRect.Right - card.BorderThickness.Right,
                             innerRight + HighlightHorizontalBleed);

            top = Math.Max(top, cardRect.Top + card.BorderThickness.Top);
            bottom = Math.Min(bottom, cardRect.Bottom - card.BorderThickness.Bottom);
        }
        else
        {
            // 不在卡片里(快捷键那条说明行等): 只按行自身扩, 别超出内容流
            left = Math.Max(0, left);
            right = Math.Min(host.ActualWidth, right);
        }

        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// 色带左右各向卡片内边距里探多少(逻辑像素)。
    /// ★ 不要取到和卡片内边距一样大(26): 那样色带会贴着卡片圆角边缘, 视觉上和卡片"融为一体",
    ///   反而看不出高亮。12 是"明显更宽、又还留一圈卡片底色"的折中。
    /// </summary>
    private const double HighlightHorizontalBleed = 12;

    /// <summary>找行所属的设置卡片(没有就是不在卡片里的行, 例如快捷键那条说明行)</summary>
    private Border? FindCardAncestor(DependencyObject row)
    {
        var cardStyle = TryFindResource("SettingsCardStyle") as Style;
        for (var d = VisualTreeHelper.GetParent(row); d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Border b && ReferenceEquals(b.Style, cardStyle))
                return b;
        return null;
    }

    /// <summary>撤掉上一次高亮</summary>
    private void RestoreFlash() => ClearHighlight();

    private void ClearHighlight()
    {
        _flashed = null;
        if (HighlightLayer.Children.Count == 0) return;
        foreach (var child in HighlightLayer.Children.OfType<System.Windows.Shapes.Rectangle>())
            if (child.Fill is SolidColorBrush b) b.BeginAnimation(SolidColorBrush.ColorProperty, null);
        HighlightLayer.Children.Clear();
    }

    /// <summary>在当前可视树里找调色盘并接上拖动事件(找不到就是"通用"Tab 不在前, 静默跳过)。</summary>
    private void HookPalette()
    {
        if (_vm == null) return;
        if (FindDescendant<Controls.ColorPalette>(this) is { } palette)
        {
            palette.DragStarted -= _vm.BeginAccentDrag;
            palette.DragEnded -= _vm.EndAccentDrag;
            palette.DragStarted += _vm.BeginAccentDrag;
            palette.DragEnded += _vm.EndAccentDrag;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        if (root is T match) return match;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(SettingsViewModel.UserFace))
        {
            _ = LoadAvatarAsync(_vm.UserFace);
        }
        else if (e.PropertyName == nameof(SettingsViewModel.TabIndex))
        {
            // 模板切换发生在绑定通知之后的一帧, 用 Dispatcher 等可视树就绪:
            // · "通用"Tab 需要重新挂调色盘的拖动事件(每次切换都是新的模板实例);
            // · "数据管理"Tab 需要重新加载头像 —— 头像在这份模板里, 切回来时是全新的
            //   Border, 之前的加载结果不会自己贴上去(真机症状: 头像不显示)。
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    HookPalette();
                    if (_vm != null) _ = LoadAvatarAsync(_vm.UserFace);
                }));
        }
    }

    /// <summary>
    /// 异步加载头像到圆形容器。
    /// ★ 重做后头像在"数据管理"Tab 的 DataTemplate 里 —— 模板内的元素不是页面的字段,
    ///   每次都要从可视树现找(找不到 = 该 Tab 未显示, 直接放弃; Tab 切回来时
    ///   PropertyChanged(TabIndex)/OnLoaded 会再触发一次)。
    /// </summary>
    private async System.Threading.Tasks.Task LoadAvatarAsync(string? faceUrl)
    {
        var (box, fallback) = FindAvatarElements();
        if (box == null) return;   // 数据管理 Tab 不在前台, 不用加载

        if (string.IsNullOrEmpty(faceUrl))
        {
            box.Background = (Brush)FindResource("AccentSoftFillBrush");
            if (fallback != null) fallback.Visibility = Visibility.Visible;
            return;
        }

        var img = await CoverLoader.LoadAsync(faceUrl, 128);
        if (img == null || _vm == null) return;

        // 模板可能在异步加载期间被换掉, 回来再找一次, 找不到就丢
        var (box2, fallback2) = FindAvatarElements();
        if (box2 == null) return;

        var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        box2.Background = brush;
        if (fallback2 != null) fallback2.Visibility = Visibility.Collapsed;
    }

    private (Border? box, TextBlock? fallback) FindAvatarElements()
        => (FindDescendantByName(this, "AvatarBox") as Border,
            FindDescendantByName(this, "AvatarFallback") as TextBlock);

    /// <summary>按名字在可视树里找元素(模板内元素没有页面字段, 只能这么找)。</summary>
    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement { Name: var n } && n == name) return root as FrameworkElement;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindDescendantByName(VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }
        return null;
    }
}
