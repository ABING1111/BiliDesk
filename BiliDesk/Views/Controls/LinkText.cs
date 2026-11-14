using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Utils;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 带链接识别的正文(播放器简介 / 图文动态正文 / 转发原文)。
///
/// ★ 为什么用只读 RichTextBox: 链接要做成 <see cref="Hyperlink"/> 才点得动,
///   而 Hyperlink 只存在于文字流里 —— TextBlock/TextBox 都不行(TextBlock 没有选中能力,
///   TextBox 放不了内联元素)。评论区(<see cref="CommentText"/>)也是同一条理由。
/// ★ 与 <see cref="SelectableText"/> 的分工: 那个是纯文本的轻量只读框;
///   这里需要"文字 + 可点链接"混排, 才用更重的 FlowDocument。
/// ★ 保留 TextBox 时代的行为: 拖选 / Ctrl+C / 右键复制 / 高度由内容撑开(无内部滚动条)。
/// </summary>
public class LinkText : RichTextBox
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LinkText),
        new PropertyMetadata("", OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public LinkText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        // ★ 只读 RichTextBox 默认 IsDocumentEnabled=false, 里面的 Hyperlink 点不动
        IsDocumentEnabled = true;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        // 高度由内容撑开: 折叠/展开靠外部的 MaxHeight 控制, 不要内部滚动条
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Cursor = Cursors.IBeam;
        IsTabStop = false;
        FocusVisualStyle = null;
        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            // 别让 FlowDocument 自作主张加列宽(窄栏里的文字会提前换行)
            ColumnWidth = double.PositiveInfinity
        };

        // 右键: 选了片段就复制片段, 没选就复制全文(与简介旧版 TextBox 的复制能力对齐)
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) =>
        {
            var selected = Selection?.Text;
            // ★ 用文档里的实际文字兜底, 而不是 Text 依赖属性: 正文可能是外部直接塞进
            //   Document 的(与 Text 不同步), 这时 Text 是空的, 复制会"什么都没发生"。
            var text = string.IsNullOrEmpty(selected) ? DocumentText() : selected;
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); } catch { /* 剪贴板被占用, 忽略 */ }
        };
        menu.Items.Add(copy);
        ContextMenu = menu;

        // ★★ 钉住 PagePadding = 0(与 CommentText 同一个坑):
        //   构造函数里写的 0 撑不过第一次排版, 文档视图会把它改回 5,
        //   于是正文整体比同栏的其它文字右缩 5px。见 CommentText 的详细说明。
        DependencyPropertyDescriptor.FromProperty(FlowDocument.PagePaddingProperty, typeof(FlowDocument))
            .AddValueChanged(Document, (_, _) =>
            {
                if (Document.PagePadding != default)
                    Document.PagePadding = new Thickness(0);
            });
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LinkText)d).Rebuild();

    /// <summary>把文档里所有文字取出来(右键"复制全文"用; 不依赖 Text 依赖属性)。</summary>
    private string DocumentText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var block in Document.Blocks)
            if (block is Paragraph p) AppendInlines(p.Inlines, sb);
        return sb.ToString();
    }

    private static void AppendInlines(InlineCollection inlines, System.Text.StringBuilder sb)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run r: sb.Append(r.Text); break;
                case Span s: AppendInlines(s.Inlines, sb); break;
            }
        }
    }

    private void Rebuild()
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        Document.Blocks.Clear();
        Document.Blocks.Add(p);

        var text = Text ?? "";
        if (text.Length == 0) return;

        // 绝大多数正文没有链接: 先做一次便宜的判断, 再决定要不要逐段切
        if (!LinkParser.MightContainLink(text))
        {
            RichTextBuilder.AppendText(p, text);
            return;
        }

        foreach (var seg in LinkParser.Split(text))
        {
            if (seg.IsLink)
                p.Inlines.Add(RichTextBuilder.CreateLink(seg.Text, seg.Url, this));
            else
                RichTextBuilder.AppendText(p, seg.Text);
        }
    }
}
