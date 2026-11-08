using System;
using System.Windows;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views;

namespace BiliDesk.Helpers;

/// <summary>
/// "下载视频"这个动作的统一入口(卡片右键 / 动态卡右键 / 播放器下载按钮共用)。
///
/// 为什么集中一处: 三个入口要做的事情完全一样 —— 补 cid → 弹选择窗 → 后台下载,
/// 而 cid 的来源各不相同(列表页没有、播放器手里有)。各写一遍必然出现
/// "某个入口少了 cid 补查" 或 "某个入口忘了关窗" 这类不一致。
/// </summary>
public static class DownloadActions
{
    /// <summary>
    /// 弹下载选择窗并在确认后开始下载。
    /// </summary>
    /// <param name="cid">已知的 cid; ≤0 时会先查一次视频详情(列表页的卡片都走这条)</param>
    /// <param name="preferredQn">默认选中的清晰度(播放器传当前实际清晰度; 列表页传设置里的默认画质)</param>
    /// <returns>下载成功后返回文件路径; 用户取消或失败返回 null</returns>
    public static async System.Threading.Tasks.Task<string?> ShowAndDownloadAsync(
        string? bvid, long cid, string? title, int preferredQn, Window? owner = null)
    {
        if (string.IsNullOrEmpty(bvid))
        {
            Svc.Toast.Show("缺少视频信息, 无法下载");
            return null;
        }

        // 1) 补 cid(下载地址依赖它)。列表页的卡片没有 cid, 只能查一次详情。
        if (cid <= 0)
        {
            var (ok, err, detail) = await Svc.Api.GetVideoAsync(bvid);
            if (!ok || detail == null || detail.Cid <= 0)
            {
                Svc.Toast.Show(err ?? "获取视频信息失败, 无法下载");
                return null;
            }
            cid = detail.Cid;
            if (string.IsNullOrWhiteSpace(title)) title = detail.Title;
        }

        // 2) 弹窗收集选择。窗口自己负责"读取清晰度/提示位置", 这里只管结果。
        var win = new DownloadWindow(bvid, cid, title, preferredQn);
        var host = owner ?? Application.Current?.MainWindow;
        if (host != null && !ReferenceEquals(host, win) && host.IsLoaded) win.Owner = host;
        // ★ 必须在 ShowDialog 之前夹高度: CenterOwner 在 Show 那一刻按当时高度算位置,
        //   之后再变高只会向下长(用户报的"底部按钮被裁掉"就是它)。
        win.PrepareBeforeShow();

        win.ShowDialog();
        if (win.Request == null) return null;   // 用户取消

        // 3) 后台下载(不阻塞调用方), 结果通过 Toast/进度条/系统通知反馈
        return await VideoDownloader.DownloadAsync(win.Request);
    }
}
