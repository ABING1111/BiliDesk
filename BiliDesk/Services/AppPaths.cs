using System;
using System.IO;

namespace BiliDesk.Services;

/// <summary>应用数据目录管理(位于 %LocalAppData%\BiliDesk)</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } = "";
    public static string CacheDir => Path.Combine(DataDir, "imgcache");
    public static string WebView2Dir => Path.Combine(DataDir, "WebView2Data");
    public static string LogFile => Path.Combine(DataDir, "errors.txt");

    /// <summary>
    /// 用户「图片另存为」的默认目录。
    /// 首选系统「下载」文件夹 —— 用户找得到、符合直觉; 拿不到时退化到应用数据目录,
    /// 保证功能在任何环境下都可用(而不是静默失败)。
    /// </summary>
    public static string DownloadDir
    {
        get
        {
            try
            {
                var dl = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(dl)) return dl;
            }
            catch { /* 退化到数据目录 */ }
            return Path.Combine(DataDir, "downloads");
        }
    }

    public static void Init()
    {
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDesk");
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(WebView2Dir);
        }
        catch
        {
            // 目录创建失败不应阻止启动
        }
    }
}