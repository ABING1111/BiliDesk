using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 播放器窗口大小/位置记忆的纯几何逻辑(2026-11-14)。
/// 覆盖: 有效性判断、按虚拟桌面/目标显示器夹取、副屏(负坐标)与"显示器被拔掉"的兜底。
/// </summary>
public class PlayerWindowPlacementTests
{
    private const double MinVisibleW = 160;
    private const double MinVisibleH = 120;

    [Fact]
    public void IsUsable_正常矩形_通过()
    {
        Assert.True(PlayerWindowPlacement.IsUsable(new(100, 100, 800, 600), 320, 200));
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(100, double.NaN)]
    [InlineData(100, double.NegativeInfinity)]
    public void IsUsable_非有限坐标_拒绝(double left, double top)
    {
        Assert.False(PlayerWindowPlacement.IsUsable(new(left, top, 800, 600), 320, 200));
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(319, 600)]
    [InlineData(800, 0)]
    [InlineData(800, 199)]
    public void IsUsable_尺寸过小_拒绝(double width, double height)
    {
        Assert.False(PlayerWindowPlacement.IsUsable(new(0, 0, width, height), 320, 200));
    }

    [Fact]
    public void ClampToBounds_完全可见_原样返回()
    {
        var desired = new PlayerWindowPlacement.PlacementRect(100, 100, 800, 600);
        var bounds = new PlayerWindowPlacement.PlacementRect(0, 0, 1536, 816);

        var result = PlayerWindowPlacement.ClampToBounds(desired, bounds, MinVisibleW, MinVisibleH);

        Assert.Equal(desired, result);
    }

    [Fact]
    public void ClampToBounds_右下越界_保留最小可见区()
    {
        var desired = new PlayerWindowPlacement.PlacementRect(1500, 800, 800, 600);
        var bounds = new PlayerWindowPlacement.PlacementRect(0, 0, 1536, 816);

        var result = PlayerWindowPlacement.ClampToBounds(desired, bounds, MinVisibleW, MinVisibleH);

        // 左上角最多退到"还留 160×120 可见"的位置, 而不是把整窗拉回屏幕内。
        Assert.Equal(1536 - MinVisibleW, result.Left);
        Assert.Equal(816 - MinVisibleH, result.Top);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void ClampToBounds_左上越界_保留最小可见区()
    {
        var desired = new PlayerWindowPlacement.PlacementRect(-1000, -1000, 800, 600);
        var bounds = new PlayerWindowPlacement.PlacementRect(0, 0, 1536, 816);

        var result = PlayerWindowPlacement.ClampToBounds(desired, bounds, MinVisibleW, MinVisibleH);

        Assert.Equal(-(800 - MinVisibleW), result.Left);
        Assert.Equal(-(600 - MinVisibleH), result.Top);
    }

    [Fact]
    public void ClampToBounds_副屏负坐标_不误杀()
    {
        // 副屏在主屏左侧: 虚拟桌面从 -1920 开始。
        var desired = new PlayerWindowPlacement.PlacementRect(-1500, 100, 800, 600);
        var virtualScreen = new PlayerWindowPlacement.PlacementRect(-1920, 0, 3456, 1080);

        var result = PlayerWindowPlacement.ClampToBounds(desired, virtualScreen, MinVisibleW, MinVisibleH);

        Assert.Equal(desired, result);
    }

    [Fact]
    public void ClampToBounds_边界比可见区还窄_不抛异常()
    {
        var desired = new PlayerWindowPlacement.PlacementRect(500, 500, 800, 600);
        var degenerate = new PlayerWindowPlacement.PlacementRect(0, 0, 100, 100);

        var result = PlayerWindowPlacement.ClampToBounds(desired, degenerate, MinVisibleW, MinVisibleH);

        // 夹到边界左上角, 不因为 min>max 抛 ArgumentException。
        Assert.Equal(0, result.Left);
        Assert.Equal(0, result.Top);
    }

    [Fact]
    public void ClampToBestWorkArea_双屏_选重叠最大的那块()
    {
        var primary = new PlayerWindowPlacement.PlacementRect(0, 0, 1920, 1080);
        var left = new PlayerWindowPlacement.PlacementRect(-1920, 0, 1920, 1080);
        var areas = new[] { primary, left };

        var onLeft = PlayerWindowPlacement.ClampToBestWorkArea(
            new(-1500, 100, 800, 600), areas, MinVisibleW, MinVisibleH);
        var onPrimary = PlayerWindowPlacement.ClampToBestWorkArea(
            new(100, 100, 800, 600), areas, MinVisibleW, MinVisibleH);

        Assert.Equal(new PlayerWindowPlacement.PlacementRect(-1500, 100, 800, 600), onLeft);
        Assert.Equal(new PlayerWindowPlacement.PlacementRect(100, 100, 800, 600), onPrimary);
    }

    [Fact]
    public void ClampToBestWorkArea_跨两块屏_选重叠大的()
    {
        var primary = new PlayerWindowPlacement.PlacementRect(0, 0, 1920, 1080);
        var left = new PlayerWindowPlacement.PlacementRect(-1920, 0, 1920, 1080);
        var areas = new[] { primary, left };

        // 700 在右屏、100 在左屏 ⇒ 应选主屏并保持位置不变。
        var result = PlayerWindowPlacement.ClampToBestWorkArea(
            new(-100, 100, 800, 600), areas, MinVisibleW, MinVisibleH);

        Assert.NotNull(result);
        Assert.Equal(-100, result!.Value.Left);
        Assert.Equal(100, result.Value.Top);
    }

    [Fact]
    public void ClampToBestWorkArea_目标在所有显示器之外_返回null()
    {
        var primary = new PlayerWindowPlacement.PlacementRect(0, 0, 1920, 1080);
        var areas = new[] { primary };

        var result = PlayerWindowPlacement.ClampToBestWorkArea(
            new(5000, 5000, 800, 600), areas, MinVisibleW, MinVisibleH);

        Assert.Null(result);
    }

    [Fact]
    public void ClampToBestWorkArea_没有显示器_返回null()
    {
        var result = PlayerWindowPlacement.ClampToBestWorkArea(
            new(100, 100, 800, 600), Array.Empty<PlayerWindowPlacement.PlacementRect>(), MinVisibleW, MinVisibleH);

        Assert.Null(result);
    }
}
