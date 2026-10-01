using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk.Views;

public partial class LoginWindow : FluentWindow
{
    private DispatcherTimer? _pollTimer;
    private bool _polling;
    private bool _started;

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        ThemeService.Instance.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        if (Wv.CoreWebView2 != null) Svc.WebView2.ApplyColorScheme(Wv);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await Svc.WebView2.InitAsync(Wv);
            Wv.CoreWebView2.NavigationStarting += (_, _) => ShowError(false);
            Wv.CoreWebView2.Navigate("https://passport.bilibili.com/login");
            // 先停掉可能在跑的旧计时器: 「重试」会再走一遍这个方法, 不复用也不停的话,
            // 上一次那个会一直 tick 下去 —— 每重试一次就多留一个轮询在后台翻 Cookie。
            _pollTimer?.Stop();
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += async (_, _) => await PollOnceAsync();
            _pollTimer.Start();
            ShowError(false);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "登录页面加载失败: " + ex.Message;
            ShowError(true);
        }
    }

    private void ShowError(bool show) => ErrorPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        ShowError(false);
        await StartAsync();
    }

    /// <summary>每 2 秒检查一次 Cookie, 出现 SESSDATA 即视为登录成功</summary>
    private async Task PollOnceAsync()
    {
        if (_polling || !IsLoaded) return;
        _polling = true;
        try
        {
            var cookies = await WebView2Service.ReadBiliCookiesAsync(Wv);
            if (!cookies.TryGetValue("SESSDATA", out var sess) || string.IsNullOrWhiteSpace(sess))
                return;

            // 登录成功: 保存会话并关闭
            SessionManager.Instance.UpdateFromCookies(cookies);
            Svc.Toast.Show("登录成功");
            DialogResult = true;
            Close();
        }
        catch
        {
            // 轮询失败忽略, 下一轮继续
        }
        finally
        {
            _polling = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeService.Instance.ThemeChanged -= OnThemeChanged;
        _pollTimer?.Stop();
        _pollTimer = null;
        try { Wv.Dispose(); } catch { /* 忽略 */ }
    }
}