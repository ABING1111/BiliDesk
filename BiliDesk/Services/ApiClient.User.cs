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
    /// 登录用户信息(nav)。
    ///
    /// ★ 2026-10-01 由裸 <c>Task&lt;UserState?&gt;</c> 改为 (ok, err, data):
    ///   原来"未登录"和"请求失败"都塌成同一个 null, 而这个方法有 5 个调用点,
    ///   其中 MainViewModel.RefreshUserAsync 靠它判断登录态 —— 分不清两者时,
    ///   网络抖一下也会被当成"登录过期", 界面上直接显示"登录状态已过期"。
    /// </summary>
    public async Task<(bool ok, string? err, UserState? data)> GetUserStateAsync()
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/nav", null);
            if (code != 0 || data == null) return (false, msg ?? $"nav 请求失败 (code {code})", null);

            var d = data.Value;
            // 注意: 未登录时 nav 仍返回 code=0, 只是 isLogin=false —— 这是**正常状态**不是错误,
            // 所以 ok=true(查询本身成功), 由调用方看 data.IsLogin 决定怎么显示。
            if (!d.TryGetProperty("isLogin", out var l) || !l.GetBoolean())
                return (true, null, new UserState { IsLogin = false });

            var u = new UserState
            {
                IsLogin = true,
                Name = GetStr(d, "uname"),
                Face = UrlUtil.Normalize(GetStr(d, "face")),
                Mid = GetLong(d, "mid"),
                Level = d.TryGetProperty("level_info", out var li) ? GetInt(li, "current_level") : 0,
                Coins = d.TryGetProperty("money", out var mo) && mo.ValueKind == JsonValueKind.Number
                    ? mo.GetDouble() : 0
            };
            // 经验值(「我的」页显示"经验 x/y"): level_info.current_exp / next_exp
            if (d.TryGetProperty("level_info", out var lev))
            {
                u.ExperienceCurrent = GetLong(lev, "current_exp");
                u.ExperienceNext = GetLong(lev, "next_exp");
            }
            return (true, null, u);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取登录信息失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 用户关系数(关注/粉丝)。接口 /x/relation/stat, 匿名即可用(2026-09-26 实测, 与登录态无关)。
    ///
    /// ★ 2026-10-01 去掉 (-1,-1) 哨兵, 改为 (ok, err, following, follower):
    ///   哨兵值的坏处是"真的 0 个粉丝"和"取不到"都是 -1, 调用方必须再自己判一次,
    ///   而且失败原因(B 站风控? 未登录? 网络?)一点都传不出来。现在 ok=false 时 err 说明原因,
    ///   界面再决定显示 "--"。
    /// </summary>
    public async Task<(bool ok, string? err, long following, long follower)> GetRelationStatAsync(long mid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation/stat",
                new Dictionary<string, string> { ["vmid"] = mid.ToString() },
                referer: $"https://space.bilibili.com/{mid}/");
            if (code != 0 || data == null) return (false, msg ?? $"relation/stat 请求失败 (code {code})", 0, 0);
            return (true, null, GetLong(data.Value, "following"), GetLong(data.Value, "follower"));
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取关注/粉丝数失败: " + ex.Message, 0, 0);
        }
    }

    /// <summary>
    /// 当前登录用户的计数三件套: 关注 / 粉丝 / 动态数。
    /// 接口: /x/web-interface/nav/stat(需要登录 Cookie, api.bilibili.com 域)。
    ///
    /// ★ 为什么不再用 api.vc.bilibili.com 的 dynamic_svr/num: 实测(2026-09-26)匿名请求
    /// 被风控页拦(返 HTML), 应用内带 Cookie 也拿不到 → 界面显示 "--"。
    /// nav/stat 一次给齐三个数, 动态数字段是 dynamic_count。
    /// ★ 2026-10-01 去掉 (-1,-1,-1) 哨兵, 改为 (ok, err, ...) —— 同 GetRelationStatAsync。
    /// </summary>
    public async Task<(bool ok, string? err, long following, long follower, long dynamicCount)> GetUserNavStatAsync()
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/nav/stat", null);
            if (code != 0 || data == null) return (false, msg ?? $"nav/stat 请求失败 (code {code})", 0, 0, 0);
            return (true, null,
                GetLong(data.Value, "following"),
                GetLong(data.Value, "follower"),
                GetLong(data.Value, "dynamic_count"));
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取用户计数失败: " + ex.Message, 0, 0, 0);
        }
    }

    /// <summary>
    /// 粉丝列表(某人的粉丝明细)。接口: GET /x/relation/followers (vmid / pn / ps)
    ///
    /// ★ 2026-11-18 新增(issue #90「我的」页要看粉丝列表):
    ///   关注列表已有 <see cref="GetFollowingsAsync"/>, 粉丝列表之前完全没有 —— 这次补上。
    ///
    /// ★★ **每页条数上限是 20**(关注列表那个接口是 50) —— 这是服务端硬上限,
    ///   请求更大的 ps 会被静默截断。所以两个接口的页大小**不能共用常量**,
    ///   调用方必须各自写死自己那个上限(见 <see cref="FollowerPageSize"/> 的说明)。
    ///
    /// ★ 需要登录 Cookie(未登录回 -101); Referer 必须是 bilibili.com 子域名
    ///   (这里给 space.bilibili.com/{vmid}/fans), 否则 -352 风控。
    ///
    /// ★ 隐私设置: 用户把粉丝列表设成不可见时服务端回 **22118**(关注列表则是 22115),
    ///   这里转成人话 —— 界面上直接甩一个裸 code 用户看不懂。
    ///
    /// ★ 数据形状与关注列表**完全一致**(mid/uname/face/sign/official_verify + total),
    ///   所以直接复用 <see cref="ParseFollowUserList"/>(同一 partial class 内, 无需改可见性) ——
    ///   另写一份解析器必然漂移。
    /// </summary>
    public async Task<(bool ok, string? err, List<FollowUser>? list, int total)> GetFollowersAsync(
        long vmid, int pn = 1, int ps = FollowerPageSize)
    {
        if (vmid <= 0) return (false, "缺少用户 UID", null, 0);
        try
        {
            await EnsureBuvidAsync();
            var ps2 = new Dictionary<string, string>
            {
                ["vmid"] = vmid.ToString(),
                ["pn"] = pn.ToString(),
                ["ps"] = Math.Min(ps, FollowerPageSize).ToString(),
                ["order"] = "desc"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation/followers", ps2,
                referer: $"https://space.bilibili.com/{vmid}/fans");
            if (code != 0 || data == null)
            {
                var errMsg = msg ?? $"请求失败 (code {code})";
                if (code == -101) errMsg = "请先登录";
                // ★ 隐私设置: 粉丝列表不可见。别把裸 code 甩给用户。
                else if (code == 22118) errMsg = "该用户把粉丝列表设成了不可见";
                else if (code == 22115) errMsg = "该用户把关注列表设成了不可见";
                return (false, errMsg, null, 0);
            }

            var users = ParseFollowUserList(data.Value, out var total);
            return (true, null, users, total);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, 0);
        }
    }

    /// <summary>
    /// 粉丝列表每页条数的**服务端硬上限**(20)。
    ///
    /// ★ 别跟关注列表的 50 混用: 关注接口给 50, 粉丝接口只给 20, 请求更大的会被静默截断
    ///   (表现为"每页只有 20 条"却没有任何报错, 分页却按 20 走 —— 看着像到底了)。
    /// </summary>
    public const int FollowerPageSize = 20;

    // ------------------------------------------------------------ 黑名单(issue #98)

    /// <summary>黑名单每页条数(服务端接受 50; 用满少打几次请求)。</summary>
    private const int BlacklistPageSize = 50;

    /// <summary>一次导入最多取多少条 —— 与 FilterService 的 MaxRulesPerKind(500)对齐。</summary>
    public const int BlacklistImportLimit = 500;

    /// <summary>
    /// 当前登录账号的 B 站黑名单(2026-11-20, issue #98: 「已屏蔽的 UP 主」支持导入)。
    ///
    /// ★ 接口需要登录 Cookie + WBI 签名(/x/relation/blacks), 未登录返回 -101, 统一转成"请先登录"。
    /// ★ 部分版本**只返回 mid**、不含昵称(实测响应形状随灰度变化):
    ///   昵称字段按 uname / name 依次尝试, 都没有就让调用方用 "UID {mid}" 占位 ——
    ///   屏蔽匹配优先按 mid, 昵称只是给面板显示用的。
    /// </summary>
    public async Task<(bool ok, string? err, List<(long Mid, string Name)> data)> GetBlacklistAsync(
        int maxCount = BlacklistImportLimit)
    {
        var result = new List<(long Mid, string Name)>();
        if (maxCount <= 0) return (true, null, result);

        try
        {
            var total = int.MaxValue;
            for (var pn = 1; result.Count < maxCount && pn <= 20; pn++)
            {
                var (code, msg, data) = await GetJsonAsync(
                    "https://api.bilibili.com/x/relation/blacks",
                    new Dictionary<string, string>
                    {
                        ["pn"] = pn.ToString(),
                        ["ps"] = BlacklistPageSize.ToString(),
                    },
                    sign: true,
                    referer: "https://www.bilibili.com/");
                if (code != 0 || data == null)
                {
                    var errMsg = msg ?? $"获取黑名单失败 (code {code})";
                    if (code == -101) errMsg = "请先登录后再导入黑名单";
                    return (false, errMsg, result);
                }

                if (data.Value.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
                    total = t.GetInt32();

                if (!data.Value.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                    break;

                var pageCount = 0;
                foreach (var item in list.EnumerateArray())
                {
                    var mid = GetLong(item, "mid");
                    if (mid <= 0) continue;

                    var name = GetStr(item, "uname");
                    if (string.IsNullOrWhiteSpace(name)) name = GetStr(item, "name");
                    name = name.Trim();
                    if (name.Length == 0) name = $"UID {mid}";

                    result.Add((mid, name));
                    pageCount++;
                    if (result.Count >= maxCount) break;
                }

                // 空页 / 已取满 / 已到总数: 三种收口, 否则会用空页无限翻下去
                if (pageCount == 0 || result.Count >= total) break;
            }

            return (true, null, result);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取黑名单失败: " + ex.Message, result);
        }
    }
}
