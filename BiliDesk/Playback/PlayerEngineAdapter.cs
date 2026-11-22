using System;
using BiliDesk.Views.Controls;

namespace BiliDesk.Playback;

/// <summary>
/// 迁移期适配器(2026-11-22, feat/mpv-core): 给 PlayerWindow 提供它现在用的那套成员名,
/// 内部全部转发到 <see cref="IPlayerEngine"/>(目前只有 mpv)。
///
/// 为什么要这层:
///   PlayerWindow 有 171 处 `_mp.*` 调用, 直接改成新接口等于把"交互逻辑"整体重写一遍。
///   这一层的存在**只为了不改交互**: 所有调用点的语义保持不变, 底下换成 mpv。
///   P4 删 VLC 的时候, 这层可以逐步改名成 `_engine` 并最终删掉。
///
/// 生命周期:
///   · 构造时**不创建引擎**(那时 VideoView 的 HWND 还没建好);
///   · 第一次 Play 时才创建 mpv, 并绑定 <see cref="PlayerVideoView.SurfaceHandle"/>;
///   · 构造到首次 Play 之间设置的音量/速率会被记住并补写。
/// </summary>
public sealed class PlayerEngineAdapter : IDisposable
{
    private readonly PlayerVideoView _view;
    private readonly Action<string>? _log;
    private IPlayerEngine? _engine;
    private int _volume = 100;
    private float _rate = 1.0f;
    private bool _mute;
    private bool _disposed;

    public PlayerEngineAdapter(PlayerVideoView view, Action<string>? log = null)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _log = log;
    }

    public event EventHandler<PlayerTimeChangedEventArgs>? TimeChanged;
    public event EventHandler<PlayerLengthChangedEventArgs>? LengthChanged;
    public event EventHandler? Playing;
    public event EventHandler? Paused;
    public event EventHandler? EndReached;
    public event EventHandler<PlayerErrorEventArgs>? EncounteredError;

    /// <summary>mpv 侧检测到"时间在走、画面停住"时触发(见 MpvEngine.CheckVideoStall)。</summary>
    public event EventHandler? VideoStalled;

    public PlayerState State => _engine?.State ?? PlayerState.Idle;

    public bool IsPlaying => _engine?.IsPlaying ?? false;

    /// <summary>当前播放位置(毫秒)。setter 等价于 SeekTo(VLC 的 `Time = x` 语义)。</summary>
    public long Time
    {
        get => _engine?.TimeMs ?? 0;
        set
        {
            if (_engine == null) return;
            _engine.SeekTo(value);
        }
    }

    public long Length => _engine?.LengthMs ?? 0;

    public float Rate
    {
        get => _engine?.Rate ?? _rate;
        set
        {
            _rate = value;
            _engine?.SetRate(value);
        }
    }

    public int Volume
    {
        get => _engine?.Volume ?? _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            _engine?.SetVolume(_volume);
        }
    }

    public bool Mute
    {
        get => _engine?.Mute ?? _mute;
        set
        {
            _mute = value;
            _engine?.SetMute(value);
        }
    }


    public bool Play(PlayerMedia media)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEngine();
        return _engine?.Play(media) ?? false;
    }

    /// <summary>继续播放(VLC 的 `_mp.Play()` 无参版本)。</summary>
    public void Play() => _engine?.Resume();

    public void Pause() => _engine?.Pause();

    /// <summary>暂停/恢复二合一(VLC 的 SetPause 同名调用点)。</summary>
    public void SetPause(bool pause)
    {
        if (pause) _engine?.Pause();
        else _engine?.Resume();
    }

    public void Stop() => _engine?.Stop();

    public void SetRate(float rate) => Rate = rate;

    /// <summary>引擎诊断快照(PlayerWindow 的调试快捷键用)。</summary>
    public string Diagnostics => _engine?.GetDiagnostics() ?? "engine=not-created";

    private void EnsureEngine()
    {
        if (_engine != null || _disposed) return;

        var hwnd = _view.SurfaceHandle;
        if (hwnd == IntPtr.Zero)
        {
            _log?.Invoke("[mpv] 视频宿主句柄还没建好, 视频可能不会嵌入窗口\n");
        }

        var verbose = Environment.GetEnvironmentVariable("BILIDESK_MPV_DEBUG") == "1";
        _engine = PlayerEngineFactory.Create(PlayerEngineKind.Mpv, new PlayerEngineOptions
        {
            WindowHandle = hwnd.ToInt64(),
            VerboseLog = verbose,
            Log = _log,
        });

        _engine.TimeChanged += (_, e) => TimeChanged?.Invoke(this, e);
        _engine.LengthChanged += (_, e) => LengthChanged?.Invoke(this, e);
        _engine.Playing += (_, _) => Playing?.Invoke(this, EventArgs.Empty);
        _engine.Paused += (_, _) => Paused?.Invoke(this, EventArgs.Empty);
        _engine.EndReached += (_, _) => EndReached?.Invoke(this, EventArgs.Empty);
        _engine.EncounteredError += (_, e) => EncounteredError?.Invoke(this, e);
        _engine.VideoStalled += (_, _) => VideoStalled?.Invoke(this, EventArgs.Empty);

        _log?.Invoke("[Engine] " + _engine.GetDiagnostics() + "\n");
        _engine.SetVolume(_volume);
        _engine.SetRate(_rate);
        _engine.SetMute(_mute);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine?.Dispose();
        _engine = null;
    }
}
