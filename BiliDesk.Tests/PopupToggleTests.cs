using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 播放器弹层「点一下展开, 再点一下收起」的纯判定逻辑(2026-11-14)。
///
/// 背景: StaysOpen=False 的弹层在"再点入口按钮"那一下的 MouseDown 阶段就被 WPF 自动收起,
/// Click 到达时 IsOpen 恒为 false —— 旧写法(无条件展开/取反 IsOpen)永远收不起来。
/// 这里穷举判定三分支: 展开 / 收起 / 抑制窗口内保持收起。
/// </summary>
public class PopupToggleTests
{
    private const long Now = 10_000;

    [Fact]
    public void 关着且从未收起过_点一下展开()
    {
        Assert.Equal(PopupToggle.Action.Open, PopupToggle.Decide(false, null, Now));
    }

    [Fact]
    public void 关着且很久以前收起过_点一下展开()
    {
        Assert.Equal(PopupToggle.Action.Open, PopupToggle.Decide(false, Now - 5_000, Now));
    }

    [Fact]
    public void 开着_点一下收起()
    {
        // 键盘触发 Click 时 WPF 不会自动收, IsOpen 仍为 true → 显式收起
        Assert.Equal(PopupToggle.Action.Close, PopupToggle.Decide(true, null, Now));
    }

    [Fact]
    public void 开着时优先收起_不受收起时刻影响()
    {
        // "刚收起又立刻展开"后的下一次点击: 只要还开着就该收起, 时间戳不参与
        Assert.Equal(PopupToggle.Action.Close, PopupToggle.Decide(true, Now - 1, Now));
    }

    [Fact]
    public void 刚被这一下点击收起_抑制窗口内保持收起()
    {
        // MouseDown(9_800) 时 WPF 自动收起, MouseUp 的 Click(10_000) 到达: 按压时长 200ms。
        // 这正是"再点一下收起"的时序 —— 绝不能重开。
        Assert.Equal(PopupToggle.Action.StayClosed, PopupToggle.Decide(false, Now - 200, Now));
    }

    [Fact]
    public void 极快点击_几十毫秒的按压也要保持收起()
    {
        Assert.Equal(PopupToggle.Action.StayClosed, PopupToggle.Decide(false, Now - 30, Now));
    }

    [Fact]
    public void 收起已超过抑制窗口_再点展开()
    {
        // 用户点别处收起弹层, 过了好一会儿再点入口按钮 → 正常展开
        Assert.Equal(PopupToggle.Action.Open,
            PopupToggle.Decide(false, Now - PopupToggle.SuppressReopenMs - 1, Now));
    }

    [Fact]
    public void 抑制窗口边界_恰好到期算展开()
    {
        var closedAt = Now - PopupToggle.SuppressReopenMs;
        Assert.Equal(PopupToggle.Action.Open, PopupToggle.Decide(false, closedAt, Now));
        Assert.Equal(PopupToggle.Action.StayClosed, PopupToggle.Decide(false, closedAt + 1, Now));
    }

    [Fact]
    public void 自定义抑制窗口生效()
    {
        Assert.Equal(PopupToggle.Action.StayClosed, PopupToggle.Decide(false, Now - 500, Now, suppressMs: 1_000));
        Assert.Equal(PopupToggle.Action.Open, PopupToggle.Decide(false, Now - 500, Now, suppressMs: 100));
    }

    [Fact]
    public void 连续开关循环_每一步状态都对()
    {
        // 模拟真实使用: 展开 → 再点收起(WPF 先收, Click 落在抑制窗口内) → 再点展开……
        // 每次"收起点击"间隔 800ms(远超抑制窗口), 按压时长 150ms。
        long? closedAt = null;
        var t = Now;
        for (var i = 0; i < 6; i++)
        {
            // 第一次点击: 展开
            Assert.Equal(PopupToggle.Action.Open, PopupToggle.Decide(false, closedAt, t));
            t += 800;
            // 第二次点击: MouseDown 时 WPF 收起(记时刻), Click 在 150ms 后到达
            closedAt = t;
            Assert.Equal(PopupToggle.Action.StayClosed, PopupToggle.Decide(false, closedAt, t + 150));
            t += 800;
        }
    }
}