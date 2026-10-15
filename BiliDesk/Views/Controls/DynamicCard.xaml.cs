using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
}
