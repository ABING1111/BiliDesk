namespace BiliDesk.Services;

/// <summary>收到 EOF 时该做什么(见 <see cref="PlaybackEndPolicy"/>)。</summary>
public enum PlaybackEndAction
{
    /// <summary>真的播完了: 进入「已播完」界面(重播 / 自动连播)。</summary>
    Finished,

    /// <summary>疑似 seek 假 EOF: 按原目标重建一次 Media 再跳过去。</summary>
    RebuildSeek,

    /// <summary>远端流提前结束: 从最后已知位置重建 input 继续播。</summary>
    ResumeFromPosition,

    /// <summary>怎么救都接不上: 兜底从头起播一次(用户至少还能看)。</summary>
    RestartFromStart,

    /// <summary>彻底救不回来: 显示失败遮罩 + 重试入口, 绝不显示「已播完」。</summary>
    Fail,
}

/// <summary>
/// 「这次 EOF 到底是不是播完了」的决策规则(2026-11-19, issue #88 / #93)。
///
/// ★ 为什么必须有:
///   issue #88「拖动进度条后显示缓冲中, 然后直接跳到最后并显示已播完」和
///   issue #93「历史记录打开长视频提示已播完, 拖动进度条又提示已播完」是同一个根因 ——
///   seek 失败/远跳落在流尾时 libvlc 同样会发 EndReached, 老代码只要"重建预算用完"
///   就把它当成播完。用户面对的是"明明还有一大半, 却告诉我看完了"。
///
/// ★ 规则(顺序即优先级):
///   1. 位置真的在片尾(或时长未知) → 播完;
///   2. 没有可重建的源 → 失败(不要把播完当兜底);
///   3. 有未落地的 seek → 先重建到目标; 已经重建过就再兜底从头起播;
///   4. 普通提前 EOF → 从最后位置续播(有次数上限);
///   5. 以上都不行 → 从头起播一次; 还不行 → 失败态。
///   **任何一条路径都不会在"位置还在中间"时返回 <see cref="PlaybackEndAction.Finished"/>。**
///
/// ★ 为什么是纯逻辑类: 与 WPF / LibVLC 无关, 可以直接把各种"pos/length/预算"
///   喂进单测(见 BiliDesk.Tests/PlaybackEndPolicyTests), 不用真的等一次 EOF。
/// </summary>
public static class PlaybackEndPolicy
{
    /// <param name="posMs">EOF 时的最后已知位置(毫秒)</param>
    /// <param name="durationMs">真实总时长(毫秒; 0 = 未知)</param>
    /// <param name="isLive">直播没有"续播"这回事, EOF 就是这一场结束</param>
    /// <param name="seekPending">还有一次 seek 在等确认(尚未落地)</param>
    /// <param name="seekLooksUnlanded">看门狗已放弃, 但位置离最近一次 seek 目标还很远</param>
    /// <param name="seekRebuildTried">这次 seek 已经用过"重建到目标"的预算</param>
    /// <param name="canResume">从当前位置续播是否还有意义(位置有效且确实推进过)</param>
    /// <param name="resumeCount">本次播放已经续播过几次</param>
    /// <param name="resumeMax">续播次数上限(防"重建 → 假 EOF → 再重建"死循环)</param>
    /// <param name="restartFromStartTried">已经兜底从头起播过一次</param>
    /// <param name="hasPlayableSource">手里还有可用的播放地址/本地文件</param>
    public static PlaybackEndAction Decide(
        long posMs,
        long durationMs,
        bool isLive,
        bool seekPending,
        bool seekLooksUnlanded,
        bool seekRebuildTried,
        bool canResume,
        int resumeCount,
        int resumeMax,
        bool restartFromStartTried,
        bool hasPlayableSource)
    {
        // 1) 直播 / 位置确实在片尾 / 时长未知: 这就是正常的结束。
        if (isLive || ResumePolicy.IsNearEnd(posMs, durationMs))
            return PlaybackEndAction.Finished;

        // 2) 地址都没了, 任何"重建"都是空转; 也不能借机显示播完。
        if (!hasPlayableSource)
            return PlaybackEndAction.Fail;

        // 3) 跳转没落地: 先按目标重建(用户明确要去的位置), 只给一次预算。
        if (seekPending || seekLooksUnlanded)
        {
            if (!seekRebuildTried) return PlaybackEndAction.RebuildSeek;
            return restartFromStartTried ? PlaybackEndAction.Fail : PlaybackEndAction.RestartFromStart;
        }

        // 4) 普通提前 EOF: 从最后已知位置续播(次数有上限)。
        if (canResume && resumeCount < resumeMax)
            return PlaybackEndAction.ResumeFromPosition;

        // 5) 续播也接不上: 兜底从头起播一次。
        return restartFromStartTried ? PlaybackEndAction.Fail : PlaybackEndAction.RestartFromStart;
    }
}
