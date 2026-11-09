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
}
