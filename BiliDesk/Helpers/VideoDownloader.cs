using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>
/// 视频下载器: 把播放地址对应的 MP4 存到用户「下载」文件夹。
///
/// 几个关键取舍:
///   1. **走 durl 单流**(见 ApiClient.GetDownloadUrlAsync 的注释): 拿到的是完整 MP4,
///      不需要 ffmpeg 合并音轨, 下载完即可播放。
///   2. **必须先请求播放地址再下**: B 站的 CDN 直链带时效签名, 不能提前存起来复用。
///   3. **下载到 .part 再改名**: 中途失败/取消时不会留下一个"看起来正常但其实是半截"的
///      mp4 文件 —— 这是下载器最容易让人踩坑的地方。
///   4. 直链同样有 Referer 校验, 必须带上, 否则会拿到 403。
/// </summary>
public static class VideoDownloader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        // 整段视频可能几百兆, 不能用短超时; 这里只约束"连接建立"阶段
        Timeout = Timeout.InfiniteTimeSpan
    };

    static VideoDownloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
    }

    /// <summary>
    /// 下载视频并保存到本地。返回最终文件路径, 失败返回 null。
    /// 整个过程会通过 Toast 给出进度反馈。
    /// </summary>
    public static async Task<string?> DownloadAsync(string bvid, string? title, Window? owner = null)
    {
        if (string.IsNullOrEmpty(bvid))
        {
            Svc.Toast.Show("缺少视频信息, 无法下载");
            return null;
        }

        // 1) 先取 cid(播放地址依赖它)
        var (detailOk, detailErr, detail) = await Svc.Api.GetVideoAsync(bvid);
        if (!detailOk || detail == null || detail.Cid <= 0)
        {
            Svc.Toast.Show(detailErr ?? "获取视频信息失败, 无法下载");
            return null;
        }

        // 2) 取单流直链
        var (ok, err, url, _) = await Svc.Api.GetDownloadUrlAsync(bvid, detail.Cid);
        if (!ok || string.IsNullOrEmpty(url))
        {
            Svc.Toast.Show(err ?? "获取下载地址失败");
            return null;
        }

        var name = string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(detail.Title) ? bvid : detail.Title!)
            : title!;

        var path = BuildTargetPath(name);
        var part = path + ".part";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // CDN 校验来源页, 不带 Referer 会被拒
            req.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{bvid}");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");

            Svc.Toast.Show("开始下载…");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                Svc.Toast.Show($"下载失败 (HTTP {(int)resp.StatusCode})");
                return null;
            }

            var total = resp.Content.Headers.ContentLength ?? 0;

            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                long done = 0;
                var lastReport = DateTime.UtcNow;
                int read;
                while ((read = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read));
                    done += read;

                    // 限频提示: 每 1.5 秒最多刷一次, 避免 Toast 被高频更新淹没
                    if ((DateTime.UtcNow - lastReport).TotalSeconds >= 1.5)
                    {
                        lastReport = DateTime.UtcNow;
                        Svc.Toast.Show(total > 0
                            ? $"下载中 {done * 100 / total}%"
                            : $"下载中 {done / 1024 / 1024} MB");
                    }
                }
            }

            // 3) 全部写完才改成正式名字(保证"看得见的文件一定是完整的")
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);

            Svc.Toast.Show("已保存 " + Path.GetFileName(path));
            return path;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* 忽略清理失败 */ }
            Svc.Toast.Show("下载失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>生成不重名的目标路径(标题里的非法字符由 FileNameUtil 清理)</summary>
    private static string BuildTargetPath(string title)
    {
        var dir = AppPaths.DownloadDir;
        try { Directory.CreateDirectory(dir); } catch { /* 后面写入会报错 */ }
        return FileNameUtil.UniquePath(dir, title, ".mp4", "video");
    }
}
