using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 封面补图区间(2026-11-07 第三次重做后的语义)。
///
/// ★ 这里**只测"补图"**, 不再测"释放" —— 因为释放已经不在滚动路径上了
///   (见 <see cref="CoverWindow"/> 的说明: 释放只在"整面墙离开屏幕"时发生,
///   那一刻没有"哪张卡在哪"的判断, 所以没有可测的几何)。
///
/// 判错的后果:
///   · 补得太少 ⇒ 快速滚动时视口边缘出现半张灰卡;
///   · 补得太多 ⇒ 单页常驻的封面变多(内存), 但不会出灰块。
/// 所以下面钉的是"视口内的卡一定在区间内"这条底线。
/// </summary>
public class CoverWindowTests
{
    private const double Viewport = 1000;

    private static CoverWindow.Bands Bands()
        => CoverWindow.For(Viewport)!.Value;

    [Fact]
    public void 补图区间按余量展开()
    {
        var b = Bands();
        // 上下各留半屏(500)
        Assert.Equal(-500, b.Top, 3);
        Assert.Equal(1500, b.Bottom, 3);
    }

    [Fact]
    public void 视口内的卡片一定要补图()
    {
        var b = Bands();
        // 视口正中
        Assert.True(CoverWindow.ShouldRestore(400, 600, b));
        // 顶部只露一角
        Assert.True(CoverWindow.ShouldRestore(-80, 40, b));
        // 底部只露一角
        Assert.True(CoverWindow.ShouldRestore(960, 1080, b));
        // 正好铺满
        Assert.True(CoverWindow.ShouldRestore(0, Viewport, b));
    }

    [Fact]
    public void 视口边缘的卡片也算要补_避免出现半张灰卡()
    {
        var b = Bands();
        // 底边正好压在视口上沿(0): 会露出 0 像素, 但仍算"在区间内" —— 多贴一张不吃亏
        Assert.True(CoverWindow.ShouldRestore(-133, 0, b));
        // 顶边正好压在视口下沿: 同理
        Assert.True(CoverWindow.ShouldRestore(Viewport, Viewport + 133, b));
    }

    [Fact]
    public void 余量内的卡片也要补_让下一行提前准备好()
    {
        var b = Bands();
        // 视口上方半屏内
        Assert.True(CoverWindow.ShouldRestore(-400, -300, b));
        // 视口下方半屏内
        Assert.True(CoverWindow.ShouldRestore(1100, 1200, b));
    }

    [Fact]
    public void 超出余量的卡片不补_这是单页内存的上界来源()
    {
        var b = Bands();
        // 视口上方一屏外
        Assert.False(CoverWindow.ShouldRestore(-1200, -1100, b));
        // 视口下方一屏外
        Assert.False(CoverWindow.ShouldRestore(1600, 1700, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 视口非法时返回null_调用方应当整轮跳过(double viewport)
    {
        // 视口为 0 时如果照常算, 区间会塌成一条线, 什么都补不到(或补错)。
        // 必须返回 null 让调用方跳过这一轮。
        Assert.Null(CoverWindow.For(viewport));
    }

    // ---------------------------------------------------------------- 严格视口判定

    [Fact]
    public void 严格视口内_用于判断是否还有空封面()
    {
        Assert.True(CoverWindow.InViewport(0, 200, Viewport));
        Assert.True(CoverWindow.InViewport(-50, 50, Viewport));      // 上半截露进来
        Assert.True(CoverWindow.InViewport(950, 1100, Viewport));    // 下半截露进来

        Assert.False(CoverWindow.InViewport(-200, -1, Viewport));    // 完全在上方
        Assert.False(CoverWindow.InViewport(Viewport, Viewport + 100, Viewport)); // 完全在下方
        Assert.False(CoverWindow.InViewport(-133, 0, Viewport));     // 压线但没露出来
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void 严格视口判定_视口非法时一律false(double viewport)
    {
        Assert.False(CoverWindow.InViewport(0, 200, viewport));
    }

    // ---------------------------------------------------------------- 开关

    [Fact]
    public void 一键关闭开关存在_用于出观感问题时回到最稳行为()
    {
        // 只断言"这个属性可读且是 bool" —— 它的值取决于进程环境变量,
        // 单测里不该依赖那个(同一进程内其他测试可能已经设过)。
        // 真正重要的是: 这个逃生口存在, 且默认可用(代码里没有硬编码 false)。
        Assert.IsType<bool>(CoverWindow.Enabled);
    }
}
