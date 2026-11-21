using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 长按倍速的 ↑/↓ 档位切换(2026-11-21)。
/// ★ 列表是循环的: 3x 再 ↑ 回到 1.5x, 1.5x 再 ↓ 回到 3x。
/// ★ 还要覆盖"当前倍速不在档位上"的边界: 长按固定从 2x 起步, 但纯函数仍按"最近档"
///   处理非档位值 —— 例如 2.5x 按 ↓ 必须回到 2x, 不能跳过它掉到 1.5x。
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
    public void 到端点后循环_3倍再加速回到1点5_1点5再减速回到3()
    {
        Assert.Equal(1.5f, FastForwardSpeedPolicy.Step(3.0f, +1));
        Assert.Equal(3.0f, FastForwardSpeedPolicy.Step(1.5f, -1));
    }

    [Fact]
    public void 非档位倍速_向上取最近更高档_向下取最近更低档()
    {
        // 2.5x(非档位值): ↑ 3x, ↓ 2x
        Assert.Equal(3.0f, FastForwardSpeedPolicy.Step(2.5f, +1));
        Assert.Equal(2.0f, FastForwardSpeedPolicy.Step(2.5f, -1));
        // 用户自定义 1.25x: ↑ 1.5x; ↓ 已经低于最低档 → 循环到 3x
        Assert.Equal(1.5f, FastForwardSpeedPolicy.Step(1.25f, +1));
        Assert.Equal(3.0f, FastForwardSpeedPolicy.Step(1.25f, -1));
        // 4x(非档位值): ↑ 循环到 1.5x; ↓ 3x
        Assert.Equal(1.5f, FastForwardSpeedPolicy.Step(4.0f, +1));
        Assert.Equal(3.0f, FastForwardSpeedPolicy.Step(4.0f, -1));
    }

    [Fact]
    public void 方向为0_原样返回()
    {
        Assert.Equal(2.0f, FastForwardSpeedPolicy.Step(2.0f, 0));
    }
}
