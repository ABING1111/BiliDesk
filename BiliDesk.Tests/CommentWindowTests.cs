using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 评论区内容窗口的区间计算(2026-11-09)。
///
/// ★ 这里测三件事:
///   ① 同步恢复带必须覆盖视口(否则滚动时可见卡片会是空模板);
///   ② 预取带要略大于视口, 但明显小于释放带(否则"刚补上又被放掉");
///   ③ 释放带之外才允许释放, 且视口内永远不能判成可释放。
/// </summary>
public class CommentWindowTests
{
    private const double Viewport = 1000;

    [Fact]
    public void 视口内的评论必须同步恢复()
    {
        Assert.True(CommentWindow.ShouldRestoreNow(0, 80, Viewport));
        Assert.True(CommentWindow.ShouldRestoreNow(450, 520, Viewport));
        Assert.True(CommentWindow.ShouldRestoreNow(970, 1040, Viewport));
    }

    [Fact]
    public void 视口外的预取带比视口略大()
    {
        Assert.Equal(-750, CommentWindow.PrefetchTop(Viewport), 3);
        Assert.Equal(1750, CommentWindow.PrefetchBottom(Viewport), 3);
        Assert.True(CommentWindow.ShouldPrefetch(-700, -620, Viewport));
        Assert.True(CommentWindow.ShouldPrefetch(1600, 1680, Viewport));
        // 再远一点就出预取带了
        Assert.False(CommentWindow.ShouldPrefetch(-900, -820, Viewport));
        Assert.False(CommentWindow.ShouldPrefetch(1800, 1880, Viewport));
    }

    [Fact]
    public void 释放带必须明显大于预取带()
    {
        Assert.True(CommentWindow.ReleaseScreens > CommentWindow.PrefetchScreens);
        Assert.Equal(-2000, CommentWindow.ReleaseTop(Viewport), 3);
        Assert.Equal(3000, CommentWindow.ReleaseBottom(Viewport), 3);
    }

    [Fact]
    public void 视口内的评论永远不会被判定为可释放()
    {
        Assert.False(CommentWindow.ShouldRelease(-10, 100, Viewport));
        Assert.False(CommentWindow.ShouldRelease(500, 600, Viewport));
        Assert.False(CommentWindow.ShouldRelease(950, 1020, Viewport));
    }

    [Fact]
    public void 离屏足够远才释放()
    {
        Assert.True(CommentWindow.ShouldRelease(-2200, -2100, Viewport));
        Assert.True(CommentWindow.ShouldRelease(3100, 3200, Viewport));
        // 只是刚出视口, 还在常驻窗口里, 不能释放
        Assert.False(CommentWindow.ShouldRelease(-1200, -1100, Viewport));
        Assert.False(CommentWindow.ShouldRelease(2100, 2200, Viewport));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void 视口非法时一律不动作(double viewport)
    {
        Assert.False(CommentWindow.ShouldRestoreNow(0, 100, viewport));
        Assert.False(CommentWindow.ShouldPrefetch(0, 100, viewport));
        Assert.False(CommentWindow.ShouldRelease(0, 100, viewport));
    }

    [Theory]
    // BV1irxX6LE9R 的真实数据: 热门只给 1 条非置顶根评论, 总数 3668 ⇒ 必须回退
    [InlineData(1, 3668, true)]
    [InlineData(0, 3668, true)]
    [InlineData(14, 3668, true)]
    // 热门正常返回: 不回退, 否则「热门」排序会被悄悄换成时间
    [InlineData(15, 3668, false)]
    [InlineData(25, 3668, false)]
    // 视频本来就少于阈值: 热门已给全, 不白打接口
    [InlineData(3, 3, false)]
    [InlineData(2, 3, true)]
    [InlineData(10, 10, false)]
    // 总数缺失/为零: 不判回退
    [InlineData(0, 0, false)]
    public void 热门评论异常少时回退到时间(int hotRoots, long total, bool expected)
    {
        Assert.Equal(expected, CommentWindow.ShouldFallbackHot(hotRoots, total));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(30, 60)]
    [InlineData(60, 120)]
    [InlineData(120, 180)]
    [InlineData(180, 180)]
    public void 限流冷却按倍数退避且封顶(int current, int expected)
    {
        Assert.Equal(expected, CommentWindow.NextRateLimitCooldown(current));
    }

    [Theory]
    [InlineData("当前网络请求过于频繁(B站限流), 请稍候再试", true)]
    [InlineData("B站限流", true)]
    [InlineData("网络请求失败: timeout", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 只把限流文案认作冷却信号(string? err, bool expected)
    {
        Assert.Equal(expected, CommentWindow.IsRateLimitMessage(err));
    }
}
