using System;
using System.Text.RegularExpressions;

namespace BiliDesk.Utils;

/// <summary>B 站 URL 解析器: 提取 bvid / uid / keyword 等参数</summary>
public static class UrlParser
{
    private static readonly Regex BvidRegex = new(@"/(BV[0-9A-Za-z]{10})", RegexOptions.Compiled);
    private static readonly Regex UidRegex = new(@"/space/(\d+)", RegexOptions.Compiled);

    // ---------------------------------------------------------------- av ↔ bv

    // 官方算法常量(见 bilibili-API-collect 的 bvid 说明)。字符表与两份映射表都是固定值,
    // 不要"顺手优化"成别的写法 —— 它跟服务端是一一对应的, 差一个字符就换出一串不存在的 BV 号。
    private const long BvXorCode = 23442827791579;
    private const long BvMaskCode = 2251799813685247;   // 2^51 - 1
    private const long BvMaxAid = 1L << 51;
    private const string BvAlphabet = "FcwAPNKTMug3GV5Lj7EJnHpWsx4tb8haYeviqBz6rkCy12mUSDQX9RdoZf";
    private static readonly int[] BvEncodeMap = { 8, 7, 0, 5, 1, 3, 2, 4, 6 };

    /// <summary>
    /// avid → bvid。
    ///
    /// 为什么需要它: **App 端推荐流(app.bilibili.com/x/v2/feed/index)只给 avid, 不给 bvid**
    /// (items[].param / player_args.aid), 而本应用从播放、收藏到历史全链路都用 bvid。
    /// 走接口换算要多发一次请求(item 数 × 1 次), 不划算, 所以本地算。
    /// 算法正确性已用热门 + 排行榜共 120 条真实数据双向校验(2026-09-27)。
    /// </summary>
    public static string AvToBv(long aid)
    {
        if (aid <= 0) return "";
        var body = new char[9];
        var tmp = (BvMaxAid | aid) ^ BvXorCode;
        for (var i = 0; i < BvEncodeMap.Length; i++)
        {
            body[BvEncodeMap[i]] = BvAlphabet[(int)(tmp % 58)];
            tmp /= 58;
        }
        return "BV1" + new string(body);
    }

    /// <summary>bvid → avid(反向换算, 目前只用于校验/兜底)</summary>
    public static long BvToAv(string? bvid)
    {
        if (string.IsNullOrEmpty(bvid) || bvid.Length < 12 || !bvid.StartsWith("BV1", StringComparison.Ordinal))
            return 0;
        var body = bvid[3..];
        long tmp = 0;
        for (var i = BvEncodeMap.Length - 1; i >= 0; i--)
        {
            var idx = BvAlphabet.IndexOf(body[BvEncodeMap[i]]);
            if (idx < 0) return 0;
            tmp = tmp * 58 + idx;
        }
        return (tmp & BvMaskCode) ^ BvXorCode;
    }

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

    /// <summary>提取 UP 主 uid(space.bilibili.com/{mid})</summary>
    public static string? ExtractUid(string urlOrUri)
    {
        if (!TryParseUri(urlOrUri, out var uri)) return null;
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