using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// UP 主页「合集和系列」卡片的数据展示规则。
///
/// ★ 合集接口的 meta.name 自带「合集·」前缀, 系列接口通常只有裸名字 ——
///   DisplayTitle 必须两种都认, 否则会出现「合集·合集·xxx」。
/// </summary>
public class SpaceCollectionTests
{
    [Fact]
    public void 合集名字已带前缀时_不再重复拼()
    {
        var item = new SpaceCollectionItem
        {
            Kind = SpaceCollectionKind.Season,
            Title = "合集·样片日记"
        };

        Assert.Equal("合集·样片日记", item.DisplayTitle);
    }

    [Fact]
    public void 合集名字没有前缀时_补上合集前缀()
    {
        var item = new SpaceCollectionItem
        {
            Kind = SpaceCollectionKind.Season,
            Title = "样片日记"
        };

        Assert.Equal("合集·样片日记", item.DisplayTitle);
    }

    [Fact]
    public void 系列名字补系列前缀()
    {
        var item = new SpaceCollectionItem
        {
            Kind = SpaceCollectionKind.Series,
            Title = "直播回放"
        };

        Assert.Equal("系列·直播回放", item.DisplayTitle);
    }

    [Fact]
    public void 系列名字已带前缀时_不再重复拼()
    {
        var item = new SpaceCollectionItem
        {
            Kind = SpaceCollectionKind.Series,
            Title = "系列·直播回放"
        };

        Assert.Equal("系列·直播回放", item.DisplayTitle);
    }

    [Fact]
    public void 空名字时_只显示类型名()
    {
        var item = new SpaceCollectionItem { Kind = SpaceCollectionKind.Season, Title = "" };

        Assert.Equal("合集", item.DisplayTitle);
    }

    [Fact]
    public void 内容数与类型文案()
    {
        var season = new SpaceCollectionItem
        {
            Kind = SpaceCollectionKind.Season,
            SeasonId = 2046621,
            Count = 26
        };
        var series = new SpaceCollectionItem
        {
            Kind = SpaceCollectionKind.Series,
            SeriesId = 30574,
            Count = 8
        };

        Assert.Equal("26个内容", season.CountText);
        Assert.Equal("合集", season.KindText);
        Assert.Equal(2046621, season.Id);

        Assert.Equal("8个内容", series.CountText);
        Assert.Equal("系列", series.KindText);
        Assert.Equal(30574, series.Id);
    }
}
