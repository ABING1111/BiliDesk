using System.Windows;
using System.Windows.Media;

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
    /// </summary>
    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
