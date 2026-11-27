using System.Collections.Generic;
using BiliDesk.Helpers;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 影视页「正在热播」的合并规则(2026-11-28 抽成纯函数)。
///
/// 这段逻辑的错法都很安静: 全是电影、重复出现、少一条或多一条 ——
/// 看着像接口数据问题, 所以逐条钉住顺序与去重。
/// </summary>
public class MediaMergeTests
{
    private static BangumiItem Item(long id) => new() { SeasonId = id, Title = "s" + id };

    private static IReadOnlyList<BangumiItem> Row(params long[] ids)
    {
        var list = new List<BangumiItem>();
        foreach (var id in ids) list.Add(Item(id));
        return list;
    }

    [Fact]
    public void Interleave_按轮转顺序交替合并()
    {
        var merged = MediaMerge.Interleave(
            new[] { Row(1, 2), Row(3, 4), Row(5, 6) }, take: 6);

        Assert.Equal(new long[] { 1, 3, 5, 2, 4, 6 },
            merged.ConvertAll(x => x.SeasonId));
    }

    [Fact]
    public void Interleave_按SeasonId去重()
    {
        var merged = MediaMerge.Interleave(
            new[] { Row(1, 2), Row(2, 3) }, take: 10);

        Assert.Equal(new long[] { 1, 2, 3 }, merged.ConvertAll(x => x.SeasonId));
    }

    [Fact]
    public void Interleave_某一组先耗尽时其余组继续补()
    {
        var merged = MediaMerge.Interleave(
            new[] { Row(1), Row(2, 3, 4) }, take: 4);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, merged.ConvertAll(x => x.SeasonId));
    }

    [Fact]
    public void Interleave_空组不会打断其它组()
    {
        var merged = MediaMerge.Interleave(
            new[] { Row(), Row(2, 3), Row() }, take: 2);

        Assert.Equal(new long[] { 2, 3 }, merged.ConvertAll(x => x.SeasonId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Interleave_take非正数返回空(int take)
        => Assert.Empty(MediaMerge.Interleave(new[] { Row(1) }, take));

    [Fact]
    public void Interleave_取够take就停()
    {
        var merged = MediaMerge.Interleave(
            new[] { Row(1, 2, 3), Row(4, 5, 6) }, take: 3);

        Assert.Equal(new long[] { 1, 4, 2 }, merged.ConvertAll(x => x.SeasonId));
    }

    [Fact]
    public void Interleave_跳过非法SeasonId()
    {
        var merged = MediaMerge.Interleave(
            new[] { new List<BangumiItem> { new() { SeasonId = 0 }, Item(7) } }, take: 5);

        Assert.Equal(new long[] { 7 }, merged.ConvertAll(x => x.SeasonId));
    }
}
