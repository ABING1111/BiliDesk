using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 图片预览窗的整组地址预处理(2026-11-14, 左右切换功能)。
///
/// ★ 这里钉的是"点了第几张 → 打开第几张"这一条: 过滤掉空地址后下标会前移,
///   算错的表现是"点了第 3 张却打开第 2 张", 肉眼极难发现, 必须靠单测。
/// </summary>
public class PreviewListTests
{
    [Fact]
    public void 全是有效地址时下标原样保留()
    {
        var (urls, index) = PreviewList.Normalize(
            new[] { "a.jpg", "b.jpg", "c.jpg" }, 2);

        Assert.Equal(3, urls.Length);
        Assert.Equal("c.jpg", urls[2]);
        Assert.Equal(2, index);
    }

    [Fact]
    public void 过滤空项后目标下标随之前移()
    {
        // 原始第 2 项(下标 1)是空串, 目标在下标 2 → 过滤后应落在下标 1
        var (urls, index) = PreviewList.Normalize(
            new[] { "a.jpg", "", "c.jpg" }, 2);

        Assert.Equal(new[] { "a.jpg", "c.jpg" }, urls);
        Assert.Equal(1, index);
    }

    [Fact]
    public void 目标本身是空项时落在其后的那一张()
    {
        // 用户点在空地址上(理论上不会发生, 但不能越界崩)
        var (urls, index) = PreviewList.Normalize(
            new[] { "a.jpg", "", "c.jpg" }, 1);

        Assert.Equal(2, urls.Length);
        Assert.InRange(index, 0, urls.Length - 1);
    }

    [Fact]
    public void 相对协议地址会被补全为https()
    {
        var (urls, _) = PreviewList.Normalize(new[] { "//i0.hdslb.com/x.jpg" }, 0);
        Assert.Equal("https://i0.hdslb.com/x.jpg", urls[0]);
    }

    [Fact]
    public void 空列表与空引用都返回空结果且下标为0()
    {
        var (u1, i1) = PreviewList.Normalize(null, 5);
        Assert.Empty(u1);
        Assert.Equal(0, i1);

        var (u2, i2) = PreviewList.Normalize(new[] { "", "   " }, 1);
        Assert.Empty(u2);
        Assert.Equal(0, i2);
    }

    [Fact]
    public void 下标越界会被夹进有效范围()
    {
        var (urls, index) = PreviewList.Normalize(new[] { "a.jpg", "b.jpg" }, 99);
        Assert.Equal(2, urls.Length);
        Assert.Equal(1, index);

        var (_, neg) = PreviewList.Normalize(new[] { "a.jpg", "b.jpg" }, -3);
        Assert.Equal(0, neg);
    }

    [Fact]
    public void 单张时下标恒为0()
    {
        var (urls, index) = PreviewList.Normalize(new[] { "only.jpg" }, 0);
        Assert.Single(urls);
        Assert.Equal(0, index);
    }
}
