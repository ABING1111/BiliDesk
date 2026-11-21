using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 首页消息入口右上角数字气泡的两个换算(2026-11-21)。
///
/// ★ 为什么单独抽: 合计口径与 99+ 截断都属于"要一眼能验证"的规则 ——
///   四类里漏掉一类、或者把 ">99" 写成 ">=99", 界面上只是一个似是而非的数字,
///   很难靠肉眼发现。抽成纯函数后由 BiliDesk.Tests/UnreadBadgeTests.cs 穷举边界。
/// </summary>
public static class UnreadBadge
{
    /// <summary>气泡最多显示到这个数, 再多用 "99+"(不至于把浮在按钮上的小圆块撑长)。</summary>
    public const int MaxDisplayCount = 99;

    /// <summary>
    /// 未读总数 = 私信 + 回复我的 + @我的 + 收到的赞。
    /// ★ 不能只算私信: 首页那个入口点进去的消息页本身就含这四类, 漏掉哪一类都会
    ///   出现"气泡没数字, 进去却有一堆未读"。
    /// </summary>
    public static int Total(MsgUnread? unread)
        => unread == null ? 0 : unread.Chat + unread.Reply + unread.At + unread.Like;

    /// <summary>气泡文字: 没有未读返回空串, 超过 99 显示 "99+"。</summary>
    public static string Format(int count)
    {
        if (count <= 0) return "";
        return count > MaxDisplayCount ? MaxDisplayCount + "+" : count.ToString();
    }
}
