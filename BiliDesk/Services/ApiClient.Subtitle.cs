using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 哔哩哔哩 API 客户端: 带浏览器 UA、Cookie、WBI 签名与风控降级逻辑。
/// ★ 2026-11-07 由单文件拆成 17 个 partial 分片(纯移动、零逻辑改动),
///   分片清单与动机见 ApiClient.Core.cs 头部说明。
/// </summary>
public partial class ApiClient
{
    // ---------------------------------------------------------------- 字幕(2026-11-15)

    /// <summary>字幕本体的两个已知 host: subtitle.bilibili.com(部分视频下发, DNS 常解析不到)
    /// 与 aisubtitle.hdslb.com(常规 AI 字幕 CDN, 实测可解析)。下载失败时在这两个域间兜底。</summary>
    internal const string SubtitleAltHost = "aisubtitle.hdslb.com";
    internal const string SubtitleBrokenHost = "subtitle.bilibili.com";

    /// <summary>
    /// 取视频的字幕轨列表(CC 人工 + AI)。
    ///
    /// 接口: <c>GET /x/player/wbi/v2?bvid=&amp;cid=</c>(需 WBI 签名 + 登录态)。
    /// ★ 实测(2026-11, 多来源交叉印证):
    ///   · 未登录时 **subtitles 是空数组**, 而 need_login_subtitle=true 表示"有字幕但看不到"
    ///     —— 调用方用这个字段区分"真没有"与"要登录", 提示语才说得准;
    ///   · **绝不能**对"code=0 但字幕为空"降级到旧接口 /x/player/v2: 旧接口有 CDN 缓存串扰,
    ///     实测会返回**别的视频**的字幕(yt-dlp PR#11708 已确认)。只在 WBI 明确失败(-403/-400)时
    ///     才降级一次 —— 那种失败是"签名坏了", 不是"没有字幕"。
    /// </summary>
    public async Task<(bool ok, string? err, List<SubtitleTrack> tracks, bool needLogin)>
        GetSubtitleTracksAsync(string bvid, long cid, CancellationToken ct = default)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString()
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/v2", ps, sign: true,
                referer: "https://www.bilibili.com/video/" + bvid);

            // WBI 侧失败(签名/风控)才走旧接口兜底; code=0 但字幕空是"真没有", 不降级。
            if (code != 0 && (code == -403 || code == -400))
            {
                var legacy = await GetLegacySubtitleTracksAsync(bvid, cid);
                if (legacy != null) return (true, null, legacy, false);
            }
            if (code != 0 || data == null)
                return (false, msg ?? "获取字幕列表失败", new List<SubtitleTrack>(), false);

            var tracks = SubtitleParser.ParseTracks(data.Value, out var needLogin);
            return (true, null, tracks, needLogin);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, new List<SubtitleTrack>(), false);
        }
    }

    /// <summary>旧接口兜底(仅在 WBI 明确失败时调用, 见 GetSubtitleTracksAsync 的说明)。</summary>
    private async Task<List<SubtitleTrack>?> GetLegacySubtitleTracksAsync(string bvid, long cid)
    {
        try
        {
            var ps = new Dictionary<string, string> { ["bvid"] = bvid, ["cid"] = cid.ToString() };
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/v2", ps,
                referer: "https://www.bilibili.com/video/" + bvid);
            if (code != 0 || data == null) return null;
            var tracks = SubtitleParser.ParseTracks(data.Value, out _);
            return tracks.Count > 0 ? tracks : null;   // 空结果不当成功: 串扰的响应多半也是空
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 下载字幕本体 JSON 并解析成时间轴。
    ///
    /// ★ 请求**故意不带 Cookie**: subtitle_url 指向 aisubtitle.hdslb.com(CDN),
    ///   把登录态发给第三方域没有任何收益, 只平添泄露面。UA + Referer 足够(实测必带 Referer)。
    /// ★ 不走 GetJsonAsync: 那个会带 Cookie + 设备指纹, 且响应包在 {code, data} 信封里 ——
    ///   字幕本体是裸 JSON, 结构完全不同。
    ///
    /// ★ 域名兜底(2026-11-15): B 站给部分视频下发的地址挂在 **subtitle.bilibili.com** 上
    ///   (web 端播放器也从这拉), 但该域在本机/公共 DNS 下**解析不到**(NXDOMAIN, 实测
    ///   8.8.8.8 与 ISP 都查不到; 分域 CDN 只对特定区域下发解析)。hdslb 域(bfs 目录结构
    ///   与它一致)实测可解析 —— DNS 失败时换 host 重试一次, 两种 host 下文件路径相同。
    /// </summary>
    public async Task<(bool ok, string? err, List<SubtitleCue> cues)>
        GetSubtitleCuesAsync(string url, CancellationToken ct = default)
    {
        var (ok, err, cues) = await GetSubtitleCuesOnceAsync(url, ct);
        if (ok) return (ok, err, cues);

        // DNS/连接类失败 + host 是 subtitle.bilibili.com → 换 hdslb 域重试一次。
        // ★ 只对"找不到主机"这类失败换: HTTP 403/404 说明域名通了, 换域没有意义。
        if (!IsDnsFailure(err)) return (ok, err, cues);
        var alt = ReplaceSubtitleHost(url);
        if (alt == url) return (ok, err, cues);

        var (ok2, err2, cues2) = await GetSubtitleCuesOnceAsync(alt, ct);
        return (ok2, err2 ?? err, cues2);
    }

    /// <summary>把 subtitle.bilibili.com 的地址换成 aisubtitle.hdslb.com(路径原样保留)。其它 host 原样返回。</summary>
    private static string ReplaceSubtitleHost(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        try
        {
            var uri = new Uri(url);
            if (!uri.Host.Equals(SubtitleBrokenHost, StringComparison.OrdinalIgnoreCase)) return url;
            return "https://" + SubtitleAltHost + uri.PathAndQuery;
        }
        catch { return url; }
    }

    /// <summary>错误信息是否属于"域名解析失败"(换域兜底只对这类失败有意义)。</summary>
    private static bool IsDnsFailure(string? err)
        => err != null && (err.Contains("不知道这样的主机", StringComparison.Ordinal)
                           || err.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
                           || err.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase));

    private async Task<(bool ok, string? err, List<SubtitleCue> cues)>
        GetSubtitleCuesOnceAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, "字幕下载失败(HTTP " + (int)resp.StatusCode + ")", new List<SubtitleCue>());

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var cues = SubtitleParser.ParseBody(doc.RootElement);
            // 空时间轴也算成功: 弹层显示"没有字幕内容", 不当错误打用户脸上
            return (true, null, cues);
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消", new List<SubtitleCue>());
        }
        catch (HttpRequestException ex)
        {
            // 裸的 SocketException 消息(如"不知道这样的主机。")对用户是天书, 给一句人话
            return (false, "字幕服务器连接失败: " + ex.Message, new List<SubtitleCue>());
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, new List<SubtitleCue>());
        }
    }
}
