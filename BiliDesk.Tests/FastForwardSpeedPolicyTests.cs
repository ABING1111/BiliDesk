using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 长按倍速的 ↑/↓ 档位切换(2026-11-21)。
/// ★ 重点在"当前倍速不在档位上"的边界: 鼠标长按取 max(2.0, 当前倍速),
///   用户把倍速设成 2.5x 时按 ↓ 必须回到 2x, 不能跳过它掉到 1.5x。
/// </summary>
public class FastForwardSpeedPolicyTests
{
    [Fact]
    public void 默认档位是2倍速()
    {
        Assert.Equal(2.0f, FastForwardSpeedPolicy.Default);
        Assert.True(FastForwardSpeedPolicy.IsPreset(FastForwardSpeedPolicy.Default));
    }

    [Fact]
    public void 从默认2倍速_上一档3倍_下一档1点5倍()
    {
        Assert.Equal(3.0f, FastForwardSpeedPolicy.Step(2.0f, +1));
        Assert.Equal(1.5f, FastForwardSpeedPolicy.Step(2.0f, -1));
    }

    [Fact]
    public void 端点不动_不会反跳()
    {
        Assert.Equal(3.0f, FastForwardSpeedPolicy.Step(3.0f, +1));
        Assert.Equal(1.5f, FastForwardSpeedPolicy.Step(1.5f, -1));
    }

    [Fact]
    public void 非档位倍速_向上取最近更高档_向下取最近更低档()
    {
        // 鼠标长按: max(2.0, 2.5) = 2.5 → ↑ 3x, ↓ 2x
        Assert.Equal(3.0f, FastForwardSpeedPolicy.Step(2.5f, +1));
        Assert.Equal(2.0f, FastForwardSpeedPolicy.Step(2.5f, -1));
        // 用户自定义 1.25x: ↑ 1.5x; ↓ 没有更低的档 → 原地
        Assert.Equal(1.5f, FastForwardSpeedPolicy.Step(1.25f, +1));
        Assert.Equal(1.25f, FastForwardSpeedPolicy.Step(1.25f, -1));
        // 4x(长按期间不降速, 目标就是 4x): ↑ 没有更高档 → 原地; ↓ 3x
        Assert.Equal(4.0f, FastForwardSpeedPolicy.Step(4.0f, +1));
        Assert.Equal(3.0f, FastForwardSpeedPolicy.Step(4.0f, -1));
    }

    [Fact]
    public void 方向为0_原样返回()
    {
        Assert.Equal(2.0f, FastForwardSpeedPolicy.Step(2.0f, 0));
    }
}
