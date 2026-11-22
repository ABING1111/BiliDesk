using System;
using BiliDesk.Mpv;

namespace BiliDesk.Playback;

/// <summary>
/// libmpv 实现(2026-11-22, feat/mpv-core)。
///
/// 这一层的唯一职责: 把 <see cref="IPlayerEngine"/> 的语义翻译到 libmpv,
/// 以及把 mpv 的事件/属性翻译回上层熟悉的事件(TimeChanged / LengthChanged / Playing /
/// Paused / EndReached / EncounteredError)。
///
/// 与 VLC 实现的行为差异(有意为之, 也是迁移收益):
///   · 没有 input-slave / start-time / demuxer 选项 —— 双流靠 audio-add, 起播定位靠 start;
///   · 没有 seek 看门狗 / 重建补偿 —— mpv 的 exact seek 直接落在目标上;
///   · 没有音轨自愈 —— mpv 没有 VLC 那个"seek 后音轨掉线"的竞态。
/// </summary>
public sealed class MpvEngine : IPlayerEngine
{
    private readonly MpvPlayer _player;
    private readonly TimeChangeThrottle _timeThrottle = new();
    private PlayerState _state = PlayerState.Idle;
    private bool _disposed;

    public MpvEngine(PlayerEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _player = new MpvPlayer(
            new MpvGlobalOptions
            {
                WindowId = options.WindowHandle,
                Headless = options.Headless,
                AudioOutput = options.AudioOutput,
            },
            options.Log);

        _player.FileLoaded += OnFileLoaded;
        _player.EndFile += OnEndFile;
        _player.PropertyChanged += OnPropertyChanged;
    }

    public static bool IsAvailable => MpvPlayer.IsAvailable;

    public PlayerEngineKind Kind => PlayerEngineKind.Mpv;

    public PlayerState State => _state;

    public bool IsPlaying => _state == PlayerState.Playing;

    public long TimeMs
    {
        get
        {
            var pos = _player.TimePos;
            return double.IsNaN(pos) || pos <= 0 ? 0 : (long)Math.Round(pos * 1000.0);
        }
    }

    public long LengthMs
    {
        get
        {
            var duration = _player.Duration;
            return double.IsNaN(duration) || duration <= 0 ? 0 : (long)Math.Round(duration * 1000.0);
        }
    }

    public float Rate => (float)_player.Speed;

    public int Volume => (int)Math.Round(_player.Volume);

    public bool Mute => _player.Mute;

    public event EventHandler<PlayerTimeChangedEventArgs>? TimeChanged;
    public event EventHandler<PlayerLengthChangedEventArgs>? LengthChanged;
    public event EventHandler? Playing;
    public event EventHandler? Paused;
    public event EventHandler? EndReached;
    public event EventHandler<PlayerErrorEventArgs>? EncounteredError;

    public bool Play(PlayerMedia media)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(media);

        var request = new MpvMediaRequest
        {
            Url = media.Url,
            AudioUrl = media.AudioUrl,
            StartMs = media.StartMs,
            IsLive = media.IsLive,
            Referer = media.Referer,
            UserAgent = media.UserAgent,
            ReadAheadSecs = MapCacheToReadAheadSeconds(media.NetworkCacheMs),
        };

        _state = PlayerState.Opening;
        _timeThrottle.Reset();
        return _player.Load(request) >= 0;
    }

    public void Pause()
    {
        if (_disposed) return;
        _player.SetPause(true);
    }

    public void Resume()
    {
        if (_disposed) return;
        _player.SetPause(false);
    }

    public void Stop()
    {
        if (_disposed) return;
        _player.Command("stop");
        _state = PlayerState.Idle;
    }

    public void SeekTo(long ms)
    {
        if (_disposed) return;
        var length = LengthMs;
        if (length > 0) ms = Math.Clamp(ms, 0, length - 500);
        if (ms < 0) ms = 0;
        _player.SeekAbsolute(ms / 1000.0);
    }

    public void SetRate(float rate)
    {
        if (_disposed || float.IsNaN(rate) || rate <= 0) return;
        _player.Speed = rate;
    }

    public void SetVolume(int volume)
    {
        if (_disposed) return;
        _player.Volume = Math.Clamp(volume, 0, 100);
    }

    public void SetMute(bool mute)
    {
        if (_disposed) return;
        _player.Mute = mute;
    }

    /// <summary>
    /// 把设置页的"缓冲(毫秒)"换算成 mpv 的预读秒数(1~10 秒)。
    /// 两者不是同一个量纲: VLC 是 aout/网络层缓存时长, mpv 是解复用器的 readahead;
    /// 这里只做单调映射, 具体手感在 P2 接入后按实测调。
    /// </summary>
    public static double MapCacheToReadAheadSeconds(int cacheMs) =>
        Math.Clamp(cacheMs / 1000.0, 1.0, 10.0);

    private void OnFileLoaded(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _state = PlayerState.Playing;
        Playing?.Invoke(this, EventArgs.Empty);
    }

    private void OnEndFile(object? sender, MpvEndFileEventArgs e)
    {
        if (_disposed) return;
        if (e.IsEof)
        {
            _state = PlayerState.Ended;
            EndReached?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Reason == MpvNative.EndFileError)
        {
            _state = PlayerState.Error;
            EncounteredError?.Invoke(this, new PlayerErrorEventArgs(MpvPlayer.ErrorText(e.Error)));
        }
        else
        {
            // 用户 stop / 换片: 不触发 EndReached(与 VLC 语义一致)
            _state = PlayerState.Idle;
        }
    }

    private void OnPropertyChanged(object? sender, MpvPropertyEventArgs e)
    {
        if (_disposed) return;
        switch (e.Name)
        {
            case "time-pos":
            {
                var ms = (long)Math.Round(e.AsDouble() * 1000.0);
                if (ms < 0) return;
                if (_timeThrottle.ShouldRaise(Environment.TickCount64))
                    TimeChanged?.Invoke(this, new PlayerTimeChangedEventArgs(ms));
                break;
            }
            case "duration":
            {
                var ms = (long)Math.Round(e.AsDouble() * 1000.0);
                if (ms > 0) LengthChanged?.Invoke(this, new PlayerLengthChangedEventArgs(ms));
                break;
            }
            case "pause":
            {
                var paused = e.Value is true;
                if (paused)
                {
                    _state = PlayerState.Paused;
                    Paused?.Invoke(this, EventArgs.Empty);
                }
                else if (_state is PlayerState.Paused or PlayerState.Opening or PlayerState.Buffering)
                {
                    _state = PlayerState.Playing;
                    Playing?.Invoke(this, EventArgs.Empty);
                }
                break;
            }
            case "eof-reached" when e.Value is true:
                _state = PlayerState.Ended;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.Dispose();
    }
}

/// <summary>
/// 事件节流: mpv 的 time-pos 回调可能比 4Hz 密, 上层(进度条/弹幕时钟)按 VLC 的节奏走即可。
/// 纯逻辑, 单测直接断言。
/// </summary>
public sealed class TimeChangeThrottle
{
    private readonly int _intervalMs;
    private long _lastAt = long.MinValue / 2;

    public TimeChangeThrottle(int intervalMs = 250)
    {
        if (intervalMs < 0) throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _intervalMs = intervalMs;
    }

    public bool ShouldRaise(long nowMs)
    {
        if (nowMs - _lastAt < _intervalMs) return false;
        _lastAt = nowMs;
        return true;
    }

    public void Reset() => _lastAt = long.MinValue / 2;
}
