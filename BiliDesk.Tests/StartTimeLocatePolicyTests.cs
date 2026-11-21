using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// "重建后 :start-time 到底有没有定位成功、该不该换方式重试"的判据(2026-11-20)。
///
/// 背景: 用户报「长视频拖进度条回到开头, 时好时坏」。真机上"这一次灵、下一次不灵"
/// 取决于当次选到的 CDN 线路(实测 mirroraliov / estgoss 的 :start-time 成功率是 0/5),
/// 很难在测试里复现, 所以把判据抽成纯函数在这里穷举边界。
/// </summary>
public class StartTimeLocatePolicyTests
{
    /// <summary>典型情形: 拖到 41 分钟, 重建后位置还停在 0 → 该换方式重试。</summary>
    [Fact]
    public void 定位没生效时应该换方式重试()
        => Assert.True(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            rebuiltViaStartTime: true,
            alreadyRetried: false,
            lastSeekTargetMs: 4_129_817,
            lastKnownMs: 0,
            isLive: false,
            isLocal: false,
            hasSource: true));

    /// <summary>定位成功(起播就在目标附近)时绝不能重试 —— 那会把好好的画面打断。</summary>
    [Theory]
    [InlineData(4_129_817L)]      // 落点正好等于目标
    [InlineData(4_129_817L - 4_000L)]  // 实测的正常偏差 1~4s
    public void 定位成功时不应该重试(long posMs)
        => Assert.False(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            rebuiltViaStartTime: true,
            alreadyRetried: false,
            lastSeekTargetMs: 4_129_817,
            lastKnownMs: posMs,
            isLive: false,
            isLocal: false,
            hasSource: true));

    /// <summary>预算只有一次: 已经换过一次方式还失败, 就如实停在失败态, 不能循环重建。</summary>
    [Fact]
    public void 已经重试过就不再重试()
        => Assert.False(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            rebuiltViaStartTime: true,
            alreadyRetried: true,
            lastSeekTargetMs: 4_129_817,
            lastKnownMs: 0,
            isLive: false,
            isLocal: false,
            hasSource: true));

    /// <summary>不是 :start-time 重建出来的 input(普通起播/续播), 不归这条判据管。</summary>
    [Fact]
    public void 非start_time重建不走这条判据()
        => Assert.False(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            rebuiltViaStartTime: false,
            alreadyRetried: false,
            lastSeekTargetMs: 4_129_817,
            lastKnownMs: 0,
            isLive: false,
            isLocal: false,
            hasSource: true));

    /// <summary>
    /// ★ 目标本身就在片头附近时, "停在 0"是**正确结果** —— 不能误判成定位失败。
    /// 否则"拖到第 3 秒"会白白重建一次, 用户看到的是多余的缓冲。
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(3_000L)]
    [InlineData(15_000L)]
    public void 目标就在片头附近时不算定位失败(long targetMs)
        => Assert.False(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            rebuiltViaStartTime: true,
            alreadyRetried: false,
            lastSeekTargetMs: targetMs,
            lastKnownMs: 0,
            isLive: false,
            isLocal: false,
            hasSource: true));

    /// <summary>本片还没 seek 过(target=-1): EOF 该交给"提前结束"那套逻辑, 不是定位问题。</summary>
    [Fact]
    public void 没seek过时不归这条判据管()
        => Assert.False(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            rebuiltViaStartTime: true,
            alreadyRetried: false,
            lastSeekTargetMs: -1,
            lastKnownMs: 0,
            isLive: false,
            isLocal: false,
            hasSource: true));

    /// <summary>直播/本地文件各自有别的路: 直播没有时间轴, 本地文件不用 :start-time。</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void 直播与本地文件不重试(bool isLive, bool isLocal)
        => Assert.False(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            rebuiltViaStartTime: true,
            alreadyRetried: false,
            lastSeekTargetMs: 4_129_817,
            lastKnownMs: 0,
            isLive: isLive,
            isLocal: isLocal,
            hasSource: true));

    /// <summary>地址都没了: 重建只会空转, 别浪费那一次预算。</summary>
    [Fact]
    public void 没有可播地址时不重试()
        => Assert.False(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            rebuiltViaStartTime: true,
            alreadyRetried: false,
            lastSeekTargetMs: 4_129_817,
            lastKnownMs: 0,
            isLive: false,
            isLocal: false,
            hasSource: false));

    /// <summary>
    /// 阈值边界: 位置刚好落在容差内算"没定位", 刚好超出算"已经定位"。
    /// ★ 钉住这两个值, 以后改容差时会立刻发现行为变了。
    /// </summary>
    [Fact]
    public void 容差边界()
    {
        Assert.True(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            true, false, 4_129_817, StartTimeLocatePolicy.MissToleranceMs - 1, false, false, true));
        Assert.False(StartTimeLocatePolicy.ShouldRetryWithoutStartTime(
            true, false, 4_129_817, StartTimeLocatePolicy.MissToleranceMs, false, false, true));
    }
}
