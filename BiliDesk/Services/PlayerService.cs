using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BiliDesk.Views;

namespace BiliDesk.Services;

/// <summary>
/// 原生播放器窗口管理(LibVLC 内核): 复用同一个窗口, 切换视频不重新加载 LibVLC。
/// </summary>
public class PlayerService
{
    private PlayerWindow? _window;

    /// <summary>
    /// 播放器窗口的"存在/播放状态/标题"发生了变化。
    /// 主窗口右下角的「播放页」按钮订阅它来显示/隐藏自己、启停声波律动。
    /// ★ 事件只在 UI 线程发: 值来自 PlayerWindow 的 UI 派发体, 订阅方可以直接摸控件。
    /// </summary>
    public event Action? StateChanged;

    /// <summary>当前是否持有一个可以唤回的播放器窗口(已开始关闭的不算)。</summary>
    public bool HasPlayer => _window is { IsClosing: false };

    /// <summary>播放器是否存在且真的在播放。低频读取, 不要放在逐帧路径里。</summary>
    public bool IsPlaying => HasPlayer && _window!.IsPlaying;

    /// <summary>
    /// 当前视频标题(已去掉窗口标题里的 " - BiliDesk" 后缀)。
    /// 换集时 PlayerWindow 会改自己的 Title, 这里按需读取, 不额外维护一份副本。
    /// </summary>
    public string? CurrentTitle
    {
        get
        {
            var title = _window?.Title;
            if (string.IsNullOrWhiteSpace(title)) return null;
            // 视频详情还没回来时窗口还是 XAML 里的默认标题, 不要把它当成视频标题显示。
            if (title == "BiliDesk 播放器") return null;

            const string suffix = " - BiliDesk";
            return title.EndsWith(suffix, StringComparison.Ordinal)
                ? title[..^suffix.Length]
                : title;
        }
    }

    /// <summary>
    /// 指定的播放器窗口是否仍是当前被复用的那个。
    /// 旧窗口关闭时用它判断"是否已经有新窗口接管", 避免把焦点抢回主窗口、
    /// 把刚打开的新播放器挤到后台(表现上就像"点了视频又立刻退出")。
    /// </summary>
    public bool IsCurrent(PlayerWindow window) => ReferenceEquals(_window, window);

    /// <summary>
    /// 播放视频(复用窗口)。
    ///
    /// ★ <paramref name="bangumi"/>: 番剧选集页把整季分集表带进来(2026-11-19)。
    ///   播放器据此在信息栏里给出「选集」入口与"播完自动下一集"。
    ///   普通视频传 null ⇒ 自动清掉上一个番剧的选集上下文。
    /// </summary>
    public void PlayVideo(string bvid, string? title = null, BiliDesk.Models.BangumiPlaylist? bangumi = null)
        => Play(title, win => win.PlayVideo(bvid, bangumi));

    /// <summary>
    /// 打开直播间(首页「直播」tab 用)。复用同一个播放器窗口, 走"直播"分支:
    /// 不查视频详情、不加载弹幕、不查三连状态、不上报历史进度。
    /// </summary>
    public void PlayLive(long roomId, string? title = null)
        => Play(title, win => win.PlayLive(roomId, title));

    /// <summary>
    /// 播放本地文件(离线缓存页用)。同样复用播放器窗口, 只是走"本地文件"分支:
    /// 不查接口、不加载弹幕、不上报历史。
    /// </summary>
    public void PlayLocalFile(string path, string? title = null)
        => Play(title, win => win.PlayLocalFile(path));

    /// <summary>
    /// 把已有播放器叫回前台(「播放页」按钮的唯一动作)。
    /// 最小化 → 还原; 被别的窗口盖住 → Activate + SetForegroundWindow。
    /// ★ 只在用户点击这类前台手势里调用, 不做任何轮询/抢焦点。
    /// </summary>
    public void ActivateCurrent()
    {
        var win = _window;
        if (win == null || win.IsClosing) return;

        try
        {
            if (win.WindowState == WindowState.Minimized)
                win.WindowState = WindowState.Normal;
            if (!win.IsVisible) win.Show();
            win.Activate();

            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        }
        catch
        {
            // 窗口正在拆解时可能已经不可用 —— 用户再点一次即可, 不弹错误框
        }
    }

    /// <summary>
    /// 三种播放入口的共同外壳: 取(或新建)播放器窗口 → 交给具体分支起播 → 设标题。
    ///
    /// 差别只在"起播那一行", 以前三个方法把取窗口、判空、设标题各连同它抄了一遍。
    /// 复用闸门(<see cref="GetOrCreateWindow"/>)是这里最要紧的部分, 抄三份意味着
    /// 改它要同步改三处, 漏一处的表现是"某个入口不走复用逻辑"(点了视频又立刻退出)。
    ///
    /// 注: 三个入口以前都带一个 <c>coverUrl</c> 参数并由 4 处调用点传进来, 但**从来没用过**
    /// —— 窗口标题只用到 title。已删除, 别再加回来: 一个从不参与行为的参数会让人以为
    /// 播放器拿得到封面(比如想据此设置窗口图标), 而实际什么也没发生。
    /// </summary>
    private void Play(string? title, Action<PlayerWindow> start)
    {
        var win = GetOrCreateWindow();
        if (win == null) return;

        start(win);
        if (!string.IsNullOrEmpty(title)) win.Title = title + " - BiliDesk";
        // 新窗口/换片后立即广播一次: 主窗口的入口不必等 Playing 事件才出现,
        // 标题也在这时已经写好(按钮的 ToolTip 会读 CurrentTitle)。
        StateChanged?.Invoke();
    }

    private PlayerWindow? GetOrCreateWindow()
    {
        // 关键: 正在关闭的窗口不能复用。
        // 关闭流程是"先隐藏窗口 -> 后台拆解原生资源 -> 最后才真正 Close()"。
        // 这段窗口期内 IsLoaded 仍为 true, 如果直接复用, 拆解结束时的 Close() 会把
        // 刚打开的新视频一起关掉 —— 表现为"点了视频又立刻自动退出"。
        if (_window is { IsLoaded: true } reuse && !reuse.IsClosing)
        {
            if (reuse.WindowState == WindowState.Minimized)
                reuse.WindowState = WindowState.Normal;
            reuse.Activate();
            return reuse;
        }

        PlayerWindow win;
        try
        {
            win = new PlayerWindow();
        }
        catch (Exception ex)
        {
            // LibVLC 原生库初始化失败时构造函数会抛异常, 这里兜住避免整个应用崩掉
            App.ReportError(ex);
            Svc.Toast.Show("播放器初始化失败: " + ex.Message);
            return null;
        }

        // 播放/暂停变化 → 转发给主窗口的「播放页」按钮。
        // ★ 旧窗口晚到的事件不能影响新窗口: 所有出口都再确认一次引用。
        win.PlaybackStateChanged += () =>
        {
            if (ReferenceEquals(_window, win)) StateChanged?.Invoke();
        };

        // 只在引用仍指向自己时才清空, 避免旧窗口的 Closed 把新窗口的引用一起置空
        win.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, win)) return;
            _window = null;
            StateChanged?.Invoke();
        };
        _window = win;
        win.Show();
        return win;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
