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

    /// <summary>
    /// 首页「直播」tab 依次尝试的父分区。
    ///
    /// 0 = 全部(按人气排全站), 先问它; 拿不到再退回娱乐(1) 与游戏(2) 这两个大户。
    /// 只多一两次请求, 比"某个分区被风控/临时为空就整页空白"稳得多。
    /// </summary>
    private static readonly int[] LiveParentAreas = { 0, 1, 2 };

    /// <summary>
    /// 取直播列表(按人气排序)。
    ///
    /// 用 `room/v1/Area/getRoomList`。**不要**换成 `xlive/web-interface/v1/second/getList` ——
    /// 后者现在必返回 `-352`(风控), 同参数用 curl 直打也是 -352, 加 buvid3、加
    /// Origin/Referer、加 web_location 都一样, 属于接口本身不再对第三方开放;
    /// 这个老接口反而一直可用(实测 page_size=20 稳定给满 20 条, 且 online 严格降序)。
    ///
    /// 它的 `data` 是**裸数组**(不是 second/getList 那种 { list: [...] }), 所以解析走两条路。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetLiveListAsync(int page = 1)
    {
        try
        {
            await EnsureBuvidAsync();
            string? lastErr = null;

            foreach (var parentArea in LiveParentAreas)
            {
                var (code, msg, data) = await GetJsonAsync(
                    "https://api.live.bilibili.com/room/v1/Area/getRoomList",
                    new Dictionary<string, string>
                    {
                        ["platform"] = "web",
                        ["parent_area_id"] = parentArea.ToString(),
                        ["cate_id"] = "0",
                        ["area_id"] = "0",
                        ["sort_type"] = "online",
                        ["page"] = page.ToString(),
                        ["page_size"] = "20"
                    },
                    referer: "https://live.bilibili.com/");
                if (code != 0 || data == null)
                {
                    // 错误原文透出去(比吞掉有用): -352 是风控, -412 是被拦, 文字能说明情况
                    lastErr = msg ?? $"请求失败 (code {code})";
                    continue;
                }

                var items = new List<VideoItem>();
                foreach (var e in LiveItemsOf(data.Value))
                {
                    var roomId = GetLong(e, "roomid");
                    if (roomId <= 0) roomId = GetLong(e, "room_id");
                    if (roomId <= 0) continue;

                    // 封面字段各版本不一: cover / system_cover / user_cover / show_cover 都见过
                    var cover = GetStr(e, "cover");
                    if (cover.Length == 0) cover = GetStr(e, "system_cover");
                    if (cover.Length == 0) cover = GetStr(e, "user_cover");
                    if (cover.Length == 0) cover = GetStr(e, "show_cover");
                    if (cover.Length == 0) cover = GetStr(e, "keyframe");

                    var item = new VideoItem
                    {
                        RoomId = roomId,
                        Title = GetStr(e, "title").StripHtml(),
                        Author = GetStr(e, "uname"),
                        // 主播 mid(直播接口里叫 uid): 卡片下方点 UP 主名也能进他的主页
                        OwnerMid = GetLong(e, "uid"),
                        Cover = UrlUtil.Normalize(cover),
                        // 直播间没有时长, 这里放"直播中"当角标, 复用卡片上那个时长位
                        Duration = "直播中",
                        ViewCount = Math.Max(0, GetLong(e, "online"))
                    };
                    items.Add(item);
                }

                if (items.Count > 0) return (true, null, items);
                lastErr = "这个分区现在没有直播";
            }

            return (false, lastErr ?? "没有取到直播列表", null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 直播列表的条目枚举。兼容两种响应形态:
    ///   - 裸数组           (room/v1/Area/getRoomList)
    ///   - { list: [...] }  (xlive/.../second/getList 那一族)
    /// </summary>
    private static IEnumerable<JsonElement> LiveItemsOf(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in data.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) yield return e;
            yield break;
        }
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) yield return e;
        }
    }

    /// <summary>
    /// 取「我关注的主播」里正在直播的列表(动态页右栏"关注直播"看板的数据源, 2026-10-31 新增)。
    ///
    /// ★★★ 接口(2026-11-03 修): 直接用官方 PC 直播页左栏「关注」面板的接口
    ///   GET /xlive/web-ucenter/v1/xfetter/GetWebList?hit_ab=true
    ///     → data.rooms[]{room_id, uid, uname, face, title, live_status, …}
    ///
    ///   ★ 之前那套"两段式"(先 /x/relation/followings 再 POST
    ///     /room/v1/Room/get_status_info_by_uids)有两个致命问题:
    ///       ① 状态接口**只认 form 编码**, 发 application/json 会返回
    ///          code=1 "invalid params"; 旧代码偏偏只发 JSON, 于是看板永远是空的;
    ///       ② 只查关注列表的**第一页 30 个**, 关注数 >30 时后面的人开播永远看不到
    ///          (实测关注 200+ 人时, 正在播的人里有相当一部分排在第 30 名之后)。
    ///     现在一次请求就能拿到"全部关注里正在直播的", 与关注总数无关。
    ///
    ///   · hit_ab=true 时 data.rooms 是**全部**正在直播的(不是前 10 个);
    ///     代价是 online/live_time/cover_from_user 被置 0/空。看板只用
    ///     uname/face/title/room_id, 不受影响(头像用 face, 比房间封面更像"人")。
    ///   · live_status: 0=未开播, 1=直播中, 2=轮播。看板只收 **1**。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetFollowingLiveAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin)
                return (false, "请先登录后查看关注直播", null);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.live.bilibili.com/xlive/web-ucenter/v1/xfetter/GetWebList",
                new Dictionary<string, string> { ["hit_ab"] = "true" },
                referer: "https://live.bilibili.com/");
            if (code != 0)
                return (false, code == -352 ? "B 站风控拦截(请稍后再试)" : (msg ?? $"查询关注直播失败 (code {code})"), null);
            // code=0 但没有 data: 当作"没人在播"处理成空列表, 不能报失败 ——
            // 失败分支会保留上一次的列表, 主播下播后看板会一直挂着旧条目。
            if (data == null) return (true, null, new List<VideoItem>());

            // rooms 与 list 内容相同(接口两个字段都给); rooms 缺失时退回 list
            if (!TryGetLiveArray(data.Value, out var rooms))
                return (true, null, new List<VideoItem>());

            var items = new List<VideoItem>();
            var seenRooms = new HashSet<long>();
            foreach (var e in rooms.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                // live_status: 0=未开播, 1=直播中, 2=轮播(轮播不算"正在直播")
                if (GetIntSafe(e, "live_status") != 1) continue;

                // room_id 是真实房间号; 老字段 roomid / short_id 只作兜底
                var roomId = GetLongSafe(e, "room_id");
                if (roomId <= 0) roomId = GetLongSafe(e, "roomid");
                if (roomId <= 0) roomId = GetLongSafe(e, "short_id");
                if (roomId <= 0 || !seenRooms.Add(roomId)) continue;

                // hit_ab=true 时 room_cover/cover_from_user/keyframe 都为空,
                // 但 face(主播头像)始终有值 —— 看板头像本来就更适合用 face。
                var cover = GetStr(e, "face");
                if (cover.Length == 0) cover = GetStr(e, "cover_from_user");
                if (cover.Length == 0) cover = GetStr(e, "keyframe");
                if (cover.Length == 0) cover = GetStr(e, "room_cover");

                var author = GetStr(e, "uname");
                if (author.Length == 0) author = GetStr(e, "nickname");

                items.Add(new VideoItem
                {
                    RoomId = roomId,
                    Title = GetStr(e, "title").StripHtml(),
                    Author = author,
                    OwnerMid = GetLongSafe(e, "uid"),
                    Cover = UrlUtil.Normalize(cover),
                    Duration = "直播中",
                    ViewCount = Math.Max(0, GetLongSafe(e, "online"))
                });
            }
            // 按 人气 倒序: 看板是"谁在播"的快照, 热的排前面。
            // (hit_ab=true 时 online 恒为 0, 此时保持接口原顺序即可。)
            if (items.Exists(i => i.ViewCount > 0))
                items.Sort((a, b) => b.ViewCount.CompareTo(a.ViewCount));
            return (true, null, items);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null);
        }
    }

    /// <summary>从 GetWebList 的 data 里取直播房间数组(rooms 优先, list 兜底)。</summary>
    private static bool TryGetLiveArray(JsonElement data, out JsonElement rooms)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("rooms", out rooms) && rooms.ValueKind == JsonValueKind.Array)
            return true;
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("list", out rooms) && rooms.ValueKind == JsonValueKind.Array)
            return true;
        rooms = default;
        return false;
    }

    /// <summary>
    /// 取某个直播间的播放地址(单条可直接喂给 LibVLC 的 URL)。
    ///
    /// 接口: /xlive/web-room/v2/index/getRoomPlayInfo —— 返回的是一棵树:
    ///   data.playurl_info.playurl.stream[]  (按协议分: http_stream / http_hls)
    ///     └ format[]  (按封装分: flv / ts / fmp4)
    ///         └ codec[]  (AVC / HEVC)
    ///             └ url_info[]  → host + base_url 拼出完整地址
    /// 优先选 http_stream + flv: 延迟最低, LibVLC 也最稳; 拿不到再退回列表里第一个。
    /// </summary>
    public async Task<(bool ok, string? err, string? url)> GetLivePlayUrlAsync(long roomId)
    {
        try
        {
            if (roomId <= 0) return (false, "缺少直播间号", null);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo",
                new Dictionary<string, string>
                {
                    ["room_id"] = roomId.ToString(),
                    ["no_playurl"] = "0",
                    ["mask"] = "1",
                    ["qn"] = "0",              // 0 = 让服务端给默认清晰度(原画由登录态决定)
                    ["platform"] = "web",
                    ["protocol"] = "0,1",
                    ["format"] = "0,1,2",
                    ["codec"] = "0,1",
                    ["dolby"] = "5",
                    ["panorama"] = "1"
                },
                referer: $"https://live.bilibili.com/{roomId}");
            if (code != 0 || data == null) return (false, msg ?? $"请求失败 (code {code})", null);

            // live_status: 0 未开播 / 1 直播中 / 2 轮播。没开播的话后面拿不到流, 直接说清楚。
            if (data.Value.TryGetProperty("live_status", out var ls) &&
                ls.ValueKind == JsonValueKind.Number && ls.GetInt32() == 0)
                return (false, "主播还没有开播", null);

            var url = PickLiveUrl(data.Value);
            return url == null ? (false, "没有取到可用的直播流地址", null) : (true, null, url);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>从 getRoomPlayInfo 的响应里挑一个播放地址</summary>
    private static string? PickLiveUrl(JsonElement data)
    {
        if (!data.TryGetProperty("playurl_info", out var pi) || pi.ValueKind != JsonValueKind.Object)
            return null;
        if (!pi.TryGetProperty("playurl", out var pu) || pu.ValueKind != JsonValueKind.Object)
            return null;
        if (!pu.TryGetProperty("stream", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return null;

        string? first = null;
        foreach (var st in streams.EnumerateArray())
        {
            var protocol = GetStr(st, "protocol_name");
            if (!st.TryGetProperty("format", out var formats) || formats.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var f in formats.EnumerateArray())
            {
                var format = GetStr(f, "format_name");
                if (!f.TryGetProperty("codec", out var codecs) || codecs.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var cd in codecs.EnumerateArray())
                {
                    if (!cd.TryGetProperty("url_info", out var infos) ||
                        infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0)
                        continue;

                    var host = GetStr(infos[0], "host");
                    // base_url 自带一串 query(过期时间/签名), 但**必须去掉**:
                    // 真正要保留的是 url_info[0].extra, 它才是配套的参数。直接拼 base_url 的
                    // 原始 query 在多数 CDN 上也能用, 但遇到需要 extra 的节点会 403 —— 统一只取路径。
                    var path = GetStr(cd, "base_url");
                    var q = path.IndexOf('?');
                    if (q >= 0) path = path[..q];
                    if (host.Length == 0 || path.Length == 0) continue;

                    var extra = GetStr(infos[0], "extra");
                    // extra 实测有不带 '?' 的(直接就是 "expires=..."), 直接拼会变成 ".flvexpires=...",
                    // 整条 CDN 全部 403/404 —— 表现就是"视频播放出错, 请检查网络或尝试其他清晰度"。
                    if (extra.Length > 0 && !extra.StartsWith('?')) extra = "?" + extra;
                    var full = host + path + extra;

                    // http_stream(原流) + flv 是最优解: 延迟低、LibVLC 支持最成熟
                    if (protocol == "http_stream" && format == "flv") return full;
                    first ??= full;
                }
            }
        }
        return first;
    }

}
