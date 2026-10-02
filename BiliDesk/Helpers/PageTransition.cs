using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace BiliDesk.Helpers;

/// <summary>
/// 切页过渡: 三段式"淡出 → 换页 → 淡入", 动画画在一块**纯色幕布**上。
///
/// ★ 为什么单独成一个类: 这段逻辑有两处容易写错、而且只有"跑起来"才看得出来 ——
///   ① 过渡途中被打断时不能停在被取消的那一页;
///   ② "点当前页"的早退判断在过渡在途时不能生效(否则点了导航项却停在旧页)。
///   放在 MainWindow 里就得开一个完整 App 才能验(探针跑不起 `new BiliDesk.App()`, 见 memory),
///   抽出来之后探针能直接拿它对着真实窗口跑 —— 验的是**发布出去的同一份代码**, 不是复制品。
///
/// ★ 承载体为什么是幕布而不是 `PageHost.Opacity`: 见 MainWindow.xaml 里 NavVeil 那段注释
///   (结论: 动画载体**不是**卡顿来源, 实测两种写法帧间隔一样; 幕布的真实收益是让"建页面"
///   落在被遮住的那段时间里)。
/// </summary>
public sealed class PageTransition
{
    /// <summary>淡出时长(幕布 0→1, 盖住旧页面)</summary>
    public const int FadeOutMs = 90;

    /// <summary>淡入时长(幕布 1→0, 露出新页面)。比淡出长一点: 出场干脆、入场柔和。</summary>
    public const int FadeInMs = 170;

    private readonly Border _veil;

    /// <summary>过渡代数。每次 Run/Jump +1, 在途回调靠它判断自己是否已被更晚的过渡取代。</summary>
    private int _epoch;

    public PageTransition(Border veil) => _veil = veil;

    /// <summary>是否有过渡正在跑(幕布已拉出/正在拉出)。调用方用它做"点当前页"的早退判断。</summary>
    public bool InFlight { get; private set; }

    /// <summary>
    /// 播一次完整过渡。<paramref name="swap"/> 在**幕布全不透明**的那一刻调用 —— 换页动作
    /// (以及首次建页)被完全遮住, 用户看不到任何跳变。
    /// 过渡途中再次调用会从幕布**当前**透明度接着走(不跳回 0), 并让上一次的回调作废。
    /// </summary>
    public void Run(Action swap)
    {
        var epoch = ++_epoch;
        InFlight = true;
        _veil.Visibility = Visibility.Visible;

        var fadeOut = new DoubleAnimation(_veil.Opacity, 1, TimeSpan.FromMilliseconds(FadeOutMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (epoch != _epoch) return;   // 已被更晚的过渡取代, 这一拍作废
            swap();

            var fadeIn = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeInMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (_, _) =>
            {
                if (epoch != _epoch) return;
                _veil.Visibility = Visibility.Collapsed;
                _veil.Opacity = 0;
                InFlight = false;
            };
            _veil.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };
        _veil.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>
    /// 不播过渡、立刻换页(首次显示窗口时用), 并清掉幕布的残留动画状态。
    /// 同时让所有在途回调作废 —— 被打断的过渡不能再改页面。
    /// </summary>
    public void Jump(Action swap)
    {
        _epoch++;
        InFlight = false;
        _veil.BeginAnimation(UIElement.OpacityProperty, null);
        _veil.Opacity = 0;
        _veil.Visibility = Visibility.Collapsed;
        swap();
    }
}
