using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using BiliDesk.Views;

namespace BiliDesk;

public partial class App : Application
{
    /// <summary>
    /// 程序版本号 —— **单一来源**, 改版本只动这一处。
    /// 同步点(漏一个就会出现"关于页写 1.2.1、安装包文件名还是 1.2.0"这种不一致):
    ///   BiliDesk.csproj 的 &lt;Version&gt;、发布包\installer.iss 的 MyAppVersion、发布.ps1 的默认 $Version。
    /// </summary>
    public const string AppVersion = "1.2.1";

    public static MainViewModel MainVm { get; } = new();

    /// <summary>
    /// "程序真的要退出了"。
    ///
    /// 为什么需要它: 关闭键默认是"最小化到托盘"(见 MainWindow.OnClosing), 那条路径会
    /// Cancel 掉关闭事件 —— 而托盘菜单的「退出」和系统关机走的是同一个应用退出流程,
    /// 必须能绕过它。所有"确实要退出"的入口先把这个置 true 再 Shutdown。
    /// </summary>
    public static bool IsExiting { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 系统关机/注销: 必须真的退出, 不能因为"关闭到托盘"把注销流程卡住
        SessionEnding += (_, _) => IsExiting = true;

        // 初始化数据目录与各项服务
        AppPaths.Init();
        Svc.Settings.Load();
        Svc.Theme.Initialize();
        Svc.Session.Load();
        // 本机观看历史(history.json)已整体移除: 历史统一看 B 站账号的云端记录。
        // 旧文件不读取也不删除 —— 万一用户日后需要, 数据还在原处。
        Svc.SearchHistory.Load();

        // 本机收藏夹(favorites.json)已整体移除: 收藏统一走 B 站账号的云端收藏夹。
        // 旧文件不再读取, 也不主动删除 —— 万一用户日后需要, 数据还在原处。

        // 后台预热 LibVLC 原生库。
        // 原生库初始化 + 插件扫描有几百毫秒开销, 提前到启动阶段跑, 首次点开视频更快;
        // 同时它也是进程级单例(见 VlcCore), 避免播放器窗口反复开关导致的原生崩溃。
        VlcCore.Prewarm();

        // 顺带清理短视频的本地合流缓存(vcache)。
        // 平时每次合流完都会清一次, 这里再补一道: 用户如果从此不再看短视频,
        // 那批文件就再也没机会被清理了。放线程池里跑, 不占启动时间。
        _ = System.Threading.Tasks.Task.Run(ShortVideoCache.Prune);

        // 全局异常兜底: 记录到文件并提示, 避免直接闪退
        DispatcherUnhandledException += (_, args) =>
        {
            ReportError(args.Exception);
            MessageBox.Show(
                "程序遇到一个错误, 已记录到\n" + AppPaths.LogFile + "\n\n" + args.Exception.Message,
                "BiliDesk 出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ReportError(ex);
        };

        // 首次启动: 先过免责声明这道门。
        // 放在建主窗口**之前** —— 不同意就直接 Shutdown, 用户不该看到主界面一闪而过。
        // 老版本升级上来的用户也会被问一次: 声明里有"账号风控/数据丢失风险自担"这类实质内容,
        // 让所有人在同一份文本上确认一次更稳妥。
        if (!Svc.Settings.DisclaimerAccepted)
        {
            // ★ 这一句是"点了同意却退出了程序"的修复。
            //
            // App.xaml 里 ShutdownMode = OnMainWindowClose, 而 WPF 会把**第一个创建的窗口**
            // 认成 Application.MainWindow —— 在这里就是免责声明窗口。于是用户点「同意并继续」、
            // 窗口一关, WPF 认为"主窗口关闭了", 直接在 OnStartup 里把整个进程 Shutdown 掉,
            // 后面那两行创建主窗口的代码根本来不及执行。对外表现就是: 同意完程序自己没了。
            //
            // 改法: 过声明这道门期间先切到"只有显式 Shutdown 才退出", 拿到真正的主窗口后再改回去。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var disclaimer = new Views.Windows.DisclaimerWindow();
            disclaimer.ShowDialog();
            if (!disclaimer.Agreed)
            {
                Shutdown();
                return;
            }
            Svc.Settings.AcceptDisclaimer();
        }

        var main = new MainWindow();
        SvcWindow.Main = main;
        Svc.Navigate = new NavigationDispatcher(main);
        // 显式指定主窗口, 并把关窗即退出这条规则挂到它身上
        // (声明窗口已经占过一次 MainWindow 的位置, 这里不重新指认的话关掉主窗口不会退出程序)
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();

        // 系统托盘: 双击回主界面, 右键可退出(失败只记日志, 不影响启动)
        TrayService.Instance.Initialize();

        // 自动检查更新: 主界面起来后等 6 秒再查(避开启动高峰, 也别让"检查"和首屏加载抢网络)。
        // 查询本身是匿名请求、失败静默, 细节见 UpdateChecker.AutoCheckAsync。
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(6000);
            await UpdateChecker.AutoCheckAsync();
        });
    }

    /// <summary>记录异常到日志文件</summary>
    public static void ReportError(Exception ex)
    {
        try
        {
            // 上限保护: errors.txt 只保留最近的错误(发布后没有任何清理它的途径,
            // 无限追加会在装了几年后变成几十 MB 的"古董日志", 反而没人看)。
            // 超过 1MB 就整份重来 —— 有新错误时它马上又会有内容。
            var f = new FileInfo(AppPaths.LogFile);
            if (f.Exists && f.Length > 1024 * 1024) f.Delete();
            File.AppendAllText(AppPaths.LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>进程退出: 释放共享的 LibVLC 原生实例(窗口关闭时不释放, 避免反复创建销毁)</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        // 设置是"异步合并写"的(见 SettingsStore.Save), 退出前把还在队列里的最后一次写入落盘,
        // 否则刚改完设置就退出会丢(表现是"改了的选项下次启动又变回去了")
        Svc.Settings.Flush();
        // 托盘图标先摘: 不摘的话 Explorer 会缓存一个"幽灵图标"直到鼠标划过
        TrayService.Instance.Dispose();
        VlcCore.Shutdown();
        base.OnExit(e);
    }
}