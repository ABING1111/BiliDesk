using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 释放大量视觉内容(切 tab / 切页面 / 关掉播放器)之后的内存回收。
///
/// 为什么需要它: WPF 的位图(解码后的像素)走终结器释放 —— 离开可视树只是让引用变成垃圾,
/// **不触发 GC 的话那些几十 MB 的封面位图会一直躺在进程里**, 任务管理器里的数字
/// 就是用户眼里的"内存只涨不掉"。在"释放了大批视觉内容"的时刻主动收一次,
/// 数字才能真正落回去。
///
/// 为什么是"延迟合并"而不是每次立即 Collect:
///   · Gen2 全量 GC 在对象多的时候要几十毫秒, 直接叠在导航动画上会卡一下;
///   · 用户快速连点几个 tab / 页面时, 中间态的回收毫无意义。
///   · 800ms 的合并窗口: 最后一次触发之后才收, 连点 5 个 tab 也只收 1 次。
///
/// ★★ 两条路径必须分开(2026-09-30):
///   · <see cref="RequestTrim"/> —— 高频(切页/切 tab)。**一律不阻塞 UI 线程**
///     (后台并发回收 + 在后台线程等终结器)。原来这条也走阻塞式回收, 几百 MB 位图时
///     UI 会停上百毫秒, 用户看到的就是"切过去卡顿一下"; 800ms 的延迟只是躲, 躲不掉。
///   · <see cref="RequestTrimAndRelease"/> —— 低频(关闭播放器)。才做阻塞式 + 压缩 LOH + 交还工作集。
/// </summary>
public static class MemoryTrim
{
    private static readonly object Gate = new();
    private static Timer? _pending;
    private static bool _releaseWorkingSet;
    private static bool _heavy;

    /// <summary>
    /// 温和回收的**内存闸门**: 距上次回收之后进程私有内存又涨了这么多才值得再收一次。
    ///
    /// ★★ 为什么要有它(2026-11-07 速度优化 P2): <see cref="RequestTrim"/> 原来是无条件
    ///   <c>GC.Collect(2, Forced)</c> + 等终结器, 而它挂在**每一次切页**上 —— 用户在首页/动态/
    ///   历史之间来回点几下, 就是连续几次 Gen2 全量回收, 每次几十毫秒的标记清扫全落在
    ///   后台线程和 UI 线程的竞争上, 表现是"切页发涩、风扇转起来"。
    ///   而切页真正释放的东西往往只有几 MB(页面常驻、卡片也不销毁), 根本不值得收。
    ///
    /// ★ 阈值取得比较宽(96MB): 目的是"把无意义的高频回收挡掉", 不是"省下每一次回收"。
    ///   真的堆了一大块(比如刚看完长视频 / 刚滚过上千张封面)照样会收。
    /// ★ 首次调用(_lastTrimBytes == 0)直接放行: 启动阶段没有任何基线, 不该被闸门挡住。
    /// </summary>
    private const long TrimThresholdBytes = 96L * 1024 * 1024;

    /// <summary>上一次真正回收完成时的进程私有内存(0 = 还没有基线)</summary>
    private static long _lastTrimBytes;

    /// <summary>当前进程私有内存(拿不到就返回 0 = 视为"不知道", 调用方按放行处理)</summary>
    private static long PrivateBytes()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64; }
        catch { return 0; }
    }

    /// <summary>
    /// 请求一次内存回收(温和版)。连续调用会合并成"最后一次调用后约 800ms"的一次真回收。
    /// 只应在"刚释放了一批视觉内容(列表容器/位图)"之后调用, 别在热路径上随手调。
    /// 用于**高频**触发点(切 tab / 切页面): 只做托管回收, 不动工作集。
    ///
    /// ★ 内部还有一道内存闸门(见 <see cref="TrimThresholdBytes"/>): 涨得不够多就直接跳过,
    ///   连续切页不会再演变成连续 Gen2 回收。
    /// </summary>
    public static void RequestTrim() => Request(releaseWorkingSet: false, heavy: false);

    /// <summary>
    /// **重载之后**请求回收: 除了托管回收, 还压缩大对象堆, 并把已经空出来的物理页交还系统。
    ///
    /// 只给"刚刚放掉一大批东西"的地方用 —— 目前就是**播放器窗口关闭**: 它手里有视频解码缓冲、
    /// 封面/头像位图、整条弹幕列表和评论树, 关掉那一刻进程里会空出一大块。用户看的就是任务管理器里的
    /// 数字, 而 .NET 的 GC 释放了内存却**不会主动把物理页还给系统**(工作集只增不减),
    /// 于是"关掉视频内存不回落"。这里显式交还一次, 数字才会真的掉下去。
    ///
    /// ★ 这是**阻塞式**回收(会短暂停住 UI 线程), 所以只能用在"用户刚关掉一个窗口"这种一次性时刻,
    ///   绝不能用在切页 / 切 tab 这种高频路径上 —— 那正是"切过去卡顿一下"的来源, 见 RequestTrim。
    /// </summary>
    public static void RequestTrimAndRelease() => Request(releaseWorkingSet: true, heavy: true);

    /// <summary>
    /// **空闲时**交还工作集(2026-11-09 内存优化)。
    ///
    /// 与 <see cref="RequestTrimAndRelease"/> 完全同一条重载路径, 只是语义更明确:
    /// 它只应该在"用户没在看这个窗口"的时刻调用(失焦一段时间 / 最小化 / 收进托盘),
    /// 这样阻塞式 GC 的几十到上百毫秒不会被用户感知。
    ///
    /// ★ 为什么必须有这一条: 用户看的是任务管理器里的"内存"(工作集)。.NET 的 GC 会
    ///   释放托管/原生对象, 但**不会主动把已经空闲的物理页还给系统** —— 所以即使封面
    ///   位图都被回收了, 数字也会停在峰值附近。只有 <see cref="ReleaseWorkingSet"/> 能把
    ///   那部分让出去。
    ///
    /// ★ 省掉"无意义的重载": 工作集低于 <see cref="IdleReleaseMinWorkingSetBytes"/>、
    ///   或距上次空闲回收不到 <see cref="IdleReleaseCooldownMs"/> 时直接跳过。
    ///   否则用户每次 Alt-Tab 都会跑一遍 gen2 + LOH 压缩 + 交还工作集,
    ///   白白烧 CPU(而且会让切回来的第一下变涩)。
    /// </summary>
    public static void RequestIdleRelease()
    {
        var ws = WorkingSetBytes();
        // 工作集已经不高就没必要再压一次(任务管理器里看到的正是这个数字)。
        if (ws > 0 && ws < IdleReleaseMinWorkingSetBytes) return;

        // 冷却: 防止用户反复 Alt-Tab 时每次都跑一遍阻塞式 GC。
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastIdleReleaseTick);
        if (last != 0 && now - last < IdleReleaseCooldownMs) return;
        Interlocked.Exchange(ref _lastIdleReleaseTick, now);

        Request(releaseWorkingSet: true, heavy: true);
    }

    /// <summary>工作集低于这个值就不必再"交还"了。见 <see cref="RequestIdleRelease"/>。</summary>
    private const long IdleReleaseMinWorkingSetBytes = 180L * 1024 * 1024;

    /// <summary>两次空闲回收之间至少隔这么久。见 <see cref="RequestIdleRelease"/>。</summary>
    private const int IdleReleaseCooldownMs = 60_000;

    private static long _lastIdleReleaseTick;

    /// <summary>当前进程工作集(拿不到就返回 0 = 视为"不知道", 调用方按放行处理)。</summary>
    private static long WorkingSetBytes()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().WorkingSet64; }
        catch { return 0; }
    }

    private static void Request(bool releaseWorkingSet, bool heavy)
    {
        lock (Gate)
        {
            // 合并窗口内如果先来了温和请求、后来了重载请求, 就以"要交还工作集"为准 ——
            // 否则重载那次会被前面那次的合并窗口吞掉。
            if (releaseWorkingSet) _releaseWorkingSet = true;
            if (heavy) _heavy = true;
            if (_pending != null) return;

            _pending = new Timer(_ =>
            {
                bool release, heavyNow;
                lock (Gate)
                {
                    _pending?.Dispose();
                    _pending = null;
                    release = _releaseWorkingSet;
                    heavyNow = _heavy;
                    _releaseWorkingSet = false;
                    _heavy = false;
                }
                Collect(release, heavyNow);
            }, null, 800, Timeout.Infinite);
        }
    }

    private static void Collect(bool releaseWorkingSet, bool heavy)
    {
        try
        {
            if (heavy) CollectBlocking(releaseWorkingSet);
            else
            {
                // ★ 温和路径的内存闸门: 距上次回收涨得不够多就跳过 —— 见 TrimThresholdBytes。
                var now = PrivateBytes();
                var baseline = Interlocked.Read(ref _lastTrimBytes);
                if (now > 0 && baseline > 0 && now - baseline < TrimThresholdBytes) return;
                CollectBackground();
            }
        }
        catch
        {
            // 回收失败不影响任何功能
        }
    }

    /// <summary>
    /// **阻塞式**回收: gen2 全量 + 压缩大对象堆 + 等终结器跑完 + (可选)交还工作集。
    /// 数字掉得最快最彻底, 代价是 UI 线程会停住 —— 几百 MB 位图时实测能到上百毫秒。
    /// 只给"刚刚放掉一大批东西"的一次性时刻用(关闭播放器)。
    /// </summary>
    private static void CollectBlocking(bool releaseWorkingSet)
    {
        // 大对象堆(LOH)默认**不压缩**: 每次解码/下载产生的大块(封面位图、JPEG byte[]、
        // 视频相关的大数组)都走 85KB 以上那条线, 死掉之后堆里留下一个个空洞,
        // 表现就是"明明都释放了, 内存却降不下去"。下一次回收顺手压缩一次。
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        GC.Collect();
        // 位图的像素数据是原生缓冲, 靠终结器释放 —— 不等终结器跑完, 后面看到的数字不会变
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (releaseWorkingSet) ReleaseWorkingSet();

        // 重载路径同样要刷新温和路径的基线(且必须是**回收之后**的数字,
        // 否则下一次会被误判成"又涨了一大截")。
        var now = PrivateBytes();
        if (now > 0) Interlocked.Exchange(ref _lastTrimBytes, now);
    }

    /// <summary>
    /// ★★ **高频路径(切页 / 切 tab)专用: 不阻塞 UI 线程的回收。**
    ///
    /// 为什么必须跟上面那条分开: 切换页面时进程里有几百 MB 位图, 一次"gen2 全量 + 压缩 LOH +
    /// 等终结器"会让 UI 线程停住上百毫秒 —— 用户看到的就是"**切过去之后卡顿一下**"
    /// (本文件开头那句"直接叠在导航动画上会卡一下"说的就是它; 原来只靠 800ms 延迟去躲, 躲不掉)。
    ///
    /// 这里改成: 后台并发回收(`blocking: false` ⇒ 标记/清扫绝大部分在后台线程上做),
    /// 然后**在后台线程上**等终结器、再补一次后台回收。UI 线程全程不参与。
    /// 代价: LOH **不压缩**(碎片留住), 交给 <see cref="CollectBlocking"/> 那一次去收拾 ——
    /// 数字照样会掉下来, 只是晚几百毫秒; 而"不卡"比"早几百毫秒掉数字"重要。
    /// </summary>
    private static void CollectBackground()
    {
        // 别让上一次重载路径设下的"下次压缩 LOH"泄漏到这条路径上
        // (那会把后台回收变成阻塞式的, 正好是我们想避免的)
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;

        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // 位图像素靠终结器释放。但"等终结器"这件事不能放在 UI 线程上做。
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
                // ★ 基线在**回收之后**刷新: 回收本身会把私有内存压下去, 拿回收**之前**的数字
                //   当基线的话, 下一次"只涨了 20MB"也会被误判成"涨了 100MB+"而再收一次。
                var after = PrivateBytes();
                if (after > 0) Interlocked.Exchange(ref _lastTrimBytes, after);
            }
            catch
            {
                // 忽略
            }
        });
    }

    /// <summary>把当前进程"已经用不到的物理页"交还系统(任务管理器里的"内存"列才会掉下来)</summary>
    private static void ReleaseWorkingSet()
    {
        try
        {
            EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        }
        catch
        {
            // 拿不到句柄/权限不足都无所谓: 这只是"让数字更好看"的最后一步
        }
    }

    /// <summary>
    /// 清空当前进程的工作集。用 psapi 的 EmptyWorkingSet(等价于 SetProcessWorkingSetSize(-1,-1))。
    /// 注意它**不释放任何东西**, 只是把已经空闲的页标记为可回收、把在用的页挪到备用列表;
    /// 所以必须排在 GC + 终结器之后调用才有意义。
    /// </summary>
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
