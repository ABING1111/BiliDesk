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

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
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
        if (_overlay != null)
        {
            try { _overlay.Content = null; } catch { }
            try { _overlay.Close(); } catch { }
            _overlay = null;
        }
    }

    /// <summary>
    /// 覆盖窗口跟着视频区走。100ms 轮询而不是只挂 LocationChanged: 主窗口最大化/还原、
    /// 全屏切换、铺满窗口都可能只改布局不改窗口位置, 轮询最不容易漏。
    /// </summary>
    private void SyncOverlayBounds()
    {
        if (_overlay == null) return;
        var owner = Window.GetWindow(this);
        if (owner == null) return;

        if (owner.WindowState == WindowState.Minimized || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
        {
            if (_overlay.IsVisible) _overlay.Hide();
            return;
        }

        if (!_overlay.IsVisible) _overlay.Show();

        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = PointToScreen(new Point(0, 0));
        _overlay.Left = topLeft.X / dpi.DpiScaleX;
        _overlay.Top = topLeft.Y / dpi.DpiScaleY;
        _overlay.Width = ActualWidth;
        _overlay.Height = ActualHeight;
    }


    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
