using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BiliDesk.Helpers;

/// <summary>
/// 弹幕关键词黑名单。
///
/// 语法(每行一条, 也支持用英文/中文逗号分隔):
///   - 普通词:   "剧透"        → 只要弹幕文本包含这个子串就丢弃
///   - 正则:     "re:^\d+$"    → 以 "re:" 开头, 后面按正则匹配
///
/// 为什么要支持正则:
/// B 站弹幕里最常见的刷屏是"日期打卡"(2026/9/20、9-20、20260920……)
/// 和"纯数字/纯时间", 这类没法用有限个关键词穷举, 用正则一条搞定。
///
/// 为什么编译成 Regex 而不是每次 Contains:
/// 一个热门视频能拉几千条弹幕, 每条都要过一遍规则。Regex 的构造与解析开销不小,
/// 所以这里在"规则集合构建时"一次性编译好(Compiled), 匹配阶段没有任何额外分配。
/// </summary>
public sealed class DanmakuKeywordFilter
{
    private readonly List<string> _plain;
    private readonly List<Regex> _regex;

    private DanmakuKeywordFilter(List<string> plain, List<Regex> regex)
    {
        _plain = plain;
        _regex = regex;
    }

    /// <summary>规则条数(供界面显示"已启用 N 条屏蔽规则")</summary>
    public int Count => _plain.Count + _regex.Count;

    /// <summary>无规则时返回 null, 让调用方可以整个跳过匹配流程</summary>
    public static DanmakuKeywordFilter? Build(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var plain = new List<string>();
        var regex = new List<Regex>();

        // 换行 / 中英文逗号 / 分号 都当分隔符
        var parts = raw.Split(
            new[] { '\n', '\r', ',', '，', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var p in parts)
        {
            var s = p.Trim();
            if (s.Length == 0) continue;

            if (s.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = s.Substring(3).Trim();
                if (pattern.Length == 0) continue;
                try
                {
                    // 编译 + 1 秒超时: 防止用户写出灾难性回溯的正则把播放线程卡死
                    regex.Add(new Regex(pattern,
                        RegexOptions.Compiled | RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(1)));
                }
                catch (ArgumentException)
                {
                    // 正则语法错误: 忽略这一条, 不影响其它规则(用户可能还在输入中)
                }
            }
            else
            {
                plain.Add(s);
            }
        }

        if (plain.Count == 0 && regex.Count == 0) return null;
        return new DanmakuKeywordFilter(plain, regex);
    }

    /// <summary>弹幕文本是否命中黑名单</summary>
    public bool IsBlocked(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var p in _plain)
        {
            if (text.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }

        for (var i = 0; i < _regex.Count; i++)
        {
            try
            {
                if (_regex[i].IsMatch(text)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                // 超时当作不匹配: 宁可漏掉一条弹幕, 也不能让播放卡住
            }
        }

        return false;
    }
}
