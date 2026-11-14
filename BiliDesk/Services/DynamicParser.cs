using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 动态 JSON → <see cref="DynamicItem"/> 的纯解析器。
///
/// ★ 为什么单独抽成 public static 类:
///   · 动态页列表(feed/all)、详情(detail)、转发列表(detail/forward)三处共用同一套字段理解;
///   · 解析逻辑不依赖 HttpClient / 登录态, 可以直接用 JSON 样本做单元测试;
///   · 旧 ApiClient.Playurl.cs 里那套"只认视频"的 ParseDynamicVideo 已被它取代。
///
/// 兼容两种返回形态:
///   ① modules 嵌套结构(feed/all / detail 的主路径) —— 有正文/图片/统计;
///   ② 扁平结构(feed/nav 兜底) —— 只有视频标题/封面, 没有正文与统计。
/// </summary>
public static class DynamicParser
{
    // ------------------------------------------------------------ 入口

    /// <summary>解析 feed/all / detail 里的一条动态。结构异常或不可展示时返回 null。</summary>
    public static DynamicItem? ParseItem(JsonElement e)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Object) return null;

            // visible=false 是折叠/不可见动态, 不展示
            if (e.TryGetProperty("visible", out var vis) &&
                vis.ValueKind == JsonValueKind.False)
                return null;

            var id = GetStr(e, "id_str");
            if (id.Length == 0)
            {
                var rid = GetLongLoose(e, "rid");
                if (rid > 0) id = rid.ToString();
            }

            var (typeText, typeNum) = ReadType(e);

            // modules 主路径
            if (e.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Object)
                return ParseModulesItem(e, mods, id, typeText, typeNum);

            // 扁平兜底(feed/nav)
            return ParseFlatItem(e, id, typeText, typeNum);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 detail/forward 里的一条转发记录。</summary>
    public static DynamicRepostItem? ParseRepostItem(JsonElement e)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            var item = new DynamicRepostItem
            {
                Id = GetStr(e, "id_str"),
                PubTime = GetStr(e, "pub_time"),
            };
            if (e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                item.Mid = GetLongLoose(u, "mid");
                item.Name = GetStr(u, "name");
                item.Face = UrlUtil.Normalize(GetStr(u, "face"));
            }
            if (e.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.Object)
                item.Text = GetStr(d, "text");
            if (item.Id.Length == 0 && item.Name.Length == 0) return null;
            return item;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------ modules 路径

    private static DynamicItem? ParseModulesItem(
        JsonElement e, JsonElement mods, string id, string typeText, int typeNum)
    {
        var item = new DynamicItem
        {
            Id = id,
            TypeText = typeText,
            Kind = KindFromType(typeText, typeNum, ""),
        };

        ReadBasic(e, item);

        if (mods.TryGetProperty("module_author", out var author) &&
            author.ValueKind == JsonValueKind.Object)
        {
            ReadAuthor(author, item);
            ReadPubTime(author, item);
        }

        JsonElement? major = null;
        if (mods.TryGetProperty("module_dynamic", out var dyn) &&
            dyn.ValueKind == JsonValueKind.Object)
        {
            if (dyn.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.Object)
                item.ContentText = GetStr(desc, "text");

            if (dyn.TryGetProperty("major", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                major = m;
                var majorType = GetStr(m, "type");
                var kind = KindFromType(typeText, typeNum, majorType);
                if (kind != DynamicKind.Unknown) item.Kind = kind;
                ReadMajor(m, item);
            }
        }

        // 转发: 原动态递归解析。外层保留转发者自己的正文/统计。
        if ((item.Kind == DynamicKind.Forward || typeNum == 1 ||
             typeText.Equals("DYNAMIC_TYPE_FORWARD", StringComparison.Ordinal)) &&
            e.TryGetProperty("orig", out var orig) && orig.ValueKind == JsonValueKind.Object)
        {
            item.Kind = DynamicKind.Forward;
            item.Orig = ParseItem(orig);
        }

        if (mods.TryGetProperty("module_stat", out var stat) && stat.ValueKind == JsonValueKind.Object)
            ReadStat(stat, item);

        EnsureCommentTarget(item);

        // 没有任何可展示内容的异常条目丢掉, 不让它变成一张空卡
        if (!item.HasText && !item.HasImages && !item.HasTitle &&
            !item.HasAnyVideo && item.Orig == null && item.Kind != DynamicKind.Live)
            return null;

        return item;
    }

    /// <summary>basic.comment_id_str / comment_type 决定评论区挂在哪(视频 aid / 图文 id)。</summary>
    private static void ReadBasic(JsonElement e, DynamicItem item)
    {
        if (!e.TryGetProperty("basic", out var basic) || basic.ValueKind != JsonValueKind.Object) return;
        item.CommentOid = GetLongLoose(basic, "comment_id_str");
        var ct = GetIntSafe(basic, "comment_type");
        if (ct > 0) item.CommentType = ct;
    }

    /// <summary>接口没给 basic 时按内容兜底: 视频评论挂 aid(type=1), 其余挂动态 id(type=17)。</summary>
    private static void EnsureCommentTarget(DynamicItem item)
    {
        if (item.CommentOid > 0) return;

        // 转发动态的评论区是"这条转发"自己的, 不是原视频的评论区
        if (item.Kind == DynamicKind.Forward && long.TryParse(item.Id, out var fwdId))
        {
            item.CommentOid = fwdId;
            item.CommentType = 17;
            return;
        }

        if (item.HasPlayableVideo && item.Aid > 0)
        {
            item.CommentOid = item.Aid;
            item.CommentType = 1;
        }
        else if (item.HasForwardVideo && item.Orig?.Aid > 0)
        {
            item.CommentOid = item.Orig.Aid;
            item.CommentType = 1;
        }
        else if (long.TryParse(item.Id, out var dynId))
        {
            item.CommentOid = dynId;
            item.CommentType = 17;
        }
    }

    private static void ReadAuthor(JsonElement author, DynamicItem item)
    {
        item.AuthorMid = GetLongLoose(author, "mid");
        item.AuthorName = GetStr(author, "name");
        item.AuthorFace = UrlUtil.Normalize(GetStr(author, "face"));
        item.PubAction = GetStr(author, "pub_action");
    }

    private static void ReadPubTime(JsonElement e, DynamicItem item)
    {
        item.Pubdate = GetLongLoose(e, "pub_ts");
        var t = GetStr(e, "pub_time");
        if (t.Length == 0) return;

        if (item.Pubdate <= 0)
        {
            if (long.TryParse(t, out var ts) && ts > 0)
            {
                item.Pubdate = ts;
            }
            else
            {
                item.Pubdate = ParsePubTimeText(t);
                item.PubDateApprox = item.Pubdate > 0;
            }
        }
        item.PubTimeLabel = t;
    }

    private static void ReadMajor(JsonElement major, DynamicItem item)
    {
        var type = GetStr(major, "type");
        switch (type)
        {
            case "MAJOR_TYPE_ARCHIVE":
                if (major.TryGetProperty("archive", out var arc) && arc.ValueKind == JsonValueKind.Object)
                    ReadArchive(arc, item);
                break;

            case "MAJOR_TYPE_DRAW":
                if (major.TryGetProperty("draw", out var draw) && draw.ValueKind == JsonValueKind.Object)
                {
                    item.Kind = DynamicKind.ImageText;
                    if (draw.TryGetProperty("items", out var pics) && pics.ValueKind == JsonValueKind.Array)
                        ReadImages(pics, item.Images);
                }
                break;

            case "MAJOR_TYPE_OPUS":
                if (major.TryGetProperty("opus", out var opus) && opus.ValueKind == JsonValueKind.Object)
                {
                    item.Kind = DynamicKind.ImageText;
                    item.Title = GetStr(opus, "title");
                    item.JumpUrl = GetStr(opus, "jump_url");
                    if (string.IsNullOrWhiteSpace(item.ContentText) &&
                        opus.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.Object)
                        item.ContentText = GetStr(sum, "text");
                    if (opus.TryGetProperty("pics", out var pics) && pics.ValueKind == JsonValueKind.Array)
                        ReadImages(pics, item.Images);
                }
                break;

            case "MAJOR_TYPE_ARTICLE":
                if (major.TryGetProperty("article", out var art) && art.ValueKind == JsonValueKind.Object)
                {
                    item.Kind = DynamicKind.Article;
                    item.Title = GetStr(art, "title");
                    item.JumpUrl = GetStr(art, "jump_url");
                    if (string.IsNullOrWhiteSpace(item.ContentText))
                        item.ContentText = GetStr(art, "desc");
                    if (art.TryGetProperty("covers", out var covers) && covers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in covers.EnumerateArray())
                        {
                            if (c.ValueKind != JsonValueKind.String) continue;
                            var url = UrlUtil.Normalize(c.GetString());
                            if (url.Length == 0) continue;
                            item.Images.Add(new DynamicImage
                            {
                                Url = url,
                                ThumbUrl = MakeThumb(url, 480),
                                DetailUrl = MakeThumb(url, 720),
                            });
                        }
                    }
                    if (item.Images.Count > 0) item.Cover = item.Images[0].Url;
                }
                break;

            case "MAJOR_TYPE_LIVE":
                if (major.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.Object)
                    ReadLive(live, item);
                break;

            case "MAJOR_TYPE_LIVE_RCMD":
                if (major.TryGetProperty("live_rcmd", out var rcmd) && rcmd.ValueKind == JsonValueKind.Object)
                {
                    // content 是一层 JSON 字符串, 里面才是直播间信息
                    var content = GetStr(rcmd, "content");
                    if (content.Length > 0)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(content);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("live_play_info", out var info) &&
                                info.ValueKind == JsonValueKind.Object)
                                ReadLive(info, item);
                        }
                        catch { /* 单条直播结构异常不影响整页 */ }
                    }
                }
                break;

            // COMMON / PGC / COURSES / MUSIC / UGC_SEASON / NONE 等:
            // 首版动态卡只综合"视频 + 图文 + 文字 + 专栏 + 转发", 其余跳过,
            // 避免广告/相关推荐/失效动态混进时间线。
            default:
                break;
        }
    }

    private static void ReadArchive(JsonElement arc, DynamicItem item)
    {
        item.Kind = DynamicKind.Video;
        item.Bvid = GetStr(arc, "bvid");
        item.Aid = GetLongLoose(arc, "aid");
        item.Title = GetStr(arc, "title");
        item.Cover = UrlUtil.Normalize(GetStr(arc, "cover"));
        item.Duration = GetStr(arc, "duration_text");
        item.JumpUrl = GetStr(arc, "jump_url");

        if (string.IsNullOrEmpty(item.Bvid))
            item.Bvid = ExtractBvidFromJumpUrl(item.JumpUrl);

        if (arc.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            item.ViewCount = ParseCountText(GetStr(st, "play"));
            if (item.ViewCount <= 0) item.ViewCount = GetLongLoose(st, "play");
            item.DanmakuCount = ParseCountText(GetStr(st, "danmaku"));
            if (item.DanmakuCount <= 0) item.DanmakuCount = GetLongLoose(st, "danmaku");
        }
    }

    private static void ReadLive(JsonElement live, DynamicItem item)
    {
        item.Kind = DynamicKind.Live;
        item.RoomId = GetLongLoose(live, "id");
        if (item.RoomId <= 0) item.RoomId = GetLongLoose(live, "room_id");
        item.Title = GetStr(live, "title");
        item.Cover = UrlUtil.Normalize(GetStr(live, "cover"));
        item.AreaName = GetStr(live, "desc_first");
        if (string.IsNullOrEmpty(item.AreaName)) item.AreaName = GetStr(live, "area_name");
        item.JumpUrl = GetStr(live, "jump_url");
    }

    private static void ReadImages(JsonElement pics, List<DynamicImage> images)
    {
        foreach (var p in pics.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var url = UrlUtil.Normalize(GetStr(p, "src"));
            if (url.Length == 0) url = UrlUtil.Normalize(GetStr(p, "url"));
            if (url.Length == 0) continue;
            images.Add(new DynamicImage
            {
                Url = url,
                ThumbUrl = MakeThumb(url, 480),
                DetailUrl = MakeThumb(url, 720),
                Width = GetIntSafe(p, "width"),
                Height = GetIntSafe(p, "height"),
            });
        }
    }

    private static void ReadStat(JsonElement stat, DynamicItem item)
    {
        item.HasStat = true;
        if (stat.TryGetProperty("forward", out var f) && f.ValueKind == JsonValueKind.Object)
            item.ForwardCount = GetLongLoose(f, "count");
        if (stat.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.Object)
            item.CommentCount = GetLongLoose(c, "count");
        if (stat.TryGetProperty("like", out var l) && l.ValueKind == JsonValueKind.Object)
        {
            item.LikeCount = GetLongLoose(l, "count");
            if (l.TryGetProperty("status", out var s))
            {
                item.IsLiked = s.ValueKind == JsonValueKind.True ||
                               (s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1);
            }
        }
    }

    // ------------------------------------------------------------ 扁平兜底(feed/nav)

    private static DynamicItem? ParseFlatItem(JsonElement e, string id, string typeText, int typeNum)
    {
        // 转发: 递归取原动态里的视频(旧行为), 但外层保留转发者信息
        if ((typeNum == 1 || typeText.Equals("DYNAMIC_TYPE_FORWARD", StringComparison.Ordinal)) &&
            e.TryGetProperty("orig", out var orig) && orig.ValueKind == JsonValueKind.Object)
        {
            var inner = ParseItem(orig);
            if (inner == null) return null;
            var outer = new DynamicItem
            {
                Id = id,
                TypeText = typeText,
                Kind = DynamicKind.Forward,
                Orig = inner,
            };
            ReadFlatAuthorAndTime(e, outer);
            EnsureCommentTarget(outer);
            return outer;
        }

        var isVideo = typeNum == 8 ||
                      typeText.Equals("DYNAMIC_TYPE_AV", StringComparison.Ordinal) ||
                      typeText.Equals("DYNAMIC_TYPE_VIDEO", StringComparison.Ordinal);
        if (!isVideo)
        {
            // 扁平结构没有正文/图片字段, 非视频动态无法综合展示, 跳过
            return null;
        }

        var bvid = ExtractBvidFromJumpUrl(GetStr(e, "jump_url"));
        if (bvid.Length == 0) bvid = GetStr(e, "bvid");
        if (bvid.Length == 0) return null;

        var item = new DynamicItem
        {
            Id = id,
            TypeText = typeText,
            Kind = DynamicKind.Video,
            Bvid = bvid,
            Title = GetStr(e, "title"),
            Cover = UrlUtil.Normalize(GetStr(e, "cover")),
            JumpUrl = GetStr(e, "jump_url"),
        };
        if (long.TryParse(GetStr(e, "rid"), out var rid)) item.Aid = rid;
        ReadFlatAuthorAndTime(e, item);
        EnsureCommentTarget(item);
        return item;
    }

    private static void ReadFlatAuthorAndTime(JsonElement e, DynamicItem item)
    {
        if (e.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
        {
            item.AuthorName = GetStr(au, "name");
            item.AuthorMid = GetLongLoose(au, "mid");
            item.AuthorFace = UrlUtil.Normalize(GetStr(au, "face"));
        }
        if (item.AuthorMid <= 0) item.AuthorMid = GetLongLoose(e, "uid");
        ReadPubTime(e, item);
    }

    // ------------------------------------------------------------ 类型 / 工具

    private static DynamicKind KindFromType(string typeText, int typeNum, string majorType)
    {
        switch (majorType)
        {
            case "MAJOR_TYPE_ARCHIVE": return DynamicKind.Video;
            case "MAJOR_TYPE_DRAW":
            case "MAJOR_TYPE_OPUS": return DynamicKind.ImageText;
            case "MAJOR_TYPE_ARTICLE": return DynamicKind.Article;
            case "MAJOR_TYPE_LIVE":
            case "MAJOR_TYPE_LIVE_RCMD": return DynamicKind.Live;
        }

        if (typeText.Length > 0)
        {
            switch (typeText)
            {
                case "DYNAMIC_TYPE_AV":
                case "DYNAMIC_TYPE_VIDEO": return DynamicKind.Video;
                case "DYNAMIC_TYPE_DRAW":
                case "DYNAMIC_TYPE_OPUS": return DynamicKind.ImageText;
                case "DYNAMIC_TYPE_WORD":
                case "DYNAMIC_TYPE_TEXT": return DynamicKind.Text;
                case "DYNAMIC_TYPE_ARTICLE": return DynamicKind.Article;
                case "DYNAMIC_TYPE_FORWARD": return DynamicKind.Forward;
                case "DYNAMIC_TYPE_LIVE":
                case "DYNAMIC_TYPE_LIVE_RCMD": return DynamicKind.Live;
            }
        }

        return typeNum switch
        {
            8 => DynamicKind.Video,
            2 => DynamicKind.ImageText,
            4 => DynamicKind.Text,
            64 => DynamicKind.Article,
            1 => DynamicKind.Forward,
            _ => DynamicKind.Unknown,
        };
    }

    private static (string text, int num) ReadType(JsonElement e)
    {
        if (!e.TryGetProperty("type", out var t)) return ("", 0);
        if (t.ValueKind == JsonValueKind.Number)
        {
            var n = t.GetInt32();
            return (n.ToString(CultureInfo.InvariantCulture), n);
        }
        if (t.ValueKind == JsonValueKind.String)
        {
            var s = t.GetString() ?? "";
            if (int.TryParse(s, out var n)) return (s, n);
            return (s, 0);
        }
        return ("", 0);
    }

    /// <summary>
    /// 给 B 站 BFS 图片加宽度缩略后缀。只限宽、不裁切, 保留原始宽高比;
    /// 卡片网格里的方形观感由 UI 的 UniformToFill 负责。
    /// ★ 用 .jpg 而不是 .webp: WPF 的 WIC 不一定装 WebP 解码器, jpg 全平台可解。
    /// </summary>
    public static string MakeThumb(string url, int width)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.Contains('@')) return url;
        if (!url.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase)) return url;
        return url + "@" + width.ToString(CultureInfo.InvariantCulture) + "w.jpg";
    }

    /// <summary>jump_url 形如 "//www.bilibili.com/video/BV1xx" → 截出 bvid。</summary>
    public static string ExtractBvidFromJumpUrl(string jumpUrl)
    {
        if (string.IsNullOrEmpty(jumpUrl)) return "";
        var i = jumpUrl.IndexOf("/video/", StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = jumpUrl[(i + "/video/".Length)..];
        var end = rest.IndexOf('?');
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>把 "1.4万" / "12.3亿" / "217" 这类展示串还原成数字。</summary>
    public static long ParseCountText(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.Trim();
        double mul = 1;
        if (s.EndsWith("万", StringComparison.Ordinal)) { mul = 10_000; s = s[..^1]; }
        else if (s.EndsWith("亿", StringComparison.Ordinal)) { mul = 100_000_000; s = s[..^1]; }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (long)(v * mul) : 0;
    }

    /// <summary>
    /// 把接口 pub_time 文本("刚刚"/"27分钟前"/"昨天 18:54"/"09-20")反推成大致时间戳。
    /// 认不出来返回 0, 由调用方回落原文。
    /// </summary>
    public static long ParsePubTimeText(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        var now = DateTime.Now;
        if (s == "刚刚") return ToEpoch(now);

        if (s.EndsWith("分钟前", StringComparison.Ordinal) &&
            int.TryParse(s[..^3], out var min)) return ToEpoch(now.AddMinutes(-min));
        if (s.EndsWith("小时前", StringComparison.Ordinal) &&
            int.TryParse(s[..^3], out var hr)) return ToEpoch(now.AddHours(-hr));
        if (s.EndsWith("天前", StringComparison.Ordinal) &&
            int.TryParse(s[..^2], out var day)) return ToEpoch(now.AddDays(-day));

        if (s.StartsWith("昨天", StringComparison.Ordinal))
        {
            var rest = s.Length > 2 ? s[2..].Trim() : "";
            var d = now.Date.AddDays(-1);
            if (rest.Length > 0)
            {
                if (!DateTime.TryParseExact(rest, "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var t2)) return 0;
                d = d.AddHours(t2.Hour).AddMinutes(t2.Minute);
            }
            return ToEpoch(d);
        }

        string[] patterns = { "MM-dd HH:mm", "MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
        foreach (var p in patterns)
        {
            if (!DateTime.TryParseExact(s, p, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d)) continue;
            if (p.StartsWith("MM", StringComparison.Ordinal))
            {
                d = new DateTime(now.Year, d.Month, d.Day, d.Hour, d.Minute, 0);
                if (d > now) d = d.AddYears(-1);
            }
            return ToEpoch(d);
        }
        return 0;
    }

    private static long ToEpoch(DateTime local)
        => new DateTimeOffset(local).ToUnixTimeSeconds();

    // ------------------------------------------------------------ JSON 小工具

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int GetIntSafe(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out var i)) return i;
            if (v.TryGetDouble(out var d)) return (int)d;
        }
        if (v.ValueKind == JsonValueKind.String &&
            int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static long GetLongLoose(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var l)) return l;
            if (v.TryGetDouble(out var d)) return (long)d;
        }
        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }
}