using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 主窗口右下角的「播放页」入口。
///
/// 生命周期:
///   · 只订阅 <see cref="BiliDesk.Services.PlayerService.StateChanged"/>, 不轮询 PlayerWindow;
///   · 播放器窗口存在时淡入并占一个按钮位; 关闭时淡出后 Collapsed(不影响下面的工具列按钮);
///   · 声波只在"按钮可见 + 真的在播放"时跑 30fps, 暂停/切页立刻停表。
/// </summary>
public partial class PlayerFab : UserControl
{
    private readonly DispatcherTimer _waveTimer;
    private int _waveTick;
    private bool _hooked;
    private bool _shown;
    private bool _isPlaying;

    /// <summary>声波每根条的相位(单位: 周期比例), 与「听视频」的错峰观感一致。</summary>
    private static readonly double[] WavePhase = { 0, 0.33, 0.66 };

    private const double WavePeriodTicks = 25.0;
    private const int ShowMs = 180;
    private const int HideMs = 140;

    public PlayerFab()
    {
        InitializeComponent();

        _waveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _waveTimer.Tick += OnWaveTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) =>
        {
            // 切页/窗口最小化后 IsVisible 变 false: 停表; 回来时按当前状态重启。
            UpdateTooltip();
            SyncWave();
        };

        // ★ 构造时就订阅: 本控件初始是 Collapsed, 而 Collapsed 元素不保证走到 Loaded;
        //   若等 Loaded 才订阅, "页面先建好、播放器后打开"没问题, 但"播放器已开着再建页面"
        //   会错过打开事件。构造时先接上, Loaded/Unloaded 只负责切页时的重挂。
        _hooked = true;
        BiliDesk.Svc.Player.StateChanged += OnPlayerStateChanged;
        ApplyState(animate: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            BiliDesk.Svc.Player.StateChanged += OnPlayerStateChanged;
            _hooked = true;
        }
        ApplyState(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hooked)
        {
            BiliDesk.Svc.Player.StateChanged -= OnPlayerStateChanged;
            _hooked = false;
        }
        StopWave(reset: true);
    }

    private void OnPlayerStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyState(animate: true)));
            return;
        }
        ApplyState(animate: true);
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        // 用户点击属于前台手势: 还原最小化 + 置前, 不在这里切播放/暂停。
        BiliDesk.Svc.Player.ActivateCurrent();
    }

    // ------------------------------------------------------------ 显示 / 隐藏

    private void ApplyState(bool animate)
    {
        var hasPlayer = BiliDesk.Svc.Player.HasPlayer;
        _isPlaying = hasPlayer && BiliDesk.Svc.Player.IsPlaying;

        if (hasPlayer)
        {
            if (!_shown)
            {
                _shown = true;
                Visibility = Visibility.Visible;
                if (animate) PlayShowAnimation();
                else SetShownVisual();
            }
            UpdateTooltip();
        }
        else if (_shown)
        {
            _shown = false;
            if (animate) PlayHideAnimation();
            else
            {
                Visibility = Visibility.Collapsed;
                ResetVisual();
            }
        }

        SyncWave();
    }

    private void UpdateTooltip()
    {
        var title = BiliDesk.Svc.Player.CurrentTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            BtnPlayPage.ToolTip = "播放页";
            return;
        }

        // 超长标题在 ToolTip 里会被屏幕边缘硬截, 先自己收一下, 保留可读的前半段。
        var shown = title.Length > 70 ? title[..70] + "…" : title;
        BtnPlayPage.ToolTip = "播放页 · " + shown;
    }

    private void PlayShowAnimation()
    {
        Root.Opacity = 0;
        RootScale.ScaleX = 0.9;
        RootScale.ScaleY = 0.9;
        RootShift.Y = 8;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ShowMs)) { EasingFunction = ease });
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(ShowMs)) { EasingFunction = ease });
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(ShowMs)) { EasingFunction = ease });
        RootShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(ShowMs)) { EasingFunction = ease });
    }

    private void PlayHideAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(HideMs))
        {
            EasingFunction = ease,
        };
        // 动画期间又出现的话(_shown 被重新置 true)不能把新状态收掉。
        fade.Completed += (_, _) =>
        {
            if (_shown) return;
            Visibility = Visibility.Collapsed;
            ResetVisual();
        };
        Root.BeginAnimation(OpacityProperty, fade);
        RootShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(RootShift.Y, 6, TimeSpan.FromMilliseconds(HideMs)) { EasingFunction = ease });
    }

    private void SetShownVisual()
    {
        Root.BeginAnimation(OpacityProperty, null);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        RootShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        Root.Opacity = 1;
        RootScale.ScaleX = 1;
        RootScale.ScaleY = 1;
        RootShift.Y = 0;
    }

    private void ResetVisual()
    {
        Root.BeginAnimation(OpacityProperty, null);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        RootShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        Root.Opacity = 0;
        RootScale.ScaleX = 0.9;
        RootScale.ScaleY = 0.9;
        RootShift.Y = 8;
    }

    // ------------------------------------------------------------ 声波律动

    private void SyncWave()
    {
        _isPlaying = _shown && BiliDesk.Svc.Player.IsPlaying;
        var shouldRun = _shown && IsVisible && _isPlaying;
        if (shouldRun) StartWave();
        else StopWave(reset: true);
    }

    private void StartWave()
    {
        if (_waveTimer.IsEnabled) return;
        _waveTick = 0;
        _waveTimer.Start();
        UpdateWave();
    }

    private void StopWave(bool reset)
    {
        if (_waveTimer.IsEnabled) _waveTimer.Stop();
        if (reset) ResetWaveBars();
    }

    private void OnWaveTick(object? sender, EventArgs e)
    {
        // 暂停/播完/切页后立刻收手: 定时器在 Dispatcher 里长期持有本控件,
        // 不能靠"下次状态变化"才停。
        if (!_shown || !IsVisible)
        {
            StopWave(reset: true);
            return;
        }
        // 每 0.5s 跟原生侧对一次表(状态事件已覆盖 Playing/Paused/EOF, 这里只是兜底),
        // 其余 tick 只读缓存 —— 不让 30fps 的心跳每次都去碰 LibVLC 原生对象。
        if (_waveTick % 15 == 0) _isPlaying = BiliDesk.Svc.Player.IsPlaying;
        if (!_isPlaying)
        {
            StopWave(reset: true);
            return;
        }
        _waveTick++;
        UpdateWave();
    }

    private void UpdateWave()
    {
        SetWaveHeight(Wave1, _waveTick, WavePhase[0]);
        SetWaveHeight(Wave2, _waveTick, WavePhase[1]);
        SetWaveHeight(Wave3, _waveTick, WavePhase[2]);
    }

    /// <summary>0.5 + 0.5*sin 映射到 [4,11], 与「听视频」的声波同一套节奏。</summary>
    private static void SetWaveHeight(Border? bar, int tick, double phaseCycles)
    {
        if (bar == null) return;
        var phase = (tick / WavePeriodTicks + phaseCycles) * Math.PI * 2;
        var k = 0.5 + 0.5 * Math.Sin(phase);
        bar.Height = 4 + 7 * k;
    }

    private void ResetWaveBars()
    {
        if (Wave1 != null) Wave1.Height = 6;
        if (Wave2 != null) Wave2.Height = 10;
        if (Wave3 != null) Wave3.Height = 7;
    }
}
