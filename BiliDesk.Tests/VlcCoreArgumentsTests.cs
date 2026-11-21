using System.Linq;
using BiliDesk.Models;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 共享 LibVLC 的基础参数(2026-11-21)。
///
/// 锁住的核心契约只有一条: **音频必须走 speex 重采样**。
/// libvlc 3 的 --audio-resampler 默认 "any" 会选中 ugly(线性插值),
/// 44.1k 音轨升到 48k 声卡时会把 44.1±1k 的镜像折叠到 2.9k/4.9k
/// (实测约 -42dBFS, 听感就是"电流声")。这条一旦被删掉, 音质会无声地退回旧行为,
/// 所以用测试钉住。
/// </summary>
public class VlcCoreArgumentsTests
{
    [Fact]
    public void 音频强制走speex重采样()
    {
        var args = VlcCore.BuildCoreArguments(1000, VideoOutputMode.Auto);

        Assert.Contains("--audio-resampler=speex", args);
        Assert.Single(args, a => a.StartsWith("--audio-resampler=", System.StringComparison.Ordinal));
    }

    [Fact]
    public void 自动视频输出不传vout()
    {
        var args = VlcCore.BuildCoreArguments(1000, VideoOutputMode.Auto);
        Assert.DoesNotContain(args, a => a.StartsWith("--vout=", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(VideoOutputMode.Direct3D11, "direct3d11")]
    [InlineData(VideoOutputMode.Direct3D9, "direct3d9")]
    [InlineData(VideoOutputMode.OpenGL, "glwin32")]
    public void 指定视频输出映射到插件名(VideoOutputMode mode, string expected)
    {
        var args = VlcCore.BuildCoreArguments(1000, mode);
        Assert.Contains("--vout=" + expected, args);
        Assert.Equal(expected, VlcCore.VoutModuleFor(mode));
    }

    [Fact]
    public void 原生日志开关只加verbose()
    {
        var args = VlcCore.BuildCoreArguments(1000, VideoOutputMode.Auto, nativeDebugLog: true);

        Assert.Contains("--verbose=2", args);
    }

    /// <summary>
    /// 3.0.24 的 Windows 包里没有 file logger 插件, 传 --file-logging 会让 new LibVLC 抛异常。
    /// 这条钉住"别再把它加回来"(落盘走 SetLogFile, 不是命令行选项)。
    /// </summary>
    [Fact]
    public void 原生日志不得传file_logging()
    {
        var args = VlcCore.BuildCoreArguments(1000, VideoOutputMode.Auto, nativeDebugLog: true);

        Assert.DoesNotContain(args, a => a.StartsWith("--file-logging", System.StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--logfile=", System.StringComparison.Ordinal));
    }

    [Fact]
    public void 默认不开原生日志()
    {
        var args = VlcCore.BuildCoreArguments(1000, VideoOutputMode.Auto);
        Assert.DoesNotContain(args, a => a.StartsWith("--verbose=", System.StringComparison.Ordinal));
    }

    [Fact]
    public void 网络缓冲值透传()
    {
        var args = VlcCore.BuildCoreArguments(2500, VideoOutputMode.Auto);
        Assert.Contains("--network-caching=2500", args);
    }
}
