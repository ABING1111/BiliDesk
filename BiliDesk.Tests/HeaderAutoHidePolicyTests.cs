using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 顶部信息区"滚过收起、回顶展开"的阈值(UP 主主页与合集详情页共用)。
///
/// ★ 两个关键反例必须钉死:
///   ① 只滚开一点点就收起 → 收起把视口撑大、偏移被夹回顶部 → 立刻又展开, 卡片上下弹;
///   ② 收起后剩下的可滚高度太小 → 同样会被夹回顶部附近, 形成同样的弹跳。
/// </summary>
public class HeaderAutoHidePolicyTests
{
    private const double Expanded = 180;
    private const double Viewport = 600;

    [Fact]
    public void 回到顶部_无条件展开()
    {
        Assert.Equal(HeaderAutoHidePolicy.Action.Expand,
            HeaderAutoHidePolicy.Decide(0, 1000, Viewport, Expanded));
        Assert.Equal(HeaderAutoHidePolicy.Action.Expand,
            HeaderAutoHidePolicy.Decide(HeaderAutoHidePolicy.ExpandAtTop, 1000, Viewport, Expanded));
    }

    [Fact]
    public void 刚滚开一点_不动()
    {
        // 9px 已经离开顶部, 但还远没滚过"头部高度 + 余量" —— 此时收起会引发弹跳
        Assert.Equal(HeaderAutoHidePolicy.Action.None,
            HeaderAutoHidePolicy.Decide(9, 1000, Viewport, Expanded));
        Assert.Equal(HeaderAutoHidePolicy.Action.None,
            HeaderAutoHidePolicy.Decide(Expanded + HeaderAutoHidePolicy.HeaderCollapseSlack,
                1000, Viewport, Expanded));
    }

    [Fact]
    public void 滚过头部且下方还有足够内容_收起()
    {
        var offset = Expanded + HeaderAutoHidePolicy.HeaderCollapseSlack + 1;
        Assert.Equal(HeaderAutoHidePolicy.Action.Collapse,
            HeaderAutoHidePolicy.Decide(offset, 1000, Viewport, Expanded));
    }

    [Fact]
    public void 滚过头部但收起后会不够滚_不收起()
    {
        // 可滚高度只有 200, 小于 头部 180 + 余量 32
        var offset = Expanded + HeaderAutoHidePolicy.HeaderCollapseSlack + 1;
        Assert.Equal(HeaderAutoHidePolicy.Action.None,
            HeaderAutoHidePolicy.Decide(offset, Viewport + 200, Viewport, Expanded));
    }

    [Fact]
    public void 还没量到头部高度_不动()
    {
        Assert.Equal(HeaderAutoHidePolicy.Action.None,
            HeaderAutoHidePolicy.Decide(500, 2000, Viewport, 0));
        Assert.Equal(HeaderAutoHidePolicy.Action.None,
            HeaderAutoHidePolicy.Decide(500, 2000, Viewport, -1));
    }
}
