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
///     Static           ← 铺满整个视频区            (白边出现的地方, 也是**白色作者**)
///     VLC video main   ← 铺满整个视频区
///     VLC video output ← 只盖住画面那一块
/// </code>
///   `video output` 只占画面那一块, 剩下的区域没人画 ⇒ 露出 `Static` 的类默认背景(白)。
///
/// ★★ 2026-10-17 第二版(用户报"退出全屏后视频区白块问题依旧"):
///   第一版只**改类背景刷**, 在探针里能修好, 但真机上"退出全屏/换清晰度"这类
///   **vout 重建**的场景仍会回白 —— 因为 libvlc 重建出来的是一个**新的 `Static` 子窗口**,
///   而"擦背景的是父窗口那一层"这件事只在**某些绘制时序**下成立: 新窗口第一次
///   WM_PAINT 之前, 屏幕上还是它的类默认白。
///   ⇒ 这一版改成**双保险**, 且把 `Static` 当作"纯装饰窗口"直接藏掉:
///     · <see cref="SuppressStaticWindow"/>: `ShowWindow(SW_HIDE)` —— 它本来就不该显示
///       任何东西(画面由 `video output` 画, 见上面的拓扑), 藏掉它 = 白边**从根上不存在**。
///       探针 bd-probe-letterbox 的逐层隐藏实测: 藏 `Static` → 边带亮度 240→0,
///       藏另外两个 → 不变。**它就是白色的作者**。
///     · <see cref="PaintClassBackgroundBlack"/>: 仍然把宿主窗口的类背景刷设成黑,
///       兜住"Static 又被 libvlc 显示出来"的那些瞬间。
///   ⇒ 两者互补: 前者治本(白边没了), 后者兜底(万一 Static 回来也是黑的)。
///
/// ★ 它是**本进程**的窗口, 所以这些 API 可用。
/// ★ 为什么不用 WPF 层解决(比如给 VideoView 加不透明背景): 那层在视频**之上**
///   (VideoAreaRoot 在 VideoView.Content 浮层里), 实测涂黑会把**画面一起盖掉**。
/// </summary>
internal static class VideoBackdropInterop
{
    /// <summary>窗口类的背景刷索引(Win32: GCLP_HBRBACKGROUND)</summary>
    private const int GclpHbrBackground = -10;

    private const int SwHide = 0;

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

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

    /// <summary>
    /// 把 libvlc 那个铺满视频区的 `Static` 窗口**藏起来**。
    ///
    /// ★★★ 产品代码**没有**用它, 保留它只为留档(2026-10-17 实测结论):
    ///   在探针里 `ShowWindow(Static, SW_HIDE)` 确实能把边带刷到 0, 看起来是个漂亮的修法;
    ///   但真机一跑就发现**整个视频画面也没了** —— 说明在这台机器/这套 vout 上,
    ///   画面就是经由 `Static` 那条链画出来的(与"画面由 video output 画"的假设不同,
    ///   不同显卡/vout 组合拓扑会变)。
    ///   ⇒ 绝对不能作为产品默认行为。产品走 <see cref="PaintClassBackgroundBlack"/>。
    /// ★ 保留而不删掉的理由: 下次再遇到"白边"时, 这段代码 + 这条结论能省掉一次同样的试错;
    ///   而它不被调用就不会进产物(纯静态方法, 无副作用)。
    /// </summary>
    /// <returns>true = 这个窗口当前确实是隐藏的</returns>
    public static bool SuppressStaticWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            if (!IsWindow(hwnd)) return false;
            if (IsWindowVisible(hwnd)) ShowWindow(hwnd, SwHide);
            // 藏完再确认一次: 有些时序下 ShowWindow 会被 libvlc 紧接着的 ShowWindow 顶回来
            return !IsWindowVisible(hwnd);
        }
        catch
        {
            return false;
        }
    }
}
