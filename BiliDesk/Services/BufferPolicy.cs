using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliDesk.Services;

/// <summary>这条媒体属于哪一类播放源(决定缓冲策略走哪条规则)</summary>
public enum BufferMediaKind
{
    /// <summary>DASH 双流(视频 + input-slave 挂音轨)</summary>
    VodDash,

    /// <summary>durl 单流(音视频合一的远端 MP4)</summary>
    VodSingle,

    /// <summary>直播(持续流, 没有"预读完了就播"的概念)</summary>
    Live,
}

/// <summary>
/// 网络缓冲(:network-caching)的统一决策点(2026-11-01 新增)。
///
/// ★ 为什么要有它: 之前缓冲值散在三处、各写各的 ——
///   · 点播: <c>BuildMedia</c> 加媒体级 <c>:network-caching=用户设置</c>;
///   · 直播: <c>PlayLive</c> 硬编码 2000(与设置页完全脱钩);
///   · 全局: <c>VlcCore</c> 用用户设置兜底(没有媒体选项的路径)。
///   三处迟早漂移, 而且都没有"按网络状况挑值"的能力。现在只留这一个出口。
///
/// 两条规则(与设置页「缓冲策略」对应):
///   · **手动**: 恒用用户设置的 NetworkCachingMs —— 与 2026-10-30 之前的旧行为一致,
///     用户特意调过缓冲的切到手动即可完全回到旧行为。直播仍保底 1000
///     (持续流被过早判定"卡住/结束"的代价远大于多等 0.7 秒缓冲)。
///   · **自动**(默认): 按最近一轮 CDN 实测吞吐(CdnService.LastMeasuredBytesPerSecond)
///     与流码率的比值挑档 —— 带宽越富余预读越浅(省内存、低延迟), 越紧张越深(抗抖动)。
///     没测过(0)用 1000 兜底, 与旧默认一致。
///
/// 取值范围与设置页的夹取范围一致(300~3000, 见 SettingsStore.NetworkCachingMin/Max),
/// 自动档只是在这个范围内"挑点", 不会越界。
///
/// ★ 为什么默认开自动: 实测(探针 bd-probe-startcfix)缓冲值对**起播**几乎无影响
///   (缓存按网速灌), 真正影响的是**播放中的抗抖动余量** —— 这恰恰是"按带宽挑值"
///   最有价值的地方。风险面: 最坏情况也就是回到手动档的某个固定值。
/// </summary>
public static class BufferPolicy
{
    /// <summary>自动档假设的流码率。DASH 常见 1080P ≈ 2~3Mbps, 取中值; durl 单流略低</summary>
    private const long AssumedDashBitrateBps = 2_500_000;
    private const long AssumedSingleBitrateBps = 2_000_000;

    /// <summary>"极短片"的时长上限 —— 与 ApiClient.ShortVideoRemuxThresholdMs 对齐(60s)。
    /// 短片预读太深没有意义(还没缓冲满就播完了), 自动档给它封个顶。</summary>
    private const long ShortVideoMaxMs = 60_000;
    private const int ShortVideoCapMs = 1000;

    /// <summary>直播的自动档值。与 PlayLive 旧硬编码一致(2020ms 那套的抗抖动余量)</summary>
    private const int LiveAutoMs = 2000;

    /// <summary>手动模式下直播的保底: 低于它的手动值对持续流太激进</summary>
    private const int LiveManualFloorMs = 1000;

    /// <summary>
    /// 算这条媒体该用的 network-caching(毫秒)。
    /// </summary>
    /// <param name="kind">播放源类型</param>
    /// <param name="mediaUrl">媒体地址(只用来提取 host 做卡顿归因; 本地路径传什么都行)</param>
    /// <param name="shortVideo">是否极短片(≤60s)。判据由调用方给(它有时长上下文, 这里没有)</param>
    /// <param name="userMs">用户设置的手动值(SettingsStore.NetworkCachingMs, 已夹取)</param>
    /// <param name="auto">是否自动档(设置页「缓冲策略」)</param>
    public static int Resolve(BufferMediaKind kind, string? mediaUrl, bool shortVideo,
        int userMs, bool auto)
    {
        if (!auto)
        {
            return kind == BufferMediaKind.Live
                ? Math.Max(userMs, LiveManualFloorMs)
                : Math.Clamp(userMs, SettingsStore.NetworkCachingMinMs, SettingsStore.NetworkCachingMaxMs);
        }

        if (kind == BufferMediaKind.Live)
            return Math.Clamp(LiveAutoMs + StallBoostMs(mediaUrl),
                SettingsStore.NetworkCachingMinMs, SettingsStore.NetworkCachingMaxMs);

        // 自动点播档: 实测吞吐 / 假设码率。比值含义是"带宽能同时拉几路这条流":
        //   ≥10 路 → 随便缓冲, 500ms 足够扛抖动;
        //   3~10 路 → 与旧默认一致, 1000ms;
        //   1.5~3 路 → 带宽刚够, 多备一点, 2000ms;
        //   <1.5 路 → 弱网兜底, 3000ms(再深 LibVLC 也只是白占内存, 不再往上加)。
        var measured = CdnService.Instance.LastMeasuredBytesPerSecond;
        int baseMs;
        if (measured <= 0)
        {
            baseMs = 1000;   // 没测过: 与旧默认一致, 别瞎猜
        }
        else
        {
            var bitrate = kind == BufferMediaKind.VodDash
                ? (double)AssumedDashBitrateBps : AssumedSingleBitrateBps;
            var ratio = measured * 8.0 / bitrate;
            baseMs = ratio >= 10 ? 500
                   : ratio >= 3 ? 1000
                   : ratio >= 1.5 ? 2000
                   : 3000;
        }

        // 极短片: 预读超过 1 秒大概率白等(还没缓冲满就播完了)
        if (shortVideo) baseMs = Math.Min(baseMs, ShortVideoCapMs);

        return Math.Clamp(baseMs + StallBoostMs(mediaUrl),
            SettingsStore.NetworkCachingMinMs, SettingsStore.NetworkCachingMaxMs);
    }

    // ------------------------------------------------------------ 卡顿自愈
    //
    // 同一条 CDN 线路短时间内反复卡顿 ⇒ 它此刻的抖动比测速时严重 ⇒ 给**后续**媒体
    // 自动加深 1000ms 预读。只影响下一条视频/下一次重建 Media, **绝不换当前在播的源**
    // —— 换源曾经踩过"跳转花屏 + 音画不同步"的坑(见 ShortVideoCache 顶注), 不再碰。

    private static readonly object StallGate = new();
    private static readonly Dictionary<string, List<long>> StallTimes = new(StringComparer.Ordinal);

    /// <summary>卡顿记录的时间窗: 60 秒内的卡顿才参与计数(再久就是另一次网络状态了)</summary>
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(60);

    /// <summary>窗口内卡顿几次才触发加深(1 次可能是偶发抖动, 2 次才像线路劣化)</summary>
    private const int StallCountToBoost = 2;

    /// <summary>触发后的预读加深量(毫秒)</summary>
    private const int StallBoostValueMs = 1000;

    /// <summary>每个 host 最多记几条(防"每 500ms 卡一次"把列表撑大)</summary>
    private const int StallRecordCap = 8;

    /// <summary>
    /// 上报一次"播放中途卡顿"。由播放器的卡顿检测(UpdateStallDetector)在弹出缓冲遮罩时调,
    /// 按 host 归因 —— 换 host 就换 CDN, 同一家 CDN 的抖动才该累加在一起。
    /// 只对点播生效(直播/本地文件不进卡顿检测), 这里不必再判。
    /// </summary>
    public static void ReportStall(string? mediaUrl)
    {
        var host = HostOf(mediaUrl);
        if (host.Length == 0) return;
        var now = Environment.TickCount64;
        lock (StallGate)
        {
            if (!StallTimes.TryGetValue(host, out var list))
            {
                list = new List<long>();
                StallTimes[host] = list;
            }
            list.Add(now);
            list.RemoveAll(t => now - t > StallWindow.TotalMilliseconds);
            if (list.Count > StallRecordCap)
                list.RemoveRange(0, list.Count - StallRecordCap);
        }
    }

    /// <summary>这条媒体要不要加深预读(加深多少毫秒)。命中条件见 StallCountToBoost</summary>
    public static int StallBoostMs(string? mediaUrl)
    {
        var host = HostOf(mediaUrl);
        if (host.Length == 0) return 0;
        var now = Environment.TickCount64;
        lock (StallGate)
        {
            if (!StallTimes.TryGetValue(host, out var list)) return 0;
            var recent = list.Count(t => now - t <= StallWindow.TotalMilliseconds);
            return recent >= StallCountToBoost ? StallBoostValueMs : 0;
        }
    }

    /// <summary>从媒体地址提取 host(非 http / 解析失败都返回空串, 调用方按"无 host"处理)</summary>
    private static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url) ||
            !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "";
        try { return new Uri(url).Host; }
        catch { return ""; }
    }
}
