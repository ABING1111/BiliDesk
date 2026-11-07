using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 哔哩哔哩 API 客户端: 带浏览器 UA、Cookie、WBI 签名与风控降级逻辑。
/// ★ 2026-11-07 由单文件拆成 17 个 partial 分片(纯移动、零逻辑改动),
///   分片清单与动机见 ApiClient.Core.cs 头部说明。
/// </summary>
public partial class ApiClient
{
    // ---------------------------------------------------------------- 视频合集

    /// <summary>
    /// 视频合集(ugc_season)。单独取一次合集信息时用它; 播放器的主路径是直接吃
    /// <see cref="GetVideoAsync"/> 顺带解析出来的 <see cref="VideoDetail.Season"/>, 不走这里。
    ///
    /// ★ 为什么要单独留一个方法: 超大合集可能被 view 接口截断(自报 ep_count 大于实际给的条数),
    ///   这时才需要走 seasons_archives_list 翻页补齐 —— 补页逻辑收在这里, 调用方只感知成败。
    /// </summary>
    public async Task<(bool ok, string? err, SeasonInfo? data)> GetSeasonAsync(string bvid)
    {
        try
        {
            var (detailOk, detailErr, detail) = await GetVideoAsync(bvid);
            if (!detailOk || detail == null) return (false, detailErr, null);
            if (detail.Season == null) return (false, "这个视频不属于任何合集", null);

            var season = detail.Season;
            // 截断才补页: 正常情况下(实测 118 集 / 254 集都是全量)这里不会触发。
            if (season.EpCount > 0 && season.EpCount > season.Episodes.Count)
            {
                await CompleteSeasonFromArchivesAsync(season);
                // 补进来的条目还没有序号/分组标题标记: 整个展平序重编一遍
                // (newly added 的落在尾部, 重编不会打乱已有条目的相对顺序)
                NumberEpisodes(season);
            }

            return (true, null, season);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取合集失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 把 ugc_season 解析成 <see cref="SeasonInfo"/>。
    ///
    /// ★ 顺序即官方顺序: sections 依次拼接展平后的序列, 与官方网页播放器默认的
    ///   `sort_reverse=false` **逐 aid 完全一致**(2026-10-02 用 118 集合集实测 118/118),
    ///   所以列表直接用这个展平序, 不需要再按发布时间排 —— 它本来也不按时间排。
    /// </summary>
    private static SeasonInfo ParseSeason(JsonElement ugc)
    {
        var season = new SeasonInfo
        {
            SeasonId = GetLong(ugc, "id"),
            Title = GetStr(ugc, "title").StripHtml(),
            // 合集封面本身就是 https, 但也过一道 Normalize —— 下一行 episodes 的 arc.pic
            // 实测是 http://, 统一在这里规范化省得 UI 层再各写一遍。
            Cover = UrlUtil.Normalize(GetStr(ugc, "cover")),
            Mid = GetLong(ugc, "mid"),
            Intro = GetStr(ugc, "intro"),
            EpCount = GetInt(ugc, "ep_count")
        };

        if (!ugc.TryGetProperty("sections", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
            return season;

        foreach (var sec in sections.EnumerateArray())
        {
            if (sec.ValueKind != JsonValueKind.Object) continue;
            var section = new SeasonSection
            {
                Id = GetLong(sec, "id"),
                Title = GetStr(sec, "title").StripHtml()
            };

            if (sec.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in eps.EnumerateArray())
                {
                    if (ep.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParseSeasonEpisode(ep, section.Id, section.Title);
                    if (parsed == null) continue;
                    section.Episodes.Add(parsed);
                    season.Episodes.Add(parsed);
                }
            }

            // 空分组不放进 Sections: 界面上一个只有标题、底下什么都没有的分组是纯噪音
            if (section.Episodes.Count > 0) season.Sections.Add(section);
        }

        NumberEpisodes(season);
        return season;
    }

    private static SeasonEpisode? ParseSeasonEpisode(JsonElement ep, long sectionId, string sectionTitle)
    {
        // episode 自己的 bvid 是权威播放键; 拿不到就整条丢掉(点不动的一行不如不显示)
        var bvid = GetStr(ep, "bvid");
        if (string.IsNullOrEmpty(bvid)) return null;

        var item = new SeasonEpisode
        {
            Bvid = bvid,
            // ★ aid 必须 long: 实测 117367564666453 已远超 int 上限
            Aid = GetLong(ep, "aid"),
            Cid = GetLong(ep, "cid"),
            Title = GetStr(ep, "title").StripHtml(),
            SectionId = sectionId,
            SectionTitle = sectionTitle
        };

        // arc 是"这条稿件"的摘要: 封面/时长/播放量/发布时间全在它里面
        if (ep.TryGetProperty("arc", out var arc) && arc.ValueKind == JsonValueKind.Object)
        {
            // 封面/时长以 arc 为准; title 优先用 episode 自己的(它是合集语境下的标题)
            item.Cover = UrlUtil.Normalize(GetStr(arc, "pic"));
            item.DurationSec = GetInt(arc, "duration");
            item.Pubdate = GetLong(arc, "pubdate");
            if (item.Title.Length == 0) item.Title = GetStr(arc, "title").StripHtml();
            if (item.Aid == 0) item.Aid = GetLong(arc, "aid");
            if (arc.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
                item.ViewCount = GetLong(st, "view");
        }
        // arc 缺了就从 page 兜底(实测两者 cid/duration 一致, 只是字段可能各自缺)
        if (ep.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
        {
            if (item.Cid == 0) item.Cid = GetLong(page, "cid");
            if (item.DurationSec == 0) item.DurationSec = GetInt(page, "duration");
        }

        return item;
    }

    /// <summary>
    /// 相关视频推荐(播放器信息栏简介下方那一列, 2026-10-12 新增)。
    ///
    /// 接口: <c>/x/web-interface/archive/related</c>(B 站播放器右侧「相关推荐」同源)。
    /// 已由 .probes/bd-probe-related 实测核对过响应形状, 有两点与直觉相反:
    ///
    /// ★ `data` **直接就是数组**, 不是 `{ list: [...] }`。
    ///   照着热门那套去取 `data.list` 会静默拿到"0 条"—— 请求成功、页面却永远空着,
    ///   是一类极难往接口头上想的失败。所以两种形状都认。
    /// ★ 条目字段与 popular **完全同构**(owner{mid,name,face} / stat{view,danmaku,...} /
    ///   top-level bvid/pic/title/duration/pubdate), 因此直接复用
    ///   <see cref="ParseVideoItem"/>, 不必再写一份解析器 —— 写两份必然漂移。
    ///   duration 单位是秒(实测 133 / 134), 与 popular 一致; 番剧 PGC 那套是毫秒, 别混。
    ///
    /// ★★ 2026-10-13 用户报"相关视频只有 8 个, 添加更多视频, 往下滑刷新":
    ///   实测(探针 bd-probe-coin, 逐组 ps/pn 对照):
    ///     · ps=12 / 40 / 50 **返回的条数完全一样, 都是 40** —— 服务端忽略了 ps,
    ///       一次就把 40 条全给;
    ///     · pn=1 与 pn=2 拿到的也是同一批 40 条 ⇒ **翻页没有意义**,
    ///       拉第二页只会得到重复内容(自己跟自己重复, 比不拉更难查)。
    ///   所以"更多"的正解是: 一次拿全 40 条, 由界面**分批展示**(先 8 条,
    ///   滚到附近再补下一批), 而不是"滚到底再请求一次"。
    ///
    /// 需要设备指纹: 与 view 一样, 无 cookie 时会被风控拦下(返回非 0 码)。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetRelatedAsync(string bvid)
    {
        try
        {
            if (string.IsNullOrEmpty(bvid)) return (false, "缺少视频 BV 号", null);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/archive/related",
                // ps 写 40 只是"表明意图": 实测服务端恒返 40 条, 这个值不影响结果(见上面那段)
                new Dictionary<string, string> { ["bvid"] = bvid, ["pn"] = "1", ["ps"] = "40" });
            if (code != 0 || data == null)
                return (false, msg ?? $"获取相关视频失败 (code {code})", null);

            // data 可能直接是数组, 也可能是 { list: [...] }; 两种都认, 见上面的说明
            JsonElement arr;
            var root = data.Value;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arr = root;
            }
            else if (root.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                arr = l;
            }
            else
            {
                return (true, null, new List<VideoItem>());   // 形状不认识: 空列表而不是崩
            }

            var items = new List<VideoItem>();
            foreach (var e in arr.EnumerateArray())
            {
                var item = ParseVideoItem(e);
                if (item == null) continue;
                // ★ 自身可能混在结果里(实测会), 点它等于原地重播 —— 先挡一道,
                //   免得列表末尾莫名出现"当前视频"且点了没反应。
                if (item.Bvid == bvid) continue;
                items.Add(item);
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取相关视频失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 给展平后的每一集补上"第几集"与"是不是分组第一集"。
    /// 单独一步是因为这两件事依赖**跨分组的累计**, 逐条解析时算不出来。
    ///
    /// 按 <see cref="SeasonInfo.Episodes"/> 的展平顺序编号(而不是按 Sections 再遍历一遍):
    /// 兜底补页追加进来的条目只落在展平序列的尾部, 两个序列的顺序在那种情况下并不一致,
    /// 以展平序为准才不会出现"第 119 集排在第 100 集前面"。
    /// </summary>
    private static void NumberEpisodes(SeasonInfo season)
    {
        var started = new HashSet<long>();
        var index = 0;
        // 单分组时不画分组标题(官方在只有"正片"一组时也不显示组名)。
        // 在这里就定下初值: 界面灌数据之前必须先有确定值(条目是普通 POCO, 没有变更通知)。
        var showHeader = season.Sections.Count > 1;
        foreach (var ep in season.Episodes)
        {
            ep.Index = ++index;
            ep.IsSectionStart = started.Add(ep.SectionId);
            ep.ShowSectionHeader = showHeader && ep.IsSectionStart;
        }
    }

    /// <summary>
    /// 超大合集的兜底补齐: 走 <c>polymer/web-space/seasons_archives_list</c> 按页取。
    ///
    /// 只有 view 接口给不全(ep_count > 实际条数)时才会被调用, 正常路径永远不触发。
    /// 补进来的条目**排在原有条目之后**: 原有那批来自 view, 顺序已与官方一致, 不能打乱;
    /// 缺的那些按接口返回的页序追加即可。
    ///
    /// ★★ 终止条件必须是"这一页没有 archives", **不能靠错误码** ——
    ///   实测越界页照样回 code=0 + 空数组, 靠错误码判断会死循环。
    /// ★ mid 会被服务端忽略(给错的 mid 也照样返回全部), 所以只用它做参数, 不做校验。
    /// ★ page_size 上限: 30/50/100 可用, 200 回 -400。这里保守取 30。
    /// </summary>
    private async Task CompleteSeasonFromArchivesAsync(SeasonInfo season)
    {
        // 卫语句: 没有 season_id/mid 就没法翻页; 已经是全量也不用翻
        if (season.SeasonId <= 0 || season.Mid <= 0) return;

        var seen = new HashSet<string>(season.Episodes.Select(e => e.Bvid), StringComparer.Ordinal);
        // 上限只是防"服务端永远不返回空页"这种病态情况, 30 页 × 30 条 = 900 集, 远超实际合集规模
        const int maxPages = 30;
        const int pageSize = 30;

        for (var pageNum = 1; pageNum <= maxPages; pageNum++)
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-space/seasons_archives_list",
                new Dictionary<string, string>
                {
                    ["mid"] = season.Mid.ToString(),
                    ["season_id"] = season.SeasonId.ToString(),
                    // false = 官方网页播放器的默认顺序(与 view 的展平序一致)
                    ["sort_reverse"] = "false",
                    ["page_num"] = pageNum.ToString(),
                    ["page_size"] = pageSize.ToString()
                },
                referer: "https://space.bilibili.com/" + season.Mid);

            if (code != 0 || data == null)
                throw new InvalidOperationException(
                    $"合集分页补齐失败 (code {code}{(string.IsNullOrEmpty(msg) ? "" : ": " + msg)})");
            if (!data.Value.TryGetProperty("archives", out var archives) ||
                archives.ValueKind != JsonValueKind.Array)
                return;

            // ★ 越界页就是"空数组 + code 0", 靠它收口
            var count = archives.GetArrayLength();
            if (count == 0) return;

            foreach (var a in archives.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var bvid = GetStr(a, "bvid");
                if (string.IsNullOrEmpty(bvid)) continue;
                if (!seen.Add(bvid)) continue;   // 原有条目已经带过, 不重复追加

                var ep = new SeasonEpisode
                {
                    Bvid = bvid,
                    Aid = GetLong(a, "aid"),
                    Title = GetStr(a, "title").StripHtml(),
                    Cover = UrlUtil.Normalize(GetStr(a, "pic")),
                    DurationSec = GetInt(a, "duration"),
                    Pubdate = GetLong(a, "pubdate")
                };
                if (a.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
                    ep.ViewCount = GetLong(st, "view");

                // 补进来的条目没有分组归属(接口不给): 挂到第一个分组名下, 免得界面上
                // 它们落在所有分组标题之外、看起来像"无家可归"的一堆。
                var host = season.Sections.Count > 0 ? season.Sections[0] : null;
                if (host != null)
                {
                    ep.SectionId = host.Id;
                    ep.SectionTitle = host.Title;
                    host.Episodes.Add(ep);
                }
                season.Episodes.Add(ep);
            }
        }
    }
}
