using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliDesk.Helpers;

/// <summary>
/// ScrollViewer 平滑滚动(鼠标滚轮)。
///
/// 背景: 历史页的 ListBox 用默认虚拟化时, 滚动单位是**条目**(ScrollUnit=Item),
/// 一格滚轮 = 3 个条目 = 3 个视频卡片, 而且是瞬移 —— 用户明确要"平滑滚动"。
///
/// 用法: 任意元素上 <c>helpers:SmoothScroll.Enabled="True"</c>
/// (挂在 ListBox/ItemsControl 上会自动找它可视树里的第一个 ScrollViewer)。
///
/// 实现: 拦截 PreviewMouseWheel, 不再让引擎瞬移, 而是把目标偏移沿一段缓动过去
/// (默认 180px / 280ms; 每处可用 <see cref="StepProperty"/> / <see cref="DurationMsProperty"/> 覆盖)。
/// 动画驱动一个代理附加属性, 它的变更回调里调 ScrollToVerticalOffset。
/// 连滚时每次从"当前实际位置"重新出发、目标累加 —— 手感是连续加速的平滑滚动。
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    /// <summary>代理偏移: 动画驱动它, 变更回调把值落到 ScrollViewer 上</summary>
    private static readonly DependencyProperty ProxyOffsetProperty = DependencyProperty.RegisterAttached(
        "ProxyOffset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is ScrollViewer sv)
                sv.ScrollToVerticalOffset((double)e.NewValue);
        }));

    /// <summary>
    /// 一格滚轮滚多少像素。用户反馈 130 偏慢(2026-09-25), 调到 180 —— 约 1 个卡片行高,
    /// 加上缓动仍是平滑的; 连滚会自然累加。
    /// ★ 这是**全应用默认值**。某页面想要别的手感就用 <see cref="StepProperty"/> 单独覆盖,
    ///   不要去改这个常量 —— 改了会把所有页面的手感一起带走。
    /// </summary>
    private const double PixelsPerNotch = 180;

    /// <summary>默认动画时长。见 <see cref="DurationMsProperty"/></summary>
    private const int DefaultDurationMs = 280;

    /// <summary>
    /// 单页覆盖: 一格滚轮滚多少像素(≤0 = 用全应用默认值)。
    /// 历史页用它调到更快 —— 那边一屏放得下更多卡片、内容更长, 用默认值会觉得"滑了半天没动"。
    /// </summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0));

    public static double GetStep(DependencyObject d) => (double)d.GetValue(StepProperty);
    public static void SetStep(DependencyObject d, double v) => d.SetValue(StepProperty, v);

    /// <summary>
    /// 单页覆盖: 平滑动画时长(毫秒, ≤0 = 用全应用默认值)。
    /// 这个值直接决定"跟手程度": 它越长, 松手后内容还在自己往前飘, 连滚时更是叠在一起慢慢追。
    /// </summary>
    public static readonly DependencyProperty DurationMsProperty = DependencyProperty.RegisterAttached(
        "DurationMs", typeof(int), typeof(SmoothScroll), new PropertyMetadata(0));

    public static int GetDurationMs(DependencyObject d) => (int)d.GetValue(DurationMsProperty);
    public static void SetDurationMs(DependencyObject d, int v) => d.SetValue(DurationMsProperty, v);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || (bool)e.NewValue == false) return;
        fe.Loaded += (_, _) =>
        {
            var sv = fe as ScrollViewer ?? FindDescendantScrollViewer(fe);
            if (sv == null) return;
            sv.PreviewMouseWheel += OnPreviewWheel;
        };
    }

    private static void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.ScrollableHeight <= 0) return;
        e.Handled = true;

        // 从当前实际位置出发(上一段动画可能还在走, VerticalOffset 就是它的当前值),
        // 先清掉旧动画并同步代理基值, 再起新动画 —— 每一格都从"此刻"平滑续走。
        var step = GetStep(sv);
        if (step <= 0) step = PixelsPerNotch;
        var ms = GetDurationMs(sv);
        if (ms <= 0) ms = DefaultDurationMs;

        var notches = e.Delta / 120.0;
        var from = sv.VerticalOffset;
        var target = Math.Clamp(from - notches * step, 0, sv.ScrollableHeight);
        if (Math.Abs(target - from) < 0.5) return;

        sv.BeginAnimation(ProxyOffsetProperty, null);
        sv.SetValue(ProxyOffsetProperty, from);
        sv.BeginAnimation(ProxyOffsetProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendantScrollViewer(VisualTreeHelper.GetChild(d, i));
            if (found != null) return found;
        }
        return null;
    }
}
