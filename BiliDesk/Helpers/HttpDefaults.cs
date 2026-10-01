namespace BiliDesk.Helpers;

/// <summary>
/// 所有出站 HTTP 请求共用的默认值。
///
/// ★ 为什么 UA 必须收敛成一份:
///   B 站按 UA 判定请求是不是"浏览器客户端", **带上 `Edg/` 之类品牌后缀会被判成非浏览器**
///   并直接回 -352(评论接口实测)。所以这个字符串是全局一致的硬约束 ——
///   分散在 7 个文件里各写一遍时, 只要有人只改了其中一处, 就会出现
///   "封面能显示、评论却加载不出来"这种极难定位的现象。
/// </summary>
public static class HttpDefaults
{
    /// <summary>纯 Chrome UA —— 见上方注释, **不要**追加任何品牌后缀。</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
}
