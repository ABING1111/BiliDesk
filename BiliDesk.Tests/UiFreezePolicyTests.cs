using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// UI 线程冻结判定的行为契约(2026-11-28 新增)。
///
/// 这里锁住的是**行为契约**: 什么时候判定为冻结、同一场冻结只报一次、
/// 恢复之后能不能再报下一场。
///
/// ★ 为什么"恢复之后要能再报"必须锁住:
///   用户一天里卡三次是常事, 而"第几次卡"恰恰是判断是否稳定复现的关键信息。
///   如果恢复后不重新武装, 只有第一次会被记录 —— 报告里就只剩"卡过一次",
///   完全看不出是偶发还是必现。
///
/// ★ 为什么"解锁只能走 OnResponsive"必须锁住(2026-11-28 探针实测出来的坑):
///   曾经用"往 Tick 传个小数字"顺带解锁, 结果探测轮次一换(每轮最多等一分钟)
///   锁存就被误清 —— 一次持续 3 分钟的冻结会报出好几遍, 日志被同一件事刷爆。
/// </summary>
public class UiFreezePolicyTests
{
    [Fact]
    public void 未响应时间不够时什么都不做()
    {
        var p = new UiFreezePolicy();
        Assert.Equal(UiFreezeAction.None, p.Tick(0));
        Assert.Equal(UiFreezeAction.None, p.Tick(1_000));
        Assert.Equal(UiFreezeAction.None, p.Tick(UiFreezePolicy.DefaultThresholdMs - 1));
        Assert.False(p.Reported);
    }

    [Fact]
    public void 超过阈值判定为冻结()
    {
        var p = new UiFreezePolicy();
        Assert.Equal(UiFreezeAction.Report, p.Tick(UiFreezePolicy.DefaultThresholdMs));
        Assert.True(p.Reported);
    }

    [Fact]
    public void 同一场冻结只报一次()
    {
        var p = new UiFreezePolicy();
        Assert.Equal(UiFreezeAction.Report, p.Tick(6_000));
        // 冻了 30 秒不该在日志里写 30 条
        Assert.Equal(UiFreezeAction.None, p.Tick(7_000));
        Assert.Equal(UiFreezeAction.None, p.Tick(30_000));
        Assert.Equal(UiFreezeAction.None, p.Tick(600_000));
    }

    [Fact]
    public void 响应之后下一场冻结还能再报()
    {
        var p = new UiFreezePolicy();
        Assert.Equal(UiFreezeAction.Report, p.Tick(6_000));
        // UI 线程缓过来了
        p.OnResponsive();
        Assert.False(p.Reported);
        // 又卡了一次 —— 必须再报一次
        Assert.Equal(UiFreezeAction.Report, p.Tick(6_000));
    }

    [Fact]
    public void 没收到响应通知就不会误解锁()
    {
        var p = new UiFreezePolicy();
        Assert.Equal(UiFreezeAction.Report, p.Tick(6_000));
        // 探测轮次换了(新一轮的第一个采样仍然是"没回来")——
        // 这时**不能**把锁存清掉, 否则一次长冻结会报好几遍。
        Assert.Equal(UiFreezeAction.None, p.Tick(6_200));
        Assert.Equal(UiFreezeAction.None, p.Tick(6_400));
        Assert.True(p.Reported);
    }

    [Fact]
    public void 自定义阈值生效()
    {
        var p = new UiFreezePolicy(1_000);
        Assert.Equal(UiFreezeAction.None, p.Tick(999));
        Assert.Equal(UiFreezeAction.Report, p.Tick(1_000));
    }

    [Fact]
    public void 非法阈值回落到默认值()
    {
        // 传 0 或负数不能变成"立刻判定为冻结"(那会让每一拍都报一次)
        var p = new UiFreezePolicy(0);
        Assert.Equal(UiFreezeAction.None, p.Tick(UiFreezePolicy.DefaultThresholdMs - 1));
        Assert.Equal(UiFreezeAction.Report, p.Tick(UiFreezePolicy.DefaultThresholdMs));
    }
}
