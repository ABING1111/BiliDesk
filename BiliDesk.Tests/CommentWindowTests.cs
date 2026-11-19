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
        // ★ 方向偏置后, 前进方向的预取带也必须仍在释放带内(否则"刚补上又被放掉")
        Assert.True(CommentWindow.ReleaseScreens > CommentWindow.PrefetchAheadScreens);
        Assert.Equal(-2000, CommentWindow.ReleaseTop(Viewport), 3);
        Assert.Equal(3000, CommentWindow.ReleaseBottom(Viewport), 3);
    }

    // ------------------------------------------------------------ 方向偏置预取带(2026-11-17)

    [Fact]
    public void 方向未知时预取带保持对称()
    {
        Assert.Equal(CommentWindow.PrefetchTop(Viewport),
            CommentWindow.DirectionalPrefetchTop(Viewport, 0), 3);
        Assert.Equal(CommentWindow.PrefetchBottom(Viewport),
            CommentWindow.DirectionalPrefetchBottom(Viewport, 0), 3);
    }

    [Fact]
    public void 向下滚时预取缓冲偏向下方()
    {
        // 上方只留 0.25 屏, 下方留 1.25 屏
        Assert.Equal(-250, CommentWindow.DirectionalPrefetchTop(Viewport, 1), 3);
        Assert.Equal(2250, CommentWindow.DirectionalPrefetchBottom(Viewport, 1), 3);
    }

    [Fact]
    public void 向上滚时预取缓冲偏向上方()
    {
        Assert.Equal(-1250, CommentWindow.DirectionalPrefetchTop(Viewport, -1), 3);
        Assert.Equal(1250, CommentWindow.DirectionalPrefetchBottom(Viewport, -1), 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(0)]
    public void 任何方向的预取带都完整覆盖视口且总宽不变(int direction)
    {
        var top = CommentWindow.DirectionalPrefetchTop(Viewport, direction);
        var bottom = CommentWindow.DirectionalPrefetchBottom(Viewport, direction);
        // 覆盖视口(否则视口边缘的卡片没人预取)
        Assert.True(top <= 0);
        Assert.True(bottom >= Viewport);
        // 总宽 = 视口 + 2×PrefetchScreens 屏: 偏置只挪缓冲, 不放大内存窗口
        Assert.Equal(Viewport * (1 + 2 * CommentWindow.PrefetchScreens), bottom - top, 3);
        // 必须仍在释放带内(迟滞, 防"刚补上又被放掉"的抖动)
        Assert.True(top > CommentWindow.ReleaseTop(Viewport));
        Assert.True(bottom < CommentWindow.ReleaseBottom(Viewport));
    }

    [Fact]
    public void 预取预算与批量常量在合理范围()
    {
        // 预算必须明显小于一帧(60Hz ≈ 16.7ms), 否则切片失去意义
        Assert.InRange(CommentWindow.PrefetchBudgetMs, 1, 10);
        Assert.InRange(CommentWindow.PrefetchBurst, 1, 8);
        Assert.InRange(CommentWindow.RevealBatch, 1, 6);
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
