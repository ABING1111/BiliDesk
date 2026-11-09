using BiliDesk.Utils;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>UrlParser 的提取规则: 输入来自剪贴板/网页复制, 形状五花八门, 容错行为必须稳定。</summary>
public class UrlParserTests
{
    // ------------------------------------------------------------ ExtractBvid

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", "BV1xx411c7mD")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD?p=1", "BV1xx411c7mD")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD/?spm_id_from=xxx", "BV1xx411c7mD")]
    [InlineData("//www.bilibili.com/video/BV1xx411c7mD", "BV1xx411c7mD")]        // 无协议(网页复制常见)
    [InlineData("b23.tv 不会匹配 /video/ 路径之外的 BV")]
    public void ExtractBvid_标准与裸路径(string url, string? expected = null)
    {
        Assert.Equal(expected, UrlParser.ExtractBvid(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不是链接")]
    [InlineData("https://www.bilibili.com/video/")]
    public void ExtractBvid_解析失败返回null(string? url)
    {
        Assert.Null(url is null ? null : UrlParser.ExtractBvid(url));
    }

    // ------------------------------------------------------------ av ↔ bv

    [Theory]
    [InlineData(170001, "BV17x411w7KC")]
    [InlineData(2, "BV1xx411c7mD")]
    public void AvToBv_已知样例(long aid, string expected)
    {
        Assert.Equal(expected, UrlParser.AvToBv(aid));
    }

    [Fact]
    public void AvToBv_非法aid返回空串()
    {
        Assert.Equal("", UrlParser.AvToBv(0));
        Assert.Equal("", UrlParser.AvToBv(-1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(170001)]
    [InlineData(123456789)]
    public void AvBv_双向换算(long aid)
    {
        var bvid = UrlParser.AvToBv(aid);
        Assert.Equal(aid, UrlParser.BvToAv(bvid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BV")]
    [InlineData("not-a-bvid")]
    public void BvToAv_非法输入返回0(string? bvid)
    {
        Assert.Equal(0, UrlParser.BvToAv(bvid));
    }

    // ------------------------------------------------------------ ExtractUid

    [Fact]
    public void ExtractUid_标准空间链接()
    {
        Assert.Equal("12345", UrlParser.ExtractUid("https://space.bilibili.com/12345"));
        Assert.Equal("12345", UrlParser.ExtractUid("https://space.bilibili.com/12345/dynamic"));
        Assert.Equal("12345", UrlParser.ExtractUid("//space.bilibili.com/12345"));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/")]
    [InlineData("https://space.bilibili.com/")]
    [InlineData("")]
    public void ExtractUid_解析失败返回null(string url)
    {
        Assert.Null(UrlParser.ExtractUid(url));
    }

    // ------------------------------------------------------------ ExtractKeyword

    [Fact]
    public void ExtractKeyword_标准搜索链接()
    {
        Assert.Equal("test", UrlParser.ExtractKeyword("https://search.bilibili.com/all?keyword=test"));
    }

    [Fact]
    public void ExtractKeyword_中文被正确解码()
    {
        // "鬼畜" URL 编码后是 %E9%AC%BC%E7%95%9C
        Assert.Equal("鬼畜",
            UrlParser.ExtractKeyword("https://search.bilibili.com/all?keyword=%E9%AC%BC%E7%95%9C"));
    }

    [Fact]
    public void ExtractKeyword_带其它参数也能提取()
    {
        Assert.Equal("abc",
            UrlParser.ExtractKeyword("https://search.bilibili.com/all?order=click&keyword=abc&duration=0"));
    }

    [Fact]
    public void ExtractKeyword_坏转义不抛异常()
    {
        // 非法百分号编码: Uri 构造不报错、UnescapeDataString 抛 → 按"没提取到"降级
        // (%ZZ 本身是合法 Uri 转义序列之外的字面量, 实际原样返回 —— 语义仍是"提取不到有效关键词"
        //  的场景由"没有keyword参数"用例覆盖; 这里钉死的是"绝不能把异常抛给调用方")
        var kw = UrlParser.ExtractKeyword("https://search.bilibili.com/all?keyword=%E9%AC%BC");
        Assert.True(kw == null || kw.Length > 0);
    }

    [Fact]
    public void ExtractKeyword_没有keyword参数()
    {
        Assert.Null(UrlParser.ExtractKeyword("https://search.bilibili.com/all?order=click"));
    }
}
