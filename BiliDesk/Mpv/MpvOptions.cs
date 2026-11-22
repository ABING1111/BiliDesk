using System;
using System.Collections.Generic;
using System.Globalization;

namespace BiliDesk.Mpv;

/// <summary>一次点播/直播请求(纯数据, 便于单测)。</summary>
public sealed record MpvMediaRequest
{
    public required string Url { get; init; }

    /// <summary>DASH 的音轨地址(可选)。mpv 用 `audio-file` 作为第二个输入一起加载。</summary>
    public string? AudioUrl { get; init; }

    /// <summary>起播位置(毫秒)。mpv 用 file-local 的 `start` 在 open 阶段定位。</summary>
    public long StartMs { get; init; }

    public bool IsLive { get; init; }

    /// <summary>B 站 CDN 校验的 Referer; 直播用直播间地址。</summary>
    public string Referer { get; init; } = "https://www.bilibili.com/";

    public string UserAgent { get; init; } = "";

    /// <summary>解复用器最多缓存多少 MB(对应 VLC 的 network-caching 语义, 但单位是字节)。</summary>
    public int DemuxerMaxBytesMb { get; init; } = 32;

    /// <summary>预读秒数。</summary>
    public double ReadAheadSecs { get; init; } = 5;
}

/// <summary>播放器(进程级)选项。</summary>
public sealed record MpvGlobalOptions
{
    /// <summary>无窗口/无音频输出(探针、离线压测用)。</summary>
    public bool Headless { get; init; }

    /// <summary>嵌入的宿主窗口句柄(0 = 不嵌入, mpv 自己开窗)。</summary>
    public long WindowId { get; init; }

    /// <summary>硬解: auto-safe / no / d3d11va ...</summary>
    public string Hwdec { get; init; } = "auto-safe";

    /// <summary>音频输出模块; 空 = mpv 自选(Windows 上为 wasapi)。</summary>
    public string AudioOutput { get; init; } = "";

    public bool VerboseLog { get; init; }
}

/// <summary>
/// 把 <see cref="MpvMediaRequest"/> 翻译成 libmpv 的选项键值对(纯函数, 单测直接断言)。
///
/// ★ 为什么单独抽出来: 选项名/取值格式是"错了就静默不生效"的东西 —— 拼错一个字母,
///   mpv 只会回一个 error, 播放照常, 直到某天发现"缓冲设置怎么没用"。锁在单测里。
/// </summary>
public static class MpvOptions
{
    /// <summary>进程级选项(mpv_set_option_string, 必须在 mpv_initialize 之前设)。</summary>
    public static List<KeyValuePair<string, string>> BuildGlobal(MpvGlobalOptions o)
    {
        var list = new List<KeyValuePair<string, string>>
        {
            // 不加载用户配置、不开终端/OSC、不抢键盘 —— 这是一个嵌入式的播放内核
            Pair("config", "no"),
            Pair("terminal", "no"),
            Pair("input-default-bindings", "no"),
            Pair("input-vo-keyboard", "no"),
            Pair("osc", "no"),
            Pair("osd-level", "0"),
            Pair("idle", "yes"),
            Pair("keep-open", "no"),
            Pair("audio-client-name", "BiliDesk"),
            Pair("hwdec", o.Hwdec),
            Pair("vo", o.Headless ? "null" : "gpu"),
            Pair("ao", o.Headless ? "null" : (string.IsNullOrEmpty(o.AudioOutput) ? "wasapi" : o.AudioOutput)),
            // 网络流: 开缓存, 但不要因为缓存停顿而永远停住
            Pair("cache", "yes"),
            Pair("cache-pause", "yes"),
            Pair("network-timeout", "15"),
        };

        if (o.WindowId != 0)
            list.Add(Pair("wid", o.WindowId.ToString(CultureInfo.InvariantCulture)));

        if (o.VerboseLog)
            list.Add(Pair("msg-level", "all=v"));

        return list;
    }

    /// <summary>
    /// 单个媒体的选项(mpv_set_property_string "file-local-options/&lt;name&gt;", 在 loadfile 之前设)。
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildFileLocal(MpvMediaRequest r)
    {
        var list = new List<KeyValuePair<string, string>>();

        if (r.StartMs > 0)
        {
            // start 是秒(浮点), 用 InvariantCulture —— 中文区域会写出 "1,5", mpv 直接报错
            list.Add(Pair("start", (r.StartMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)));
        }

        // 网络头: mpv 的格式是 "Name: value" 逗号分隔, 用 file-local 保证按请求走
        var headers = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Referer)) headers.Add("Referer: " + r.Referer);
        if (!string.IsNullOrWhiteSpace(r.UserAgent)) headers.Add("User-Agent: " + r.UserAgent);
        if (headers.Count > 0)
            list.Add(Pair("http-header-fields", string.Join(",", headers)));

        // 精确 seek + 变速不变调(与 VLC 的 scaletempo 对应)
        list.Add(Pair("hr-seek", "yes"));
        list.Add(Pair("audio-pitch-correction", "yes"));

        // 缓存用字节/秒表达, 与 VLC 的 network-caching 不是同一个量纲(要按实测调)
        list.Add(Pair("demuxer-max-bytes", r.DemuxerMaxBytesMb.ToString(CultureInfo.InvariantCulture) + "MiB"));
        list.Add(Pair("demuxer-readahead-secs",
            r.ReadAheadSecs.ToString("0.###", CultureInfo.InvariantCulture)));

        return list;
    }

    private static KeyValuePair<string, string> Pair(string name, string value) => new(name, value);

    /// <summary>
    /// DASH 外部音轨的加载命令。
    /// ★ 实测: file-local 的 `audio-file` 在当前 libmpv 上不生效(轨道表里没有音轨),
    ///   必须等 FILE_LOADED 之后发 `audio-add` —— 见 MpvPlayer.AttachExternalAudio。
    /// </summary>
    public static string[] BuildAudioAddCommand(string audioUrl) => new[] { "audio-add", audioUrl };
}
