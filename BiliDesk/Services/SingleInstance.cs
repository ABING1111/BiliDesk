using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BiliDesk.Services;

/// <summary>
/// 单实例守卫: 保证同一台机器上只跑**一个** BiliDesk 进程。
///
/// 为什么需要它(2026-10-06 用户反馈): 双击 exe / 点开始菜单图标时, 又起了一个全新进程 ——
///   于是托盘里出现两个图标、两个窗口抢同一个 session.json(设置/登录态互相覆盖),
///   而且两个进程都持有 LibVLC, 白白多占一份内存。
///
/// 做法(两件套, 缺一不可):
///   ① **命名 Mutex** 判定"是不是第一个" —— 用 Local\ 前缀(每用户会话一个命名空间),
///      不用 Global\: 同一台机器的另一个 Windows 用户登录时起自己的实例是合理的,
///      Global\ 会把不同用户也挡掉。
///   ② **命名管道**把"用户又点了一次"这个意图**转达给已在跑的那个进程**, 让它把窗口
///      显示到前台。只做 ① 不做 ② 的表现是"双击图标毫无反应" —— 用户会以为程序坏了,
///      然后不停地点(这也是为什么很多单实例程序被吐槽"点不开")。
///
/// ★ Mutex 必须活到进程结束: 它作为静态字段持有。被 GC 回收掉的话, 第二个进程
///   会以为自己才是第一个, 于是双开照样发生 —— 这是单实例实现里最常见的坑。
/// ★ 管道监听要放到**后台线程**上: BeginWaitForConnection 等的是 UI 线程的话,
///   主窗口还没建好时会把启动流程卡住。
/// </summary>
public static class SingleInstance
{
    /// <summary>互斥体专名。带产品名 + 版本无关, 避免和别的程序撞</summary>
    private const string MutexName = @"Local\BiliDesk.SingleInstance.Mutex";

    /// <summary>管道的名字(第二个实例通过它发"请显形"指令)</summary>
    private const string PipeName = "BiliDesk.SingleInstance.Pipe";

    /// <summary>
    /// 持有 Mutex 的引用。**必须是静态字段** —— 局部变量会被 GC 回收, 那样锁就释放了。
    /// </summary>
    private static Mutex? _mutex;

    /// <summary>是否本进程就是"第一个实例"</summary>
    public static bool IsFirstInstance { get; private set; }

    /// <summary>收到"再来一个实例"的请求时触发(在后台线程上, 订阅方自己 Dispatcher 派发)</summary>
    public static event Action? ShowRequested;

    /// <summary>
    /// 尝试成为唯一实例。
    /// 返回 true = 本进程是第一个(调用方继续正常启动);
    /// 返回 false = 已经有实例在跑(调用方**必须立刻退出**, 不要建窗口)。
    /// </summary>
    public static bool Acquire()
    {
        try
        {
            // initiallyOwned: true —— 抢到就归我们
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            IsFirstInstance = createdNew;
            if (createdNew)
            {
                StartPipeServer();
                return true;
            }

            // 不是第一个: 把"请显形"发给已经在跑的那个, 然后本进程退出。
            // 这一步失败也不影响"不双开"这个核心目标, 所以吞掉异常。
            TrySignalExisting();
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            // 上一个进程是被强杀的(没来得及释放) —— 这种情况下"锁归我们了", 照常启动。
            // 不处理的话这里会抛异常, 表现就是"杀过一次进程之后再双击就永远打不开"。
            IsFirstInstance = true;
            StartPipeServer();
            return true;
        }
        catch
        {
            // 任何意外(例如权限受限创建不了命名对象): 宁可让它照常启动, 也不要打不开程序。
            IsFirstInstance = true;
            return true;
        }
    }

    /// <summary>
    /// 后台监听管道。每收到一次连接就触发一次 <see cref="ShowRequested"/>,
    /// 然后**继续监听**(用户可能点很多次)。
    /// </summary>
    private static void StartPipeServer()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    // 内容不重要: 管道里只要来了连接, 就是"请把你的窗口显示出来"
                    ShowRequested?.Invoke();
                }
                catch
                {
                    // 管道被占用/建立失败: 歇一下再试, 别在这里空转烧 CPU
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>把"请显形"发给已在运行的实例(超时很短: 对方没在听就直接放弃)</summary>
    private static void TrySignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // 1 秒: 正常情况是毫秒级。对方没在监听(例如正卡在启动)时不要拖着不退出。
            client.Connect(1000);
            client.WriteByte(1);
            client.Flush();
        }
        catch
        {
            // 对方还没来得及建管道(常见于"刚启动就再双击一次"): 忽略即可 ——
            // 它那边的窗口马上就出来了, 用户体验上没差别。
        }
    }
}
