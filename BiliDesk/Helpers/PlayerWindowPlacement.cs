using System;
using System.Collections.Generic;

namespace BiliDesk.Helpers;

/// <summary>
/// 播放器窗口"大小 + 位置记忆"的**纯逻辑**部分(不依赖 WPF/显示器 API, 可直接单测)。
///
/// ★ 为什么单独拆出来:
///   几何夹取这段逻辑是最容易出错、又最难在真机上复现的部分(多屏负坐标、副屏被拔、
///   窗口比工作区还大……)。把它做成纯 DIP 矩形运算, 就能在 BiliDesk.Tests 里穷举,
///   而不是靠"摆一台 125% 的竖屏副屏"去试。
/// ★ 这里只管**坐标与可见性**, 不管 DPI 换算与 SetWindowPos —— 那些留在 PlayerWindow
///   里做(需要 HWND 与当前显示器 DPI)。
/// </summary>
public static class PlayerWindowPlacement
{
    /// <summary>窗口几何(DIP; 左上角 + 宽高)。命名避开 <see cref="System.Windows.Rect"/>。</summary>
    public readonly record struct PlacementRect(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    /// <summary>
    /// 几何是否可用于记忆。0/负数、NaN、无穷大一律拒绝:
    /// 手改配置、读到了尚未布局的窗口、或把全屏尺寸当常规尺寸都走这一道闸。
    /// </summary>
    public static bool IsUsable(PlacementRect rect, double minWidth, double minHeight)
    {
        return double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
            && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
            && rect.Width >= minWidth && rect.Height >= minHeight;
    }

    /// <summary>两个矩形的重叠面积(不相交 = 0)。</summary>
    public static double IntersectionArea(PlacementRect a, PlacementRect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>
    /// 把窗口夹进 <paramref name="bounds"/>(虚拟桌面或某个显示器的工作区):
    /// 允许窗口有一部分在边界外, 但**至少** <paramref name="visibleMinWidth"/>×
    /// <paramref name="visibleMinHeight"/> 的区域留在里面(标题栏一定可抓)。
    ///
    /// ★ 多屏负坐标是合法位置(副屏在主屏左侧/上方), 调用方给 bounds 时要把负区间包含进去,
    ///   本方法自身不做"必须为正"的判断 —— 那会把合法的副屏位置误杀。
    /// </summary>
    public static PlacementRect ClampToBounds(
        PlacementRect desired, PlacementRect bounds,
        double visibleMinWidth, double visibleMinHeight)
    {
        // 无效输入原样返回: 调用方会先用 IsUsable 判断。这里不抛异常, 免得一条坏配置崩窗口。
        if (!double.IsFinite(desired.Left) || !double.IsFinite(desired.Top)
            || !double.IsFinite(desired.Width) || !double.IsFinite(desired.Height))
            return desired;
        if (bounds.Width <= 0 || bounds.Height <= 0) return desired;

        // 可见下限不能超过窗口自身(小窗口至少整窗可见), 也不能超过边界(边界比可见区还窄时,
        // 把整条边界都露出来就是最优解, 不能因为"凑不够 160"把窗口推到边界外)。
        var visibleW = Math.Clamp(visibleMinWidth, 0, Math.Min(desired.Width, bounds.Width));
        var visibleH = Math.Clamp(visibleMinHeight, 0, Math.Min(desired.Height, bounds.Height));

        var minLeft = bounds.Left - (desired.Width - visibleW);
        var maxLeft = bounds.Right - visibleW;
        var minTop = bounds.Top - (desired.Height - visibleH);
        var maxTop = bounds.Bottom - visibleH;

        // 边界窄到放不下"可见区"时(极端小屏/手改配置), 夹成边界左/上, 避免 Math.Clamp(min>max) 抛异常。
        if (minLeft > maxLeft) minLeft = maxLeft = bounds.Left;
        if (minTop > maxTop) minTop = maxTop = bounds.Top;

        return desired with
        {
            Left = Math.Clamp(desired.Left, minLeft, maxLeft),
            Top = Math.Clamp(desired.Top, minTop, maxTop),
        };
    }

    /// <summary>
    /// 从多个候选工作区(每个显示器一块)里挑**与目标重叠最多**的那块并夹取。
    /// 一块都没有重叠(副屏被拔掉 / 显示器重新排列)时返回 null —— 由调用方决定
    /// "退回主屏居中", 而不是把窗口硬塞进某个缝里。
    /// </summary>
    public static PlacementRect? ClampToBestWorkArea(
        PlacementRect desired, IReadOnlyList<PlacementRect> workAreas,
        double visibleMinWidth, double visibleMinHeight)
    {
        if (workAreas == null || workAreas.Count == 0) return null;

        var best = workAreas[0];
        var bestArea = IntersectionArea(desired, best);
        for (var i = 1; i < workAreas.Count; i++)
        {
            var area = IntersectionArea(desired, workAreas[i]);
            if (area > bestArea)
            {
                bestArea = area;
                best = workAreas[i];
            }
        }

        if (bestArea <= 0) return null;
        return ClampToBounds(desired, best, visibleMinWidth, visibleMinHeight);
    }
}
