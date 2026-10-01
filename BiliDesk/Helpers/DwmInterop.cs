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

    /// <summary>DWMWA_TRANSITIONS_FORCEDISABLED —— 关掉该窗口的 DWM 过渡动画</summary>
    private const int DwmwaTransitionsForceDisabled = 3;

    // DWMSBT 值: 1=None 2=Mica 3=Acrylic 4=Tabbed
    private const int DwmWcpRound = 2;         // 圆角窗口

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static IntPtr GetHwnd(Window window) => new WindowInteropHelper(window).Handle;

    /// <summary>是否为 Win11(Build 22000+)</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

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

    /// <summary>
    /// 关掉这个窗口的 DWM 过渡动画(最大化/还原/显示隐藏时那一下"缩放"由 DWM 播, 不归 WPF 管)。
    ///
    /// 谁需要它: **播放器的全屏**。全屏切换本来就有自己的遮黑过渡, 再叠一层 DWM 的窗口缩放动画
    /// 就是"按了之后要等它慢慢放大"的那种拖沓感(实测体感很明显)。WPF 那一侧的内容变换动画
    /// (FluentWindow 里那段 Scale + 淡入)2026-09-30 已经整体删除, 所以现在只剩 DWM 这一层要关。
    ///
    /// 只给播放器用: 主窗口的最大化保留系统动画, 那是符合预期的观感。
    /// </summary>
    public static bool DisableWindowTransitions(Window window)
        => SetAttr(GetHwnd(window), DwmwaTransitionsForceDisabled, 1);
}