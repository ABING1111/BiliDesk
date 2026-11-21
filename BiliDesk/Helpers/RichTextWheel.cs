using System.Windows;
using System.Windows.Input;

namespace BiliDesk.Helpers;

/// <summary>
/// 只读富文本框(RichTextBox)的滚轮转交: 一律让外层滚动区来滚。
///
/// ★★ 为什么需要(2026-11-22 修"鼠标压在简介上滚轮无法滚动"):
///   RichTextBox 的模板里有一个自己的 ScrollViewer。平时内容高度自适应、没得滚;
///   但控件一旦被 MaxHeight 压住(播放器简介折叠就是 108), 那个内部 ScrollViewer
///   就报出 ScrollableHeight>0(实测 extent 304.8 / viewport 108)。而 WPF 的
///   ScrollViewer.OnMouseWheel 只要 HandlesMouseWheelScrolling(默认 true)就
///   **无条件**把 MouseWheel 标成 Handled —— 连滚动条是 Disabled 都不看 ——
///   于是滚轮滚的是"折叠框里看不见的那几行", 外层 InfoScroll 一动不动。
///   本项目的这几个只读文本框都把滚动条设成 Disabled(高度由内容撑开, 折叠交给外部),
///   它们本来就不该自己滚, 所以在这里**统一改交给外层**。
///
/// ★ 为什么在 Preview(隧道)里接管, 而不是等冒泡:
///   内层 ScrollViewer 就在冒泡路径上, 且它先于外层收到事件。预览与冒泡是一对
///   共用事件数据的孪生事件(微软文档: 预览里置 Handled, 冒泡那份也是 Handled),
///   所以在隧道阶段置 Handled, 内层就会在自己的 OnMouseWheel 开头直接 return。
///
/// ★ 为什么重新发到 Parent, 而不是原控件:
///   RichTextBox 自己也可能挂着滚轮处理, 在原控件上重发会被再吃一次;
///   从 Parent 起跳则完全跳过它的内部滚动区, 事件正常冒泡到页面级 ScrollViewer,
///   由 <see cref="WheelScroll"/> 接着做平滑滚动(距离/手感与其它地方完全一致)。
/// </summary>
public static class RichTextWheel
{
    /// <summary>
    /// 给只读 RichTextBox 挂上"滚轮交给外层"。只挂在**确定不需要内部滚动**的控件上
    /// (它们的 VerticalScrollBarVisibility 都是 Disabled, 见各控件的构造函数)。
    /// </summary>
    public static void ForwardToParent(FrameworkElement control)
    {
        control.PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled) return;      // 祖先已经决定怎么处理, 别抢
            e.Handled = true;

            // 还没进可视树(或 Parent 不是 UIElement): 没地方可转发, 静默结束。
            if (control.Parent is not UIElement parent) return;

            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
                Source = control,
            });
        };
    }
}
