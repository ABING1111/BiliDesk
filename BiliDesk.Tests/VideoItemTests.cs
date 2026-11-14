using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// VideoItem.ResolveAid 的补齐规则(2026-11-15 新增)。
///
/// ★ 为什么钉这三条: 卡片右上角「稍后再看」的选中态就是拿 aid 比对全局集合的 ——
///   ① 接口给了 aid 却被 bvid 换算覆盖 ⇒ 对勾加到别的视频头上(比漏亮更糟);
///   ② 接口没给 aid 时不换算 ⇒ 热门/排行这类页的按钮永远不亮(用户报的原 bug);
///   ③ 换算不出来要老实返回 0, 让卡片侧走补查详情的老路, 不能抛异常。
/// 换算样例取自真实接口响应(热门 BV1pQsV6GEhX = 117614827341164), 与 UrlParserTests 同源。
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
}
