using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 调色盘控件(饱和度 × 明度方块 + 色相竖条)。
///
/// 只负责"让用户挑一个颜色", 不碰任何业务: 当前色由 <see cref="Color"/> 双向暴露,
/// 设置页把它绑到 <c>SettingsViewModel.AccentColor</c> 上。
///
/// 为什么不用 WPF 自带的 <c>System.Windows.Forms.ColorDialog</c>:
///   那个是 Win32 老式对话框, 和整套 Fluent 圆角风格完全不搭, 而且它会以模态窗口弹出
///   —— 在设置页里想"边调边看效果"就得反复开关。内嵌的调色盘可以拖一下就立刻看到整个
///   应用的强调色跟着变(换色本身是实时的, 见 ThemeService.SetAccent)。
///
/// 内部状态用 HSV 而不是只存一个 Color:
///   选中纯黑时 H/S/V 全都退化成 0, 从 Color 反推不回来 —— 用户把明度拖到底之后色相会
///   自己跳回红色, 拖回去就变色了。所以 H/S/V 自己留着, Color 只是它的投影。
/// </summary>
public partial class ColorPalette : UserControl
{
    // ------------------------------------------------------------------ 依赖属性

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(ColorPalette),
        new FrameworkPropertyMetadata(Colors.Red,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorChanged));

    /// <summary>当前选中的颜色。外部改它 → 调色盘跟着动; 用户拖动 → 它被写回。</summary>
    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>
    /// 开始 / 结束拖动。
    ///
    /// 宿主(设置页)用它把"拖动中"这一段时间标出来: 拖动期间换色可以跳过那次
    /// 全窗口可视树重解析(每秒几十次, 每次都遍历一遍是白烧的帧), 松手时再补做一次。
    /// 用事件而不是给控件塞一个"是否跳过"的属性: 这件事完全是宿主的策略, 控件不该知道。
    /// </summary>
    public event Action? DragStarted;
    public event Action? DragEnded;

    // ------------------------------------------------------------------ HSV 状态

    private double _h = 1;   // 0~360
    private double _s = 1;   // 0~1
    private double _v = 1;   // 0~1

    /// <summary>
    /// 抑制回写。拖动时我们改 Color → 触发 OnColorChanged → 它又去反推 HSV,
    /// 而反推在灰色/黑色上会丢信息(见类注释)。这个标志把那次回灌挡掉。
    /// </summary>
    private bool _suppress;

    /// <summary>正在拖动哪个区域(鼠标移出控件后仍要继续跟随, 所以不能只看 IsMouseOver)</summary>
    private bool _dragSv;
    private bool _dragHue;

    public ColorPalette()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncMarkers();
        // 圆角裁剪(2026-10-03 用户要求"为画盘添加圆角"):
        // 内层 Border 的 CornerRadius 只圆它自己的底, **不会裁剪子元素** ——
        // 里面的渐变 Rectangle 实际渲染出来是直角, 圆角框被四个直角盖住等于没有。
        // 在 SizeChanged 时按最新尺寸生成圆角 RectangleGeometry 裁到内容上。
        SvHost.SizeChanged += (_, _) => ApplyRoundedClip(SvHost, SvClipRadius);
        HueHost.SizeChanged += (_, _) => ApplyRoundedClip(HueHost, HueClipRadius);
    }

    /// <summary>色块的圆角半径(与外层 CornerRadius=8 的内缘一致)</summary>
    private const double SvClipRadius = 7;
    /// <summary>色相条的圆角半径</summary>
    private const double HueClipRadius = 5;

    private static void ApplyRoundedClip(System.Windows.UIElement host, double radius)
    {
        if (host is not System.Windows.FrameworkElement fe) return;
        var w = fe.ActualWidth;
        var h = fe.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = Math.Min(radius, Math.Min(w, h) / 2);
        fe.Clip = new RectangleGeometry(new System.Windows.Rect(0, 0, w, h), r, r);
    }

    // ------------------------------------------------------------------ 属性同步

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (ColorPalette)d;
        if (p._suppress) return;
        p.AdoptColor((Color)e.NewValue);
    }

    /// <summary>把一个外部来的颜色反推成 HSV 并刷新界面</summary>
    private void AdoptColor(Color c)
    {
        RgbToHsv(c, out _h, out _s, out _v);
        UpdateHueBase();
        SyncMarkers();
    }

    /// <summary>只有色相底色需要随 _h 重刷; 饱和度和明度是靠叠上去的两层渐变表现的</summary>
    private void UpdateHueBase() => HueBase.Color = HsvToRgb(_h, 1, 1);

    /// <summary>把两个游标挪到当前 HSV 对应的位置</summary>
    private void SyncMarkers()
    {
        // Loaded 之前 ActualWidth 还是 0, 这时算出来会全挤在左上角 —— 等布局完再补一次
        var w = SvHost.ActualWidth;
        var h = SvHost.ActualHeight;
        if (w > 0 && h > 0)
        {
            Canvas.SetLeft(SvMarker, _s * w - SvMarker.Width / 2);
            Canvas.SetTop(SvMarker, (1 - _v) * h - SvMarker.Height / 2);
        }

        var hh = HueHost.ActualHeight;
        if (hh > 0)
            Canvas.SetTop(HueMarker, _h / 360.0 * hh - HueMarker.Height / 2);
    }

    /// <summary>窗口尺寸变化时游标要跟着重新定位(百分比没变, 但像素位置变了)</summary>
    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var result = base.ArrangeOverride(arrangeBounds);
        SyncMarkers();
        return result;
    }

    // ------------------------------------------------------------------ 拖动

    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        _dragSv = true;
        // 抢捕获: 拖动过程中鼠标会跑到方块外面, 不捕获的话后续 Move/Up 全收不到,
        // 松手位置也容易跑偏(拖出边界就"断线"了)。
        SvHost.CaptureMouse();
        DragStarted?.Invoke();
        ApplySv(e.GetPosition(SvHost));
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (!_dragSv) return;
        ApplySv(e.GetPosition(SvHost));
    }

    private void OnSvUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragSv) return;
        _dragSv = false;
        SvHost.ReleaseMouseCapture();
        DragEnded?.Invoke();
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _dragHue = true;
        HueHost.CaptureMouse();
        DragStarted?.Invoke();
        ApplyHue(e.GetPosition(HueHost));
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (!_dragHue) return;
        ApplyHue(e.GetPosition(HueHost));
    }

    private void OnHueUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragHue) return;
        _dragHue = false;
        HueHost.ReleaseMouseCapture();
        DragEnded?.Invoke();
    }

    /// <summary>
    /// 拖动中鼠标松开、或捕获被系统抢走(Alt+Tab / 弹出别的窗口)。
    /// 不接这个事件的话 <c>_dragSv</c> 会一直是 true —— 用户回来一晃鼠标, 颜色就莫名其妙跟着变。
    /// </summary>
    private void OnHostLostCapture(object sender, MouseEventArgs e)
    {
        // 两个都归位, 并补一次 DragEnded: 捕获丢了却不同步宿主, 那边会永远以为还在拖,
        // 于是后续所有换色都省掉重解析(切页面就会看到旧颜色)。
        var wasDragging = _dragSv || _dragHue;
        _dragSv = false;
        _dragHue = false;
        if (wasDragging) DragEnded?.Invoke();
    }

    /// <summary>把方块内的一个点换算成饱和度和明度。
    /// 注意坐标要夹到 [0,1]: 拖动时鼠标会跑到方块外面, 不夹的话会算出负饱和度(变黑)或 >1(溢出)。</summary>
    private void ApplySv(Point p)
    {
        var w = SvHost.ActualWidth;
        var h = SvHost.ActualHeight;
        if (w <= 0 || h <= 0) return;

        _s = Math.Clamp(p.X / w, 0, 1);
        _v = Math.Clamp(1 - p.Y / h, 0, 1);
        Push();
    }

    private void ApplyHue(Point p)
    {
        var h = HueHost.ActualHeight;
        if (h <= 0) return;

        _h = Math.Clamp(p.Y / h, 0, 1) * 360.0;
        // 360 和 0 是同一个红, 但在渐变上是两端 —— 夹到 359.99 免得游标跳到顶上
        if (_h >= 360) _h = 359.99;
        UpdateHueBase();
        Push();
    }

    /// <summary>把当前 HSV 投影成 Color 写回去, 并刷新两个游标</summary>
    private void Push()
    {
        SyncMarkers();
        _suppress = true;
        try
        {
            Color = HsvToRgb(_h, _s, _v);
        }
        finally
        {
            _suppress = false;
        }
    }

    // ------------------------------------------------------------------ 色彩换算

    /// <summary>HSV → RGB。H: 0~360, S/V: 0~1</summary>
    public static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;   // 负数/超界都归一化
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;

        double r, g, b;
        switch ((int)(h / 60))
        {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>RGB → HSV。灰色的 H 会算成 0 —— 这正是本类要自己存 HSV 而不是只存 Color 的原因。</summary>
    public static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;

        v = max;
        s = max <= 0 ? 0 : d / max;

        if (d <= 0)
        {
            h = 0;
            return;
        }

        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }
}
