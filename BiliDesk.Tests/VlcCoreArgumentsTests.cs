using System.Linq;
using BiliDesk.Models;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 共享 LibVLC 的基础参数 + 音频重采样模块选择(2026-11-21 起, 2026-11-22 修订)。
///
/// 锁住的核心契约是**两条一起**:
///   ① 非蓝牙端点必须 speex —— libvlc 3 的 "any" 实际选中 ugly(最近邻),
///      44.1k 音轨升到 48k 声卡时会把 44.1±1k 的镜像折叠到 2.9k/4.9k
///      (实测约 -42dBFS, 听感就是"电流声")。
///   ② 蓝牙端点必须回到 any —— speex 的 audio-resampler 子模块**对 1:1 也不放手**,
///      而且这个对象同时是 VLC 的时钟漂移补偿器; 蓝牙(独立时钟 + 端点延迟大且抖)
///      会因此每块重算滤波器, 表现是"断断续续/一卡一卡"。
/// 任一条被改掉, 都会让另一类用户"无声地"退回故障状态, 所以两条都钉住。
/// </summary>
public class VlcCoreArgumentsTests
{
    [Fact]
    public void 非蓝牙端点强制走speex重采样()
    {
        var args = VlcCore.BuildCoreArguments(1000, VideoOutputMode.Auto,
            audioResampler: VlcCore.ResolveAudioResampler("auto", defaultOutputIsBluetooth: false));

        Assert.Contains("--audio-resampler=speex", args);
        Assert.Single(args, a => a.StartsWith("--audio-resampler=", System.StringComparison.Ordinal));
    }

    [Fact]
    public void 蓝牙端点回落any()
    {
        var module = VlcCore.ResolveAudioResampler("auto", defaultOutputIsBluetooth: true);
        Assert.Equal("any", module);

        var args = VlcCore.BuildCoreArguments(1000, VideoOutputMode.Auto, audioResampler: module);
        Assert.Contains("--audio-resampler=any", args);
        Assert.DoesNotContain("--audio-resampler=speex", args);
    }

    /// <summary>
    /// 三级优先级: 环境变量 &gt; 设置项 &gt; 端点类型。手改配置/拼错的值一律落回 auto,
    /// 不能把一个不存在的模块名塞给 libvlc(虽然它有回落, 但那只是运气)。
    /// </summary>
    [Theory]
    [InlineData("speex", false, "speex")]
    [InlineData("speex", true, "speex")]      // 显式强制时不看端点(用户自己承担卡顿)
    [InlineData("any", false, "any")]
    [InlineData("compat", false, "any")]
    [InlineData("ugly", false, "any")]
    [InlineData("auto", true, "any")]
    [InlineData("auto", false, "speex")]
    [InlineData("", true, "any")]
    [InlineData(null, false, "speex")]
    [InlineData("垃圾值", false, "speex")]
    [InlineData("垃圾值", true, "any")]
    public void 重采样模块解析(string? preference, bool bluetooth, string expected)
        => Assert.Equal(expected, VlcCore.ResolveAudioResampler(preference, bluetooth));

    [Fact]
    public void 环境变量覆盖设置项()
    {
        // 支持让用户零构建 A/B: 即使设置里选了 speex, 环境变量也要赢
        Assert.Equal("any", VlcCore.ResolveAudioResampler("speex", false, "any"));
        Assert.Equal("speex", VlcCore.ResolveAudioResampler("any", true, "speex"));
        Assert.Equal("speex", VlcCore.ResolveAudioResampler("auto", true, "speex"));
    }

    [Fact]
    public void 设置项映射到偏好串()
    {
        Assert.Equal("speex", VlcCore.PreferenceOf(AudioResamplerMode.Speex));
        Assert.Equal("any", VlcCore.PreferenceOf(AudioResamplerMode.Compat));
        Assert.Equal("auto", VlcCore.PreferenceOf(AudioResamplerMode.Auto));
    }

    /// <summary>
    /// 端点分类: 枚举器是权威判据(BTHENUM\... / BTHHFENUM\...), 拿不到时才看设备名;
    /// 两样都拿不到 = 未知(null), 调用方按"非蓝牙"处理(保持现状)。
    /// </summary>
    [Theory]
    [InlineData("BTHENUM\\{0.0.0.00000000}.{abc}", "", true)]
    [InlineData("BTHHFENUM\\{0.0.0.00000000}.{abc}", "", true)]
    [InlineData("USB\\VID_1234", "USB Audio", false)]
    [InlineData("HDAUDIO\\FUNC_01", "Realtek", false)]
    [InlineData("", "XXX 蓝牙耳机", true)]
    [InlineData("", "Speakers (Realtek)", false)]
    [InlineData("", "", null)]
    [InlineData(null, null, null)]
    public void 蓝牙端点判定(string? enumerator, string? name, bool? expected)
        => Assert.Equal(expected, AudioOutputProfile.ClassifyBluetooth(enumerator, name));

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
