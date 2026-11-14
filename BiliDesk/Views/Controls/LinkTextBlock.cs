using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using BiliDesk.Helpers;
using BiliDesk.Utils;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 带链接识别的 TextBlock(可截断的那种正文)。
///
/// ★ 为什么还要一个 TextBlock 版: 动态卡收起态靠 <c>MaxHeight + TextTrimming="CharacterEllipsis"</c>
///   做"几行 + 省略号"的干净截断 —— RichTextBox(<see cref="LinkText"/>)没有省略号,
///   塞进去只会把文字硬切一刀。而 TextBlock 的 Inlines 同样能放 <see cref="Hyperlink"/>,
///   链接照样点得动。
/// ★ 为什么属性叫 <see cref="SourceText"/> 而不是 Text: TextBlock 自带 Text 依赖属性,
///   设置它会**清空 Inlines**(正好把链接抹掉), 所以只能另起一个名字。
/// ★ 因为它继承 TextBlock, 那些 <c>TargetType="TextBlock"</c> 的样式(用户主页签名、
///   番剧简介)可以直接套上来 —— 截断逻辑一行都不用改。
/// </summary>
public class LinkTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(LinkTextBlock),
        new PropertyMetadata("", OnSourceTextChanged));

    /// <summary>要显示的原始文本(纯文本, 里面的 URL 会被渲染成可点的链接)。</summary>
    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    private static void OnSourceTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LinkTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        var text = SourceText ?? "";
        if (text.Length == 0) return;

        // 绝大多数正文没有链接: 一次便宜的判断先挡掉, 省掉切分的分配
        if (!LinkParser.MightContainLink(text))
        {
            Inlines.Add(new Run(text));
            return;
        }

        foreach (var seg in LinkParser.Split(text))
        {
            if (seg.IsLink)
                Inlines.Add(RichTextBuilder.CreateLink(seg.Text, seg.Url, this));
            else
                Inlines.Add(new Run(seg.Text));
        }
    }
}
