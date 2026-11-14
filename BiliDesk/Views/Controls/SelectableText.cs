using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 可自由选中复制的正文文本(图文动态的正文 / 转发原文 / 详情页正文用)。
///
/// ★ 为什么用只读 TextBox 而不是 TextBlock:
///   TextBlock 不能选中, 用户没法复制动态里的文字; TextBox 自带拖选 / Ctrl+C / 双击选词,
///   做成"看起来像 TextBlock"的只读控件, 就能同时拿到排版与复制能力。
/// ★ 为什么不用 RichTextBox(评论区的 CommentText):
///   评论区要内联表情图片, 必须 RichTextBox; 图文正文是纯文本, TextBox 更轻,
///   而且自带 TextWrapping 下的自动高度, 不必自己管 FlowDocument 的 PagePadding。
/// ★ 右键菜单被显式关掉(用户要求"去除右键复制菜单"):
///   只读 TextBox 默认会在 ContextMenuOpening 里**自己造**一个"复制"菜单,
///   所以光把 ContextMenu 设成 null 没用 —— 必须在事件里 Handled=true 才真的不弹。
/// </summary>
public class SelectableText : TextBox
{
    public SelectableText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        // 只读框默认还有一层系统底色和边框, 透明掉才能融进卡片
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        // 高度由内容撑开(不要内部滚动条)
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        TextWrapping = TextWrapping.Wrap;
        AcceptsReturn = true;
        Cursor = Cursors.IBeam;
        IsTabStop = false;
        FocusVisualStyle = null;

        // 右键菜单: 一律不弹(用户要求)。见类注释。
        ContextMenuOpening += (_, e) => e.Handled = true;
    }

    /// <summary>
    /// 拖选时不要触发卡片的"点击打开详情"。
    /// ★ 只标记 handled、**不吞掉**鼠标按下: 吞掉的话 TextBox 自己的选词/拖动选择就不工作了。
    ///   卡片那边另有判据(见 DynamicCard.OnCardClick 里对正文控件的祖先检查)。
    /// ★ 2026-11-14 起动态正文改用 <see cref="LinkText"/> / <see cref="LinkTextBlock"/>
    ///   (要能点链接), 本控件暂无用武之地; 保留是因为"纯文本、不需要链接"的轻量场景
    ///   仍然该用它 —— RichTextBox 的开销明显更大。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
    }
}