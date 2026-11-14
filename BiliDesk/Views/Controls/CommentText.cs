using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Utils;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 评论正文(带 B 站表情与可点链接), 支持**拖选复制**。
///
/// 表情是自定义图片, 只能内联进文字流(InlineUIContainer)。之前用 TextBlock 承载 ——
/// TextBlock 不能选中, 评论区只能靠右键"复制评论文字"兜底, 用户明确要回拖选复制。
/// 换成只读的 RichTextBox: 同样支持 InlineUIContainer, 还自带选中 / Ctrl+C / 拖拽选择。
///
/// ★ 2026-11-14 加链接识别: 正文里的 http(s)/www./B 站域名渲染成 Hyperlink,
///   单击用**内置浏览器**打开(见 RichTextBuilder.CreateLink / LinkLauncher)。
///   因此本控件必须 <c>IsDocumentEnabled = true</c>, 否则只读框里的链接点不动。
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
    /// <summary>
    /// 正文内容是否已被"离屏释放"。
    ///
    /// ★ 为什么需要: 评论区是无限加载的, 但没有虚拟化(容器不会销毁)。每条评论的
    ///   RichTextBox + FlowDocument + 内联表情/图片一旦生成就常驻内存, 滚几十页后
    ///   能涨到 100~200MB。这里只释放**文档内容**(视觉树和控件本身保留), 由播放器
    ///   按滚动位置对离屏卡片调用 <see cref="ReleaseContent"/>, 回到视野附近再
    ///   <see cref="RestoreContent"/> 重建。
    /// </summary>
    private bool _contentReleased;

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
        // ★ 只读 RichTextBox 默认 IsDocumentEnabled=false, 里面的 Hyperlink **点不动**
        //   (鼠标压上去连手型都不给)。正文里的链接要能点开, 必须显式打开。
        IsDocumentEnabled = true;
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

    /// <summary>
    /// 释放正文的文档内容(离屏评论用)。
    ///
    /// ★ 只清 <c>Document.Blocks</c>, **不销毁控件**: 清空后 RichTextBox 的高度会塌成 0,
    ///   所以调用方必须先把外层卡片容器的高度冻结住, 否则下面的评论会整体上移、滚动位置会跳。
    /// ★ 表情图片、内联图片都随 Blocks 一起被放掉; 文字与表情数据仍在 CommentItem 里,
    ///   恢复时不需要重新请求评论区接口。
    /// </summary>
    public void ReleaseContent()
    {
        if (_contentReleased) return;
        _contentReleased = true;
        Document.Blocks.Clear();
    }

    /// <summary>把离屏释放掉的正文重建回来(从 CommentItem 重新排版)。</summary>
    public void RestoreContent()
    {
        if (!_contentReleased) return;
        _contentReleased = false;
        Rebuild();
    }

    private static void OnCommentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CommentText)d).Rebuild();

    private void Rebuild()
    {
        // 数据变了 / 恢复内容时都算"已加载": 之后可以被再次离屏释放
        _contentReleased = false;

        var p = new Paragraph { Margin = new Thickness(0) };
        Document.Blocks.Clear();
        Document.Blocks.Add(p);

        var item = Comment;
        var text = item?.Content ?? "";

        // 前缀(用户名那一截)先入流: 它与正文同行混排、一起折行
        var prefix = Prefix ?? "";
        if (prefix.Length > 0)
            RichTextBuilder.AppendText(p, prefix, PrefixForeground, FontWeights.SemiBold);

        if (text.Length == 0) return;

        var emotes = item?.Emotes ?? (IReadOnlyDictionary<string, CommentEmote>)new Dictionary<string, CommentEmote>();
        var mentionBrush = MentionForeground;
        var hasEmotes = emotes.Count > 0;
        // ★ @ 高亮只在"传了颜色"且"正文里确实有 @"时才做 —— 没传颜色就别扫,
        //   扫了也白扫(而且 Split 会多跑一遍)。
        var hasMentions = mentionBrush != null && text.Contains('@');
        // ★ 链接同理: 绝大多数评论没有链接, 先做一次便宜的"可能含链接"判断,
        //   免得每条评论都白跑一遍切分(评论区是无限加载的)。
        var hasLinks = LinkParser.MightContainLink(text);

        // 最常见的情况: 既没有表情、没有 @、也没有链接 ⇒ 整段一次加完
        if (!hasEmotes && !hasMentions && !hasLinks)
        {
            RichTextBuilder.AppendText(p, text);
            return;
        }

        // ---------------------------------------------------------------
        // 依次按 **链接 → @提及 → 表情** 三层切分:
        //   · 链接段做成 Hyperlink, 点击走内置浏览器(地址里可能带 @, 所以链接必须最先切,
        //     否则 "https://x.com/@user" 会被 @ 切成两半);
        //   · 剩下的文字再按 @ 提及切段, 提及段整段染强调色;
        //   · 最后的普通文字扫表情短代码, 换成内联图片。
        // ---------------------------------------------------------------
        var linkSegments = hasLinks
            ? LinkParser.Split(text)
            : new List<LinkSegment> { new(text, false, "") };

        foreach (var link in linkSegments)
        {
            if (link.IsLink)
            {
                // ★ 链接段不再扫表情: 地址里的 [xx] 是查询参数, 不是表情。
                p.Inlines.Add(RichTextBuilder.CreateLink(link.Text, link.Url, this));
                continue;
            }

            var segments = hasMentions
                ? MentionParser.Split(link.Text)
                : new List<(string Text, bool IsMention)> { (link.Text, false) };

            foreach (var (segText, isMention) in segments)
            {
                if (isMention)
                {
                    // 提及段: 整段(含 @)染成强调色。★ 里面**不再**处理表情 ——
                    //   昵称里出现表情短代码的概率极低, 真出现了也只是显示成字面文本, 可接受。
                    RichTextBuilder.AppendText(p, segText, mentionBrush);
                    continue;
                }

                AppendTextWithEmotes(this, p, segText, emotes, hasEmotes, item);
            }
        }
    }

    /// <summary>
    /// 把一段**普通文本**加进段落, 途中遇到表情短代码就换成内联图片。
    /// </summary>
    private static void AppendTextWithEmotes(CommentText self, Paragraph p, string text,
        IReadOnlyDictionary<string, CommentEmote> emotes, bool hasEmotes, CommentItem? owner)
    {
        if (!hasEmotes || emotes.Count == 0)
        {
            RichTextBuilder.AppendText(p, text);
            return;
        }

        var buffer = new System.Text.StringBuilder();

        void FlushText()
        {
            if (buffer.Length == 0) return;
            RichTextBuilder.AppendText(p, buffer.ToString());
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
