using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 动态流里的一条视频动态卡: 顶部 [UP 头像 + 昵称 + 发布时间], 下方左侧封面(带时长角标)、
/// 右侧标题与播放/弹幕数据。版式对齐 B 站动态页。
///
/// 纯展示 + 两个点击入口, 没有入场/悬停动画 —— 动态流是**单列大卡**, 一屏只有几条,
/// 卡片墙那套"一屏上百张同时播入场动画"的负担在这里不存在, 也就没必要引入它那套
/// ConditionalWeakTable 去重逻辑(见 VideoCard)。
/// </summary>
public partial class DynamicCard : UserControl
{
    public DynamicCard() => InitializeComponent();

    /// <summary>当前卡片对应的条目(右键菜单的各个处理器都从它取数据)</summary>
    private VideoItem? Current => DataContext as VideoItem;

    /// <summary>点封面或标题 → 打开这条视频(与卡片墙上点封面是同一条路)</summary>
    private void OnOpenClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not VideoItem v) return;
        if (string.IsNullOrEmpty(v.Bvid)) return;
        Svc.Player.PlayVideo(v.Bvid, v.Title);
    }

    /// <summary>点头像或昵称 → 进 TA 的主页(复用播放器/卡片墙那条 NavigationDispatcher 路径)</summary>
    private void OnUpClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not VideoItem v) return;
        if (v.OwnerMid <= 0)
        {
            Svc.Toast.Show("没拿到 UP 主的 UID, 打不开主页");
            return;
        }
        new NavigationDispatcher(Window.GetWindow(this)).OpenUserSpace(v.OwnerMid.ToString());
    }

    // ------------------------------------------------------------ 右键菜单
    //
    // ★ 三个动作与 VideoCard 的**逐字同款**(动态卡是默认排版, 不能少功能)。
    //   没有把三者抽成公共方法: VideoCard 那三份是 private, 抽出来要动它的公开面;
    //   而这里每个都只有两三行, 重复的代价小于为它新建一个共享类的代价。
    //   (若将来第三处也用同一套, 再抽到 Helpers/MediaActions 旁边。)

    private async void OnDownloadCover(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null) return;
        await MediaActions.SaveImageAsync(v.Cover, v.Title);
    }

    private async void OnDownloadVideo(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        await VideoDownloader.DownloadAsync(v.Bvid, v.Title, Window.GetWindow(this));
    }

    /// <summary>
    /// 把这条视频加入 B 站账号的「稍后再看」。
    /// ★ 动态条目的 rid 就是 avid(Aid 已在解析时填好), 所以这里通常不必补查详情;
    ///   补查那一步留着是因为 Aid 为 0 的形态确实存在(转发/结构异常), 与 VideoCard 同款兜底。
    /// </summary>
    private async void OnAddWatchLater(object sender, RoutedEventArgs e)
    {
        var v = Current;
        if (v == null || string.IsNullOrEmpty(v.Bvid)) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("稍后再看需要先登录");
            return;
        }

        var aid = v.Aid;
        if (aid <= 0)
        {
            var (_, _, vd) = await Svc.Api.GetVideoAsync(v.Bvid);
            aid = vd?.Aid ?? 0;
        }
        if (aid <= 0)
        {
            Svc.Toast.Show("没拿到这条视频的 avid, 加不了");
            return;
        }

        var (ok, err) = await Svc.Api.AddWatchLaterAsync(aid);
        Svc.Toast.Show(ok ? "已加入稍后再看" : "加入失败: " + (err ?? "未知错误"));
    }
}
