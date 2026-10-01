using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views.Windows;

namespace BiliDesk.Helpers;

/// <summary>
/// 图片/视频相关的通用用户操作(查看大图、下载到本地)。
///
/// 为什么单独抽一个类:
/// "右键封面 → 看大图 / 保存" 这个动作在十来个页面里都会用到(首页、搜索、收藏、历史、
/// 关注的 UP 头像、个人空间…)。如果每处各写一遍, 窗口的 Owner 设置、异常兜底、
/// Toast 文案很快就会不一致。集中在这里, 各页面只负责把 url 和标题传进来。
/// </summary>
public static class MediaActions
{
    /// <summary>
    /// 打开图片预览窗。
    /// owner 传当前窗口, 保证预览窗居中于调用它的窗口而不是整个屏幕。
    /// </summary>
    public static void ShowImage(string? url, string? title = null, Window? owner = null)
    {
        var u = UrlUtil.Normalize(url);
        if (u.Length == 0)
        {
            Svc.Toast.Show("没有可预览的图片");
            return;
        }

        try
        {
            var win = new ImagePreviewWindow();
            var host = owner ?? Application.Current?.MainWindow;
            if (host != null && !ReferenceEquals(host, win) && host.IsLoaded)
                win.Owner = host;
            // 非模态: 用户能一边看大图一边继续浏览列表
            win.Show();
            _ = win.LoadAsync(u, title);
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("打开预览失败: " + ex.Message);
        }
    }

    /// <summary>把图片保存到本地下载目录, 并用 Toast 反馈结果</summary>
    public static async Task SaveImageAsync(string? url, string? baseName)
    {
        var u = UrlUtil.Normalize(url);
        if (u.Length == 0)
        {
            Svc.Toast.Show("没有可下载的图片");
            return;
        }

        var name = string.IsNullOrWhiteSpace(baseName) ? "image" : baseName!;
        Svc.Toast.Show("正在下载…");
        var path = await ImageDownloader.SaveAsync(u, name);
        if (path == null)
        {
            Svc.Toast.Show("下载失败, 请检查网络或磁盘权限");
            return;
        }

        // 提示里带上文件名; 目录路径太长, 单独交给"打开文件夹"按钮处理
        Svc.Toast.Show("已保存 " + Path.GetFileName(path));
    }
}
