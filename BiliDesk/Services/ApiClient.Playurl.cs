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
    /// 短视频的"本地合流"时长阈值(毫秒)。**只对极短片**(≤60 秒)生效。
    ///
    /// 为什么短视频要特殊处理: B 站的 DASH 把画面和声音分成两个文件, 播放器只能用
    /// LibVLC 的 `input-slave` 把音轨挂上去 —— 那是**两个各自独立的输入**(各自解复用、
    /// 各自时钟、各自 EOF)。实测(2026-09-25, 同一个 76.5 秒视频跳到 60s):
    ///   · 画面恢复要 1383ms(单个本地文件只要 470ms);
    ///   · 旧版 mp4 demux 还会漏读 fMP4 的最后一段, 结尾固定少 1911ms(没声音 + 进度条提前满格);
    ///     ★ 2026-11-05 已查明那是 libvlc mp4 demux 的 bug, 现由 Media 级
    ///     `:demux=avformat` 修掉(见 PlayerWindow.BuildMedia), 不再是合流的理由。
    ///   ⇒ 本地合流如今只剩"seek 更快更准"这一个收益, 而"下完再播"会把起播变成下载:
    ///     3 分钟 1080P 约 20MB, 慢线路要十几秒, 用户看到的就是"点开一直转圈"
    ///     (2026-10-30 用户报"视频加载慢/卡住")。
    /// ⇒ 只保留最划算的一档: ≤60 秒且 ≤8MB(见 ShortVideoCache.WorthCaching)才预下,
    ///   1~2 秒就能下完; 超过就**直接远端双流起播**, 不为"跳转更准"牺牲起播速度。
    /// </summary>
    private const long ShortVideoRemuxThresholdMs = 60_000;

    /// <summary>
    /// 动态导航接口的**增量**查询(2026-11-06 新动态红点用)。
    ///
    /// 接口仍是 feed/nav(扁平、轻量), 与综合动态列表(feed/all, 见 ApiClient.Dynamic.cs)分开:
    ///   · 可以带 `update_baseline` —— 接口会按这个基线返回 `update_num`(基线以上的新动态数),
    ///     这是 B 站自己统计的"有多少条新动态", 比本地拿 bvid 逐个比对可靠;
    ///   · 解析的是轻量的 <see cref="DynamicNavEntry"/>(id/mid/时间), 不需要视频卡片字段。
    ///
    /// 返回: 条目、下一页 offset、has_more、本次响应里的 update_baseline、update_num。
    /// </summary>
    public async Task<(bool ok, string? err, List<DynamicNavEntry>? entries,
        string? nextOffset, bool hasMore, string? updateBaseline, int updateNum)>
        GetDynamicNavAsync(string? offset = null, string? updateBaseline = null)
    {
        var entries = new List<DynamicNavEntry>();
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(offset)) ps["offset"] = offset;
            if (!string.IsNullOrEmpty(updateBaseline)) ps["update_baseline"] = updateBaseline;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/nav", ps,
                sign: true, referer: "https://t.bilibili.com/");
            if (code != 0 || data == null)
                return (false, code == -101 ? "请先登录后查看动态" : (msg ?? "动态加载失败"),
                    null, null, false, null, 0);

            var d = data.Value;
            var hasMore = d.TryGetProperty("has_more", out var hm) &&
                          (hm.ValueKind == JsonValueKind.True ||
                           (hm.ValueKind == JsonValueKind.Number && hm.GetInt32() == 1));
            var next = d.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.String
                ? off.GetString() : null;
            var baseline = d.TryGetProperty("update_baseline", out var ub) &&
                           ub.ValueKind == JsonValueKind.String
                ? ub.GetString() : null;
            var updateNum = d.TryGetProperty("update_num", out var un) &&
                            un.ValueKind == JsonValueKind.Number
                ? un.GetInt32() : 0;

            if (d.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    var entry = ParseDynamicNavEntry(e);
                    if (entry != null) entries.Add(entry);
                }
            }
            return (true, null, entries, next, hasMore, baseline, updateNum);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null, null, false, null, 0);
        }
    }

    /// <summary>把一条动态解析成红点判断用的轻量元数据。</summary>
    private static DynamicNavEntry? ParseDynamicNavEntry(JsonElement e)
    {
        try
        {
            var id = GetStr(e, "id_str");
            if (id.Length == 0)
            {
                // 旧形态偶尔把 id 放在 rid; 用数字转字符串兜底。
                var rid = GetLong(e, "rid");
                if (rid > 0) id = rid.ToString();
            }
            if (id.Length == 0) return null;

            var mid = 0L;
            if (e.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
                mid = GetLongLoose(au, "mid");
            if (mid <= 0) mid = GetLongLoose(e, "uid");
            if (mid <= 0) return null;

            var authorName = "";
            if (e.TryGetProperty("author", out var auName) && auName.ValueKind == JsonValueKind.Object)
                authorName = GetStr(auName, "name");
            if (authorName.Length == 0) authorName = GetStr(e, "uname");

            // 头像(2026-11-07): 与 author.name 同一个对象里的 author.face; 扁平形态偶尔
            // 把它放在顶层 face, 一并兜底。Normalize 统一 http→https,
            // 与 portal 解析(GetRecentUpdatedUpsAsync)里对 face 的处理保持一致。
            var authorFace = "";
            if (e.TryGetProperty("author", out var auFace) && auFace.ValueKind == JsonValueKind.Object)
                authorFace = UrlUtil.Normalize(GetStr(auFace, "face"));
            if (authorFace.Length == 0) authorFace = UrlUtil.Normalize(GetStr(e, "face"));

            var flatType = 0;
            var typeText = GetStr(e, "type");
            if (typeText.Length > 0) int.TryParse(typeText, out flatType);
            else if (e.TryGetProperty("type", out var tv) && tv.ValueKind == JsonValueKind.Number)
                flatType = tv.GetInt32();

            var bvid = flatType == 8 ? DynamicParser.ExtractBvidFromJumpUrl(GetStr(e, "jump_url")) : "";
            if (bvid.Length == 0) bvid = GetStr(e, "bvid");

            // 复用视频那边的 pub_time 解析(相对时间反推 + 原文), 返回的 Pubdate 是近似秒。
            var tmp = new VideoItem();
            ReadDynamicPubTime(e, tmp);

            return new DynamicNavEntry
            {
                Id = id,
                Mid = mid,
                AuthorName = authorName,
                AuthorFace = authorFace,
                Pubdate = tmp.Pubdate,
                Bvid = bvid,
                Type = flatType,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 动态条目的发布时间。两条解析路径的字段名不一样, 都收敛到这里, 免得各写一份漂移:
    ///   · modules 路径: module_author.pub_ts(时间戳) + pub_time(排好版的文本)
    ///   · 扁平路径:     只有 pub_time
    /// pub_time 多数是"2小时前"这种文本, 但也见过把时间戳当字符串给的形态 ——
    /// 纯数字就按时间戳用, 否则原样当展示文本(见 VideoItem.PubTimeLabel / PubText)。
    /// </summary>
    private static void ReadDynamicPubTime(JsonElement e, VideoItem item)
    {
        item.Pubdate = GetLong(e, "pub_ts");
        var t = GetStr(e, "pub_time");
        if (t.Length == 0) return;
        if (item.Pubdate <= 0)
        {
            // 纯数字 = 接口真给了时间戳(实测见过这种形态) ⇒ 是精确值
            if (long.TryParse(t, out var ts) && ts > 0) item.Pubdate = ts;
            else
            {
                // 从 "2小时前" 反推 ⇒ 近似值, 悬停提示要加"约"
                item.Pubdate = ParseDynamicTime(t);
                item.PubDateApprox = item.Pubdate > 0;
            }
        }
        // ★ 原文**一律留一份**(即使已经算出了时间戳): 卡片上直接显示的就是它
        //   (用户 2026-10-15 定: 直接显示用接口原文, 悬浮才看精确时间 —— 见 VideoItem.PubText);
        //   Pubdate 那份是给悬停提示和"按时间排序"之类用的。
        item.PubTimeLabel = t;
    }

    /// <summary>
    /// 把动态接口的 `pub_time` **文本**换算成大致的时间戳。
    ///
    /// 实测采集到的写法(2026-10-15, 6 页样本):
    ///   "刚刚" / "27分钟前" / "2小时前" / "昨天 18:54" / 更早的会是 "09-20"、"2025-09-20 14:30" 这类。
    ///   —— 接口**只给这个展示文本, 不给时间戳**。
    ///
    /// ★ 前三类是**相对文本**, 只能拿本机当前时间反推: 服务端渲染那一刻算出来的东西,
    ///   与本机时钟差几分钟属于正常范围, 不必为它做时钟校准。
    /// ★ 那为什么还要换算(而不是干脆不要 Pubdate): 卡片上**直接显示**用的确实是原文
    ///   (见 VideoItem.PubText, 用户 2026-10-15 定稿); 换算出来的 Pubdate 只供**悬停提示**
    ///   和"按时间排序"这类需要真正时间轴的地方用。没有它, 悬停就只能把原文再显示一遍。
    /// ★ 认不出来返回 0, 由调用方回落原文。
    /// </summary>
    private static long ParseDynamicTime(string s)
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

        // "昨天 18:54" / "昨天"
        if (s.StartsWith("昨天", StringComparison.Ordinal))
        {
            var rest = s.Length > 2 ? s[2..].Trim() : "";
            var d = now.Date.AddDays(-1);
            if (rest.Length > 0)
            {
                if (!DateTime.TryParseExact(rest, "HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var t2)) return 0;
                d = d.AddHours(t2.Hour).AddMinutes(t2.Minute);
            }
            return ToEpoch(d);
        }

        // 绝对日期: "09-20" / "09-20 14:30" / "2025-09-20" / "2025-09-20 14:30"
        string[] patterns = { "MM-dd HH:mm", "MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
        foreach (var p in patterns)
        {
            if (!DateTime.TryParseExact(s, p, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d)) continue;
            // 没带年份的写法按"今年"算(接口用这种写法时指的就是今年)。
            // ★ 但必须挡住**跨年**: 一月初看到接口给的 "12-31", 按今年算会得到大约
            //   一年后的时间 —— 那不是"更早的动态", 而是个未来时间(悬停提示会显示 12-31 的
            //   明年, 排序也会把它排到最前面)。比"现在"还晚就说明指的是去年。
            if (p.StartsWith("MM"))
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

    /// <summary>一条 DASH 音轨候选(普通 AAC / 杜比 / Hi-Res 无损)。</summary>
    private sealed class AudioCandidate
    {
        public int Id { get; init; }
        public long Bandwidth { get; init; }
        public string Codecs { get; init; } = "";
        public bool FromFlac { get; init; }
        public bool FromDolby { get; init; }
        public JsonElement Json { get; init; }
        public List<string> Urls { get; init; } = new();
    }

    /// <summary>音轨优先级: 无损 > 杜比 > 192K/132K/64K AAC。</summary>
    private static int AudioRank(AudioCandidate a)
    {
        if (a.FromFlac) return 400;
        if (a.FromDolby) return 300;
        return a.Id switch
        {
            30280 => 200,   // 192K
            30232 => 180,   // 132K
            30216 => 160,   // 64K
            _ => 100
        };
    }

    /// <summary>
    /// 按用户**指定档位**打分。返回值 int.MinValue = 这一档被排除(不参与比较)。
    ///
    /// 降级规则(与画质那边同一套思路):
    ///   · 精确命中指定档 → 最高分(1000 + 档位高度), 保证一定排第一;
    ///   · 比指定档**低**的可用档 → 500 + 高度, 取其中最高的那条(用户宁可低也不要没有);
    ///   · 比指定档**高**的可用档 → 100 + 高度, 只有在"低的都没有"时才会用到
    ///     (例如用户选了 132K, 而这条视频只有无损 —— 有声音总比没声音好)。
    /// ★ 为什么不直接返回 AudioRank: 那个是"音质绝对值排序", 无法表达"我要 132K, 有 192K 时
    ///   不要拿 192K 顶上" —— 用户指定档位就是要那个码率(省带宽 / 设备兼容)。
    /// </summary>
    private static int AudioRank(AudioCandidate a, AudioTier wanted)
    {
        if (wanted == AudioTier.Auto) return AudioRank(a);
        var wantLevel = AudioPreference.LevelOf((int)wanted);
        var level = AudioPreference.LevelOf(a.Id);
        // flac/dolby 节点里的 id 实测就是 30251/30250, 但兜底按来源再确认一次 ——
        // 某些视频的 dolby 节点 id 可能是别的值(接口历史上变过), 不能只信 id。
        if (a.FromFlac && level < AudioPreference.LevelOf(30251)) level = AudioPreference.LevelOf(30251);
        if (a.FromDolby && level < AudioPreference.LevelOf(30250)) level = AudioPreference.LevelOf(30250);

        if (level == wantLevel) return 1000 + level;
        if (level < wantLevel) return 500 + level;
        return 100 + level;
    }

    /// <summary>
    /// 把 dash.audio / dash.dolby.audio / dash.flac.audio 里的一段节点收进候选表。
    /// ★ flac.audio 是**对象**而不是数组(实测), dolby.audio 是数组 —— 两种都认。
    /// ★ B 站同时给 baseUrl/backupUrl 与 base_url/backup_url, 两套字段都要读。
    /// </summary>
    private static void CollectAudioCandidates(List<AudioCandidate> into, JsonElement node,
        bool fromFlac, bool fromDolby)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in node.EnumerateArray())
                CollectAudioCandidates(into, a, fromFlac, fromDolby);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;

        var urls = new List<string>();
        var baseUrl = GetStr(node, "baseUrl");
        if (baseUrl.Length == 0) baseUrl = GetStr(node, "base_url");
        if (baseUrl.Length > 0) urls.Add(baseUrl);

        var backup = node.TryGetProperty("backupUrl", out var bu) ? bu : default;
        if (backup.ValueKind != JsonValueKind.Array && node.TryGetProperty("backup_url", out var bu2))
            backup = bu2;
        if (backup.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in backup.EnumerateArray())
            {
                var s = u.GetString();
                if (!string.IsNullOrEmpty(s) && !urls.Contains(s)) urls.Add(s);
            }
        }
        if (urls.Count == 0) return;

        into.Add(new AudioCandidate
        {
            Id = GetInt(node, "id"),
            Bandwidth = GetLong(node, "bandwidth"),
            Codecs = GetStr(node, "codecs"),
            FromFlac = fromFlac,
            FromDolby = fromDolby,
            Json = node,
            Urls = urls,
        });
    }

    /// <summary>
    /// 「最近更新的关注 UP」列表(2026-11-06 新动态红点用)。
    ///
    /// 接口: GET /x/polymer/web-dynamic/v1/portal
    ///   data.up_list = [{ mid, uname, face, has_update }] —— B 站自己统计的
    ///   "这个 UP 最近有没有更新", 服务端判定, 不依赖本地基准。
    ///
    /// ★ 为什么需要它(而不是只靠 feed/nav):
    ///   feed/nav 只返回**近期**动态(通常几天内)。关注 2790 人时, 一个 UP 前天发了视频、
    ///   用户一直没点开, 它早就被挤出动态流, 红点也就永远亮不起来 —— 这正是
    ///   "经常收不到 UP 更新的红点提示"的根因之一。
    ///   portal 的 up_list 覆盖面更广, 用它给"有没有新动态"打底; feed/nav 的 id 用来算条数。
    /// ★ face 字段要留着: 左栏的未读占位行(还没被分页加载出来的 UP)只能从这里拿头像,
    ///   缺了它那一格会显示别人的头像(虚拟化回收时旧图没清掉)。
    /// </summary>
    public async Task<(bool ok, string? err, List<(long mid, string name, string face, bool hasUpdate)>? ups)>
        GetRecentUpdatedUpsAsync()
    {
        try
        {
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/portal",
                new Dictionary<string, string>(),
                referer: "https://t.bilibili.com/");
            if (code != 0 || data == null)
                return (false, code == -101 ? "请先登录" : (msg ?? "请求失败"), null);

            var ups = new List<(long, string, string, bool)>();
            if (data.Value.TryGetProperty("up_list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    var mid = GetLongLoose(e, "mid");
                    if (mid <= 0) continue;
                    var hasUpdate = e.TryGetProperty("has_update", out var hu) &&
                                    (hu.ValueKind == JsonValueKind.True ||
                                     (hu.ValueKind == JsonValueKind.Number && hu.GetInt32() == 1));
                    ups.Add((mid, GetStr(e, "uname"), UrlUtil.Normalize(GetStr(e, "face")), hasUpdate));
                }
            }
            return (true, null, ups);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>获取视频播放地址。
    /// 优先 DASH 流(最高可用清晰度), 视频/音频分离——播放器用 input-slave 合并音轨;
    /// DASH 完全不可用时回退 durl 单流。
    /// 返回 (ok, err, 主 url、备选 urls、时长、可用清晰度列表、实际返回的清晰度、
    /// 音轨 url(仅 DASH)、以及**短视频的本地合流规格**(非 null 表示播放器可以去下它并
    /// 合成本地文件, 见 ShortVideoCache))。
    ///
    /// ★ 2026-10-01: 前面补上了 `ok`/`err`。以前这个元组里**一个能说明成败的字段都没有**,
    ///   调用方只能靠 `url == null` 猜, 于是播放器在失败时只能弹一句"该清晰度暂时不可用" ——
    ///   而真正的理由(网络失败 / 大会员专属 / 该清晰度无 DASH 也无 durl)在这一层就被丢掉了。
    ///   现在与本类其它方法统一为 `(bool ok, string? err, ...)` 开头, 失败原因能直接透给用户。
    /// </summary>
    /// <param name="audioTier">
    /// 用户指定的音质档位(2026-11-06 播放器音质按钮)。
    /// null / <see cref="AudioTier.Auto"/> = 走原来的"无损 &gt; 杜比 &gt; 192K &gt; 132K &gt; 64K"排序;
    /// 指定档位时只取那一档, 该档不可用则降到**不高于它**的最高可用档
    /// (见下方选轨分支), 与画质那边的降级规则同一套思路。
    /// </param>
    public async Task<(bool ok, string? err, string? url, List<string> backups, long durationMs,
        List<(int qn, string label)> qualities, int actualQn, string? audioUrl, DashStreams? remux,
        long lastPlayMs, long lastPlayCid, List<int> audioIds, int actualAudioId)>
        GetPlayUrlAsync(string bvid, long cid, int qn = 80, AudioTier? audioTier = null)
    {
        var qualities = new List<(int qn, string label)>();
        try
        {
            await EnsureBuvidAsync();

            // ★★★ 2026-10-17 播放器手术第 1 步: durl 合流单流**按需**优先(仅 ≤720P)。
            //
            //   背景(全部实测, 探针 bd-probe-muxed / muxed2 / muxed5):
            //   DASH 双流要用 `:input-slave` 把音轨挂到视频流上, 而 libvlc 3.0.20 在
            //   **带 input-slave 的组合**下, 对 frag-MP4 的远距离 seek 会**内部失败**
            //   (cannot seek ⇒ 位置掉回开头), 这就是"拖过去又跳回去、画面卡死"的根因。
            //   durl(fnval=1) 是**音视频已合流的单个 MP4**, 不需要 input-slave; 配
            //   `:demux=avformat` 后实测来回 7 跳全部准确(偏差 0.9~2.2s)且画面持续推进。
            //
            //   ★ 代价: durl **封顶 720P**(实测带不带 ApplyTryLook 都一样, bd-probe-muxed3),
            //     而 DASH 能到 1080P ⇒ 只有用户真的要 ≤720P 时才值得走 durl。
            //
            // ★★★ 两个必须避开的坑(都实测踩过, 用户报"几乎所有视频都是 720p"):
            //   ① **"无条件先试 durl"是错的**: 默认档 1080P 时 durl 只返回 [64,16],
            //      `actualQn` 变 64, 而播放器用 actualQn 覆盖 `_currentQn`
            //      (见 FillQualityMenu) ⇒ 之后每条视频都被钉死在 720P;
            //      画质菜单也被换成 durl 的列表 ⇒ **1080P 那档直接消失**, 用户再也选不回去。
            //   ② **AutoQn(=0) 不是最低画质, 而是"自动(最高可用)"**(见 QualityPreference,
            //      内部换算成 120/4K, 封顶在能正常渲染的最高档)。所以判"是否 ≤720P"必须
            //      **先排除它** —— 直接写 `qn <= 64` 会把 0 也吃进去, 于是用户默认就落进 durl 那条路。
            var isAuto = qn == QualityPreference.AutoQn;
            if (!isAuto && qn <= 64)
            {
                var (mUrl, mBackups, mQn, mQualities, durMux, lastPlayMsMux, lastPlayCidMux) =
                    await FetchMuxedAsync(bvid, cid, qn);
                if (!string.IsNullOrEmpty(mUrl))
                {
                    if (mQualities.Count > 0) qualities = FilterPlayableQualities(mQualities);
                    return (true, null, mUrl, mBackups, durMux, qualities, mQn, null, null,
                        lastPlayMsMux, lastPlayCidMux, new List<int>(), 0);
                }
                // durl 取不到(多段切片/大会员/该档无源) → 落到下面的 DASH, 行为与手术前一致
            }

            // ---- DASH: 清晰度不受 html5 平台 720P 限制 ----
            // ★ 2026-11-06: fnval 从 16 改为 4048(全部 DASH 流), 否则拿不到
            //   dash.flac(30251 Hi-Res 无损) / dash.dolby(30250 杜比)。
            //   关闭「优先高音质音轨」时仍用 16, 保持旧行为、避免改变视频流集合 ——
            //   除非用户在播放器里明确指定了音质档位(那时必须带 4048, 否则选无损等于没选)。
            var wantHighQualityStreams = Svc.Settings.PreferHighQualityAudio ||
                (audioTier != null && AudioPreference.Normalize(audioTier ?? AudioTier.Auto) != AudioTier.Auto);
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = wantHighQualityStreams ? "4048" : "16",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            ApplyTryLook(ps);
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code == 0 && data != null &&
                data.Value.TryGetProperty("dash", out var dash) && dash.ValueKind == JsonValueKind.Object)
            {
                long dur = data.Value.TryGetProperty("timelength", out var tl) && tl.ValueKind == JsonValueKind.Number
                    ? tl.GetInt64() : 0;

                // 上次播放进度(毫秒)与上次播放的分 P cid。B 站网页版就是靠这两个字段做续播的;
                // 拿流时顺手带回, 不需要再发一次历史接口。
                long lastPlayMs = 0, lastPlayCid = 0;
                if (data.Value.TryGetProperty("last_play_time", out var lpt) &&
                    lpt.ValueKind == JsonValueKind.Number)
                    lastPlayMs = lpt.GetInt64();
                if (data.Value.TryGetProperty("last_play_cid", out var lpc) &&
                    lpc.ValueKind == JsonValueKind.Number)
                    lastPlayCid = lpc.GetInt64();

                string? videoUrl = null, audioUrl = null;
                var backups = new List<string>();
                int chosenQn = 0;
                // 可选音轨 id 列表(从高到低)与最终选中的 id —— 播放器的音质弹层用。
                var audioIds = new List<int>();
                var actualAudioId = 0;
                // 短视频做本地合流要用到这几项(码率用于估算体积, 其余写进容器)
                JsonElement? chosen = null;
                JsonElement? chosenAudio = null;

                if (dash.TryGetProperty("video", out var videos) && videos.ValueKind == JsonValueKind.Array)
                {
                    // 收集可用清晰度(按 id 去重, 从高到低)
                    var seen = new HashSet<int>();
                    foreach (var v in videos.EnumerateArray())
                    {
                        var id = GetInt(v, "id");
                        if (id > 0 && seen.Add(id))
                            qualities.Add((id, QnToLabel(id)));
                    }
                    qualities.Sort((a, b) => b.qn.CompareTo(a.qn));
                    // ★ 菜单只列**能正常渲染**的档(2026-11-14): 杜比视界/HDR/8K 在这里挡掉。
                    //   fnval=4048 是为了音轨(无损/杜比)才带上的, 它顺带把这三档视频流也
                    //   放进了 dash.video; 但播放器渲染不了它们, 列出来只会让用户点了发现
                    //   画面发灰/卡死。选流逻辑不受影响(见下面的兜底)。
                    FilterPlayableQualities(qualities);

                    // 选流: 不超过请求 qn 的最高清晰度; **同档优先 HEVC**。
                    //
                    // ★ 为什么同档优先 hevc 而不是 avc(2026-09-28 实测, 见下):
                    //   同一个视频(限免 4K 样片 BV1NGZtBwELa)同一档清晰度, 服务端同时给
                    //   avc 与 hevc 两条流, 声明码率差一倍:
                    //       id=120(4K)    avc 12.60Mbps   hevc 7.03Mbps   (hevc 省 44%)
                    //       id=112(1080P+) avc  4.17Mbps   hevc 2.63Mbps   (省 37%)
                    //       id=80 (1080P)  avc  2.67Mbps   hevc 1.34Mbps   (省 50%)
                    //   同画质(同一档 qn)下 hevc 只要一半左右的数据量。原来"同档优先 avc"等于
                    //   每次起播/跳转都要多搬 40%~100% 的字节 —— 4K 档最明显, 这正是
                    //   "缓冲特别久"的主因之一。硬解(VLC 默认 avcodec-hw=any)对 hevc 与 avc
                    //   同样支持, 所以这不是"拿兼容性换速度"。
                    //   av01(若有)排在最后: 体积更小但硬解支持面窄, 不主动选。
                    JsonElement best = default; int bestId = 0; int bestRank = -1;
                    foreach (var v in videos.EnumerateArray())
                    {
                        var id = GetInt(v, "id");
                        var codec = GetStr(v, "codecs");
                        if (id <= 0 || id > qn) continue;
                        var rank = CodecRank(codec);
                        if (bestId == 0 || id > bestId || (id == bestId && rank > bestRank))
                        {
                            best = v; bestId = id; bestRank = rank;
                        }
                    }
                    // 请求的 qn 不可用时取最高档(同档仍按上面的编码优先级挑)
                    // ★ 兜底也要守住"自动不落进杜比视界/HDR/8K"这条规则(2026-11-07):
                    //   本分支原来没有任何过滤, 直接取整个列表的最高一条 —— 视频里有 DV 流时
                    //   会挑中 126。而这条路径的触发条件恰恰是"qn 太低/该档没源", 用户此时
                    //   最不想要的就是一条渲染不了的高档流。所以先只在"常规档"里挑,
                    //   整条视频真的只有 DV/HDR/8K 时才退回原来的最高档(宁可播也别什么都不播)。
                    if (bestId == 0 && videos.GetArrayLength() > 0)
                    {
                        var foundRegular = false;
                        foreach (var v in videos.EnumerateArray())
                        {
                            var id = GetInt(v, "id");
                            if (id <= 0 || QualityPreference.IsUnsupportedTier(id)) continue;
                            var rank = CodecRank(GetStr(v, "codecs"));
                            if (!foundRegular || id > bestId || (id == bestId && rank > bestRank))
                            {
                                best = v; bestId = id; bestRank = rank;
                                foundRegular = true;
                            }
                        }
                        if (!foundRegular)
                        {
                            foreach (var v in videos.EnumerateArray())
                            {
                                var id = GetInt(v, "id");
                                var rank = CodecRank(GetStr(v, "codecs"));
                                if (id > bestId || (id == bestId && rank > bestRank))
                                {
                                    best = v; bestId = id; bestRank = rank;
                                }
                            }
                        }
                    }
                    if (bestId > 0)
                    {
                        // ★ 线路选择交给 CdnService(2026-10-03 新增, 设置页可配)。
                        //   它负责三件事: 按用户策略(自动测速/手动指定/跟随服务端)选线路、
                        //   剔除 PCDN、全部失败时回退。详细依据见 Services/CdnService.cs。
                        //
                        //   以前这里只有一条静态规则"优先非 mcdn", 问题是:
                        //     · 无法利用"各家 CDN 同一时刻吞吐差 4 倍"这件事(实测 2.5~11MB/s);
                        //     · 认不出**伪装域名** —— 实测 B 站给过
                        //       `mv0bz14m.edge.mountaintoys.cn`(query 里 os=mcdn), 域名看不出
                        //       PCDN 特征, 旧规则会把它当成正常线路用。
                        var cands = new List<string>();
                        var baseUrl = GetStr(best, "baseUrl");
                        if (baseUrl.Length > 0) cands.Add(baseUrl);
                        if (best.TryGetProperty("backupUrl", out var bu) && bu.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var u in bu.EnumerateArray())
                            {
                                var s = u.GetString();
                                if (!string.IsNullOrEmpty(s)) cands.Add(s);
                            }
                        }
                        if (cands.Count > 0)
                        {
                            var picked = await CdnService.Instance.SelectUrlAsync(cands[0], cands);
                            videoUrl = picked;
                            // 备用线路 = 其余候选(去重、且不包含已选中的那条)
                            foreach (var c in cands)
                                if (!string.Equals(c, picked, StringComparison.Ordinal) && !backups.Contains(c))
                                    backups.Add(c);
                            chosenQn = bestId;
                            chosen = best;
                        }
                    }
                }

                // ---- 选音轨(2026-11-06: 支持 Hi-Res 无损 / 杜比) ----
                // 普通 AAC 在 dash.audio; 高音质在 dash.dolby.audio / dash.flac.audio。
                // 关闭「优先高音质音轨」时只看 dash.audio, 与旧行为一致。
                // ★ 但用户在播放器里**明确指定**了音质档位时, 必须把高音质节点也收进来 ——
                //   否则"选无损"这件事永远只能拿到 192K, 按钮点了等于没点。
                var wantHighQuality = Svc.Settings.PreferHighQualityAudio ||
                                      (audioTier != null && AudioPreference.Normalize(audioTier ?? AudioTier.Auto) != AudioTier.Auto);
                var audioCandidates = new List<AudioCandidate>();
                if (dash.TryGetProperty("audio", out var audios))
                    CollectAudioCandidates(audioCandidates, audios, fromFlac: false, fromDolby: false);
                if (wantHighQuality)
                {
                    if (dash.TryGetProperty("dolby", out var dolby) &&
                        dolby.ValueKind == JsonValueKind.Object &&
                        dolby.TryGetProperty("audio", out var dolbyAudio))
                        CollectAudioCandidates(audioCandidates, dolbyAudio, fromFlac: false, fromDolby: true);
                    if (dash.TryGetProperty("flac", out var flac) &&
                        flac.ValueKind == JsonValueKind.Object &&
                        // display=false 表示服务端不给这一档(常见于无权限/未开放)
                        (!flac.TryGetProperty("display", out var flacDisplay) ||
                         flacDisplay.ValueKind != JsonValueKind.False) &&
                        flac.TryGetProperty("audio", out var flacAudio))
                        CollectAudioCandidates(audioCandidates, flacAudio, fromFlac: true, fromDolby: false);
                }

                if (audioCandidates.Count > 0)
                {
                    // 可选音轨 id 列表(从高到低, 去重) —— 播放器的音质弹层用它决定哪些档可点。
                    foreach (var a in audioCandidates)
                        if (a.Id > 0 && !audioIds.Contains(a.Id)) audioIds.Add(a.Id);
                    audioIds.Sort((a, b) => AudioPreference.LevelOf(b).CompareTo(AudioPreference.LevelOf(a)));

                    // 选轨: 用户指定档位时按"精确命中 → 不高于它的最高可用档 → 最低的更高档"降级;
                    // 自动档则按 Hi-Res > 杜比 > 192K > 132K > 64K; 同档再比带宽。
                    // 音轨虽然只有 0.1~1.5Mbps, 但 DASH 是靠 input-slave 挂进来的:
                    // 它拉不动会连累整个 input 的起播与跳转(见 DashRemuxer 里"音轨 EOF 拖垮 input")。
                    var wanted = AudioPreference.Normalize(audioTier ?? AudioTier.Auto);
                    AudioCandidate? bestAudio = null;
                    var bestRank = int.MinValue;
                    foreach (var a in audioCandidates)
                    {
                        var rank = AudioRank(a, wanted);
                        if (rank == int.MinValue) continue;      // 这一档被用户的指定档位排除了
                        if (bestAudio == null || rank > bestRank ||
                            (rank == bestRank && a.Bandwidth > bestAudio.Bandwidth))
                        {
                            bestAudio = a;
                            bestRank = rank;
                        }
                    }
                    // 指定档位一个都没命中(比如选了无损但这条视频没有无损): 退回到"最接近"的可用档,
                    // 绝不让用户落到"没有音轨"这条路上。
                    if (bestAudio == null)
                    {
                        foreach (var a in audioCandidates)
                            if (bestAudio == null || a.Bandwidth > bestAudio.Bandwidth)
                                bestAudio = a;
                    }

                    if (bestAudio != null && bestAudio.Urls.Count > 0)
                    {
                        // 音轨复用视频那次测速的结果, 不单独测速(见 SelectSecondaryUrl 的说明)
                        audioUrl = CdnService.Instance.SelectSecondaryUrl(
                            bestAudio.Urls[0], bestAudio.Urls);
                        chosenAudio = bestAudio.Json;
                        actualAudioId = bestAudio.Id;
                        System.Diagnostics.Trace.WriteLine(
                            $"[AudioPick] id={bestAudio.Id} bw={bestAudio.Bandwidth} " +
                            $"codecs={bestAudio.Codecs} flac={bestAudio.FromFlac} dolby={bestAudio.FromDolby} " +
                            $"want={wanted}");
                    }
                }

                if (!string.IsNullOrEmpty(videoUrl))
                {
                    // ---- 短视频: 顺带给出"本地合流"所需的规格(播放器后台下载并合成本地文件) ----
                    // 注意这里**不改播放地址**: 起播仍然用远端 DASH(零等待), 合流是后台在做,
                    // 就绪后播放器再切过去。见 ShortVideoRemuxThresholdMs 的说明。
                    DashStreams? remux = null;
                    if (dur > 0 && dur <= ShortVideoRemuxThresholdMs &&
                        !string.IsNullOrEmpty(audioUrl) && chosen != null && chosenAudio != null)
                    {
                        var v = chosen.Value;
                        var au = chosenAudio.Value;
                        remux = new DashStreams
                        {
                            VideoUrl = videoUrl!,
                            AudioUrl = audioUrl!,
                            DurationMs = dur,
                            VideoBandwidth = GetLong(v, "bandwidth"),
                            VideoCodecs = GetStr(v, "codecs"),
                            VideoWidth = GetInt(v, "width"),
                            VideoHeight = GetInt(v, "height"),
                            AudioBandwidth = GetLong(au, "bandwidth"),
                            AudioCodecs = GetStr(au, "codecs")
                        };
                    }
                    return (true, null, videoUrl, backups, dur, qualities, chosenQn, audioUrl, remux,
                        lastPlayMs, lastPlayCid, audioIds, actualAudioId);
                }
            }

            // ---- 2. 回退 durl 单流 ----
            // 手术前 durl 只在这条"兜底"路径上被用到; 现在它同时也是 ≤720P 的**主路**。
            // 这里仍然保留兜底: DASH 取不到(付费/大会员/该档无源)时, 单流往往还能救回来。
            var (mUrl2, mBackups2, mQn2, mQualities2, dur2, lastPlayMs2, lastPlayCid2) =
                await FetchMuxedAsync(bvid, cid, qn);
            if (mUrl2 == null)
                return (false, "DASH 与 durl 都没能取到可播放的地址(可能是大会员/付费专属, 或该清晰度无源)",
                    null, new List<string>(), 0, qualities, 0, null, null, 0, 0,
                    new List<int>(), 0);
            if (mQualities2.Count > 0) qualities = FilterPlayableQualities(mQualities2);
            return (true, null, mUrl2, mBackups2, dur2, qualities, mQn2, null, null,
                lastPlayMs2, lastPlayCid2, new List<int>(), 0);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取播放地址失败: " + ex.Message,
                null, new List<string>(), 0, qualities, 0, null, null, 0, 0,
                new List<int>(), 0);
        }
    }

    /// <summary>
    /// 请求 durl(单流)并挑出可直接播放的那一条, 返回 (url, 备用线路, 实际清晰度, 清晰度列表, 时长)。
    ///
    /// 两个必须注意的地方:
    ///   1. **不要带 `platform=html5`**。带上它现在一律返回 `-404 啥都木有`(实测),
    ///      回退分支以前就是因为这个参数常年跑不通。去掉之后 qn=80 在 pc 平台能正常拿到 durl。
    ///   2. durl 可能是**多段**(长视频被切片, 每段带 order)。播放器只会播第一条,
    ///      所以这里要求"必须只有一段"才算可用 —— 宁可让上面回退到 DASH, 也不要给用户
    ///      一条"播到一半就没了"的地址。
    /// </summary>
    private async Task<(string? url, List<string> backups, int qn,
        List<(int qn, string label)> qualities, long durationMs,
        long lastPlayMs, long lastPlayCid)>
        FetchMuxedAsync(string bvid, long cid, int qn)
    {
        var qualities = new List<(int qn, string label)>();
        var backups = new List<string>();
        try
        {
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "1",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null) return (null, backups, 0, qualities, 0, 0, 0);

            long dur = data.Value.TryGetProperty("timelength", out var tl) && tl.ValueKind == JsonValueKind.Number
                ? tl.GetInt64() : 0;
            long lastPlayMs = 0, lastPlayCid = 0;
            if (data.Value.TryGetProperty("last_play_time", out var lpt) &&
                lpt.ValueKind == JsonValueKind.Number)
                lastPlayMs = lpt.GetInt64();
            if (data.Value.TryGetProperty("last_play_cid", out var lpc) &&
                lpc.ValueKind == JsonValueKind.Number)
                lastPlayCid = lpc.GetInt64();

            var segs = new List<string>();
            if (data.Value.TryGetProperty("durl", out var durl) && durl.ValueKind == JsonValueKind.Array)
            {
                foreach (var seg in durl.EnumerateArray())
                {
                    var u = GetStr(seg, "url");
                    if (!string.IsNullOrEmpty(u)) segs.Add(u);
                }
            }
            // 多段没法播(播放器不会自动续下一段), 当作拿不到
            if (segs.Count != 1) return (null, backups, 0, qualities, dur, lastPlayMs, lastPlayCid);

            var actualQn = GetInt(data.Value, "quality");
            if (data.Value.TryGetProperty("accept_quality", out var aq) && aq.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in aq.EnumerateArray())
                    if (q.ValueKind == JsonValueKind.Number)
                        qualities.Add((q.GetInt32(), QnToLabel(q.GetInt32())));
            }
            if (qualities.Count == 0 && actualQn > 0)
                qualities.Add((actualQn, QnToLabel(actualQn)));
            qualities.Sort((a, b) => b.qn.CompareTo(a.qn));

            return (segs[0], backups, actualQn, qualities, dur, lastPlayMs, lastPlayCid);
        }
        catch
        {
            // 解析失败按"没有可用地址"处理, 交给调用方走降级(报错/回落 durl) ——
            // 这里刻意不 ReportError: 拿到了 200 但结构不认识属于内容问题, 不该弹窗打扰用户。
            return (null, backups, 0, qualities, 0, 0, 0);
        }
    }

    /// <summary>
    /// 把一份清晰度列表里**播放器暂时渲染不了**的档位剔掉(杜比视界/HDR/8K), 原地修改并返回它。
    ///
    /// ★ 为什么在取流层过滤而不是在 FillQualityMenu 里(2026-11-14):
    ///   qualities 这份列表同时喂给"菜单可选项"和"按钮上显示的档位名", 只在 UI 层滤一遍
    ///   会出现"菜单里没有、按钮上还写着杜比视界"的割裂。放在这里, 播放器的三条取流路径
    ///   (DASH / ≤720P 主路 durl / DASH 失败兜底 durl)一次覆盖, 切档后重新取流也自动生效。
    /// ★ 判据与"自动选流要跳过的档"同源, 见 <see cref="QualityPreference.IsUnsupportedTier"/>。
    /// ★ 只动**可选范围**, 不动选流: 整条视频只有这些流时仍按原兜底挑一条播(宁可播也别黑屏)。
    /// ★ 下载那条路(ApiClient.Download)刻意不走这里 —— 下载是存文件, 不经过播放器渲染,
    ///   8K/HDR/杜比视界照样可以下。
    /// </summary>
    private static List<(int qn, string label)> FilterPlayableQualities(
        List<(int qn, string label)> qualities)
    {
        qualities.RemoveAll(x => QualityPreference.IsUnsupportedTier(x.qn));
        return qualities;
    }

    /// <summary>
    /// 未登录时补上"免登录 1080P"参数(仿 PiliPlus 的做法)。
    ///
    /// ★★ 这里有一个**只加 try_look 会完全无效**的坑, 实测结论(2026-10-03, 见 .probes/bd-probe-trylook):
    ///
    ///   必要性矩阵(未登录, 同一视频, 每格重复 2 轮结果一致):
    ///     都不加                       -> dash.video id = [16,32]           只有 360P/480P
    ///     **只加 try_look=1**          -> dash.video id = [16,32]           依然只有 360P/480P  ← 无效!
    ///     **只加 dm_img_* 四个**        -> dash.video id = [16,32]           依然只有 360P/480P  ← 无效!
    ///     try_look=1 + dm_img_* 四个   -> dash.video id = [16,32,64,80]     ★ 720P + 1080P 解锁
    ///
    ///   也就是说这是**两要素同时成立**才放行: try_look 触发"试看"通道, 四个 dm_img_*
    ///   是被校验的设备指纹。少任何一个都退回 360P/480P —— 而且**四个一个都不能少**
    ///   (单独抽掉 dm_img_inter 就会立刻退回 [16,32])。
    ///
    ///   为什么别的项目里"只发 try_look"也能看到 1080P 的说法靠不住:
    ///   PiliPlus 是 2026-05-08 才补上 dm_img_* 的(此前只发 try_look), 那次补丁正是为了修
    ///   它自己 issue #2020「免登录1080p看不了」。照抄它的旧版参数就会复现那个 bug。
    ///
    /// 顺带实测确认**不需要**的东西(避免以后有人"顺手加上去"):
    ///   · 不需要 Cookie(完全无 session 也能解锁)
    ///   · 不需要改 UA / Referer / Origin / env / app-key / x-bili-aurora-zone
    ///   · 不需要把 fnval 从 16 改成 4048, 也不需要动 fourk / fnver
    ///   · qn 不决定返回哪些流(qn=16 照样返回 [16,32,64,80]), 它只决定哪一条带可播放地址
    ///
    /// 能力上限: 只到 1080P(id=80)。1080P+(112) / 60帧(116) / 4K(120) / HDR 匿名拿不到,
    /// 那是账号等级限制, 不是参数问题。番剧(PGC)也不适用 —— 那条接口匿名返回空 dash。
    ///
    /// ⚠️ data.quality 在 qn=80 时会返回 64(看起来像只给了 720P), 但 dash.video 里
    ///    **确实有** id=80 的 1920x1080 流。判断清晰度必须看 dash.video[].id, 不能看它。
    ///
    /// ⚠️ 这是一套设备指纹启发式校验, B 站随时可能改判据(历史上已失效过两次)。
    ///    所以失败要能静默回退 —— 这里只是"多加几个参数", 最坏情况就是退回现在的 360P/480P,
    ///    不会让播放本身出错。
    /// </summary>
    private static void ApplyTryLook(Dictionary<string, string> ps)
    {
        // 只在"未登录 + 用户开着该开关"时发。
        // 已登录时 B 站按账号等级给流, 发这个参数没有意义(还可能干扰), 所以直接不发。
        if (SessionManager.Instance.HasLogin) return;
        if (!SettingsStore.Instance.NoLogin1080P) return;

        ps["try_look"] = "1";
        // 四个设备指纹参数必须**成组**出现, 缺一不可(见上方必要性矩阵)。
        // ★★ 2026-10-17 修「退出登录后无法播放」: 必须是**进程内固定的一组**, 不能每次随机。
        //   上一版用 RandomBase64 每次请求都生成新值 —— 这正是"长驻进程才出问题、
        //   探针复现不了"的原因: 探针只发一两次请求, 而产品每看一个视频、每切一次清晰度
        //   都打一次 playurl ⇒ **同一个 buvid 却每次带着一组全新的设备指纹**,
        //   服务端眼里就是"一台设备在不断更换自己的硬件特征" ⇒ 判为爬虫 ⇒ -412。
        //   (重新登录后正常, 因为 SESSDATA 给设备特征不一致背了书。)
        //   取值本身几乎不被校验(形状像指纹即可), 所以固定下来的成本为零。
        ps["dm_img_list"] = "[]";
        ps["dm_img_str"] = StableDeviceFingerprint(16, 64);
        ps["dm_cover_img_str"] = StableDeviceFingerprint(32, 128);
        ps["dm_img_inter"] = "{\"ds\":[],\"wh\":[0,0,0],\"of\":[0,0,0]}";
    }

    /// <summary>dm_img_str 的进程内缓存(一台设备一套, 见 StableDeviceFingerprint)</summary>
    private static string? _imgStrCache;

    /// <summary>dm_cover_img_str 的进程内缓存(与上面那份相互独立)</summary>
    private static string? _coverImgStrCache;

    /// <summary>
    /// 进程内固定的"设备指纹"随机串: 同一字段每次调用返回**同一个值**。
    ///
    /// ★ 为什么不能每次都随机(2026-10-17 实测教训, 见 ApplyTryLook):
    ///   同一 buvid 每次请求都换一组硬件特征, 在服务端看来就是"设备特征不稳定的爬虫",
    ///   于是稳定 -412。而**探针复现不了**这个 bug —— 它只发一两次请求,
    ///   只有长驻进程连续取流才会暴露。这与 MEMORY 里
    ///   "探针的 ApiClient 是全新实例, 用户跑的是长驻进程"那条教训是同一个模式。
    /// </summary>
    private static string StableDeviceFingerprint(int minLen, int maxLen)
    {
        // 用 maxLen 区分两个字段, 各缓存一份(彼此独立, 但各自稳定)
        if (maxLen <= 64)
            return _imgStrCache ??= RandomBase64(minLen, maxLen);
        return _coverImgStrCache ??= RandomBase64(minLen, maxLen);
    }

    /// <summary>生成指定长度范围内的随机 base64 串(用于 dm_img_str / dm_cover_img_str)</summary>
    private static string RandomBase64(int minLen, int maxLen)
    {
        var n = Random.Shared.Next(minLen, maxLen + 1);
        var bytes = new byte[n];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// 取一组"带签名的真实媒体 URL", 专门给 CDN 测速当样本用(设置页「重新测速」)。
    ///
    /// 为什么单独一个方法、而不是复用 GetPlayUrlAsync:
    ///   GetPlayUrlAsync 返回的是**已经按用户策略选好线路**的那一条, 拿它测速等于
    ///   "只测了当前用的这条", 测不出别的线路更快。测速需要的是**原始候选集**
    ///   (baseUrl + 所有 backupUrl), 所以这里返回全部候选。
    ///
    /// 返回空列表 = 没有可用样本(取流失败/付费视频), 调用方据此提示用户。
    /// </summary>
    public async Task<List<string>> GetCdnSampleUrlsAsync(string bvid, long cid, int qn = 80)
    {
        var urls = new List<string>();
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "16",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            ApplyTryLook(ps);
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null) return urls;

            if (!data.Value.TryGetProperty("dash", out var dash) ||
                dash.ValueKind != JsonValueKind.Object ||
                !dash.TryGetProperty("video", out var videos) ||
                videos.ValueKind != JsonValueKind.Array ||
                videos.GetArrayLength() == 0)
                return urls;

            // 用最高那段视频当样本(它最吃带宽, 测出来的差距最有代表性)
            JsonElement best = default;
            var bestId = 0;
            foreach (var v in videos.EnumerateArray())
            {
                var id = GetInt(v, "id");
                if (id > bestId) { bestId = id; best = v; }
            }
            if (bestId <= 0) return urls;

            var baseUrl = GetStr(best, "baseUrl");
            if (baseUrl.Length > 0) urls.Add(baseUrl);
            if (best.TryGetProperty("backupUrl", out var bu) && bu.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in bu.EnumerateArray())
                {
                    var s = u.GetString();
                    if (!string.IsNullOrEmpty(s) && !urls.Contains(s)) urls.Add(s);
                }
            }
        }
        catch (Exception ex)
        {
            // 测速样本拿不到不该弹错(那是设置页的一个辅助功能), 但要在日志里留痕
            App.ReportError(ex);
        }
        return urls;
    }

    /// <summary>清晰度 id 转显示名</summary>
    public static string QnToLabel(int qn) => qn switch
    {
        127 => "8K",
        126 => "杜比视界",
        125 => "HDR",
        120 => "4K",
        116 => "1080P60",
        112 => "1080P+",
        100 => "智能修复",
        80 => "1080P",
        74 => "720P60",
        64 => "720P",
        32 => "480P",
        16 => "360P",
        _ => qn + "P"
    };

}
