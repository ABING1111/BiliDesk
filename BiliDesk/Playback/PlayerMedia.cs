using System;

namespace BiliDesk.Playback;

/// <summary>播放内核种类(feat/mpv-core: 最终只剩 Mpv, Vlc 仅用于迁移期对照)。</summary>
public enum PlayerEngineKind
{
    Vlc = 0,
    Mpv = 1,
}

/// <summary>播放器状态(与 LibVLC 的 VLCState 语义对齐, 但不依赖它)。</summary>
public enum PlayerState
{
    Idle = 0,
    Opening = 1,
    Buffering = 2,
    Playing = 3,
    Paused = 4,
    Ended = 5,
    Error = 6,
}

/// <summary>
/// 一次播放请求(引擎无关)。
///
/// 这是上层(PlayerWindow)唯一需要知道的东西: 放什么、从哪开始、带什么头、是不是直播。
/// VLC 的 `input-slave` / `:demux=avformat` / `:start-time` 这类实现细节不允许出现在这里。
/// </summary>
public sealed record PlayerMedia
{
    public required string Url { get; init; }

    /// <summary>DASH 的音轨地址(与 Url 同时起播); 单流/本地文件传 null。</summary>
    public string? AudioUrl { get; init; }

    /// <summary>起播位置(毫秒)。0 = 从头。</summary>
    public long StartMs { get; init; }

    public bool IsLive { get; init; }

    public string Referer { get; init; } = "https://www.bilibili.com/";

    public string UserAgent { get; init; } = "";

    /// <summary>
    /// 期望的网络缓冲(毫秒, 沿用设置页"起播缓冲"的语义)。
    /// 每个引擎自己换算: VLC 是 :network-caching, mpv 是 demuxer-readahead-secs / max-bytes。
    /// </summary>
    public int NetworkCacheMs { get; init; } = 1000;
}

/// <summary>引擎构造参数(渲染宿主、无头模式、诊断日志)。</summary>
public sealed record PlayerEngineOptions
{
    /// <summary>视频要嵌进去的宿主窗口句柄(0 = 引擎自己开窗/无头)。</summary>
    public long WindowHandle { get; init; }

    /// <summary>无视频/无音频输出(离线压测、探针)。</summary>
    public bool Headless { get; init; }

    /// <summary>引擎自己的详细诊断日志(对应 mpv 的 msg-level=all=v)。</summary>
    public bool VerboseLog { get; init; }

    /// <summary>音频输出模块; 空 = 引擎自选。</summary>
    public string AudioOutput { get; init; } = "";

    /// <summary>引擎自己的诊断日志(转给 PlayerLog.Write 落盘)。</summary>
    public Action<string>? Log { get; init; }
}

public sealed class PlayerTimeChangedEventArgs : EventArgs
{
    public PlayerTimeChangedEventArgs(long timeMs) => TimeMs = timeMs;
    public long TimeMs { get; }
}

public sealed class PlayerLengthChangedEventArgs : EventArgs
{
    public PlayerLengthChangedEventArgs(long lengthMs) => LengthMs = lengthMs;
    public long LengthMs { get; }
}

public sealed class PlayerErrorEventArgs : EventArgs
{
    public PlayerErrorEventArgs(string message) => Message = message;
    public string Message { get; }
}
