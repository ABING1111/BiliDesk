using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Services;
using Microsoft.Web.WebView2.Core;

namespace BiliDesk.Views;

/// <summary>
/// 登录窗口: 两种方式, 顶部可切换。
///
///   扫码登录 —— 二维码**自己画**(passport 接口拿 url → 本地编码 → 矢量绘制)。单独一张卡片,
///              不嵌官方网页, 所以窗口打开快、状态(未扫/已扫码待确认/已过期)由我们自己说清楚。
///   密码登录 —— 自己的账号密码表单。提交后把账密**替用户敲进官方登录页并提交**,
///              人机验证(极验)在官方页面里完成 —— 这是被接口逼出来的:
///              `POST /x/passport-login/web/login` 无条件要求极验(实测: 带/不带 buvid、
///              带 captcha 接口换来的 token, 三种组合都只回 `-105 验证码错误`), 而极验必须由
///              官方页面的 JS 在浏览器里跑。所以这里不做半吊子的"自行 RSA 加密直连"。
///
/// 两条路的成功判定是同一个: 拿到 SESSDATA。
///   - 扫码: 轮询 passport 接口, 成功时凭据在 Set-Cookie / 跳转 url 里;
///   - 密码: 轮询 WebView2 档案里的 Cookie(官方页面登录成功后自己写进去)。
/// </summary>
public partial class LoginWindow : FluentWindow
{
    /// <summary>二维码的"还算新鲜"阈值: 回到扫码页时比这更旧的码就重新要一张</summary>
    private static readonly TimeSpan QrFreshFor = TimeSpan.FromSeconds(90);

    /// <summary>登录窗常规尺寸(只放自己的表单/二维码)</summary>
    private const double NarrowWidth = 400, NarrowHeight = 600;

    /// <summary>验证那一步的窗口尺寸: 人机验证的图和字都要看得清, 窗口太小就只能挤成一团</summary>
    private const double WideWidth = 980, WideHeight = 920;

    /// <summary>官方页面的可选缩放档位(点「缩放」按钮循环)。130% 起步 —— 官方页原始尺寸对验证码来说偏小</summary>
    private static readonly double[] ZoomSteps = { 1.0, 1.3, 1.6, 1.9 };

    private readonly DispatcherTimer _qrTimer;      // 2 秒轮询二维码状态
    private readonly DispatcherTimer _webTimer;     // 2 秒轮询官方页面 Cookie
    private readonly DispatcherTimer _pageTimer;    // 1 秒看官方页面: 填表结果 / 验证码是否出现

    private bool _started;
    private bool _qrBusy;         // 正在申请二维码(防连点)
    private bool _qrPolling;      // 防轮询重入
    private bool _webPolling;
    private bool _pagePolling;
    private bool _webStarted;     // 官方页面已初始化并导航过
    private bool _closing;        // 窗口正在关, 异步回调别再碰 UI

    private string? _qrKey;
    private DateTime _qrFetchedAt;

    private double _webZoom = 1.3;   // 官方页面的当前缩放
    private bool _captchaSeen;       // 本轮已经提示过"去完成验证"
    private bool _captchaFit;        // 本轮已经为验证码调过缩放(只调一次, 免得来回跳)
    private bool _fillReported;      // 填表结果只上报一次(否则每秒都会把验证码的引导文字顶掉)

    public LoginWindow()
    {
        InitializeComponent();

        _qrTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _qrTimer.Tick += async (_, _) => await PollQrAsync();
        _webTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _webTimer.Tick += async (_, _) => await PollWebCookiesAsync();
        _pageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pageTimer.Tick += async (_, _) => await PollPageStateAsync();

        // 事件在 InitializeComponent 之后挂: XAML 里 QrModeTab 的 IsChecked="True" 在建树时已经赋过值,
        // 挂晚了正好不会为"初始状态"再跑一遍切换逻辑(初始可见性就是 XAML 里的默认值, 与切换结果一致)。
        QrModeTab.Checked += (_, _) => SwitchToQrMode();
        PwdModeTab.Checked += (_, _) => SwitchToPasswordMode();

        // 输入框里的提示文字(水印): 只要还是空的就显示(聚焦时不藏 —— 藏了用户就看不出这栏要填什么;
        // 水印在 XAML 里比输入区右移了 3px, 留出光标的位置)
        AccountBox.TextChanged += (_, _) => UpdateWatermarks();
        PwdBox.PasswordChanged += (_, _) => UpdateWatermarks();
        PwdPlainBox.TextChanged += (_, _) => UpdateWatermarks();

        Loaded += OnLoaded;
        Closed += OnClosed;
        ThemeService.Instance.ThemeChanged += OnThemeChanged;
    }

    // ---------------------------------------------------------------- 账号/密码输入行

    /// <summary>密码当前在哪一行: 明文框可见时以它为准(显示密码状态下用户可能改过内容)</summary>
    private bool PasswordShown => PwdPlainBox.Visibility == Visibility.Visible;

    private string CurrentPassword => PasswordShown ? PwdPlainBox.Text : PwdBox.Password;

    /// <summary>只要输入框还是空的就显示提示文字, 填了内容就藏起来</summary>
    private void UpdateWatermarks()
    {
        AccountHint.Visibility = AccountBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PwdHint.Visibility = CurrentPassword.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>显示 / 隐藏密码: 两个同格的输入框互斥切换, 内容互相同步(焦点跟着走, 可以接着打字)</summary>
    private void OnTogglePasswordVisible(object sender, RoutedEventArgs e)
    {
        if (!PasswordShown)
        {
            // 切到明文
            PwdPlainBox.Text = PwdBox.Password;
            PwdBox.Visibility = Visibility.Collapsed;
            PwdPlainBox.Visibility = Visibility.Visible;
            PwdPlainBox.CaretIndex = PwdPlainBox.Text.Length;
            PwdPlainBox.Focus();
            PwdEyeButton.Content = "\uED1A";     // 划线眼 = "再点就藏起来"
            PwdEyeButton.ToolTip = "隐藏密码";
        }
        else
        {
            PwdBox.Password = PwdPlainBox.Text;
            PwdPlainBox.Visibility = Visibility.Collapsed;
            PwdBox.Visibility = Visibility.Visible;
            PwdBox.Focus();
            PwdEyeButton.Content = "\uE7B3";     // 眼睛 = "点一下看明文"
            PwdEyeButton.ToolTip = "显示密码";
        }
        UpdateWatermarks();
    }

    // ---------------------------------------------------------------- 生命周期

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;

        // 先把设备指纹备好: 登录请求带上 buvid3/buvid4 才是"这台设备在登录"(官方页面此时也是这么发的)。
        // 不 await —— 拿不到指纹不该把二维码卡住, 这两个接口不带指纹也能用。
        _ = Svc.Api.EnsureBuvidAsync();

        await RequestQrAsync();
    }

    private void OnThemeChanged()
    {
        if (Wv.CoreWebView2 != null) Svc.WebView2.ApplyColorScheme(Wv);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        ThemeService.Instance.ThemeChanged -= OnThemeChanged;
        _qrTimer.Stop();
        _webTimer.Stop();
        _pageTimer.Stop();
        try { Wv.Dispose(); } catch { /* 忽略 */ }
    }

    // ---------------------------------------------------------------- 窗口尺寸

    /// <summary>
    /// 表单/二维码用常规尺寸; 官方页面(验证那一步)放大到接近整屏 —— 验证码的图和字都要看得清。
    /// 尺寸按主人窗口所在显示器的工作区夹取, 并重新居中到主人窗口上。
    /// </summary>
    private void ApplyWebLayout(bool wide)
    {
        var area = OwnerWorkArea();
        if (wide)
        {
            Width = Math.Min(WideWidth, area.Width - 40);
            Height = Math.Min(WideHeight, area.Height - 40);
        }
        else
        {
            Width = NarrowWidth;
            Height = NarrowHeight;
        }
        CenterOverOwner(area);
    }

    private void CenterOverOwner((double Left, double Top, double Width, double Height) area)
    {
        var owner = Owner;
        double cx = owner != null ? owner.Left + owner.ActualWidth / 2 : area.Left + area.Width / 2;
        double cy = owner != null ? owner.Top + owner.ActualHeight / 2 : area.Top + area.Height / 2;

        // 夹进工作区: 窗口比主人窗口还高时(验证那一步), 纯居中会让标题栏跑到屏幕外
        double left = cx - Width / 2;
        double top = cy - Height / 2;
        Left = Math.Max(area.Left, Math.Min(left, area.Left + area.Width - Width));
        Top = Math.Max(area.Top, Math.Min(top, area.Top + area.Height - Height));
    }

    /// <summary>主人窗口所在显示器的工作区(单位换成 DIP: Screen 给的是物理像素)</summary>
    private (double Left, double Top, double Width, double Height) OwnerWorkArea()
    {
        try
        {
            var owner = Owner;
            if (owner != null)
            {
                var handle = new WindowInteropHelper(owner).Handle;
                if (handle != IntPtr.Zero)
                {
                    var wa = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                    var dpi = VisualTreeHelper.GetDpi(owner);
                    return (wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY,
                            wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
                }
            }
        }
        catch
        {
            // 拿不到就用主显示器兜底
        }
        var fallback = SystemParameters.WorkArea;
        return (fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    // ---------------------------------------------------------------- 面板切换

    private void ShowPanel(bool qr, bool pwd, bool web)
    {
        QrPanel.Visibility = qr ? Visibility.Visible : Visibility.Collapsed;
        PwdPanel.Visibility = pwd ? Visibility.Visible : Visibility.Collapsed;
        WebPanel.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        if (!web) ApplyWebLayout(false);   // 离开官方页面就收回到常规尺寸
    }

    private void SwitchToQrMode()
    {
        // 离开官方页面就停掉它的 Cookie 轮询: 否则用户在扫码页看着, 窗口会突然因为
        // "另一个页面里登录成功了"而自己关掉。
        _webTimer.Stop();
        _pageTimer.Stop();
        ShowPanel(qr: true, pwd: false, web: false);
        SubtitleText.Text = "打开哔哩哔哩手机客户端扫码登录";

        // 手里那张码还新鲜就接着用(来回切页签不该每次都向服务端要新的); 过期或太旧才重新申请。
        if (_qrKey == null || DateTime.Now - _qrFetchedAt > QrFreshFor
            || QrOverlay.Visibility == Visibility.Visible)
            _ = RequestQrAsync();
        else
            _qrTimer.Start();
    }

    private void SwitchToPasswordMode()
    {
        _qrTimer.Stop();
        _pageTimer.Stop();
        ShowPanel(qr: false, pwd: true, web: false);
        SubtitleText.Text = "使用账号密码登录";
        PwdErrorText.Visibility = Visibility.Collapsed;
        UpdateWatermarks();
        // 焦点给"下一个该填的": 账号还没填就落在账号上, 已经填过就落在密码上(从官方页面返回时就是这样)
        if (AccountBox.Text.Trim().Length == 0) AccountBox.Focus();
        else (PasswordShown ? (FrameworkElement)PwdPlainBox : PwdBox).Focus();
    }

    private void OnBackFromWeb(object sender, RoutedEventArgs e)
    {
        // 直接调切换、不去动页签的选中态: 从密码表单进到官方页面时, "密码登录"页签**一直是选中的**,
        // 再给它 IsChecked = true 不构成变化, Checked 事件不会触发 —— 表现就是"返回按钮点了没反应"。
        // (官方页面留着不销毁: WebView2 关掉重建要几百毫秒。)
        SwitchToPasswordMode();
    }

    // ---------------------------------------------------------------- 扫码登录

    /// <summary>申请一张新二维码并开始轮询</summary>
    private async Task RequestQrAsync()
    {
        if (_qrBusy || _closing) return;
        _qrBusy = true;
        _qrTimer.Stop();
        ShowQrOverlay("\uE895", "正在生成二维码…", showButton: false);

        try
        {
            var (ok, err, url, key) = await Svc.Passport.CreateQrAsync();
            if (_closing) return;

            if (!ok || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            {
                ShowQrOverlay("\uE783", err ?? "二维码获取失败", showButton: true);
                return;
            }

            _qrKey = key;
            _qrFetchedAt = DateTime.Now;
            QrImage.Source = BuildQrImage(QrCode.Encode(url));
            HideQrOverlay();
            QrStatusText.Text = "打开「哔哩哔哩」手机客户端, 用扫一扫扫描二维码";
            _qrTimer.Start();
        }
        catch (Exception ex)
        {
            if (!_closing) ShowQrOverlay("\uE783", "生成二维码失败: " + ex.Message, showButton: true);
        }
        finally
        {
            _qrBusy = false;
        }
    }

    private async void OnQrRefresh(object sender, RoutedEventArgs e) => await RequestQrAsync();

    private async Task PollQrAsync()
    {
        if (_qrPolling || _closing || _qrKey == null) return;
        _qrPolling = true;
        try
        {
            var (state, _, cookies) = await Svc.Passport.PollQrAsync(_qrKey);
            if (_closing) return;

            switch (state)
            {
                case QrLoginState.Scanned:
                    QrStatusText.Text = "扫描成功, 请在手机上点击「确认登录」";
                    break;
                case QrLoginState.Expired:
                    _qrTimer.Stop();
                    ShowQrOverlay("\uE72C", "二维码已过期, 刷新后重新扫码", showButton: true);
                    break;
                case QrLoginState.Success:
                    // ★ 用户已经切走了就不再理会这一轮结果: 请求是切走之前发出去的,
                    //   当场关窗会变成"正在输密码, 窗口自己没了"。
                    if (cookies != null && QrPanel.Visibility == Visibility.Visible)
                        FinishLogin(cookies, syncToWebProfile: true);
                    break;
                default:
                    // 单轮失败不改界面: 网络抖一下不该把已经掏出来的二维码判死, 下一轮接着问。
                    // (码本身失效时服务端会回 86038, 那才是真的该提示刷新。)
                    break;
            }
        }
        catch
        {
            // 定时器回调里抛出去会惊动全局异常处理(写日志 + 弹框): 这里吞掉, 下一轮继续。
        }
        finally
        {
            _qrPolling = false;
        }
    }

    /// <summary>
    /// 把模块矩阵画成矢量图 —— 用 DrawingImage 而不是位图: 全局"软件缩放"会给窗口内容整体加
    /// LayoutTransform, 位图一放大就糊(二维码糊了手机就扫不出来), 矢量图重画一遍依然锐利。
    /// </summary>
    private static ImageSource BuildQrImage(bool[,] matrix)
    {
        const int Quiet = 4;      // 静默区(标准要求 4 个模块, 少了扫不出来)
        const int Target = 232;   // 卡片内可用边长(240 见方减边框与一点余量) —— 尽量占满, 模块才够大

        int n = matrix.GetLength(0);
        int total = n + Quiet * 2;
        int module = Math.Max(2, Target / total);   // ★ 取整: 非整数模块宽会在边界留下抗锯齿灰缝
        double m = module;

        // 逐行把连续的深色模块并成一整条(而不是每格一个方块): 相邻矩形的接缝能少一大半。
        var dark = new GeometryGroup();
        for (int r = 0; r < n; r++)
        {
            int c = 0;
            while (c < n)
            {
                if (!matrix[r, c]) { c++; continue; }
                int from = c;
                while (c < n && matrix[r, c]) c++;
                dark.Children.Add(new RectangleGeometry(
                    new Rect((from + Quiet) * m, (r + Quiet) * m, (c - from) * m, m)));
            }
        }

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null,
            new RectangleGeometry(new Rect(0, 0, total * m, total * m))));
        group.Children.Add(new GeometryDrawing(Brushes.Black, null, dark));
        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private void ShowQrOverlay(string icon, string text, bool showButton)
    {
        QrOverlayIcon.Text = icon;
        QrOverlayText.Text = text;
        QrOverlayButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
        QrOverlay.Visibility = Visibility.Visible;
    }

    private void HideQrOverlay() => QrOverlay.Visibility = Visibility.Collapsed;

    // ---------------------------------------------------------------- 密码登录

    private async void OnPasswordLogin(object sender, RoutedEventArgs e) => await StartPasswordLoginAsync();

    private async void OnPasswordFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;      // 别让回车冒泡出去触发别的默认按钮
        await StartPasswordLoginAsync();
    }

    private async Task StartPasswordLoginAsync()
    {
        if (_closing) return;

        var user = AccountBox.Text.Trim();
        var pass = CurrentPassword;
        if (user.Length == 0 || pass.Length == 0)
        {
            ShowPwdError("请先填写账号和密码");
            return;
        }
        ShowPwdError(null);
        await OpenOfficialLoginAsync(user, pass);
    }

    private void ShowPwdError(string? text)
    {
        PwdErrorText.Text = text ?? "";
        PwdErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>打开官方登录页, 把账密填进去并提交(人机验证由用户在页面里完成)</summary>
    private async Task OpenOfficialLoginAsync(string user, string pass)
    {
        ApplyWebLayout(true);          // 先放大窗口再显示, 免得看到一次缩放跳变
        ShowPanel(qr: false, pwd: false, web: true);
        SubtitleText.Text = "在官方页面完成人机验证";
        WebHintText.Text = "正在打开官方登录页…";
        PwdLoginButton.IsEnabled = false;
        _captchaSeen = false;
        _captchaFit = false;
        _fillReported = false;

        try
        {
            await EnsureWebViewAsync();
            if (_closing) return;

            await FillOfficialFormAsync(user, pass);
            if (_closing) return;

            _webTimer.Start();
            _pageTimer.Start();   // 等下用它确认"填进去了没有 + 验证码出现了没有"
        }
        catch (Exception ex)
        {
            if (!_closing) WebHintText.Text = "官方登录页加载失败: " + ex.Message;
        }
        finally
        {
            PwdLoginButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- 官方页面的缩放与人机验证

    /// <summary>把缩放应用到页面并同步按钮上的字数</summary>
    private void ApplyWebZoom()
    {
        if (Wv.CoreWebView2 != null)
        {
            try { Wv.ZoomFactor = _webZoom; } catch { /* 控件还没就绪, 忽略 */ }
        }
        WebZoomButton.Content = $"缩放 {_webZoom * 100:0}%";
    }

    /// <summary>点「缩放」: 在几个档位之间循环, 页面太小看不清时用户可以自己继续放大</summary>
    private void OnCycleWebZoom(object sender, RoutedEventArgs e)
    {
        int i = Array.IndexOf(ZoomSteps, _webZoom);
        _webZoom = ZoomSteps[(i + 1) % ZoomSteps.Length];
        _captchaFit = true;      // 用户自己定过的缩放, 别再被"自动适配"改掉
        ApplyWebZoom();
    }

    /// <summary>
    /// 看官方页面现在的样子: 填表成功没有 / 验证码浮层出现没有。
    ///
    /// ★ 为什么要认得验证码: 验证码(极验的滑块 / 图片码)是官方页面里弹出来的浮层,
    ///   用户说"看不清"的正是它 —— 认出来之后就能(1)把话说清楚让他知道该干什么,
    ///   (2)必要时把缩放降回来, 保证整个验证码都在视野里, 而不是被窗口切掉一半。
    /// </summary>
    private async Task PollPageStateAsync()
    {
        if (_pagePolling || _closing || Wv.CoreWebView2 == null
            || WebPanel.Visibility != Visibility.Visible) return;
        _pagePolling = true;
        try
        {
            var raw = await Wv.CoreWebView2.ExecuteScriptAsync(PageStateScript);
            if (_closing) return;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // 1) 填表结果如实说: 选择器一旦失效, 这里会明说"请手动填写", 而不是假报"已填好"。
            //    ★ 只报一次: 这段每秒都会跑, 每次都写就会把"请完成人机验证"那句反复顶掉。
            if (!_fillReported
                && root.TryGetProperty("fill", out var fill) && fill.ValueKind == JsonValueKind.String)
            {
                var state = fill.GetString();
                if (state == "ok")
                {
                    _fillReported = true;
                    WebHintText.Text = "账号密码已替你填好; 请完成下方的人机验证";
                }
                else if (state == "failed")
                {
                    _fillReported = true;
                    WebHintText.Text = "没找到官方页面的输入框, 请在下方手动输入账号密码并登录";
                }
            }

            // 2) 验证码浮层出现 -> 提示 + 保证它完整在视野里
            bool captcha = root.TryGetProperty("captcha", out var cap) && cap.ValueKind == JsonValueKind.True;
            if (captcha)
            {
                if (!_captchaSeen)
                {
                    _captchaSeen = true;
                    WebHintText.Text = "请完成人机验证: 按页面提示拖动滑块 / 点击指定内容, 通过后自动登录";
                }

                // 浮层比视野还高 = 会被切掉一半(那才是真的"看不清"): 把缩放降到装得下为止。
                // 只自动调一次; 用户手动缩放过的(_captchaFit 已置位)不再动。
                double h = root.TryGetProperty("h", out var hh) && hh.TryGetDouble(out var hv) ? hv : 0;
                double vh = root.TryGetProperty("vh", out var vv) && vv.TryGetDouble(out var vhv) ? vhv : 0;
                if (!_captchaFit && h > 0 && vh > 0 && h > vh - 8 && _webZoom > 1.0)
                {
                    _captchaFit = true;
                    _webZoom = Math.Max(1.0, _webZoom * (vh - 16) / h);
                    ApplyWebZoom();
                }
            }
            else if (_captchaSeen)
            {
                // 验证码收起来了(过了 / 被关掉): 提示回到"填表"那一档
                _captchaSeen = false;
                _captchaFit = false;
                WebHintText.Text = "账号密码已替你填好; 请完成下方的人机验证";
            }
        }
        catch
        {
            // 页面还在导航时脚本会失败, 忽略, 下一秒再看
        }
        finally
        {
            _pagePolling = false;
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webStarted) return;
        _webStarted = true;
        try
        {
            await Svc.WebView2.InitAsync(Wv);

            // 档案里可能还留着上一份登录态(退出登录清 Cookie 失败、或用户手删了 session.json):
            // 不清掉的话下面的 Cookie 轮询会立刻读到旧 SESSDATA, 变成"还没验证就登录成功"。
            // 用隐藏的那个 WebView2 去清 —— 拿当前这个正在显示的控件去清, 助手收尾时会把它的
            // Visibility 置回 Collapsed, 页面就没了。
            if (!Svc.Session.HasLogin && SvcWindow.HiddenWebView is { } hidden)
                await Svc.WebView2.ClearProfileCookiesAsync(hidden);

            ApplyWebZoom();   // 官方页原始尺寸偏小: 默认 130% 起, 验证码才看得清

            var navigated = new TaskCompletionSource();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult();

            Wv.CoreWebView2.NavigationCompleted += OnNav;
            try
            {
                Wv.CoreWebView2.Navigate("https://passport.bilibili.com/login");
                // 首次导航完成即可(官方页面是 SPA, 之后的内部跳转不会再触发这个事件);
                // 超时也照样往下走 —— 表单可能已经能用了。
                await Task.WhenAny(navigated.Task, Task.Delay(TimeSpan.FromSeconds(8)));
            }
            finally
            {
                Wv.CoreWebView2.NavigationCompleted -= OnNav;
            }
        }
        catch
        {
            _webStarted = false;
            throw;
        }
    }

    /// <summary>
    /// 把账密替用户敲进官方表单并点登录。
    ///
    /// ★ 直接 `el.value = x` 对 Vue 的 v-model 是无效的(框架状态不更新, 一提交就是空值),
    ///   必须"用原生 value setter 赋值 + 派发 input 事件"。
    /// ★ 表单是切到密码页签才渲染的, 所以脚本里带重试; 账密用 JSON 序列化塞进去,
    ///   引号/反斜杠交给序列化器转义, 不手工拼字符串。
    /// </summary>
    private async Task FillOfficialFormAsync(string user, string pass)
    {
        if (Wv.CoreWebView2 == null) return;
        var payload = JsonSerializer.Serialize(new { u = user, p = pass });
        await Wv.CoreWebView2.ExecuteScriptAsync(FillFormScript.Replace("__PAYLOAD__", payload));
    }

    /// <summary>轮询官方页面的 Cookie, 出现 SESSDATA 即登录成功(与旧的登录窗口同一套判定)</summary>
    private async Task PollWebCookiesAsync()
    {
        if (_webPolling || _closing || Wv.CoreWebView2 == null) return;
        _webPolling = true;
        try
        {
            var cookies = await WebView2Service.ReadBiliCookiesAsync(Wv);
            if (_closing) return;
            if (cookies.TryGetValue("SESSDATA", out var sess) && !string.IsNullOrWhiteSpace(sess))
                FinishLogin(cookies, syncToWebProfile: false);
        }
        catch
        {
            // 轮询失败忽略, 下一轮继续
        }
        finally
        {
            _webPolling = false;
        }
    }

    // ---------------------------------------------------------------- 收尾

    /// <summary>
    /// 登录成功收尾: 保存会话并关窗。
    /// </summary>
    /// <param name="syncToWebProfile">
    /// 扫码那条路要额外把凭据写进浏览器 Profile —— 它只落在 session.json 里, 而内嵌网页读的是 Profile;
    /// 密码那条路本来就是官方页面写进去的, 不用再写一遍。
    /// </param>
    private void FinishLogin(IReadOnlyDictionary<string, string> cookies, bool syncToWebProfile)
    {
        if (!cookies.TryGetValue("SESSDATA", out var sess) || string.IsNullOrWhiteSpace(sess))
        {
            // 判定成功却没带回凭据: 宁可让用户重来, 也不能写一份没有凭据的"登录态"。
            // (Cookie 轮询那条进来前已经确认过 SESSDATA, 只有扫码这条路需要兜这个底。)
            if (!syncToWebProfile) return;
            ShowQrOverlay("\uE783", "登录没有拿到凭据, 刷新二维码后重试", showButton: true);
            return;
        }

        _qrTimer.Stop();
        _webTimer.Stop();
        _pageTimer.Stop();
        Svc.Session.UpdateFromCookies(cookies);
        Svc.Toast.Show("登录成功");

        if (syncToWebProfile && SvcWindow.HiddenWebView is { } hidden)
        {
            // 不 await: 要起一个 WebView2, 几百毫秒; 失败也只是内嵌网页需要重新登录, 不该拖住关窗。
            var snapshot = new Dictionary<string, string>(cookies);
            _ = Svc.WebView2.SyncSessionCookiesAsync(hidden, snapshot);
        }

        DialogResult = true;
        Close();
    }

    /// <summary>注入官方页面的填表脚本(原理见 FillOfficialFormAsync 的说明)</summary>
    private const string FillFormScript = @"
window.__bdFill = 'pending';
(function (v) {
  function setValue(el, val) {
    var desc = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value');
    if (desc && desc.set) desc.set.call(el, val); else el.value = val;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }
  function visible(el) { return !!el && el.offsetParent !== null; }
  function pickButton() {
    var all = document.querySelectorAll('.btn_primary');
    for (var i = 0; i < all.length; i++) if (visible(all[i])) return all[i];
    return null;
  }
  var tries = 0;
  function attempt() {
    tries++;
    // 先切到「密码登录」页签(官方页面默认停在扫码页)
    var tabs = document.querySelectorAll('.tabs_wp > div');
    for (var i = 0; i < tabs.length; i++) {
      if ((tabs[i].textContent || '').trim() === '密码登录' && !tabs[i].classList.contains('tab_active')) {
        tabs[i].click();
      }
    }
    var box = document.querySelector('.login-pwd');
    if (!visible(box)) { retry(); return; }
    var user = box.querySelector('input[type=""text""]');
    var pwd = box.querySelector('input[type=""password""]');
    if (!visible(user) || !visible(pwd)) { retry(); return; }

    setValue(user, v.u);
    setValue(pwd, v.p);

    // 「登录」按钮在账号密码都非空之前带 disabled 类(点了不提交), 所以要等 Vue 刷完状态再点。
    var btn = pickButton();
    if (btn && !btn.classList.contains('disabled')) {
      // 如实回报给窗口: 填表到底成没成, 别让界面报一句可能不成立的话
      window.__bdFill = 'ok';
      btn.click();
      return;
    }
    retry();
  }
  function retry() {
    if (tries < 24) setTimeout(attempt, 250);
    else window.__bdFill = 'failed';
  }
  attempt();
})(__PAYLOAD__);
";

    /// <summary>
    /// 读官方页面当前状态: 填表结果 + 验证码浮层在不在 / 多大。
    /// 返回 {fill, captcha, h, vh}: h = 浮层高度(CSS px), vh = 视口高度(CSS px)。
    /// </summary>
    private const string PageStateScript = @"
(function () {
  var out = { fill: window.__bdFill || null, captcha: false, h: 0, vh: window.innerHeight || 0 };
  var vw = window.innerWidth || 0;
  var list = document.querySelectorAll('[class*=""geetest""], [class*=""captcha""], [class*=""verify""]');
  var best = null;
  for (var i = 0; i < list.length; i++) {
    var r = list[i].getBoundingClientRect();
    if (r.width < 120 || r.height < 100) continue;                    // 太小 = 不是浮层本体
    if (r.width > vw * 0.96 && r.height > out.vh * 0.96) continue;    // 铺满整屏 = 蒙层, 不是内容
    if (!best || r.width * r.height > best.w * best.h) best = { w: r.width, h: r.height };
  }
  if (best) { out.captcha = true; out.h = best.h; }
  return out;
})()
";
}
