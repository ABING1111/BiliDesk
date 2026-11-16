using System;
using System.Windows;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Services;
using Microsoft.Web.WebView2.Core;

namespace BiliDesk.Views;

/// <summary>
/// 通用 WebView2 浏览窗口: 用户主动触发的回退入口(排行榜网页版、网页搜索、正文里的链接等)。
/// 内部跳转不再自动拦截, 由用户主动关闭此窗口回主界面。
///
/// ★ 2026-11-16 按用户要求改了两处(截图: 内置浏览器里上下两条一模一样的网址):
///   ① **删掉自绘的 44px 网址栏**, 地址只留给系统标题栏显示 —— 标题跟当前页面实时更新
///      (见 <see cref="OnSourceChanged"/>), 网页区也多出 44px。别再把它加回来。
///   ② 新增"跳转本地(系统默认)浏览器": WebView2 是 HwndHost, WPF 元素叠在网页上会被空域压掉,
///      所以按钮**注入在网页 DOM 里**(右下角悬浮胶囊, 见 <see cref="OpenExternalScript"/>),
///      点击经 WebMessageReceived 回到 <see cref="OnWebMessage"/> 再交给系统浏览器。
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

    /// <summary>当前顶层文档地址(地址栏已删, 仅供标题与"在系统浏览器中打开"使用)。</summary>
    private string _currentUrl;

    /// <summary>上一次"在系统浏览器中打开"的时刻(TickCount64), 用来挡掉连点/双击。</summary>
    private long _lastExternalOpen;

    /// <summary>网页内胶囊按钮 postMessage 的固定内容(不带地址, 见 OnWebMessage 的安全说明)。</summary>
    private const string OpenExternalMessage = "bd-open-external";

    /// <summary>两次"在系统浏览器中打开"的最小间隔(ms)。</summary>
    private const long OpenExternalThrottleMs = 500;

    /// <summary>本窗口打开的目标地址(供 <see cref="LinkLauncher"/> 判断"这个链接是不是已经开着了")。</summary>
    public string InitialUrl => _initialUrl;

    /// <summary>窗口是否已经在关闭过程中(还没从 Application.Windows 里移除, 但不能再复用判断)。</summary>
    public bool IsClosedForReuse { get; private set; }

    public BrowserWindow(string url, string title)
    {
        InitializeComponent();
        _initialUrl = url;
        _currentUrl = url;
        Title = title;
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
            var core = Wv.CoreWebView2;
            core.SourceChanged += OnSourceChanged;
            core.WebMessageReceived += OnWebMessage;
            // ★ 只在本窗口注入悬浮胶囊(不放 WebView2Service.InitAsync): 登录窗/离屏控件不该出现这个按钮。
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildOpenExternalScript());
            core.Navigate(_initialUrl);
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("网页加载失败: " + ex.Message);
            Close();
        }
    }

    // ------------------------------------------------------------ 标题 / 当前地址

    /// <summary>
    /// 标题跟当前页面地址走。
    ///
    /// ★ 为什么需要它: 自绘网址栏已经删掉, 系统标题栏成了**唯一**显示地址的地方 ——
    ///   若仍沿用构造时那个初始地址, 用户点进第二层页面后标题就一直停在旧地址上。
    /// ★ 不用 e.IsNewDocument 过滤: 单页应用(hash 路由)的地址变化也要反映出来。
    /// </summary>
    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        try
        {
            var src = Wv.CoreWebView2?.Source;
            if (string.IsNullOrWhiteSpace(src)) return;
            _currentUrl = src;
            Title = src;
        }
        catch
        {
            // 关窗途中读 CoreWebView2 可能抛 ObjectDisposedException: 忽略
        }
    }

    // ------------------------------------------------------------ 在系统浏览器中打开

    /// <summary>
    /// 网页内胶囊按钮被点击(脚本里 window.chrome.webview.postMessage)。
    ///
    /// ★★ 安全: **不信任消息里的地址** —— 页面自己也 postMessage, 伪造一个地址就能让
    ///   系统浏览器打开任意站点。这里只用 WebView2 的**顶层文档地址**, 且再过一遍
    ///   <see cref="LinkLauncher.IsWebUrl"/>(只放行 http/https)。
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }   // 不是字符串消息(页面自己发的 JSON 等): 与我们无关
        if (!string.Equals(message, OpenExternalMessage, StringComparison.Ordinal)) return;

        string? url;
        try { url = Wv.CoreWebView2?.Source; }
        catch { return; }
        if (!LinkLauncher.IsWebUrl(url ?? "")) return;

        // 连点/双击只开一次浏览器窗口: 系统浏览器启动本身有几百毫秒的体感延迟,
        // 不挡的话"点了没反应再点一下"就会开出两个标签页。
        var now = Environment.TickCount64;
        if (now - _lastExternalOpen < OpenExternalThrottleMs) return;
        _lastExternalOpen = now;

        if (LinkLauncher.OpenInSystemBrowser(url))
            Svc.Toast.Show("已在默认浏览器中打开");
        else
            Svc.Toast.Show("打开默认浏览器失败");
    }

    /// <summary>
    /// 注入到每个新文档的悬浮胶囊: 右下角小按钮「在浏览器中打开」。
    ///
    /// ★ 为什么放在网页里而不是 XAML: WebView2 是 HwndHost, WPF 元素叠在它上面会被空域压掉
    ///   (与播放器的 LibVLC 同一类问题)。注入 DOM 是唯一"不占布局且任何页面都可见"的做法。
    /// ★ 兼容页面自己的样式: 全部走行内样式 + 超高 z-index, 不依赖页面 CSS;
    ///   插在 body 的直接子节点上, 页面整块重绘把它摘掉后由 2s 轮询补回来(开销可忽略)。
    /// ★ 只在 http/https 页面出现(不会在 about:blank / 错误页上挂一个点不动的按钮)。
    /// ★★ 2026-11-16 用户反馈"暗色背景下这个提示不够明显": 原来是半透明深灰底(在深色网页上就是
    ///   一块更黑的黑), 现在改成**跟随主题强调色的实底 + 自动对比的文字色 + 一圈浅色描边**。
    ///   值不写死 —— 由 <see cref="BuildOpenExternalScript"/> 从 AccentBrush / TextOnAccentBrush 注入,
    ///   用户自定义主题色时按钮也跟着变。默认不透明度提到 .92(悬停 1), 不再"隐形"。
    /// </summary>
    private const string OpenExternalScriptTemplate = """
(function () {
  if (window.__bdOpenExternalReady) return;
  window.__bdOpenExternalReady = true;

  var ID = '__bd-open-external';

  function ensure() {
    try {
      // ★ 只挂顶层文档: 这个脚本也会注入到每个子 frame(见 AddScriptToExecuteOnDocumentCreatedAsync),
      //   不挡的话每个 iframe 里都会出现一颗 fixed 按钮(相对 iframe 视口定位, 会跑到页面中间)。
      if (window.top !== window) return;
      if (!/^https?:$/i.test(location.protocol)) return;
      if (!document.body) return;
      if (document.getElementById(ID)) return;

      var btn = document.createElement('button');
      btn.id = ID;
      btn.type = 'button';
      btn.textContent = '在浏览器中打开 ↗';
      btn.title = '用系统的默认浏览器打开当前页面';
      btn.style.cssText = [
        'position:fixed', 'right:16px', 'bottom:16px', 'z-index:2147483647',
        'margin:0', 'padding:8px 15px', 'border:0', 'border-radius:999px',
        'font:600 13px/1.4 "Microsoft YaHei",system-ui,sans-serif',
        'color:__BD_ON_ACCENT__', 'background:__BD_ACCENT__',
        // 深色网页上靠这圈浅色描边与底色分开, 浅色网页上则靠投影 —— 两种情况都醒目
        'box-shadow:0 0 0 1px rgba(255,255,255,.55), 0 4px 14px rgba(0,0,0,.45)',
        'cursor:pointer', 'opacity:.92', 'transition:opacity .15s,transform .15s'
      ].join(';');
      btn.onmouseenter = function () { btn.style.opacity = '1'; btn.style.transform = 'translateY(-1px)'; };
      btn.onmouseleave = function () { btn.style.opacity = '.92'; btn.style.transform = 'none'; };
      btn.onclick = function (ev) {
        ev.preventDefault();
        ev.stopPropagation();
        try { window.chrome.webview.postMessage('bd-open-external'); } catch (e) { }
      };
      document.body.appendChild(btn);
    } catch (e) { }
  }

  if (document.readyState === 'loading')
    document.addEventListener('DOMContentLoaded', ensure, { once: true });
  else ensure();
  setInterval(ensure, 2000);
})();
""";

    /// <summary>
    /// 把模板里的两个占位符换成**当前主题**的强调色 / 强调色上的文字色。
    /// ★ 取不到资源时回落到 B 站粉 + 白字(与 Themes/Accent.xaml 的兜底一致),
    ///   绝不能让"资源还没加载"变成按钮没颜色。
    /// </summary>
    private string BuildOpenExternalScript()
    {
        var accent = (TryFindResource("AccentBrush") as SolidColorBrush)?.Color
                     ?? Color.FromRgb(0xFB, 0x72, 0x99);
        var onAccent = (TryFindResource("TextOnAccentBrush") as SolidColorBrush)?.Color
                       ?? Colors.White;
        return OpenExternalScriptTemplate
            .Replace("__BD_ACCENT__", ToCss(accent))
            .Replace("__BD_ON_ACCENT__", ToCss(onAccent));
    }

    /// <summary>#RRGGBB(忽略 alpha: CSS 那边一律用实色, 半透明正是"看不清"的根源)</summary>
    private static string ToCss(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ------------------------------------------------------------ 残留鼠标捕获

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
        try
        {
            if (Wv?.CoreWebView2 is { } core)
            {
                core.SourceChanged -= OnSourceChanged;
                core.WebMessageReceived -= OnWebMessage;
            }
        }
        catch
        {
            // CoreWebView2 可能已经释放: 退订失败无所谓
        }
        try { Wv?.Dispose(); } catch { }
    }
}
