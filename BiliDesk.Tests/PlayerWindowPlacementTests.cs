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

    // ------------------------------------------------------------ 小窗模式(issue #127)

    private const double MiniRatio = 0.28;
    private const double MiniMinW = 320;
    private const double MiniMinH = 180;
    private const double MiniMaxW = 560;
    private const double MiniMargin = 16;

    private static PlayerWindowPlacement.PlacementRect? Mini(
        PlayerWindowPlacement.PlacementRect[] areas,
        PlayerWindowPlacement.PlacementRect current,
        PlayerWindowPlacement.PlacementRect? last = null)
        => PlayerWindowPlacement.ComputeMiniWindowRect(
            areas, current, last, MiniRatio, MiniMinW, MiniMinH, MiniMaxW, MiniMargin);

    private static readonly PlayerWindowPlacement.PlacementRect[] MainArea =
        { new(0, 0, 1536, 816) };

    [Fact]
    public void ComputeMiniWindowRect_首次进入_贴当前屏右下角_留边距_16比9()
    {
        var r = Mini(MainArea, new(100, 100, 1280, 760));

        Assert.NotNull(r);
        // 1536 × 0.28 = 430.08, 落在 [320, 560] 内
        Assert.Equal(430.08, r!.Value.Width, 3);
        Assert.Equal(430.08 / PlayerWindowPlacement.MiniWindowAspect, r.Value.Height, 3);
        Assert.Equal(1536 - MiniMargin, r.Value.Right, 3);
        Assert.Equal(816 - MiniMargin, r.Value.Bottom, 3);
    }

    [Fact]
    public void ComputeMiniWindowRect_比例算出的宽度小于下限_用下限()
    {
        // 1000 × 0.28 = 280 < 320
        var r = Mini(new[] { new PlayerWindowPlacement.PlacementRect(0, 0, 1000, 800) },
            new(0, 0, 900, 700));

        Assert.Equal(320, r!.Value.Width, 3);
        Assert.Equal(180, r.Value.Height, 3);
        Assert.Equal(1000 - MiniMargin, r.Value.Right, 3);
        Assert.Equal(800 - MiniMargin, r.Value.Bottom, 3);
    }

    [Fact]
    public void ComputeMiniWindowRect_大屏_宽度不超过上限()
    {
        var r = Mini(new[] { new PlayerWindowPlacement.PlacementRect(0, 0, 3840, 2080) },
            new(0, 0, 2000, 1200));

        Assert.Equal(560, r!.Value.Width, 3);
        Assert.Equal(315, r.Value.Height, 3);
    }

    [Fact]
    public void ComputeMiniWindowRect_工作区很窄_整窗仍在工作区内()
    {
        var area = new PlayerWindowPlacement.PlacementRect(0, 0, 340, 300);
        var r = Mini(new[] { area }, new(0, 0, 300, 280));

        Assert.NotNull(r);
        Assert.True(r!.Value.Left >= area.Left);
        Assert.True(r.Value.Top >= area.Top);
        Assert.True(r.Value.Right <= area.Right);
        Assert.True(r.Value.Bottom <= area.Bottom);
    }

    [Fact]
    public void ComputeMiniWindowRect_上次的小窗位置_原样复用()
    {
        var last = new PlayerWindowPlacement.PlacementRect(200, 100, 400, 225);

        var r = Mini(MainArea, new(100, 100, 1280, 760), last);

        Assert.Equal(last, r);
    }

    [Fact]
    public void ComputeMiniWindowRect_上次的位置被拖出一部分_整窗夹回工作区()
    {
        var r = Mini(MainArea, new(100, 100, 1280, 760), new(-200, 700, 400, 225));

        Assert.NotNull(r);
        Assert.Equal(0, r!.Value.Left);
        Assert.Equal(816 - 225, r.Value.Top);
        Assert.Equal(400, r.Value.Width);
        Assert.Equal(225, r.Value.Height);
    }

    [Fact]
    public void ComputeMiniWindowRect_上次的位置完全在屏外_回到默认位()
    {
        var fallback = Mini(MainArea, new(100, 100, 1280, 760));
        var r = Mini(MainArea, new(100, 100, 1280, 760), new(5000, 5000, 400, 225));

        Assert.Equal(fallback, r);
    }

    [Fact]
    public void ComputeMiniWindowRect_上次的尺寸小于下限_视为无效()
    {
        var fallback = Mini(MainArea, new(100, 100, 1280, 760));

        Assert.Equal(fallback, Mini(MainArea, new(100, 100, 1280, 760), new(100, 100, 200, 120)));
    }

    [Fact]
    public void ComputeMiniWindowRect_双屏_默认位跟着窗口所在的屏走_负坐标成立()
    {
        var left = new PlayerWindowPlacement.PlacementRect(-1536, 0, 1536, 816);
        var areas = new[] { MainArea[0], left };

        var r = Mini(areas, new(-1200, 100, 1280, 760));

        Assert.NotNull(r);
        Assert.Equal(left.Right - MiniMargin, r!.Value.Right, 3);
        Assert.Equal(left.Bottom - MiniMargin, r.Value.Bottom, 3);
        Assert.True(r.Value.Left < 0);
    }

    [Fact]
    public void ComputeMiniWindowRect_双屏_上次在副屏_仍落在副屏()
    {
        var left = new PlayerWindowPlacement.PlacementRect(-1536, 0, 1536, 816);
        var areas = new[] { MainArea[0], left };
        var last = new PlayerWindowPlacement.PlacementRect(-1400, 500, 400, 225);

        Assert.Equal(last, Mini(areas, new(100, 100, 1280, 760), last));
    }

    [Fact]
    public void ComputeMiniWindowRect_副屏被拔掉_上次位置回到主屏默认位()
    {
        var last = new PlayerWindowPlacement.PlacementRect(-1400, 500, 400, 225);
        var fallback = Mini(MainArea, new(-1400, 500, 1280, 760));

        // 窗口本身也跟着落到了主屏之外 ⇒ 默认位退到第一块工作区(主屏)
        Assert.Equal(fallback, Mini(MainArea, new(-1400, 500, 1280, 760), last));
    }

    [Fact]
    public void ComputeMiniWindowRect_没有工作区_返回null()
    {
        Assert.Null(Mini(Array.Empty<PlayerWindowPlacement.PlacementRect>(), new(0, 0, 800, 600)));
    }
}
