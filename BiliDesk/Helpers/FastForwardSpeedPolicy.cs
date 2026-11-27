using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 长按倍速播放(键盘长按 → / 鼠标长按视频区)时, ↑/↓ 或鼠标滚轮在固定档位间切换(2026-11-21)。
///
/// ★ 档位只给 1.5x / 2x / 3x 三档: 这是"临时加速"能接受的区间 ——
///   低于 1.5 没有快进意义, 高于 3 在长按这种短时手势里画面已经跟不上。
///   默认 2x(见 PlayerWindow.ForwardKeyFastRate / FastForwardRate)。
///
/// ★ 列表是**循环**的(用户指定): 3x 再按 ↑ 回到 1.5x, 1.5x 再按 ↓ 回到 3x。
///   长按手势里没人愿意"按到头没反应", 循环也省掉"是不是到头了"的判断。
///
/// ★ 为什么抽成纯函数: 虽然长按固定从 2x 起步(见 PlayerWindow.StartFastForward), 正常路径上
///   传进来的一定是档位值; 但纯函数仍按"最近档"处理非档位值 —— 这样以后若允许自定义加速档,
///   或某个旧调用方传了 2.5x, 也不会出现"从 2.5x 按 ↓ 反而跳到 1.5x"这种反直觉行为。
///   规则: ↑ 取"比当前大的最小档"(没有则回到第一档), ↓ 取"比当前小的最大档"
///   (没有则回到最后一档)。
/// </summary>
public static class FastForwardSpeedPolicy
{
    /// <summary>可选的加速档位(升序)。OSD 的竖排「倍速选择」按这个顺序从上到下排。</summary>
    public static readonly float[] Steps = { 1.5f, 2.0f, 3.0f };

    /// <summary>长按进入时的默认倍速。</summary>
    public const float Default = 2.0f;

    /// <summary>比较倍速时的容差 —— 1.5f/2.0f/3.0f 都是二进制可精确表示的值, 用极小容差即可。</summary>
    private const float Epsilon = 0.001f;

    /// <summary>
    /// 沿 <paramref name="direction"/>(+1 加速 / -1 减速)迈一档, **到端点后循环**。
    /// </summary>
    public static float Step(float current, int direction)
    {
        if (direction > 0)
        {
            foreach (var s in Steps)
                if (s > current + Epsilon) return s;
            return Steps[0];          // 已在最高档 → 回到最低档
        }

        if (direction < 0)
        {
            for (var i = Steps.Length - 1; i >= 0; i--)
                if (Steps[i] < current - Epsilon) return Steps[i];
            return Steps[^1];         // 已在最低档 → 回到最高档
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
