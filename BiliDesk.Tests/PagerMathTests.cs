using BiliDesk.Helpers;
using BiliDesk.ViewModels;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 页码分页的两条换算(2026-11-19 收藏页改页码分页时抽出)。
///
/// ★ 这两个式子最容易在"总数为 0 / 零头 / 当前页越界"上写错, 而写错的表现是
///   "最后一页永远点不到"或"第 1 页就显示成空", 都不是崩溃、很难靠肉眼发现。
/// </summary>
public class PagerMathTests
{
    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(-5, 20, 0)]
    [InlineData(1, 20, 1)]
    [InlineData(19, 20, 1)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    [InlineData(40, 20, 2)]
    // 实测的收藏夹: media_count = 1783, ps = 20 ⇒ 90 页(pn=90 正好是末页)
    [InlineData(1783, 20, 90)]
    [InlineData(1, 0, 0)]
    public void TotalPages_按总数与页大小算页数(int total, int pageSize, int expected)
        => Assert.Equal(expected, PagerMath.TotalPages(total, pageSize));

    [Theory]
    [InlineData(1, 90, 1)]
    [InlineData(90, 90, 90)]
    [InlineData(91, 90, 90)]
    [InlineData(0, 90, 1)]
    [InlineData(-3, 90, 1)]
    // 还没有页(空收藏夹 / 总数未知)时: 仍然落到第 1 页, 好让调用方真的去请求一次
    [InlineData(5, 0, 1)]
    [InlineData(5, -1, 1)]
    public void ClampPage_把页码夹进合法范围(int page, int totalPages, int expected)
        => Assert.Equal(expected, PagerMath.ClampPage(page, totalPages));

    /// <summary>
    /// 大总数不能把 "total + pageSize - 1" 撑成负数(int 溢出会导致页数直接算错)。
    /// </summary>
    [Fact]
    public void TotalPages_超大总数不溢出()
    {
        var pages = PagerMath.TotalPages(int.MaxValue, 20);
        Assert.True(pages > 0);
        Assert.Equal(int.MaxValue / 20 + 1, pages);
    }
}

/// <summary>
/// 页码栏控件的契约(2026-11-19)。
///
/// ★ 为什么需要: Views/Controls/PagerBar 绑定的是宿主 VM, 而 WPF 的绑定是**运行期按名字**
///   解析的 —— 属性改名/写错不会编译报错, 只会安静地"页码栏不显示"。这里把
///   <see cref="IPagedViewModel"/> 的成员逐个摸一遍, 至少保证两个接入分页的 VM 都齐。
/// </summary>
public class PagedViewModelContractTests
{
    [Fact]
    public void 收藏页与UP主页都满足页码栏契约()
    {
        AssertPaged(new FavoritesViewModel());
        AssertPaged(new UserSpaceViewModel());
    }

    private static void AssertPaged(IPagedViewModel vm)
    {
        Assert.NotNull(vm.PageItems);
        Assert.NotNull(vm.GoToPageCommand);
        Assert.NotNull(vm.PrevPageCommand);
        Assert.NotNull(vm.NextPageCommand);

        // 还没加载任何内容: 页码栏收起、上一页/下一页都不可点
        Assert.False(vm.ShowPager);
        Assert.False(vm.CanGoPrev);
        Assert.False(vm.CanGoNext);
        Assert.True(vm.CanChangePage);

        // 页码序列为空(还没算过)
        Assert.Empty(vm.PageItems);
    }
}
