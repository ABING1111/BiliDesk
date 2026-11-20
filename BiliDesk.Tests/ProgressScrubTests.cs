using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 进度条「按住任意位置拖动」的换算逻辑(2026-11-20)。
///
/// 背景: 拖动从"WPF 原生 Thumb 拖动"改成代码接管 —— 按下轨道任意位置即进入拖动,
/// 指针位置直接换算成目标时间, 松手才发一次 seek。换算一旦偏半个滑块,
/// 表现就是"气泡说 12:34、实际落在 12:31"(长视频里 1% ≈ 50 秒), 真机上极难复现。
/// 这里穷举边界: 两端、拖出轨道、行程未测量、原地按一下不跳。
/// </summary>
public class ProgressScrubTests
{
    // 典型几何: 轨道 1000 宽、滑块 11 宽 → 行程起点 5.5、行程长度 989
    private const double Offset = 5.5;
    private const double Usable = 989;

    [Fact]
    public void 行程起点对应量程下限()
    {
        // 滑块中心在 x = 半宽 处时正是 Value=0(见 TrackMetrics)
        Assert.Equal(0, ProgressScrub.ValueFromPointer(Offset, Offset, Usable, 0, 1000), 6);
    }

    [Fact]
    public void 行程终点对应量程上限()
    {
        Assert.Equal(1000, ProgressScrub.ValueFromPointer(Offset + Usable, Offset, Usable, 0, 1000), 6);
    }

    [Fact]
    public void 行程中点对应量程中值()
    {
        Assert.Equal(500, ProgressScrub.ValueFromPointer(Offset + Usable / 2, Offset, Usable, 0, 1000), 6);
    }

    [Fact]
    public void 指针在轨道最左边缘也算片头()
    {
        // x=0 比行程起点还靠左半个滑块: 必须夹到 0, 不能算出负数
        Assert.Equal(0, ProgressScrub.ValueFromPointer(0, Offset, Usable, 0, 1000), 6);
    }

    [Fact]
    public void 指针拖出轨道右侧夹到片尾()
    {
        // 有鼠标捕获时指针可以跑到控件外很远, 不能算出超过上限的值
        Assert.Equal(1000, ProgressScrub.ValueFromPointer(5000, Offset, Usable, 0, 1000), 6);
    }

    [Fact]
    public void 指针拖出轨道左侧夹到片头()
    {
        Assert.Equal(0, ProgressScrub.ValueFromPointer(-800, Offset, Usable, 0, 1000), 6);
    }

    [Fact]
    public void 非零下限的量程也按比例换算()
    {
        // 音量条这类 0~100 的量程走同一份公式
        Assert.Equal(50, ProgressScrub.ValueFromPointer(Offset + Usable / 2, Offset, Usable, 0, 100), 6);
        Assert.Equal(75, ProgressScrub.ValueFromPointer(Offset + Usable * 0.75, Offset, Usable, 0, 100), 6);
    }

    [Fact]
    public void 带下限偏移的量程也正确()
    {
        // 下限非 0 时(如 10~110), 指针落在起点应当是下限而不是 0
        Assert.Equal(10, ProgressScrub.ValueFromPointer(Offset, Offset, Usable, 10, 110), 6);
        Assert.Equal(110, ProgressScrub.ValueFromPointer(Offset + Usable, Offset, Usable, 10, 110), 6);
    }

    [Fact]
    public void 行程未测量时返回下限而不是NaN()
    {
        // 模板还没应用/控件被折叠: usable=0。返回 NaN 会让 Slider.Value 赋值直接抛异常
        Assert.Equal(0, ProgressScrub.ValueFromPointer(300, Offset, 0, 0, 1000));
        Assert.Equal(0, ProgressScrub.ValueFromPointer(300, Offset, -5, 0, 1000));
        Assert.Equal(0, ProgressScrub.ValueFromPointer(300, Offset, double.NaN, 0, 1000));
        Assert.Equal(0, ProgressScrub.ValueFromPointer(300, Offset, double.PositiveInfinity, 0, 1000));
    }

    [Fact]
    public void 原地按一下松手不跳转()
    {
        // 按在滑块上又松开(值没变): 再发 seek 只会打断正在播的解复用器
        Assert.False(ProgressScrub.ShouldSeek(420, 420));
    }

    [Fact]
    public void 值有一点点变化就算拖动()
    {
        // 1px 的拖动在常见轨道宽度下约等于 1, 不能被当成"没动"
        Assert.True(ProgressScrub.ShouldSeek(420, 421));
        Assert.True(ProgressScrub.ShouldSeek(420, 419));
    }

    [Fact]
    public void 阈值边界_恰好等于epsilon不算移动()
    {
        Assert.False(ProgressScrub.ShouldSeek(420, 420.01));
        Assert.True(ProgressScrub.ShouldSeek(420, 420.011));
    }

    [Fact]
    public void 点击轨道两端都能跳转()
    {
        // 点最左/最右是常见操作(跳到片头重看、跳到片尾看结尾)
        Assert.True(ProgressScrub.ShouldSeek(300, 0));
        Assert.True(ProgressScrub.ShouldSeek(300, 1000));
    }
}
