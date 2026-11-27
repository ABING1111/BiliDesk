using System;
using System.Windows;
using System.Windows.Controls;

namespace BiliDesk.Helpers;

/// <summary>
/// 全局缩短「鼠标悬停多久才出现 ToolTip」的等待时间(2026-11-27 用户要求)。
///
/// ★ 为什么改 DependencyProperty 的元数据, 而不是在 Styles.xaml 里加一条 Setter:
///   ToolTipService.InitialShowDelay 是**附加属性且不继承**(没有 FrameworkPropertyMetadataOptions.Inherits),
///   写在 ToolTip 的 Style 里改不到"宿主元素"上的延时, 在窗口/页面根上设一次也传不到子元素;
///   全项目 190+ 处 ToolTip 逐个挂值更不现实。
///   ⇒ 用 DependencyProperty.AddOwner 给 FrameworkElement 补一份默认元数据:
///     之后所有 FrameworkElement(及其派生控件)读到的默认值就是这里给的值,
///     某个元素上显式设过 InitialShowDelay 的仍然以它自己的为准。
/// ★ 默认值取系统 MouseHoverTime(本机 1000ms)。缩到 250ms: 明显更快, 又不至于
///   鼠标扫过按钮时到处弹提示。
/// </summary>
internal static class ToolTipTiming
{
    private const int DelayMs = 250;

    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            ToolTipService.InitialShowDelayProperty.AddOwner(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(DelayMs));
        }
        catch (InvalidOperationException)
        {
            // 同一进程里 "FrameworkElement 已是该属性的 owner" 后再加会抛(探针会多次建 App)。
            // 这不是功能错误 —— 延时已经短了。
        }
    }
}
