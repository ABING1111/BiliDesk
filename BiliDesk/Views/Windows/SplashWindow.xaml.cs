using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BiliDesk.Helpers;

namespace BiliDesk.Views.Windows;

/// <summary>
/// 开屏窗: 品牌字标 BILIDESK **逐笔画出来**, 然后退场。
///
/// ★ 这是一个**独立窗口**, 不是主窗口上的一层:
///   主窗口一启动就要建首页(实测 XAML 解析 ~360ms), 那段时间界面是空的;
///   开屏窗在**下面**等着, 正好填上这段空窗期, 用户看到的是"logo 在画 → 主界面接手"。
///
/// ★★★ 三条实现约束(都来自本项目实测, 见各处注释):
///   ① 动画只作用在**单个 Path** 的 StrokeDashOffset 上(几何属性, 不是透明度);
///      退场只淡出 SplashRoot 这一层。整棵子树做透明度动画在本机**不上屏**
///      (Helpers/PageTransition 的类注释里有实测记录)。
///   ② 字标几何来自 Assets/brand-logo.xaml(pack URI 资源字典), 16 段笔画。
///      描边生长要求 dash 长度 = 路径真实长度 ⇒ 用 GeometryHelper.PathLength 算,
///      写死就会"线画不到头"。
///   ③ 窗口**不可交互**、不进任务栏、**最多显示 1.6 秒** ——
///      动画万一出异常, 用户不会被卡在开屏上。
/// </summary>
public partial class SplashWindow : FluentWindow
{
    /// <summary>字标画布: 1000 x 270(与 Assets/brand-logo.xaml 一致)</summary>
    private const double CanvasWidth = 1000;
    private const double CanvasHeight = 270;

    /// <summary>笔画粗细(画布单位)。放大到 300px 宽时约合 8px, 与图一观感接近。</summary>
    private const double StrokeThickness = 26;

    /// <summary>字标颜色: 品牌粉(与 App.xaml 里的 AccentBrush 一致)。</summary>
    private static readonly Color LogoColor = Color.FromRgb(0xFB, 0x72, 0x99);

    /// <summary>
    /// 每段笔画的书写时长。220ms 一段: 太短像"唰"一下, 太长等得心急。
    /// 16 段错开 55ms ⇒ 全部画完约 220 + 55*15 = 1045ms。
    /// </summary>
    private const int StrokeDurationMs = 220;

    /// <summary>段与段之间的错开间隔。</summary>
    private const int StrokeStaggerMs = 55;

    /// <summary>画完之后整体回弹收敛的时长。</summary>
    private const int SettleMs = 280;

    /// <summary>退场时长。</summary>
    private const int ExitMs = 260;

    /// <summary>
    /// 开屏窗最多显示这么久。到点无条件退场 ——
    /// 动画出异常/某段路径长度为 0 时不至于把用户卡在开屏上。
    /// </summary>
    private const int MaxLifetimeMs = 1600;

    private readonly List<Path> _strokes = new();
    private bool _exiting;

    public SplashWindow()
    {
        // ★★★ 品牌字标的资源字典必须**在 InitializeComponent 之前**合并进去 ——
        //   否则 XAML 里的 {StaticResource Brand_*} 在解析期就找不到资源, 直接抛
        //   XamlParseException("无法找到名为 Brand_B_Stem 的资源")。
        //   (实测踩过: 先 InitializeComponent 再合并 ⇒ 开屏窗根本建不起来。)
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/BiliDesk;component/Assets/brand-logo.xaml",
                UriKind.Absolute)
        });

        InitializeComponent();

        // 字标笔画的视觉参数: 圆头圆角、粉色、无填充。
        // ★ StrokeStartLineCap/StrokeEndLineCap 用 Round: 手写体是"起笔收笔圆润"的,
        //   默认的 Flat 会在笔头留一个方角, 一眼就看出是"机器画线"。
        var brush = new SolidColorBrush(LogoColor);
        brush.Freeze();
        foreach (var p in new[]
                 {
                     Stroke_B_Stem, Stroke_B_Top, Stroke_B_Bottom,
                     Stroke_I1,
                     Stroke_L_Stem, Stroke_L_Bar,
                     Stroke_I2,
                     Stroke_D_Stem, Stroke_D_Bowl,
                     Stroke_E_Stem, Stroke_E_Top, Stroke_E_Middle, Stroke_E_Bottom,
                     Stroke_S_Top, Stroke_S_Bottom,
                     Stroke_K_Stem, Stroke_K_Upper, Stroke_K_Lower
                 })
        {
            p.Stroke = brush;
            p.StrokeThickness = StrokeThickness;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.StrokeLineJoin = PenLineJoin.Round;
            p.Fill = Brushes.Transparent;
            _strokes.Add(p);
        }
    }

    /// <summary>
    /// 播完整套动画, 播完(或到点)后**自动关窗**。
    ///
    /// ★ 调用方**不要 await**它: 主窗口的创建与显示必须与动画并行,
    ///   否则用户会先盯着空桌面等 logo 画完才看到界面。
    /// </summary>
    public void Play()
    {
        // 兜底计时器: 无论动画链怎么走, 到点一定退场
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MaxLifetimeMs) };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            BeginExit();
        };
        guard.Start();

        var finished = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MaxLifetimeMs + 200) };
        finished.Tick += (_, _) =>
        {
            finished.Stop();
            BeginExit();
        };
        finished.Start();

        // 描边生长: 逐段错开。
        // ★ 用 StrokeDashOffset 从"整条长度"走到 0 ⇒ 笔画从起点长出来。
        //   dash 数组是 [长度, 长度]: 实线段正好是整条路径, 间隙也是整条长度,
        //   于是"偏移=长度"时完全看不见, "偏移=0"时整条画全。
        var idx = 0;
        foreach (var p in _strokes)
        {
            var len = GeometryHelper.PathLength(p.Data);
            if (len <= 0.5)
            {
                // 路径长度为 0(几何算不出来): 直接当"已画好", 不给它做动画 ——
                // 宁可少一笔, 也不要卡住整个开屏。
                p.StrokeDashArray = null;
                continue;
            }

            p.StrokeDashArray = new DoubleCollection(new[] { len, len });
            p.StrokeDashOffset = len;

            var anim = new DoubleAnimation(len, 0, TimeSpan.FromMilliseconds(StrokeDurationMs))
            {
                EasingFunction = AnimationCurves.StrokeDraw,
                BeginTime = TimeSpan.FromMilliseconds(idx * StrokeStaggerMs)
            };
            p.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
            idx++;
        }

        var totalMs = idx * StrokeStaggerMs + StrokeDurationMs;

        // 画完之后整体回弹收敛: 1.04 → 1.0, 带一点过冲(SettleBack 的 y1=1.56)。
        // ★ 只动 RenderTransform, 不动 Opacity —— 整块的透明度变化在本机不可靠。
        var settle = new DoubleAnimation(1.04, 1.0, TimeSpan.FromMilliseconds(SettleMs))
        {
            EasingFunction = AnimationCurves.SettleBack,
            BeginTime = TimeSpan.FromMilliseconds(totalMs)
        };
        var scale = new ScaleTransform(1.04, 1.04);
        LogoViewbox.RenderTransform = scale;
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, settle);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, settle);

        // 退场时机: 画完 + 回弹
        var exit = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(totalMs + SettleMs + 120)
        };
        exit.Tick += (_, _) =>
        {
            exit.Stop();
            BeginExit();
        };
        exit.Start();
    }

    /// <summary>
    /// 退场: 淡出 + 微缩, 然后关窗。
    ///
    /// ★ 只对 SplashRoot 这一层做透明度动画(单控件, 本机可靠),
    ///   窗口本身不淡 —— 有边框/圆角时整窗淡出会露出桌面边缘, 很脏。
    /// </summary>
    private void BeginExit()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(ExitMs))
            {
                EasingFunction = AnimationCurves.EmphasizedAccelerate
            };
            fade.Completed += (_, _) => Close();
            SplashRoot.BeginAnimation(OpacityProperty, fade);

            // 微微缩一点, 退场更有"沉下去"的感觉
            var scale = new ScaleTransform(1.0, 1.0);
            SplashRoot.RenderTransformOrigin = new Point(0.5, 0.5);
            SplashRoot.RenderTransform = scale;
            var shrink = new DoubleAnimation(1.0, 0.96, TimeSpan.FromMilliseconds(ExitMs))
            {
                EasingFunction = AnimationCurves.EmphasizedAccelerate
            };
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, shrink);
        }
        catch
        {
            // 动画出问题就直接关: 开屏是锦上添花, 绝不能因此卡住程序
            Close();
        }
    }

    /// <summary>开屏窗永远不参与命中测试(它只是"品牌一闪")</summary>
    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        try { IsHitTestVisible = false; } catch { /* 忽略 */ }
    }
}
