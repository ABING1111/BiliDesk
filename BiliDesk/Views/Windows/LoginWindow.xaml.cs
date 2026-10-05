using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using BiliDesk.Helpers;
using BiliDesk.Services;
using QRCoder;

namespace BiliDesk.Views;

/// <summary>
/// 扫码登录窗口: passport 二维码接口 + 原生渲染(QRCoder), 轮询状态驱动界面。
/// 以前是 WebView2 加载整页 passport 登录、从浏览器里翻 Cookie —— 页面自带滚动条、
/// 还要拉起整个浏览器内核, 只为了一个二维码; 现在窗口里只有二维码本身。
/// </summary>
public partial class LoginWindow : FluentWindow
{
    /// <summary>轮询间隔, 与官方网页端一致</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly QrLoginService _qr = new();
    private CancellationTokenSource? _cts;

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = RestartAsync();
        // 关窗即终止整条 generate/轮询链路: 不取消的话循环还在后台跑, 直到窗口 GC
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _qr.Dispose();
        };
    }

    /// <summary>(重新)获取二维码并进入轮询。「重试」按钮也走这里。</summary>
    private async Task RestartAsync()
    {
        // 先停旧链路再开新的: 和旧版 WebView 轮询计时器同一个坑 ——
        // 不停的话每点一次重试就多留一条循环在后台跑
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            ShowMask(null);
            QrImage.Source = null;
            QrImage.Visibility = Visibility.Collapsed;
            Loading.Visibility = Visibility.Visible;
            StatusText.Text = "正在获取二维码…";

            var code = await _qr.GenerateAsync(ct);
            if (code == null)
            {
                ShowMask("二维码获取失败");
                return;
            }

            QrImage.Source = RenderQr(code.Url);
            Loading.Visibility = Visibility.Collapsed;
            QrImage.Visibility = Visibility.Visible;
            StatusText.Text = "打开手机 App 扫一扫登录";

            await PollLoopAsync(code.Key, ct);
        }
        catch (OperationCanceledException) { /* 关窗/重试打断, 正常 */ }
        catch (Exception ex)
        {
            ShowMask("出错了: " + ex.Message);
        }
    }

    private async Task PollLoopAsync(string key, CancellationToken ct)
    {
        var consecutiveErrors = 0;
        while (true)
        {
            await Task.Delay(PollInterval, ct);
            var result = await _qr.PollAsync(key, ct);
            switch (result.Status)
            {
                case QrLoginService.PollStatus.Waiting:
                    consecutiveErrors = 0;
                    StatusText.Text = "打开手机 App 扫一扫登录";
                    break;

                case QrLoginService.PollStatus.Scanned:
                    consecutiveErrors = 0;
                    StatusText.Text = "已扫码, 请在手机上确认";
                    break;

                case QrLoginService.PollStatus.Expired:
                    ShowMask("二维码已失效");
                    return;

                case QrLoginService.PollStatus.Success:
                    SessionManager.Instance.UpdateFromCookies(result.Cookies!);
                    Svc.Toast.Show("登录成功");
                    DialogResult = true;
                    Close();
                    return;

                case QrLoginService.PollStatus.Error:
                    // 单次网络抖动不终结流程, 下一轮接着试; 连着失败才认输
                    consecutiveErrors++;
                    if (consecutiveErrors >= 3)
                    {
                        ShowMask("网络异常, 轮询已停止");
                        return;
                    }
                    break;
            }
        }
    }

    private void ShowMask(string? text)
    {
        if (text == null)
        {
            QrMask.Visibility = Visibility.Collapsed;
            return;
        }
        MaskText.Text = text;
        MaskButton.Content = text.Contains("失效") ? "重新获取" : "重试";
        Loading.Visibility = Visibility.Collapsed;
        QrMask.Visibility = Visibility.Visible;
    }

    private async void OnRetry(object sender, RoutedEventArgs e) => await RestartAsync();

    /// <summary>二维码内容 → PNG → BitmapImage。白底黑码由 PngByteQRCode 默认生成,
    /// 放在白色卡片上, 扫码对比度与主题无关。</summary>
    private static BitmapSource RenderQr(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        var png = qr.GetGraphic(4);   // 每模块 4px: 最长的码(约 v7)也在 208px 白卡内放得下
        var image = new BitmapImage();
        using var ms = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
