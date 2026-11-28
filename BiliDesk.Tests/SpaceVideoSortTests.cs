using System.Collections.Generic;
using BiliDesk.Helpers;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// UP 主主页投稿排序(2026-11-28, issue #131)。
///
/// ★ 这里钉死两类东西:
///   ① 枚举 ↔ 接口 order 字符串的映射 —— 写错一个字母, 接口**不报错**, 而是静默按
///      pubdate 返回, 表现成"点了最多播放但顺序没变", 光看界面根本查不出来;
///   ② 降级路径的本地兜底排序 —— 那条路只能排当前页, 且"最多收藏"拿不到收藏数,
///      必须退化成按播放量排(而不是什么都不排)。
/// </summary>
public class SpaceVideoSortTests
{
    // ------------------------------------------------ 映射

    [Theory]
    [InlineData(SpaceVideoOrder.Latest, "pubdate")]
    [InlineData(SpaceVideoOrder.MostPlayed, "click")]
    [InlineData(SpaceVideoOrder.MostFavorited, "stow")]
    public void 排序枚举映射到接口order参数(SpaceVideoOrder order, string expected)
        => Assert.Equal(expected, SpaceVideoSort.ToApiOrder(order));

    [Theory]
    [InlineData(SpaceVideoOrder.Latest, "最新发布")]
    [InlineData(SpaceVideoOrder.MostPlayed, "最多播放")]
    [InlineData(SpaceVideoOrder.MostFavorited, "最多收藏")]
    public void 排序枚举有界面文案(SpaceVideoOrder order, string expected)
        => Assert.Equal(expected, SpaceVideoSort.Label(order));

    [Fact]
    public void 界面上的排序项包含三档且默认在最前()
    {
        Assert.Equal(new[]
        {
            SpaceVideoOrder.Latest,
            SpaceVideoOrder.MostPlayed,
            SpaceVideoOrder.MostFavorited,
        }, SpaceVideoSort.All);
    }

    [Theory]
    [InlineData(0, SpaceVideoOrder.Latest)]
    [InlineData(1, SpaceVideoOrder.MostPlayed)]
    [InlineData(2, SpaceVideoOrder.MostFavorited)]
    [InlineData(-1, SpaceVideoOrder.Latest)]   // 配置/脏值 → 回落默认
    [InlineData(99, SpaceVideoOrder.Latest)]
    public void 越界的排序值回落最新发布(int raw, SpaceVideoOrder expected)
        => Assert.Equal(expected, SpaceVideoSort.Normalize(raw));

    // ------------------------------------------------ 降级路径的本地兜底排序

    private static VideoItem V(string bvid, long play, long pubdate)
        => new() { Bvid = bvid, ViewCount = play, Pubdate = pubdate };

    [Fact]
    public void 降级时按播放量降序()
    {
        var items = new List<VideoItem>
        {
            V("BV3", 300, 1_700_000_003),
            V("BV1", 100, 1_700_000_001),
            V("BV2", 900, 1_700_000_002),
        };

        SpaceVideoSort.LocalSort(items, SpaceVideoOrder.MostPlayed);

        Assert.Equal(new[] { "BV2", "BV3", "BV1" }, items.ConvertAll(x => x.Bvid));
    }

    /// <summary>
    /// 降级接口不下发收藏数 ⇒ "最多收藏"退化成按播放量排。
    /// ★ 这条是**刻意的降级**而不是 bug: 界面上会同时显示"收藏数不可用, 暂按播放量排列"。
    ///   哪天模型里补上了收藏数字段, 这里应该改成按收藏数排, 并删掉提示文案。
    /// </summary>
    [Fact]
    public void 降级时最多收藏退化成按播放量降序()
    {
        var items = new List<VideoItem>
        {
            V("BV3", 300, 0),
            V("BV1", 100, 0),
            V("BV2", 900, 0),
        };

        SpaceVideoSort.LocalSort(items, SpaceVideoOrder.MostFavorited);

        Assert.Equal(new[] { "BV2", "BV3", "BV1" }, items.ConvertAll(x => x.Bvid));
        Assert.True(SpaceVideoSort.IsApproximateInFallback(SpaceVideoOrder.MostFavorited));
        Assert.False(SpaceVideoSort.IsApproximateInFallback(SpaceVideoOrder.MostPlayed));
        Assert.False(SpaceVideoSort.IsApproximateInFallback(SpaceVideoOrder.Latest));
    }

    [Fact]
    public void 降级时最新发布按时间降序()
    {
        var items = new List<VideoItem>
        {
            V("BV1", 999, 1_700_000_001),
            V("BV3", 1, 1_700_000_003),
            V("BV2", 50, 1_700_000_002),
        };

        SpaceVideoSort.LocalSort(items, SpaceVideoOrder.Latest);

        Assert.Equal(new[] { "BV3", "BV2", "BV1" }, items.ConvertAll(x => x.Bvid));
    }

    /// <summary>
    /// 播放量相同时按 bvid 兜底 —— List.Sort 不是稳定排序, 没有第二键时同一份数据
    /// 每次刷新可能给出不同顺序, 用户会看到"没动任何东西卡片却换位了"。
    /// </summary>
    [Fact]
    public void 播放量相同时按bvid兜底_顺序稳定()
    {
        var items = new List<VideoItem>
        {
            V("BV2", 500, 0),
            V("BV1", 500, 0),
            V("BV3", 500, 0),
        };

        SpaceVideoSort.LocalSort(items, SpaceVideoOrder.MostPlayed);

        Assert.Equal(new[] { "BV1", "BV2", "BV3" }, items.ConvertAll(x => x.Bvid));
    }

    [Fact]
    public void 空列表与单条不炸()
    {
        var empty = new List<VideoItem>();
        SpaceVideoSort.LocalSort(empty, SpaceVideoOrder.MostPlayed);
        Assert.Empty(empty);

        var one = new List<VideoItem> { V("BV1", 1, 1) };
        SpaceVideoSort.LocalSort(one, SpaceVideoOrder.MostFavorited);
        Assert.Single(one);
    }
}
