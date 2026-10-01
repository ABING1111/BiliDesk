using System;
using System.Threading.Tasks;

namespace BiliDesk.Services;

/// <summary>
/// 观看进度上报服务: 把本地播放的进度自动同步到 B 站云端历史。
///
/// 为什么需要节流:
/// 播放器每 500ms 刷一次进度条, 如果每次都上报, 一个小时的视频会产生上万个请求,
/// 必然触发风控(-352)甚至封禁。这里的原则是:
///   - 起播后**立刻报一次**(让视频尽快出现在云端"最近观看"里);
///   - 播放中每 ReportIntervalSeconds 秒报一次;
///   - 暂停 / 关闭窗口时**补报一次**(保证结束进度被记录, 便于网页端续播);
///   - 同一个 cid 的相邻上报去重(避免暂停后反复触发)。
///
/// 未登录 / 接口失败都静默忽略 —— 上报失败绝不能影响本地播放体验。
/// </summary>
public class HistorySyncService
{
    public static HistorySyncService Instance { get; } = new();

    /// <summary>播放中周期上报的间隔(秒)。15 秒是"进度足够准"与"请求足够少"的折中。</summary>
    private const int ReportIntervalSeconds = 15;

    /// <summary>
    /// 进度到多少秒就允许上报。
    ///
    /// 这里取 3 秒而不是更大值: 用户可能只点开看两眼就跑, 也可能很快拖到中段。
    /// 门槛设太高(比如 5 秒)时, 这类"短观看"会完全静默丢失, 用户感知就是"同步没生效"。
    /// 3 秒足以过滤掉"误点一下立刻关"的噪声, 又不会漏掉真实观看。
    /// </summary>
    private const int MinProgressSeconds = 3;

    private long _lastAid;
    private long _lastCid;
    private long _lastReportedProgress;
    private DateTime _lastReportAt = DateTime.MinValue;

    /// <summary>
    /// 是否应该在这一刻上报。
    /// 由播放器在进度刷新时调用, 判断逻辑集中在这里, 播放器只管"到点就报"。
    /// </summary>
    public bool ShouldReport(long aid, long cid, long progressSeconds)
    {
        if (aid <= 0 || cid <= 0) return false;
        if (progressSeconds < MinProgressSeconds) return false;

        // 换了视频: 立刻允许上报(新视频的第一条记录很重要)
        if (aid != _lastAid || cid != _lastCid) return true;

        // 同一视频: 间隔不够就跳过
        if ((DateTime.UtcNow - _lastReportAt).TotalSeconds < ReportIntervalSeconds) return false;

        // 进度几乎没动(暂停中)就不重复报。
        // 注意阈值要明显小于 ReportIntervalSeconds: 正常播放时 15 秒至少推进 15 秒,
        // 所以这条只在"卡住/缓冲/反复暂停"时才会拦下来, 不会误杀正常的周期上报。
        if (Math.Abs(progressSeconds - _lastReportedProgress) < 2) return false;

        return true;
    }

    /// <summary>
    /// 上报进度(内部会先做去重判断)。返回是否真的发起了请求。
    /// </summary>
    public async Task<bool> ReportAsync(long aid, long cid, long progressSeconds)
    {
        if (!ShouldReport(aid, cid, progressSeconds)) return false;

        // 先记录状态再发请求: 即使请求失败也不需要重试(下一次周期上报自然会补上),
        // 但可以避免"请求还没回来就又触发一次"的并发重复上报。
        _lastAid = aid;
        _lastCid = cid;
        _lastReportedProgress = progressSeconds;
        _lastReportAt = DateTime.UtcNow;

        var (ok, _) = await Svc.Api.ReportHistoryAsync(aid, cid, progressSeconds);
        return ok;
    }

    /// <summary>
    /// 强制上报(暂停 / 关闭窗口时用), 跳过间隔与进度差判断。
    /// 但"换了视频"和"进度太小"这两个安全判断仍然生效。
    /// </summary>
    public async Task<bool> ReportNowAsync(long aid, long cid, long progressSeconds)
    {
        if (aid <= 0 || cid <= 0) return false;
        if (progressSeconds < MinProgressSeconds) return false;

        _lastAid = aid;
        _lastCid = cid;
        _lastReportedProgress = progressSeconds;
        _lastReportAt = DateTime.UtcNow;

        var (ok, _) = await Svc.Api.ReportHistoryAsync(aid, cid, progressSeconds);
        return ok;
    }

    /// <summary>切换视频时重置节流状态, 让新视频的第一条进度能立刻上报</summary>
    public void Reset()
    {
        _lastAid = 0;
        _lastCid = 0;
        _lastReportedProgress = 0;
        _lastReportAt = DateTime.MinValue;
    }
}
