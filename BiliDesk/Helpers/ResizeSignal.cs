using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 「这次宽度变化算不算用户改了窗口大小」的判定(2026-11-21 从 <see cref="CardWall"/> 抽出)。
///
/// ★★★ 为什么必须单独判(用户报"第一次进入卡片页只排 3 列, 一秒后跳回 4 列"):
///   卡片墙的宽度会被两类完全不同的原因改小/改大 ——
///     · **用户改窗口**(拖边 / 最大化还原 / 跨 DPI 显示器): 要走"缩放中"那套, 等尺寸稳定后
///       一次性收敛(逐帧收敛会把卡片抽起来, 见 <see cref="CardWall.IsResizing"/>);
///     · **页面内部版式**(竖向滚动条出现/消失 ≈18px、导航栏在 Compact 档收窄 72→60、
///       页面内边距按档位切换): 与用户意图无关, 必须**当帧**收敛, 否则卡片还按旧槽宽折行,
///       每行就少一张, 要等 220ms 的 settle 才"啪"地跳回来 —— 那就是用户看到的一秒顿挫。
///   ⇒ 判据取**宿主窗口**的宽度: 窗口没变宽/变窄, 就一定是页面内部版式变化。
///
/// ★ 本类只管"两次读数之间窗口宽度有没有变", 取窗口、记账都在
///   <see cref="CardWall.WindowResized"/> 里。纯函数, 可单测(见 ResizeSignalTests)。
/// </summary>
public static class ResizeSignal
{
    /// <summary>
    /// 窗口宽度差到什么程度才算"变了"(DIP)。取 0.5 是布局的取整粒度:
    /// WPF 的布局尺寸在 DIP 上按 0.5 对齐, 更小的差异不可能是窗口真的动了。
    /// </summary>
    public const double Tolerance = 0.5;

    /// <summary>
    /// <paramref name="windowWidth"/> 与上次记下的 <paramref name="lastWindowWidth"/> 相比,
    /// 是不是一次真正的窗口宽度变化。
    ///
    /// ★ 上次值为 0 / 非法时返回 false —— 那是**首次播种**(墙还没记过窗口宽度),
    ///   不能因此把"滚动条出现"误判成缩放。这时候宁可走当帧收敛那条路(多算一次目标值),
    ///   也不要白等 220ms。
    /// </summary>
    public static bool IsWindowWidthChange(double lastWindowWidth, double windowWidth)
    {
        if (double.IsNaN(lastWindowWidth) || double.IsNaN(windowWidth)) return false;
        if (lastWindowWidth <= 0) return false;          // 还没播种
        if (windowWidth <= 0) return false;              // 窗口还没量出来
        return Math.Abs(windowWidth - lastWindowWidth) >= Tolerance;
    }
}
