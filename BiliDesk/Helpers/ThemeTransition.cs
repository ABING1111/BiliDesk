using System;
using System.Collections.Generic;
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
    /// 波纹目标半径的外扩系数(&gt;1)。
    ///
    /// ★★ 为什么外扩(2026-11-07 二次修「动画快结束时卡」):
    ///   EaseOut 缓动的收尾段速度趋近 0 —— 若动画终点恰好等于"盖住远角", 最后 ~100ms
    ///   洞沿每帧只挪 1~4px, 观感就是波纹在最后一段**爬行/卡住不动**; 随后 Completed
    ///   摘层把最后一条旧主题残角**瞬间抹掉**(一记跳变)。「慢爬 + 突跳」正是用户看到的
    ///   "动画快结束的时候卡"。
    ///   外扩 15% 后, 远角在约 2/3 进度处(洞沿还有明显速度时)就被扫过 —— 代入
    ///   QuadraticEase: p = 1/1.15 ≈ 0.87 → t ≈ 0.64, 此处洞沿速度仍有 ~200px/帧;
    ///   剩余 36% 时间在**窗外不可见区域**爬行, 用户看不到。Completed 摘层时洞已盖满
    ///   全窗(CombinedGeometry 已是空几何), 摘层零视觉变化, "突跳"随之消失。
    /// </summary>
    private const double RevealOvershoot = 1.15;

    /// <summary>
    /// 第一段兜底: 从快照上屏到波纹真正开始之间的保护窗。
    /// 这段包含"换肤(Apply) + 整棵可视树的重渲染"—— 卡片很多时实测能到 1 秒上下,
    /// 给 4 秒是留足慢机器/更多卡片的余量; 超过它仍没等到波纹(窗口被最小化等),
    /// 就把覆盖层摘掉, 界面停留在已换好的新主题上(等价于"立即换肤"的降级)。
    /// </summary>
    private const int PreRippleGuardMs = 4000;

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

    /// <summary>
    /// 快照位图的小池子(最多 <see cref="MaxPooledSnapshots"/> 张, 按尺寸/DPI 复用)。
    ///
    /// ★★ 为什么必须复用(2026-11-07 修"内存随点击次数上涨"):
    ///   RenderTargetBitmap 的像素缓冲区是**原生内存**, 每次点击都新建一张 420k 像素的图
    ///   (≈1.6MB), 托管包装对象很小、不触发 GC, 原生缓冲只能等终结器 —— 连点十几次,
    ///   任务管理器里就是"点一次涨一点"。复用同一张缓冲后, 反复切换只在两张之间轮换,
    ///   内存水位固定, 不再随点击次数叠加。
    ///
    /// ★ 为什么是两张而不是一张: 覆盖层正在屏幕上显示时, 渲染线程可能还在读这张位图。
    ///   轮到下一次点击时如果立刻往同一张上重绘, 会拍到"半张旧图"(见方法头第 1 条)。
    ///   两张交替 ⇒ 每张至少间隔一次完整切换(用户点击间隔远大于一帧), 重绘一定安全。
    /// </summary>
    private static readonly List<PooledSnapshot> Pool = new();
    private const int MaxPooledSnapshots = 2;

    private sealed class PooledSnapshot
    {
        public int Width, Height;
        public double DpiX, DpiY;
        public RenderTargetBitmap Bitmap = null!;
        public long LastUse;
    }

    private static long _poolClock;

    /// <summary>上一次真正被拍进去的那张 —— 绝不复用它, 见 <see cref="RentSnapshot"/>。</summary>
    private static RenderTargetBitmap? _lastRented;

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
        //
        // ★★ 两段式(2026-11-07): 第一段要覆盖"换肤 + 全树重渲染"的等待期 —— 卡片多时
        //   这段能有 1 秒上下, 定短了会在波纹还没开播时就把覆盖层摘掉(等于动画直接消失);
        //   波纹真正开始时会用"波纹时长 + 900ms"重启一次定时器, 两段互不挤占。
        StartSafety(PreRippleGuardMs);

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

            // ★★★ 换肤后的"全树重渲染"必须先落地, 波纹才能开始(2026-11-07 修"动画卡顿"):
            //
            //   换肤把所有 DynamicResource 换掉 ⇒ 整棵可视树(几百上千张卡, 上万个节点)的
            //   渲染数据要在 UI 线程上重建一遍 —— 实测 500 张卡要 ~600ms。
            //   原来只等 2 个合成帧就开始播波纹, 这 600ms 会**撞进动画的头几帧**:
            //   波纹冻住 → 直接跳到接近结尾, 这正是"加载视频后切深浅色动画卡顿"的样子
            //   (探针时间线: 579ms@852 的巨型 UI 停顿正好落在波纹窗口里)。
            //
            //   修法: 换肤后排两级"排空跳"—— Background(4) 排在 Render(7)/Loaded(6) 之后,
            //   ContextIdle(3) 再兜一层。调度器按优先级出队, 轮到 ContextIdle 时,
            //   换肤触发的渲染批次必然已经跑完 —— 这时才起波纹, 动画从第一帧起就是顺的。
            //   这段时间用户看到的是**静止的旧主题快照**(没有任何跳变), 代价只是动画
            //   开始得稍晚 —— 而不是动画本身冻住。
            //   (不能改回"等合成帧": 排空之后窗口往往已无脏区, Rendering 事件不再来,
            //    波纹会永远起不来, 只能靠兜底定时器降级成"无动画直接换"。)
            root.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!ReferenceEquals(_active, overlay)) return;
                root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    if (!ReferenceEquals(_active, overlay)) return;

                    // 波纹开始: 第一段兜底(重渲染等待期)结束, 换成"波纹时长 + 余量"。
                    StartSafety(RippleMs + 900);

                    // ★ 终点半径外扩 15%(见 RevealOvershoot): "盖住远角"这一步发生在 ~2/3 进度处,
                    //   洞沿还有明显速度; 最后一段在窗外爬行, 摘层时已无可见的旧主题残角。
                    var maxRadius = MaxCornerDistance(center, w, h) * RevealOvershoot;
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
                }));
            }));
        }));
    }

    /// <summary>(重新)启动兜底定时器: 超时仍未收尾就把覆盖层摘掉, 不让快照糊在界面上。</summary>
    private static void StartSafety(int ms)
    {
        _safety?.Stop();
        _safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _safety.Tick += (_, _) => DetachActive();
        _safety.Start();
    }

    /// <summary>
    /// 收尾: 把当前覆盖层从可视树摘掉并断开位图引用。
    ///
    /// ★ 位图本身不会随之销毁 —— 它已经还给 <see cref="Pool"/> 复用(见 RentSnapshot)。
    ///   这里只断开 Image.Source 与可视树的关系, 让下一帧不再绘制旧主题。
    ///   `_active` 必须置空: 它就是"最多只挂一层"这个不变量的载体。
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
        var dpiX = 96 * dpi.DpiScaleX * scale;
        var dpiY = 96 * dpi.DpiScaleY * scale;

        var bmp = RentSnapshot(pxW, pxH, dpiX, dpiY);
        if (bmp == null) return null;
        bmp.Render(root);

        // ★ 刻意**不 Freeze**: 池子要往同一张位图上重绘。这里只在本线程(UI)当 Image.Source
        //   短暂显示, 不需要跨线程共享; 冻结后 Render() 会直接抛异常。

        // 缩放模式要设在**显示这张位图的那个元素**上(不是被拍的根元素):
        // 默认的 Fant 插值在整屏放大时更慢, 而这张图几百毫秒后就被"擦掉"了, 双线性足够。
        var image = new Image { Source = bmp };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        return image;
    }

    /// <summary>
    /// 从池子里取一张同尺寸的位图(没有就新建)。见 <see cref="Pool"/> 的说明。
    /// 池子最多留两张: 超过时丢掉最久没用的 —— 它对应的覆盖层早已被摘掉。
    /// </summary>
    private static RenderTargetBitmap? RentSnapshot(int w, int h, double dpiX, double dpiY)
    {
        RenderTargetBitmap? reuse = null;
        PooledSnapshot? oldest = null;
        foreach (var p in Pool)
        {
            if (p.Width == w && p.Height == h &&
                Math.Abs(p.DpiX - dpiX) < 0.01 && Math.Abs(p.DpiY - dpiY) < 0.01)
            {
                reuse = p.Bitmap;
                p.LastUse = ++_poolClock;
                break;
            }
            if (oldest == null || p.LastUse < oldest.LastUse) oldest = p;
        }

        // ★ 绝不复用"上一次刚拍过"的那张: 它的内容可能还在渲染线程的当帧里被读取。
        //   (正常情况下池里两张会交替命中, 走不到这里; 这是窗口尺寸变化时的兜底。)
        if (reuse != null && ReferenceEquals(reuse, _lastRented)) reuse = null;

        RenderTargetBitmap? created = null;
        if (reuse == null)
        {
            created = new RenderTargetBitmap(w, h, dpiX, dpiY, PixelFormats.Pbgra32);
            Pool.Add(new PooledSnapshot
            {
                Width = w,
                Height = h,
                DpiX = dpiX,
                DpiY = dpiY,
                Bitmap = created,
                LastUse = ++_poolClock
            });
            while (Pool.Count > MaxPooledSnapshots)
            {
                oldest = null;
                foreach (var p in Pool)
                    if (oldest == null || p.LastUse < oldest.LastUse) oldest = p;
                if (oldest == null) break;
                Pool.Remove(oldest);
            }
            reuse = created;
        }

        // 重绘前清空: 尺寸没变时上一帧的像素还在, 不清的话边缘会留下旧主题的残影。
        try { reuse.Clear(); } catch { return reuse; }
        _lastRented = reuse;
        return reuse;
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
