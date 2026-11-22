using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace BiliDesk.Mpv;

/// <summary>播放器事件(在 mpv 的事件线程上触发, 调用方需要自己切回 UI 线程)。</summary>
public sealed class MpvPropertyEventArgs : EventArgs
{
    public MpvPropertyEventArgs(string name, object? value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    /// <summary>double / long / bool / string / null(属性当前无值)。</summary>
    public object? Value { get; }

    public double AsDouble() => Value switch
    {
        double d => d,
        long l => l,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) => r,
        _ => 0,
    };
}

public sealed class MpvEndFileEventArgs : EventArgs
{
    public MpvEndFileEventArgs(int reason, int error)
    {
        Reason = reason;
        Error = error;
    }

    public int Reason { get; }
    public int Error { get; }

    /// <summary>正常播完(区别于用户 stop / 切片 / 出错)。</summary>
    public bool IsEof => Reason == MpvNative.EndFileEof;
}

/// <summary>
/// libmpv 播放器封装(2026-11-22, feat/mpv-core)。
///
/// 设计原则:
///   · 只做"播放内核"该做的事 —— 加载、播放控制、时间/时长/速率/音量、事件;
///   · 不碰 UI、不碰 B 站接口、不做 seek 兜底(这些留给调用方, 与现有 VLC 内核同构);
///   · 事件在后台线程触发, 与 LibVLCSharp 的语义一致, 调用方自己 BeginInvoke。
///
/// ★ 生命周期: mpv_create → set options → mpv_initialize → 事件线程 → loadfile…
///   Dispose 时先 quit 让事件线程退出, 再 mpv_terminate_destroy(顺序反了会崩)。
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private readonly IntPtr _handle;
    private readonly Thread? _eventThread;
    private readonly Action<string>? _log;
    private volatile bool _disposed;

    /// <summary>下一次 FILE_LOADED 时要挂上去的外部音轨(DASH 双流用 audio-add, 见 AttachExternalAudio)。</summary>
    private string? _pendingAudioUrl;

    public MpvPlayer(MpvGlobalOptions options, Action<string>? log = null)
    {
        _log = log;
        if (!MpvNative.IsLibraryPresent)
            throw new DllNotFoundException("libmpv-2.dll 不在 " + MpvNative.LibraryPath);

        _handle = MpvNative.mpv_create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create 失败");

        foreach (var kv in MpvOptions.BuildGlobal(options))
        {
            var rc = MpvNative.mpv_set_option_string(_handle, kv.Key, kv.Value);
            if (rc < 0) _log?.Invoke($"[mpv] option {kv.Key}={kv.Value} rejected: {ErrorText(rc)}");
        }

        var init = MpvNative.mpv_initialize(_handle);
        if (init < 0) throw new InvalidOperationException("mpv_initialize 失败: " + ErrorText(init));

        if (options.VerboseLog) MpvNative.mpv_request_log_messages(_handle, "v");

        // 观察对上层有用的属性; 其余按需用 GetDouble/GetString 现读
        foreach (var (name, format) in new[]
                 {
                     ("time-pos", MpvNative.FormatDouble),
                     ("duration", MpvNative.FormatDouble),
                     ("pause", MpvNative.FormatFlag),
                     ("speed", MpvNative.FormatDouble),
                     ("volume", MpvNative.FormatDouble),
                     ("mute", MpvNative.FormatFlag),
                     ("eof-reached", MpvNative.FormatFlag),
                     ("idle-active", MpvNative.FormatFlag),
                     ("estimated-frame-number", MpvNative.FormatInt64),
                 })
        {
            MpvNative.mpv_observe_property(_handle, 0, name, format);
        }

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "mpv-event-loop",
        };
        _eventThread.Start();
    }

    public static bool IsAvailable => MpvNative.IsLibraryPresent;

    /// <summary>原生句柄(嵌入/高级调用用, 平时不需要)。</summary>
    public IntPtr Handle => _handle;

    public event EventHandler? FileLoaded;
    public event EventHandler<MpvEndFileEventArgs>? EndFile;
    public event EventHandler<MpvPropertyEventArgs>? PropertyChanged;

    /// <summary>加载一个媒体。DASH 音轨走 file-local 的 audio-file, 与视频同一时刻起播。</summary>
    public int Load(MpvMediaRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var kv in MpvOptions.BuildFileLocal(request))
        {
            // 先按 file-local 设(只对 start / audio-file 这类"每文件"选项有效);
            // 其余选项(http-header-fields / hr-seek / 缓存…)会被拒, 退回运行期属性。
            var rc = MpvNative.mpv_set_property_string(_handle, "file-local-options/" + kv.Key, kv.Value);
            if (rc < 0) rc = MpvNative.mpv_set_property_string(_handle, kv.Key, kv.Value);
            if (rc < 0) _log?.Invoke($"[mpv] option {kv.Key}={kv.Value} rejected: {ErrorText(rc)}");
        }

        _pendingAudioUrl = request.AudioUrl;
        return Command("loadfile", request.Url, "replace");
    }

    public int Command(params string[] args)
    {
        if (_disposed) return -1;
        var pointers = new IntPtr[args.Length + 1];
        IntPtr array = IntPtr.Zero;
        try
        {
            for (var i = 0; i < args.Length; i++) pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            pointers[args.Length] = IntPtr.Zero;
            array = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
            Marshal.Copy(pointers, 0, array, pointers.Length);
            return MpvNative.mpv_command(_handle, array);
        }
        catch
        {
            return -1;
        }
        finally
        {
            foreach (var p in pointers)
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            if (array != IntPtr.Zero) Marshal.FreeHGlobal(array);
        }
    }

    /// <summary>绝对跳转(秒), 精确模式 —— 对应 VLC 的 `Time = x`, 但不需要 start-time 兜底。</summary>
    public int SeekAbsolute(double seconds) =>
        Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute", "exact");

    public double TimePos => GetDouble("time-pos");
    public double Duration => GetDouble("duration");
    public bool IsPaused => GetFlag("pause");
    public bool EofReached => GetFlag("eof-reached");

    public double Speed
    {
        get => GetDouble("speed");
        set => SetDouble("speed", value);
    }

    public double Volume
    {
        get => GetDouble("volume");
        set => SetDouble("volume", value);
    }

    public bool Mute
    {
        get => GetFlag("mute");
        set => SetFlag("mute", value);
    }

    public void SetPause(bool pause) => SetFlag("pause", pause);

    public double GetDouble(string name) =>
        _disposed ? 0 : (MpvNative.mpv_get_property_double(_handle, name, MpvNative.FormatDouble, out var v) >= 0 ? v : 0);

    public bool GetFlag(string name) =>
        !_disposed && MpvNative.mpv_get_property_int(_handle, name, MpvNative.FormatFlag, out var v) >= 0 && v != 0;

    public string? GetString(string name)
    {
        if (_disposed) return null;
        var ptr = MpvNative.mpv_get_property_string(_handle, name);
        if (ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(ptr); }
        finally { MpvNative.mpv_free(ptr); }
    }

    public void SetDouble(string name, double value)
    {
        if (_disposed) return;
        MpvNative.mpv_set_property_double(_handle, name, MpvNative.FormatDouble, ref value);
    }

    public void SetFlag(string name, bool value)
    {
        if (_disposed) return;
        var v = value ? 1 : 0;
        MpvNative.mpv_set_property_int(_handle, name, MpvNative.FormatFlag, ref v);
    }

    public static string ErrorText(int code)
    {
        try
        {
            var ptr = MpvNative.mpv_error_string(code);
            return ptr == IntPtr.Zero ? code.ToString(CultureInfo.InvariantCulture) : Marshal.PtrToStringUTF8(ptr) ?? "";
        }
        catch
        {
            return code.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            IntPtr ptr;
            try { ptr = MpvNative.mpv_wait_event(_handle, 0.2); }
            catch { return; }
            if (_disposed) return;
            // ★ mpv_wait_event 返回的是指向**内部事件结构**的指针(不是结构体本身),
            //   生命周期只到下一次 wait_event —— 必须立刻拷出来, 不能存引用。
            if (ptr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvNative.MpvEvent>(ptr);

            switch (ev.EventId)
            {
                case MpvNative.EventNone:
                    continue;
                case MpvNative.EventShutdown:
                    return;
                case MpvNative.EventFileLoaded:
                    AttachExternalAudio();
                    Raise(FileLoaded);
                    break;
                case MpvNative.EventPropertyChange when ev.Data != IntPtr.Zero:
                    RaiseProperty(ev);
                    break;
                case MpvNative.EventEndFile when ev.Data != IntPtr.Zero:
                {
                    var end = Marshal.PtrToStructure<MpvNative.MpvEventEndFile>(ev.Data);
                    EndFile?.Invoke(this, new MpvEndFileEventArgs(end.Reason, end.Error));
                    break;
                }
                case MpvNative.EventLogMessage when ev.Data != IntPtr.Zero:
                {
                    var msg = Marshal.PtrToStructure<MpvNative.MpvEventLogMessage>(ev.Data);
                    var text = msg.Text == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(msg.Text) ?? "";
                    var prefix = msg.Prefix == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(msg.Prefix) ?? "";
                    _log?.Invoke("[mpv] " + prefix + text);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 文件加载完成后挂外部音轨(DASH 视频/音频分离)。
    ///
    /// ★ 为什么不用 file-local 的 `audio-file`: 实测那个选项在当前 libmpv 上不会生效
    ///   (track-list 里根本没有音轨, audio-codec-name 为空), 必须走 `audio-add` 命令。
    /// ★ 必须等 FILE_LOADED 之后: 提前发 audio-add 会因为"还没有当前文件"被拒。
    /// </summary>
    private void AttachExternalAudio()
    {
        var audio = _pendingAudioUrl;
        _pendingAudioUrl = null;
        if (string.IsNullOrEmpty(audio)) return;
        var rc = Command(MpvOptions.BuildAudioAddCommand(audio));
        _log?.Invoke(rc < 0
            ? $"[mpv] audio-add failed: {ErrorText(rc)}"
            : "[mpv] audio-add ok");
    }

    private void RaiseProperty(MpvNative.MpvEvent ev)
    {
        var prop = Marshal.PtrToStructure<MpvNative.MpvEventProperty>(ev.Data);
        var name = prop.Name == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(prop.Name) ?? "";
        object? value = null;
        if (prop.Data != IntPtr.Zero)
        {
            value = prop.Format switch
            {
                MpvNative.FormatDouble => Marshal.PtrToStructure<double>(prop.Data),
                MpvNative.FormatInt64 => Marshal.PtrToStructure<long>(prop.Data),
                MpvNative.FormatFlag => Marshal.ReadInt32(prop.Data) != 0,
                MpvNative.FormatString => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(prop.Data)),
                _ => null,
            };
        }
        PropertyChanged?.Invoke(this, new MpvPropertyEventArgs(name, value));
    }

    private static void Raise(EventHandler? handler)
    {
        try { handler?.Invoke(null, EventArgs.Empty); } catch { /* 订阅方自己负责 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { MpvNative.mpv_command_string(_handle, "quit"); } catch { }
        try { _eventThread?.Join(1500); } catch { }
        try { MpvNative.mpv_terminate_destroy(_handle); } catch { }
    }
}
