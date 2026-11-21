using System;
using System.Collections.Generic;
using System.Text;

namespace BiliDesk.Helpers;

/// <summary>发布说明里出现过的块级元素。</summary>
public enum MdBlockKind
{
    Heading,
    Bullet,
    Ordered,
    Paragraph,
    Quote,
    Code,
    Rule
}

/// <summary>一个块级元素。<see cref="Depth"/> 只对列表有意义, <see cref="Level"/> 只对标题有意义。</summary>
public sealed class MdBlock
{
    public MdBlockKind Kind { get; init; }

    /// <summary>标题层级(1~4)</summary>
    public int Level { get; init; }

    /// <summary>列表嵌套层级(0 = 顶层)</summary>
    public int Depth { get; init; }

    /// <summary>正文(列表项是去掉标记后的内容; 代码块是原样文本)</summary>
    public string Text { get; init; } = "";

    /// <summary>有序列表的序号 / 代码块的语言标记</summary>
    public string Meta { get; init; } = "";
}

/// <summary>行内元素种类。</summary>
public enum MdSpanKind
{
    Text,
    Bold,
    Code,
    Link,
    Key
}

/// <summary>一个行内片段(加粗 / 行内代码 / 链接 / 键帽 / 普通文字)。</summary>
public readonly struct MdSpan
{
    public MdSpan(MdSpanKind kind, string text, string url = "")
    {
        Kind = kind;
        Text = text;
        Url = url;
    }

    public MdSpanKind Kind { get; }
    public string Text { get; }
    public string Url { get; }
}

/// <summary>
/// 发布说明用的 **Markdown 子集** 解析器。
///
/// ★ 为什么不引库: 需要的只是"把 GitHub Release 的 body 读成人话"这几条规则 ——
///   标题 / 列表 / 加粗 / 行内代码 / 链接 / 引用 / 代码块 / 分隔线。
///   一个通用渲染库会给弹窗与关于页拖进整套主题化、图片、表格、HTML 复杂度,
///   而发布说明里这些从来不用(表与图片为零)。规则少, 就能把边界写成单测。
/// ★ 不追求规范: 只认"行首标记", 不做 setext 标题、引用块嵌套、任务列表。
///   ★ 未知 HTML 标签一律剥掉、保留内文 —— 发布说明里偶尔出现 &lt;kbd&gt;,
///   其余标签(如旧的 &lt;details&gt;)按纯文字显示, 总好过把尖括号原样丢给用户。
/// </summary>
public static class MarkdownLite
{
    /// <summary>列表最多缩进到第 3 层(发布说明最深只到 2 层, 再深就只是排版噪音)。</summary>
    private const int MaxListDepth = 3;

    /// <summary>把 Markdown 切成块。<paramref name="markdown"/> 为空时返回空列表。</summary>
    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<MdBlock>();
        return ParseBlocks(markdown);
    }

    private static IReadOnlyList<MdBlock> ParseBlocks(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var quote = new List<string>();
        var code = new StringBuilder();
        var codeLang = "";
        var inFence = false;
        var fenceOpened = false;

        // 逐行扫, 块级标记之间用"先收束上一段"的方式衔接(与 Markdown 的块边界一致)。
        // ★ 用本地函数往同一个 List 里攒, 是为了让"收束上一段"只写一次。
        var result = new List<MdBlock>();

        void FlushParagraphToResult()
        {
            if (paragraph.Count == 0) return;
            result.Add(new MdBlock { Kind = MdBlockKind.Paragraph, Text = string.Join(" ", paragraph) });
            paragraph.Clear();
        }

        void FlushQuoteToResult()
        {
            if (quote.Count == 0) return;
            result.Add(new MdBlock { Kind = MdBlockKind.Quote, Text = string.Join(" ", quote) });
            quote.Clear();
        }

        void FlushAll()
        {
            FlushParagraphToResult();
            FlushQuoteToResult();
        }

        void FlushFence()
        {
            if (!fenceOpened) return;
            var text = code.ToString().TrimEnd('\n');
            if (text.Length > 0)
                result.Add(new MdBlock { Kind = MdBlockKind.Code, Text = text, Meta = codeLang });
            code.Clear();
            codeLang = "";
            fenceOpened = false;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();

            if (inFence)
            {
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = false;
                    FlushFence();
                }
                else
                {
                    code.Append(line).Append('\n');
                }
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushAll();
                inFence = true;
                fenceOpened = true;
                codeLang = trimmed[3..].Trim();
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushAll();
                continue;
            }

            if (TryHeading(trimmed, out var level, out var heading))
            {
                FlushAll();
                result.Add(new MdBlock { Kind = MdBlockKind.Heading, Level = level, Text = heading });
                continue;
            }

            if (IsRule(trimmed))
            {
                FlushAll();
                result.Add(new MdBlock { Kind = MdBlockKind.Rule });
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraphToResult();
                var text = trimmed[1..];
                quote.Add(text.StartsWith(' ') ? text[1..].Trim() : text.Trim());
                continue;
            }

            FlushQuoteToResult();

            if (TryListItem(line, out var depth, out var ordered, out var marker, out var content))
            {
                FlushParagraphToResult();
                result.Add(new MdBlock
                {
                    Kind = ordered ? MdBlockKind.Ordered : MdBlockKind.Bullet,
                    Depth = depth,
                    Text = content,
                    Meta = marker
                });
                continue;
            }

            paragraph.Add(trimmed);
        }

        // 未闭合的围栏 / 最后一段: 照样交给界面, 不能因为正文没收尾就整段消失
        if (inFence) FlushFence();
        FlushAll();
        return result;
    }

    /// <summary>`#{1,4} 标题`。返回 false 时 <paramref name="text"/> 不可用。</summary>
    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = "";
        if (trimmed.Length == 0 || trimmed[0] != '#') return false;

        var i = 0;
        while (i < trimmed.Length && trimmed[i] == '#') i++;
        if (i > 6 || i >= trimmed.Length || trimmed[i] != ' ') return false;

        level = Math.Min(i, 4);
        text = trimmed[i..].Trim();
        // 收尾的 ### 是装饰(ATX 闭合), 去掉
        while (text.EndsWith("#", StringComparison.Ordinal)) text = text[..^1].TrimEnd();
        return text.Length > 0;
    }

    /// <summary>`---` / `***` / `___`(3 个以上同一字符)。</summary>
    private static bool IsRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        var c = trimmed[0];
        if (c != '-' && c != '*' && c != '_') return false;
        foreach (var ch in trimmed)
            if (ch != c) return false;
        return true;
    }

    /// <summary>
    /// 列表项。缩进按 **2 空格 = 一层** 计算 —— 发布说明与 GitHub 的缩进都是这么写的;
    /// 4 空格缩进的文档在这里会算成两层, 视觉上仍然"缩进了", 不会丢内容。
    /// </summary>
    private static bool TryListItem(string line, out int depth, out bool ordered,
        out string marker, out string content)
    {
        depth = 0;
        ordered = false;
        marker = "";
        content = "";

        var spaces = 0;
        while (spaces < line.Length && line[spaces] == ' ') spaces++;
        var rest = line[spaces..];
        if (rest.Length < 2) return false;

        var first = rest[0];
        if ((first == '-' || first == '*' || first == '+') && rest[1] == ' ')
        {
            content = rest[2..].Trim();
            if (content.Length == 0) return false;
            depth = Math.Min(spaces / 2, MaxListDepth);
            return true;
        }

        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;
        if (digits == 0 || digits >= rest.Length) return false;
        if (rest[digits] != '.' && rest[digits] != ')') return false;
        if (digits + 1 >= rest.Length || rest[digits + 1] != ' ') return false;

        marker = rest[..digits];
        content = rest[(digits + 2)..].Trim();
        if (content.Length == 0) return false;
        depth = Math.Min(spaces / 2, MaxListDepth);
        ordered = true;
        return true;
    }

    /// <summary>
    /// 行内标记切成片段。支持的写法:
    /// `**加粗**`、`` `行内代码` ``、`[文字](url)`、`&lt;kbd&gt;F5&lt;/kbd&gt;`、`\\` 转义;
    /// 其它 HTML 标签丢弃、保留内文。
    /// </summary>
    public static IReadOnlyList<MdSpan> ParseInlines(string? text)
    {
        var spans = new List<MdSpan>();
        if (string.IsNullOrEmpty(text)) return spans;

        var buffer = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Bold, text[(i + 2)..end].Trim()));
                    i = end + 2;
                    continue;
                }
            }

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    spans.Add(new MdSpan(MdSpanKind.Code, text[(i + 1)..end].Trim()));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i + 1 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var paren = text.IndexOf(')', close + 2);
                    if (paren > close + 2)
                    {
                        var label = text[(i + 1)..close].Trim();
                        var url = text[(close + 2)..paren].Trim();
                        // [text](url "title") —— 标题只是提示, 取自第一个空格之前
                        var titleAt = url.IndexOf(' ');
                        if (titleAt > 0) url = url[..titleAt];
                        if (label.Length > 0 && IsWebUrl(url))
                        {
                            Flush();
                            spans.Add(new MdSpan(MdSpanKind.Link, label, url));
                            i = paren + 1;
                            continue;
                        }
                    }
                }
            }

            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close > i)
                {
                    var tag = text[(i + 1)..close].Trim();
                    if (tag.Equals("kbd", StringComparison.OrdinalIgnoreCase))
                    {
                        var endKbd = text.IndexOf("</kbd>", close + 1, StringComparison.OrdinalIgnoreCase);
                        if (endKbd > close)
                        {
                            Flush();
                            spans.Add(new MdSpan(MdSpanKind.Key, text[(close + 1)..endKbd].Trim()));
                            i = endKbd + 6;
                            continue;
                        }
                    }
                    if (tag.Equals("br", StringComparison.OrdinalIgnoreCase)
                        || tag.Equals("br/", StringComparison.OrdinalIgnoreCase))
                    {
                        buffer.Append(' ');
                        i = close + 1;
                        continue;
                    }

                    // 其它标签丢掉标记, 内文照常显示
                    i = close + 1;
                    continue;
                }
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return spans;

        void Flush()
        {
            if (buffer.Length == 0) return;
            spans.Add(new MdSpan(MdSpanKind.Text, buffer.ToString()));
            buffer.Clear();
        }
    }

    private static bool IsWebUrl(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 纯文本化(给断言/无障碍用): 抹掉所有行内标记, 只留能读的字。
    ///
    /// ★ 只用在测试与 ToolTip 这类"要一句话"的地方 —— 界面渲染走 <see cref="ParseInlines"/>,
    ///   别拿它当渲染结果(它把加粗/链接的层次信息都丢了)。
    /// </summary>
    public static string ToPlainText(string? markdown)
    {
        var blocks = Parse(markdown);
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block.Kind == MdBlockKind.Rule) continue;
            if (sb.Length > 0) sb.Append('\n');
            foreach (var span in ParseInlines(block.Text)) sb.Append(span.Text);
        }
        return sb.ToString();
    }
}
