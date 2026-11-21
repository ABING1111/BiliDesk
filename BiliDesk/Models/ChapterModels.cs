using System;
using System.Collections.Generic;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-21 新增: 视频章节(UP 主在进度条上标注的"分段章节")。
//
// 数据结构依据(bilibili-API-collect docs/video/player.md + 2026-11 实测):
//   · 数据来自 GET /x/player/wbi/v2 的 data.view_points[] —— 和字幕轨是**同一个响应**,
//     所以章节不额外发请求(见 ApiClient.GetPlayerInfoAsync)。
//   · 每项: { content, from, to, type, imgUrl, logoUrl, team_type, team_name }
//       from / to 是**秒**(整数); content 是章节名; imgUrl 是 B 站自动截的章节封面。
//   · 章节按 **cid** 返回: 多P 稿件每个 P 各有自己的章节。
//   · 没有章节的视频给**空数组**(不是不返回这个字段), 所以"空"就是"无章节"。
//   · imgUrl 常是 http:// 或协议相对地址("//i0.hdslb.com/..."), 统一补成 https。
//
// ★ 旧接口 /x/player/v2 **绝不能**用来兜底章节: 那条路有 CDN 缓存串扰(实测会返回别的
//   视频的字幕, yt-dlp PR#11708 已确认), 章节同样不可信。见 ApiClient 里的说明。

/// <summary>一个章节。时间统一存**毫秒**(与播放器内部一致, 绘制/跳转不用再换算)。</summary>
public sealed class VideoChapter
{
    /// <summary>章节名。解析时已保证非空(服务端给空串会退化成「第 N 章」)。</summary>
    public string Content { get; set; } = "";

    /// <summary>起始位置(毫秒, 含)。</summary>
    public long FromMs { get; set; }

    /// <summary>结束位置(毫秒, 不含)。</summary>
    public long ToMs { get; set; }

    /// <summary>章节封面地址(已补全 https; 可能为空 —— 服务端偶尔不给图)。</summary>
    public string ImageUrl { get; set; } = "";

    /// <summary>弹层里显示的时间("02:37:47" / "09:15")。</summary>
    public string TimeText => ChapterParser.FormatOffset(FromMs);

    /// <summary>章节时长(毫秒)。</summary>
    public long DurationMs => Math.Max(0, ToMs - FromMs);
}

/// <summary>
/// 章节的解析与查找(纯函数, 只吃 JsonElement / 列表, 不做 IO)。
/// 单测直接喂 JSON 样本与时间点 —— 与 SubtitleParser / DanmakuParser 同一套做法。
/// </summary>
public static class ChapterParser
{
    /// <summary>
    /// 解析 <c>data.view_points</c>。
    ///
    /// 容错原则: **坏数据宁可丢一条, 不能画错**。
    ///   · from 为负 → 夹到 0;
    ///   · to &lt;= from(时长为零/倒挂) → 丢弃, 留着会让进度条刻度重叠、跳转到原地;
    ///   · content 为空 → 退化成「第 N 章」(N 按**最终时间顺序**, 与弹层里看到的位置一致);
    ///   · 同一 from 重复出现 → 只留第一条(服务端偶发重复, 会在刻度上叠成一条线);
    ///   · 结果按 FromMs 升序 —— 找当前章靠有序性, 与字幕时间轴同理。
    /// </summary>
    public static List<VideoChapter> Parse(JsonElement data)
    {
        var list = new List<VideoChapter>();
        if (!data.TryGetProperty("view_points", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var e in arr.EnumerateArray())
        {
            var fromMs = (long)Math.Round(Math.Max(0, GetDouble(e, "from")) * 1000);
            var toMs = (long)Math.Round(GetDouble(e, "to") * 1000);
            if (toMs <= fromMs) continue;

            list.Add(new VideoChapter
            {
                Content = GetStr(e, "content").Trim(),
                FromMs = fromMs,
                ToMs = toMs,
                ImageUrl = NormalizeImageUrl(GetStr(e, "imgUrl"))
            });
        }

        list.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));

        // 去重(按起点): 排序后才能一趟扫完
        var deduped = new List<VideoChapter>(list.Count);
        foreach (var c in list)
        {
            if (deduped.Count > 0 && deduped[^1].FromMs == c.FromMs) continue;
            deduped.Add(c);
        }

        // 空章名在**排序之后**才补: 用最终时间顺序的序号, 否则「第 N 章」会跟界面上的
        // 位置对不上(服务端返回顺序不保证有序, 而我们显示的是时间轴顺序)。
        for (var i = 0; i < deduped.Count; i++)
        {
            if (deduped[i].Content.Length == 0) deduped[i].Content = $"第 {i + 1} 章";
        }
        return deduped;
    }

    /// <summary>
    /// 章节数够不够撑起"章节功能"。
    /// ★ 少于 2 章一律当没有: 单章视频就是"整片一段", 画刻度/给入口纯属噪音。
    /// </summary>
    public static bool IsUsable(IReadOnlyList<VideoChapter> chapters) => chapters.Count >= 2;

    /// <summary>
    /// 找 timeMs 落在哪一章(返回下标; 找不到返回 -1)。
    ///
    /// 语义刻意宽于"严格落在 [from, to) 内":
    ///   · 章节之间有空隙(服务端数据不严丝合缝)时, 算**前一个**章 —— 用户看到的章节名
    ///     应该是"刚过去的那一段", 而不是突然变成空白;
    ///   · 超过最后一章的 to(结尾常有一小段没标)也算最后一章, 同理;
    ///   · 只有"早于第一章"才返回 -1(那种情况控制栏宁可显示笼统的「章节」)。
    /// </summary>
    public static int FindIndex(IReadOnlyList<VideoChapter> chapters, long timeMs)
    {
        if (chapters.Count == 0) return -1;

        // 按 FromMs 二分: 找最后一个 FromMs <= timeMs 的项
        var lo = 0;
        var hi = chapters.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (chapters[mid].FromMs <= timeMs)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    /// <summary>
    /// 章节时间戳的显示格式: 不足一小时 "mm:ss", 满一小时 "h:mm:ss"(与播放器时间标签同款)。
    /// ★ 不能直接用 TimeSpan 的 "mm": 它不会进位到小时(项目里踩过这个坑, 见 FormatClock)。
    /// </summary>
    public static string FormatOffset(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    /// <summary>
    /// 章节封面地址规范化: 补协议、http 升 https。
    /// ★ 升 https 的理由: 播放器窗口里其它封面(CoverLoader)走的都是 https, 混着 http
    ///   在部分网络下会被劫持/重定向, 而且没必要为一张缩略图留一条明文请求。
    /// </summary>
    private static string NormalizeImageUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var url = raw.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "https://" + url.Substring("http://".Length);
        return url;
    }

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.String || v.ValueKind == JsonValueKind.Number)
            ? v.ToString() : "";

    private static double GetDouble(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;
}

/// <summary>
/// 章节弹层里的一行(绑定用)。
/// ★ 单独包一层而不是直接绑 VideoChapter: 弹层要"第几章"的序号(用于空标题兜底与
///   无障碍读屏), 而模型本身不该带 UI 序号。
/// </summary>
public sealed class ChapterRow
{
    public ChapterRow(VideoChapter chapter, int index)
    {
        Chapter = chapter;
        Index = index;
    }

    public VideoChapter Chapter { get; }
    public int Index { get; }
    public string Content => Chapter.Content;
    public string TimeText => Chapter.TimeText;
    public string ImageUrl => Chapter.ImageUrl;
}
