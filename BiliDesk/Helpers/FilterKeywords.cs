using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliDesk.Helpers;

/// <summary>
/// 「屏蔽标题含 / 屏蔽 UP 主含」关键词的**纯逻辑**(2026-11-20, issue #98)。
///
/// ★ 为什么单独拆出来: 面板从"一整串放进一个文本框"改成"标签墙逐条增删"之后,
///   分词/去重/截断/封顶这套规则被 FilterService(增删) 与 SettingsStore(读配置清洗) 两边共用。
///   各写一份必然漂移 —— 表现是"界面加了 201 个词, 重启后变成 200 个"这种说不清的差异。
/// ★ 存储格式**故意不变**: 仍然是一个空格分隔的字符串(见 <see cref="Join"/>),
///   老版本读得懂, 用户降级回去不丢配置。
/// </summary>
public static class FilterKeywords
{
    /// <summary>标题/UP 关键词各最多多少个。旧版单个文本框限 80 字, 实际只能放十几个词。</summary>
    public const int MaxTerms = 200;

    /// <summary>单个关键词的最大长度(超长直接截断, 面板里一条标签不该撑满整行)。</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// 分隔符: 与旧版单行文本框完全一致(空格 / 中英文逗号 / 顿号 / 中英文分号 / Tab)。
    /// 保留这套规则是为了让"一次贴一串词"的老习惯继续可用。
    /// </summary>
    private static readonly char[] Separators = { ' ', ',', '，', '、', ';', '；', '\t' };

    /// <summary>把一串文本拆成词条(去空、Trim)。不做去重/截断, 那些在 <see cref="Merge"/> 里做。</summary>
    public static string[] Split(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 把 <paramref name="raw"/> 里能加的新词合并到 <paramref name="existing"/> 后面:
    /// 去重(忽略大小写)、截断超长、总数封顶 <see cref="MaxTerms"/>。返回新列表, 不改入参。
    /// </summary>
    public static List<string> Merge(IReadOnlyList<string> existing, string? raw)
    {
        var list = new List<string>(existing);
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        foreach (var part in Split(raw))
        {
            var term = part.Trim();
            if (term.Length == 0) continue;
            if (term.Length > MaxLength) term = term[..MaxLength];
            if (!seen.Add(term)) continue;
            if (list.Count >= MaxTerms) break;
            list.Add(term);
        }
        return list;
    }

    /// <summary>删掉一个词(忽略大小写)。返回新列表, 不改入参。</summary>
    public static List<string> Remove(IReadOnlyList<string> existing, string? term)
    {
        var value = (term ?? "").Trim();
        if (value.Length == 0) return new List<string>(existing);
        return existing
            .Where(t => !string.Equals(t, value, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>拼回持久化用的字符串(空格分隔)。</summary>
    public static string Join(IEnumerable<string> terms) => string.Join(" ", terms);
}
