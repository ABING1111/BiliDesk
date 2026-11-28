using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 统一的小搜索框(胶囊形): 放大镜 + 占位符 + 清除按钮。
///
/// 宿主只绑 <see cref="Text"/>(双向)与 <see cref="PlaceholderText"/>:
///   · 历史页: Text → HistoryViewModel.SearchText(本地过滤已加载条目);
///   · 收藏页: Text → FavoritesViewModel.SearchText(服务端 keyword 搜索整个收藏夹);
///   · 动态页: Text → FollowViewModel.UserSearchText(服务端搜索已关注的 UP);
///             右栏页内搜索则用 <see cref="Collapsible"/>=true 的收起态。
///
/// ★ 为什么占位符/清除按钮由 code-behind 直接控制:
///   上面三处都踩过"绑定时占位符不消失"的坑(绑定失败是静默的)。TextChanged 是输入、
///   删除、粘贴、代码赋值都会走的确定性事件, 不依赖转换器/DataContext。
///
/// ★ Collapsible(2026-11-28 用户要求"动态页搜索默认收缩、点击展开"):
///   默认 false 保持原来的常驻输入框; 打开后收起态 = 与高度等宽的方块(放大镜按钮),
///   点击后宽度动画展开成 240 的输入框。收纳规则与 TabSearchBox 一致:
///     · 只有内容为空时才自动收回(失焦 / Esc / 点页面其它地方);
///     · 有内容时按 Esc 只清空, 再按一次才收回;
///     · 程序把 Text 设成非空时自动展开, 避免"看不到输入框、列表却还在过滤"。
///   收缩/展开只改自己的 Width: 宿主把它放在 Grid 的 Auto 列里时, 宽度变化会由
///   左侧的 * 列吸收 —— 动态页因此表现为"向右锚定、向左生长", 右侧的排版 chip 不动。
///
/// ★ ShapeRadius(2026-11-28 用户要求"胶囊改成圆角矩形"):
///   收起态是"高×高"的方块, 圆角由它决定 —— 17(默认)= 正圆, 8 = 圆角方块。
///   8 与设置页输入类控件同一个基准; 动态页右栏与 UP 主页用它, 历史/收藏保持圆。
/// </summary>
public partial class SearchBox : UserControl
{
    /// <summary>展开后的目标宽度。</summary>
    private const double ExpandedWidth = 240;

    private const int ExpandMs = 200;
    private const int CollapseMs = 160;
    private const int ContentFadeMs = 140;

    private bool _expanded;

    /// <summary>展开期间挂上去的窗口级"点外面就收起"监听(收起/卸载时必须退掉)。</summary>
    private Window? _hookedWindow;

    /// <summary>
    /// 底衬/悬停底的圆角(2026-11-28, 用户要求"胶囊改成圆角矩形")。
    ///
    /// ★ 默认 17 = 高 34 的一半 ⇒ 胶囊。历史 / 收藏 / 动态左栏那些老调用方不设它,
    ///   观感与改动前完全一致。
    /// ★ 设 8 得到圆角矩形 —— 8 是本项目输入类控件的既有基准(见 Themes/Styles.xaml 的
    ///   SettingsComboStyle / 下拉框底衬)。UP 主页与动态页右栏的页内搜索用它。
    /// ★ 收起态(仅 Collapsible)是"34×34 + 本圆角": 半径 17 是正圆, 半径 8 是圆角方块。
    /// </summary>
    public static readonly DependencyProperty ShapeRadiusProperty = DependencyProperty.Register(
        nameof(ShapeRadius), typeof(CornerRadius), typeof(SearchBox),
        new FrameworkPropertyMetadata(new CornerRadius(17)));

    public CornerRadius ShapeRadius
    {
        get => (CornerRadius)GetValue(ShapeRadiusProperty);
        set => SetValue(ShapeRadiusProperty, value);
    }

    /// <summary>搜索文本(双向)。</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextPropertyChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>占位提示文案。</summary>
    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata("搜索", OnPlaceholderTextChanged));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>是否可折叠: true = 默认收成一枚圆形放大镜按钮, 点击展开。</summary>
    public static readonly DependencyProperty CollapsibleProperty = DependencyProperty.Register(
        nameof(Collapsible), typeof(bool), typeof(SearchBox),
        new FrameworkPropertyMetadata(false, OnCollapsibleChanged));

    public bool Collapsible
    {
        get => (bool)GetValue(CollapsibleProperty);
        set => SetValue(CollapsibleProperty, value);
    }

    /// <summary>当前是否展开(只读, 给探针/调试看)。</summary>
    public bool IsExpanded => _expanded;

    /// <summary>
    /// 内容为空时收起(2026-11-28)。
    ///
    /// ★ 为什么需要这个公开入口: UP 主页的「投稿」与「图文」两个 Tab 各有一个搜索框,
    ///   但**共用同一个 SearchText**。在投稿框里输入会把图文档一起带成"有内容"从而自动展开,
    ///   清空后它却不会自己收回去(只有失焦/Esc/点外面才收) —— 用户切到图文 Tab 会看到一个
    ///   展开的空框。切 Tab 时对两个框都调一次这个方法, 空的收起、有词的保持展开。
    /// </summary>
    public void CollapseIfEmpty()
    {
        if (!Collapsible || !_expanded) return;
        if (Box.Text.Trim().Length == 0) Collapse();
    }

    public SearchBox()
    {
        InitializeComponent();

        MouseEnter += (_, _) => ApplyChrome();
        MouseLeave += (_, _) => ApplyChrome();

        Loaded += (_, _) =>
        {
            if (!Collapsible) return;
            // 绑定期自动展开时窗口可能还没建好, AttachWindowHook 只能拿到 null; 重新进树时补回来。
            if (_expanded) AttachWindowHook();
            else Width = CollapsedSize;
        };
        Unloaded += (_, _) => DetachWindowHook();

        // 绑定可能在构造之后才落值; 这里先按当前值同步一次占位符/清除按钮
        SyncVisualState();
        ApplyChrome();
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SearchBox)d).SyncVisualState();

    private static void OnPlaceholderTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SearchBox)d;
        if (box.Placeholder != null) box.Placeholder.Text = e.NewValue as string ?? "搜索";
    }

    private static void OnCollapsibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SearchBox)d;
        if (!box.Collapsible || box._expanded) return;

        // XAML 里 Collapsible / Text 的赋值顺序不保证: 若关键词已经在了, 直接落到展开态,
        // 否则会出现"输入框藏起来了、列表却还在按关键词过滤"。
        var hasText = !string.IsNullOrWhiteSpace(box.Text) ||
                      (box.Box != null && !string.IsNullOrWhiteSpace(box.Box.Text));
        if (hasText) box.Expand(focus: false);
        else box.ApplyCollapsedVisuals();
    }

    /// <summary>收起态直径 = 控件高度(默认 34)。未量到之前用 XAML 的 Height 兜底。</summary>
    private double CollapsedSize => Height > 0.5 && !double.IsNaN(Height) ? Height : 34;

    private void OnOpenClick(object sender, RoutedEventArgs e) => Expand(focus: true);

    private void Expand(bool focus)
    {
        if (!Collapsible || _expanded) return;
        _expanded = true;

        OpenButton.Visibility = Visibility.Collapsed;
        // 放大镜从"圆心"切到"输入框左端"; 收起时再切回圆心
        SearchGlyph.HorizontalAlignment = HorizontalAlignment.Left;
        SearchGlyph.Margin = new Thickness(12, 0, 0, 0);

        ContentHost.Visibility = Visibility.Visible;
        if (IsLoaded)
        {
            ContentHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ContentFadeMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            AnimateWidth(ExpandedWidth, ExpandMs);
        }
        else
        {
            // 尚未进树(例如 VM 里保留了上次的关键词): 不做动画, 直接落到展开态。
            ContentHost.Opacity = 1;
            Width = ExpandedWidth;
        }

        AttachWindowHook();
        ApplyChrome();

        if (focus) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => { Box.Focus(); Box.CaretIndex = Box.Text.Length; }));
    }

    private void Collapse()
    {
        if (!Collapsible || !_expanded) return;
        _expanded = false;

        DetachWindowHook();

        if (!IsLoaded)
        {
            ApplyCollapsedVisuals();
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ContentFadeMs));
        fade.Completed += (_, _) =>
        {
            if (_expanded) return;   // 淡出途中又被点开了
            ContentHost.Visibility = Visibility.Collapsed;
            OpenButton.Visibility = Visibility.Visible;
        };
        ContentHost.BeginAnimation(OpacityProperty, fade);

        AnimateWidth(CollapsedSize, CollapseMs, () =>
        {
            if (_expanded) return;
            SearchGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            SearchGlyph.Margin = new Thickness(0);
            ApplyChrome();
        });
    }

    /// <summary>无动画地落到收起态(初始化 / 未进树时用)。</summary>
    private void ApplyCollapsedVisuals()
    {
        if (OpenButton == null || ContentHost == null || SearchGlyph == null) return;

        Width = CollapsedSize;
        OpenButton.Visibility = Visibility.Visible;
        ContentHost.Visibility = Visibility.Collapsed;
        ContentHost.Opacity = 0;
        SearchGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        SearchGlyph.Margin = new Thickness(0);
        ApplyChrome();
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

    private void OnTextChanged(object sender, TextChangedEventArgs e) => SyncVisualState();

    private void SyncVisualState()
    {
        if (Placeholder == null || ClearButton == null) return;
        var empty = string.IsNullOrWhiteSpace(Box.Text);
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        // 程序把关键词设回来(页面被缓存复用 / 收藏夹切换保留关键词)时自动展开,
        // 不能让输入框藏着一颗圆、列表却在过滤。
        if (Collapsible && !empty && !_expanded) Expand(focus: false);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // 走 TextBox: 绑定会同步回 Text, TextChanged 也会刷新占位符
        Box.Text = "";
        Box.Focus();
    }

    /// <summary>Esc: 有内容先清空; 已经空了再收起(仅折叠模式)。</summary>
    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Box.Text.Length > 0)
        {
            Box.Text = "";
            e.Handled = true;
        }
        else if (Collapsible)
        {
            Collapse();
            e.Handled = true;
        }
    }

    private void OnBoxFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        ApplyChrome();

        // 焦点还在控件内部(点清除按钮)时不收; 只有空着离开才收回圆。
        if (Collapsible && !IsKeyboardFocusWithin && Box.Text.Trim().Length == 0) Collapse();
    }

    /// <summary>
    /// 悬停 / 聚焦的外观。用 SetResourceReference(而不是把 Brush 取出来赋给属性):
    /// 主题切换时 DynamicResource 会自动换成新主题的色值。
    /// </summary>
    private void ApplyChrome()
    {
        if (Bd == null || SearchGlyph == null) return;

        // 收起态只显示一枚圆按钮, 悬停底由 OpenButton 自己画, 这里保持干净的浅灰圆。
        if (Collapsible && !_expanded)
        {
            Bd.BorderBrush = Brushes.Transparent;
            Bd.SetResourceReference(Border.BackgroundProperty, "ControlFillDefaultBrush");
            SearchGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            return;
        }

        var focused = Box.IsKeyboardFocusWithin;
        if (focused)
        {
            Bd.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            Bd.SetResourceReference(Border.BackgroundProperty, "ControlFillHoverBrush");
            SearchGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            return;
        }

        if (IsMouseOver)
        {
            Bd.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
            Bd.SetResourceReference(Border.BackgroundProperty, "ControlFillHoverBrush");
        }
        else
        {
            Bd.BorderBrush = Brushes.Transparent;
            Bd.SetResourceReference(Border.BackgroundProperty, "ControlFillDefaultBrush");
        }
        SearchGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
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
