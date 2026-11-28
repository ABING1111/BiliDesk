using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 连续自动补页闸门(2026-11-28, 修"搜索页再次点击搜索后不断下拉刷新")。
///
/// 核心语义: <see cref="AutoFillGuard.OnFilled"/> **只增不减** ——
/// 旧写法在 finally 里减回去, 计数器只等于"在飞请求数", 永远到不了上限, 等于没有刹车。
/// </summary>
public class AutoFillGuardTests
{
    [Fact]
    public void 初始状态可以补页()
    {
        var guard = new AutoFillGuard();
        Assert.True(guard.CanFill);
        Assert.Equal(0, guard.Consecutive);
    }

    [Fact]
    public void 连续补满上限后不再放行()
    {
        var guard = new AutoFillGuard();
        for (var i = 0; i < AutoFillGuard.MaxConsecutive; i++)
        {
            Assert.True(guard.CanFill);
            guard.OnFilled();
        }

        Assert.Equal(AutoFillGuard.MaxConsecutive, guard.Consecutive);
        Assert.False(guard.CanFill);
    }

    [Fact]
    public void 计数只增不减_请求结束后不会自己回退()
    {
        var guard = new AutoFillGuard();
        guard.OnFilled();
        guard.OnFilled();

        // 这里刻意**不**模拟"请求结束": 旧实现的 bug 正是在 await 结束后减回去。
        Assert.Equal(2, guard.Consecutive);
        Assert.True(guard.CanFill);
    }

    [Fact]
    public void 重置后额度重开()
    {
        var guard = new AutoFillGuard();
        for (var i = 0; i < AutoFillGuard.MaxConsecutive; i++) guard.OnFilled();
        Assert.False(guard.CanFill);

        guard.Reset();

        Assert.Equal(0, guard.Consecutive);
        Assert.True(guard.CanFill);
    }

    [Fact]
    public void 上限为正数且不会因清零而失效()
    {
        Assert.True(AutoFillGuard.MaxConsecutive > 0);

        var guard = new AutoFillGuard();
        guard.Reset();   // 已经是 0, 再清一次也不该出问题
        Assert.True(guard.CanFill);
    }
}
