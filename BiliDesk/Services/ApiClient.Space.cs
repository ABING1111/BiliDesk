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
    ///   1. WBI 签名的 /x/space/wbi/acc/info (信息最全)
    ///   2. 公开的 /x/web-interface/card (网页卡片悬浮预览接口, 风控宽松, 附带粉丝/投稿数)
    ///   3. 旧版 /x/space/acc/info
    /// 全部失败才返回 ok=false。★ 2026-10-01 由裸 <c>Task&lt;SpaceInfo?&gt;</c> 改为 (ok, err, data):
    /// 三个降级接口的 code 原本全被丢掉了, 调用方只知道"没取到", 排查风控时无从下手。</summary>
    public async Task<(bool ok, string? err, SpaceInfo? data)> GetSpaceInfoAsync(long mid)
    {
        try
        {
            await EnsureBuvidAsync();
            var referer = $"https://space.bilibili.com/{mid}/";

            // 1. WBI 签名接口
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/wbi/acc/info",
                new Dictionary<string, string> { ["mid"] = mid.ToString() },
                sign: true, referer: referer);
            if (code == 0 && data != null)
            {
                var d = data.Value;
                return (true, null, new SpaceInfo
                {
                    Name = GetStr(d, "name"),
                    Face = UrlUtil.Normalize(GetStr(d, "face")),
                    Sign = GetStr(d, "sign"),
                    Mid = mid,
                    Level = GetInt(d, "level")
                });
            }

            // 2. 公开名片接口(风控宽松, 含粉丝/关注/投稿数)
            (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/card",
                new Dictionary<string, string> { ["mid"] = mid.ToString() },
                referer: referer);
            if (code == 0 && data != null)
            {
                var d = data.Value;
                if (d.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
                {
                    return (true, null, new SpaceInfo
                    {
                        Name = GetStr(card, "name"),
                        Face = UrlUtil.Normalize(GetStr(card, "face")),
                        Sign = GetStr(card, "sign"),
                        Mid = mid,
                        Level = card.TryGetProperty("level_info", out var li) ? GetInt(li, "current_level") : 0,
                        Follower = GetLong(d, "follower"),
                        Following = GetLong(d, "following"),
                        VideoCount = GetLong(d, "archive_count")
                    });
                }
            }

            // 3. 旧版接口兜底
            (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/acc/info",
                new Dictionary<string, string> { ["mid"] = mid.ToString() },
                referer: referer);
            if (code == 0 && data != null)
            {
                var d = data.Value;
                return (true, null, new SpaceInfo
                {
                    Name = GetStr(d, "name"),
                    Face = UrlUtil.Normalize(GetStr(d, "face")),
                    Sign = GetStr(d, "sign"),
                    Mid = mid,
                    Level = GetInt(d, "level")
                });
            }
            return (false, "三个空间接口都取不到该 UP 主的信息(可能是 mid 不存在或被风控)", null);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取 UP 主信息失败: " + ex.Message, null);
        }
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
