using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>
/// FlowDocument 的构建小工具: 逐段写入文字(含 emoji 字体回退) + 造可点击的链接。
///
/// ★ 为什么抽出来: 评论区(<see cref="Views.Controls.CommentText"/>)、播放器简介、
///   图文动态正文(<see cref="Views.Controls.LinkText"/>)三处都要同一套排版规则,
///   各写一份的话 emoji 崩溃的修复(见 <see cref="AppendText"/>)迟早只落在其中一处。
/// </summary>
public static class RichTextBuilder
{
    /// <summary>emoji 字形专用字体(见 <see cref="AppendText"/> 里的崩溃说明)。</summary>
    private static readonly FontFamily EmojiFont = new("Segoe UI Symbol");

    /// <summary>链接的默认颜色资源键(主题换肤时 DynamicResource 会自动跟随)。</summary>
    public const string LinkBrushKey = "AccentTextBrush";

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
    public static void AppendText(Paragraph p, string text,
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

    /// <summary>
    /// 这段文本里有没有需要单独指定 emoji 字体的字符。
    /// 供调用方判断"要不要走切分路径"(评论区的快路径就是这么用的)。
    /// </summary>
    public static bool ContainsEmoji(string text)
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
    /// 造一个可点击的链接(Hyperlink)。
    ///
    /// ★ 不用 NavigateUri: 那会走 WPF 的 RequestNavigate, 默认交给系统浏览器 ——
    ///   用户要的是**内置浏览器**, 所以自己接 Click 走 <see cref="LinkLauncher"/>。
    /// ★ 颜色用 SetResourceReference: 主题换肤时链接色跟着强调色走, 不用逐处重设。
    /// ★ 宿主控件必须是 <c>IsDocumentEnabled = true</c> 的只读 RichTextBox, 否则点不动。
    ///
    /// ★★ 点击判定为什么这么绕(2026-11-14 探针 bd-probe-link 实测钉下来的):
    ///   ① 只读 RichTextBox 里, 鼠标按下会被**文本框**捕获 —— 松开事件到不了 Hyperlink,
    ///      真正触发的是 WPF 内建的那次 Click。所以只接 PreviewMouseLeftButtonUp 会漏。
    ///   ② 拖选文字时那次内建 Click **照样会来**。只看"Up 的位移"挡不住,
    ///      必须在 Click 里也问一次"松开点离按下点多远", 否则用户选一段话就把浏览器点开了。
    ///   ③ 在链接外松开时 Click 不会来, 按下点会残留 —— 下一次正常单击会被误判成拖选,
    ///      表现是"这个链接点了没反应"。所以宿主控件松开后延迟清一次。
    /// </summary>
    public static Hyperlink CreateLink(string displayText, string url, DependencyObject host,
        Brush? foreground = null)
    {
        var link = new Hyperlink { Cursor = System.Windows.Input.Cursors.Hand };
        if (foreground != null)
            link.Foreground = foreground;
        else
            link.SetResourceReference(TextElement.ForegroundProperty, LinkBrushKey);
        link.TextDecorations = TextDecorations.Underline;

        // 链接文字本身不再切 emoji: 地址里不会有 emoji, 走普通 Run 最轻。
        link.Inlines.Add(new Run(displayText));

        // ★★ 同一次点击可能走到两条路: ① 下面自己接的 PreviewMouseLeftButtonUp;
        //   ② WPF 只读 RichTextBox 内建的 Hyperlink 点击(会再抛一次 Click)。
        //   实测: 在 RichTextBox 里, **Up 不会送到 Hyperlink**(鼠标被文本框捕获),
        //   真正触发的是 ② 那条内建 Click。所以两条路都要接, 并用时间戳去重 ——
        //   宁可少开一次, 也不能一次点击弹出两个浏览器窗口。
        long lastOpenTicks = 0;
        void Open()
        {
            var now = Environment.TickCount64;
            if (now - lastOpenTicks < LinkDedupMs) return;
            lastOpenTicks = now;
            LinkLauncher.OpenInBrowser(url, Window.GetWindow(host));
        }

        // ★★ 拖选文字的判定必须**同时**挂在两条路上:
        //   在链接上按下 → 拖到别处 → 松开, 那是"选中这段文字", 不该开浏览器。
        //   实测(探针 bd-probe-link): RichTextBox 里的 Up 只到文本框, 到不了 Hyperlink,
        //   所以只看 Up 的位移拦不住 —— 必须在内建 Click 里也问一次"松开点离按下点多远"。
        //
        // ★ 坐标一律相对**宿主控件**(RichTextBox)量: Hyperlink 是 FrameworkContentElement,
        //   对它调 Mouse.GetPosition 在 RichTextBox 里量出来的不是屏幕上的实际位移
        //   (探针实测拖了 60px 仍判成 0), 会漏判成"单击"。
        var reference = host as System.Windows.IInputElement ?? link;
        Point? down = null;
        // ★ 拖选后 WPF 仍会抛一次 Click: 用这个标记把"这一次 Click 吃掉"。
        //   不能只靠 down 是否为 null —— Click 必须能看出"刚才那一下是拖选"。
        var suppressNextClick = false;
        link.PreviewMouseLeftButtonDown += (_, e) =>
        {
            down = e.GetPosition(reference);
            suppressNextClick = false;
        };

        bool DraggedAway()
        {
            if (down is not { } d) return false;
            var now = System.Windows.Input.Mouse.GetPosition(reference);
            if (Math.Abs(now.X - d.X) > LinkClickTolerance
                || Math.Abs(now.Y - d.Y) > LinkClickTolerance)
                return true;
            // 兜底: 正文里已经有选中内容 ⇒ 用户是在选文字, 不是点链接
            return host is System.Windows.Controls.RichTextBox { Selection.IsEmpty: false };
        }

        link.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!link.IsEnabled) return;
            var dragged = DraggedAway();
            if (dragged)
            {
                // ★ 不清 down: 紧接着 WPF 还会抛一次 Click, 那一次必须也能判出"这是拖选"。
                //   清掉的话 Click 看到 down=null 就当成单击, 浏览器照样弹出来(探针实测踩过)。
                suppressNextClick = true;
                return;
            }
            down = null;
            e.Handled = true;
            Open();
        };

        // ★★ 兜底释放鼠标捕获。
        //
        //   根因(2026-11-14 探针 bd-probe-browser-click 实测):
        //     · 在只读 RichTextBox 里按下链接时, Hyperlink 会 CaptureMouse()(为了判定拖选/单击);
        //     · 而松开事件被文本框截走, **Up 到不了 Hyperlink** ⇒ 捕获永远不释放;
        //     · Win32 的鼠标捕获是**全局**的: 捕获留在 Hyperlink 上之后, 之后**所有**点击
        //       都被重定向给它 —— 包括点刚开出来的浏览器窗口、点那个窗口的关闭按钮;
        //     · 于是每点一下又开一个新内置浏览器窗口。网页没加载出来时窗口是空白的,
        //       用户会到处点, 表现得尤其明显(用户报的正是这个)。
        //   ⇒ 松开时排一次"本次输入处理完之后再释放"(见 ReleaseCaptureLater)。
        //     不能当场释放: 那会把 Hyperlink 正在进行的拖选判定打断。
        //   ★ 只挂 Up: 探针实测按下那一刻捕获还是 null(捕获发生在更晚的处理阶段),
        //     Down 里释放是空操作; Up 这一次才真正收得掉。
        link.PreviewMouseLeftButtonUp += (_, _) => ReleaseCaptureLater(link);

        // ★ 这条才是 RichTextBox 里真正会触发的那条(见上)。
        //   ★ 拖选时它也照样会来: 先看拖选标记, 再看"松开点离按下点多远";
        //     键盘触发(没有按下记录)照常放行。
        link.Click += (_, e) =>
        {
            var suppressed = suppressNextClick;
            suppressNextClick = false;
            var dragged = DraggedAway();
            down = null;
            if (suppressed || dragged) return;
            e.Handled = true;
            Open();
        };

        // ★ 不在宿主控件上挂"松开就清"的处理器: 评论区正文每次重建都会造一批新链接,
        //   挂在宿主上的处理器没人摘, 会随滚动不断累积(内存与事件分发双输)。
        //   上面两条路已经覆盖了所有会留下残留按下点的顺序:
        //     · 在链接上松开 → Up 到了就清(或置 suppress 交给 Click 清);
        //     · 在链接外松开 → Click 不来, 但下一次在链接上按下会先重置 down。

        // 悬停时把"这条能点"的提示交给系统: 地址本身在 ToolTip 里给出,
        // 长链接在窄栏里被折行时也还能看全。
        link.ToolTip = url;
        return link;
    }

    /// <summary>"按下 → 松开"位移超过它就算拖选, 不当点击(与楼中楼预览框同一个阈值)。</summary>
    private const double LinkClickTolerance = 4;

    /// <summary>
    /// 延迟释放鼠标捕获(如果捕获者是这条链接)。
    ///
    /// ★ 为什么要"延迟": Hyperlink 是在**自己的**鼠标处理里捕获鼠标的, 我们在隧道阶段
    ///   当场释放会把它的拖选/单击判定打断。排到输入优先级之后, 等它做完再收尾。
    /// ★ 为什么必须释放: 见 <see cref="CreateLink"/> 里那段根因说明 ——
    ///   捕获留在链接上会让之后**所有**点击都被重定向回它, 每点一下开一个浏览器窗口。
    /// ★ 只在"捕获者是链接自己"时释放: 别的控件(滚动条拖动、弹层)持有捕获时不该被抢。
    /// </summary>
    private static void ReleaseCaptureLater(Hyperlink link)
    {
        var dispatcher = link.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            try
            {
                if (ReferenceEquals(System.Windows.Input.Mouse.Captured, link))
                    System.Windows.Input.Mouse.Capture(null);
            }
            catch
            {
                // 释放失败不影响功能(下一次点击会重新判定)
            }
        }));
    }

    /// <summary>
    /// 同一次点击的去重窗口(ms)。
    /// ★ 自己接的 Preview 与 WPF 内建的 Hyperlink 点击可能各触发一次, 这个窗口把它们并成一次。
    /// </summary>
    private const long LinkDedupMs = 400;
}
