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
/// 全局鼠标滚轮: 按设置的距离 + **两级低通平滑**(而不是引擎原生的逐格瞬移)。
///
/// 背景(三次迭代, 都是用户实测反馈):
///   · 2026-11-07: 原生滚轮一格瞬移 48px, 一顿一顿。第一版用"每个事件重启一段 EaseOut",
///     连滚时速度反复冲顶再刹住, 还是卡。改成"目标累加 + 单级指数跟随(Tau=0.05s)"后
///     曲线连续了, 但 60Hz 首帧只走 28%, 一格要 ~230ms 才落位, 连滚时目标越推越远 ——
///     用户报"不跟手"。
///   · 2026-11-14 上午: 为了跟手, 改成"输入事件里先兑现 50% + 短尾缓动"。跟手了,
///     但每一格都先"啪"地跳掉一半, 用户报"看起来像瞬移, 不丝滑"。
///   · 2026-11-14 下午(本版): 重写为**两级临界阻尼低通**:
///       rawTarget(滚轮累加) → smoothTarget(一级) → 实际位置(二级)。
///     阶跃响应是一条 S 曲线: **起步速度为 0**, 先柔和加速、再柔和减速,
///     没有任何一帧突然跳掉一大段; 同时每级 Tau 只有 0.02s,
///     60Hz 下首帧 ~20%、50% 在 ~33ms、95% 在 ~100ms, 不会像 0.05s 那样拖尾。
///     连滚只是把 rawTarget 推远, 两级状态连续叠加, 不重启动画、不积压队列;
///     反向时把 smoothTarget 拉回当前位置, 立即换向但仍走 S 曲线。
///   距离仍由设置页「交互 → 滚轮速度」控制(100% = 与引擎原生一格的距离相同)。
///
/// ★★ 为什么按帧驱动、而不是 DoubleAnimation:
///   动画的"每个事件重启"会顿挫(见上)。这里用 CompositionTarget.Rendering 与渲染同拍,
///   按真实 dt 积分两级低通的**精确解**(见 <see cref="OnRendering"/>), 高刷新率/掉帧都稳定。
///   这个模式与本项目中键自动滚动(见 <see cref="MiddleClickAutoscroll"/>)一致。
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
/// ★ 嵌套滚动区(2026-11-14 修): 从 e.OriginalSource 往上找**第一个能纵向滚的**
///   ScrollViewer, 而不是要求"最近的那个就是 sender"。鼠标压在一个内容不满一屏的
///   嵌套小列表(MaxHeight 的直播看板、弹层里的规则列表、播放器分P列表…)上时,
///   它自己滚不动, 事件应该继续交给外层页面 —— 旧判定会让里层和外层都不处理, 滚轮像坏了。
///
/// ★ 条目滚动的虚拟化列表(关注页左栏、会话列表等)保持引擎原生: 一格 3 条,
///   再按像素动画会一笔跳过好几张整卡。动态页**动态卡**是例外: 它显式设了
///   ScrollUnit=Pixel(见 FollowPage.xaml 的 FeedListStyle), 偏移是像素, 所以同样平滑。
///   识别逻辑见 <see cref="IsItemScrolling"/>。
///
/// ★ 为什么不用 <see cref="SmoothScroll"/>: 那个是逐处 opt-in 的旧实现(2026-11-14 起
///   播放器合集列表也改走本类, 它目前无调用), 这里要的是**全局 + 可调速**, 两者语义不同, 别合并。
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
    /// 两级低通**每一级**的时间常数(秒)。手感就由它决定:
    ///   · 越大越"飘"(更晚停住, 更接近惯性滚动);
    ///   · 越小越跟手, 但太小会重新变成"一顿一顿"。
    /// 0.02s × 2 级 ≈ 一格 120px: 首帧 ~20%(S 曲线起步, 不跳), 50% 在 ~33ms,
    /// 95% 在 ~100ms, 完全落位 ~130ms。连滚时的平均延迟 ≈ 2×Tau = 40ms,
    /// 远小于旧版单级 0.05s 的 100ms, 也不会像"先兑现 50%"那样瞬移。
    /// ★ 要整体调手感只改这一处。
    /// </summary>
    private const double SmoothTauSeconds = 0.02;

    /// <summary>单帧最多按这么长的时间推进(秒)。掉帧/断点恢复时别一次冲一大段。</summary>
    private const double MaxFrameSeconds = 0.10;

    /// <summary>
    /// 小于这个间隔的 Rendering 回调直接跳过(秒)。
    /// WPF 偶尔会在同一帧里连发两次 Rendering(第二次 dt≈0): 那一步只移动零点几像素,
    /// 既看不出来又可能因取整产生细碎抖动。跳过它, 把时间累计到下一帧一次走完。
    /// </summary>
    private const double MinFrameSeconds = 0.002;

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

    /// <summary>本段滑动的**原始目标**偏移: 连滚时在它上面累加, 松手前不丢距离。</summary>
    private static readonly DependencyProperty TargetOffsetProperty = DependencyProperty.RegisterAttached(
        "TargetOffset", typeof(double), typeof(WheelScroll), new PropertyMetadata(0.0));

    /// <summary>
    /// 一级低通的输出(平滑目标)。它先柔和地逼近 <see cref="TargetOffsetProperty"/>,
    /// 实际位置再柔和地逼近它 —— 两级串起来就是起步速度为 0 的 S 曲线。
    /// </summary>
    private static readonly DependencyProperty SmoothTargetProperty = DependencyProperty.RegisterAttached(
        "SmoothTarget", typeof(double), typeof(WheelScroll), new PropertyMetadata(0.0));

    /// <summary>
    /// 二级低通的输出 = 我们自己推动"到过"的最新偏移(不读 VerticalOffset 反推):
    /// 跟随每帧只需要它和 SmoothTarget/Target, 也方便识别"这次 ScrollChanged 是不是我们干的"。
    /// </summary>
    private static readonly DependencyProperty CurrentOffsetProperty = DependencyProperty.RegisterAttached(
        "CurrentOffset", typeof(double), typeof(WheelScroll), new PropertyMetadata(0.0));

    /// <summary>是否有一段滑动在跑(落位或被外部滚动打断时置 false)。</summary>
    private static readonly DependencyProperty AnimatingProperty = DependencyProperty.RegisterAttached(
        "Animating", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false));

    /// <summary>是否已经给这个 ScrollViewer 挂过"外部滚动即让位"的监听(只挂一次)。</summary>
    private static readonly DependencyProperty ScrollHookedProperty = DependencyProperty.RegisterAttached(
        "ScrollHooked", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false));

    /// <summary>
    /// 正在由我们自己写偏移(见 <see cref="PushOffset"/>)。
    /// 个别布局时序下 ScrollChanged 可能在调用栈内同步发出来, 那时 CurrentOffset 已经写成新值、
    /// VerticalOffset 还可能读到旧值 —— 外部让位逻辑会把它误判成"用户拖了滚动条"并掐掉刚起的动画。
    /// 写入期间置位, 直接忽略即可。
    /// </summary>
    private static readonly DependencyProperty PushingProperty = DependencyProperty.RegisterAttached(
        "Pushing", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false));

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

        // ★ 2026-11-14: 从鼠标真正所在的位置往上找**第一个能纵向滚的** ScrollViewer。
        //   旧写法要求"最近的那个 ScrollViewer == sender": 鼠标压在一个内容不满一屏的
        //   嵌套小列表上时, 里层因为 ScrollableHeight<=0 直接 return, 外层又因为最近祖先
        //   不是自己被拒绝 —— 滚轮整个失效。现在跳过滚不动的里层, 与原生冒泡一致。
        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(
            e.OriginalSource as DependencyObject, static v => v.ScrollableHeight > 0);
        if (sv == null) return;

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
        var smooth = animating ? GetSmoothTarget(sv) : current;
        var target = animating ? GetTargetOffset(sv) : current;

        // 反向滚动: 把平滑目标也拉回当前位置, 立即换向 —— 但位置仍走 S 曲线, 不瞬移。
        if (animating && (target - current) * delta < 0)
        {
            target = current;
            smooth = current;
        }

        target = Math.Clamp(target + delta, 0, sv.ScrollableHeight);

        // 已经到头(或幅度小到看不清): 不动, 也不吃掉事件 —— 交给引擎, 边界行为与原生一致。
        if (Math.Abs(target - current) < 0.5) return;

        // ★ 这里**不**直接写偏移: 任何"输入事件里先跳一段"的做法都会被看成瞬移。
        //   只更新目标, 让渲染帧里的两级低通从当前位置柔和地加速过去。
        SetSmoothTarget(sv, smooth);
        SetCurrentOffset(sv, current);
        SetTargetOffset(sv, target);
        SetAnimating(sv, true);

        if (!Active.Contains(sv)) Active.Add(sv);
        EnsureRendering();

        e.Handled = true;
    }

    /// <summary>
    /// 把偏移写到 ScrollViewer 上, 同时更新"我们自己推动到哪儿"的记账。
    /// ★ 写入期间 <see cref="PushingProperty"/> 置位, 让 <see cref="OnScrollChanged"/>
    ///   忽略调用栈内同步冒出来的 ScrollChanged(见该属性的说明)。
    /// </summary>
    private static void PushOffset(ScrollViewer sv, double offset)
    {
        SetCurrentOffset(sv, offset);
        SetPushing(sv, true);
        try { sv.ScrollToVerticalOffset(offset); }
        finally { SetPushing(sv, false); }
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
    /// 每渲染帧积分一次**两级临界阻尼低通**的精确解。
    ///
    /// 系统: d(smooth)/dt = (target - smooth)/Tau,  d(current)/dt = (smooth - current)/Tau。
    /// 令 u = dt/Tau, a = e^(-u), e1 = target - smooth, e2 = smooth - current, 则:
    ///   smooth' = target - e1·a
    ///   current' = smooth' - (e2 + e1·u)·a
    /// 这是常数目标下 dt 区间内的解析解(不是"每帧乘一个系数"的离散近似),
    /// 所以 60Hz / 144Hz / 掉帧时的轨迹一致, 且起步速度为 0、永不越过目标。
    /// </summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        if (dt <= 0) return;
        if (dt < MinFrameSeconds) return;   // 同一帧里的重复回调: 不更新 _lastTick, 让时间累计到下一帧
        _lastTick = now;
        if (dt > MaxFrameSeconds) dt = MaxFrameSeconds;   // 首帧/断点恢复等异常间隔, 别拿来一次冲一大段

        var u = dt / SmoothTauSeconds;
        var a = Math.Exp(-u);

        // 快照: 下面的 ScrollToVerticalOffset 会同步触发 ScrollChanged,
        // 那里可能在"外部滚动让位"里把元素从 Active 移除 —— 不能边遍历边改。
        foreach (var sv in Active.ToArray())
        {
            if (!GetAnimating(sv)) { Active.Remove(sv); continue; }

            var current = GetCurrentOffset(sv);
            var smooth = GetSmoothTarget(sv);
            var target = GetTargetOffset(sv);

            var e1 = target - smooth;
            var e2 = smooth - current;
            var nextSmooth = target - e1 * a;
            var nextCurrent = nextSmooth - (e2 + e1 * u) * a;

            // 数值兜底: 解析解本不该越过目标, 真越过了就钉在目标上。
            if ((target - nextCurrent) * (target - current) < 0) nextCurrent = target;

            if (Math.Abs(target - nextCurrent) <= SnapEpsilon)
            {
                SetSmoothTarget(sv, target);
                PushOffset(sv, target);
                SetAnimating(sv, false);
                Active.Remove(sv);
                continue;
            }

            SetSmoothTarget(sv, nextSmooth);
            PushOffset(sv, nextCurrent);
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
        if (!GetAnimating(sv) || GetPushing(sv)) return;

        var actual = sv.VerticalOffset;
        var current = GetCurrentOffset(sv);
        var smooth = GetSmoothTarget(sv);
        var target = GetTargetOffset(sv);

        // ★ 拖滚动条: 用户直接控制滑块, 无论如何立刻让位(包括同向拖动)。
        //   必须放在"自己推的"容差判断之前: 用户可能正好拖回上一帧的偏移值。
        if (Mouse.LeftButton == MouseButtonState.Pressed &&
            (Mouse.Captured is Thumb || Mouse.Captured is ScrollBar))
        {
            CancelFollow(sv, actual);
            return;
        }

        // 我们自己推动的那一帧: 偏移就是我们刚写进去的值, 不算外部滚动。
        // 容差留 1.5px: 高 DPI / 布局取整可能让读回值与写入值差零点几像素,
        // 卡在 0.5 上会把"自己推的"误判成"外部滚动", 动画刚起就被自己掐掉(症状是更卡)。
        if (Math.Abs(actual - current) < 1.5) return;

        var pending = target - current;

        // ★ 延迟落位: ScrollToVerticalOffset 偶尔要等下一次布局才反映到 VerticalOffset,
        //   这时读回值会落在"当前 → 目标"之间。它是我们自己推的进度, 不是外部滚动 ——
        //   旧逻辑只要差 >1.5px 就停掉动画, 快速滚动时会被自己的事件反复掐停(不跟手的来源)。
        if (pending != 0 && (actual - current) * pending > 0)
        {
            if ((actual - target) * pending >= 0)
            {
                // 已经到目标(或被布局推过了): 就地收尾, 别再反向拉回来。
                CancelFollow(sv, actual);
            }
            else
            {
                // 还在追的路上: 认这一帧进度, 继续。smooth 必须保持在 actual 与 target 之间。
                SetCurrentOffset(sv, actual);
                if ((smooth - actual) * pending < 0) SetSmoothTarget(sv, actual);
                else if ((smooth - target) * pending > 0) SetSmoothTarget(sv, target);
            }
            return;
        }

        // ★ 布局变化(内容长高/变矮、卡片墙补锚点)把偏移推走了: 三级状态一起平移,
        //   保留还没走完的轮距。直接取消的话, 自动加载/锚点补偿会让滚动半路"定住"。
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            var shift = actual - current;
            var max = Math.Max(0, sv.ScrollableHeight);
            SetCurrentOffset(sv, actual);
            SetSmoothTarget(sv, Math.Clamp(smooth + shift, 0, max));
            SetTargetOffset(sv, Math.Clamp(target + shift, 0, max));
            return;
        }

        // 真正的外部滚动(键盘 PageDown / 触摸 / 程序性跳转): 让它赢, 别跟用户抢。
        CancelFollow(sv, actual);
    }

    /// <summary>停掉这个滚动区的跟随, 并把三级记账都对齐到实际偏移。</summary>
    private static void CancelFollow(ScrollViewer sv, double actual)
    {
        SetAnimating(sv, false);
        Active.Remove(sv);
        SetCurrentOffset(sv, actual);
        SetSmoothTarget(sv, actual);
        SetTargetOffset(sv, actual);
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

    private static double GetSmoothTarget(DependencyObject d) => (double)d.GetValue(SmoothTargetProperty);
    private static void SetSmoothTarget(DependencyObject d, double v) => d.SetValue(SmoothTargetProperty, v);

    private static double GetCurrentOffset(DependencyObject d) => (double)d.GetValue(CurrentOffsetProperty);
    private static void SetCurrentOffset(DependencyObject d, double v) => d.SetValue(CurrentOffsetProperty, v);

    private static bool GetAnimating(DependencyObject d) => (bool)d.GetValue(AnimatingProperty);
    private static void SetAnimating(DependencyObject d, bool v) => d.SetValue(AnimatingProperty, v);

    private static bool GetPushing(DependencyObject d) => (bool)d.GetValue(PushingProperty);
    private static void SetPushing(DependencyObject d, bool v) => d.SetValue(PushingProperty, v);
}
