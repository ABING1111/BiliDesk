using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 吸顶 Tab 栏右侧的可折叠搜索(2026-11-20, 历史 / 收藏页共用)。
///
/// 收起 = 34×34 的磨砂圆(放大镜); 点击后宽度动画展开成输入框并自动聚焦。
/// 收纳规则(避免"搜索着搜索着词没了"):
///   · 只有**内容为空**时才允许收回圆: 点页面其它地方(由窗口 PreviewMouseDown 监听)、
///     失焦、Esc 都走这一条;
///   · 有内容时按 Esc 只清空文字, 再按一次才收回;
///   · 程序把 Text 设成非空(VM 里保留了上次的关键词)时自动展开, 不会出现
///     "看不见输入框、列表却还在过滤"的状态。
///
/// 与 <see cref="SearchBox"/> 的分工: SearchBox 是常驻输入框(动态页左栏 / 右栏的页内搜索),
/// 这里是 tab 栏里的可折叠版本(历史 / 收藏页)。占位符/清除按钮同样由 TextChanged 直接置 Visibility。
/// </summary>
public partial class TabSearchBox : UserControl
{
    /// <summary>
    /// 展开后的目标宽度(空间不够时会按可用宽度收敛, 见 ExpandedTargetWidth)。
    /// </summary>
    private const double ExpandedWidth = 240;

    /// <summary>
    /// 可用宽度再小也不会低于这个值(否则输入框没法用)。
    /// 低于它时控件会**向左借位**(见 ExpandedLayout): 右缘仍贴着宿主右缘,
    /// 多出来的部分临时盖在 tab 胶囊右端上方, 不会被 ScrollViewer 裁掉。
    /// </summary>
    private const double MinExpandedWidth = 180;

    private const int ExpandMs = 200;
    private const int CollapseMs = 160;
    private const int ContentFadeMs = 140;

    private bool _expanded;

    /// <summary>展开期间挂上去的窗口级"点外面就收起"监听(收起/卸载时必须退掉)。</summary>
    private Window? _hookedWindow;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TabSearchBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextPropertyChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(TabSearchBox),
        new FrameworkPropertyMetadata("搜索", OnPlaceholderTextChanged));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>当前是否展开(只读, 给探针/调试看)。</summary>
    public bool IsExpanded => _expanded;

    public TabSearchBox()
    {
        InitializeComponent();
        // 卸载时退掉"点外面收起"的窗口监听; 重新进树时(页面缓存复用)补回来 ——
        // 绑定期自动展开时窗口可能还没建好, AttachWindowHook 只能拿到 null。
        Unloaded += (_, _) => DetachWindowHook();
        Loaded += (_, _) =>
        {
            if (_expanded) AttachWindowHook();
            else Width = CollapsedSize;      // 高度由 XAML 绑到 tab 胶囊, 收起态是等高等宽的圆
        };
        // 高度跟着 tab 胶囊走时(H 绑定变化), 收起态直径也要跟着变
        SizeChanged += (_, _) =>
        {
            if (!_expanded && Math.Abs(Width - CollapsedSize) > 0.5) Width = CollapsedSize;
        };
        SyncTextUi();
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TabSearchBox)d).SyncTextUi();

    private static void OnPlaceholderTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (TabSearchBox)d;
        if (box.Placeholder != null) box.Placeholder.Text = e.NewValue as string ?? "搜索";
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => Expand(focus: true);

    /// <summary>收起态直径 = 控件高度(= tab 胶囊高度)。未量到之前用 XAML 的 44 兜底。</summary>
    private double CollapsedSize => ActualHeight > 0.5 ? ActualHeight : (double.IsNaN(Height) ? 44 : Height);

    /// <summary>
    /// 展开后的 (宽度, 向左借位):
    ///   · 默认 240, 右边放不下就收到"可用宽度";
    ///   · 可用宽度不足 <see cref="MinExpandedWidth"/> 时, 宽度保底、多出的部分用
    ///     TranslateTransform 向左借(tab 胶囊很长时, 搜索会临时盖住它右端) ——
    ///     这样右缘永远贴着宿主行右缘, 不会被 ScrollViewer 裁掉。
    /// </summary>
    private (double Width, double Shift) ExpandedLayout()
    {
        var target = ExpandedWidth;
        if (Parent is not FrameworkElement host || host.ActualWidth <= 0) return (target, 0);

        // 以未借位时的左缘算可用宽度: 借位只影响渲染位移, 不影响布局位置
        var left = TranslatePoint(new Point(0, 0), host).X - SlideX.X;
        var avail = host.ActualWidth - left;
        if (avail <= 0) return (target, 0);
        if (avail >= target) return (target, 0);

        target = Math.Max(MinExpandedWidth, avail);
        return (target, Math.Max(0, target - avail));
    }

    private void Expand(bool focus)
    {
        if (_expanded) return;
        _expanded = true;

        OpenButton.Visibility = Visibility.Collapsed;
        // 放大镜从"圆心"切到"输入框左端"; 收起时再切回圆心
        SearchGlyph.HorizontalAlignment = HorizontalAlignment.Left;
        SearchGlyph.Margin = new Thickness(10, 0, 0, 0);

        ContentHost.Visibility = Visibility.Visible;
        ContentHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ContentFadeMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        var (targetWidth, shift) = ExpandedLayout();
        AnimateWidth(targetWidth, ExpandMs);
        AnimateShift(-shift, ExpandMs);
        AttachWindowHook();

        if (focus) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => { Box.Focus(); Box.CaretIndex = Box.Text.Length; }));
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;

        DetachWindowHook();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ContentFadeMs));
        fade.Completed += (_, _) =>
        {
            if (_expanded) return;   // 淡出途中又被点开了
            ContentHost.Visibility = Visibility.Collapsed;
            OpenButton.Visibility = Visibility.Visible;
        };
        ContentHost.BeginAnimation(OpacityProperty, fade);

        AnimateShift(0, CollapseMs);
        AnimateWidth(CollapsedSize, CollapseMs, () =>
        {
            if (_expanded) return;
            SearchGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            SearchGlyph.Margin = new Thickness(0);
        });
    }

    private void AnimateShift(double to, int ms)
    {
        SlideX.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>宽度动画。用 ActualWidth 当起点: 连续开合时从"当前画面上的宽度"接着走, 不跳。</summary>
    private void AnimateWidth(double to, int ms, Action? done = null)
    {
        var from = ActualWidth > 0 ? ActualWidth : Width;
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(WidthProperty, null);
            Width = to;
            done?.Invoke();
        };
        BeginAnimation(WidthProperty, anim);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => SyncTextUi();

    private void SyncTextUi()
    {
        if (Placeholder == null || ClearButton == null) return;
        var empty = string.IsNullOrWhiteSpace(Box.Text);
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        // 程序把关键词设回来(页面被缓存复用 / 收藏夹切换保留关键词): 自动展开,
        // 不能让输入框藏着一颗圆、列表却在过滤。
        if (!empty && !_expanded) Expand(focus: false);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Box.Text = "";
        Box.Focus();
    }

    /// <summary>Esc: 有内容先清空; 已经空了才收起。</summary>
    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Box.Text.Length > 0) Box.Text = "";
        else Collapse();
        e.Handled = true;
    }

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 焦点还在控件内部(点清除按钮)时不收
        if (IsKeyboardFocusWithin) return;
        if (Box.Text.Trim().Length == 0) Collapse();
    }

    private void AttachWindowHook()
    {
        var win = Window.GetWindow(this);
        if (win == null || ReferenceEquals(win, _hookedWindow)) return;
        DetachWindowHook();
        _hookedWindow = win;
        win.PreviewMouseDown += OnWindowMouseDown;
    }

    private void DetachWindowHook()
    {
        if (_hookedWindow == null) return;
        _hookedWindow.PreviewMouseDown -= OnWindowMouseDown;
        _hookedWindow = null;
    }

    /// <summary>点页面其它地方: 只有内容为空才收起(避免把用户刚输入的关键词藏起来)。</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_expanded || IsMouseOver) return;
        if (Box.Text.Trim().Length == 0) Collapse();
    }
}
