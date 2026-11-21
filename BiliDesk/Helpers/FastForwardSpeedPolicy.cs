using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 长按倍速播放(键盘长按 → / 鼠标长按视频区)时, ↑/↓ 在固定档位间切换(2026-11-21)。
///
/// ★ 档位只给 1.5x / 2x / 3x 三档: 这是"临时加速"能接受的区间 ——
///   低于 1.5 没有快进意义, 高于 3 在长按这种短时手势里画面已经跟不上。
///   默认 2x(见 PlayerWindow.RightKeyFastRate / FastForwardRate)。
///
/// ★ 为什么抽成纯函数: 鼠标长按的目标是 <c>max(2.0, 当前倍速)</c>, 当前倍速是用户
///   在倍速弹层里设的任意值(1.25/2.5/4.0…), 于是"当前不在档位上"是常态。
///   这种边界必须钉死, 否则很容易变成"从 2.5x 按 ↓ 反而跳到 1.5x"这种反直觉行为。
///   规则: ↑ 取"比当前大的最小档", ↓ 取"比当前小的最大档"; 已在端点则原地不动。
/// </summary>
public static class FastForwardSpeedPolicy
{
    /// <summary>可选的加速档位(升序)。OSD 的「倍速选择」按这个顺序排。</summary>
    public static readonly float[] Steps = { 1.5f, 2.0f, 3.0f };

    /// <summary>长按进入时的默认倍速。</summary>
    public const float Default = 2.0f;

    /// <summary>比较倍速时的容差 —— 1.5f/2.0f/3.0f 都是二进制可精确表示的值, 用极小容差即可。</summary>
    private const float Epsilon = 0.001f;

    /// <summary>
    /// 沿 <paramref name="direction"/>(+1 加速 / -1 减速)迈一档。
    /// 已经在档位两端时返回**原值**(不会为了"动一下"反而把 3x 降到 1.5x)。
    /// </summary>
    public static float Step(float current, int direction)
    {
        if (direction > 0)
        {
            foreach (var s in Steps)
                if (s > current + Epsilon) return s;
            return current;
        }

        if (direction < 0)
        {
            for (var i = Steps.Length - 1; i >= 0; i--)
                if (Steps[i] < current - Epsilon) return Steps[i];
            return current;
        }

        return current;
    }

    /// <summary>该倍速是否正好落在某个档位上(OSD 高亮用)。</summary>
    public static bool IsPreset(float rate)
    {
        foreach (var s in Steps)
            if (Math.Abs(rate - s) <= Epsilon) return true;
        return false;
    }
}
