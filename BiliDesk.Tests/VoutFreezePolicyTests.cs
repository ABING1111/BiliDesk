using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「音频在走、画面不再出帧」的判定规则(2026-11-22, issue #119)。
///
/// 这里锁住的是行为契约: 正常出帧绝不动作、暂停时间不算冻结、位置也冻住时让给
/// 卡顿自愈、同片最多自动恢复两次。真实数据由播放器每秒一次的 MediaStats 采样喂进来,
/// 所以测试直接喂毫秒时间戳与 DisplayedPictures 计数。
/// </summary>
public class VoutFreezePolicyTests
{
    [Fact]
    public void 画面持续出帧时不动作()
    {
        var p = new VoutFreezePolicy();
        for (var i = 0; i < 20; i++)
        {
            var action = p.Tick(i * 1_000L, 100 + i * 30, i * 1_000L, eligible: true);
            Assert.Equal(VoutFreezeAction.None, action);
        }
        Assert.Equal(0, p.Recoveries);
        Assert.False(p.GaveUp);
    }

    [Fact]
    public void 统计不可用时只记录不恢复()
    {
        var p = new VoutFreezePolicy();
        for (var i = 0; i < 20; i++)
            Assert.Equal(VoutFreezeAction.None, p.Tick(i * 1_000L, 0, i * 1_000L, eligible: true));

        Assert.True(p.StatsUnavailable);
        Assert.Equal(0, p.Recoveries);
    }

    [Fact]
    public void 暂停时间不计入冻结()
    {
        var p = new VoutFreezePolicy();
        Assert.Equal(VoutFreezeAction.None, p.Tick(0, 100, 0, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(1_000, 110, 1_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(2_000, 120, 2_000, true));

        // 暂停 10 秒: 画面计数本来就不涨, 绝不能从这里开始算“停了 4 秒”。
        for (var t = 3_000L; t <= 12_000; t += 1_000)
            Assert.Equal(VoutFreezeAction.None, p.Tick(t, 120, 2_000, eligible: false));

        // 恢复播放后重新计时: 3 秒还不算, 满 4 秒才恢复。
        Assert.Equal(VoutFreezeAction.None, p.Tick(13_000, 120, 3_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(15_000, 120, 5_000, true));
        Assert.Equal(VoutFreezeAction.Recover, p.Tick(17_000, 120, 7_000, true));
        Assert.Equal(1, p.Recoveries);
    }

    [Fact]
    public void 画面四秒不涨且时钟在走才恢复()
    {
        var p = new VoutFreezePolicy();
        Assert.Equal(VoutFreezeAction.None, p.Tick(0, 100, 0, true));

        Assert.Equal(VoutFreezeAction.None, p.Tick(1_000, 100, 1_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(3_000, 100, 3_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(4_999, 100, 4_999, true));
        Assert.Equal(VoutFreezeAction.Recover, p.Tick(5_000, 100, 5_000, true));
    }

    [Fact]
    public void 播放位置也冻住时交给卡顿自愈()
    {
        var p = new VoutFreezePolicy();
        Assert.Equal(VoutFreezeAction.None, p.Tick(0, 100, 50_000, true));

        // 画面与位置同时停住: 这属于“整条 input 卡住”, 不是 vout 的锅。
        for (var t = 1_000L; t <= 20_000; t += 1_000)
            Assert.Equal(VoutFreezeAction.None, p.Tick(t, 100, 50_000, true));

        Assert.Equal(0, p.Recoveries);
    }

    [Fact]
    public void 恢复后要等新画面再重新武装_最多两次后放弃()
    {
        var p = new VoutFreezePolicy();
        Assert.Equal(VoutFreezeAction.None, p.Tick(0, 100, 0, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(1_000, 100, 1_000, true));
        Assert.Equal(VoutFreezeAction.Recover, p.Tick(5_000, 100, 5_000, true));   // 第 1 次

        // 沉降期: 新 input 还没出帧, 不能立刻再判冻结。
        Assert.Equal(VoutFreezeAction.None, p.Tick(6_000, 100, 0, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(7_000, 100, 0, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(8_000, 100, 1_000, true));

        // 新 input 出了第一帧才重新武装。
        Assert.Equal(VoutFreezeAction.None, p.Tick(9_000, 110, 2_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(10_000, 110, 3_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(11_000, 110, 4_000, true));
        Assert.Equal(VoutFreezeAction.Recover, p.Tick(15_000, 110, 8_000, true));  // 第 2 次

        // 第二次沉降 + 再出帧后, 第三段冻结就该放弃。
        Assert.Equal(VoutFreezeAction.None, p.Tick(16_000, 120, 0, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(18_000, 120, 1_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(19_000, 120, 2_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(20_000, 130, 3_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(21_000, 130, 4_000, true));
        Assert.Equal(VoutFreezeAction.GiveUp, p.Tick(25_000, 130, 8_000, true));
        Assert.True(p.GaveUp);
        Assert.Equal(2, p.Recoveries);

        // 放弃之后不再反复要重建。
        Assert.Equal(VoutFreezeAction.None, p.Tick(40_000, 120, 30_000, true));
    }

    [Fact]
    public void 换片复位后重新拥有两次预算()
    {
        var p = new VoutFreezePolicy();
        Assert.Equal(VoutFreezeAction.None, p.Tick(0, 100, 0, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(1_000, 100, 1_000, true));
        Assert.Equal(VoutFreezeAction.Recover, p.Tick(5_000, 100, 5_000, true));
        Assert.Equal(1, p.Recoveries);

        p.Reset();
        Assert.Equal(0, p.Recoveries);
        Assert.False(p.GaveUp);

        Assert.Equal(VoutFreezeAction.None, p.Tick(10_000, 7, 0, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(11_000, 7, 1_000, true));
        Assert.Equal(VoutFreezeAction.Recover, p.Tick(15_000, 7, 5_000, true));
        Assert.Equal(1, p.Recoveries);
    }

    [Fact]
    public void 统计计数回退时重新取基线()
    {
        var p = new VoutFreezePolicy();
        Assert.Equal(VoutFreezeAction.None, p.Tick(0, 100, 0, true));

        // 重建/换片后计数从小数重新开始: 不能把“从 100 掉到 10”当成异常或冻结。
        Assert.Equal(VoutFreezeAction.None, p.Tick(1_000, 10, 1_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(2_000, 10, 2_000, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(5_000, 10, 5_000, true));
        Assert.Equal(VoutFreezeAction.Recover, p.Tick(6_000, 10, 6_000, true));
    }

    [Fact]
    public void 读不到统计的拍子不会清掉恢复预算()
    {
        var p = new VoutFreezePolicy();
        Assert.Equal(VoutFreezeAction.None, p.Tick(0, 100, 0, true));
        Assert.Equal(VoutFreezeAction.None, p.Tick(1_000, 100, 1_000, true));
        Assert.Equal(VoutFreezeAction.Recover, p.Tick(5_000, 100, 5_000, true));
        Assert.Equal(1, p.Recoveries);

        // 重建途中读不到统计: 只清冻结窗口, 次数不能还回去。
        for (var t = 5_000L; t <= 12_000; t += 1_000)
            p.OnCannotSample();

        Assert.Equal(1, p.Recoveries);
    }
}
