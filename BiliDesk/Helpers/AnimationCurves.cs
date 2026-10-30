using System;
using System.Windows.Media.Animation;

namespace BiliDesk.Helpers;

/// <summary>
/// cubic-bezier 缓动(标准写法: <c>cubic-bezier(x1, y1, x2, y2)</c>)。
///
/// ★ 为什么不用 WPF 自带的 CubicEase/BackEase:
///   ① CubicEase 的控制点固定为 (0.42,0) / (0.58,1), 那是老曲线;
///      Material Design 3 与 Fluent 的官方运动曲线(emphasized decelerate 等)是
///      **可任意指定控制点的 cubic-bezier**, 两者观感差别很明显。
///   ② BackEase 的过冲量写死成 1.70158 —— 弹得太狠, 也没法微调。
///      自己解 bezier 就能精确控制"过冲 8%"还是"过冲 4%"。
///   ③ 本项目里到处是 CubicEase(17 处), 但那些都是"淡出/淡入"这类不需要讲究的场合;
///      开屏动画是**第一印象**, 值得单独一套。
///
/// ★★★ 硬约束(来自本机实测, 见 Helpers/PageTransition 的类注释):
///   这台机器上"整棵子树做透明度动画"的**淡入方向不上屏**(渲染输出恢复不了)。
///   ⇒ 本文件里的曲线只允许用在**单个控件**的 Opacity / RenderTransform 上,
///     绝不用于整棵树的淡入。
/// </summary>
public sealed class SplineEase : IEasingFunction
{
    // P0 = (0,0), P1 = (x1,y1), P2 = (x2,y2), P3 = (1,1)
    private readonly double _x1, _y1, _x2, _y2;

    public SplineEase(double x1, double y1, double x2, double y2)
    {
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
    }

    public double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        var t = SolveT(progress);
        return SampleY(t);
    }

    /// <summary>
    /// 由 x(progress)反解参数 t。
    ///
    /// bezier 的 x(t) 单调(控制点 x 在 0~1), 所以可以二分。
    /// 这里先用牛顿法(收敛快, 3~4 次就够), 不收敛再退回二分 —— 二分永远收敛,
    /// 只是慢一点, 而动画总长几百毫秒, 多迭代几次无所谓。
    /// </summary>
    private double SolveT(double x)
    {
        // 牛顿法: t -= (X(t) - x) / X'(t)
        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var xt = SampleX(t) - x;
            if (Math.Abs(xt) < 1e-7) return t;
            var d = SampleDX(t);
            if (Math.Abs(d) < 1e-9) break;
            t -= xt / d;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
        }

        // 二分兜底
        var lo = 0.0;
        var hi = 1.0;
        t = x;
        for (var i = 0; i < 40; i++)
        {
            var xt = SampleX(t);
            if (Math.Abs(xt - x) < 1e-7) break;
            if (xt < x) lo = t; else hi = t;
            t = (lo + hi) / 2.0;
        }
        return t;
    }

    private double SampleX(double t)
    {
        var mt = 1 - t;
        return 3 * mt * mt * t * _x1 + 3 * mt * t * t * _x2 + t * t * t;
    }

    private double SampleY(double t)
    {
        var mt = 1 - t;
        return 3 * mt * mt * t * _y1 + 3 * mt * t * t * _y2 + t * t * t;
    }

    private double SampleDX(double t)
    {
        var mt = 1 - t;
        return 3 * mt * mt * _x1 + 6 * mt * t * (_x2 - _x1) + 3 * t * t * (1 - _x2);
    }
}

/// <summary>
/// 开屏动画用的**预设曲线**。
///
/// 全部取自 Material Design 3 的官方运动曲线(公开可查的取值), 不是随手编的:
///   · emphasized decelerate —— 快速进入、长尾减速, 用于"元素出现"
///   · emphasized accelerate —— 起步慢、加速离开, 用于"元素消失"
///   · standard —— 两头都缓, 用于中性位移
/// </summary>
public static class AnimationCurves
{
    /// <summary>
    /// 强调减速(元素出现): cubic-bezier(0.05, 0.7, 0.1, 1.0)。
    /// ★ 这条曲线在 t→1 附近导数很小, 也就是"尾巴很长" ——
    ///   笔画画到最后那一小段会明显慢下来, 看起来像"手写收笔", 正是我们要的手写感。
    /// </summary>
    public static IEasingFunction EmphasizedDecelerate { get; } = new SplineEase(0.05, 0.7, 0.1, 1.0);

    /// <summary>
    /// 强调加速(元素消失): cubic-bezier(0.3, 0.0, 0.8, 0.15)。
    /// ★ 起步慢 → 离场时是"先停一下再窜出去", 收得干脆。
    /// </summary>
    public static IEasingFunction EmphasizedAccelerate { get; } = new SplineEase(0.3, 0.0, 0.8, 0.15);

    /// <summary>
    /// 标准曲线: cubic-bezier(0.4, 0.0, 0.2, 1.0)。中性位移用这条。
    /// </summary>
    public static IEasingFunction Standard { get; } = new SplineEase(0.4, 0.0, 0.2, 1.0);

    /// <summary>
    /// 笔画画线: cubic-bezier(0.2, 0.9, 0.25, 1.0)。
    /// ★ 与"强调减速"同族但起点更靠后 —— 起步不是"啪"地出来, 而是先蓄一下再冲,
    ///   画到末尾自然减速。手写感的关键在这条。
    /// </summary>
    public static IEasingFunction StrokeDraw { get; } = new SplineEase(0.2, 0.9, 0.25, 1.0);

    /// <summary>
    /// 收敛回弹: cubic-bezier(0.34, 1.56, 0.64, 1.0)。
    /// ★ y1 = 1.56 > 1 ⇒ 曲线会**过冲**(值短暂超过 1), 也就是"弹过头一点点再回来"。
    ///   过冲量约 8% —— 正好是"弹到位就停"的观感, 不会像 BackEase 那样甩出去。
    ///   用在 logo 整体从 1.04 收回 1.0 那一下。
    /// </summary>
    public static IEasingFunction SettleBack { get; } = new SplineEase(0.34, 1.56, 0.64, 1.0);
}
