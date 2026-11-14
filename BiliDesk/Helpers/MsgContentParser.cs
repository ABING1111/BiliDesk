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
    {
        var result = new List<MsgTextSegment>();
        if (string.IsNullOrEmpty(text)) return result;

        // 没有表情表 ⇒ 整段一个文字段, 不扫、不建多余对象
        if (emotes == null || emotes.Count == 0)
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
                    if (emotes.TryGetValue(sign, out var emote))
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
