using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>DWM(桌面窗口管理器)互操作: 云母/Mica 背景、深色标题栏、亚克力等</summary>
public static class DwmInterop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    /// <summary>DWMWA_NCRENDERING_POLICY: 控制 DWM 是否绘制非客户区(系统标题栏/按钮)</summary>
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmNcrpUseWindowStyle = 0;   // DWMNCRP_USEWINDOWSTYLE(默认)
    private const int DwmNcrpDisabled = 1;         // DWMNCRP_DISABLED
    private const int DwmwaWindowCornerPreference = 33;
    // ★ DWMWA_USE_HOSTBACKDROPBRUSH(17) 的常量已删除(2026-10-06): 它曾被误当成"失焦防回落"的开关,
    //   探针 A/B 证明它与失焦行为**完全无关**(是"让我自己用 Composition 画 host backdrop"的开关)。
    //   别再为"失焦变灰"把它加回来 —— 那个问题在上层窗口底色里解决(见 SetAcrylicBackdrop 的说明)。

    // DWMSBT 值: 1=None 2=Mica 3=Acrylic 4=Tabbed
    private const int DwmWcpRound = 2;         // 圆角窗口

    // ---- 窗口样式(GWL_STYLE) ----
    private const int GWL_STYLE = -16;
    /// <summary>WS_CAPTION = WS_BORDER | WS_DLGFRAME(0x00C00000)</summary>
    private const long WS_CAPTION = 0x00C00000L;
    /// <summary>WS_SYSMENU = 0x00080000: 系统菜单。DWM 画"系统标题栏按钮"也要它</summary>
    private const long WS_SYSMENU = 0x00080000L;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;

    public static IntPtr GetHwnd(Window window) => new WindowInteropHelper(window).Handle;

    /// <summary>是否为 Win11(Build 22000+)</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// 把 <c>WS_CAPTION</c> 样式位补回给**无边框**窗口, 让 DWM 愿意播它自己的
    /// 最小化/最大化/还原过渡动画。
    ///
    /// ★★★ 为什么需要它(2026-10-02, 用户报"软件全屏和最小化没有动画"的**真根因**):
    ///   `WindowStyle="None"` 会把 `WS_CAPTION` 从窗口样式里摘掉, 而 **DWM 只对"有标题栏
    ///   样式位"的窗口播窗口过渡动画**。实测(探针 `%TEMP%\bd-probe-dwmanim`, 覆盖度曲线口径):
    ///     · 标准窗口:           最小化 `92→55→0`, 最大化 `0→53→60`(渐变 = 有动画)
    ///     · 本工程现状(不补):   最小化 `100→0`,    最大化 `0→100`(一跳 = **无动画**)
    ///     · 补回 WS_CAPTION 后: 最小化 `100→81→0`, 最大化 `0→48→58→60`(渐变 = 有动画)
    ///   这解释了为什么**最小化**和**全屏**同时没动画 —— 两者共用这一个样式位,
    ///   与"我们自己画不画动画"无关。★ 所以只删 `DisableWindowTransitions` 是不够的。
    ///
    /// ★ 为什么补样式位**不会**把系统标题栏画出来(这是能这么修的前提):
    ///   `WindowChrome` 会处理 `WM_NCCALCSIZE`, 把非客户区压成 0 ⇒ 客户区仍然铺满整窗。
    ///   实测(同一探针): 补回后窗口 1000x700、客户区 **1000x700**、边框偏移 (0,0) —— 与不补时
    ///   完全一致; 而对照的标准窗口是 982x653、偏移 (9,38)(证明这套度量确实能测出边框, 不是假绿)。
    ///
    /// ★ 必须在**窗口显示之前**设置。`SourceInitialized`(句柄刚建好、WPF 还没 Show)是最右时机;
    ///   放到 `Loaded`/`Show` 之后会被 DWM 当成"运行时改样式", 动画可能已经错过这一轮
    ///   (探针里 `SourceInitialized` 时机实测有效: 最小化 `→81`、最大化 `→48→58`)。
    ///
    /// ★ 用 `SetWindowLongPtr` 而不是直接改 XAML 的 `WindowStyle`: 本项目所有窗口都靠
    ///   `WindowStyle=None` + `WindowChrome` 做自绘标题栏, 改成 `SingleBorderWindow` 会
    ///   让系统标题栏与自绘栏打架(这一点历史上已踩过, 见 PlayerWindow 的注释)。
    ///   **只补样式位、不动 WPF 的 WindowStyle** 才是安全的下手点。
    /// </summary>
    public static bool RestoreCaptionForDwmAnimation(Window window)
    {
        try
        {
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero) return false;

            var style = GetWindowLongPtr(hwnd, GWL_STYLE);
            if ((style & WS_CAPTION) == WS_CAPTION) return true;   // 已经有, 无需重设

            // ★ 不能用 SetWindowLongPtr 的返回值判断成败 —— 它返回的是**旧值**,
            //   而旧值完全可能恰好是 0。补完**读回来**核对才算数。
            SetWindowLongPtr(hwnd, GWL_STYLE, style | WS_CAPTION);
            return (GetWindowLongPtr(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        }
        catch
        {
            // 补不上不致命: 只是没有窗口过渡动画, 功能不受影响
            return false;
        }
    }

    private static bool SetAttr(IntPtr hwnd, int attr, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开启/关闭深色标题栏</summary>
    public static bool SetDarkTitleBar(Window window, bool dark)
        => SetAttr(GetHwnd(window), DwmwaUseImmersiveDarkMode, dark ? 1 : 0);

    /// <summary>圆角窗口(Win11 默认就有, 显式设置一次更保险)</summary>
    public static void SetRoundCorners(Window window)
        => SetAttr(GetHwnd(window), DwmwaWindowCornerPreference, DwmWcpRound);

    /// <summary>关闭云母背景(深色模式下使用, 避免 Mica 透出系统浅色云母)。
    /// 返回 true 表示成功关闭。</summary>
    public static bool DisableMica(Window window)
    {
        try
        {
            if (!IsWindows11) return true;
            var hwnd = GetHwnd(window);
            // DWMSBT_AUTO=0, DWMSBT_NONE=1, DWMSBT_MAINWINDOW=2(Mica)
            return SetAttr(hwnd, DwmwaSystemBackdropType, 1);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------ 全局亚克力(实验性)

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE 的取值(与 Models.BackdropMaterial 的枚举值一一对应)</summary>
    private const int DwmsbtNone = 1;
    private const int DwmsbtAcrylic = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>
    /// <summary>
    /// 开启/关闭"全局亚克力"背景(Win11 实验性)。
    ///
    /// ★★★ 客户区扩展**必须**走 <c>WindowChrome.GlassFrameThickness</c>(2026-10-05 修):
    ///   本工程窗口全是 WindowStyle=None + WindowChrome, 而 WindowChromeWorker 会在窗口初始化时
    ///   按 GlassFrameThickness 自己调一次 DwmExtendFrameIntoClientArea —— **晚于**我们能在
    ///   SourceInitialized 里做的任何事, 会把我们设的 -1 边距覆盖掉。
    ///   症状很隐蔽: 材质类型设上了(DwmGetWindowAttribute 读回确实是 3=Acrylic), 但客户区没扩展,
    ///   WPF 把整块画成不透明灰 ⇒ 用户看到"开了亚克力却几乎没有, 只是偏灰"。
    ///   实测(探针 .probes/bd-probe-acrylic2/3): 五种时序(SourceInitialized 先后 / Loaded 重试 /
    ///   消息钩子重试 / 运行时改属性)里, 只有让 WindowChromeWorker 拿到 -1 才真正合成出桌面内容。
    ///   ★ 真机验证(红底 A/B, 噪声基线 0.1%): 修复前红底只引起 1% 像素变化, 修复后 42~46%,
    ///     运行时开关也能双向切换(backdrop 3↔1)并立即生效。
    ///
    /// 关闭时把 GlassFrameThickness 还原成 XAML 里的 0,0,0,1, 并把材质设回 None。
    ///
    /// 返回是否成功(Win10 或不支持时返回 false, 调用方据此回退到普通纯色背景)。
    /// </summary>
    /// <summary>
    /// 按"亚克力是否开启 + 窗口当前状态"把 <c>WS_CAPTION</c> 同步到该有的样子。
    ///
    /// 规则:
    ///   · 开了亚克力 → 必须**摘掉**(否则 DWM 会在客户区右上角再画一套系统标题栏按钮,
    ///     与自绘 WindowButtons 重叠, 见 <see cref="SetAcrylicBackdrop"/> 的说明);
    ///   · 没开亚克力 → 窗口处于 Normal 时**补回**(系统最小化/最大化过渡动画靠它),
    ///     处于最大化/全屏时不动(全屏本来就该没有它, 由 ApplyFullscreenLayout 自己管)。
    ///
    /// ★ 为什么单独开一个方法: 退全屏时"窗口状态还原"与"补 WS_CAPTION"有先后依赖 ——
    ///   ApplyChrome 跑在还是 Maximized 的时刻, 那时补不回来; 必须等状态还原后再调一次这个。
    /// </summary>
    public static void SyncCaptionForAcrylic(Window window)
    {
        try
        {
            // ★ AcrylicBackground 现在是由材质**派生**的("材质 != None"), 语义仍然是
            //   "有没有用材质" —— 云母/云母Alt 同样需要摘 WS_SYSMENU(它们也会让 DWM 画系统按钮),
            //   所以这里按派生值判断是对的, 不要改成"只有亚克力才算"。
            if (Svc.Settings.AcrylicBackground)
            {
                // 开亚克力: 去掉 WS_SYSMENU —— DWM 便不再画它那套标题栏按钮,
                // 而 **WS_CAPTION 保留**, 最小化/最大化/全屏的系统过渡动画也就在。
                // (早先那版是摘 WS_CAPTION, 会把动画一起摘掉 —— 已废弃, 别再改回去。)
                HideSystemCaptionButtons(window);
            }
            else
            {
                RestoreSystemCaptionButtons(window);
            }

            // ★ WS_CAPTION 的去留只跟"是不是全屏/最大化"有关, 与亚克力无关:
            //   播放器全屏时摘掉它是为了盖住任务栏(见 SuppressCaptionForFullscreen),
            //   退全屏(状态已还原成 Normal)时补回来, 动画才会恢复。
            //   ★ 时机: 必须在 WindowState 已经回到 Normal **之后**调这个方法 ——
            //     ApplyChrome 跑在"还是 Maximized"的时刻, 那时补不回来。
            if (window.WindowState == WindowState.Normal) RestoreCaption(window);
        }
        catch
        {
            // 同步失败不致命: 顶多是少个过渡动画, 或(开亚克力时)多一套系统按钮
        }
    }

    /// <summary>
    /// 去掉 <c>WS_SYSMENU</c> —— 让 DWM 不画系统标题栏按钮。
    ///
    /// ★ 为什么不摘 <c>WS_CAPTION</c>(那是上一版的做法): <c>WS_CAPTION</c> 同时是
    ///   **"DWM 播窗口过渡动画"的依据** —— 摘了它, 最小化/最大化/全屏的动画会一起消失
    ///   (2026-10-05 用户报"全屏过渡动画消失"就是这个原因)。
    ///   而系统按钮那套图形还额外要求 <c>WS_SYSMENU</c>(窗口菜单), 去掉它既能消掉重复按钮,
    ///   又不动动画所依赖的样式位。
    /// </summary>
    private static bool HideSystemCaptionButtons(Window window)
    {
        try
        {
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero) return false;
            var style = GetWindowLongPtr(hwnd, GWL_STYLE);
            if ((style & WS_SYSMENU) == 0) return true;
            SetWindowLongPtr(hwnd, GWL_STYLE, style & ~WS_SYSMENU);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
            return (GetWindowLongPtr(hwnd, GWL_STYLE) & WS_SYSMENU) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 <c>WS_SYSMENU</c> 补回来(关掉亚克力时)</summary>
    private static bool RestoreSystemCaptionButtons(Window window)
    {
        try
        {
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero) return false;
            var style = GetWindowLongPtr(hwnd, GWL_STYLE);
            if ((style & WS_SYSMENU) == WS_SYSMENU) return true;
            SetWindowLongPtr(hwnd, GWL_STYLE, style | WS_SYSMENU);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
            return (GetWindowLongPtr(hwnd, GWL_STYLE) & WS_SYSMENU) == WS_SYSMENU;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// 按用户选择的**材质变体**下发背景材质(Win11 实验性)。
    ///
    /// ★★★ 参数为什么是枚举而不是 bool(2026-10-06): 系统没有任何 API 能改**模糊半径**
    ///   (模糊是 DWM 写死的内部常量), 唯一官方开放、且观感差异明显的就是"用哪种材质"。
    ///   所以把 <c>DWMWA_SYSTEMBACKDROP_TYPE</c> 这个选择器开放给用户, 见 BackdropMaterial。
    ///   ★ 枚举值直接就是 DWMSBT_* 的取值, 这里不做映射 —— 少一层转换少一处写错。
    /// </summary>
    /// <param name="material">要下发的材质; <see cref="BackdropMaterial.None"/> 表示不申请材质(全透明)</param>
    /// <returns>true 表示该窗口已按此材质设置好(调用方据此决定窗口底色)</returns>
    public static bool SetAcrylicBackdrop(Window window, BackdropMaterial material)
    {
        try
        {
            if (!IsWindows11) return false;
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero) return false;

            // ★ 无材质 == 关闭: 复用下面"关闭"那条路(玻璃边距还原、材质置 None)
            var on = material != BackdropMaterial.None;
            // ★★★ 关键: 客户区扩展**必须**走 WindowChrome.GlassFrameThickness,
            //   不能只自己调 DwmExtendFrameIntoClientArea —— 实测(探针 bd-probe-acrylic2):
            //   本工程窗口是 WindowStyle=None + WindowChrome, WindowChromeWorker 会**在**
            //   我们的 SourceInitialized 之后**按 GlassFrameThickness 自己再调一次那个 API,
            //   把我们设的 -1 边距覆盖掉。结果: 材质类型设上了(读回确实是 3), 但客户区没扩展,
            //   WPF 把整块画成不透明灰 —— 用户看到的就是"开了亚克力却几乎没有, 只是偏灰"。
            //   实测五种时序(SourceInitialized 先后 / Loaded 重试 / 消息钩子重试)全是灰,
            //   只有 GlassFrameThickness = -1 才真正合成出桌面内容(偏红 251,159,159)。
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            if (chrome != null)
            {
                var glass = on ? new Thickness(-1) : new Thickness(0, 0, 0, 1);
                if (chrome.GlassFrameThickness != glass) chrome.GlassFrameThickness = glass;
            }
            else
            {
                // ★★★ 没有 WindowChrome 的窗口**不支持**亚克力, 直接拒绝(2026-10-05 修):
                //   本工程有两类窗口 —— 自绘边框(MainWindow / PlayerWindow / ImagePreviewWindow,
                //   都是 WindowStyle=None + WindowChrome)与**系统标准窗口**(收藏夹选择/登录/浏览器/
                //   免责声明/更新提示, 用的是系统标题栏)。
                //   对标准窗口扩展客户区玻璃会把**系统标题栏之外的内容区也变成非客户区**,
                //   DWM 用系统材质覆盖它 —— 浅色主题下 WPF 画的内容被压黑, 用户看到的就是
                //   "收藏界面整个发黑"(它们本来就不该参与亚克力, 也就没有"自绘按钮重复"的问题)。
                //   所以这里返回 false, 让调用方(ApplyChrome)回落到普通实色背景。
                return false;
            }


            // ★★★ 处理"右上角按钮重复"(2026-10-05):
            //   GlassFrameThickness=-1 把整个客户区纳入 DWM 合成后, DWM 会**自己也在右上角
            //   画一套系统标题栏按钮**, 与自绘 WindowButtons 叠在一起。
            //   实测(真机截图, 右上角 300x60 暗像素占比): 关闭亚克力 0.49%(仅自绘字形),
            //   开亚克力不处理 0.91%(多出一套系统按钮)。
            //   ★ 做法: 去掉 **WS_SYSMENU**(见 SyncCaptionForAcrylic) —— **不要**动 WS_CAPTION,
            //     那是 DWM 播窗口过渡动画的依据, 摘了会让最小化/最大化/全屏都没动画。
            SyncCaptionForAcrylic(window);

            // ★★★ "失焦变灰"在这个函数里**修不了**, 别再往这里加代码(2026-10-06 第三次确认):
            //   现象: 窗口失焦后亚克力消失、整窗变灰(221,222,223)。
            //   根因: DWM 的 Background Acrylic 材质**在窗口失焦时会被替换成实色** —— 这是
            //         微软的**设计行为**, 不是 bug, 也没有 API 能关掉。文档原话(见下面的链接):
            //           "only background acrylic will replace its translucency and texture with a
            //            solid color: When an app window on desktop deactivates."
            //         (https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)
            //   ★ 试过但**实测无效**、别再试:
            //     · DWMWA_USE_HOSTBACKDROPBRUSH(17)=1 —— 探针 bd-probe-acrylic-inactive 做了
            //       A/B(同一窗口先 1 后 0): **两种状态画面完全一样** ⇒ 它是"让我自己用
            //       Composition 画 host backdrop"的**开关**, 不是"阻止失焦回落"的开关, 加了等于没加。
            //     · 失焦时重新下发 DWMWA_SYSTEMBACKDROP_TYPE —— 读回仍是 3, 画面仍灰(属空操作:
            //       这个属性只决定"用哪种材质", 失焦回落是材质自身行为, 与属性值无关)。
            //     · 换成云母 DWMSBT_MAINWINDOW(2) —— 云母文档同样写明会回落, 且它**本身不透明**
            //       (只采样一次壁纸、从不透出后面的窗口), 换过去连"透"都没有了。
            //   ⇒ 修法在**上层**: FluentWindow 按激活态切换自己的窗口底色(见
            //     FluentWindow.UpdateAcrylicWindowBase)。材质照旧申请, 但失焦时用我们的主题实色
            //     盖住 DWM 那块灰, 让"变灰"变成"看起来像刻意进入的背景态"。
            var value = on ? (int)material : DwmsbtNone;
            return SetAttr(hwnd, DwmwaSystemBackdropType, value);
        }
        catch
        {
            return false;
        }
    }

    // ★★★ 这里原来有一个 DisableWindowTransitions(Window)(写
    //   DWMWA_TRANSITIONS_FORCEDISABLED=3, 关掉该窗口的 DWM 过渡动画), 2026-10-02
    //   **连同那个 DWMWA 常量一起删除, 别再加回来**。
    //
    //   删除原因 = 用户要求"把全屏动画改成 Windows 系统自带的动画, 不再自己绘制":
    //     播放器以前是"秒切、无动画", 为了让那一下不叠上系统的窗口缩放过渡, 就在
    //     SourceInitialized 里把这个开关打开, 主动把 Windows 自己的动画掐掉了。
    //     现在方向正好相反 —— 自绘动画已全部删除, 要的就是 DWM 那段系统过渡。
    //     一旦有谁再调一次 DisableWindowTransitions, 系统动画会**静默消失**(窗口照常最大化,
    //     只是没有过渡), 表现出来就是"改了没用 / 又变成硬切", 极难排查。

    // ------------------------------------------------------------ 全屏盖任务栏

    /// <summary>
    /// 摘掉 <c>WS_CAPTION</c> 样式位。播放器**进全屏**时调; 退全屏用 <see cref="RestoreCaption"/>。
    ///
    /// ★★★ 为什么进全屏必须摘掉它(2026-10-03, 用户报"视频全屏后盖不住任务栏"的根因):
    ///   同一天为了"最小化/全屏有系统动画", <see cref="RestoreCaptionForDwmAnimation"/> 把
    ///   WS_CAPTION 补回了所有自绘标题栏窗口 —— 动画回来了, 但 Windows 外壳的全屏检测
    ///   (FULLSCREENOBJECT/任务栏自动隐藏)只认"**无**标题栏样式 + 铺满显示器"的窗口:
    ///   带 WS_CAPTION 的窗口就算尺寸铺满整屏, 外壳仍当它是普通最大化窗口, 任务栏照常浮在上面。
    ///   (历史上一直没暴露, 是因为旧代码从补 WS_CAPTION 那天起才有这个样式位。)
    ///
    ///   全屏期间自绘标题栏整行已收成 0 高(见 PlayerWindow.ApplyFullscreenLayout),
    ///   WS_CAPTION 没有可见作用; 而全屏的第一诉求就是盖住任务栏, 所以这里摘掉,
    ///   退全屏时由 <see cref="RestoreCaption"/> 补回来, 系统动画照旧。
    ///
    /// ★ SetWindowLongPtr 之后必须跟一个 SetWindowPos(SWP_FRAMECHANGED), 否则新的样式
    ///   不会立刻反映到外壳的窗口判定上(样式改了但框架没重算, 任务栏该露还露)。
    /// </summary>
    public static bool SuppressCaptionForFullscreen(Window window)
    {
        try
        {
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero) return false;
            var style = GetWindowLongPtr(hwnd, GWL_STYLE);
            if ((style & WS_CAPTION) == 0) return true;   // 本来就没有, 无需动
            SetWindowLongPtr(hwnd, GWL_STYLE, style & ~WS_CAPTION);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
            return (GetWindowLongPtr(hwnd, GWL_STYLE) & WS_CAPTION) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 <c>WS_CAPTION</c> 补回来(退全屏用)。与 <see cref="RestoreCaptionForDwmAnimation"/> 相同的核对方式。</summary>
    public static bool RestoreCaption(Window window)
    {
        try
        {
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero) return false;
            var style = GetWindowLongPtr(hwnd, GWL_STYLE);
            if ((style & WS_CAPTION) == WS_CAPTION) return true;
            SetWindowLongPtr(hwnd, GWL_STYLE, style | WS_CAPTION);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
            return (GetWindowLongPtr(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        }
        catch
        {
            return false;
        }
    }
}
