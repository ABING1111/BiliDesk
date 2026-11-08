using System.Text;

namespace BiliDesk.Helpers;

/// <summary>
/// WBI 签名里的 mixin key 计算(2026-11-13 从 ApiClient 抽出来, 纯函数可单测)。
///
/// ★ 为什么要单独抽: 算法只有两行, 但"只取前 32 位"这件事**极易写漏** ——
///   原来的实现在这里漏了截断, 后面所有签名都多出 32 个字符。大多数 wbi 接口对
///   w_rid 是软校验, 所以一直没暴露; 直播弹幕的 getDanmuInfo 严格校验, 直接 -352。
///   抽成纯函数后, 用官方文档那组已知 img/sub 就能钉死"必须是 32 位"。
/// </summary>
public static class Wbi
{
    /// <summary>官方 JS 里的 mixinKeyEncTab(固定 64 个下标)。</summary>
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52
    };

    /// <summary>
    /// 由 nav 接口返回的 img/sub 文件名算出 mixin key。
    /// ★ 先按表取 64 个字符, **再截前 32** —— 截断不是可选项, 是官方算法的一部分。
    /// </summary>
    public static string MixinKey(string img, string sub)
    {
        if (string.IsNullOrEmpty(img) || string.IsNullOrEmpty(sub)) return "";

        var raw = img + sub;
        var permuted = new StringBuilder(MixinKeyEncTab.Length);
        foreach (var index in MixinKeyEncTab)
        {
            // 文件名比表长时按官方实现会越界; 这里用 0 兜底, 让"接口给了个短名"降级成
            // 签名失败(上层会走重试/降级), 而不是直接抛异常把整次请求打断。
            permuted.Append(index < raw.Length ? raw[index] : '0');
        }
        return permuted.ToString(0, 32);
    }
}
