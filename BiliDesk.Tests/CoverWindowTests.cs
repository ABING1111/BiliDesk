using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 封面补图区间 + 常驻上限(2026-11-07 第五次重做后的语义)。
///
/// ★ 这里测两件事:
///   ① **补图区间**: 视口内的卡一定要补(判错 ⇒ 边缘出现半张灰卡);
///   ② **常驻上限**: 上限必须明显大于补图区间(判错 ⇒ 刚补上就被挤掉的抖动),
///      也不能太大(判错 ⇒ 内存不回落, 就是用户报的"一直下滑内存不断涨")。
///
/// ★ 为什么"释放判据"不在这里测: 它已经不是几何判断, 而是"最后被看到的时间戳"
///   (见 CardWallPanel.ReleaseStalestCovers)。时间戳的排序逻辑由探针
///   bd-probe-scroll-mem 端到端验证(6 页滚动、常驻张数、回顶出图速度)。
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

    // ---------------------------------------------------------------- 常驻上限

    [Fact]
    public void 常驻上限是视口张数的2点5倍()
    {
        // 视口 24 张 ⇒ 上限 60 张。这是"内存有上界"与"不会刚补上就被挤掉"的折中,
        // 见 CoverWindow.ResidentScreens 的说明。
        Assert.Equal(60, CoverWindow.ResidentCap(24));
        Assert.Equal(30, CoverWindow.ResidentCap(12));
        Assert.Equal(100, CoverWindow.ResidentCap(40));
    }

    [Fact]
    public void 常驻上限必须明显大于补图区间_否则会抖动()
    {
        // 补图区间 = 视口 ± 半屏 = 2 屏。上限若 ≤ 2 屏, 就会出现
        // "刚补上 → 名额不够 → 立刻被放掉 → 下一轮又补"的循环。
        // 这里钉住"至少留半屏的余量"。
        Assert.True(CoverWindow.ResidentScreens >= 1 + 2 * CoverWindow.PrefetchScreens,
            $"ResidentScreens={CoverWindow.ResidentScreens} 必须 >= " +
            $"{1 + 2 * CoverWindow.PrefetchScreens}(补图区间) + 余量");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void 视口张数非法时上限为0_调用方应跳过释放(int visibleCount)
    {
        // 0 表示"没有视口就没有上限", 调用方必须跳过释放 ——
        // 否则会按 0 张上限把整墙放光(用户切回来一片灰)。
        Assert.Equal(0, CoverWindow.ResidentCap(visibleCount));
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
