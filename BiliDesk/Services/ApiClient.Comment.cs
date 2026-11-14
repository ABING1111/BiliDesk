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
    /// 视频评论(返回前 N 条评论, 并带上每条的内联前几条回复)。
    ///
    /// ★ 2026-10-01 由裸 <c>Task&lt;List&lt;CommentItem&gt;&gt;</c> 改为 (ok, err, items):
    ///   原来"接口报错"与"这个视频一条评论都没有"都返回**空列表**, 调用方无法区分,
    ///   于是评论区会显示成"还没有人评论" —— 网络失败被伪装成了正常状态。
    ///
    /// ★ 2026-10-06 增加 <paramref name="mode"/>(评论区「热门 / 时间」切换)与返回的
    ///   <c>total</c>(根评论总数)。两个都来自同一个接口, 不需要额外请求:
    ///     · <c>mode=3</c> 热门 / <c>mode=2</c> 时间 —— 与 B 站网页版同一组参数;
    ///     · 总数在 <c>data.cursor.all_count</c>。
    ///   ★ 为什么要把 total 透出来: 以前调用方拿 <c>list.Count</c> 当总数显示"共 N 条评论",
    ///     而一页只拉 30 条 —— 视频有几千条评论时那句话是**错的**。排序入口就挂在这行文字右边,
    ///     数字不对会让整个入口看着像坏的。
    ///
    /// ★★ 2026-11-08 增加翻页(修 issue #55「评论区只能显示有限几条, 滑不下去」):
    ///   `/x/v2/reply/main` 的分页是**游标式**的, 不是页码式:
    ///     · 响应 `data.cursor.next` = 下一页游标(第一页就是 2/3/80562 这种值);
    ///     · 响应 `data.cursor.is_end` = 是否已到最后一页;
    ///     · 请求把上一页的 `next` 原样带回去即可(实测 mode=2 / mode=3 都可用)。
    ///   原来的 `pn=1` 是**写死的**, 接口对这个参数实际会忽略 —— 所以永远只有第一页。
    ///   ★ `next` 的优先级高于已弃用的 `pagination_str`, 也高于 `pn`; 首屏不传 `next` 即可。
    /// </summary>
    public async Task<(bool ok, string? err, List<CommentItem> items, long total, long next, bool isEnd)>
        GetCommentsAsync(long aid, int count = 20, int mode = 3, long next = 0,
            int type = 1, long upperMid = 0)
    {
        var result = new List<CommentItem>();
        var total = 0L;
        var nextCursor = 0L;
        var isEnd = false;
        try
        {
            var ps = new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = type.ToString(),
                ["mode"] = mode.ToString(),
                ["ps"] = count.ToString()
            };
            // 首屏不带游标; 之后原样带上上一页的 cursor.next。
            // ★ 不要再带 pn: 这个接口不认页码, 带着只会让人误以为"分页是靠 pn 做的"。
            if (next > 0) ps["next"] = next.ToString();
            // ★ fingerprint: 按**登录态**决定带不带 Cookie。
            //
            // 实测(2026-09-25, 冷启动单请求, 同一个 aid):
            //   未登录 + 不带任何 Cookie            -> 20 条, 但 reply_control 里**没有 location**
            //                                          (所以评论区一条 IP 属地都看不到)
            //   已登录 + 完整 Cookie(buvid3+buvid4) -> 20 条, 内联楼中楼 34 条,
            //                                          location = "IP属地：广东"
            // 即: IP 属地是**登录态才下发**的字段; 而 2026-09-24 那条"带设备指纹会被降级成
            // 3 条"的结论只对**未登录**成立 —— 登录态下带指纹一样是满 20 条。
            // 于是: 登录了就带(拿得到 IP 属地), 没登录就不带(保住 20 条不被降级)。
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/reply/main", ps,
                fingerprint: SessionManager.Instance.HasLogin,
                referer: type is 11 or 17 ? "https://t.bilibili.com/" : null);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取评论失败 (code {code})", result, 0, 0, true);

            // 根评论总数(不受 ps 分页影响)。缺失时保持 0, 由调用方回落到"本页条数"。
            // 同一份 cursor 里还带下一页游标与"是否到底", 翻页全靠它们。
            if (data.Value.TryGetProperty("cursor", out var cursor) &&
                cursor.ValueKind == JsonValueKind.Object)
            {
                total = GetLongSafe(cursor, "all_count");
                nextCursor = GetLongSafe(cursor, "next");
                isEnd = cursor.TryGetProperty("is_end", out var ie) &&
                        (ie.ValueKind == JsonValueKind.True ||
                         (ie.ValueKind == JsonValueKind.Number && ie.GetInt32() == 1));
            }

            // 本稿件的 UP 主 mid: 用来给"UP 主自己的评论"打 [UP] 标。
            // 动态评论区(type=17)接口不一定带 upper, 调用方可以直接把动态作者 mid 传进来。
            if (upperMid <= 0 &&
                data.Value.TryGetProperty("upper", out var up) && up.ValueKind == JsonValueKind.Object)
                upperMid = GetLongSafe(up, "mid");

            // ★★★ 置顶评论(2026-10-03 修"置顶评论不可见"): 置顶那条**不在 replies 里**,
            //   而是在 `data.top.upper`(UP 主置顶)或 `data.top.admin`(管理员置顶)。
            //   只读 replies 就会把它整条丢掉 —— 用户看到的就是"明明有置顶, 却一条都看不到"。
            //   实测(data.top.upper.rpid=315658667873 在 replies 里搜不到)。
            //   ★ 放在 replies **之前**: 置顶本来就该排在最前面。
            //   ★ 两个都可能有值, 但语义上只应展示一条置顶, 所以 upper 优先、admin 兜底。
            if (data.Value.TryGetProperty("top", out var top) && top.ValueKind == JsonValueKind.Object)
            {
                var topEl = TryObject(top, "upper") ?? TryObject(top, "admin");
                if (topEl != null)
                {
                    var pinned = ParseComment(topEl.Value, withReplies: true, upperMid: upperMid);
                    pinned.IsPinned = true;
                    result.Add(pinned);
                }
            }

            if (data.Value.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    result.Add(ParseComment(e, withReplies: true, upperMid: upperMid));
                }
            }
        }
        catch (Exception ex)
        {
            // 不能真的"静默": 解析里任何一处异常都会让后面的评论**整批丢掉**,
            // 表现是"评论只出来前几条", 而排查时一点线索都没有。记到日志里。
            App.ReportError(ex);
            // 堆栈首行一起带出去: "invalid format"这类错误只有堆栈才能定位到具体字段
            var stackTop = (ex.StackTrace ?? "").Split('\n')
                .FirstOrDefault(l => l.Contains("BiliDesk") && !l.Contains("ThrowHelper")) ?? "";
            return (false, "解析评论失败: " + ex.Message + (stackTop.Length > 0 ? " @ " + stackTop.Trim() : ""), result, 0, 0, true);
        }
        // isEnd 还要兜一层: 游标为 0 时也说明没有下一页了(接口偶尔不给 is_end)。
        return (true, null, result, total, nextCursor, isEnd || nextCursor <= 0);
    }

    /// <summary>取对象里的某个子对象; 不存在或是 null 时返回 null(置顶评论可能只有 upper 或只有 admin)</summary>
    private static JsonElement? TryObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>
    /// 取某条评论下的**全部**回复(楼中楼)。
    /// 接口: /x/v2/reply/reply —— root 传顶层评论的 rpid, oid 是视频 aid / 动态 id,
    /// type=1(稿件) / 17(动态)。首页接口 /x/v2/reply/main 只内联前 3 条回复,
    /// 要看全就只能走这个接口, 否则用户会觉得"别人回复的内容看不到"。
    /// </summary>
    public async Task<(bool ok, string? err, List<CommentItem> items)> GetReplyRepliesAsync(
        long aid, long rootRpid, int pn = 1, int ps = 20, int type = 1, long upperMid = 0)
    {
        var result = new List<CommentItem>();
        try
        {
            if (aid <= 0 || rootRpid <= 0) return (false, "参数不完整", result);
            var ps2 = new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = type.ToString(),
                ["root"] = rootRpid.ToString(),
                ["ps"] = ps.ToString(),
                ["pn"] = pn.ToString()
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/reply/reply", ps2,
                referer: type is 11 or 17 ? "https://t.bilibili.com/" : null);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取楼中楼失败 (code {code})", result);

            // 楼中楼里 UP 主自己的回复同样要打 [UP] 标 —— reply/reply 的 data 里也带 upper
            if (upperMid <= 0 &&
                data.Value.TryGetProperty("upper", out var up) && up.ValueKind == JsonValueKind.Object)
                upperMid = GetLongSafe(up, "mid");

            if (data.Value.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    result.Add(ParseComment(e, withReplies: false, upperMid: upperMid));
                }
            }
        }
        catch (Exception ex)
        {
            // 不能真的"静默": 解析里任何一处异常都会让后面的评论**整批丢掉**,
            // 表现是"评论只出来前几条", 而排查时一点线索都没有。记到日志里。
            App.ReportError(ex);
            return (false, "解析评论失败: " + ex.Message, result);
        }
        return (true, null, result);
    }

    /// <summary>
    /// 解析一条评论 JSON。顶层评论与楼中楼回复的字段结构完全一致, 所以共用同一份解析,
    /// 免得两边各写一遍之后字段理解跑偏。
    /// </summary>
    /// <param name="withReplies">是否解析 rcount 与内联的 replies(只有顶层评论才有)</param>
    /// <param name="upperMid">本稿件 UP 主的 mid; 评论者 mid 与它相同就打 [UP] 标。
    /// 0 = 调用方拿不到(或不是从"稿件维度"取的), 此时谁都不打标。</param>
    private static CommentItem ParseComment(JsonElement e, bool withReplies, long upperMid = 0)
    {
        // 当前登录用户对这条评论的操作: 1 = 已赞(0 = 无)。未登录时恒为 0。
        // 2 = 已踩: 只在服务端确实会下发这个值时才成立, 见 CommentItem.IsDisliked 的说明。
        var myAction = GetIntSafe(e, "action");

        var item = new CommentItem
        {
            // ★ 数值字段一律 GetLongSafe/GetIntSafe: 实测服务端会把 like 等字段下发成
            //   小数(如 12.0), 普通 GetLong 对浮点调 GetInt64() 直接抛
            //   "One of the identified items was in an invalid format",
            //   而 ParseComment 外层只有一个大 try —— 一条评论抛异常整页全丢。
            Rpid = GetLongSafe(e, "rpid"),
            // 评论者 mid: 顶层字段就有, 取不到再从 member.mid 兜一次
            Mid = GetLongLoose(e, "mid"),
            // 被回复的那条评论 id(详情页里"回复 @"指向谁)
            ParentRpid = GetLongLoose(e, "parent"),
            LikeCount = GetIntSafe(e, "like"),
            IsLiked = myAction == 1,
            IsDisliked = myAction == 2
        };

        if (e.TryGetProperty("member", out var m))
        {
            item.UserName = GetStr(m, "uname");
            item.UserFace = UrlUtil.Normalize(GetStr(m, "avatar"));
            if (item.Mid <= 0) item.Mid = GetLongLoose(m, "mid");
            // 等级徽章。member.level_info.current_level(0~6), 缺了就不显示徽章
            if (m.TryGetProperty("level_info", out var lv) && lv.ValueKind == JsonValueKind.Object)
                item.Level = GetIntSafe(lv, "current_level");
        }

        // UP 标: 评论者就是本稿件 UP 主本人
        item.IsUp = upperMid > 0 && item.Mid == upperMid;

        // IP 属地。接口给的是 `reply_control.location`, 形如 "IP属地：上海";
        // 它**本来就是带前缀的展示串**, 所以直接取用、不再自己拼"IP属地"。
        // 值可能缺失(老接口 / 用户设置里关了展示), 缺了就不显示。
        if (e.TryGetProperty("reply_control", out var rc) && rc.ValueKind == JsonValueKind.Object)
            item.Location = GetStr(rc, "location").Trim();

        // 评论内容在 content.message 嵌套里
        item.Content = e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object
            ? GetStr(c, "message")
            : "";

        // 表情: content.emote 是 { "[短代码]": { url, meta:{size} } } 的字典,
        // key 就是正文里原样出现的短代码(实测 "[打call]"、"鸣潮·共鸣与群星_逼近" 都在 message 里)。
        // 只收正文里真的用到的, 免得白存一堆用不上的地址。
        if (c.ValueKind == JsonValueKind.Object &&
            c.TryGetProperty("emote", out var emote) && emote.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in emote.EnumerateObject())
            {
                if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                if (!item.Content.Contains(kv.Name, StringComparison.Ordinal)) continue;
                var url = UrlUtil.Normalize(GetStr(kv.Value, "url"));
                if (url.Length == 0) url = UrlUtil.Normalize(GetStr(kv.Value, "webp_url"));
                if (url.Length == 0) continue;
                var size = 1;
                if (kv.Value.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    var s = GetIntSafe(meta, "size");
                    if (s > 1) size = s;
                }
                item.Emotes[kv.Name] = new CommentEmote { Url = url, Size = size };
            }
        }

        item.Ctime = e.TryGetProperty("ctime", out var ctRaw) && ctRaw.ValueKind == JsonValueKind.Number
            ? GetLongSafe(e, "ctime") : 0L;
        item.RcTime = item.Ctime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(item.Ctime).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : "";

        // @提及(2026-10-30 新增): 扫出正文里 @ 到的昵称, 界面据此把它们染成强调色。
        // ★ 接口给的 content.message 是**纯文本**, 没有现成的结构化引用字段可用,
        //   所以只能按 B 站网页版的规则自己扫(见 CommentItem.Mentions 的说明)。
        foreach (var name in MentionParser.Parse(item.Content))
            item.Mentions.Add(name);

        // 评论图片(2026-10-31 新增): content.pictures 数组, 带图评论才有。
        // ★ 字段名带 img_ 前缀(img_src/img_width/img_height/img_size), 与 emote 那套不同名。
        if (c.ValueKind == JsonValueKind.Object &&
            c.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pics.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var url = UrlUtil.Normalize(GetStr(p, "img_src"));
                if (url.Length == 0) continue;
                item.Pictures.Add(new CommentPicture
                {
                    Url = url,
                    Width = GetIntSafe(p, "img_width"),
                    Height = GetIntSafe(p, "img_height"),
                    // img_size 实测是 KB 数(可能带小数), 用 LongSafe 兜住
                    FileSize = GetLongSafe(p, "img_size")
                });
            }
        }

        if (withReplies)
        {
            item.ReplyCount = GetIntSafe(e, "rcount");
            // /x/v2/reply/main 会把前 3 条回复直接内联在 replies 里 —— 先渲染出来,
            // 用户不点任何东西就能看见别人的回复内容
            if (e.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    item.Replies.Add(ParseComment(r, withReplies: false, upperMid: upperMid));
                }
            }
            // 服务端有时返回的 rcount 比实际内联的还少, 兜一下, 否则"展开"按钮会一直挂着
            if (item.ReplyCount < item.Replies.Count) item.ReplyCount = item.Replies.Count;
            // 列表页方框只展示短预览; 详情页仍然用 Replies 看全部(见 PreviewReplies 的说明)
            item.SyncPreviewReplies();
        }

        return item;
    }
    // ---------------------------------------------------------------- 直播
}
