using System;

namespace BiliDesk.Services;

/// <summary>画面输出冻结判定此刻该做什么(见 <see cref="VoutFreezePolicy"/>)</summary>
public enum VoutFreezeAction
{
    /// <summary>什么都不用做(统计还没就绪 / 还没到阈值 / 正在沉降)</summary>
    None,

    /// <summary>画面输出确认停住: 用软件解码重建一次当前 input</summary>
    Recover,

    /// <summary>自动恢复预算用完仍然停住: 收手, 交给用户</summary>
    GiveUp,
}

/// <summary>
/// “音频在走、画面却不再出帧”的判定(2026-11-22, issue #119)。
///
/// ★ 为什么需要它:
///   用户点画面暂停/复播若干次后, 画面永久冻在最后一帧, 音频继续, 拖进度条也救不回来。
///   根因在打包的 libvlc 3.0.24: 显示池需要超过 30 个 slice 时, 它对 NVIDIA 一律退回一个
///   “刚好够解码器用”的内部输出池; 暂停帧 + 显示缓冲帧把池占满后, 恢复时创建不出新的
///   DecoderOutputView, 解码/显示线程就停住了。音频走另一条 aout, 所以听起来一切正常。
///   上游在 VLC 3.0.25 才修(videolan/vlc#30187, MR 10436/10437), 而 NuGet 目前只有 3.0.24。
///
/// ★ 为什么不能靠现有 StallRecoveryPolicy:
///   那套只看 `_mp.Time` 是否冻结。本故障里音频时钟继续推进, Time 一直在走,
///   所以它永远不会触发。这里改用 VLC 的 `MediaStats.DisplayedPictures`(vout 真正
///   出帧的次数)作为“画面是否还活着”的判据。
///
/// ★ 为什么是纯逻辑类:
///   阈值/沉降/预算都是“看着心跳数字做决定”, 与 WPF、LibVLC 无关, 抽出来才能单测。
///
/// 决策时间轴(全部以**合格的播放中采样**计, 暂停/拖动/缓冲的时间不计):
///   0s        观察到 DisplayedPictures 至少增长过一次 ⇒ 武装
///   4s        画面计数一直不涨、且媒体时钟同期推进 ≥ 2.5s ⇒ 第 1 次 Recover
///   之后      4s 沉降期(新 input 起播), 再重复; 同片最多 2 次
///   仍冻结     GiveUp(不再无限重建)
///
/// ★ 真机复现(2026-11-22, .probes/bd-probe-voutfreeze; GTX 1050 Ti + 1080p HEVC):
///   连续第 35 次 pause/resume 后, DisplayedPictures 停在 5330、DecodedVideo 停在 9432,
///   而 Time 继续从 160.5s 走到 165.0s(4.5 秒); 同一位置用 `:avcodec-hw=none` 重建后,
///   DisplayedPictures 立刻恢复增长(9→25→41…)。这与用户报告的“画面冻住、音频继续、
///   拖进度条也救不回来”完全一致, 也证明软解重建确实能恢复。
/// </summary>
public sealed class VoutFreezePolicy
{
    /// <summary>画面计数连续不涨多久才认定“停住”。必须明显长于正常起播/缓冲抖动。</summary>
    public const long FreezeAfterMs = 4_000;

    /// <summary>同一窗口内媒体时钟至少推进多少, 用来排除“播放位置也冻住”(那交给卡顿自愈)。</summary>
    public const long MinClockAdvanceMs = 2_500;

    /// <summary>重建后忽略多久(新 input 在 Opening/Buffering, 画面本来就不会立刻出帧)。</summary>
    public const long SettleAfterRecoverMs = 4_000;

    /// <summary>开始播放后多久还没见到一次画面增长, 就记录“统计不可用”(只记一次, 不做恢复)。</summary>
    public const long StatsGraceMs = 8_000;

    /// <summary>同一片最多自动恢复几次。</summary>
    public const int MaxRecoveries = 2;

    /// <summary>本片已经自动恢复过几次(诊断用)</summary>
    public int Recoveries => _recoveries;

    /// <summary>是否已经放弃自动恢复</summary>
    public bool GaveUp => _gaveUp;

    /// <summary>是否已确认“这份播放拿不到 DisplayedPictures 统计”</summary>
    public bool StatsUnavailable => _statsUnavailable;

    /// <summary>本片是否已经进入过“画面停住”窗口(诊断用)</summary>
    public bool IsFrozenWindowOpen => _frozenSince >= 0;

    private int _lastDisplayed = -1;
    private long _lastMediaMs = -1;
    private long _armedAt = -1;
    private long _frozenSince = -1;
    private long _frozenMediaStartMs = -1;
    private long _settleUntil = -1;
    private bool _seenDisplay;
    private bool _gaveUp;
    private bool _statsUnavailable;
    private int _recoveries;

    /// <summary>彻底归零(换片 / 重新打开视频)。</summary>
    public void Reset()
    {
        _lastDisplayed = -1;
        _lastMediaMs = -1;
        _armedAt = -1;
        _frozenSince = -1;
        _frozenMediaStartMs = -1;
        _settleUntil = -1;
        _seenDisplay = false;
        _gaveUp = false;
        _statsUnavailable = false;
        _recoveries = 0;
    }

    /// <summary>
    /// 这一拍读不到统计(原生对象失效/正在拆解): 只清“停住窗口”的计时,
    /// **不能**动恢复预算 —— 否则重建途中的几拍会把次数还回去, 变成无限重建。
    /// </summary>
    public void OnCannotSample() => ClearFreezeWindow();

    /// <summary>
    /// 心跳调用一次(建议每秒一次)。
    /// </summary>
    /// <param name="nowMs">当前时刻(Environment.TickCount64)</param>
    /// <param name="displayedPictures">VLC 统计里的 DisplayedPictures(vout 已出帧次数)</param>
    /// <param name="mediaMs">当前播放位置(毫秒)</param>
    /// <param name="eligible">
    /// 此刻是否**允许**判定: 正在播放、非直播/聆听、没在拖动或等待 seek、
    /// 没有加载遮罩、窗口可见且处于激活态。false 时此拍不计时。
    /// </param>
    public VoutFreezeAction Tick(long nowMs, int displayedPictures, long mediaMs, bool eligible)
    {
        if (_gaveUp) return VoutFreezeAction.None;

        // 不合格的拍子(暂停/拖动/缓冲/切走): 冻结窗口作废, 但保留已观察到的基线。
        // ★ 暂停时间绝不能算进“画面停了 4 秒” —— 否则用户暂停再恢复会被误判。
        if (!eligible)
        {
            ClearFreezeWindow();
            Remember(displayedPictures, mediaMs);
            return VoutFreezeAction.None;
        }

        // 自动恢复后的沉降期: 新 input 还在起播, 画面本来就不会马上出帧。
        // 这里重新取基线, 等沉降结束再重新武装。
        if (_settleUntil > 0)
        {
            if (nowMs < _settleUntil)
            {
                ClearFreezeWindow();
                Remember(displayedPictures, mediaMs);
                return VoutFreezeAction.None;
            }
            _settleUntil = -1;
            _armedAt = -1;
        }

        if (_armedAt < 0) _armedAt = nowMs;

        // 第一次拿到统计(或刚换 input): 只记基线, 不判定。
        if (_lastDisplayed < 0)
        {
            _seenDisplay = displayedPictures > 0;
            Remember(displayedPictures, mediaMs);
            return VoutFreezeAction.None;
        }

        // 统计或播放位置回退 = 新 input(重建/换片), 重新取基线。
        if (displayedPictures < _lastDisplayed || mediaMs + 1_000 < _lastMediaMs)
        {
            ClearFreezeWindow();
            _seenDisplay = displayedPictures > 0;
            Remember(displayedPictures, mediaMs);
            return VoutFreezeAction.None;
        }

        // 画面在出帧: 一切正常, 冻结窗口作废。
        if (displayedPictures > _lastDisplayed)
        {
            _seenDisplay = true;
            ClearFreezeWindow();
            Remember(displayedPictures, mediaMs);
            return VoutFreezeAction.None;
        }

        // 计数不动。但一次画面都没见到过时不能判定 —— 要么是统计不可用,
        // 要么是起播还没到首帧; 记一条诊断, 继续等。
        if (!_seenDisplay)
        {
            if (!_statsUnavailable && nowMs - _armedAt >= StatsGraceMs)
                _statsUnavailable = true;
            Remember(displayedPictures, mediaMs);
            return VoutFreezeAction.None;
        }

        // 画面计数不动: 从这里开始计时。媒体时钟同期也必须推进,
        // 否则那是“整条 input 卡住”, 由 StallRecoveryPolicy 负责。
        if (_frozenSince < 0)
        {
            _frozenSince = nowMs;
            _frozenMediaStartMs = mediaMs;
            Remember(displayedPictures, mediaMs);
            return VoutFreezeAction.None;
        }

        var wall = nowMs - _frozenSince;
        var clock = mediaMs - _frozenMediaStartMs;
        Remember(displayedPictures, mediaMs);
        if (wall < FreezeAfterMs || clock < MinClockAdvanceMs)
            return VoutFreezeAction.None;

        // 确认停住: 先按预算决定是“再救一次”还是“收手”。
        ClearFreezeWindow();
        if (_recoveries >= MaxRecoveries)
        {
            _gaveUp = true;
            return VoutFreezeAction.GiveUp;
        }

        _recoveries++;
        _settleUntil = nowMs + SettleAfterRecoverMs;
        _lastDisplayed = -1;
        _lastMediaMs = -1;
        _seenDisplay = false;
        _armedAt = -1;
        return VoutFreezeAction.Recover;
    }

    private void Remember(int displayedPictures, long mediaMs)
    {
        _lastDisplayed = displayedPictures;
        _lastMediaMs = mediaMs;
    }

    private void ClearFreezeWindow()
    {
        _frozenSince = -1;
        _frozenMediaStartMs = -1;
    }
}
