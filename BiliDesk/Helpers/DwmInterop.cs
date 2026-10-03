using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BiliDesk.Helpers;

/// <summary>DWM(桌面窗口管理器)互操作: 云母/Mica 背景、深色标题栏、亚克力等</summary>
public static class DwmInterop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaWindowCornerPreference = 33;

    // DWMSBT 值: 1=None 2=Mica 3=Acrylic 4=Tabbed
    private const int DwmWcpRound = 2;         // 圆角窗口

    // ---- 窗口样式(GWL_STYLE) ----
    private const int GWL_STYLE = -16;
    /// <summary>WS_CAPTION = WS_BORDER | WS_DLGFRAME(0x00C00000)</summary>
    private const long WS_CAPTION = 0x00C00000L;

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
