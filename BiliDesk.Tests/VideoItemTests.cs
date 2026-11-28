using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// VideoItem 的纯函数口径(2026-11-15 起)。
///
/// ★ 为什么钉这三条: 卡片右上角「稍后再看」的选中态就是拿 aid 比对全局集合的 ——
///   ① 接口给了 aid 却被 bvid 换算覆盖 ⇒ 对勾加到别的视频头上(比漏亮更糟);
///   ② 接口没给 aid 时不换算 ⇒ 热门/排行这类页的按钮永远不亮(用户报的原 bug);
///   ③ 换算不出来要老实返回 0, 让卡片侧走补查详情的老路, 不能抛异常。
/// 换算样例取自真实接口响应(热门 BV1pQsV6GEhX = 117614827341164), 与 UrlParserTests 同源。
///
/// ★ 2026-11-28 追加历史卡片的两种展示口径: 观看时间(今天/昨天/几月几日)与观看进度
///   (进度条比例 + "已看 / 总长"角标)。它们全站共用一个 VideoCard, 判定写错就是
///   首页/搜索的卡片跟着变样, 所以边界(0 / -1 / 超出总长 / 时长缺失)都在这里钉死。
/// </summary>
public class VideoItemTests
{
    [Theory]
    [InlineData(117614827341164, "BV1pQsV6GEhX")]
    [InlineData(2, "BV17x411w7KC")]   // 接口的 aid 优先, 即使 bvid 对不上另一个视频也不覆盖
    public void ResolveAid_接口给了aid时原样返回(long aid, string bvid)
    {
        Assert.Equal(aid, VideoItem.ResolveAid(aid, bvid));
    }

    [Theory]
    [InlineData("BV1pQsV6GEhX", 117614827341164)]   // 热门接口实测样例
    [InlineData("BV17x411w7KC", 170001)]            // 官方文档样例
    [InlineData("BV1xx411c7mD", 2)]
    public void ResolveAid_没给aid时按bvid本地换算(string bvid, long expected)
    {
        Assert.Equal(expected, VideoItem.ResolveAid(0, bvid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-bvid")]
    [InlineData("BV")]
    public void ResolveAid_换算不出来返回0且不抛异常(string? bvid)
    {
        Assert.Equal(0, VideoItem.ResolveAid(0, bvid));
    }

    // ------------------------------------------------------------ 历史卡片的观看时间 / 进度(2026-11-28)

    /// <summary>本机时区的 DateTime → Unix 秒(与 VideoItem 里的折算方向相反, 两边都用本地时区)</summary>
    private static long Ts(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

    [Fact]
    public void FormatViewTime_今天只到时分()
    {
        var t = DateTime.Today.AddHours(14).AddMinutes(30);
        Assert.Equal("今天 14:30", VideoItem.FormatViewTime(Ts(t)));
    }

    [Fact]
    public void FormatViewTime_昨天带日期前缀()
    {
        var t = DateTime.Today.AddDays(-1).AddHours(21).AddMinutes(5);
        Assert.Equal("昨天 21:05", VideoItem.FormatViewTime(Ts(t)));
    }

    [Fact]
    public void FormatViewTime_更早显示几月几日几点几分()
    {
        // ★ 用"今天 - 3 天"而不是写死日期: 硬编码的某一天会在跨年后变成"去年", 断言就跟着挂
        var t = DateTime.Today.AddDays(-3).AddHours(8).AddMinutes(7);
        Assert.Equal($"{t.Month}月{t.Day}日 08:07", VideoItem.FormatViewTime(Ts(t)));
    }

    [Fact]
    public void FormatViewTime_跨年用短横线日期()
    {
        // ★ 不用"2024年11月20日": 卡片最窄 220 时, 那一串 128.7px 会把 UP 名挤掉半截
        //   (见 VideoItem.FormatViewTime 的实测说明), 短横线版只占 104px。
        var t = new DateTime(DateTime.Today.Year - 2, 11, 20, 18, 40, 0, DateTimeKind.Local);
        Assert.Equal($"{t:yyyy-MM-dd} 18:40", VideoItem.FormatViewTime(Ts(t)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void FormatViewTime_没有时间戳时为空(long viewAt)
        => Assert.Equal("", VideoItem.FormatViewTime(viewAt));

    [Fact]
    public void 有进度时角标是已看比总长()
    {
        var item = new VideoItem { Duration = VideoItem.FormatSeconds(81), DurationSec = 81, ProgressSec = 80 };

        Assert.Equal("1:20 / 1:21", item.DurationBadgeText);
        Assert.True(item.ShowProgressBar);
        Assert.Equal(80d / 81d, item.ProgressRatio, 6);
    }

    [Theory]
    [InlineData(0, 600)]     // 没看过(接口给 0)
    [InlineData(-1, 600)]    // -1 = 已看完: 不长满格进度条
    [InlineData(600, 600)]   // 已看到片尾
    [InlineData(700, 600)]   // 脏数据(进度超过总长)
    [InlineData(120, 0)]     // 时长缺失
    public void 没进度时不画进度条且角标退回总时长(int progressSec, int durationSec)
    {
        var durationText = VideoItem.FormatSeconds(durationSec);
        var item = new VideoItem { Duration = durationText, DurationSec = durationSec, ProgressSec = progressSec };

        Assert.Equal(0, item.ProgressRatio);
        Assert.False(item.ShowProgressBar);
        Assert.Equal(durationText, item.DurationBadgeText);
    }

    [Fact]
    public void 有观看时间时不再重复显示日期()
    {
        var history = new VideoItem
        {
            Pubdate = Ts(DateTime.Today.AddHours(9)),
            ViewTimeText = VideoItem.FormatViewTime(Ts(DateTime.Today.AddHours(9)))
        };
        // 历史: Pubdate 就是观看时间, 第二行右端已经说了"今天 09:00", 日期那格让出来
        Assert.Equal("", history.CardDateText);

        var home = new VideoItem { Pubdate = Ts(DateTime.Today.AddHours(9)) };
        Assert.Equal(home.PubDateShort, home.CardDateText);
        Assert.False(home.HasViewTime);
    }
}
