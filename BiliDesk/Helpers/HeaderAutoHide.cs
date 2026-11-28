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
/// 顶部信息区的收起/展开执行器: 记住展开高度, 之后按
/// <see cref="HeaderAutoHidePolicy"/> 的判定做高度动画。
///
/// 用法: 页面构造时 new 一个(传入头部元素), ScrollChanged 里调 <see cref="HandleScroll"/>,
/// 切 Tab / 重新进场时调 <see cref="Reset"/>(恢复展开并重新量高度)。
///
/// ★ 展开高度**不是量一次就定终身**: 头部的实际高度会随内容变(简介接口回来后折行、
///   点「详情」展开简介、窗口变窄多折一行), 所以缓存在展开态跟着 SizeChanged 走,
///   展开动画结束也把 Height 放回 Auto —— 详见 OnHostSizeChanged 与 SetCollapsed。
/// </summary>
public sealed class HeaderAutoHideController
{
    private readonly FrameworkElement _host;

    /// <summary>展开高度。NaN = 还没量到(第一帧布局可能还没完成, 量到 0 不能记下来)。</summary>
    private double _expandedHeight = double.NaN;

    /// <summary>头部当前是否已收起</summary>
    private bool _collapsed;

    /// <summary>正在播高度动画。这期间 ActualHeight 是过渡值, 不能当成"展开高度"记下来。</summary>
    private bool _animating;

    /// <summary>动画代次: 收起/展开连着触发时, 只认最后一次动画的 Completed。</summary>
    private int _animGen;

    public HeaderAutoHideController(FrameworkElement host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _host.SizeChanged += OnHostSizeChanged;
    }

    /// <summary>
    /// 展开态下高度变了就刷新缓存。
    ///
    /// ★★★ 这一条是"简介两行以上时, 收起再展开后投稿那一栏显示不全"的根因修复。
    ///   原来只在第一次滚动时量一次高度并永久缓存, 而 UP 主信息(昵称/简介)是
    ///   **接口回来之后**才填进去的 —— 首帧量到的是"空简介"的高度, 简介折成两行后
    ///   头部实际更高, 缓存却还是旧值。收起再展开时按旧高度把 Height 钉死,
    ///   ClipToBounds 就把底部的 Tab 行(投稿/图文/合集和系列)裁掉一截。
    ///   简介行数越多差得越多, 所以只有两行以上才看得出来。
    ///
    /// ★ 点「详情」把简介展开、窗口变窄让简介多折一行, 也走这条路 —— 高度都得跟着改。
    /// </summary>
    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 收起态(Height=0)、动画中途、重量过程中量到的都不是"展开高度"
        if (_animating || _collapsed || _measuring) return;
        if (e.NewSize.Height <= 0) return;
        _expandedHeight = e.NewSize.Height;
    }

    /// <summary>滚动了: 先量高度(只量一次), 再按策略收起/展开。</summary>
    public void HandleScroll(double offset, double extent, double viewport)
    {
        if (_measuring) return;   // 重量高度时 Height 临时是 Auto, 这期间的滚动值不可信
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
        _animating = false;
        _animGen++;
        _host.BeginAnimation(FrameworkElement.HeightProperty, null);
        _host.Height = double.NaN;   // Auto
        _expandedHeight = double.NaN;
    }

    /// <summary>正在重量高度。这期间布局会临时改成 Auto, 不能让 ScrollChanged 重入。</summary>
    private bool _measuring;

    /// <summary>
    /// 收起态下把 Height 临时放回 Auto, 量出**当前内容**的真实高度。
    ///
    /// ★ 收起时 Height 被钉成 0, 直接读 ActualHeight 只会得到 0 —— 缓存若在收起期间
    ///   过期(简介是收起之后才折行到位、或窗口变窄多折了一行), 按旧值展开就会
    ///   把底部的 Tab 行裁掉。这里量一次再把 0 写回去, 全程在同一个调度操作里完成,
    ///   渲染在之后, 所以不会闪出整块头部。
    /// </summary>
    private void MeasureExpandedHeight()
    {
        if (_measuring) return;
        _measuring = true;
        try
        {
            _host.BeginAnimation(FrameworkElement.HeightProperty, null);
            _host.Height = double.NaN;      // Auto
            _host.UpdateLayout();           // 同步布局: 不产生中间帧
            var height = _host.ActualHeight;
            _host.Height = 0;               // 还原收起态, 下面由动画接管
            if (height > 0) _expandedHeight = height;
        }
        finally
        {
            _measuring = false;
        }
    }

    private void SetCollapsed(bool collapse)
    {
        if (_collapsed == collapse) return;
        _collapsed = collapse;

        // 动画起点: 收起时从当前(展开)高度开始; 展开时固定从 0 开始 ——
        // 重量高度会把 Height 临时改成 Auto, 直接读 ActualHeight 会拿到展开高度,
        // 那样展开动画就变成"原地不动"了。
        var from = _host.ActualHeight;
        if (!collapse)
        {
            MeasureExpandedHeight();   // 按真实内容重量一次, 缓存可能是收起期间就过期的旧值
            from = 0;
        }

        if (double.IsNaN(_expandedHeight) || _expandedHeight <= 0) return;

        var target = collapse ? 0 : _expandedHeight;
        var gen = ++_animGen;
        _animating = true;
        var anim = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        // 动画结束后把基准值落到目标值, 下次从正确高度起算
        anim.Completed += (_, _) =>
        {
            // 半路又被反向触发: 这条动画作废, 别把旧目标写回 Height
            if (gen != _animGen) return;
            _animating = false;
            _host.BeginAnimation(FrameworkElement.HeightProperty, null);
            // ★ 收起钉 0(ClipToBounds 才能真的让出空间); **展开回 Auto** ——
            //   这是"投稿那一栏显示不全"的另一半修复。原来展开后把 Height 钉成动画目标
            //   (一个数值), 之后内容只要高一点(简介多折一行 / 点「详情」/ 窗口变窄),
            //   固定高度就再也跟不上, ClipToBounds 又把 Tab 行裁掉。回 Auto 永远贴合内容。
            _host.Height = collapse ? 0 : double.NaN;
        };
        _host.BeginAnimation(FrameworkElement.HeightProperty, anim);
    }
}
