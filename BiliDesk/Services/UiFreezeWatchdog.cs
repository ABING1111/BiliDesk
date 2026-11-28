using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace BiliDesk.Services;

/// <summary>
/// UI 线程冻结看门狗(2026-11-28 新增)。
///
/// ★ 要解决的正是用户报的那一类问题: "窗口冻住 → 我只能强杀进程 → 事后什么日志都没有"。
///   冻结中的 UI 线程跑不了任何异常处理器(.NET 的兜底也在那个线程上), 所以必须
///   **由另一个线程**去发现它, 并把"当时卡在哪"记下来。
///
/// ★ 三个部件:
///   ① <see cref="_probeThread"/> —— 后台线程, 定期往 UI 线程派发一个空操作,
///      **分段等**它完成。只要过了阈值还没完成, 就认定 UI 线程被卡住了。
///   ② <see cref="DiagnosticsState"/> 的"UI 线程当前操作"标记 —— 在那些**已知可能很久**
///      的活儿开始前打标记、做完撤掉。冻结发生时看一眼这个标记, 就知道它大概率卡在哪。
///   ③ <see cref="_policy"/> —— 判定阈值与"同一场冻结只报一次"的锁存(纯逻辑, 可单测)。
///
/// ★★★ **为什么必须"分段等"而不是一次性等到底**(2026-11-28 探针实测出来的坑):
///   一次性 `Task.Wait(30s)` 的写法, 只有在 UI 线程**恢复之后**才能拿到往返耗时 ——
///   也就是说"冻结报告"是在冻结**结束**时才写出来的, 而那时
///   "当时在做的那件事"早就做完了、标记也撤了 ⇒ 报告里只会写"(未标记)",
///   正好把最关键的那条线索丢掉。
///   ⇒ 改成每 200ms 问一次"回来了没": 一旦累计超过阈值, **当场**就写报告,
///     那时 UI 线程还卡着, 标记还在, 现场才是真的。
///
/// ★ 为什么不给 UI 线程抓调用栈:
///   .NET 没有跨线程抓另一个线程调用栈的公开手段(抓自己的可以, 但冻结时 UI 线程
///   根本跑不到抓栈那一步)。要拿到栈只能靠进程内 dump 或调试器, 都太重。
///   ⇒ 退而求其次: 用上面那个"当前操作"标记给出**线索**, 而不是假装能给出栈。
///   这也是为什么 <see cref="DiagnosticsState.BeginUiOperation"/> 要让各个耗时路径打标记。
///
/// ★ 线程安全: 探测线程与 UI 线程都会读写状态, 状态层自己加锁。
/// </summary>
public sealed class UiFreezeWatchdog
{
    /// <summary>两次探测之间的间隔(毫秒)。</summary>
    private const int ProbeIntervalMs = 1_000;

    /// <summary>一次探测里"分段等"的每段长度(毫秒)。</summary>
    private const int ProbeSliceMs = 200;

    /// <summary>
    /// 单次探测最多累计等多久(毫秒)。超过就放弃这一轮(UI 线程可能已经死了,
    /// 或者进程正在退出) —— 再等下去也只是白占一个线程。
    /// </summary>
    private const long ProbeWaitCapMs = 60_000;

    private readonly Dispatcher _dispatcher;
    private readonly UiFreezePolicy _policy;
    private readonly Thread _probeThread;

    /// <summary>进程是否正在退出(退出后不再探测, 否则 Dispatcher 已停, 探测线程会白等)。</summary>
    private volatile bool _stopping;

    /// <summary>冻结发生次数(诊断用: 一天卡了几次)。</summary>
    private int _freezeCount;

    /// <summary>最近一次冻结的描述(诊断用)。</summary>
    private volatile string _lastFreezeSummary = "(未发生过冻结)";

    public UiFreezeWatchdog(Dispatcher dispatcher, long thresholdMs = UiFreezePolicy.DefaultThresholdMs)
    {
        _dispatcher = dispatcher;
        _policy = new UiFreezePolicy(thresholdMs);

        _probeThread = new Thread(ProbeLoop)
        {
            IsBackground = true,   // 后台线程: 就算没停干净也不会拦着进程退出
            Name = "BiliDesk-UI看门狗",
            Priority = ThreadPriority.BelowNormal
        };
    }

    /// <summary>启动看门狗。</summary>
    public void Start() => _probeThread.Start();

    /// <summary>停掉看门狗(进程退出时调)。</summary>
    public void Stop() => _stopping = true;

    /// <summary>最近一次冻结的描述(给导出诊断报告用)。</summary>
    public string LastFreezeSummary => _lastFreezeSummary;

    /// <summary>冻结次数(给导出诊断报告用)。</summary>
    public int FreezeCount => Volatile.Read(ref _freezeCount);

    /// <summary>探测循环(跑在后台线程上)。</summary>
    private void ProbeLoop()
    {
        while (!_stopping)
        {
            ProbeOnce();
            try { Thread.Sleep(ProbeIntervalMs); }
            catch { break; }
        }
    }

    /// <summary>
    /// 一轮探测: 派一个空操作给 UI 线程, 分段等它回来。
    /// 一旦累计超过阈值还没回来, **当场**判定为冻结并写报告(UI 线程还卡着, 现场是真的)。
    /// </summary>
    private void ProbeOnce()
    {
        DispatcherOperation op;
        try
        {
            // Background 优先级: 不要插到渲染前面抢时间, 也不要在退出时排在最后跑不完。
            op = _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        catch
        {
            return;   // Dispatcher 已经停了(正在退出)
        }

        var sw = Stopwatch.StartNew();
        var responsive = false;

        try
        {
            while (!_stopping && sw.ElapsedMilliseconds < ProbeWaitCapMs)
            {
                // ★ 分段等: 每 200ms 醒一次。UI 线程还卡着的话, 这段等待会一直超时,
                //   于是我们能在冻结**持续期间**就判定并报告 —— 而不是等它结束后才补记。
                if (op.Task.Wait(ProbeSliceMs))
                {
                    responsive = true;   // 回来了 ⇒ UI 线程是通的
                    break;
                }

                var elapsed = sw.ElapsedMilliseconds;
                var action = _policy.Tick(elapsed);
                if (action == UiFreezeAction.Report)
                    ReportFreeze(elapsed);
            }
        }
        catch
        {
            // 派发操作被取消/出错: 忽略
        }
        finally
        {
            // 这一轮结束(无论是回来了还是超时放弃):
            //   · 回来了 ⇒ 冻结结束, 解锁(下一次冻结还能再报);
            //   · 没回来 ⇒ 把它作废, 否则它还挂在派发队列里, 等 UI 线程缓过来时还会跑一遍
            //     (虽然是个空操作, 但会污染时序测量)。
            //   ★ 解锁**只能**走 OnResponsive: 用"传个小数字给 Tick"顺带解锁是错的 ——
            //     探测轮次一换(每轮最多等一分钟)锁存就被误清, 一次长冻结会重复报好几遍。
            if (responsive)
            {
                _policy.OnResponsive();
            }
            else
            {
                try { op.Abort(); } catch { /* 已经执行了就算了 */ }
            }
        }

    }

    /// <summary>确认冻结: 把现场写进日志。</summary>
    private void ReportFreeze(long elapsedMs)
    {
        var count = Interlocked.Increment(ref _freezeCount);
        var summary = $"UI 线程无响应约 {elapsedMs / 1000.0:0.0} 秒 (第 {count} 次)";
        _lastFreezeSummary = summary;

        // 此刻 UI 线程正在做的事(标记法, 见类注释)。
        var currentOp = DiagnosticsState.CurrentUiOperation ?? "(未标记 — 说明是某个没打标记的路径)";
        var ops = DiagnosticsState.ActiveUiOperations;

        var state = DiagnosticsState.Describe();
        App.ReportNote(
            $"[冻结] {summary}\n" +
            $"UI 线程当时正在做: {currentOp}\n" +
            (ops.Count > 1 ? $"进行中的操作: {string.Join(" → ", ops)}\n" : "") +
            $"\n现场快照:\n{state}");

        DiagnosticsState.Note($"[冻结] {summary} (当时在做: {currentOp})");
    }
}
