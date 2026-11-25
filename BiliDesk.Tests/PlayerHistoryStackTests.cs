using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 播放器「上一个 / 下一个视频」记录栈(2026-11-25)。
///
/// 语义与浏览器后退/前进一致。这里钉死的几条不变量:
///   · 新打开的一条进后退栈、前进栈清空;
///   · 后退/前进只在"目标真的打开了"那一刻兑现, 加载失败不动栈;
///   · 同一条稿件换分P 只更新当前条目, 不算新的一条;
///   · 判等按种类走不同身份(在线比 bvid、直播比 roomId、本地比路径), 跨种类永不相等。
/// </summary>
public class PlayerHistoryStackTests
{
    // ---------------------------------------------------------------- 基本前后关系

    [Fact]
    public void 第一条视频_两个方向都不可用()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));

        Assert.Null(stack.Previous);
        Assert.Null(stack.Next);
        Assert.Equal("BV1", stack.Current!.Bvid);
    }

    [Fact]
    public void 打开第二条_上一条进了后退栈()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));

        Assert.Equal("BV1", stack.Previous!.Bvid);
        Assert.Null(stack.Next);
    }

    [Fact]
    public void 后退再前进_回到刚才那条()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));

        var target = stack.RequestBack();
        Assert.Equal("BV1", target!.Bvid);
        stack.OnOpened(Video("BV1", 101));

        // 退到 BV1 之后: 后面没有可退的, 前面是刚才所在的 BV2
        Assert.Null(stack.Previous);
        Assert.Equal("BV2", stack.Next!.Bvid);

        var back = stack.RequestForward();
        Assert.Equal("BV2", back!.Bvid);
        stack.OnOpened(Video("BV2", 201));

        Assert.Equal("BV2", stack.Current!.Bvid);
        Assert.Equal("BV1", stack.Previous!.Bvid);
        Assert.Null(stack.Next);
    }

    [Fact]
    public void 后退之后打开新视频_前进栈清空()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));
        stack.RequestBack();
        stack.OnOpened(Video("BV1", 101));

        stack.OnOpened(Video("BV3", 301));

        Assert.Null(stack.Next);
        Assert.Equal("BV1", stack.Previous!.Bvid);
        Assert.Equal("BV3", stack.Current!.Bvid);
    }

    // ---------------------------------------------------------------- 同一条稿件

    [Fact]
    public void 同一稿件换分P_不新增历史_只更新当前条目()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV1", 102));

        Assert.Null(stack.Previous);
        Assert.Equal(102, stack.Current!.Cid);
    }

    [Fact]
    public void 同一稿件换分P_保留前进栈()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));
        stack.RequestBack();
        stack.OnOpened(Video("BV1", 101));

        // 在 BV1 上换了 P: 仍然算"还是这一条", 不该把 BV2 那条前进记录冲掉
        stack.OnOpened(Video("BV1", 103));

        Assert.Equal("BV2", stack.Next!.Bvid);
        Assert.Equal(103, stack.Current!.Cid);
    }

    // ---------------------------------------------------------------- 请求与兑现

    [Fact]
    public void 请求后退但目标没打开_栈保持原样()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));

        stack.RequestBack();
        // 加载失败 ⇒ 没有 OnOpened

        Assert.Equal("BV1", stack.Previous!.Bvid);
        Assert.Equal("BV2", stack.Current!.Bvid);
    }

    [Fact]
    public void 请求后退但被别的视频顶替_按新访问处理()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));

        stack.RequestBack();
        // 后退途中用户从主窗口点了 BV9
        stack.OnOpened(Video("BV9", 901));

        Assert.Equal("BV2", stack.Previous!.Bvid);
        Assert.Equal("BV9", stack.Current!.Bvid);
        Assert.Null(stack.Next);
    }

    [Fact]
    public void 请求后退_失败后重新打开同一目标_仍按后退兑现()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));

        stack.RequestBack();
        // 第一次打不开(视频临时失败), 用户又试了一次同一条
        stack.OnOpened(Video("BV1", 101));

        Assert.Null(stack.Previous);
        Assert.Equal("BV2", stack.Next!.Bvid);
    }

    // ---------------------------------------------------------------- 判等

    [Fact]
    public void 在线条目按bvid判等_不看cid()
    {
        Assert.True(PlayerHistoryEntry.ForVideo("BV1", 101, "a").SameVideo(
            PlayerHistoryEntry.ForVideo("BV1", 999, "b")));
        Assert.False(PlayerHistoryEntry.ForVideo("BV1", 101, "a").SameVideo(
            PlayerHistoryEntry.ForVideo("BV2", 101, "a")));
    }

    [Fact]
    public void 直播按房间号判_本地按路径判且不区分大小写()
    {
        Assert.True(PlayerHistoryEntry.ForLive(7, "a").SameVideo(PlayerHistoryEntry.ForLive(7, "b")));
        Assert.False(PlayerHistoryEntry.ForLive(7, "a").SameVideo(PlayerHistoryEntry.ForLive(8, "a")));

        Assert.True(PlayerHistoryEntry.ForLocal(@"C:\a\b.mp4", "a").SameVideo(
            PlayerHistoryEntry.ForLocal(@"c:\A\B.MP4", "b")));
        Assert.False(PlayerHistoryEntry.ForLocal(@"C:\a\b.mp4", "a").SameVideo(
            PlayerHistoryEntry.ForLocal(@"C:\a\c.mp4", "a")));
    }

    [Fact]
    public void 不同种类永不相等()
    {
        // 直播/本地条目的 bvid 都是空串, 不能靠空串互相认成同一条
        Assert.False(PlayerHistoryEntry.ForLive(0, "").SameVideo(PlayerHistoryEntry.ForLocal("", "")));
        Assert.False(PlayerHistoryEntry.ForVideo("", 0, "").SameVideo(PlayerHistoryEntry.ForLive(0, "")));
        Assert.False(PlayerHistoryEntry.ForLocal(@"C:\a.mp4", "").SameVideo(PlayerHistoryEntry.ForVideo("", 0, "")));
    }

    // ---------------------------------------------------------------- 上限与删除

    [Fact]
    public void 超过上限_丢掉最旧的()
    {
        var stack = new PlayerHistoryStack();
        for (var i = 0; i <= PlayerHistoryStack.MaxEntries; i++)
            stack.OnOpened(Video("BV" + i, i));

        // 满栈之后最新那条仍然是当前, 栈顶是它前面那条
        Assert.Equal("BV" + PlayerHistoryStack.MaxEntries, stack.Current!.Bvid);
        Assert.Equal("BV" + (PlayerHistoryStack.MaxEntries - 1), stack.Previous!.Bvid);
    }

    [Fact]
    public void 一栈满之后_后退到底仍然能走满上限条()
    {
        var stack = new PlayerHistoryStack();
        var total = PlayerHistoryStack.MaxEntries + 10;
        for (var i = 0; i <= total; i++)
            stack.OnOpened(Video("BV" + i, i));

        var steps = 0;
        while (true)
        {
            var target = stack.RequestBack();
            if (target == null) break;
            stack.OnOpened(target);
            steps++;
        }
        Assert.Equal(PlayerHistoryStack.MaxEntries, steps);
        // 退到底时停在还留在栈里的最旧那条: BV(total-Max)
        Assert.Equal("BV" + (total - PlayerHistoryStack.MaxEntries), stack.Current!.Bvid);
    }

    [Fact]
    public void Remove_只摘掉指定那条()
    {
        var stack = new PlayerHistoryStack();
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));
        stack.OnOpened(Video("BV3", 301));

        stack.Remove(Video("BV2", 201));

        // BV2 从后退栈里消失: 剩下的队头是 BV1, 再退一步就没有了
        Assert.Equal("BV1", stack.Previous!.Bvid);
        Assert.Equal("BV3", stack.Current!.Bvid);
    }

    [Fact]
    public void 请求不到目标时不改方向标记()
    {
        var stack = new PlayerHistoryStack();
        Assert.Null(stack.RequestBack());
        Assert.Null(stack.RequestForward());

        // 之后打开一条新视频: 不应被任何残留方向影响
        stack.OnOpened(Video("BV1", 101));
        stack.OnOpened(Video("BV2", 201));
        Assert.Equal("BV1", stack.Previous!.Bvid);
        Assert.Null(stack.Next);
    }

    private static PlayerHistoryEntry Video(string bvid, long cid)
        => PlayerHistoryEntry.ForVideo(bvid, cid, bvid);
}
