using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// PGC 条目的展示映射(2026-11-28 影视页新增)。
///
/// ★ 这些属性直接决定卡片上写什么: 分类名写错(电影显示成"番剧")、评分不显示
///   或把番剧的更新进度挤掉, 都不会报错, 只会在界面上悄悄不对。
/// </summary>
public class BangumiItemTests
{
    [Theory]
    [InlineData(1, "番剧")]
    [InlineData(2, "电影")]
    [InlineData(3, "纪录片")]
    [InlineData(4, "国创")]
    [InlineData(5, "电视剧")]
    [InlineData(7, "综艺")]
    [InlineData(99, "番剧")]
    public void SeasonTypeNameOf_按类型给中文名(int type, string expected)
        => Assert.Equal(expected, BangumiItem.SeasonTypeNameOf(type));

    [Fact]
    public void SeasonTypeText_优先用接口给的名字()
    {
        var item = new BangumiItem { SeasonType = 2, SeasonTypeName = "电影" };
        Assert.Equal("电影", item.SeasonTypeText);
    }

    [Fact]
    public void SeasonTypeText_接口没给名字时按类型兜底()
    {
        var item = new BangumiItem { SeasonType = 5 };
        Assert.Equal("电视剧", item.SeasonTypeText);
    }

    [Fact]
    public void IsTeleplay_覆盖影视四类()
    {
        Assert.False(new BangumiItem { SeasonType = 1 }.IsTeleplay);
        Assert.False(new BangumiItem { SeasonType = 4 }.IsTeleplay);
        Assert.True(new BangumiItem { SeasonType = 2 }.IsTeleplay);
        Assert.True(new BangumiItem { SeasonType = 3 }.IsTeleplay);
        Assert.True(new BangumiItem { SeasonType = 5 }.IsTeleplay);
        Assert.True(new BangumiItem { SeasonType = 7 }.IsTeleplay);
    }

    [Fact]
    public void CardBadgeText_影视优先显示评分()
    {
        var item = new BangumiItem
        {
            SeasonType = 2,
            Score = "9.6",
            IndexShow = "2022年6月30日上映"
        };
        Assert.Equal("9.6", item.CardBadgeText);
    }

    [Fact]
    public void CardBadgeText_影视没有评分时退回更新进度()
    {
        var item = new BangumiItem
        {
            SeasonType = 5,
            IndexShow = "更新至第 30 集"
        };
        Assert.Equal("更新至第 30 集", item.CardBadgeText);
    }

    [Fact]
    public void CardBadgeText_番剧不拿评分挤掉更新进度()
    {
        var item = new BangumiItem
        {
            SeasonType = 1,
            Score = "9.6",
            IndexShow = "更新至第 12 话"
        };
        Assert.Equal("更新至第 12 话", item.CardBadgeText);
    }

    [Fact]
    public void MediaSection_空栏目不算有内容()
    {
        var section = new MediaSection { Title = "电影热播" };
        Assert.False(section.HasItems);
        Assert.Equal("", section.CountText);
    }

    [Fact]
    public void MediaSection_有内容时给条数()
    {
        var section = new MediaSection
        {
            Title = "电影热播",
            Items = new[] { new BangumiItem { SeasonId = 1 } }
        };
        Assert.True(section.HasItems);
        Assert.Equal("(1)", section.CountText);
    }
}
