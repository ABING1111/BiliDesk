using System;
using System.Collections.Generic;
using System.Text;

namespace BiliDesk.Utils;

/// <summary>正文里切出来的一段: 普通文字, 或一条可点击的链接。</summary>
public readonly record struct LinkSegment(string Text, bool IsLink, string Url);

/// <summary>
/// 纯文本里的链接扫描(评论 / 简介 / 图文动态正文共用)。
///
/// ★ 为什么要自己扫: B 站接口给的正文全是**纯文本**, 链接没有任何结构化字段,
///   要把它变成可点的入口就只能从字符串里认。
///
/// ★ 认哪些形状(来自真实评论区的观察):
///   · <c>https://…</c> / <c>http://…</c>
///   · <c>//www.bilibili.com/…</c>(网页复制常见, 没有协议头)
///   · <c>www.example.com/…</c>
///   · 不带协议也不带 www 的 B 站域名: <c>b23.tv/xxx</c>、<c>bilibili.com/video/BV…</c>、
///     <c>space.bilibili.com/123</c> —— 只认这几个域名, 否则"文件.txt"之类的普通文字会被误认。
///
/// ★ 边界规则(比正则更好控):
///   · 遇到空白 / 中文 / 全角标点 / emoji / 引号 / 尖括号 / 方括号就截断;
///   · ASCII 圆括号**允许出现在链接里**(维基那种 <c>Foo_(bar)</c>), 末尾不配对时再剪掉;
///   · 末尾的句号/逗号/感叹号等标点一律剪掉("看这个 https://x.com。" 不该把句号算进链接)。
///
/// ★ 只认 http/https: javascript:/ftp:/file: 这类一律不匹配, 渲染层也就不会给出可点入口。
/// </summary>
public static class LinkParser
{
    /// <summary>不带协议也认的域名(只放 B 站自己的, 避免误伤普通英文句子)。</summary>
    private static readonly string[] BareHosts =
    {
        "bilibili.com",
        "b23.tv",
        "bili2233.cn",
    };

    /// <summary>
    /// 把一段文本切成"普通文字 / 链接"若干段(相邻普通文字已合并)。
    /// 没有链接时返回**一个**普通段; 空串返回空列表。
    /// </summary>
    public static List<LinkSegment> Split(string? text)
    {
        var result = new List<LinkSegment>();
        if (string.IsNullOrEmpty(text)) return result;

        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (TryMatchAt(text, i, out var end, out var url))
            {
                if (plain.Length > 0)
                {
                    result.Add(new LinkSegment(plain.ToString(), false, ""));
                    plain.Clear();
                }
                result.Add(new LinkSegment(text[i..end], true, url));
                i = end;
                continue;
            }

            plain.Append(text[i]);
            i++;
        }

        if (plain.Length > 0) result.Add(new LinkSegment(plain.ToString(), false, ""));
        return result;
    }

    /// <summary>
    /// 便宜的前置判断: 这段文本**可能**含链接吗?
    ///
    /// ★ 为什么需要: 评论区一屏几十条、无限加载, 每条都跑一遍 <see cref="Split"/>
    ///   会白分配一堆 StringBuilder。绝大多数评论根本没有链接, 先用几次 Contains
    ///   把它们挡掉, 真正跑 Split 的只剩少数。
    /// ★ 宁可多放行: 这里只是"可能", 判断错(漏掉)会让链接显示成普通文字,
    ///   所以认的线索尽量宽(任何 "//" 都算)。
    /// </summary>
    public static bool MightContainLink(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains("//", StringComparison.Ordinal)) return true;   // 含 ://
        if (text.Contains("www.", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var host in BareHosts)
            if (text.Contains(host, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>整串就是一个链接时取出归一化地址(给"粘贴链接"这类场景用)。</summary>
    public static bool TryGetUrl(string? text, out string url)
    {
        url = "";
        var segments = Split(text);
        if (segments.Count != 1 || !segments[0].IsLink) return false;
        url = segments[0].Url;
        return true;
    }

    // ------------------------------------------------------------ 匹配

    /// <summary>
    /// 从 <paramref name="start"/> 开始尝试匹配一条链接。
    /// 成功时 <paramref name="end"/> 是链接在原文本里的结束位置(不含), <paramref name="url"/> 已归一化。
    /// </summary>
    private static bool TryMatchAt(string text, int start, out int end, out string url)
    {
        end = start;
        url = "";

        int hostStart;
        if (MatchPrefix(text, start, "https://", out hostStart)
            || MatchPrefix(text, start, "http://", out hostStart)
            || MatchToken(text, start, "//", out hostStart))
        {
            // 协议之后紧跟主机名
        }
        else if (MatchToken(text, start, "www.", out hostStart))
        {
            hostStart = start;   // www. 属于主机名的一部分
        }
        else if (IsBareHostStart(text, start))
        {
            hostStart = start;
        }
        else
        {
            return false;
        }

        // 往后扫到第一个"不可能属于链接"的字符
        var bodyEnd = hostStart;
        while (bodyEnd < text.Length && !IsUrlTerminator(text[bodyEnd])) bodyEnd++;

        bodyEnd = TrimTrailing(text, start, bodyEnd);
        if (bodyEnd <= hostStart) return false;

        var host = ExtractHost(text, hostStart, bodyEnd);
        if (!IsValidHost(host)) return false;

        var raw = text[start..bodyEnd];
        url = Normalize(raw);
        end = bodyEnd;
        return true;
    }

    /// <summary>
    /// 带协议的前缀: 不区分大小写, 且前面不能紧贴**ASCII** 字母/数字(避免 "abchttp://" 被认成链接)。
    ///
    /// ★ 判据只认 ASCII: 中文是 Unicode 字母, 用 <c>char.IsLetterOrDigit</c> 会把
    ///   "链接https://…"(中文后面直接跟地址, 没空格)整条挡掉 —— 那是评论区的常见写法。
    /// </summary>
    private static bool MatchPrefix(string text, int start, string prefix, out int after)
    {
        after = start;
        if (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1]) || text[start - 1] == '@')) return false;
        if (!StartsWith(text, start, prefix, StringComparison.OrdinalIgnoreCase)) return false;
        after = start + prefix.Length;
        return true;
    }

    /// <summary>短前缀(<c>//</c> / <c>www.</c>): 同样要求前面不是 ASCII 单词字符。</summary>
    private static bool MatchToken(string text, int start, string prefix, out int after)
    {
        after = start;
        if (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1])
                          || text[start - 1] is '.' or '_' or '-' or '@' or '/' or ':'))
            return false;
        if (!StartsWith(text, start, prefix, StringComparison.OrdinalIgnoreCase)) return false;
        after = start + prefix.Length;
        return true;
    }

    private static bool StartsWith(string text, int start, string value, StringComparison comparison)
    {
        if (start < 0 || start + value.Length > text.Length) return false;
        return string.Compare(text, start, value, 0, value.Length, comparison) == 0;
    }

    /// <summary>
    /// 这个位置是不是一个"裸域名"的开头(B 站域名, 不带协议/不带 www)。
    /// 要求前一个字符不是单词字符, 否则 "mybilibili.com" 也会被认出来。
    /// </summary>
    private static bool IsBareHostStart(string text, int start)
    {
        if (start > 0)
        {
            var prev = text[start - 1];
            // ★ 只挡 ASCII 单词字符: 中文后面直接跟域名("看bilibili.com")是常见写法。
            if (char.IsAsciiLetterOrDigit(prev) || prev is '.' or '_' or '-' or '@' or '/' or ':') return false;
        }

        var host = ReadHost(text, start);
        if (host.Length == 0) return false;

        var lower = host.ToLowerInvariant();
        foreach (var bare in BareHosts)
        {
            if (lower == bare) return true;
            if (lower.EndsWith("." + bare, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>读取一段主机名字符(字母/数字/点/连字符), 遇到别的字符就停。</summary>
    private static string ReadHost(string text, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-')) break;
            i++;
        }
        return text[start..i];
    }

    // ------------------------------------------------------------ 边界

    /// <summary>
    /// 这个字符是否"一定不属于链接"。
    ///
    /// ★ ASCII 圆括号**不算**终止符 —— 它们可能成对出现在链接里(维基条目 <c>Foo_(bar)</c>),
    ///   末尾不配对的由 <see cref="TrimTrailing"/> 剪掉。
    /// ★ 方括号/花括号**算**终止符: 链接里几乎不会出现, 而 B 站表情短代码就是
    ///   <c>[doge]</c> 这种形状 —— 不截断的话 "https://b23.tv/abc[doge]" 会把表情吞进链接,
    ///   表情图片消失、链接还变成坏的。
    /// ★ 中文/全角/emoji 一律截断: 链接里出现中文的概率极低, 而"链接后面紧跟一句中文"
    ///   是评论区的常态, 不截断就会把整句中文都染成链接。
    /// </summary>
    private static bool IsUrlTerminator(char c)
    {
        if (char.IsWhiteSpace(c) || char.IsControl(c)) return true;

        if (c < 0x80)
        {
            return c switch
            {
                '<' or '>' or '"' or '\'' or '`' or '\\' or '^' or '{' or '}' or '|' or '*'
                    or '[' or ']' => true,
                _ => false,
            };
        }

        if (char.IsHighSurrogate(c)) return true;          // emoji / 增补平面字符
        if (c is >= (char)0x3000 and <= (char)0x303F) return true;   // 中文标点
        if (c is >= (char)0x3040 and <= (char)0x30FF) return true;   // 假名
        if (c is >= (char)0x3400 and <= (char)0x4DBF) return true;   // 扩展 A
        if (c is >= (char)0x4E00 and <= (char)0x9FFF) return true;   // 常用汉字
        if (c is >= (char)0xF900 and <= (char)0xFAFF) return true;   // 兼容汉字
        if (c is >= (char)0xFE30 and <= (char)0xFE4F) return true;   // 竖排标点
        if (c is >= (char)0xFF00 and <= (char)0xFFEF) return true;   // 全角字符
        return false;
    }

    /// <summary>剪掉链接末尾的标点, 以及**不配对**的右括号。</summary>
    private static int TrimTrailing(string text, int start, int end)
    {
        while (end > start)
        {
            var c = text[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?'
                or '。' or '，' or '、' or '；' or '：' or '！' or '？' or '…')
            {
                end--;
                continue;
            }
            if (c is ')' or ']' or '}')
            {
                var open = c switch { ')' => '(', ']' => '[', _ => '{' };
                if (Count(text, start, end, open) < Count(text, start, end, c))
                {
                    end--;
                    continue;
                }
            }
            break;
        }
        return end;
    }

    private static int Count(string text, int start, int end, char ch)
    {
        var n = 0;
        for (var i = start; i < end; i++)
            if (text[i] == ch) n++;
        return n;
    }

    // ------------------------------------------------------------ 主机名

    /// <summary>取主机名部分(到 / ? # : 为止), 不含协议与路径。</summary>
    private static string ExtractHost(string text, int hostStart, int bodyEnd)
    {
        var i = hostStart;
        while (i < bodyEnd)
        {
            var c = text[i];
            if (c is '/' or '?' or '#' or ':') break;
            i++;
        }
        return text[hostStart..i];
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length == 0) return false;
        if (host[0] == '.' || host[^1] == '.') return false;
        if (!host.Contains('.', StringComparison.Ordinal)) return false;
        if (host.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var c in host)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-'))
                return false;
        return true;
    }

    /// <summary>补全协议: 裸域名与 // 开头的地址统一走 https。</summary>
    private static string Normalize(string raw)
    {
        if (raw.StartsWith("//", StringComparison.Ordinal)) return "https:" + raw;
        if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return raw;
        return "https://" + raw;
    }
}
