using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 右下角工具列的「回到顶部 / 刷新」二合一按钮。
///
/// 页面只负责两件事:
///   ① 每次 ScrollChanged / 页面重新可见时调用 <see cref="UpdateForOffset(double)"/>;
///   ② 处理 <see cref="Click"/> 时读 <see cref="ShowsBackToTop"/> 决定刷新还是回顶。
/// 阈值与交叉淡入都在控件内部, 六个页面共用同一套手感。
/// </summary>
public partial class TopRefreshButton : UserControl
{
    /// <summary>往下滑超过这个距离才切到"回到顶部"。</summary>
    private const double EnterTopOffset = 32;

    /// <summary>回到这个距离以内才切回"刷新"(回差, 防临界点闪烁)。</summary>
    private const double ExitTopOffset = 12;

    private const int CrossFadeMs = 150;
    private const double IconMinScale = 0.88;

    private bool _showsBackToTop;

    /// <summary>当前是否处于"回到顶部"状态。</summary>
    public bool ShowsBackToTop => _showsBackToTop;

    /// <summary>点击(宿主页面据此执行刷新或 ScrollToTop)。</summary>
    public event RoutedEventHandler? Click;

    public TopRefreshButton()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 按滚动偏移更新图标状态。传 null(还没量出 ScrollViewer)时按顶部处理。
    /// 值没跨过阈值时直接返回, 滚动过程中不会重复起动画。
    /// </summary>
    public void UpdateForOffset(double verticalOffset)
    {
        if (double.IsNaN(verticalOffset) || verticalOffset < 0) verticalOffset = 0;

        // 回差: 已经显示"回到顶部"时必须真的回到 12px 内才切回刷新;
        // 当前是刷新时, 往下滑超过 32px 才切过去。
        var showTop = verticalOffset > (_showsBackToTop ? ExitTopOffset : EnterTopOffset);
        if (showTop == _showsBackToTop) return;

        _showsBackToTop = showTop;
        ApplyState(showTop, animate: true);
    }

    public void UpdateForOffset(ScrollViewer? scrollViewer)
        => UpdateForOffset(scrollViewer?.VerticalOffset ?? 0);

    private void ApplyState(bool showTop, bool animate)
    {
        Btn.ToolTip = showTop ? "回到顶部" : "刷新（F5）";
        AutomationProperties.SetName(Btn, showTop ? "回到顶部" : "刷新");

        var target = showTop ? TopIcon : RefreshIcon;
        var other = showTop ? RefreshIcon : TopIcon;

        if (!animate)
        {
            target.Opacity = 1;
            other.Opacity = 0;
            IconScale.ScaleX = 1;
            IconScale.ScaleY = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        target.BeginAnimation(OpacityProperty,
            new DoubleAnimation(target.Opacity, 1, TimeSpan.FromMilliseconds(CrossFadeMs))
            {
                EasingFunction = ease,
            });
        other.BeginAnimation(OpacityProperty,
            new DoubleAnimation(other.Opacity, 0, TimeSpan.FromMilliseconds(CrossFadeMs))
            {
                EasingFunction = ease,
            });

        IconScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(IconMinScale, 1, TimeSpan.FromMilliseconds(CrossFadeMs)) { EasingFunction = ease });
        IconScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(IconMinScale, 1, TimeSpan.FromMilliseconds(CrossFadeMs)) { EasingFunction = ease });
    }

    private void OnButtonClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
