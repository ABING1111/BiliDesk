using System;
using System.Collections.Generic;
using BiliDesk.Helpers;

namespace BiliDesk.Models;

/// <summary>
/// 一个可下载的清晰度档位(下载选择窗里的一行)。
///
/// ★ 与播放用的 <see cref="QualityPreference"/> 分开:
///   那边是"开始播放前可预选的档位表"(固定几档, 不含 8K/杜比视界/HDR);
///   这里是**接口这次真实返回了什么**, 所以可能包含 127(8K)/126(杜比视界)/125(HDR),
///   也可能一个都没有(只能回退 durl 单流)。菜单只列真实存在的档, 不列"点了必然降级"的假项。
/// </summary>
public sealed class DownloadQualityOption
{
    /// <summary>B 站清晰度 id(127/126/125/120/116/112/80/74/64/32/16…)</summary>
    public int Qn { get; init; }

    /// <summary>给用户看的档位名(见 <see cref="Services.ApiClient.QnToLabel"/>)</summary>
    public string Label { get; init; } = "";

    /// <summary>这一档实际选中的编码(HEVC / AVC / AV1 / 单流)。同档优先 HEVC: 体积约为 AVC 的一半。</summary>
    public string CodecLabel { get; init; } = "";

    /// <summary>视频流码率(bps), 用于估算体积与展示</summary>
    public long Bandwidth { get; init; }

    /// <summary>按码率估算的下载体积(字节)。0 = 无法估算(durl 单流没有码率字段)</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>估算的视频流字节数(用于下载进度折算)</summary>
    public long EstimatedVideoBytes { get; init; }

    /// <summary>估算的音频流字节数(用于下载进度折算)</summary>
    public long EstimatedAudioBytes { get; init; }

    /// <summary>预计大小的可读文本; 估算不出来时显示"大小未知"</summary>
    public string SizeText => EstimatedBytes > 0 ? "约 " + ByteSize.Format(EstimatedBytes) : "大小未知";

    /// <summary>
    /// 这一档的候选下载地址(已选中的线路排第一, 其余是备用线路)。
    /// 下载时按顺序重试 —— 单条 CDN 偶发 403/超时时不至于整个任务失败。
    /// </summary>
    public List<string> VideoUrls { get; init; } = new();
}

/// <summary>
/// 一次"读取下载选项"的结果: 可选的清晰度 + 音轨 + 时长。
///
/// ★ 音轨只有一份(不随清晰度变化): 下载走 DASH 双流合流, 音轨取当前账号能拿到的最高档,
///   与播放器里"音质"按钮的选择无关 —— 下载是"拿走一份成品", 不该因为当时播放用 64K
///   就只下 64K 的声音。
/// </summary>
public sealed class DownloadOptions
{
    public string Bvid { get; init; } = "";
    public long Cid { get; init; }
    public string Title { get; init; } = "";

    /// <summary>接口给的时长(毫秒), 用于体积估算与展示</summary>
    public long DurationMs { get; init; }

    /// <summary>可选清晰度, 从高到低</summary>
    public List<DownloadQualityOption> Qualities { get; init; } = new();

    /// <summary>音轨候选地址(已选线路排第一)</summary>
    public List<string> AudioUrls { get; init; } = new();

    /// <summary>音轨档位的短文案(如 "192K" / "无损")</summary>
    public string AudioLabel { get; init; } = "";

    /// <summary>
    /// true = 没有可用的 DASH 双流, 只能下载 durl 单流(音视频已合并的 MP4, 上限 720P)。
    /// 这时 <see cref="MuxedUrls"/> 有值, 且 <see cref="Qualities"/> 只有一项。
    /// </summary>
    public bool UseMuxed { get; init; }

    /// <summary>durl 单流的候选地址(已选线路排第一)</summary>
    public List<string> MuxedUrls { get; init; } = new();
}

/// <summary>
/// 交给 <see cref="Helpers.VideoDownloader"/> 的一次下载任务。
/// 由下载选择窗在用户点「开始下载」时组装, 调用方不需要再碰接口。
/// </summary>
public sealed class VideoDownloadRequest
{
    public string Bvid { get; init; } = "";
    public string Title { get; init; } = "";
    public long Cid { get; init; }
    public int Qn { get; init; }

    /// <summary>视频流候选地址(第一项优先)</summary>
    public List<string> VideoUrls { get; init; } = new();

    /// <summary>音轨候选地址(第一项优先)</summary>
    public List<string> AudioUrls { get; init; } = new();

    public long DurationMs { get; init; }

    /// <summary>true = 走 durl 单流(不下载音轨、不合流)</summary>
    public bool UseMuxed { get; init; }

    public List<string> MuxedUrls { get; init; } = new();

    /// <summary>按码率估算的总字节数(下载进度条的分母; 0 = 未知)</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>估算的视频流字节数(0 = 未知)。用于把两路下载折算成一条总进度。</summary>
    public long EstimatedVideoBytes { get; init; }

    /// <summary>估算的音频流字节数(0 = 未知)</summary>
    public long EstimatedAudioBytes { get; init; }

    /// <summary>完成后在资源管理器里定位到文件</summary>
    public bool OpenFolderWhenDone { get; init; }
}

/// <summary>
/// 下载选择窗的**纯逻辑**部分(不碰 UI, 便于单测)。
/// </summary>
public static class DownloadSelection
{
    /// <summary>
    /// 选默认档: 优先"与当前播放清晰度完全一致"的那一档;
    /// 没有就取**不高于**它的最高档(降级比升级更符合预期);
    /// 连更低的都没有(例如当前是 360P 而接口只给了 1080P)才取最高档。
    /// 列表为空返回 null。
    /// </summary>
    public static DownloadQualityOption? PickDefault(
        IReadOnlyList<DownloadQualityOption>? options, int preferredQn)
    {
        if (options == null || options.Count == 0) return null;

        DownloadQualityOption? exact = null;
        DownloadQualityOption? below = null;
        DownloadQualityOption? highest = null;

        foreach (var o in options)
        {
            if (highest == null || o.Qn > highest.Qn) highest = o;
            if (o.Qn == preferredQn) exact = o;
            if (preferredQn > 0 && o.Qn <= preferredQn &&
                (below == null || o.Qn > below.Qn)) below = o;
        }

        return exact ?? below ?? highest;
    }
}
