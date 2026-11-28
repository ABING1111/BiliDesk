using System;
using System.Collections.Generic;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>正文里的一小段: 普通文字, 或一个表情</summary>
public readonly struct MsgTextSegment
{
    public MsgTextSegment(string text, MsgEmote? emote)
    {
        Text = text;
        Emote = emote;
    }

    public string Text { get; }

    /// <summary>非 null 表示这一段是一个表情(Text 是它的短代码)</summary>
    public MsgEmote? Emote { get; }

    public bool IsEmote => Emote != null;
}

/// <summary>
/// 把私信正文按「表情短代码」切开。
///
/// ★ 为什么单独抽成纯函数: 私信气泡(<see cref="Views.Controls.MessageText"/>)要拿它排版,
///   输入框的"发送前预览"也想要同一套切法 —— 两处各写一份, 迟早出现
///   "预览里表情是图、发出去是字"这种不一致。它不碰 WPF, 可以直接单测。
///
/// ★ 规则与评论区(<see cref="Views.Controls.CommentText"/>)保持一致:
///   短代码形如 `[doge]`, 最长 40 字符, 只认表情表里真实存在的那些;
///   表里没有的 `[xxx]` 原样当文字显示 —— 不能因为不认识就把用户写的方括号吃掉。
/// </summary>
public static class MsgContentParser
{
    /// <summary>短代码的最大长度(含中括号)。超过它的一定不是表情, 省掉无谓的查表。</summary>
    public const int MaxShortcodeLength = 40;

    public static List<MsgTextSegment> Split(string? text, IReadOnlyDictionary<string, MsgEmote>? emotes)
        => SplitMany(text, emotes == null ? null : new[] { emotes });

    /// <summary>
    /// 多张表情表按**先到先得**合并后切分。
    ///
    /// ★★★ 为什么需要多张表(2026-11-28 修"切页面回来表情变文字"):
    ///   私信的表情图地址来自服务端的 `data.e_infos`, 而实测它**经常漏掉"我自己发的"表情**
    ///   (拉 50 个会话核对: 对方发的表情基本都在, 我发的经常整条 e_infos 都没有)。
    ///   于是重载会话后短代码就找不到图, 被当普通文字画出来。
    ///   ⇒ 调用方按「服务端 e_infos → 本地表情面板」的顺序传两张表:
    ///     服务端给的图更准(含大会员/联动表情), 面板覆盖更全(362 个常用短代码)。
    /// ★ 名字不叫 Split 重载: `Split(text, null)` 会在两个重载之间产生二义性(单测里就踩到了)。
    /// </summary>
    public static List<MsgTextSegment> SplitMany(
        string? text, IEnumerable<IReadOnlyDictionary<string, MsgEmote>>? tables)
    {
        var result = new List<MsgTextSegment>();
        if (string.IsNullOrEmpty(text)) return result;

        // ★ 这里刻意**不**调 Merge 先把表合成一张(2026-11-28 性能):
        //   私信每条消息都有"自带表 + 面板表(362 项)", 一屏 30 条 × 2 次(Rebuild + 宽度预估)
        //   就是 60 个 362 项的字典。改成"命中一个短代码就逐表查" —— 每条消息通常只有
        //   零到两个短代码, 总查表次数反而更少, 且全程零分配。
        var tableList = Materialize(tables);
        if (tableList.Count == 0)
        {
            result.Add(new MsgTextSegment(text, null));
            return result;
        }

        var buffer = new System.Text.StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            result.Add(new MsgTextSegment(buffer.ToString(), null));
            buffer.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var end = text.IndexOf(']', i + 1);
                if (end > i && end - i < MaxShortcodeLength)
                {
                    var sign = text.Substring(i, end - i + 1);
                    if (Lookup(tableList, sign, out var emote))
                    {
                        Flush();
                        result.Add(new MsgTextSegment(sign, emote));
                        i = end + 1;
                        continue;
                    }
                }
            }
            buffer.Append(text[i]);
            i++;
        }
        Flush();

        return result;
    }

    /// <summary>按**先到先得**在多张表里找一个短代码。</summary>
    private static bool Lookup(List<IReadOnlyDictionary<string, MsgEmote>> tables,
        string code, out MsgEmote? emote)
    {
        foreach (var t in tables)
            if (t.TryGetValue(code, out emote)) return true;
        emote = null;
        return false;
    }

    /// <summary>把传进来的表摊平成列表(跳过 null 与空表)。表很少, 这一步开销可以忽略。</summary>
    private static List<IReadOnlyDictionary<string, MsgEmote>> Materialize(
        IEnumerable<IReadOnlyDictionary<string, MsgEmote>>? tables)
    {
        var list = new List<IReadOnlyDictionary<string, MsgEmote>>(2);
        if (tables == null) return list;
        foreach (var t in tables)
            if (t != null && t.Count > 0) list.Add(t);
        return list;
    }

    /// <summary>
    /// 列出正文里**看起来像短代码**的那些片段(形如 `[xxx]`, 长度在限制内)。
    ///
    /// ★ 只做形状判断, 不查表 —— 调用方拿它去几张表里找图(见 MessagesViewModel 的表情兜底)。
    ///   之所以要它: 直接遍历整张面板表(362 个)对每条消息做 Contains 是纯浪费,
    ///   一条消息里通常只有零到一个短代码。
    /// </summary>
    public static IEnumerable<string> Shortcodes(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '[')
            {
                i++;
                continue;
            }

            var end = text.IndexOf(']', i + 1);
            if (end > i && end - i < MaxShortcodeLength)
            {
                yield return text.Substring(i, end - i + 1);
                i = end + 1;
                continue;
            }

            i++;
        }
    }

    /// <summary>
    /// 正文里表情图片的边长(跟着字号走)。
    ///
    /// ★★★ 必须只有**一份**计算(2026-11-28): 排版(<see cref="Views.Controls.MessageText"/>)、
    ///   宽度预估(同一控件里的 UpdateIdealWidth)、评论区(<see cref="Views.Controls.CommentText"/>)
    ///   三处都要这个数。以前各处各写一遍, 一旦有人只改了一处, 表现就是
    ///   "气泡宽度与里面图片的实际宽度对不上" —— 右边多出一截空白, 或者末字被挤折行。
    /// </summary>
    /// <param name="fontSize">控件字号; 非正数按 12.5 兜底(字号未继承到时的时序)</param>
    /// <param name="emoteSize">接口的 size: 1 = 小(约一行文字高), 2 = 大(游戏联动那类)</param>
    public static double EmoteSide(double fontSize, int emoteSize)
    {
        var scale = emoteSize > 1 ? 2.4 : 1.55;
        return Math.Round((fontSize > 0 ? fontSize : 12.5) * scale);
    }

    /// <summary>
    /// 会话列表里的摘要: 把表情短代码换成文字化的占位, 免得列表里出现一堆 `[tv_doge]`。
    /// ★ 不换成图片: 列表行只有一行 11px 的小字, 塞图片会撑高行高、还会让整页多下载几十张图。
    /// </summary>
    public static string ToPreview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("\r", "").Replace("\n", " ");
    }
}
