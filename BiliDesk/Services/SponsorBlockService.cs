using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// SponsorBlock 片段数据源(就是"小电视空降助手"背后那套服务端)。
///
/// 为什么自己实现、而不是把那个开源项目移植进来:
///   上游是浏览器扩展(TypeScript, 操作 DOM / video 元素), 而我们要的只是
///   "按 bvid 问一句、拿回一串时间区间"这一件事 —— 按 HTTP 协议自己写几十行更短、更直白。
///   而且它就是 GPL-3.0, 搬代码会把整个 BiliDesk 拖进传染性许可。
///
/// 三条硬约束(全部是 2026-09-27 实测出来的, 详见根目录 SponsorBlock集成可行性.md):
///   1. 只能用自建服务端 www.bsbsb.top —— 官方的 sponsor.ajay.app 不认 BV 号, 直接 Not Found。
///   2. bvid **大小写敏感**。
///   3. 任何失败都必须静默降级成"这个视频没有片段"。这个功能是锦上添花,
///      **绝不允许**它拖慢起播、更不允许它影响播放 —— 所以有硬超时和结果缓存。
/// </summary>
public sealed class SponsorBlockService
{
    private const string ApiBase = "https://www.bsbsb.top/api/skipSegments";

    /// <summary>
    /// 查询硬超时。3 秒是"慢网络也够用、又不会让人干等"的折中;
    /// 而且起播时最多只会为它等 800ms(见 PlayerWindow), 剩下的由后台补 —— 所以它慢不影响起播。
    ///
    /// ★ **这一行必须排在 Instance 前面**: C# 的静态字段初始化按**声明顺序**执行, 而
    /// Instance 的 `new()` 会在自己的构造函数里读这个值。声明在后面的话, 构造单例时它还是
    /// default(TimeSpan) = 0, `HttpClient.Timeout = 0` 抛 ArgumentOutOfRangeException,
    /// 整个类型初始化失败 —— 而本类所有静态方法(Select/SplitCsv)也会连带抛
    /// TypeInitializationException, 也就是说"设置页一打开就崩"。
    /// (2026-09-27 就是被离屏探针抓到的, 不是推理出来的。)
    /// </summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>缓存条目上限(一条 = 一个视频的一个分P)。够连着看一晚上的量</summary>
    private const int CacheCap = 64;

    /// <summary>单例(声明位置见上面 QueryTimeout 的说明, 别往上挪)</summary>
    public static SponsorBlockService Instance { get; } = new();

    private readonly HttpClient _http;

    /// <summary>
    /// 结果缓存(键 = bvid|cid)。
    ///
    /// 只缓存**查询成功**的结果, 包括"确实没有片段"这个空结果 ——
    /// 空结果也缓存是有意的: 绝大多数视频都没人标注过, 不缓存的话每看一次就要问一次第三方,
    /// 既慢又没必要。而**失败**(超时 / 网络错 / 响应畸形)一律不缓存, 下次还能再试。
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<SponsorSegment>> _cache = new();

    /// <summary>缓存键的插入顺序, 用于按"最早进来的先淘汰"收缩到 CacheCap</summary>
    private readonly Queue<string> _order = new();

    /// <summary>同一次查询的并发合并: 同一个视频被两个入口同时要, 只发一个请求</summary>
    private readonly Dictionary<string, Task<IReadOnlyList<SponsorSegment>?>> _inflight = new();

    private readonly object _gate = new();

    private SponsorBlockService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = QueryTimeout
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    /// <summary>
    /// 取某个分 P 的**原始**片段列表(未按设置过滤, 含所有类别)。
    ///
    /// 刻意返回未过滤的: 过滤规则(开关 / 勾了哪些类别)属于**设置**, 与数据无关。
    /// 这样用户改类别时可以用缓存里的原始数据立刻重算, 不必再问一次第三方。
    /// 失败 / 超时 / 取消一律返回空列表, **不抛异常** —— 调用方不该为它写任何错误分支。
    /// </summary>
    public async Task<IReadOnlyList<SponsorSegment>> GetRawAsync(
        string bvid, long cid, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return Array.Empty<SponsorSegment>();

        var key = bvid + "|" + cid;
        Task<IReadOnlyList<SponsorSegment>?> task;
        var owner = false;

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            if (_inflight.TryGetValue(key, out var running))
            {
                task = running;         // 复用在跑的那次查询(由它负责清理登记)
            }
            else
            {
                task = QueryAsync(bvid, ct);
                _inflight[key] = task;
                owner = true;
            }
        }

        IReadOnlyList<SponsorSegment>? list;
        try
        {
            list = await task.ConfigureAwait(false);
        }
        catch
        {
            list = null;                // 取消(换片/关窗)与网络失败走同一条路
        }

        if (owner)
        {
            lock (_gate) _inflight.Remove(key);
        }

        if (list == null) return Array.Empty<SponsorSegment>();   // 失败不进缓存, 下次还能再试

        lock (_gate)
        {
            if (!_cache.ContainsKey(key))
            {
                _cache[key] = list;
                _order.Enqueue(key);
                while (_order.Count > CacheCap) _cache.Remove(_order.Dequeue());
            }
        }
        return list;
    }

    /// <summary>请求一次。返回 null = 这次查询**失败**(超时/网络/响应畸形), 与"没有片段"(空列表)区分开</summary>
    private async Task<IReadOnlyList<SponsorSegment>?> QueryAsync(string bvid, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(QueryTimeout);

        var url = ApiBase + "?videoID=" + Uri.EscapeDataString(bvid);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var text = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        return Parse(text);
    }

    /// <summary>
    /// 解析服务端返回的片段数组。
    ///
    /// 服务端给的是一个**裸数组**(没有 code/data 包壳):
    /// <code>
    /// [{"cid":"42162454697","category":"sponsor","actionType":"skip",
    ///   "segment":[342.551,429.825],"UUID":"2d44da50…","votes":2,"locked":0}]
    /// </code>
    /// 没有片段时返回 `[]`(不是 404)。**畸形响应返回 null(算失败, 不进缓存)** ——
    /// 不能把"服务端改格式了"缓存成"这个视频没有片段", 那会一直错到重启。
    /// </summary>
    private static IReadOnlyList<SponsorSegment>? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var result = new List<SponsorSegment>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty("segment", out var seg) || seg.ValueKind != JsonValueKind.Array) continue;
                var parts = seg.EnumerateArray().ToArray();
                if (parts.Length < 2) continue;
                if (!parts[0].TryGetDouble(out var start) || !parts[1].TryGetDouble(out var end)) continue;

                // cid 是**字符串**型(不是数字), 和 UUID 一样得自己转
                long.TryParse(Str(e, "cid"), out var cid);

                result.Add(new SponsorSegment
                {
                    Cid = cid,
                    Category = Str(e, "category"),
                    ActionType = Str(e, "actionType"),
                    StartSec = start,
                    EndSec = end,
                    Uuid = Str(e, "UUID"),
                    Votes = Int(e, "votes")
                });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.String || v.ValueKind == JsonValueKind.Number)
            ? v.ToString() : "";

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    /// <summary>
    /// 从原始数据里挑出"这个分 P 现在该跳的那些片段"。
    ///
    /// 做成纯函数(不读设置、不碰 UI)是有意的: 过滤规则是这个功能唯一容易出错的地方,
    /// 纯函数才能被探针反复验证。四条规则:
    ///   1. **cid 必须相同** —— 多 P 视频的片段按分 P 标注, 用错分 P 会跳到不相干的位置;
    ///   2. **只认 actionType == "skip"** —— `full` 的 segment 是 [0,0](整条视频都是该类内容),
    ///      当区间处理会把用户直接送回开头; `mute`/`poi` 也不是"跳过"语义;
    ///   3. **votes >= 0** —— 实测存在 votes = -1 的存疑标注, 跳了是帮倒忙;
    ///   4. **按勾选的类别过滤** —— 逗号分隔的 category 原值。
    /// 最后按起点排序, 方便播放器顺着找"当前落在哪一条里"。
    /// </summary>
    public static IReadOnlyList<SponsorSegment> Select(
        IReadOnlyList<SponsorSegment> raw, long cid, string? categoriesCsv)
    {
        if (raw.Count == 0) return Array.Empty<SponsorSegment>();

        var wanted = SplitCsv(categoriesCsv);
        if (wanted.Count == 0) return Array.Empty<SponsorSegment>();

        var list = new List<SponsorSegment>();
        foreach (var s in raw)
        {
            if (s.Cid > 0 && s.Cid != cid) continue;
            if (!string.Equals(s.ActionType, "skip", StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Votes < 0) continue;
            if (!wanted.Contains(s.Category)) continue;
            if (s.EndMs <= s.StartMs) continue;
            list.Add(s);
        }
        list.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return list;
    }

    /// <summary>把逗号分隔的类别串拆成集合(顺带去掉空项与首尾空白)</summary>
    public static HashSet<string> SplitCsv(string? csv)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = part.Trim();
            if (t.Length > 0) set.Add(t);
        }
        return set;
    }
}
