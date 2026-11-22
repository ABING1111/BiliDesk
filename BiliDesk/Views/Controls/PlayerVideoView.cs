using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;

namespace BiliDesk.Views.Controls;

/// <summary>
/// mpv 视频区宿主(2026-11-22, feat/mpv-core)。
///
/// 替代 LibVLCSharp.WPF 的 <c>VideoView</c>:
///   · 自己是一个 HwndHost(原生子窗口), mpv 通过 `wid` 把画面画进来;
///   · XAML 里的子内容(控制栏/弹幕/字幕/浮层)**不在主窗口里渲染**, 而是搬进一个
///     跟随本控件矩形的无边框透明顶层窗口 —— 只有这样才压得住原生视频窗口(airspace)。
///   · <see cref="OverlayWindow"/> 暴露给 PlayerWindow 挂键盘/滚轮/激活事件
///     (原来是反射拿 LibVLCSharp 的 ForegroundWindow)。
///
/// 用法与 VideoView 相同:
/// <code>
/// &lt;controls:PlayerVideoView x:Name="VideoView"&gt;
///     &lt;Grid&gt;…覆盖层内容…&lt;/Grid&gt;
/// &lt;/controls:PlayerVideoView&gt;
/// </code>
/// </summary>
[ContentProperty(nameof(OverlayContent))]
public sealed class PlayerVideoView : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;

    private Window? _overlay;
    private Window? _owner;
    private HwndSource? _ownerSource;
    private IntPtr _overlayHwnd;
    private DispatcherTimer? _timer;
    private object? _overlayContent;
    private bool _surfaceReadyRaised;

    public PlayerVideoView()
    {
        Loaded += (_, _) => AttachOverlay();
        Unloaded += (_, _) => DetachOverlay();
        SizeChanged += (_, _) => SyncOverlayBounds();
    }

    /// <summary>原生视频窗口已建好(宿主 HWND 可用)。</summary>
    public event EventHandler? SurfaceReady;

    /// <summary>mpv 的 `wid` 要用的宿主窗口句柄。</summary>
    public IntPtr SurfaceHandle { get; private set; }

    /// <summary>跟随视频区的透明覆盖窗口(控制栏等内容在这里)。</summary>
    public Window? OverlayWindow => _overlay;

    /// <summary>XAML 里的子内容(控制栏/弹幕/字幕), 会被搬进覆盖窗口。</summary>
    public object? OverlayContent
    {
        get => _overlayContent;
        set
        {
            _overlayContent = value;
            if (_overlay != null) _overlay.Content = value;
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        SurfaceHandle = CreateWindowEx(
            0, "static", "",
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (SurfaceHandle == IntPtr.Zero)
            throw new InvalidOperationException("创建视频宿主窗口失败: " + Marshal.GetLastWin32Error());

        return new HandleRef(this, SurfaceHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        SurfaceHandle = IntPtr.Zero;
    }

    private void AttachOverlay()
    {
        if (_overlay != null) return;
        var owner = Window.GetWindow(this);
        if (owner == null) return;

        _overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            Content = _overlayContent,
            Owner = owner,
        };
        _overlay.Show();

        // ★ 2026-11-22: 拖动主窗口时覆盖层必须**逐帧**跟随, 100ms 轮询会明显"分离"。
        //   这里直接挂 owner 的窗口消息(WM_MOVE/WM_SIZE/WM_WINDOWPOSCHANGED),
        //   并用 GetWindowRect(宿主 HWND) + SetWindowPos 在**物理像素**上同步,
        //   完全不经过 WPF 的 Left/Top(它有额外的布局延迟)。
        _owner = owner;
        _ownerSource = HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle);
        _ownerSource?.AddHook(OwnerWndProc);
        owner.LocationChanged += OnOwnerGeometryChanged;
        owner.SizeChanged += OnOwnerGeometryChanged;
        owner.StateChanged += OnOwnerGeometryChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => SyncOverlayBounds();
        _timer.Start();
        SyncOverlayBounds();

        if (!_surfaceReadyRaised)
        {
            _surfaceReadyRaised = true;
            SurfaceReady?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DetachOverlay()
    {
        _timer?.Stop();
        _timer = null;

        if (_owner != null)
        {
            try
            {
                _owner.LocationChanged -= OnOwnerGeometryChanged;
                _owner.SizeChanged -= OnOwnerGeometryChanged;
                _owner.StateChanged -= OnOwnerGeometryChanged;
            }
            catch { }
            _owner = null;
        }

        if (_ownerSource != null)
        {
            try { _ownerSource.RemoveHook(OwnerWndProc); } catch { }
            _ownerSource = null;
        }

        if (_overlay != null)
        {
            try { _overlay.Content = null; } catch { }
            try { _overlay.Close(); } catch { }
            _overlay = null;
        }

        _overlayHwnd = IntPtr.Zero;
    }

    private void OnOwnerGeometryChanged(object? sender, EventArgs e) => SyncOverlayBounds();

    /// <summary>
    /// 主窗口的移动/缩放消息: 在拖动(模态 move loop)期间也会同步到达,
    /// 用它把覆盖层窗口"钉"在视频区上, 避免出现"控制栏和画面分离"。
    /// </summary>
    private IntPtr OwnerWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmMove:
            case WmSize:
            case WmWindowPosChanged:
            case WmEnterSizeMove:
            case WmExitSizeMove:
            case WmDpiChanged:
                SyncOverlayBounds();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 覆盖窗口跟着视频区走。
    /// ★ 用宿主 HWND 的屏幕矩形(物理像素)而不是 WPF 的 PointToScreen/Left-Top:
    ///   后者在拖动时有一拍以上的延迟, 表现就是"控制栏拖在后面"。
    /// </summary>
    private void SyncOverlayBounds()
    {
        if (_overlay == null) return;

        if (_owner == null || _owner.WindowState == WindowState.Minimized ||
            !IsVisible || SurfaceHandle == IntPtr.Zero)
        {
            if (_overlay.IsVisible) _overlay.Hide();
            return;
        }

        if (!_overlay.IsVisible) _overlay.Show();

        if (_overlayHwnd == IntPtr.Zero)
            _overlayHwnd = new WindowInteropHelper(_overlay).Handle;
        if (_overlayHwnd == IntPtr.Zero) return;

        if (!GetWindowRect(SurfaceHandle, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            if (_overlay.IsVisible) _overlay.Hide();
            return;
        }

        SetWindowPos(_overlayHwnd, IntPtr.Zero, rect.Left, rect.Top, width, height,
            SwpNoActivate | SwpNoZOrder);
    }

    private const int WmMove = 0x0003;
    private const int WmSize = 0x0005;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDpiChanged = 0x02E0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);


    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
