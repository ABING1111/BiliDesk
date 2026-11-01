using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// CDN(线路)测速与选择。★ 2026-10-03 新增。
///
/// 做什么:
///   · 自动模式: 从候选线路里实测吞吐, 挑最快的一条;
///   · 手动模式: 用用户指定的那一家 CDN;
///   · 跟随服务端: 不换 host, 用接口给的线路;
///   · 任何模式下都能屏蔽 PCDN(点对点分发)。
///
/// ★★ 换 host 的边界(2026-10-03 实测, 见 .probes/bd-probe-matrix):
///   同一份视频的 baseUrl/backupUrl **只有 host 不同**, path 与 query(含签名)一致,
///   所以"换 host"通常有效。但有**一个明确例外**:
///     · 模板 host 是 `*.mcdn.bilivideo.cn` → 换成任何标准 CDN **一律 403**
///       (实测 4 个视频 × 9 家 = 36 次全部 403, 签名与这个 host 绑死);
///     · 模板 host 是其它(`upos-sz-mirror*` / `estgoss` / `bcache` /
///       `*.edge.mountaintoys.cn`) → 换 9 家**全部成功**(实测 72/72)。
///   所以本类**永远不会拿 mcdn 主机当模板**去做替换(见 IsSwappableTemplate)。
///
/// ★★ 另一个实测发现(也见 CdnOption): PCDN 有两种写法
///   · 域名带 `mcdn.` / `pcdn.`;
///   · **伪装域名**: `*.edge.mountaintoys.cn`, 域名看不出特征, 靠 query 里的 `os=mcdn` 认。
///   旧实现只看域名, 会漏掉第二种 —— 这正是"屏蔽 PCDN"要客户端自己判断的原因。
///
/// ★★ 设计原则: 测速即校验。
///   每条候选都通过一次带 `Range` 的小请求来计时, 只有真返回 200/206 的才进排名。
///   于是"测出来最快的那条"必然也是**真的能拉**的那条, 不需要额外再验一遍。
///   全部候选都失败时静默回退到服务端原样线路 —— 换 CDN 是锦上添花,
///   绝不能因为它让本来能播的视频播不了。
/// </summary>
public sealed class CdnService
{
    public static CdnService Instance { get; } = new();

    /// <summary>单条线路的测速窗口</summary>
    private const int SpeedTestBytes = 128 * 1024;
    private const int SpeedTestTimeoutMs = 3000;

    /// <summary>
    /// 起播路径上给整轮测速的**硬预算**。
    ///
    /// ★ 为什么必须有(2026-10-30 实测): 候选里既有 0.16~0.24s 的常规线路, 也有
    ///   `upos-sz-mirroraliov`(2.11s)与直接连不上的 `08c`。`Task.WhenAll` 要等**最慢**
    ///   的那条 —— 于是每条视频的起播都被拖 2.1s+(最坏 3s 探针超时)。
    ///   到点只取已完成且校验通过的线路, 剩下的直接取消: 排名略粗糙, 但起播快得多。
    /// </summary>
    private static readonly TimeSpan StartupRankBudget = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// 测速结果缓存时长。
    ///
    /// 为什么要缓存: 起播路径上做一轮测速要几秒, 每次切清晰度/换集都测一遍会明显变慢。
    /// 为什么 10 分钟: CDN 排名主要随**本地网络状况**变化, 短时间内不会突变;
    /// 真变了用户手动点一次「重新测速」即可 —— 比每次起播都慢几秒划算。
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly object _gate = new();

    // 缓存: 上一次测速的赢家 **host**。
    // ★ 存 host 而不是完整 URL: URL 里带 deadline/trid/upsig, 每条视频都不同,
    //   拿它当缓存键会让缓存永远不命中(这正是"每条视频都重新测速 2 秒"的根因)。
    // ★ 也**不再按"候选池"分键**: 服务端给同一批视频的 base/backup host 组合每条都变
    //   (mirrorhwb / estgoss / mirrorali …), 按池分键仍然几乎每条都 miss。
    //   而"换 host"对非 mcdn 模板是普遍成立的(实测 72/72), 与具体视频无关 ——
    //   所以一个 10 分钟有效的全局赢家就够, 与音轨那条 SelectSecondaryUrl 的做法一致。
    //   命中时用**当前视频**的模板 ReplaceHost 重建, 不会复用别人的过期签名。
    private string? _cachedBestHost;
    private DateTime _cachedAt = DateTime.MinValue;
    private string? _lastReport;

    /// <summary>最近一轮测速冠军的吞吐(字节/秒); 没测过为 0。给 BufferPolicy 自动档用</summary>
    private long _lastMeasuredBytesPerSecond;

    /// <summary>最近一次后台测速是否在飞(防止重复排任务)</summary>
    private int _warmingUp;

    private CdnService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            // 连不上就快点失败, 别让一条坏线路拖垮整轮测速
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Helpers.HttpDefaults.UserAgent);
    }

    /// <summary>最近一次测速的可读报告(设置页展示用, 让用户知道测出了什么)</summary>
    public string LastReport
    {
        get { lock (_gate) return _lastReport ?? "尚未测速"; }
    }

    /// <summary>
    /// 最近一轮测速冠军线路的吞吐(字节/秒); 从未测过为 0。
    /// 只读属性(内部加锁), 供 BufferPolicy 自动档挑预读深度用 ——
    /// 它与 CachedBest 一样有 10 分钟 TTL 语义: 过期值照样返回, 由调用方接受"略旧"的误差
    /// (挑缓冲档位不需要精确, 差一档无所谓; 再为它做读取时的过期清零反而引入抖动)。
    /// </summary>
    public long LastMeasuredBytesPerSecond
    {
        get { lock (_gate) return _lastMeasuredBytesPerSecond; }
    }

    /// <summary>缓存的"当前最快"是哪一家(没测过或已过期返回 null)</summary>
    public CdnOption? CachedBest
    {
        get
        {
            lock (_gate)
            {
                if (_cachedBestHost == null) return null;
                if (DateTime.UtcNow - _cachedAt > CacheTtl) return null;
                return CdnOption.All.FirstOrDefault(o =>
                    string.Equals(o.Host, _cachedBestHost, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    /// <summary>清掉测速缓存(用户手动点「重新测速」、或改了 CDN 设置时调用)</summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cachedBestHost = null;
            _cachedAt = DateTime.MinValue;
            // 吞吐也一起作废: BufferPolicy 会据此回落 1000 兜底, 与"没测过"同义。
            // (设置页改线路模式后旧吞吐属于另一条线路的排名, 留着会误导自动档。)
            _lastMeasuredBytesPerSecond = 0;
        }
    }

    /// <summary>
    /// 给一条媒体 URL 选线路。
    ///
    /// 返回的 URL 只换了 host(scheme/path/query 原样保留)。
    /// ★ 2026-11-01 起本方法**同步返回**(不再有内部 await): 测速与手动线路的可用性探测
    ///   都挪到了后台, 起播路径上零等待。保留 Task 返回值以免改动 8 个调用点的签名。
    /// </summary>
    /// <param name="url">服务端给的一条 baseUrl(作为原样回退用)</param>
    /// <param name="allCandidates">该流的全部候选(含 baseUrl 与所有 backupUrl)</param>
    public Task<string> SelectUrlAsync(string url, IReadOnlyList<string> allCandidates)
    {
        var settings = SettingsStore.Instance;

        // 屏蔽 PCDN: 对三种模式都生效("用哪家 CDN"和"要不要 PCDN"是两件独立的事)
        var pool = settings.BlockPcdn
            ? allCandidates.Where(u => !IsPcdn(u)).ToList()
            : new List<string>(allCandidates);
        // 过滤后一条不剩: 宁可不过滤, 也不能让视频播不了
        if (pool.Count == 0) pool = new List<string>(allCandidates);
        if (pool.Count == 0) pool = new List<string> { url };

        if (settings.CdnMode == CdnSelectMode.ServerDefault)
            return Task.FromResult(pool[0]);

        // 手动: 想要的那一家能验证通过就用它, 否则退回自动。
        // ★ 2026-11-01: 手动模式的可用性探测(ProbeAsync ≤3s)也挪到了**后台** ——
        //   它同样是起播路径上的固定等待。现在先用指定线路起播; 探测发现拉不动时
        //   只更新报告并预热缓存, 让下一条视频走赢家(不在播放中途换源)。
        if (settings.CdnMode == CdnSelectMode.Manual)
        {
            var target = CdnOption.ById(settings.CdnManualId);
            var manualUrl = target == null ? null : TryBuildSwapped(pool, target.Host);
            if (manualUrl != null)
            {
                _ = Task.Run(async () =>
                {
                    var (ok, _, _) = await ProbeAsync(manualUrl!).ConfigureAwait(false);
                    if (ok) return;
                    // 指定的这家拉不动(实测个别 CDN 会 403): 预热一轮测速,
                    // 让下一条视频直接用赢家; 本条已经用指定线路在播, 不动它。
                    lock (_gate)
                        _lastReport = $"{DateTime.Now:HH:mm:ss} 指定的 {target!.Name} 不可用, 下一条起将自动改用最快线路";
                    WarmUpInBackground(pool);
                });
                return Task.FromResult(manualUrl);
            }
        }

        return Task.FromResult(AutoPick(pool));
    }

    /// <summary>
    /// 音轨等**次要流**用的线路选择: 复用视频那次的结果, **不重新测速**。
    ///
    /// 为什么音轨不单独测速: 它只有 0.1~0.2Mbps, 单独跑一轮纯属浪费; 而且它与视频
    /// 属于同一个 input(DASH 靠 input-slave 挂进来), 用同一家 CDN 反而更稳。
    /// </summary>
    public string SelectSecondaryUrl(string url, IReadOnlyList<string> allCandidates)
    {
        var settings = SettingsStore.Instance;

        var pool = settings.BlockPcdn
            ? allCandidates.Where(u => !IsPcdn(u)).ToList()
            : new List<string>(allCandidates);
        if (pool.Count == 0) pool = new List<string>(allCandidates);
        if (pool.Count == 0) return url;

        if (settings.CdnMode == CdnSelectMode.ServerDefault) return pool[0];

        if (settings.CdnMode == CdnSelectMode.Manual)
        {
            var target = CdnOption.ById(settings.CdnManualId);
            var manualUrl = target == null ? null : TryBuildSwapped(pool, target.Host);
            if (manualUrl != null) return manualUrl;
        }

        // 自动: 用视频那次测速的赢家, 没测过就用兜底 CDN
        var cached = CachedBest;
        if (cached != null)
        {
            var swapped = TryBuildSwapped(pool, cached.Host);
            if (swapped != null) return swapped;
        }
        return pool[0];
    }

    /// <summary>
    /// 自动: 有缓存且没过期就直接用(不再按候选池比对)。
    ///
    /// ★★ 2026-11-01 起播路径**不再等待测速**(用户已确认接受该行为变化):
    ///   之前这里会同步等一轮 800ms 预算的测速 —— 那是每个会话首条视频起播前
    ///   最固定的一块等待(10 分钟缓存过期后的第一条必中)。现在改为:
    ///     ① 立即返回服务端首选线路(PCDN 屏蔽在 SelectUrlAsync 里已做, 与测速无关);
    ///     ② 后台延迟 1.5 秒再预热测速 —— 等首帧把带宽用起来之后再跑, 免得跟它抢;
    ///     ③ 测完写进缓存, 只影响**下一条**视频/下一次切清晰度。
    ///   绝不在播放中途换源: 换 host 重连曾实测踩过"跳转花屏 + 音画不同步"
    ///   (见 ShortVideoCache 顶注), 收益不抵风险。
    /// </summary>
    private string AutoPick(List<string> pool)
    {
        lock (_gate)
        {
            if (_cachedBestHost != null && DateTime.UtcNow - _cachedAt <= CacheTtl)
            {
                // ★ 缓存里只有 host: 必须用**当前视频**的 URL 模板重建,
                //   绝不能把上一条视频的完整 URL 直接返回(签名会过期且 session 不对)。
                var swapped = TryBuildSwapped(pool, _cachedBestHost);
                if (swapped != null) return swapped;
            }
        }

        // 缓存没命中: 先用服务端线路起播, 后台补测速(见方法顶注)
        WarmUpInBackground(pool);

        // ★ 起播路径到此就返回服务端线路 —— 不再等测速。
        //   (RankAsync 的完整等待只保留给设置页「开始测速」, 那里的语义是"把家底都测一遍"。)
        return pool[0];
    }

    /// <summary>
    /// 后台预热测速: 延迟 1.5 秒(让首帧先跑), 再以 800ms 预算跑一轮完整 RankAsync。
    /// 结果经 Publish 落进缓存与 LastMeasuredBytesPerSecond, 服务下一条视频。
    /// 同时只有一个预热在飞(Interlocked 闸); 失败静默 —— 下一 条视频还会再试。
    /// </summary>
    private void WarmUpInBackground(List<string> pool)
    {
        if (Interlocked.CompareExchange(ref _warmingUp, 1, 0) != 0) return;
        var candidates = pool.ToList();   // 调用方的池可能被复用, 拷一份给后台
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                await RankAsync(candidates, StartupRankBudget).ConfigureAwait(false);
            }
            catch
            {
                // 预热是纯增益: 失败不影响本次播放, 也不值得记错误日志
            }
            finally
            {
                Interlocked.Exchange(ref _warmingUp, 0);
            }
        });
    }

    /// <summary>
    /// 对候选池做一轮完整测速(设置页「开始测速」用), 返回按吞吐从高到低排序且**已验证可用**的线路。
    ///
    /// ★ 它同时负责更新测速报告与缓存 —— 因为设置页的「开始测速」按钮直接调这个方法,
    ///   如果报告只在测速完成处写, 用户点按钮后看到的会是**上一次的旧结果**
    ///   (实测踩过: 报告写"阿里云 2461KB/s", 而真实第一名是 13473KB/s)。
    ///
    /// 并排测: 总耗时 ≈ 最慢那条的窗口时间, 而不是累加。
    /// </summary>
    public Task<List<(string Url, long BytesPerSecond, long ElapsedMs)>> RankAsync(List<string> pool)
        => RankAsync(pool, TimeSpan.Zero);

    /// <summary>
    /// 带预算的测速。<paramref name="budget"/> 为 <see cref="TimeSpan.Zero"/> 时等全部候选
    /// (设置页手动测速); 否则到点取消剩余候选, 只用已完成的结果排名。
    ///
    /// ★ 为什么预算只用在起播路径: 手动测速的语义是"把家底都测一遍", 截断会让报告说谎;
    ///   而起播只需要"够快的一条", 不值得为最慢的海外线路等 2 秒。
    /// </summary>
    private async Task<List<(string Url, long BytesPerSecond, long ElapsedMs)>> RankAsync(
        List<string> pool, TimeSpan budget)
    {
        var candidates = BuildCandidateSet(pool);
        var budgeted = budget > TimeSpan.Zero;

        using var cts = new CancellationTokenSource();
        if (budgeted) cts.CancelAfter(budget);

        var tasks = candidates.Select(async u =>
        {
            var (ok, ms, bytes) = await ProbeAsync(u, cts.Token).ConfigureAwait(false);
            return (url: u, ok, ms, bytes);
        }).ToList();

        if (budgeted)
        {
            // 到点就收工。★ 不能 await Task.WhenAll: 它会把被取消的探针也等完(取消本身
            //   也需要一次 IO 回调), 起播路径上这几百毫秒同样是白等。
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(budget)).ConfigureAwait(false);
            try { cts.Cancel(); } catch { /* 已经结束 */ }
        }
        else
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        var ranked = new List<(string Url, long BytesPerSecond, long ElapsedMs)>();
        foreach (var t in tasks)
        {
            // 未完成/被取消的探针直接跳过 —— 绝不在这里 .Result 阻塞
            if (t.Status != TaskStatus.RanToCompletion) continue;
            var (u, ok, ms, bytes) = t.Result;
            if (ok && bytes > 0)
                ranked.Add((u, bytes * 1000 / Math.Max(1, ms), ms));
        }

        // 同速时**优先服务端原样线路**(它没有换 host 的 403 风险)
        var poolSet = new HashSet<string>(pool, StringComparer.Ordinal);
        ranked.Sort((a, b) =>
        {
            var bySpeed = b.BytesPerSecond.CompareTo(a.BytesPerSecond);
            if (bySpeed != 0) return bySpeed;
            return (poolSet.Contains(b.Url) ? 1 : 0) - (poolSet.Contains(a.Url) ? 1 : 0);
        });

        // 写报告 + 缓存(设置页测速与后台预热共用这一份写入逻辑, 免得两处写法漂移)
        Publish(ranked);
        return ranked;
    }

    /// <summary>把一轮测速的结果写成报告与缓存。ranked 为空表示全部不可达。</summary>
    private void Publish(List<(string Url, long BytesPerSecond, long ElapsedMs)> ranked)
    {
        lock (_gate)
        {
            if (ranked.Count > 0)
            {
                _cachedBestHost = SafeHost(ranked[0].Url);
                _cachedAt = DateTime.UtcNow;
                _lastMeasuredBytesPerSecond = ranked[0].BytesPerSecond;

                var others = ranked.Skip(1).Take(3)
                    .Select(r => $"{CdnOption.All.FirstOrDefault(o =>
                        string.Equals(o.Host, SafeHost(r.Url), StringComparison.OrdinalIgnoreCase))?.Name
                        ?? SafeHost(r.Url)} {r.BytesPerSecond / 1024}KB/s");
                _lastReport = $"{DateTime.Now:HH:mm:ss} 最快 {CdnName(ranked[0].Url)} " +
                              $"({ranked[0].BytesPerSecond / 1024}KB/s)" +
                              (ranked.Count > 1 ? $"; 其次 {string.Join(", ", others)}" : "");
            }
            else
            {
                _cachedBestHost = null;
                _lastMeasuredBytesPerSecond = 0;
                _lastReport = $"{DateTime.Now:HH:mm:ss} 测速失败: 候选线路都不可达, 已回退服务端线路";
            }
        }
    }

    /// <summary>把一个 URL 的 host 翻译成候选表里的显示名(认不出就退回 host)</summary>
    private static string CdnName(string url)
    {
        var host = SafeHost(url);
        return CdnOption.All.FirstOrDefault(o =>
            string.Equals(o.Host, host, StringComparison.OrdinalIgnoreCase))?.Name ?? host;
    }

    /// <summary>
    /// 组出要测的候选集 = 服务端原样线路 + 从"可换模板"派生的标准 CDN 线路。
    ///
    /// 为什么两套都要: 原样线路是**一定安全**的兜底; 派生线路把可选项从服务端给的
    /// 2~3 条扩到 9 家, 实测各家吞吐差 4 倍以上, 多试几家更有机会挑到快的。
    /// </summary>
    private static List<string> BuildCandidateSet(List<string> pool)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        // 1) 原样
        foreach (var u in pool)
            if (seen.Add(u)) result.Add(u);

        // 2) 派生(只有找到可换模板才做)
        var template = pool.FirstOrDefault(IsSwappableTemplate);
        if (template != null)
        {
            foreach (var opt in CdnOption.All)
            {
                var swapped = ReplaceHost(template, opt.Host);
                if (seen.Add(swapped)) result.Add(swapped);
            }
        }
        return result;
    }

    /// <summary>
    /// 从池子里挑一个**可换模板**, 换成指定 host。
    /// 找不到可换模板(全是 mcdn)时返回 null —— 那种情况换 host 必 403(见类注释)。
    /// </summary>
    private static string? TryBuildSwapped(List<string> pool, string targetHost)
    {
        var template = pool.FirstOrDefault(IsSwappableTemplate);
        return template == null ? null : ReplaceHost(template, targetHost);
    }

    /// <summary>
    /// 这条线路能不能当"换 host 的模板"。
    ///
    /// ★ 实测: `*.mcdn.bilivideo.cn` 的签名与该 host 绑死, 换成任何标准 CDN 都 403,
    ///   所以它不能当模板。其它 host(含伪装成 edge.* 但 os=mcdn 的那些)都可以。
    /// </summary>
    private static bool IsSwappableTemplate(string url)
    {
        var host = SafeHost(url);
        return host.Length > 0 &&
               !host.Contains("mcdn.", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeHost(string url)
    {
        try { return new Uri(url).Host; } catch { return ""; }
    }

    /// <summary>
    /// 测一条线路: 限 byte 数 + 限时, 返回 (是否可用, 耗时ms, 读到的字节数)。
    ///
    /// ★ 这个探测**同时承担可用性校验**: 只有真返回 200/206 并且读出字节才算成功。
    ///   换 host 后可能 403 —— 403 会在这里被判为不可用, 于是不会进排名。
    /// ★ <paramref name="outer"/>: 起播路径的整轮预算, 到点会把未完成的探针一起取消。
    /// </summary>
    private async Task<(bool ok, long ms, long bytes)> ProbeAsync(string url, CancellationToken outer = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            req.Headers.TryAddWithoutValidation("Range", $"bytes=0-{SpeedTestBytes - 1}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(SpeedTestTimeoutMs);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                sw.Stop();
                return (false, sw.ElapsedMilliseconds, 0);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buf = new byte[64 * 1024];
            long total = 0;
            while (total < SpeedTestBytes)
            {
                var n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
            }
            sw.Stop();
            return (total > 0, sw.ElapsedMilliseconds, total);
        }
        catch (OperationCanceledException)
        {
            // 超时/整轮预算到点: 这条线路不参与排名
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, 0);
        }
        catch
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, 0);
        }
    }

    /// <summary>把 URL 的 host 换成目标 CDN(路径/查询串全部保留; 统一走 https)</summary>
    public static string ReplaceHost(string url, string host)
    {
        try
        {
            return new UriBuilder(url) { Host = host, Port = -1, Scheme = "https" }.Uri.ToString();
        }
        catch
        {
            // URL 结构异常(理论上不会): 原样返回, 至少不破坏播放
            return url;
        }
    }

    /// <summary>
    /// 这条线路是不是 PCDN(点对点分发)。
    ///
    /// ★ 两个来源都要认(2026-10-03 实测):
    ///   1. 域名带 `mcdn.` / `pcdn.`;
    ///   2. **伪装域名**: 实测 B 站给过 `mv0bz14m.edge.mountaintoys.cn` /
    ///      `b-baacc8r65...edge.mountaintoys.cn`, 域名完全看不出 PCDN 特征,
    ///      但 query 里带 `os=mcdn`。只看域名会漏掉它们。
    /// </summary>
    public static bool IsPcdn(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host;

            if (host.Contains("mcdn.", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("pcdn.", StringComparison.OrdinalIgnoreCase))
                return true;

            var query = uri.Query;
            if (query.Length == 0) return false;

            foreach (var kv in query.TrimStart('?').Split('&'))
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                if (!kv[..eq].Equals("os", StringComparison.OrdinalIgnoreCase)) continue;
                if (kv[(eq + 1)..].StartsWith("mcdn", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch { return false; }
    }
}
