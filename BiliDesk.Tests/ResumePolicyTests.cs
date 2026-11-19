using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 续播 / 片尾判定(2026-11-19, issue #88 / #93)。
///
/// 这里锁住的是**行为契约**: 哪些位置值得续播、哪些已经算看完、
/// 以及 "EOF 位置到底算不算真的到片尾"。播放器与进度服务都只调这一份规则,
/// 避免两边阈值漂移导致"记录说还能续、播放器却 seek 到片尾触发假 EOF"。
/// </summary>
public class ResumePolicyTests
{
    [Fact]
    public void 太靠前的位置不续播()
    {
        Assert.False(ResumePolicy.IsResumable(0, 600_000));
        Assert.False(ResumePolicy.IsResumable(4_999, 600_000));
        Assert.True(ResumePolicy.IsResumable(5_000, 600_000));
    }

    [Fact]
    public void 中间位置可以续播()
    {
        Assert.True(ResumePolicy.IsResumable(300_000, 600_000));
        Assert.True(ResumePolicy.IsResumable(580_000, 600_000));   // 距结尾 20s
    }

    [Fact]
    public void 距结尾十秒内算看完()
    {
        Assert.False(ResumePolicy.IsResumable(590_000, 600_000));  // 正好 10s
        Assert.False(ResumePolicy.IsResumable(599_000, 600_000));
        Assert.True(ResumePolicy.IsCompleted(599_000, 600_000));
    }

    [Fact]
    public void 超过百分之九十八也算看完()
    {
        // 2 小时: 98% = 7056s, 距结尾还有 72s —— 尾差规则放行, 比例规则必须拦住。
        const long dur = 7_200_000;
        Assert.False(ResumePolicy.IsResumable((long)(dur * 0.981), dur));
        Assert.True(ResumePolicy.IsResumable((long)(dur * 0.97), dur));
    }

    [Fact]
    public void 时长未知时只排除太靠前()
    {
        Assert.False(ResumePolicy.IsResumable(4_999, 0));
        Assert.True(ResumePolicy.IsResumable(5_000, 0));
        Assert.True(ResumePolicy.IsResumable(9_999_999, 0));
    }

    [Fact]
    public void 续播目标不会进到尾部门区()
    {
        const long dur = 600_000;
        Assert.Equal(300_000, ResumePolicy.ClampResumeMs(300_000, dur));
        Assert.Equal(dur - ResumePolicy.TailGuardMs, ResumePolicy.ClampResumeMs(dur - 1_000, dur));
        Assert.Equal(0, ResumePolicy.ClampResumeMs(-1, dur));
        // 时长未知时原样返回, 不猜上界
        Assert.Equal(123_456, ResumePolicy.ClampResumeMs(123_456, 0));
    }

    [Fact]
    public void 片尾判定()
    {
        // 位置为 0 且时长已知: 没有可信位置, 不能判"看完"
        Assert.False(ResumePolicy.IsNearEnd(0, 600_000));
        Assert.False(ResumePolicy.IsNearEnd(300_000, 600_000));
        Assert.True(ResumePolicy.IsNearEnd(600_000 - ResumePolicy.TailGuardMs, 600_000));
        Assert.True(ResumePolicy.IsNearEnd(600_000, 600_000));
        // 时长未知: 只能相信 EOF 本身
        Assert.True(ResumePolicy.IsNearEnd(0, 0));
    }
}
