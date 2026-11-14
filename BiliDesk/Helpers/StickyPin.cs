using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 「吸顶胶囊」控制器 —— 设置页顶部那颗 Tab 胶囊的折叠吸顶效果, 抽出来给收藏页 / 历史页共用。
///
/// ## 效果
/// 胶囊先跟着内容一起上滑, 碰到距视口顶端 <see cref="PinnedTopGap"/> 处就钉住不动;
/// 页面大标题照常滚走。**没有整条吸顶栏、没有背景色块**, 内容从胶囊两侧与下方正常经过。
///
/// ## 做法(与设置页 2026-11-14 那版逐字同构)
/// 胶囊是滚动内容里的**唯一实例**(不再有"缩小版"副本), 尺寸/字号/内边距全程不变:
///   · 它在内容坐标里的自然顶边 = <c>_restTop</c>(由占位块量出来, 见 <see cref="Sync"/>);
///   · 每帧只改 <see cref="TranslateTransform.Y"/> = <c>max(0, 已滚动 - (restTop - gap))</c>,
///     用反向平移抵消滚动量 —— 滚不到阈值时位移为 0(跟着内容走), 之后正好停在 gap 处。
/// ★ 只动 RenderTransform, 不碰布局: 滚动过程中每帧不触发测量/排版。
///
/// ## 页面要怎么摆
/// <code>
/// ScrollViewer(PageScroll)
///   └ Grid
///       ├ StackPanel(正常内容流, 里面放一个 **Hidden 的占位块** 撑出胶囊的高度)
///       └ 胶囊(最后一个子元素 ⇒ 画在卡片之上; VerticalAlignment=Top + Margin.Top=restTop)
/// </code>
/// 占位块必须留在内容流里: 胶囊本体是浮层, 不占高度, 没有占位块内容就会顶到标题下面。
///
/// ★ 钉住后胶囊顶端会进入窗口标题栏区域(MainWindow 的 CaptionHeight=20):
///   胶囊上要标 <c>shell:WindowChrome.IsHitTestVisibleInChrome="True"</c>, 否则那块区域里的
///   点击会被 WindowChrome 当成"拖动窗口"吞掉。
/// </summary>
public sealed class StickyPin
{
    /// <summary>
    /// 吸顶后胶囊距视口顶端留多少(逻辑像素)。
    /// ★ 不要贴到 0: 胶囊带 DropShadowEffect, 贴边时阴影会在窗口顶边被切掉, 看着很"顶";
    ///   8px 既留出呼吸空间, 又能让整圈阴影完整露出来(阴影外扩约 7px)。
    /// </summary>
    public const double PinnedTopGap = 8;

    private readonly ScrollViewer _scroll;
    private readonly FrameworkElement _slot;
    private readonly FrameworkElement _capsule;
    private readonly TranslateTransform _shift;

    // 胶囊的左/右/下边距由 XAML 决定(收藏/历史是左对齐 36, 设置页是居中 0),
    // Sync 只改 Top —— 别把人家写好的水平位置抹掉。
    private readonly double _marginLeft;
    private readonly double _marginRight;
    private readonly double _marginBottom;

    /// <summary>胶囊在"未滚动内容坐标"里的自然顶边。初值取 XAML 写的 Margin.Top。</summary>
    private double _restTop;

    /// <param name="scroll">整页滚动的那个 ScrollViewer(标题也在里面)。</param>
    /// <param name="slot">内容流里的占位块, 只用来量自然位置, 不渲染。</param>
    /// <param name="capsule">胶囊本体(滚动内容的最后一个子元素)。</param>
    /// <param name="shift">胶囊 RenderTransform 上的那个 TranslateTransform。</param>
    public StickyPin(ScrollViewer scroll, FrameworkElement slot, FrameworkElement capsule, TranslateTransform shift)
    {
        _scroll = scroll;
        _slot = slot;
        _capsule = capsule;
        _shift = shift;
        _marginLeft = capsule.Margin.Left;
        _marginRight = capsule.Margin.Right;
        _marginBottom = capsule.Margin.Bottom;
        _restTop = capsule.Margin.Top;
    }

    /// <summary>
    /// 每次 ScrollChanged 调一次: 按当前偏移算反向位移。纯 RenderTransform, 不触发布局。
    /// </summary>
    public void Update()
    {
        var offset = Math.Max(0, _scroll.VerticalOffset);
        // 位移 = max(0, 已滚动 - (自然位置 - 吸顶留白)):
        //   滚动不到 restTop-gap 时位移为 0(跟着内容走), 之后正好停在距顶端 gap 处。
        _shift.Y = Math.Max(0, offset - (_restTop - PinnedTopGap));
    }

    /// <summary>
    /// 量一次胶囊在内容里的自然位置并写回上边距。
    /// ★ 不硬编码: 标题字号/行高变化、字体换了、胶囊比标题矮时, 自然位置都会变;
    ///   用占位块的实际位置更稳 —— 加回滚动偏移就是"未滚动时"的坐标。
    /// ★ 占位块通常是 Hidden(不渲染但参与排版), 所以这里只排除 **Collapsed**:
    ///   那种情况下它没参与排版, 量出来的是过期值(收藏页还没有收藏夹时就是这样,
    ///   等夹子到位后 ScheduleOverflowUpdate 会再量一次)。
    /// </summary>
    public void Sync()
    {
        if (_slot.Visibility != Visibility.Collapsed)
        {
            try
            {
                // TransformToAncestor(ScrollViewer) 给的是**视口坐标**(已含滚动偏移),
                // 加回 VerticalOffset 就还原成内容坐标。
                var y = _slot.TransformToAncestor(_scroll).Transform(new Point(0, 0)).Y;
                var rest = Math.Max(0, y + _scroll.VerticalOffset);
                if (Math.Abs(rest - _restTop) >= 0.5)
                {
                    _restTop = rest;
                    _capsule.Margin = new Thickness(_marginLeft, rest, _marginRight, _marginBottom);
                }
            }
            catch
            {
                // 还没进可视树: 保持当前值, 滚动时照样能钉住
            }
        }

        Update();
    }

    /// <summary>
    /// 等布局落定后再 <see cref="Sync"/>。Loaded / 变可见 / 数据到位这些时机都要用它:
    /// 当场量拿到的是上一拍的版式。
    /// </summary>
    public void SyncDeferred()
        => _capsule.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Sync));
}