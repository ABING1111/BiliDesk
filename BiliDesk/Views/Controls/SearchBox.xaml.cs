using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 统一的小搜索框(胶囊形): 放大镜 + 占位符 + 清除按钮。
///
/// 宿主只绑 <see cref="Text"/>(双向)与 <see cref="PlaceholderText"/>:
///   · 历史页: Text → HistoryViewModel.SearchText(本地过滤已加载条目);
///   · 收藏页: Text → FavoritesViewModel.SearchText(服务端 keyword 搜索整个收藏夹);
///   · 动态页: Text → FollowViewModel.UserSearchText(服务端搜索已关注的 UP)。
///
/// ★ 为什么占位符/清除按钮由 code-behind 直接控制:
///   上面三处都踩过"绑定时占位符不消失"的坑(绑定失败是静默的)。TextChanged 是输入、
///   删除、粘贴、代码赋值都会走的确定性事件, 不依赖转换器/DataContext。
/// </summary>
public partial class SearchBox : UserControl
{
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

    public SearchBox()
    {
        InitializeComponent();

        MouseEnter += (_, _) => ApplyChrome();
        MouseLeave += (_, _) => ApplyChrome();

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

    private void OnTextChanged(object sender, TextChangedEventArgs e) => SyncVisualState();

    private void SyncVisualState()
    {
        if (Placeholder == null || ClearButton == null) return;
        var empty = string.IsNullOrWhiteSpace(Box.Text);
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // 走 TextBox: 绑定会同步回 Text, TextChanged 也会刷新占位符
        Box.Text = "";
        Box.Focus();
    }

    /// <summary>Esc: 有内容时清空并留在输入框里; 没内容时把键盘焦点还给页面。</summary>
    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Box.Text.Length > 0)
        {
            Box.Text = "";
            e.Handled = true;
        }
    }

    private void OnBoxFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => ApplyChrome();

    /// <summary>
    /// 悬停 / 聚焦的外观。用 SetResourceReference(而不是把 Brush 取出来赋给属性):
    /// 主题切换时 DynamicResource 会自动换成新主题的色值。
    /// </summary>
    private void ApplyChrome()
    {
        if (Bd == null) return;

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
            Bd.BorderBrush = System.Windows.Media.Brushes.Transparent;
            Bd.SetResourceReference(Border.BackgroundProperty, "ControlFillDefaultBrush");
        }
        SearchGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
    }
}
