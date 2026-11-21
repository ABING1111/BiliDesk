using BiliDesk.Helpers;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 首页消息入口数字气泡的换算(2026-11-21)。
///
/// ★ 这些边界在真机上很难分别验证: 0 / 99 / 100 各需要不同的账号状态,
///   而写错时界面只差一位数字, 不会报错。
/// </summary>
public class UnreadBadgeTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1, "1")]
    [InlineData(42, "42")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(1234, "99+")]
    public void Format_按未读数生成气泡文字(int count, string expected)
        => Assert.Equal(expected, UnreadBadge.Format(count));

    [Fact]
    public void Total_四类未读都要计入()
    {
        var unread = new MsgUnread { Chat = 2, Reply = 3, At = 4, Like = 5 };
        Assert.Equal(14, UnreadBadge.Total(unread));
    }

    [Fact]
    public void Total_没有未读时为零()
        => Assert.Equal(0, UnreadBadge.Total(new MsgUnread()));

    [Fact]
    public void Total_空对象按零处理()
        => Assert.Equal(0, UnreadBadge.Total(null));
}
