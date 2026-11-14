using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-15 新增: 视频字幕(CC / AI)的模型与解析。
//   解析逻辑全部是纯函数(只吃 JsonElement, 不做 IO), 单测直接喂 JSON 样本 ——
//   与 DanmakuParser / DynamicParser 同一套做法。
//   接口数据结构依据(2026-11 实测 + bilibili-API-collect):
//     · /x/player/wbi/v2 的 data.subtitle.subtitles[] 每项:
//       { id, id_str, lan, lan_doc, subtitle_url, subtitle_url_v2, type, ai_type, ai_status }
//       subtitle_url 是 **协议相对** 地址("//aisubtitle.hdslb.com/..."), 要补 https:
//     · data.need_login_subtitle = true 表示"有字幕但未登录拿不到" ——
//       这时 subtitles 是空数组, 必须靠这个字段区分"真没有"与"有但看不到"
//     · 字幕本体 JSON: { font_size, ..., body: [ { from, to, content, ... } ] }
//       from/to 是**秒**(浮点), content 是纯文本

/// <summary>一条字幕轨(CC 人工 / AI 生成)。列表弹层与"自动选轨"都吃它。</summary>
public sealed class SubtitleTrack
{
    public string Id { get; set; } = "";
    /// <summary>语言代码(zh-Hans / ai-zh / ai-en / en-US …)。ai- 前缀 = AI 生成</summary>
    public string Lan { get; set; } = "";
    /// <summary>语言的显示名(服务端下发的中文, 如「中文(简体)」「英语(机器翻译)」)</summary>
    public string LanDoc { get; set; } = "";
    public bool IsAi { get; set; }
    /// <summary>字幕本体 JSON 的下载地址(已补全 https:)</summary>
    public string Url { get; set; } = "";

    /// <summary>弹层里显示的名字: 优先服务端的 lan_doc, AI 轨追加标注</summary>
    public string DisplayName
    {
        get
        {
            var baseName = string.IsNullOrWhiteSpace(LanDoc) ? Lan : LanDoc;
            return IsAi && !baseName.Contains("AI", StringComparison.Ordinal)
                ? baseName + " · AI"
                : baseName;
        }
    }
}

/// <summary>一条字幕: 毫秒时间轴 + 纯文本。解析时统一从秒转成毫秒。</summary>
public sealed class SubtitleCue
{
    public long FromMs { get; set; }
    public long ToMs { get; set; }
    public string Content { get; set; } = "";
}

/// <summary>字幕 JSON 的解析(纯函数, 可单测)。</summary>
public static class SubtitleParser
{
    /// <summary>
    /// 解析 player/wbi/v2 响应里的字幕轨列表。
    ///
    /// ★★ 地址必须用 **subtitle_url(v1)**, 不能用 subtitle_url_v2(2026-11-15 用户实测
    ///   "HTTP 400" 的根因): v2 是 B 站新的**混淆地址**, path 是一串 URL 编码的控制字符
    ///   (%01%1B%5C=_%04…), 根本不是合法路径 —— 挂在 subtitle.bilibili.com 域上(DNS 在多数
    ///   网络解析不到), 无论原域还是换域请求都必然失败。v1 是明文地址 + auth_key query,
    ///   实测 HTTP 200 直接可下。v2 只在 v1 完全缺失时作为最后手段保留。
    /// ★ 空 URL 的轨直接丢弃 —— B 站偶尔会下发一条没有地址的占位轨, 拿它去下载必然 404。
    /// </summary>
    public static List<SubtitleTrack> ParseTracks(JsonElement data, out bool needLogin)
    {
        needLogin = data.TryGetProperty("need_login_subtitle", out var nl) && nl.ValueKind == JsonValueKind.True;

        var list = new List<SubtitleTrack>();
        if (!data.TryGetProperty("subtitle", out var sub) ||
            !sub.TryGetProperty("subtitles", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var e in arr.EnumerateArray())
        {
            var url = PickUrl(e);
            if (string.IsNullOrEmpty(url)) continue;
            var lan = GetStr(e, "lan");
            list.Add(new SubtitleTrack
            {
                Id = GetStr(e, "id_str") is { Length: > 0 } id ? id : GetStr(e, "id"),
                Lan = lan,
                LanDoc = GetStr(e, "lan_doc"),
                IsAi = lan.StartsWith("ai", StringComparison.OrdinalIgnoreCase),
                Url = url
            });
        }

        // 按 (id, url) 去重: 同一条轨出现两次时只留一条(保留顺序, 方便弹层稳定)
        return list
            .GroupBy(t => t.Id + "|" + t.Url, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// 选一条下载地址: v1(明文 + auth_key)优先; v2 仅当 v1 缺失时兜底,
    /// 且必须长得像合法 URL(见 IsPlausibleSubtitleUrl) —— 否则宁可用不上也不要拿乱码去请求。
    /// "//" 开头补 https: (协议相对地址)。
    /// </summary>
    private static string PickUrl(JsonElement e)
    {
        foreach (var name in new[] { "subtitle_url", "subtitle_url_v2" })
        {
            var raw = GetStr(e, name);
            if (string.IsNullOrEmpty(raw) || raw == "/") continue;
            var url = raw.StartsWith("//", StringComparison.Ordinal) ? "https:" + raw : raw;
            if (IsPlausibleSubtitleUrl(url)) return url;
        }
        return "";
    }

    /// <summary>
    /// 地址合法性粗检: host 可解析出、path 不含控制字符。
    /// ★ 专为拦截 subtitle_url_v2 那种混淆地址(path 是 %01%1B%5C 这类控制字符) ——
    ///   它请求必然 400, 还会把错误甩到用户脸上。
    /// </summary>
    private static bool IsPlausibleSubtitleUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (uri.Host.Length == 0) return false;
            foreach (var c in uri.AbsoluteUri)
            {
                if (c < 0x20 && c != '\n') return false;   // 控制字符 = 混淆地址, 直接否掉
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 解析字幕本体 JSON 的 body[] → 时间轴(毫秒、按开始时间排序、滤掉空文本)。
    /// ★ from/to 是秒(浮点); 排序是硬要求 —— 二分查找的前提就是有序。
    /// </summary>
    public static List<SubtitleCue> ParseBody(JsonElement root)
    {
        var list = new List<SubtitleCue>();
        if (!root.TryGetProperty("body", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var e in arr.EnumerateArray())
        {
            var content = GetStr(e, "content");
            if (string.IsNullOrWhiteSpace(content)) continue;
            var from = GetDouble(e, "from");
            var to = GetDouble(e, "to");
            // 结束早于开始(坏数据)直接丢: 留着会让二分结果乱跳
            if (to <= from) continue;
            list.Add(new SubtitleCue
            {
                FromMs = (long)Math.Round(from * 1000),
                ToMs = (long)Math.Round(to * 1000),
                Content = content.Trim()
            });
        }

        list.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));
        return list;
    }

    /// <summary>
    /// 自动选轨: 用户上次选的语言 → 人工中文 → AI 中文 → 第一条。
    /// ★ 上次语言排最前: 它是用户**显式选过**的偏好(比如选了英语), 必须压过"默认偏好中文"
    ///   —— 否则选了英语的用户, 下条视频永远被 AI 中文顶掉, 选择等于没记住。
    ///   从没选过(空串)时才走默认序。
    /// </summary>
    public static SubtitleTrack? PickPreferred(IReadOnlyList<SubtitleTrack> tracks, string? preferredLan)
    {
        if (tracks.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredLan))
        {
            var last = tracks.FirstOrDefault(t =>
                string.Equals(t.Lan, preferredLan, StringComparison.OrdinalIgnoreCase));
            if (last != null) return last;
        }

        var humanZh = tracks.FirstOrDefault(t => !t.IsAi && IsChinese(t));
        if (humanZh != null) return humanZh;
        var aiZh = tracks.FirstOrDefault(t => t.IsAi && IsChinese(t));
        if (aiZh != null) return aiZh;
        return tracks[0];
    }

    /// <summary>语言代码是否像中文(zh / zh-Hans / zh-CN …)。★ ai-zh 也算: AI 识别出的中文轨
    ///   的 lan 是 "ai-" + 语言, 中文部分在**后缀** —— 用 Contains 而不是 StartsWith。</summary>
    private static bool IsChinese(SubtitleTrack t)
        => t.Lan.Contains("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 二分查找 timeMs 处应显示的第一条 cue(from &lt;= t &lt; to)。
    /// 返回满足条件的**第一条**的索引; 没有 = -1。
    /// ★★ 只能对 FromMs(有序)二分, 绝不能对 ToMs 二分: 排序键是开始时间,
    ///   重叠时结束时间无序 —— 一条 1~4s 的长 cue 夹着一条 2~3s 的短 cue,
    ///   对 ToMs 二分会被那条短 cue 卡住, 长 cue 反而找不到(单测"重叠拼接"实测)。
    /// </summary>
    public static int FindVisible(IReadOnlyList<SubtitleCue> cues, long timeMs)
    {
        // 二分: 第一个 FromMs > timeMs 的位置 —— 候选区间是 [0, hi)
        var lo = 0;
        var hi = cues.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (cues[mid].FromMs > timeMs) hi = mid;
            else lo = mid + 1;
        }

        for (var i = 0; i < hi; i++)
        {
            if (cues[i].FromMs <= timeMs && cues[i].ToMs > timeMs) return i;
        }
        return -1;
    }

    /// <summary>
    /// 收集 timeMs 处应显示的全部文本(重叠 cue 用换行拼接)。
    /// ★ 重叠是真实存在的(AI 字幕在快速对话处常见), 全部显示比覆盖显示信息更全;
    ///   换行而不是空格拼接: 中文字幕没有空格分词习惯, 连成一串反而难读。
    /// ★ 性能: FromMs 有序, 循环在第一个"开始时间晚于当前"处提前收 ——
    ///   字幕总量是几千条的量级, 100ms 心跳 × 几千次比较可以忽略; 正确性优先于微优化。
    /// </summary>
    public static string VisibleText(IReadOnlyList<SubtitleCue> cues, long timeMs)
    {
        var parts = new List<string>();
        for (var i = 0; i < cues.Count && cues[i].FromMs <= timeMs; i++)
        {
            if (cues[i].ToMs > timeMs) parts.Add(cues[i].Content);
        }
        return string.Join("\n", parts);
    }

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.String || v.ValueKind == JsonValueKind.Number)
            ? v.ToString() : "";

    private static double GetDouble(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;
}
