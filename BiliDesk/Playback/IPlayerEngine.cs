using System;

namespace BiliDesk.Playback;

/// <summary>
/// 播放内核契约(2026-11-22, feat/mpv-core)。
///
/// ★ 目标: PlayerWindow 只依赖这个接口, 不出现 `_mp` / `MediaPlayer` / `VideoView`。
///   迁移完成后 Vlc 实现整个删掉, 仓库里只剩 <see cref="MpvEngine"/>。
///
/// ★ 事件线程: 与 LibVLCSharp 一致 —— 事件在**引擎自己的线程**上触发, 订阅方负责
///   Dispatcher.BeginInvoke 切回 UI 线程(现有 PlayerWindow 已经是这么写的, 不需要改)。
///
/// ★ 刻意**不**进接口的东西(都是 VLC 实现细节):
///   · AudioTrack / AudioTrackCount / SetAudioTrack —— VLC 的"音轨掉线自愈"专用;
///   · Vout 事件 / 视频输出模块选择 —— VLC 的白闪守护专用;
///   · Position(0~1) —— 只保留毫秒时间轴, 避免两套单位。
/// </summary>
public interface IPlayerEngine : IDisposable
{
    PlayerEngineKind Kind { get; }

    PlayerState State { get; }

    bool IsPlaying { get; }

    /// <summary>当前播放位置(毫秒)。</summary>
    long TimeMs { get; }

    /// <summary>媒体总时长(毫秒)。未知/直播为 0。</summary>
    long LengthMs { get; }

    /// <summary>播放速率(1.0 = 原速)。</summary>
    float Rate { get; }

    /// <summary>音量(0~100, 应用层权威值, 不直接反映设备就绪状态)。</summary>
    int Volume { get; }

    bool Mute { get; }

    /// <summary>位置变化(引擎按自身频率触发, 现有实现约 4Hz)。</summary>
    event EventHandler<PlayerTimeChangedEventArgs>? TimeChanged;

    /// <summary>时长确定/变化。</summary>
    event EventHandler<PlayerLengthChangedEventArgs>? LengthChanged;

    /// <summary>开始播放(起播/恢复)。</summary>
    event EventHandler? Playing;

    event EventHandler? Paused;

    /// <summary>正常播完(不含 Stop / 换片)。</summary>
    event EventHandler? EndReached;

    event EventHandler<PlayerErrorEventArgs>? EncounteredError;

    /// <summary>
    /// 时间在走、但画面不再前进(视频流卡死)。
    /// ★ 与 TimeChanged 停住的"普通卡顿"区分: 这里音频时钟还在推进, 只暂停/重连救不回来。
    /// </summary>
    event EventHandler? VideoStalled;

    /// <summary>诊断快照(引擎侧关键状态), 写日志/排障用。</summary>
    string GetDiagnostics();

    /// <summary>加载并开始播放。返回 false 表示引擎拒绝(未初始化/无可播地址)。</summary>
    bool Play(PlayerMedia media);

    /// <summary>暂停(幂等)。</summary>
    void Pause();

    /// <summary>从暂停恢复(幂等; 没有已加载媒体时忽略)。</summary>
    void Resume();

    /// <summary>停止当前播放(不触发 EndReached)。</summary>
    void Stop();

    /// <summary>绝对跳转(毫秒)。</summary>
    void SeekTo(long ms);

    void SetRate(float rate);

    void SetVolume(int volume);

    void SetMute(bool mute);
}
