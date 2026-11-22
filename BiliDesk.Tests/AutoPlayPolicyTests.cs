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
/// 另外钉死队列项的身份(2026-11-22): 多P 视频的各个 P bvid 相同, 必须连 cid 一起比;
/// 以及倒计时的边界: 逐秒递减、到 0 就停, 绝不出现负数。
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

    // ---------------------------------------------------------------- 队列项身份(分P)

    [Fact]
    public void 同一稿件不同分P_不是同一项()
    {
        // ★ 分P 之间 bvid 完全相同, 只比 bvid 会把 P2 当成 P1: 出队删错位置,
        //   表现是"播完 P1 又播 P1"。
        Assert.False(AutoPlayPolicy.SameEntry("BV1", 1001, "BV1", 1002));
    }

    [Fact]
    public void bvid与cid都相同_才是同一项()
    {
        Assert.True(AutoPlayPolicy.SameEntry("BV1", 1001, "BV1", 1001));
        // 合集/番剧/相关视频都没有分P 信息, cid 全是 0, 仍按 bvid 区分。
        Assert.True(AutoPlayPolicy.SameEntry("BV1", 0, "BV1", 0));
    }

    [Fact]
    public void 整条稿件与它的某个分P_不是同一项()
    {
        Assert.False(AutoPlayPolicy.SameEntry("BV1", 0, "BV1", 1001));
        Assert.False(AutoPlayPolicy.SameEntry("BV2", 0, "BV1", 0));
    }

    // ---------------------------------------------------------------- 当前项之后

    [Fact]
    public void 当前项之后的下标_按顺序给全()
    {
        Assert.Equal(new[] { 2, 3, 4 }, AutoPlayPolicy.FollowingIndexes(5, 1));
    }

    [Fact]
    public void 当前是第一项_给出其余全部()
    {
        Assert.Equal(new[] { 1, 2 }, AutoPlayPolicy.FollowingIndexes(3, 0));
    }

    [Fact]
    public void 当前是最后一项_没有下一条()
    {
        Assert.Empty(AutoPlayPolicy.FollowingIndexes(5, 4));
    }

    [Fact]
    public void 定位不上当前项_不猜下一条()
    {
        Assert.Empty(AutoPlayPolicy.FollowingIndexes(5, -1));
        Assert.Empty(AutoPlayPolicy.FollowingIndexes(0, 0));
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
