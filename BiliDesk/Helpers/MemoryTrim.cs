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
    /// 请求一次内存回收(温和版)。连续调用会合并成"最后一次调用后约 800ms"的一次真回收。
    /// 只应在"刚释放了一批视觉内容(列表容器/位图)"之后调用, 别在热路径上随手调。
    /// 用于**高频**触发点(切 tab / 切页面): 只做托管回收, 不动工作集。
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
            else CollectBackground();
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
