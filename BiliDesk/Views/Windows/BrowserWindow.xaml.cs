using System;
using System.Windows;
using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk.Views;

/// <summary>
/// 通用 WebView2 浏览窗口: 用户主动触发的回退入口(排行榜网页版、网页搜索、正文里的链接等)。
/// 内部跳转不再自动拦截, 由用户主动关闭此窗口回主界面。
///
/// ★★ 2026-11-14 修"网页没加载出来时, 点窗口任意位置或关闭窗口会不断开新内置浏览器窗口":
///   根因不在本窗口, 而在**来源控件留下了全局鼠标捕获**(Hyperlink 在只读 RichTextBox 里
///   捕获鼠标后 Up 被文本框截走, 捕获永不释放) —— 那种情况下 Win32 会把之后**所有**点击
///   重定向回那个链接, 于是点哪儿都再开一个窗口。主修复见 RichTextBuilder.CreateLink /
///   ReleaseCaptureLater, LinkLauncher 里另有一道同地址去重。
///   本窗口这侧再加一道防线: 打开/关闭时清掉**残留的全局鼠标捕获**,
///   保证"即使别处又漏了捕获"也不会表现成连环弹窗。
/// </summary>
public partial class BrowserWindow : FluentWindow
{
    private readonly string _initialUrl;

    /// <summary>本窗口打开的目标地址(供 <see cref="LinkLauncher"/> 判断"这个链接是不是已经开着了")。</summary>
    public string InitialUrl => _initialUrl;

    /// <summary>窗口是否已经在关闭过程中(还没从 Application.Windows 里移除, 但不能再复用判断)。</summary>
    public bool IsClosedForReuse { get; private set; }

    public BrowserWindow(string url, string title)
    {
        InitializeComponent();
        _initialUrl = url;
        Title = title;
        UrlText.Text = url;
        Loaded += OnLoaded;
        Closed += OnClosed;
        // ★ 新窗口一出现就把残留捕获收掉: 此刻还持有捕获的控件一定来自"打开这个窗口的那次点击",
        //   它不该继续把后续点击往自己身上吸(见类注释)。
        ReleaseStrayCapture();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Svc.WebView2.InitAsync(Wv);
            Wv.CoreWebView2.Navigate(_initialUrl);
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("网页加载失败: " + ex.Message);
            Close();
        }
    }

    /// <summary>
    /// 清掉全局鼠标捕获(只清"不是本窗口控件"的那种)。
    ///
    /// ★ 为什么放在这里: 捕获是**进程级**的, 一个控件抓走之后所有点击都会被重定向给它。
    ///   用户报的连环弹窗就是它造成的; 打开新窗口时顺手收掉, 让交互立刻恢复正常。
    /// ★ 不无条件清: 若捕获者确实是本窗口里的控件(用户正按着滚动条拖), 清掉会打断操作。
    /// </summary>
    private void ReleaseStrayCapture()
    {
        try
        {
            var captured = System.Windows.Input.Mouse.Captured;
            if (captured == null) return;
            // 捕获者若在本窗口的可视树里就保留; 否则(来自别的窗口/文字流)一律释放。
            if (captured is DependencyObject d && IsInThisWindow(d)) return;
            System.Windows.Input.Mouse.Capture(null);
        }
        catch
        {
            // 释放失败不影响功能
        }
    }

    private bool IsInThisWindow(DependencyObject d)
    {
        try
        {
            return d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                && ReferenceEquals(Window.GetWindow(d), this);
        }
        catch
        {
            return false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // ★ 先置标记再收捕获: 关窗途中 LinkLauncher 可能还在遍历窗口列表,
        //   不置的话它会把这个"已经关掉的窗口"当成"这个地址还开着"。
        IsClosedForReuse = true;
        // ★ 关窗时也收一次: 网页没加载出来时用户点关闭, 那一下同样可能是被重定向过来的。
        ReleaseStrayCapture();
        try { Wv?.Dispose(); } catch { }
    }
}
