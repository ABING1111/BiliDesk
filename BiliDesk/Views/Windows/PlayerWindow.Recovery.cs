using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BiliDesk.Services;
using BiliDesk.Utils;
using LibVLCSharp.Shared;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 播放中途卡顿的自愈
    //
    // ★★★ 2026-11-14(issue #81「长时间播放/听视频后出现正在缓冲并卡住」):
    //
    //   症状: 挂着听几个小时音频(或长视频)之后, 画面/声音全停、进度条不动,
    //        遮罩一直写着"正在缓冲"; 用户点一下「听视频」切到视频模式(或切回来)
    //        就立刻恢复。
    //
    //   排查结论(证据见 长期记忆/vlc-debug.txt 与本机 libvlc 3.0.24 探针):
    //     · 真正卡住的是 LibVLC 那条 HTTP/fMP4 input —— 长连接被 CDN 静默掐断后,
    //       读线程会永久阻塞, 而且因为 access 没报错, 连 EncounteredError 都不会来;
    //     · B 站 playurl 的签名只有约 **2 小时**有效期(URL 里的 deadline),
    //       "听几个小时"必然跨过它 —— 届时任何新建连接(reconnect/重建/seek 的
    //       Range 请求)都会 403, 而项目从不重取地址;
    //     · 播放器对卡顿**只弹遮罩、从不重建 input**, 所以只有用户手动切模式
    //       (内部是 Stop + 新 Media)能自救。
    //
    //   修法(本文件 + BuildMedia 里的 :http-reconnect + StallRecoveryPolicy):
    //     ① 断流时由 VLC 自己重连(第一道保险);
    //     ② 位置冻结 6s 换文案, 10s 自动重建 input(= 用户那套操作), 退避重试 3 次;
    //     ③ 重建前若地址已过期/太旧, 先重取 playurl(只换"将来重建用"的那一份);
    //     ④ 仍失败给出「重试」入口, 不再无限转圈。

    /// <summary>读播放器状态: 原生对象可能已失效(窗口正在关闭), 这里统一兜住。</summary>
    private VLCState SafePlayerState()
    {
        try { return _mp.State; }
        catch { return VLCState.Error; }
    }

    /// <summary>
    /// 执行 <see cref="StallRecoveryPolicy"/> 给出的动作。
    /// ★ 只有这一个地方会因为"卡顿"动播放器 —— 别在别的路径上加第二套判定。
    /// </summary>
    private void HandleStallAction(StallRecoveryAction action, long posMs)
    {
        switch (action)
        {
            case StallRecoveryAction.ShowHint:
                if (LoadingOverlay == null || LoadingText == null) return;
                if (LoadingOverlay.Visibility == Visibility.Visible)
                {
                    // 遮罩上可能写着"正在切换清晰度…"之类(它有自己的生命周期), 只换我们自己那张
                    if (IsShowingBuffering()) SetLoadingText(StallReconnectingText);
                }
                else
                {
                    ShowLoading(StallReconnectingText);
                }
                break;

            case StallRecoveryAction.Rebuild:
                WriteLibVLCDebug(
                    $"[Stall] 卡顿自愈第{_stallPolicy.Rebuilds}次: 重建 input " +
                    $"pos={posMs}ms listen={_isListenMode} " +
                    $"deadline={_playUrlDeadline} url={HostOf(CurrentMediaUrl)}\n");
                _ = RecoverFromStallAsync();
                break;

            case StallRecoveryAction.GiveUp:
                WriteLibVLCDebug(
                    $"[Stall] 自动重连放弃(已重建 {_stallPolicy.Rebuilds} 次) pos={posMs}ms " +
                    $"listen={_isListenMode} url={HostOf(CurrentMediaUrl)}\n");
                // 不再无限转圈: 复用失败遮罩(它自带「重试」按钮, 见 OnRetryClick)。
                ShowFail("网络卡顿，自动重连失败，请检查网络后重试");
                break;
        }
    }

    /// <summary>
    /// 卡顿自愈: 重建一条全新 input, 从 <see cref="_lastKnownMs"/> 接上。
    ///
    /// ★ 这就是用户手动"切一下听视频"做的事 —— `Stop()` + 新的 `Media`
    ///   (见 EnterListenMode), 只是不需要用户操作、也不改变视频/音频模式。
    /// ★ 重建前先做两件"如果真是地址过期就必须做"的事:
    ///   重取 playurl(旧地址 403 时重建等于白重建)、必要时换一条候选线路。
    /// </summary>
    private async Task RecoverFromStallAsync()
    {
        if (_closing || _mp == null || _stallRecoveryInFlight) return;

        var gen = _loadGen;
        _stallRecoveryInFlight = true;
        try
        {
            // 先把文案换掉: 重取地址可能要几百毫秒, 期间不能让用户对着旧文案干等。
            ShowLoading(StallReconnectingText);
            ClearSeekWatchdog(hideBuffering: false);

            // ① 地址保鲜: 跨过签名有效期(或地址已经很旧)时先换一条新地址。
            if (ShouldRefreshUrlBeforeRebuild())
            {
                await RefreshPlayUrlAsync();
                if (_closing || gen != _loadGen) return;
                // 重取期间用户拖了进度条/发起了新跳转: 让位给 seek 看门狗。
                if (_isSeeking || _pendingSeekMs >= 0) return;
            }

            // ② 同一条线路已经卡死过两次: 换一条候选 CDN 再试(只影响"下次重建用的地址")。
            if (_stallPolicy.Rebuilds >= 2) UseBackupUrlIfAny();

            // ③ 起新 input, 位置接回来。聆听态由 RebuildMediaAndSeek 自己挑音轨地址。
            var resume = _lastKnownMs > 0 ? _lastKnownMs : 0;
            RebuildMediaAndSeek(resume, stallRecovery: true);

            // 旧的位置基线作废: 新 input 从 Opening 开始, 下一跳重新取参照。
            _stallLastMs = -1;
            _stallTicks = 0;
        }
        catch (Exception ex)
        {
            WriteLibVLCDebug($"[Stall] 自愈异常: {ex}\n");
        }
        finally
        {
            _stallRecoveryInFlight = false;
        }
    }

    // ------------------------------------------------------------ 播放地址保鲜

    /// <summary>
    /// 心跳里调(500ms 一次, 平时零开销): 地址快过期时后台换一条新的。
    /// ★ 只更新"将来重建 / 重播"用的 <see cref="_lastPlayUrl"/> 等字段,
    ///   **绝不动当前在播的 input** —— CdnService 里"播放中途不换源"的结论仍然有效
    ///   (换源实测会花屏/音画不同步)。
    /// </summary>
    private void MaybeRefreshPlayUrl()
    {
        if (_closing || _urlRefreshInFlight || _stallRecoveryInFlight) return;
        if (!ShouldRefreshUrlBeforeRebuild()) return;
        // 失败后别每个心跳都打接口
        if (_urlRefreshAttemptAt > 0 &&
            Environment.TickCount64 - _urlRefreshAttemptAt < UrlRefreshRetryMs) return;
        _ = RefreshPlayUrlAsync();
    }

    /// <summary>当前存着的地址是不是"该换了"(已过期 / 快过期 / 太旧)。</summary>
    private bool ShouldRefreshUrlBeforeRebuild()
    {
        if (_isLive || _isLocalPlayback || _aid <= 0 || _currentCid <= 0) return false;
        if (string.IsNullOrEmpty(_lastPlayUrl) ||
            !_lastPlayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        // 正在换清晰度: 那边会自己取流并覆盖地址, 这里别插一脚
        if (_switchingQuality) return false;

        if (_playUrlDeadline > 0 &&
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= _playUrlDeadline - UrlRefreshLeadSeconds)
            return true;

        // 地址里没有 deadline(极少数线路)时的兜底: 90 分钟换一次
        return _playUrlFetchedAt > 0 &&
               Environment.TickCount64 - _playUrlFetchedAt >= UrlRefreshFallbackMs;
    }

    /// <summary>
    /// 重取一次 playurl, 只把结果写进"将来重建用"的地址字段。
    /// 失败静默(记一行日志), 下一轮心跳 60 秒后还会再试。
    /// </summary>
    private async Task RefreshPlayUrlAsync()
    {
        if (_closing || _urlRefreshInFlight) return;
        var bvid = _currentBvid;
        var cid = _currentCid;
        if (string.IsNullOrEmpty(bvid) || cid <= 0) return;

        var gen = _loadGen;
        var qn = _currentQn;
        _urlRefreshInFlight = true;
        _urlRefreshAttemptAt = Environment.TickCount64;
        try
        {
            var (ok, err, main, backups, _, _, _, audioUrl, _, _, _, _, _) =
                await Svc.Api.GetPlayUrlAsync(bvid, cid, qn, Svc.Settings.PreferredAudioTier);

            // 期间换了片/换了分P: 结果是上一条的, 丢掉
            if (_closing || gen != _loadGen || _currentCid != cid) return;
            if (!ok || string.IsNullOrEmpty(main))
            {
                WriteLibVLCDebug($"[UrlFresh] 重取地址失败: {err}\n");
                return;
            }

            // ★ 只换"将来用"的那一份。保持原来的源形状:
            //   · 原来挂了 input-slave(DASH 双流)才更新 _lastAudioUrl;
            //   · 原来没有音轨(durl 单流 / 本地合流)就不要凭空塞一条 slave 进去
            //     (那等于给同一个文件再挂一条音轨, 声音会叠)。
            var wasDash = !string.IsNullOrEmpty(_lastAudioUrl);
            _lastPlayUrl = main;
            RememberPlayUrlFreshness(main, backups);
            if (wasDash && !string.IsNullOrEmpty(audioUrl)) _lastAudioUrl = audioUrl;
            if (!string.IsNullOrEmpty(audioUrl)) _listenAudioUrl = audioUrl;
            _playUrlRefreshedAt = Environment.TickCount64;
            WriteLibVLCDebug($"[UrlFresh] 已换新地址 listen={_isListenMode} host={HostOf(main)}\n");
        }
        catch (Exception ex)
        {
            WriteLibVLCDebug($"[UrlFresh] 异常: {ex.Message}\n");
        }
        finally
        {
            _urlRefreshInFlight = false;
        }
    }

    /// <summary>
    /// 换一条候选线路(playurl 的 backups)作为"下次重建用的地址"。
    /// ★ 只在**已经重建失败过两次**时调: 同一条 CDN 刚卡死过, 原地重试大概率还是它。
    /// ★ 聆听态跳过: backups 是**视频流**的候选, 音轨地址没有对应的候选表
    ///   (音频那条靠 CdnService.SelectSecondaryUrl 在重取地址时换)。
    /// </summary>
    private void UseBackupUrlIfAny()
    {
        if (_isListenMode || _backups.Count == 0) return;
        if (string.IsNullOrEmpty(_lastPlayUrl) ||
            !_lastPlayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

        var next = _backups[0];
        _backups.RemoveAt(0);
        if (string.IsNullOrEmpty(next) || string.Equals(next, _lastPlayUrl, StringComparison.Ordinal))
            return;
        _lastPlayUrl = next;
        WriteLibVLCDebug($"[Stall] 换候选线路: {HostOf(next)}\n");
    }

    /// <summary>
    /// EncounteredError 的免打扰自救(返回 true = 已接管, 不要再弹失败遮罩)。
    ///
    /// ★ 场景(issue #81 的"播几个小时后"): 长连接断开后 VLC 自己重连(`:http-reconnect`),
    ///   但 Media 里那条地址已经过了 2 小时签名有效期 → 403 → EncounteredError。
    ///   这时我们手里通常已经有一条**刷新过的新地址**(心跳在 1h50m 就换好了),
    ///   用它自动重建一次即可无缝续播, 不必用一句"视频播放出错"打断用户。
    ///
    /// ★ 只在"本次播放确实刷新过地址"且刷新不算太久远时才动手 —— 否则错误与过期无关
    ///   (付费/解码/文件不存在), 重建一次只是白白多等两秒, 直接交给失败遮罩更诚实。
    /// ★ 每次播放只给一次预算(_errorRecoveryTried), 换片/手动重试会复位, 绝不循环。
    /// </summary>
    private bool TryAutoRecoverFromError()
    {
        if (_closing || _isLive || _isLocalPlayback || _errorRecoveryTried) return false;
        if (_playUrlRefreshedAt <= 0) return false;
        if (string.IsNullOrEmpty(_lastPlayUrl)) return false;
        // 刷新已经过去半小时以上: 那条新地址的可信度也在下降, 不再盲试
        if (Environment.TickCount64 - _playUrlRefreshedAt > 30 * 60 * 1000) return false;

        _errorRecoveryTried = true;
        WriteLibVLCDebug("[Stall] 播放出错 → 用刷新后的地址自动重建一次\n");
        _ = RecoverFromStallAsync();
        return true;
    }

    /// <summary>当前真正在拉的那条流(聆听态是音轨地址, 其余是视频地址)。诊断/卡顿归因用。</summary>
    private string? CurrentMediaUrl =>
        _isListenMode && !string.IsNullOrEmpty(_listenAudioUrl) ? _listenAudioUrl : _lastPlayUrl;

    /// <summary>
    /// 记下"这份播放地址的有效期与候选线路"。
    /// ★ 两条取流路径(起播 / 换清晰度)必须共用: 各写一遍必然漏一处,
    ///   而漏掉的那次会让地址保鲜拿着**上一条地址**的 deadline 做判断。
    /// </summary>
    private void RememberPlayUrlFreshness(string? mainUrl, IEnumerable<string>? backups)
    {
        _backups.Clear();
        if (backups != null) _backups.AddRange(backups);
        _playUrlDeadline = UrlParser.ExtractQueryLong(mainUrl, "deadline");
        _playUrlFetchedAt = Environment.TickCount64;
        if (_playUrlDeadline > 0)
            WriteLibVLCDebug(
                $"[UrlFresh] deadline={_playUrlDeadline} " +
                $"({DateTimeOffset.FromUnixTimeSeconds(_playUrlDeadline).ToLocalTime():yyyy-MM-dd HH:mm:ss} 本地)\n");
    }

    /// <summary>从媒体地址里取 host(日志用; 取不到返回"?"), 避免把整条带签名的 URL 写进日志。</summary>
    private static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        try { return new Uri(url).Host; } catch { return "?"; }
    }
}
