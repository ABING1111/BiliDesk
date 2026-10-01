using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>
/// 图片下载器: 把封面/头像等图片另存到用户「下载」文件夹。
///
/// 为什么不复用 CoverLoader:
/// CoverLoader 的职责是"给界面喂图", 它会把文件塞进 imgcache 并按键名(MD5)存储,
/// 文件名对用户毫无意义。而"下载"是面向用户的: 需要可读的文件名、要避开重名、
/// 要落在用户找得到的地方。两件事的产物不同, 强行复用只会互相拖累。
/// </summary>
public static class ImageDownloader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static ImageDownloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
    }

    /// <summary>
    /// 下载图片到下载目录。成功返回最终文件路径, 失败返回 null。
    /// 采用"先查本地缓存"策略: 列表里已经显示过这张图, 缓存里就有原图,
    /// 省掉一次网络往返, 断网时也能保存。
    /// </summary>
    public static async Task<string?> SaveAsync(string url, string baseName)
    {
        url = UrlUtil.Normalize(url);
        if (url.Length == 0) return null;

        var ext = GuessExtension(url);
        var dir = AppPaths.DownloadDir;
        try { Directory.CreateDirectory(dir); } catch { /* 已存在或权限问题, 后面写入会报 */ }

        var path = FileNameUtil.UniquePath(dir, baseName, ext, "image");

        try
        {
            // 1) 优先用本地缓存(封面显示时已落盘)
            var cacheFile = Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg");
            if (File.Exists(cacheFile))
            {
                File.Copy(cacheFile, path, overwrite: false);
                return path;
            }

            // 2) 缓存没有就现下载
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return null;
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            // 写入失败时清掉可能产生的半截文件, 避免留下 0 字节的"损坏下载"
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
            return null;
        }
    }

    /// <summary>从 URL 猜扩展名。B 站的图多为 .jpg/.png/.webp, 猜不到就用 .jpg</summary>
    private static string GuessExtension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var dot = path.LastIndexOf('.');
            if (dot >= 0)
            {
                var e = path.Substring(dot).ToLowerInvariant();
                if (e.Length is >= 4 and <= 6 &&
                    (e is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp"))
                    return e;
            }
        }
        catch { /* URL 不规范时走默认值 */ }
        return ".jpg";
    }
}
