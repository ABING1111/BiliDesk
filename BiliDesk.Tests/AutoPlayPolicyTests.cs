using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 自动连播的纯判定(2026-11-19)。
///
/// 背景: 连播队列是"打开视频那一刻"固定下来的, 用户一旦手动换了片, 旧队列就不再成立
/// (继续按它播会跳到跟用户操作完全无关的东西上)。这里把三条不变量钉死:
///   · 还在会话起点 ⇒ 继续;
///   · 当前 bvid == 正在自动切过去的目标 ⇒ 继续;
///   · 其余(用户手动点的别的视频) ⇒ 作废重开。
/// 另外把倒计时的边界钉死: 逐秒递减、到 0 就停, 绝不出现负数。
/// </summary>
public class AutoPlayPolicyTests
{
    // ---------------------------------------------------------------- 会话是否继续

    [Fact]
    public void 还在会话起点_视为会话继续()
    {
        // 刚打开播放器、队列还没走第一步。
        Assert.True(AutoPlayPolicy.ContinuesSession("BV1", "", "BV1"));
    }

    [Fact]
    public void 等于自动切换目标_视为会话继续()
    {
        // 倒计时结束切到下一条: 此刻 current == 预期目标 —— 不能把它误判成"用户手动换片"。
        Assert.True(AutoPlayPolicy.ContinuesSession("BV1", "BV2", "BV2"));
    }

    [Fact]
    public void 既不是起点也不是目标_视为用户手动换片()
    {
        Assert.False(AutoPlayPolicy.ContinuesSession("BV1", "BV2", "BV9"));
    }

    [Fact]
    public void 还没自动切过时_手动点起点以外的视频_作废()
    {
        // expected 为空 = 队列还没走过任何一步。
        Assert.False(AutoPlayPolicy.ContinuesSession("BV1", "", "BV7"));
    }

    [Fact]
    public void 当前bvid为空_不继续()
    {
        // 直播/本地文件那条路会把 _currentBvid 清空 —— 不该被当成"会话继续"。
        Assert.False(AutoPlayPolicy.ContinuesSession("", "", ""));
        Assert.False(AutoPlayPolicy.ContinuesSession("BV1", "BV2", ""));
    }

    [Fact]
    public void 起点与目标相同_仍然算继续()
    {
        // 相邻两集指向同一条(脏数据)时, 至少行为要稳定: 继续, 不炸。
        Assert.True(AutoPlayPolicy.ContinuesSession("BV1", "BV1", "BV1"));
    }

    [Fact]
    public void bvid比较区分大小写且不做模糊匹配()
    {
        // bvid 是精确标识, 大小写不同就是另一条视频。
        Assert.False(AutoPlayPolicy.ContinuesSession("BV1abc", "", "BV1ABC"));
    }

    // ---------------------------------------------------------------- 倒计时

    [Fact]
    public void 倒计时逐秒递减()
    {
        var remaining = AutoPlayPolicy.CountdownSeconds;
        Assert.Equal(5, remaining);
        Assert.Equal(4, AutoPlayPolicy.NextCountdown(remaining));
        Assert.Equal(0, AutoPlayPolicy.NextCountdown(1));
    }

    [Fact]
    public void 倒计时归零_不再变负()
    {
        Assert.Equal(0, AutoPlayPolicy.NextCountdown(0));
        Assert.Equal(0, AutoPlayPolicy.NextCountdown(-3));
    }

    [Fact]
    public void 倒计时归零_应当换片()
    {
        Assert.False(AutoPlayPolicy.ShouldAdvance(1));
        Assert.True(AutoPlayPolicy.ShouldAdvance(0));
        Assert.True(AutoPlayPolicy.ShouldAdvance(-1));
    }
}
