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
    /// 当前登录用户的收藏夹列表(**带封面/隐私字段**，「我的」页的文件夹卡片用)。
    /// 接口: /x/v3/fav/folder/created/list(分页接口, pn=1/ps=50 一次拿够)。
    /// 与 GetFavFoldersAsync(list-all) 的区别: 那个只取 id/标题/数量(收藏页 chip 够用),
    /// 这个才下发 cover 和 privacy。失败语义与 GetFavFoldersAsync 一致。
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetFavFoldersFullAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["pn"] = "1",
                    ["ps"] = "50",
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist");
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
                    // ★ 公开/私密不是 privacy 字段 —— 列表接口里根本没有它, 藏在 attr 位掩码里:
                    //   attr bit0 = 是否默认收藏夹, attr bit1 = 1 表示私密(bilibili-API-collect 收藏夹文档)。
                    //   之前按 privacy 解析永远是 0, 私密夹全显示成"公开"。
                    var attr = GetLong(f, "attr");
                    var title = GetStr(f, "title");
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = title,
                        MediaCount = GetInt(f, "media_count"),
                        Cover = UrlUtil.Normalize(GetStr(f, "cover")),
                        Privacy = (attr & 2) != 0 ? 1 : 0,
                        IsDefault = IsDefaultFavFolder(title)
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
    /// 我订阅的 UGC 合集（「我的订阅」页的「合集」tab / 「我的」页预览）。
    ///
    /// 接口: /x/v3/fav/folder/collected/list —— 收藏与订阅混在同一个列表里:
    ///   type=21 = 订阅的 UGC 合集; type=11 = 收藏的收藏夹（不属于订阅, 这里过滤掉）。
    ///
    /// ★★ 没有"只要合集"的服务端筛选: 响应里的 count 是两类之和, 不能当合集总数。
    ///    调用方按 has_more 翻页, 自己累计"已加载 N 个合集"。
    /// ★ 签名: 先按同域 created/list 的写法走 WBI(sign: true); PiliPlus 的实现是无签名 ——
    ///   真实账号下若被拒, 再退回无签名的调用(注释保留这条退路)。
    /// ★ 需要登录; 未登录直接返回失败, 与收藏夹接口同一套语义。
    /// </summary>
    public async Task<(bool ok, string? err, List<SpaceCollectionItem>? items, bool hasMore)>
        GetSubscribedCollectionsAsync(long mid, int pn = 1, int ps = 20)
    {
        if (mid <= 0) return (false, "用户 ID 无效", null, false);
        if (!SessionManager.Instance.HasLogin) return (false, "云端订阅需要登录", null, false);

        try
        {
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/collected/list",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid.ToString(),
                    ["pn"] = Math.Max(1, pn).ToString(),
                    ["ps"] = Math.Clamp(ps, 1, 50).ToString(),
                    ["platform"] = "web"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, false);
            if (data.Value.ValueKind != JsonValueKind.Object)
                return (true, null, new List<SpaceCollectionItem>(), false);

            var items = new List<SpaceCollectionItem>();
            if (data.Value.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    // 11 = 收藏的收藏夹; 只有 21 才是订阅的 UGC 合集
                    if (GetInt(f, "type") != 21) continue;
                    var id = GetLong(f, "id");
                    if (id <= 0) continue;

                    var updated = GetLong(f, "mtime");
                    if (updated <= 0) updated = GetLong(f, "ctime");
                    items.Add(new SpaceCollectionItem
                    {
                        Kind = SpaceCollectionKind.Season,
                        SeasonId = id,
                        Mid = GetLong(f, "mid"),
                        Title = GetStr(f, "title"),
                        Cover = UrlUtil.Normalize(GetStr(f, "cover")),
                        Intro = GetStr(f, "intro"),
                        Count = GetInt(f, "media_count"),
                        UpdatedAt = updated,
                        // 卡片封面右下角已有「合集」角标, 标题不再重复前缀
                        ShowKindPrefix = false
                    });
                }
            }

            var hasMore = data.Value.TryGetProperty("has_more", out var hm) &&
                          hm.ValueKind == JsonValueKind.True;
            return (true, null, items, hasMore);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, false);
        }
    }


    /// 接口: POST /x/v3/fav/folder/add (需要 csrf, 所以必须登录)。
    ///
    /// 参数: title 名称 / intro 简介(这里固定空串) / privacy 0 公开 1 私密。
    /// ★ 名称长度、重名、收藏夹数量上限全部由服务端判: 这里**不**替服务端做业务裁决,
    ///   只把 message 原样透出(界面侧另有一层"空/超长"的前置校验, 见 Helpers/FavFolderName)。
    /// ★ 返回的 <c>data</c> 里才有新夹的 id: 解析不到 id 时返回 ok + folder=null,
    ///   由调用方走"重新拉一次列表"的兜底 —— 不要在这里造一个假 id。
    /// </summary>
    public async Task<(bool ok, string? err, FavFolder? folder)> CreateFavFolderAsync(
        string title, int privacy = 0)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) return (false, "收藏夹名称不能为空", null);
        if (!SessionManager.Instance.HasLogin) return (false, "新建收藏夹需要登录", null);
        var mid = SessionManager.Instance.Current.DedeUserID;

        var (code, msg, data) = await PostFormDataAsync(
            "https://api.bilibili.com/x/v3/fav/folder/add",
            new Dictionary<string, string>
            {
                ["title"] = title,
                ["intro"] = "",
                ["privacy"] = privacy == 1 ? "1" : "0",
                ["platform"] = "web",
                ["web_location"] = "333.1387"
            },
            referer: $"https://space.bilibili.com/{mid}/favlist");
        if (code != 0) return (false, msg ?? $"请求失败 (code {code})", null);

        FavFolder? folder = null;
        if (data.HasValue && data.Value.ValueKind == JsonValueKind.Object)
        {
            var d = data.Value;
            var id = GetLong(d, "id");
            if (id > 0)
            {
                // 与 GetFavFoldersFullAsync 同一套隐私解析: attr bit1 = 私密。
                // 服务端没回 attr 时按请求时的 privacy 兜底(用户刚选的就是他要的)。
                var attr = GetLong(d, "attr");
                var name = GetStr(d, "title");
                folder = new FavFolder
                {
                    Id = id,
                    Title = name.Length > 0 ? name : title,
                    MediaCount = GetInt(d, "media_count"),
                    Cover = UrlUtil.Normalize(GetStr(d, "cover")),
                    Privacy = (attr & 2) != 0 || privacy == 1 ? 1 : 0
                };
            }
        }
        return (true, null, folder);
    }

    /// <summary>
    /// 删除一个云端收藏夹。
    /// 接口: POST /x/v3/fav/folder/del, 参数 media_ids(mlid, 多个用逗号分隔) + csrf。
    ///
    /// ★ **默认收藏夹删不掉** —— 服务端会拒绝。界面侧已按 <see cref="FavFolder.IsDefault"/>
    ///   把入口禁掉了, 这里的返回仍然把服务端 message 原样透出, 防的是"属性位解析不到"
    ///   这类边角情况: 那时用户会看到一句真实的拒绝原因, 而不是一个假成功。
    /// ★ 删除不可撤销: 调用方必须先确认(见 Helpers/FavFolderActions.DeleteAsync)。
    /// </summary>
    public async Task<(bool ok, string? err)> DeleteFavFolderAsync(long mediaId)
    {
        if (mediaId <= 0) return (false, "缺少收藏夹 id");
        if (!SessionManager.Instance.HasLogin) return (false, "删除收藏夹需要登录");

        var mid = SessionManager.Instance.Current.DedeUserID;
        return await PostFormAsync(
            "https://api.bilibili.com/x/v3/fav/folder/del",
            new Dictionary<string, string>
            {
                ["media_ids"] = mediaId.ToString(),
                ["platform"] = "web"
            },
            referer: $"https://space.bilibili.com/{mid}/favlist");
    }

}
