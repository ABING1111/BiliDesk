using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「动态」页: 左栏 = 全部动态 + 关注 UP 列表(有未读时头像左侧带红点),
/// 右栏 = 选中对象的内容, 两档排版(单列动态卡 / 多列卡片墙)由右上角的两枚 chip 切换。
/// 选中态与数据都在 FollowViewModel; 这个 code-behind 只做事件转发、滚动加载与排版 chip 的选中态。
/// </summary>
public partial class FollowPage : UserControl
{
    private const double AutoLoadThreshold = 240;

    /// <summary>看板轮询间隔: "关注的主播开播了"要能自己冒出来, 不能靠用户去点刷新。</summary>
    private static readonly TimeSpan LiveBoardPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>未读红点轮询间隔: 比直播状态慢一拍, 避免每分钟多打一次动态接口。</summary>
    private static readonly TimeSpan UnreadPollInterval = TimeSpan.FromMinutes(2);

    private DispatcherTimer? _liveBoardTimer;
    private DispatcherTimer? _unreadTimer;

    public FollowPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // 屏蔽把动态流筛空后补下一页(否则会停在一片空白上)
        FilterService.Instance.Changed += OnFilterApplied;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm)
        {
            // 动态流与"关注的主播正在直播"都纳入全局屏蔽(与首页/收藏/历史同一套条件)
            FilterService.Instance.Attach(vm.FeedItems);
            FilterService.Instance.Attach(vm.LiveNow);

            // 看板默认折叠(2026-11-03 用户要求"仅点击后再展开"): 页面实例会被 MainWindow
            // 缓存、VM 里的展开态会跨次保留, 所以每次进入页面都归位到收起, 不沿用上次的展开态。
            vm.LiveBoardOpen = false;
            UpdateLayoutChips(vm);
            await vm.InitAsync();
            StartPolling();
        }
    }

    /// <summary>屏蔽条件落地后, 等布局量完再判断要不要补一页动态</summary>
    private void OnFilterApplied()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
        {
            if (!IsLoaded || DataContext is not FollowViewModel vm) return;
            var sv = FeedScroll;
            if (sv == null) return;
            var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
            var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
            if (distanceToBottom > AutoLoadThreshold && !contentShort) return;
            await vm.LoadMoreFeedAsync();
        }));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面实例不会跟着导航销毁(MainWindow 缓存), 定时器必须自己停:
        // 否则用户在别的页面上, 这边还在定时打接口。
        _liveBoardTimer?.Stop();
        _unreadTimer?.Stop();
    }

    private void StartPolling()
    {
        _liveBoardTimer ??= new DispatcherTimer { Interval = LiveBoardPollInterval };
        _liveBoardTimer.Tick -= OnLiveBoardTick;
        _liveBoardTimer.Tick += OnLiveBoardTick;
        _liveBoardTimer.Start();

        _unreadTimer ??= new DispatcherTimer { Interval = UnreadPollInterval };
        _unreadTimer.Tick -= OnUnreadTick;
        _unreadTimer.Tick += OnUnreadTick;
        _unreadTimer.Start();
    }

    private void OnLiveBoardTick(object? sender, EventArgs e)
    {
        if (!IsPageActive()) return;
        if (DataContext is FollowViewModel vm) _ = vm.RefreshLiveBoardAsync(showLoading: false);
    }

    private void OnUnreadTick(object? sender, EventArgs e)
    {
        if (!IsPageActive()) return;
        if (DataContext is FollowViewModel vm) _ = vm.RefreshUnreadBadgesAsync();
    }

    /// <summary>最小化到托盘 / 切到别的页面时不再轮询; 重新可见后下一拍自然恢复。</summary>
    private bool IsPageActive()
    {
        var win = Window.GetWindow(this);
        return IsVisible && win != null && win.IsVisible &&
               win.WindowState != WindowState.Minimized;
    }

    // ------------------------------------------------------------ 右栏排版切换

    private void OnLayoutCardsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm) { vm.UseCardWall = false; UpdateLayoutChips(vm); }
    }

    private void OnLayoutWallClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is FollowViewModel vm) { vm.UseCardWall = true; UpdateLayoutChips(vm); }
    }

    /// <summary>
    /// 同步两枚 chip 的选中态。★ 只在这里写 Tag(与播放页评论排序同一套约定: 字符串 "on"/"off"),
    /// 分散着改必然出现"两边都亮"或"两边都不亮"。
    /// </summary>
    private void UpdateLayoutChips(FollowViewModel vm)
    {
        if (BtnLayoutCards == null || BtnLayoutWall == null) return;
        BtnLayoutCards.Tag = vm.UseCardWall ? "off" : "on";
        BtnLayoutWall.Tag = vm.UseCardWall ? "on" : "off";
    }

    // ------------------------------------------------------------ 选择

    /// <summary>点「全部动态」: 右栏显示关注 UP 近期的视频投稿</summary>
    private void OnSelectAll(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is FollowViewModel vm) _ = vm.SelectAllAsync();
    }

    /// <summary>点某个 UP: 右栏只显示 TA 的投稿视频</summary>
    private void OnSelectUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (((FrameworkElement)sender).DataContext is not FollowUser u) return;
        if (DataContext is FollowViewModel vm) _ = vm.SelectUpAsync(u);
    }

    // ------------------------------------------------------------ 关注直播看板(2026-10-31)

    /// <summary>点看板标题行: 折叠/展开主播列表</summary>
    private void OnToggleLiveBoard(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FollowViewModel vm) vm.LiveBoardOpen = !vm.LiveBoardOpen;
    }

    /// <summary>点看板里的一个主播: 直接进 TA 的直播间</summary>
    private void OnLiveItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (((FrameworkElement)sender).DataContext is not VideoItem item) return;
        if (DataContext is FollowViewModel vm) vm.OpenLive(item);
    }

    // ------------------------------------------------------------ 滚动加载

    /// <summary>右栏: 内容滚动到底自动补下一页(全部动态 = 游标; 单 UP = 页码)</summary>
    private void FeedScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not FollowViewModel vm) return;
        var sv = (ScrollViewer)sender;
        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > AutoLoadThreshold) return;
        _ = vm.LoadMoreFeedAsync();
    }

    /// <summary>左栏: UP 列表滚到底自动补(一页 30 个)</summary>
    private void UpListScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not FollowViewModel vm) return;
        var sv = (ScrollViewer)sender;
        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > AutoLoadThreshold) return;
        if (vm.Users.Count == 0) return;
        _ = vm.LoadMoreAsync();
    }

    // ------------------------------------------------------------ 左栏条目的右键操作

    private void OnVisitClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
        }
    }

    /// <summary>取消关注该 UP 主, 成功后从列表移除(移除的是选中项时自动回到全部动态)</summary>
    private async void OnUnfollowClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.DataContext is not FollowUser u) return;
        if (DataContext is not FollowViewModel vm) return;

        var (ok, err) = await Svc.Api.FollowUpAsync(u.Mid, follow: false);
        if (ok)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => vm.RemoveUser(u));
            Svc.Toast.Show($"已取消关注 {u.Name}");
        }
        else
        {
            Svc.Toast.Show("取消关注失败: " + (err ?? "未知错误"));
        }
    }

    /// <summary>右键头像 → 预览大图</summary>
    private void OnViewAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            MediaActions.ShowImage(u.Face, u.Name + " 的头像", Window.GetWindow(this));
        }
    }

    /// <summary>右键头像 → 下载到本地</summary>
    private async void OnDownloadAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
        {
            await MediaActions.SaveImageAsync(u.Face, u.Name + "_头像");
        }
    }
}
