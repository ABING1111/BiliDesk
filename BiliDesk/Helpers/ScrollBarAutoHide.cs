using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 滚动条自动隐藏(2026-11-19 新增): 默认整个滚动条透明, 鼠标进入它所属的滚动区才淡入,
/// 离开 1 秒后淡出。挂载方式见 <see cref="EnabledProperty"/> —— 由 Themes/Styles.xaml 的
/// 全局隐式 ScrollBar 样式统一打开, 不需要任何页面单独写。
///
/// ## 为什么用"页面级悬停"而不是"滚动条自己悬停"
/// 滚动条只有 12px 宽、又贴在窗口右缘, 让用户先去"精确指到"它才显示等于没显示。
/// 这里监听的是**最近的那个 ScrollViewer** 的 MouseEnter/MouseLeave —— 鼠标在页面任意位置
/// 都算"在页面里", 滚动条随页面一起亮起来, 与 Windows 11 / Edge 的悬浮滚动条一致。
///
/// ## 为什么用 Opacity 而不是 Visibility / Width
/// ★★★ 滚动条在 ScrollViewer 的模板里**占布局宽度**(12px + 6px 右边距)。把它 Collapsed 或
///   把宽度改成 0 会让内容区宽度跳变 —— 卡片墙(CardWall/CardWallPanel)会因此重排列数,
///   正好落进项目里 2026-10-xx 那批"滚动条出现/消失导致整墙抖动"的坑。
///   淡出只改渲染层, 布局一动不动, 出现/隐藏全程零重排。
///
/// ## 为什么**不**在隐藏时关掉 IsHitTestVisible
/// 关掉之后, 鼠标移到那条 12px 的空档上就不再"落在 ScrollViewer 里" ——
/// ScrollViewer.IsMouseOver 变 false, 触发 MouseLeave, 反而把刚想抓的滚动条淡出掉。
/// 保持命中测试一直开着(它本来就是透明轨道), 鼠标压上去立刻淡入, 想点也来得及看见;
/// 唯一的代价是"看不见却可点"的窗口只剩指针移过去的那 160ms, 实际不可能误触。
///
/// ## 其它取舍
///   · 拖动保护: 松手前鼠标移出窗口(Thumb 持有捕获)时不隐藏, 否则滚动条会在拖动中途消失。
///   · 嵌套滚动区(直播看板、表情面板、TextBox 内部、播放器信息栏)各自独立 ——
///     每个 ScrollBar 找的是**离自己最近**的 ScrollViewer, 互不牵连。
///   · 没有 ScrollViewer 祖先的 ScrollBar(理论上不存在)保持常显, 不参与本行为。
/// </summary>
public static class ScrollBarAutoHide
{
    /// <summary>淡入时长(毫秒)。要短: 鼠标刚进页面时滚动条就该"已经在那儿了"。</summary>
    private const int FadeInMs = 160;

    /// <summary>淡出时长(毫秒)。略长一点, 收得柔和, 不抢注意力。</summary>
    private const int FadeOutMs = 220;

    /// <summary>鼠标离开滚动区后多久开始淡出(毫秒)。1s: 扫视/来回移动时不会闪。</summary>
    private const int HideDelayMs = 1000;

    /// <summary>
    /// 打开自动隐藏。由全局隐式 ScrollBar 样式设置, 不用手工挂。
    /// </summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollBarAutoHide),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    /// <summary>每个 ScrollBar 的运行态(弱表: 滚动条被模板重建/页面销毁后自然回收)。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollBar, State> States = new();

    private sealed class State
    {
        public ScrollViewer? Viewer;

        /// <summary>延迟隐藏用的计时器(每根滚动条一个, 数量是个位数)。</summary>
        public DispatcherTimer? Timer;

        public MouseEventHandler? OnEnter;
        public MouseEventHandler? OnLeave;
        public DependencyPropertyChangedEventHandler? OnViewerVisibleChanged;
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollBar bar) return;

        if (e.NewValue is true)
        {
            bar.Loaded += OnBarLoaded;
            // 样式可能在已经 Loaded 之后才应用(罕见的运行时换样式): 补一次
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

        // 最近的 ScrollViewer: 嵌套滚动区各管各的
        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(bar);
        if (sv == null) return;   // 不在滚动区里: 保持常显

        var state = new State { Viewer = sv };

        MouseEventHandler enter = (_, _) => Show(bar, state);
        MouseEventHandler leave = (_, _) => ScheduleHide(bar, state);
        sv.MouseEnter += enter;
        sv.MouseLeave += leave;
        state.OnEnter = enter;
        state.OnLeave = leave;

        // 页面被切走再切回来时鼠标可能早就在页面里(MouseEnter 不会补发):
        // 可见性一变就按当前鼠标位置校准一次。
        DependencyPropertyChangedEventHandler visibleChanged = (_, args) =>
        {
            if (args.NewValue is true && sv.IsMouseOver) Show(bar, state);
        };
        sv.IsVisibleChanged += visibleChanged;
        state.OnViewerVisibleChanged = visibleChanged;

        state.Timer = new DispatcherTimer(DispatcherPriority.Background, bar.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(HideDelayMs)
        };
        state.Timer.Tick += (_, _) =>
        {
            state.Timer!.Stop();
            HideIfIdle(bar, state);
        };

        States.Add(bar, state);

        bar.Unloaded += OnBarUnloaded;

        // 初始态不播动画: 鼠标本来就在页面里就直接显示, 否则直接隐藏(避免启动时闪一下)
        if (sv.IsMouseOver)
        {
            bar.Opacity = 1;
        }
        else
        {
            bar.Opacity = 0;
        }
    }

    private static void OnBarUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollBar bar) Detach(bar);
    }

    /// <summary>解绑全部事件并停表。滚动条被模板重建(换主题/换样式)时会走这里。</summary>
    private static void Detach(ScrollBar bar)
    {
        bar.Unloaded -= OnBarUnloaded;

        if (!States.TryGetValue(bar, out var state)) return;
        States.Remove(bar);

        state.Timer?.Stop();
        state.Timer = null;

        if (state.Viewer != null)
        {
            if (state.OnEnter != null) state.Viewer.MouseEnter -= state.OnEnter;
            if (state.OnLeave != null) state.Viewer.MouseLeave -= state.OnLeave;
            if (state.OnViewerVisibleChanged != null)
                state.Viewer.IsVisibleChanged -= state.OnViewerVisibleChanged;
        }

        state.Viewer = null;
        state.OnEnter = null;
        state.OnLeave = null;
        state.OnViewerVisibleChanged = null;
    }

    private static void Show(ScrollBar bar, State state)
    {
        state.Timer?.Stop();

        // 从当前有效值起步(不写 From): 淡出途中又把鼠标移回来时不会先跳回 1 再淡入
        bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(FadeInMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static void ScheduleHide(ScrollBar bar, State state)
    {
        state.Timer?.Stop();
        state.Timer?.Start();
    }

    /// <summary>到点了: 只有"确实已经离开"才真正淡出。</summary>
    private static void HideIfIdle(ScrollBar bar, State state)
    {
        var sv = state.Viewer;
        if (sv == null) return;

        // 页面里还有鼠标 / 鼠标压在滚动条上 / 正拖着 Thumb(哪怕拖到窗口外) → 都不隐藏
        if (sv.IsMouseOver || bar.IsMouseOver) return;
        if (Mouse.Captured is Thumb || Mouse.Captured is ScrollBar) return;

        bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeOutMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
