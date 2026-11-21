using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BiliDesk.Helpers;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 把发布说明的 Markdown 子集(<see cref="MarkdownLite"/>)画成一块可读的正文。
///
/// ★ 为什么是 StackPanel 而不是 RichTextBox: 这里只需要"读", 不需要光标/选区/滚动条。
///   RichTextBox 塞进卡片里要处理自动高度、滚动条、内边距、焦点框一整套,
///   而每个块自己做排版反而能精确对齐站点版式(圆点缩进、分组标题的间距)。
/// ★ 为什么所有颜色都走 SetResourceReference: 深浅色与自定义主题色切换时,
///   整块正文要跟着变 —— 局部值(直接赋 Brush)会在切换后停在旧颜色上,
///   而重建整棵树只在 Markdown 变化时发生。
/// ★ 正文里的链接复用 <see cref="RichTextBuilder.CreateLink"/>: 它已经解决了
///   "只读正文里 Hyperlink 抢鼠标捕获 / 拖选误触发"这两个坑, 点击统一走 LinkLauncher。
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Rebuild()));

    /// <summary>Markdown 原文(发布说明体量, 几 KB)</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private const double BodyFontSize = 13.5;
    private const double BodyLineHeight = 21;

    /// <summary>列表最多缩进到几层(与解析器的上限一致, 再深就没有可读性了)</summary>
    private const int MaxIndentDepth = 3;

    private static readonly FontFamily MonoFont = new("Consolas, Microsoft YaHei UI");

    public MarkdownView()
    {
        // 正文块之间靠各自 Margin 拉开, 不需要额外间距
        SnapsToDevicePixels = true;
    }

    private void Rebuild()
    {
        Children.Clear();
        var blocks = MarkdownLite.Parse(Markdown);
        for (var i = 0; i < blocks.Count; i++)
        {
            var element = BuildBlock(blocks[i], i == 0);
            if (element != null) Children.Add(element);
        }
    }

    private FrameworkElement? BuildBlock(MdBlock block, bool isFirst)
    {
        switch (block.Kind)
        {
            case MdBlockKind.Heading:
                return BuildHeading(block, isFirst);
            case MdBlockKind.Bullet:
                return BuildListItem(block, ordered: false);
            case MdBlockKind.Ordered:
                return BuildListItem(block, ordered: true);
            case MdBlockKind.Paragraph:
                return BuildParagraph(block);
            case MdBlockKind.Quote:
                return BuildQuote(block);
            case MdBlockKind.Code:
                return BuildCodeBlock(block);
            case MdBlockKind.Rule:
                return BuildRule();
            default:
                return null;
        }
    }

    /// <summary>分组标题(`### 新增` / `### 修复`)。第一块不留上边距, 免得卡片顶部空一块。</summary>
    private static TextBlock BuildHeading(MdBlock block, bool isFirst)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = block.Level <= 2 ? 15.5 : 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, isFirst ? 0 : (block.Level <= 2 ? 20 : 16), 0, 6)
        };
        text.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
        FillInlines(text.Inlines, block.Text, text);
        return text;
    }

    private static FrameworkElement BuildListItem(MdBlock block, bool ordered)
    {
        var depth = System.Math.Clamp(block.Depth, 0, MaxIndentDepth);
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = BodyFontSize,
            LineHeight = BodyLineHeight,
            Margin = new Thickness(0, 0, 0, 8)
        };
        text.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
        FillInlines(text.Inlines, block.Text, text);

        var row = new Grid { Margin = new Thickness(18 + depth * 16, 0, 0, 0) };

        if (ordered)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var number = new TextBlock
            {
                Text = block.Meta + ".",
                FontFamily = MonoFont,
                FontSize = 12,
                Margin = new Thickness(0, 3, 8, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            number.SetResourceReference(TextElement.ForegroundProperty, "AccentTextBrush");
            row.Children.Add(number);
            Grid.SetColumn(text, 1);
        }
        else
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 站点模板/参照图的列表圆点: 6px、强调色、半透明, 与第一行文字垂直居中
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1, (BodyLineHeight - 6) / 2, 0, 0)
            };
            dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
            row.Children.Add(dot);
            Grid.SetColumn(text, 1);
        }

        row.Children.Add(text);
        return row;
    }

    private static TextBlock BuildParagraph(MdBlock block)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = BodyFontSize,
            LineHeight = BodyLineHeight,
            Margin = new Thickness(0, 0, 0, 8)
        };
        text.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
        FillInlines(text.Inlines, block.Text, text);
        return text;
    }

    /// <summary>引用块(如"已安装旧版本的用户覆盖安装即可"): 左侧强调色竖条 + 更淡的字。</summary>
    private static FrameworkElement BuildQuote(MdBlock block)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 2, 10, 2),
            Opacity = 0.45
        };
        bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        grid.Children.Add(bar);

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = BodyFontSize,
            LineHeight = BodyLineHeight
        };
        text.SetResourceReference(TextElement.ForegroundProperty, "TextTertiaryBrush");
        FillInlines(text.Inlines, block.Text, text);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static FrameworkElement BuildCodeBlock(MdBlock block)
    {
        var text = new TextBlock
        {
            Text = block.Text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MonoFont,
            FontSize = 12,
            LineHeight = 18
        };
        text.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 2, 0, 10),
            Child = text
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillDefaultBrush");
        return card;
    }

    private static FrameworkElement BuildRule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 14) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        return line;
    }

    /// <summary>把行内标记写进行集合。加粗用 <see cref="Bold"/>, 颜色靠资源引用跟着主题走。</summary>
    private static void FillInlines(InlineCollection target, string text, DependencyObject host)
    {
        foreach (var span in MarkdownLite.ParseInlines(text))
        {
            switch (span.Kind)
            {
                case MdSpanKind.Text:
                    RichTextBuilder.AppendInlines(target, span.Text);
                    break;

                case MdSpanKind.Bold:
                {
                    var bold = new Bold();
                    // 站点的 `<strong>` 用主色, 与"次级色的说明文字"形成层次
                    bold.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    FillInlines(bold.Inlines, span.Text, host);
                    target.Add(bold);
                    break;
                }

                case MdSpanKind.Code:
                {
                    var code = new Run(span.Text)
                    {
                        FontFamily = MonoFont,
                        FontSize = 12
                    };
                    code.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    code.SetResourceReference(TextElement.BackgroundProperty, "ControlFillDefaultBrush");
                    target.Add(code);
                    break;
                }

                case MdSpanKind.Link:
                    target.Add(RichTextBuilder.CreateLink(span.Text, span.Url, host));
                    break;

                case MdSpanKind.Key:
                {
                    // <kbd>F5</kbd>: 与行内代码同一套等宽字, 再用更实的底色与加粗做出"按键"感
                    var key = new Run(span.Text)
                    {
                        FontFamily = MonoFont,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold
                    };
                    key.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    key.SetResourceReference(TextElement.BackgroundProperty, "AccentSoftFillBrush");
                    target.Add(key);
                    break;
                }
            }
        }
    }
}
