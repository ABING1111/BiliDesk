using System.Collections.Generic;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// App 端推荐流游标(app.bilibili.com/x/v2/feed/index)的纯逻辑。
///
/// ★ 钉三条不变量:
///   1. 刷新 = pull=true 且不带 idx; 加载更多 = pull=false 且带 idx;
///   2. 一批里取**最小** idx(推荐池最深处), 不是最大;
///   3. 失败/空批次不推进游标 —— 否则会跳过一段永远补不回来的内容。
/// </summary>
public class AppRecommendCursorTests
{
    [Fact]
    public void 初始状态_刷新语义_没有游标()
    {
        var c = new AppRecommendCursor();

        Assert.True(c.Pull);
        Assert.Equal(0, c.Idx);

        var ps = new Dictionary<string, string>();
        c.ApplyTo(ps);
        Assert.Equal("true", ps["pull"]);
        Assert.False(ps.ContainsKey("idx"));
    }

    [Fact]
    public void 加载更多_保留游标_pull为false()
    {
        var c = new AppRecommendCursor();
        c.OnLoaded(new List<long> { 900, 890, 880 });

        c.BeginLoadMore();

        Assert.False(c.Pull);
        Assert.Equal(880, c.Idx);

        var ps = new Dictionary<string, string>();
        c.ApplyTo(ps);
        Assert.Equal("false", ps["pull"]);
        Assert.Equal("880", ps["idx"]);
    }

    [Fact]
    public void 没有游标时请求加载更多_退回刷新语义()
    {
        var c = new AppRecommendCursor();

        c.BeginLoadMore();

        // pull=false 且无 idx 是实测"窗口不动"的组合; 没有游标时必须仍然从池子最前面取。
        Assert.True(c.Pull);
        Assert.Equal(0, c.Idx);
    }

    [Fact]
    public void 刷新_清空游标_从推荐池最前面重新取()
    {
        var c = new AppRecommendCursor();
        c.OnLoaded(new List<long> { 900, 890 });
        c.BeginLoadMore();

        c.BeginRefresh();

        Assert.True(c.Pull);
        Assert.Equal(0, c.Idx);
    }

    [Fact]
    public void 成功批次_取最小正idx而不是最大()
    {
        var c = new AppRecommendCursor();

        // idx 在一批里从大到小排; 服务端语义是"给我 idx 小于这个值的内容",
        // 所以游标必须取最深(最小)的那个, 下一批才真的前进 10 条。
        c.OnLoaded(new List<long> { 1000, 990, 980, 970 });

        Assert.Equal(970, c.Idx);
    }

    [Fact]
    public void 空批次或非法idx_不推进游标()
    {
        var c = new AppRecommendCursor();
        c.OnLoaded(new List<long> { 500 });

        c.OnLoaded(new List<long>());
        Assert.Equal(500, c.Idx);

        c.OnLoaded(new List<long> { 0, -3 });
        Assert.Equal(500, c.Idx);
    }

    [Fact]
    public void 只归位_回到刷新语义()
    {
        var c = new AppRecommendCursor();
        c.OnLoaded(new List<long> { 500 });
        c.BeginLoadMore();

        c.Rewind();

        Assert.True(c.Pull);
        Assert.Equal(0, c.Idx);
    }
}
