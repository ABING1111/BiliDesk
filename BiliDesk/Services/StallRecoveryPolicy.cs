using System;

namespace BiliDesk.Services;

/// <summary>卡顿自愈此刻该做什么(见 <see cref="StallRecoveryPolicy"/>)</summary>
public enum StallRecoveryAction
{
    /// <summary>什么都不用做(位置正常 / 还没到阈值 / 正在退避等待)</summary>
    None,

    /// <summary>卡太久了: 把遮罩文案换成"网络卡顿，正在重连…"(还不动播放器)</summary>
    ShowHint,

    /// <summary>该重建 input 了(等价于用户手动切一下听视频/视频模式)</summary>
    Rebuild,

    /// <summary>重建预算用完仍然卡着: 收手, 交给用户点「重试」</summary>
    GiveUp,
}

/// <summary>
/// "播放中途卡住"的自愈决策(2026-11-14 新增, issue #81)。
///
/// ★ 为什么需要它:
///   用户的原始报告是"长时间播放/听视频后卡在『正在缓冲』, 切一下听视频又好了"。
///   排查结论: 真正卡住的是 LibVLC 那条 HTTP/fMP4 input(长连接被 CDN 静默掐断后
///   读线程永久阻塞), 而播放器对"正在缓冲"**只弹遮罩、从不重建 input** ——
///   所以只有用户手动切模式(内部是 `Stop()` + 新 `Media`)才能恢复。
///   这个类就是把那套手动操作自动化: 位置冻结够久 → 重建一次 input。
///
/// ★ 为什么是纯逻辑类: 阈值/退避/上限都是"看着心跳数字做决定", 与 WPF、LibVLC
///   都无关。抽出来才能进单测(见 BiliDesk.Tests/StallRecoveryPolicyTests),
///   也才不会和 seek 看门狗、提前 EOF 自救那两套状态机在前台代码里缠成一团。
///
/// 决策时间轴(全都是"位置冻结"的累计时长):
///   0s ─ 6s   什么都不做(正常抖动不该救)
///   6s        换文案提示"正在重连…"
///   10s       第 1 次重建(起一条全新 input, 从当前播放位置接上)
///   之后      按 8s / 15s / 25s 退避再试, 最多 3 次
///   仍失败    收手(GiveUp), 让 UI 给出「重试」入口, 不再无限转圈
///
/// ★ 位置一旦推进就 <see cref="OnAdvance"/> 全部归零: 卡顿结束了, 重建预算也该还回去。
/// </summary>
public sealed class StallRecoveryPolicy
{
    /// <summary>卡多久之后开始提示"正在重连…"</summary>
    public const long HintAfterMs = 6_000;

    /// <summary>卡多久之后才真正重建 input(第一次)。必须明显大于普通网络抖动, 否则会误伤。</summary>
    public const long RebuildAfterMs = 10_000;

    /// <summary>整段卡顿最多重建几次(每次都是一次完整重新拉流)</summary>
    public const int MaxRebuilds = 3;

    /// <summary>两次重建之间的退避(第 1/2/3 次之后)。给新 input 留出起播时间。</summary>
    private static readonly long[] BackoffMs = { 8_000, 15_000, 25_000 };

    /// <summary>本次"卡住"从哪一刻开始(TickCount64, -1 = 当前没卡住)</summary>
    private long _frozenSince = -1;

    /// <summary>下一次允许重建的时刻(TickCount64)</summary>
    private long _nextAttemptAt = long.MaxValue;

    /// <summary>本段卡顿里已经重建过几次</summary>
    private int _rebuilds;

    /// <summary>已经放弃(等待用户手动重试)</summary>
    private bool _gaveUp;

    /// <summary>本段卡顿里已经重建过几次(写诊断日志用)</summary>
    public int Rebuilds => _rebuilds;

    /// <summary>是否已经放弃自动重连</summary>
    public bool GaveUp => _gaveUp;

    /// <summary>当前是否处于"位置冻结"阶段</summary>
    public bool IsFrozen => _frozenSince >= 0;

    /// <summary>彻底归零(换片 / 重播 / 换清晰度等"这是一条新 input"的路径)</summary>
    public void Reset()
    {
        _frozenSince = -1;
        _nextAttemptAt = long.MaxValue;
        _rebuilds = 0;
        _gaveUp = false;
    }

    /// <summary>位置推进了: 卡顿结束, 重建预算一并作废(下次卡顿重新拥有 3 次机会)</summary>
    public void OnAdvance() => Reset();

    /// <summary>位置不再冻结(暂停 / seek / 播完等): 只清"冻结起点", 保留重建预算与退避。</summary>
    /// <remarks>
    /// ★ 为什么不能顺手 <see cref="Reset"/>: 重建后 LibVLC 会先进入 Opening/Buffering,
    ///   那几秒位置本来就不动, 调用方会走这个分支。如果这里把预算也清了, 就会
    ///   "重建 → 归零 → 再重建"无限循环 —— 正是 _seekRebuildTried 那条注释里踩过的坑。
    /// </remarks>
    public void OnUnfrozen() => _frozenSince = -1;

    /// <summary>
    /// 心跳调用一次。返回此刻该执行的动作。
    /// </summary>
    /// <param name="nowMs">当前时刻(Environment.TickCount64)</param>
    /// <param name="frozen">播放位置是否已经停住(由调用方按 _mp.Time 判定)</param>
    /// <param name="recoverable">此刻是否**允许**动播放器(不在拖动/seek 等待/重建途中, 且播放器确实在播)</param>
    public StallRecoveryAction Tick(long nowMs, bool frozen, bool recoverable)
    {
        if (_gaveUp) return StallRecoveryAction.None;

        if (!frozen)
        {
            _frozenSince = -1;
            return StallRecoveryAction.None;
        }

        if (_frozenSince < 0) _frozenSince = nowMs;
        var age = nowMs - _frozenSince;

        // ---- 已经重建过: 只按退避时间等, 不再看 RebuildAfterMs ----
        // (重建后的起播时间由退避覆盖, 再叠一层 10s 只会把重试拖长, 没有意义)
        if (_rebuilds > 0)
        {
            if (nowMs < _nextAttemptAt) return StallRecoveryAction.None;
            // 退避到点了, 但此刻不能动播放器(用户切走了/暂停了): 继续等, 别消耗预算。
            if (!recoverable) return StallRecoveryAction.None;
            if (_rebuilds >= MaxRebuilds)
            {
                _gaveUp = true;
                return StallRecoveryAction.GiveUp;
            }

            _rebuilds++;
            _frozenSince = nowMs;
            _nextAttemptAt = nowMs + BackoffMs[Math.Min(_rebuilds - 1, BackoffMs.Length - 1)];
            return StallRecoveryAction.Rebuild;
        }

        // ---- 本段卡顿的第一次 ----
        if (age < HintAfterMs) return StallRecoveryAction.None;
        if (!recoverable) return StallRecoveryAction.ShowHint;
        if (age < RebuildAfterMs) return StallRecoveryAction.ShowHint;

        _rebuilds = 1;
        // 冻结起点前移: 新 input 需要一段起播时间, 别在它还没起来时又判"还在卡"。
        _frozenSince = nowMs;
        _nextAttemptAt = nowMs + BackoffMs[0];
        return StallRecoveryAction.Rebuild;
    }
}
