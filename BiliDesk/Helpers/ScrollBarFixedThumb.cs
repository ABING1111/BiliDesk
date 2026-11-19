using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BiliDesk.Helpers;

/// <summary>
/// 固定滚动条滑块长度(2026-11-20, 用户要求"不要随内容多少变长变短, 统一长度")。
/// 由 Themes/Styles.xaml 的全局隐式 ScrollBar 样式统一打开, 页面不用写。
///
/// ## 默认行为为什么是"比例长度"
/// WPF 的 <see cref="Track"/> 在 ScrollBar 模式下按
/// <c>thumb = track * ViewportSize / (Maximum - Minimum + ViewportSize)</c>
/// 算滑块长度 —— 内容越多滑块越短。这是"能看出还剩多少内容"的设计, 但用户要的是
/// 视觉统一, 所以这里把 ViewportSize 改写成"能让滑块恰好等于目标长度"的值。
///
/// ## 做法: 伪造 ViewportSize, 而不是自己画滑块
/// 反解上式得到需要的 ViewportSize:
/// <code>
///   thumb = track * V / (range + V)   ⇒   V = thumb * range / (track - thumb)
/// </code>
/// 只要把它写回 <see cref="ScrollBar.ViewportSize"/>, Track 自己就会把滑块长度算成
/// <c>thumb</c>, 一切都是原生行为:
///   · 拖动滑块的位置映射用的是 <c>(track - thumbLength)</c> 而不是 ViewportSize,
///     所以拖到两头仍然精确对应 Minimum / Maximum, 不会拖不到底;
///   · 点轨道翻页走 RepeatButton 的 PageUp/PageDown 命令, 与 ViewportSize 无关;
///   · 滑块的最小长度(系统参数键, 见 Styles.xaml)此时已经不起作用, 留着只是兜底。
///
/// ## 谁会改回 ViewportSize
/// <see cref="ScrollViewer"/> 在内容/视口变化时会把 ViewportSize 重新写成真实视口高度,
/// 所以必须监听它的变化, 并在它之后把伪造值再写回去(见 <see cref="Update"/>)。
/// 这条来回是同步完成的(同一个布局轮次内), 渲染前就已收敛, 不会闪烁;
/// 而且 ViewportSize 只影响 Track 内部的滑块尺寸, 不影响 ScrollBar 自身的测量,
/// 因此也不存在"改了尺寸又反过来触发布局"的循环。
///
/// ## 边界
///   · 内容不满一屏(range ≤ 0): 让滑块铺满轨道, 与原生一致;
///   · 轨道太短(小列表/折叠中): 目标长度按轨道的 70% 封顶, 保证仍有拖动行程;
///   · 只改滑块"长度"这一个观感, 位置/拖动/翻页/键盘/滚轮全部保持原生。
/// </summary>
public static class ScrollBarFixedThumb
{
    /// <summary>
    /// 目标滑块长度(DIP, 纵向是高度、横向是宽度)。≤ 0 = 关闭本行为。
    /// 样式里给 88。
    /// </summary>
    public static readonly DependencyProperty LengthProperty = DependencyProperty.RegisterAttached(
        "Length", typeof(double), typeof(ScrollBarFixedThumb),
        new PropertyMetadata(0.0, OnLengthChanged));

    public static double GetLength(DependencyObject d) => (double)d.GetValue(LengthProperty);
    public static void SetLength(DependencyObject d, double value) => d.SetValue(LengthProperty, value);

    /// <summary>每根滚动条的状态(弱表: 模板重建/页面销毁后自动回收)。</summary>
    private static readonly ConditionalWeakTable<ScrollBar, State> States = new();

    private sealed class State
    {
        public SizeChangedEventHandler? OnSizeChanged;
        public EventHandler? OnMaximumChanged;
        public EventHandler? OnViewportChanged;

        /// <summary>自己写 ViewportSize 时置位, 避免"写入 → 回调 → 再写入"的死循环。</summary>
        public bool Updating;
    }

    private static void OnLengthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollBar bar) return;

        if (e.NewValue is double len && len > 0)
        {
            bar.Loaded += OnBarLoaded;
            if (bar.IsLoaded) Attach(bar);
        }
        else
        {
            bar.Loaded -= OnBarLoaded;
            Detach(bar);
        }
    }

    private static void OnBarLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollBar bar) Attach(bar);
    }

    private static void Attach(ScrollBar bar)
    {
        if (States.TryGetValue(bar, out _)) return;

        var state = new State();

        SizeChangedEventHandler sizeChanged = (_, _) => Update(bar, state);
        bar.SizeChanged += sizeChanged;
        state.OnSizeChanged = sizeChanged;

        // 内容长度变了(Maximum 变) → 目标长度没变, 但 Track 的公式要重算
        EventHandler maximumChanged = (_, _) => Update(bar, state);
        DependencyPropertyDescriptor
            .FromProperty(RangeBase.MaximumProperty, typeof(ScrollBar))
            .AddValueChanged(bar, maximumChanged);
        state.OnMaximumChanged = maximumChanged;

        // ScrollViewer 会把 ViewportSize 改回真实值: 跟着再改回去
        EventHandler viewportChanged = (_, _) => Update(bar, state);
        DependencyPropertyDescriptor
            .FromProperty(ScrollBar.ViewportSizeProperty, typeof(ScrollBar))
            .AddValueChanged(bar, viewportChanged);
        state.OnViewportChanged = viewportChanged;

        States.Add(bar, state);
        bar.Unloaded += OnBarUnloaded;

        Update(bar, state);
    }

    private static void OnBarUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollBar bar) Detach(bar);
    }

    private static void Detach(ScrollBar bar)
    {
        bar.Unloaded -= OnBarUnloaded;

        if (!States.TryGetValue(bar, out var state)) return;
        States.Remove(bar);

        if (state.OnSizeChanged != null) bar.SizeChanged -= state.OnSizeChanged;
        if (state.OnMaximumChanged != null)
            DependencyPropertyDescriptor
                .FromProperty(RangeBase.MaximumProperty, typeof(ScrollBar))
                .RemoveValueChanged(bar, state.OnMaximumChanged);
        if (state.OnViewportChanged != null)
            DependencyPropertyDescriptor
                .FromProperty(ScrollBar.ViewportSizeProperty, typeof(ScrollBar))
                .RemoveValueChanged(bar, state.OnViewportChanged);

        state.OnSizeChanged = null;
        state.OnMaximumChanged = null;
        state.OnViewportChanged = null;
    }

    private static void Update(ScrollBar bar, State state)
    {
        if (state.Updating) return;

        var length = GetLength(bar);
        if (length <= 0) return;

        // 轨道长度: 优先量 PART_Track 自己的实际尺寸 —— 那才是 Track 公式里用的 trackLength。
        // 直接在 ScrollBar 上量只在"模板没有额外内边距/按钮"时才成立(我们的模板满足,
        // 但别把结论建立在模板细节上)。≤1 说明还没布局或正被折叠, 等 SizeChanged 再算。
        var trackHost = bar.Template?.FindName("PART_Track", bar) as Track;
        double track = 0;
        if (trackHost != null)
            track = bar.Orientation == Orientation.Vertical ? trackHost.ActualHeight : trackHost.ActualWidth;
        if (track <= 1)   // 模板还没应用 / Track 还没测量: 退回 ScrollBar 自己的尺寸
            track = bar.Orientation == Orientation.Vertical ? bar.ActualHeight : bar.ActualWidth;
        if (track <= 1) return;

        var range = bar.Maximum - bar.Minimum;

        // 目标长度: 固定值, 但不超过轨道的 70%(小列表里否则会顶满、没有拖动行程)
        var thumb = Math.Min(length, Math.Max(8, track * 0.7));

        double viewport;
        if (range <= 0)
        {
            // 内容不满一屏: 滑块铺满轨道(Track 的公式在 range=0 时恒等于 track)。
            viewport = track;
        }
        else
        {
            if (track - thumb <= 0.5) return;
            // 反解: Track 的滑块长度 = track * V / (range + V)
            viewport = thumb * range / (track - thumb);
        }

        if (!(viewport > 0) || double.IsInfinity(viewport) || double.IsNaN(viewport)) return;
        if (Math.Abs(bar.ViewportSize - viewport) < 0.5) return;

        state.Updating = true;
        try
        {
            bar.ViewportSize = viewport;
        }
        finally
        {
            state.Updating = false;
        }
    }
}
