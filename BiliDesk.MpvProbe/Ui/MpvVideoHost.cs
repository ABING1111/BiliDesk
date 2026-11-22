using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BiliDesk.MpvProbe.Ui;

/// <summary>
/// 把 mpv 的画面挂进 WPF 的 HwndHost(2026-11-22, feat/mpv-core PoC)。
///
/// ★ 与 VLC 一样, 这是**原生子窗口**, 不在 WPF 合成器里 —— 任何 WPF 元素都会被它盖住
///   (airspace)。所以控制栏/弹幕必须放在一个独立的顶层覆盖窗口里, 见 <see cref="MpvOverlayWindow"/>。
/// </summary>
public sealed class MpvVideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;

    public IntPtr Hwnd { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        Hwnd = CreateWindowEx(
            0, "static", "",
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx 失败: " + Marshal.GetLastWin32Error());
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        Hwnd = IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
