using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace BiliDesk.Helpers;

/// <summary>
/// 切页过渡: 三段式"淡出 → 换页 → 淡入"。
///
/// ★★★ 承载体改回**页面自己**(<see cref="UIElement.Opacity"/>), 不再用"幕布"(2026-10-11)。
///
///   为什么把幕布去掉: 幕布是一块**自己上色**的板子, 颜色必须在本地算出来。
///   纯色(无材质)模式下算得准(半透明页面底合成在窗口实色底上); 但**材质(云母/亚克力)
///   模式下算不准** —— 那时窗口底色是**全透明的**(要让 DWM 采样壁纸的合成层透出来),
///   真正的底色是**壁纸**, 程序无从得知。拿主题实色去合成, 就会得到一块比真实背景
///   **亮十几个色阶**的板子 —— 用户报的"云母/亚克力下白色幕布很突兀"就是这个:
///   实测浅色壁纸时幕布 241,241,242 而真实底色约 229(探针 bd-probe-navveil3 读数)。
///   ⇒ 与其继续猜壁纸颜色, 不如**根本不画那块板子**:
///     淡出页面本身, 露出来的就是**窗口自己的底色**, 而页面底本来就是半透明的
///     AppBackgroundBrush 压在同一层上 —— 所以过渡色在**任何材质/主题/壁纸**下自动正确。
///
///   ★ 幕布当初的**真正收益被完整保留**: 建页动作仍发生在"看不见"的那一拍 ——
///     页面透明度到 0 之后才换页+建页, 用户先看到干净底色, 再看到新页面淡入。
///   ★ 性能没有退步: 本类旧注释记过实测结论 —— 动画 PageHost.Opacity 与动画幕布
///     帧间隔**一样**(p50 ~7ms / p95 ~14ms / 掉帧 0), 载体不是卡顿来源。
///
/// ★ 为什么单独成一个类: 这段逻辑有两处容易写错、而且只有"跑起来"才看得出来 ——
///   ① 过渡途中被打断时不能停在被取消的那一页;
///   ② "点当前页"的早退判断在过渡在途时不能生效(否则点了导航项却停在旧页)。
///   抽出来之后探针能直接拿它对着真实窗口跑 —— 验的是**发布出去的同一份代码**, 不是复制品。
/// </summary>
public sealed class PageTransition
{
    /// <summary>淡出时长(页面 1→0, 露出底色)</summary>
    public const int FadeOutMs = 90;

    /// <summary>淡入时长(页面 0→1, 新页面浮出)。比淡出长一点: 出场干脆、入场柔和。</summary>
    public const int FadeInMs = 170;

    /// <summary>被淡入淡出的页面(实参是 MainWindow 的 PageHost)</summary>
    private readonly UIElement _content;

    /// <summary>过渡代数。每次 Run/Jump +1, 在途回调靠它判断自己是否已被更晚的过渡取代。</summary>
    private int _epoch;

    public PageTransition(UIElement content) => _content = content;

    /// <summary>是否有过渡正在跑(页面已淡出/正在淡出)。调用方用它做"点当前页"的早退判断。</summary>
    public bool InFlight { get; private set; }

    /// <summary>
    /// 播一次完整过渡。<paramref name="swap"/> 在**页面完全不可见**的那一刻调用 ——
    /// 换页动作(以及首次建页)被完全遮住, 用户看不到任何跳变。
    /// 过渡途中再次调用会从页面**当前**透明度接着走(不跳回 1), 并让上一次的回调作废。
    /// </summary>
    public void Run(Action swap)
    {
        var epoch = ++_epoch;
        InFlight = true;

        // ★ From 取**当前**透明度而不是写死 1: 上一拍淡入还没播完时用户又切页(连点导航项),
        //   写死 1 会让页面先"跳"回全不透明再淡出 —— 看起来就是闪一下。
        //   传当前值则从半透明状态继续往 0 走, 视觉上是连贯的。
        var fadeOut = new DoubleAnimation(_content.Opacity, 0, TimeSpan.FromMilliseconds(FadeOutMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (epoch != _epoch) return;   // 已被更晚的过渡取代, 这一拍作废
            swap();

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (_, _) =>
            {
                if (epoch != _epoch) return;
                // ★ 收尾必须把动画清掉并落定到 1: 留着动画时钟的话, 元素会一直被动画值托管,
                //   别处(如 SwitchPageNow)再想直接设 Opacity 就会被动画值盖住。
                _content.BeginAnimation(UIElement.OpacityProperty, null);
                _content.Opacity = 1;
                InFlight = false;
            };
            _content.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };
        _content.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>
    /// 不播过渡、立刻换页(首次显示窗口时用), 并清掉页面上可能残留的透明度动画。
    /// 同时让所有在途回调作废 —— 被打断的过渡不能再改页面。
    /// </summary>
    public void Jump(Action swap)
    {
        _epoch++;
        InFlight = false;
        _content.BeginAnimation(UIElement.OpacityProperty, null);
        _content.Opacity = 1;
        swap();
    }
}
