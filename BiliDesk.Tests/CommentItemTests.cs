using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 关闭评论详情页时的楼中楼回收(2026-11-09 评论区内存优化)。
///
/// ★ 关键不变量:
///   ① 回收后列表预览(PreviewReplies)一条都不能少 —— 否则列表里的楼中楼方框会变空;
///   ② 页码要复位, 下次进详情页才能重新拉到后面的回复;
///   ③ 本来就只加载了一页(≤ 预览条数)时不要动, 免得白白重拉。
/// </summary>
public class CommentItemTests
{
    private static CommentItem Reply(long rpid)
        => new() { Rpid = rpid, Content = "回复" + rpid };

    [Fact]
    public void 关闭详情后只保留预览回复()
    {
        var root = new CommentItem { Rpid = 1, ReplyCount = 50 };
        for (var i = 1; i <= 10; i++) root.Replies.Add(Reply(i));
        root.SyncPreviewReplies();
        root.RepliesNextPage = 4;

        root.TrimRepliesToPreview();

        Assert.Equal(3, root.Replies.Count);
        Assert.Equal(root.PreviewReplies, root.Replies);
        Assert.Equal(1, root.RepliesNextPage);
        Assert.True(root.HasMoreReplies);
    }

    [Fact]
    public void 只有预览量时不做无谓的重置()
    {
        var root = new CommentItem { Rpid = 1, ReplyCount = 3 };
        for (var i = 1; i <= 3; i++) root.Replies.Add(Reply(i));
        root.SyncPreviewReplies();
        root.RepliesNextPage = 2;

        root.TrimRepliesToPreview();

        Assert.Equal(3, root.Replies.Count);
        Assert.Equal(2, root.RepliesNextPage);
    }

    [Fact]
    public void 预览已显示全部回复时不显示总条数()
    {
        var root = new CommentItem { Rpid = 1, ReplyCount = 3 };
        for (var i = 1; i <= 3; i++) root.Replies.Add(Reply(i));
        root.SyncPreviewReplies();

        Assert.Equal("", root.ReplyBoxText);
    }

    [Fact]
    public void 预览没显示全部回复时仍显示总条数()
    {
        var root = new CommentItem { Rpid = 1, ReplyCount = 5 };
        for (var i = 1; i <= 3; i++) root.Replies.Add(Reply(i));
        root.SyncPreviewReplies();

        Assert.Equal("共 5 条回复", root.ReplyBoxText);
    }

    // ------------------------------------------------------------ 删除权限(2026-11-28)

    [Fact]
    public void 自己的评论可以删除()
    {
        var c = new CommentItem { Rpid = 10, Mid = 1001, SelfMid = 1001, OwnerMid = 2002 };
        Assert.True(c.CanDelete);
        Assert.True(c.CanDeleteNow);
    }

    [Fact]
    public void 别人视频里别人的评论不能删除()
    {
        var c = new CommentItem { Rpid = 10, Mid = 3003, SelfMid = 1001, OwnerMid = 2002 };
        Assert.False(c.CanDelete);
    }

    [Fact]
    public void UP主可以删除自己视频下他人的评论()
    {
        var c = new CommentItem { Rpid = 10, Mid = 3003, SelfMid = 2002, OwnerMid = 2002 };
        Assert.True(c.CanDelete);
    }

    [Fact]
    public void 未登录时没有删除入口()
    {
        var c = new CommentItem { Rpid = 10, Mid = 1001, SelfMid = 0, OwnerMid = 1001 };
        Assert.False(c.CanDelete);
    }

    [Fact]
    public void 删除请求在途时不能再点()
    {
        var c = new CommentItem { Rpid = 10, Mid = 1001, SelfMid = 1001, OwnerMid = 2002 };
        c.DeleteBusy = true;
        Assert.True(c.CanDelete);      // 入口仍然显示
        Assert.False(c.CanDeleteNow);  // 但暂时不可点
        c.DeleteBusy = false;
        Assert.True(c.CanDeleteNow);
    }
}
