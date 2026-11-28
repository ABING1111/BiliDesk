using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Diagnostics.Eventing.Reader;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace BiliDesk.Services;

/// <summary>
/// 一键导出诊断报告(2026-11-28 新增)。
///
/// ★ 要解决的现实问题: 用户报"卡死/崩溃", 但他手上只有一台电脑, 让他手动去
///   %LocalAppData% 里翻 errors.txt、vlc-debug.txt、vlc-native.log, 再一个个上传 ——
///   十有八九是做不到的, 于是问题永远停在"描述一下现象"这一步, 定位不到根因。
///   ⇒ 这里把"该收的东西"一次收成一个 zip, 用户只要把这个文件发出来。
///
/// ★ 收什么(按"能不能定位问题"排序):
///   · <b>报告正文</b> —— 程序/系统/硬件/关键设置 + 冻结看门狗的现场 + 事件流水。
///   · <b>errors.txt / errors.txt.old</b> —— .NET 托管异常(带调用栈)。
///   · <b>vlc-debug.txt</b> —— 应用侧播放诊断(选流、首帧耗时、卡顿自愈…)。
///   · <b>vlc-native.log</b> —— libvlc 原生日志(只有开了调试开关才有)。
///   · <b>*.dmp</b> —— 崩溃转储(开了崩溃转储开关且崩过才有)。
///   · <b>事件查看器里的崩溃记录</b> —— 应用程序错误/.NET Runtime/WER, 最近的若干条。
///
/// ★ ★ **绝不能收进去的东西**:
///   · <c>session.json</c> —— 里面有 SESSDATA / bili_jct, 等于用户的登录凭据。
///   · <c>settings.json</c> —— 本身不含密钥, 但里面可能有用户自定义的关键词/屏蔽列表,
///     属于隐私; 报告里只挑"与播放/渲染有关"的几个字段, 不整份打包。
///   · 缓存目录(imgcache/vcache)—— 纯图片与视频, 动辄几百 MB, 也没诊断价值。
///
/// ★ 输出位置: 默认落在**下载目录**(用户找得到、能直接拖去发消息), 而不是数据目录
///   —— 数据目录是隐藏的, 用户根本不知道那在哪。
/// </summary>
public static class DiagnosticReportService
{
    /// <summary>报告正文的文件名(放在 zip 里)</summary>
    public const string ReportFileName = "report.txt";

    /// <summary>报告里最多放几条本程序的崩溃记录。</summary>
    public const int EventLogEntryLimit = 40;

    /// <summary>
    /// 事件日志最多**扫描**多少条(不管命中多少)。
    ///
    /// ★ 这是"导出按钮卡不卡"的关键: 系统 Application 日志里绝大部分是别的程序,
    ///   只数"命中几条"的话, 一次导出可能要读完几万条(实测十几秒)。
    ///   扫 3000 条足够覆盖到最近的崩溃, 再往前的翻它也没意义。
    /// </summary>
    public const int EventLogScanLimit = 3_000;

    /// <summary>
    /// 导出报告(zip), 返回生成的文件路径。
    /// </summary>
    /// <param name="targetPath">目标 zip 路径; null = 自动在下载目录生成</param>
    /// <param name="dumpDirectory">崩溃转储目录; null = 用默认目录</param>
    public static string Export(string? targetPath = null, string? dumpDirectory = null)
    {
        var path = string.IsNullOrWhiteSpace(targetPath)
            ? DefaultReportPath()
            : Path.GetFullPath(targetPath);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 先写临时文件再移动: 中途失败不会留下一个"看着像报告"的半截 zip。
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddText(zip, ReportFileName, BuildReportText(dumpDirectory));
                AddLogFiles(zip);
                AddDumpFiles(zip, dumpDirectory);
                AddCrashNotes(zip, dumpDirectory);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 尽力而为 */ }
        }
        return path;
    }

    /// <summary>默认报告路径(下载目录, 文件名带版本与时间戳)。</summary>
    public static string DefaultReportPath()
    {
        var name = $"BiliDesk-诊断报告-v{App.AppVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        return Path.Combine(AppPaths.DownloadDir, name);
    }

    /// <summary>往 zip 里加一个文本条目。</summary>
    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        w.Write(content);
    }

    /// <summary>
    /// 收日志文件。**只收白名单里的那几个** —— 数据目录里还有 session.json(登录凭据)、
    /// 缓存目录、WebView2 数据, 一个都不能碰。
    /// </summary>
    private static void AddLogFiles(ZipArchive zip)
    {
        // ★ 白名单**只放这几个**。数据目录里还有 session.json(登录凭据)、settings.json
        //   (用户自定义的关键词/屏蔽列表)、缓存目录、WebView2 数据 —— 一个都不能碰。
        // ★ 路径全部从 AppPaths 取(不要在本地重拼文件名): 轮转后的日志名曾经在这边
        //   写成 "errors.old" 而实际是 "errors.txt.old", 结果上一份日志永远收不进来。
        var whitelist = new[]
        {
            AppPaths.LogFile,                                  // errors.txt
            AppPaths.RotatedLogFile,                           // errors.txt.old
            Path.Combine(AppPaths.DataDir, "vlc-debug.txt"),
            Path.Combine(AppPaths.DataDir, "vlc-native.log"),
        };

        foreach (var src in whitelist)
        {
            try
            {
                if (!File.Exists(src)) continue;
                var info = new FileInfo(src);
                // 单个文件超过 8MB 就不收了: 那多半是原生日志开着跑了一整晚,
                // 用户发不出去, 而且里面真正的线索在末尾(最近一次)。
                if (info.Length > 8 * 1024 * 1024) continue;
                var entry = zip.CreateEntry("logs/" + Path.GetFileName(src), CompressionLevel.Optimal);
                using var src2 = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = entry.Open();
                src2.CopyTo(dst);
            }
            catch
            {
                // 收不到就跳过, 不能让整个报告失败
            }
        }

    }

    /// <summary>
    /// 收崩溃笔记(异常码 + 出错地址)。
    /// ★ 这是崩溃时唯一"人能直接读"的线索 —— 不用打开 WinDbg 就能看出
    ///   是 0xC0000005 还是 0xC000041D、出错地址落在哪个区间。
    /// </summary>
    private static void AddCrashNotes(ZipArchive zip, string? dumpDirectory)
    {
        try
        {
            var notes = Path.Combine(dumpDirectory ?? CrashDumpService.DefaultDumpDirectory, "crash-notes.txt");
            if (!File.Exists(notes)) return;
            var entry = zip.CreateEntry("logs/crash-notes.txt", CompressionLevel.Optimal);
            using var src = new FileStream(notes, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
        catch { /* 收不到就算了 */ }
    }

    /// <summary>收崩溃转储文件。</summary>
    private static void AddDumpFiles(ZipArchive zip, string? dumpDirectory)
    {
        var files = CrashDumpService.ListDumpFiles(dumpDirectory);
        foreach (var f in files)
        {
            try
            {
                var info = new FileInfo(f);
                // 单个转储上限 50MB: 正常情况只有几百 KB(见 CrashDumpService 的实测),
                // 超过这个数说明是"全量转储"那类大文件 —— 用户发不出去, 收进来只是
                // 让整个 zip 变成一个发不出去的附件。
                if (info.Length > 50 * 1024 * 1024) continue;
                var entry = zip.CreateEntry("dumps/" + Path.GetFileName(f), CompressionLevel.Optimal);
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
            catch
            {
                // 跳过
            }
        }
    }

    /// <summary>报告正文。</summary>
    private static string BuildReportText(string? dumpDirectory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BiliDesk 诊断报告");
        sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("== 程序 ==");
        sb.AppendLine("版本: " + App.AppVersion);
        sb.AppendLine("可执行文件: " + RedactUserProfile(SafeAppBaseDirectory()));
        sb.AppendLine("运行时: " + RuntimeDescription());
        sb.AppendLine("进程启动时间: " + SafeProcessStartTime());
        sb.AppendLine();

        sb.AppendLine("== 系统 ==");
        sb.AppendLine("OS: " + SafeOsDescription());
        sb.AppendLine("系统位数: " + SafeIs64Bit());
        sb.AppendLine("CPU 核心数: " + Environment.ProcessorCount);
        sb.AppendLine("显卡: " + (GpuDescription() ?? "(未取到)"));
        sb.AppendLine();

        sb.AppendLine("== 播放相关设置 ==");
        AppendSetting(sb, "视频输出模块", () => Svc.Settings.VideoOutputMode.ToString());
        AppendSetting(sb, "硬件解码", () => Svc.Settings.HardwareDecodingEnabled ? "开" : "关");
        AppendSetting(sb, "网络缓冲(ms)", () => Svc.Settings.NetworkCachingMs.ToString(CultureInfo.InvariantCulture));
        AppendSetting(sb, "缓冲策略", () => Svc.Settings.AutoBufferPolicy ? "自动" : "手动");
        AppendSetting(sb, "音频重采样", () => Svc.Settings.AudioResamplerMode.ToString());
        AppendSetting(sb, "首选音质", () => Svc.Settings.PreferredAudioTier.ToString());
        AppendSetting(sb, "默认画质", () => Svc.Settings.PreferredQualityQn.ToString(CultureInfo.InvariantCulture));
        AppendSetting(sb, "自动连播", () => Svc.Settings.AutoPlayNext ? "开" : "关");
        sb.AppendLine();

        sb.AppendLine("== 诊断开关 ==");
        sb.AppendLine("崩溃转储: " + (CrashDumpService.IsEnabled() ? "已开启" : "未开启"));
        sb.AppendLine("转储目录: " + RedactUserProfile(CrashDumpService.CurrentDumpDirectory() ?? "(无)"));
        sb.AppendLine("libvlc 原生日志: " + (Environment.GetEnvironmentVariable("BILIDESK_VLC_DEBUG") == "1" ? "开" : "关"));
        sb.AppendLine();

        // ★ 冻结统计: 用户报"卡住"时, 这是最直接的证据 ——
        //   发生过几次、最近一次是什么时候、当时卡在做什么。
        var watchdog = Svc.FreezeWatchdog;
        sb.AppendLine("== UI 线程冻结统计 ==");
        if (watchdog == null)
        {
            sb.AppendLine("(看门狗未启动)");
        }
        else
        {
            sb.AppendLine("本次运行冻结次数: " + watchdog.FreezeCount);
            sb.AppendLine("最近一次: " + watchdog.LastFreezeSummary);
        }
        sb.AppendLine();

        sb.AppendLine("== 崩溃转储文件 ==");
        var dumps = CrashDumpService.ListDumpFiles(dumpDirectory);
        if (dumps.Length == 0)
        {
            sb.AppendLine("(没有转储文件 —— 崩溃转储开关没开, 或还没崩过)");
        }
        else
        {
            foreach (var f in dumps)
            {
                try
                {
                    var fi = new FileInfo(f);
                    // ★ 用 ByteSize 而不是 "字节/1024/1024": 正常转储只有 300KB 左右,
                    //   整除下来会显示成 "0 MB", 看着像"没写成功"。
                    sb.AppendLine($"{Path.GetFileName(f)}  {Helpers.ByteSize.Format(fi.Length)}  " +
                                  $"{fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                }
                catch { sb.AppendLine(Path.GetFileName(f)); }
            }
        }
        sb.AppendLine();

        // 崩溃笔记(异常码 + 出错地址)—— 崩溃时唯一"人能直接读"的线索
        var notesDir = dumpDirectory ?? CrashDumpService.DefaultDumpDirectory;
        var crashNotes = Path.Combine(notesDir, "crash-notes.txt");
        if (File.Exists(crashNotes))
        {
            sb.AppendLine("== 崩溃笔记(异常码 / 出错地址) ==");
            try { sb.AppendLine(File.ReadAllText(crashNotes).Trim()); }
            catch { sb.AppendLine("(读取失败)"); }
            sb.AppendLine();
        }

        sb.AppendLine("== 事件查看器(最近的崩溃记录) ==");
        sb.AppendLine(EventLogExcerpt());
        sb.AppendLine();

        sb.AppendLine("== 运行时现场 ==");
        sb.AppendLine(DiagnosticsState.Describe());
        return sb.ToString();
    }

    /// <summary>
    /// 把路径里的**用户名**换成占位符。
    ///
    /// ★★★ 为什么要做(2026-11-28 审查时补的):
    ///   报告抬头写着"不含登录凭据与个人信息", 但可执行文件路径常常是
    ///   <c>C:\Users\张三\Desktop\BiliDesk\</c> —— **用户名就在里面**。
    ///   这种"说明与实际不符"的承诺比不写更糟: 用户以为没事, 结果把名字发出去了。
    ///   ⇒ 把 <c>C:\Users\&lt;某人&gt;</c> 这一段替换成 <c>%USERPROFILE%</c>。
    ///   (只处理这个最常见的位置; 用户自定义到别处的目录名本身不含身份信息。)
    /// </summary>
    private static string RedactUserProfile(string path)
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile)) return path;
            return path.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>读一个设置项并写进报告; 读失败就写"(读取失败)"。</summary>
    private static void AppendSetting(StringBuilder sb, string name, Func<string> read)
    {
        string value;
        try { value = read(); }
        catch (Exception ex) { value = "(读取失败: " + ex.GetType().Name + ")"; }
        sb.AppendLine($"{name}: {value}");
    }

    private static string SafeAppBaseDirectory()
    {
        try { return AppContext.BaseDirectory; } catch { return "(未知)"; }
    }

    private static string RuntimeDescription()
    {
        try
        {
            return Environment.Version + " / " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        }
        catch { return "(未知)"; }
    }

    private static string SafeProcessStartTime()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch { return "(未知)"; }
    }

    private static string SafeOsDescription()
    {
        try
        {
            return Environment.OSVersion.VersionString;
        }
        catch { return "(未知)"; }
    }

    private static string SafeIs64Bit()
    {
        try { return Environment.Is64BitProcess ? "64 位" : "32 位"; }
        catch { return "(未知)"; }
    }

    /// <summary>
    /// 显卡型号。
    ///
    /// ★ 为什么显卡型号重要: 用户报的"卡死"高度怀疑与显卡驱动/覆盖层有关
    ///   (NVIDIA overlay 会把本程序当成游戏注入, 见 VlcCore 的诊断笔记)。
    ///
    /// ★★★ 为什么读**注册表**而不是 WMI(2026-11-28 审查时推翻的上一版):
    ///   上一版用 COM late binding 调 <c>WbemScripting.SWbemLocator</c>, 探针一测
    ///   永远是"(未取到)" —— 因为拿到的对象是 <c>System.__ComObject</c>,
    ///   它上面**没有**反射元数据, <c>GetProperty("_NewEnum")</c> 一律返回 null,
    ///   整条链路在第一句就断了(还看不出来: catch 把它吞成了"未取到")。
    ///   要修就得全程 <c>InvokeMember</c>, 又啰嗦又要管 COM 引用释放。
    ///   ⇒ 改成**读注册表**: 显示适配器在
    ///     <c>HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}</c>
    ///     下面, 每个 <c>0000</c>/<c>0001</c>… 子键有 <c>DriverDesc</c> 与
    ///     <c>DriverVersion</c>。同步、无依赖、无 COM 生命周期问题。
    ///   ★ 只读 HKLM, 不需要管理员权限。
    /// </summary>
    private static string? GpuDescription()
    {
        const string displayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        try
        {
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(displayClassKey);
            if (classKey == null) return null;

            var parts = new List<string>();
            foreach (var subName in classKey.GetSubKeyNames())
            {
                // 只有 0000/0001/… 这种编号子键是真实适配器;
                // "Properties" / "Configuration" 之类的兄弟键要跳过。
                if (subName.Length != 4 || !int.TryParse(subName, out _)) continue;

                try
                {
                    using var sub = classKey.OpenSubKey(subName);
                    var name = sub?.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var drv = sub?.GetValue("DriverVersion") as string;
                    var entry = string.IsNullOrWhiteSpace(drv) ? name : $"{name} ({drv})";
                    if (!parts.Contains(entry)) parts.Add(entry);   // 多显示器时会有重复项
                }
                catch
                {
                    // 单个子键读不了就跳过
                }
            }

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从事件查看器里捞**本程序**最近的崩溃记录。
    ///
    /// ★ 用 <c>System.Diagnostics.Eventing.Reader</c>(.NET 内置, 不用加包):
    ///   它是 <c>EventLogReader</c> 的新版实现, 直接读 .evtx, 不依赖 EventLog 服务。
    /// ★ 读不到(权限不足/日志被清)就写一行说明, 不让整个报告失败 ——
    ///   报告里少一段远好过整份导不出来。
    ///
    /// ★★★ 必须做两件事(2026-11-28 审查时补的, 两个都是实测踩出来的):
    ///   ① **只留本程序的记录**。系统 Application 日志里混着这台机器上**所有**程序的崩溃;
    ///      不筛的话用户发来的报告里全是 msedgewebview2、别的游戏、安装程序……
    ///      真正的 BiliDesk 崩溃被埋在一堆噪音里, 反而更难找。
    ///   ② **限制总读取条数**(不只是命中条数)。上一版只数"命中了几条",
    ///      如果前几千条都不匹配就会一路读到日志末尾 —— 用户机器上那可能有几万条,
    ///      导出按钮会卡十几秒(而它在后台线程, 用户只看到"点了没反应")。
    /// </summary>
    private static string EventLogExcerpt()
    {
        const string logName = "Application";
        var wantedProviders = new[] { "Application Error", ".NET Runtime", "Windows Error Reporting" };

        // 本程序的可执行文件名(带 .exe): "BiliDesk.exe"
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "BiliDesk.exe");

        EventLogReader? reader = null;
        try
        {
            var query = new EventLogQuery(logName, PathType.LogName)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };
            reader = new EventLogReader(query);

            var sb = new StringBuilder();
            var matched = 0;
            var scanned = 0;

            while (matched < EventLogEntryLimit && scanned < EventLogScanLimit)
            {
                var ev = reader.ReadEvent();
                if (ev == null) break;
                scanned++;
                try
                {
                    var provider = ev.ProviderName ?? "";
                    if (!wantedProviders.Any(w => provider.Contains(w, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // FormatDescription 在"消息 DLL 缺失"时会抛 —— 单独兜住,
                    // 否则一条坏记录会把后面所有好记录一起带走。
                    string desc;
                    try { desc = ev.FormatDescription(); }
                    catch { desc = "(这条记录的消息正文无法解析)"; }

                    // ★ 只留**本程序真的崩了**的那几条。
                    if (!IsCrashOfThisApp(desc, exeName)) continue;

                    sb.AppendLine($"[{ev.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] " +
                                  $"{provider} (ID {ev.Id})");
                    if (!string.IsNullOrWhiteSpace(desc))
                        sb.AppendLine(Indent(desc));
                    sb.AppendLine();
                    matched++;
                }
                finally
                {
                    ev.Dispose();
                }
            }

            if (sb.Length == 0)
            {
                return scanned >= EventLogScanLimit
                    ? $"(最近 {EventLogScanLimit} 条应用日志里没有找到本程序的崩溃记录)"
                    : "(最近没有找到本程序的崩溃记录)";
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "(读取事件日志失败: " + ex.GetType().Name + " —— 通常是没有权限)";
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>
    /// 这条事件记录是不是**本程序**的崩溃。
    ///
    /// ★ 为什么需要它(2026-11-28 审查时实测出来的):
    ///   Windows 的 Application 日志里混着这台机器上**所有**程序的崩溃。不筛的话
    ///   用户发来的报告里全是 msedgewebview2、别的游戏、安装脚本……
    ///   真正的崩溃被埋在一堆噪音里, 反而更难找。
    ///
    /// ★★★ 为什么不能只判 "消息里含 BiliDesk":
    ///   崩溃消息里常带**源码路径**(<c>C:\...\BiliDesk-main\...</c>), 于是任何从那个
    ///   目录启动的进程(比如探针用 dotnet.exe 跑)都会被误认成 BiliDesk 崩溃 ——
    ///   探针实测时报告里就混进了一堆 dotnet.exe 的记录。
    ///   ⇒ 必须按 Windows 自己写的**那一段**匹配:
    ///     · .NET Runtime(1026) / WER: <c>Application: BiliDesk.exe</c>
    ///     · Application Error(1000): <c>Faulting application name: BiliDesk.exe</c>
    ///
    /// ★ 抽成纯函数是为了能单测(见 DiagnosticReportTests)。
    /// </summary>
    public static bool IsCrashOfThisApp(string message, string exeName)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(exeName)) return false;
        return message.Contains("Application: " + exeName, StringComparison.OrdinalIgnoreCase)
               || message.Contains("Faulting application name: " + exeName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>给多行文本加一级缩进, 便于在报告里看清结构。</summary>
    private static string Indent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select(l => string.IsNullOrEmpty(l) ? l : "    " + l));
    }
}
