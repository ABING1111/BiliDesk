using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliDesk.Mpv;

namespace BiliDesk.MpvProbe.Ui;

/// <summary>
/// mpv 嵌入 PoC 主窗口(2026-11-22): 原生视频区 + 独立覆盖层窗口。
/// 只用来验证"能播 + 能盖住 + 能操作", 不是正式播放器界面。
/// </summary>
public sealed class MpvTestWindow : Window
{
    private readonly MpvVideoHost _host = new();
    private readonly MpvMediaRequest _request;
    private MpvPlayer? _player;
    private MpvOverlayWindow? _overlay;

    public MpvTestWindow(MpvMediaRequest request)
    {
        _request = request;
        Title = "BiliDesk mpv core PoC";
        Width = 1000;
        Height = 620;
        Background = Brushes.Black;
        Content = new Grid { Children = { _host } };
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_host.Hwnd == IntPtr.Zero)
        {
            MessageBox.Show("视频宿主窗口没建出来");
            return;
        }

        _player = new MpvPlayer(
            new MpvGlobalOptions { WindowId = _host.Hwnd.ToInt64() },
            Console.WriteLine);

        var rc = _player.Load(_request);
        if (rc < 0) Title += "  (load failed: " + MpvPlayer.ErrorText(rc) + ")";

        _overlay = new MpvOverlayWindow(_player, _host, this);
        _overlay.Show();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _overlay?.Close(); } catch { }
        try { _player?.Dispose(); } catch { }
    }
}
