using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 右上角的下载进度条(2026-11-04, issue #38)。
///
/// 状态全部来自 <see cref="Svc.DownloadProgress"/>; 本控件只做两件事:
///   1. 订阅进度变化, 把合并后的文案贴到 TextBlock;
///   2. 管理"出现/消失"的透明度 —— **只在从无到有时淡入**。
///
/// ★ 关键: 后续每一条进度更新都只换文字, 绝不重播淡入。
///   以前 Toast 每次 Show 都从透明度 0 淡到 1, 进度一刷新就黑一下,
///   用户看到的就是"每 1% 闪烁"。这里的更新必须是完全静态的。
///
/// ★ 订阅挂在 Loaded/Unloaded 上: 播放器窗口会反复开关, 不摘事件会一直把旧窗口
///   留在 DownloadProgressService.Changed 的调用列表里(窗口关不掉 / 内存泄漏)。
/// </summary>
public partial class DownloadProgressBanner : UserControl
{
    private bool _subscribed;
    private bool _hiding;

    public DownloadProgressBanner()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;
        _subscribed = true;
        Svc.DownloadProgress.Changed += OnChanged;
        // 窗口是在下载途中打开的(比如播放器): 立刻把当前进度补上。
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        _subscribed = false;
        Svc.DownloadProgress.Changed -= OnChanged;
    }

    private void OnChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            // 下载可能在后台线程上报; 所有 UI 更新统一回 Dispatcher。
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        if (!_subscribed) return;   // 已卸载(窗口关闭)后才到达的排队回调, 直接丢掉

        var summary = Svc.DownloadProgress.Summary;
        if (summary.Length == 0)
        {
            // 没有任务了: 淡出后折叠。已经是隐藏/正在淡出时什么都不用做。
            if (Visibility != Visibility.Visible || _hiding) return;
            _hiding = true;
            Fade(0, 160, () =>
            {
                if (!_hiding) return;   // 淡出期间又开了新下载 → 别把它折叠掉
                Visibility = Visibility.Collapsed;
                _hiding = false;
            });
            return;
        }

        var wasHiding = _hiding;
        _hiding = false;
        Text.Text = summary;

        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
            Fade(1, 180);   // 只有"从无到有"这一次淡入
        }
        else if (wasHiding)
        {
            // 淡出中途又来了新下载: 撤掉淡出, 直接把透明度拉回 1。
            // ★ 只有 wasHiding 才动透明度 —— 普通进度更新(包括正在淡入时)一律不碰,
            //   否则刚淡入 50ms 就被第二条进度打断, 等于没有淡入。
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
    }

    /// <summary>
    /// 透明度动画。基值先设成终值 + FillBehavior.Stop: 动画结束后落回终值,
    /// 不会出现"淡出结束时闪回不透明"的一帧。
    /// </summary>
    private void Fade(double to, int milliseconds, Action? completed = null)
    {
        var from = Opacity;   // 读当前动画值: 淡出中途被新任务打断也能接着往上走
        BeginAnimation(OpacityProperty, null);
        Opacity = to;
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.Stop
        };
        if (completed != null) animation.Completed += (_, _) => completed();
        BeginAnimation(OpacityProperty, animation);
    }
}
