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
    /// 获取视频弹幕(protobuf 段接口)。
    ///
    /// 为什么换成这个接口: 旧实现走的 /x/v1/dm/list.so 早已被 B 站废弃,
    /// 多数视频返回 404 或空 XML, 属于"代码在跑但永远没数据"。
    /// 现行方案是 /x/v2/dm/web/seg.so —— 弹幕按 6 分钟一段(segment)存储,
    /// 每段独立返回一份 protobuf 二进制, 需要按段号依次拉取再合并。
    ///
    /// 拉取策略:
    ///   - 已知视频总时长时只拉需要的前 N 段(6 分钟一段), 避免长视频白拉几十个请求;
    ///   - 各段并发请求(默认 4 并发), 明显快于串行;
    ///   - 单段失败不影响其它段, 尽力而为。
    /// </summary>
    /// <param name="cid">视频 cid</param>
    /// <param name="durationSec">视频总时长(秒), &lt;=0 时按最多 20 段拉</param>
    /// <param name="max">条数上限</param>
    /// <returns>按时间排序的弹幕(时间秒 / 文本 / 模式 / 颜色)</returns>
    public async Task<List<RawDanmaku>>
        GetDanmakuAsync(long cid, int durationSec = 0, int max = 6000)
    {
        var result = new List<RawDanmaku>();

        // 启动硬超时: 单 SocketsHttpHandler 实例共用连接池, 超时设 15s 就够,
        // 但外层再兜一个, 防止某个段卡住时整页干等。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        try
        {
            const int segLen = 360; // B 站弹幕固定 6 分钟一段
            var segCount = durationSec > 0
                ? Math.Max(1, (int)Math.Ceiling(durationSec / (double)segLen))
                : 20;
            segCount = Math.Min(segCount, 120); // 上限保护(12 小时)

            var segments = new List<RawDanmaku>[segCount];
            var sem = new SemaphoreSlim(4); // 并发闸门: 同时最多 4 个段请求

            var tasks = new List<Task>(segCount);
            for (var i = 0; i < segCount; i++)
            {
                var index = i; // 闭包捕获
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        segments[index] = await FetchDanmakuSegmentAsync(cid, index + 1, cts.Token)
                            .ConfigureAwait(false) ?? new List<RawDanmaku>();
                    }
                    catch
                    {
                        segments[index] = new List<RawDanmaku>();
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, cts.Token));
            }

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch { /* 个别段失败无所谓 */ }

            // 合并所有段并按时间排序
            var merged = new List<RawDanmaku>();
            foreach (var seg in segments)
                if (seg is { Count: > 0 }) merged.AddRange(seg);

            if (merged.Count == 0) return result;
            merged.Sort((a, b) => a.Time.CompareTo(b.Time));

            foreach (var d in merged)
            {
                if (result.Count >= max) break;
                if (string.IsNullOrWhiteSpace(d.Text)) continue;
                result.Add(d);
            }
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
        return result;
    }

    /// <summary>拉取单个弹幕段并解析为 RawDanmaku 列表(失败返回 null)</summary>
    private async Task<List<RawDanmaku>?> FetchDanmakuSegmentAsync(
        long cid, int segmentIndex, CancellationToken ct)
    {
        try
        {
            var url = "https://api.bilibili.com/x/v2/dm/web/seg.so" +
                      $"?type=1&oid={cid}&segment_index={segmentIndex}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var cookie = AppendFingerprint(SessionManager.Instance.BuildCookieHeader());
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            // 该时间段没有弹幕时, B 站返回 HTTP 304 + 头 "Bili-Status-Code: -304"
            // (不是 200 带空 body)。这属于正常情况, 返回空列表而不是当失败处理。
            if ((int)resp.StatusCode == 304) return new List<RawDanmaku>();
            if (resp.Headers.TryGetValues("Bili-Status-Code", out var codes) &&
                codes.Any(c => c.Contains("-304")))
                return new List<RawDanmaku>();
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return new List<RawDanmaku>();

            return DanmakuParser.ParseSegReply(bytes);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------ 发送

    /// <summary>
    /// 发送一条滚动弹幕(2026-11-12 新增, 用户要求"在播放器下方增加弹幕发送的输入框")。
    ///
    /// 接口: POST /x/v2/dm/post
    ///   · type=1     视频弹幕
    ///   · oid        稿件 cid(不是 avid/bvid)
    ///   · progress   弹幕出现的时间点(毫秒), 不是进度条秒数
    ///   · mode=1     滚动弹幕
    /// 风控: 复用点赞/投币那条 PostFormAsync(csrf + Cookie + Origin + 写前预热),
    /// 失败时返回的是已经翻译过的、可以照做的提示。
    /// </summary>
    public async Task<(bool ok, string? err)> SendDanmakuAsync(long cid, string msg, long progressMs)
    {
        if (cid <= 0) return (false, "还没有拿到这条视频的弹幕参数(cid)");
        if (!SessionManager.Instance.HasLogin) return (false, "发弹幕需要登录");

        var text = (msg ?? "").Trim();
        if (text.Length == 0) return (false, "弹幕内容不能为空");
        if (text.Length > 100) return (false, "弹幕最长 100 个字");

        var form = new Dictionary<string, string>
        {
            ["type"] = "1",
            ["oid"] = cid.ToString(),
            ["msg"] = text,
            ["progress"] = Math.Max(0, progressMs).ToString(),
            ["color"] = "16777215",   // 白色(与本应用弹幕层默认色一致)
            ["fontsize"] = "25",
            ["mode"] = "1",           // 滚动弹幕
            ["pool"] = "0",
            ["plat"] = "1",
        };

        return await PostFormAsync(
            "https://api.bilibili.com/x/v2/dm/post",
            form,
            referer: "https://www.bilibili.com/");
    }
}
