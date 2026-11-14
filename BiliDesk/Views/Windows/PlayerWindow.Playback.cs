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
    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_mp == null) return;

        // 已经播完了: 按播放键就是"重播"。LibVLC 到 EOF 之后直接 Play() 有时不会回到开头,
        // 显式把时间归零最稳。
        if (_ended)
        {
            RestartFromBeginning();
            return;
        }

        if (_mp.IsPlaying) _mp.Pause();
        else _mp.Play();
        UpdatePlayButton();

        // 暂停是一次"自然的段落结束", 补报一次让云端记录的进度与用户当下的位置一致
        // (否则用户看到一半暂停去网页端, 只能续播到最多 15 秒前的旧位置)
        if (!_mp.IsPlaying) SyncProgressNow();
    }

    /// <summary>
    /// 从头重播(播完之后点播放键 / 点遮罩上的「重播」)。
    ///
    /// 这里**不能**只写 `_mp.Time = 0; _mp.Play();` —— 那正是"点了重播没反应"的根因:
    /// LibVLC 到 EOF 之后, 那条 input 已经结束了, 对它发 seek 和 play 都会被丢掉
    /// (状态已经是 Ended, 不再处理控制指令)。所以必须**重新丢一条 Media 进去**,
    /// 让 LibVLC 起一个全新的 input —— 和"点另一个视频"走的是同一条路, 那条路一直是好的。
    ///
    /// 三个来源分别重建: 直播重新拉流、本地文件用 FromPath、在线视频重建音视频组合
    /// (DASH 的音轨是 input-slave 挂上去的, 不带上它重播就只有画面没有声音)。
    /// </summary>
    private void RestartFromBeginning()
    {
        if (_closing || _mp == null) return;
        // 用户明确要从头重播: 删除本机进度, 否则下次打开又跳回旧位置。
        if (!_isLive && !_isLocalPlayback)
            Svc.PlaybackProgress.MarkCompleted(_currentBvid, _currentCid);
        try
        {
            // ★ 先把"能不能重播"验完, 再动界面状态(2026-10-17 修"点重播黑屏"):
            //   早退发生在验完之后 —— 原来先清 `_ended` / 收起 EndOverlay 再验地址,
            //   于是地址取不到(直播失效、本地文件已被删)时直接 return:
            //   遮罩已经收起、"重播"按钮也没了, 手势层又还压着 EOF 那层不透明黑
            //   (见 OnEndReached), 而新 input 永远不会起来 ⇒ 屏幕上一片死黑,
            //   连重试的入口都没有。验不过就什么都不改, 让用户还能再点一次。
            if (_isLive)
            {
                // 直播没有"从头"的概念: 重新连一次拿一条新的流地址
                // (LoadLiveAsync 自己会重置遮罩与加载提示, 所以这里先不管本地状态)
                _ = LoadLiveAsync(_liveRoomId);
                return;
            }

            if (_isLocalPlayback)
            {
                var path = _lastPlayUrl;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                EnterReplayState();
                ShowLoading("正在重新播放…");
                var local = new Media(_libVLC, path, FromType.FromPath);
                PlayMedia(local);
            }
            else
            {
                var url = _lastPlayUrl;
                if (string.IsNullOrEmpty(url)) return;
                EnterReplayState();
                // 给个加载提示: 重播要重新拉一次流, 几秒内不会有画面 ——
                // 没有提示的话用户会以为"点了没反应"(这正是之前那版的观感)。
                // 遮罩由 Playing 事件负责收起。
                ShowLoading("正在重新播放…");
                // ★ 聆听态下重播仍然只拉音轨: 重播是"再听一遍", 不该突然开始下视频。
                if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                    PlayMedia(BuildMedia(_listenAudioUrl!, null));
                else
                    PlayMedia(BuildMedia(url, _lastAudioUrl));
            }

            // ★★ 2026-10-17 删掉这里的 `MarkSeekIssued(0)`(重播曾报"无限缓冲 + 音频重复")。
            //   它是个**假跳转**: 重播并不需要 seek(新 Media 本来就从 0 开始), 却让看门狗
            //   以为"有一次跳到 0 的 seek 还没确认", 于是:
            //     ① 2.5s 内没到位就走重建分支 → RebuildMediaAndSeek(0) → **又建一次 Media**,
            //        第二次重新拉流 ⇒ 用户看到"进度条在跑、画面却一直缓冲"(无限缓冲);
            //     ② 两次 input 的音轨叠在一起 ⇒ "音频重复播放"。
            //   重播只需要把进度条/时间归零, 不需要任何 seek 机制。
            if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
            _pendingSeekMs = -1;
            _seekObservedMs = -1;
            _anchorMs = -1;
            _displayedMs = -1;
            UpdatePlayButton();
        }
        catch { /* 播放器可能已失效 */ }
    }

    /// <summary>
    /// 真正要起播一个新 input 了, 才把"已播完"这套状态收掉。
    ///
    /// ★★★ 2026-10-17 重播黑屏**真正的修法**在这里(前几次都修错了地方):
    ///   EOF 时 <see cref="OnEndReached"/> 把手势层压成不透明黑, 用来盖住已被拆掉的
    ///   vout HWND 露出的白底。**重播根本不需要那层黑** —— 新 input 的 vout 建好之前
    ///   只有几百毫秒, 而且那种情况下露出的是"背景白", 由 <see cref="LoadingOverlay"/>
    ///   (重播时已经显示"正在重新播放…")全程盖着, 手势层压根不必参与。
    ///   ⇒ 所以在**起播之前**就把手势层还原。原先指望 `Playing`/`Vout` 事后揭幕,
    ///   两者都不可靠: EOF 刻意不调 `_mp.Stop()`(见 OnEndReached), 那条 input 的 vout
    ///   可能并未被销毁, `Vout` 因而不发; 而 `Playing` 在 EOF→新 input 的状态切换里
    ///   也可能不再触发一次。结果黑层就永久留下了 —— 而它在 vout HWND **之上**,
    ///   于是"幕布后面视频正常播放"(用户原话)。
    ///   ★ 也解释了"双击全屏后恢复": 全屏必然重建 vout, 顺带把黑幕撤掉了。
    /// </summary>
    private void EnterReplayState()
    {
        _ended = false;
        EndOverlay.Visibility = Visibility.Collapsed;
        // 重播是同一片源的全新 input: 自救计数清零, 之后遇到提前 EOF 仍能续播
        _eofResumeCount = 0;
        _lastEofResumeMs = 0;
        // "播完后的跳转"那个闸一并清掉: 重播之后用户又能正常拖进度条了。
        // (不清的话, 上一次"播完后点进度条"留下的闸会挡住之后的正常跳转。)
        _seekAfterEndedGuard = false;
        // 揭掉 EOF 那层黑(见上面说明: 重播期间不需要它, 遮罩由 LoadingOverlay 负责)
        if (_eofBlackActive)
        {
            RestoreVideoAreaBackground();
            WriteLibVLCDebug("[Replay] 起播前揭掉 EOF 黑层\n");
        }
    }
    private void OnReplayClick(object sender, RoutedEventArgs e) => RestartFromBeginning();

    /// <summary>强制上报一次当前进度(暂停 / 关闭窗口时用), 绕过节流间隔判断</summary>
    private void SyncProgressNow()
    {
        if (_closing || _mp == null) return;
        if (_aid <= 0 || _currentCid <= 0) return;
        var progressMs = _mp.Time;
        var progressSec = progressMs / 1000;
        // 暂停/拖动是"自然的段落结束", 本机记录也立即落盘。
        Svc.PlaybackProgress.SaveNow(_currentBvid, _currentCid, progressMs, _mp.Length,
            _isLive, _isLocalPlayback);
        _ = Svc.HistorySync.ReportNowAsync(_aid, _currentCid, progressSec);
    }

    private void UpdatePlayButton()
    {
        if (_closing || BtnPlay == null) return;
        // 标准语义: 正在播放时显示"暂停"图标(双竖条), 停止/暂停时显示"播放"图标(三角)
        // 用矢量 Path 而非字体图标, 彻底避免字体缺失时显示为方块的问题
        BtnPlay.Content = _mp.IsPlaying ? CreatePauseIcon() : CreatePlayIcon();
    }

    private static System.Windows.Shapes.Path CreatePlayIcon()
    {
        var geo = System.Windows.Media.Geometry.Parse("M 7,4 L 18,11 L 7,18 Z");
        geo.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = Brushes.White,
            Width = 15,
            Height = 15,
            Stretch = System.Windows.Media.Stretch.Uniform
        };
    }

    private static System.Windows.Shapes.Path CreatePauseIcon()
    {
        var geo = System.Windows.Media.Geometry.Parse("M 5,4 L 9,4 L 9,18 L 5,18 Z M 12,4 L 16,4 L 16,18 L 12,18 Z");
        geo.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = Brushes.White,
            Width = 15,
            Height = 15,
            Stretch = System.Windows.Media.Stretch.Uniform
        };
    }

    private void UpdateProgressUi()
    {
        if (_closing || _mp == null) return;

        // ★ 直播弹幕时钟(2026-11-13): 直播没有 Length, DispatchDanmaku 第一句就 return,
        //   点播那条 "TimeChanged → DispatchDanmaku → CalibrateDanmakuClock" 的校准链
        //   根本不会跑 —— 弹幕会在上屏后一动不动。这里用进度心跳(500ms)补上。
        //   ★ 只在直播里做: 点播的校准归 DispatchDanmaku, 两处都写会互相抢基准。
        //   ★ 暂停时冻结在"此刻所见"(见 SetDanmakuClockRunning 的说明), 恢复后下一跳再校准。
        if (_isLive)
        {
            try
            {
                if (_mp.IsPlaying) CalibrateDanmakuClock(_mp.Time / 1000.0);
                else SetDanmakuClockRunning(false);
            }
            catch { /* 原生对象可能已失效 */ }
        }

        // 宽限期(SeekGraceMs): 这段时间内不回写进度条。等待期已由下面的看门狗分支挡住,
        // 这里只作为"看门狗提前放行"时的兜底 —— 见 SeekGraceMs 的说明。
        var inSeekGrace = Environment.TickCount64 < _seekGraceUntil;

        // ---- 跳转看门狗: 目标迟迟没到位就重发 seek, 等待久了给缓冲提示 ----
        if (_pendingSeekMs >= 0 && !_isSeeking && !_ended && _mp.Length > 0)
        {
            var now = Environment.TickCount64;

            // ⚠ 这里踩过一个很隐蔽的坑(2026-09-25 实测):
            // LibVLC 的 Time **在 seek 指令发出的那一刻就跳成目标值**(实测 100ms 内),
            // 而此时的画面还冻在旧位置 —— 真正恢复画面要 0.4s(单文件)到 1.4s(DASH 双流)。
            // 所以"Time 接近目标"根本不能单独作为到位依据: 看门狗会在第一个心跳就判成功,
            // 后果有两个 ——
            //   1. 重发 seek 的分支永远不会执行(丢掉的 seek 没人救);
            //   2. "正在缓冲"提示永远不会弹(因为立刻就 return 了), 用户只能对着冻住的画面干等。
            // 正确的判据是"位置落在目标附近 **并且相对上一次观察真的动过**", 那才代表画面回来了。
            var time = _mp.Time;
            var settled = IsSeekSettled(time, _pendingSeekMs, _seekObservedMs, !_mp.IsPlaying);

            // ★★ 2026-10-17 大修: 原来的判据只要求"远离起点 + 远离目标", 没有**上界**,
            //   结果把"seek 彻底失效、位置回到片子开头"误判成"落地但偏早", 于是收手不重试 ——
            //   这正是用户报的"拉过去又跳回去、画面卡住"(探针 bd-probe-seekstick 实测):
            //     拖到 329s → 落在 5.1s, 拖到 724s → 落在 11.1s, 拖到 988s → 落在 200s。
            //   而 `_seekFromMs` 当时是 3.4s / 8.9s / 198s, 用 `> 1500ms` 判"远离起点"轻而易举
            //   就成立 ⇒ 立刻被当成"落地偏早"认账。**判据缺的是"离目标不能太远"这个上界。**
            //
            //   真实"落地偏早"(DASH 分片边界)的偏差是**秒级**: 实测 60s→55.2s(4.8s)、
            //   150s→145.7s(4.3s); 而这次失效的偏差是 324s / 713s / 788s —— 差两个数量级。
            //   所以判据取"偏早不超过目标的 1/8 且不超过 30s": 正常偏早(几秒)仍被认账,
            //   跳到开头/跳到很远的地方则一律判为"没落地", 交给看门狗重发。
            //
            // ★ 为什么上界要用**相对目标的比例**而不是固定值: 目标可能在 5 分钟处也可能在
            //   20 分钟处, 固定 30s 对短跳转太松(5s 目标偏 25s = 完全跑偏却仍认账)。
            var earlyBy = _pendingSeekMs - time;                 // >0 表示落在目标之前
            var earlyUpper = Math.Min(SeekLandingMaxEarlyMs,
                                      Math.Max(SeekLandingMinEarlyMs, _pendingSeekMs / 8));
            var farFromBoth = _seekFromMs >= 0 &&
                              Math.Abs(time - _seekFromMs) > SeekLandingToleranceMs &&
                              Math.Abs(time - _pendingSeekMs) > SeekConfirmWindowMs;
            // ★ 只接受"落在目标之前、且偏早幅度在合理范围内"; 落在目标**之后**超窗也不算
            //   (那同样是跑偏, 该重试)
            var withinEarlyBand = earlyBy > SeekConfirmWindowMs && earlyBy <= earlyUpper;
            var landedElsewhere = farFromBoth && withinEarlyBand &&
                                  _seekElsewhereSeenMs >= 0 && time != _seekElsewhereSeenMs;
            _seekElsewhereSeenMs = farFromBoth ? time : -1;

            // ★★★ "落点弹回起点"的提前重建(2026-10-31 引入, 2026-11-05 修误判):
            //   DASH 双流上远跳偶尔整体失效, 落点直接弹回片子开头(或某个离目标极远的分片)。
            //   这种落点距目标分钟级, settled/landedElsewhere 都判不上, 只能干等
            //   3.5s×3 轮重发 + 一次重建 —— 期间进度条钉在目标、画面停在开头。
            //   ⇒ 判定"弹到天边去了"后直接重建, 不再走无意义的重发。
            //
            //   ★★ 但"弹回"必须**持续** <see cref="SeekBounceConfirmMs"/> 才作数:
            //   原来用"连续两次心跳(≈200ms)采样值不同"当判据, 会把 libvlc 受理 seek 后
            //   "先冲过目标、再回退 1.5s 慢慢爬回"的正常过渡误判成弹回 —— 重建 Media 又打断
            //   正在恢复的 input, 表现为用户报的"一直闪正在缓冲, 最后回到开头"。
            if (!settled && !landedElsewhere && farFromBoth && !_seekRebuildTried)
            {
                if (_seekBounceSeenMs < 0)
                {
                    _seekBounceSeenMs = time;
                    _seekBounceAt = now;
                }
                else if (now - _seekBounceAt >= SeekBounceConfirmMs)
                {
                    _seekRebuildTried = true;
                    var bounceTarget = _pendingSeekMs;
                    var bounceFrom = _seekFromMs;
                    // ★ 先换文案再清看门狗: ClearSeekWatchdog 默认会淡出遮罩, 紧跟着
                    //   RebuildMediaAndSeek 又淡入 —— 中间那 170ms 就是用户看到的"闪屏"。
                    //   保持遮罩可见、只换文案, 整个重建过程一张遮罩盖到底。
                    ShowLoading("正在跳转…");
                    ClearSeekWatchdog(hideBuffering: false);
                    WriteLibVLCDebug(
                        $"[Seek] 落点弹回别处(time={time}ms target={bounceTarget}ms from={bounceFrom}ms) → 重建 Media\n");
                    RebuildMediaAndSeek(bounceTarget);
                    return;
                }
            }
            else
            {
                _seekBounceSeenMs = -1;
                _seekBounceAt = -1;
            }

            _seekObservedMs = time;

            if (settled || landedElsewhere)
            {
                ClearSeekWatchdog();
            }
            else if (now >= _seekDeadline)
            {
                if (_seekRetries < SeekRetryMax)
                {
                    // seek 指令被 LibVLC 丢了: 重发一次(重发后位置会被重新 ack, 观察值也要清)
                    _seekRetries++;
                    _seekDeadline = now + SeekConfirmTimeoutMs;
                    _seekIssuedAt = now;
                    _seekObservedMs = -1;
                    _seekBounceSeenMs = -1;
                    _seekBounceAt = -1;
                    try { _mp.Time = _pendingSeekMs; } catch { }
                }
                else if (!_seekRebuildTried && !_isReliableSeekSource &&
                         !string.IsNullOrEmpty(_lastPlayUrl))
                {
                    // ★★★ 2026-10-17 手术: 重发也救不回来时**重建一次**, 而不是收手。
                    //
                    //   实测(探针 bd-probe-muxed5, DASH+slave): 同一路连跳 90%/55%/80%
                    //   全部被 libvlc 丢掉(偏差 -1180s/-590s/-910s), 而"重发"实测同样无效
                    //   —— 那条 input 的解复用器已经认死了, 再发几次 Time 都一样。
                    //   重建 = 起一条全新 input, 这正是"换片/切清晰度"那条一直好用的路。
                    //
                    //   只对**不可靠源**做: 可靠源(合流单流)本就一发到位, 真走到这里
                    //   说明是网络卡顿, 重建只会白白打断播放。
                    _seekRebuildTried = true;
                    var target = _pendingSeekMs;
                    ShowLoading("正在跳转…");
                    ClearSeekWatchdog(hideBuffering: false);
                    RebuildMediaAndSeek(target);
                }
                else
                {
                    ClearSeekWatchdog();
                }
            }
            else if (now - _seekIssuedAt > SeekBufferingDelayMs &&
                     LoadingOverlay != null && LoadingText != null &&
                     LoadingOverlay.Visibility == Visibility.Collapsed &&
                     !_isLive && !_isLocalPlayback)
            {
                // 跳转后迟迟不到位: 与其让用户对着冻住的老画面干等, 不如把"视频开头那套"
                // 加载动画(转圈 + 颜文字)搬出来说清楚"在缓冲"。
                ShowLoading(BufferingText);
            }

            // 等待期间不回写进度条(目标位置已由 MarkSeekIssued 钉住), 时间文字同理
            return;
        }

        // ---- 播放中途卡顿(网络缓冲)检测: 与"跳转"无关, 纯播放中卡住 ----
        UpdateStallDetector(inSeekGrace);
        // ★ seek 后音量写回被 aout 就绪窗口吞掉的延迟补写(没排队时零开销)
        RestoreVolumeWhenReady();

        if (_mp.Length > 0 && !inSeekGrace)
        {
            // ★ 进度条与时间文字必须同源(2026-10-01, 用户截图 24:43/24:49 实锤"进度条卡在
            //   最右端但时间还剩 6 秒"): 以前进度条用 `_mp.Position`, 时间文字用 `_mp.Time`。
            //   尾部解复用把较短的那路流(DASH 的音轨)读完时, Position 会先飘到 ~1.0 而
            //   Time 还在走 —— 两源一错位, 进度条就"提前满格卡住"。统一从 Time 派生后
            //   两者的行为永远一致(播完钉满格的规则不变)。
            // Slider 0-1000, 按比例(_updatingSlider 防止回写触发 ValueChanged 造成 seek 循环)
            var ms = _ended ? _mp.Length : GetDisplayedTimeMs(_mp.Length);
            var pos = ms / (double)_mp.Length; // 0.0~1.0
            if (_ended) pos = 1;
            if (!_isSeeking)
            {
                _updatingSlider = true;
                ProgressSlider.Value = pos * 1000;
                _updatingSlider = false;
            }
            TimeText.Text = $"{FormatClock(TimeSpan.FromMilliseconds(ms))} / " +
                            $"{FormatClock(TimeSpan.FromMilliseconds(_mp.Length))}";
        }
    }

    // --- 尾部进度外推(输入停滞、解码缓冲还在播的那几秒) ---
    // DASH 双流在结尾会出现"Time 停滞几秒"的窗口: 输入线程在等另一路流/最后分片,
    // 而解码缓冲里的内容还在正常播。此时若如实显示, 进度条和时间会冻在最后 5~6 秒
    // (用户报的"进度条最后五秒卡住不动")。尾部(剩余 < TailQuietZoneMs)改用
    // "锚点 + 墙钟 × 倍速"外推显示; 走出尾部或 EOF(钉满格)时自动归位。
    // 中间卡住仍如实显示, 那是真缓冲, 骗用户没有意义。
    //
    // ★★ 外推的触发判据是"真实读数没追上显示值", **不是**"Time 一格不动"(2026-10-01
    //   第二次返工的教训): 第一版只认 Time 完全冻结, 但实测尾部停滞时时钟常常**每跳
    //   挪几十毫秒**—— 每次都算"在走", 外推永远不启动, 进度条就以 ~6% 的速度龟速爬,
    //   看起来照样是卡死。锚点方案对"完全冻结"和"龟速蠕动"一视同仁。
    /// <summary>外推锚点: 最后一次确认"真实读数追上显示"的位置(-1 = 尚未建立)。</summary>
    private long _anchorMs = -1;
    /// <summary>锚点对应的墙钟(毫秒, Environment.TickCount64)。</summary>
    private long _anchorWall;
    /// <summary>
    /// 上一次**显示**的进度。尾部只进不退的钳制基准(见 GetDisplayedTimeMs);
    /// seek / 换片时归零, 否则"从结尾往回跳转"会被钳在旧位置上。
    /// </summary>
    private long _displayedMs = -1;

    /// <summary>取"应该显示的播放位置": 正常时就是 _mp.Time; 尾部按"锚点 + 墙钟×倍速"外推。</summary>
    private long GetDisplayedTimeMs(long lengthMs)
    {
        var t = _mp.Time;
        var wall = Environment.TickCount64;

        // 非尾部 / 暂停: 如实显示, 锚点对齐当下(暂停时不积累外推量, 恢复后从暂停点继续)
        if (!_mp.IsPlaying || lengthMs - t >= TailQuietZoneMs)
        {
            _anchorMs = t;
            _anchorWall = wall;
            _displayedMs = t;
            return t;
        }

        long shown;
        if (t > _displayedMs)
        {
            // 真实读数追上/超过显示值: 以真实为准, 抬锚
            _anchorMs = t;
            _anchorWall = wall;
            shown = t;
        }
        else
        {
            // 真实读数落后(停滞/龟速/时钟回跳): 从锚点按墙钟外推, 封顶在片尾
            if (_anchorMs < 0) { _anchorMs = t; _anchorWall = wall; }
            var rate = _mp.Rate;
            if (rate <= 0) rate = 1.0f;
            shown = Math.Min(_anchorMs + (long)((wall - _anchorWall) * rate), lengthMs);
        }

        // ★ 只进不退: 时钟重锚/回跳不让显示跟着倒。真正允许倒退的路径
        //   (用户拖进度条、点重播、换片)都会先归零 _displayedMs / _anchorMs。
        if (_displayedMs >= 0 && shown < _displayedMs)
            shown = _displayedMs;
        _displayedMs = shown;
        return shown;
    }

    /// <summary>
    /// 播放器时间标签的时钟格式: 不足一小时用 mm:ss, **满一小时用 h:mm:ss**。
    /// ★ 之前恒用 mm:ss, 一个多小时的视频总时长"1:13:25"会被截成"01:13"(用户截图实锤:
    ///   播到 46:52 时右侧显示 01:13) —— TimeSpan 的 "mm" 格式符**不会进位到小时**,
    ///   它只是"分钟位的两位数字"。小时位要显式给, B 站的样式是"1:13:25"(小时不补零)。
    /// </summary>
    private static string FormatClock(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";

    /// <summary>
    /// 判断一次跳转是不是**真的**到位了(画面回来了), 而不是只收到了 LibVLC 的"已受理"回执。
    ///
    /// 为什么需要这么拧巴的判据(2026-09-25 真机实测得出来):
    ///   LibVLC 的 Time 在 seek 指令发出的那一刻就跳成目标值 —— 实测发出后 100ms 内返回
    ///   60000ms(正好等于目标), 而画面要到 380ms(单文件源)~1410ms(DASH 双流)后才动。
    ///   换句话说"Time 接近目标"只是一张回执, 完全没有画面信息。
    ///   老代码只看这个, 于是看门狗每个跳转都在第一跳就判成功, 连带两个后果:
    ///     ① 重发 seek 的分支永远走不到 —— 被丢掉的 seek 没人救;
    ///     ② "正在缓冲"提示永远弹不出来 —— 用户只能对着冻住的画面干等(这正是"拖进度条卡"
    ///        最难受的那部分: 不是慢, 是没有任何反馈)。
    ///
    /// 所以判据改成"接近目标 **并且** 相对上一次观察真的动过"。
    /// 唯一的例外是暂停态: 暂停时位置本来就不前进, 而 VLC 已经按目标解出了那一帧,
    /// 这时"接近目标"就是完整的到位信号(不加这条, 暂停中拖进度条会白弹一个缓冲提示)。
    /// </summary>
    /// <param name="timeMs">本次观察到的播放位置(毫秒)</param>
    /// <param name="targetMs">跳转目标(毫秒)</param>
    /// <param name="observedMs">上一次观察到的位置, -1 = 还没观察过</param>
    /// <param name="paused">播放器当前是否处于暂停/停止态</param>
    private static bool IsSeekSettled(long timeMs, long targetMs, long observedMs, bool paused)
    {
        // ★ 2026-11-05: 容差从 900ms 放宽到 3s。libvlc 受理 seek 后 Time 会先冲过目标、
        //   再回退 ~1.5s 逐步爬回; 900ms 会把这个正常过渡误判成"没到位", 进而触发弹回重建。
        if (Math.Abs(timeMs - targetMs) > SeekSettledToleranceMs) return false;
        if (paused) return true;
        return observedMs >= 0 && timeMs != observedMs;
    }

    /// <summary>跳转确认到位(或放弃重试)后收尾: 关掉"正在缓冲"提示, 清掉待确认目标</summary>
    /// <param name="hideBuffering">
    /// 是否顺手收起"正在缓冲…"遮罩。重建 Media 前要传 false —— 由调用方先换成
    /// "正在跳转…"并让同一张遮罩盖到新 input 起播, 避免淡出/淡入造成的闪屏。
    /// </param>
    private void ClearSeekWatchdog(bool hideBuffering = true)
    {
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        _seekFromMs = -1;
        _seekElsewhereSeenMs = -1;
        _seekBounceSeenMs = -1;
        _seekBounceAt = -1;
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        // 卡顿检测的基线也归零: 上一个心跳的参照是"seek 之前的位置", 留着会让落地后的
        // 第一跳被误算成"推进了一大步"(或反过来), 与随后的 2s 冷却一起避免提示闪跳。
        _stallLastMs = -1;
        _stallTicks = 0;
        RestoreVolumeAfterSeek();
        if (hideBuffering) HideBufferingIfShown();
        // ★ 2026-10-31: 到位这一刻正是"音轨偶发没跟上"的暴露点, 顺手做一次自愈检查
        //   (见 HealAudioIfDead —— 命中才动作, 不命中零开销)。
        HealAudioIfDead("seek-settled");
    }

    // ------------------------------------------------------------ seek 后音频自愈

    /// <summary>音频自愈检查的宽限期(毫秒): 重建 Media / 长按快进恢复 1x 之后,
    /// aout 重建与音轨枚举都需要一点时间, 立刻检查必然误报。</summary>
    private const int AudioHealGraceMs = 1500;

    /// <summary>上次音频自愈检查的时刻(TickCount64), 用于实现"每个检查点只查一次"。</summary>
    private long _audioHealCheckAt = -1;

    /// <summary>
    /// seek / 长按快进之后音轨偶发静默的兜底自愈。
    ///
    /// ★★★ 症状(2026-10-31 用户报"长视频快进后有时没声音"): 画面正常、时间在走,
    ///   但就是没声。实测机理是 libvlc 在 seek/变速时重建音频输出, 偶发地**把选中的
    ///   音轨丢回"无轨道"**(AudioTrack 变 -1, 音轨数明明 >0) —— 这是库的内部竞态,
    ///   离线无法稳定复现, 但恢复动作本身是幂等且无害的。
    ///
    /// 检查判据(三个条件同时成立才动手, 宁可漏报不可误伤):
    ///   ① 在播放且不在拖动/等待 seek —— 排除过渡期;
    ///   ② 距上次检查超过宽限期 —— 避免对同一次过渡反复操作;
    ///   ③ 音轨数 >0 而 AudioTrack == -1 —— "有轨道却没选"就是那个竞态的指纹。
    /// 修复动作 = SetAudioTrack(0) 把第一条音轨选回来(单音轨片源恒正确;
    /// 多音轨片源 libvlc 本来就默认选 0)。只打一次日志, 避免刷爆 vlc-debug.txt。
    /// </summary>
    /// <param name="why">调用点标签, 进诊断日志</param>
    private void HealAudioIfDead(string why)
    {
        if (_closing || _mp == null) return;
        var now = Environment.TickCount64;
        if (_audioHealCheckAt >= 0 && now - _audioHealCheckAt < AudioHealGraceMs) return;
        _audioHealCheckAt = now;
        try
        {
            // 拖动中/等待 seek 确认期间音轨本来就处于切换途中, 不检查
            if (!_mp.IsPlaying || _isSeeking || _pendingSeekMs >= 0) return;
            if (_mp.AudioTrackCount <= 0) return;          // 本来就没有音轨(纯视频/直播占位), 无事可做
            if (_mp.AudioTrack != -1) return;              // 音轨选着呢, 一切正常
            var ok = _mp.SetAudioTrack(0);
            WriteLibVLCDebug(
                $"[Audio] 自愈: 检出音轨掉线({why}, tracks={_mp.AudioTrackCount}) → SetAudioTrack(0)={ok}\n");
        }
        catch { /* 播放器已失效就算了 */ }
    }

    // ------------------------------------------------------------ seek 静音

    /// <summary>跳转发出: 静音(记住原音量)。连续拖动时已静音则只更新心跳, 不覆盖原值。</summary>
    private void MuteForSeek()
    {
        if (_closing || _mp == null || _seekMuteSavedVolume >= 0) return;
        try
        {
            // 读权威值而不是原生 _mp.Volume: aout 未就绪时原生读回 -1, 会被 "v<=0" 误判成
            // "本来就静音" 而跳过, 恢复点也就不会把音量补写回 aout(见 _volumeCache 的说明)
            var v = _volumeCache;
            if (v <= 0) return;   // 本来就静音, 无事可做
            _seekMuteSavedVolume = v;
            _mp.Volume = 0;
        }
        catch { /* 播放器已失效就算了 */ }
    }

    /// <summary>画面确认到位(或放弃/换片/播完): 恢复跳转前的音量。
    ///
    /// ★★ 2026-10-31 补"写回被吞"的兜底: aout 未就绪时 libvlc_audio_set_volume 是
    ///   **静默丢弃**的(反编译 3.8.0 确认 setter 不看返回值), 而 seek 到位的那一刻
    ///   新 input 的 aout 可能刚好还没建好 —— 写回丢了, aout 就绪后音量停在 0,
    ///   表现就是"快进过去画面正常但一直没声"。所以写回后要读一次原生值核对,
    ///   读不回(-1)或没写上(≠v)就交给 <see cref="RestoreVolumeWhenReady"/> 延迟补写。</summary>
    private void RestoreVolumeAfterSeek()
    {
        if (_seekMuteSavedVolume < 0) return;
        var v = _seekMuteSavedVolume;
        _seekMuteSavedVolume = -1;
        _volumeCache = v;   // 静音期间滚轮/滑块一直在改权威值, 恢复时对齐一次, 防止两条账本漂移
        try
        {
            if (_mp == null) return;
            _mp.Volume = v;
            var applied = _mp.Volume;   // aout 未就绪时读回 -1
            if (applied != v)
            {
                _volumeRestoreDeadline = Environment.TickCount64 + VolumeRestoreTimeoutMs;
                WriteLibVLCDebug($"[Audio] 音量写回未生效(applied={applied}, want={v}) → 稍后补写\n");
            }
        }
        catch { }
    }

    /// <summary>音量补写的最长等待(毫秒): 超过就放弃(避免无限期挂着)。</summary>
    private const int VolumeRestoreTimeoutMs = 8000;

    /// <summary>音量补写的截止时刻(>0 表示有补写在排队, 见 RestoreVolumeWhenReady)。</summary>
    private long _volumeRestoreDeadline;

    /// <summary>补写已重试的次数(每次心跳 +1, 夹在超时内; 日志防刷)。</summary>
    private int _volumeRestoreTries;

    /// <summary>
    /// 心跳里调: seek 后音量写回被 aout 就绪窗口"吞掉"时的延迟补写。
    /// 只在 <see cref="_volumeRestoreDeadline"/> 排了队时才干活, 平时零开销。
    /// 超时放弃时按权威值再硬写一次 —— 哪怕这次仍被丢弃, 下一次 Playing/调音量也会对齐。
    /// </summary>
    private void RestoreVolumeWhenReady()
    {
        if (_volumeRestoreDeadline <= 0 || _closing || _mp == null) return;
        var now = Environment.TickCount64;
        try
        {
            var v = _volumeCache;
            if (v <= 0) { _volumeRestoreDeadline = 0; return; }   // 用户自己静音了, 收队
            if (_seekMuteSavedVolume >= 0) return;                // 新一轮 seek 静音接管了, 让位
            _mp.Volume = v;
            var applied = _mp.Volume;
            if (applied == v)
            {
                WriteLibVLCDebug($"[Audio] 音量补写成功(第{_volumeRestoreTries + 1}次尝试, v={v})\n");
                _volumeRestoreDeadline = 0;
                _volumeRestoreTries = 0;
                return;
            }
        }
        catch { _volumeRestoreDeadline = 0; _volumeRestoreTries = 0; return; }

        if (now >= _volumeRestoreDeadline)
        {
            // 超时: 别再耗着, 直接按权威值硬写一次然后收队(playing 事件还会再补一轮)
            try { _mp.Volume = _volumeCache; } catch { }
            WriteLibVLCDebug($"[Audio] 音量补写超时放弃(tried={_volumeRestoreTries + 1})\n");
            _volumeRestoreDeadline = 0;
            _volumeRestoreTries = 0;
            return;
        }
        _volumeRestoreTries++;
    }

    // ------------------------------------------------------------ 倍速切换与"松手后静音"
    //
    // ★★★ 2026-10-16 定稿(实测见 .probes/bd-probe-rateguard2, 前面两次结论都有偏差, 以本条为准):
    //
    //  现象: 长按 2x 松手后, 会听到一小段"没有声音"。
    //
    //  根因: **libvlc 在 SetRate 生效时会丢弃整个音频输出缓冲**, 原生日志:
    //          playback way too late (~529-655ms): flushing buffers
    //        官方 flush 定义 = "discard all pending buffers and stop playback as soon as
    //        possible" ⇒ 待播音频被整体丢掉, 要重填才恢复出声。
    //        ★ 这是 **VLC 自身行为**, 不是本项目的代码 bug(探针裸调 libvlc 也复现)。
    //
    //  ★★ 但**这个代价比之前估计的小得多**: 真正的停顿只有 **0.25~0.5s**
    //     (base 变体两次实测: 停顿 0.25s / 0.50s, 松手后吞吐比 1.12 / 0.79)。
    //     曾经记成"约 2 秒"是**误读**: 松手瞬间 OS 缓冲里还排着已解码音频, 它仍以
    //     **2 倍速**播完(实测按住期间吞吐 = 正常 1x 的 3.4 倍)⇒ 听感像"还在快进",
    //     等它播空才轮到那一小段静音。**静音本身只有零点几秒。**
    //
    //  ★★★ 三个"缓解"手段全都**实测更糟**, 已被否掉(别再想):
    //    · 松手前先压音量再改 rate  → 停顿拉长到 2.25s, 吞吐比 0.01
    //    · 按住期间全程静音          → 停顿 2.00s, 吞吐比 0.27
    //    · 松手不改 rate 只补一次 seek → 停顿 1.50s
    //    ⇒ 什么都不做(base)就是最优。**不要去"治"那 0.3 秒。**
    //
    //  ★ 早期逐一实测过、确认**无效**的参数(别再重复): `--network-caching` 1000/3000/5000/6000、
    //    `--no-audio-time-stretch`(libvlc 级与 media 级; 该选项不生效, scaletempo 照旧加载)、
    //    `--aout=mmdevice / directsound / waveout / afile / amem`、`--clock-jitter=0`、
    //    `--clock-synchro=0`、Pause/Play 包住 SetRate、分两步换速率(flush 翻倍)、
    //    SetRate 后补 seek(flush 暴增)、Stop + 重建 Media(更差)、先压音量再 SetRate(同前)。
    //  ★ 也扫过 libvlccore/libvlc 的选项表: **没有**能调 aout 缓冲深度的开关
    //    (--audio-buffer / --aout-buffer / --mmdevice-buffer 等全部不存在)。
    //
    //  ⇒ 结论: 长按快进就用 SetRate。顺滑度是它天生就有的(与倍速按钮同款),
    //    代价只有松手后零点几秒静音 —— 换 seek 式反而会把顺滑度整个丢掉(实测用户反馈"画面和音频均卡顿")。


    /// <summary>当前遮罩是不是我们自己弹的"正在缓冲"/"正在重连"(据此判断能不能收掉)</summary>
    private bool IsShowingBuffering()
        => LoadingOverlay != null && LoadingText != null &&
           LoadingOverlay.Visibility == Visibility.Visible &&
           IsStallOverlayText(LoadingText.Text);

    /// <summary>
    /// 这两条文案都是"卡顿"这套机制自己弹的: 普通缓冲与"网络卡顿，正在重连…"。
    /// 收遮罩、遮罩安全网、文案升级都以它为准 —— 别去动"正在切换清晰度…"那些有自己生命周期的提示。
    /// </summary>
    private static bool IsStallOverlayText(string? text)
        => text == BufferingText || text == StallReconnectingText;

    /// <summary>
    /// 收起"正在缓冲"遮罩 —— **只收这一种**。
    /// 不能直接 HideLoading(): 此刻遮罩上可能写着"正在切换清晰度…""正在获取播放地址…",
    /// 那些有自己的生命周期, 被这里顺手收掉就会露出后面的黑屏。
    /// </summary>
    private void HideBufferingIfShown()
    {
        if (_closing) return;
        if (IsShowingBuffering()) HideLoading();
    }

    // --- 播放中途卡顿检测 ---
    // 在每 500ms 一次的进度刷新里比较 _mp.Time 有没有推进: 连续几个心跳原地不动就是卡住了。
    //
    // 为什么不用 LibVLC 的 Buffering 事件: 它在正常播放时也会频繁给到 100, 在部分片源上
    // 又长期给不满 100 —— 拿它当"该不该提示"的依据很容易把提示卡在屏上收不掉。
    // 用"播放位置有没有推进"这个客观事实判断: 最多漏报(极少数片源 Time 会假性前进),
    // 不会误报, 而且位置一动就自动恢复。
    //
    // 这个检测同时覆盖了"快进后卡住"和"看着看着网络卡住"两种情况 —— 用户感知都是同一件事。
    private long _stallLastMs = -1;
    private int _stallTicks;

    /// <summary>连续几次心跳没推进才算卡顿(2 * 500ms ≈ 1 秒, 正常的缓冲抖动不会被判成卡顿)</summary>
    private const int StallTicksToReport = 2;

    /// <summary>判定"没推进"的阈值(毫秒): 500ms 的心跳间隔下正常播放至少推进几百毫秒</summary>
    private const long StallIdleToleranceMs = 60;

    private void UpdateStallDetector(bool inSeekGrace)
    {
        if (_closing || _mp == null) return;

        // 直播/本地文件没有"缓冲卡顿"这回事; 播完了也不算
        if (_isLive || _isLocalPlayback || _ended)
        {
            EndStallWatch();
            return;
        }

        // 暂停/停止/出错: 位置本来就不动, 不是网络卡顿。
        // ★★ 2026-11-14(issue #81): 这里以前是 `!_mp.IsPlaying` 就直接 return(既不清计数
        //   也不收遮罩), 于是 LibVLC 一旦翻成 buffering/stopped, 那张"正在缓冲…"就永久挂在
        //   屏幕上 —— 用户看到的就是"卡在正在缓冲"。现在按**播放器状态**分流:
        //   暂停/停止是正常态, Playing/Buffering 才是可疑态(继续观察)。
        // ★★★ 这里**只能**清位置基线, 绝不能重置自愈预算: 自愈重建走的正是
        //   Stop() → Opening → Playing, 途中状态必然不是 Playing。把预算一起清了
        //   就会变成"重建 → 归零 → 再重建"的无限循环(与 _seekRebuildTried 同一个坑)。
        var state = SafePlayerState();
        if (state != VLCState.Playing && state != VLCState.Buffering)
        {
            ClearStallBaseline();
            // 遮罩只在"确定不会自己起来"的状态下收: 暂停/出错/已结束。
            // Stopped/Opening 是重建途中的过渡态, 遮罩必须继续盖着(否则用户看到的是一块黑屏)。
            if (state == VLCState.Paused || state == VLCState.Error || state == VLCState.Ended)
                HideBufferingIfShown();
            return;
        }

        // 起播/重建过渡期拿不到时长: 既判不了"剩余多少", 也判不了卡顿。
        // 同样只清基线(不清预算), 原因见上面那段。
        if (_mp.Length <= 0)
        {
            ClearStallBaseline();
            return;
        }

        // ★ 起播阶段(这条 input 还从来没产生过时间推进)不参与自愈:
        //   "打开视频一直转圈"属于加载链的问题(它有失败提示/重试), 而 10 秒以上的慢起播
        //   在弱网下是常态 —— 这时候重建 input 只会把正在灌的缓冲全部丢掉, 越救越慢。
        //   ★ 已经自愈重建过则例外: 否则重建后卡在 0 会永远等不到"放弃"那一步。
        if (_lastKnownMs <= 0 && _stallPolicy.Rebuilds == 0)
        {
            ClearStallBaseline();
            return;
        }

        // 拖动/跳转宽限期内同理 —— 都不算卡顿
        // ★ 2026-10-16: 这里**不再**豁免长按快进。那条豁免是 seek 式方案加的(那时我们自己
        //   每 400ms 发一次 seek, 位置会一阵阵停住)。改回 SetRate 后位置照常推进
        //   (只是快 2 倍)—— 与"倍速按钮 2x"完全同款, 那个从来没豁免过。
        //   留着反而会掩盖长按期间真实的卡顿。
        if (_isSeeking || inSeekGrace || _pendingSeekMs >= 0)
        {
            ClearStallBaseline();
            return;
        }

        // ★ 2026-11-05: 刚发过 seek 的 2 秒内不参与"中途卡顿"判定。
        //   seek 落地后解码器/音频输出都要重启, 这段短暂停顿属于"跳转尾巴";
        //   它已经由 seek 看门狗统一负责提示, 两套逻辑抢同一张遮罩就是用户看到的闪烁。
        //   ★ 只清位置基线、**不重置自愈预算**: 卡顿自愈的重建本身就会打 MarkSeekIssued,
        //     把预算一起清了就会"重建 → 归零 → 再重建"无限循环。
        if (_seekIssuedAt >= 0 &&
            Environment.TickCount64 - _seekIssuedAt < 2_000)
        {
            ClearStallBaseline();
            return;
        }

        // ★ 结尾豁免(2026-10-01, 用户实测"长视频最后五秒弹'正在缓冲'"): 剩余不足
        // TailQuietZoneMs 时不再报缓冲。DASH 音视频是两路独立流, 长度略有差异,
        // 短的那路数据先耗尽时 Time 会停几秒等另一路 —— 这是"快播完了"的正常形态,
        // 不是网络卡顿(三分钟以下的视频走本地合流单文件没有双流, 所以从来不出这个提示)。
        // 就算真是网络慢, 结尾几秒也没有"等它缓冲好"的价值, EndReached 马上会来收场。
        // ★ 2026-11-14: 尾部也不做自愈 —— 只有几秒了, 重建一次(必然黑屏缓冲一趟)比少几秒更糟。
        var now = _mp.Time;
        var remainingMs = _mp.Length - now;
        if (remainingMs < TailQuietZoneMs)
        {
            _stallTicks = 0;
            _stallLastMs = now;
            _stallPolicy.Reset();
            HideBufferingIfShown();
            return;
        }

        if (_stallLastMs >= 0 && Math.Abs(now - _stallLastMs) < StallIdleToleranceMs)
        {
            _stallTicks++;
            if (_stallTicks >= StallTicksToReport && LoadingOverlay != null &&
                LoadingOverlay.Visibility != Visibility.Visible &&
                // 失败遮罩(含"自动重连失败，请点重试")已经在屏幕上时不要再弹缓冲遮罩
                (FailOverlay == null || FailOverlay.Visibility != Visibility.Visible))
            {
                // 遮罩上已经有别的内容(切清晰度等)就不抢, 免得把对方的文案顶掉
                ShowLoading(BufferingText);
                // 给 BufferPolicy 记一笔(按 CDN host 归因): 同一线路 60s 内卡 2 次,
                // 后续媒体自动加深 1s 预读 —— 只影响下一条, 不动当前在播的源。
                // ★ 用"真正在拉的那条流": 聆听态是音轨 host, 拿视频地址归因会记错 CDN。
                BufferPolicy.ReportStall(CurrentMediaUrl);
                // 诊断: 用户报"卡住"时, 这行能说明是"位置冻住了"(而不是没起播)
                WriteLibVLCDebug(
                    $"[Stall] 播放位置冻结 pos={now}ms listen={_isListenMode} " +
                    $"host={HostOf(CurrentMediaUrl)}\n");
            }

            // ---- 卡顿自愈(2026-11-14, issue #81) ----
            // 位置冻结得够久就重建一次 input —— 这正是用户手动"切一下听视频"做的事。
            // 决策(阈值/退避/上限)全在 StallRecoveryPolicy, 这里只负责执行动作。
            // ★ 正在换清晰度时不算"可救": 那条路自己会重建 input, 两边一起动手会互相打断。
            var recoverable = !_stallRecoveryInFlight && !_switchingQuality;
            var action = _stallPolicy.Tick(Environment.TickCount64, frozen: true, recoverable: recoverable);
            HandleStallAction(action, now);
            return;
        }

        // 位置推进了 -> 卡顿结束。只收我们自己弹的那张遮罩
        _stallLastMs = now;
        _stallTicks = 0;
        _stallPolicy.OnAdvance();
        HideBufferingIfShown();
    }

    /// <summary>
    /// 位置基线归零(下一次心跳重新取参照), 但**保留**自愈预算与退避。
    /// 用在暂停/拖动/刚 seek 完这些"暂时判断不了"的窗口里 —— 见 StallRecoveryPolicy.OnUnfrozen。
    /// </summary>
    private void ClearStallBaseline()
    {
        _stallTicks = 0;
        _stallLastMs = -1;
        _stallPolicy.OnUnfrozen();
    }

    /// <summary>卡顿这段彻底结束(暂停 / 停止 / 播完 / 换片): 基线、预算、遮罩一起收。</summary>
    private void EndStallWatch()
    {
        _stallTicks = 0;
        _stallLastMs = -1;
        _stallPolicy.Reset();
        HideBufferingIfShown();
    }

    /// <summary>把一次跳转记下来: 打上宽限期, 并立刻把进度条/时间钉到目标位置</summary>
    private void MarkSeekIssued(long targetMs)
    {
        var now = Environment.TickCount64;
        _seekGraceUntil = now + SeekGraceMs;
        _seekIssuedAt = now;
        _lastSeekTargetMs = targetMs;
        MuteForSeek();
        // 启动/重置跳转看门狗: 超过 SeekConfirmTimeoutMs 还没回到目标附近就重发 seek
        _pendingSeekMs = targetMs;
        // 记下"跳转前的位置"给看门狗判"是不是真的丢了"(见 _seekFromMs 的说明)。
        // ★ 必须在这里取: 调用方通常已经先设过 `_mp.Time = target`, 那个属性读回来的是受理回执
        //   (= 目标值), 拿它当起点会让"落地偏早"永远判不出来。
        _seekFromMs = _lastKnownMs > 0 ? _lastKnownMs : -1;
        _seekRetries = 0;
        _seekDeadline = now + SeekConfirmTimeoutMs;
        // 观察值清空: 第一个心跳拿到的会是"目标值本身"(那是 LibVLC 的 ack, 不是画面), 不能当作推进
        _seekObservedMs = -1;
        _seekElsewhereSeenMs = -1;
        _seekBounceSeenMs = -1;
        _seekBounceAt = -1;
        _ended = false;
        // ★ "播完后的跳转"那个闸在这里清掉: 它只用来挡**同一次点击**的第二条路
        //   (ValueChanged + Thumb.DragCompleted)。新 input 已经起播, 后续的拖动是
        //   正常的 seek, 不该再被这个闸拦住。
        _seekAfterEndedGuard = false;
        // 进度外推的锚点与显示钳制基线作废: 不作废的话, 跳转落地后可能外推出凭空进度,
        // 往回拖进度条也无法倒着显示
        _anchorMs = -1;
        _displayedMs = -1;
        EndOverlay.Visibility = Visibility.Collapsed;
        // 等待期把心跳调密, 好让缓冲提示与到位判定都及时(见 ProgressTickSeeking 的说明)
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickSeeking;

        if (_totalMs <= 0) return;
        _updatingSlider = true;
        ProgressSlider.Value = Math.Clamp(targetMs * 1000.0 / _totalMs, 0, 1000);
        _updatingSlider = false;
        TimeText.Text = $"{FormatClock(TimeSpan.FromMilliseconds(targetMs))} / " +
                        $"{FormatClock(TimeSpan.FromMilliseconds(_totalMs))}";
    }

    /// <summary>
    /// 统一的 seek 发起入口 —— **所有跳转都必须走这里**(拖进度条 / 方向键 / 快进快退 /
    /// 点击轨道 / 跳过推广片段)。
    ///
    /// ★★★ 曾经为什么需要这么多补偿(2026-10-17 已全部删除, 这里是结论留档):
    ///   当年以为根因是"libvlc 对 frag-MP4 的 seek 有单次跳跃距离上限", 于是叠了
    ///   分段跳(每段 ≤150s)、重建补跳(最多 20 次、要求连续 3 次稳住)、看门狗重发。
    ///   探针 bd-probe-muxed5 实测否掉了这个前提 —— DASH 双流连发三次同一个 Time,
    ///   落点**完全一样**(偏差 -1180s/-590s/-910s 稳定复现), 说明不是"距离太大"而是
    ///   这条 input 的解复用器**认死了**, 补偿只能把一次跳换成多次跳, 中途照样丢。
    ///
    ///   真正的根因是 **`input-slave` 音轨**这个组合: 去掉它(改用 durl 合流单流)之后,
    ///   seek 一次到位, 于是整套补偿就没有存在意义了(见 BuildMedia 与 ApiClient)。
    /// </summary>
    private void SeekTo(long targetMs)
    {
        if (_closing || _mp == null) return;
        var length = _mp.Length;
        if (length <= 0) return;
        targetMs = Math.Clamp(targetMs, 0, Math.Max(0, length - 500));

        var cur = _lastKnownMs > 0 ? _lastKnownMs : 0;
        var delta = targetMs - cur;

        // ★★ 2026-11-05: 同一次点击会被**两条路**各发一次 ——
        //   ValueChanged(点击轨道时 WPF 先把值挪到点击点) + Thumb.DragCompleted
        //   (WPF 把点击轨道也当成一次拖动)。第二条到达时第一条的 seek 还在飞,
        //   再设一次 Time 会把 libvlc 的解复用器打断, 正是"点一下进度条却无限缓冲"的放大器。
        //   目标相同(±到位窗口)且上一次还没收尾 ⇒ 直接忽略。
        if (_pendingSeekMs >= 0 &&
            Math.Abs(_pendingSeekMs - targetMs) <= SeekConfirmWindowMs) return;

        // 已经在目标上(或只差 250ms): 没必要为一次"点在滑块上"启动整轮 seek。
        if (_pendingSeekMs < 0 && Math.Abs(cur - targetMs) < 250) return;

        // ★ 用户**主动**发起了一次新跳转 ⇒ 重建预算复位(新的一次该再给一次机会)。
        //   只在这里复位, 不在 MarkSeekIssued 里 —— 见 _seekRebuildTried 的说明。
        _seekRebuildTried = false;
        _eofSeekRebuildTried = false;

        // ★★★ 2026-11-05 修「播完 → 点进度条 → 画面全黑、重播按钮消失、没有画面」。
        //   已结束的 input 上发 seek 是**无效操作**: libvlc 只把 Time 改成目标值当回执,
        //   不重建 input、不重建 vout(探针 bd-probe-eofseek 实测 state 恒为 Ended、
        //   vout=0、位置不动)。而 MarkSeekIssued 会顺手把"重播页"收起来 ⇒ 屏幕上只剩黑。
        //   ⇒ 这条路必须**重建 Media**, 与"点重播"同一条路。
        //   判据用 `_ended`(我们自己的播完标记), 不用 `_mp.State`: 直播/本地文件也可能
        //   停在别的状态, 但它们根本不会走到这里(进度条对直播是禁用的)。
        //
        // ★ `_seekAfterEndedGuard` 挡住**同一次点击的第二条路**(ValueChanged +
        //   Thumb.DragCompleted): 第一条已经重建过了, 第二条若再对刚起播的新 input
        //   发一次 seek, 就会把它的解复用器打断(表现: 又要缓冲一次)。
        if (_seekAfterEndedGuard)
        {
            WriteLibVLCDebug("[Seek] 播完后的跳转已处理过, 忽略重复触发的那条\n");
            return;
        }
        if (_ended)
        {
            _seekAfterEndedGuard = true;
            WriteLibVLCDebug($"[Seek] 已播完, 进度条跳转 → 重建 Media(目标 {targetMs}ms)\n");
            RebuildMediaAndSeek(targetMs);
            return;
        }

        // ★★★ 2026-10-17 播放器手术第 2 步: 两条路, 由**片源类型**决定, 不由距离决定。
        //
        //   【可靠路】单文件合流(durl + :demux=avformat): seek 一次到位, 实测来回 7 跳
        //           偏差 0.9~2.2s 且画面持续推进(探针 bd-probe-muxed2)。
        //           ⇒ 只发一次 `_mp.Time = target`, 剩下的交给那套**通用**看门狗。
        //
        //   【旧路】DASH 双流(input-slave): libvlc 对 frag-MP4 的远跳会 `cannot seek`,
        //           位置掉回开头(实测偏差 -1180s/-910s/-590s, 探针 bd-probe-muxed5)。
        //           这条路救不回来 —— 分段跳/重建补跳实测只是把"一次跳"换成
        //           "多次跳", 中途照样丢, 用户体感就是"卡在半路、无限缓冲"。
        //           既然主路已换成可靠源(见 ApiClient.GetPlayUrlAsync), 这里
        //           **不再叠补偿**: 发一次 + 看门狗超时后重建一次, 重建也失败就如实
        //           停在原地(让用户看见真实位置), 好过让进度条说谎。
        //           ★ 2026-10-31: 曾试过"长跳一律直接重建"以修"跳 1 小时没声",
        //             结果引入"打开黑屏 / 拖进度条卡死"两个更严重的回归(见 RebuildMediaAndSeek),
        //             已回退。真正的无声根因是音量写回被 aout 就绪窗口吞掉, 由
        //             RestoreVolumeWhenReady 那套补写修复 —— 与跳转距离无关。
        if (_isReliableSeekSource)
        {
            try { _mp.Time = targetMs; } catch { }
            MarkSeekIssued(targetMs);
            return;
        }

        // DASH: 短跳(≤150s)实测可靠, 一步到位
        if (Math.Abs(delta) <= SeekSingleMaxMs)
        {
            try { _mp.Time = targetMs; } catch { }
            MarkSeekIssued(targetMs);
            return;
        }

        // 长跳: 发一次, 由看门狗确认; 确认不到就重建一次(见 UpdateProgressUi 的看门狗分支)
        try { _mp.Time = targetMs; } catch { }
        MarkSeekIssued(targetMs);
    }

    /// <summary>
    /// 当前片源的 seek 是否"一发就准"(单文件合流, 无 input-slave)。
    ///
    /// 由 <see cref="BuildMedia"/> 在建 Media 时顺带置位 —— 判定依据是"远端分片流":
    /// 挂了 input-slave 的 DASH 双流、以及「听视频」的音轨单流都是**分片 fMP4**
    /// (远跳不可靠), 只有 durl 合流单流与本地文件是普通 MP4(可靠)。
    /// </summary>
    private bool _isReliableSeekSource = true;

    /// <summary>单次 seek 能可靠完成的最大距离(毫秒)。超过它 libvlc 会把位置丢回原处。</summary>
    private const long SeekSingleMaxMs = 150_000;

    /// <summary>
    /// 重建 Media 后跳到目标 —— **看门狗放弃时的兜底**(不是长跳的常规路径)。
    ///
    /// ★ 这就是"换片/切清晰度"那条路: 丢一条全新的 Media 进去, 起播后立刻设目标。
    ///   实测落点偏差 416ms(见探针 I 段)。音轨 slave 与主输入**一起新建**,
    ///   不存在"slave 远跳掉线"的状态。
    ///
    /// ★★★ 2026-10-31 补 `Stop()`(修"打开长视频黑屏 / 拖进度条程序卡死"):
    ///   这条路径原先**漏了 Stop**。它此前只在看门狗彻底放弃时才走到, 而那种时刻旧
    ///   input 往往已经死了(seek 失败 → 状态回落), 所以 `play()` 还能被接受, 一直没暴露。
    ///   一旦让它承接更常见的情形, 就会踩中 LibVLC 3 的硬规则:
    ///   **已有 input 在播时 `play()` 被直接忽略**(返回值 false, 见 SwitchQualityAsync 的同款
    ///   注释) ⇒ 新 Media 根本没起播, 画面被 ShowLoading 遮着 ⇒ **黑屏**;
    ///   而 `_resumeAfterSwitchMs` 永远等不到 Playing, 那次跳转的恢复也一并丢失。
    ///   ⇒ 必须先 Stop 再 Play(与换片/切清晰度/重播那几条路完全一致)。
    ///   ★ 代价: `Stop()` 会阻塞约 1 秒(见 TeardownAndCloseAsync 的实测)。这条路径本来就
    ///     已经压着"正在跳转…"遮罩, 用户感知是"缓冲了一下", 可接受; 但**绝不能让它在
    ///     常规操作里反复触发** —— 那才会变成"卡死"。这正是长跳不再一律走这里的理由。
    /// </summary>
    /// <param name="stallRecovery">
    /// 这次重建是不是"播放中途卡顿自愈"发起的。失败时的收尾不同:
    /// 卡顿自愈要引导用户点「重试」, 而不是退回"已播完/重播"态(那会误导成片子看完了)。
    /// </param>
    private void RebuildMediaAndSeek(long targetMs, bool stallRecovery = false)
    {
        if (_closing) return;

        // ★ 2026-11-05: 这条重建可能是因为"在已结束的 input 上点了进度条"
        //   (见 SeekTo 里 _ended 的分支)。那种情况下必须先收掉"已播完"这套状态:
        //   否则 EndOverlay(重播页)还挂着, 用户看到的是"遮罩上盖着遮罩"。
        //   _ended 由 Playing 事件起播后自然清掉(见 MarkSeekIssued), 这里先清一次,
        //   保证"重播按钮消失"与"画面回来"是同步的。
        _ended = false;
        if (EndOverlay != null) EndOverlay.Visibility = Visibility.Collapsed;

        if (_isLocalPlayback)
        {
            var path = _lastPlayUrl;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                // 地址没了(文件被删): 什么都做不了, 但**别把重播入口也弄丢** ——
                // 让用户还能看到"已播完"并点重播重试。
                RestoreEndedOverlay();
                return;
            }
            ShowLoading("正在跳转…");
            _resumeAfterSwitchMs = targetMs;
            _resumeByStartTime = false;   // 本地文件走原来的"起播后设 Time"
            try { _mp.Stop(); } catch { }   // 同上: 不 Stop 则 play() 被忽略
            if (!PlayMedia(new Media(_libVLC, path, FromType.FromPath)))
            {
                _resumeAfterSwitchMs = 0;
                WriteLibVLCDebug("[Seek] 重建后 play() 未被接受(本地文件)\n");
                RestoreEndedOverlay();
            }
            return;
        }

        var url = _lastPlayUrl;
        if (string.IsNullOrEmpty(url))
        {
            // 地址没了(直播失效/切走了): 同样别把重播入口弄丢
            if (stallRecovery) ShowFail("播放地址已失效, 请点击重试重新获取");
            else RestoreEndedOverlay();
            return;
        }

        // ★★★ 2026-11-14(issue #81): 聆听态必须用**音轨地址**重建。
        //   原来固定拿 `_lastPlayUrl`(视频流)再传 `audioUrl: null`, 于是聆听态下一旦
        //   走重建(卡顿自愈 / seek 看门狗), 就会建出一条"只有画面没有声音"的 input ——
        //   而顶栏还写着"正在聆听"。这和 EnterListenMode/重播/切清晰度的约定相反。
        var audioForRebuild = _isListenMode ? null : _lastAudioUrl;
        var urlForRebuild = _isListenMode && !string.IsNullOrEmpty(_listenAudioUrl)
            ? _listenAudioUrl!
            : url;
        WriteLibVLCDebug(
            $"[Seek] 重建 Media url={(urlForRebuild == url ? "video" : "listen-audio")} " +
            $"audio={(!string.IsNullOrEmpty(audioForRebuild) ? "slave" : "none")} " +
            $"stall={stallRecovery} start-time={targetMs}ms\n");
        ShowLoading("正在跳转…");
        // ★ 重建前把"最后已知位置"清掉: 它记的是**旧 input** 的位置(比如 72 万 ms),
        //   留着的话补跳会拿它当"当前位置"算出荒谬的下一段(2026-10-17 实测踩到)。
        _lastKnownMs = 0;
        // 起播后由 Playing 事件读到这个值并**只打看门狗、不再二次设 Time**
        // (原因见 _resumeByStartTime 的说明)。
        _resumeAfterSwitchMs = targetMs;
        _resumeByStartTime = true;
        // ★★ 必须先 Stop: LibVLC 3 里已有 input 在播时 play() 会被**直接忽略**
        //    —— 漏掉这一步就是"黑屏 + 恢复进度丢失"(见方法说明)。
        // ★ 2026-11-05: 播完之后那条 input 已经停在 ENDED(EOF 时刻意不 Stop),
        //   `play()` 同样会被忽略 —— 探针 bd-probe-eofseek 实测: 不 Stop 时新 Media
        //   根本起不来(state 恒为 Ended、vout=0), 于是"画面全黑、没有画面"。
        //   ⇒ 播完后的跳转必须 Stop 掉那条死 input, 与上面那条路完全一致。
        try { _mp.Stop(); } catch { }
        // ★ 2026-11-05: 重建时把目标交给 **Media 的 :start-time** —— 让解复用器在 open
        //   阶段直接定位, 而不是"从 0 起播后再设一次 Time"。实测(探针 bd-probe-seekmode):
        //   起播即落在目标附近(偏差 ~1.8s)且位置持续推进, 比"Playing 里的二次 seek"稳,
        //   后者在 input 尚未稳定的窗口里设 Time 会再次落回开头 —— 正是用户报的"回到 8 秒"。
        if (!PlayMedia(BuildMedia(urlForRebuild, audioForRebuild, targetMs)))
        {
            _resumeAfterSwitchMs = 0;
            _resumeByStartTime = false;
            WriteLibVLCDebug("[Seek] 重建后 play() 未被接受(远端流)\n");
            // 起播没被接受 ⇒ 屏幕上不会有画面。此时必须把"重播"入口还回去,
            // 否则用户面对的是"遮罩收掉了但什么都没有"(这正是本次要修的那个症状)。
            // ★ 卡顿自愈发起的重建: 退回"已播完"是误导(用户会以为片子看完了),
            //   应该给出可重试的失败态。
            if (stallRecovery) ShowFail("网络卡顿，自动重连失败，请检查网络后重试");
            else RestoreEndedOverlay();
        }
    }

    /// <summary>
    /// 重建失败时把"已播完"那套界面状态还回去 —— 让用户还能看到重播按钮并重试。
    ///
    /// ★ 2026-11-05: 这是"播完点进度条"那条路的**兜底**。正常情况下重建成功,
    ///   画面回来、重播页收起; 但若地址失效/播放没被接受, 就会出现
    ///   "遮罩没了、按钮也没了、什么都没有" —— 与用户报的症状一模一样。
    ///   ⇒ 失败时宁可退回"已播完"态(有按钮可点), 也不要留一个死界面。
    /// </summary>
    private void RestoreEndedOverlay()
    {
        if (_closing) return;
        _ended = true;
        // 重播页的遮罩是半透明的, 底下是 EOF 黑层 —— 那层黑此刻正好盖住被拆掉的
        // vout 露出的白底, 所以不用额外处理。
        if (EndOverlay != null) EndOverlay.Visibility = Visibility.Visible;
        // ★ 闸要放开: 退回"已播完"态是让用户**再试一次**, 不能把他锁在"点进度条没反应"里。
        _seekAfterEndedGuard = false;
        HideBufferingIfShown();
        WriteLibVLCDebug("[Seek] 重建失败 → 退回「已播完」态(保留重播入口)\n");
    }

    /// <summary>进度条值变化: 点击轨道任意位置直接跳转(IsMoveToPointEnabled + 本处理器)。
    /// 拖动过程由 _isSeeking 屏蔽, 松手才生效</summary>
    private void OnProgressValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_closing || _mp == null || _updatingSlider) return;

        // ★ 拖动中: 只更新气泡, **一个 seek 都不发**。
        //   发 seek 会打断正在飞的解复用器(表现: 无限缓冲), 所以拖动期间只提示目标时间,
        //   真正的跳转等松手(见 OnProgressDragEnd)。
        //   ★ _isSeeking 必须是**单独**一条分支: 它原先和别的条件合在一起提前 return,
        //     那样气泡就永远等不到"拖动中"这个信号。
        //   ★ 还要看 _progressDragMoved: "点击轨道"也会走这条路(见 OnProgressDragDelta),
        //     那种情况下不该冒气泡。
        if (_isSeeking)
        {
            if (_progressDragMoved) UpdateProgressDragHint(e.NewValue);
            return;
        }

        // ★ 非拖动的程序化回写(心跳/seek 钉值)不该赶走悬停气泡: 悬停时气泡显示的是
        //   **鼠标位置**的时间, 跟播放进度无关; 而且此时气泡如果是悬停亮起的,
        //   这里若把它藏掉, 用户会发现气泡"自己灭了"。程序化改动不碰气泡。
        if (_mp.Length <= 0) return;

        // 用 Time(毫秒) 而不是 Position(0~1 的浮点): 位置量本身就是毫秒,
        // 少一层换算, 短视频(6 秒时 0.001 只代表 6ms)上也不会被"变化太小"的过滤挡掉。
        var targetMs = (long)Math.Round(e.NewValue / 1000.0 * _mp.Length);
        // ★ 这里读 _lastKnownMs 而不是 _mp.Time: seek 受理后 _mp.Time 已经是**目标值**,
        //   用它判"已在附近"等于永远成立, 同一次点击的第二条 seek 就挡不住。
        var currentMs = _lastKnownMs > 0 ? _lastKnownMs : _mp.Time;
        if (Math.Abs(currentMs - targetMs) < 250) return; // 已经在目标附近, 不必发跳转
        SeekTo(targetMs);
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            _totalMs = e.Length;
            UpdateTotalDurationText();
        }));

    /// <summary>上一次 TimeChanged 的位置(遮罩安全网用来判"位置真的在推进")。-1 = 还没收到。</summary>
    private long _lastLoadingRevealMs = -1;

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            // 记下最后已知位置: EOF 之后 _mp.Time 会归零(那条 input 已结束),
            // "提前结束自救"只能靠这里留底
            //
            // ★ 只收**合法值**: libvlc 在 input 重建 / 分段 seek 途中会报出 -1 或
            //   超大值(实测读到 18446744073709551 —— ulong 溢出), 一旦写进 _lastKnownMs,
            //   依赖它的分段跳/看门狗就会算出一个荒谬的目标(2026-10-17 修 seek 时踩到)。
            if (e.Time >= 0 && e.Time < 24L * 3600 * 1000)
                _lastKnownMs = e.Time;
            // ★ EOF 黑层: 能收到时间推进 = 视频正在出帧, 这一刻撤掉最准。
            //   不能只靠 Playing 事件 —— 进全屏时视频本来就在播, Playing 不会再来一次。
            RestoreVideoAreaBackground();
            // ★★ 2026-10-31 遮罩安全网(修"打开长视频黑屏"的兜底):
            //   "正在跳转…""正在缓冲视频…"这类遮罩**只由 Playing 事件收掉**。万一那次
            //   play() 没被接受(或被 LibVLC 直接忽略), Playing 永远不来, 遮罩就一直盖着
            //   画面 —— 用户看到的就是"黑屏"(其实背后可能已经在播)。
            //   这里加一道客观判据: **位置真的在推进**(连续两次 TimeChanged 且值在涨)说明
            //   画面已经在出帧, 遮罩没有存在意义, 强制收起。
            //   ★ 只收"跳转/缓冲"这一类, 不动"正在切换清晰度""正在获取播放地址" ——
            //     那些是加载链自己的生命周期(见 HideBufferingIfShown 的同款约定)。
            if (e.Time >= 0 && e.Time != _lastLoadingRevealMs)
            {
                if (_lastLoadingRevealMs >= 0 && e.Time > _lastLoadingRevealMs &&
                    LoadingOverlay != null && LoadingOverlay.Visibility == Visibility.Visible &&
                    LoadingText != null &&
                    (IsStallOverlayText(LoadingText.Text) ||
                     LoadingText.Text.StartsWith("正在跳转", StringComparison.Ordinal)))
                {
                    HideLoading();
                }
                _lastLoadingRevealMs = e.Time;
            }
            // ★ 这里**不**调 UpdateProgressUi —— 进度条与跳转看门狗的唯一心跳是 _progressTimer。
            //   曾经这里也在调, 于是进度 UI 有两个时钟: 等待期定时器 100ms 一跳, 而 TimeChanged
            //   会在两跳之间插进来。这不只是重复干活 —— 看门狗判"落地但偏早"要求
            //   **连续两次心跳**都成立(见 UpdateProgressUi), 那道闸的用意是挡掉 input 重启
            //   那一瞬的过渡位置; 双时钟会把"两次采样"压到 1ms 内, 过渡值照样骗过它,
            //   后果是 seek 静音提前解除、缓冲提示提前消失(画面没回来声音先到)。
            //   定时器全程在跑(构造里 Start, 等待期 100ms / 平时 500ms), 少这一个调用不丢刷新。
            DispatchDanmaku();
            // 字幕同样要在这对齐一次(2026-11-15): 心跳是 100ms 粒度, seek 后靠 TimeChanged
            // 立即把字幕切到新位置 —— 否则拖完进度条字幕还会挂在旧画面上最多 100ms。
            RefreshSubtitleNow();
            MaybeSyncProgress();
        }));

    /// <summary>
    /// 播放中周期上报进度到云端历史。
    /// 节流逻辑全部在 HistorySyncService 里, 这里只是"到点就报"。
    /// 两个额外的守卫:
    ///   - 只在真正处于播放态时上报(暂停/拖动时不报, 避免上报错误的中间进度);
    ///   - 拖动进度条期间(_isSeeking)跳过, 松手后下一次心跳自然会带上新位置。
    /// 整个调用是 fire-and-forget: 上报失败绝不能影响播放。
    /// </summary>
    private void MaybeSyncProgress()
    {
        if (_closing || _mp == null) return;
        if (!_mp.IsPlaying || _isSeeking) return;
        if (_aid <= 0 || _currentCid <= 0) return;

        var progressSec = _mp.Time / 1000;
        // 本机进度: 比云端 15 秒上报更细, 关闭/崩溃后也能续播。
        Svc.PlaybackProgress.MaybeSave(_currentBvid, _currentCid, _mp.Time, _mp.Length,
            _isLive, _isLocalPlayback);

        // ShouldReport 内部已含"未登录不上报"的语义(ReportHistoryAsync 会静默跳过),
        // 这里先做一次本地判断, 避免无意义地起 Task。
        if (!Svc.HistorySync.ShouldReport(_aid, _currentCid, progressSec)) return;

        _ = Svc.HistorySync.ReportAsync(_aid, _currentCid, progressSec);
    }

    private void UpdateTotalDurationText()
    {
        if (_closing || _totalMs <= 0) return;
        TimeText.Text = $"00:00 / {FormatClock(TimeSpan.FromMilliseconds(_totalMs))}";
    }

    /// <summary>
    /// 播放结束。LibVLC 官方警告: 绝不能在其事件回调线程里直接调 Stop()(必死锁),
    /// 必须异步派发到 UI 线程执行, 且 Stop 包 try/catch(状态竞争时可能抛异常)
    /// </summary>
    /// <summary>
    /// 播完了。**刻意不调用 `_mp.Stop()`**:
    /// Stop 会把视频输出整个拆掉, 那个 HWND 就成一块没有内容的空白面 —— 浅色界面下看着
    /// 就是"整块全白"(用户报的就是这个)。停在那里不动、上面盖一层遮罩, 既挡住空白,
    /// 又能顺手给出"重播"入口; 下次播放走 RestartFromBeginning 把时间归零。
    ///
    /// LibVLC 官方警告: 绝不能在其事件回调线程里直接操作播放器(必死锁), 所以这里依旧
    /// 异步派发到 UI 线程。
    /// </summary>
    private void OnEndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing) return;

        // ---- 提前结束的自救 ----
        // 远端流在离结尾还很远时 EOF(网络截断/服务端只给到某一段), 从最后已知位置
        // 重建 Media 续播(次数与推进距离双保险, 防止片源本来就短时无限循环续播)。
        //
        // 历史上的触发原因是"DASH 音轨(input-slave)先于视频 EOF", 2026-11-05 已查明
        // 那是 libvlc mp4 demux 读不出 fMP4 最后分片的 bug, 现由 `:demux=avformat` 修复
        // (见 BuildMedia)。这里保留自救作为远端截断的兜底。
        //
        // 两个收紧(短视频"播完又跳回几秒重播"的根因都在这):
        //   1. 长度基准用 **_mp.Length**(LibVLC 实际解封装出的时长), 而不是接口的
        //      timelength —— B 站接口给的 timelength 经常比流的真实时长长几秒,
        //      按 _totalMs 判断会把"正常播完"误判成"提前结束", 于是重建 Media
        //      从"几秒前"续播 —— 用户看到的就是"明明结束了又自己倒回去放"。
        //   2. 单文件源(_lastAudioUrl == null, durl/合流)没有独立音轨,
        //      不存在"音轨先 EOF"这个问题, 直接跳过自救 —— 否则任何风吹草动的
        //      提前 EOF 都会在单文件源上触发一次无谓的回跳续播。
        //
        //   ★★★ 3(2026-11-01 修「4h 视频快进回到 9s 处」): **跳转进行中绝不自救**。
        //      DASH 双流远跳失效时, 音轨 seek 失败会拖死整个 input 触发本事件 ——
        //      而此刻 _lastKnownMs 已经掉回片头(用户报的就是 ~9s), 用它当断点续播
        //      等于"从坏位置再播一次", 正是"快进 → 缓冲几次 → 回到 9s"的最后一环。
        //      看门狗自己会重建到目标(_pendingSeekMs 还挂着), 这里必须让路。
        //
        //   ★★★ 4(2026-11-05 修「点进度条快进后回到开头 8 秒」): 光判 pending 还不够 ——
        //      看门狗可能刚刚**放弃**(pending 被清成 -1), 紧接着音轨 EOF 才到, 于是
        //      这条自救拿掉回开头的 _lastKnownMs 当断点。现在再判一次"最近一次 seek
        //      的目标与当前位置差得远", 差得远就绝不按当前位置续播, 而是用 start-time
        //      重建到目标(只给一次预算, 防循环)。
        var effectiveTotalMs = _mp.Length > 0 ? _mp.Length : _totalMs;
        var seekAgeMs = _seekIssuedAt >= 0
            ? Environment.TickCount64 - _seekIssuedAt
            : long.MaxValue;
        var seekLooksUnlanded = _lastSeekTargetMs >= 0 &&
                                seekAgeMs <= SeekEofRecoverWindowMs &&
                                Math.Abs(_lastKnownMs - _lastSeekTargetMs) > SeekEofRecoverMinMissMs;
        if (!_isLive && !_isLocalPlayback && !_ended && _pendingSeekMs < 0 &&
            !seekLooksUnlanded &&
            effectiveTotalMs > 0 &&
            !string.IsNullOrEmpty(_lastAudioUrl) &&
            // 尾部静默区见 TailQuietZoneMs 的说明: 距结尾 10s 内重建只会黑屏缓冲一趟,
            // 即使接上也只剩几秒, 不值得(2026-10-01 用户两轮实测)
            _lastKnownMs > 0 && _lastKnownMs < effectiveTotalMs - TailQuietZoneMs &&
            _eofResumeCount < EofResumeMax &&
            _lastKnownMs - _lastEofResumeMs >= EofResumeMinAdvanceMs &&
            !string.IsNullOrEmpty(_lastPlayUrl))
        {
            _eofResumeCount++;
            _lastEofResumeMs = _lastKnownMs;
            var resume = _lastKnownMs;
            ShowLoading("正在继续播放…");
            // 起播后由 Playing 事件恢复到断点(见 _resumeAfterSwitchMs 那套机制)
            _resumeAfterSwitchMs = resume;
            _resumeByStartTime = false;
            // ★ 2026-11-14(issue #81): 聆听态只能续音轨。原来无条件用
            //   BuildMedia(_lastPlayUrl, _lastAudioUrl) —— 在聆听态下会把"视频+音轨"
            //   整个塞回来(画面突然复活、还多拉一路视频流)。与 EnterListenMode /
            //   RebuildMediaAndSeek 的约定保持一致。
            try
            {
                if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                    PlayMedia(BuildMedia(_listenAudioUrl!, null));
                else
                    PlayMedia(BuildMedia(_lastPlayUrl!, _lastAudioUrl));
            }
            catch { }
            return;
        }

        _ended = true;
        RestoreVolumeAfterSeek();   // 播完若还挂在 seek 静音里, 立刻恢复(重播/下一条别哑)
        // ★ 把手势层切成不透明黑(2026-10-01, 用户报"浅色全屏全白/深色一层阴影遮罩"):
        //   EOF 后 vout 被拆掉, 视频 HWND 露出**默认白底** —— 它在所有 WPF 元素之上
        //   (airspace), RootGrid 切黑盖不住它。表现就是: 浅色主题全屏一块纯白;
        //   深色主题上半透明的"重播"遮罩(#CC000000)盖在白底上 = "一层阴影遮罩"。
        //   手势层在覆盖窗口(VideoView.Content)里, 位于 HWND **之上**, 切黑才真的盖得住。
        //   ★ 2026-11-03: 这是**唯一**还压黑手势层的路径。resize/最大化/全屏的白闪已改为
        //     在原生宿主窗口上根治(见 VideoBackdropInterop.EnsureBlackVideoHost), 不再压黑布。
        //     所以这里只需记一个 `_eofBlackActive`, 起播时由 RestoreVideoAreaBackground() 撤掉。
        //   ★★★ 2026-11-01 修「听视频模式快进提示已播完」: EOF 时若还有**未确认的跳转**
        //     在飞, 那多半是远跳甩到流尾的假 EOF(音轨 seek 失败被当成播完) ——
        //     上面那条"跳转中不自救"的闸会放它落到这里。此刻不能宣告播完:
        //     把 _ended 压回去、给目标重新打上 MarkSeekIssued 走看门狗重建, 唯独不进重播态。
        //   ★ 2026-11-05: 看门狗刚放弃(pending=-1)但落点离 seek 目标还很远时同样处理,
        //     且只给一次预算(_eofSeekRebuildTried) —— 防"重建→假EOF→再重建"死循环。
        if (!_isLive && !_isLocalPlayback && !_eofSeekRebuildTried &&
            !string.IsNullOrEmpty(_lastPlayUrl) &&
            (_pendingSeekMs >= 0 || seekLooksUnlanded))
        {
            var target = _pendingSeekMs >= 0 ? _pendingSeekMs : _lastSeekTargetMs;
            _ended = false;
            _eofSeekRebuildTried = true;
            WriteLibVLCDebug(
                $"[EndReached] 疑似远跳假EOF(target={target}ms time={_lastKnownMs}ms " +
                $"pending={_pendingSeekMs}) → 重建 Media 续跳\n");
            // ★ 先换文案再清看门狗并保持遮罩可见: 避免淡出/淡入造成闪屏。
            ShowLoading("正在跳转…");
            ClearSeekWatchdog(hideBuffering: false);
            RebuildMediaAndSeek(target);
            return;
        }
        WriteLibVLCDebug(
            $"[EndReached] 压黑手势层 time={_lastKnownMs} bgBefore={DescribeVideoAreaBg()}\n");
        VideoAreaRoot.Background = Brushes.Black;
        _eofBlackActive = true;
        // 播完了: 跳转看门狗必须收掉 —— 它在外面的条件是 !_ended, 留着 _pendingSeekMs >= 0
        // 会让卡顿检测被永久跳过(那个判断里也有 _pendingSeekMs >= 0), 之后重播的卡顿就不再提示了。
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        // 字幕必须藏: "已播完"遮罩下面还挂着最后一行字幕, 起重播后又会闪出旧文本
        HideSubtitleLayer();
        EndOverlay.Visibility = Visibility.Visible;
        // 进度条钉在满格 + 时间显示成总时长, 否则 Position 可能报 0, 看起来像"回到开头了"
        UpdateProgressUi();
        UpdatePlayButton();

        // 播到结尾也算一次"自然的段落结束", 把进度补报到云端(下次能从结尾续上)。
        // 注意不能用 _mp.Time —— EOF 之后它已经归零, 会把云端进度错误地报成 0;
        // 用 TimeChanged 留底的 _lastKnownMs 才是真实看完的位置。
        // ★ 本机记录在看完时删除: 下次打开这条视频应该从头开始, 而不是一进去就播完。
        Svc.PlaybackProgress.MarkCompleted(_currentBvid, _currentCid);
        if (_aid > 0 && _currentCid > 0)
            _ = Svc.HistorySync.ReportNowAsync(_aid, _currentCid, _lastKnownMs / 1000);
    }));

}
