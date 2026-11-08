using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// RecommendSeenStore 是**静态类**, 用例之间会共享状态 —— 若与其它测试类并行跑,
/// 别人往同一个集合里写就会让"计数/淘汰"这类断言随机失败。
/// 单独成组(DisableParallelization)是这里唯一可靠的隔离方式。
/// </summary>
[CollectionDefinition(nameof(RecommendSeenStoreTests), DisableParallelization = true)]
public class RecommendSeenStoreCollection
{
}

/// <summary>
/// 推荐流「已展示」记录(2026-11-08)。
///
/// ★ 它挡的是"刷新一次，刚看过的 30 条又原样回来"——服务端的推荐池会滚动,
///   相邻批次重叠是常态, 而 VideoList.AppendDistinct 只管当前列表, 刷新清空后就失效了。
/// 注意: 这是**静态类**, 用例之间会共享状态, 所以每个用例开头都要 Clear。
/// </summary>
[Collection(nameof(RecommendSeenStoreTests))]
public class RecommendSeenStoreTests
{
    public RecommendSeenStoreTests() => RecommendSeenStore.Clear();

    [Fact]
    public void 标记过的条目才算已展示()
    {
        Assert.False(RecommendSeenStore.Contains("BV1xx411c7mD"));

        RecommendSeenStore.Mark("BV1xx411c7mD");

        Assert.True(RecommendSeenStore.Contains("BV1xx411c7mD"));
        Assert.False(RecommendSeenStore.Contains("BV1yy411c7mD"));
    }

    [Fact]
    public void 重复标记不会让计数膨胀()
    {
        RecommendSeenStore.Mark("BV1xx411c7mD");
        RecommendSeenStore.Mark("BV1xx411c7mD");
        RecommendSeenStore.Mark("BV1xx411c7mD");

        Assert.Equal(1, RecommendSeenStore.Count);
    }

    [Fact]
    public void 空bvid一律忽略()
    {
        RecommendSeenStore.Mark("");
        RecommendSeenStore.Mark(null!);

        Assert.Equal(0, RecommendSeenStore.Count);
        Assert.False(RecommendSeenStore.Contains(""));
    }

    [Fact]
    public void 超出上限时淘汰最早记的()
    {
        // 上限 800: 记 900 条, 最早的 100 条应该被挤掉
        for (var i = 0; i < 900; i++) RecommendSeenStore.Mark($"BV{i}");

        Assert.Equal(800, RecommendSeenStore.Count);
        Assert.False(RecommendSeenStore.Contains("BV0"));    // 最早的被淘汰
        Assert.True(RecommendSeenStore.Contains("BV899"));   // 最新的还在
    }

    [Fact]
    public void 清空后不再命中()
    {
        RecommendSeenStore.Mark("BV1xx411c7mD");

        RecommendSeenStore.Clear();

        Assert.Equal(0, RecommendSeenStore.Count);
        Assert.False(RecommendSeenStore.Contains("BV1xx411c7mD"));
    }
}
