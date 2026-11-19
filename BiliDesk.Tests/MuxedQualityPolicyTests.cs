using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// durl 取流结果是否满足用户显式选择的清晰度。
///
/// ★ 回归背景(2026-11-20): durl 部分视频没有 480P 单流, 请求 qn=32 会静默返回
///   quality=16。若不拦住, 用户点 480P 实际看到 360P。
/// </summary>
public class MuxedQualityPolicyTests
{
    [Fact]
    public void HonorsRequest_FalseWhenServerSilentlyDowngrades()
    {
        // 实测样例: 请求 480P, 服务端给 360P, accept_quality 里也没有 480P
        var accepted = new[] { (64, "720P"), (16, "360P") };

        Assert.False(MuxedQualityPolicy.HonorsRequest(32, 16, accepted));
    }

    [Theory]
    [InlineData(32, 32)]   // 精确命中
    [InlineData(32, 64)]   // 服务端给了更高档, 不算降级
    [InlineData(64, 64)]
    [InlineData(16, 16)]
    public void HonorsRequest_TrueWhenServerMeetsOrExceedsRequest(int requested, int actual)
    {
        var accepted = new[] { (64, "720P"), (16, "360P") };

        Assert.True(MuxedQualityPolicy.HonorsRequest(requested, actual, accepted));
    }

    [Fact]
    public void HonorsRequest_FallsBackToAcceptedListWhenActualMissing()
    {
        var with480 = new[] { (64, "720P"), (32, "480P"), (16, "360P") };
        var without480 = new[] { (64, "720P"), (16, "360P") };

        Assert.True(MuxedQualityPolicy.HonorsRequest(32, 0, with480));
        Assert.False(MuxedQualityPolicy.HonorsRequest(32, 0, without480));
    }
}
