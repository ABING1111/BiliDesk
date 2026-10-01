using System;
using System.Security.Cryptography;
using System.Text;

namespace BiliDesk.Helpers;

/// <summary>
/// 哈希工具。
///
/// 只有一行的实现, 但它出现在三个互不相关的地方: 封面缓存的键名(CoverLoader)、
/// 图片下载的缓存命中判断(ImageDownloader)、以及 WBI 签名(ApiClient)。
/// 三处各写一遍的下场是"哪天要换算法得先想起来有几份" —— 集中一份。
/// </summary>
public static class Hashing
{
    /// <summary>
    /// MD5 的小写十六进制串。
    ///
    /// ★ **不是用于安全** —— 只是拿 URL/参数当缓存键与签名串, 与 B 站服务端的约定一致,
    /// 换算法会直接导致签名不通过、缓存全部失效。别"顺手升级"成 SHA256。
    /// </summary>
    public static string Md5Hex(string s)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
