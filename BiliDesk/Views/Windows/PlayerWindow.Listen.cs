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
    // ------------------------------------------------------------ 「听视频」纯音频模式

    /// <summary>
    /// 是否处于「听视频」纯音频模式。
    /// 登记在 <see cref="PersistentFields"/>: 它是**窗口级的播放模式**(同 _isFillWindow),
    /// 用户切到纯音频连听几集是常态, 换片不该把它弹回视频。
    /// </summary>
    private bool _isListenMode;

    /// <summary>
    /// 「听视频」用的 DASH 音轨地址(与 _lastAudioUrl 分开存)。
    ///
    /// ★ 为什么不能复用 _lastAudioUrl: 那个字段的语义是"当前 Media 的 input-slave",
    ///   退出聆听态要用它重建**原视频组合**。聆听态期间它会被改成音轨单流,
    ///   退出时就再也拼不回视频了。
    /// </summary>
    private string? _listenAudioUrl;

    /// <summary>进入聆听态前记下的进度(切流后恢复, 与切清晰度同一套机制)</summary>
    private long _listenResumeMs;

    /// <summary>声波律动定时器(顶栏小 5 根 + 画面区大 7 根共用这一个)</summary>
    private DispatcherTimer? _listenWaveTimer;
    private int _listenWaveTick;

    /// <summary>声波每根条的相位延迟(设计稿 .11s 一档), 单位是 tick</summary>
    private static readonly double[] ListenWavePhase = { 0, 0.11, 0.22, 0.33, 0.44, 0.55, 0.66 };

    /// <summary>
    /// 「听视频」入口的可用性。
    /// 直播是连续流(没有"音轨地址"这种可单取的东西), 本地文件也没有独立音轨 ——
    /// 点了必然失败, 所以整颗按钮收起来而不是让它点了报错(设计稿第 05 节"无音轨就别给")。
    /// </summary>
    private void UpdateListenButtonState()
    {
        if (BtnListen == null) return;
        var usable = !_isLive && !_isLocalPlayback;
        BtnListen.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        // 切到不支持的模式(换成直播/本地文件)时必须把聆听态收掉 —— 否则聆听遮罩会盖在
        // 一个根本没有独立音轨的片源上(用户看到"正在聆听", 实际播的是视频)。
        // ★ 这里只做**状态与视觉**的收尾, 绝不去重建 Media: 本方法是在 ResetForNewMedia
        //   里被调的, 那时 _lastPlayUrl 还是**上一条视频**的地址 —— 让 ExitListenMode 跑
        //   完整流程会用旧地址重新起播一次, 而调用方紧接着就要播新片源了。
        if (!usable && _isListenMode)
        {
            _isListenMode = false;
            ApplyListenMode(false);
        }
    }

    private void OnListenToggleClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_isListenMode) ExitListenMode();
        else EnterListenMode();
    }

    /// <summary>
    /// 进入「听视频」: 切到 DASH 音轨单流播放, 画面区盖上聆听视觉。
    ///
    /// ★ 为什么真的换流(而不是只给 Media 加 :no-video):
    ///   加 :no-video 只是让 LibVLC 不解码画面, **视频流照样在下载** —— 该省的流量一点没省,
    ///   而"听视频"这个功能的全部价值恰恰是省流量(以及省电/让笔记本安静)。
    ///   所以我们改播 playurl 里那条音频地址(input-slave 用的同一条), 视频流一个字节都不拉。
    ///
    /// ★ 起播必然要重建 Media, 所以进度必须自己接上: 走 _resumeAfterSwitchMs 那套
    ///   (Playing 事件里恢复), 与切清晰度完全同一条路 —— 别在这里再写一份恢复逻辑。
    /// </summary>
    private void EnterListenMode()
    {
        if (_closing || _isListenMode) return;
        if (_isLive || _isLocalPlayback) return;
        if (string.IsNullOrEmpty(_listenAudioUrl))
        {
            Svc.Toast.Show("这条视频没有可用的音轨");
            return;
        }

        _isListenMode = true;
        // 先在暂停/播到一半时也能接上: 用当前播放位置(拿不到就从 0 开始)
        try { _listenResumeMs = _mp.Time; } catch { _listenResumeMs = 0; }
        if (_listenResumeMs < 0) _listenResumeMs = 0;

        ApplyListenMode(true);
        // 音轨是单流: audioUrl 传 null, 否则等于给音频文件再挂一条音频
        ShowLoading("正在切换到听视频…");
        _resumeAfterSwitchMs = _listenResumeMs;
        _resumeByStartTime = false;
        try { _mp.Stop(); } catch { /* 已停/未起播 */ }
        PlayMedia(BuildMedia(_listenAudioUrl!, null));
    }

    /// <summary>
    /// 退出「听视频」: 用原来的视频 + 音轨组合重建 Media(进度同样接回来), 收掉聆听视觉。
    ///
    /// ★ 退出时**不能**直接 _mp.Stop() 就完事: 那样视频就再也不播了。
    ///   必须用 _lastPlayUrl/_lastAudioUrl 把"进入之前那条组合"原样拼回去 ——
    ///   这正是这两个字段必须原封不动保留的原因(见 _listenAudioUrl 的说明)。
    /// </summary>
    private void ExitListenMode()
    {
        if (_closing || !_isListenMode) return;
        _isListenMode = false;

        try { _listenResumeMs = _mp.Time; } catch { _listenResumeMs = 0; }
        if (_listenResumeMs < 0) _listenResumeMs = 0;

        ApplyListenMode(false);

        // 直播/本地最后走到了这里也没关系: _lastPlayUrl 就是它们的源, 原样重播
        var url = _lastPlayUrl;
        if (string.IsNullOrEmpty(url))
        {
            // 没有可回溯的源: 只能停下(比播着一个不知道是什么的流强)
            try { _mp.Stop(); } catch { }
            HideLoading();
            return;
        }
        ShowLoading("正在恢复视频…");
        _resumeAfterSwitchMs = _listenResumeMs;
        _resumeByStartTime = false;
        try { _mp.Stop(); } catch { /* 已停 */ }
        // 本地文件是单文件源, 不能带 input-slave
        PlayMedia(BuildMedia(url, _isLocalPlayback ? null : _lastAudioUrl));
    }

    /// <summary>
    /// 聆听态的视觉开关(**唯一改 ListenOverlay / 顶栏按钮形态的地方**)。
    /// 收在一个方法里: 顶栏按钮有 4 个部件(耳机/声波/文案/底色)要跟着状态换,
    /// 散着写必然出现"按钮点亮了但遮罩没出来"这种半状态。
    /// </summary>
    private void ApplyListenMode(bool on)
    {
        if (ListenOverlay != null)
            ListenOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (BtnListen != null)
            BtnListen.Tag = on ? "on" : "off";   // 驱动 ListenGhostBtn 的琥珀 DataTrigger
        if (ListenHeadIcon != null)
            ListenHeadIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        // ★ 胶囊里原先那 5 根小波条已删除(2026-10-06 用户要求"胶囊里的音频波纹去掉,
        //   只保留视频播放器的"), 所以这里不再有 ListenWavePanel 要切显隐。
        //   聆听态画面里那组大波纹(BigWave1~7)照旧由 _listenWaveTimer 驱动, 不受影响。
        // ★ 2026-10-07: 文案改成**一个 TextBlock**整体写入(见 XAML 的说明)——
        //   原来这里是两个 TextBlock 叠放 + 切 Opacity, 那套既让待命态的文字被挤在一侧,
        //   又会在切换瞬间改变胶囊宽度、把顶栏右对齐的按钮推着走。现在只改一个 Text 的值。
        if (ListenLabelText != null)
        {
            ListenLabelText.Text = on ? FormatListenLabel() : "听视频";
            // 状态提示移进 ToolTip: 胶囊里只有"正在聆听 m:ss", 不再重复写一遍"正在聆听"
        }
        if (BtnListen != null)
            BtnListen.ToolTip = on ? "退出听视频" : "听视频 (只播声音, 省流量)";

        if (on)
        {
            // 封面 + 标题: 让"在听什么"一眼可读
            if (ListenOverlayTitle != null) ListenOverlayTitle.Text = _videoTitle;
            LoadListenCover(_coverUrl);
            StartListenWave();
        }
        else
        {
            StopListenWave();
        }
    }

    /// <summary>
    /// 聆听中的完整文案: 「正在聆听 m:ss」。秒数取自播放位置, 每秒由 UpdateListenLabel 刷。
    ///
    /// ★ 2026-10-07: 抽成FormatListenLabel(), 因为胶囊现在只有**一个** TextBlock,
    ///   初次进入与每秒刷新都必须调同一个函数, 否则两处格式会走偏。
    /// </summary>
    private string FormatListenLabel()
    {
        long ms = 0;
        try { ms = _mp.Time; } catch { /* 播放器未就绪 */ }
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        // ★ 分钟**补两位**(设计稿写的是 "0:14", 但那样字串长度会在 10 分钟那一刻从 4 位变 5 位,
        //   而按钮在顶栏右对齐的 StackPanel 里 —— 宽度一变右侧那排窗口按钮就整体挪一下,
        //   与「稍后再看」那条"整排被推走"是同一类问题。补零后小时以内长度恒定。
        //跨过 1 小时仍会多一段(视频普遍不止一小时, 不再为它牺牲可读性)。
        return t.TotalHours >= 1
            ? $"正在聆听 {(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"正在聆听 {(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }

    private void UpdateListenLabel()
    {
        if (ListenLabelText == null || !_isListenMode) return;
        ListenLabelText.Text = FormatListenLabel();
    }

    /// <summary>封面图: 异步取一次(解码宽度按显示尺寸 260 的两倍, 够清晰又不浪费内存)</summary>
    private void LoadListenCover(string? url)
    {
        if (string.IsNullOrEmpty(url) || ListenCover == null) return;
        _ = LoadListenCoverAsync(url);
    }

    private async Task LoadListenCoverAsync(string url)
    {
        var img = await CoverLoader.LoadAsync(url, 520);
        if (img == null || _closing) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closing || ListenCover == null) return;
            ListenCover.Source = img;
        });
    }

    /// <summary>
    /// 启动声波律动。★ 用 DispatcherTimer 而不是 XAML Storyboard:
    ///   ① 七根条各有各的相位与高度, Storyboard 要写十几组 BeginStoryboard;
    ///   ② 更重要的是**退出聆听必须能一处停掉** —— Storyboard 挂在模板上时,
    ///      遮罩收起了动画还在跑, 白烧 CPU(项目里"关掉还在跑"的坑不止一个)。
    /// 33ms ≈ 30fps: 声波是随机的律动, 不需要 60fps, 省一半 CPU。
    /// ★ 现在只驱动聆听态画面里的 7 根大波纹(胶囊里那 5 根小波条已删除)。
    /// </summary>
    private void StartListenWave()
    {
        _listenWaveTick = 0;
        if (_listenWaveTimer == null)
        {
            _listenWaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _listenWaveTimer.Tick += (_, _) =>
            {
                if (_closing || !_isListenMode) { StopListenWave(); return; }
                _listenWaveTick++;
                UpdateListenWave();
                // 文案里的秒数也挂在这个心跳上: 另开一个 1 秒定时器纯属多余
                if (_listenWaveTick % 30 == 0) UpdateListenLabel();
            };
        }
        _listenWaveTimer.Start();
        UpdateListenWave();
    }

    private void StopListenWave() => _listenWaveTimer?.Stop();

    /// <summary>
    /// 逐帧算声波高度。每根条 = 相位偏移的正弦 + 一点点随机抖动,
    /// 比纯随机更像"随声音起伏"(纯随机会跳得没有节奏感)。
    ///
    /// ★ 2026-10-06: 胶囊里那 5 根小波条已按用户要求删除, 现在只驱动**聆听态画面**里的
    ///   大波纹(BigWave1~7)。胶囊待命/聆听都只显示耳机图标, 状态靠琥珀底色与文案区分。
    /// </summary>
    private void UpdateListenWave()
    {
        // tick 到相位: 0.82s 一个周期 ≈ 25 tick(33ms 一 tick)
        const double periodTicks = 25.0;
        var t = _listenWaveTick;

        var big = new[] { BigWave1, BigWave2, BigWave3, BigWave4, BigWave5, BigWave6, BigWave7 };
        for (var i = 0; i < big.Length; i++)
            SetWaveHeight(big[i], t, periodTicks, ListenWavePhase[i], min: 10, max: 32);
    }

    /// <summary>按相位算一根声波条的高度(0.5 + 0.5*sin 映射到 [min,max])</summary>
    private static void SetWaveHeight(Border? bar, int tick, double periodTicks, double phaseTicks,
        double min, double max)
    {
        if (bar == null) return;
        var phase = (tick + phaseTicks * periodTicks) / periodTicks * Math.PI * 2;
        var k = 0.5 + 0.5 * Math.Sin(phase);
        bar.Height = min + (max - min) * k;
    }

}
