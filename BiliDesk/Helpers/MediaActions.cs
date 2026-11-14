using System;
using System.Collections.Generic;
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
    /// 打开图片预览窗(单张)。封面/头像这类"本来就只有一张"的调用走这里。
    /// 内部转调整组重载, 单张时预览窗不会显示左右箭头。
    /// </summary>
    public static void ShowImage(string? url, string? title = null, Window? owner = null)
        => ShowImage(new[] { url ?? "" }, 0, title, owner);

    /// <summary>
    /// 打开图片预览窗(**一组图**, 从 <paramref name="index"/> 那张开始)。
    ///
    /// 用于动态九宫格 / 评论多图 / 私信图片这类"同一处有一串图"的场景:
    /// 用户点开任意一张后, 能在窗口里用左右箭头或 ←→ 键继续翻, 不必关掉再点下一张。
    ///
    /// ★ 空串/无效地址在这里就滤掉, 并把 <paramref name="index"/> 换算成过滤后的位置 ——
    ///   交给窗口的永远是"干净的组 + 对得上的下标"。
    /// </summary>
    /// <param name="urls">整组图片地址(可含空项, 会被丢弃)</param>
    /// <param name="index">用户点的那张在**原列表**里的下标</param>
    public static void ShowImage(IReadOnlyList<string>? urls, int index,
                                 string? title = null, Window? owner = null)
    {
        // ★ 过滤空地址 + 下标换算统一走 PreviewList(纯函数, 有单测):
        //   这一步算错的表现是"点了第 3 张却打开第 2 张", 肉眼极难发现。
        var (clean, target) = PreviewList.Normalize(urls, index);

        if (clean.Length == 0)
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
            _ = win.LoadAsync(clean, target, title);
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
