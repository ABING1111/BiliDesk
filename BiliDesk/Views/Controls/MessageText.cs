using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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

        // ★ 只读富文本框不该自己滚(滚动条是 Disabled), 见 RichTextWheel。
        RichTextWheel.ForwardToParent(this);

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
        //   "短"字和一句 20 字的话气泡同宽。做法: Rebuild 后按分段真实宽度反设 Width
        //   (见 UpdateIdealWidth); 超过气泡上限时保持 NaN 交给折行。
        //   必须在 Loaded 与 Content/Emotes 变化后各排一次。
        Document.PagePadding = new Thickness(0);
        Loaded += (_, _) => UpdateIdealWidth();
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateIdealWidth(); };

        // ★★ 监听数据源的"表情表补全"(2026-11-28 修"切页面回来表情变文字"的最后一公里):
        //   MsgItem.Emotes 是 get-only 的**可变字典**, 补内容不会触发依赖属性变更 ⇒ 必须
        //   靠 MsgItem 主动发 PropertyChanged(见 MsgItem.RaiseEmotesChanged)。
        //   这里收到就重建排版(同时重算宽度)。
        // ★ 订阅跟着 Loaded/Unloaded 走: 容器被回收(离屏)时摘掉, 回来时重新挂 ——
        //   不然一次切页之后这个气泡就永远收不到补全通知了。
        Loaded += (_, _) => SyncItemSubscription();
        Unloaded += (_, _) => SyncItemSubscription(subscribe: false);
        DataContextChanged += (_, _) => SyncItemSubscription();

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
    /// ★★★ 2026-11-28 重写: 原来用一个隐藏 TextBlock 量**原始正文**, 而正文里表情是
    ///   **短代码字面量**(`[嘘声]`、`[大会员粉豆人_宝贝]`) —— 字面量宽度与渲染成图片后的
    ///   宽度差得很远, 于是气泡被撑得比内容宽, 右侧留一截空白。探针实测(真控件):
    ///     「你好」              控件 27.00 / 内容 25.00 / 空白 2.00px
    ///     「你好[嘘声]」        控件 60.97 / 内容 44.00 / 空白 **16.97px**
    ///     「你好[大会员粉豆人_宝贝]」控件 143.16 / 内容 54.00 / 空白 **89.16px**
    ///   ⇒ 空白 = (短代码字面量宽 − 图片宽) 之和, 短代码越长空白越大。
    ///   现在改成按**分段后的真实宽度**求和: 文字段量文字宽, 表情段按图片边长算
    ///   (尺寸与 <see cref="BuildEmote"/> 共用 <see cref="MsgContentParser.EmoteSide"/>)。
    ///
    /// ★ 为什么不用 Document.ExtentWidth: 控件还没排过版时它是 0, 排版时机又和布局循环打架。
    ///   分段求和是纯计算, 没有这个死结。
    /// </summary>
    private void UpdateIdealWidth()
    {
        try
        {
            var text = Content ?? "";
            if (text.Length == 0) { Width = double.NaN; return; }

            var fontSize = FontSize > 0 ? FontSize : 12.5;
            var brush = TextForeground ?? Brushes.Black;
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            var pixelsPerDip = PixelsPerDip();

            var width = 0.0;
            foreach (var seg in Segments())
            {
                if (seg.IsEmote && seg.Emote != null)
                {
                    width += MsgContentParser.EmoteSide(fontSize, seg.Emote.Size);
                    continue;
                }

                if (seg.Text.Length == 0) continue;
                var ft = new FormattedText(seg.Text, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, fontSize, brush, pixelsPerDip);
                width += ft.WidthIncludingTrailingWhitespace;
            }

            const double bubbleMax = 440 - 22; // 气泡 Border MaxWidth(440) - 左右 Padding(11×2)
            if (width > 0 && width < bubbleMax)
                Width = width + 2; // +2: 富文本排版比量出来的略紧, 留一点余量防"末字被挤折行"
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

    /// <summary>
    /// 正文的分段结果。表情图优先用服务端 e_infos(气泡自带的那张表), 缺的用本地表情面板补。
    ///
    /// ★ 这里再兜一层(2026-11-28): 历史消息的表情本应由 VM 在加载时补全(见 AttachEmotes),
    ///   但乐观追加、翻页、以及"面板晚于历史到位"这几种时序下都可能漏。
    ///   渲染时再查一次面板是最省心的兜底 —— 表是常驻的, 查一次只是字典命中。
    /// </summary>
    private List<MsgTextSegment> Segments()
        => MsgContentParser.SplitMany(Content ?? "", EmoteTables());

    /// <summary>当前可用的表情表: 服务端优先, 本地面板兜底。</summary>
    private IEnumerable<IReadOnlyDictionary<string, MsgEmote>> EmoteTables()
    {
        var own = Emotes;
        if (own is { Count: > 0 }) yield return own;

        var panel = Svc.CommentEmotes.ByCode;
        if (panel.Count > 0) yield return panel;
    }

    private double PixelsPerDip()
    {
        try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { return 1.0; }
    }

    /// <summary>
    /// 数据源(MsgItem)的通知: 只有"表情表变了"需要重建(其余字段不走这个控件的绑定)。
    /// </summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MsgItem.Emotes)) return;
        Rebuild();
    }

    /// <summary>当前正挂着的那个数据源(避免重复订阅/漏摘)。</summary>
    private MsgItem? _subscribedItem;

    /// <summary>
    /// 退订期间数据源可能发过"表情表补全"的通知, 而那时我们收不到 —— 回来后要补一次重建。
    /// </summary>
    private bool _possiblyStale;

    /// <summary>
    /// 把 PropertyChanged 订阅对齐到当前 DataContext。
    /// ★ 用"记住挂了谁"而不是每次先摘 e.OldValue: 容器回收时 DataContext 可能已经变了,
    ///   靠事件参数猜旧值迟早漏摘(漏摘的后果是离屏的旧气泡还在被通知、每补一次就重建一次)。
    /// ★ 退订与"可能变旧"必须成对: 退订期间收到的补全通知会丢, 所以挂回来时要重建一次,
    ///   否则"离屏期间面板到货"这条路上气泡会永远停在短代码。
    /// </summary>
    private void SyncItemSubscription(bool subscribe = true)
    {
        var target = subscribe ? DataContext as MsgItem : null;
        if (ReferenceEquals(_subscribedItem, target)) return;

        if (_subscribedItem != null)
        {
            _subscribedItem.PropertyChanged -= OnItemPropertyChanged;
            _possiblyStale = true;   // 这段空窗期的通知收不到
        }

        _subscribedItem = target;
        if (_subscribedItem != null) _subscribedItem.PropertyChanged += OnItemPropertyChanged;

        if (subscribe && _possiblyStale)
        {
            _possiblyStale = false;
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        Document.Blocks.Clear();
        Document.Blocks.Add(p);

        var text = Content ?? "";
        if (text.Length == 0)
        {
            // ★ 空正文也必须把宽度复位: 容器复用来显示一条空消息时, 残留的 Width 会把气泡撑成
            //   一条"什么都没有的横杠"(旧代码在 Content 变化时走 UpdateIdealWidth 会复位,
            //   但表情表补全触发的 Rebuild 不走那条路 —— 统一在这里收口)。
            Width = double.NaN;
            return;
        }

        var brush = TextForeground;
        var segments = Segments();

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
        // ★ 尺寸必须与 UpdateIdealWidth 的预估**同源**(共用 MsgContentParser.EmoteSide),
        //   否则气泡宽度与图片实际宽度对不上 —— 右边多一截空白或末字被挤折行。
        var side = MsgContentParser.EmoteSide(FontSize, emote.Size);

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
