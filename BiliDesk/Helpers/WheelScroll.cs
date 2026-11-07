using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BiliDesk.Helpers;

/// <summary>
/// 全局鼠标滚轮: 按设置的距离 + 平滑跟随(而不是引擎原生的逐格瞬移)。
///
/// 背景(2026-11-07 用户要求): 原生滚轮一格瞬移 48px, 观感是一顿一顿、没有滑动的曲线。
/// 这里把每一格滚轮累加成一个"目标偏移", 内容再按帧以**指数跟随**逼近它
/// (速度与剩余距离成正比, 越接近越慢 = 天然的 ease-out)。距离由设置页
/// 「交互 → 滚轮速度」控制(100% = 与引擎原生一格的距离相同)。
///
/// ★★ 为什么用 CompositionTarget.Rendering 按帧跟随、而不是 DoubleAnimation:
///   第一版是"每个滚轮事件重启一段 240ms 的 EaseOut", 用户实测"不瞬移了但是卡卡的" ——
///   因为连滚/连触控板时每来一个事件速度就重新冲到最大再刹住, 一顿一顿。
///   指数跟随没有"重启"这回事: 新的一格只是把目标推远, 当前速度连续叠加,
///   所以是一只连续加速/减速的曲线。这个模式与本项目中键自动滚动
///   (见 <see cref="MiddleClickAutoscroll"/>)一致, 是这套 UI 上验证过的手感。
///
/// 挂载: App 启动时调一次 <see cref="Install"/>。注册的是 ScrollContentPresenter /
/// ScrollViewer 的**类处理器**, 全应用(含动态创建的滚动区)自动生效。
///
/// ★ 为什么挂在**冒泡**的 MouseWheelEvent, 而不是 Preview:
///   滚轮是一对事件(Preview 隧道 + MouseWheel 冒泡)。在 Preview 里抢先把事件吃掉,
///   会把"本该由控件自己处理的滚轮"一起拦下 —— 典型是 ComboBox: 它注册的类处理器
///   带 handledEventsToo=true, 我们就算置了 Handled 它照跑, 结果是页面和下拉一起动。
///   冒泡到 ScrollContentPresenter 时, 内容里的控件(ComboBox/TextBox…)已经先挑过了,
///   被置 Handled 的事件我们根本收不到(注册时 handledEventsToo=false) —— 判定点与引擎一致。
///   ScrollViewer 上的那一份只补"滚轮压在滚动条/模板空隙上"的情况: 那里不在内容宿主的
///   子树里。两条路径不会重复 —— 内容宿主先处理的话, ScrollViewer 那份收到的已是 Handled。
///
/// ★ 条目滚动的虚拟化列表(关注页左栏、会话列表等)保持引擎原生: 一格 3 条,
///   再按像素动画会一笔跳过好几张整卡。动态页**动态卡**是例外: 它显式设了
///   ScrollUnit=Pixel(见 FollowPage.xaml 的 FeedListStyle), 偏移是像素, 所以同样平滑。
///   识别逻辑见 <see cref="IsItemScrolling"/>。
///
/// ★ 为什么不用 <see cref="SmoothScroll"/>: 那个是逐处 opt-in 的旧实现(现在只剩播放器
///   合集列表在用), 这里要的是**全局 + 可调速**, 两者语义不同, 别合并。
/// </summary>
public static class WheelScroll
{
    /// <summary>滑杆下限: 100 = 每格距离与引擎原生一致(只是变成平滑滑过去, 不允许更慢)。</summary>
    public const int MinPercent = 100;

    /// <summary>滑杆上限(500% ≈ 每格 240px, 差不多一整行卡片)。</summary>
    public const int MaxPercent = 500;

    /// <summary>
    /// 默认 250%: 用户 2026-11-07 反馈原生 48px/格偏慢, 定在约 120px/格。
    /// ★ 只用在本类与设置项默认值上, 设置页滑杆的上下限也引用这里的常量。
    /// </summary>
    public const int DefaultPercent = 250;

    /// <summary>
    /// 引擎里"一行"的物理步长(WPF ScrollViewer 的 _scrollLineDelta = 16 DIP)。
    /// 只用来算 100% 的基准距离, 不直接调用引擎的行滚动。
    /// </summary>
    private const double NativeLineDelta = 16;

    /// <summary>
    /// 跟随的时间常数(秒)。手感就由它决定:
    ///   · 越大越"飘"(更晚停住, 更接近惯性滚动);
    ///   · 越小越跟手, 但太小会重新变成"一顿一顿"。
    /// 0.05s ≈ 一格 120px 在 ~230ms 内滑完, 前 1/4 的位移在头两帧就完成 —— 跟手且有曲线感。
    /// ★ 要整体调手感只改这一处。
    /// </summary>
    private const double FollowTauSeconds = 0.05;

    /// <summary>剩余距离小于这个值就直接落位, 免得永远差零点几像素、白白多跑几十帧。</summary>
    private const double SnapEpsilon = 0.8;

    private static int _percent = DefaultPercent;

    /// <summary>当前生效的每格距离百分比(静态: 滚轮事件里直接读, 不必每帧查设置对象)。</summary>
    public static int Percent => _percent;

    /// <summary>设置百分比并夹取(设置页拖滑杆 / Load 启动同步时调用)。</summary>
    public static void SetPercent(int percent) => _percent = ClampPercent(percent);

    /// <summary>规范化: ≤0(老配置缺字段反序列化成 0)回落默认, 其余夹进范围。</summary>
    public static int ClampPercent(int percent) =>
        percent <= 0 ? DefaultPercent : Math.Clamp(percent, MinPercent, MaxPercent);

    // ------------------------------------------------------------ 每格距离

    /// <summary>
    /// 一格滚轮该滑多少 DIP: 系统行数 × 16 × 用户倍数。
    /// 系统把 WheelScrollLines 设成 0(禁用滚轮)时返回 0, 调用方据此放行、什么都不做。
    /// </summary>
    private static double PerNotchDistance()
    {
        var lines = SystemParameters.WheelScrollLines;
        if (lines <= 0) return 0;
        return lines * NativeLineDelta * _percent / 100.0;
    }

    /// <summary>
    /// "一次滚一页"(WheelScrollLines = -1)时的一格距离 = 一个视口高度,
    /// 与引擎的 PageDown 同量级。页已经很大, 不再乘用户倍数。
    /// </summary>
    private static double PageDistance(ScrollViewer sv) => Math.Max(sv.ViewportHeight, NativeLineDelta);

    // ------------------------------------------------------------ 附加状态

    /// <summary>本段滑动的**目标**偏移: 连滚时在它上面累加, 松手前不丢距离。</summary>
    private static readonly DependencyProperty TargetOffsetProperty = DependencyProperty.RegisterAttached(
        "TargetOffset", typeof(double), typeof(WheelScroll), new PropertyMetadata(0.0));

    /// <summary>
    /// 我们自己推动"到过"的最新偏移(不读 VerticalOffset 反推):
    /// 跟随每帧只需要它和 Target, 也方便识别"这次 ScrollChanged 是不是我们干的"。
    /// </summary>
    private static readonly DependencyProperty CurrentOffsetProperty = DependencyProperty.RegisterAttached(
        "CurrentOffset", typeof(double), typeof(WheelScroll), new PropertyMetadata(0.0));

    /// <summary>是否有一段滑动在跑(落位或被外部滚动打断时置 false)。</summary>
    private static readonly DependencyProperty AnimatingProperty = DependencyProperty.RegisterAttached(
        "Animating", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false));

    /// <summary>是否已经给这个 ScrollViewer 挂过"外部滚动即让位"的监听(只挂一次)。</summary>
    private static readonly DependencyProperty ScrollHookedProperty = DependencyProperty.RegisterAttached(
        "ScrollHooked", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false));

    /// <summary>正在跟随的滚动区。每帧只遍历这一小撮, 空闲时连渲染回调都摘掉。</summary>
    private static readonly List<ScrollViewer> Active = new();

    private static bool _installed;
    private static bool _renderingHooked;
    private static long _lastTick;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        // 内容区(绝大多数滚轮都落在这里)。
        EventManager.RegisterClassHandler(typeof(ScrollContentPresenter), Mouse.MouseWheelEvent,
            new MouseWheelEventHandler(OnWheel));

        // 补"滚轮压在滚动条/模板空隙上": 那里不在内容宿主的子树里。
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.MouseWheelEvent,
            new MouseWheelEventHandler(OnWheel));
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        var sv = sender as ScrollViewer ?? VisualTreeUtil.FindAncestor<ScrollViewer>(sender as DependencyObject);
        if (sv == null) return;

        // 只认最内层: 嵌套滚动区里鼠标真正所在的那一个, 外层不替内层滚。
        if (!ReferenceEquals(VisualTreeUtil.FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject), sv))
            return;

        // 没有纵向可滚空间(纯横向滚动条/内容不满)交给引擎, 行为与原生一致。
        if (sv.ScrollableHeight <= 0) return;

        // 条目滚动的虚拟化列表保持原生 —— 见类头说明。
        if (IsItemScrolling(sv)) return;

        var lines = SystemParameters.WheelScrollLines;
        var perNotch = lines < 0 ? PageDistance(sv) : PerNotchDistance();
        if (perNotch <= 0) return;   // 系统把滚轮关掉了(WheelScrollLines = 0)

        HookExternalScroll(sv);

        // 触控板/精密滚轮会给 ±1~±40 的小 delta: 按 notch 折算(120 = 一格),
        // 距离与手感都和真实滚动量成比例; 标准鼠标一格恰好 1.0。
        var delta = -(e.Delta / 120.0) * perNotch;

        var animating = GetAnimating(sv);
        var current = animating ? GetCurrentOffset(sv) : sv.VerticalOffset;
        var target = animating ? GetTargetOffset(sv) : current;

        // 反向滚动: 不要先"抵消"在途的目标(否则往反方向滚要等它追回来才动)。
        if (animating && (target - current) * delta < 0) target = current;

        target = Math.Clamp(target + delta, 0, sv.ScrollableHeight);

        // 已经到头(或幅度小到看不清): 不动, 也不吃掉事件 —— 交给引擎, 边界行为与原生一致。
        if (Math.Abs(target - current) < 0.5) return;

        SetCurrentOffset(sv, current);
        SetTargetOffset(sv, target);
        SetAnimating(sv, true);

        if (!Active.Contains(sv)) Active.Add(sv);
        EnsureRendering();

        e.Handled = true;
    }

    // ------------------------------------------------------------ 按帧跟随

    private static void EnsureRendering()
    {
        if (_renderingHooked) return;
        _renderingHooked = true;
        _lastTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>
    /// 每渲染帧逼近一次目标: 位移 = 剩余距离 × (1 - e^(-dt/Tau))。
    /// 速度只由"此刻还差多少"决定, 所以连滚时新的一格只是把目标推远、速度自然叠加,
    /// 没有 DoubleAnimation 那种"每个事件重启一段缓动"的顿挫。
    /// </summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        if (dt <= 0) return;
        if (dt > 0.05) dt = 0.05;   // 首帧/断点恢复等异常间隔, 别拿来一次冲一大段

        var k = 1 - Math.Exp(-dt / FollowTauSeconds);

        // 快照: 下面的 ScrollToVerticalOffset 会同步触发 ScrollChanged,
        // 那里可能在"外部滚动让位"里把元素从 Active 移除 —— 不能边遍历边改。
        foreach (var sv in Active.ToArray())
        {
            if (!GetAnimating(sv)) { Active.Remove(sv); continue; }

            var current = GetCurrentOffset(sv);
            var target = GetTargetOffset(sv);
            var diff = target - current;

            if (Math.Abs(diff) <= SnapEpsilon)
            {
                SetCurrentOffset(sv, target);
                sv.ScrollToVerticalOffset(target);
                SetAnimating(sv, false);
                Active.Remove(sv);
                continue;
            }

            var next = current + diff * k;
            if ((target - next) * diff < 0) next = target;   // 别越过目标
            SetCurrentOffset(sv, next);
            sv.ScrollToVerticalOffset(next);
        }

        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    // ------------------------------------------------------------ 外部滚动让位

    /// <summary>
    /// 拖动滚动条/键盘/触摸/程序性跳动时, 立刻停掉在途的跟随 ——
    /// 否则最多几百毫秒内它还在往回拉, 表现是"滚动条拖不动"。
    /// 判据: 偏移与我们推动到的值对不上 ⇒ 这次变化不是我们干的。
    /// </summary>
    private static void HookExternalScroll(ScrollViewer sv)
    {
        if ((bool)sv.GetValue(ScrollHookedProperty)) return;
        sv.SetValue(ScrollHookedProperty, true);
        sv.ScrollChanged += OnScrollChanged;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (!GetAnimating(sv)) return;

        // 我们自己推动的那一帧: 偏移就是我们刚写进去的值, 不算外部滚动。
        // 容差留 1.5px: 高 DPI / 布局取整可能让读回值与写入值差零点几像素,
        // 卡在 0.5 上会把"自己推的"误判成"外部滚动", 动画刚起就被自己掐掉(症状是更卡)。
        if (Math.Abs(sv.VerticalOffset - GetCurrentOffset(sv)) < 1.5) return;

        SetAnimating(sv, false);
        Active.Remove(sv);
        SetCurrentOffset(sv, sv.VerticalOffset);
        SetTargetOffset(sv, sv.VerticalOffset);
    }

    // ------------------------------------------------------------ 条目滚动识别

    /// <summary>
    /// 这个 ScrollViewer 是不是**条目滚动**(滚动单位 = 条, 不是像素)。
    ///
    /// CanContentScroll=false ⇒ 一定是像素滚;
    /// =true 时看内容主轴上的第一个面板: 它挂 IScrollInfo(虚拟化列表的
    /// VirtualizingStackPanel 就是)才是条目滚动 —— 普通 StackPanel 仍然走
    /// ScrollContentPresenter 的像素滚动。
    /// (ScrollViewer.ScrollInfo 是 internal, 只能这样从内容反推。)
    /// ★ 例外: 虚拟化列表明示了 ScrollUnit=Pixel(动态页动态卡就是) —— 那种偏移
    ///   仍是像素, 可以动画。这个附加属性挂在 **ItemsControl** 上、由面板去读,
    ///   所以必须按面板的 ItemsOwner 读, 读面板自身永远是默认的 Item。
    /// </summary>
    private static bool IsItemScrolling(ScrollViewer sv)
    {
        if (!ScrollViewer.GetCanContentScroll(sv)) return false;

        var panel = FindContentPanel(sv.Content as DependencyObject);
        if (panel is not IScrollInfo) return false;

        if (panel is VirtualizingPanel)
        {
            DependencyObject owner = ItemsControl.GetItemsOwner(panel) as DependencyObject ?? panel;
            if (VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Pixel) return false;
        }
        return true;
    }

    /// <summary>
    /// 从内容往下沿"内容主轴"找第一个面板。只穿透装饰性容器(ItemsPresenter / Border /
    /// ContentPresenter…), **不进面板内部** —— 承担滚动的面板只会是主轴上的第一个,
    /// 再往里的面板属于卡片/列表项的排版, 与 ScrollInfo 无关。
    /// 深度封顶 8: 正常主轴 ≤4 层, 封顶是防止异常模板把整面卡片墙扫一遍。
    /// </summary>
    private static Panel? FindContentPanel(DependencyObject? d, int depth = 0)
    {
        if (d == null || depth > 8) return null;
        if (d is Panel panel) return panel;
        if (d is not Visual) return null;   // Run/字符串等内容元素没有可视子树
        var count = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < count; i++)
        {
            var found = FindContentPanel(VisualTreeHelper.GetChild(d, i), depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    // ------------------------------------------------------------ 附加属性访问器

    private static double GetTargetOffset(DependencyObject d) => (double)d.GetValue(TargetOffsetProperty);
    private static void SetTargetOffset(DependencyObject d, double v) => d.SetValue(TargetOffsetProperty, v);

    private static double GetCurrentOffset(DependencyObject d) => (double)d.GetValue(CurrentOffsetProperty);
    private static void SetCurrentOffset(DependencyObject d, double v) => d.SetValue(CurrentOffsetProperty, v);

    private static bool GetAnimating(DependencyObject d) => (bool)d.GetValue(AnimatingProperty);
    private static void SetAnimating(DependencyObject d, bool v) => d.SetValue(AnimatingProperty, v);
}
