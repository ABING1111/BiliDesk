using System;
using System.Windows;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 卡片墙"铺满宽度"的规则：按容器实际宽度算出一行放几张、每张多宽，让卡片**随窗口大小一起变**，
/// 而不是固定 236 宽、右边空一截（或塞不下就换行）。
///
/// ## 挂在哪儿
/// 挂在卡片墙的 `<see cref="WrapPanel"/>` 上（`helpers:CardWall.Enable="True"`）。
/// 挂 WrapPanel 而不是 ItemsControl，是因为 WrapPanel 的 `ActualWidth` 正好就是"能摆卡片的宽度"，
/// 而它又是卡片的直接父级 —— 算出来的值写在**可继承的附加属性**上，卡片直接就能读到，不用逐级绑定。
///
/// ## 为什么要有迟滞
/// 卡片墙在 ScrollViewer 里、滚动条是 Auto：一旦内容超过一屏，竖向滚动条出现，
/// WrapPanel 的可用宽度会**突然少 17px**。按"算出来是几列就几列"的写法，这一下可能正好踩过列数阈值，
/// 于是"刚开始滚动，整墙从 4 列跳成 3 列" —— 非常难看。
/// 所以列数带死区（<see cref="Dead"/>）：已经在 N 列的，得真的窄出去 24px 才降到 N-1 列。
///
/// ## 与 VideoCard 的耦合（改一处要改两处）
/// 卡片自己的外边距是 `Margin="8,10"`（左右各 8）。<see cref="Gap"/> 必须等于这个水平外边距之和，
/// 否则"每行正好 N 张"就不成立。探针里有一条断言钉着这个关系。
/// </summary>
public static class CardWall
{
    /// <summary>每张卡片占的横向**额外**空档 = VideoCard 的左右外边距之和(8 + 8)</summary>
    public const double Gap = 16;

    /// <summary>一列至少这么宽。低于它就减一列 —— 这个值决定了"窗口多大时是几列"</summary>
    public const double MinItemWidth = 220;

    /// <summary>列数的迟滞死区(折算成卡片宽度)。见类型注释里"滚动条"那段</summary>
    private const double Dead = 24;

    /// <summary>
    /// 每列预留的取整余量(DIP)。见 <see cref="Update"/> 里的说明 —— 专门用来吸收
    /// <c>UseLayoutRounding</c> 在非整数 DPI 下给每张卡带来的 &lt;0.5 DIP 放大。
    /// </summary>
    private const double RoundSlack = 1.0;

    /// <summary>
    /// 挂到卡片墙的 WrapPanel 上。**不挂就完全不参与**：卡片仍按设计宽度 236 排。
    /// </summary>
    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable", typeof(bool), typeof(CardWall),
            new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject d) => (bool)d.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject d, bool value) => d.SetValue(EnableProperty, value);

    /// <summary>
    /// 每张卡片应有的宽度。**Inherits** —— 写在 WrapPanel 上，卡片直接继承到。
    /// 默认值就是设计值，所以没挂 Enable 的列表行为与从前完全一致。
    /// </summary>
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.RegisterAttached(
            "ItemWidth", typeof(double), typeof(CardWall),
            new FrameworkPropertyMetadata(236.0, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetItemWidth(DependencyObject d) => (double)d.GetValue(ItemWidthProperty);

    /// <summary>
    /// 封面高度。跟着宽度按 16:9 走 —— 否则卡片变宽了封面还是 132 高，画面会被拉扁。
    /// 同样 Inherits，同样默认设计值。
    /// </summary>
    public static readonly DependencyProperty CoverHeightProperty =
        DependencyProperty.RegisterAttached(
            "CoverHeight", typeof(double), typeof(CardWall),
            new FrameworkPropertyMetadata(132.0, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetCoverHeight(DependencyObject d) => (double)d.GetValue(CoverHeightProperty);

    /// <summary>上次算出来的列数(迟滞要用它当起点; 0 = 还没算过)</summary>
    private static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.RegisterAttached("Columns", typeof(int), typeof(CardWall),
            new PropertyMetadata(0));

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if ((bool)e.NewValue) el.SizeChanged += OnWallSizeChanged;
        else el.SizeChanged -= OnWallSizeChanged;
        Update(el);
    }

    private static void OnWallSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement el) Update(el);
    }

    private static void Update(FrameworkElement wall)
    {
        var avail = wall.ActualWidth;
        // 首次布局前是 0：这时算出来的列数没有意义，等真布局跑完再算
        if (double.IsNaN(avail) || avail <= 0) return;

        // ★★ 拖动窗口边缘时的**合流**(见 NotifyModalResize)。这里必须直接 return:
        //    一旦往下算, SetValue 打的是**可继承**属性 ⇒ 整棵子树失效 ⇒ 全墙卡片重新测量,
        //    而拖动期间 WM_SIZE 每帧都来 —— 那就是"拖边缘一卡一卡"的来源。
        if (_modalResizing)
        {
            _pendingWall = wall;
            EnsureThrottleTimer();
            return;
        }

        ApplyTo(wall, avail);
    }

    /// <summary>
    /// 真正算一次并写下去。`avail` 由调用方给, 免得节流补算时又读一次可能已经变了的宽度。
    /// </summary>
    private static void ApplyTo(FrameworkElement wall, double avail)
    {
        var cols = ColumnsFor(avail, (int)wall.GetValue(ColumnsProperty));
        wall.SetValue(ColumnsProperty, cols);

        // ★ 槽位宽必须**向下取整**, 而且还要留出**取整余量**。
        //
        // 两个都是"每行少一张"的直接原因, 实测踩过(用户截图: 窗口缩到最小宽度后只排 3 张,
        // 右边空一大块):
        //   ① 向上取整: cols 个槽位加起来超过 avail, WrapPanel 就把最后一张挤到下一行;
        //   ② **UseLayoutRounding**(主窗口开着) + 125% 缩放: 每一张卡的实际占位会被按**设备像素**
        //      取整放大最多 0.4 DIP(230 DIP → 287.5 设备px → 288 → 230.4 DIP)。4 张就是 1.6 DIP,
        //      而 `floor(avail/cols)` 只留了 `avail - cols*slot` 那点余量(实测只有 1 DIP) ⇒ 正好被吃掉。
        // 所以按**每列 1 DIP** 预留: 最多损失 cols 个 DIP(肉眼不可辨), 换"算几列就真的排几列"。
        var slot = Math.Floor((avail - cols * RoundSlack) / cols);
        var item = Math.Max(80.0, slot - Gap);
        var cover = Math.Round(item * 9.0 / 16.0);

        // 值真的变了才写。拖动窗口时 SizeChanged 每帧都进来，而宽度大部分时候没变到"跨过整数像素"，
        // 无条件 SetValue 会让这个可继承属性每帧都广播一次子树。
        if (Math.Abs(GetItemWidth(wall) - item) > 0.01) wall.SetValue(ItemWidthProperty, item);
        if (Math.Abs(GetCoverHeight(wall) - cover) > 0.01) wall.SetValue(CoverHeightProperty, cover);
    }

    // ------------------------------------------------------------ 拖动缩放时的重算节流

    /// <summary>拖动期间最多这么久重算一次。见 <see cref="NotifyModalResize"/></summary>
    private const int ThrottleMs = 120;

    /// <summary>用户是否正拖着窗口边缘(Win32 的模态缩放循环)</summary>
    private static bool _modalResizing;

    /// <summary>节流期间攒下的"最后那个要算的墙"</summary>
    private static FrameworkElement? _pendingWall;

    private static DispatcherTimer? _throttleTimer;

    /// <summary>
    /// 由 `FluentWindow` 在 `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE` 时调用。
    ///
    /// 拖动期间把重算**合流到 ~120ms 一次**, 松手时立刻补算收尾。这样拖动过程里列数仍然会变
    /// (有反馈、不是冻住), 但不会每帧推翻整墙卡片的测量。
    /// 非拖动场景(启动、切页、最大化)完全不受影响 —— 那些本来就是一次性的, 照样立刻算。
    /// </summary>
    public static void NotifyModalResize(bool entering)
    {
        _modalResizing = entering;
        if (entering) return;

        // 松手: 先收掉定时器, 再立刻把攒下的那次算掉(不靠定时器, 免得最后一下要等 120ms)
        StopThrottleTimer();
        FlushPending();
    }

    private static void EnsureThrottleTimer()
    {
        if (_throttleTimer != null) return;
        _throttleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ThrottleMs) };
        _throttleTimer.Tick += (_, _) =>
        {
            StopThrottleTimer();
            FlushPending();
        };
        _throttleTimer.Start();
    }

    private static void StopThrottleTimer()
    {
        _throttleTimer?.Stop();
        _throttleTimer = null;
    }

    /// <summary>把攒下的最后一次算掉。攒着的墙可能已经不在可视树上了(切页), 那就读不到宽度、直接跳过。</summary>
    private static void FlushPending()
    {
        var wall = _pendingWall;
        _pendingWall = null;
        if (wall == null) return;

        var avail = wall.ActualWidth;
        if (double.IsNaN(avail) || avail <= 0) return;
        ApplyTo(wall, avail);
    }

    /// <summary>
    /// 按可用宽度算列数。带迟滞：先按"当前列数"判，太挤才减、有富余才加，两边各留 <see cref="Dead"/>。
    /// 纯逻辑、无副作用，探针可以直接拿它跑"窗口从最小拖到最大"的整条路径。
    /// </summary>
    public static int ColumnsFor(double avail, int current)
    {
        var n = current > 0
            ? current
            : (int)Math.Floor((avail + Gap) / (MinItemWidth + Gap));
        if (n < 1) n = 1;

        // 挤不下就逐列减
        while (n > 1 && (avail + Gap) / n - Gap < MinItemWidth - Dead) n--;
        // 还有富余就逐列加
        while ((avail + Gap) / (n + 1) - Gap >= MinItemWidth + Dead) n++;
        return n;
    }
}
