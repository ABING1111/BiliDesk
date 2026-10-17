using System;
using System.Runtime.InteropServices;

namespace BiliDesk.Helpers;

/// <summary>
/// 播放器视频区「原生窗口底色」的最小 P/Invoke 集合。
///
/// ★ 为什么需要它(2026-10-17): 视频没填满视频区时, 上下(或左右)那条空白是**白色**的。
///   真根因不在 WPF —— `VideoView` 是 <c>HwndHost</c>, 它内部由 libvlc 建了一串同进程子窗口:
/// <code>
///   HwndWrapper[...]   ← HwndHost 宿主窗口        ★改它的类背景刷才有效
///     Static           ← 铺满整个视频区            (白边出现的地方)
///     VLC video main   ← 铺满整个视频区
///     VLC video output ← 只盖住画面那一块
/// </code>
///   `video output` 只占画面那一块, 剩下的区域没人画 ⇒ 露出 `Static` 的类默认背景(白)。
///   逐层隐藏实测: 藏 `Static` → 边带亮度 240→0; 藏另外两个 → 不变。
///
/// ★★ 关键细节(容易搞反, 两个都实测过):
///   · 对 `Static` **自己**涂黑 → **无效**(边带仍 242);
///   · 对它的**父窗口** `HwndWrapper` 涂黑 → **有效**(边带 242→0, 画面正常, 持续播放不回弹)。
///   ⇒ "白边是 Static 显示出来的"≠"改 Static 就能黑": 擦背景的是父窗口那一层。
///
/// ★ 它是**本进程**的窗口, 所以这些 API 可用。
/// ★ 为什么不用 WPF 层解决(比如给 VideoView 加不透明背景): 那层在视频**之上**
///   (VideoAreaRoot 在 VideoView.Content 浮层里), 实测涂黑会把**画面一起盖掉**。
/// </summary>
internal static class VideoBackdropInterop
{
    /// <summary>窗口类的背景刷索引(Win32: GCLP_HBRBACKGROUND)</summary>
    private const int GclpHbrBackground = -10;

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    /// <summary>
    /// 把视频区那块原生窗口的类背景刷设成纯黑并立刻擦一次。
    ///
    /// 传进来的应当是 HwndHost 的**宿主窗口**(`HwndWrapper`) —— 见类注释里那条"对 Static
    /// 自己涂黑无效"的实测结论。
    /// ★ 用 `SetClassLongPtr`(改类)而不是 `SetWindowLongPtr(GWL_HBRBACKGROUND)`:
    ///   后者只在 Win32 子类化路径里有效, 对 WPF 的 HwndWrapper 不生效(实测)。
    /// ★ 失败一律静默: 顶多还是白边, 绝不能因为"修外观"影响播放。
    /// </summary>
    public static void PaintClassBackgroundBlack(IntPtr hwnd)
    {
        var target = GetParent(hwnd);      // ★ 必须是父窗口, 见类注释
        if (target == IntPtr.Zero) target = hwnd;   // 兜底: 拿不到父级就试自己
        try
        {
            var brush = CreateSolidBrush(0x000000);
            if (brush == IntPtr.Zero) return;
            SetClassLongPtr(target, GclpHbrBackground, brush);
            // 立刻擦一次: 用户报的就是"一直白着", 不能等下一次重绘
            InvalidateRect(target, IntPtr.Zero, true);
            UpdateWindow(target);
        }
        catch { /* 外观修复失败不影响播放 */ }
    }
}
