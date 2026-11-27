using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// 这条动态能否进「关注时间线」。
    /// ★ feed/all 会在关注动态之间插播"你可能感兴趣"的推荐条目, 它们的
    ///   module_author.following 明确是 false —— 那正是 issue #99 里"动态页出现
    ///   未关注者的视频"的来源, 必须滤掉。
    /// ★ 只认接口明确给出的 false: 字段缺失(旧形态/空间动态)或为 null(自己的动态)
    ///   一律放行, 否则一种返回形态变化就可能把整条时间线滤成空。
    /// </summary>
    public static bool IsFromFollowedAuthor(DynamicItem item) => item.AuthorFollowing != false;

    /// <summary>
    /// 解析关注时间线(feed/all)里的一条动态: 与 <see cref="ParseItem"/> 相同,
    /// 但接口标了"未关注作者"的推荐条目直接丢弃(返回 null)。
    /// </summary>
    public static DynamicItem? ParseTimelineItem(JsonElement e)
    {
        var item = ParseItem(e);
        return item != null && IsFromFollowedAuthor(item) ? item : null;
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

        if (mods.TryGetProperty("module_dynamic", out var dyn) &&
            dyn.ValueKind == JsonValueKind.Object)
        {
            if (dyn.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.Object)
            {
                item.ContentText = ReadRichText(desc);
                ReadEmotes(desc, item);
            }

            if (dyn.TryGetProperty("major", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                var majorType = GetStr(m, "type");
                var kind = KindFromType(typeText, typeNum, majorType);
                if (kind != DynamicKind.Unknown) item.Kind = kind;
                ReadMajor(m, item);
            }
        }

        // ★★★ 2026-11-28 修"图文动态图片显示不全":
        //   专栏动态在详情接口里 major 会变成 MAJOR_TYPE_OPUS(只剩封面), 但 type 仍是
        //   DYNAMIC_TYPE_ARTICLE。KindFromType 优先认 major, 于是详情窗把专栏当成普通图文,
        //   既不显示专栏标题、也不会去补正文图。这里按 type 掰回 Article。
        if (typeText.Equals("DYNAMIC_TYPE_ARTICLE", StringComparison.Ordinal))
        {
            item.Kind = DynamicKind.Article;
            if (item.ArticleId.Length == 0 && item.CommentType == 12 && item.CommentOid > 0)
                item.ArticleId = item.CommentOid.ToString(CultureInfo.InvariantCulture);
        }

        // 专栏摘要/图文正文里会用 `[图片]` 代表内嵌图片。图片已经单独收进 Images,
        // 再把占位符当普通文字画出来就是用户看到的"一堆 [图片]"。
        if (item.Images.Count > 0 && item.ContentText.Contains("[图片]", StringComparison.Ordinal))
            item.ContentText = StripImagePlaceholders(item.ContentText);

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

        // ★ 专栏动态的评论挂在专栏 id 上(comment_type=12), 这个 rid 也正是
        //   x/article/view 需要的 article id。详情接口会把 major 变成 OPUS,
        //   只有这里还能稳定拿到专栏 id(见 DynamicItem.ArticleId)。
        if (ct == 12)
        {
            var rid = GetStr(basic, "rid_str");
            if (rid.Length == 0) rid = GetStr(basic, "comment_id_str");
            if (rid.Length > 0) item.ArticleId = rid;
        }
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
        item.AuthorFollowing = GetBoolNullable(author, "following");
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
                    {
                        item.ContentText = ReadRichText(sum);
                        ReadEmotes(sum, item);
                    }
                    // 个别形态把 rich_text_nodes 直接挂在 opus 上(不套 summary)。
                    // 只在前面没收到表情时补一次, 不覆盖 summary 里同一份数据。
                    if (item.Emotes.Count == 0) ReadEmotes(opus, item);
                    if (opus.TryGetProperty("pics", out var pics) && pics.ValueKind == JsonValueKind.Array)
                        ReadImages(pics, item.Images);
                }
                break;

            case "MAJOR_TYPE_ARTICLE":
                if (major.TryGetProperty("article", out var art) && art.ValueKind == JsonValueKind.Object)
                {
                    item.Kind = DynamicKind.Article;
                    item.ArticleId = GetStr(art, "id");
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
        item.VideoPubdate = GetLongLoose(arc, "pubdate");

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

    // ------------------------------------------------------------ 专栏正文

    /// <summary>
    /// 解析 `x/article/view` 的 `data`: 专栏正文文字 + 正文里的全部图片。
    ///
    /// ★ 为什么需要: 动态接口对专栏只给 `major.article.covers[0]`(1 张封面),
    ///   正文里的几十张图全在文章的 opus content 里。详情窗必须再请求一次
    ///   `x/article/view` 才能把"只显示 1 张"补成完整图集。
    ///
    /// 兼容三种形态:
    ///   ① 新版专栏/图文 —— `data.opus.content.paragraphs`, `para_type=1` 文字 / `2` 图片 / `7` 链接卡;
    ///   ② 旧版专栏 —— `data.content` 是 HTML;
    ///   ③ 兜底 —— `data.image_urls`(通常只有封面)。
    ///
    /// ★ 图片按出现顺序收集, 文字按段落换行拼接; 详情窗是"正文在前、图集在后",
    ///   不做严格的段落图文交错(这个帖子是壁纸合集, 视觉上没有区别)。
    /// </summary>
    public static DynamicArticle? ParseArticleContent(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;

        var article = new DynamicArticle();
        var text = new StringBuilder();

        // ① 新版: opus.content.paragraphs
        if (data.TryGetProperty("opus", out var opus) && opus.ValueKind == JsonValueKind.Object &&
            opus.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("paragraphs", out var paragraphs) &&
            paragraphs.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in paragraphs.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                switch (GetIntSafe(p, "para_type"))
                {
                    case 1:
                        AppendArticleText(text, ReadArticleText(p));
                        break;

                    case 2:
                        if (!p.TryGetProperty("pic", out var pic) || pic.ValueKind != JsonValueKind.Object)
                            break;
                        if (!pic.TryGetProperty("pics", out var pics) || pics.ValueKind != JsonValueKind.Array)
                            break;
                        ReadImages(pics, article.Images);
                        break;

                    // 段落里嵌入的站内卡片(通常是"我的其他文章"): 至少把标题保留下来
                    case 7:
                        if (p.TryGetProperty("link_card", out var linkCard) &&
                            linkCard.ValueKind == JsonValueKind.Object &&
                            linkCard.TryGetProperty("card", out var card) &&
                            card.ValueKind == JsonValueKind.Object)
                        {
                            var title = GetStr(card, "show_text");
                            if (title.Length == 0) title = GetStr(linkCard, "default_text");
                            AppendArticleText(text, title);
                        }
                        break;
                }
            }
        }

        // ② 旧版: content 是 HTML。新版也可能只给 content 文字(图片在 opus 里),
        //    所以这里按缺什么补什么, 避免把同一段文字拼两遍。
        if (data.TryGetProperty("content", out var html) && html.ValueKind == JsonValueKind.String)
        {
            var raw = html.GetString() ?? "";
            if (raw.Length > 0)
            {
                if (text.Length == 0) AppendArticleText(text, StripHtml(raw));
                if (article.Images.Count == 0)
                {
                    foreach (Match m in ImgSrcRegex.Matches(raw))
                    {
                        var url = UrlUtil.Normalize(m.Groups[1].Value);
                        if (url.Length > 0) AddArticleImage(url, 0, 0, article.Images);
                    }
                }
            }
        }

        // ③ 兜底: 至少把 image_urls 收进来
        if (article.Images.Count == 0 &&
            data.TryGetProperty("image_urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in urls.EnumerateArray())
            {
                if (u.ValueKind != JsonValueKind.String) continue;
                var url = UrlUtil.Normalize(u.GetString());
                if (url.Length > 0) AddArticleImage(url, 0, 0, article.Images);
            }
        }

        article.Text = text.ToString().Trim();
        return article.Images.Count > 0 || article.HasText ? article : null;
    }

    /// <summary>读一段专栏正文: `text.nodes[].word.words`(旧形态直接给 `text.text`)。</summary>
    private static string ReadArticleText(JsonElement paragraph)
    {
        if (!paragraph.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.Object)
            return "";

        if (text.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object) continue;
                if (n.TryGetProperty("word", out var word) && word.ValueKind == JsonValueKind.Object)
                {
                    var w = GetStr(word, "words");
                    if (w.Length == 0) w = GetStr(word, "text");
                    sb.Append(w);
                }
                else
                {
                    sb.Append(GetStr(n, "text"));
                }
            }
            return sb.ToString().Trim();
        }

        return GetStr(text, "text").Trim();
    }

    private static void AppendArticleText(StringBuilder sb, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(text);
    }

    private static void AddArticleImage(string url, int width, int height, List<DynamicImage> images)
    {
        images.Add(new DynamicImage
        {
            Url = url,
            ThumbUrl = MakeThumb(url, 480),
            DetailUrl = MakeThumb(url, 720),
            Width = width,
            Height = height,
        });
    }

    private static readonly Regex ImgSrcRegex = new(
        "(?is)<img[^>]+src\\s*=\\s*[\"']([^\"']+)", RegexOptions.Compiled);

    /// <summary>旧版专栏 content 是 HTML: 去标签只留文字, 图片由 <see cref="ImgSrcRegex"/> 单独抽。</summary>
    private static string StripHtml(string html)
    {
        var s = Regex.Replace(html, "(?i)<br\\s*/?>", "\n");
        s = Regex.Replace(s, "(?i)</p\\s*>", "\n");
        s = Regex.Replace(s, "<[^>]+>", "");
        s = s.Replace("&nbsp;", " ", StringComparison.Ordinal)
             .Replace("&amp;", "&", StringComparison.Ordinal)
             .Replace("&lt;", "<", StringComparison.Ordinal)
             .Replace("&gt;", ">", StringComparison.Ordinal)
             .Replace("&quot;", "\"", StringComparison.Ordinal)
             .Replace("&#39;", "'", StringComparison.Ordinal);
        s = Regex.Replace(s, "\\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>
    /// 去掉正文里的 `[图片]` 占位符。
    ///
    /// ★ B 站把内嵌图片在纯文本里表示成 `[图片]`, 图片本身在另一组字段里。
    ///   图片已经单独展示时, 再把这些占位符当普通文字画出来就是满屏 `[图片]`。
    /// ★ 只在动态确实有图时调用(见 ParseModulesItem), 纯文字动态里作者真写了
    ///   `[图片]` 三个字的情况不会被误删。
    /// </summary>
    public static string StripImagePlaceholders(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[图片]", StringComparison.Ordinal))
            return text;

        var s = text.Replace("[图片]", "", StringComparison.Ordinal)
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (++blank > 1) continue;
            }
            else
            {
                blank = 0;
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString().Trim();
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

    // ------------------------------------------------------------ 富文本(正文 + 表情)

    /// <summary>
    /// 读一段富文本的正文。
    ///
    /// ★ 正常情况 `text` 就是排好的纯文本(表情短代码原样留在里面);
    ///   极少数形态只给 `rich_text_nodes`, 这时按节点顺序拼回来,
    ///   否则整条动态会因为"没有正文"变成空卡。
    /// </summary>
    private static string ReadRichText(JsonElement container)
    {
        var text = GetStr(container, "text");
        if (text.Length > 0) return text;

        if (!container.TryGetProperty("rich_text_nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
            return text;

        var sb = new System.Text.StringBuilder();
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            var t = GetStr(n, "text");
            if (t.Length == 0) t = GetStr(n, "orig_text");
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 从 `rich_text_nodes` 里收集表情: 短代码 → 图片地址。
    ///
    /// ★ 2026-11-28 修"图文动态正文不显示表情": 正文是纯文本, 表情只在节点里带
    ///   `emoji.icon_url`(形如 `{"type":"RICH_TEXT_NODE_TYPE_EMOJI",
    ///   "text":"[脱单doge]","emoji":{"icon_url":"//i0.hdslb.com/..."}}`)。
    ///   之前只取 text, 界面就把 `[脱单doge]` 当普通文字画出来了。
    /// ★ 短代码优先取节点 text(与正文里出现的字符完全一致), 其次 emoji.text。
    ///   个别节点 text 是表情名而不是短代码, 这时用 emoji.text 才能对上正文。
    /// </summary>
    private static void ReadEmotes(JsonElement container, DynamicItem item)
    {
        if (!container.TryGetProperty("rich_text_nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
            return;

        foreach (var n in nodes.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            if (!n.TryGetProperty("emoji", out var emoji) || emoji.ValueKind != JsonValueKind.Object)
                continue;

            var url = UrlUtil.Normalize(GetStr(emoji, "icon_url"));
            if (url.Length == 0) continue;

            var code = GetStr(n, "text");
            if (!code.StartsWith("[", StringComparison.Ordinal)) code = GetStr(emoji, "text");
            if (code.Length == 0) code = GetStr(n, "text");
            if (code.Length == 0) continue;
            if (item.Emotes.ContainsKey(code)) continue;

            item.Emotes[code] = new MsgEmote
            {
                Text = code,
                Url = url,
                // 动态表情没有 `meta.size` 那套, 少数形态会带 size; 只有 2 才当大表情。
                Size = GetIntSafe(emoji, "size") > 1 ? 2 : 1,
            };
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
            item.AuthorFollowing = GetBoolNullable(au, "following");
        }
        if (item.AuthorMid <= 0) item.AuthorMid = GetLongLoose(e, "uid");
        if (item.AuthorFollowing == null) item.AuthorFollowing = GetBoolNullable(e, "following");
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

    /// <summary>读可能缺失/为 null 的布尔字段(如 module_author.following)。</summary>
    private static bool? GetBoolNullable(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

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