using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 评论正文(带 B 站表情), 支持**拖选复制**。
///
/// 表情是自定义图片, 只能内联进文字流(InlineUIContainer)。之前用 TextBlock 承载 ——
/// TextBlock 不能选中, 评论区只能靠右键"复制评论文字"兜底, 用户明确要回拖选复制。
/// 换成只读的 RichTextBox: 同样支持 InlineUIContainer, 还自带选中 / Ctrl+C / 拖拽选择。
///
/// 性能说明: RichTextBox 比 TextBlock 重, 但评论区一屏就几十条且列表有虚拟化,
/// 这份开销换复制能力是值得的。
///
/// 高度自适应的三件事(少一件内容就会被裁掉一行):
///   1. VerticalScrollBarVisibility=Disabled —— 让文档把高度撑开而不是滚动;
///   2. Document 的 PagePadding 清零 —— RichTextBox 默认给它留一圈边距;
///   3. 控件不设 Height, 由内容决定。
/// </summary>
public class CommentText : RichTextBox
{
    public static readonly DependencyProperty CommentProperty = DependencyProperty.Register(
        nameof(Comment), typeof(CommentItem), typeof(CommentText),
        new PropertyMetadata(null, OnCommentChanged));

    public CommentItem? Comment
    {
        get => (CommentItem?)GetValue(CommentProperty);
        set => SetValue(CommentProperty, value);
    }

    /// <summary>
    /// 正文前面的那一小截(楼中楼方框里的"用户名：" / "用户名 回复 @XX")。
    ///
    /// 为什么做成控件自己的属性、而不是在外面另摆一个 TextBlock: 前缀必须和正文处在
    /// **同一个文字流**里, 才能出现"用户名：内容…"同一行、超宽自动折行的效果;
    /// 摆成两个控件的话前缀永远独占一行, 白白多出一行高度。
    /// </summary>
    public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(
        nameof(Prefix), typeof(string), typeof(CommentText),
        new PropertyMetadata("", OnCommentChanged));

    public string Prefix
    {
        get => (string)GetValue(PrefixProperty);
        set => SetValue(PrefixProperty, value);
    }

    /// <summary>前缀的颜色。XAML 里传 DynamicResource, 主题切换时跟着变。</summary>
    public static readonly DependencyProperty PrefixForegroundProperty = DependencyProperty.Register(
        nameof(PrefixForeground), typeof(Brush), typeof(CommentText),
        new PropertyMetadata(null, OnCommentChanged));

    public Brush? PrefixForeground
    {
        get => (Brush?)GetValue(PrefixForegroundProperty);
        set => SetValue(PrefixForegroundProperty, value);
    }

    /// <summary>
    /// @提及的高亮色(2026-10-30 新增)。XAML 里传 DynamicResource(强调色), 主题切换时跟着变。
    /// ★ 传 null 就不做 @ 高亮(默认行为) —— 这样"用别处"不会突然多出一堆蓝色。
    /// </summary>
    public static readonly DependencyProperty MentionForegroundProperty = DependencyProperty.Register(
        nameof(MentionForeground), typeof(Brush), typeof(CommentText),
        new PropertyMetadata(null, OnCommentChanged));

    public Brush? MentionForeground
    {
        get => (Brush?)GetValue(MentionForegroundProperty);
        set => SetValue(MentionForegroundProperty, value);
    }

    public CommentText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        // 选中态的视觉: 只读框默认还有一层系统底色, 透明掉, 让它融进评论卡片
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Cursor = Cursors.IBeam;
        IsTabStop = false;
        FocusVisualStyle = null;
        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            // 别让 FlowDocument 自作主张加列宽(窄栏里的文字会提前换行)
            ColumnWidth = double.PositiveInfinity
        };

        // 右键: 选了片段就复制片段, 没选就复制整条
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) =>
        {
            var text = string.IsNullOrEmpty(Selection?.Text) ? Comment?.Content ?? "" : Selection!.Text;
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); } catch { /* 剪贴板被占用, 忽略 */ }
        };
        menu.Items.Add(copy);
        ContextMenu = menu;

        // ★★ 钉住 PagePadding = 0(2026-10-31 修"正文和昵称不对齐")。
        //
        //   构造函数里给 FlowDocument.PagePadding 写的 0 **撑不过第一次排版**:
        //   文档视图(FlowDocumentView)在排版阶段会把它改回默认的 5, 每个 LineVisual
        //   因此被推到 X=+5 —— 探针 bd-probe-commentalign 实测: 昵称墨迹从 X=39 起,
        //   正文墨迹从 X=44 起, 整条正文比昵称右缩 5px, 上下看着不齐。
        //   ★ 构造后再设一次也挡不住(重置发生在排版阶段, 不是赋值阶段),
        //     所以这里挂属性变更监听: 只要它被改回 5 就拨回 0。
        //     (DependencyPropertyDescriptor 走的是 DependencyProperty 的实际变更,
        //      排版阶段的修改也会触发; 只在非 0 时才赋值, 不会造成死循环。)
        DependencyPropertyDescriptor.FromProperty(FlowDocument.PagePaddingProperty, typeof(FlowDocument))
            .AddValueChanged(Document, (_, _) =>
            {
                if (Document.PagePadding != default)
                    Document.PagePadding = new Thickness(0);
            });
    }

    private static void OnCommentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CommentText)d).Rebuild();

    private void Rebuild()
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        Document.Blocks.Clear();
        Document.Blocks.Add(p);

        var item = Comment;
        var text = item?.Content ?? "";

        // 前缀(用户名那一截)先入流: 它与正文同行混排、一起折行
        var prefix = Prefix ?? "";
        if (prefix.Length > 0)
            AppendText(p, prefix, PrefixForeground, FontWeights.SemiBold);

        if (text.Length == 0) return;

        var emotes = item?.Emotes ?? (IReadOnlyDictionary<string, CommentEmote>)new Dictionary<string, CommentEmote>();
        var mentionBrush = MentionForeground;
        var hasEmotes = emotes.Count > 0;
        // ★ @ 高亮只在"传了颜色"且"正文里确实有 @"时才做 —— 没传颜色就别扫,
        //   扫了也白扫(而且 Split 会多跑一遍)。
        var hasMentions = mentionBrush != null && text.Contains('@');

        // 最常见的情况: 既没有表情也没有 @ ⇒ 整段一次加完
        if (!hasEmotes && !hasMentions)
        {
            AppendText(p, text);
            return;
        }

        // ---------------------------------------------------------------
        // 有 @ 时按"提及段 / 普通段"切开, 逐段处理; 没 @ 时就是**一整段普通文本**。
        // 两段两种处理方式共用同一套"扫描表情"逻辑(见 AppendTextWithEmotes)。
        // ---------------------------------------------------------------
        var segments = hasMentions
            ? MentionParser.Split(text)
            : new List<(string Text, bool IsMention)> { (text, false) };

        foreach (var (segText, isMention) in segments)
        {
            if (isMention)
            {
                // 提及段: 整段(含 @)染成强调色。★ 里面**不再**处理表情 ——
                //   昵称里出现表情短代码的概率极低, 真出现了也只是显示成字面文本, 可接受。
                AppendText(p, segText, mentionBrush);
                continue;
            }

            AppendTextWithEmotes(this, p, segText, emotes, hasEmotes, item);
        }
    }

    /// <summary>emoji 字形专用字体(见 <see cref="AppendText"/> 里的崩溃说明)。</summary>
    private static readonly FontFamily EmojiFont = new("Segoe UI Symbol");

    /// <summary>
    /// 把一段文本拆成"普通文字 / emoji"两类 Run 后加进段落。
    ///
    /// ★ 2026-11-04 修"打开评论详情页卡死": WPF 的 FlowDocument 在**走字体回退**渲染
    ///   某些带变体选择符(U+FE0F)的 emoji 时会 FailFast(WindowsBase!TextParaClient.CreateLineVisual)。
    ///   实测 "🖐️" 连写 7 个、或 "👍️" 连写 8 个就必崩; 把该 Run 的 FontFamily 显式设成
    ///   **Segoe UI Symbol** 后同一段文字正常排版(20 个连写也稳)。
    ///   ★ 不用 Segoe UI Emoji: 它是彩色字体, 这台机器上的 WPF RichTextBox 渲染它时整行空白
    ///     (探针 bd-probe-emoji-render 实测); Symbol 是单色字形, 显示不如彩色 emoji 好看,
    ///     但至少能看到、也不会崩。
    ///   所以这里按 grapheme cluster 切分, 只给 emoji 那一截指定 emoji 字体,
    ///   普通文字(含数字/中英文)仍走继承来的界面字体, 观感不变。
    /// </summary>
    private static void AppendText(Paragraph p, string text,
        Brush? foreground = null, FontWeight? weight = null)
    {
        if (text.Length == 0) return;

        // 快路径: 绝大多数评论没有 unicode emoji, 不切分、不建额外 Run, 保持原来的排版开销。
        if (!ContainsEmoji(text))
        {
            var plain = new Run(text);
            if (foreground != null) plain.Foreground = foreground;
            if (weight.HasValue) plain.FontWeight = weight.Value;
            p.Inlines.Add(plain);
            return;
        }

        var normal = new StringBuilder();
        var emoji = new StringBuilder();

        void Flush(StringBuilder sb, bool isEmoji)
        {
            if (sb.Length == 0) return;
            var run = new Run(sb.ToString());
            if (isEmoji) run.FontFamily = EmojiFont;
            if (foreground != null) run.Foreground = foreground;
            if (weight.HasValue) run.FontWeight = weight.Value;
            p.Inlines.Add(run);
            sb.Clear();
        }

        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var cluster = (string)e.Current;
            if (IsEmojiCluster(cluster))
            {
                Flush(normal, false);
                emoji.Append(cluster);
            }
            else
            {
                Flush(emoji, true);
                normal.Append(cluster);
            }
        }
        Flush(normal, false);
        Flush(emoji, true);
    }

    private static bool ContainsEmoji(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (IsEmojiRune(rune.Value)) return true;
        return false;
    }

    /// <summary>
    /// 这个 grapheme cluster 是不是 emoji(需要用 emoji 字体单独渲染)。
    /// 判据放宽一点: 增补平面字符 + 变体选择符/ZWJ/键帽组合符都算 ——
    /// 漏掉一个就可能把上面的 WPF 崩溃重新引回来。
    /// </summary>
    private static bool IsEmojiCluster(string cluster)
    {
        foreach (var rune in cluster.EnumerateRunes())
            if (IsEmojiRune(rune.Value)) return true;
        return false;
    }

    private static bool IsEmojiRune(int v)
    {
        // 增补平面的 emoji 区块(1F000~1FAFF) + 变体选择符 / ZWJ / 键帽组合符.
        // ★ 不用"v > 0xFFFF"一把抓: 那会把 CJK 扩展 B 区(𠀀 这类)也塞进 emoji 字体,
        //   虽然通常能回退, 但没必要让生僻汉字走错字体。
        return (v >= 0x1F000 && v <= 0x1FAFF)
               || v == 0xFE0F || v == 0xFE0E || v == 0x200D || v == 0x20E3;
    }

    /// <summary>
    /// 把一段**普通文本**加进段落, 途中遇到表情短代码就换成内联图片。
    /// </summary>
    private static void AppendTextWithEmotes(CommentText self, Paragraph p, string text,
        IReadOnlyDictionary<string, CommentEmote> emotes, bool hasEmotes, CommentItem? owner)
    {
        if (!hasEmotes || emotes.Count == 0)
        {
            AppendText(p, text);
            return;
        }

        var buffer = new StringBuilder();

        void FlushText()
        {
            if (buffer.Length == 0) return;
            AppendText(p, buffer.ToString());
            buffer.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var end = text.IndexOf(']', i + 1);
                // 短代码最短也是 "[x]" 三位; 太长的不可能是表情, 免得为一堆 "[" 反复 IndexOf
                if (end > i && end - i <= 40)
                {
                    var sign = text.Substring(i, end - i + 1);
                    if (emotes.TryGetValue(sign, out var emote))
                    {
                        FlushText();
                        if (owner != null) p.Inlines.Add(self.BuildEmote(emote, owner));
                        i = end + 1;
                        continue;
                    }
                }
            }
            buffer.Append(text[i]);
            i++;
        }
        FlushText();
    }

    /// <summary>把一个表情做成内联图片。图片异步加载, 加载完再赋值(期间按占位尺寸撑住行高)</summary>
    private InlineUIContainer BuildEmote(CommentEmote emote, CommentItem owner)
    {
        // 尺寸跟着字号走: 小表情比文字略高一点, 大表情(meta.size=2, 游戏联动那类)再大一档
        var scale = emote.Size > 1 ? 2.4 : 1.55;
        var side = Math.Round((FontSize > 0 ? FontSize : 12) * scale);

        var image = new Image
        {
            Width = side,
            Height = side,
            Stretch = Stretch.Uniform,
            // 行内图片的垂直对齐: 不写会贴着基线往上顶, 看起来像错行
            VerticalAlignment = VerticalAlignment.Center
        };

        // 表情图片很小(源图通常 20~40px), 解码宽度给 40 就够 —— 走的是量化后的 64 档,
        // 一张只占十几 KB。**不要**用 Cover.ImageSource(它按 512 解码, 一张表情就要 1MB)。
        _ = LoadEmoteAsync(image, emote.Url, owner);

        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
    }

    private async System.Threading.Tasks.Task LoadEmoteAsync(Image image, string url, CommentItem owner)
    {
        var bmp = await CoverLoader.LoadAsync(url, 40);
        if (bmp == null) return;
        // 控件可能已经被复用到另一条评论上(列表虚拟化), 那就丢弃这次结果
        if (!ReferenceEquals(Comment, owner)) return;
        image.Source = bmp;
    }
}
