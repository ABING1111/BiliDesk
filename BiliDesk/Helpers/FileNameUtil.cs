using System;
using System.IO;
using System.Text;

namespace BiliDesk.Helpers;

/// <summary>
/// 面向用户的下载文件名规则。
///
/// 为什么和 CoverLoader 的命名不同: 缓存是"给界面喂图", 按 MD5 存、文件名对用户无意义;
/// 下载是面向用户的, 要可读、要避开重名、要落在用户找得到的地方。
/// 但"图片下载"和"视频下载"面对的是**同一套**规则(sanitize → 截长 → 查重加序号),
/// 所以规则本身只该有一份。
/// </summary>
public static class FileNameUtil
{
    /// <summary>标题过长时的截断长度(留着扩展名, 避免路径超长)</summary>
    private const int MaxBaseNameLength = 80;

    /// <summary>重名后缀 " (n)" 的上限, 纯防御: 正常不会走到</summary>
    private const int UniqueSuffixLimit = 999;

    /// <summary>
    /// 清理成合法文件名: 非法字符换成 '_', 去掉首尾空白, 超长截断。
    /// 清完变成空串时(标题全是非法字符的极端情况)用 <paramref name="fallback"/> 兜底。
    /// </summary>
    public static string Sanitize(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var s = sb.ToString().Trim();
        if (s.Length > MaxBaseNameLength) s = s[..MaxBaseNameLength];
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>
    /// 生成不重名的完整路径: 已存在就追加 " (1)"、" (2)"… 而不是覆盖用户的旧文件。
    /// <paramref name="baseName"/> 会先过一遍 <see cref="Sanitize"/>。
    /// 调用方负责保证目录存在。
    /// </summary>
    public static string UniquePath(string dir, string baseName, string extension, string fallback)
    {
        var safe = Sanitize(baseName, fallback);
        var candidate = Path.Combine(dir, safe + extension);
        var i = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{safe} ({i}){extension}");
            i++;
            if (i > UniqueSuffixLimit) break;
        }
        return candidate;
    }
}
