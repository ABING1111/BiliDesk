using System;

namespace BiliDesk.Services;

/// <summary>
/// 「续播位置」与「片尾」的统一判定规则(2026-11-19, issue #88 / #93)。
///
/// ★ 为什么抽成纯静态类:
///   同一套规则原来散落在三处 —— <see cref="PlaybackProgressService"/> 判断"这条记录还能不能续"、
///   播放器 <c>Playing</c> 事件里直接设 <c>_mp.Time</c>、<c>OnEndReached</c> 判断"是不是提前结束"。
///   三处阈值各写一份, 必然出现"进度服务认为还能续、播放器却已经 seek 到片尾"的错配:
///   长视频从历史记录打开时直接跳到最后一个分片触发假 EOF, 表现就是"一打开就提示已播完"。
///   抽出来后既只有一份口径, 也能进单测(见 BiliDesk.Tests/ResumePolicyTests)。
///
/// ★ 三个量分别是什么:
///   · <see cref="MinResumeMs"/>  —— 太靠片头的记录不值得续(点开就关掉的噪声);
///   · <see cref="TailGuardMs"/>  —— 距结尾多少毫秒内算"已看完", 同时作为 seek 的安全边界;
///   · <see cref="CompletedRatio"/> —— 接口时长与真实流长有偏差时的兜底比例(超过 98% 算看完)。
/// </summary>
public static class ResumePolicy
{
    /// <summary>低于这个位置不续播(刚点开就关掉的噪声)。</summary>
    public const long MinResumeMs = 5_000;

    /// <summary>
    /// 距结尾不足这个值就不续播/直接算看完, 避免下次一打开就播完。
    /// ★ 同时也是 seek 目标的上界: 绝不把播放位置设进最后这段"尾部静默区" ——
    ///   fMP4 的最后分片在 libvlc 上最容易出"seek 进最后一段就立刻 EOF"的问题。
    /// </summary>
    public const long TailGuardMs = 10_000;

    /// <summary>超过总时长的这个比例一律算看完(接口时长偏短/偏长时的兜底)。</summary>
    public const double CompletedRatio = 0.98;

    /// <summary>
    /// 这个位置是否值得续播。
    /// </summary>
    /// <param name="posMs">上次看到的位置(毫秒)</param>
    /// <param name="durationMs">总时长(毫秒; 0 = 还不知道, 此时只排除太靠前的位置)</param>
    public static bool IsResumable(long posMs, long durationMs)
    {
        if (posMs < MinResumeMs) return false;
        if (durationMs <= 0) return true;
        if (posMs >= durationMs - TailGuardMs) return false;
        return posMs <= durationMs * CompletedRatio;
    }

    /// <summary>这个位置是否已经算"看完"(与 <see cref="IsResumable"/> 互补)。</summary>
    public static bool IsCompleted(long posMs, long durationMs) => !IsResumable(posMs, durationMs);

    /// <summary>
    /// 把续播位置夹到安全区间内。-1/非法值归零, 上界为"总时长 - 尾部长"。
    /// ★ 只在调用方已经知道时长时才有意义; 时长未知时原样返回(不猜)。
    /// </summary>
    public static long ClampResumeMs(long posMs, long durationMs)
    {
        if (posMs <= 0) return 0;
        if (durationMs <= 0) return posMs;
        var upper = Math.Max(0, durationMs - TailGuardMs);
        return Math.Min(posMs, upper);
    }

    /// <summary>
    /// EOF 此刻的位置是否**真的**在片尾。
    ///
    /// ★ 这是"能不能显示已播完"的最后一道闸: 只有这里返回 true, 播放器才允许进入
    ///   "已播完 + 重播/自动连播"态; 位置还在中间时一律按"假 EOF / 流被截断"处理,
    ///   走重建、续播或失败重试, 绝不告诉用户"片子看完了"。
    /// ★ 时长未知(0)时只能相信 EOF 本身(老行为); 位置为 0 且时长已知时视为"没有可信位置",
    ///   不判片尾 —— 那种情况多半是重建后还没来得及产生第一个时间戳。
    /// </summary>
    public static bool IsNearEnd(long posMs, long durationMs)
    {
        if (durationMs <= 0) return true;
        return posMs > 0 && posMs >= durationMs - TailGuardMs;
    }
}
