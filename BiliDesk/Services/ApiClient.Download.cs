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
    // ---------------------------------------------------------------- 视频下载

    /// <summary>
    /// 取"可直接保存为单个 MP4 文件"的播放地址(durl 单流)。
    ///
    /// 为什么下载不用播放那套 DASH 地址:
    /// DASH 是音视频**分离**的两条流, 直接存下来会是"有画面没声音"的残缺文件,
    /// 要合并必须依赖 ffmpeg 做 remux —— 本项目不带 ffmpeg, 引入它会让安装包暴涨几十兆。
    /// durl 单流本身就是一个完整 MP4(音视频已封装在一起), 保存下来就能直接播,
    /// 代价是清晰度上限 720P。对"下载到本地看"这个场景, 能播 >> 高清晰度。
    /// </summary>
    public async Task<(bool ok, string? err, string? url, string? audioUrl)> GetDownloadUrlAsync(
        string bvid, long cid, int qn = 64)
    {
        try
        {
            if (string.IsNullOrEmpty(bvid) || cid <= 0) return (false, "参数不完整", null, null);
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "1",      // 1 = durl 单流(音视频合一的 MP4)
                ["fnver"] = "0",
                ["fourk"] = "1",
                // ★ 不要带 platform=html5。FetchMuxedAsync 的实测结论(同接口、同 fnval=1):
                //   带上它一律返回 -404"啥都木有"。这里以前偏偏带了, 于是下载走的是一条
                //   已知跑不通的参数组合 —— 表现是"该视频没有可下载的单流地址"或 code -404,
                //   很容易被误判成"付费/大会员专属"。改接口参数前先看上面 FetchMuxedAsync 的说明。
                ["high_quality"] = "1"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取下载地址失败 (code {code})", null, null);

            if (data.Value.TryGetProperty("durl", out var durl) &&
                durl.ValueKind == JsonValueKind.Array && durl.GetArrayLength() > 0)
            {
                var url = GetStr(durl[0], "url");
                if (!string.IsNullOrEmpty(url)) return (true, null, url, null);
            }
            return (false, "该视频没有可下载的单流地址(可能是付费/大会员专属)", null, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, null);
        }
    }

    /// <summary>
    /// 读取**下载**可用的清晰度列表(下载选择窗的数据源)。
    ///
    /// 为什么不再用 <see cref="GetDownloadUrlAsync"/> 那一套:
    /// 那条路走 durl 单流, 音视频已封装在一起、下载完就能播, 但**清晰度封顶 720P**。
    /// 既然工程里已经有 <see cref="DashRemuxer"/>(短视频缓存用, 纯拷贝合流), 下载也可以
    /// 走 DASH 双流 → 本地合成 MP4, 于是 1080P / 4K / 8K 都能下。
    ///
    /// 与播放取流的差别:
    ///   · **不按用户设置降级** —— 菜单要列出接口这次给的全部档位, 让用户自己挑;
    ///   · 每一档都预先选好线路(CdnService), 下载时直接按候选顺序重试;
    ///   · 音轨只选一份(下载是"拿走成品", 不该因为当时播放用 64K 就只下 64K 的声音)。
    ///
    /// 返回的 <see cref="DownloadOptions.UseMuxed"/> 为 true 表示没有 DASH 可用,
    /// 已回退到 durl 单流(上限 720P, 只有一档)。
    /// </summary>
    public async Task<(bool ok, string? err, DownloadOptions? data)> GetDownloadOptionsAsync(
        string bvid, long cid, string? title = null)
    {
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return (false, "参数不完整", null);
        try
        {
            await EnsureBuvidAsync();

            // ★ fnval 固定 4048: 下载要尽可能拿到全部档位(含 4K/8K/HDR/杜比视界),
            //   与"优先高音质音轨"设置无关 —— 那是播放侧为了兼容性做的取舍, 下载侧不该继承。
            //   (GetPlayUrlAsync 里用 16 是为了避免改变视频流集合, 见那里的说明。)
            // ★ qn 用 127(8K) 而不是 QualityPreference.HighestQn(120): 下载是用户**显式挑档**,
            //   不该像"自动"那样把 8K 挡在外面。实测同一视频 120 与 127 返回的 dash.video
            //   完全一致(这个上限只影响服务端给不给 8K 那条, 不会额外塞进 DV/HDR)。
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = "127",
                ["fnval"] = "4048",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            ApplyTryLook(ps);

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);

            if (code != 0 || data == null)
                return (false, msg ?? $"获取下载地址失败 (code {code})", null);

            var root = data.Value;
            var durationMs = root.TryGetProperty("timelength", out var tl) &&
                             tl.ValueKind == JsonValueKind.Number
                ? tl.GetInt64() : 0;

            // ---- 音轨: 与播放同一套候选收集, 但固定取"最高可用" ----
            var audioCandidates = new List<AudioCandidate>();
            if (root.TryGetProperty("dash", out var dash) && dash.ValueKind == JsonValueKind.Object)
            {
                if (dash.TryGetProperty("audio", out var audios))
                    CollectAudioCandidates(audioCandidates, audios, fromFlac: false, fromDolby: false);
                if (dash.TryGetProperty("dolby", out var dolby) &&
                    dolby.ValueKind == JsonValueKind.Object &&
                    dolby.TryGetProperty("audio", out var dolbyAudio))
                    CollectAudioCandidates(audioCandidates, dolbyAudio, fromFlac: false, fromDolby: true);
                if (dash.TryGetProperty("flac", out var flac) &&
                    flac.ValueKind == JsonValueKind.Object &&
                    (!flac.TryGetProperty("display", out var flacDisplay) ||
                     flacDisplay.ValueKind != JsonValueKind.False) &&
                    flac.TryGetProperty("audio", out var flacAudio))
                    CollectAudioCandidates(audioCandidates, flacAudio, fromFlac: true, fromDolby: false);
            }

            AudioCandidate? bestAudio = null;
            foreach (var a in audioCandidates)
                if (bestAudio == null || AudioRank(a) > AudioRank(bestAudio) ||
                    (AudioRank(a) == AudioRank(bestAudio) && a.Bandwidth > bestAudio.Bandwidth))
                    bestAudio = a;

            var audioUrls = new List<string>();
            var audioLabel = "";
            if (bestAudio != null && bestAudio.Urls.Count > 0)
            {
                var picked = CdnService.Instance.SelectSecondaryUrl(bestAudio.Urls[0], bestAudio.Urls);
                audioUrls.Add(picked);
                foreach (var u in bestAudio.Urls)
                    if (!string.Equals(u, picked, StringComparison.Ordinal) && !audioUrls.Contains(u))
                        audioUrls.Add(u);
                audioLabel = AudioPreference.ShortLabelOfId(bestAudio.Id);
            }

            var audioBandwidth = bestAudio?.Bandwidth ?? 0;

            // ---- 视频: 按清晰度分组, 每组内优先 HEVC ----
            var qualities = new List<DownloadQualityOption>();
            if (dash.ValueKind == JsonValueKind.Object &&
                dash.TryGetProperty("video", out var videos) &&
                videos.ValueKind == JsonValueKind.Array)
            {
                // 同一档可能出现多条(不同编码): 保留"编码优先级最高"的那条
                var best = new Dictionary<int, JsonElement>();
                foreach (var v in videos.EnumerateArray())
                {
                    if (v.ValueKind != JsonValueKind.Object) continue;
                    var id = GetInt(v, "id");
                    if (id <= 0) continue;
                    if (!best.TryGetValue(id, out var cur) ||
                        CodecRank(GetStr(v, "codecs")) > CodecRank(GetStr(cur, "codecs")))
                        best[id] = v;
                }

                var ordered = new List<int>(best.Keys);
                ordered.Sort((a, b) => b.CompareTo(a));   // 从高到低

                foreach (var qn in ordered)
                {
                    var v = best[qn];
                    var cands = new List<string>();
                    var baseUrl = GetStr(v, "baseUrl");
                    if (baseUrl.Length == 0) baseUrl = GetStr(v, "base_url");
                    if (baseUrl.Length > 0) cands.Add(baseUrl);
                    var backup = v.TryGetProperty("backupUrl", out var bu) ? bu : default;
                    if (backup.ValueKind != JsonValueKind.Array &&
                        v.TryGetProperty("backup_url", out var bu2)) backup = bu2;
                    if (backup.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var u in backup.EnumerateArray())
                        {
                            var s = u.GetString();
                            if (!string.IsNullOrEmpty(s) && !cands.Contains(s)) cands.Add(s);
                        }
                    }
                    if (cands.Count == 0) continue;

                    var picked = await CdnService.Instance.SelectUrlAsync(cands[0], cands);
                    var urls = new List<string> { picked };
                    foreach (var c in cands)
                        if (!string.Equals(c, picked, StringComparison.Ordinal) && !urls.Contains(c))
                            urls.Add(c);

                    var bw = GetLong(v, "bandwidth");
                    var videoBytes = durationMs > 0 && bw > 0
                        ? (long)(bw / 8.0 * (durationMs / 1000.0))
                        : 0;
                    var audioBytes = durationMs > 0 && audioBandwidth > 0
                        ? (long)(audioBandwidth / 8.0 * (durationMs / 1000.0))
                        : 0;

                    qualities.Add(new DownloadQualityOption
                    {
                        Qn = qn,
                        Label = QnToLabel(qn),
                        CodecLabel = CodecLabelOf(GetStr(v, "codecs")),
                        Bandwidth = bw,
                        EstimatedBytes = videoBytes + audioBytes,
                        EstimatedVideoBytes = videoBytes,
                        EstimatedAudioBytes = audioBytes,
                        VideoUrls = urls
                    });
                }
            }

            if (qualities.Count > 0 && audioUrls.Count > 0)
            {
                return (true, null, new DownloadOptions
                {
                    Bvid = bvid,
                    Cid = cid,
                    Title = title ?? "",
                    DurationMs = durationMs,
                    Qualities = qualities,
                    AudioUrls = audioUrls,
                    AudioLabel = audioLabel,
                    UseMuxed = false
                });
            }

            // ---- 回退: durl 单流(音视频已合并, 上限 720P) ----
            // ★ 这条路径不传 qn 上限, 让服务端给它能给的最高单流档(常见 720P)。
            var (mUrl, mBackups, mQn, mQualities, dur, _, _) =
                await FetchMuxedAsync(bvid, cid, QualityPreference.HighestQn);
            if (string.IsNullOrEmpty(mUrl))
                return (false, "这个视频没有可用的下载地址(可能是付费/大会员专属)", null);

            var muxedUrls = new List<string> { mUrl };
            foreach (var b in mBackups)
                if (!string.Equals(b, mUrl, StringComparison.Ordinal) && !muxedUrls.Contains(b))
                    muxedUrls.Add(b);

            var muxedQn = mQn > 0 ? mQn : (mQualities.Count > 0 ? mQualities[0].qn : 64);
            var muxedQuality = new DownloadQualityOption
            {
                Qn = muxedQn,
                Label = QnToLabel(muxedQn),
                CodecLabel = "单流",
                Bandwidth = 0,
                EstimatedBytes = 0,
                VideoUrls = muxedUrls
            };

            return (true, null, new DownloadOptions
            {
                Bvid = bvid,
                Cid = cid,
                Title = title ?? "",
                DurationMs = dur > 0 ? dur : durationMs,
                Qualities = new List<DownloadQualityOption> { muxedQuality },
                UseMuxed = true,
                MuxedUrls = muxedUrls
            });
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取下载选项失败: " + ex.Message, null);
        }
    }

    /// <summary>编码串 → 给用户看的短名(HEVC / AVC / AV1)。未知编码原样截短显示。</summary>
    private static string CodecLabelOf(string codecs)
    {
        if (string.IsNullOrEmpty(codecs)) return "";
        if (codecs.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
            codecs.StartsWith("hvc", StringComparison.OrdinalIgnoreCase)) return "HEVC";
        if (codecs.StartsWith("avc", StringComparison.OrdinalIgnoreCase)) return "AVC";
        if (codecs.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return "AV1";
        var comma = codecs.IndexOf(',');
        return comma > 0 ? codecs[..comma] : codecs;
    }

    // ---------------------------------------------------------------- 观看进度上报

    /// <summary>
    /// 上报观看进度到 B 站云端历史。
    ///
    /// 接口: /x/v2/history/report (POST 表单, 需 csrf)
    /// 参数: aid / cid / progress(秒) / type=3(投稿视频)
    ///
    /// 调用时机由播放器控制: 起播后节流上报 + 暂停/关闭时补报一次,
    /// 这样"用 BiliDesk 看过的视频"会出现在 App 与网页端的观看记录里。
    /// 未登录直接返回 ok=false, 不产生任何网络请求。
    ///
    /// ★ 2026-10-01 由 <c>Task&lt;bool&gt;</c> 改为 (ok, err): 上报是 fire-and-forget,
    ///   失败**绝不能影响播放**, 但原因仍要带出来 —— 以前"没登录"和"网络失败"都是同一个
    ///   false, 排查"我的视频没进观看记录"时无从下手。
    /// </summary>
    public async Task<(bool ok, string? err)> ReportHistoryAsync(long aid, long cid, long progressSeconds)
    {
        if (!SessionManager.Instance.HasLogin) return (false, "未登录");
        if (aid <= 0 || cid <= 0) return (false, "参数不完整");

        try
        {
            await EnsureBuvidAsync();
            var (ok, err) = await PostFormAsync(
                "https://api.bilibili.com/x/v2/history/report",
                new Dictionary<string, string>
                {
                    ["aid"] = aid.ToString(),
                    ["cid"] = cid.ToString(),
                    ["progress"] = Math.Max(0, progressSeconds).ToString(),
                    ["type"] = "3",
                    ["csrf"] = SessionManager.Instance.Current.BiliJct ?? ""
                },
                referer: "https://www.bilibili.com/");
            return (ok, err);
        }
        catch (Exception ex)
        {
            // 上报失败绝不能影响播放, 但记一笔便于排查
            App.ReportError(ex);
            return (false, "上报失败: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 番剧 / 追番(PGC)
    //
    // ★ 这三个接口与项目里其余接口有一处关键差别: **必须是 www.bilibili.com 的 Referer**。
    //   PGC 是网页版播放器的后端, 对来源校验比 UGC 那套严; Referer 给成别的域会被拒。
    //   (GetJsonAsync 的默认 Referer 正好就是 www.bilibili.com, 但这里显式写出来 ——
    //    免得以后有人改全局默认值时顺手把这一处带崩。)

    /// <summary>
    /// 某个用户关注的番剧("我的追番")。
    ///
    /// 接口: /x/space/bangumi/follow/list —— type: 1 追番 / 2 追剧(电视剧、纪录片)。
    ///
    /// ★ 为什么不是网上常见的 /x/v2/follow/seasons 或 /pgc/season/follow/list:
    ///   那两条 2026-10-11 实测**已经 404**(带登录 Cookie 也一样), 这个才是活的。
    ///   候选是逐个探测出来的, 别再改回去。
    ///
    /// ★ 需要登录: 匿名访问私有设置的用户会拿到 53013。调用方应先判 <c>SessionManager.HasLogin</c>。
    /// </summary>
    public async Task<(bool ok, string? err, List<BangumiItem>? items)>
        GetMyBangumiAsync(long vmid, int pn = 1, int ps = 30)
    {
        if (vmid <= 0) return (false, "用户 ID 无效", null);
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/bangumi/follow/list",
                new Dictionary<string, string>
                {
                    ["vmid"] = vmid.ToString(),
                    ["pn"] = Math.Max(1, pn).ToString(),
                    ["ps"] = Math.Clamp(ps, 1, 100).ToString(),
                    ["type"] = "1" // 只要番剧; 追剧(影视)不在追番页里
                },
                referer: "https://www.bilibili.com/");

            if (code != 0 || data == null) return (false, msg ?? $"加载追番失败({code})", null);
            if (!data.Value.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return (true, null, new List<BangumiItem>());

            var items = new List<BangumiItem>();
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var item = ParseBangumiItem(el);
                if (item != null) items.Add(item);
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "加载追番失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 番剧推荐 / 国创推荐(首页追番页那两栏)。
    ///
    /// 接口: /pgc/season/index/result —— 返回热门番剧列表, 不需要登录。
    ///
    /// ★★ 接口的 <c>season_type</c> 参数**实测不生效**: season_type=1 与 =4 返回的是同一批
    ///   (2026-10-11 实测两边 list 完全一致)。真正的分类在**返回项自己的 season_type 字段**里。
    ///   所以这里只请求一次, 由调用方按 <see cref="BangumiItem.SeasonType"/> 分栏 ——
    ///   分两次请求不但没效果, 还会让两栏内容重复。
    /// </summary>
    public async Task<(bool ok, string? err, List<BangumiItem>? items, bool hasNext)>
        GetBangumiRecommendAsync(int pn = 1, int ps = 25)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/pgc/season/index/result",
                new Dictionary<string, string>
                {
                    ["season_type"] = "1",
                    ["type"] = "0", // 0 = 综合热门(按 type 细分的那套分类不要)
                    ["page"] = Math.Max(1, pn).ToString(),
                    ["ps"] = Math.Clamp(ps, 1, 50).ToString()
                },
                referer: "https://www.bilibili.com/");

            if (code != 0 || data == null) return (false, msg ?? $"加载番剧推荐失败({code})", null, false);
            if (!data.Value.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return (true, null, new List<BangumiItem>(), false);

            var items = new List<BangumiItem>();
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var item = ParseBangumiItem(el);
                if (item != null) items.Add(item);
            }

            // has_next 缺省时按"这一页是不是读满了"兜底 —— 两个信号都拿不到就只能认为到底了,
            // 那只会少翻一页, 比无限翻下去安全。
            var hasNext = data.Value.TryGetProperty("has_next", out var hn)
                && hn.ValueKind == JsonValueKind.Number
                ? hn.GetInt32() == 1
                : items.Count >= Math.Clamp(ps, 1, 50);

            return (true, null, items, hasNext);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "加载番剧推荐失败: " + ex.Message, null, false);
        }
    }

    /// <summary>
    /// 番剧详情 + **选集列表**。
    ///
    /// 接口: /pgc/view/web/season —— 不需要登录(能拿到番剧的基本信息与分集表)。
    ///
    /// ★ 选集取的是**顶层 episodes**, 不是 section[].episodes:
    ///   顶层是"正片"列表, 顺序就是官方播放顺序; 而 section[] 里混着预告 / 特别花絮 / UP 杂谈,
    ///   凡人修仙传实测顶层 307 集、section 各项加起来却是 1130 集 ——
    ///   把它们串起来当"选集"会让用户从第 307 话一路翻到花絮区去。
    /// </summary>
    public async Task<(bool ok, string? err, BangumiDetail? data)>
        GetBangumiDetailAsync(long seasonId)
    {
        if (seasonId <= 0) return (false, "番剧 ID 无效", null);
        try
        {
            // ★ payloadKey: "result" —— 这个接口把内容放在根级 result 下, 没有 data。
            //   照常取 data 会拿到 null, 而 code 是 0、message 是 success(接口完全正常)。
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/pgc/view/web/season",
                new Dictionary<string, string> { ["season_id"] = seasonId.ToString() },
                referer: "https://www.bilibili.com/",
                payloadKey: "result");

            if (code != 0 || data == null) return (false, msg ?? $"加载番剧详情失败({code})", null);

            var res = data.Value;

            var detail = new BangumiDetail
            {
                SeasonId = seasonId,
                Title = GetStr(res, "title").StripHtml(),
                Cover = UrlUtil.Normalize(GetStr(res, "cover")),
                Subtitle = GetStr(res, "subtitle").StripHtml(),
                Evaluate = GetStr(res, "evaluate").StripHtml(),
                Renewal = GetStr(res, "renewal_time")
            };

            // 分类名在 show_season_type / type 上, 优先取能拿到名字的那个
            detail.SeasonTypeName = GetStr(res, "season_type_name").StripHtml();
            if (string.IsNullOrEmpty(detail.SeasonTypeName))
            {
                var st = TryObject(res, "show_season_type");
                detail.SeasonTypeName = st is { } s ? GetStr(s, "name").StripHtml() : "";
            }

            if (TryObject(res, "rating") is { } rating)
            {
                detail.Score = GetStr(rating, "score");
                detail.ScoreCount = GetLong(rating, "count");
            }
            if (TryObject(res, "stat") is { } stat)
                detail.FollowCount = GetLong(stat, "follow");

            if (res.TryGetProperty("areas", out var areas) && areas.ValueKind == JsonValueKind.Array)
            {
                var names = areas.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.Object)
                    .Select(a => GetStr(a, "name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
                detail.Area = string.Join(" / ", names);
            }

            if (res.TryGetProperty("styles", out var styles) && styles.ValueKind == JsonValueKind.Array)
            {
                var names = styles.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.Object)
                    .Select(a => GetStr(a, "name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
                detail.Styles = string.Join(" / ", names);
            }

            if (TryObject(res, "publish") is { } pub)
                detail.PublishDate = GetStr(pub, "pub_time_show");

            if (res.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in eps.EnumerateArray())
                {
                    if (ep.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParseBangumiEpisode(ep);
                    // 没有 bvid 的条目播不了(预告条目常见), 直接丢掉而不是留一个点不动的行
                    if (parsed == null || string.IsNullOrEmpty(parsed.Bvid)) continue;
                    detail.Episodes.Add(parsed);
                }
            }

            return (true, null, detail);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "加载番剧详情失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 解析一个番剧条目。
    ///
    /// 两个接口(追番 / 推荐)字段基本同名但**都不完整**, 各缺一块, 所以逐个兜底:
    ///   追番接口有 progress / subtitle / season_type_name, 但没有 score、index_show 藏在 new_ep;
    ///   推荐接口有 score / index_show, 但没有 progress。
    /// </summary>
    private static BangumiItem? ParseBangumiItem(JsonElement el)
    {
        var seasonId = GetLong(el, "season_id");
        if (seasonId <= 0) return null;

        var item = new BangumiItem
        {
            SeasonId = seasonId,
            MediaId = GetLong(el, "media_id"),
            Title = GetStr(el, "title").StripHtml(),
            Cover = UrlUtil.Normalize(GetStr(el, "cover")),
            Subtitle = GetStr(el, "subtitle").StripHtml(),
            SeasonType = GetInt(el, "season_type"),
            SeasonTypeName = GetStr(el, "season_type_name").StripHtml(),
            Badge = GetStr(el, "badge").StripHtml(),
            IsFinish = GetInt(el, "is_finish") == 1,
            Progress = GetStr(el, "progress").StripHtml(),
            Url = GetStr(el, "url")
        };

        // total_count 缺失时退回 new_ep 的集号(番外/剧场版给的是 0)
        item.TotalCount = GetInt(el, "total_count");
        if (item.TotalCount <= 0) item.TotalCount = GetInt(el, "formal_ep_count");
        // ★ 搜索接口(media_bangumi)把总集数叫 ep_size, 三个名字轮着来, 都试一遍
        if (item.TotalCount <= 0) item.TotalCount = GetInt(el, "ep_size");

        // 更新进度: 推荐接口直接给 index_show; 追番接口把它放在 new_ep 里
        item.IndexShow = GetStr(el, "index_show").StripHtml();
        if (string.IsNullOrEmpty(item.IndexShow) && TryObject(el, "new_ep") is { } nep)
            item.IndexShow = GetStr(nep, "index_show").StripHtml();

        if (string.IsNullOrEmpty(item.IndexShow) && item.TotalCount > 0)
            item.IndexShow = "全" + item.TotalCount + "话";

        // 角标底色: badge_info.bg_color(追番)/ badge_info.bg_color_night
        if (TryObject(el, "badge_info") is { } bi)
        {
            item.BadgeColor = GetStr(bi, "bg_color");
            if (string.IsNullOrEmpty(item.BadgeColor))
                item.BadgeColor = GetStr(bi, "bg_color_night");
            // 有些条目 badge 为空但 badge_info 里带着文字
            if (string.IsNullOrEmpty(item.Badge))
                item.Badge = GetStr(bi, "text").StripHtml();
        }

        if (TryObject(el, "stat") is { } st)
            item.FollowCount = GetLong(st, "follow");

        if (string.IsNullOrEmpty(item.Url))
            item.Url = $"https://www.bilibili.com/bangumi/play/ss{seasonId}";

        return item;
    }

    /// <summary>解析一集。注意 duration 是**毫秒**, 与 UGC 那边(秒)不一样。</summary>
    private static BangumiEpisode? ParseBangumiEpisode(JsonElement ep)
    {
        var epId = GetLong(ep, "ep_id");
        if (epId <= 0) epId = GetLong(ep, "id");
        if (epId <= 0) return null;

        var durationMs = GetLong(ep, "duration");
        return new BangumiEpisode
        {
            EpId = epId,
            Aid = GetLong(ep, "aid"),
            Cid = GetLong(ep, "cid"),
            Bvid = GetStr(ep, "bvid"),
            Title = GetStr(ep, "title"),
            LongTitle = GetStr(ep, "long_title").StripHtml(),
            Cover = UrlUtil.Normalize(GetStr(ep, "cover")),
            // 毫秒 → 秒, 四舍五入到整秒(播放器与卡片都按整秒显示)
            DurationSec = (int)Math.Round(durationMs / 1000.0),
            PubTime = GetLong(ep, "pub_time"),
            // status: 2/13 = 已播; 0 = 预告未播
            IsAired = GetInt(ep, "status") is 2 or 13
        };
    }
}


/// <summary>字符串工具扩展</summary>
public static class StringExtensions
{
    /// <summary>去除搜索标题里的高亮标签</summary>
    public static string StripHtml(this string s)
        => string.IsNullOrEmpty(s)
            ? s
            : s.Replace("<em class=\"keyword\">", "").Replace("</em>", "");
}
