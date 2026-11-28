using BiliDesk.Helpers;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// UP 主页「图文」Tab 的收录范围(2026-11-28)。
///
/// ★ 回归点: 之前只收 <see cref="DynamicKind.ImageText"/>, UP 发的专栏
///   (<see cref="DynamicKind.Article"/>) 在图文 Tab 里完全不出现。
///   专栏卡片本身已支持标题/封面/点开阅读, 这里把"放行 Article"钉死。
/// </summary>
public class SpaceImagesPolicyTests
{
    [Theory]
    [InlineData(DynamicKind.ImageText, true)]
    [InlineData(DynamicKind.Article, true)]
    [InlineData(DynamicKind.Video, false)]
    [InlineData(DynamicKind.Text, false)]
    [InlineData(DynamicKind.Live, false)]
    [InlineData(DynamicKind.Forward, false)]
    [InlineData(DynamicKind.Unknown, false)]
    public void Accepts_只收图文与专栏(DynamicKind kind, bool expected)
    {
        var item = new DynamicItem { Kind = kind };

        Assert.Equal(expected, SpaceImagesPolicy.Accepts(item));
    }

    [Fact]
    public void Accepts_空条目不放行()
        => Assert.False(SpaceImagesPolicy.Accepts(null));
}
