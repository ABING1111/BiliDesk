using System.Linq;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// UP 主主页投稿页码栏的序列生成(2026-11-08)。
/// 省略号用 <see cref="PageBar.Ellipsis"/>(0) 表示。
/// </summary>
public class PaginationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Build_总页数为零或负数_返回空(int total)
    {
        Assert.Empty(PageBar.Build(1, total));
    }

    [Fact]
    public void Build_一页()
    {
        Assert.Equal(new[] { 1 }, PageBar.Build(1, 1));
    }

    [Fact]
    public void Build_页数不多时全部列出()
    {
        Assert.Equal(Enumerable.Range(1, 9), PageBar.Build(5, 9));
    }

    [Fact]
    public void Build_第一页_首段加末页()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, PageBar.Ellipsis, 33 },
            PageBar.Build(1, 33));
    }

    [Fact]
    public void Build_最后一页_首页加尾段()
    {
        Assert.Equal(new[] { 1, PageBar.Ellipsis, 27, 28, 29, 30, 31, 32, 33 },
            PageBar.Build(33, 33));
    }

    [Fact]
    public void Build_中间页_首页与末页始终可见()
    {
        Assert.Equal(new[] { 1, PageBar.Ellipsis, 15, 16, 17, 18, 19, PageBar.Ellipsis, 33 },
            PageBar.Build(17, 33));
    }

    [Theory]
    [InlineData(0, new[] { 1, 2, 3, 4, 5, 6, 7, 0, 33 })]
    [InlineData(99, new[] { 1, 0, 27, 28, 29, 30, 31, 32, 33 })]
    public void Build_当前页越界会被夹到合法范围(int current, int[] expected)
    {
        Assert.Equal(expected, PageBar.Build(current, 33));
    }

    [Fact]
    public void Build_自定义槽位()
    {
        // maxSlots=5: 首段 1..5 … N
        Assert.Equal(new[] { 1, 2, 3, 4, 5, PageBar.Ellipsis, 20 },
            PageBar.Build(2, 20, maxSlots: 5));
    }
}
