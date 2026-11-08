using System.Collections.Generic;
using System.Reflection;
using BiliDesk.ViewModels;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// UP 主主页等级徽章的刷新链路(2026-11-13)。
///
/// ★ 这是一个**只靠接口探针发现不了**的 bug: GetSpaceInfoAsync 明明返回 level=6,
///   页面上却一直显示 LV0。原因是 ViewModel 只通知了 HasLevel(控制徽章显隐),
///   没有通知 LevelText(徽章文字), WPF 就一直用初始化时的 "LV0"。
///   本测试把"值变化后三个属性都要通知"钉死。
/// </summary>
public class UserSpaceViewModelTests
{
    private static void SetLevel(UserSpaceViewModel vm, int level)
        => typeof(UserSpaceViewModel)
            .GetProperty(nameof(UserSpaceViewModel.Level), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(vm, level);

    [Fact]
    public void 等级变化后_LevelText与HasLevel都要通知()
    {
        var vm = new UserSpaceViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        SetLevel(vm, 6);

        Assert.Contains(nameof(UserSpaceViewModel.Level), changed);
        Assert.Contains(nameof(UserSpaceViewModel.HasLevel), changed);
        Assert.Contains(nameof(UserSpaceViewModel.LevelText), changed);
        Assert.Equal("LV6", vm.LevelText);
        Assert.True(vm.HasLevel);
    }

    [Fact]
    public void 等级为零时不显示徽章()
    {
        var vm = new UserSpaceViewModel();
        SetLevel(vm, 0);

        Assert.False(vm.HasLevel);
        Assert.Equal("LV0", vm.LevelText);
    }
}
