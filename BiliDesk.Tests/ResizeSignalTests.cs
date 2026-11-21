using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「宽度变化算不算窗口缩放」的判定(2026-11-21 修"第一次进入卡片页只排 3 列")。
///
/// ★ 判错的两种后果都很具体:
///   · 把"滚动条出现的 18px"当缩放 ⇒ 卡片按旧槽宽折行, 每行少一张, 220ms 后才跳回来;
///   · 把"用户拖窗口"当内部版式变化 ⇒ 逐帧收敛, 卡片跟着拖动一路抽。
///   所以边界(首次播种 / 0.5DIP 死区 / NaN)必须钉住。
/// </summary>
public class ResizeSignalTests
{
    [Theory]
    // 首次播种: 墙还没记过窗口宽度, 这一趟不能算缩放(否则滚动条一出现就被当成拖窗口)
    [InlineData(0, 1200, false)]
    [InlineData(0, 0, false)]
    // 窗口宽度没变 ⇒ 墙的宽度变化来自页面内部版式(滚动条 / 导航栏档位), 不是缩放
    [InlineData(1200, 1200, false)]
    // 死区以内(布局按 0.5DIP 取整, 亚像素差异不算窗口动了)
    [InlineData(1200, 1200.4, false)]
    [InlineData(1200, 1199.6, false)]
    // 真的变了: 拖边 / 最大化还原 / 跨 DPI
    [InlineData(1200, 1200.5, true)]
    [InlineData(1200, 1201, true)]
    [InlineData(1200, 1100, true)]
    [InlineData(1200, 1920, true)]
    // 窗口还没量出来(启动第一拍): 当成"没变", 交给 SizeChanged 下一趟
    [InlineData(1200, 0, false)]
    public void IsWindowWidthChange_判定窗口宽度是否真的变了(double last, double current, bool expected)
        => Assert.Equal(expected, ResizeSignal.IsWindowWidthChange(last, current));

    [Fact]
    public void IsWindowWidthChange_NaN一律不算缩放()
    {
        Assert.False(ResizeSignal.IsWindowWidthChange(double.NaN, 1200));
        Assert.False(ResizeSignal.IsWindowWidthChange(1200, double.NaN));
        Assert.False(ResizeSignal.IsWindowWidthChange(double.NaN, double.NaN));
    }

    /// <summary>
    /// 竖滚动条出现那一下: 窗口宽度还是 1447, 变窄的是**墙**(墙的可用宽度从 1447 变成约 1430)。
    /// 判据只比较**窗口**宽度, 所以这一趟必须判成"不是缩放" —— 这是本类存在的唯一理由。
    /// </summary>
    [Fact]
    public void IsWindowWidthChange_滚动条挤掉的是墙宽而不是窗口宽()
    {
        const double windowWidth = 1447;
        Assert.False(ResizeSignal.IsWindowWidthChange(windowWidth, windowWidth));
    }
}
