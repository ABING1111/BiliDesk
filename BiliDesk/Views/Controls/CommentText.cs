using System;
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
        {
            var run = new Run(prefix) { FontWeight = FontWeights.SemiBold };
            if (PrefixForeground != null) run.Foreground = PrefixForeground;
            p.Inlines.Add(run);
        }

        if (text.Length == 0) return;

        // 没有表情就整段一次加完(最常见的情况, 别为它走扫描逻辑)
        if (item == null || item.Emotes.Count == 0)
        {
            p.Inlines.Add(new Run(text));
            return;
        }

        var emotes = item.Emotes;
        var buffer = new StringBuilder();

        void FlushText()
        {
            if (buffer.Length == 0) return;
            p.Inlines.Add(new Run(buffer.ToString()));
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
                        p.Inlines.Add(BuildEmote(emote, item));
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
