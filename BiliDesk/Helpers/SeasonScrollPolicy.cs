using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 「把列表滚到某一项并让它**居中**」的纯换算逻辑(不依赖 WPF, 可直接单测)。
///
/// ★ 为什么需要(2026-11-28 用户报): 100 多集的合集里自动连播跳到下一集后, 右边合集列表
///   会回到最顶上, 用户得一路往下划才能找到当前集。根因是换片时 ItemsSource 被清空重灌,
///   WPF 把偏移夹回 0 —— 而"滚到当前集"只在用户**手动点开**面板时才做过一次。
///
/// ★ 为什么居中而不是"只保证可见": WPF 原生 ScrollIntoView 只把条目**贴进**视口,
///   往下走时它会贴在视口最底边 —— 当前集下面是空的, 下一集全在屏幕外, 连播时每集都要
///   再划一次。居中后上下各能看到几集, 连播的"下一集"就在眼皮底下。
///
/// ★ 与 PopupPlacement / AutoPlayPolicy 同款拆法: 判定是纯函数, 量坐标、调
///   ScrollToVerticalOffset 的接线留在 PlayerWindow(见 ScrollListToItemCentered)。
/// </summary>
public static class SeasonScrollPolicy
{
    /// <summary>
    /// 算出"让 <paramref name="itemTop"/> 处、高 <paramref name="itemHeight"/> 的条目
    /// 落在视口正中间"所需的纵向偏移。
    /// </summary>
    /// <param name="currentOffset">列表当前纵向偏移(条目顶边就是相对它量的)</param>
    /// <param name="itemTop">条目顶边在**视口坐标系**里的位置(0 = 视口顶边)</param>
    /// <param name="itemHeight">条目高度</param>
    /// <param name="viewportHeight">列表视口高度</param>
    /// <param name="scrollableHeight">列表可滚动的总距离(ScrollViewer.ScrollableHeight)</param>
    /// <returns>夹在 <c>[0, scrollableHeight]</c> 内的目标偏移。</returns>
    public static double CenterOffset(
        double currentOffset, double itemTop, double itemHeight,
        double viewportHeight, double scrollableHeight)
    {
        // 内容不满一屏 / 还没量出可滚距离: 没有"中间"可去, 钉在顶部最稳
        if (scrollableHeight <= 0) return 0;

        if (!double.IsFinite(currentOffset)) currentOffset = 0;

        // 布局还没跑完(视口 0 高)或条目还没生成: 保持原样, 别把列表乱跳一下
        if (!double.IsFinite(itemTop) || !double.IsFinite(itemHeight) || !double.IsFinite(viewportHeight))
            return Clamp(currentOffset, scrollableHeight);
        if (itemHeight <= 0 || viewportHeight <= 0)
            return Clamp(currentOffset, scrollableHeight);

        // 条目中心对准视口中心: 当前偏移 + 条目顶边 - (视口 - 条目) / 2
        var target = currentOffset + itemTop - (viewportHeight - itemHeight) / 2;

        // 首项贴顶、末项贴底时夹住 —— 否则会滚出一片空白
        return Clamp(target, scrollableHeight);
    }

    private static double Clamp(double value, double max)
        => value < 0 ? 0 : value > max ? max : value;
}
