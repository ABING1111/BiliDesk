using System;
using System.Globalization;
using System.Text;
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
    private readonly Action<string>? _log;
    private long _frameNumber = -1;
    private long _stallWindowAt = -1;
    private long _stallWindowTimeMs = -1;
    private long _stallWindowFrame = -1;
    private int _stallStreak;
    private long _startMs;
    private int _loadGeneration;
    private PlayerState _state = PlayerState.Idle;
    private bool _disposed;

    public MpvEngine(PlayerEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _log = options.Log;
        _player = new MpvPlayer(
            new MpvGlobalOptions
            {
                WindowId = options.WindowHandle,
                Headless = options.Headless,
                AudioOutput = options.AudioOutput,
                VerboseLog = options.VerboseLog,
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

    /// <summary>时间在走、但视频帧号不再前进(2026-11-22: mpv 下拖动后视频流重连、画面冻结的兜底信号)。</summary>
    public event EventHandler? VideoStalled;

    /// <summary>诊断快照(写日志/用户反馈用): 一次给出引擎侧关键状态。</summary>
    public string GetDiagnostics()
    {
        var sb = new StringBuilder();
        sb.Append("state=").Append(State)
          .Append(" pos=").Append(TimeMs)
          .Append(" len=").Append(LengthMs)
          .Append(" rate=").Append(Rate.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(" volume=").Append(Volume)
          .Append(" mute=").Append(Mute)
          .Append(" frames=").Append(_frameNumber)
          .Append('\n');
        sb.Append("video=").Append(_player.GetString("video-codec"))
          .Append(" audio=").Append(_player.GetString("audio-codec-name"))
          .Append(" tracks=").Append(_player.GetString("track-list/count"))
          .Append(" cache=").Append(_player.GetString("demuxer-cache-duration"))
          .Append(" buffering=").Append(_player.GetString("cache-buffering-state"))
          .Append(" pausedForCache=").Append(_player.GetString("paused-for-cache"))
          .Append('\n');
        sb.Append("drop=").Append(_player.GetString("frame-drop-count"))
          .Append(" decDrop=").Append(_player.GetString("decoder-frame-drop-count"))
          .Append(" mpv=").Append(_player.GetString("mpv-version"))
          .Append(" ffmpeg=").Append(_player.GetString("ffmpeg-version"))
          .Append('\n');
        return sb.ToString();
    }

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
        _startMs = media.StartMs;
        _loadGeneration++;
        _frameNumber = -1;
        _stallWindowAt = -1;
        _stallWindowTimeMs = -1;
        _stallWindowFrame = -1;
        _stallStreak = 0;
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

    /// <summary>
    /// 视频卡死检测: 每 ~2 秒看一次"时间推进了多少 / 帧号动没动"。
    /// ★ 只把"时间在走 + 帧号不动"当卡死 —— 暂停/缓冲时两者都不动, 不能误判。
    /// ★★ 帧号**直接读属性**而不是用观察到的值: 观察事件在部分片源上收不到/恒为 0,
    ///    会把它误判成"画面停住"(实测: 起播 2 秒就触发一次假自愈)。
    /// ★★ 连续两个窗口都不动才判定, 且起播 5 秒内不判 —— 起播缓冲阶段帧号本来就可能滞后。
    /// </summary>
    private void CheckVideoStall(long timeMs)
    {
        if (string.IsNullOrEmpty(_player.GetString("video-codec")))
        {
            _stallWindowAt = -1;
            _stallStreak = 0;
            return;
        }

        var frames = ReadFrameNumber();
        if (frames <= 0 || timeMs < 5000)
        {
            _stallWindowAt = -1;
            _stallStreak = 0;
            return;
        }

        var now = Environment.TickCount64;
        if (_stallWindowAt < 0)
        {
            _stallWindowAt = now;
            _stallWindowTimeMs = timeMs;
            _stallWindowFrame = frames;
            return;
        }

        if (now - _stallWindowAt < 2000) return;

        var advanced = timeMs - _stallWindowTimeMs;
        var framesMoved = frames != _stallWindowFrame;
        if (advanced > 1500 && !framesMoved)
        {
            _stallStreak++;
            if (_stallStreak >= 2) VideoStalled?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _stallStreak = 0;
        }

        _stallWindowAt = now;
        _stallWindowTimeMs = timeMs;
        _stallWindowFrame = frames;
    }

    private long ReadFrameNumber()
    {
        var text = _player.GetString("estimated-frame-number");
        var value = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
        _frameNumber = value;
        return value;
    }

    private void OnFileLoaded(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _state = PlayerState.Playing;
        Playing?.Invoke(this, EventArgs.Empty);

        // ★ 2026-11-22: mpv 的 `start` 选项在个别片源/线路上不生效, 会从 0 起播;
        //   App 侧以为解复用器已定位(不再二次 seek), 表现就是"重建后回到开头"。
        //   这里兜底核对一次, 明显没到目标就补一次精确 seek。
        if (_startMs > 1000) ArmStartFallback(_loadGeneration, _startMs);
    }

    private void ArmStartFallback(int generation, long startMs)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200).ConfigureAwait(false);
                if (_disposed || generation != _loadGeneration) return;
                var now = TimeMs;
                if (now + 2500 >= startMs) return;
                _log?.Invoke($"[mpv] start 未生效(now={now}ms want={startMs}ms) → 补 seek\n");
                _player.SeekAbsolute(startMs / 1000.0);
            }
            catch { }
        });
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
                CheckVideoStall(ms);
                break;
            }
            case "estimated-frame-number":
                _frameNumber = e.Value is long f ? f : _frameNumber;
                break;
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
