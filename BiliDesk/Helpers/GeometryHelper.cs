using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace BiliDesk.Helpers;

/// <summary>
/// 几何小工具: 算路径长度。
///
/// ★ 为什么需要: 描边生长动画靠 <c>StrokeDashArray</c> + <c>StrokeDashOffset</c> 实现,
///   而 dash 数组里的"一段有多长"必须是这条路径的**真实长度** ——
///   写死了就会短一截(线画不到头)或长一截(线早就画完了还在空转)。
///   WPF 的 <see cref="PathGeometry"/> **没有** GetPointAtLength(那是 StreamGeometry 才有的),
///   所以这里自己按"折线化后逐段累加"来算。
/// </summary>
public static class GeometryHelper
{
    /// <summary>
    /// 路径长度(用户单位)。
    /// 折线化的容差取 0.1: 我们的字标画布是 1000x270, 0.1 的误差肉眼不可见,
    /// 而容差太大(比如 1.0)会让弧线被粗略近似, 长度偏短。
    /// </summary>
    public static double PathLength(Geometry geometry)
    {
        if (geometry == null) return 0;
        try
        {
            // ★ 容差用 ToleranceType.Relative: 绝对容差(0.1 用户单位)在 1000x270 的画布上
            //   对小笔画(比如 I 的竖, 长 150)算出的折线点数很少, 长度会偏;
            //   相对容差按几何尺寸缩放, 结果稳定得多。
            var flat = geometry.GetFlattenedPathGeometry(0.001, ToleranceType.Relative);
            double total = 0;
            foreach (PathFigure fig in flat.Figures)
            {
                var pts = new List<Point>(4) { fig.StartPoint };
                foreach (PathSegment seg in fig.Segments)
                {
                    // 折线化之后只会有 LineSegment, 但不同类型的安全取法不一样:
                    // LineSegment/ArcSegment 用 Point, Bezier 用 Point1~3, PolyLine 用 Points。
                    switch (seg)
                    {
                        case LineSegment ls:
                            pts.Add(ls.Point);
                            break;
                        case PolyLineSegment pls:
                            foreach (var pt in pls.Points) pts.Add(pt);
                            break;
                        case BezierSegment bs:
                            pts.Add(bs.Point2);
                            pts.Add(bs.Point3);
                            break;
                        case QuadraticBezierSegment qbs:
                            pts.Add(qbs.Point2);
                            break;
                        case ArcSegment asg:
                            pts.Add(asg.Point);
                            break;
                    }
                }

                for (var i = 1; i < pts.Count; i++)
                {
                    var dx = pts[i].X - pts[i - 1].X;
                    var dy = pts[i].Y - pts[i - 1].Y;
                    total += Math.Sqrt(dx * dx + dy * dy);
                }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
