using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace BiliDesk.Helpers;

/// <summary>
/// 顶部信息区「滚过就收起、回顶再展开」的判定(纯函数, 便于单测)。
///
/// 这套阈值原本写在 UP 主主页里(2026-11-19 修"往下滑卡片上下跳")。合集详情页
/// 也要同一种行为, 于是抽到这里 —— 两处各抄一份迟早会走歪。
///
/// ★★★ 两条判据必须在**偏移轴**上隔开:
///   触发收起要滚过"头部高度 + <see cref="HeaderCollapseSlack"/>",
///   触发展开只要回到 <see cref="ExpandAtTop"/> 以内。
///   如果允许"滚 9px 就收起", 收起会把视口撑大、把偏移夹回顶部附近,
///   立刻满足"回到顶部 ⇒ 展开", 而展开又把可滚高度长回去 —— 卡片跟着一帧一帧
///   上下弹, 这就是用户看到的抽搐。
/// </summary>
public static class HeaderAutoHidePolicy
{
    /// <summary>收起头部**至少要先滚过**"头部高度 + 这么多"。</summary>
    public const double HeaderCollapseSlack = 40;

    /// <summary>
    /// 收起之后**至少还要能滚出这么多**才允许收起。
    /// 收起会让 ScrollViewer 的视口变大、可滚高度随之变小, 用户还会被夹回
    /// offset ≈ (可滚 − 头部高度)。余量一旦落进 <see cref="ExpandAtTop"/> 里,
    /// 收起立刻又满足"回到顶部 ⇒ 展开"。所以余量必须明显大于 ExpandAtTop。
    /// </summary>
    public const double MinScrollAfterCollapse = 32;

    /// <summary>偏移 ≤ 这个值视为"回到顶部", 无条件展开。</summary>
    public const double ExpandAtTop = 8;

    public enum Action
    {
        None,
        Expand,
        Collapse
    }

    /// <summary>
    /// 根据当前滚动状态决定头部该怎么动。
    /// <paramref name="expandedHeight"/> 为 0/负数 表示还没量到展开高度(不动)。
    /// </summary>
    public static Action Decide(double offset, double extent, double viewport, double expandedHeight)
    {
        if (expandedHeight <= 0) return Action.None;

        // 回到顶部: 无条件展开
        if (offset <= ExpandAtTop) return Action.Expand;

        // ① 偏移轴: 还没滚过"头部高度 + 余量"就收起, 等于在屏幕中间把内容猛地往上抽
        //    一截, 接着又被夹回顶部、又展开 —— 卡片上下弹。
        if (offset <= expandedHeight + HeaderCollapseSlack) return Action.None;

        // ② 可滚高度: 收起之后仍然有得滚, 否则视口变大后整页刚好装下, 立刻又满足"回到顶部"。
        if (extent - viewport <= expandedHeight + MinScrollAfterCollapse) return Action.None;

        return Action.Collapse;
    }
}

/// <summary>
/// 顶部信息区的收起/展开执行器: 量一次展开高度, 之后按
/// <see cref="HeaderAutoHidePolicy"/> 的判定做高度动画。
///
/// 用法: 页面构造时 new 一个(传入头部元素), ScrollChanged 里调 <see cref="HandleScroll"/>,
/// 切 Tab / 重新进场时调 <see cref="Reset"/>(恢复展开并重新量高度)。
/// </summary>
public sealed class HeaderAutoHideController
{
    private readonly FrameworkElement _host;

    /// <summary>展开高度。NaN = 还没量到(第一帧布局可能还没完成, 量到 0 不能记下来)。</summary>
    private double _expandedHeight = double.NaN;

    /// <summary>头部当前是否已收起</summary>
    private bool _collapsed;

    public HeaderAutoHideController(FrameworkElement host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>滚动了: 先量高度(只量一次), 再按策略收起/展开。</summary>
    public void HandleScroll(double offset, double extent, double viewport)
    {
        if (double.IsNaN(_expandedHeight))
        {
            var measured = _host.ActualHeight;
            // 量到 0 说明这一帧还没布局完, 保持 NaN 下次再量 ——
            // 直接把 0 记下来会让头部永远收不起来。
            if (measured <= 0) return;
            _expandedHeight = measured;
        }

        switch (HeaderAutoHidePolicy.Decide(offset, extent, viewport, _expandedHeight))
        {
            case HeaderAutoHidePolicy.Action.Expand:
                SetCollapsed(false);
                break;
            case HeaderAutoHidePolicy.Action.Collapse:
                SetCollapsed(true);
                break;
        }
    }

    /// <summary>恢复展开并忘掉量到的高度(切 Tab / 换页面 / 重新进场时用)。</summary>
    public void Reset()
    {
        _collapsed = false;
        _host.BeginAnimation(FrameworkElement.HeightProperty, null);
        _host.Height = double.NaN;   // Auto
        _expandedHeight = double.NaN;
    }

    private void SetCollapsed(bool collapse)
    {
        if (_collapsed == collapse) return;
        _collapsed = collapse;
        if (double.IsNaN(_expandedHeight) || _expandedHeight <= 0) return;

        var target = collapse ? 0 : _expandedHeight;
        var anim = new DoubleAnimation(_host.ActualHeight, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        // 动画结束后把基准值落到目标值, 下次从正确高度起算
        anim.Completed += (_, _) =>
        {
            _host.BeginAnimation(FrameworkElement.HeightProperty, null);
            _host.Height = target;
        };
        _host.BeginAnimation(FrameworkElement.HeightProperty, anim);
    }
}
