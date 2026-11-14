using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BiliDesk.Helpers;

/// <summary>
/// 围绕元素外框的一圈进度描边(顺时针)。
///
/// ★ 为什么用 Adorner 而不是放在按钮 Content 里的 Rectangle(2026-11-12 用户实测):
///   Adorner **不参与被装饰元素的布局** —— 环画在按钮边界上(甚至略微溢出), 但不会改变
///   按钮的 DesiredSize。之前把 Rectangle 放进 Content 并按按钮尺寸撑开, 环的期望尺寸
///   把 ContentPresenter 撑大, 按钮跟着变宽, 右侧按钮被挤到可视区外。
/// </summary>
public sealed class PressRingAdorner : Adorner
{
    public PressRingAdorner(UIElement adorned) : base(adorned)
    {
        IsHitTestVisible = false;
    }

    /// <summary>0~1 的进度(顺时针从左上角开始)。</summary>
    public double Progress { get; set; }

    /// <summary>描边色(点赞红 / 投币金 / 收藏蓝)。</summary>
    public Brush Stroke { get; set; } = Brushes.Transparent;

    /// <summary>线宽。</summary>
    public double Thickness { get; set; } = 2;

    /// <summary>圆角半径(与按钮底色的圆角一致)。</summary>
    public double Radius { get; set; } = 8;

    protected override void OnRender(DrawingContext dc)
    {
        var w = AdornedElement.RenderSize.Width;
        var h = AdornedElement.RenderSize.Height;
        if (w <= Thickness || h <= Thickness) return;

        // 描边居中: 向内缩半个线宽, 让红线的外沿恰好压在按钮边框上, 不会压到相邻按钮
        var inset = Thickness / 2;
        var rect = new Rect(inset, inset, w - Thickness, h - Thickness);
        var geo = new RectangleGeometry(rect, Radius, Radius);
        var len = RingLength(rect.Width, rect.Height, Radius);
        if (len <= 0) return;

        // DashStyle 的长度单位是"线宽的倍数"(与 StrokeDashArray 同一套换算)
        var units = len / Thickness;
        var pen = new Pen(Stroke, Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            DashStyle = new DashStyle(
                new[] { Math.Clamp(Progress, 0, 1) * units, units }, 0)
        };
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>圆角矩形的周长(与 WPF 描边路径一致)。</summary>
    private static double RingLength(double w, double h, double r)
    {
        if (w <= 0 || h <= 0) return 0;
        var rr = Math.Min(r, Math.Min(w, h) / 2);
        return 2 * (w - 2 * rr) + 2 * (h - 2 * rr) + 2 * Math.PI * rr;
    }
}
