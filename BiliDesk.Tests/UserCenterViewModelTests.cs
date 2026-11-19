using System.Collections.Generic;
using System.Reflection;
using BiliDesk.ViewModels;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「我的空间」ViewModel 的状态通知与 Tab 切换(2026-11-18, issue #90)。
///
/// ★ 这个测试盯的是**属性通知**而不是业务逻辑 ——
///   WPF 不会自动重算计算属性(FollowingsEmpty / SummaryText …),
///   少通知一个, 界面上就会留下"列表已经有内容了, 但空态提示还挂着"这类残骸。
///   这类 bug 靠肉眼点很难稳定复现, 用测试钉死最省事。
///
/// ★★★ 断言的对象必须与 UserCenterPage.xaml 里的绑定**逐一对应**。
///   2026-11-19 的教训: 这里原来断言的是 TabEmpty / TabLoading / TabErrorOverlay ——
///   三个聚合属性, 页面一个都没绑(它绑的是每个 Tab 自己的 ...Empty)。测试全绿,
///   界面却在切 Tab 后一直挂着"还没有关注任何人"。断言一个没人绑的属性等于没断言。
/// </summary>
public class UserCenterViewModelTests
{
    /// <summary>用反射把私有 setter 的属性写进去(照 UserSpaceViewModelTests 的写法)。</summary>
    private static void SetProp<T>(object vm, string name, T value)
        => typeof(UserCenterViewModel)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(vm, value);

    [Fact]
    public void 切Tab_三个互斥标记都要通知()
    {
        var vm = new UserCenterViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.SetInitialTab(UserCenterTab.Followings);

        Assert.Contains(nameof(UserCenterViewModel.Tab), changed);
        Assert.Contains(nameof(UserCenterViewModel.IsDynamicsTab), changed);
        Assert.Contains(nameof(UserCenterViewModel.IsFollowingsTab), changed);
        Assert.Contains(nameof(UserCenterViewModel.IsFollowersTab), changed);

        Assert.False(vm.IsDynamicsTab);
        Assert.True(vm.IsFollowingsTab);
        Assert.False(vm.IsFollowersTab);
    }

    [Fact]
    public void 切Tab_标题与概要要跟着通知()
    {
        var vm = new UserCenterViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.SetInitialTab(UserCenterTab.Followers);

        // 这两个是计算属性: 不通知的话界面上会一直显示上一个 Tab 的标题/概要
        Assert.Contains(nameof(UserCenterViewModel.TitleText), changed);
        Assert.Contains(nameof(UserCenterViewModel.SummaryText), changed);
    }

    [Fact]
    public void 切到同一个Tab_不重复通知()
    {
        var vm = new UserCenterViewModel();
        vm.SetInitialTab(UserCenterTab.Followers);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.SetInitialTab(UserCenterTab.Followers);

        Assert.Empty(changed);
    }

    [Fact]
    public void 列表变化时_空态与概要要通知()
    {
        var vm = new UserCenterViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.Followings.Add(new BiliDesk.Models.FollowUser { Mid = 1, Name = "a" });

        // ★ 页面绑的就是这个 FollowingsEmpty: 集合变了不通知它 ⇒
        //   "还没有关注任何人"会一直压在已经加载出来的列表上(用户 2026-11-19 报的 bug)。
        Assert.Contains(nameof(UserCenterViewModel.FollowingsEmpty), changed);
        Assert.Contains(nameof(UserCenterViewModel.FollowingsSummary), changed);
        Assert.Contains(nameof(UserCenterViewModel.SummaryText), changed);
    }

    [Fact]
    public void 粉丝列表变化时_粉丝自己的空态要通知()
    {
        var vm = new UserCenterViewModel();
        vm.SetInitialTab(UserCenterTab.Followers);
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.Followers.Add(new BiliDesk.Models.FollowUser { Mid = 2, Name = "b" });

        // 「切到粉丝 Tab, 列表有内容, 上面还挂着还没有粉丝」—— 差的就是这一条通知
        Assert.Contains(nameof(UserCenterViewModel.FollowersEmpty), changed);
        Assert.Contains(nameof(UserCenterViewModel.FollowersSummary), changed);
    }

    [Fact]
    public void 切换Tab后_该Tab的空态标记要跟着重算通知()
    {
        var vm = new UserCenterViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        _ = vm.SwitchTabAsync(UserCenterTab.Followers);

        // 换 Tab 等于"要显示的到底是哪一句空态"整个变了
        Assert.Contains(nameof(UserCenterViewModel.DynamicsEmpty), changed);
        Assert.Contains(nameof(UserCenterViewModel.FollowingsEmpty), changed);
        Assert.Contains(nameof(UserCenterViewModel.FollowersEmpty), changed);
    }

    [Fact]
    public void 未登录时_空态成立()
    {
        var vm = new UserCenterViewModel();
        // 没有任何数据、没在加载、没报错 ⇒ 三个 Tab 的"空"都成立
        Assert.True(vm.DynamicsEmpty);
        Assert.True(vm.FollowingsEmpty);
        Assert.True(vm.FollowersEmpty);
    }

    [Fact]
    public void 有报错且列表为空时_错误遮罩成立()
    {
        var vm = new UserCenterViewModel();
        // ★ 先切到「粉丝」Tab: 错误遮罩是**按当前 Tab** 判定的 ——
        //   给别的 Tab 报错时, 当前 Tab 不该被盖上错误遮罩。
        vm.SetInitialTab(UserCenterTab.Followers);
        SetProp(vm, nameof(UserCenterViewModel.FollowersError), "接口挂了");

        Assert.True(vm.ShowFollowersError);
        // 有错误就不该再显示"还没有粉丝"那条空态
        Assert.False(vm.FollowersEmpty);
    }

    [Fact]
    public void 别的Tab报错时_当前Tab不受影响()
    {
        var vm = new UserCenterViewModel();
        // 当前在「动态」Tab, 但「粉丝」那个 Tab 有错 —— 当前 Tab 不该被挡住
        SetProp(vm, nameof(UserCenterViewModel.FollowersError), "接口挂了");

        Assert.False(vm.ShowDynamicsError);
        Assert.True(vm.DynamicsEmpty);
    }

    [Fact]
    public void 标题_看别人时带名字()
    {
        var vm = new UserCenterViewModel();
        SetProp(vm, nameof(UserCenterViewModel.Name), "某某UP");

        Assert.Equal("某某UP 的空间", vm.TitleText);
    }

    [Fact]
    public void 标题_没有名字时回落到我的空间()
    {
        var vm = new UserCenterViewModel();
        Assert.Equal("我的空间", vm.TitleText);
    }

    [Fact]
    public void 概要_关注与粉丝各带自己的标签()
    {
        var vm = new UserCenterViewModel();
        vm.SetInitialTab(UserCenterTab.Followings);
        Assert.StartsWith("关注", vm.SummaryText);

        vm.SetInitialTab(UserCenterTab.Followers);
        Assert.StartsWith("粉丝", vm.SummaryText);
    }
}
