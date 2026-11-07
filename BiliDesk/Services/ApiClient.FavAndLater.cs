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
    // ---------------------------------------------------------------- 删除 / 清空

    /// <summary>
    /// 删除**单条**云端观看历史。
    /// 接口: /x/v2/history/delete (POST)。
    ///
    /// 参数 `kid` 不是裸 id, 而是 `{业务类型}_{目标id}`:
    ///   · 视频 → `archive_{avid}`(稿件 avid, 不是 bvid)
    ///   · 直播 → `live_{房间号}`
    /// ★ 前缀写错不会报错, 而是**静默不生效**(接口对未知 kid 也回 code=0, 实测),
    ///   所以视频和直播必须各传对(2026-10-03 加直播历史时踩到这条)。
    /// </summary>
    public async Task<(bool ok, string? err)> DeleteHistoryAsync(long aid, ContentKind kind = ContentKind.Video)
    {
        var prefix = kind == ContentKind.Live ? "live" : "archive";
        if (aid <= 0) return (false, kind == ContentKind.Live ? "缺少直播间号" : "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录");

        return await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/delete",
            new Dictionary<string, string> { ["kid"] = $"{prefix}_{aid}" },
            referer: "https://www.bilibili.com/account/history");
    }

    /// <summary>清空**全部**云端观看历史。接口: /x/v2/history/clear (POST), 只需要 csrf。</summary>
    public async Task<(bool ok, string? err)> ClearHistoryAsync()
    {
        if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录");

        return await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/clear",
            new Dictionary<string, string>(),
            referer: "https://www.bilibili.com/account/history");
    }

    /// <summary>
    /// 批量把视频从**指定收藏夹**里移出。
    /// 接口: /x/v3/fav/resource/batch-del (POST, 注意是连字符不是斜杠)。
    ///
    /// `resources` 的格式是 `{内容id}:{内容类型}`, 成员之间用逗号分隔 ——
    /// 内容 id 是**稿件 avid**(不是 bvid, 也不需要 cid), 视频的 type 固定为 2。
    /// 一次请求可以塞多个, 所以"批量移出"不需要挨个调 deal 接口。
    /// </summary>
    public async Task<(bool ok, string? err)> BatchDelFavResourcesAsync(long mediaId, IEnumerable<long> aids)
    {
        if (mediaId <= 0) return (false, "缺少收藏夹 id");
        if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录");

        var res = string.Join(",", aids.Where(x => x > 0).Select(x => $"{x}:2"));
        if (res.Length == 0) return (false, "没有可移出的内容");

        var mid = SessionManager.Instance.Current.DedeUserID;
        return await PostFormAsync(
            "https://api.bilibili.com/x/v3/fav/resource/batch-del",
            new Dictionary<string, string>
            {
                ["resources"] = res,
                ["media_id"] = mediaId.ToString(),
                ["platform"] = "web"
            },
            referer: $"https://space.bilibili.com/{mid}/favlist?fid={mediaId}");
    }

    // ---------------------------------------------------------------- 云端收藏夹

    /// <summary>
    /// 获取当前登录用户的云端收藏夹列表。
    /// 接口: /x/v3/fav/folder/created/list-all —— 一次拿全(不分页), 返回 (ok, err, folders)
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetFavFoldersAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list-all",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<FavFolder>();
            // data.list 是收藏夹数组; 部分账号返回 null 表示没有自建收藏夹
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count")
                    });
                }
            }
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 获取某个云端收藏夹内的视频(分页)。
    /// 接口: /x/v3/fav/resource/list —— 需要 wbi 签名, 未登录时会被风控。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, bool hasMore)>
        GetFavResourcesAsync(long mediaId, int pn = 1, int ps = 20)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null, false);
            await EnsureBuvidAsync();
            var mid = SessionManager.Instance.Current.DedeUserID;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/resource/list",
                new Dictionary<string, string>
                {
                    ["media_id"] = mediaId.ToString(),
                    ["pn"] = pn.ToString(),
                    ["ps"] = ps.ToString(),
                    ["keyword"] = "",
                    ["order"] = "mtime",   // 按收藏时间倒序
                    ["type"] = "0",
                    ["tid"] = "0",
                    ["platform"] = "web",
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist?fid={mediaId}");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, false);

            var items = new List<VideoItem>();
            var hasMore = false;
            if (data.Value.ValueKind == JsonValueKind.Object)
            {
                hasMore = data.Value.TryGetProperty("has_more", out var hm) &&
                          hm.ValueKind == JsonValueKind.True;
                if (data.Value.TryGetProperty("medias", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        // 失效视频的 title 为 "已失效视频", bvid 为空 —— 跳过, 否则点开是空窗
                        var bvid = GetStr(m, "bvid");
                        if (string.IsNullOrEmpty(bvid)) continue;
                        // 播放量藏在 cnt_info.play 里(upper 也可能为空, 用 upper.name 兜底)
                        var author = GetStr(m, "upper");
                        if (author.Length == 0) author = GetUpperName(m);
                        items.Add(new VideoItem
                        {
                            Bvid = bvid,
                            // 收藏夹条目里 aid 字段名是 "id"
                            Aid = GetLong(m, "id"),
                            Title = GetStr(m, "title").StripHtml(),
                            Cover = UrlUtil.Normalize(GetStr(m, "cover")),
                            Author = author,
                            // upper 是对象时拿它的 mid(upper 为纯字符串的老格式则取不到, 保持 0)
                            OwnerMid = GetNestedLong(m, "upper", "mid"),
                            Duration = VideoItem.FormatSeconds(GetInt(m, "duration")),
                            ViewCount = GetNestedLong(m, "cnt_info", "play"),
                            DanmakuCount = GetNestedLong(m, "cnt_info", "danmaku"),
                            Pubdate = GetLong(m, "pubtime")
                        });
                    }
                }
            }
            return (true, null, items, hasMore);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, false);
        }
    }

    /// <summary>
    /// 查询"这个视频被收藏进了哪些收藏夹"。
    ///
    /// 接口必须用 **/x/v3/fav/folder/created/list-all**。
    /// 之前写成 /x/v3/fav/folder/created/list(没有 -all), 那个接口**不接受 rid 参数**,
    /// 带上它只会得到 -400「请求错误」—— 这就是"收藏状态获取失败: 请求错误"的根因。
    /// list-all 加上 rid 后, 每个收藏夹会多一个 fav_state 字段表示该视频是否在里面,
    /// 于是"是否已收藏"和"该往哪个夹放"一次拿到。
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetVideoFavFoldersAsync(long aid)
    {
        try
        {
            if (aid <= 0) return (false, "缺少视频 aid", null);
            if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list-all",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["type"] = "2",        // 2 = 视频稿件
                    ["rid"] = aid.ToString(),
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://www.bilibili.com/video/");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<FavFolder>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count"),
                        // fav_state 为 1 表示该视频已在此收藏夹中
                        HasVideo = GetInt(f, "fav_state") == 1
                    });
                }
            }
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 收藏 / 取消收藏(现行 Web 端接口, 双端通用)。
    /// 接口: /x/v3/fav/resource/deal
    ///   rid          稿件 avid (**不是 bvid**)
    ///   type         固定 2(视频稿件)
    ///   add_media_ids 要加入的收藏夹 mlid, 多个用逗号分隔
    ///   del_media_ids 要移出的收藏夹 mlid, 多个用逗号分隔
    /// 已知业务码: 11201 已经收藏过了 / 11202 已经取消收藏了 —— 这两种对用户来说都是"目标已达成",
    /// 所以在上层当成成功处理, 不要弹红字报错。
    /// </summary>
    public async Task<(bool ok, string? err)> DealFavoriteAsync(
        long aid, IEnumerable<long>? addIds = null, IEnumerable<long>? delIds = null)
    {
        if (aid <= 0) return (false, "缺少视频 aid");
        if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录");

        var add = addIds == null ? "" : string.Join(",", addIds.Where(x => x > 0));
        var del = delIds == null ? "" : string.Join(",", delIds.Where(x => x > 0));
        if (add.Length == 0 && del.Length == 0) return (false, "未指定收藏夹");

        var mid = SessionManager.Instance.Current.DedeUserID;
        var ps = new Dictionary<string, string>
        {
            ["rid"] = aid.ToString(),
            ["type"] = "2",
            ["platform"] = "web"
        };
        if (add.Length > 0) ps["add_media_ids"] = add;
        if (del.Length > 0) ps["del_media_ids"] = del;

        var (code, msg) = await PostFormCodeAsync(
            "https://api.bilibili.com/x/v3/fav/resource/deal",
            ps,
            referer: $"https://space.bilibili.com/{mid}/favlist");

        // 11201/11202: "已经收藏过了 / 已经取消收藏了" —— 用户想要的状态本来就成立,
        // 属于幂等成功, 不该弹报错(重复点击时非常容易撞上这两个码)
        if (code is 0 or 11201 or 11202) return (true, null);
        return (false, msg);
    }

    private static string GetUpperName(JsonElement m)
        => m.TryGetProperty("upper", out var u) && u.ValueKind == JsonValueKind.Object
            ? GetStr(u, "name") : "";

    private static long GetNestedLong(JsonElement e, string objName, string propName)
        => e.TryGetProperty(objName, out var o) && o.ValueKind == JsonValueKind.Object
            ? GetLong(o, propName) : 0;

    // ---------------------------------------------------------------- 稍后再看

    /// <summary>
    /// 获取「稍后再看」列表。
    /// 接口: GET /x/v2/history/toview(需要登录 Cookie, 不需要 WBI 签名)。
    ///
    /// 返回形状实测有两种(服务端改过版): `data` 既可能是 `{count, list:[...]}`, 也可能是裸数组 ——
    /// 两种都接住, 免得某次灰度之后这里静默变成"空列表"。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, int count)> GetWatchLaterAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录", null, 0);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/history/toview",
                null,
                sign: false,
                referer: "https://www.bilibili.com/watchlater/list");

            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, 0);

            var count = 0;
            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array)
            {
                arr = data.Value;
                count = arr.GetArrayLength();
            }
            else if (data.Value.TryGetProperty("list", out var lst) && lst.ValueKind == JsonValueKind.Array)
            {
                arr = lst;
                count = GetInt(data.Value, "count");
                if (count <= 0) count = arr.GetArrayLength();
            }
            else
            {
                return (false, "接口返回结构不认识", null, 0);
            }

            var items = new List<VideoItem>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var bvid = GetStr(e, "bvid");
                if (string.IsNullOrEmpty(bvid)) continue;

                var cover = GetStr(e, "pic");
                string author = "";
                long ownerMid = 0;
                if (e.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
                {
                    author = GetStr(owner, "name");
                    ownerMid = GetLong(owner, "mid");
                }
                items.Add(new VideoItem
                {
                    Bvid = bvid,
                    Aid = GetLong(e, "aid"),
                    Title = GetStr(e, "title").StripHtml(),
                    Cover = UrlUtil.Normalize(cover),
                    Author = author,
                    OwnerMid = ownerMid,
                    Duration = VideoItem.FormatSeconds(GetInt(e, "duration")),
                    ViewCount = Math.Max(0, GetNestedLong(e, "stat", "view")),
                    DanmakuCount = Math.Max(0, GetNestedLong(e, "stat", "danmaku")),
                    Pubdate = GetLong(e, "add_at")
                });
            }
            return (true, null, items, count > 0 ? count : items.Count);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, 0);
        }
    }

    /// <summary>
    /// 某条视频是否已在「稍后再看」里。
    ///
    /// 服务端没有"单条查询"的接口, 只能拉整个列表再找 —— 而列表可能有两百多条
    /// (实测某个账号 249 条), 每开一个视频都拉一遍太浪费。所以这里缓存一份 aid 集合,
    /// 60 秒内直接复用; 我们自己增删之后会立刻让缓存失效(见 InvalidateWatchLaterCache)。
    ///
    /// 返回 <c>ok=false</c> = **没查到**(未登录 / 请求失败) —— 调用方不要把 value 当成
    /// "不在列表里", 否则界面会显示一个错误的状态。2026-10-01 由 <c>Task&lt;bool?&gt;</c>
    /// 改为 (ok, err, value), 未登录/查询失败时 err 会说明是哪一种。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> IsInWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "参数不完整", false);
        if (!SessionManager.Instance.HasLogin) return (false, "未登录", false);

        var set = await GetWatchLaterAidSetAsync();
        if (set == null) return (false, "查询稍后再看失败", false);
        return (true, null, set.Contains(aid));
    }

    private HashSet<long>? _watchLaterAids;
    private DateTime _watchLaterFetchedAt = DateTime.MinValue;
    private readonly object _watchLaterGate = new();

    /// <summary>aid 集合的缓存有效期。列表本身刷新很慢(实测服务端写入后约 2s 才可见), 60 秒足够新。</summary>
    private static readonly TimeSpan WatchLaterCacheTtl = TimeSpan.FromSeconds(60);

    private async Task<HashSet<long>?> GetWatchLaterAidSetAsync()
    {
        lock (_watchLaterGate)
        {
            if (_watchLaterAids != null && DateTime.UtcNow - _watchLaterFetchedAt < WatchLaterCacheTtl)
                return _watchLaterAids;
        }

        var (ok, _, items, _) = await GetWatchLaterAsync();
        if (!ok || items == null) return null;   // 失败不缓存, 也不谎报状态

        var set = new HashSet<long>(items.Where(x => x.Aid > 0).Select(x => x.Aid));
        lock (_watchLaterGate)
        {
            _watchLaterAids = set;
            _watchLaterFetchedAt = DateTime.UtcNow;
        }
        return set;
    }

    /// <summary>本机刚改过稍后再看 —— 让 aid 缓存立刻失效, 否则按钮状态会停在旧值上。</summary>
    private void InvalidateWatchLaterCache()
    {
        lock (_watchLaterGate)
        {
            _watchLaterAids = null;
            _watchLaterFetchedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 加入「稍后再看」。
    /// 接口: POST /x/v2/history/toview/add, 参数只有 `aid`(稿件 avid)+ csrf。
    ///
    /// 幂等: 已经在列表里再调一次仍然返回 code 0(服务端不报错也不重复加),
    /// 所以调用方不需要先查一遍列表。
    /// </summary>
    public async Task<(bool ok, string? err)> AddWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/add",
            new Dictionary<string, string> { ["aid"] = aid.ToString() },
            referer: "https://www.bilibili.com/");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }

    /// <summary>
    /// 从「稍后再看」移除一条。
    /// 接口: POST /x/v2/history/toview/del, 参数 `aid` —— 注意同名的 `viewed=1` 是另一种语义
    /// ("清掉已经看完的"), 这里只做单条移除, 所以永远带 aid。
    /// </summary>
    public async Task<(bool ok, string? err)> RemoveWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/del",
            new Dictionary<string, string> { ["aid"] = aid.ToString() },
            referer: "https://www.bilibili.com/watchlater/list");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }

    /// <summary>
    /// 清空「稍后再看」。接口: POST /x/v2/history/toview/clear(只需要 csrf)。
    /// 服务端不可撤销, 调用方必须先确认。
    /// </summary>
    public async Task<(bool ok, string? err)> ClearWatchLaterAsync()
    {
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/clear",
            new Dictionary<string, string>(),
            referer: "https://www.bilibili.com/watchlater/list");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }

}
