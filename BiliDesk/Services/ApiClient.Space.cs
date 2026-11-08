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
    /// <summary>获取 UP 主空间信息。
    /// acc/info 系列接口风控严格(未带完整设备指纹时返回 -403「访问权限不足」),
    /// 因此采用多接口降级策略:
    ///   1. WBI 签名的 /x/space/wbi/acc/info (昵称/头像/签名/等级, 信息最全)
    ///   2. 公开的 /x/web-interface/card (网页卡片悬浮预览接口, 风控宽松, 附带粉丝/投稿数/获赞数/大会员)
    ///   3. 旧版 /x/space/acc/info
    /// 全部失败才返回 ok=false。★ 2026-10-01 由裸 <c>Task&lt;SpaceInfo?&gt;</c> 改为 (ok, err, data):
    /// 三个降级接口的 code 原本全被丢掉了, 调用方只知道"没取到", 排查风控时无从下手。
    ///
    /// ★★ 2026-11-08 修两处数据问题(与 UP 主主页顶部改版一起):
    ///   ① 原来走通第 1 条就**直接 return**, 而 acc/info 里没有粉丝/关注/投稿数 ——
    ///      于是主页经常显示"0 粉丝 0 关注";
    ///   ② 第 2 条里 `card.following` 实际是**布尔值**(当前登录用户是否关注了 TA),
    ///      被当成"关注数"取出来只会是 0/1。
    ///   现在固定并行拉 acc/info + card + /x/relation/stat 后合并:
    ///     · 关注数/粉丝数 → relation/stat(权威且不需要签名);
    ///     · 获赞数/投稿数/大会员 → card(archive_count / like_num / vip.label)。
    /// </summary>
    public async Task<(bool ok, string? err, SpaceInfo? data)> GetSpaceInfoAsync(long mid)
    {
        try
        {
            await EnsureBuvidAsync();
            var referer = $"https://space.bilibili.com/{mid}/";
            var midParam = new Dictionary<string, string> { ["mid"] = mid.ToString() };

            // 三路并行: 任一失败都不影响其余信息(每个接口的失败都被 GetJsonAsync 包成返回值)
            var infoTask = GetJsonAsync(
                "https://api.bilibili.com/x/space/wbi/acc/info", midParam,
                sign: true, referer: referer);
            var cardTask = GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/card", midParam, referer: referer);
            var statTask = GetJsonAsync(
                "https://api.bilibili.com/x/relation/stat",
                new Dictionary<string, string> { ["vmid"] = mid.ToString() }, referer: referer);

            var (infoRes, cardRes, statRes) = (await infoTask, await cardTask, await statTask);

            // ---------- 名片: 获赞数 / 投稿数 / 大会员 ----------
            long follower = 0, followingCount = 0, likeCount = 0, videoCount = 0;
            var vipLabel = "";
            var vipColor = "";
            JsonElement? cardEl = null;
            var cardOk = false;
            if (cardRes.code == 0 && cardRes.data != null)
            {
                var d = cardRes.data.Value;
                if (d.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
                {
                    cardEl = card.Clone();
                    cardOk = true;
                    ParseVip(card, ref vipLabel, ref vipColor);
                }
                follower = GetLong(d, "follower");
                videoCount = GetLong(d, "archive_count");
                likeCount = GetLong(d, "like_num");
            }

            // ---------- relation/stat: 关注数 / 粉丝数(权威值, 覆盖名片里的) ----------
            if (statRes.code == 0 && statRes.data != null)
            {
                follower = GetLong(statRes.data.Value, "follower");
                followingCount = GetLong(statRes.data.Value, "following");
            }

            // ---------- 资料主体: acc/info → card → 旧接口 ----------
            // ★ 等级的唯一稳定来源是 card.level_info.current_level:
            //   wbi/acc/info 经常被风控(-352)或返回的 level 为 0, 而 card 一直带等级。
            //   先把它单独取出来, 后面无论资料主体来自哪个接口都用它兜底(见 2026-11-13 修"等级一直为 0")。
            var cardLevel = 0;
            if (cardEl is JsonElement levelCard && levelCard.TryGetProperty("level_info", out var cardLevelEl))
                cardLevel = (int)GetLongLoose(cardLevelEl, "current_level");

            SpaceInfo? info = null;
            if (infoRes.code == 0 && infoRes.data != null)
            {
                var d = infoRes.data.Value;
                info = new SpaceInfo
                {
                    Name = GetStr(d, "name"),
                    Face = UrlUtil.Normalize(GetStr(d, "face")),
                    Sign = GetStr(d, "sign"),
                    Mid = mid,
                    // ★ 用 Loose: 等级可能是字符串("6"), 只认数字会静默读成 0
                    Level = (int)GetLongLoose(d, "level")
                };
                ParseVip(d, ref vipLabel, ref vipColor);
            }

            if (info == null && cardOk && cardEl is JsonElement cardData)
            {
                info = new SpaceInfo
                {
                    Name = GetStr(cardData, "name"),
                    Face = UrlUtil.Normalize(GetStr(cardData, "face")),
                    Sign = GetStr(cardData, "sign"),
                    Mid = mid,
                    Level = cardLevel
                };
            }

            if (info == null)
            {
                var (code, _, data) = await GetJsonAsync(
                    "https://api.bilibili.com/x/space/acc/info", midParam, referer: referer);
                if (code == 0 && data != null)
                {
                    var d = data.Value;
                    info = new SpaceInfo
                    {
                        Name = GetStr(d, "name"),
                        Face = UrlUtil.Normalize(GetStr(d, "face")),
                        Sign = GetStr(d, "sign"),
                        Mid = mid,
                        Level = (int)GetLongLoose(d, "level")
                    };
                    ParseVip(d, ref vipLabel, ref vipColor);
                }
            }

            if (info == null)
                return (false, "三个空间接口都取不到该 UP 主的信息(可能是 mid 不存在或被风控)", null);

            // ★ 等级兜底(2026-11-13 修"UP 主页等级一直为 0"):
            //   wbi/acc/info 即使 code=0, level 也可能缺失/为 0; card 的等级是稳定的。
            if (info.Level <= 0 && cardLevel > 0) info.Level = cardLevel;

            info.Follower = follower;
            info.Following = followingCount;
            info.VideoCount = videoCount;
            info.LikeCount = likeCount;
            info.VipLabel = vipLabel;
            info.VipColor = vipColor;
            return (true, null, info);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取 UP 主信息失败: " + ex.Message, null);
        }
    }

    /// <summary>从 acc/info 或 card 里解析大会员标签(接口给的文字与底色原样用)。</summary>
    private static void ParseVip(JsonElement owner, ref string label, ref string color)
    {
        if (!owner.TryGetProperty("vip", out var vip) || vip.ValueKind != JsonValueKind.Object)
            return;
        if (!vip.TryGetProperty("label", out var lbl) || lbl.ValueKind != JsonValueKind.Object)
            return;
        var text = GetStr(lbl, "text");
        if (text.Length == 0) return;
        label = text;
        color = GetStr(lbl, "bg_color");
    }

    /// <summary>
    /// 获取 UP 主空间视频列表。
    /// 优先 arc/search(WBI 签名); 被风控(-403/-352)时降级到公开的 recArchivesByKeywords。
    ///
    /// ★★ 2026-11-06 新增返回 **total**(权威总条数):
    ///   调用方原来用 "本页返回条数 &lt; 30 ⇒ 到底了" 判断, 而这个判据**不成立** ——
    ///   ① 降级接口 recArchivesByKeywords 自己按 17/22 这种零头分页(实测 952 投稿的
    ///      UP 在 pn=31 只给 17 条、pn=32 给 22 条), 于是第 31 页就被误判成到底;
    ///   ② 主路径 arc/search 也有坑: vlist 里偶尔有 ParseVideoItem 解析不了的条目,
    ///      被丢掉后本页条数不足 30, 同样误判。
    ///   ⇒ 用接口给的 page.count(总投稿数)当权威值, 调用方按 "已加载数 &lt; total" 判断到底。
    ///   total 取不到时(降级路径)返回 0, 调用方退回原来的启发式。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, int total)>
        GetSpaceVideosAsync(long mid, int pn = 1, int ps = 30)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps2 = new Dictionary<string, string>
            {
                ["mid"] = mid.ToString(),
                ["pn"] = pn.ToString(),
                ["ps"] = Math.Min(ps, 50).ToString(),
                ["order"] = "pubdate",
                ["platform"] = "web",
                ["web_location"] = "1550101"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/wbi/arc/search", ps2, sign: true,
                referer: $"https://space.bilibili.com/{mid}/video");
            if (code != 0 || data == null)
            {
                // 风控降级: 公开的合集推荐接口, 不需要签名, 返回完整 archive 对象
                var fb = await GetSpaceVideosFallbackAsync(mid, pn, ps);
                return (fb.ok, fb.err, fb.items, 0);
            }

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Object
                && arr.TryGetProperty("vlist", out var vl) && vl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in vl.EnumerateArray())
                {
                    var item = ParseVideoItem(e);
                    if (item != null) items.Add(item);
                }
            }
            if (items.Count == 0)
            {
                var fb = await GetSpaceVideosFallbackAsync(mid, pn, ps);
                return (fb.ok, fb.err, fb.items, 0);
            }

            // page.count = 该 UP 的总投稿数(权威的"到底"判据)
            var total = 0;
            if (data.Value.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
                total = GetInt(page, "count");
            return (true, null, items, total);
        }
        catch (Exception ex) { return (false, ex.Message, null, 0); }
    }

    /// <summary>空间视频列表降级接口(series/recArchivesByKeywords, 公开无签名)。
    /// 返回 data.archives 数组, 字段与 arc/search 的 vlist 略有不同</summary>
    private async Task<(bool ok, string? err, List<VideoItem>? items)>
        GetSpaceVideosFallbackAsync(long mid, int pn, int ps)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/series/recArchivesByKeywords",
                new Dictionary<string, string>
                {
                    ["mid"] = mid.ToString(),
                    ["keywords"] = "",
                    ["pn"] = pn.ToString(),
                    ["ps"] = Math.Min(ps, 50).ToString()
                },
                referer: $"https://space.bilibili.com/{mid}/video");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("archives", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    // ★ 走通用解析器(2026-10-15): 这里原先是手抄的一份, 只认 owner/pubdate,
                    //   而这个接口的 archives 给的是 **upMid + pubdate + duration(秒)** ——
                    //   手抄版把 OwnerMid 读成 0、头像/名字全丢。通用解析器两种写法都认, 不必两份。
                    var item = ParseVideoItem(e);
                    if (item == null) continue;
                    // 空间投稿: 没给 mid 时就用当前空间主(这条列表本来就是他的)
                    if (item.OwnerMid <= 0) item.OwnerMid = mid;
                    items.Add(item);
                }
            }
            return (true, null, items);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

}
