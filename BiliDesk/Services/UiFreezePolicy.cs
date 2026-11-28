using System;

namespace BiliDesk.Services;

/// <summary>UI 线程冻结时看门狗该做什么(见 <see cref="UiFreezePolicy"/>)</summary>
public enum UiFreezeAction
{
    /// <summary>一切正常(或者已经报告过了, 别刷屏)</summary>
    None,

    /// <summary>确认冻结: 把现场写进日志</summary>
    Report,
}

/// <summary>
/// "UI 线程卡住"的判定(2026-11-28 新增)。
///
/// ★ 背景: 用户报"窗口冻住 → 进程消失"。这类问题有两种形态:
///   ① **原生崩溃**(libvlc 里的 0xc0000005)—— 进程直接没了, 我们的代码根本没机会跑;
///      那一类靠崩溃转储抓(见 <see cref="CrashDumpService"/>)。
///   ② **UI 线程死锁/长时间阻塞** —— 窗口还在, 但点了没反应, 用户只能强杀。
///      进程被强杀时, .NET 的异常处理器统统没机会跑, errors.txt 里同样是空的。
///   ②这一类只能靠"**从别的线程**上看出来": 有个后台线程定期问 UI 线程一句
///      "在吗", 问回来花的时间就是 UI 线程被卡住的时长。
///
/// ★ 为什么是纯逻辑类:
///   阈值、锁存(同一场冻结只报一次)、恢复判定都是"看着数字做决定", 与 WPF 无关,
///   抽出来才能单测(真冻住 UI 线程没法在测试里复现)。
///
/// ★★★ 判定模型(2026-11-28 探针实测后定的, 别改回"一次问到底"的写法):
///   看门狗往 UI 线程派一个空操作, 然后**分段等**它回来:
///     · 每 200ms 醒一次, 醒来看还没回来 ⇒ 累计"未响应时长";
///     · 累计时长越过阈值 ⇒ 判定冻结, **当场**报告(那时 UI 线程还卡着,
///       "当时在做的那件事"的标记还在, 现场才是真的);
///     · 操作终于完成 ⇒ 冻结结束, 解锁, 让下一场冻结能重新报。
///   ⇒ 因此这里的入参是**累计未响应时长**(不是某一趟的往返耗时),
///     解锁必须走 <see cref="OnResponsive"/>, 不能靠"传了个小数字"顺带解 ——
///     否则探测轮次一换(每轮最多等一分钟)锁存就被误清, 一次长冻结会报好几遍。
/// </summary>
public sealed class UiFreezePolicy
{
    /// <summary>累计未响应超过这个值(毫秒)就认定 UI 线程被卡住。</summary>
    public const long DefaultThresholdMs = 5_000;

    private readonly long _thresholdMs;
    private bool _reported;

    public UiFreezePolicy(long thresholdMs = DefaultThresholdMs)
        => _thresholdMs = thresholdMs > 0 ? thresholdMs : DefaultThresholdMs;

    /// <summary>当前是否已经报告过这一场冻结</summary>
    public bool Reported => _reported;

    /// <summary>
    /// 观察一次采样: UI 线程**还没**响应, 累计未响应 <paramref name="unresponsiveMs"/> 毫秒。
    /// </summary>
    /// <param name="unresponsiveMs">从派发那次操作起, 累计等了多久还没回来</param>
    public UiFreezeAction Tick(long unresponsiveMs)
    {
        if (unresponsiveMs < _thresholdMs) return UiFreezeAction.None;

        // 已经报过了就别再报: 冻住 30 秒不该在日志里写 30 条。
        if (_reported) return UiFreezeAction.None;

        _reported = true;
        return UiFreezeAction.Report;
    }

    /// <summary>
    /// UI 线程响应了 —— 冻结结束, 解锁。
    ///
    /// ★ 必须解锁: 否则用户一天里卡三次, 只有第一次会被记录下来 ——
    ///   而"第几次卡"恰恰是判断是否稳定复现的关键信息。
    /// ★ 只在**操作真的完成了**时调(看门狗知道这件事); 不能用"传了个小数字给 Tick"
    ///   顺带解锁 —— 探测轮次一换就会误清锁存, 一次长冻结会重复报。
    /// </summary>
    public void OnResponsive() => _reported = false;
}
