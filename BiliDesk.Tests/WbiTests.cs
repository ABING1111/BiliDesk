using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// WBI mixin key(2026-11-13 修复)。
///
/// ★ 为什么单独钉一个测试: 这里曾经漏了"取前 32 位"的截断, 所有签名多带 32 个字符。
///   由于多数 wbi 接口软校验 w_rid, 这个错误在正常功能里**完全看不出来**;
///   直到直播弹幕的 getDanmuInfo 严格校验才暴露(-352)。
///   用 B 站前端固定的一组 img/sub → mixin 向量, 以后任何人改这段算法都会立刻失败。
/// </summary>
public class WbiTests
{
    // B 站前端长期使用的固定向量(公开文档/前端 JS 里都是这组值, 不是任何账号的凭据)
    private const string ImgKey = "7cd084941338484aae1ad9425b84077c";
    private const string SubKey = "4932caff0ff746eab6f01bf08b70ac45";
    private const string MixinKey = "ea1db124af3c7062474693fa704f4ff8";

    [Fact]
    public void MixinKey_官方向量_必须恰好32位()
    {
        var actual = Wbi.MixinKey(ImgKey, SubKey);

        Assert.Equal(MixinKey, actual);
        Assert.Equal(32, actual.Length);
    }

    [Theory]
    [InlineData(null, SubKey)]
    [InlineData(ImgKey, null)]
    [InlineData("", "")]
    public void MixinKey_缺少输入返回空串(string? img, string? sub)
    {
        Assert.Equal("", Wbi.MixinKey(img!, sub!));
    }

    [Fact]
    public void MixinKey_文件名过短时不抛异常()
    {
        var actual = Wbi.MixinKey("abc", "def");

        Assert.Equal(32, actual.Length);
    }
}
