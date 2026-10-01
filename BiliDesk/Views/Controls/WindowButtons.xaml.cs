using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 窗口右上角的三个自绘按钮(最小化 / 最大化还原 / 关闭)。
///
/// 用法: 窗口设 WindowStyle=None + WindowChrome, 在标题栏右侧放一个 &lt;controls:WindowButtons/&gt;。
/// 最大化状态与图标由它自己跟随窗口的 StateChanged 维护, 宿主不用管。
/// 颜色通过 <see cref="IconBrush"/> / <see cref="HoverBrush"/> 传进来:
/// 浅色顶栏用主题文字色, 播放器那种深色顶栏直接传白色。
/// </summary>
public partial class WindowButtons : UserControl
{
    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(WindowButtons), new PropertyMetadata(null));

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(WindowButtons), new PropertyMetadata(null));

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public Brush? HoverBrush
    {
        get => (Brush?)GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    public WindowButtons()
    {
        InitializeComponent();
        // 默认值走资源引用(跟随深浅色主题); 宿主显式设了值就会覆盖掉它
        SetResourceReference(IconBrushProperty, "TextPrimaryBrush");
        SetResourceReference(HoverBrushProperty, "ControlFillHoverBrush");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private Window? _win;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _win = Window.GetWindow(this);
        if (_win == null) return;
        _win.StateChanged += OnWindowStateChanged;
        UpdateMaxGlyph();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_win != null) _win.StateChanged -= OnWindowStateChanged;
        _win = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaxGlyph();

    /// <summary>最大化时图标变成"还原", 提示语也跟着换</summary>
    private void UpdateMaxGlyph()
    {
        var maximized = _win?.WindowState == WindowState.Maximized;
        // E922 = 最大化, E923 = 还原(两个正方形错开)
        MaxGlyph.Text = maximized ? "\uE923" : "\uE922";
        BtnMax.ToolTip = maximized ? "向下还原" : "最大化";
    }

    private void OnMinClick(object sender, RoutedEventArgs e)
    {
        if (_win != null) _win.WindowState = WindowState.Minimized;
    }

    private void OnMaxClick(object sender, RoutedEventArgs e)
    {
        if (_win == null) return;
        _win.WindowState = _win.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaxGlyph();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _win?.Close();
}
