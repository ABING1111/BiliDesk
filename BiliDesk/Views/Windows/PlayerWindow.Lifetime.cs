using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // ★ 必须在最前面: 下面会把全屏态(WindowStyle/WindowState)还原成普通窗口,
        //   之后再读几何就变成了还原后的值, 记忆会失效。
        SavePlayerWindowPlacement();

        // 为什么要"先隐藏 -> 后台拆解 -> 再真正 Close":
        // LibVLC 的 Stop/Dispose 会阻塞等待解复用/解码线程退出(实测约 1 秒),
        // 原先在 UI 线程同步调用, 表现就是"点了关闭要卡一下窗口才消失"。
        // 现在第一次关闭请求只做两件极快的事(置标志 + 隐藏窗口), 用户看到的是瞬间关闭;
        // 慢的原生拆解丢到后台线程, 完成后回来真正 Close()。
        if (!_teardownDone)
        {
            e.Cancel = true;
            // 必须在 Stop() 之前抓取进度: 一旦拆解开始, _mp.Time 会归零/_mp 会被 Dispose,
            // 再取就只能拿到 0, 云端历史会丢位置。这里同步取好数值交给 fire-and-forget 任务。
            var finalAid = _aid;
            var finalCid = _currentCid;
            var finalBvid = _currentBvid;
            var finalProgressMs = 0L;
            var finalLength = 0L;
            try
            {
                if (_mp != null)
                {
                    finalLength = _mp.Length;
                    var t = _mp.Time;
                    if (t > 0) finalProgressMs = t;
                }
            }
            catch { /* 播放器已失效 */ }
            var finalProgress = finalProgressMs / 1000;
            if (finalAid > 0 && finalCid > 0)
                _ = Svc.HistorySync.ReportNowAsync(finalAid, finalCid, finalProgress);
            // 本机进度也在 Stop() 之前抓取并落盘 —— 关窗后云端可能还要等一会儿,
            // 本机记录是"下次打开立刻续上"的兜底。
            Svc.PlaybackProgress.SaveNow(finalBvid, finalCid, finalProgressMs, finalLength,
                _isLive, _isLocalPlayback);

            _closing = true;

            // 直播弹幕: 立刻取消 WS 并退订事件 —— 关窗后后台线程不能再往队列/UI 里塞东西。
            // ★ Dispose 只发取消信号、不等待, 不会拖慢关闭。
            StopLiveDanmaku();

            // 弹幕逐帧推进器订阅的是 CompositionTarget.Rendering —— 它是**静态**事件、
            // 不随窗口销毁, 所以必须显式解绑, 否则窗口关了渲染回调还在,
            // 回调里又摸 DanmakuCanvas —— 轻则空转, 重则已释放对象。
            StopDanmakuClock();

            // ★ 立刻停掉声音 —— 这是"窗口关了但声音还响半秒"的修法。
            //
            // 为什么会响: 真正的 Stop() 会阻塞约 1 秒, 只能丢到后台线程去做(见 TeardownAndCloseAsync),
            // 而 Hide() 之后窗口已经看不见了、音频却还在播 —— 用户听到的就是"关掉后余音半秒"。
            //
            // Pause 是非阻塞的, 在 UI 线程当场生效, 于是声音在 Hide() 之前就断了。
            // 位置必须在 `_closing = true` 之后、DetachVideoOverlay 之前 —— 与上面读进度是同一个
            // "拆解尚未开始"的安全窗口, 再晚就有碰已释放原生指针的风险。
            // ★ 这里**不要**再叠一句 `_mp.Mute = true`: 实测(2026-09-30 探针)在 LibVLCSharp 3.8 +
            //   本机 libvlc 上 `MediaPlayer.Mute` 是不可靠读数 —— 全新进程里第一个播放器读回就是
            //   True, 显式置 false 也改不动; 往它上面写只会留下一个查不出真假的静音隐患。
            //   停声靠 Pause 就够了。
            try { _mp?.SetPause(true); } catch { /* 已经停了/正在拆解 */ }

            _progressTimer?.Stop();
            _kaomojiTimer?.Stop();
            // 手势定时器必须停掉: 它们可能在窗口已经关闭之后才 Tick, 而 Tick 里会去碰
            // 正在被 Dispose 的 _mp。访问已释放的原生指针不是托管异常, try/catch 拦不住,
            // 表现为进程级访问违例(0xc0000005)。反复"打开-关闭"时这几率会被放大。
            _longPressTimer?.Stop();
            _singleClickTimer?.Stop();
            // 长按快进也要收: 松手恢复速率的调用会碰 _mp, 而这时 _mp 可能正在 Dispose
            // (与上面两个手势定时器同理)
            StopFastForward();
            _localToastTimer?.Stop();
            _controlsHideTimer?.Stop();
            StopVolumeOsd();
            StopSpeedOsd();
            // 拖动气泡: 关闭时若还挂着, 会跟着浮层窗口一起残留在屏幕上。
            HideProgressDragHint();

            // 先把覆盖层摘掉再隐藏窗口, 否则底部控制栏会以残影形式留在屏幕上
            DetachVideoOverlay();

            // 从全屏态直接关闭时, 先摘掉"无边框 + 最大化"这个组合。
            // 该状态下窗口句柄的清理容易不干净(参见 Esc 分支里的同款处理),
            // 反复"全屏打开 -> 直接关闭"会累积成残留窗口/句柄, 也是原生访问违例的温床。
            if (WindowStyle == WindowStyle.None)
            {
                try
                {
                    // 进全屏时 WS_CAPTION 被摘掉了(盖任务栏用, 见 ApplyFullscreenLayout):
                    // 这里要切成 SingleBorderWindow, 必须先把样式位补回来, 否则系统标题栏缺失。
                    DwmInterop.RestoreCaption(this);
                    WindowStyle = WindowStyle.SingleBorderWindow;
                    WindowState = WindowState.Normal;
                }
                catch { /* 已经处于不可切换状态, 忽略 */ }
            }

            // 立刻隐藏, 感知上就是"秒关"; 拆解过程在后台继续
            Hide();

            _ = TeardownAndCloseAsync();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 异步拆解原生资源后真正关闭窗口。
    /// WPF 控件(VideoView)必须在 UI 线程释放, 只有会阻塞的 LibVLC Stop/Dispose 放到后台线程。
    /// </summary>
    private async Task TeardownAndCloseAsync()
    {
        var mp = _mp;

        // 顺序很关键: **先 Stop, 再解绑 Hwnd**。
        //
        // 反过来写(VideoView.MediaPlayer = null 会走到 libvlc_media_player_set_hwnd(NULL))
        // 有两个问题:
        //   1. 视频还在播的情况下把 Hwnd 清成 NULL, LibVLC 会另建一个属于它自己的顶层输出窗口,
        //      关闭瞬间就会闪出一个"多出来的视频窗口";
        //   2. "边播边换 HWND, 紧接着 Stop" 正是原生层最容易踩访问违例(0xc0000005)的组合 ——
        //      而这恰好对应用户反馈的"反复打开关闭后偶发 Unexpected parameters"。
        // Stop 会阻塞等待解码线程退出(约 1 秒), 所以先丢到后台线程。
        if (mp != null)
        {
            await Task.Run(() =>
            {
                try { mp.Stop(); } catch { /* 可能已经停了 */ }
            });
        }

        // 回到 UI 线程解绑(VideoView 是 DependencyObject, 只能在 UI 线程操作)
        try { VideoView.MediaPlayer = null; } catch { }
        try { VideoView.Content = null; } catch { }
        // 释放 VideoView 自己持有的原生子窗口 + ForegroundWindow
        try { VideoView.Dispose(); } catch { }

        // MediaPlayer 的 Dispose 同样可能有内部等待, 也放后台
        if (mp != null)
        {
            await Task.Run(() =>
            {
                try { mp.Dispose(); } catch { }
            });
        }

        _teardownDone = true;
        try { Close(); } catch { /* 应用可能已在退出 */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        // 后台合流任务要停: 窗口都没了, 下完也没地方切(而且它还在往磁盘写)
        CancelRemuxPrefetch();
        // ★ 全部定时器在这里统一停 —— 漏掉任何一个都会让整个窗口漏掉(见 StopAllTimers 的说明)
        StopAllTimers();
        StopVolumeOsd();
        StopSpeedOsd();
        HideProgressDragHint();
        // 正常路径下 _mp / VideoView 已在 TeardownAndCloseAsync 里释放过了,
        // 这里只兜底那些没走完正常关闭流程的情况(例如构造中途抛异常),
        // 避免对同一个 MediaPlayer 重复 Dispose。
        if (!_teardownDone)
        {
            try { _mp?.Stop(); } catch { }
            try { _mp?.Dispose(); } catch { }
            try { VideoView?.Dispose(); } catch { }
        }
        // 注意: 绝不释放 _libVLC —— 它是进程级共享实例(见 VlcCore),
        // 由 App.OnExit 统一释放。窗口级别的反复创建/销毁不会再触碰原生库生命周期。
        if (_playerRegistered)
        {
            _playerRegistered = false;
            VlcCore.UnregisterPlayer();
        }
        Svc.Toast.Shown -= OnToast;
        Svc.Session.Changed -= UpdateButtonState;
        Svc.Settings.DanmakuSettingsChanged -= OnDanmakuSettingsChanged;
        Helpers.UiFont.Changed -= OnUiFontChanged;
        // 跳过赞助片段: 退订 + 掐掉还没回来的查询
        Svc.Settings.SponsorBlockChanged -= OnSponsorSettingsChanged;
        CancelSponsorFetch();
        // 主动松掉"占地大"的那几处引用, 再请求一次回收 + 把空出来的物理页交还系统。
        // 顺序不能反: 先松引用, 紧接着那次回收才有东西可收 —— 对用户就是"关掉视频内存数字马上掉"。
        ReleaseHeavyContent();
        MemoryTrim.RequestTrimAndRelease();
        // 把焦点/激活权交还主窗口, 避免关闭后主界面不响应输入。
        // 但如果此刻已经有新的播放器窗口接管(_window 已指向别人), 就绝不能抢焦点 ——
        // 否则新窗口会被挤到后台, 看起来就像"点了视频又立刻退出了"。
        if (Svc.Player.IsCurrent(this))
        {
            try { (System.Windows.Application.Current?.MainWindow)?.Activate(); } catch { }
        }
    }

    /// <summary>
    /// 停掉本窗口拥有的**全部** DispatcherTimer。
    ///
    /// ★ 这不是"顺手收拾一下", 而是防内存泄漏的关键一步, 每一个都不能漏:
    ///   DispatcherTimer 一旦 Start 就被 Dispatcher 长期持有, 而它的 Tick 委托几乎都捕获了 this ——
    ///   只要**有一个还在跑**, 整个已关闭的 PlayerWindow(连同它手里的弹幕列表、评论树、位图、
    ///   整棵可视树)就永远回收不掉。而播放器是"看完就关、下次再看新建一个"的, 于是
    ///   **每看一个视频就永久泄漏一个窗口**。
    ///
    /// 2026-09-27 实测: `_controlsHideTimer` 就是漏掉的那一个 —— 它只在 HideControls/ShowControls
    /// 里停, 而"刚动过鼠标(倒计时重启)就把窗口关掉"正好落在它还在跑的时刻。用户报的
    /// "看视频内存涨很多、关掉也不回落" 就是它攒出来的。
    ///
    /// ★ 2026-10-01: 清单本身已改成**反射收集**(见 TimerFields), 所以"加定时器必须回来补一笔"
    /// 这条规矩**作废了** —— 新加的定时器会被自动停掉。原先靠"探针里一条断言守这份白名单"
    /// 来兜底, 现在兜底换成了机制本身: 漏写不再可能, 探针那条断言也就不再是必需项。
    /// </summary>
    private void StopAllTimers()
    {
        foreach (var f in TimerFields)
        {
            // 防御: 构造函数可能在某个定时器初始化之前就抛了(LibVLC 加载失败时), 所以取不到就跳过
            if (f.GetValue(this) is DispatcherTimer t) t.Stop();
        }
    }

    /// <summary>
    /// 本类(以及它的基类链上, 直到 <see cref="Window"/>)声明的**全部** DispatcherTimer 字段。
    ///
    /// ★ 2026-10-01: 这里原来是一份手打的 8 行白名单, 漏一个就是泄漏一个窗口 ——
    ///   2026-09-27 实测漏掉的就是 `_controlsHideTimer`, 用户报"看视频内存涨很多、关掉也不回落"。
    ///   改用反射收集之后, **加定时器不必再记得回来改这里**, 这类 bug 从根上没了。
    ///
    /// 为什么按**字段类型**收集而不是按名字/按白名单:
    ///   · 按名字 ⇒ `_localToastTimer` 声明时写的是全限定名
    ///     `System.Windows.Threading.DispatcherTimer?`(不是短名), 白名单迟早漏掉它;
    ///   · 显式列全类型 ⇒ 谁新增都跑不掉, 漏不掉就是漏不掉。
    ///
    /// 遍历基类链是防"以后有人把某个定时器挪到 FluentWindow 上"; 只取本类的话挪过去就会失效。
    /// 类型化只扫一次(静态初始化), 运行期开销可忽略。
    /// </summary>
    private static readonly FieldInfo[] TimerFields = CollectTimerFields();

    private static FieldInfo[] CollectTimerFields()
    {
        var found = new List<FieldInfo>();
        for (var t = typeof(PlayerWindow); t != null && t != typeof(Window); t = t.BaseType)
        {
            found.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => typeof(DispatcherTimer).IsAssignableFrom(f.FieldType)));
        }
        return found.ToArray();
    }

    /// <summary>
    /// 关窗时主动松掉几处"占地大"的引用。
    ///
    /// 窗口整体被回收当然也能释放它们, 但那要等下一次 GC 真的跑到那个代;
    /// 这里先断开, 紧接着的那次 RequestTrimAndRelease 才有东西可收。
    /// 顺序上它是"先松手再回收"的前半截, 少了它回收基本收不到什么。
    /// </summary>
    private void ReleaseHeavyContent()
    {
        try { DanmakuCanvas?.Children.Clear(); } catch { }
        try { _danmaku.Clear(); } catch { }
        try { _danmakuRaw.Clear(); } catch { }
        _sponsorRaw = Array.Empty<SponsorSegment>();
        _sponsorActive = Array.Empty<SponsorSegment>();
        _sponsorHandled.Clear();
        if (CommentList != null) CommentList.ItemsSource = null;
        // 评论集合本身也要放掉: 关闭窗口时 ItemsSource 清空了, 但集合还拎着几十个
        // CommentItem(含 RichTextBox 用的富文本/图片), 不 clear 等于没松手。
        ResetCommentCardParts();
        _commentItems.Clear();
        _commentContentWindowTimer?.Stop();
        _commentContentWindowTimer = null;
        _commentCooldownTimer?.Stop();
        _commentCooldownTimer = null;
        // 封面/头像的 ImageBrush 也是"挂着位图"的引用, 一并放掉
        if (UpAvatarBox != null) UpAvatarBox.Background = null;
    }

    /// <summary>音频链路诊断是否已经记过(每个片源只记一次)</summary>
    private bool _audioStateLogged;

    /// <summary>
    /// 每个片源只在**第一次** Playing 时记一条音频链路状态。
    ///
    /// 为什么需要它: "画面正常但没有声音"这类问题离线复现不了 —— 是没挂上音轨、还是音量被
    /// 压到 0、还是走了单文件源, 只有起播那一刻的原生状态能回答。Playing 之后 aout 才建立,
    /// 所以这个时机是唯一能读到有意义数值的地方(起播前原生音量读回 -1)。
    /// 记一次就够: Playing 在缓冲/seek 后会反复触发, 每次都写会把日志冲掉。
    /// </summary>
    private void LogAudioStateOnce()
    {
        if (_audioStateLogged) return;
        _audioStateLogged = true;
        try
        {
            WriteLibVLCDebug(
                $"[Audio] bvid={_currentBvid} cid={_currentCid} qn={_currentQn} " +
                $"音轨数={_mp.AudioTrackCount} 原生音量={_mp.Volume} 应用音量={_volumeCache} " +
                $"独立音轨={(string.IsNullOrEmpty(_lastAudioUrl) ? "无(单文件源)" : "有")}\n");
        }
        catch { /* 诊断信息拿不到就算了, 绝不影响播放 */ }
    }

    /// <summary>
    /// LibVLC 诊断日志(选流/初始化环境/播放失败时的上下文)。
    ///
    /// 两个发布前修掉的问题:
    ///   1. 旧版写到 <c>AppContext.BaseDirectory</c> —— 装进 Program Files 后该目录**不可写**,
    ///      日志会永远静默丢失; 现在写到数据目录(与 errors.txt 同一处)。
    ///   2. 旧版无限追加(开发机上已积到 600KB+); 现在超过 256KB 就整份重来 ——
    ///      这份日志只在"排查最近一次播放问题"时有价值, 老内容没必要留。
    /// </summary>
    /// <summary>诊断用: 描述手势层当前底色(纯黑 / 近透明 / 其它)</summary>
    private string DescribeVideoAreaBg()
    {
        if (VideoAreaRoot == null) return "(null)";
        return VideoAreaRoot.Background switch
        {
            SolidColorBrush sb => sb.Color.ToString(),
            null => "(none)",
            var b => b.GetType().Name,
        };
    }

    /// <summary>诊断用: 安全读一次播放位置(日志里不能因为异常就丢整条记录)</summary>
    private long SafeReadTime()
    {
        try
        {
            var t = _mp?.Time ?? -1;
            return (t < 0 || t > 24L * 3600 * 1000) ? -1 : t;
        }
        catch { return -1; }
    }

    /// <summary>原生日志(排查最近一次播放问题用, 见 WriteLibVLCDebug)</summary>
    private static void WriteLibVLCDebug(string content)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "vlc-debug.txt");
            var f = new FileInfo(path);
            if (f.Exists && f.Length > 256 * 1024) f.Delete();
            File.AppendAllText(path, content);
        }
        catch { /* 静默 */ }
    }
}
