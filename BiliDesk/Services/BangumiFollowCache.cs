using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BiliDesk.Services;

/// <summary>
/// 「这一季我追了吗」的查询与缓存(2026-11-19, 2026-11-28 扩展追剧)。
///
/// ★★★ 为什么不能直接用详情接口的 <c>user_status.follow</c>:
///   2026-11-19 实测(带完整登录 Cookie, 同一账号):
///     · 追番接口返回 code=0, 「我的追番」列表里**立刻**出现了该番;
///     · 而 <c>pgc/view/web/season</c> 的 <c>user_status.follow</c> **始终是 0**
///       (连 follow_status 也是 0), 隔几分钟再查仍然是 0。
///   ⇒ 拿它当"已追番"的判据会永远显示「+ 追番」—— 这正是用户报的
///     "已追番的番剧再点开还是显示追番"。
///
/// ★ 目前唯一可靠的判据: <c>/x/space/bangumi/follow/list</c> 里有没有这一季 ——
///   实测追番后立刻能查到、取消后立刻消失。
///   该接口**不支持按 season_id 过滤**(带上 season_id 也照样返回整页, 2026-11-19 实测),
///   所以只能把整份列表拉下来在本地查。
///
/// ★★ 2026-11-28 影视页新增后**必须按类型分开缓存**:
///   同一个接口的 <c>type=1</c> 是追番、<c>type=2</c> 是追剧(电影/电视剧/纪录片/综艺),
///   两份列表互不包含。以前只查 type=1, 于是追过的电影在详情页永远显示"未追"。
///   现在两个快照各存一份, 由调用方按详情页的 season_type 选择查哪一份。
///
/// ★ 缓存: 详情页会被反复打开, 每次都翻一遍页是不可接受的。这里把整份 id 集合缓存
///   <see cref="TtlMs"/>, 追番/追剧成功时**就地更新**它。
/// ★ 拉取失败**不覆盖**旧快照: 一次网络抖动不该被固化成"没追番", 下次调用会重试。
/// </summary>
public sealed class BangumiFollowCache
{
    public static BangumiFollowCache Instance { get; } = new();

    /// <summary>缓存有效期。追番/追剧状态变化不频繁, 3 分钟足够挡住"进进出出详情页"的重复请求。</summary>
    private const int TtlMs = 3 * 60 * 1000;

    /// <summary>最多翻几页(30/页 ⇒ 300 部)。再往上就不再找了: 宁可显示"未追", 也别无限翻页。</summary>
    private const int MaxPages = 10;

    private const int PageSize = 30;

    /// <summary>一份"我的追番 / 我的追剧"快照。</summary>
    private sealed class Snapshot
    {
        public long Mid;
        public HashSet<long>? Ids;
        public long FetchedAt;
        public Task? Inflight;
    }

    private readonly object _gate = new();
    private readonly Snapshot _bangumi = new();
    private readonly Snapshot _teleplay = new();

    /// <summary>
    /// 快照代次。追番/取消追番成功后 +1 —— 用来**作废在途的那次拉取**,
    /// 否则会出现这个竞态: 详情页刚打开(拉取在飞) → 用户立刻点追番 → 拉取结果回来,
    /// 里面还没有这次追番 ⇒ 刚追的番又变回"未追"。
    /// </summary>
    private int _generation;

    /// <summary>
    /// 查这一季在不在「我的追番 / 我的追剧」里。
    /// </summary>
    /// <param name="teleplay">
    /// true = 查追剧列表(type=2, 电影/电视剧/纪录片/综艺);
    /// false = 查追番列表(type=1)。
    /// </param>
    /// ★ 未登录直接 false 且**不发请求**: 未登录时那个接口本来就回 53013(隐私设置未公开)。
    public async Task<bool> IsFollowedAsync(long seasonId, bool teleplay = false)
    {
        if (seasonId <= 0) return false;
        if (!Svc.Session.HasLogin) return false;

        var mid = CurrentMid();
        if (mid <= 0) return false;

        var snapshot = teleplay ? _teleplay : _bangumi;
        await EnsureFreshAsync(snapshot, mid, teleplay ? 2 : 1);
        lock (_gate) return snapshot.Ids?.Contains(seasonId) == true;
    }

    /// <summary>
    /// 追番/取消追番成功后同步本地快照 —— 这样立刻回到详情页不必再等一次网络。
    /// ★ 同时作废在途拉取并把 <c>FetchedAt</c> 清零: 下一次查询会**重新跟服务端对齐**
    ///   (本地这一笔只是过渡值, 服务端才是权威)。
    /// ★ 快照还没建过就只作废在途拉取: 让下一次查询自己拉, 别在这里凭空造一个不完整的集合
    ///   (只塞进一条会让其余真追过的番全变成"未追")。
    /// </summary>
    public void SetFollowed(long seasonId, bool followed, bool teleplay = false)
    {
        if (seasonId <= 0) return;
        var snapshot = teleplay ? _teleplay : _bangumi;
        lock (_gate)
        {
            _generation++;          // 作废在途的那次拉取(见 _generation 的说明)
            snapshot.FetchedAt = 0; // 让下一次查询重新对齐服务端
            if (snapshot.Ids == null) return;
            if (followed) snapshot.Ids.Add(seasonId);
            else snapshot.Ids.Remove(seasonId);
        }
    }

    private Task EnsureFreshAsync(Snapshot snapshot, long mid, int type)
    {
        lock (_gate)
        {
            if (snapshot.Ids != null && snapshot.Mid == mid
                && Environment.TickCount64 - snapshot.FetchedAt < TtlMs)
                return Task.CompletedTask;
            if (snapshot.Inflight != null && snapshot.Mid == mid) return snapshot.Inflight;

            snapshot.Mid = mid;
            snapshot.Inflight = FetchAsync(snapshot, mid, type, _generation);
            return snapshot.Inflight;
        }
    }

    private async Task FetchAsync(Snapshot snapshot, long mid, int type, int generation)
    {
        var ids = new HashSet<long>();
        var gotAny = false;
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var (ok, _, items, _) = await Svc.Api.GetMyBangumiPageAsync(mid, type, page, PageSize);
                if (!ok || items == null) break;
                gotAny = true;
                foreach (var it in items)
                    if (it.SeasonId > 0) ids.Add(it.SeasonId);
                if (items.Count < PageSize) break;   // 已经是最后一页
            }
        }
        finally
        {
            lock (_gate)
            {
                snapshot.Inflight = null;
                // ★ 拉取期间用户追过/取消过(_generation 变了) ⇒ 这份结果已经过时, 直接丢弃;
                //   SetFollowed 已把 FetchedAt 清零, 下一次查询会重新拉。
                if (generation == _generation && gotAny)
                {
                    snapshot.Ids = ids;
                    snapshot.FetchedAt = Environment.TickCount64;
                }
                // 一页都没拿到 ⇒ 保留旧快照, 下次重试。
            }
        }
    }

    /// <summary>当前登录用户 mid(DedeUserID)。取不到就是 0 ⇒ 调用方按"未登录"处理。</summary>
    private static long CurrentMid()
        => long.TryParse(Svc.Session.Current.DedeUserID, out var mid) ? mid : 0;
}
