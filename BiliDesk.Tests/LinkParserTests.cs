using System.Linq;
using BiliDesk.Utils;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 正文链接识别(评论区 / 简介 / 图文正文共用)。
///
/// ★ 关键不变量:
///   ① 只认 http/https(以及裸写的 B 站域名), javascript:/file: 绝不能变成可点入口;
///   ② 链接后面紧跟中文/标点时必须**截断**, 不能把整句中文染成链接;
///   ③ 邮箱里的 @ 不能让链接切分错位;
///   ④ 切分结果拼回去必须与原文**逐字相同**(渲染层不允许丢字)。
/// </summary>
public class LinkParserTests
{
    private static string RoundTrip(string text)
        => string.Concat(LinkParser.Split(text).Select(s => s.Text));

    // ------------------------------------------------------------ 认哪些形状

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", "https://www.bilibili.com/video/BV1xx411c7mD")]
    [InlineData("http://example.com/a", "http://example.com/a")]
    [InlineData("//www.bilibili.com/video/BV1xx411c7mD", "https://www.bilibili.com/video/BV1xx411c7mD")]
    [InlineData("www.example.com/path", "https://www.example.com/path")]
    [InlineData("b23.tv/abc123", "https://b23.tv/abc123")]
    [InlineData("bilibili.com/video/BV1xx411c7mD", "https://bilibili.com/video/BV1xx411c7mD")]
    [InlineData("space.bilibili.com/12345", "https://space.bilibili.com/12345")]
    [InlineData("HTTPS://Example.COM/A", "HTTPS://Example.COM/A")]
    public void 识别完整链接并补全协议(string input, string expectedUrl)
    {
        var segs = LinkParser.Split(input);
        var link = Assert.Single(segs);
        Assert.True(link.IsLink);
        Assert.Equal(expectedUrl, link.Url);
        Assert.Equal(input, link.Text);
    }

    [Fact]
    public void 中文后面直接跟链接也能认()
    {
        var segs = LinkParser.Split("链接https://b23.tv/abc看看");
        Assert.Equal(3, segs.Count);
        Assert.False(segs[0].IsLink);
        Assert.Equal("链接", segs[0].Text);
        Assert.True(segs[1].IsLink);
        Assert.Equal("https://b23.tv/abc", segs[1].Url);
        Assert.False(segs[2].IsLink);
        Assert.Equal("看看", segs[2].Text);
    }

    // ------------------------------------------------------------ 边界

    [Theory]
    [InlineData("看这个 https://b23.tv/abc。", "https://b23.tv/abc")]
    [InlineData("看这个 https://b23.tv/abc，真的", "https://b23.tv/abc")]
    [InlineData("(https://b23.tv/abc)", "https://b23.tv/abc")]
    [InlineData("https://b23.tv/abc!!!", "https://b23.tv/abc")]
    [InlineData("https://b23.tv/abc?t=1", "https://b23.tv/abc?t=1")]
    public void 末尾标点与多余括号被剪掉(string input, string expectedUrl)
    {
        var link = LinkParser.Split(input).First(s => s.IsLink);
        Assert.Equal(expectedUrl, link.Url);
    }

    [Fact]
    public void 中文标点处截断不吞后面的句子()
    {
        var segs = LinkParser.Split("地址https://b23.tv/abc，然后再说别的");
        var link = Assert.Single(segs, s => s.IsLink);
        Assert.Equal("https://b23.tv/abc", link.Text);
        Assert.Contains(segs, s => !s.IsLink && s.Text.Contains("然后再说别的"));
    }

    [Fact]
    public void 括号成对时保留()
    {
        var link = LinkParser.Split("https://zh.wikipedia.org/wiki/Foo_(bar)").First(s => s.IsLink);
        Assert.Equal("https://zh.wikipedia.org/wiki/Foo_(bar)", link.Url);
    }

    [Fact]
    public void 链接后面紧跟表情短代码时不吃掉它()
    {
        // B 站表情短代码是 "[doge]" 这种形状: 方括号必须当终止符,
        // 否则表情会被吞进链接, 表情图片消失、链接也变成坏的。
        var segs = LinkParser.Split("看 https://b23.tv/abc[doge] 这个");
        var link = Assert.Single(segs, s => s.IsLink);
        Assert.Equal("https://b23.tv/abc", link.Text);
        Assert.Contains(segs, s => !s.IsLink && s.Text.Contains("[doge]"));
    }

    [Fact]
    public void 链接带查询串里的方括号不会被误截()
    {
        // 少数地址确实带 [] (如某些 CDN 的参数): 宁可少认也不能把整条链接切坏 ——
        // 这里钉住的是"截断点选在方括号之前", 即链接到 [ 为止, 剩下的当普通文字。
        var segs = LinkParser.Split("https://a.com/x?a=1[b]");
        var link = Assert.Single(segs, s => s.IsLink);
        Assert.Equal("https://a.com/x?a=1", link.Url);
    }

    // ------------------------------------------------------------ 不该认的

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/a")]
    [InlineData("file:///C:/Windows")]
    [InlineData("这不是链接")]
    [InlineData("文件名.txt")]
    [InlineData("someone@example.com")]
    [InlineData("a@b.c")]
    [InlineData("1.2.3")]
    public void 非网页链接不识别(string input)
    {
        var segs = LinkParser.Split(input);
        Assert.All(segs, s => Assert.False(s.IsLink));
    }

    [Fact]
    public void 邮箱不会把后面的文本连成链接()
    {
        var segs = LinkParser.Split("联系 someone@example.com 谢谢");
        Assert.All(segs, s => Assert.False(s.IsLink));
        Assert.Equal("联系 someone@example.com 谢谢", RoundTrip("联系 someone@example.com 谢谢"));
    }

    // ------------------------------------------------------------ 切分完整性

    [Theory]
    [InlineData("")]
    [InlineData("纯文字没有链接")]
    [InlineData("两个链接 https://a.com/x 和 https://b.com/y 结束")]
    [InlineData("表情[doge]和链接https://b23.tv/x混排")]
    [InlineData("多行\n文本 https://b23.tv/x\n第二行")]
    public void 切分后拼回原文一字不差(string input)
    {
        Assert.Equal(input, RoundTrip(input));
    }

    [Fact]
    public void 一段里的多个链接都识别出来()
    {
        var segs = LinkParser.Split("https://a.com/x 和 https://b.com/y");
        var links = segs.Where(s => s.IsLink).ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("https://a.com/x", links[0].Url);
        Assert.Equal("https://b.com/y", links[1].Url);
    }

    // ------------------------------------------------------------ MightContainLink

    [Theory]
    [InlineData("https://x.com", true)]
    [InlineData("看 b23.tv/abc", true)]
    [InlineData("www.example.com", true)]
    [InlineData("纯文字", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 便宜判断只做粗筛(string? input, bool expected)
    {
        Assert.Equal(expected, LinkParser.MightContainLink(input));
    }

    // ------------------------------------------------------------ TryGetUrl

    [Fact]
    public void 整串是链接时能取出地址()
    {
        Assert.True(LinkParser.TryGetUrl("https://b23.tv/abc", out var url));
        Assert.Equal("https://b23.tv/abc", url);
    }

    [Fact]
    public void 夹着别的文字时不算整串链接()
    {
        Assert.False(LinkParser.TryGetUrl("看 https://b23.tv/abc", out _));
        Assert.False(LinkParser.TryGetUrl("纯文字", out _));
    }
}
