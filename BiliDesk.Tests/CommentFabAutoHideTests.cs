using BiliDesk.Helpers;

namespace BiliDesk.Tests;

/// <summary>
/// 评论区工具列「滚动自动收起」的判定(issue #89):
/// 向下滚过阈值收起、向上滚/回到顶部放回, 内容撑高与输入面板展开都不参与。
/// </summary>
public class CommentFabAutoHideTests
{
    [Fact]
    public void ScrollDownPastThreshold_Hides()
    {
        Assert.True(CommentFabAutoHide.Decide(
            hidden: false, offset: 600, verticalChange: 120, extentChange: 0, composerOpen: false));
    }

    [Fact]
    public void ScrollDownNearTop_StaysVisible()
    {
        Assert.False(CommentFabAutoHide.Decide(
            hidden: false, offset: 60, verticalChange: 30, extentChange: 0, composerOpen: false));
    }

    [Fact]
    public void ScrollUp_ShowsAgain()
    {
        Assert.False(CommentFabAutoHide.Decide(
            hidden: true, offset: 600, verticalChange: -120, extentChange: 0, composerOpen: false));
    }

    [Fact]
    public void BackAtTop_AlwaysShows()
    {
        // 已经到顶: 哪怕这次位移是 0(如程序化 ScrollToTop 后的收尾事件)也要放回来
        Assert.False(CommentFabAutoHide.Decide(
            hidden: true, offset: 0, verticalChange: 0, extentChange: 0, composerOpen: false));
    }

    [Fact]
    public void ComposerOpen_AlwaysShows()
    {
        // 面板展开时 FAB 就是「收起输入框」按钮, 不能藏
        Assert.False(CommentFabAutoHide.Decide(
            hidden: true, offset: 900, verticalChange: 200, extentChange: 0, composerOpen: true));
    }

    [Fact]
    public void ContentGrewWithoutScrolling_KeepsState()
    {
        // 追加评论 / 图片解码撑高列表: 不是用户在往下翻, 状态不许变
        Assert.False(CommentFabAutoHide.Decide(
            hidden: false, offset: 400, verticalChange: 0, extentChange: 300, composerOpen: false));
        Assert.True(CommentFabAutoHide.Decide(
            hidden: true, offset: 400, verticalChange: 0, extentChange: 300, composerOpen: false));
    }

    [Fact]
    public void NoMovement_KeepsState()
    {
        Assert.True(CommentFabAutoHide.Decide(
            hidden: true, offset: 400, verticalChange: 0, extentChange: 0, composerOpen: false));
        Assert.False(CommentFabAutoHide.Decide(
            hidden: false, offset: 400, verticalChange: 0, extentChange: 0, composerOpen: false));
    }
}
