using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace BiliDesk.Services;

/// <summary>
/// LibVLC 原生实例的进程级单例。
///
/// 为什么必须做成单例:
/// 之前每个播放器窗口都在构造函数里 new 一个 LibVLC, 在窗口关闭时 Dispose —— 而
/// LibVLCSharp 的 Core.Initialize / new LibVLC 属于"全进程只能初始化一次"的原生状态。
/// 快速反复开关播放器窗口时, 原生库被反复创建/销毁, 加上窗口销毁期间仍有原生回调
/// 进入托管代码, 会抛出 Windows 级错误弹窗:
///     "Exception Processing Message 0xc0000005 - Unexpected parameters"
/// (0xc0000005 = 访问违例, 不是 .NET 异常, 所以 DispatcherUnhandledException 兜不住它)
///
/// 现在统一由这里持有, 只在进程退出时释放一次。窗口开关只是挂载/卸载 MediaPlayer,
/// 不再触碰原生库的生命周期, 既消除了崩溃, 也让再次打开播放器变得更快。
/// </summary>
public static class VlcCore
{
    private static readonly object Gate = new();
    private static LibVLC? _instance;
    private static int _livePlayers;

    /// <summary>libvlc.dll / libvlccore.dll 所在目录</summary>
    public static string LibVlcDirectory =>
        Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");

    /// <summary>
    /// libvlc 插件目录(&lt;outdir&gt;/libvlc/win-x64/plugins/&lt;cat&gt;/*.dll)。
    ///
    /// ★ 为什么是"跟 libvlc.dll 放一起"而不是输出根目录(2026-09-26 瘦身时定的):
    ///   libvlccore 的插件扫描找的就是 libvlc.dll **同目录**下的 plugins/。以前我们把 plugins/
    ///   另拷一份到根目录、把 VLC_PLUGIN_PATH 指向那里, 结果发布包里同一套插件出现两次(-90MB)。
    ///   实测(bd-probe-vlc 探针, 在被测输出目录真播一段 MP4): 只留这一份时加载的模块数一模一样(497),
    ///   连 VLC_PLUGIN_PATH 指向不存在的目录都照样能播 —— 说明唯一必需的就是这一份。
    /// </summary>
    public static string PluginsDirectory =>
        Path.Combine(LibVlcDirectory, "plugins");

    /// <summary>
    /// 取得共享 LibVLC 实例; 首次调用时初始化原生库。
    /// 线程安全: 多个窗口/预热线程同时进入只会真正初始化一次。
    /// </summary>
    public static LibVLC Get()
    {
        lock (Gate)
        {
            if (_instance != null) return _instance;

            // VideoLAN.LibVLC.Windows 把原生库部署到 <outdir>/libvlc/win-x64/,
            // 而 LibVLC 默认只在 exe 同目录查找, 所以显式把路径告诉它。
            var libvlcDir = LibVlcDirectory;
            if (Directory.Exists(libvlcDir))
                Environment.SetEnvironmentVariable("LIBVLC_PATH", libvlcDir);

            try { Core.Initialize(Directory.Exists(libvlcDir) ? libvlcDir : null); }
            catch { /* 已初始化过, 忽略 */ }

            // 显式给一份插件绝对路径(和 libvlc.dll 同目录那份是同一个地方)。
            // ★ 不要再传 `--plugin-path=`: VLC 3.0.20 已经删掉了这个选项, 传了会打印
            //   "Warning: option --plugin-path no longer exists"(实测, 它不生效, 只是噪声)。
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", PluginsDirectory);

            // 网络缓冲: 用用户设置(设置页「网络缓冲」, 默认 1000 = 旧版行为)。
            // ★ 媒体级还会再带一份(见 PlayerWindow.BuildMedia), 这里给"没有带选项的路径"
            //   (直播/重播兜底/探针)一个一致的值; 运行中改设置不重建内核, 所以这里只在首次生效。
            // ★ 2026-11-01: 自动档时这里也用 BufferPolicy 算出的值 —— 没有媒体选项的路径
            //   一样该吃到"按带宽挑预读"的收益; 手动档等价于直接用设置值。
            // ★ 实测它对**起播速度**几乎没影响(缓存按网速灌), 影响的是播放中的抗抖动余量。
            //
            // ★★★ 别加**全局** `--demux=avformat`(2026-10-17 试过, 有致命副作用, 已撤回):
            //   它确实能修好 seek —— 实测 25%/70% 落点偏差都是 0ms(基线是 -32万ms)。
            //   **但它会把所有输入(包括 DASH 的 input-slave 音轨)都强制成 avformat**,
            //   且在旧版 3.0.20 + 同进程连开多个 LibVLC 时会让 seek 后直接进 Ended。
            //   ★ 2026-11-05: 现在改在 **Media 级**只给需要的那条流加 `:demux=avformat`
            //     (见 PlayerWindow.BuildMedia), 3.0.24 实测双流 + 远跳/HEVC 均正常;
            //     更重要的是它修掉了 mp4 demux 读不出 fMP4 最后分片、导致片尾静音的 bug。
            //   ⇒ 保持这里不传全局选项, 各片源在 BuildMedia 里自行决定。
            var caching = BufferPolicy.Resolve(BufferMediaKind.VodSingle, null, shortVideo: false,
                SettingsStore.Instance.NetworkCachingMs, SettingsStore.Instance.AutoBufferPolicy);
            _instance = new LibVLC(
                "--intf=dummy",
                "--no-osd",
                "--network-caching=" + caching);

            return _instance;
        }
    }

    /// <summary>
    /// 后台预热: 把原生库初始化 + 插件扫描的开销提前到"用户已经表现出要看的意图"时,
    /// 这样第一次点开视频通常不用再等那几百毫秒。
    ///
    /// ★★★ 2026-11-09: 不再在**启动**时预热。实测(本机 1080p/125%)LibVLC 的插件扫描
    ///   会让进程常驻多约 **28MB 私有内存 / 31MB 工作集** —— 而绝大多数启动会话
    ///   用户根本没点视频。现在改为**第一次悬停视频卡片时**再预热(见 VideoCard.OnMouseEnter),
    ///   既保住了"悬停→点击"这段通常有几百毫秒的准备时间, 又不让只看列表的用户白付这笔内存。
    ///   真的一启动就点视频(没经过悬停)也不会坏, 只是首次打开多等一次初始化。
    ///
    /// ★ 设环境变量 <c>BILIDESK_NO_VLC_PREWARM=1</c> 可完全关闭预热(内存敏感时用)。
    /// 失败只写日志, 不影响启动(真正的错误会在用户点开视频时暴露)。
    /// </summary>
    public static void PrewarmOnDemand()
    {
        if (Environment.GetEnvironmentVariable("BILIDESK_NO_VLC_PREWARM") == "1") return;
        if (Interlocked.Exchange(ref _prewarmStarted, 1) != 0) return;

        _ = Task.Run(() =>
        {
            try { Get(); }
            catch (Exception ex) { App.ReportError(ex); }
        });
    }

    private static int _prewarmStarted;

    /// <summary>当前有没有存活的播放器窗口(供"空闲回收"避让正在播放/暂停的视频)。</summary>
    public static bool HasLivePlayers => Volatile.Read(ref _livePlayers) > 0;

    /// <summary>登记一个窗口级 MediaPlayer(PlayerWindow 构造成功后调用)</summary>
    public static void RegisterPlayer() => Interlocked.Increment(ref _livePlayers);

    /// <summary>注销一个窗口级 MediaPlayer(PlayerWindow 关闭时调用)</summary>
    public static void UnregisterPlayer() => Interlocked.Decrement(ref _livePlayers);

    /// <summary>
    /// 进程退出时释放共享实例。
    /// 只有在"没有任何窗口级 MediaPlayer 存活"时才真正 Dispose:
    /// 退出瞬间若还有播放器没拆干净, 释放原生库正是 0xc0000005 访问违例的高发场景。
    /// 此时宁可交给操作系统回收(进程马上就结束了), 也不要冒弹错误框的风险。
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_instance == null) return;
            if (Volatile.Read(ref _livePlayers) > 0) return;
            try { _instance.Dispose(); } catch { /* 退出阶段忽略 */ }
            _instance = null;
        }
    }
}
