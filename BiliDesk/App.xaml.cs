using System;
using System.Collections.Generic;
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
    /// 同步点(漏一个就会出现"关于页写 1.2.3、安装包文件名还是 1.2.2"这种不一致):
    ///   BiliDesk.csproj 的 &lt;Version&gt;、Publish\installer.iss 的 MyAppVersion、发布.ps1 的默认 $Version。
    /// </summary>
    public const string AppVersion = "1.7.2";

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

        // ★★★ 全局异常兜底必须放在**最前面**。
        //   2026-11-07 修: 原来它注册在 Svc.Settings.Load()/Session.Load()/VlcCore.Prewarm()
        //   之后 —— 初始化阶段的任何异常(配置文件被手改坏、LibVLC 原生库加载失败、
        //   数据目录不可写)会直接闪退, 且 errors.txt 里一个字都不会有, 用户与作者都拿不到线索。
        //   处理器自身只引用 AppPaths/App.ReportError, 不依赖任何服务, 前移无副作用。
        DispatcherUnhandledException += (_, args) =>
        {
            ReportError(args.Exception);
            // 致命异常(OOM/栈溢出/非法结构)继续"处理"只会让进程停在更坏的状态:
            // 记完日志就放行, 让进程按它自己的方式结束。
            var fatal = args.Exception is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException;
            if (fatal) return;
            // 弹框节流: 初始化竞态或坏接口可能让这个处理器几秒内被触发十几次,
            // 连环弹框会把用户锁死在"点掉-又弹"里, 看不到任何可操作的东西。
            if (CanShowErrorDialog())
                MessageBox.Show(
                    "程序遇到一个错误, 已记录到\n" + AppPaths.LogFile + "\n\n" + args.Exception.Message,
                    "BiliDesk 出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ReportError(ex);
        };

        // ★★★ 单实例闸门必须放在**最前面**, 在初始化任何服务之前。
        //   为什么: 第二个实例一旦跑起来, 它会 Load 同一份 session.json/settings.json,
        //   再 Save 回去就会覆盖第一个进程刚写的状态(登录态/设置互相打架), 还会多占一份
        //   LibVLC 原生库。所以"不是第一个"时要把整个启动流程掐掉。
        //   把已在跑的实例叫到前台这件事由 SingleInstance 自己通过管道转达(见该文件)。
        if (!SingleInstance.Acquire())
        {
            // 不建任何窗口, 直接退出。用 Shutdown() 而不是 Environment.Exit():
            // 这时还没有窗口/托盘资源, 但走 WPF 的正规退出路径更稳。
            Shutdown();
            return;
        }

        // 系统关机/注销: 必须真的退出, 不能因为"关闭到托盘"把注销流程卡住
        SessionEnding += (_, _) => IsExiting = true;

        // 初始化数据目录与各项服务
        AppPaths.Init();
        Svc.Settings.Load();
        // 列表屏蔽条件存在 settings.json 里, 必须在 Load 之后同步一次。
        // (FilterService 构造时也读, 这行是兜住"更早被 XAML 的 x:Static 提前实例化"的顺序意外)
        FilterService.Instance.Reload();
        Svc.Theme.Initialize();
        Svc.Session.Load();
        // 本机观看历史(history.json)已整体移除: 历史统一看 B 站账号的云端记录。
        // 旧文件不读取也不删除 —— 万一用户日后需要, 数据还在原处。
        Svc.SearchHistory.Load();

        // 本机收藏夹(favorites.json)已整体移除: 收藏统一走 B 站账号的云端收藏夹。
        // 旧文件不再读取, 也不主动删除 —— 万一用户日后需要, 数据还在原处。

        // 后台预热 LibVLC 原生库。
        // ★★★ 2026-11-09: 不再在启动时预热(会让"只看列表"的用户白付约 28MB 常驻内存)。
        //   改为第一次悬停视频卡片时预热(见 VlcCore.PrewarmOnDemand 的说明) ——
        //   "悬停→点击"通常有几百毫秒, 足够把插件扫描跑完; 真没跑完也只是首次打开稍慢。
        //   (VlcCore 仍是进程级单例, 避免播放器反复开关导致的原生崩溃。)

        // 全局中键自动滚动(浏览器同款): 注册 ScrollViewer 类处理器, 一次装好全应用生效
        MiddleClickAutoscroll.Install();

        // 全局滚轮: "按设置的距离 + 缓动曲线滑过去" —— 引擎原生是逐格瞬移,
        // 观感一顿一顿的(2026-11-07 用户要求改成有滑动的曲线)。
        WheelScroll.Install();

        // 顺带清理短视频的本地合流缓存(vcache)。
        // 平时每次合流完都会清一次, 这里再补一道: 用户如果从此不再看短视频,
        // 那批文件就再也没机会被清理了。放线程池里跑, 不占启动时间。
        _ = System.Threading.Tasks.Task.Run(ShortVideoCache.Prune);

        // 封面图磁盘缓存(imgcache)的自动清理。
        // ★ 必须放在启动时做一次: 用户关了程序之后, 上一次会话里攒下的冷门封面
        //   没有机会再被"按下载量触发"的清理碰到 —— 而实测重度使用两天就能堆到 759MB。
        //   放线程池、不 await: 几千个文件的枚举不能挡启动。
        // ★ 运行期间还有一道"攒够 200 次新下载 + 间隔 30 分钟"的清理, 见 CoverLoader.NotifyCacheWritten。
        _ = System.Threading.Tasks.Task.Run(CoverLoader.PruneDiskCache);

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

        // UP 主动态新消息: 应用级轮询, 不依赖动态页是否打开。
        // 启动后立刻拉一次, 之后每 60 秒一次; 导航栏红点与动态页左栏共用这份状态。
        DynamicNotifyService.Instance.Start();

        // ★ 补齐写操作风控 Cookie(2026-11-12 修"换了扫码登录之后点赞报风控"):
        //   用户的 session.json 是上一版登录流程写的, 里面没有 b_nut/sid 这几项,
        //   而它们在 WebView2 的 Profile 里本来就躺着。启动后读一次即可, 用户无感 ——
        //   否则就得让用户"退出登录再登一次"才能用上点赞, 对这个修复来说太贵。
        //   后台做、不 await: WebView2 初始化要几百毫秒, 不能挡启动。
        // ★ 写操作风控预热(2026-10-12 用户报"冷门视频点赞报 -352 风控依然存在"):
        //   把设备指纹激活(buvid ExClimbWuzhi)提前到启动时做, 而不是等用户第一次点赞。
        //   否则用户点下点赞的那一刻要多等一次网络往返, 体感就是"点了没反应"。
        //   只读的静默请求, 失败无副作用。
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500);
            try { await Svc.Api.WarmUpWriteAsync(); } catch { /* 预热失败不阻塞启动 */ }
        });

        BackfillRiskCookiesAsync();

        // ★ 用户**又双击了一次 exe**: 已经在跑的这个实例收到管道通知, 把主窗口叫到前台。
        //   只挡"不双开"而不做这件事, 用户看到的是"双击图标毫无反应", 会以为程序坏了。
        //   ShowRequested 在后台线程触发, 这里派发回 UI 线程再碰窗口。
        SingleInstance.ShowRequested += () =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 复用托盘那套"显示主窗口"的逻辑(它已经处理了最小化还原 + Show + Activate)
                TrayService.Instance.ShowMainWindow();
                // 顺带把主窗口置顶闪一下: 只 Activate 有时会被其它前台窗口压住
                if (SvcWindow.Main is { } w)
                {
                    if (w.WindowState == WindowState.Minimized)
                        w.WindowState = WindowState.Normal;
                    w.Show();
                    w.Activate();
                    w.Topmost = true;
                    w.Topmost = false;   // 立刻取消: 只是借它把窗口顶到最前, 不做常驻置顶
                    w.Focus();
                }
            }));

        // 自动检查更新: 主界面起来后等 6 秒再查(避开启动高峰, 也别让"检查"和首屏加载抢网络)。
        // 查询本身是匿名请求、失败静默, 细节见 UpdateChecker.AutoCheckAsync。
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(6000);
            await UpdateChecker.AutoCheckAsync();
        });
    }

    /// <summary>
    /// 从 WebView2 的 Profile 补齐写操作风控 Cookie(见 <see cref="SessionManager.BackfillRiskCookies"/> 的说明)。
    ///
    /// ★ 用**临时**隐藏的 WebView2(2026-11-07 P4), 不碰用户正在看的那个 —— 初始化一个 WebView2
    ///   要几百毫秒, 不能在启动路径上同步等它, 更不能抢正在显示的控件。
    /// ★ 全程静默: 这只是"锦上添花"的补齐, 失败/取不到都只是维持原状(旧行为), 不该打扰用户。
    /// </summary>
    private static async void BackfillRiskCookiesAsync()
    {
        try
        {
            if (!Svc.Session.HasLogin) return;

            // ★★ 设备指纹(buvid3/buvid4)优先补齐, 且**不依赖 WebView2**(2026-10-12 16:33 实测):
            //   用户重新扫码登录后 session.json 里 buvid3/buvid4/b_nut 全是空的, 点赞照旧报风控。
            //   这三个值 SPI 接口就能领到, 没必要等 WebView2 起不来就整体放弃。
            //   (登录窗口里也补了一次, 但那里是 fire-and-forget 被 Close() 掐断过 —— 双保险。)
            if (string.IsNullOrEmpty(Svc.Session.Current.Buvid3) ||
                string.IsNullOrEmpty(Svc.Session.Current.Buvid4))
            {
                await Svc.Api.EnsureBuvidAsync();
            }

            if (Svc.Session.HasRiskCookies) return;

            // ★ 用**临时**隐藏 WebView2(2026-11-07 P4): 以前是主窗口里常驻一个 1×1 的控件,
            //   初始化过之后那组 Chromium 进程会一直挂到进程退出 —— 登录用户即便从不打开
            //   内嵌网页, 也要常年付这 80~150MB。现在只在补齐这一下用一次, 用完立刻 Dispose。
            //   (详见 WebView2Service.CreateHiddenAsync 的说明。)
            var ok = false;
            try
            {
                await Svc.WebView2.WithHiddenAsync(async wv =>
                {
                    var cookies = await WebView2Service.ReadBiliCookiesAsync(wv);
                    Svc.Session.BackfillRiskCookies(cookies);
                    ok = true;
                });
            }
            catch
            {
                // 临时控件建不起来(WebView2 运行时装不上/被策略禁用): 下面还有兜底
            }

            // ★ WebView2 拿不到也要把 b_nut 补上: 它是登录时刻时间戳, 不依赖浏览器
            //   (2026-10-12: 原来这条路上是 return, 于是"WebView2 起不来"就等于"永远不补 b_nut")
            if (!ok) Svc.Session.BackfillRiskCookies(new Dictionary<string, string>());
        }
        catch
        {
            // 静默: 补不齐就退回改动前的行为(带 6 个 Cookie), 不会更坏
        }
    }

    /// <summary>记录异常到日志文件</summary>
    public static void ReportError(Exception ex)
    {
        try
        {
            lock (LogGate)
            {
                // 上限保护: errors.txt 只保留最近的错误(发布后没有任何清理它的途径,
                // 无限追加会在装了几年后变成几十 MB 的"古董日志", 反而没人看)。
                // ★ 2026-11-07 改为轮转而不是整份删除: 旧版超 1MB 直接 f.Delete(),
                //   正要排查的问题现场说没就没; 挪成 errors.old, 现场永远保留最近两份。
                var f = new FileInfo(AppPaths.LogFile);
                if (f.Exists && f.Length > 1024 * 1024)
                {
                    var old = AppPaths.LogFile + ".old";
                    try { f.MoveTo(old, overwrite: true); }
                    catch { try { f.Delete(); } catch { /* 挪不掉也删不掉, 追加继续 */ } }
                }

                // ★ 带调用栈: SessionManager 这类"守卫主动上报"的合成异常没有原始堆栈,
                //   不补栈的话日志里只有一句话, 定位不到是哪条调用路径触发的。
                // ★ ex 本身有堆栈时原样输出(ToString 已含), 只给"干净"的合成异常补。
                var hasStack = !string.IsNullOrEmpty(ex.StackTrace);
                var body = hasStack ? ex.ToString() : ex + Environment.NewLine + Environment.NewLine +
                    new System.Diagnostics.StackTrace(1);
                File.AppendAllText(AppPaths.LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {body}\n\n");
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>日志写入闸门: 多线程同时 AppendAllText 会抛 IOException("正由另一进程使用"), 串行化之。</summary>
    private static readonly object LogGate = new();

    /// <summary>错误弹框节流: 同一分钟内最多弹两次, 其余只记日志(见 DispatcherUnhandledException 里的说明)。</summary>
    private static DateTime _lastErrorDialogUtc;
    private static int _errorDialogCountThisMinute;

    private static bool CanShowErrorDialog()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastErrorDialogUtc).TotalMinutes >= 1)
        {
            _lastErrorDialogUtc = now;
            _errorDialogCountThisMinute = 0;
        }
        if (++_errorDialogCountThisMinute > 2) return false;
        return true;
    }

    /// <summary>进程退出: 释放共享的 LibVLC 原生实例(窗口关闭时不释放, 避免反复创建销毁)</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        DynamicNotifyService.Instance.Stop();
        // 播放进度: 退出前把内存里的最后一次位置落盘(播放器关窗时通常已经写过, 这里是兜底)。
        Svc.PlaybackProgress.Flush();
        // 设置是"异步合并写"的(见 SettingsStore.Save), 退出前把还在队列里的最后一次写入落盘,
        // 否则刚改完设置就退出会丢(表现是"改了的选项下次启动又变回去了")
        Svc.Settings.Flush();
        // 托盘图标先摘: 不摘的话 Explorer 会缓存一个"幽灵图标"直到鼠标划过
        TrayService.Instance.Dispose();
        VlcCore.Shutdown();
        base.OnExit(e);
    }
}
