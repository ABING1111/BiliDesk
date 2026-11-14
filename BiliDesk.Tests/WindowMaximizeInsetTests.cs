using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 最大化内缩计算(2026-11-14)。
///
/// ★ 数据取自本机真机实测(1920x1080@125%):
///   工作区 = (0,0)-(1920,1020) 物理像素;
///   最大化窗口 = (-9,-9)-(1929,1029) —— Windows 给带缩放边框的窗口每边补 9px。
///   WindowChrome 下客户区 = 窗口矩形, 所以每边要内缩 9/1.25 = 7.2 DIP,
///   根内容才会正好落在可见的工作区里。
/// </summary>
public class WindowMaximizeInsetTests
{
    [Fact]
    public void 满屏最大化时每边内缩边框量再按DPI换成DIP()
    {
        var inset = WindowMaximizeInset.Compute(
            -9, -9, 1929, 1029,
            0, 0, 1920, 1020,
            1.25, 1.25);

        Assert.Equal(7.2, inset.Left, 3);
        Assert.Equal(7.2, inset.Top, 3);
        Assert.Equal(7.2, inset.Right, 3);
        Assert.Equal(7.2, inset.Bottom, 3);
    }

    [Fact]
    public void 一百百分比缩放下边框量就是DIP()
    {
        var inset = WindowMaximizeInset.Compute(
            -8, -8, 1928, 1032,
            0, 0, 1920, 1024,
            1.0, 1.0);

        Assert.Equal(8, inset.Left, 3);
        Assert.Equal(8, inset.Top, 3);
        Assert.Equal(8, inset.Right, 3);
        Assert.Equal(8, inset.Bottom, 3);
    }

    [Fact]
    public void 播放器全屏铺满整块显示器时同样按每边内缩()
    {
        // MaximizeCoversTaskbar=true: 目标改成 rcMonitor, 底部不再扣任务栏。
        var inset = WindowMaximizeInset.Compute(
            -9, -9, 1929, 1089,
            0, 0, 1920, 1080,
            1.25, 1.25);

        Assert.Equal(7.2, inset.Left, 3);
        Assert.Equal(7.2, inset.Top, 3);
        Assert.Equal(7.2, inset.Right, 3);
        Assert.Equal(7.2, inset.Bottom, 3);
    }

    [Fact]
    public void 窗口矩形与目标一致时不内缩()
    {
        var inset = WindowMaximizeInset.Compute(
            0, 0, 1920, 1020,
            0, 0, 1920, 1020,
            1.25, 1.25);

        Assert.Equal(0, inset.Left);
        Assert.Equal(0, inset.Top);
        Assert.Equal(0, inset.Right);
        Assert.Equal(0, inset.Bottom);
    }

    [Fact]
    public void 目标比窗口大时夹成零而不是反向外扩()
    {
        var inset = WindowMaximizeInset.Compute(
            10, 10, 1910, 1010,
            0, 0, 1920, 1020,
            1.25, 1.25);

        Assert.Equal(0, inset.Left);
        Assert.Equal(0, inset.Top);
        Assert.Equal(0, inset.Right);
        Assert.Equal(0, inset.Bottom);
    }

    [Fact]
    public void DPI异常时按一比一兜底而不是抛异常()
    {
        var inset = WindowMaximizeInset.Compute(
            -9, -9, 1929, 1029,
            0, 0, 1920, 1020,
            0, double.NaN);

        Assert.Equal(9, inset.Left, 3);
        Assert.Equal(9, inset.Top, 3);
        Assert.Equal(9, inset.Right, 3);
        Assert.Equal(9, inset.Bottom, 3);
    }
}
