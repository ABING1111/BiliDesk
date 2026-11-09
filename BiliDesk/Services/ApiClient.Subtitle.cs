using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BiliDesk.Models;

namespace BiliDesk.Services;

public partial class ApiClient
{
    /// <summary>
    /// 获取视频的字幕轨道列表(外挂字幕 + AI 字幕)。
    /// 调 /x/player/wbi/v2 —— 这是播放信息接口(playurl 那条链路上的 wbi 版),
    /// 字幕挂在 data.subtitle.subtitles 上。**不是** GetPlayUrlAsync 用的那个接口:
    /// 那边走的是 /x/player/wbi/playurl, 两者字段不互通, 别拿一个的返回去套另一个。
    /// AI 字幕与 UP 主上传的外挂字幕都在同一份列表里, 判定口径见 SubtitleTrack.IsAi
    /// (type 为主判据, ai_status / ai_type 兜底, 三处注释必须同口径)。
    /// </summary>
    /// <param name="bvid">视频 bvid</param>
    /// <param name="cid">视频 cid(分 P 用当前 cid 取对应字幕)</param>
    /// <returns>字幕轨列表(可能为空); ok=false 时 err 为失败原因</returns>
    public async Task<(bool ok, string? err, List<SubtitleTrack> tracks)> GetSubtitleTracksAsync(
        string bvid, long cid)
    {
        var tracks = new List<SubtitleTrack>();
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/v2", ps, sign: true);
            if (code != 0 || data == null)
                return (false, $"字幕轨获取失败({code} {msg})".Trim(), tracks);

            if (!data.Value.TryGetProperty("subtitle", out var sub) ||
                sub.ValueKind != JsonValueKind.Object ||
                !sub.TryGetProperty("subtitles", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                // 接口正常但没有字幕字段 = 该视频无字幕, 不算失败
                return (true, null, tracks);
            }

            foreach (var t in arr.EnumerateArray())
            {
                var url = NormalizeSubtitleUrl(GetStr(t, "subtitle_url"));
                if (string.IsNullOrEmpty(url)) continue;
                tracks.Add(new SubtitleTrack
                {
                    Lan = GetStr(t, "lan"),
                    LanDoc = GetStr(t, "lan_doc"),
                    // 判定口径(与 SubtitleTrack.IsAi 一致, 评审意见第 8 条要求三处注释统一):
                    //   type == 1 是主判据; ai_status == 2 / ai_type == 1 只是兜底。
                    //   ★ 不能只认 ai_status/ai_type: 实测 AI 轨常是 type=1 + ai_type=0 + ai_status=2,
                    //     老口径(ai_status==1 || ai_type>0)会把它误判成普通字幕, 两条中文轨因此同名为"中文"。
                    //   ★ 也不能只认 type: 部分老稿件/番剧的 AI 轨只带 ai_status, type 是 0。
                    // 三者都保留在模型里(ai_type 仅作 IsAi 兜底), 判定统一收在 IsAi 一个属性上, 显示名统一追加 " · AI"(点前后各留一个空格, 弹层里读作「中文 · AI」)。
                    Type = GetIntSafe(t, "type"),
                    AiType = GetIntOrNull(t, "ai_type"),
                    AiStatus = GetIntOrNull(t, "ai_status"),
                    SubtitleUrl = url,
                });
            }
            return (true, null, tracks);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, tracks);
        }
    }

    /// <summary>
    /// 下载并解析字幕 JSON(body 数组: from / to / content)。
    /// 字幕 URL 由 GetSubtitleTracksAsync 返回。
    /// </summary>
    public async Task<(bool ok, string? err, List<SubtitleCue> cues)> GetSubtitleCuesAsync(
        string subtitleUrl)
    {
        var cues = new List<SubtitleCue>();
        // ★ 再兜一次归一化(轨列表里已经归一化过): 只要有一个入口把原始的 "//..." 地址
        //   递给 HttpClient, 就会抛 "An invalid request URI was provided"(相对地址),
        //   异常被下面 catch 掉, 表现就是"字幕轨有、字幕永远不出来"。
        subtitleUrl = NormalizeSubtitleUrl(subtitleUrl);
        if (string.IsNullOrEmpty(subtitleUrl)) return (false, "字幕地址为空", cues);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, subtitleUrl);
            // ★ 只带设备指纹, **不带登录态**(评审意见第 9 条):
            //   字幕 JSON 挂在 aisubtitle.hdslb.com 这类静态 CDN 上, 属于公开资源,
            //   用不着 SESSDATA/bili_jct。原来这里拼的是 BuildCookieHeader(), 等于把
            //   完整登录 Cookie 发给一个只需要读取公开文件的第三方域名 —— 一旦那边日志
            //   落到别人手里就是账号凭据泄露。AppendFingerprint("") 拿到的只有 buvid3/buvid4。
            var cookie = AppendFingerprint("");
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"字幕下载失败 HTTP {(int)resp.StatusCode}", cues);

            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("body", out var body) ||
                body.ValueKind != JsonValueKind.Array)
                return (true, null, cues); // 无字幕内容, 不算失败

            foreach (var c in body.EnumerateArray())
            {
                var content = GetStr(c, "content");
                if (string.IsNullOrWhiteSpace(content)) continue;
                cues.Add(new SubtitleCue
                {
                    From = GetDoubleSafe(c, "from"),
                    To = GetDoubleSafe(c, "to"),
                    Content = content,
                });
            }
            return (true, null, cues);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, cues);
        }
    }

    /// <summary>
    /// 字幕地址归一化。
    ///
    /// ★ B 站字幕接口(aisubtitle.hdslb.com)给的 subtitle_url 是**协议相对地址**
    ///   ("//aisubtitle.hdslb.com/bfs/subtitle/xxx.json")。直接塞进 HttpRequestMessage
    ///   会抛 InvalidOperationException("An invalid request URI was provided"), 被
    ///   catch 之后表现为"字幕按钮有轨、字幕永远不出来"(errors.txt 里已实证)。
    ///   等价于 JS 中的 `u.startsWith('//') ? 'https:' + u : u`。
    ///
    /// ★ 明文 http 一律升到 https(评审意见第 9 条): 接口偶尔给的 subtitle_url 是
    ///   "http://aisubtitle.hdslb.com/..."。原样用会走明文 HTTP 拉取, 内容(以及请求行里的
    ///   Referer)可被链路上的任何一跳看到; 该 CDN 两种协议都支持, 直接升级没有副作用。
    /// </summary>
    private static string NormalizeSubtitleUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var u = url.Trim();
        if (u.StartsWith("//", StringComparison.Ordinal)) return "https:" + u;
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "https://" + u["http://".Length..];
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return u;
        return "https://" + u.TrimStart('/');
    }

    /// <summary>取可空 int: 字段缺失/null 返回 null, 用来区分"没有这个字段"和"字段值就是 0"。</summary>
    private static int? GetIntOrNull(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? GetIntSafe(e, name) : null;

    private static double GetDoubleSafe(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
