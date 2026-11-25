using BiliDesk.Helpers;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 动态页页内搜索的匹配规则(issue #127)。
/// 覆盖: 分词、空词条放行、标题/正文/UP 名命中、转发看原动态、多词要求全部命中、大小写不敏感。
/// </summary>
public class FeedSearchPolicyTests
{
    private static readonly System.Collections.Generic.IReadOnlyList<string> Empty =
        System.Array.Empty<string>();

    private static DynamicItem Video(string title, string author = "某UP", string bvid = "BV1xx411c7mD") => new()
    {
        Kind = DynamicKind.Video,
        Title = title,
        AuthorName = author,
        Bvid = bvid,
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SplitTerms_空白_没有任何词(string? keyword)
    {
        Assert.Empty(FeedSearchPolicy.SplitTerms(keyword));
    }

    [Fact]
    public void SplitTerms_空格与中英文逗号都能分词()
    {
        Assert.Equal(new[] { "黑神话", "攻略", "第1章" },
            FeedSearchPolicy.SplitTerms("黑神话 攻略，第1章"));
    }

    [Fact]
    public void Matches_没有词条时全部命中()
    {
        Assert.True(FeedSearchPolicy.Matches(Video("任意标题"), Empty));
        Assert.True(FeedSearchPolicy.Matches((DynamicItem?)null, Empty));
    }

    [Fact]
    public void Matches_标题命中()
    {
        var terms = FeedSearchPolicy.SplitTerms("黑神话");
        Assert.True(FeedSearchPolicy.Matches(Video("黑神话 全流程攻略"), terms));
    }

    [Fact]
    public void Matches_正文与UP名也能命中()
    {
        var byText = new DynamicItem { Kind = DynamicKind.Text, ContentText = "今天聊聊显卡驱动", AuthorName = "阿宾" };
        var terms = FeedSearchPolicy.SplitTerms("显卡");
        Assert.True(FeedSearchPolicy.Matches(byText, terms));

        terms = FeedSearchPolicy.SplitTerms("阿宾");
        Assert.True(FeedSearchPolicy.Matches(byText, terms));
    }

    [Fact]
    public void Matches_转发的动态看原动态的标题()
    {
        var forward = new DynamicItem
        {
            Kind = DynamicKind.Forward,
            ContentText = "这个攻略不错",
            AuthorName = "转发的人",
            Orig = Video("黑神话 隐藏结局", author: "原作者", bvid: "BV1yy411c7mE"),
        };

        Assert.True(FeedSearchPolicy.Matches(forward, FeedSearchPolicy.SplitTerms("隐藏结局")));
        Assert.True(FeedSearchPolicy.Matches(forward, FeedSearchPolicy.SplitTerms("原作者")));
    }

    [Fact]
    public void Matches_多个词要求全部命中()
    {
        var item = Video("黑神话 全流程攻略");
        Assert.True(FeedSearchPolicy.Matches(item, FeedSearchPolicy.SplitTerms("黑神话 攻略")));
        Assert.False(FeedSearchPolicy.Matches(item, FeedSearchPolicy.SplitTerms("黑神话 速通")));
    }

    [Fact]
    public void Matches_大小写不敏感()
    {
        var item = Video("ELDEN RING 女武神");
        Assert.True(FeedSearchPolicy.Matches(item, FeedSearchPolicy.SplitTerms("elden")));
    }

    [Fact]
    public void Matches_Object重载_非动态条目放行()
    {
        Assert.True(FeedSearchPolicy.Matches((object)"任意对象", FeedSearchPolicy.SplitTerms("不存在的词")));
    }
}
