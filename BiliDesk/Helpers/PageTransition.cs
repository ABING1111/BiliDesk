using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 切页过渡: "淡出 → 换页 → 出现"。
///
/// ★★★ 淡入**不再用动画** —— 保留淡出(它是"遮住换页"的关键), 换页后直接落定 1。
///   (2026-10-12 修"切回首页空白一秒", 探针 bd-probe-switchback2 本机稳定复现):
///   在这台机器上, 把 PageHost 淡入到可见的那段动画**永远不上屏**:
///     · DoubleAnimation 版: 淡出段的屏幕变化正常(sd 9), 淡入段一帧都不出,
///       Opacity 被 HoldEnd 托管在 0, InFlight 永久 true;
///     · 改成 DispatcherTimer 手动插值(不依赖渲染 tick)后, Opacity 数值轨迹完全正确
///       (0.04 → 1.000 逐帧落定), 屏幕**仍然**纯白(sd 0.5);
///     · 期间 InvalidateMeasure/InvalidateVisual/UpdateLayout 全部无效;
///     · 换页方式 A/B: **Jump(不经过淡出/淡入, 直接换 Content) 553ms 正常上屏**。
///   ⇒ 该环境渲染器对"整棵子树做透明度动画"存在缺陷: 淡出方向勉强能动,
///     淡入方向(从透明到不透明)恢复渲染输出的那条路是坏的。
///   ⇒ 挡不住就绕开: 淡出保留(它负责"换页被遮住", 而且实测能动),
///     换页后**不再播淡入** —— 直接清动画落定 1。用户看到的是
///     "旧页淡出 → 一瞬底色 → 新页直接出现", 没有"浮现"但**绝不空白**;
///     换页那一拍本来就隔着一次淡出(90ms), 突兀感可忽略。
///   ★ 健康机器上的损失: 只有"淡入的 170ms 柔和感"。换来的: 所有机器上都不空白。
///
/// ★ 承载体是**页面自己**(<see cref="UIElement.Opacity"/>), 不用"幕布"(2026-10-11):
///   幕布要自己算底色, 云母/亚克力下算不准(那时窗口底透明、真底是壁纸);
///   淡出页面本身, 露出来的就是窗口底色, 任何材质/主题/壁纸下自动正确。
/// </summary>
public sealed class PageTransition
{
    /// <summary>淡出时长(页面 1→0, 露出底色)。</summary>
    public const int FadeOutMs = 90;

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

            // ★★★ 淡入不做动画(原因见类注释): 直接清动画 + 落定 1。
            //   ★ "清动画"这步不能省 —— 淡出的 DoubleAnimation 还挂在 Opacity 上
            //     (HoldEnd 托管在 0), 不清掉的话直接设 Opacity 会被动画值盖住。
            _content.BeginAnimation(UIElement.OpacityProperty, null);
            _content.Opacity = 1;
            InFlight = false;
        };
        InFlight = true;
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
