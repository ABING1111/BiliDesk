using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 性能修复的契约测试(2026-11-22, 性能审查 #1/#4/#5)。
/// 只钉住"去重语义不变""通知批次数不变"这类肉眼点不出来的回归。
/// </summary>
public class PerformanceFixTests
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void 番剧推荐批量去重_按SeasonId保留第一条且顺序不变()
    {
        var method = typeof(HomeViewModel).GetMethod("AddDistinct", StaticPrivate,
            new[] { typeof(ObservableCollection<BangumiItem>), typeof(IReadOnlyList<BangumiItem>) });
        Assert.NotNull(method);

        var first = new BangumiItem { SeasonId = 10, Title = "first" };
        var target = new ObservableCollection<BangumiItem> { first };
        var incoming = new List<BangumiItem>
        {
            new() { SeasonId = 10, Title = "dup" },
            new() { SeasonId = 20, Title = "second" },
            new() { SeasonId = 20, Title = "dup2" },
        };

        method!.Invoke(null, new object[] { target, incoming });

        Assert.Equal(2, target.Count);
        Assert.Same(first, target[0]);
        Assert.Equal("second", target[1].Title);
    }

    [Fact]
    public void 动态ID新旧比较_数字与非数字语义不变()
    {
        Assert.True(IdCompare.IsNewer("100", "99"));
        Assert.False(IdCompare.IsNewer("99", "100"));
        Assert.False(IdCompare.IsNewer("100", "100"));
        Assert.True(IdCompare.IsNewer("abc", "abd"));
        Assert.False(IdCompare.IsNewer("abc", "abc"));
        // 基准是数字而新 id 不是: 退化成字符串比较, 与旧版一致
        Assert.True(IdCompare.IsNewer("BV123", "100"));
        Assert.False(IdCompare.IsNewer("", "100"));
        Assert.False(IdCompare.IsNewer("100", ""));

        // 预解析基准的版本(一轮里只解析一次)必须与单次调用等价
        var parsed = IdCompare.ParseBaseline("100");
        Assert.True(IdCompare.IsNewer("101", "100", parsed));
        Assert.False(IdCompare.IsNewer("99", "100", parsed));
        Assert.False(IdCompare.IsNewer("100", "", IdCompare.ParseBaseline("")));
        Assert.False(IdCompare.IsNewer("", "100", parsed));
    }

    [Fact]
    public void 关注列表批量追加_计算属性只在结尾通知一次()
    {
        var vm = new UserCenterViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        var method = typeof(UserCenterViewModel).GetMethod("AppendFollowingsPage", InstanceNonPublic,
            new[] { typeof(IReadOnlyList<FollowUser>) });
        Assert.NotNull(method);

        var page = new List<FollowUser>();
        for (var i = 1; i <= 5; i++) page.Add(new FollowUser { Mid = i, Name = "u" + i });
        method!.Invoke(vm, new object[] { page });

        Assert.Equal(5, vm.Followings.Count);
        // 逐条通知的话每个名字会出现 5 次; 批量期间压住 + 结尾补一次 = 恰好 1 次。
        Assert.Equal(1, changed.Count(n => n == nameof(UserCenterViewModel.FollowingsSummary)));
        Assert.Equal(1, changed.Count(n => n == nameof(UserCenterViewModel.SummaryText)));
        // 这两条来自结尾的 RaiseTabState: 删掉它, 空态/错误遮罩就会停在"还没有关注任何人"。
        Assert.Equal(1, changed.Count(n => n == nameof(UserCenterViewModel.FollowingsEmpty)));
        Assert.Equal(1, changed.Count(n => n == nameof(UserCenterViewModel.ShowFollowingsError)));
    }
}
