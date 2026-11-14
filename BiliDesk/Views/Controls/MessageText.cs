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

namespace BiliDesk.Views.Controls;

/// <summary>
/// 私信气泡里的正文, 支持**内联 B 站表情**与**拖选复制**。
///
/// ★ 用 RichTextBox 而不是 TextBlock(用户 2026-11-15 要求"加上自由复制的能力"):
///   TextBlock 不能选中, 只能整条右键复制; 用户要的是像微信那样"选中一段复制"。
///   评论区的 <see cref="CommentText"/> 就是同一套做法, 复制能力已经验证过。
///
/// ★ 气泡贴着文字收缩的处理: RichTextBox 默认会撑满父容器给的宽度, 短消息会变成
///   一个占满整行的大方块。这里在 XAML 侧把气泡放进一个宽度自适应的容器,
///   控件本身 HorizontalAlignment=Left/Right + 父容器不给拉伸宽度即可。
///
/// ★ PagePadding 必须钉成 0(评论区同一教训): 文档视图会在排版阶段把它改回 5,
///   正文整体右缩 5px, 与昵称错位。见 CommentText 构造函数里的那段说明。
///
/// ★ emoji 字体回退的崩溃问题走 RichTextBuilder(与评论区同一份修复)。
/// </summary>
public class MessageText : RichTextBox
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(string), typeof(MessageText),
        new PropertyMetadata("", OnContentChanged));

    /// <summary>
    /// 正文。
    /// ★ 不能叫 Text —— RichTextBox 自己已经有 Text 依赖属性(.NET Core 3.0+),
    ///   再注册一个同名 DP 会直接抛异常。
    /// </summary>
    public string Content
    {
        get => (string)GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public static readonly DependencyProperty EmotesProperty = DependencyProperty.Register(
        nameof(Emotes), typeof(IReadOnlyDictionary<string, MsgEmote>),
        typeof(MessageText), new PropertyMetadata(null, OnContentChanged));

    /// <summary>短代码 → 表情。为空就只当普通文字排版。</summary>
    public IReadOnlyDictionary<string, MsgEmote>? Emotes
    {
        get => (IReadOnlyDictionary<string, MsgEmote>?)GetValue(EmotesProperty);
        set => SetValue(EmotesProperty, value);
    }

    /// <summary>文字颜色。自己的气泡里是"强调色上的白字", 对方是主文字色。</summary>
    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground), typeof(Brush), typeof(MessageText),
        new PropertyMetadata(null, OnContentChanged));

    public Brush? TextForeground
    {
        get => (Brush?)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    public MessageText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Cursor = Cursors.IBeam;
        IsTabStop = false;
        FocusVisualStyle = null;
        IsDocumentEnabled = true;
        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity
        };

        // ★★ 钉住 PagePadding = 0: 构造函数里赋的值撑不过第一次排版(评论区实测),
        //   排版阶段会被改回 5, 表现为正文比昵称右缩 5px。挂监听拨回来。
        DependencyPropertyDescriptor.FromProperty(FlowDocument.PagePaddingProperty, typeof(FlowDocument))
            .AddValueChanged(Document, (_, _) =>
            {
                if (Document.PagePadding != default)
                    Document.PagePadding = new Thickness(0);
            });

        // ★★★ 宽度贴合内容(短消息就是个小气泡, 不能被拉伸成一整行):
        //   RichTextBox 是"控件级"的, 布局时默认吃满父容器给的宽度 —— 探针实测
        //   "短"字和一句 20 字的话气泡同宽。做法: Rebuild 后用 TextBlock 预测文字
        //   自然宽度反设 Width(见 UpdateIdealWidth); 超过气泡上限时保持 NaN 交给折行。
        //   必须在 Loaded 与 Content/Emotes 变化后各排一次。
        Document.PagePadding = new Thickness(0);
        Loaded += (_, _) => UpdateIdealWidth();
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateIdealWidth(); };

        // 右键: 选了片段就复制片段, 没选就复制整条
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制" };
        copy.Click += (_, _) =>
        {
            var text = string.IsNullOrEmpty(Selection?.Text) ? Content ?? "" : Selection!.Text;
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); } catch { /* 剪贴板被占用 */ }
        };
        menu.Items.Add(copy);
        ContextMenu = menu;
    }

    /// <summary>
    /// 让控件宽度贴合正文内容(短消息就是个小气泡, 不能被拉伸成一整行)。
    ///
    /// ★ 为什么不用 Document.ExtentWidth: RichTextBox 没排过版(还不在可视树里)时它是 0,
    ///   排版时机又和布局循环打架 —— 探针里反复调不稳。
    ///   这里改用**用 TextBlock 预测文字宽度**: 同字号下 TextBlock 量出的单行宽度
    ///   就是正文自然宽度(误差 < 1px), 表情段按其固定尺寸加进去。预测值 ≤ 上限就设 Width,
    ///   超过就保持 NaN 让气泡按 MaxWidth 折行。
    /// </summary>
    private void UpdateIdealWidth()
    {
        try
        {
            var text = Content ?? "";
            if (text.Length == 0) { Width = double.NaN; return; }

            // 单行自然宽度: 用一个隐藏 TextBlock 以同字号量
            var probe = new TextBlock
            {
                Text = text,
                FontSize = FontSize > 0 ? FontSize : 12.5,
                FontFamily = FontFamily,
                FontWeight = FontWeight,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0),
                Padding = new Thickness(0)
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var natural = probe.DesiredSize.Width;

            // 表情段贡献的额外宽度(文字里每个短代码被替换成 side 像素的图):
            // TextBlock 量的是"短代码字面宽度", 图宽与它差不了几像素, 粗略相抵,
            // 不单独算 —— 精确度目标是"贴上去", 不是像素级对齐。
            var width = natural;

            const double bubbleMax = 440 - 22; // 气泡 Border MaxWidth(440) - 左右 Padding(11×2)
            if (width > 0 && width < bubbleMax)
                Width = width + 2; // +2: 富文本排版比 TextBlock 略紧, 留一点余量防"末字被挤折行"
            else
                Width = double.NaN;
        }
        catch
        {
            Width = double.NaN;
        }
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MessageText)d).Rebuild();

    private void Rebuild()
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        Document.Blocks.Clear();
        Document.Blocks.Add(p);

        var text = Content ?? "";
        if (text.Length == 0) return;

        var brush = TextForeground;
        var segments = MsgContentParser.Split(text, Emotes);

        foreach (var seg in segments)
        {
            if (seg.IsEmote && seg.Emote != null)
            {
                p.Inlines.Add(BuildEmote(seg.Emote));
                continue;
            }

            RichTextBuilder.AppendText(p, seg.Text, brush);
        }

        // 排完内容再算"贴文字"的宽度
        UpdateIdealWidth();
    }

    /// <summary>把一个表情做成内联图片(尺寸跟着字号走)</summary>
    private InlineUIContainer BuildEmote(MsgEmote emote)
    {
        var scale = emote.Size > 1 ? 2.4 : 1.55;
        var side = Math.Round((FontSize > 0 ? FontSize : 12.5) * scale);

        var image = new Image
        {
            Width = side,
            Height = side,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };

        // ★ 表情源图通常只有 20~40px, 按 40 解码就够 —— 走 CoverLoader 的量化档位,
        //   一张十几 KB。**不要**用 Cover.ImageSource(它按 512 解码, 一张表情就 1MB)。
        // ★ 记下"造这张图时的表情表": 气泡被复用去显示另一条消息时表会换,
        //   用它判断这次异步结果还要不要贴(见 LoadEmoteAsync)。
        var owner = Emotes;
        var url = emote.Url;
        _ = LoadEmoteAsync(image, url, owner);

        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
    }

    private async System.Threading.Tasks.Task LoadEmoteAsync(
        Image image, string url, IReadOnlyDictionary<string, MsgEmote>? owner)
    {
        var bmp = await CoverLoader.LoadAsync(url, 40);
        if (bmp == null) return;
        // 控件可能已经被复用(气泡回收)到另一条消息上 —— 那就丢弃这次结果。
        // ★ 判据用"表情表还是不是造图时那一份", 不能用 IsLoaded:
        //   异步回来时控件可能还没进可视树, 那会把正常结果也丢掉(表情永远不显示)。
        if (!ReferenceEquals(Emotes, owner)) return;
        image.Source = bmp;
    }
}
