using System.Collections.Generic;
using System.Linq;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 首页推荐流翻页游标(2026-11-08 修「加载更多等于没加载」)。
///
/// ★ 为什么钉这些用例: 旧实现把 fresh_idx / brush 全写死成 1, 于是每次「加载更多」
///   都在告诉服务端"我第一次打开首页" —— 返回的还是同一批内容。
///   这套状态机的三条不变量任何一条被改坏, 都会悄悄退回那个 bug(接口照常返回 200,
///   只是内容重复, 没有任何报错), 所以只能靠单测守。
/// </summary>
public class RecommendCursorTests
{
    private static List<long> Aids(int from, int count)
        => Enumerable.Range(from, count).Select(i => (long)i).ToList();

    [Fact]
    public void 初始状态_从第一页开始()
    {
        var c = new RecommendCursor("u1");

        Assert.Equal(1, c.FreshIdx);
        Assert.Equal(1, c.FreshIdx1h);
        Assert.Equal(1, c.Brush);
        Assert.Equal(1, c.FetchRow);
        Assert.Equal("", c.LastShowList);
    }

    [Fact]
    public void 刷新_换一批_刷子加一_页码归位_清空已展示清单()
    {
        var c = new RecommendCursor("u1");
        c.OnLoaded(Aids(1, 30));

        c.Reset();

        Assert.Equal(2, c.Brush);        // 刷子推进 → 服务端知道这是新的一轮
        Assert.Equal(1, c.FreshIdx);     // 页码归 1
        Assert.Equal(1, c.FreshIdx1h);
        Assert.Equal(1, c.FetchRow);
        Assert.Equal("", c.LastShowList);
    }

    [Fact]
    public void 只归位_不动刷子()
    {
        var c = new RecommendCursor("u1");
        c.OnLoaded(Aids(1, 30));

        c.Rewind();

        // 换账号这类场景: 位置归 1, 但"换一批"留给紧随其后的 reset: true 请求自己做,
        // 两处都推进刷子会让它一次跳 2, 与服务端"刷新一次 +1"的语义对不上。
        Assert.Equal(1, c.Brush);
        Assert.Equal(1, c.FreshIdx);
        Assert.Equal("", c.LastShowList);
    }

    [Fact]
    public void 连续加载更多_页码与行号持续推进()
    {
        var c = new RecommendCursor("u1");

        c.OnLoaded(Aids(1, 30));
        Assert.Equal(2, c.FreshIdx);
        // 30 条 / 每行 4 条 ⇒ 8 行; 1 + 8 = 9
        Assert.Equal(9, c.FetchRow);

        c.OnLoaded(Aids(100, 30));
        Assert.Equal(3, c.FreshIdx);
        Assert.Equal(17, c.FetchRow);
        // 翻页不动刷子 —— 动了就等于每页都"换一批", 服务端会当作用户刷新
        Assert.Equal(1, c.Brush);
    }

    [Fact]
    public void 失败不推进游标_重试复用同一组参数()
    {
        var c = new RecommendCursor("u1");
        c.OnLoaded(Aids(1, 30));

        var before = c.BuildQuery();
        // 失败路径**不调用** OnLoaded —— 游标必须原地不动
        var after = c.BuildQuery();

        Assert.Equal(before["fresh_idx"], after["fresh_idx"]);
        Assert.Equal(before["fetch_row"], after["fetch_row"]);
        Assert.Equal(before["brush"], after["brush"]);
    }

    [Fact]
    public void 采用服务端回显的页码()
    {
        var c = new RecommendCursor("u1");

        // 服务端说"你已经读到第 5 页了" ⇒ 下一轮从 6 开始
        c.OnLoaded(Aids(1, 30), serverFreshIdx: 5, serverFreshIdx1h: 5);

        Assert.Equal(6, c.FreshIdx);
        Assert.Equal(6, c.FreshIdx1h);
    }

    [Fact]
    public void 服务端回显比本地小_不允许倒退()
    {
        var c = new RecommendCursor("u1");
        c.OnLoaded(Aids(1, 30));      // 本地到 2
        c.OnLoaded(Aids(100, 30));    // 本地到 3

        // 服务端偶尔回一个更小的值(风控/缓存)。倒退等于把翻过的几页重看一遍。
        c.OnLoaded(Aids(200, 30), serverFreshIdx: 1);

        Assert.Equal(4, c.FreshIdx);
    }

    [Fact]
    public void 已展示清单_最多十二条_且只保留最近的()
    {
        var c = new RecommendCursor("u1");

        c.OnLoaded(Aids(1, 30));

        Assert.Equal(RecommendCursor.LastShowMax, c.LastShow.Count);
        // 取的是这一批的**前** 12 条(与官方前端一致)
        Assert.Equal(1, c.LastShow[0]);
        Assert.Equal(12, c.LastShow[^1]);
        Assert.StartsWith("av_1,av_2", c.LastShowList);
    }

    [Fact]
    public void 已展示清单_跳过非法aid()
    {
        var c = new RecommendCursor("u1");

        c.OnLoaded(new List<long> { 0, -5, 7 });

        Assert.Single(c.LastShow);
        Assert.Equal("av_7", c.LastShowList);
    }

    [Fact]
    public void 查询参数_包含官方网页端那一整套()
    {
        var c = new RecommendCursor("uniq123");
        var ps = c.BuildQuery();

        foreach (var key in new[]
                 {
                     "fresh_type", "ps", "fresh_idx", "fresh_idx_1h", "brush", "fetch_row",
                     "feed_version", "homepage_ver", "y_num", "last_y_num", "uniq_id", "web_location"
                 })
        {
            Assert.True(ps.ContainsKey(key), $"缺少参数 {key}");
        }

        Assert.Equal("V8", ps["feed_version"]);
        Assert.Equal(RecommendCursor.PageSize.ToString(), ps["ps"]);
        Assert.Equal("uniq123", ps["uniq_id"]);
        // 第一页还没有上一屏可报
        Assert.False(ps.ContainsKey("last_showlist"));
    }

    [Fact]
    public void 查询参数_第二页带上上一屏清单()
    {
        var c = new RecommendCursor("uniq123");
        c.OnLoaded(Aids(1, 30));

        var ps = c.BuildQuery();

        Assert.Equal("2", ps["fresh_idx"]);
        Assert.True(ps.ContainsKey("last_showlist"));
        Assert.StartsWith("av_1,av_2", ps["last_showlist"]);
    }

    [Fact]
    public void 空批次_页码仍推进_但行号不变()
    {
        var c = new RecommendCursor("u1");

        // 服务端返回成功但列表为空(翻到底)。行号不该乱加, 页码要往前走,
        // 否则下一次请求会永远停在同一页、反复拿到空列表。
        c.OnLoaded(new List<long>());

        Assert.Equal(2, c.FreshIdx);
        Assert.Equal(1, c.FetchRow);
        Assert.Equal("", c.LastShowList);
    }

    [Fact]
    public void 未注入uniqId时_自行生成且整轮不变()
    {
        var c = new RecommendCursor();

        Assert.False(string.IsNullOrEmpty(c.UniqId));
        var first = c.BuildQuery()["uniq_id"];
        c.Reset();
        Assert.Equal(first, c.BuildQuery()["uniq_id"]);
    }
}
