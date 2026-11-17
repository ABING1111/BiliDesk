using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 弹幕运动的算式(2026-11-17 补): 位移与**时钟校准**。
///
/// ★ 为什么单独测校准: 用户报的"弹幕滑动几帧几帧地跳"根因就在这里 ——
///   外推值被实测值每 4Hz 硬拽一次。真实播放器里这条路径每帧都跑, 起一个真播放器
///   去测既慢又不稳定; 而"跳没跳"完全是这条纯函数的性质, 直接喂数断言最准。
/// </summary>
public class DanmakuMotionTests
{
    // ------------------------------------------------------------ 位移

    [Fact]
    public void 滚动弹幕匀速从右缘走到完全离开()
    {
        const double canvas = 1000;
        const double width = 200;
        const double total = 8;

        // 起点: 右缘之外
        Assert.Equal(canvas, DanmakuMotion.RollingX(canvas, width, 0, total));
        // 终点: 右边缘刚好完全离开左侧
        Assert.Equal(-width, DanmakuMotion.RollingX(canvas, width, total, total));

        // 匀速: 任意两个等长区间的位移必须相等(任何缓动都会破坏这一条)
        var a = DanmakuMotion.RollingX(canvas, width, 1, total);
        var b = DanmakuMotion.RollingX(canvas, width, 2, total);
        var c = DanmakuMotion.RollingX(canvas, width, 3, total);
        Assert.Equal(a - b, b - c, 6);
    }

    [Fact]
    public void 滚动弹幕越界不抛异常()
    {
        Assert.Equal(1000, DanmakuMotion.RollingX(1000, 200, -5, 8));   // elapsed < 0
        Assert.Equal(-200, DanmakuMotion.RollingX(1000, 200, 99, 8));   // 已飞完
        Assert.Equal(1000, DanmakuMotion.RollingX(1000, 200, 1, 0));    // 总时长为 0
    }

    // ------------------------------------------------------------ 时钟校准

    [Fact]
    public void 校准_小偏差当作噪声_位置完全不动()
    {
        // 10ms 的相位差在 60fps 下连半个像素都不到, 去纠它只会把位移搅出细碎抖动
        var next = DanmakuMotion.Calibrate(current: 100.0, measured: 100.01);
        Assert.Equal(100.0, next);
    }

    [Fact]
    public void 校准_中等偏差只吃掉一部分_不跳变()
    {
        // 200ms 偏差: 按 25% 收敛, 这一拍只走 50ms —— 远小于"整段跳过去"
        var next = DanmakuMotion.Calibrate(current: 100.0, measured: 100.2);
        Assert.Equal(100.05, next, 6);
        Assert.True(next < 100.2, "不能一步到位, 那就是硬校准");
    }

    [Fact]
    public void 校准_连续收敛后逼近实测值且始终单调()
    {
        var v = 100.0;
        var last = v;
        for (var i = 0; i < 60; i++)
        {
            v = DanmakuMotion.Calibrate(v, 100.2);
            Assert.True(v >= last, "收敛过程必须单调, 不能来回拉");
            last = v;
        }
        // ★ 收敛到 tolerance(0.02)以内就停 —— 那是有意设计的停止带:
        //   再往下纠只是在追相位噪声, 反而会把位移搅出细碎抖动。
        Assert.True(Math.Abs(100.2 - v) <= 0.02, $"应落在停止带内, 实际 {v}");
    }

    [Fact]
    public void 校准_大偏差视为seek_直接对齐()
    {
        // 拖进度条 / 换片: 位置本来就该突变, 慢慢爬会让弹幕错位好几秒
        Assert.Equal(500.0, DanmakuMotion.Calibrate(current: 100.0, measured: 500.0));
        Assert.Equal(100.0, DanmakuMotion.Calibrate(current: 500.0, measured: 100.0));
    }

    [Fact]
    public void 校准_脏实测值不污染基准()
    {
        // libvlc 在 input 重建时会报 -1 / ulong 溢出之类的值, 不能让它把时钟带飞
        Assert.Equal(100.0, DanmakuMotion.Calibrate(100.0, double.NaN));
        Assert.Equal(100.0, DanmakuMotion.Calibrate(100.0, double.PositiveInfinity));
    }

    // ------------------------------------------------------------ 外推

    [Fact]
    public void 外推_乘倍速()
    {
        // 2 倍速下墙钟走 1 秒, 媒体时间应前进 2 秒(否则每次校准都会往回拽)
        Assert.Equal(12.0, DanmakuMotion.Extrapolate(10.0, 1.0, 2.0), 6);
        Assert.Equal(10.5, DanmakuMotion.Extrapolate(10.0, 1.0, 0.5), 6);
        // 脏倍速按 1x 处理, 别让外推停摆
        Assert.Equal(11.0, DanmakuMotion.Extrapolate(10.0, 1.0, 0.0), 6);
        Assert.Equal(11.0, DanmakuMotion.Extrapolate(10.0, 1.0, -3.0), 6);
    }

    [Fact]
    public void 直播时钟_墙钟外推不会因为媒体时间抖动而跳()
    {
        // 直播: 校准被完全忽略, 所以这里只验外推本身的连续性 ——
        // 每秒 30 帧, 每帧推进量必须恒定(这正是"不卡顿"的定义)。
        var prev = DanmakuMotion.Extrapolate(0, 0, 1.0);
        for (var frame = 1; frame <= 30; frame++)
        {
            var now = DanmakuMotion.Extrapolate(0, frame / 30.0, 1.0);
            Assert.Equal(1.0 / 30.0, now - prev, 9);
            prev = now;
        }
    }
}
