using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;

namespace BiliDesk.Views;

/// <summary>
/// Fluent 窗口基类: 圆角 + 深色标题栏 + 跟随主题的纯色背景
/// 统一使用纯色背景(WindowSolidLight/DarkBrush), 避免 Mica 与半透明内容叠加导致的色彩不一致
///
/// 另外在这里统一处理"无边框窗口最大化"这件事(见 ApplyWorkingAreaLimit):
/// 主窗口和播放器都用 WindowStyle=None + WindowChrome 自绘标题栏, 而 WPF 自己的最大化
/// 会把窗口撑到显示器**整屏**大小(各方向多出调整边框的量), 结果是任务栏被压住、底部内容看不见。
/// </summary>
public class FluentWindow : Window
{
    private bool _hooked;

    public FluentWindow()
    {
        FontFamily = new System.Windows.Media.FontFamily(
            "Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI, Microsoft YaHei");
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            DwmInterop.SetRoundCorners(this);
            // 不启用 Mica: 半透明 AppBackgroundBrush + Mica 透色 = 偏暗且不一致
            DwmInterop.DisableMica(this);
        }
        catch
        {
            // 忽略
        }

        // 无边框窗口要靠 WM_GETMINMAXINFO 把最大化限制在"工作区"里。
        // 挂不上也不致命 —— 只是最大化会盖住任务栏。
        // ★ 这个钩子必须是**实例方法**: 播放器全屏时要临时把最大化改成"整块显示器"
        //   (盖住任务栏), 静态方法读不到那个开关。
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        }
        catch
        {
            // 忽略
        }

        if (!_hooked)
        {
            _hooked = true;
            ThemeService.Instance.ThemeChanged += OnThemeChanged;
        }
        ApplyChrome();
    }

    private void OnThemeChanged() => ApplyChrome();

    // protected: 播放器退出全屏时要主动把窗口底色从"全屏黑"还原成主题色
    protected void ApplyChrome()
    {
        try
        {
            var dark = ThemeService.Instance.IsDark;
            DwmInterop.SetDarkTitleBar(this, dark);
            // 统一纯色背景, 深浅主题用各自的 WindowSolid* 资源
            Background = dark
                ? (Brush)FindResource("WindowSolidDarkBrush")
                : (Brush)FindResource("WindowSolidLightBrush");
        }
        catch
        {
            // 忽略主题应用异常
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hooked) ThemeService.Instance.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------ 最大化 / 还原的过渡

    /// <summary>
    /// 最大化/还原时给内容补一段过渡动画。
    ///
    /// ★★ 为什么敢把 Scale 加回来(2026-10-01, 第三版): 第一版逐帧改整棵树的 ScaleTransform,
    ///   **每一帧都要把整棵内容树重新栅格化**, 首页几百张卡片的页面扛不住 ⇒ "全屏化很卡"。
    ///   现在动画期间给根元素挂 **`BitmapCache`**: 把整棵树栅格化成**一张 GPU 纹理**,
    ///   之后每帧的缩放只是变换这张纹理 —— 成本从"每帧重绘整窗"降到"每帧一次纹理变换",
    ///   动画结束立刻摘掉, 恢复矢量渲染(文字保持清晰)。
    ///
    /// ★ 仍然要能关掉: 播放器切全屏是"画面本身要瞬间变化"的场景, 叠这层动画纯属干扰 ——
    ///   它用 <see cref="SuppressStateTransition"/> 关掉。
    /// </summary>
    protected bool SuppressStateTransition { get; set; }

    /// <summary>状态过渡时长。要明显是个动画, 但不能长到让人等。</summary>
    private const int StateAnimMs = 200;

    /// <summary>连点保护: 递增序号。收尾回调对不上号, 说明新一轮动画已接管, 不得拆它的现场。</summary>
    private int _stateAnimSeq;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // 启动阶段(IsLoaded 之前)不播; 最小化不播(窗口本来就不可见)
        if (!IsLoaded) return;
        if (WindowState == WindowState.Minimized) return;
        if (SuppressStateTransition) return;
        if (Content is not FrameworkElement) return;

        // ★★ 必须等这次状态变化引发的**真实窗口尺寸**落到布局上再动手(2026-10-02 定位):
        //   `StateChanged` 是在窗口真正被 resize **之前**发的 —— 此刻即使 `UpdateLayout()`, 拿到的
        //   还是**旧客户区**尺寸, 于是卡片墙按旧宽度算出的"目标卡宽"与现状相同 ⇒ 下面的"收敛可视区"
        //   一个孩子都收敛不动(探针在 CardWallPanel 里埋点实测 `收敛=0`), 之后渐进的 PumpStep 才把
        //   **整墙含视野上方**按 16 张一批地收敛(150 张要 10 批、约 600ms), 而卡片高度随宽度变 ⇒
        //   可见区被一批批顶走 —— 这就是"滚一屏后最大化/还原, 卡片抽动很明显"的根。
        //   放到 Background 优先级(= 这批消息处理完、resize 与布局/渲染已经跑过)再播, 目标值才是新的。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded || SuppressStateTransition) return;
            if (WindowState == WindowState.Minimized) return;
            if (Content is not FrameworkElement root) return;

            double fromScale;
            if (WindowState == WindowState.Maximized)
            {
                // 从"还原尺寸 ÷ 工作区"的等比缩小量出发 —— 动画看起来就像窗口从原来的尺寸长满整屏。
                // 来源必须是 RestoreBounds / 工作区, 不能读当前尺寸(现在已是最大化尺寸, 比例 ≈1)。
                var before = RestoreBounds;
                var work = GetWorkArea();
                if (before.Width < 80 || before.Height < 80 || work.Width < 80 || work.Height < 80) return;
                fromScale = Math.Clamp(Math.Min(before.Width / work.Width, before.Height / work.Height), 0.75, 0.99);
            }
            else
            {
                fromScale = 1.02;   // 还原: 轻微收缩回落
            }

            PlayStateTransition(root, fromScale);
        }));
    }

    private void PlayStateTransition(FrameworkElement root, double fromScale)
    {
        var scale = root.RenderTransform as ScaleTransform;
        if (scale == null)
        {
            // 框架有时会在首次布局时往根上放一个**恒等**的 MatrixTransform(实测矩阵全是单位值),
            // 覆盖它是安全的; 但如果根上挂的是"有实际作用"的变换, 就不能覆盖 ——
            // 那等于把别人的变换弄丢, 宁可这次不播。
            var existing = root.RenderTransform;
            if (existing != null && !existing.Value.IsIdentity) return;

            scale = new ScaleTransform(1, 1);
            root.RenderTransform = scale;
        }
        else
        {
            // 手快连点最大化 / 还原时, 上一段动画还挂着 —— 先摘掉, 否则起止值会互相打架
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        // 用 RenderTransformOrigin 而不是 CenterX/CenterY: 它是**比例**, 窗口尺寸怎么变都自动居中
        root.RenderTransformOrigin = new Point(0.5, 0.5);

        // ★★ 卡片墙必须在动画前"静止"(见 CardWallPanel._held 的说明): 渐进切宽的每一批
        //   都会让 BitmapCache 的整窗纹理失效重栅格化, 动画过程就一抽一抽的。
        //   顺序: ① 最大化引发的布局先付清(CardWall 的目标宽度落账);
        //         ② 可视区卡片一次性收敛(≈30 张的重测) + 再付一次布局 —— 之后树完全静止;
        //         ③ 此刻才挂 BitmapCache 栅格化 ⇒ 动画的每一帧都是纯纹理变换, 平滑。
        //
        // ★★ 还有一条**异步**的失效源必须一起掐掉(2026-10-02, 用户报的"界面抽动"根因):
        //   封面重解码。卡片一变宽就触发"按新宽度重新解码", 解码完成回 UI 线程贴图的那一下
        //   会落在动画中间 ⇒ 整窗纹理失效重栅格化 ⇒ 一抽一抽。
        //   先**无条件开一次闸**(连点最大化时上一段动画的收尾可能被序号保护挡掉了, 不能让它欠着),
        //   再关闸: 从现在到动画收尾, 封面一律只记账不上屏(见 Cover.SuspendVisualUpdates)。
        Cover.ResumeVisualUpdates();
        Cover.SuspendVisualUpdates();
        root.UpdateLayout();
        WalkCardWallPanels(root, p => p.ConvergeVisibleThenHold());
        root.UpdateLayout();

        var prevCache = root.CacheMode;
        root.CacheMode = new BitmapCache();

        var seq = ++_stateAnimSeq;
        var dur = TimeSpan.FromMilliseconds(StateAnimMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var sx = new DoubleAnimation(fromScale, 1, dur) { EasingFunction = ease };
        var sy = new DoubleAnimation(fromScale, 1, dur) { EasingFunction = ease };
        var fade = new DoubleAnimation(root.Opacity < 1 ? root.Opacity : 0.8, 1, dur) { EasingFunction = ease };

        void Cleanup()
        {
            // 三个动画同时到达, 第一个到的负责收尾; 序号对不上 = 新一轮已接管, 别拆它的现场
            if (seq != _stateAnimSeq) return;

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            root.BeginAnimation(OpacityProperty, null);
            // 先放开卡片墙的渐进(剩余都是视野外卡片), 再恢复矢量渲染
            WalkCardWallPanels(root, p => p.ReleaseHold());
            root.CacheMode = prevCache;   // ★ 恢复矢量渲染, 别让文字一直停在栅格纹理上
            // ★ 最后开闸补图: 此刻 BitmapCache 已摘掉, 换封面不再让任何纹理失效
            //   (见 Cover.SuspendVisualUpdates 的说明)
            Cover.ResumeVisualUpdates();
        }

        sx.Completed += (_, _) => Cleanup();
        sy.Completed += (_, _) => Cleanup();
        fade.Completed += (_, _) => Cleanup();

        root.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    /// <summary>
    /// 遍历可视树里的卡片墙面板(首页那种零虚拟化墙), 供状态过渡做"收敛可视区/放开渐进"协作。
    /// 只有挂了卡片墙的页面有实例 —— 设置页/"我的"页走这里等于空操作。
    /// </summary>
    private static void WalkCardWallPanels(DependencyObject d, Action<CardWallPanel> visit)
    {
        if (d is CardWallPanel panel) visit(panel);
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) WalkCardWallPanels(VisualTreeHelper.GetChild(d, i), visit);
    }

    /// <summary>
    /// 当前窗口所在显示器的工作区, 换算成 WPF 的 DIP 单位(窗口实际用的坐标系)。
    /// 状态动画要用它算"还原尺寸 → 整屏"的比例; SystemParameters.WorkArea 只认主屏。
    /// </summary>
    private Size GetWorkArea()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                return new Size(
                    (info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
                    (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
            }
        }
        catch
        {
            // 落到下面的兜底
        }
        var area = SystemParameters.WorkArea;
        return new Size(area.Width, area.Height);
    }

    // ------------------------------------------------------------ 拖动缩放: 让重活先让路

    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    // ------------------------------------------------------------ 最大化尺寸修正

    private const int WM_GETMINMAXINFO = 0x0024;

    /// <summary>取"离这个窗口最近的显示器"(多显示器时按窗口当前所在的那块算)</summary>
    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// 最大化时**盖住任务栏**(把最大化尺寸从"工作区"放大到"整块显示器")。
    ///
    /// 为什么需要它: <see cref="ApplyWorkingAreaLimit"/> 是给主窗口那种"普通最大化"定的规矩 ——
    /// 最大化应当只占工作区, 不该压住任务栏。但播放器的全屏是另一回事: 它要的就是**全屏**,
    /// 任务栏必须被盖住。两件事共用同一个 WM_GETMINMAXINFO 钩子, 所以在这里开一个开关。
    ///
    /// ★ 改这个值之后**必须**让窗口重新走一次最大化(先 Normal 再 Maximized):
    ///   WM_GETMINMAXINFO 只在窗口开始最大化/移动/缩放时发一次, 窗口已经处于 Maximized 时
    ///   再改这个开关不会有任何效果 —— 这正是"全屏进不去/任务栏还在"的坑。
    /// </summary>
    protected bool MaximizeCoversTaskbar { get; set; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkingAreaLimit(hwnd, lParam, MaximizeCoversTaskbar);
            handled = true;
        }
        else if (msg == WM_ENTERSIZEMOVE || msg == WM_EXITSIZEMOVE)
        {
            // 用户开始/结束"拖着窗口边缘改大小"。★ **不要设 handled** —— 这两个消息还有别人要处理
            // (WindowChrome 与 WPF 自己的排版), 吃掉会让拖动行为出问题。
            // 广播出去是为了让卡片墙把重算**合流**: 拖动期间跟着每一帧重算整墙卡片,
            // 就是用户说的"拖边缘很卡"。
            CardWall.NotifyModalResize(msg == WM_ENTERSIZEMOVE);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// DIP → 物理像素。**NaN / 无穷大 / 非正数一律返回 0**，即"这一侧没设限制"。
    ///
    /// ★★ 为什么必须显式挡无穷大：`Window.MaxWidth` / `MaxHeight` 的**默认值是
    ///   `double.PositiveInfinity`，不是 NaN**。曾经这里只写了 `> 0 && !IsNaN(...)`，
    ///   两个守卫都被无穷大放行 ⇒ `(int)Math.Ceiling(∞ * 1.25)` 溢出成 **`int.MinValue`**
    ///   （一个巨大的负数）并写进 `ptMaxTrackSize`。后果不是"限制失效"，而是
    ///   **窗口被永久钉死在 `ptMinTrackSize` 上**（2026-09-30 实测）：
    ///     ① 拖动边缘没有任何反应，一动就回弹到最小尺寸；
    ///     ② 点最大化只把窗口挪到屏幕左上角 (0,0)、尺寸仍是最小尺寸，看起来像"全屏失效"；
    ///     ③ 还原也回不到原尺寸（RestoreBounds 已经不可信了）。
    ///   离线复现方式见 `%TEMP%\bd-probe-refactor --win`：同一个窗口类、只切换"有没有钩子"，
    ///   对照组的最大化是 1936×1096（正常），被测组是 1325×825（＝最小尺寸）。
    /// </summary>
    private static int ToPhysicalPixels(double dip, double scale)
    {
        if (double.IsNaN(dip) || double.IsInfinity(dip) || dip <= 0) return 0;
        var px = dip * scale;
        if (px >= int.MaxValue) return int.MaxValue;
        return (int)Math.Ceiling(px);
    }

    /// <summary>
    /// 把最大化的位置与尺寸改成**工作区**(而不是整屏); <paramref name="coverTaskbar"/> 为 true 时
    /// 反过来改成**整块显示器**(播放器全屏用)。
    ///
    /// ptMaxPosition 是**相对显示器左上角**的偏移, 所以多显示器下要减掉 rcMonitor 的原点 ——
    /// 直接塞工作区的绝对坐标会把窗口甩到主屏上(副屏最大化时最明显)。
    /// 全屏那条路要的是整屏, 偏移正好是 0, 但这里仍按同一套公式写, 免得两条路各算一遍。
    ///
    /// 必须是**实例方法**: 下面要读窗口自己的 MinWidth/MinHeight/MaxWidth/MaxHeight 与 DPI。
    /// </summary>
    private void ApplyWorkingAreaLimit(IntPtr hwnd, IntPtr lParam, bool coverTaskbar)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return;

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            var target = coverTaskbar ? info.Monitor : info.Work;
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            mmi.MaxPosition.X = target.Left - info.Monitor.Left;
            mmi.MaxPosition.Y = target.Top - info.Monitor.Top;
            mmi.MaxSize.X = target.Right - target.Left;
            mmi.MaxSize.Y = target.Bottom - target.Top;

            // ★★ 必须自己把 ptMinTrackSize 填上 —— 否则**全工程所有窗口的 MinWidth/MinHeight 都形同虚设**。
            //
            // 为什么: **WPF 是靠它自己的窗口过程在这个消息里按 MinWidth/MinHeight 填这两个字段的**,
            // 而 AddHook 挂的钩子跑在窗口过程**之前**, 我们又设了 `handled = true` ——
            // WPF 那一步就再也不会执行。实测(2026-09-30 探针): MinWidth=1060 的窗口, 发真
            // WM_GETMINMAXINFO 读回来 MinTrackSize 是 **0×0**, 于是用户能把主窗口一路拖到 330 多像素宽
            // (首页卡片被裁成一团)。ptMaxSize(最大化尺寸)一直是好的, 因为那个字段本来就由我们自己写。
            //
            // 单位陷阱: WM_GETMINMAXINFO 用**物理像素**, MinWidth/MinHeight 是 DIP ⇒ 必须乘 DPI。
            // 只填 MinTrackSize / (有 MaxWidth 时才填) MaxTrackSize: 不填的字段保持系统预填的默认值,
            // 别去覆盖成 0。
            //
            // ★ 还要**夹进工作区**: 屏幕比 MinWidth 还小时(1024 宽的屏 + 主窗口 MinWidth=1060),
            //   下限必须让位, 否则窗口既放不下也缩不动 —— 那正是"缩小界面时内容被裁掉"的另一种表现。
            //   下限存在的意义是"别小到没法用", 不是"在大不了的地方硬撑"。
            // ★★ 单位换算这一步有个**必须挡住无穷大**的坑, 见 ToPhysicalPixels。
            var dpi = VisualTreeHelper.GetDpi(this);
            var workW = target.Right - target.Left;
            var workH = target.Bottom - target.Top;

            var minW = ToPhysicalPixels(MinWidth, dpi.DpiScaleX);
            var minH = ToPhysicalPixels(MinHeight, dpi.DpiScaleY);
            if (minW > 0) mmi.MinTrackSize.X = Math.Min(minW, workW);
            if (minH > 0) mmi.MinTrackSize.Y = Math.Min(minH, workH);

            var maxW = ToPhysicalPixels(MaxWidth, dpi.DpiScaleX);
            var maxH = ToPhysicalPixels(MaxHeight, dpi.DpiScaleY);
            if (maxW > 0) mmi.MaxTrackSize.X = Math.Min(maxW, mmi.MaxSize.X);
            if (maxH > 0) mmi.MaxTrackSize.Y = Math.Min(maxH, mmi.MaxSize.Y);

            Marshal.StructureToPtr(mmi, lParam, true);
        }
        catch
        {
            // 修不了就交给系统按默认来(顶多最大化盖住任务栏), 绝不能因此抛异常
        }
    }
}