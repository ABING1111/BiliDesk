using System.Collections.Generic;
using System.Reflection;
using BiliDesk.ViewModels;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「我的」页资料头的两个派生属性(2026-11-28 卡片化时补的测试)。
///
/// ★ 等级徽章: 和 UserSpaceViewModel 当年那个坑同源 —— 徽章的 Text/Visibility 绑的是
///   LevelText, 资料却是在页面 Loaded 之后才到。只通知 Level 的话, 徽章永远是初始的空串,
///   用户看到的是"昵称右边一个空的粉色小方块"。本测试把 LevelText 的通知钉死。
/// ★ 经验百分比: 进度条改成满信息列宽后, 右端那行 "94%" 由 ExpPercentText 提供;
///   没有经验数据(ExpText 为空)时必须给空串, 让整行跟着隐藏。
/// </summary>
public class MineViewModelTests
{
    private static void Set<T>(MineViewModel vm, string name, T value)
        => typeof(MineViewModel)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(vm, value);

    [Fact]
    public void 等级变化后_LevelText要通知()
    {
        var vm = new MineViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        Set(vm, nameof(MineViewModel.Level), 6);

        Assert.Contains(nameof(MineViewModel.Level), changed);
        Assert.Contains(nameof(MineViewModel.LevelText), changed);
        Assert.Equal("LV6", vm.LevelText);
    }

    [Fact]
    public void 经验百分比_按进度四舍五入()
    {
        var vm = new MineViewModel();
        Set(vm, nameof(MineViewModel.ExpText), "27136/28800");
        Set(vm, nameof(MineViewModel.ExpProgress), 0.9422);

        Assert.Equal("94%", vm.ExpPercentText);
    }

    [Fact]
    public void 没有经验数据时_百分比为空()
    {
        var vm = new MineViewModel();
        Set(vm, nameof(MineViewModel.ExpProgress), 0.5);

        Assert.Equal("", vm.ExpPercentText);
    }
}
