using BiliDesk.Helpers;
using BiliDesk.ViewModels;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「返回上一级」的页面关系与来源记录(2026-11-28 用户要求: 订阅 / 历史 / 稍后再看 /
/// 收藏 / 离线缓存 / 私信 增加鼠标侧键 + Esc 返回上一级)。
///
/// 这里钉死的几条:
///   · 只有那 6 个"入口不在左侧导航栏上"的页面有上一级; 其余页面侧键 / Esc 不动作;
///   · 兜底上级与各页原来的返回按钮一致(私信 → 首页, 其余五个 → 我的);
///   · 有来源页时回来源页, 来源缺失 / 等于自己才落回兜底;
///   · MainViewModel 只在**真切页**时记来源 —— 点当前页不能把来源冲成自己,
///     否则"点一下导航项再按侧键"就退不出去了。
/// </summary>
public class PageBackPolicyTests
{
    // ---------------------------------------------------------------- 哪些页面有上一级

    [Theory]
    [InlineData(PageKey.Subscription)]
    [InlineData(PageKey.History)]
    [InlineData(PageKey.WatchLater)]
    [InlineData(PageKey.Favorites)]
    [InlineData(PageKey.Cache)]
    [InlineData(PageKey.Messages)]
    public void 六个子页面都有上一级(PageKey key) => Assert.True(PageBackPolicy.IsChildPage(key));

    [Theory]
    [InlineData(PageKey.Home)]
    [InlineData(PageKey.Follow)]
    [InlineData(PageKey.Search)]
    [InlineData(PageKey.Settings)]
    [InlineData(PageKey.Region)]
    [InlineData(PageKey.Mine)]
    public void 导航栏页面没有上一级(PageKey key) => Assert.False(PageBackPolicy.IsChildPage(key));

    // ---------------------------------------------------------------- 兜底上级

    [Theory]
    [InlineData(PageKey.Subscription)]
    [InlineData(PageKey.History)]
    [InlineData(PageKey.WatchLater)]
    [InlineData(PageKey.Favorites)]
    [InlineData(PageKey.Cache)]
    public void 我的页入口的五个页面兜底回我的(PageKey key)
        => Assert.Equal(PageKey.Mine, PageBackPolicy.DefaultParent(key));

    [Fact]
    public void 私信页兜底回首页()
        => Assert.Equal(PageKey.Home, PageBackPolicy.DefaultParent(PageKey.Messages));

    // ---------------------------------------------------------------- 解析

    [Fact]
    public void 有来源页时回来源页()
        => Assert.Equal(PageKey.Mine, PageBackPolicy.Resolve(PageKey.History, PageKey.Mine));

    [Fact]
    public void 来源页可以是任意页面()
    {
        // 以后从动态页点进观看记录也不该被送错地方 —— 来源优先于兜底
        Assert.Equal(PageKey.Follow, PageBackPolicy.Resolve(PageKey.History, PageKey.Follow));
        Assert.Equal(PageKey.Mine, PageBackPolicy.Resolve(PageKey.Messages, PageKey.Mine));
    }

    [Fact]
    public void 没有来源页时用兜底()
    {
        Assert.Equal(PageKey.Mine, PageBackPolicy.Resolve(PageKey.Favorites, null));
        Assert.Equal(PageKey.Home, PageBackPolicy.Resolve(PageKey.Messages, null));
    }

    [Fact]
    public void 来源页等于自己时用兜底()
        => Assert.Equal(PageKey.Mine, PageBackPolicy.Resolve(PageKey.Favorites, PageKey.Favorites));

    [Fact]
    public void 没有上一级的页面解析结果是自己()
    {
        // 调用方(MainWindow.TryGoBackOneLevel)靠"目标 == 当前页"判断"没得退"
        Assert.Equal(PageKey.Home, PageBackPolicy.Resolve(PageKey.Home, PageKey.Mine));
        Assert.Equal(PageKey.Settings, PageBackPolicy.Resolve(PageKey.Settings, null));
    }

    // ---------------------------------------------------------------- MainViewModel 记来源

    [Fact]
    public void 进入子页面时记下来源页()
    {
        var vm = new MainViewModel();
        vm.Navigate(PageKey.Mine);
        vm.Navigate(PageKey.History);

        Assert.Equal(PageKey.Mine, vm.OriginOf(PageKey.History));
        Assert.Equal(PageKey.Mine, vm.ResolveBackTarget(PageKey.History));
    }

    [Fact]
    public void 再次进入会用最新来源覆盖()
    {
        var vm = new MainViewModel();
        // 启动后就在首页: 从首页进私信, 来源 = 首页
        vm.Navigate(PageKey.Messages);
        Assert.Equal(PageKey.Home, vm.OriginOf(PageKey.Messages));

        // 换一条路: 首页 →「我的」→ 私信, 来源必须刷新成「我的」
        vm.Navigate(PageKey.Mine);
        vm.Navigate(PageKey.Messages);

        Assert.Equal(PageKey.Mine, vm.ResolveBackTarget(PageKey.Messages));
    }

    [Fact]
    public void 点当前页不会把来源冲成自己()
    {
        var vm = new MainViewModel();
        vm.Navigate(PageKey.Mine);
        vm.Navigate(PageKey.Favorites);

        // 再点一次"收藏"(例如重复点了快捷入口): 页面不变, 来源必须还是「我的」
        vm.Navigate(PageKey.Favorites);

        Assert.Equal(PageKey.Mine, vm.OriginOf(PageKey.Favorites));
    }

    [Fact]
    public void 导航栏页面不记来源()
    {
        var vm = new MainViewModel();
        vm.Navigate(PageKey.Home);
        vm.Navigate(PageKey.Settings);

        Assert.Null(vm.OriginOf(PageKey.Settings));
        // 没有上一级 = 解析结果是自己(侧键不动作)
        Assert.Equal(PageKey.Settings, vm.ResolveBackTarget(PageKey.Settings));
    }
}
