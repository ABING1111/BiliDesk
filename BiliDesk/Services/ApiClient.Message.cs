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
    // ---------------------------------------------------------------- 评论


    /// <summary>
    /// 私信接口统一的来源页。
    /// 这几个接口在 message.bilibili.com 域名下被调用, 不带对应 Referer 很容易吃 -403,
    /// 所以每处都显式传它。
    /// </summary>
    private const string MsgReferer = "https://message.bilibili.com/";

    private static long ParseMid()
        => long.TryParse(SessionManager.Instance.Current.DedeUserID, out var v) ? v : 0;

    /// <summary>获取私信会话列表(含昵称/头像, 一次请求批量补齐)</summary>
    public async Task<(bool ok, string? err, List<MsgSession>? sessions)> GetMsgSessionsAsync(
        int sessionType = 1, int size = 50)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录后查看", null);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.vc.bilibili.com/session_svr/v1/session_svr/get_sessions",
                new Dictionary<string, string>
                {
                    ["session_type"] = sessionType.ToString(), // 1 = 私聊 + 系统消息
                    ["sort_rule"] = "2",                       // 按活跃时间倒序
                    ["size"] = size.ToString(),
                    ["build"] = "0",
                    ["mobi_app"] = "web"
                },
                referer: MsgReferer);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<MsgSession>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("session_list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in arr.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    var talker = GetLong(s, "talker_id");
                    if (talker <= 0) continue; // 系统会话可能没有 talker_id, 跳过

                    var atTime = GetLong(s, "at_time");
                    list.Add(new MsgSession
                    {
                        TalkerId = talker,
                        UnreadCount = GetInt(s, "unread_count"),
                        AckSeqno = GetLong(s, "ack_seqno"),
                        MaxSeqno = GetLong(s, "max_seqno"),
                        TimeText = atTime > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(atTime).LocalDateTime
                                .ToString("MM-dd HH:mm")
                            : "",
                        Preview = s.TryGetProperty("last_msg", out var lm) ? PreviewOf(lm) : ""
                    });
                }
            }

            await FillSessionProfilesAsync(list);
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 批量补会话的昵称与头像。
    /// 会话列表接口本身不返回用户资料, 得用 /account/v1/user/cards 单独查, 一次最多 50 个 UID。
    /// 失败只是没有名字(会退化成 "UID xxxx"), 不影响会话列表本身可用。
    /// </summary>
    private async Task FillSessionProfilesAsync(List<MsgSession> sessions)
    {
        var ids = sessions.Select(s => s.TalkerId).Distinct().ToList();
        for (var i = 0; i < ids.Count; i += 50)
        {
            var batch = ids.Skip(i).Take(50).ToList();
            try
            {
                var (code, _, data) = await GetJsonAsync(
                    "https://api.vc.bilibili.com/account/v1/user/cards",
                    new Dictionary<string, string> { ["uids"] = string.Join(",", batch) },
                    referer: MsgReferer);
                if (code != 0 || data == null) continue;
                if (data.Value.ValueKind != JsonValueKind.Array) continue;

                var cards = new Dictionary<long, (string name, string face)>();
                foreach (var c in data.Value.EnumerateArray())
                {
                    var mid = GetLong(c, "mid");
                    if (mid > 0)
                        cards[mid] = (GetStr(c, "name"), UrlUtil.Normalize(GetStr(c, "face")));
                }

                foreach (var s in sessions)
                    if (cards.TryGetValue(s.TalkerId, out var v))
                    {
                        s.Name = v.name;
                        s.Face = v.face;
                    }
            }
            catch { /* 单批失败不影响其它批次 */ }
        }
    }

    /// <summary>
    /// 拉取与某个会话的私信记录。
    /// 接口: /svr_sync/v1/svr_sync/fetch_session_msgs (GET, Cookie 认证)。
    /// 返回结果按 msg_seqno 升序排好(旧 → 新), 直接可以正序渲染。
    ///
    /// ★ 2026-11-14: 额外解析 data.e_infos(本次会话用到的表情表), 并把它挂到
    ///   每一条**真的用到**该表情的消息上(MsgItem.Emotes)。表情是自定义图片,
    ///   没有 Unicode 对应, 只能靠短代码替换成内联图片。
    /// </summary>
    public async Task<(bool ok, string? err, List<MsgItem>? items, bool hasMore)> GetMsgHistoryAsync(
        long talkerId, int size = 30, long endSeqno = 0)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录后查看", null, false);
            if (talkerId <= 0) return (false, "会话无效", null, false);
            var me = ParseMid();
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["sender_device_id"] = "1",
                ["talker_id"] = talkerId.ToString(),
                ["session_type"] = "1",
                ["size"] = size.ToString(),
                ["build"] = "0",
                ["mobi_app"] = "web"
            };
            if (endSeqno > 0) ps["end_seqno"] = endSeqno.ToString();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.vc.bilibili.com/svr_sync/v1/svr_sync/fetch_session_msgs",
                ps,
                referer: MsgReferer);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, false);

            var list = new List<MsgItem>();
            var hasMore = false;
            // 本次响应用到的表情: 短代码 → 表情。整段会话一次性下发, 与消息本身解耦。
            var emotes = new Dictionary<string, MsgEmote>(StringComparer.Ordinal);

            if (data.Value.ValueKind == JsonValueKind.Object)
            {
                hasMore = data.Value.TryGetProperty("has_more", out var hm) &&
                          hm.ValueKind == JsonValueKind.True;

                // e_infos 先读: 后面解析消息正文时要靠它判断"这条是不是用了表情"
                if (data.Value.TryGetProperty("e_infos", out var eInfos) &&
                    eInfos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var em in eInfos.EnumerateArray())
                    {
                        if (em.ValueKind != JsonValueKind.Object) continue;
                        var text = GetStr(em, "text");
                        // ★ 实测字段名是 **url**(不是 uri): 文档写的是 uri, 真实返回是 url。
                        //   两个都读, 免得服务端哪天又改回去。
                        var uri = UrlUtil.Normalize(GetStr(em, "url"));
                        if (uri.Length == 0) uri = UrlUtil.Normalize(GetStr(em, "uri"));
                        if (text.Length == 0 || uri.Length == 0) continue;
                        var emoteSize = GetInt(em, "size");
                        emotes[text] = new MsgEmote { Text = text, Url = uri, Size = emoteSize > 1 ? 2 : 1 };
                    }
                }

                if (data.Value.TryGetProperty("messages", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        var uid = GetLong(m, "sender_uid");
                        var msgType = GetInt(m, "msg_type");
                        var ts = GetLong(m, "timestamp");
                        var content = GetStr(m, "content");

                        var item = new MsgItem
                        {
                            SenderUid = uid,
                            IsMine = uid == me,
                            Seqno = GetLong(m, "msg_seqno"),
                            MsgType = msgType,
                            Timestamp = ts,
                            TimeText = ts > 0
                                ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime
                                    .ToString("MM-dd HH:mm")
                                : ""
                        };

                        // 被撤回的消息(msg_status=1)不显示原文, 给一句提示占位
                        var status = GetInt(m, "msg_status");
                        if (status == 1 || status == 2)
                        {
                            item.Text = "[该消息已撤回]";
                            item.MsgType = 1;
                            list.Add(item);
                            continue;
                        }

                        switch (msgType)
                        {
                            case 2:   // 图片
                            case 6:   // 自定义表情(结构同图片, 但显示成小图)
                                item.ImageUrl = ExtractImageUrl(content);
                                item.IsEmote = msgType == 6;
                                if (item.ImageUrl.Length == 0) item.Text = msgType == 6 ? "[表情]" : "[图片]";
                                break;

                            case 18:  // 系统提示(仅自己可见, 如"对方回复前最多发 1 条")
                                item.Text = ExtractSystemTip(content);
                                break;

                            default:
                                item.Text = DecodeMsgContent(content);
                                if (item.Text.Length == 0) item.Text = "[暂不支持的消息类型]";
                                break;
                        }

                        // 把这条消息**真的用到**的表情挑出来挂上(没用到就不占内存)
                        if (emotes.Count > 0 && item.Text.Length > 0)
                        {
                            foreach (var kv in emotes)
                                if (item.Text.Contains(kv.Key, StringComparison.Ordinal))
                                    item.Emotes[kv.Key] = kv.Value;
                        }

                        list.Add(item);
                    }
                }
            }

            // 按序号升序(旧 → 新)。不依赖接口返回顺序 —— 那个在新旧方向上没有稳定保证。
            // 序号缺失(0)的消息保持原有相对次序。
            list.Sort((a, b) => a.Seqno.CompareTo(b.Seqno));
            return (true, null, list, hasMore);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, false);
        }
    }

    /// <summary>发送文字私信</summary>
    public Task<(bool ok, string? err)> SendMsgAsync(long receiverId, string text)
    {
        if (receiverId <= 0) return Task.FromResult<(bool, string?)>((false, "缺少接收者"));
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult<(bool, string?)>((false, "消息不能为空"));

        // ★ 补 new_face_version=1: 新版表情包的短代码(如 [tv_doge])只在声明新版本时被识别;
        //   不传的话服务端会把 [doge] 之类自动转换, 用户看到的和发出去的不一致。
        return SendMsgCoreAsync(receiverId, 1,
            JsonSerializer.Serialize(new { content = text }), extra: ("msg[new_face_version]", "1"));
    }

    /// <summary>
    /// 发送图片私信。
    /// 接口要求 content 是 {"url":..., "width":..., "height":...} 的 JSON 字符串,
    /// 且 url **必须**是 B 站图床地址(第三方图床会回 21037)。
    /// </summary>
    public Task<(bool ok, string? err)> SendMsgImageAsync(
        long receiverId, string url, int width, int height)
    {
        if (receiverId <= 0) return Task.FromResult<(bool, string?)>((false, "缺少接收者"));
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<(bool, string?)>((false, "图片地址为空"));

        var content = JsonSerializer.Serialize(new
        {
            url,
            width = width > 0 ? width : 0,
            height = height > 0 ? height : 0,
            imageType = ImageTypeOf(url),
            original = 1
        });
        return SendMsgCoreAsync(receiverId, 2, content);
    }

    /// <summary>从图片地址推断 imageType(接口不强制, 但带上更贴近官方客户端)</summary>
    private static string ImageTypeOf(string url)
    {
        var path = url;
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0) path = path[..q];
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "png";
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return "gif";
        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "webp";
        return "jpeg";
    }

    /// <summary>
    /// 文字/图片共用的发送核心。
    ///
    /// ★ 抽出来的理由: 两者的参数只有 msg_type 与 content 不同, 其余(Cookie / csrf /
    ///   dev_id / 时间戳 / Referer)必须逐字一致 —— 各写一份迟早有一处漏掉 csrf 或 dev_id,
    ///   表现是"文字能发、图片报 -400", 很难往参数差异上想。
    /// </summary>
    private async Task<(bool ok, string? err)> SendMsgCoreAsync(
        long receiverId, int msgType, string content, (string Key, string Value)? extra = null)
    {
        if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录");

        var me = ParseMid();
        if (me <= 0) return (false, "无法获取自己的 UID");

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var ps = new Dictionary<string, string>
        {
            ["msg[sender_uid]"] = me.ToString(),
            ["msg[receiver_id]"] = receiverId.ToString(),
            ["msg[receiver_type]"] = "1",   // 1 = 用户
            ["msg[msg_type]"] = msgType.ToString(),
            ["msg[msg_status]"] = "0",
            ["msg[content]"] = content,
            ["msg[timestamp]"] = ts,
            ["msg[dev_id]"] = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            // 官方文档写的是 csrf_token, 但别的实现里也用 csrf; 两个都带上最稳
            ["csrf_token"] = SessionManager.Instance.Current.BiliJct ?? "",
            ["build"] = "0",
            ["mobi_app"] = "web"
        };
        if (extra is { } ex) ps[ex.Key] = ex.Value;

        var (code, err) = await PostFormCodeAsync(
            "https://api.vc.bilibili.com/web_im/v1/web_im/send_msg", ps, referer: MsgReferer);

        return code == 0 ? (true, null) : (false, FriendlySendError(code, err));
    }

    /// <summary>
    /// 把发送接口的错误码翻成人话。
    ///
    /// ★ 必须按**错误码**判断, 不能按 message 字符串: PostFormCodeAsync 会用服务端的
    ///   message 覆盖掉 "code NNNNN", 而同一个码的 message 文案会随服务端版本变
    ///   (有时是中文, 有时只是 "请求错误") —— 按字符串匹配迟早全部失效。
    /// </summary>
    private static string? FriendlySendError(int code, string? err) => code switch
    {
        21020 => "发送太快了, 请稍后再发",
        21047 => "对方主动回复或关注你前, 最多发送 1 条消息",
        21015 => "只有绑定手机号的账号才能发送消息",
        21037 => "图片格式不合法, 请换一张试试",
        21007 => "消息过长, 无法发送",
        21026 => "不能给自己发送消息",
        21028 => "系统升级中, 暂时无法发送",
        21035 => "这类消息暂时无法发送",
        25003 => "因对方隐私设置, 暂时无法发送消息",
        25005 => "你已拉黑对方, 请先移出黑名单",
        -101 => "登录状态已失效, 请重新登录",
        _ => err ?? $"发送失败 (code {code})"
    };

    /// <summary>
    /// 上传一张图片到 B 站图床, 返回可直接发私信的地址与尺寸。
    ///
    /// ★ 为什么走"动态图片"这条通道: 私信没有公开的独立上传接口, 但私信接口只校验
    ///   "是不是 B 站图床的地址", 不校验来源通道。`x/dynamic/feed/draw/upload_bfs`
    ///   是最稳的一条(带 csrf、返回宽高), 失败时退回老的 drawImage/upload。
    /// ★ 超时单独放宽到 60 秒: 主 HttpClient 是 15 秒, 那是给 JSON 接口定的;
    ///   一张 5MB 的图在慢网上传不完, 会表现为"选了图没反应"。
    /// </summary>
    public async Task<(bool ok, string? err, string url, int width, int height)> UploadMsgImageAsync(
        string filePath)
    {
        if (!SessionManager.Instance.HasLogin) return (false, "上传图片需要先登录", "", 0, 0);
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            return (false, "找不到这个文件", "", 0, 0);

        byte[] bytes;
        try { bytes = await System.IO.File.ReadAllBytesAsync(filePath); }
        catch (Exception ex) { return (false, "读取图片失败: " + ex.Message, "", 0, 0); }

        if (bytes.Length == 0) return (false, "图片是空文件", "", 0, 0);
        // 接口上限 20MB; 再大基本也传不上去, 提前拦下省一次等待
        if (bytes.Length > 20 * 1024 * 1024) return (false, "图片太大了(上限 20MB)", "", 0, 0);

        var fileName = System.IO.Path.GetFileName(filePath);
        var (ok, err, url, w, h) = await UploadImageCoreAsync(
            "https://api.bilibili.com/x/dynamic/feed/draw/upload_bfs", bytes, fileName);
        if (ok) return (true, null, url, w, h);

        // 主通道失败再试老通道(部分账号在灰度里只放行其中一个)
        var (ok2, err2, url2, w2, h2) = await UploadImageCoreAsync(
            "https://api.vc.bilibili.com/api/v1/drawImage/upload", bytes, fileName);
        if (ok2) return (true, null, url2, w2, h2);

        return (false, err ?? err2 ?? "图片上传失败", "", 0, 0);
    }

    /// <summary>真正的 multipart 上传(两个通道参数名不同, 一起带上)</summary>
    private async Task<(bool ok, string? err, string url, int width, int height)> UploadImageCoreAsync(
        string endpoint, byte[] bytes, string fileName)
    {
        try
        {
            var csrf = SessionManager.Instance.Current.BiliJct ?? "";
            if (csrf.Length == 0) return (false, "未登录", "", 0, 0);

            using var form = new System.Net.Http.MultipartFormDataContent();
            var fileContent = new System.Net.Http.ByteArrayContent(bytes);
            fileContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(GuessMime(fileName));
            // 两个通道的字段名都叫 file_up; 另外补 category/biz 以兼容两种实现
            form.Add(fileContent, "file_up", fileName);
            form.Add(new System.Net.Http.StringContent("daily"), "category");
            form.Add(new System.Net.Http.StringContent("new_dyn"), "biz");
            form.Add(new System.Net.Http.StringContent(csrf), "csrf");

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            req.Headers.TryAddWithoutValidation("Cookie",
                AppendFingerprint(SessionManager.Instance.BuildCookieHeader()));
            req.Headers.TryAddWithoutValidation("Referer", MsgReferer);
            req.Headers.TryAddWithoutValidation("Origin", "https://message.bilibili.com");

            // ★ 独立超时: 上传比 JSON 接口慢得多, 用主 _http 的 15 秒会中途掐断
            using var uploadHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            uploadHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);
            using var resp = await uploadHttp.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
            if (code != 0)
            {
                var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                return (false, msg ?? $"上传失败 (code {code})", "", 0, 0);
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return (false, "上传返回为空", "", 0, 0);

            var url = UrlUtil.Normalize(GetStr(data, "image_url"));
            if (url.Length == 0) return (false, "上传未返回图片地址", "", 0, 0);

            return (true, null, url, GetInt(data, "image_width"), GetInt(data, "image_height"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message, "", 0, 0);
        }
    }

    /// <summary>按扩展名猜 MIME(只覆盖私信能发的几种)</summary>
    private static string GuessMime(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }

    /// <summary>把某个会话标记为已读(失败无所谓, 只是未读数不准)</summary>
    public async Task MarkMsgReadAsync(long talkerId, long ackSeqno)
    {
        if (talkerId <= 0 || ackSeqno <= 0) return;
        try
        {
            await PostFormAsync(
                "https://api.vc.bilibili.com/session_svr/v1/session_svr/update_ack",
                new Dictionary<string, string>
                {
                    ["talker_id"] = talkerId.ToString(),
                    ["session_type"] = "1",
                    ["ack_seqno"] = ackSeqno.ToString(),
                    ["build"] = "0",
                    ["mobi_app"] = "web"
                },
                referer: MsgReferer);
        }
        catch { /* 已读回执失败不影响功能 */ }
    }

    /// <summary>会话列表里的 last_msg: 可能是对象, 也可能是被序列化成字符串的 JSON</summary>
    private static string PreviewOf(JsonElement lastMsg)
    {
        if (lastMsg.ValueKind == JsonValueKind.String)
        {
            var raw = lastMsg.GetString() ?? "";
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return PreviewOf(doc.RootElement);
            }
            catch { return ""; }
        }
        if (lastMsg.ValueKind != JsonValueKind.Object) return "";
        var msgType = GetInt(lastMsg, "msg_type");
        if (msgType == 2) return "[图片]";
        if (msgType == 6) return "[表情]";
        var text = DecodeMsgContent(GetStr(lastMsg, "content"));
        if (text.Length > 0) return text;
        return "";
    }

    /// <summary>
    /// msg_type=18 系统提示: content 是**序列化后的 JSON 数组**,
    /// 每项形如 {"text":"对方主动回复或关注你前，最多发送1条消息", "color_day":"#9499A0", ...}。
    /// 这些提示是 B 站自己的规则说明, 必须显示(否则用户不知道自己为什么发不出去)。
    /// </summary>
    private static string ExtractSystemTip(string contentJson)
    {
        if (string.IsNullOrEmpty(contentJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;

            // content 本身是 {"content":"[{...}]"} 这一层包着的情况也见过
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("content", out var inner) &&
                inner.ValueKind == JsonValueKind.String)
            {
                return ExtractSystemTip(inner.GetString() ?? "");
            }

            if (root.ValueKind != JsonValueKind.Array) return "";

            var parts = new List<string>();
            foreach (var e in root.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var t = GetStr(e, "text");
                if (t.Length > 0) parts.Add(t);
            }
            return string.Join("\n", parts);
        }
        catch { return ""; }
    }

    /// <summary>
    /// 私信的 content 字段是一段 **JSON 字符串**(不是对象), 需要按消息类型再解一层。
    /// 文字是 {"content":"..."}; 分享卡片有 title/text; 图片是 url。
    /// 解不出来时原样返回, 至少不让消息凭空消失。
    /// </summary>
    private static string DecodeMsgContent(string contentJson)
    {
        if (string.IsNullOrEmpty(contentJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return contentJson;
            foreach (var key in new[] { "content", "title", "text" })
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) return s!;
                }
            if (root.TryGetProperty("url", out _)) return "[图片]";
            return "";
        }
        catch { return contentJson; }
    }

    /// <summary>图片/表情消息的 content: {"url":"https://...", "width":..., "height":...}</summary>
    private static string ExtractImageUrl(string contentJson)
    {
        if (string.IsNullOrEmpty(contentJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            if (doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                return UrlUtil.Normalize(u.GetString());
        }
        catch { /* 解析失败当普通文本处理 */ }
        return "";
    }

    private async Task<string?> RegisterVoucherAsync(string voucherId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.bilibili.com/x/gaia-vgate/v1/register")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { voucher_id = voucherId }),
                    Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            // ★ 先补指纹再拼 cookie(2026-10-31): 未登录时 BuildCookieHeader() 可能一个 cookie
            //   都不剩 —— 而 gaia-vgate 是风控网关, 裸游客请求它大概率不放行, voucher 验证
            //   流程等于白走。EnsureBuvidAsync 有内存/磁盘两级缓存, 已有指纹时零网络开销。
            await EnsureBuvidAsync();
            var cookie = AppendFingerprint(SessionManager.Instance.BuildCookieHeader());
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.GetInt32() == 0 &&
                root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "token", "v_token", "f_token" })
                {
                    if (d.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(t.GetString()))
                        return t.GetString();
                }
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }

}
