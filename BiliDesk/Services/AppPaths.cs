using System;
using System.IO;

namespace BiliDesk.Services;

/// <summary>应用数据目录管理(位于 %LocalAppData%\BiliDesk)</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } = "";
    public static string WebView2Dir => Path.Combine(DataDir, "WebView2Data");
    public static string LogFile => Path.Combine(DataDir, "errors.txt");

    /// <summary>
    /// **内部缓存根目录**(封面图 imgcache + 短视频合流 vcache)。
    /// ★ 2026-10-11 起**不再暴露给用户**: 设置页的"缓存位置"改管下载目录(见 <see cref="DownloadDir"/>)。
    ///   这两类是可再生缓存, 用户看不见也无需管理, 固定留在 <see cref="DataDir"/> 下。
    ///   保留 CacheRoot 这层抽象(而不是直接用 DataDir)是为了让"缓存跟数据分开"这件事在代码里
    ///   仍然成立, 将来若再要做"清理缓存"之类不会退化成"删掉整个数据目录"。
    /// ★ 由 <see cref="ResetCacheRoot"/> 在启动时复位; 默认 = <see cref="DataDir"/>。
    /// </summary>
    public static string CacheRoot { get; private set; } = "";

    /// <summary>默认缓存根目录(= <see cref="DataDir"/>, 内部缓存不再自定义)</summary>
    public static string DefaultCacheRoot => DataDir;

    public static string CacheDir => Path.Combine(CacheRoot, "imgcache");
    public static string VideoCacheDir => Path.Combine(CacheRoot, "vcache");

    /// <summary>
    /// 用户「图片另存为 / 下载视频」的存放目录 —— **设置页「缓存位置」管的就是这里**。
    ///
    /// ★ 语义澄清(2026-10-11 用户指出): 界面上叫"缓存位置"、离线缓存页也写"缓存位置",
    ///   用户理解的"缓存"就是"下载下来的视频存在哪"。所以这个设置控制**下载目录**,
    ///   而不是 imgcache/vcache(那两项是内部可再生缓存, 用户看不见也不关心,
    ///   固定留在 <see cref="DataDir"/> 下就好, 不暴露给用户)。
    /// - 默认 = 系统「下载」文件夹(用户找得到、符合直觉); 拿不到时退化到数据目录。
    /// - 用户可自定义(设置页改); 由 <see cref="SetDownloadDir"/> 在设置读取后写入,
    ///   与 <see cref="CacheRoot"/> 同一套路(配置驱动的静态值必须显式同步)。
    /// </summary>
    private static string? _downloadDirOverride;

    /// <summary>下载/另存为的目标目录: 用户自定义优先, 否则系统「下载」文件夹, 再否则数据目录。</summary>
    public static string DownloadDir
    {
        get
        {
            if (!string.IsNullOrEmpty(_downloadDirOverride)) return _downloadDirOverride!;
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

    /// <summary>默认下载目录(用户没自定义时的"恢复默认"目标)</summary>
    public static string DefaultDownloadDir
    {
        get
        {
            try
            {
                var dl = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(dl)) return dl;
            }
            catch { /* 退化 */ }
            return Path.Combine(DataDir, "downloads");
        }
    }

    /// <summary>
    /// 设置下载目录(内部会规范化; 不可用则回落默认)。**不做文件迁移** ——
    /// 已下载的视频是用户的成品文件, 不是可再生缓存, 静默搬动等于替用户做决定
    /// (可能几十 GB、可能他就是想留在那儿)。改了位置就是"以后新下的存到这儿"。
    /// </summary>
    /// <returns>实际生效的目录</returns>
    public static string SetDownloadDir(string? path)
    {
        var next = NormalizeDir(path);
        _downloadDirOverride = next;      // null/非法 ⇒ 回落默认
        try { Directory.CreateDirectory(DownloadDir); } catch { /* 写入时各自 try/catch */ }
        return DownloadDir;
    }

    public static void Init()
    {
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDesk");
        // 先落到默认值: 配置还没读(SettingsStore.Load 在后面), 这段时间内任何缓存路径
        // 都必须是个可用路径 —— 不能是空串, 否则 Path.Combine("", "imgcache") 会得到相对路径,
        // 缓存就落到进程当前目录(启动方式不同还会换地方)。
        CacheRoot = DataDir;
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(VideoCacheDir);
            Directory.CreateDirectory(WebView2Dir);
        }
        catch
        {
            // 目录创建失败不应阻止启动
        }
    }

    /// <summary>
    /// 校验并规范化"用户填的目录"。返回 null 表示"这个值不可用, 应当回落默认"(空串也算)。
    ///
    /// ★ 校验必须做, 不能把用户输入原样拿去 Path.Combine: 非法路径会在下游
    ///   "创建目录/写文件"那一层抛异常, 而那里大多在后台线程与 try/catch 里 ——
    ///   表现为"封面全都不显示 / 下载一直失败", 很难定位到是路径设置写错了。
    ///
    /// ★ 同时供 <see cref="NormalizeCacheRoot"/>(内部缓存)与下载目录共用 —— 校验规则完全一样,
    ///   没必要写两份, 更不该让两者在某天悄悄分叉。
    /// </summary>
    public static string? NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            // 必须是**绝对路径**: 相对路径的含义随"进程当前目录"变, 同一个配置
            // 从快捷方式启动和从命令行启动会指向不同地方。
            if (!Path.IsPathRooted(full)) return null;
            // 盘符不存在的路径(如已拔掉的 U 盘)会在这里被判掉(驱动器就绪才认)。
            if (!Directory.Exists(full)) return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>校验缓存根目录(见 <see cref="NormalizeDir"/>); 保留旧名是因为外部在用</summary>
    public static string? NormalizeCacheRoot(string? path) => NormalizeDir(path);

    /// <summary>
    /// 把内部缓存根目录复位到默认(= <see cref="DataDir"/>)并建好子目录。
    ///
    /// ★ 2026-10-11 起缓存根**不再由用户设置驱动**, 所以这里没有 migrate 参数、
    ///   也不再搬文件 —— 以前"用户改设置 → 搬旧缓存"的整套逻辑(见旧 MigrateCache)已删除。
    ///   保留这个方法是为了给启动流程一个"确保缓存目录就位"的单一入口。
    /// </summary>
    /// <returns>实际生效的根目录</returns>
    public static string ResetCacheRoot()
    {
        CacheRoot = DefaultCacheRoot;
        EnsureCacheDirs();
        return CacheRoot;
    }

    private static void EnsureCacheDirs()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(VideoCacheDir);
        }
        catch
        {
            // 创建失败不致命: 下游写缓存时会各自 try/catch, 顶多是缓存功能不生效
        }
    }
    // ★ 旧版 MigrateCache("用户改缓存位置就把 imgcache/vcache 搬过去")已随
    //   "缓存位置改为管下载目录"一起删除 —— 内部缓存不再由用户设置驱动, 没有"换位置"这回事。
    //   而且下载目录下的视频是**用户成品**, 那种自动搬运本来就不该做。
}
