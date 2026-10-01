using System;
using System.Windows;
using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk.Views;

/// <summary>通用 WebView2 浏览窗口: 用户主动触发的回退入口(排行榜网页版、网页搜索等)
/// 内部跳转不再自动拦截, 由用户主动关闭此窗口回主界面</summary>
public partial class BrowserWindow : FluentWindow
{
    private readonly string _initialUrl;

    public BrowserWindow(string url, string title)
    {
        InitializeComponent();
        _initialUrl = url;
        Title = title;
        UrlText.Text = url;
        Loaded += OnLoaded;
        Closed += OnClosed;
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

    private void OnClosed(object? sender, EventArgs e)
    {
        try { Wv?.Dispose(); } catch { }
    }
}