using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「这次 EOF 到底是不是播完了」的决策规则(2026-11-19, issue #88 / #93)。
///
/// 最关键的一条契约: **位置还在中间时, 任何分支都不允许返回 Finished**。
/// 老代码在"重建预算用完"后直接显示「已播完」, 正是用户报的
/// "拖动进度条/从历史打开长视频后直接提示已播完"。
/// </summary>
public class PlaybackEndPolicyTests
{
    private static PlaybackEndAction Decide(
        long pos = 300_000,
        long dur = 600_000,
        bool isLive = false,
        bool seekPending = false,
        bool seekLooksUnlanded = false,
        bool seekRebuildTried = false,
        bool canResume = true,
        int resumeCount = 0,
        int resumeMax = 3,
        bool restartTried = false,
        bool hasSource = true)
        => PlaybackEndPolicy.Decide(pos, dur, isLive, seekPending, seekLooksUnlanded,
            seekRebuildTried, canResume, resumeCount, resumeMax, restartTried, hasSource);

    [Fact]
    public void 真正播完才显示已播完()
    {
        Assert.Equal(PlaybackEndAction.Finished, Decide(pos: 600_000));
        Assert.Equal(PlaybackEndAction.Finished, Decide(pos: 600_000 - ResumePolicy.TailGuardMs));
        Assert.Equal(PlaybackEndAction.Finished, Decide(pos: 300_000, dur: 0));
        Assert.Equal(PlaybackEndAction.Finished, Decide(pos: 300_000, isLive: true));
    }

    [Fact]
    public void 中间位置提前结束_先续播()
    {
        Assert.Equal(PlaybackEndAction.ResumeFromPosition, Decide(pos: 300_000));
    }

    [Fact]
    public void 续播次数用完后兜底从头起播()
    {
        Assert.Equal(PlaybackEndAction.RestartFromStart,
            Decide(pos: 300_000, resumeCount: 3));
    }

    [Fact]
    public void 从头起播也试过之后进入失败重试态()
    {
        Assert.Equal(PlaybackEndAction.Fail,
            Decide(pos: 300_000, resumeCount: 3, restartTried: true));
    }

    [Fact]
    public void 没有可播放地址时直接失败_不能借机报播完()
    {
        Assert.Equal(PlaybackEndAction.Fail, Decide(pos: 300_000, hasSource: false));
    }

    [Fact]
    public void 跳转未落地时先重建到目标()
    {
        Assert.Equal(PlaybackEndAction.RebuildSeek,
            Decide(pos: 300_000, seekPending: true));
        Assert.Equal(PlaybackEndAction.RebuildSeek,
            Decide(pos: 300_000, seekLooksUnlanded: true));
    }

    [Fact]
    public void 重建过一次仍假EOF_再兜底从头()
    {
        Assert.Equal(PlaybackEndAction.RestartFromStart,
            Decide(pos: 300_000, seekPending: true, seekRebuildTried: true));
    }

    [Fact]
    public void 重建和从头都用完_失败重试()
    {
        Assert.Equal(PlaybackEndAction.Fail,
            Decide(pos: 300_000, seekPending: true, seekRebuildTried: true, restartTried: true));
    }

    [Fact]
    public void 没有可信位置时不能判播完()
    {
        // 重建刚清空 _lastKnownMs、新 input 还没产生第一个时间戳时:
        // 绝不能因为"位置是 0"就当成播完, 必须走重建/续播/失败重试。
        Assert.NotEqual(PlaybackEndAction.Finished, Decide(pos: 0, seekPending: true));
        Assert.NotEqual(PlaybackEndAction.Finished, Decide(pos: 0, canResume: false));
    }

    [Theory]
    [InlineData(1_000, false)]
    [InlineData(300_000, false)]
    [InlineData(589_999, false)]
    [InlineData(600_000, true)]
    public void 中间位置永远不会被判成播完(long pos, bool nearEnd)
    {
        Assert.Equal(nearEnd, ResumePolicy.IsNearEnd(pos, 600_000));
        if (!nearEnd)
        {
            Assert.NotEqual(PlaybackEndAction.Finished,
                Decide(pos: pos, seekPending: true, seekRebuildTried: true, restartTried: true));
            Assert.NotEqual(PlaybackEndAction.Finished,
                Decide(pos: pos, canResume: false, restartTried: true));
        }
    }
}
