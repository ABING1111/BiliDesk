using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「滚到某一项并居中」的纯换算逻辑(2026-11-28)。
///
/// 背景: 100 多集的合集自动连播跳下一集后, 右边列表回到最顶上 —— 换片清空 ItemsSource
/// 把偏移夹回 0, 而"滚到当前集"只在手动点开面板时做过。修法里"该滚到多少"抽成了
/// <see cref="SeasonScrollPolicy.CenterOffset"/>, 这里穷举边界: 居中 / 贴顶 / 贴尾 / 入参无效。
/// </summary>
public class SeasonScrollPolicyTests
{
    // 典型几何: 视口 600 高, 条目 70 高, 120 集共 8400 内容高
    private const double Viewport = 600;
    private const double Item = 70;
    private const double Scrollable = 8400 - Viewport;

    [Fact]
    public void 中间条目_落在视口正中()
    {
        // 当前偏移 1000, 条目顶边在视口内 300 处 → 条目中心 1335, 视口中心 1300
        // 目标 = 1000 + 300 - (600 - 70)/2 = 1035
        var offset = SeasonScrollPolicy.CenterOffset(1000, 300, Item, Viewport, Scrollable);
        Assert.Equal(1035, offset, precision: 3);
        // 复核: 滚过去之后条目中心确实对准视口中心
        var centerAfter = 300 + Item / 2 - (offset - 1000);
        Assert.Equal(Viewport / 2, centerAfter, precision: 3);
    }

    [Fact]
    public void 第一集_贴顶不滚出空白()
    {
        var offset = SeasonScrollPolicy.CenterOffset(0, 0, Item, Viewport, Scrollable);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void 最后一集_夹在可滚距离上限()
    {
        // 条目在视口底部: 想居中就得超过 ScrollableHeight
        var offset = SeasonScrollPolicy.CenterOffset(Scrollable, Viewport - Item, Item, Viewport, Scrollable);
        Assert.Equal(Scrollable, offset);
    }

    [Fact]
    public void 任意位置_结果都不越界()
    {
        for (var cur = 0.0; cur <= Scrollable; cur += 250)
        for (var top = 0.0; top <= Viewport; top += 100)
        {
            var offset = SeasonScrollPolicy.CenterOffset(cur, top, Item, Viewport, Scrollable);
            Assert.InRange(offset, 0, Scrollable);
        }
    }

    [Fact]
    public void 内容不满一屏_停在顶部()
    {
        Assert.Equal(0, SeasonScrollPolicy.CenterOffset(0, 100, Item, Viewport, 0));
        Assert.Equal(0, SeasonScrollPolicy.CenterOffset(500, 100, Item, Viewport, -20));
    }

    [Fact]
    public void 视口还没量出来_保持原偏移()
    {
        // 面板刚由 Collapsed 变 Visible: ViewportHeight 还是 0, 此时不能把列表跳走
        Assert.Equal(500, SeasonScrollPolicy.CenterOffset(500, 100, Item, 0, Scrollable));
    }

    [Fact]
    public void 条目高度未知_保持原偏移()
    {
        Assert.Equal(500, SeasonScrollPolicy.CenterOffset(500, 100, 0, Viewport, Scrollable));
        Assert.Equal(500, SeasonScrollPolicy.CenterOffset(500, 100, double.NaN, Viewport, Scrollable));
    }

    [Fact]
    public void 条目顶边无效_保持原偏移()
    {
        Assert.Equal(500, SeasonScrollPolicy.CenterOffset(500, double.NaN, Item, Viewport, Scrollable));
        Assert.Equal(500, SeasonScrollPolicy.CenterOffset(500, double.PositiveInfinity, Item, Viewport, Scrollable));
    }

    [Fact]
    public void 条目比视口还高_仍能滚到可见()
    {
        // 条目 800 高、视口 600: (视口 - 条目)/2 为负, 目标会把条目顶边往上推 —— 合理
        var offset = SeasonScrollPolicy.CenterOffset(1000, 400, 800, Viewport, Scrollable);
        Assert.Equal(1000 + 400 + 100, offset, precision: 3);
    }

    [Fact]
    public void 偏移本身无效_按0处理()
    {
        var offset = SeasonScrollPolicy.CenterOffset(double.NaN, 0, Item, Viewport, Scrollable);
        Assert.Equal(0, offset);
    }
}
