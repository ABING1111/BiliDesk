using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Mpv;

namespace BiliDesk.MpvProbe.Ui;

/// <summary>
/// 跟随视频区矩形、盖在 mpv 原生窗口之上的覆盖层(2026-11-22 PoC)。
///
/// 这就是 LibVLCSharp.WPF 里 ForegroundWindow 的角色: 一个无边框透明顶层窗口,
/// 盖住视频区, 里面放控制栏/弹幕/字幕。原生视频窗口永远在 WPF 合成器之外,
/// 只有**另一个 HWND** 才能可靠地压在它上面。
/// </summary>
public sealed class MpvOverlayWindow : Window
{
    private readonly MpvPlayer _player;
    private readonly FrameworkElement _host;
    private readonly Window _owner;
    private readonly Slider _slider;
    private readonly TextBlock _time;
    private readonly TextBlock _state;
    private readonly DispatcherTimer _timer;
    private bool _dragging;

    public MpvOverlayWindow(MpvPlayer player, FrameworkElement host, Window owner)
    {
        _player = player;
        _host = host;
        _owner = owner;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _time = new TextBlock
        {
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            Text = "00:00 / 00:00",
        };

        _slider = new Slider
        {
            Minimum = 0,
            Maximum = 1000,
            Width = 380,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
        };
        _slider.PreviewMouseLeftButtonDown += (_, _) => _dragging = true;
        _slider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _dragging = false;
            SeekToSlider();
        };
        _slider.ValueChanged += (_, _) =>
        {
            if (_dragging) _time.Text = Format(_slider.Value / 1000.0 * _player.Duration);
        };

        _state = new TextBlock
        {
            Text = "mpv overlay PoC",
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            FontSize = 12,
            Margin = new Thickness(14, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(12),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = BuildBar(),
        };

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_state);
        root.Children.Add(bar);
        root.MouseLeftButtonDown += (_, e) =>
        {
            // 空白处点击 = 播放/暂停(与正式播放器一致); 控制栏上的点击已 handled
            _player.SetPause(!_player.IsPaused);
            e.Handled = true;
        };
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        KeyDown += OnKeyDown;
    }

    private UIElement BuildBar()
    {
        var play = MakeButton("⏯", (_, _) => _player.SetPause(!_player.IsPaused));
        var back = MakeButton("-10s", (_, _) => _player.SeekAbsolute(Math.Max(0, _player.TimePos - 10)));
        var fwd = MakeButton("+10s", (_, _) => _player.SeekAbsolute(_player.TimePos + 10));
        var speed1 = MakeButton("1x", (_, _) => _player.Speed = 1);
        var speed2 = MakeButton("2x", (_, _) => _player.Speed = 2);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(play);
        panel.Children.Add(back);
        panel.Children.Add(fwd);
        panel.Children.Add(speed1);
        panel.Children.Add(speed2);
        panel.Children.Add(_slider);
        panel.Children.Add(_time);
        return panel;
    }

    private static Button MakeButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12,
            Cursor = Cursors.Hand,
        };
        button.Click += onClick;
        return button;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _player.SetPause(!_player.IsPaused);
                e.Handled = true;
                break;
            case Key.Left:
                _player.SeekAbsolute(Math.Max(0, _player.TimePos - 5));
                e.Handled = true;
                break;
            case Key.Right:
                _player.SeekAbsolute(_player.TimePos + 5);
                e.Handled = true;
                break;
        }
    }

    private void SeekToSlider()
    {
        var duration = _player.Duration;
        if (duration <= 0) return;
        _player.SeekAbsolute(_slider.Value / 1000.0 * duration);
    }

    private void Tick()
    {
        if (_owner.WindowState == WindowState.Minimized)
        {
            if (IsVisible) Hide();
            return;
        }

        if (!IsVisible) Show();
        UpdateBounds();

        var duration = _player.Duration;
        if (duration <= 0) return;
        if (!_dragging)
        {
            _slider.Value = _player.TimePos / duration * 1000.0;
            _time.Text = Format(_player.TimePos) + " / " + Format(duration);
        }
        _state.Text = $"mpv overlay PoC   speed={_player.Speed:0.##}  paused={_player.IsPaused}";
    }

    private void UpdateBounds()
    {
        if (_host.ActualWidth <= 0 || _host.ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(_host);
        var topLeft = _host.PointToScreen(new Point(0, 0));
        Left = topLeft.X / dpi.DpiScaleX;
        Top = topLeft.Y / dpi.DpiScaleY;
        Width = _host.ActualWidth;
        Height = _host.ActualHeight;
    }

    private static string Format(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
