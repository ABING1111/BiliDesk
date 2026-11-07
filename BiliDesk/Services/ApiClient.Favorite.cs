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
    /// 当前登录用户的收藏夹列表(**带封面/隐私字段**, 「我的」页的文件夹卡片用)。
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
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count"),
                        Cover = UrlUtil.Normalize(GetStr(f, "cover")),
                        Privacy = (attr & 2) != 0 ? 1 : 0
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

}
