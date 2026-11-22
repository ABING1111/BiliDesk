using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 弹层水平夹取的纯判定逻辑(2026-11-22)。
///
/// 背景: 分P 入口在右侧 400px 信息栏靠右处, 300px 宽的弹层按 Bottom 左缘对齐会顶出窗口。
/// 这里覆盖三种情况: 装得下不挪、装不下贴右缘、容器比弹层还窄时保左缘。
/// </summary>
public class PopupPlacementTests
{
    private const double WindowRight = 1280;

    [Fact]
    public void 装得下_不挪动_偏移为0()
    {
        // 入口在 600, 弹层 300 → 右缘 900, 离窗口右缘还有 380
        Assert.Equal(0, PopupPlacement.ClampHorizontalOffset(600, 300, 0, WindowRight));
    }

    [Fact]
    public void 恰好留出空隙_不挪动()
    {
        // 右缘 + 空隙 恰好等于容器右缘 → 不算溢出
        var anchorLeft = WindowRight - PopupPlacement.EdgeMargin - 300;
        Assert.Equal(0, PopupPlacement.ClampHorizontalOffset(anchorLeft, 300, 0, WindowRight));
    }

    [Fact]
    public void 顶出窗口_左移到右缘留出空隙()
    {
        // 播放器的真实场景: 信息栏 400 宽, 入口在栏内约 175 处, 弹层 300 宽
        // → 默认右缘 (1055+300) 比窗口右缘(1280) 多出 75, 再留 8 空隙 ⇒ 左移 83
        const double anchorLeft = WindowRight - 225;
        var offset = PopupPlacement.ClampHorizontalOffset(anchorLeft, 300, 0, WindowRight);
        Assert.Equal(-83, offset, precision: 3);
        // 左移后弹层右缘正好贴住"容器右缘 - 空隙"
        Assert.Equal(WindowRight - PopupPlacement.EdgeMargin,
            anchorLeft + 300 + offset, precision: 3);
    }

    [Fact]
    public void 只左移不右移_偏移恒不为正()
    {
        for (var anchor = 0.0; anchor <= WindowRight; anchor += 40)
            Assert.True(PopupPlacement.ClampHorizontalOffset(anchor, 300, 0, WindowRight) <= 0);
    }

    [Fact]
    public void 容器比弹层还窄_保住左缘()
    {
        // 窗口只剩 200 宽, 弹层 300 → 右边怎么都超, 但不能让弹层开头跑出窗口
        var offset = PopupPlacement.ClampHorizontalOffset(50, 300, 0, 200);
        Assert.Equal(-50, offset, precision: 3);
    }

    [Fact]
    public void 弹层宽度未知_不插手()
    {
        Assert.Equal(0, PopupPlacement.ClampHorizontalOffset(1000, 0, 0, WindowRight));
        Assert.Equal(0, PopupPlacement.ClampHorizontalOffset(1000, -5, 0, WindowRight));
    }

    [Fact]
    public void 容器不成立_不插手()
    {
        Assert.Equal(0, PopupPlacement.ClampHorizontalOffset(100, 300, 500, 500));
        Assert.Equal(0, PopupPlacement.ClampHorizontalOffset(100, 300, 600, 500));
    }

    [Fact]
    public void 自定义空隙生效()
    {
        var noMargin = PopupPlacement.ClampHorizontalOffset(1000, 300, 0, WindowRight, margin: 0);
        var wideMargin = PopupPlacement.ClampHorizontalOffset(1000, 300, 0, WindowRight, margin: 20);
        Assert.Equal(-20, noMargin, precision: 3);
        Assert.Equal(-40, wideMargin, precision: 3);
    }

    // ------------------------------------------------------------ 竖直: 挂在按钮下方

    private const double WindowBottom = 720;

    [Fact]
    public void 下方放得下_挂在按钮下方()
    {
        // 按钮底边 130 + 间隙 4
        Assert.Equal(134, PopupPlacement.DropdownTop(100, 130, 200, 0, WindowBottom), precision: 3);
    }

    [Fact]
    public void 下方放不下_翻到按钮上方()
    {
        // 下方 534+200+8 > 720 → 上方 500-4-200 = 296
        Assert.Equal(296, PopupPlacement.DropdownTop(500, 530, 200, 0, WindowBottom), precision: 3);
    }

    [Fact]
    public void 上下都放不下_夹回容器底部()
    {
        // 弹层 600 高: 下方 334+600+8 超底, 上方 300-4-600 为负 → 贴底 720-8-600 = 112
        Assert.Equal(112, PopupPlacement.DropdownTop(300, 330, 600, 0, WindowBottom), precision: 3);
    }

    [Fact]
    public void 容器比弹层还矮_保住顶边()
    {
        // 容器只有 300 高、弹层 400 高: 从上方夹只会算出负数, 必须停在顶边空隙处
        Assert.Equal(8, PopupPlacement.DropdownTop(100, 130, 400, 0, 300), precision: 3);
    }

    [Fact]
    public void 弹层高度未知_退回默认位置()
    {
        Assert.Equal(134, PopupPlacement.DropdownTop(100, 130, 0, 0, WindowBottom), precision: 3);
        Assert.Equal(134, PopupPlacement.DropdownTop(100, 130, -1, 0, WindowBottom), precision: 3);
    }

    [Fact]
    public void 容器不成立_退回默认位置()
    {
        Assert.Equal(134, PopupPlacement.DropdownTop(100, 130, 200, 500, 500), precision: 3);
    }

    [Fact]
    public void 翻面时不压住按钮_至少留出间隙()
    {
        var top = PopupPlacement.DropdownTop(500, 530, 200, 0, WindowBottom);
        Assert.Equal(500 - PopupPlacement.AnchorGap, top + 200, precision: 3);
    }

    [Fact]
    public void 容器内任意位置_弹层都不越界()
    {
        foreach (var anchorTop in new[] { 0.0, 40, 200, 400, 600, 700 })
        {
            var top = PopupPlacement.DropdownTop(anchorTop, anchorTop + 30, 200, 0, WindowBottom);
            Assert.True(top >= PopupPlacement.EdgeMargin - 0.001);
            Assert.True(top + 200 <= WindowBottom - PopupPlacement.EdgeMargin + 0.001);
        }
    }
}
