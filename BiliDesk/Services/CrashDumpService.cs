using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace BiliDesk.Services;

/// <summary>
/// 崩溃转储(minidump)的开关与落地(2026-11-28 新增)。
///
/// ★ 为什么需要它:
///   用户报的"窗口冻住 → 进程消失"是**原生层**的崩溃 —— 典型是 libvlc 里的
///   0xc0000005(访问违例)。这类崩溃**不是 .NET 异常**, 所以
///   <c>DispatcherUnhandledException</c> 根本兜不住(errors.txt 里一个字都不会有);
///   事件查看器里也只剩一条"应用程序错误", 看不到崩在哪个模块的哪一行。
///   ⇒ 唯一能定位到具体代码路径的手段就是 minidump: 拿到转储后能直接看到崩时的
///     线程栈与寄存器, 分得出是 libvlccore / nvoglv / 我们自己的 P/Invoke。
///
/// ★★★ 为什么**自己写**而不只靠 Windows Error Reporting(2026-11-28 探针实测):
///   WER 的 LocalDumps 机制写好了注册表、崩溃也确实发生了, 但 **.dmp 一个都没出来**。
///   原因: WER 是"进程外"的, 要靠 WerSvc 起来处理; 而 .NET 运行时自己会先把
///   某些原生异常转成托管异常处理(ID 1026 事件), 那一路上 WER 根本没插手。
///   ⇒ 所以这里改成**进程内自己写**: 用向量异常处理器(VEH)接住原生异常,
///     直接调 dbghelp 的 MiniDumpWriteDump 落盘。
///     这条路不依赖任何外部服务, 在用户机器上一定能成。
///   ⇒ WER 的注册表那条路**仍然保留**: 它能覆盖"进程被外部杀掉/栈溢出"这类
///     我们自己接不住的情况, 两条路互补。
///
/// ★ 什么时候开:
///   默认**关** —— 崩溃时写转储要占磁盘(约 300KB)也会拖慢崩溃处理。
///   设置页「问题诊断」里有开关, 遇到"必现的卡死/崩溃"时让用户打开, 复现一次,
///   再导出诊断报告。
/// </summary>
public static class CrashDumpService
{
    /// <summary>WER 的 LocalDumps 根键(HKCU 下, 不需要管理员权限)</summary>
    public const string LocalDumpsRoot =
        @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    /// <summary>WER 的转储类型: 1 = 完整转储, 2 = 小型转储(minidump)</summary>
    public const int MiniDumpType = 2;

    /// <summary>最多保留几个转储文件</summary>
    public const int MaxDumpFiles = 10;

    /// <summary>
    /// 出事的线程最多等转储写多久(毫秒)。
    ///
    /// ★ 必须有上限: 转储卡住不能变成"进程永远挂着" —— 那比崩溃还糟
    ///   (用户会以为程序彻底死了, 连强杀都得开任务管理器)。
    /// 30 秒对"写一个几十 MB 的文件"绰绰有余。
    /// </summary>
    public const int DumpWaitTimeoutMs = 30_000;

    /// <summary>默认转储目录(数据目录下的 dumps 子目录)</summary>
    public static string DefaultDumpDirectory => Path.Combine(AppPaths.DataDir, "dumps");

    // ---------------- Windows API ----------------

    /// <summary>MiniDumpWriteDump 的转储类型组合(见 dbghelp 的 MINIDUMP_TYPE)</summary>
    [Flags]
    private enum MiniDumpTypeFlags : uint
    {
        Normal = 0x00000000,
        /// <summary>
        /// "只保留栈, 不要数据段"。
        /// ★★★ 实测(2026-11-28, bd-probe-dumpsizecrash): 崩溃现场各组合的体积 ——
        ///   Normal = 151KB / Normal+FilterMemory+WithThreadInfo = 151KB /
        ///   Normal+WithUnloadedModules = 36MB ← "已卸载模块"会连带拖出几十 MB 数据。
        ///   所以**只**用 FilterMemory + WithThreadInfo, 千万别加 WithUnloadedModules。
        /// </summary>
        FilterMemory = 0x00000008,
        /// <summary>抓线程上下文(崩在哪条线程、栈是什么)</summary>
        WithThreadInfo = 0x00001000,
    }

    // ---------------- 异常码白名单 ----------------

    /// <summary>
    /// "这个异常码算不算崩溃"。
    ///
    /// ★★★ 为什么必须白名单(2026-11-28 审查出来的):
    ///   VEH 会在**每一次结构化异常**上被调用 —— 包括大量**正常的控制流异常**。
    ///   如果不筛, 光 libvlc(C++)内部抛的 <c>0xE06D7363</c> 就能让正常播放时疯狂写转储
    ///   (每个 300KB, 而且要 Join 等它写完) —— 开了这个开关等于把程序变卡。
    ///   托管异常(0xE0434352)实测**不会**走到这里(.NET 自己先处理了), 但不能依赖这一点:
    ///   其它运行库、原生代码的异常都会过来。
    ///
    /// ★ 只认"进程要死了"的那几类(NTSTATUS 的致命异常)。
    ///   断点(0x80000003)、单步、C++ EH(0xE06D7363)、CLR 异常(0xE0434352)、
    ///   SetThreadName(0x406D1388) 一律不认 —— 那些都是正常控制流。
    /// </summary>
    private static bool IsFatalExceptionCode(uint code) => code switch
    {
        0xC0000005 => true,   // ACCESS_VIOLATION           —— libvlc 里最常见的那一类
        0xC000001D => true,   // ILLEGAL_INSTRUCTION
        0xC0000025 => true,   // NONCONTINUABLE_EXCEPTION
        0xC0000094 => true,   // INTEGER_DIVIDE_BY_ZERO
        0xC0000096 => true,   // PRIVILEGED_INSTRUCTION
        0xC0000374 => true,   // HEAP_CORRUPTION
        0xC0000409 => true,   // STACK_BUFFER_OVERRUN / fail-fast
        0xC000041D => true,   // FATAL_USER_CALLBACK_EXCEPTION —— WPF 的 WndProc 里崩了
        0xC0000420 => true,   // ASSERTION_FAILURE
        0xC0000602 => true,   // FAIL_FAST_EXCEPTION
        _ => false,
    };

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, IntPtr hFile, MiniDumpTypeFlags dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam, IntPtr callbackParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();



    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool RemoveVectoredExceptionHandler(IntPtr handle);

    /// <summary>向量异常处理器的委托签名</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint VectoredHandler(IntPtr exceptionPointers);

    // ---------------- 状态 ----------------

    /// <summary>VEH 句柄(非零 = 已装上)。</summary>
    private static IntPtr _vehHandle;

    /// <summary>
    /// 必须**持有**这个委托的引用, 否则会被 GC 回收。
    ///
    /// ★★★ 这是 P/Invoke 回调的经典致命坑(2026-11-28 审查出来的):
    ///   把委托直接传给 <c>AddVectoredExceptionHandler</c> 之后, 如果托管侧没有引用,
    ///   GC 认为它没人用就回收掉 —— 而系统那边还保存着它的函数指针。
    ///   等到真崩溃时, 系统跳转到一个**已释放的内存**, 崩溃处理本身又崩一次,
    ///   转储自然一个都出不来(还掩盖了原始崩溃)。
    /// </summary>
    private static VectoredHandler? _vehDelegate;

    /// <summary>转储目录(VEH 是静态回调, 只能从静态字段拿)。</summary>
    private static string _dumpDirectory = "";

    /// <summary>是否已装上进程内的异常处理器。</summary>
    public static bool IsHandlerInstalled => _vehHandle != IntPtr.Zero;

    /// <summary>
    /// 写转储时的重入保护: 写转储本身也可能出问题, 再崩一次会无限递归。
    /// </summary>
    private static int _dumping;

    /// <summary>
    /// "这次崩溃的转储已经写过了"。
    ///
    /// ★ 同一个崩溃会**多次**进 VEH(第一次机会 / 第二次机会 / 每个线程的链),
    ///   不加这个标记会写出好几份一模一样的转储, 白白拖慢崩溃处理。
    /// ★ 写成功后**不再重置**: 进程反正马上要死了, 留一份最有价值的就够。
    ///   (真遇到"崩溃被 CLR 吞掉、进程没死"的情况, 也只是这次运行里不再记录 —— 可接受。)
    /// </summary>
    private static int _dumped;

    /// <summary>
    /// 转储按**本程序的文件名**登记 —— WER 是拿 exe 名去匹配 LocalDumps 子键的,
    /// 写死 "BiliDesk.exe" 会在改名/换目录后失配(用户以为开了却一个转储都没有)。
    /// </summary>
    private static string ExeName
    {
        get
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                var path = p.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                    return Path.GetFileName(path);
            }
            catch
            {
                // 拿不到就退回默认名(例如某些受限宿主)
            }
            return "BiliDesk.exe";
        }
    }

    /// <summary>本平台是否支持(只有 Windows 有 WER 与 dbghelp)</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>注册表里当前登记的转储目录; 没开则 null</summary>
    public static string? CurrentDumpDirectory()
    {
        if (!IsSupported) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LocalDumpsRoot + "\\" + ExeName);
            return key?.GetValue("DumpFolder") as string;
        }
        catch
        {
            // 读不到就当没开 —— 注册表被策略锁住时不该让设置页炸掉
            return null;
        }
    }

    /// <summary>当前是否已开启(设置项为真, 或注册表里有我们这一项)</summary>
    public static bool IsEnabled()
    {
        if (IsHandlerInstalled) return true;
        var dir = CurrentDumpDirectory();
        return !string.IsNullOrWhiteSpace(dir);
    }

    /// <summary>
    /// 开启转储: 装上进程内异常处理器 + 登记 WER 的 LocalDumps。返回实际生效的目录。
    /// ★ 目录一定要**先建好**: WER 不会替你创建 DumpFolder, 目录不存在时它会静默不写;
    ///   我们自己写转储也一样(打不开文件就没法写)。
    /// </summary>
    public static string Enable(string? dumpDirectory)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("只有 Windows 支持崩溃转储");

        var dir = string.IsNullOrWhiteSpace(dumpDirectory) ? DefaultDumpDirectory : dumpDirectory.Trim();
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);

        // ① 进程内处理器(主力): 不依赖任何外部服务。
        InstallHandler(dir);

        // 顺手清一次老转储: 每次启动都开着的用户不该被十几个 .dmp 慢慢吃满磁盘。
        PruneOldDumps(dir);

        // ② WER 注册表(补漏): 覆盖"栈溢出/进程被外部杀掉"这类我们接不住的情况。
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LocalDumpsRoot + "\\" + ExeName, writable: true)
                            ?? throw new IOException("无法写入注册表: " + LocalDumpsRoot);
            // DumpFolder 必须是 REG_EXPAND_SZ —— WER 只认这一种类型, 写成 REG_SZ 会被忽略。
            key.SetValue("DumpFolder", dir, RegistryValueKind.ExpandString);
            key.SetValue("DumpType", MiniDumpType, RegistryValueKind.DWord);
            key.SetValue("DumpCount", MaxDumpFiles, RegistryValueKind.DWord);
        }
        catch
        {
            // 写不进注册表不影响主力功能(进程内处理器已经装上了)
        }

        return dir;
    }

    /// <summary>关闭转储(卸掉处理器 + 删掉注册表项)。失败静默。</summary>
    public static void Disable()
    {
        if (!IsSupported) return;
        try
        {
            var handle = Interlocked.Exchange(ref _vehHandle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                try { RemoveVectoredExceptionHandler(handle); } catch { /* 忽略 */ }
            }
        }
        catch
        {
            // 忽略
        }
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(LocalDumpsRoot + "\\" + ExeName, throwOnMissingSubKey: false);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 装上向量异常处理器。
    ///
    /// ★ 为什么用 VEH 而不是 <c>SetUnhandledExceptionFilter</c>:
    ///   未处理异常过滤器会被后装的过滤器顶掉(.NET 运行时自己就装了一个),
    ///   而 VEH 是**注册顺序**调用、谁也不顶掉谁 —— 我们的处理器总能第一个接到。
    ///
    /// ★ 委托必须存进静态字段(见 <see cref="_vehDelegate"/> 的说明), 否则会被 GC 回收。
    /// </summary>
    private static void InstallHandler(string dumpDirectory)
    {
        _dumpDirectory = dumpDirectory;
        if (_vehHandle != IntPtr.Zero) return;

        // ★ 先赋值再登记: 保证从登记那一刻起就有托管引用兜住它。
        _vehDelegate = OnVectoredException;
        var handle = AddVectoredExceptionHandler(1, _vehDelegate);
        if (handle != IntPtr.Zero) _vehHandle = handle;
        else _vehDelegate = null;   // 没装上就别留个悬着的引用
    }

    /// <summary>
    /// 向量异常处理器: **只**在真正的致命异常(访问违例等)上写 minidump。
    /// ★ 返回 EXCEPTION_CONTINUE_SEARCH(0): 让异常继续按原路走完 ——
    ///   我们只是"顺便留个证据", 不改变进程的死活方式。
    /// </summary>
    private static uint OnVectoredException(IntPtr exceptionPointers)
    {
        const uint ExceptionContinueSearch = 0;

        // ★★ 重入检查必须放在 try **外面**:
        //   放在 try 里的话, 重入时 return 会触发 finally 把 _dumping 清成 0,
        //   反而把外层正在生效的保护给拆了。
        if (Interlocked.Exchange(ref _dumping, 1) != 0) return ExceptionContinueSearch;

        try
        {
            // 已经为这次崩溃写过转储了 ⇒ 不再重复写(见 _dumped 的说明)。
            if (Volatile.Read(ref _dumped) != 0) return ExceptionContinueSearch;

            // EXCEPTION_POINTERS: [0]=EXCEPTION_RECORD*, [8]=CONTEXT*
            var recordPtr = Marshal.ReadIntPtr(exceptionPointers, 0);
            if (recordPtr == IntPtr.Zero) return ExceptionContinueSearch;

            // ★ EXCEPTION_RECORD: ExceptionCode 在偏移 0。先看一眼是不是"要命的"
            //   —— 不是就直接放行, 绝不能因为一个正常控制流异常就去写转储。
            var exceptionCode = (uint)Marshal.ReadInt32(recordPtr, 0);
            if (!IsFatalExceptionCode(exceptionCode)) return ExceptionContinueSearch;

            // EXCEPTION_RECORD.ExceptionAddress: x64 偏移 16, x86 偏移 12
            var exceptionAddress = IntPtr.Size == 8
                ? Marshal.ReadIntPtr(recordPtr, 16)
                : Marshal.ReadIntPtr(recordPtr, 12);

            // 顺手把异常码与出错地址记下来 —— 转储之外再给一行"人能直接读"的线索:
            // 0xC0000005 + 地址落在哪个模块, 有时不用打开 WinDbg 就能猜个八九不离十。
            var crashNote = $"异常码 0x{exceptionCode:X8}, 出错地址 0x{exceptionAddress.ToInt64():X}";

            // ★★★ **必须放到别的线程去写**(2026-11-28 探针实测):
            //   在 VEH 里**直接**调 MiniDumpWriteDump 会让它自己撞上同一个异常 ——
            //   dbghelp 要读出事的那个线程的 CONTEXT, 而那一刻那个线程正卡在异常分发里,
            //   读它就等于在拆自己正在跑的栈, 于是 MiniDumpWriteDump 自己访问违例,
            //   最后只留下一个 0 字节的 .dmp。
            //   ⇒ 交给一个**专用后台线程**: 它是正常状态, 让它去 dump 整个进程
            //     (MiniDumpWriteDump 的第一个参数就是目标进程句柄, 跨线程完全支持)。
            var writer = new Thread(() => WriteMiniDump(_dumpDirectory, crashNote))
            {
                IsBackground = true,
                Name = "BiliDesk-崩溃转储",
                // ★ 优先级压到最低: 崩溃现场优先, 别让转储跟正常业务抢 CPU。
                Priority = ThreadPriority.Lowest
            };
            writer.Start();

            // ★★★ **必须在这里等它写完**(2026-11-28 探针实测):
            //   不等的话, 异常从 VEH 返回后进程立刻就死了 —— 转储线程还没来得及
            //   落盘, 目录里一个文件都没有(探针实测: 等 4 秒后目录是空的)。
            //   出事的线程卡在 VEH 里等着, 进程就不会死, 转储线程才有机会把文件写完。
            //   ★ 一定要有超时: 转储卡住不能变成"进程永远挂着" —— 那比崩溃还糟
            //     (用户会以为程序彻底死了, 连强杀都要找任务管理器)。
            writer.Join(DumpWaitTimeoutMs);
        }
        catch
        {
            // 写转储失败也不能影响进程原本的死法
        }
        finally
        {
            Volatile.Write(ref _dumping, 0);
        }

        return ExceptionContinueSearch;
    }

    /// <summary>
    /// 把当前进程的 minidump 写到目录里。
    ///
    /// ★ 这个方法跑在**专用后台线程**上(见 OnVectoredException 的说明):
    ///   MiniDumpWriteDump 的第一个参数是目标进程句柄, 所以跨线程写完全没问题,
    ///   而且不会撞上出事的那个线程正在跑的异常分发。
    ///
    /// ★★★ **异常信息结构必须传 null**(2026-11-28 探针实测):
    ///   传了结构体就会带上"出事线程的 CONTEXT 指针", 而 MiniDumpWriteDump 会去读它 ——
    ///   那个线程此刻正卡在异常分发里, 读它的 CONTEXT 就会读到半死不活的状态,
    ///   于是 dbghelp 自己访问违例(现场就是它崩的), 最后只留下 0 字节的 .dmp。
    ///   传 null 则 dbghelp 用**调用线程**的 CONTEXT, 那是完全正常的状态, 稳。
    ///   ⇒ 代价: 转储里那个线程的 CONTEXT 不是出事前的那一刻 ——
    ///   但**线程栈与指令指针仍然是崩时的**(那些是从内存里读出来的), 足够定位。
    /// </summary>
    private static void WriteMiniDump(string dumpDirectory, string crashNote)
    {
        try
        {
            Directory.CreateDirectory(dumpDirectory);
            var name = $"BiliDesk-crash-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.dmp";
            var path = Path.Combine(dumpDirectory, name);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var hFile = fs.SafeFileHandle.DangerousGetHandle();

                var flags = MiniDumpTypeFlags.Normal
                            | MiniDumpTypeFlags.FilterMemory
                            | MiniDumpTypeFlags.WithThreadInfo;

                var ok = MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId,
                    hFile, flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (!ok)
                {
                    // 写失败也别留一个 0 字节的假文件(用户会以为拿到了转储)
                    try { fs.Dispose(); File.Delete(path); } catch { /* 忽略 */ }
                    return;
                }
            }

            // 标记"这次崩溃的转储已经写了", 后续同一个崩溃不再重复写。
            Volatile.Write(ref _dumped, 1);

            // ★ 这里**只能直接写文件**, 不能走 App.ReportNote:
            //   那个方法要拿 LogGate 锁, 而冻结/崩溃的原因很可能就是"UI 线程正卡在写日志上"
            //   —— 在崩溃路径上去抢一把可能永远拿不到的锁, 等于把最后一份证据也丢了。
            try
            {
                File.AppendAllText(Path.Combine(dumpDirectory, "crash-notes.txt"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {crashNote} → {name}{Environment.NewLine}");
            }
            catch { /* 尽力而为 */ }
        }
        catch
        {
            // 忽略: 写不出转储不能影响进程原本的死法
        }
    }

    /// <summary>
    /// 列出已有的转储文件(新的在前)。给"导出诊断报告"用。
    /// </summary>
    public static string[] ListDumpFiles(string? directory)
    {
        var dir = string.IsNullOrWhiteSpace(directory) ? DefaultDumpDirectory : directory;
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.dmp", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 清洗转储目录: 只保留最近 <see cref="MaxDumpFiles"/> 个文件。
    /// 转储文件动辄几十 MB, 攒着不清会把磁盘吃满。
    /// </summary>
    public static void PruneOldDumps(string? directory)
    {
        var files = ListDumpFiles(directory);
        for (var i = MaxDumpFiles; i < files.Length; i++)
        {
            try { File.Delete(files[i]); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary>
    /// 清空转储目录里的**全部**转储(用户手动清理时用, 界面上的"清空"按钮)。
    /// ★ 只删 .dmp 与 crash-notes.txt, 不碰目录里别的东西。
    /// </summary>
    public static int ClearDumps(string? directory)
    {
        var removed = 0;
        foreach (var f in ListDumpFiles(directory))
        {
            try { File.Delete(f); removed++; } catch { /* 尽力而为 */ }
        }
        try
        {
            var notes = Path.Combine(directory ?? DefaultDumpDirectory, "crash-notes.txt");
            if (File.Exists(notes)) { File.Delete(notes); removed++; }
        }
        catch { /* 忽略 */ }
        return removed;
    }
}
