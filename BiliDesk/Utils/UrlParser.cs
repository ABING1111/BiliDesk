using System;
using System.Text.RegularExpressions;

namespace BiliDesk.Utils;

/// <summary>B 站 URL 解析器: 提取 bvid / uid / keyword 等参数</summary>
public static class UrlParser
{
    private static readonly Regex BvidRegex = new(@"/(BV[0-9A-Za-z]{10})", RegexOptions.Compiled);
    // 老版空间地址: 路径里带 /space/{mid}(www.bilibili.com/space/12345 这类)
    private static readonly Regex UidRegex = new(@"/space/(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// 把"可能是完整 URL、也可能是不带协议的裸路径"的串解析成 <see cref="Uri"/>。
    ///
    /// 三个提取方法(视频/UP/关键词)面对的是同一批输入 —— 剪贴板里粘来的、网页版复制的、
    /// 只带了 `//` 的 —— 所以容错规则只该有一份, 否则迟早出现"BV 号能提取、关键词却提取不到"。
    /// 解析不出来就让调用方返回 null(降级), 不抛异常。
    /// </summary>
    private static bool TryParseUri(string urlOrUri, out Uri uri)
    {
        uri = null!;
        try
        {
            uri = urlOrUri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Uri(urlOrUri) : new Uri("https:" + urlOrUri);
            return true;
        }
        catch
        {
            // 空串 / 只给了相对路径 / 协议名非法 —— 都算"不是链接", 交给调用方降级
            return false;
        }
    }

    /// <summary>提取视频 BV 号</summary>
    public static string? ExtractBvid(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        var m = BvidRegex.Match(uri.AbsolutePath);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>提取 UP 主 uid</summary>
    public static string? ExtractUid(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        var host = uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase);
        if (host)
        {
            // 现行主页地址: space.bilibili.com/{mid}(AbsolutePath 就是 "/{mid}")
            // 2026-11-07 修: 原来只有 /space/{mid} 一条正则, 现行域名下的地址永远提取不到 ——
            // HandleUrl(重新接线后的统一入口)对空间链接会全部失灵。
            var seg = uri.AbsolutePath.Trim('/');
            var head = seg.Split('/')[0];
            return long.TryParse(head, out _) && head.Length > 0 ? head : null;
        }
        // 老式地址: 路径里带 /space/{mid}(如 www.bilibili.com/space/12345)
        var m = UidRegex.Match(uri.AbsolutePath);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>从 /search?... 中提取 keyword(手动解析, 不依赖 System.Web)</summary>
    public static string? ExtractKeyword(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
        try
        {
            var q = uri.Query.TrimStart('?');
            foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = Uri.UnescapeDataString(pair[..eq]);
                if (key != "keyword") continue;
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return null;
        }
        catch
        {
            // 查询串里的转义是坏的(非法百分号编码): 当作"没提取到关键词", 让调用方走普通搜索
            return null;
        }
    }
}