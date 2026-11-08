using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliDesk.Helpers;

/// <summary>
/// 首页推荐流(`wbi/index/top/feed/rcmd`)的**翻页游标**。
///
/// ★★ 为什么要有它(2026-11-08): 旧实现把 `fresh_idx / fresh_idx_1h / brush` 全写死成 1,
///   等于每次请求都在说"我第一次打开首页"。官方网页端是靠这几个参数接力翻页的:
///     · fresh_idx / fresh_idx_1h —— 当前翻页号, 从 1 开始, 每次成功请求后 +1;
///     · brush —— "刷子", 刷新时 +1(翻页不动它), 用来区分"同一轮浏览"与"换了一批";
///     · fetch_row —— 已经抓取到第几行(1 + 累计行数), 配合 y_num 让服务端知道读到哪了;
///     · last_showlist —— 上一屏已经展示过的 `av_{aid}`, 服务端据此避开重复。
///
/// ★★★ 但**不要**把"内容重复"当成加游标的理由 —— 实测(探针 bd-probe-rcmd)服务端本身
///   就在轮换推荐池: 用写死第 1 页的旧参数连打 20 次(560 条), 结果全部唯一、相邻页零重复。
///   游标的真正价值是"让服务端知道读到哪了"(往深处推)与 last_showlist(主动避重),
///   而不是"修一个重复 bug"—— 那个 bug 并不存在。
///
/// ★ 本类是**纯逻辑**(不碰网络、不碰 UI), 翻页状态机全部可以单测:
///   失败时**不要**调用 <see cref="OnLoaded"/>, 游标原地不动, 重试复用同一组参数 ——
///   这是"失败一次就跳页, 跳过的内容永远补不回来"的防线。
/// </summary>
public sealed class RecommendCursor
{
    /// <summary>单页条数。接口硬上限 30(见 ApiClient.GetWebRecommendCoreAsync)。</summary>
    public const int PageSize = 30;

    /// <summary>
    /// 普通列数。官方前端默认 4, 与 <see cref="PageSize"/> 一起决定"这一页占几行",
    /// 进而决定 fetch_row 该加多少。
    /// </summary>
    public const int YNum = 4;

    /// <summary>last_showlist 最多带多少条(官方实测 12 条, 再多对服务端没有额外意义)。</summary>
    public const int LastShowMax = 12;

    /// <summary>当前翻页号, 从 1 开始。</summary>
    public int FreshIdx { get; private set; } = 1;

    /// <summary>"一小时前"那一档翻页号, 与 <see cref="FreshIdx"/> 同步推进。</summary>
    public int FreshIdx1h { get; private set; } = 1;

    /// <summary>刷新次数(刷子), 刷新时 +1, 翻页不动。</summary>
    public int Brush { get; private set; } = 1;

    /// <summary>已抓取到第几行(1 + 累计行数)。</summary>
    public int FetchRow { get; private set; } = 1;

    /// <summary>本次运行的会话 id(官方前端也是随机生成、整轮不变)。</summary>
    public string UniqId { get; }

    private readonly List<long> _lastShow = new();

    /// <summary>上一屏展示过的 aid(按展示顺序, 最多 <see cref="LastShowMax"/> 条)。</summary>
    public IReadOnlyList<long> LastShow => _lastShow;

    /// <summary>拼成接口要的 `av_123,av_456` 形式; 没有时返回空串。</summary>
    public string LastShowList => string.Join(",", _lastShow.Select(a => "av_" + a));

    /// <param name="uniqId">仅供测试注入固定值; 正常运行传 null 即可。</param>
    public RecommendCursor(string? uniqId = null)
    {
        UniqId = string.IsNullOrEmpty(uniqId)
            ? Random.Shared.NextInt64(100_000_000_000L, 999_999_999_999L).ToString()
            : uniqId!;
    }

    /// <summary>
    /// 刷新: 换一批。刷子 +1(让服务端知道"这是新的一轮"), 翻页号与行号归 1,
    /// 清掉上一屏清单(旧内容对新一轮没有参考价值)。
    /// </summary>
    public void Reset()
    {
        Brush++;
        Rewind();
    }

    /// <summary>
    /// 只把翻页位置归 1, **不动刷子**。
    ///
    /// ★ 用于"换了账号"这类场景(见 ApiClient.ResetRecommendCursor): 新账号要从第 1 页读起,
    ///   但"换一批"这个动作由紧随其后的那次请求(reset: true)自己完成 ——
    ///   两处都推进刷子的话, 一次登录切换会让刷子跳 2, 与服务端"刷新一次 +1"的语义对不上。
    /// </summary>
    public void Rewind()
    {
        FreshIdx = 1;
        FreshIdx1h = 1;
        FetchRow = 1;
        _lastShow.Clear();
    }

    /// <summary>
    /// 一次**成功**请求之后推进游标。失败时不要调用(见类注释)。
    /// </summary>
    /// <param name="shownAids">本次真正拿到的视频 aid(用于 last_showlist 与行数累计)。</param>
    /// <param name="serverFreshIdx">
    /// 响应 `data.userfeature` 里回显的 fresh_idx(0 = 没给)。
    /// ★ 以服务端回显为准: 它才是"服务端认为你读到第几页", 本地自增只是兜底。
    /// </param>
    /// <param name="serverFreshIdx1h">同上, fresh_idx_1h。</param>
    public void OnLoaded(IReadOnlyList<long> shownAids, int serverFreshIdx = 0, int serverFreshIdx1h = 0)
    {
        var count = shownAids?.Count ?? 0;
        if (count > 0)
            FetchRow += Math.Max(1, (int)Math.Ceiling(count / (double)YNum));

        FreshIdx = NextIndex(FreshIdx, serverFreshIdx);
        FreshIdx1h = NextIndex(FreshIdx1h, serverFreshIdx1h);

        _lastShow.Clear();
        if (shownAids == null) return;
        foreach (var aid in shownAids)
        {
            if (aid <= 0) continue;
            if (_lastShow.Count >= LastShowMax) break;
            _lastShow.Add(aid);
        }
    }

    /// <summary>
    /// 下一轮页码: 服务端给了就用 `服务端 + 1`, 否则本地 +1。
    /// 两种都**不允许倒退** —— 服务端偶尔回一个比本地小的值(风控/缓存)时,
    /// 倒退等于把刚翻过的几页重看一遍。
    /// </summary>
    private static int NextIndex(int current, int server)
    {
        var next = server > 0 ? server + 1 : current + 1;
        return next > current ? next : current + 1;
    }

    /// <summary>拼出本次请求要带的参数(签名由 ApiClient 统一加 wts/w_rid)。</summary>
    public Dictionary<string, string> BuildQuery()
    {
        var ps = new Dictionary<string, string>
        {
            ["fresh_type"] = "4",
            ["ps"] = PageSize.ToString(),
            ["fresh_idx"] = FreshIdx.ToString(),
            ["fresh_idx_1h"] = FreshIdx1h.ToString(),
            ["brush"] = Brush.ToString(),
            ["fetch_row"] = FetchRow.ToString(),
            // 官方网页端现行版本号; 缺了它服务端会按老版首页的推荐池下发。
            ["feed_version"] = "V8",
            ["homepage_ver"] = "1",
            ["y_num"] = YNum.ToString(),
            ["last_y_num"] = (YNum + 1).ToString(),
            ["uniq_id"] = UniqId,
            ["web_location"] = "1430650"
        };

        var lastShow = LastShowList;
        if (lastShow.Length > 0) ps["last_showlist"] = lastShow;
        return ps;
    }
}
