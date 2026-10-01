using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 深浅色切换的"圆形扩散"过渡动画。
///
/// 做法(这也是 WPF 里最稳的一种, 不依赖任何第三方库):
///   1. 换肤**之前**先把当前窗口内容拍一张位图快照, 铺在可视树最上层;
///   2. 给快照加一个"挖了个圆洞"的裁剪(矩形 减去 圆), 此时洞的半径是 0 —— 等于快照完整可见;
///   3. 在快照的遮挡下真正切换主题(用户看不到这一瞬间的跳变);
///   4. 把洞的半径从 0 动画到"能盖住整窗", 视觉上就是新主题从点击处向外扩散, 直到铺满。
///   5. 动画结束后移除覆盖层并断开位图引用。
///
/// 几个刻意的取舍:
///   - 快照只覆盖窗口的**内容区**(OS 标题栏不属于 WPF 可视树, 拍不到也不该拍);
///   - 快照按**像素预算**缩放(见 SnapshotBudgetPixels)。这是"切主题先卡一下"的根治:
///     RenderTargetBitmap 的开销正比于输出像素数, 4K 全窗快照要 200ms 以上, 而这段时间
///     UI 线程是死的 —— 用户点一下, 界面先僵住, 然后动画突然冒出来。降到 1/4~1/3 的像素量
///     只要 20~40ms, 代价是过渡期间画面略糊(400ms 的过渡, 无人在意);
///   - 任何一步出问题(拿不到可视根 / 快照失败 / 尺寸为 0)都直接降级为"立即换肤" ——
///     过渡效果是锦上添花, 绝不能因为它让功能不可用。
/// </summary>
public static class ThemeTransition
{
    /// <summary>扩散时长。太短看不出"波纹", 太长会拖慢操作感。</summary>
    private const int RippleMs = 400;

    /// <summary>
    /// 快照的像素预算。
    ///
    /// RenderTargetBitmap 是把整棵可视树**同步重绘一遍**, 耗时基本正比于输出像素数:
    /// 实测 1280x800 约 40~60ms, 换成 1080p 就是两倍, 4K 上要 200ms 以上 ——
    /// 这段时间整个界面是不响应的, 用户感知就是"点一下先卡住, 然后动画突然冒出来"。
    ///
    /// 所以这里不按窗口实际分辨率拍, 而是按预算反推一个缩放比。
    /// 420k 像素约是 1080p 的 0.20 面积(1280x800 的 0.41): 一次 400 毫秒的过渡,
    /// 快照糊到只剩色块关系也无人在意 —— 卡顿才是实打实被看见的。
    ///
    /// 2026-09-24 从 600k 收到 420k: 这一步是**同步阻塞 UI 线程**的, 每降一档都是
    /// 直接把首帧的等待时间砍掉一截。
    /// </summary>
    private const long SnapshotBudgetPixels = 420_000;

    /// <summary>缩放的绝对下限。再低就只剩色块了, 宁可多花点时间也要看得清是"上个界面"。</summary>
    private const double MinSnapshotScale = 0.28;

    /// <summary>
    /// 当前挂在界面上的覆盖层。**任何时刻最多只有一个** —— 这是连点场景的关键。
    /// 见 DetachActive 里的说明。
    /// </summary>
    private static Image? _active;
    private static Panel? _activeRoot;
    private static DispatcherTimer? _safety;

    /// <summary>在浅色 / 深色之间切换, 并以 origin 为圆心播扩散动画。
    /// origin 为 null(例如键盘触发)时用窗口中心。</summary>
    public static void ToggleLightDark(Window? window, Point? origin)
    {
        // 当前是"跟随系统"时, 就切到当前实际外观的反面 —— 用户的预期是"按一下换一种"
        var target = ThemeService.Instance.IsDark ? AppTheme.Light : AppTheme.Dark;
        SwitchWithRipple(window, origin, target);
    }

    /// <summary>切换到指定主题, 并以 origin 为圆心播扩散动画</summary>
    public static void SwitchWithRipple(Window? window, Point? origin, AppTheme target)
    {
        // ★ 先把上一次的覆盖层摘掉, 再拍快照。这两个动作的顺序是这个方法里最要紧的地方:
        //
        //   1) 快照的实现是把可视树渲染进 RenderTargetBitmap, 而上一层的 Image.Source
        //      **本身也是一个 RenderTargetBitmap**。在同一个渲染批次里再去读它, 内容很可能
        //      还没被渲染线程写完 —— 拍出来的就是残缺的图。表现就是"快速连点深浅色按钮,
        //      界面上残留一块旧主题的扇形", 而且松手后动画跑完就又正常了(所以很像玄学)。
        //   2) 顺手保证无论点多快, 挂着的覆盖层始终只有一个, 不会越点越堆。
        //
        // 先摘再拍**不会闪**: 摘掉 → 拍快照 → 换肤 → 铺新覆盖层, 全程在同一个 UI 线程回合里
        // 同步完成, 中间没有任何一帧被真正呈现出去。
        DetachActive();

        var root = window?.Content as Panel;
        if (root == null || root.ActualWidth < 1 || root.ActualHeight < 1)
        {
            // 拿不到可视根或还没完成布局 —— 直接换, 不影响功能
            ThemeService.Instance.SetMode(target);
            return;
        }

        var w = root.ActualWidth;
        var h = root.ActualHeight;

        Image? overlay;
        try
        {
            overlay = Capture(root, w, h);
        }
        catch
        {
            overlay = null; // 位图分配/渲染失败(极端尺寸、显存不足)时降级
        }

        if (overlay == null)
        {
            ThemeService.Instance.SetMode(target);
            return;
        }

        var center = Clamp(origin ?? new Point(w / 2, h / 2), w, h);

        overlay.Width = w;
        overlay.Height = h;
        overlay.HorizontalAlignment = HorizontalAlignment.Left;
        overlay.VerticalAlignment = VerticalAlignment.Top;
        overlay.Stretch = Stretch.Fill;
        overlay.IsHitTestVisible = false; // 别把动画期间的点击吃掉
        // 根面板是 Grid, 让覆盖层跨过所有行列(否则只会落在第 0 列)
        Grid.SetRowSpan(overlay, 99);
        Grid.SetColumnSpan(overlay, 99);

        // 圆洞: 矩形 减去 圆。半径 0 时"减掉"的区域为空, 快照完整可见。
        // 半径不能真的写 0 —— 退化几何在组合运算里行为不一致, 用 0.01 更稳。
        var hole = new EllipseGeometry(center, 0.01, 0.01);
        overlay.Clip = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, w, h)),
            hole);

        root.Children.Add(overlay);
        _active = overlay;
        _activeRoot = root;

        // 兜底: 万一 Completed 没来(动画被替换、渲染线程异常…), 也不让这块快照永远糊在界面上。
        // 覆盖层是一张几 MB 的位图, 卡住不只是难看, 还会拦掉所有鼠标操作。
        _safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RippleMs + 900) };
        _safety.Tick += (_, _) => DetachActive();
        _safety.Start();

        // ★ 卡顿的根治点: 把"拍快照 / 换肤重绘 / 起波纹动画"三件重活**拆到不同的帧**去做。
        //
        // 原实现把三件事塞在同一个 UI 线程回合里: 用户点下去先吃掉"快照渲染 + 全树资源
        // 重解析 + 整窗重绘"两段大几十到上百毫秒的死时间, 动画已经从中途才开始跑 ——
        // 感知就是"先僵一下, 波纹突兀地蹦出来"。
        // 现在分三帧:
        //   第 1 帧: 快照上屏(本回合结束, 让 WPF 先把覆盖层画出来, 点击立刻有反馈);
        //   第 2 帧: 在快照的遮挡下换肤 —— 全树重绘虽然还是那一下开销, 但发生在动画开始
        //           之前, 且用户看到的是静止的旧主题快照, 不会感知成"动画卡住";
        //   第 3 帧: 重绘刷完再起波纹, 动画帧之间不再有重活抢 UI 线程。
        // 每一步都要校验 _active 还是"自己这一层": 连点时上一层早已被摘掉, 不能越界操作。
        root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(_active, overlay)) return;

            // 先换肤: 此刻整窗都被快照盖住, 跳变对用户不可见
            ThemeService.Instance.SetMode(target);

            // 再等"换肤后的第一帧真的上屏", 然后才开始撑洞。
            //
            // 为什么不是直接 BeginInvoke(Render): 换肤会让渲染线程把**整个窗口**重新栅格化
            // 一遍(所有 DynamicResource 重新解析 + 文字重新栅格化), 这是这条链路上最重的一笔。
            // 如果波纹紧接着就开始, 它的前几帧会和这次全窗重绘抢渲染线程 —— 表现就是
            // "波纹刚起的那一小段一顿一顿的"。等两帧(CompositionTarget.Rendering 每帧一次)
            // 能确保那一笔已经落地, 动画从第一帧起就是独占渲染线程的。
            WaitFrames(2, () =>
            {
                if (!ReferenceEquals(_active, overlay)) return;

                // 把洞撑满整窗
                var maxRadius = MaxCornerDistance(center, w, h);
                // 缓动从 Cubic 换成 Quadratic: 两条都是 EaseOut, 但起点速度正比于 3R/T 与 2R/T ——
                // Quadratic 起步慢 1/3, 扩散不会在第一帧"炸开", 观感上顺得多。
                // (半径最大能到斜对角, 大窗口上 Cubic 的起步速度非常夸张, 那正是"突兀"的来源。)
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                var growX = new DoubleAnimation(0.01, maxRadius, TimeSpan.FromMilliseconds(RippleMs))
                {
                    EasingFunction = ease
                };
                growX.Completed += (_, _) =>
                {
                    // 只清理"自己那一层": 连点时上一层早已被摘掉, 这里不能去动新的一层
                    if (ReferenceEquals(_active, overlay)) DetachActive();
                };

                hole.BeginAnimation(EllipseGeometry.RadiusXProperty, growX);
                hole.BeginAnimation(EllipseGeometry.RadiusYProperty,
                    new DoubleAnimation(0.01, maxRadius, TimeSpan.FromMilliseconds(RippleMs))
                    {
                        EasingFunction = ease
                    });
            });
        }));
    }

    /// <summary>
    /// 等 count 个合成帧之后再执行 action(CompositionTarget.Rendering 每帧触发一次)。
    ///
    /// 用途: 把"动画起点"排到"上一步的重绘已经上屏"之后。
    /// 注意: 窗口被完全遮挡/最小化时可能不产生合成帧, 所以这里必须配合外部兜底定时器
    /// (见 _safety) —— 拿不到帧就由兜底把覆盖层摘掉, 不会留下永远糊在界面上的快照。
    /// </summary>
    private static void WaitFrames(int count, Action action)
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (--count > 0) return;
            CompositionTarget.Rendering -= handler;
            action();
        };
        CompositionTarget.Rendering += handler;
    }

    /// <summary>
    /// 收尾: 把当前覆盖层从可视树摘掉并断开位图引用。
    ///
    /// 位图是几 MB 级别的托管内存, 不主动清空要等 GC 才回收 —— 反复切主题会明显抬高内存水位。
    /// 摘掉之后必须把 `_active` 置空: 它就是"最多只挂一层"这个不变量的载体。
    /// </summary>
    private static void DetachActive()
    {
        _safety?.Stop();
        _safety = null;

        var root = _activeRoot;
        var overlay = _active;
        _activeRoot = null;
        _active = null;

        if (root == null || overlay == null) return;
        try { root.Children.Remove(overlay); } catch { /* 窗口可能已经关闭 */ }
        try { overlay.Source = null; } catch { }
    }

    /// <summary>
    /// 拍一张整窗内容区的快照(按像素预算缩放, 见 SnapshotBudgetPixels)。
    /// 拿不到(尺寸异常 / 位图分配失败)时返回 null, 调用方降级为"立即换肤"。
    /// </summary>
    private static Image? Capture(FrameworkElement root, double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(root);
        var fullW = (int)Math.Ceiling(w * dpi.DpiScaleX);
        var fullH = (int)Math.Ceiling(h * dpi.DpiScaleY);
        if (fullW <= 0 || fullH <= 0) return null;

        var scale = Math.Min(1.0,
            Math.Sqrt(SnapshotBudgetPixels / (double)((long)fullW * fullH)));
        if (scale < MinSnapshotScale) scale = MinSnapshotScale;

        var pxW = Math.Max(1, (int)Math.Ceiling(fullW * scale));
        var pxH = Math.Max(1, (int)Math.Ceiling(fullH * scale));

        var bmp = new RenderTargetBitmap(
            pxW, pxH, 96 * dpi.DpiScaleX * scale, 96 * dpi.DpiScaleY * scale, PixelFormats.Pbgra32);
        bmp.Render(root);
        bmp.Freeze(); // 冻结后可跨线程/被多个 ImageBrush 共享, 也少一层校验开销

        // 缩放模式要设在**显示这张位图的那个元素**上(不是被拍的根元素):
        // 默认的 Fant 插值在整屏放大时更慢, 而这张图几百毫秒后就被"擦掉"了, 双线性足够。
        var image = new Image { Source = bmp };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        return image;
    }

    private static Point Clamp(Point p, double w, double h)
        => new(Math.Clamp(p.X, 0, w), Math.Clamp(p.Y, 0, h));

    /// <summary>圆心到最远那个角的距离 —— 半径长到它就能盖住整窗</summary>
    private static double MaxCornerDistance(Point c, double w, double h)
    {
        double D(double x, double y) => Math.Sqrt((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y));
        return Math.Max(
            Math.Max(D(0, 0), D(w, 0)),
            Math.Max(D(0, h), D(w, h)));
    }
}
