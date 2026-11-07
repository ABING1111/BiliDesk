using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 切页过渡: **先淡出旧页内容 → 换页 → 再淡入新页内容**(fade-through)。
///
/// ★★★ 2026-11-07 第四次重做(用户报"切页时第一张页面和第二张页面叠在一起"):
///   上一版是"旧页淡出、新页一直不透明地待在下面"的**交叉淡化** ——
///   旧页 Opacity 从 1 走到 0 的那 200ms 里, 屏幕上同一个位置**同时有新旧两页的内容**,
///   只是按比例混合。卡片墙、标题、文字叠在一起, 看起来就是"两页叠在一起"。
///   那不是竞态(连点/收尾都被 epoch 处理得很干净), 而是交叉淡化本身的观感。
///
///   这一版改成 fade-through, 任何一帧都只有**一页的内容**可见:
///     ① 旧页内容 1→0(旧页底色不动, 新页整页不可见);
///     ② 中点换页: 旧页收起、新页早已在旧页遮挡下完成布局, 此刻两页内容都是 0;
///     ③ 新页内容 0→1。
///
/// ★ 为什么只动**内容**的 Opacity, 不动页面底色:
///   页面底色(AppBackgroundBrush)是半透明的, 换页那一拍如果连底色一起淡, 露出的就是
///   窗口底色(材质模式下是壁纸, 纯色下是浅色) —— 那正是历史上反复出现的"闪白"。
///   底色全程保持不动, 换页前后像素完全一样, 中间没有任何一帧是空的。
///
/// ★ 为什么新页在淡出阶段要整页 Opacity=0(而不是只把内容设成 0):
///   两页底色是同一个半透明笔刷, 两层同时可见会叠成更深的一档(70% 上再叠 70% ≈ 91%),
///   过渡中就会看到一次"底色变深再变浅"的色阶跳变。让新页在旧页收起前完全不参与合成,
///   换页那一拍就是"一层底色换成同一个底色", 像素完全一致。
///
/// ★ 为什么新页不做缩放/位移动画: 整页 ScaleTransform 会让整棵卡片墙每帧重新栅格化,
///   实测 p95 从 ~14ms 涨到 ~28ms(离屏探针)。纯透明度变化只需要合成一层, 明显更稳。
///
/// ★ 连点导航项: Run 会作废旧回调(epoch); 调用方在开始新过渡前先 <see cref="FinishNow"/>
///   把上一段立即收尾(该补的换页也会补上), 保证任何时候最多只有一层在动、记账不会错。
/// </summary>
public sealed class PageTransition
{
    /// <summary>旧页内容淡出时长(ms)。</summary>
    public const int FadeOutMs = 100;

    /// <summary>新页内容淡入时长(ms)。过渡以这条结束。</summary>
    public const int FadeInMs = 190;

    /// <summary>
    /// 过渡中点的内容不透明度。
    ///
    /// ★★ 为什么必须是 0(而不是留一点"幽灵"):
    ///   若中点停在非零值(试过 0.18 / 0.3), 换页那一拍屏幕上会**同时**从"30% 的旧页内容"
    ///   变成"30% 的新页内容" —— 卡片边缘、大标题的轮廓会明显跳一下, 那正是"动画显示
    ///   错误"的另一种样子。停在 0 时两页内容都是"什么都没有", 换页不可见。
    ///
    /// ★ 代价: 中点会有 1 帧(实测 16ms)只看到页面底色。这是 fade-through 的数学必然
    ///   (1→0→1 必然经过 0), 也是 Win11 自己切页的做法; 而且**底色全程不变**,
    ///   所以那一帧看到的是"和刚才一样的底色", 不是闪白、不是空白窗口。
    /// </summary>
    private const double DipOpacity = 0;

    private int _epoch;
    private UIElement? _outgoing;
    private UIElement? _incoming;

    /// <summary>真正被做透明度动画的元素(通常是页面的 Content; 见 <see cref="FadeTarget"/>)。</summary>
    private UIElement? _outFade;
    private UIElement? _inFade;

    private Action? _swap;
    private Action? _done;
    private bool _swapped;
    private DispatcherTimer? _safety;

    /// <summary>是否有过渡正在跑。</summary>
    public bool InFlight { get; private set; }

    /// <summary>
    /// 播一次过渡。outgoing 必须在上层(ZIndex 更高), incoming 在下面且**已经完成布局**。
    /// swap 在旧页内容淡完之后调用(调用方在这里收起旧页 / 更新当前页记账);
    /// done 在整段过渡收尾后调用。
    /// </summary>
    public void Run(UIElement outgoing, UIElement incoming, Action swap, Action done)
    {
        StopSafety();

        var epoch = ++_epoch;
        InFlight = true;
        _swapped = false;
        _outgoing = outgoing;
        _incoming = incoming;
        _outFade = FadeTarget(outgoing);
        _inFade = FadeTarget(incoming);
        _swap = swap;
        _done = done;

        // 起点: 旧页内容不透明, 新页整页不可见(它的底色也不能参与合成 —— 见下面的说明)。
        SetOpacity(outgoing, 1);
        SetOpacity(_outFade, 1);
        SetOpacity(incoming, 0);
        SetOpacity(_inFade, DipOpacity);

        // ★ 淡出用 Linear 而不是 EaseOut: EaseOut 在尾部速度趋近 0, 会在"内容快看不见"
        //   的那几帧慢慢爬(实测空窗从 1 帧拖到 3 帧, 25~30ms), 观感是"画面卡了一下"。
        //   线性淡出速度恒定, 干净利落地穿过 0; 淡入仍用 EaseOut(快速出现后缓落, 更自然)。
        var fadeOut = new DoubleAnimation(1, DipOpacity, TimeSpan.FromMilliseconds(FadeOutMs));
        fadeOut.Completed += (_, _) =>
        {
            if (epoch != _epoch) return;   // 已被更晚的过渡取代
            Midpoint();
        };
        _outFade.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        // 兜底: 窗口最小化/渲染被挂起时 Completed 可能不来。目标页在那之前一直是
        // IsHitTestVisible=false, 卡住就等于整页点不动 —— 必须让它准时落定。
        StartSafety(FadeOutMs + FadeInMs + 600);
    }

    /// <summary>
    /// 过渡中点: 旧页内容已经透明, 此刻换页用户看不到任何跳变。
    /// 由淡出动画的 Completed 触发; 兜底定时器也会直接走到 <see cref="Complete"/> 补上这一步。
    /// </summary>
    private void Midpoint()
    {
        if (!InFlight || _swapped) return;
        _swapped = true;
        var epoch = _epoch;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 换页: 旧页收起、新页提到上面并记为当前页。两页内容此刻都是 0, 旧页整页消失后
        // 再把新页整页恢复不透明 —— 两页用的是同一个半透明底色笔刷, 同一位置换成另一页
        // 的底色, 合成结果完全一样, 看不到任何跳变。
        // ★ 顺序不能反: 先收起旧页再点亮新页, 中间不会有"两层底色同时不透明"的一帧。
        //   (新页在淡出阶段整页 Opacity=0 也是刻意的, 理由同上。)
        // ★ 页面没有 Content(过渡对象就是页面本身)时不能先点亮: 那样内容会硬跳出来,
        //   直接把淡入动画挂在页面本身即可(这种页面连底色一起淡, 目前产品里没有)。
        var fadeWholePage = ReferenceEquals(_incoming, _inFade);
        _swap?.Invoke();
        if (!fadeWholePage) SetOpacity(_incoming, 1);

        var fadeIn = new DoubleAnimation(DipOpacity, 1, TimeSpan.FromMilliseconds(FadeInMs))
        {
            EasingFunction = ease
        };
        fadeIn.Completed += (_, _) =>
        {
            if (epoch != _epoch) return;
            Complete();
        };
        _inFade?.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private void StopSafety()
    {
        _safety?.Stop();
        _safety = null;
    }

    private void StartSafety(int ms)
    {
        StopSafety();
        _safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _safety.Tick += (_, _) =>
        {
            if (_safety != null) Complete();
        };
        _safety.Start();
    }

    /// <summary>
    /// 立即结束当前过渡(连点导航时用), 并把最终状态落定:
    /// 该换的页补换, 两页都回到"内容不透明、页面不透明", 然后调用 done。
    /// </summary>
    public void FinishNow()
    {
        if (!InFlight) return;
        _epoch++;
        Complete();
    }

    /// <summary>
    /// 不播动画、清掉可能挂着的过渡(首次显示窗口 / 强制跳页时用)。
    /// 不会调用 swap/done —— 调用方自己设置"当前页"。
    /// </summary>
    public void Cancel()
    {
        _epoch++;
        InFlight = false;
        StopSafety();

        var o = _outgoing;
        var i = _incoming;
        var of = _outFade;
        var inf = _inFade;
        _outgoing = _incoming = _outFade = _inFade = null;
        _swap = _done = null;
        _swapped = false;

        SetOpacity(inf, 1);
        SetOpacity(i, 1);
        SetOpacity(of, 1);
        SetOpacity(o, 1);
    }

    private void Complete()
    {
        if (!InFlight) return;
        StopSafety();

        // 兜底路径(动画被渲染挂起): 还没到中点就先补一次换页, 保证调用方的记账正确。
        if (!_swapped)
        {
            _swapped = true;
            _swap?.Invoke();
        }

        var o = _outgoing;
        var i = _incoming;
        var of = _outFade;
        var inf = _inFade;
        var done = _done;

        _outgoing = _incoming = _outFade = _inFade = null;
        _swap = _done = null;
        InFlight = false;

        // 复位: 新页内容不透明; 旧页内容/底色也复位(它随后被收起, 复位看不到)。
        // 必须在 done 之前做 —— done 会顺手把旧页 Collapsed。
        SetOpacity(inf, 1);
        SetOpacity(i, 1);
        SetOpacity(of, 1);
        SetOpacity(o, 1);

        done?.Invoke();
    }

    /// <summary>
    /// 把某个页面复位成"完全可见": 页面本身和它的内容都不透明, 动画已摘掉。
    /// 不播过渡地显示某一页(首次显示 / 兜底)时用。
    /// </summary>
    public static void ResetVisual(UIElement page)
    {
        SetOpacity(page, 1);
        SetOpacity(FadeTarget(page), 1);
    }

    /// <summary>
    /// 过渡开始前把目标页摆成"完全不可见"(页面与内容都是 0)。
    /// 它必须**在 Visible + UpdateLayout 之前**调用: 目标页的底色是半透明的,
    /// 若以不透明状态上屏, 哪怕只有一帧, 也会和旧页底色叠成更深的一档。
    /// </summary>
    public static void PrepareIncoming(UIElement page)
    {
        SetOpacity(page, 0);
        SetOpacity(FadeTarget(page), 0);
    }

    /// <summary>
    /// 过渡真正作用的元素: 有 Content 的页面(ContentControl 派生的 UserControl)只淡它的
    /// 内容, 页面底色保持不动; 没有 Content 时退回页面本身。
    /// </summary>
    private static UIElement FadeTarget(UIElement page)
        => page is ContentControl { Content: UIElement content } ? content : page;

    private static void SetOpacity(UIElement? element, double opacity)
    {
        if (element == null) return;
        try
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = opacity;
        }
        catch
        {
            // 窗口关闭途中可能已经断开, 不影响功能
        }
    }
}
