using System;
using System.Collections.Generic;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 动态页「页内搜索」的匹配规则(纯函数, 2026-11-25, issue #127)。
///
/// 搜索范围 = 卡片上能看到的文字:
///   · <see cref="DynamicItem.Title"/>(视频 / 专栏 / 图文标题)、<see cref="DynamicItem.ContentText"/>(动态正文)、
///   · <see cref="DynamicItem.AuthorName"/>(UP 主昵称);
///   · 转发的动态连**原动态**的标题与正文一起看 —— 转发卡上显示的就是原动态内容,
///     只匹配外层等于"看得见的字搜不到"。
///
/// ★ 多个词(空格/逗号分隔, 与全局屏蔽同一套分词)要求**全部命中**: 搜索是缩小范围,
///   与屏蔽关键词的"命中任意一个就屏蔽"正好相反。
/// ★ 大小写不敏感, 与 <see cref="FilterService"/> 的屏蔽匹配同一口径 —— 同一个词,
///   在屏蔽里能命中, 在搜索里也该能命中。
/// ★ 只做**已加载内容**的筛选: 动态流是游标分页, 应用没有"搜索关注动态"的服务端接口
///   (那等于扫所有关注的动态)。没匹配到时会提示"可继续加载更多动态"。
/// </summary>
public static class FeedSearchPolicy
{
    /// <summary>把搜索框文本拆成词条。空/全空白 = 不过滤(返回空数组)。</summary>
    public static string[] SplitTerms(string? keyword) => FilterKeywords.Split(keyword);

    /// <summary><paramref name="item"/> 是否命中全部词条(空词条 = 恒命中)。</summary>
    public static bool Matches(DynamicItem? item, IReadOnlyList<string> terms)
    {
        if (terms == null || terms.Count == 0) return true;
        if (item == null) return false;

        foreach (var term in terms)
            if (!MatchesTerm(item, term, depth: 0))
                return false;
        return true;
    }

    /// <summary>
    /// 给 <see cref="FilterService.Attach"/> 的 extra 谓词用: 非 <see cref="DynamicItem"/>
    /// (探针/别的列表误挂)一律放行, 不掺和别人的列表。
    /// </summary>
    public static bool Matches(object? item, IReadOnlyList<string> terms)
        => item is not DynamicItem dynamicItem || Matches(dynamicItem, terms);

    /// <summary>
    /// 单个词是否出现在这条动态(或它转发的原动态)的标题 / 正文 / UP 昵称里。
    /// <paramref name="depth"/> 只允许两层: 转发里再嵌转发时按原动态的标题/正文算,
    /// 不再无限递归(接口理论上只有一层)。
    /// </summary>
    private static bool MatchesTerm(DynamicItem item, string term, int depth)
    {
        if (Contains(item.Title, term)) return true;
        if (Contains(item.ContentText, term)) return true;
        if (Contains(item.AuthorName, term)) return true;
        if (depth < 2 && item.Orig is { } orig) return MatchesTerm(orig, term, depth + 1);
        return false;
    }

    private static bool Contains(string? text, string term)
        => !string.IsNullOrEmpty(text) && text.Contains(term, StringComparison.OrdinalIgnoreCase);
}
