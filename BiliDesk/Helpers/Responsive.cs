using System;
using System.ComponentModel;
using System.Windows;

namespace BiliDesk.Helpers;

/// <summary>
/// 窗口宽度档位。
///
/// ★ **枚举名必须与 XAML 里 VisualState 的 `x:Name` 逐字一致** —— 代码是拿 `ToString()` 当状态名
///   传给 `VisualStateManager.GoToElementState` 的。少一个字母不会报错，只会静默地什么都不做
///   (GoToElementState 找不到状态就返回 false)，所以探针里有专门一条断言把这三个名字钉住。
/// </summary>
public enum WidthClass { Compact, Medium, Wide }

/// <summary>
/// 主窗口的"响应式布局"机制 —— 把窗口宽度归成三档，档位一变就让挂了 VisualState 的元素切状态。
///
/// ## 为什么不用"每个元素自己听 SizeChanged 再改属性"
/// 那样写有两个致命问题：
///   1. **反馈回路**：导航栏在窄屏收窄 → 内容区反而变宽 → 内容区里的元素又判定"我不窄了" → 来回抖。
///   2. **重绘风暴**：拖动窗口时 SizeChanged 每帧都发，每次都改一批属性 = 整个界面逐帧重排。
///
/// 这里的做法是把"档位"变成**唯一真相源**，且只按**窗口**宽度算：
///   - 只有一处（主窗口根元素）算档，结果写进一个 **Inherits 的附加属性**，整棵可视树自动读到同一份值；
///   - 内部布局怎么变都不影响档位 ⇒ 没有反馈回路；
///   - 带**迟滞**（进/出用不同阈值，20px 死区）⇒ 指针停在断点上不会左右横跳；
///   - 只在**真的跨档**时才通知 ⇒ 拖动一整趟下来通常只动作 1~2 次，不是几百次。
///
/// ## 用法
/// 主窗口根元素（唯一真相源）：
/// <code>helpers:Responsive.Source="True" helpers:Responsive.Enable="True"</code>
/// 想跟着重排的页面/控件根元素：
/// <code>helpers:Responsive.Enable="True"</code> + 自己那组 &lt;VisualStateManager.VisualStateGroups&gt;
///
/// 状态名就是 <c>Compact</c> / <c>Medium</c> / <c>Wide</c>；把"正常尺寸"那档留空 Storyboard 即可 ——
/// 退出某个状态时 WPF 会撤掉它设的动画值、自动回到设计值，配合
/// <c>VisualTransition.GeneratedDuration</c> 连"回退"也是平滑的。
/// </summary>
public static class Responsive
{
    // ------------------------------------------------------------ 断点
    //
    // 全部按**窗口宽度**(DIP)算。窗口 MinWidth 是 1060，所以 Compact 这一档在最小宽度附近是真实可达的。
    //
    // ★ 每组"进/出"两个阈值相差 20px —— 这就是迟滞死区。取 20 的理由：拖动窗口时指针不可能停得
    //   比 20px 还稳，而死区再大手感就变成"该变的时候没变"。

    /// <summary>Compact → Medium 的**进入**阈值（宽度涨到这儿才升档）</summary>
    public const double CompactEnter = 1200;

    /// <summary>Medium → Compact 的**退出**阈值（宽度缩到这儿以下才降档）</summary>
    public const double CompactExit = 1180;

    /// <summary>Medium → Wide 的**进入**阈值</summary>
    public const double WideEnter = 1540;

    /// <summary>Wide → Medium 的**退出**阈值</summary>
    public const double WideExit = 1520;

    /// <summary>
    /// 按宽度和当前档位算下一档。**纯函数** —— 可以在离屏探针里把整条拖动路径跑一遍验证不抖。
    ///
    /// 注意允许一次跨两档（例如从最小宽度直接最大化），所以向上判断是"先看够不够 Wide"而不是逐档+1。
    /// </summary>
    public static WidthClass Classify(double width, WidthClass current) => current switch
    {
        WidthClass.Compact => width >= WideEnter ? WidthClass.Wide
                            : width >= CompactEnter ? WidthClass.Medium
                            : WidthClass.Compact,

        WidthClass.Medium => width >= WideEnter ? WidthClass.Wide
                           : width < CompactExit ? WidthClass.Compact
                           : WidthClass.Medium,

        // Wide
        _ => width < CompactExit ? WidthClass.Compact
           : width < WideExit ? WidthClass.Medium
           : WidthClass.Wide,
    };

    // ------------------------------------------------------------ 附加属性

    /// <summary>
    /// 标记"这一处是档位的**真相源**"：按自己的宽度算档并写进 <see cref="ClassProperty"/>。
    /// **全应用只应有一处**（主窗口根元素）—— 挂两处就是两个真相源互相覆盖。
    /// </summary>
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source", typeof(bool), typeof(Responsive),
            new PropertyMetadata(false, OnSourceChanged));

    public static bool GetSource(DependencyObject d) => (bool)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, bool value) => d.SetValue(SourceProperty, value);

    /// <summary>
    /// 标记"这一处要跟着档位切 VisualState"：读继承来的档位，跨档时调 <c>GoToElementState</c>。
    /// 页面/控件的根元素都挂它；元素上没有对应的状态组时是无害的空操作。
    /// </summary>
    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable", typeof(bool), typeof(Responsive),
            new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject d) => (bool)d.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject d, bool value) => d.SetValue(EnableProperty, value);

    /// <summary>
    /// 当前档位。**Inherits = true** —— 真相源写在窗口根上，整棵可视树（含各页面）自动读到同一份值。
    /// 这是"单一真相源"能成立的关键，也是没有反馈回路的原因。
    /// </summary>
    public static readonly DependencyProperty ClassProperty =
        DependencyProperty.RegisterAttached(
            "Class", typeof(WidthClass), typeof(Responsive),
            new FrameworkPropertyMetadata(WidthClass.Wide, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>读当前档位。页面 code-behind 也可以用它做"布局之外"的调整（例如改一段几何常量）。</summary>
    public static WidthClass GetClass(DependencyObject d) => (WidthClass)d.GetValue(ClassProperty);

    // 内部用的两个记账位（不对外）
    private static readonly DependencyProperty SubscribedProperty =
        DependencyProperty.RegisterAttached("Subscribed", typeof(bool), typeof(Responsive),
            new PropertyMetadata(false));

    private static readonly DependencyProperty FirstAppliedProperty =
        DependencyProperty.RegisterAttached("FirstApplied", typeof(bool), typeof(Responsive),
            new PropertyMetadata(false));

    // ------------------------------------------------------------ 真相源

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) el.SizeChanged += OnSourceSizeChanged;
        else el.SizeChanged -= OnSourceSizeChanged;
        UpdateClass(el);
    }

    private static void OnSourceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement el) UpdateClass(el);
    }

    /// <summary>
    /// 重新判档并（仅在真跨档时）写回。
    ///
    /// ★ **"只在跨档时写"是抗重绘的全部要点**：拖动窗口期间 SizeChanged 每帧都发，但 99% 的调用
    ///   都算回原档，直接 return；真正通知下游的可能一次都没有。别改成"每次都写" —— 那样
    ///   继承属性每帧变一次，全树的 GoToElementState 会跟着每帧跑一遍。
    /// </summary>
    private static void UpdateClass(FrameworkElement el)
    {
        var w = el.ActualWidth;
        // 首次布局之前 ActualWidth 是 0：这时判档只会得到一个假的 Compact，等真布局跑完再判
        if (double.IsNaN(w) || w <= 0) return;

        var current = GetClass(el);
        var next = Classify(w, current);
        if (next == current) return;
        el.SetValue(ClassProperty, next);
    }

    // ------------------------------------------------------------ 应用端

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;

        if (!(bool)e.NewValue)
        {
            el.Loaded -= OnLoaded;
            el.Unloaded -= OnUnloaded;
            Unsubscribe(el);
            return;
        }

        el.Loaded += OnLoaded;
        el.Unloaded += OnUnloaded;
        if (el.IsLoaded) OnLoaded(el, new RoutedEventArgs());
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        Subscribe(el);

        // 首次挂载时**不带动画**落一次状态。带的话启动瞬间（以及每次切页回来）会看到一段
        // "从默认档滑到当前档"的过渡 —— 用户没在拖窗口，却看到东西自己在动，很像故障。
        if ((bool)el.GetValue(FirstAppliedProperty)) return;
        el.SetValue(FirstAppliedProperty, true);
        Apply(el, animate: false);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 摘出可视树就退订：依赖属性描述符的 AddValueChanged 会**强引用宿主**，
        // 不退订的话每个短命元素（例如列表里被回收的卡片）都会在描述符的全局表里留一条。
        if (sender is FrameworkElement el) Unsubscribe(el);
    }

    private static void OnClassChanged(object? sender, EventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        // 还没做过首次应用的话交给 OnLoaded —— 那里能保证"不带动画"
        if (!(bool)el.GetValue(FirstAppliedProperty)) return;
        Apply(el, animate: true);
    }

    private static void Subscribe(FrameworkElement el)
    {
        if ((bool)el.GetValue(SubscribedProperty)) return;
        el.SetValue(SubscribedProperty, true);
        DependencyPropertyDescriptor
            .FromProperty(ClassProperty, typeof(FrameworkElement))
            ?.AddValueChanged(el, OnClassChanged);
    }

    private static void Unsubscribe(FrameworkElement el)
    {
        if (!(bool)el.GetValue(SubscribedProperty)) return;
        el.SetValue(SubscribedProperty, false);
        DependencyPropertyDescriptor
            .FromProperty(ClassProperty, typeof(FrameworkElement))
            ?.RemoveValueChanged(el, OnClassChanged);
    }

    private static void Apply(FrameworkElement el, bool animate)
    {
        // 没有对应状态组的元素照样调 —— GoToElementState 找不到就返回 false，不会抛也不会改任何东西
        VisualStateManager.GoToElementState(el, GetClass(el).ToString(), animate);
    }
}
