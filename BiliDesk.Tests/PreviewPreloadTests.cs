using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 图片预览窗的相邻图预取策略(2026-11-28, "快速切图时新图迟迟不出现")。
///
/// ★ 钉两件事: ① 预取顺序与边界(首张没有上一张、末张没有下一张, 不能算出越界下标);
///   ② `IsWanted` 的窗口边界 —— 它同时被当成"要不要放弃解码"的判据,
///   判宽了会让过期请求白占解码槽, 判窄了会把用户正要看的那张判死(画面停在旧图上)。
/// </summary>
public class PreviewPreloadTests
{
    [Fact]
    public void 单张时没有可预取的邻居()
    {
        Assert.Empty(PreviewPreload.Order(1, 0));
        Assert.Empty(PreviewPreload.Order(0, 0));
    }

    [Fact]
    public void 中间位置按下一张优先交错给出偏移()
    {
        // Ahead=2 / Behind=1
        Assert.Equal(new[] { 1, -1, 2 }, PreviewPreload.Order(9, 2));
    }

    [Fact]
    public void 首张没有上一张_末张没有下一张()
    {
        Assert.Equal(new[] { 1, 2 }, PreviewPreload.Order(5, 0));
        Assert.Equal(new[] { -1 }, PreviewPreload.Order(5, 4));
    }

    [Fact]
    public void 靠近首尾时只给出不越界的那些()
    {
        // 下标 1: 上一张(0)还在, 再往前(-1)没有了
        Assert.Equal(new[] { 1, -1, 2 }, PreviewPreload.Order(9, 1));
        // 下标 7(倒数第二): 下一张(8)在, 再下一张(9)没有; 上一张(6)在
        Assert.Equal(new[] { 1, -1 }, PreviewPreload.Order(9, 7));
    }

    [Fact]
    public void 任何组大小与下标都只给出范围内的偏移且不重复()
    {
        for (var count = 1; count <= 12; count++)
        {
            for (var index = 0; index < count; index++)
            {
                var seen = new System.Collections.Generic.HashSet<int>();
                foreach (var off in PreviewPreload.Order(count, index))
                {
                    Assert.NotEqual(0, off);
                    Assert.True(seen.Add(off), $"偏移重复: count={count} index={index} off={off}");

                    var target = index + off;
                    Assert.InRange(target, 0, count - 1);
                }
            }
        }
    }

    [Fact]
    public void 越界的组参数返回空而不是抛异常()
    {
        Assert.Empty(PreviewPreload.Order(5, -1));
        Assert.Empty(PreviewPreload.Order(5, 5));
    }

    [Fact]
    public void 窗口内的邻居都算还要()
    {
        // 当前第 4 张(下标 3), 窗口 = 3 + {+1,+2,-1}
        Assert.True(PreviewPreload.IsWanted(9, 3, 3));
        Assert.True(PreviewPreload.IsWanted(9, 3, 4));
        Assert.True(PreviewPreload.IsWanted(9, 3, 5));
        Assert.True(PreviewPreload.IsWanted(9, 3, 2));
    }

    [Fact]
    public void 窗口外的邻居算没人要()
    {
        // 下标 6 是 +3(超出 Ahead=2), 下标 1 是 -2(超出 Behind=1)
        Assert.False(PreviewPreload.IsWanted(9, 3, 6));
        Assert.False(PreviewPreload.IsWanted(9, 3, 1));
        Assert.False(PreviewPreload.IsWanted(9, 3, 0));
        Assert.False(PreviewPreload.IsWanted(9, 3, 8));
    }

    [Fact]
    public void 首尾处的窗口按实际存在的邻居算()
    {
        // 首张: 没有上一张, 左右两侧越界的不算"还要"
        Assert.True(PreviewPreload.IsWanted(5, 0, 1));
        Assert.True(PreviewPreload.IsWanted(5, 0, 2));
        Assert.False(PreviewPreload.IsWanted(5, 0, 3));

        // 末张: 只留 -1
        Assert.True(PreviewPreload.IsWanted(5, 4, 3));
        Assert.False(PreviewPreload.IsWanted(5, 4, 2));
    }

    [Fact]
    public void 越界的目标一律返回false()
    {
        Assert.False(PreviewPreload.IsWanted(5, 2, -1));
        Assert.False(PreviewPreload.IsWanted(5, 2, 5));
        Assert.False(PreviewPreload.IsWanted(0, 0, 0));
        Assert.False(PreviewPreload.IsWanted(5, -1, 0));
    }
}
