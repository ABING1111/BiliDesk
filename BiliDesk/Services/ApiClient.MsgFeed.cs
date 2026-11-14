using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 哔哩哔哩 API 客户端: 带浏览器 UA、Cookie、WBI 签名与风控降级逻辑。
/// ★ 2026-11-07 由单文件拆成 17 个 partial 分片(纯移动、零逻辑改动),
///   分片清单与动机见 ApiClient.Core.cs 头部说明。
///
/// ★ 2026-11-14 新增本分片: 消息页「回复我的 / @我的 / 收到的赞」三个 Tab、
///   未读数角标、私信表情面板。与 Message.cs(私信会话/历史/发送)分开是因为
///   它们走的是 api.bilibili.com 的 msgfeed 一套, 与 vc.bilibili.com 的私信不是同一族。
/// </summary>
public partial class ApiClient
{
    /// <summary>互动消息列表(回复我的 / @我的 / 收到的赞)</summary>
    /// <param name="kind">Reply / At / Like</param>
    /// <param name="cursorId">上一页游标 id(第一页传 0)</param>
    /// <param name="cursorTime">上一页游标时间(第一页传 0)</param>
    public async Task<(bool ok, string? err, MsgFeedPage? page)> GetMsgFeedAsync(
        MsgTab kind, long cursorId = 0, long cursorTime = 0)
    {
        var path = kind switch
        {
            MsgTab.Reply => "https://api.bilibili.com/x/msgfeed/reply",
            MsgTab.At => "https://api.bilibili.com/x/msgfeed/at",
            MsgTab.Like => "https://api.bilibili.com/x/msgfeed/like",
            _ => ""
        };
        if (path.Length == 0) return (false, "不支持的消息类别", null);
        if (!SessionManager.Instance.HasLogin) return (false, "查看消息需要先登录", null);

        var ps = new Dictionary<string, string>
        {
            ["build"] = "0",
            ["mobi_app"] = "web",
            ["platform"] = "web",
            ["web_location"] = "333.999"
        };
        if (cursorId > 0) ps["id"] = cursorId.ToString();
        // 三个接口的时间参数名不同, 按类别分别传
        if (cursorTime > 0)
        {
            ps[kind switch
            {
                MsgTab.Reply => "reply_time",
                MsgTab.At => "at_time",
                MsgTab.Like => "like_time",
                _ => "time"
            }] = cursorTime.ToString();
        }

        try
        {
            var (code, msg, data) = await GetJsonAsync(path, ps,
                referer: "https://message.bilibili.com/");
            if (code != 0 || data == null)
                return (false, msg ?? $"加载失败 (code {code})", null);

            var page = MsgFeedParser.ParsePage(kind, data.Value);

            // 点赞是"最新一批 + 全部"两层合并出来的, 天然是乱序的 —— 按时间倒序排一次,
            // 否则界面上会出现"上周的赞夹在今天的赞中间"。
            if (kind == MsgTab.Like)
                page.Items.Sort((a, b) => b.Time.CompareTo(a.Time));

            return (true, null, page);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 未读数(四个 Tab 的角标)。
    ///
    /// 接口: GET /x/msgfeed/unread —— 它**不返回**未读私信数, 私信那边要用
    /// session_svr/single_unread 单独查, 所以这里并行补一次。
    /// </summary>
    public async Task<MsgUnread> GetMsgUnreadAsync()
    {
        var r = new MsgUnread();
        if (!SessionManager.Instance.HasLogin) return r;

        try
        {
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/msgfeed/unread",
                new Dictionary<string, string> { ["build"] = "0", ["mobi_app"] = "web" },
                referer: "https://message.bilibili.com/");
            if (code == 0 && data != null && data.Value.ValueKind == JsonValueKind.Object)
            {
                var d = data.Value;
                r.Reply = GetInt(d, "reply");
                r.At = GetInt(d, "at");
                // 新版字段 recv_like / recv_reply, 老版是 like / reply —— 两个都读, 取大的
                var like = GetInt(d, "recv_like");
                r.Like = like > 0 ? like : GetInt(d, "like");
            }
        }
        catch { /* 角标拿不到不影响页面 */ }

        try
        {
            var (code, _, data) = await GetJsonAsync(
                "https://api.vc.bilibili.com/session_svr/v1/session_svr/single_unread",
                new Dictionary<string, string>
                {
                    ["unread_type"] = "0",
                    ["show_unfollow_list"] = "0",
                    ["show_dustbin"] = "0",
                    ["build"] = "0",
                    ["mobi_app"] = "web"
                },
                referer: MsgReferer);
            if (code == 0 && data != null && data.Value.ValueKind == JsonValueKind.Object)
            {
                var d = data.Value;
                r.Chat = GetInt(d, "follow_unread") + GetInt(d, "unfollow_unread");
            }
        }
        catch { /* 同上 */ }

        return r;
    }

    /// <summary>
    /// 表情面板(私信输入框的「表情」按钮展开的内容)。
    ///
    /// ★ business 的取值: 官方文档只写了 reply(评论区) 与 dynamic(动态), 私信没有独立值。
    ///   实测 `reply` 返回的就是**该账号可用的全套表情**(含大会员表情包), 私信发过去也能识别,
    ///   所以这里就用 reply —— 换别的值反而可能拿到空列表。
    /// </summary>
    public async Task<(bool ok, string? err, List<MsgEmotePackage>? packages)> GetMsgEmotePanelAsync()
    {
        if (!SessionManager.Instance.HasLogin) return (false, "表情需要先登录", null);

        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/emote/user/panel/web",
                new Dictionary<string, string> { ["business"] = "reply" },
                referer: "https://message.bilibili.com/");
            if (code != 0 || data == null)
                return (false, msg ?? $"表情加载失败 (code {code})", null);

            var list = new List<MsgEmotePackage>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("packages", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var package = new MsgEmotePackage
                    {
                        Id = GetLong(p, "id"),
                        Text = GetStr(p, "text")
                    };

                    // 表情包自己的 meta.size 是默认尺寸, 单个表情的 meta.size 优先
                    var defaultSize = 1;
                    if (p.TryGetProperty("meta", out var pm) && pm.ValueKind == JsonValueKind.Object)
                    {
                        var s = GetInt(pm, "size");
                        if (s > 1) defaultSize = s;
                    }

                    if (p.TryGetProperty("emote", out var emotes) && emotes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var em in emotes.EnumerateArray())
                        {
                            if (em.ValueKind != JsonValueKind.Object) continue;

                            // 颜文字(type=4)的 url 是文字不是图片, 面板里画不出来, 跳过
                            var type = GetInt(em, "type");
                            if (type == 4) continue;

                            var text = GetStr(em, "text");
                            var url = UrlUtil.Normalize(GetStr(em, "url"));
                            if (text.Length == 0 || url.Length == 0) continue;

                            var size = defaultSize;
                            if (em.TryGetProperty("meta", out var mm) && mm.ValueKind == JsonValueKind.Object)
                            {
                                var s = GetInt(mm, "size");
                                if (s > 0) size = s;
                            }

                            package.Emotes.Add(new MsgEmote { Text = text, Url = url, Size = size });
                        }
                    }

                    if (package.Emotes.Count > 0) list.Add(package);
                }
            }

            return (true, null, list);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null);
        }
    }
}
