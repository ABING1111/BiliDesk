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
    /// 获取登录用户的**直播**观看历史。
    ///
    /// ★★★ 接口选型(2026-10-03 实测定的, 别换回 /x/v2/history):
    ///   旧的那条 `/x/v2/history` **只给视频稿件**, 直播条目它根本不返回; 而且 `type=live`
    ///   作为参数传过去会直接 -400(实测)。
    ///   真正管用的是**新游标接口** `/x/web-interface/history/cursor`, 它自带一个 `tab` 数组:
    ///       [{type:"archive",name:"视频"}, {type:"live",name:"直播"}, {type:"article",name:"专栏"}]
    ///   —— 这正是官方历史页那三个标签, 说明官方自己就是靠 `type` 分流的。传 `type=live`
    ///   返回的就是真实直播记录(实测拿到 kid=22603245 / uri=https://live.bilibili.com/22603245)。
    ///   ★ 参数名必须是 **type**。`business=live` / `tab=live` / `business_type=live` 都会被忽略、
    ///     静默返回视频记录(实测), 那种"看起来成功但内容不对"的错最难查。
    ///
    /// 分页: 游标式 —— 用上一页返回的 `cursor.view_at` 与 `cursor.max` 当下一页的
    /// `view_at` / `max`。这里对外仍保持"页码"形状(pn), 内部用保存下来的游标推进,
    /// 免得把游标状态泄漏给页面/VM。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetLiveHistoryAsync(int pn = 1)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "直播历史需要登录", null);
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["type"] = "live",
                ["ps"] = "20"
            };
            // 第 1 页不带游标; 之后用上一次拿到的游标(见 _liveCursor)
            if (pn > 1 && _liveCursorViewAt > 0)
            {
                ps["view_at"] = _liveCursorViewAt.ToString();
                ps["max"] = _liveCursorMax.ToString();
            }

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/history/cursor", ps,
                referer: "https://www.bilibili.com/account/history");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in list.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;

                    // 直播记录的"房间号"有两个来源, 都试一遍:
                    //   · 顶层 kid —— 实测就是房间号(22603245);
                    //   · history.oid —— 同为房间号, 顶层缺失时兜底。
                    var roomId = GetLong(h, "kid");
                    if (roomId <= 0 && h.TryGetProperty("history", out var hist))
                        roomId = GetLong(hist, "oid");
                    if (roomId <= 0) continue;

                    var cover = GetStr(h, "cover");
                    if (cover.Length == 0 && h.TryGetProperty("covers", out var covers) &&
                        covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0)
                        cover = covers[0].GetString() ?? "";

                    // 主播名 / mid: 顶层 author_name / author_mid(与视频那条同一组字段名)
                    var author = GetStr(h, "author_name");
                    var ownerMid = GetLong(h, "author_mid");
                    if (h.TryGetProperty("owner", out var owner))
                    {
                        if (author.Length == 0) author = GetStr(owner, "name");
                        if (ownerMid <= 0) ownerMid = GetLong(owner, "mid");
                    }

                    items.Add(new VideoItem
                    {
                        RoomId = roomId,
                        Title = GetStr(h, "title").StripHtml(),
                        Cover = UrlUtil.Normalize(cover),
                        Author = author,
                        OwnerMid = ownerMid,
                        // 时长位显示开播状态: live_status 1=直播中, 0=未开播(接口还给 badge 文案)
                        Duration = GetInt(h, "live_status") == 1 ? "直播中" : "未开播",
                        ViewCount = 0,   // 历史接口不给人气, 留 0 让卡片隐藏那个胶囊
                        Pubdate = GetLong(h, "view_at"),
                    });
                }
            }

            // 记下游标供下一页用。到底时 cursor 会归零(max/view_at 都是 0), 下次自然空手而归。
            if (data.Value.TryGetProperty("cursor", out var cur))
            {
                _liveCursorViewAt = GetLong(cur, "view_at");
                _liveCursorMax = GetLong(cur, "max");
            }
            else
            {
                _liveCursorViewAt = 0;
                _liveCursorMax = 0;
            }

            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>直播历史的游标(接口是游标式, 不是页码式)。由 <see cref="GetLiveHistoryAsync"/> 维护。</summary>
    private long _liveCursorViewAt;
    private long _liveCursorMax;

    /// <summary>重置直播历史游标(重新从头拉之前调, 否则第 2 页会接着上次的位置)</summary>
    public void ResetLiveHistoryCursor()
    {
        _liveCursorViewAt = 0;
        _liveCursorMax = 0;
    }

    /// <summary>获取登录用户的云端观看历史(x/v2/history, 分页)。
    /// 返回 (ok, err, items): items 为 VideoItem 列表, IsSelected 可复用</summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetCloudHistoryAsync(int pn = 1, int ps = 30)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录", null);
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/history",
                new Dictionary<string, string>
                {
                    ["pn"] = pn.ToString(),
                    ["ps"] = ps.ToString(),
                    ["platform"] = "web"
                },
                sign: true,
                referer: "https://www.bilibili.com/account/history");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in data.Value.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;
                    var bvid = GetStr(h, "bvid");
                    if (string.IsNullOrEmpty(bvid)) continue;
                    // 新旧版字段兼容: pic/cover, author_name/owner.name
                    var cover = GetStr(h, "pic");
                    if (cover.Length == 0 && h.TryGetProperty("covers", out var covers) &&
                        covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0)
                        cover = covers[0].GetString() ?? "";
                    string author = GetStr(h, "author_name");
                    // 历史接口的 UP 主 mid 在顶层 author_mid; 老版结构在 owner.mid
                    long ownerMid = GetLong(h, "author_mid");
                    if (h.TryGetProperty("owner", out var owner))
                    {
                        if (author.Length == 0) author = GetStr(owner, "name");
                        if (ownerMid <= 0) ownerMid = GetLong(owner, "mid");
                    }
                    items.Add(new VideoItem
                    {
                        Bvid = bvid,
                        Aid = GetLong(h, "aid"),
                        Title = GetStr(h, "title").StripHtml(),
                        Cover = UrlUtil.Normalize(cover),
                        Author = author,
                        OwnerMid = ownerMid,
                        Duration = VideoItem.FormatSeconds(GetInt(h, "duration")),
                        // 播放量在 **stat.view** 里, 顶层没有 view 字段。
                        // 之前写成 GetLong(h, "view") 拿到的一直是 0, 于是云端历史每条都显示"0 播放"。
                        // 另外 stat.view 为 -1 表示"播放量被屏蔽", 当成 0 处理而不是显示负数。
                        ViewCount = Math.Max(0, GetNestedLong(h, "stat", "view")),
                        DanmakuCount = Math.Max(0, GetNestedLong(h, "stat", "danmaku")),
                        LikeCount = Math.Max(0, GetNestedLong(h, "stat", "like")),
                        Pubdate = GetLong(h, "view_at")
                    });
                }
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

}
