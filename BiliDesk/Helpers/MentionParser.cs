using System;
using System.Collections.Generic;

namespace BiliDesk.Helpers;

/// <summary>
/// 评论正文里的 <c>@昵称</c> 扫描。
///
/// ★ 为什么需要: B 站接口的 <c>content.message</c> 是**纯文本** ——
///   表情另给 <c>content.emote</c> 字典, 而"回复 @某某"里的那个某某**只是文本**,
///   没有结构化字段可用。B 站网页版是把它染成蓝色, 我们要同样的观感,
///   就只能自己扫一遍文本。
///
/// ★ 规则(照 B 站网页版):
///   · <c>@</c> 后面跟一段**非空白**字符, 到下一个空白/标点为止;
///   · 昵称里可能有中文、字母、数字、点、下划线、减号, 所以不能只认"单词字符";
///   · 长度上限 20: 超长的多半是误匹配。
///
/// ★★ 邮箱要排除(2026-10-30 探针实测踩到):
///   "someone@example.com" 里那个 @ 会被扫成整串 "someone@example.com"(18 字, 在 20 上限内),
///   光看长度拦不住。判据是 **@ 的前一个字符是字母/数字/下划线** ——
///   邮箱的本地部分后面紧跟 @; 而真人 @ 别人时 @ 前面要么是空白/标点, 要么就是开头。
/// </summary>
public static class MentionParser
{
    /// <summary>昵称最大长度</summary>
    private const int MaxNameLength = 20;

    /// <summary>
    /// 扫出正文里 @ 到的昵称(按出现顺序, 可重复)。
    /// </summary>
    public static IEnumerable<string> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content)) yield break;

        var i = 0;
        while (i < content.Length)
        {
            var at = content.IndexOf('@', i);
            if (at < 0) yield break;

            var end = at + 1;
            while (end < content.Length && !IsSeparator(content[end])) end++;

            var name = content[(at + 1)..end];
            if (name.Length > 0 && name.Length <= MaxNameLength
                && !name.Contains('@', StringComparison.Ordinal)
                && !IsEmailPart(content, at))
                yield return name;

            // ★ 从 @ 本身继续找, 不从 name 末尾继续 ——
            //   否则 "@a @b" 里 @a 后面那个空白之后的 @b 会被跳过。
            i = at + 1;
        }
    }

    /// <summary>
    /// 这个 @ 是不是**邮箱的一部分**(不是真的 @ 了一个人)。
    ///
    /// 判据: @ 的**前一个字符是字母/数字/下划线** —— 邮箱的本地部分后面紧跟 @,
    /// 而真人 @ 别人时 @ 前面要么是空白/标点, 要么就是开头。
    /// </summary>
    private static bool IsEmailPart(string content, int at)
    {
        if (at <= 0) return false;
        var prev = content[at - 1];
        return char.IsLetterOrDigit(prev) || prev == '_';
    }

    /// <summary>
    /// 昵称段的结束字符(分隔符)。
    /// ★ 包含全角标点 —— 正文里的"@某人，"那个逗号一定是分隔符, 所以全角逗号/句号都要算。
    /// ★ 但**不含 @**: 邮箱那一条由 <see cref="IsEmailPart"/> 单独处理。
    /// </summary>
    private static bool IsSeparator(char ch)
    {
        if (char.IsWhiteSpace(ch)) return true;
        return ch switch
        {
            ',' or '，' or '.' or '。' or ';' or '；' or ':' or '：'
                or '!' or '！' or '?' or '？' or '(' or ')' or '（' or '）'
                or '[' or ']' or '【' or '】' or '"' or '“' or '”' or '\'' or '’' => true,
            _ => false
        };
    }

    /// <summary>
    /// 把正文按"@提及"切成若干段, 供渲染时逐段上色。
    /// 返回的每一项: <c>Text</c> 是文本, <c>IsMention</c> 标记它是不是一次 @提及。
    ///
    /// ★ 提及段**含 @ 号本身**(即 "@雨霁影清" 整段都是提及段) ——
    ///   B 站网页版就是把 @ 和昵称一起染蓝的, 分开反而会出现"@ 是白的、名字是蓝的"。
    ///
    /// ★ 为什么要切成段而不是"整段染色": 昵称必须**只**染昵称那几个字,
    ///   "@雨霁影清 你说得对" 里该蓝的是"雨霁影清", 不是整句。
    /// </summary>
    public static List<(string Text, bool IsMention)> Split(string? content)
    {
        var result = new List<(string, bool)>();
        if (string.IsNullOrEmpty(content)) return result;

        var i = 0;
        var plainStart = 0;
        while (i < content.Length)
        {
            var at = content.IndexOf('@', i);
            if (at < 0) break;

            var end = at + 1;
            while (end < content.Length && !IsSeparator(content[end])) end++;

            var nameLen = end - at - 1;
            var nameText = content[(at + 1)..end];
            if (nameLen == 0 || nameLen > MaxNameLength
                || nameText.Contains('@', StringComparison.Ordinal)
                || IsEmailPart(content, at))
            {
                // 不是一次 @提及: 继续往后找
                i = at + 1;
                continue;
            }

            // @ 之前的那一段普通文本
            if (at > plainStart)
                result.Add((content[plainStart..at], false));
            // 提及段: 含 @ 号本身
            result.Add((content[at..end], true));

            i = end;
            plainStart = end;
        }

        if (plainStart < content.Length)
            result.Add((content[plainStart..], false));

        return result;
    }
}
