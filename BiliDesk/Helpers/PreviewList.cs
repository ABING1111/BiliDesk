using System;
using System.Collections.Generic;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 图片预览窗的"整组地址"预处理: 去掉空/无效地址, 并把"用户点的下标"换算到过滤后的位置。
///
/// ★ 为什么单独抽成纯函数(2026-11-14):
///   动态/评论/私信交给预览窗的都是"业务列表", 里面可能混着空地址
///   (接口偶尔给空 img_src、或者本地拼出来的默认空串)。过滤之后**下标会前移**,
///   这一步算错的表现是"点了第 3 张, 打开的却是第 2 张"—— 肉眼极难发现,
///   而它又完全不依赖 WPF, 所以放在这里用单测钉住。
/// </summary>
public static class PreviewList
{
    /// <summary>
    /// 过滤 + 换算。
    /// </summary>
    /// <param name="urls">原始地址列表(可含 null/空串)</param>
    /// <param name="index">用户在**原始列表**里点的那个下标</param>
    /// <returns>
    /// Urls = 已 Normalize 的非空地址(保持原顺序);
    /// Index = 同一个元素在 Urls 里的位置; 列表为空时恒为 0。
    /// </returns>
    public static (string[] Urls, int Index) Normalize(IReadOnlyList<string?>? urls, int index)
    {
        if (urls == null || urls.Count == 0) return (Array.Empty<string>(), 0);

        var clean = new List<string>(urls.Count);
        var target = 0;
        for (var i = 0; i < urls.Count; i++)
        {
            var n = UrlUtil.Normalize(urls[i]);
            if (n.Length == 0) continue;
            // 目标之前的有效项有几个, 换算后的下标就是几 —— 目标自己那一项在这里先不加
            if (i < index) target++;
            clean.Add(n);
        }

        if (clean.Count == 0) return (Array.Empty<string>(), 0);

        // 目标本身无效(或被夹到边界外)时不能越界: 落在"它之前那些有效项"的末尾附近。
        // ★ 注意这里**不再** +1: target 表示"目标之前的有效项数", 正好就是
        //   目标应当落在的下标; 目标无效时它等于"紧随其后的那一项", 也是合理的落点。
        return (clean.ToArray(), Math.Clamp(target, 0, clean.Count - 1));
    }
}
