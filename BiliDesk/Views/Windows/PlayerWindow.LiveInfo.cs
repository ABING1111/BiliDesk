using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 直播间主播信息(2026-11-13)
    //
    // 参考图右侧顶部那块: 头像 + 昵称 + "已开播 X · 9.9万人看过"。
    // ★ 只读: 不做关注/大航海入口(按需求), 头像与昵称点击进 UP 主主页。

    /// <summary>当前直播间的概览数据(null = 还没取到/不是直播)</summary>
    private LiveRoomInfo? _liveRoomInfo;

    /// <summary>刷新"已开播多久"的定时器(看过人数不频繁变, 只在进入/重连时取一次)</summary>
    private DispatcherTimer? _liveInfoTimer;

    private static readonly TimeSpan LiveInfoTick = TimeSpan.FromSeconds(30);

    /// <summary>进入直播间后异步取概览; 失败只影响这块小面板, 不影响播放。</summary>
    private async Task LoadLiveRoomInfoAsync(long roomId)
    {
        if (roomId <= 0) return;

        var (ok, err, info) = await Svc.Api.GetLiveRoomInfoAsync(roomId);
        if (_closing || !_isLive || roomId != _liveRoomId) return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (_closing || !_isLive || roomId != _liveRoomId) return;

            if (!ok || info == null)
            {
                if (LiveAnchorMetaText != null)
                    LiveAnchorMetaText.Text = string.IsNullOrWhiteSpace(err) ? "直播间信息获取失败" : err;
                return;
            }
            ApplyLiveRoomInfo(info);
        });
    }

    private void ApplyLiveRoomInfo(LiveRoomInfo info)
    {
        _liveRoomInfo = info;

        // 复用点播的 UP 主页入口(_ownerMid 在 OnUpPageClick 里被读)
        if (info.AnchorUid > 0) _ownerMid = info.AnchorUid;
        if (!string.IsNullOrEmpty(info.AnchorName)) _ownerName = info.AnchorName;
        if (!string.IsNullOrEmpty(info.AnchorFace)) _ownerFace = info.AnchorFace;

        if (LiveAnchorNameText != null)
        {
            LiveAnchorNameText.Text = string.IsNullOrWhiteSpace(info.AnchorName) ? "UP 主" : info.AnchorName;
            LiveAnchorNameText.ToolTip = string.IsNullOrWhiteSpace(info.Title)
                ? "进入 UP 主主页"
                : info.Title + "\n进入 UP 主主页";
        }

        if (LiveAnchorAreaChip != null)
        {
            if (string.IsNullOrWhiteSpace(info.AreaName))
            {
                LiveAnchorAreaChip.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (LiveAnchorAreaText != null) LiveAnchorAreaText.Text = info.AreaName;
                LiveAnchorAreaChip.Visibility = Visibility.Visible;
            }
        }

        UpdateLiveAnchorMeta();
        StartLiveInfoTimer();
        _ = LoadLiveAnchorAvatarAsync(info.AnchorFace);
    }

    private void UpdateLiveAnchorMeta()
    {
        if (LiveAnchorMetaText == null) return;
        LiveAnchorMetaText.Text = FormatLiveMeta(_liveRoomInfo);
    }

    /// <summary>"已开播 2 小时 15 分 · 9.9万人看过"。</summary>
    private static string FormatLiveMeta(LiveRoomInfo? info)
    {
        if (info == null) return "正在获取直播间信息…";
        if (info.LiveStatus == 0) return "未开播";

        var parts = new List<string>(3);

        if (info.LiveStartTime > 0)
        {
            var elapsed = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeSeconds(info.LiveStartTime);
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            parts.Add(elapsed.TotalMinutes < 1
                ? "刚刚开播"
                : elapsed.TotalHours < 1
                    ? $"已开播 {(int)elapsed.TotalMinutes} 分钟"
                    : elapsed.TotalDays < 1
                        ? $"已开播 {(int)elapsed.TotalHours} 小时 {(int)elapsed.Minutes} 分"
                        : $"已开播 {(int)elapsed.TotalDays} 天 {(int)elapsed.Hours} 小时");
        }
        else if (info.LiveStatus == 2)
        {
            parts.Add("轮播中");
        }
        else
        {
            parts.Add("直播中");
        }

        // 服务端直接给的文案: "9.9万人看过" 或 "602.3万人气"(房间类型不同口径不同)
        if (!string.IsNullOrWhiteSpace(info.WatchedText))
            parts.Add(info.WatchedText);
        else if (info.Watched > 0)
            parts.Add(FormatLiveCount(info.Watched) + "人看过");
        else if (info.Online > 0)
            parts.Add("人气 " + FormatLiveCount(info.Online));

        return string.Join(" · ", parts);
    }

    private static string FormatLiveCount(long n)
    {
        if (n >= 100_000_000) return (n / 100_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "亿";
        if (n >= 10_000) return (n / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "万";
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private void StartLiveInfoTimer()
    {
        if (_liveInfoTimer == null)
        {
            _liveInfoTimer = new DispatcherTimer { Interval = LiveInfoTick };
            _liveInfoTimer.Tick += (_, _) => UpdateLiveAnchorMeta();
        }
        _liveInfoTimer.Start();
    }

    private async Task LoadLiveAnchorAvatarAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        // 头像 44px, 按 96 解码即可
        var source = await CoverLoader.LoadAsync(url, 96);
        if (source == null) return;

        await Dispatcher.InvokeAsync(() =>
        {
            // 换房间/关窗后旧图不能回填
            if (_closing || !_isLive || !string.Equals(_liveRoomInfo?.AnchorFace, url, StringComparison.Ordinal))
                return;

            var brush = new ImageBrush(source) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            if (LiveAnchorAvatarBox != null) LiveAnchorAvatarBox.Background = brush;
            if (LiveAnchorAvatarFallback != null) LiveAnchorAvatarFallback.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>清空主播信息面板(离开直播/进入新直播间时)。</summary>
    private void ResetLiveInfoPanel()
    {
        _liveInfoTimer?.Stop();
        _liveRoomInfo = null;

        if (LiveAnchorAvatarBox != null)
        {
            LiveAnchorAvatarBox.Background = TryFindResource("AccentSoftFillBrush") as Brush;
            if (LiveAnchorAvatarFallback != null) LiveAnchorAvatarFallback.Visibility = Visibility.Visible;
        }
        if (LiveAnchorNameText != null)
        {
            LiveAnchorNameText.Text = "UP 主";
            LiveAnchorNameText.ToolTip = "进入 UP 主主页";
        }
        if (LiveAnchorAreaChip != null) LiveAnchorAreaChip.Visibility = Visibility.Collapsed;
        if (LiveAnchorMetaText != null) LiveAnchorMetaText.Text = "正在获取直播间信息…";
    }

    /// <summary>点主播头像/昵称 → 进 UP 主主页(与点播页同一个入口)。</summary>
    private void OnLiveAnchorClick(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        var mid = _liveRoomInfo?.AnchorUid ?? _ownerMid;
        if (mid <= 0) return;

        var dispatcher = new NavigationDispatcher(this);
        dispatcher.OpenUserSpace(mid.ToString());
        e.Handled = true;
    }
}
