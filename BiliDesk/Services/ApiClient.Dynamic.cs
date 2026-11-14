using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 哔哩哔哩 API 客户端: 综合动态(视频 + 图文 + 文字 + 专栏 + 转发)。
///
/// ★ 2026-11-14 从 ApiClient.Playurl.cs 拆出并重做:
///   旧实现只打 feed/nav 的扁平结构、只保留视频; 现在列表主路径换成 feed/all,
///   能拿到正文/图片/统计/发布动作, 动态卡才能做成 B 站那种综合时间线。
///   feed/nav 仍保留为兜底(风控/异常时退回"只有视频"的旧行为), 未读红点服务也继续用它。
/// </summary>
public partial class ApiClient
{
    /// <summary>feed/all 的功能开关: 让服务端按网页版形态返回 opus 图文/大封面/统计。</summary>
    private const string DynamicFeedFeatures =
        "itemOpusStyle,listOnlyfans,opusBigCover,onlyfansVote,decorationCard,onlyfansAssetsV2,forwardListHidden,ugcDelete";

    /// <summary>
    /// 关注 UP 的综合动态时间线(「动态」页的"全部动态")。
    /// 返回 DynamicItem 列表, 视频/图文/文字/专栏/转发都在里面。
    /// </summary>
    public async Task<(bool ok, string? err, List<DynamicItem>? items, string? nextOffset, bool hasMore)>
        GetDynamicFeedAsync(string? offset = null)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["type"] = "all",
                ["features"] = DynamicFeedFeatures,
                ["platform"] = "web",
                ["web_location"] = "333.1365",
            };
            if (!string.IsNullOrEmpty(offset)) ps["offset"] = offset;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all", ps,
                sign: true, referer: "https://t.bilibili.com/");

            if (code == 0 && data != null)
            {
                var (items, next, hasMore) = ReadDynamicPage(data.Value);
                return (true, null, items, next, hasMore);
            }

            // 未登录没必要再兜底, 直接给明确提示
            if (code == -101)
                return (false, "请先登录后查看动态", null, null, false);

            // feed/all 被风控/异常时退回 feed/nav 扁平路径(只有视频, 但页面不至于整体打不开)
            var fallback = await GetDynamicFeedNavFallbackAsync(offset);
            if (fallback.ok) return fallback;

            return (false, msg ?? "动态加载失败", null, null, false);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null, null, false);
        }
    }

    /// <summary>
    /// 某个 UP 主空间里的动态(UP 主页「图文」Tab 用)。
    /// 接口: GET /x/polymer/web-dynamic/v1/feed/space?host_mid=...
    /// 返回全部类型, 由调用方筛出图文/纯文字(视频动态单独在「投稿」Tab 展示)。
    /// </summary>
    public async Task<(bool ok, string? err, List<DynamicItem>? items, string? nextOffset, bool hasMore)>
        GetSpaceDynamicsAsync(long mid, string? offset = null)
    {
        if (mid <= 0) return (false, "缺少 UP 主 UID", null, null, false);
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["host_mid"] = mid.ToString(),
                ["features"] = DynamicFeedFeatures + ",commentsNewVersion,onlyfansQaCard",
                ["platform"] = "web",
                ["web_location"] = "333.1387",
            };
            if (!string.IsNullOrEmpty(offset)) ps["offset"] = offset;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space", ps,
                sign: true, referer: "https://t.bilibili.com/");
            if (code != 0 || data == null)
                return (false, msg ?? $"加载 UP 主动态失败 (code {code})", null, null, false);

            var (items, next, hasMore) = ReadDynamicPage(data.Value);
            return (true, null, items, next, hasMore);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null, null, false);
        }
    }

    /// <summary>feed/nav 扁平兜底: 只解析得出视频动态, 与旧版行为一致。</summary>
    private async Task<(bool ok, string? err, List<DynamicItem>? items, string? nextOffset, bool hasMore)>
        GetDynamicFeedNavFallbackAsync(string? offset)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(offset)) ps["offset"] = offset;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/nav", ps,
                sign: true, referer: "https://t.bilibili.com/");
            if (code != 0 || data == null)
                return (false, code == -101 ? "请先登录后查看动态" : (msg ?? "动态加载失败"), null, null, false);

            var (items, next, hasMore) = ReadDynamicPage(data.Value);
            return (true, null, items, next, hasMore);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null, null, false);
        }
    }

    /// <summary>从 feed/all / feed/nav 的 data 里读 items + 游标。</summary>
    private static (List<DynamicItem> items, string? nextOffset, bool hasMore) ReadDynamicPage(JsonElement d)
    {
        var items = new List<DynamicItem>();
        var hasMore = d.TryGetProperty("has_more", out var hm) &&
                      (hm.ValueKind == JsonValueKind.True ||
                       (hm.ValueKind == JsonValueKind.Number && hm.GetInt32() == 1));
        string? next = null;
        if (d.TryGetProperty("offset", out var off))
        {
            if (off.ValueKind == JsonValueKind.String) next = off.GetString();
            else if (off.ValueKind == JsonValueKind.Number)
            {
                if (off.TryGetInt64(out var n)) next = n.ToString();
                else if (off.TryGetDouble(out var dn)) next = ((long)dn).ToString();
            }
        }

        if (d.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                var item = DynamicParser.ParseItem(e);
                if (item != null) items.Add(item);
            }
        }
        return (items, next, hasMore);
    }

    /// <summary>
    /// 单条动态详情(详情窗打开时补全全文/完整图片)。
    /// 接口: GET /x/polymer/web-dynamic/v1/detail?id=...
    /// </summary>
    public async Task<(bool ok, string? err, DynamicItem? item)> GetDynamicDetailAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return (false, "缺少动态 id", null);
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["id"] = id,
                ["features"] = DynamicFeedFeatures + ",commentsNewVersion",
                ["platform"] = "web",
                ["web_location"] = "333.1368",
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/detail", ps,
                sign: true, referer: "https://t.bilibili.com/");
            if (code != 0 || data == null)
                return (false, msg ?? $"获取动态详情失败 (code {code})", null);

            if (!data.Value.TryGetProperty("item", out var itemEl) ||
                itemEl.ValueKind != JsonValueKind.Object)
                return (false, "动态详情结构异常", null);

            var item = DynamicParser.ParseItem(itemEl);
            return item == null ? (false, "动态详情解析失败", null) : (true, null, item);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 动态点赞 / 取消点赞。
    /// 接口: POST /x/dynamic/feed/dyn/thumb (JSON body: dyn_id_str + up 1/2)。
    /// ★ 与视频点赞(/archive/like)、评论点赞(/reply/action)是三套接口, 别混。
    /// </summary>
    public async Task<(bool ok, string? err)> LikeDynamicAsync(string id, bool like)
    {
        if (string.IsNullOrEmpty(id)) return (false, "缺少动态 id");
        if (!SessionManager.Instance.HasLogin) return (false, "请先登录后再点赞");

        var csrf = SessionManager.Instance.Current.BiliJct;
        if (string.IsNullOrEmpty(csrf)) return (false, "登录状态不完整, 请重新登录");

        var body = JsonSerializer.Serialize(new
        {
            dyn_id_str = id,
            up = like ? 1 : 2,
        });

        var (code, msg) = await PostJsonCodeAsync(
            "https://api.bilibili.com/x/dynamic/feed/dyn/thumb?csrf=" + Enc(csrf),
            body, referer: "https://t.bilibili.com/");
        if (code == 0) return (true, null);

        var friendly = code switch
        {
            -101 => "请先登录后再点赞",
            -111 => "登录状态已失效, 请重新登录",
            -412 => "操作太频繁, 请稍后再试",
            _ => msg ?? $"点赞失败 (code {code})",
        };
        return (false, friendly);
    }

    /// <summary>
    /// 动态转发列表(详情窗「转发」页)。
    /// 接口: GET /x/polymer/web-dynamic/v1/detail/forward?id=...&offset=...
    /// </summary>
    public async Task<(bool ok, string? err, List<DynamicRepostItem> items, string? nextOffset, bool hasMore, long total)>
        GetDynamicRepostsAsync(string id, string? offset = null)
    {
        var result = new List<DynamicRepostItem>();
        if (string.IsNullOrEmpty(id)) return (false, "缺少动态 id", result, null, false, 0);
        try
        {
            var ps = new Dictionary<string, string> { ["id"] = id };
            if (!string.IsNullOrEmpty(offset)) ps["offset"] = offset;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/detail/forward", ps,
                referer: "https://t.bilibili.com/");
            if (code != 0 || data == null)
                return (false, msg ?? $"获取转发列表失败 (code {code})", result, null, false, 0);

            var d = data.Value;
            var hasMore = d.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
            var next = d.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.String
                ? off.GetString() : null;
            var total = d.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number
                ? tt.GetInt64() : 0;

            if (d.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var r = DynamicParser.ParseRepostItem(e);
                    if (r != null) result.Add(r);
                }
            }
            return (true, null, result, next, hasMore, total);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, result, null, false, 0);
        }
    }

    /// <summary>
    /// POST JSON + 自动带 Cookie / Origin / Referer / 写操作风控预热。
    /// 动态点赞等新接口用 JSON body, 现有 PostFormAsync 是表单编码, 不能直接复用。
    /// </summary>
    private async Task<(int code, string? msg)> PostJsonCodeAsync(
        string url, string jsonBody, string? referer = null)
    {
        try
        {
            await EnsureWriteReadyAsync();

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Cookie",
                AppendFingerprint(SessionManager.Instance.BuildCookieHeader()));
            if (!string.IsNullOrEmpty(referer))
            {
                req.Headers.TryAddWithoutValidation("Referer", referer);
                req.Headers.TryAddWithoutValidation("Origin", OriginFor(referer));
            }
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
            if (code != 0)
            {
                var errMsg = "code " + code;
                if (doc.RootElement.TryGetProperty("message", out var m) &&
                    m.ValueKind == JsonValueKind.String)
                    errMsg = m.GetString() ?? errMsg;
                return (code, errMsg);
            }
            return (0, null);
        }
        catch (Exception ex)
        {
            return (-999, ex.Message);
        }
    }
}