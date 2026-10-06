using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

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
        // ★ 软件缩放(2026-10-06): 内容一就绪就登记进 UiScale。
        //   用 Loaded 而不是 SourceInitialized —— 那时 Content 才真正挂上可视树,
        //   拿到的根元素才是最终会被渲染的那一个。
        //   ★ 播放器窗口覆写 ScalesWithUi=真实false 把自己排除掉(见该属性的说明)。
        Loaded += (_, _) =>
        {
            if (ScalesWithUi) UiScale.Register(Content as FrameworkElement);
        };
    }

    /// <summary>
    /// 本窗口是否参与全局「软件缩放」。
    ///
    /// ★★★ 默认为 true; **播放器窗口必须覆写成 false**(2026-10-06 用户决定):
    ///   实测该窗口有 **3 棵独立可视树**(主窗口 + LibVLC 视频浮层 + 弹幕/控制栏浮层),
    ///   而且视频画面是**原生 HWND** —— WPF 的 LayoutTransform 对它无效(airspace 硬限制)。
    ///   给主树加缩放只会让"视频区按原生尺寸、周围 UI 放大", 两者对不上 ⇒ 宁可整个窗口不缩。
    ///   证据: `.probes/bd-probe-uiscale-player` 实测到 3 个 PresentationSource。
    /// </summary>
    protected virtual bool ScalesWithUi => true;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            DwmInterop.SetRoundCorners(this);
            // 云母/亚克力的最终状态由 ApplyChrome 统一决定(两者都走 DWMWA_SYSTEMBACKDROP_TYPE,
            // 必须一个入口说了算, 否则"开亚克力又被 DisableMica 关掉"这类互相打架必现)
            // ★★★ 把 WS_CAPTION 补回来, 否则 DWM **不会播**最小化/最大化/还原的窗口过渡动画。
            //   本工程所有窗口都是 WindowStyle=None 的自绘标题栏, 这个样式位被摘掉了 ——
            //   这正是"最小化没动画、全屏硬切"的真根因(与本类那层已删的自绘动画无关)。
            //   实测数据见 DwmInterop.RestoreCaptionForDwmAnimation 的说明。
            //   ★ 时机必须在这里(句柄刚建好、还没 Show): 放到 Show 之后会被当成运行时改样式。
            DwmInterop.RestoreCaptionForDwmAnimation(this);
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
            Svc.Settings.AcrylicChanged += OnAcrylicChanged;
            Svc.Settings.LayoutChanged += OnLayoutChanged;
        }
        ApplyChrome();
    }

    private void OnThemeChanged() => ApplyChrome();

    /// <summary>全局亚克力开关变化(来自设置页): 所有已开窗口都要立刻跟着换背景</summary>
    private void OnAcrylicChanged() => ApplyChrome();

    /// <summary>
    /// 影响布局的设置变化(列数/软件缩放)。列数由 CardWall 自己重算, 这里只管缩放 ——
    /// 缩放必须由本窗口重新施加一次(LayoutTransform 是**每个根元素各自持有**的, 不是全局资源)。
    /// </summary>
    private void OnLayoutChanged() => UiScale.ApplyAll();

    // protected: 播放器退出全屏时要主动把窗口底色从"全屏黑"还原成主题色
    protected void ApplyChrome()
    {
        try
        {
            var dark = ThemeService.Instance.IsDark;
            DwmInterop.SetDarkTitleBar(this, dark);

            // ★ 亚克力(实验性)与"禁用云母"共用 DWMWA_SYSTEMBACKDROP_TYPE, 所以必须在这里**一次决定**:
            //   开亚克力时不去禁云母(会被覆盖), 关亚克力时按老规矩禁掉 Mica。
            //   两处各写一遍必然出现"开了亚克力又被下一句关掉"。
            // ★★★ 亚克力只对**自绘边框窗口**生效(2026-10-05 修"浅色亚克力下收藏界面发黑"):
            //   SetAcrylicBackdrop 对没有 WindowChrome 的**系统标准窗口**会返回 false ——
            //   那种窗口要把客户区当普通内容画, 不能扩展玻璃(否则整个内容区被 DWM 当非客户区压黑)。
            //   ★ 所以必须**先看返回值再决定窗口底色**: 只有亚克力真的启用了, 才能把窗口背景设成透明
            //     (透明是靠底下的 DWM 合成层兜住的); 失败时一旦也设透明, 客户区就没有任何底色 —— 直接黑。
            var acrylicOn = false;
            if (Svc.Settings.AcrylicBackground)
                acrylicOn = DwmInterop.SetAcrylicBackdrop(this, Svc.Settings.AcrylicMaterial);
            _acrylicActive = acrylicOn;

            if (acrylicOn)
            {
                // 亚克力生效的前提是窗口背景能透出合成层: **聚焦时**用全透明的窗口底色,
                // 页面那层半透明 AppBackgroundBrush 再叠上去(见 Colors.*.xaml 的说明)。
                // ★ 失焦时不能还透明 —— 理由见 UpdateAcrylicWindowBase。
                // ★ 窗口还**没显示**时按"激活"处理: ApplyChrome 跑在 SourceInitialized(Show 之前),
                //   那一刻 IsActive 还是 false, 照它给底色会让"刚打开就被自己涂成实色",
                //   等 OnActivated 再变回透明 —— 观感是窗口亮起来时闪一下实色底。
                //   真实显示之后由 OnActivated/OnDeactivated 接管, 不会漏。
                // ★ 这里**不做淡入**(animate: false): 它是主题/开关变化与启动路径, 要即时到位。
                UpdateAcrylicWindowBase(IsVisible ? IsActive : true, animate: false);
            }
            else
            {
                // 没开亚克力 / 该窗口不支持亚克力: 一律回落普通实色背景
                DwmInterop.SetAcrylicBackdrop(this, BackdropMaterial.None);
                DwmInterop.DisableMica(this);
                // ★ 必须先卸掉动画载体: 否则那个笔刷上还挂着 ColorAnimation,
                //   会在后台继续按旧目标改色(表现是主题切换后底色又自己跳一下)。
                StopAcrylicBaseAnimation();
                // 统一纯色背景, 深浅主题用各自的 WindowSolid* 资源
                Background = dark
                    ? (Brush)FindResource("WindowSolidDarkBrush")
                    : (Brush)FindResource("WindowSolidLightBrush");
            }
        }
        catch
        {
            // 忽略主题应用异常
        }
    }

    /// <summary>本窗口当前是否**真的**启用了亚克力(即 SetAcrylicBackdrop 返回 true)</summary>
    private bool _acrylicActive;

    /// <summary>
    /// 亚克力窗口底色的可变动画笔(动画载体)。
    ///
    /// ★ 为什么要单独持有它、而不是每次赋 <c>Brushes.Transparent</c>/资源笔刷:
    ///   要淡入就得**改 Color** —— 而 <c>Brushes.Transparent</c> 与色板里的笔刷都是**冻结**的,
    ///   冻结对象挂不了动画(BeginAnimation 会当场抛)。所以这里自己 new 一个可变的。
    /// </summary>
    private SolidColorBrush? _acrylicBaseBrush;

    /// <summary>底色淡入时长。150ms: 与设置页开关的 120ms 同量级, 够顺滑又不拖。</summary>
    private static readonly Duration AcrylicBaseFadeDuration = new(TimeSpan.FromMilliseconds(150));

    /// <summary>
    /// 亚克力窗口的"窗口底色"按**激活态**给。
    ///
    /// ★★★ 为什么需要它(2026-10-06, 用户第三次报"点别的窗口后变灰"的最终修法):
    ///   DWM 的 **Background Acrylic 材质在窗口失焦时会被自己替换成实色**, 这是**设计行为**,
    ///   不是 bug, 也没有任何 API 能阻止。微软文档原话([Acrylic material]):
    ///     "only background acrylic will replace its translucency and texture with a solid color:
    ///      When an app window on desktop deactivates."
    ///   ⇒ 结论: **挡不住就接管** —— 失焦时我们自己给一块**主题实色底**,
    ///     从而"变灰"变成"看起来像刻意进入的背景态"。聚焦时照旧全透明, 毛玻璃质感不变。
    ///
    /// ★ 实测证据(.probes/bd-probe-acrylic-inactive, build 26200, 每变体 2 轮, redcheck 全红):
    ///     backdrop=3(亚克力)        聚焦/失焦**都**读回 3, 但画面都是灰(221,222,223)
    ///     backdrop=3 + HOSTBACKDROPBRUSH=1   与不加**完全一样** ⇒ 上一轮那个"缓解方案"是**空操作**
    ///     backdrop=2(云母)          同样回落(文档也写明了) ⇒ 换云母解决不了
    ///   ⇒ 所以这里不再去 DWM/DWMWA 上找开关(找不到), 只改我们自己的窗口底色。
    ///
    /// ★ 为什么换窗口底色就能盖住: DWM 的材质画在窗口内容**之下**,
    ///   窗口 Background 一旦不透明, 材质就被内容盖住 —— 用户看到的是我们的主题色, 不是 DWM 的灰。
    /// </summary>
    /// <param name="active">窗口当前是否处于激活(前台)态</param>
    /// <param name="animate">是否用 150ms 淡入过渡(仅焦点变化时用; 主题/开关变化要即时到位)</param>
    private void UpdateAcrylicWindowBase(bool active, bool animate)
    {
        // 播放器全屏时窗口底色必须保持纯黑(见 SuppressAcrylicActivationSwap), 不许被这里改掉
        if (SuppressAcrylicActivationSwap) return;
        try
        {
            var solid = ThemeWindowSolidColor();

            // ★ 只动 **Alpha**, RGB 始终是主题实色的 RGB —— 这样淡入是一次干净的"透明度过渡";
            //   若直接用 Transparent(0,0,0,0) 去插值, RGB 会一起从黑拉到主题色,
            //   中途会泛出一层灰黑(比"生硬切换"更难看)。
            var target = active
                ? Color.FromArgb(0x00, solid.R, solid.G, solid.B)     // 全透明(alpha=0)
                : Color.FromArgb(0xFF, solid.R, solid.G, solid.B);    // 主题实色

            // 首次调用 / 不要求动画: 直接给到位, 不建动画
            if (_acrylicBaseBrush == null || !animate)
            {
                StopAcrylicBaseAnimation();
                _acrylicBaseBrush = new SolidColorBrush(target);
                Background = _acrylicBaseBrush;
                return;
            }

            // 动画路径: 从**当前有效色**(含上一段动画的当前帧)插值到目标色。
            // ★★★ 必须显式给 From, 且**不要**先 BeginAnimation(null):
            //   先清动画会让属性立刻回落到"建笔刷时那个基准色", 而不是停在屏幕上正在显示的色,
            //   于是"实色 → 透明"会变成生硬跳变(而不是淡出)。
            //   带 From/To 的新动画直接替换旧动画, 不依赖基准值 —— 这正是 WPF 的 snapshot-and-replace 用法。
            var from = _acrylicBaseBrush.Color;   // 有动画时读到的就是当前帧的插值结果
            _acrylicBaseBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(from, target, AcrylicBaseFadeDuration)
                {
                    FillBehavior = FillBehavior.HoldEnd,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        catch
        {
            // 取不到资源/挂不上动画都不致命: 顶多这次不做过渡
        }
    }

    /// <summary>卸掉底色笔刷上的动画并清空载体(关亚克力、或要走"直接给底色"那条路时调)</summary>
    private void StopAcrylicBaseAnimation()
    {
        if (_acrylicBaseBrush == null) return;
        _acrylicBaseBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _acrylicBaseBrush = null;
    }

    /// <summary>当前主题的窗口实色底(单一样本来处: 色板里的 WindowSolid*Brush)</summary>
    private Color ThemeWindowSolidColor()
        => FindResource(ThemeService.Instance.IsDark ? "WindowSolidDarkBrush" : "WindowSolidLightBrush")
           is SolidColorBrush scb
            ? scb.Color
            : (ThemeService.Instance.IsDark
                ? Color.FromRgb(0x20, 0x20, 0x20)     // 兜底值, 与 Colors.Dark.xaml 一致
                : Color.FromRgb(0xF3, 0xF3, 0xF3));   // 兜底值, 与 Colors.Light.xaml 一致

    /// <summary>
    /// 置 true 可让"失焦换底色"这套处理停手。
    ///
    /// ★ 播放器**全屏**时必须是 true: 全屏画面之外的所有缝隙都要求是纯黑
    ///   (见 PlayerWindow.ApplyFullscreenLayout 里对 Background / RootGrid 的赋值),
    ///   而全屏途中窗口失焦(比如点到别的显示器)会被失焦逻辑改成主题色 ⇒ 缝隙一片浅色。
    /// </summary>
    protected bool SuppressAcrylicActivationSwap { get; set; }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // animate: true —— 焦点变化走 150ms 淡入(用户要求的"更平滑")
        if (_acrylicActive) UpdateAcrylicWindowBase(true, animate: true);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_acrylicActive) UpdateAcrylicWindowBase(false, animate: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hooked) ThemeService.Instance.ThemeChanged -= OnThemeChanged;
        Svc.Settings.AcrylicChanged -= OnAcrylicChanged;
        Svc.Settings.LayoutChanged -= OnLayoutChanged;
        UiScale.Unregister(Content as FrameworkElement);
        base.OnClosed(e);
    }

    // ------------------------------------------------------------ 最大化 / 还原

    /// <summary>
    /// 最大化/还原的动画**完全交给 Windows 自己播**(DWM 的窗口过渡)。
    ///
    /// ★★★ 2026-10-02 按用户要求"不再自己绘制动画", 这里以前那套 WPF 自绘过渡
    ///   (整窗 Scale + 淡入 + 动画期间挂 `BitmapCache` 把整棵树栅格化成一张 GPU 纹理,
    ///   200ms, 见旧探针 `bd-probe-{twitch,cardtwitch}`)已**整体删除**。
    ///   同一批删除的还有它专用的一整套配套机制 —— 它们只服务于那层自绘动画:
    ///     · `SuppressStateTransition`(播放器全屏用来关掉它的开关);
    ///     · `CardWallPanel.ConvergeVisibleThenHold` / `ReleaseHold`(动画期间冻结卡片树的"保持"开关);
    ///     · `Cover.SuspendVisualUpdates` / `ResumeVisualUpdates`(动画期间封面只记账不上屏的闸门);
    ///     · `DwmInterop.DisableWindowTransitions`(播放器原来把**系统**动画关掉, 好让"秒切"干净)。
    ///   **别复活其中任何一个**: 只要还有一处自绘动画/纹理, 就会与系统动画叠成"两段动画"(观感是拖沓或抽动)。
    ///
    /// ★ 注意:`DwmInterop.DisableWindowTransitions` 的删除方向与上面相反 —— 播放器全屏以前是
    ///   "秒切"(主动关掉系统动画), 现在正是要靠系统动画, 所以那里已改为**不再关**。
    ///
    /// ★ 唯一保留的动作是"把卡片墙按新宽度**一次性收敛**"(见 <see cref="CardWallPanel.ConvergeForResize"/>):
    ///   那不是动画, 是布局结算 —— 系统动画期间卡片若还按 16 张一批渐进重排, 就会在缩放动画里
    ///   一批批顶走可见区(历史上"抽动"的根)。先收敛好, 系统动画的每一帧才是静止的内容。
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // 启动阶段(IsLoaded 之前)不处理; 最小化不处理(窗口本来就不可见)
        if (!IsLoaded) return;
        if (WindowState == WindowState.Minimized) return;
        if (Content is not FrameworkElement) return;

        // ★★ 必须等这次状态变化引发的**真实窗口尺寸**落到布局上再动手(2026-10-01 定位):
        //   `StateChanged` 是在窗口真正被 resize **之前**发的 —— 此刻即使 `UpdateLayout()`, 拿到的
        //   还是**旧客户区**尺寸, 于是卡片墙按旧宽度算出的"目标卡宽"与现状相同 ⇒ 收敛一个孩子都动不了
        //   (探针实测 `收敛=0`), 之后渐进的 PumpStep 才把**整墙含视野上方**按 16 张一批地收敛
        //   (150 张要 10 批、约 600ms), 而卡片高度随宽度变 ⇒ 可见区被一批批顶走。
        //   放到 Background 优先级(= 这批消息处理完、resize 与布局/渲染已经跑过)再收敛, 目标值才是新的。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded) return;
            if (WindowState == WindowState.Minimized) return;
            if (Content is not FrameworkElement root) return;

            WalkCardWallPanels(root, p => p.ConvergeForResize());
        }));
    }

    /// <summary>
    /// 遍历可视树里的卡片墙面板(首页那种零虚拟化墙), 供状态变化做"一次性收敛"协作。
    /// 只有挂了卡片墙的页面有实例 —— 设置页/"我的"页走这里等于空操作。
    /// </summary>
    private static void WalkCardWallPanels(DependencyObject d, Action<CardWallPanel> visit)
    {
        if (d is CardWallPanel panel) visit(panel);
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) WalkCardWallPanels(VisualTreeHelper.GetChild(d, i), visit);
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

            // ★ 2026-10-05: 播放器靠这一对钩子在"拖动改大小"期间**按住**视频区遮罩,
            //   盖住 LibVLC 反复重协商 vout 露出的白底(拖边缘闪白)。
            //   ★★ 为什么开始/结束**都要**通知(上一版只发开始, 结果没修好 —— 2026-10-05):
            //     遮罩若只在开始时压一次、靠"新帧到达"撤掉, 那么第一次重协商一过去就被撤了;
            //     而拖动期间窗口尺寸每帧都在变, LibVLC 会**反复**重协商 vout
            //     ⇒ 后面每一轮又露出白底, 于是"闪白依旧"。
            //     所以遮罩的语义必须是"从 ENTER 一直压到 EXIT", 中途绝不放行。
            if (msg == WM_ENTERSIZEMOVE) OnModalResizeStarted();
            else OnModalResizeEnded();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 用户**开始**"拖着窗口边缘改大小"时调用(仅 <c>WM_ENTERSIZEMOVE</c>), 供子类挂副作用。
    /// 与 <see cref="OnModalResizeEnded"/> 成对, 语义是"这段模态 resize 的起止"。
    ///
    /// ★ 播放器用它压黑视频区手势层, 盖住 LibVLC 在 resize 期间重协商 vout 露出的白底。
    ///   做成虚方法而不是在基类里判断类型: 基类不该知道播放器有视频区这回事。
    ///   默认空实现 —— 不重写的窗口(主窗口/登录框等)行为完全不变。
    /// </summary>
    protected virtual void OnModalResizeStarted()
    {
    }

    /// <summary>
    /// 用户**结束**"拖着窗口边缘改大小"时调用(仅 <c>WM_EXITSIZEMOVE</c>), 与
    /// <see cref="OnModalResizeStarted"/> 成对。
    ///
    /// ★ 播放器在**这里**才撤掉遮罩, 而不是靠"新帧到达" —— 理由见 WndProc 里那段注释:
    ///   拖动期间会反复重协商 vout, 中途撤罩等于放行后面每一轮的白闪。
    /// </summary>
    protected virtual void OnModalResizeEnded()
    {
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
