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
    /// <summary>POST 表单 + 自动加 CSRF / Cookie / Referer / 错误码解析</summary>
    private async Task<(bool ok, string? err)> PostFormAsync(string url, Dictionary<string, string> ps, string? referer = null)
    {
        var (code, msg) = await PostFormCodeAsync(url, ps, referer);
        return code == 0 ? (true, null) : (false, msg);
    }

    /// <summary>
    /// 与 PostFormAsync 相同, 但把业务码原样返回。
    /// 少数接口需要区分具体错误码才能判断"算不算成功" —— 例如收藏的
    /// 11201(已经收藏过了) / 11202(已经取消收藏了)。
    /// </summary>
    private async Task<(int code, string? msg)> PostFormCodeAsync(
        string url, Dictionary<string, string> ps, string? referer = null)
    {
        var csrf = SessionManager.Instance.Current.BiliJct;
        if (string.IsNullOrEmpty(csrf)) return (-101, "未登录");
        ps["csrf"] = csrf;
        try
        {
            // ★ 写操作(点赞/投币/收藏/三连)的风控门比读接口严, 实测要点见下面 EnsureWriteReadyAsync。
            //   在真正发写请求前把设备身份预热一次, 不做的话新登录用户第一次点赞极易撞 -352。
            await EnsureWriteReadyAsync();

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Cookie",
                AppendFingerprint(SessionManager.Instance.BuildCookieHeader()));
            if (!string.IsNullOrEmpty(referer))
                req.Headers.TryAddWithoutValidation("Referer", referer);
            // ★ Origin 必须与 Referer 同源: B 站 web 端所有 POST 都带这个头, 缺了
            //   读接口照样过, 写接口却会被当成"非浏览器客户端"直接判风控。
            req.Headers.TryAddWithoutValidation("Origin", OriginFor(referer));
            req.Content = new FormUrlEncodedContent(ps);
            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
            if (code != 0)
            {
                var errMsg = "code " + code;
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    errMsg = m.GetString() ?? errMsg;
                return (code, errMsg);
            }
            return (0, null);
        }
        catch (Exception ex) { return (-999, ex.Message); }
    }

    /// <summary>
    /// 对**评论**点赞 / 取消点赞。
    ///
    /// 接口: `POST /x/v2/reply/action` —— `oid` 是视频 aid、`type=1`(稿件)、`rpid` 是评论 id、
    /// `action` 1 = 赞 / 0 = 取消赞。要带 csrf(bili_jct), 所以必须登录。
    /// 注意它和视频点赞是**两套接口**, 别混: 视频走 `/x/web-interface/archive/like`(like=1/2)。
    ///
    /// ★ **点踩是另一个接口**(`/x/v2/reply/hate`), 见 <see cref="CommentDislikeAsync"/>。
    ///   2026-09-27 修: 之前把踩也当成 action=2 发给这个接口, 服务端直接回
    ///   `12011 不合法的赞或踩`, 表现就是"点踩点了报错"。
    /// </summary>
    public Task<(bool ok, string? err)> CommentLikeAsync(long aid, long rpid, bool like, int type = 1)
        => CommentReactionAsync("https://api.bilibili.com/x/v2/reply/action", aid, rpid, like, type);

    /// <summary>
    /// 对**评论**点踩 / 取消点踩。
    ///
    /// 接口: `POST /x/v2/reply/hate`, 参数与点赞完全一样, 只是 `action` 的含义变成
    /// 1 = 踩 / 0 = 取消踩。同样要带 csrf, 同样必须登录。
    ///
    /// 赞与踩的服务端行为是**互相覆盖**: 点赞成功会自动消掉踩、点踩成功会自动消掉赞
    /// (这是接口文档写明的副作用, 不是我们本地凑出来的规矩) —— 所以界面上的两个按钮状态
    /// 也必须互斥, 否则会出现"两个都高亮但服务端只认后一个"的假象。
    /// 另外踩**没有计数**: 接口不下发 dislike 总数, 所以踩只有状态、没有数字。
    /// </summary>
    public Task<(bool ok, string? err)> CommentDislikeAsync(long aid, long rpid, bool dislike, int type = 1)
        => CommentReactionAsync("https://api.bilibili.com/x/v2/reply/hate", aid, rpid, dislike, type);

    /// <summary>赞与踩的公共实现(两个接口的参数、返回值、错误码完全一致, 只有地址不同)</summary>
    private async Task<(bool ok, string? err)> CommentReactionAsync(
        string url, long aid, long rpid, bool on, int type = 1)
    {
        if (aid <= 0 || rpid <= 0) return (false, "参数不完整");
        var (code, msg) = await PostFormCodeAsync(
            url,
            new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = type.ToString(),
                ["rpid"] = rpid.ToString(),
                ["action"] = on ? "1" : "0"
            },
            referer: type is 11 or 17 ? "https://t.bilibili.com/" : "https://www.bilibili.com/");
        if (code == 0) return (true, null);

        // 这串错误码里有一半是"用户能看懂并且知道该怎么办"的, 直接说人话;
        // 剩下的原样透出服务端 message(带上 code 更好排查)。
        var friendly = code switch
        {
            -101 => "请先登录后再操作",
            -111 => "登录状态已失效, 请重新登录",
            -400 => "评论可能已被删除",
            -404 => "评论不存在或已被删除",
            -509 => "操作太频繁, 稍后再试",
            12002 => "该视频的评论区已关闭",
            12004 => "该评论禁止赞或踩(可能被折叠或已被删除)",
            12006 => "评论不存在或已被删除",
            12009 => "评论区类型不合法(这是客户端 bug, 请反馈)",
            12011 => "服务端不认这个操作(这是客户端 bug, 请反馈)",
            _ => msg
        };
        return (false, friendly);
    }

    /// <summary>
    /// 发表评论, 或**回复楼中楼**。
    ///
    /// 接口: `POST /x/v2/reply/add` —— `oid` 是**视频 aid**(不是 bvid)、`type=1`(稿件)、
    /// `message` 是正文(带换行也行, 表单编码会处理)。要带 csrf(bili_jct), 所以必须登录。
    /// `plat=1` 声明"来自 web 端", 少了它评论会以"未知来源"落库(网页版看不到来源标识)。
    ///
    /// 回复楼中楼时必须**同时**带 `root`(顶层评论 id) 与 `parent`(被回复的那条 id) ——
    /// 实测(2026-09-25 抓包): 只回复楼主时两者相等; 回复楼中楼里的某个人时 root 仍是顶层、
    /// parent 指向那个人。少了 root 会被服务端当成普通评论挂到根上(表现是"回错位置")。
    /// </summary>
    /// <param name="rootRpid">要回复的**顶层评论** id; 0 = 发普通评论(挂在新楼层上)</param>
    /// <param name="parentRpid">被回复的那条 id; 传 0 表示"回复楼主"(自动用 rootRpid)</param>
    public async Task<(bool ok, string? err)> PostCommentAsync(
        long aid, string message, long rootRpid = 0, long parentRpid = 0,
        int type = 1, string? referer = null)
    {
        if (aid <= 0) return (false, type == 17 ? "没拿到动态 id, 暂时不能评论" : "没拿到视频 aid, 暂时不能评论");
        if (string.IsNullOrWhiteSpace(message)) return (false, "评论内容不能为空");
        // B 站单条评论上限 1000 字。界面里也卡了 MaxLength, 这里再兜一次 ——
        // 超长会被服务端整条拒掉, 用户辛辛苦苦写的字就白费了。
        if (message.Length > 1000) message = message[..1000];

        var form = new Dictionary<string, string>
        {
            ["oid"] = aid.ToString(),
            ["type"] = type.ToString(),
            ["message"] = message,
            ["plat"] = "1",
            ["ordering"] = "heat"
        };
        if (rootRpid > 0)
        {
            form["root"] = rootRpid.ToString();
            form["parent"] = (parentRpid > 0 ? parentRpid : rootRpid).ToString();
        }

        var (code, msg) = await PostFormCodeAsync(
            "https://api.bilibili.com/x/v2/reply/add", form,
            referer: referer ?? (type == 17 ? "https://t.bilibili.com/" : "https://www.bilibili.com/"));
        if (code == 0) return (true, null);

        // 这几个码给用户看得懂的话, 其余原样透出服务端的 message
        var friendly = code switch
        {
            -101 => "请先登录后再发表评论",
            -111 => "登录状态已失效, 请重新登录",
            -400 => "评论内容不合规, 已拒绝发布",
            -403 => "当前账号没有评论权限",
            -412 => "操作太频繁, 请稍后再试",
            -509 => "操作太频繁, 请稍后再试",
            // 实测(空正文探针): 服务端用 12066 表示"内容为空" ——
            // 客户端已经 Trim 过, 正常不会走到这里, 留着是为了不把裸数字抛给用户
            12066 => "评论内容不能为空",
            _ => msg
        };
        return (false, friendly);
    }

    /// <summary>
    /// 查询当前用户是否已点赞该视频(aid)。
    ///
    /// ★ 2026-10-01 从 <c>Task&lt;bool?&gt;</c> 改为 (ok, err, value):
    ///   原来的三态约定是"null = 查询失败", 但**失败原因被丢掉了**, 调用方只能默默
    ///   保持原状, 用户看到的就是"点了没反应"。现在 ok=false 时 err 会说明原因。
    ///   value 仍然只在 ok=true 时有意义。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> HasLikedAsync(long aid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/archive/has/like",
                new Dictionary<string, string> { ["aid"] = aid.ToString() });
            if (code != 0 || data == null) return (false, msg ?? $"查询点赞状态失败 (code {code})", false);
            // data: 1 已赞 / 0 未赞
            return (true, null, data.Value.ValueKind == JsonValueKind.Number && data.Value.GetInt32() == 1);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询点赞状态失败: " + ex.Message, false);
        }
    }

    /// <summary>
    /// 查询当前用户对该视频**已投了几枚币**(0 = 没投过)。
    ///
    /// ★★ 2026-10-13 用户报"投过币、关掉重开, 投币键还是灰的"——真根因是
    ///   <c>GetVideoAsync</c> 里那句 <c>GetInt(d, "coin") == 1</c> 读的字段**根本不存在**。
    ///   实测(探针 bd-probe-coin, 带真实登录 Cookie):
    ///     · <c>/x/web-interface/view</c> 的 data 里**没有** coin / req_user 这类用户态字段,
    ///       只有 <c>data.stat.coin</c> —— 那是**全站投币总数**(实测 128 万), 与本账号无关;
    ///     · 真正管用的是 <c>/x/web-interface/archive/coins</c>, 返回
    ///       <c>{"code":0,"data":{"multiply":N}}</c>, N 就是本人投的枚数(0/1/2)。
    ///   决定性验证: 真投 1 枚后, 同一个 aid 的 multiply 由 0 变成 1(探针已跑通)。
    ///   ⇒ 判断"曾经投过币"必须走这里, 不能靠详情接口。
    ///
    /// ★ 返回**枚数**而不是 bool: 单稿件上限 2 枚, 界面上"已投 1 枚还想再投 1 枚"
    ///   是有意义的场景, bool 会把这个信息丢掉。
    /// </summary>
    public async Task<(bool ok, string? err, int coins)> GetCoinCountAsync(long aid)
    {
        try
        {
            if (aid <= 0) return (false, "缺少视频 aid", 0);
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/archive/coins",
                new Dictionary<string, string> { ["aid"] = aid.ToString() });
            if (code != 0 || data == null) return (false, msg ?? $"查询投币状态失败 (code {code})", 0);
            // data.multiply: 本人已投枚数(实测 0/1)
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("multiply", out var m) && m.ValueKind == JsonValueKind.Number)
                return (true, null, m.GetInt32());
            // 字段缺失时按"没投过"处理, 不能让按钮卡在"已投"上 —— 那会让用户再也投不了
            return (true, null, 0);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询投币状态失败: " + ex.Message, 0);
        }
    }

    /// <summary>
    /// 把服务端的风控文案翻译成**用户能照着做**的提示。
    ///
    /// ★ 为什么需要它(2026-10-06 用户报"点赞投币依然报错"):
    ///   账号被风控时服务端回的是 `-403 账号异常,操作失败` / `-401 非法访问` ——
    ///   这两句对用户毫无信息量, 他只会反复点、然后以为程序坏了。
    ///   实测把完整响应打出来后, 真因写在 data.ga_data 里:
    ///       decisions = ["verify_captcha_level2"]   ← 要求二级人机验证
    ///   这是 B 站的**风控门**, 客户端绕不过(实测: 换 Referer/Origin/参数/端点/稿件,
    ///   以及补 b_nut/_uuid/bili_ticket 全部无效)。所以只能把话说清楚, 指引用户去官方
    ///   渠道完成一次验证 —— 而不是把服务端原文丢给用户。
    ///
    /// ★ 只替换**明确是风控**的这几种文案, 其它错误原样透出 —— 不吃掉真实报错。
    /// </summary>
    internal static string FriendlyRiskMessage(string? raw)
    {
        var msg = raw ?? "";
        // 这三句是同一道人机验证门的不同说法(见 v_voucher 文档的 -352 与实测的 -401/-403)
        if (msg.Contains("账号异常", StringComparison.Ordinal) ||
            msg.Contains("非法访问", StringComparison.Ordinal) ||
            msg.Contains("风控", StringComparison.Ordinal))
        {
            return "B 站风控拦截: 请在手机 App 或网页版用同一账号完成一次验证后重试";
        }
        return string.IsNullOrEmpty(msg) ? "未知错误" : msg;
    }

    /// <summary>点赞/取消点赞视频(现行 web 接口)。like=1 点赞, like=2 取消</summary>
    public async Task<(bool ok, string? err)> LikeVideoAsync(string bvid, bool liked)
    {
        var (ok, err) = await PostFormAsync(
            "https://api.bilibili.com/x/web-interface/archive/like",
            new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["like"] = liked ? "1" : "2"
            },
            referer: "https://www.bilibili.com/video/" + bvid);
        return (ok, ok ? err : FriendlyRiskMessage(err));
    }

    /// <summary>
    /// 投币(现行 web 接口, 需要 aid)。count=1 或 2。
    /// alsoLike=true 时带 select_like=1 —— 服务端会在同一次请求里把赞也点上
    /// (对应投币弹窗里的"同时点赞内容"; 拆成两次请求的话, 赞失败就该不该退币说不清了)。
    /// </summary>
    public async Task<(bool ok, string? err)> CoinVideoAsync(long aid, int count, string bvid, bool alsoLike = false)
    {
        var (ok, err) = await PostFormAsync(
            "https://api.bilibili.com/x/web-interface/coin/add",
            new Dictionary<string, string>
            {
                ["aid"] = aid.ToString(),
                ["multiply"] = count.ToString(),
                ["select_like"] = alsoLike ? "1" : "0"
            },
            referer: "https://www.bilibili.com/video/" + bvid);
        return (ok, ok ? err : FriendlyRiskMessage(err));
    }

    /// <summary>
    /// 查询与指定用户的关系(是否已关注)。同 <see cref="HasLikedAsync"/>: 2026-10-01 由
    /// <c>Task&lt;bool?&gt;</c> 改为 (ok, err, value), 失败时给出原因而不是静默返回 null。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> GetRelationAsync(long mid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation",
                new Dictionary<string, string> { ["fid"] = mid.ToString() },
                referer: "https://space.bilibili.com/" + mid);
            if (code != 0 || data == null) return (false, msg ?? $"查询关注状态失败 (code {code})", false);
            // data.attribute: 0 未关注, 2 已关注, 3 互关, 6 悄悄关注
            return (true, null,
                data.Value.TryGetProperty("attribute", out var a)
                && a.ValueKind == JsonValueKind.Number && a.GetInt32() > 0);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询关注状态失败: " + ex.Message, false);
        }
    }

    /// <summary>
    /// 批量查询与一批 UP 主的关系, 只挑出**已关注**的 mid(2026-11-14, 搜索页「已关注」标签用)。
    ///
    /// 接口: GET /x/relation/relations?fids=a,b,c —— 需要登录 Cookie, 未登录回 -101。
    /// ★ 搜索接口(search/type)的字段表里**没有** `is_followed`(只有首页推荐流带),
    ///   所以搜索页想显示卡片上那枚「已关注」标签, 只能拿当前页作者的 mid 来这里批量问一次;
    ///   逐个调 /x/relation 会把一页拆成 20 个请求, 更容易踩风控。
    /// ★ data 有两种形状: 有关系时是 `{ "123": { attribute: 2, ... } }`, 一个都没有时
    ///   服务端给的是 `[]` 而不是 `{}` —— 只按 Object 读, 其它形状一律当"没人被关注"。
    /// ★ attribute: 0 未关注 / 2 已关注 / 3、6 互关 / 128 拉黑。只看 &gt;0 会把拉黑过的人
    ///   也算成"已关注", 所以显式排除 128。
    /// ★ 失败一律静默: 标签只是装饰, 不能因为关注查询失败打断搜索结果(未登录就是最常见的失败)。
    /// </summary>
    public async Task<(bool ok, string? err, HashSet<long> followed)> GetFollowedMidsAsync(
        IReadOnlyCollection<long> mids)
    {
        var followed = new HashSet<long>();
        var list = mids.Where(m => m > 0).Distinct().ToList();
        if (list.Count == 0) return (true, null, followed);

        try
        {
            // fids 用逗号拼, 一次最多 40 个(社区实测上限; 直播一页正好 40 条)。
            for (var i = 0; i < list.Count; i += 40)
            {
                var chunk = list.Skip(i).Take(40);
                var (code, msg, data) = await GetJsonAsync(
                    "https://api.bilibili.com/x/relation/relations",
                    new Dictionary<string, string> { ["fids"] = string.Join(",", chunk) },
                    referer: "https://space.bilibili.com/");
                if (code != 0 || data == null)
                    return (false, msg ?? $"查询关注状态失败 (code {code})", followed);

                if (data.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in data.Value.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!p.Value.TryGetProperty("attribute", out var attr) ||
                        attr.ValueKind != JsonValueKind.Number) continue;
                    var value = attr.GetInt32();
                    if (value > 0 && value != 128 && long.TryParse(p.Name, out var mid))
                        followed.Add(mid);
                }
            }
            return (true, null, followed);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询关注状态失败: " + ex.Message, followed);
        }
    }

    /// <summary>关注/取消关注 UP 主。act=1 关注, act=2 取消关注</summary>
    public Task<(bool ok, string? err)> FollowUpAsync(long mid, bool follow)
    {
        return PostFormAsync(
            "https://api.bilibili.com/x/relation/modify",
            new Dictionary<string, string>
            {
                ["fid"] = mid.ToString(),
                ["act"] = follow ? "1" : "2",
                ["re_src"] = "11"
            },
            referer: "https://space.bilibili.com/" + mid);
    }

}
