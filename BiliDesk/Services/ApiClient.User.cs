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

}
