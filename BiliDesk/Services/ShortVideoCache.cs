using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 短视频的"本地合流缓存": 把 DASH 的 1080P 音视频下到本地、合成一个 mp4 存起来,
/// 之后播的就是这个本地文件 —— 于是既能 1080P, 又拿到"本地文件"才有的快跳转与完整结尾。
///
/// 为什么这么做(2026-09-25 实测, 详见 Helpers/DashRemuxer 的对照表):
///   远端双流(DASH + input-slave)跳到 60s 要 1383ms 才恢复画面、结尾还固定少 1.9 秒;
///   换成单个本地文件后是 470ms / 349ms —— 因为 `input-slave` 那套机制本身就有这两个硬伤,
///   跟音轨在本地还是远端无关(实测把音轨换成本地文件, 结尾照样少 1911ms)。
///   本地 MPD 方案(交给 VLC 的 adaptive 模块)实测更差(3805ms), 已否决。
///
/// 起播策略(用户 2026-09-25 选定): **下完再播** —— 命中缓存直接播本地; 没有就边下边显示进度
/// (遮罩上"正在下载高清视频 N%"), 下完合流再播。曾试过"先用远端 DASH 开播、后台合流、就绪后换源",
/// 真机上换源那一下暴露了容器定位问题(见 DashRemuxer 的说明), 已删除。
///
/// 磁盘策略("允许写盘但要清理"): 目录 <c>%LocalAppData%\BiliDesk\vcache</c>,
/// 每次写完都 Prune —— 超过 14 天、超过 50 个、总量超 500MB 的一律按"最久没动过"先删;
/// 中断残留的 .part/.tmp 也会被清掉。单片按码率估算超过 150MB 的直接不做(不值得占盘)。
/// 另外启动时也会 Prune 一次(见 App.OnStartup), 免得用户长时间不看视频时旧缓存一直占着。
/// </summary>
public static class ShortVideoCache
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        // 整段视频可能几十兆: 只约束连接建立阶段, 不在读取上设总超时(靠 CancellationToken 取消)
        Timeout = Timeout.InfiniteTimeSpan
    };

    static ShortVideoCache()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
    }

    /// <summary>缓存目录。放在数据目录下而不是系统临时目录: 它是要跨会话复用的</summary>
    public static string Dir => Path.Combine(AppPaths.DataDir, "vcache");

    // 下面三个上限是公开的: 设置页要把"自动清理规则"原样写给用户看,
    // 数字散成两处迟早会对不上, 所以只留这一份。
    public const long MaxTotalBytes = 500L * 1024 * 1024;
    public const int MaxFiles = 50;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>单片上限(按码率估算)。超过就不做本地合流 —— 为"拖进度条顺一点"占太多盘不值</summary>
    private const long MaxSingleBytes = 150L * 1024 * 1024;

    /// <summary>残留的中间文件多久算"没人要了"(避免误删正在下的那一个)</summary>
    private static readonly TimeSpan PartGarbageAge = TimeSpan.FromHours(1);

    /// <summary>
    /// 单次"下载 + 合流"的总超时。到点就放弃本地合流、回落远端 DASH ——
    /// 与其让用户对着进度条等到怀疑人生, 不如先按旧体验播起来。
    /// 实测正常情况 2.5 秒左右(18MB), 45 秒已经是很宽的余量。
    /// </summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 合流产物的**格式版本**。只要合流器改了对容器有影响的字段, 这里就必须 +1 ——
    /// 否则用户机器上那些"旧版本合出来的文件"会被继续命中, 修复等于没生效
    /// (这是真踩过的: v1 的 mfhd 序列号没重新编号, 真机表现为跳转花屏 + 音轨不同步,
    ///  修好之后如果不换版本, 已经缓存过的那几条视频仍然会复现同样的症状)。
    /// v1: 首版(分片 MP4, mfhd 序列号没重新编号);
    /// v2: mfhd 序列号重新编号 + elst.segment_duration 补成真实时长;
    /// v3: **改成"展开成普通 MP4"**(完整 stbl 表, 与 B 站自己的单流文件同构) —— 分片 MP4
    ///     在跳转时只能靠解复用器边扫边定位, 真机上表现为花屏 + 音轨不同步。
    /// 带旧版本前缀的文件由 Prune 清掉, 相当于自动重下。
    /// </summary>
    private const string FormatVersion = "v3";

    /// <summary>缓存键: 同一视频的不同清晰度是两份文件(切清晰度要重新合)</summary>
    private static string KeyOf(string bvid, int qn) => $"{FormatVersion}_{bvid}_{qn}";

    /// <summary>取已合流好的本地文件; 没有(或在写入中)返回 null</summary>
    public static string? TryGet(string bvid, int qn)
    {
        try
        {
            if (string.IsNullOrEmpty(bvid) || qn <= 0) return null;
            var path = Path.Combine(Dir, KeyOf(bvid, qn) + ".mp4");
            if (!File.Exists(path)) return null;
            // 长度为 0 的一律当没有: 合流是"先写 .tmp 再改名", 正常不会出现这种文件,
            // 出现就说明被外力打断了, 宁可重下也不要拿它去喂播放器
            if (new FileInfo(path).Length <= 0) return null;
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>该不该为这个片源做本地合流(地址齐全 + 估算体积在预算内)</summary>
    public static bool WorthCaching(DashStreams? spec)
    {
        if (spec == null) return false;
        if (string.IsNullOrEmpty(spec.VideoUrl) || string.IsNullOrEmpty(spec.AudioUrl)) return false;
        var est = spec.EstimatedBytes;
        return est > 0 && est <= MaxSingleBytes;
    }

    /// <summary>
    /// 下载两路 → 合流 → 落到缓存目录。返回最终文件路径; 失败/被取消返回 null。
    /// 调用方在后台跑, 换片/切清晰度/关窗时用 CancellationToken 取消。
    /// </summary>
    /// <param name="progress">下载进度 0~1(按"已下字节 / 按码率估算的总字节"算, 估算有偏差,
    /// 所以到 1 之后还可能要等合流那一下 —— 那步是纯拷贝, 很快)</param>
    public static async Task<string?> PrepareAsync(string bvid, int qn, DashStreams spec,
        CancellationToken ct, IProgress<double>? progress = null)
    {
        if (!WorthCaching(spec)) return null;

        var key = KeyOf(bvid, qn);
        var final = Path.Combine(Dir, key + ".mp4");
        var vPart = Path.Combine(Dir, key + ".v.part");
        var aPart = Path.Combine(Dir, key + ".a.part");

        // 已经有就直接用(含"上一次任务刚写完但播放器还没用上"的情况) ——
        // 不先查一次的话, 每次 Prepare 都会白下几十兆再白合一遍
        var existing = TryGet(bvid, qn);
        if (existing != null)
        {
            progress?.Report(1);
            return existing;
        }

        // 单次准备的总超时。网络半死不活时不能把"起播"卡住太久 ——
        // 到点就放弃这条路, 由调用方回落到远端 DASH(用户至少能看, 只是回到旧体验)
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TotalTimeout);

        // 两路并行下载, 共用同一个字节计数(数组包一层, 好在 lambda 里用 Interlocked)
        var done = new long[1];
        var total = Math.Max(1, spec.EstimatedBytes);

        try
        {
            Directory.CreateDirectory(Dir);

            // 两路常来自不同 CDN 主机, 串行下会白等一个来回 —— 并行
            await Task.WhenAll(
                DownloadAsync(spec.VideoUrl, vPart, timeout.Token, Report),
                DownloadAsync(spec.AudioUrl, aPart, timeout.Token, Report)).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();

            if (!DashRemuxer.TryMerge(vPart, aPart, final, out var err))
            {
                // 合流失败不是"致命错误", 但必须留痕: 这是片源结构变化的第一手线索
                App.ReportError(new InvalidOperationException(
                    $"本地合流失败 bvid={bvid} qn={qn}: {err}"));
                return null;
            }
            progress?.Report(1);
            return final;

            void Report(long delta)
            {
                var d = Interlocked.Add(ref done[0], delta);
                progress?.Report(Math.Min(1.0, d / (double)total));
            }
        }
        catch (OperationCanceledException)
        {
            return null;   // 用户换片/关窗, 或超时放弃 —— 都属正常路径, 不记日志
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return null;
        }
        finally
        {
            TryDelete(vPart);
            TryDelete(aPart);
            TryDelete(final + ".tmp");
            Prune();
        }
    }

    private static async Task DownloadAsync(string url, string path, CancellationToken ct,
        Action<long> onBytes)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // CDN 校验来源页: 不带 Referer 会 403(与播放器给 LibVLC 的请求头保持一致)
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        // 手写拷贝循环(不用 CopyToAsync): 每一块都要回报进度
        var buf = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            onBytes(read);
        }
    }

    /// <summary>
    /// 清理。规则(按顺序): 非法/空文件与过期文件先删 → 再按"最久没动过"删到不超量不超容。
    /// 清理失败一律静默: 它是附加优化, 绝不能影响播放。
    /// </summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var now = DateTime.UtcNow;
            var kept = new List<FileInfo>();

            foreach (var f in new DirectoryInfo(Dir).GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                var isFinal = f.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
                if (!isFinal)
                {
                    // 中间文件只有"够老"才删 —— 可能有一个同键的下载正在进行中
                    if (now - f.LastWriteTimeUtc > PartGarbageAge) TryDelete(f.FullName);
                    continue;
                }
                // 旧格式版本(或来历不明的)一律删掉重下: 见 FormatVersion 的说明
                if (!f.Name.StartsWith(FormatVersion + "_", StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(f.FullName);
                    continue;
                }
                if (f.Length <= 0 || now - f.LastWriteTimeUtc > MaxAge)
                {
                    TryDelete(f.FullName);
                    continue;
                }
                kept.Add(f);
            }

            kept.Sort((x, y) => LastTouch(x).CompareTo(LastTouch(y)));
            long total = 0;
            foreach (var f in kept) total += f.Length;

            var count = kept.Count;
            foreach (var f in kept)
            {
                if (count <= MaxFiles && total <= MaxTotalBytes) break;
                total -= f.Length;
                count--;
                TryDelete(f.FullName);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 当前缓存的规模(文件数 + 总字节), 给设置页显示用。
    /// 只统计**当前格式版本**的文件 —— 旧版本残留会被 Prune 清掉, 把它们算进"占用"里
    /// 会让用户看到"这里 84MB"却怎么清都是 0(它们本来就不该被算作有效缓存)。
    /// </summary>
    public static (int Count, long Bytes) GetStats()
    {
        try
        {
            if (!Directory.Exists(Dir)) return (0, 0);
            var count = 0;
            long bytes = 0;
            foreach (var f in new DirectoryInfo(Dir).GetFiles(FormatVersion + "_*.mp4",
                         SearchOption.TopDirectoryOnly))
            {
                if (f.Length <= 0) continue;
                count++;
                bytes += f.Length;
            }
            return (count, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 清空整个缓存目录(设置页的"清理缓存"按钮)。返回释放的字节数。
    ///
    /// 不用先统计再删: 边删边累加, 顺便把 .part/.tmp 这些中间文件也一起吃掉
    /// (Prune 平时只删"够老"的中间文件, 但用户主动清空时没有"正在下"的顾虑之外的顾虑了)。
    ///
    /// 两个会被静默跳过的情形, 都是故意的:
    ///   · 正在下载的 .part 被 FileShare.None 占用 → 删不掉, 留下(它属于那次下载, 不是垃圾);
    ///   · 正在播放的 .mp4 被 LibVLC 占用 → 删不掉, 留下(下次启动 Prune 会收拾)。
    /// 所以"清理后仍显示几 MB"是正常的, 不是没清干净。
    /// </summary>
    public static long ClearAll()
    {
        try
        {
            var di = new DirectoryInfo(Dir);
            if (!di.Exists) return 0;
            long freed = 0;
            foreach (var f in di.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                var size = f.Length;
                if (!TryDelete(f.FullName)) continue;
                freed += size;
            }
            return freed;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>最近一次"被碰到"的时间。优先用访问时间(真正在看的会被刷新), 取不到就退回修改时间</summary>
    private static DateTime LastTouch(FileInfo f) =>
        f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc;

    /// <summary>删除成功返回 true(文件本来就不存在也算成功)</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
