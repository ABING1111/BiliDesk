using System.Linq;
using BiliDesk.Mpv;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// mpv 选项拼装(feat/mpv-core)。选项名/格式错了 mpv 只会静默不生效, 所以逐条锁住。
/// </summary>
public class MpvOptionsTests
{
    [Fact]
    public void DASH音轨用audio_add命令()
    {
        var args = MpvOptions.BuildAudioAddCommand("https://example.com/audio.m4s");
        Assert.Equal(new[] { "audio-add", "https://example.com/audio.m4s" }, args);
    }

    [Fact]
    public void 头部里的逗号要转义_否则CDN返回400()
    {
        // 浏览器 UA 天生带 "(KHTML, like Gecko)"; 不转义会被 mpv 的逗号列表切成两条畸形头,
        // estgcos 等 CDN 直接回 HTTP 400(2026-11-22 实测)。
        var opts = MpvOptions.BuildFileLocal(new MpvMediaRequest
        {
            Url = "https://example.com/video.m4s",
            Referer = "https://www.bilibili.com/",
            UserAgent = "Mozilla/5.0 (KHTML, like Gecko) Chrome/131.0.0.0",
        });
        var header = opts.Single(kv => kv.Key == "http-header-fields").Value;
        Assert.Contains("KHTML\\, like Gecko", header);
        Assert.DoesNotContain("KHTML, like Gecko", header);
    }

    [Fact]
    public void 转义同时处理反斜杠()
    {
        Assert.Equal("a\\,b\\\\c", MpvOptions.EscapeHeaderValue("a,b\\c"));
    }

    [Fact]
    public void 文件本地选项里不再写audio_file()
    {
        // 实测 file-local 的 audio-file 不生效, 统一走 audio-add; 这条锁住"别再写回去"
        var opts = MpvOptions.BuildFileLocal(new MpvMediaRequest
        {
            Url = "https://example.com/video.m4s",
            AudioUrl = "https://example.com/audio.m4s",
        });
        Assert.DoesNotContain(opts, kv => kv.Key == "audio-file");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(12345, true)]
    public void 起播位置只在大于零时写且用不变文化(long ms, bool expect)
    {
        var opts = MpvOptions.BuildFileLocal(new MpvMediaRequest { Url = "u", StartMs = ms });
        var start = opts.SingleOrDefault(kv => kv.Key == "start");
        if (expect)
        {
            Assert.False(string.IsNullOrEmpty(start.Value));
            Assert.Equal("12.345", start.Value);
        }
        else
        {
            Assert.Equal(default, start);
        }
    }

    [Fact]
    public void 请求头按mpv格式拼成逗号分隔()
    {
        var opts = MpvOptions.BuildFileLocal(new MpvMediaRequest
        {
            Url = "u",
            Referer = "https://www.bilibili.com/",
            UserAgent = "UA/1.0",
        });
        var headers = opts.Single(kv => kv.Key == "http-header-fields").Value;
        Assert.Equal("Referer: https://www.bilibili.com/,User-Agent: UA/1.0", headers);
    }

    [Fact]
    public void 精确seek与变速不变调默认开启()
    {
        var opts = MpvOptions.BuildFileLocal(new MpvMediaRequest { Url = "u" });
        Assert.Contains(opts, kv => kv.Key == "hr-seek" && kv.Value == "yes");
        Assert.Contains(opts, kv => kv.Key == "audio-pitch-correction" && kv.Value == "yes");
    }

    [Fact]
    public void 全局选项关掉配置与OSC并开硬解()
    {
        var opts = MpvOptions.BuildGlobal(new MpvGlobalOptions());
        Assert.Contains(opts, kv => kv.Key == "config" && kv.Value == "no");
        Assert.Contains(opts, kv => kv.Key == "osc" && kv.Value == "no");
        Assert.Contains(opts, kv => kv.Key == "terminal" && kv.Value == "no");
        Assert.Contains(opts, kv => kv.Key == "hwdec" && kv.Value == "auto-safe");
        Assert.Contains(opts, kv => kv.Key == "idle" && kv.Value == "yes");
        Assert.DoesNotContain(opts, kv => kv.Key == "wid");
    }

    [Fact]
    public void 嵌入时写wid_headless时vo与ao为null()
    {
        var embed = MpvOptions.BuildGlobal(new MpvGlobalOptions { WindowId = 1234 });
        Assert.Contains(embed, kv => kv.Key == "wid" && kv.Value == "1234");

        var headless = MpvOptions.BuildGlobal(new MpvGlobalOptions { Headless = true });
        Assert.Contains(headless, kv => kv.Key == "vo" && kv.Value == "null");
        Assert.Contains(headless, kv => kv.Key == "ao" && kv.Value == "null");
    }

    [Fact]
    public void 缓存参数用字节与秒表达()
    {
        var opts = MpvOptions.BuildFileLocal(new MpvMediaRequest
        {
            Url = "u",
            DemuxerMaxBytesMb = 64,
            ReadAheadSecs = 3.5,
        });
        Assert.Contains(opts, kv => kv.Key == "demuxer-max-bytes" && kv.Value == "64MiB");
        Assert.Contains(opts, kv => kv.Key == "demuxer-readahead-secs" && kv.Value == "3.5");
    }

    [Theory]
    [InlineData(HwdecMode.AutoSafe, "auto-safe")]
    [InlineData(HwdecMode.AutoCopy, "auto-copy")]
    [InlineData(HwdecMode.D3D11Va, "d3d11va")]
    [InlineData(HwdecMode.D3D11VaCopy, "d3d11va-copy")]
    [InlineData(HwdecMode.D3D12, "d3d12")]
    [InlineData(HwdecMode.Nvdec, "nvdec")]
    [InlineData(HwdecMode.Vulkan, "vulkan")]
    [InlineData(HwdecMode.No, "no")]
    public void 硬解模式映射到mpv的hwdec(HwdecMode mode, string expected)
    {
        Assert.Equal(expected, mode.ToMpvValue());
    }

    [Fact]
    public void 全局选项带硬件解码与音频流保持开关()
    {
        var opts = MpvOptions.BuildGlobal(new MpvGlobalOptions
        {
            Hwdec = "d3d11va",
            AudioStreamSilence = true,
        });
        Assert.Contains(opts, kv => kv.Key == "hwdec" && kv.Value == "d3d11va");
        Assert.Contains(opts, kv => kv.Key == "audio-stream-silence" && kv.Value == "yes");

        var off = MpvOptions.BuildGlobal(new MpvGlobalOptions());
        Assert.Contains(off, kv => kv.Key == "audio-stream-silence" && kv.Value == "no");
    }

    [Fact]
    public void D3D12与Vulkan需要gpu_next()
    {
        var d3d12 = MpvOptions.BuildGlobal(new MpvGlobalOptions { Hwdec = "d3d12" });
        Assert.Contains(d3d12, kv => kv.Key == "vo" && kv.Value == "gpu-next");

        var vulkan = MpvOptions.BuildGlobal(new MpvGlobalOptions { Hwdec = "vulkan" });
        Assert.Contains(vulkan, kv => kv.Key == "vo" && kv.Value == "gpu-next");
        Assert.Contains(vulkan, kv => kv.Key == "gpu-api" && kv.Value == "vulkan");

        var d3d11 = MpvOptions.BuildGlobal(new MpvGlobalOptions { Hwdec = "d3d11va" });
        Assert.Contains(d3d11, kv => kv.Key == "vo" && kv.Value == "gpu");
    }
}
