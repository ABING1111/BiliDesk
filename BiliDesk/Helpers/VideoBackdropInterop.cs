using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BiliDesk.Helpers;

/// <summary>
/// 播放器视频区「原生窗口底色」的最小 P/Invoke 集合。
///
/// ★ 为什么需要它(2026-10-17): 视频没填满视频区时, 上下(或左右)那条空白是**白色**的。
///   真根因不在 WPF —— `VideoView` 是 <c>HwndHost</c>, 它内部由 libvlc 建了一串同进程子窗口:
/// <code>
///   HwndWrapper[...]   ← WPF HwndHost 宿主窗口      ★父窗口
///     Static           ← LibVLCSharp.WPF 建的视频宿主 ★白边/白闪的作者
///     VLC video main   ← 铺满整个视频区
///     VLC video output ← 只盖住画面那一块
/// </code>
///   `video output` 只占画面那一块, 剩下的区域没人画 ⇒ 露出 `Static` 的类默认背景(白)。
///
/// ★★ 2026-11-03: 去掉 resize/最大化/全屏期间的 WPF 黑布, 改为在原生层把 `Static` 治成
///   "永远近黑底且不覆盖子窗口":
///     · <see cref="AddClipChildren"/>: 给 Static 与父 HwndWrapper 加 `WS_CLIPCHILDREN`。
///       没有它时, 父窗口重绘会把自己的白底刷到 `VLC video output` 子窗口上, 表现就是
///       拖动窗口/最大化时整块白闪 (LibVLCSharp 上游 2025-07 的同类修复也是加这个 style)。
///     · <see cref="EnsureSubclassed"/>: Static 本来就是一个没有内容的系统 `static` 窗口,
///       子类化后只接管 `WM_ERASEBKGND`, 把客户区填成**近黑**; 其余消息原样转发。
///       `WS_CLIPCHILDREN` 保证填色只发生在"没有视频子窗口盖住"的边带区域, 不会盖住画面。
///       ★ 不接管 `WM_PAINT`: 某些 vout/显卡组合下画面可能由 Static 自己绘制, 吞掉
///       WM_PAINT 会把画面一起盖黑; `WM_ERASEBKGND` + 近黑类背景刷已经足够挡住白底。
///     · <see cref="PaintClassBackgroundLetterbox"/>: 继续把父窗口类背景刷设成近黑, 兜住
///       Static 被 libvlc 重新显示/重绘的瞬间。
///
/// ★★★ 2026-11-16 修「云母/亚克力下改窗口大小, 视频上下黑边不断闪」:
///   材质开启时 <c>FluentWindow.ApplyChrome</c> 会把 <c>WindowChrome.GlassFrameThickness</c>
///   设成 -1, **整个客户区都成为 DWM 玻璃区**。而 DWM 对 GDI 绘制的**纯黑**(0x00000000)
///   按全透明处理(Aero 玻璃的经典坑: "用黑色 GDI 笔刷画出来的就是透明色")。
///   于是视频区的黑边在材质下变成了"透明洞", 露出后面的云母/亚克力; 窗口尺寸一变,
///   Static 反复擦除/重绘, 观感就是黑边在"黑"和"材质色"之间不断闪。
///   ⇒ 视频区的底色一律用 **近黑 #010101**: 肉眼与纯黑无法区分, 但它不是 DWM 的
///     透明色。★ 不要改回 0x000000 —— 普通材质下看不出问题, 云母/亚克力下必然复发。
///
/// ★ 它是**本进程**的窗口, 所以这些 API 可用。
/// ★ 为什么不用 WPF 层解决(比如给 VideoView 加不透明背景): 那层在视频**之上**
///   (VideoAreaRoot 在 VideoView.Content 浮层里), 实测涂黑会把**画面一起盖掉**。
/// </summary>
internal static class VideoBackdropInterop
{
    /// <summary>窗口类的背景刷索引(Win32: GCLP_HBRBACKGROUND)</summary>
    private const int GclpHbrBackground = -10;

    /// <summary>窗口过程索引(Win32: GWLP_WNDPROC)</summary>
    private const int GwlpWndproc = -4;

    /// <summary>窗口样式索引(Win32: GWL_STYLE)</summary>
    private const int GwlStyle = -16;

    /// <summary>WS_CLIPCHILDREN: 重绘父窗口时把子窗口区域排除在外</summary>
    private const long WsClipChildren = 0x02000000L;

    private const int SwHide = 0;

    private const int WmEraseBkgnd = 0x0014;

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;

    private delegate IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 进程级唯一的「视频区边带」GDI 笔刷。类背景刷要求笔刷长期有效, 不能用完就删。
    ///
    /// ★★★ 为什么是 **近黑 #010101 而不是纯黑 #000000**(2026-11-16):
    ///   云母/亚克力开启时整个客户区是 DWM 玻璃区(见 <see cref="EnsureLetterboxVideoHost"/>
    ///   的类注释)。DWM 把 GDI 绘制的**纯黑**(0x00000000)视为**全透明**,
    ///   于是视频上下/左右的黑边会露出材质, 并在 resize 时反复闪。
    ///   RGB(1,1,1) 肉眼与纯黑无差别, 但不会命中 DWM 的透明色。
    ///   ★ 这是 GDI 在玻璃上的固有坑 —— 换成 <c>FillRect</c> 的任意纯黑笔刷都会复发。
    /// </summary>
    private static readonly IntPtr LetterboxBrush = CreateSolidBrush(0x010101);

    /// <summary>子类化用的委托必须静态持有, 否则 GC 后窗口过程会指向已释放的托管对象。</summary>
    private static readonly WindowProc SubclassProcDelegate = SubclassWndProc;

    private static readonly IntPtr SubclassProcPtr =
        Marshal.GetFunctionPointerForDelegate(SubclassProcDelegate);

    /// <summary>HWND → 原来的窗口过程。只在 UI 线程/同一进程内使用。</summary>
    private static readonly Dictionary<IntPtr, IntPtr> OriginalProcs = new();

    private static readonly object Gate = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref NativeRect lprc, IntPtr hbr);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd,
        int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    /// <summary>
    /// 把视频区原生宿主窗口处理成"近黑底 + 不覆盖子窗口"。
    ///
    /// 传进来的是 HwndHost 的 `Static` 子窗口(见 <c>PlayerWindow.FindVideoStaticWindow</c>)。
    /// 失败一律静默: 顶多还是白边/白闪, 绝不能因为"修外观"影响播放。
    /// ★ 底色是近黑 <c>#010101</c> 而不是纯黑 —— 原因见类注释与 <see cref="LetterboxBrush"/>。
    /// </summary>
    public static void EnsureLetterboxVideoHost(IntPtr staticHwnd)
    {
        if (staticHwnd == IntPtr.Zero || !IsWindow(staticHwnd)) return;

        try
        {
            // ① 先把类背景刷设成近黑: 这样后面加样式/重算非客户区时, 已经不会擦出白底。
            var parent = GetParent(staticHwnd);
            SetClassBrushLetterbox(staticHwnd);
            if (parent != IntPtr.Zero) SetClassBrushLetterbox(parent);

            // ② 父窗口先裁剪子窗口: 否则父窗口重绘时会把白底刷到视频子窗口上。
            if (parent != IntPtr.Zero) AddClipChildren(parent);
            AddClipChildren(staticHwnd);

            // ③ 子类化 Static: 它的客户区被擦/重绘时填近黑。
            EnsureSubclassed(staticHwnd);
        }
        catch { /* 外观修复失败不影响播放 */ }
    }

    /// <summary>
    /// 把视频区原生窗口的类背景刷设成**近黑**并立刻擦一次(**兜底**)。
    ///
    /// ★ 2026-11-03 起主修是 <see cref="EnsureLetterboxVideoHost"/>; 这个方法保留给
    ///   `Vout` 原生回调那种"不能做子类化/SetWindowPos"的线程使用, 以及作为额外兜底。
    /// ★ 用 `SetClassLongPtr`(改类)而不是 `SetWindowLongPtr(GWL_HBRBACKGROUND)`:
    ///   后者只在 Win32 子类化路径里有效, 对 WPF 的 HwndWrapper 不生效(实测)。
    /// ★ 底色必须是近黑 #010101, 不是纯黑 —— 云母/亚克力下纯黑 GDI 会被 DWM 当透明
    ///   (见类注释与 <see cref="LetterboxBrush"/>)。
    /// ★ 失败一律静默: 顶多还是白边, 绝不能因为"修外观"影响播放。
    /// </summary>
    public static void PaintClassBackgroundLetterbox(IntPtr hwnd)
    {
        var target = GetParent(hwnd);      // 必须改父窗口的类背景刷
        if (target == IntPtr.Zero) target = hwnd;   // 兜底: 拿不到父级就试自己
        SetClassBrushLetterbox(target);
    }

    private static void SetClassBrushLetterbox(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || LetterboxBrush == IntPtr.Zero) return;
        try
        {
            SetClassLongPtr(hwnd, GclpHbrBackground, LetterboxBrush);
            // 立刻擦一次: 用户报的就是"一直白着", 不能等下一次重绘
            InvalidateRect(hwnd, IntPtr.Zero, true);
            UpdateWindow(hwnd);
        }
        catch { /* 外观修复失败不影响播放 */ }
    }

    /// <summary>给窗口加 `WS_CLIPCHILDREN`。已经是该样式时不做任何事。</summary>
    private static void AddClipChildren(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        if ((style & WsClipChildren) != 0) return;

        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style | WsClipChildren));
        // 样式改变后必须让窗口重新计算客户区/边框, 否则部分窗口不会按新样式绘制。
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNosize | SwpNomove | SwpNozorder | SwpNoactivate | SwpFramechanged);
    }

    /// <summary>只子类化一次; HWND 变了(重建宿主)就重新接上。</summary>
    private static void EnsureSubclassed(IntPtr hwnd)
    {
        lock (Gate)
        {
            if (GetWindowLongPtr(hwnd, GwlpWndproc) == SubclassProcPtr) return;

            var previous = SetWindowLongPtr(hwnd, GwlpWndproc, SubclassProcPtr);
            if (previous != IntPtr.Zero) OriginalProcs[hwnd] = previous;
        }
    }

    /// <summary>
    /// Static 宿主窗口的窗口过程。
    ///
    /// ★ 只接管 `WM_ERASEBKGND` 一条消息: 填**近黑**并返回 1 —— 不让系统默认白底擦出来,
    ///   也不让 DWM 把纯黑当透明色(见类注释)。
    ///   ★ 不接管 `WM_PAINT`: 某些 vout/显卡组合下画面可能由 Static 自己绘制, 吞掉
    ///     WM_PAINT 会把画面一起盖黑; 实测 `WM_ERASEBKGND` + `WS_CLIPCHILDREN` + 近黑类背景刷
    ///     已经足够挡住 resize/最大化时的白底(bd-probe-resize/white/fullscreen 全过)。
    /// ★ 其余消息必须原样转发, 否则 HwndHost 的鼠标/尺寸/焦点处理会断。
    /// </summary>
    private static IntPtr SubclassWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmEraseBkgnd)
        {
            FillClientRect(hwnd, wParam);
            return new IntPtr(1);
        }

        // ★ 2026-11-03: 只接管 WM_ERASEBKGND。WM_PAINT 仍交给原来的 Static 过程 ——
        //   Static 没有 SS_* 样式, 默认过程本来就不画内容; 视频由 VLC 子窗口绘制。
        //   这样即使某些 vout/显卡组合下画面是经由 Static 自己画的, 也不会被我们盖掉。
        IntPtr original;
        lock (Gate)
            OriginalProcs.TryGetValue(hwnd, out original);

        return original != IntPtr.Zero
            ? CallWindowProc(original, hwnd, msg, wParam, lParam)
            : DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void FillClientRect(IntPtr hwnd, IntPtr hdc)
    {
        if (hdc == IntPtr.Zero || LetterboxBrush == IntPtr.Zero) return;
        if (GetClientRect(hwnd, out var rc))
            FillRect(hdc, ref rc, LetterboxBrush);
    }

    /// <summary>
    /// 把 libvlc 那个铺满视频区的 `Static` 窗口**藏起来**。
    ///
    /// ★★★ 产品代码**没有**用它, 保留它只为留档(2026-10-17 实测结论):
    ///   在探针里 `ShowWindow(Static, SW_HIDE)` 确实能把边带刷到 0, 看起来是个漂亮的修法;
    ///   但真机一跑就发现**整个视频画面也没了** —— 说明在这台机器/这套 vout 上,
    ///   画面就是经由 `Static` 那条链画出来的(与"画面由 video output 画"的假设不同,
    ///   不同显卡/vout 组合拓扑会变)。
    ///   ⇒ 绝对不能作为产品默认行为。产品走 <see cref="EnsureLetterboxVideoHost"/>。
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
