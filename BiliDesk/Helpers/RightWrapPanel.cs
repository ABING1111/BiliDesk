using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace BiliDesk.Helpers;

/// <summary>
/// 从左到右依次排、放不下就折行, 且**每一行都靠右对齐**的流式面板(2026-11-27 新增)。
///
/// 用在「我的」页页首右侧那四枚快捷入口上: 窗口变窄时它们先折成两行留在资料头右边,
/// 连两行都放不下才落到资料头下方(落位判定见 MinePage.UpdateQuickEntryPlacement)。
///
/// ★ 为什么不用 WrapPanel: WrapPanel 只会让**第一行**贴着给定的宽度, 后面的行都从左边起排。
///   面板整体靠右、内容折成两行时, 第二行就贴在右侧那块区域的**最左边** ——
///   右边看是齐的, 两行的起点却一长一短, 很乱。这里让每一行都从右边起算。
/// ★ 行间距/项间距都用子元素自己的 Margin 表达(与 WrapPanel 一致), 面板不另设属性。
/// ★ 子元素比可用宽度还宽时独占一行并向右溢出(与 WrapPanel 的处理一致), 不缩放、不裁剪。
/// </summary>
public class RightWrapPanel : Panel
{
    /// <summary>
    /// 折行时是否**均分**到各行(而不是"每行能塞几个塞几个")。
    ///
    /// ★ 关掉时是贪心折行: 右侧宽度够放三枚时, 四枚会被排成"上面三个、下面一个"
    ///   (2026-11-27 用户截图反馈)。打开后先按贪心算出需要几行, 再把子元素平均分给每一行
    ///   —— 四枚两行就是 2 + 2。均分后若某一行反而放不下, 退回贪心的结果(至少不会溢出)。
    /// </summary>
    public static readonly DependencyProperty BalanceLinesProperty = DependencyProperty.Register(
        nameof(BalanceLines), typeof(bool), typeof(RightWrapPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool BalanceLines
    {
        get => (bool)GetValue(BalanceLinesProperty);
        set => SetValue(BalanceLinesProperty, value);
    }

    /// <summary>一次 Measure 里算出的行: 起始下标 / 个数 / 行宽 / 行高。Arrange 直接照它摆。</summary>
    private readonly List<(int Start, int Count, double Width, double Height)> _lines = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        // 无限宽(Auto 列 / 横向 StackPanel 里)= 不折行
        var limit = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;

        foreach (UIElement child in InternalChildren)
            child.Measure(availableSize);

        var greedy = BuildGreedy(limit);
        _lines.Clear();
        _lines.AddRange(BalanceLines && greedy.Count > 1 ? Balance(greedy.Count, limit, greedy) : greedy);

        double totalHeight = 0, maxWidth = 0;
        foreach (var line in _lines)
        {
            totalHeight += line.Height;
            maxWidth = System.Math.Max(maxWidth, line.Width);
        }

        // 高度照实上报; 宽度也照实 —— 面板自己靠右对齐时拿到的就是这个"最长一行"的宽
        return new Size(maxWidth, totalHeight);
    }

    /// <summary>"能塞几个塞几个"的折行。每行从左往右放, 放不下就换行。</summary>
    private List<(int Start, int Count, double Width, double Height)> BuildGreedy(double limit)
    {
        var lines = new List<(int, int, double, double)>();
        double x = 0, lineHeight = 0;
        var start = 0;
        var count = 0;

        foreach (UIElement child in InternalChildren)
        {
            // DesiredSize 含 Margin —— 间距因此自动算进排列里
            var size = child.DesiredSize;

            // 已经有内容且再放就超宽 ⇒ 换行。留 0.5 的容差: 设备像素取整后"刚好放得下"的
            // 那一行不该因为小数误差被挤到下一行去。
            if (count > 0 && x + size.Width > limit + 0.5)
            {
                lines.Add((start, count, x, lineHeight));
                start += count;
                count = 0;
                x = 0;
                lineHeight = 0;
            }

            x += size.Width;
            lineHeight = System.Math.Max(lineHeight, size.Height);
            count++;
        }

        if (count > 0) lines.Add((start, count, x, lineHeight));
        return lines;
    }

    /// <summary>把子元素平均分到 <paramref name="lineCount"/> 行(前面的行可以多一枚)。放不下就返回贪行的结果。</summary>
    private List<(int Start, int Count, double Width, double Height)> Balance(
        int lineCount, double limit,
        List<(int Start, int Count, double Width, double Height)> fallback)
    {
        var total = InternalChildren.Count;
        var perLine = (total + lineCount - 1) / lineCount;

        var lines = new List<(int, int, double, double)>();
        for (var start = 0; start < total; start += perLine)
        {
            var count = System.Math.Min(perLine, total - start);
            double width = 0, height = 0;
            for (var i = start; i < start + count; i++)
            {
                var size = InternalChildren[i].DesiredSize;
                width += size.Width;
                height = System.Math.Max(height, size.Height);
            }
            // 均分之后这一行反而放不下: 整体退回贪心结果
            if (width > limit + 0.5) return fallback;
            lines.Add((start, count, width, height));
        }
        return lines;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var (start, count, width, height) in _lines)
        {
            // 每一行都从右边起算
            var lineX = finalSize.Width - width;
            for (var i = start; i < start + count; i++)
            {
                var child = InternalChildren[i];
                // 槽位高度取行高(而不是各自的 DesiredSize.Height): 同一行里子元素默认纵向拉伸,
                // 高度一致时没差别, 不一致时这一行仍然等高、底边对齐。
                child.Arrange(new Rect(lineX, y, child.DesiredSize.Width, height));
                lineX += child.DesiredSize.Width;
            }
            y += height;
        }
        return finalSize;
    }
}
