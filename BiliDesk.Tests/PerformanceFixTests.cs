using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using BiliDesk.Models;
using BiliDesk.Services;
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
    public void 通知ID新旧比较_数字与非数字语义不变()
    {
        var method = typeof(DynamicNotifyService).GetMethod("IsIdNewer", StaticPrivate,
            new[] { typeof(string), typeof(string), typeof(bool), typeof(ulong) });
        Assert.NotNull(method);

        bool Newer(string id, string baseline)
        {
            var numeric = ulong.TryParse(baseline, out var num);
            return (bool)method!.Invoke(null, new object[] { id, baseline, numeric, num })!;
        }

        Assert.True(Newer("100", "99"));
        Assert.False(Newer("99", "100"));
        Assert.False(Newer("100", "100"));
        Assert.True(Newer("abc", "abd"));
        Assert.False(Newer("abc", "abc"));
        // 基准是数字而新 id 不是: 退化成字符串比较, 与旧版一致
        Assert.True(Newer("BV123", "100"));
        Assert.False(Newer("", "100"));
        Assert.False(Newer("100", ""));
    }

    [Fact]
    public void 关注列表批量添加期间_不逐条发计算属性通知()
    {
        var vm = new UserCenterViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        typeof(UserCenterViewModel).GetField("_followingsBatch", InstanceNonPublic)!.SetValue(vm, true);
        for (var i = 1; i <= 5; i++)
            vm.Followings.Add(new FollowUser { Mid = i, Name = "u" + i });
        Assert.Empty(changed);

        typeof(UserCenterViewModel).GetField("_followingsBatch", InstanceNonPublic)!.SetValue(vm, false);
        vm.Followings.Add(new FollowUser { Mid = 6, Name = "u6" });

        Assert.Contains(nameof(UserCenterViewModel.FollowingsSummary), changed);
        Assert.Contains(nameof(UserCenterViewModel.SummaryText), changed);
        Assert.Contains(nameof(UserCenterViewModel.FollowingsEmpty), changed);
        Assert.Contains(nameof(UserCenterViewModel.ShowFollowingsError), changed);
    }
}
