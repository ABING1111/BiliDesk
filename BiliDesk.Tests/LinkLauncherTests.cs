using System;
using System.Collections.Generic;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 链接 → 内置浏览器 的入口约束。
///
/// ★ 背景(2026-11-14 用户报"网页没加载出来时点任意位置/关闭窗口会不断开新窗口"):
///   根因是来源控件留下了全局鼠标捕获(已修), 这里钉住第二道防线 ——
///   **同一地址的重复请求必须在入口处挡掉**, 这样即使别处又漏了捕获,
///   用户也不会看到窗口成排弹出来。
/// </summary>
public class LinkLauncherTests
{
    // ------------------------------------------------------------ 安全边界

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", true)]
    [InlineData("http://example.com", true)]
    [InlineData("HTTPS://Example.COM/A", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("ftp://example.com/a", false)]
    [InlineData("", false)]
    [InlineData("不是链接", false)]
    public void 只放行http与https(string url, bool expected)
    {
        Assert.Equal(expected, LinkLauncher.IsWebUrl(url));
    }

    // ------------------------------------------------------------ 同地址去重

    [Fact]
    public void 同一地址在时间窗内只算一次()
    {
        var url = "https://b23.tv/dedup-same";
        Assert.False(LinkLauncher.IsDuplicate(url));   // 第一次: 放行
        Assert.True(LinkLauncher.IsDuplicate(url));    // 紧接着: 挡掉
        Assert.True(LinkLauncher.IsDuplicate(url));    // 仍然挡
    }

    [Fact]
    public void 不同地址互不影响()
    {
        var a = "https://b23.tv/dedup-a";
        var b = "https://b23.tv/dedup-b";
        Assert.False(LinkLauncher.IsDuplicate(a));
        Assert.False(LinkLauncher.IsDuplicate(b));
        Assert.True(LinkLauncher.IsDuplicate(a));
        Assert.True(LinkLauncher.IsDuplicate(b));
    }

    [Fact]
    public void 地址大小写不同视为同一个()
    {
        Assert.False(LinkLauncher.IsDuplicate("https://EXAMPLE.com/x"));
        Assert.True(LinkLauncher.IsDuplicate("https://example.com/x"));
    }

    // ------------------------------------------------------------ "这个链接已经开着了"

    [Fact]
    public void 已有同地址窗口时不再开新窗口()
    {
        // ★ 这是连环弹窗真正的拦路闸: 不管触发多少次、间隔多长,
        //   只要那个地址的窗口还开着, 就不该再开第二个。
        var open = new List<(string Url, bool Closed)>
        {
            ("https://b23.tv/opened-once", false),
        };

        Assert.True(LinkLauncher.AnyOpenMatch(open, "https://b23.tv/opened-once"));
        // 大小写不同也算同一个地址
        Assert.True(LinkLauncher.AnyOpenMatch(open, "HTTPS://B23.TV/OPENED-ONCE"));
        // 别的地址不受影响
        Assert.False(LinkLauncher.AnyOpenMatch(open, "https://b23.tv/other"));
    }

    [Fact]
    public void 窗口已关闭后不再算作开着()
    {
        // ★ 不区分"已关闭"的话, 用户关掉窗口后再点同一个链接会永远打不开。
        var closing = new List<(string Url, bool Closed)>
        {
            ("https://b23.tv/closed", true),
        };
        Assert.False(LinkLauncher.AnyOpenMatch(closing, "https://b23.tv/closed"));
    }

    [Fact]
    public void 没有窗口时不会误判()
    {
        Assert.False(LinkLauncher.AnyOpenMatch(Array.Empty<(string, bool)>(), "https://b23.tv/x"));
    }
}
