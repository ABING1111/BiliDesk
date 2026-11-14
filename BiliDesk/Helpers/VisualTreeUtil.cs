using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace BiliDesk.Helpers;

/// <summary>
/// 可视树查找工具。
///
/// 为什么单独抽出来: "这次点击到底落在哪个控件上"是列表页普遍要问的问题 ——
/// 行的点击处理里得先看看是不是点在复选框/按钮上, 是就不能再走行的逻辑
/// (复选框自己会翻转 IsSelected, 事件再冒泡到行这一层又翻一次, 结果就是"点了没反应")。
/// 历史页和缓存页各写过一份逐字相同的实现, 合并到这里。
/// </summary>
public static class VisualTreeUtil
{
    /// <summary>
    /// 从 <paramref name="d"/> 开始往上找第一个 T 类型的祖先(含自身)。找不到返回 null。
    ///
    /// ★★★ 必须同时兼容 **ContentElement**(2026-11-07 用户报错弹框:
    ///   “System.Windows.Documents.Run”不是 Visual 或 Visual3D)。
    ///   WPF 的鼠标 OriginalSource 不保证是 Visual: 点在 TextBlock 的文字上时可能是
    ///   它内部的 <see cref="Run"/>(一个 FrameworkContentElement, 不在可视树里)。
    ///   直接对它调 <see cref="VisualTreeHelper.GetParent"/> 会抛 InvalidOperationException,
    ///   而全局滚轮处理器(WheelScroll.OnWheel)正好每一步滚轮都要问一次"鼠标所在的是哪个
    ///   ScrollViewer" —— 于是只要滚轮压在任何带文字的控件上就会弹一次错误框。
    ///   处理: 遇到非 Visual 先沿 ContentElement/逻辑树往上跳到最近的 Visual, 再继续。
    /// </summary>
    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        => FindAncestor<T>(d, static _ => true);

    /// <summary>
    /// 带条件的祖先查找: 只返回第一个满足 <paramref name="match"/> 的 T。
    ///
    /// ★ 为什么需要条件版(2026-11-14, 修"嵌套滚动区吞滚轮"):
    ///   全局滚轮要从鼠标位置往上找"**能纵向滚的**" ScrollViewer。只看类型的话,
    ///   第一个命中的可能是内容不满一屏的嵌套小列表(MaxHeight 的直播看板、
    ///   弹层里的规则列表…), 它滚不动, 后面的页面却因为"最近祖先是它"被挡住。
    ///   有了条件版就能跳过它继续往上, 与 WPF 原生冒泡行为一致。
    /// </summary>
    public static T? FindAncestor<T>(DependencyObject? d, Func<T, bool> match) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t && match(t)) return t;
            d = GetParent(d);
        }
        return null;
    }

    /// <summary>沿可视树/内容元素/逻辑树往上取一级父级(兼容 Run 这类 FrameworkContentElement)。</summary>
    public static DependencyObject? GetParent(DependencyObject? d)
    {
        if (d == null) return null;

        if (d is Visual || d is Visual3D)
        {
            // 正常路径: 可视树父级
            return VisualTreeHelper.GetParent(d);
        }
        if (d is FrameworkContentElement fce)
        {
            // Run/Span/Paragraph 这类内容元素: 先回到它所属的 TextBlock(或其父 Span)
            return fce.Parent;
        }

        // 其它不在可视树里的 DependencyObject: 退回逻辑树; 个别类型 LogicalTreeHelper
        // 也会抛, 兜住并当作"到头了"。
        try { return LogicalTreeHelper.GetParent(d); }
        catch { return null; }
    }
}
