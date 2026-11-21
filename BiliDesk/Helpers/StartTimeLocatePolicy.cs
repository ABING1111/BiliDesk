namespace BiliDesk.Helpers;

/// <summary>
/// 重建后"<c>:start-time</c> 到底有没有定位成功"的判定, 以及该怎么补救(2026-11-20)。
///
/// ★★★ 为什么需要: 用户报「长视频拖进度条回到开头, 时好时坏」。
///   根因不在线路也不在 seek 本身, 而在**重建路径的定位方式**:
///   为了避开"起播后再设 Time 会打断解复用器"的窗口, 重建时把目标交给 Media 的
///   <c>:start-time</c>。但在换过 host 的 fMP4 流上, 这一步有相当比例会**静默失效** ——
///   画面起来了, 位置却恒为 0(libvlc 原生日志 `stream 0 ... partial file`),
///   随后立刻 EOF, 应用据此判"没落地", 预算用尽就从头起播。
///
///   实测(见 .probes/bd-probe-vlcseek, 同一份签名只换视频 host, 5 个目标位置):
///     mirrorali 5/5、mirrorhw 5/5、mirrorbd 5/5、mirrorcos 3~5/5,
///     **mirroraliov 0/5、estgoss 0/5**。
///   ⇒ 当次选到哪条线路, 决定了这次拖进度条灵不灵 —— 这就是"时好时坏"。
///
/// ★ 为什么补救是"换个定位方式"而不是"换条线路":
///   同一条路再走一次只会同样失败; 实测换成"正常起播 + 起播后设 Time"
///   在上述失败线路上能成功。所以预算只给一次, 用完就如实停在失败态。
///
/// ★ 为什么抽成纯函数: 判据里全是"位置/目标/预算"的比较, 真机上要复现
///   "这一次灵、下一次不灵"得靠运气; 放进单测才能把边界穷举。
/// </summary>
public static class StartTimeLocatePolicy
{
    /// <summary>
    /// 位置还停在片头多久之内算"没定位"。单位毫秒。
    ///
    /// ★ 取 15s: 定位成功时实测落点偏差是 1~4s(重建要重新 open 一遍),
    ///   取 15s 不会被正常抖动误判; 而任何有意义的跳转都远大于它
    ///   (用户拖进度条的典型距离是分钟级)。
    /// </summary>
    public const long MissToleranceMs = 15_000;

    /// <summary>
    /// 这次 EOF 是不是"<c>:start-time</c> 没定位成功"造成的, 该不该换方式重试。
    /// </summary>
    /// <param name="rebuiltViaStartTime">当前这条 input 是用 <c>:start-time</c> 重建的</param>
    /// <param name="alreadyRetried">本次播放已经换过一次方式(预算只有一次)</param>
    /// <param name="lastSeekTargetMs">最近一次 seek 的目标(-1 = 本片还没 seek 过)</param>
    /// <param name="lastKnownMs">EOF 时的最后已知位置</param>
    /// <param name="isLive">直播没有"重建到某位置"这回事</param>
    /// <param name="isLocal">本地文件不走 <c>:start-time</c>, 另有一条路</param>
    /// <param name="hasSource">手里还有可用的播放地址</param>
    public static bool ShouldRetryWithoutStartTime(
        bool rebuiltViaStartTime,
        bool alreadyRetried,
        long lastSeekTargetMs,
        long lastKnownMs,
        bool isLive,
        bool isLocal,
        bool hasSource)
    {
        if (!rebuiltViaStartTime) return false;
        if (alreadyRetried) return false;
        if (isLive || isLocal) return false;
        if (!hasSource) return false;

        // 没 seek 过就没有"定位"可言: 这种 EOF 该由 PlaybackEndPolicy 按"提前结束"处理
        if (lastSeekTargetMs <= 0) return false;

        // 目标本身就在片头附近: 停在 0 是**正确结果**, 不是失败
        // (否则"拖到第 3 秒"会被误判成定位失败, 白重建一次)
        if (lastSeekTargetMs <= MissToleranceMs) return false;

        return lastKnownMs < MissToleranceMs;
    }
}
