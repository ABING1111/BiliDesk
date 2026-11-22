namespace BiliDesk.Helpers;

/// <summary>
/// 把表情短代码插进评论输入框的**光标处**(纯计算, 不依赖 WPF, 可直接单测)。
///
/// ★ 为什么不是追加到末尾: 用户常常是"写一半 → 点个表情 → 接着写",
///   追加会把表情甩到句子外面去。私信输入框(OnEmoteClick)已经是这个行为, 评论这边保持一致。
/// ★ 为什么要单独抽出来: 播放器与动态详情两个输入框都要这段逻辑,
///   而"光标越界怎么兜、超字数怎么办"这两条边界一旦各写一遍就会分叉。
/// </summary>
public static class EmoteInsert
{
    /// <summary>评论输入框的字数上限(B 站单条评论 1000 字, 两个输入框的 MaxLength 都是它)。</summary>
    public const int DefaultMaxLength = 1000;

    /// <summary>
    /// 算出插入后的文本与光标位置。
    /// </summary>
    /// <param name="text">当前文本(null 视作空串)</param>
    /// <param name="caret">光标位置。越界(&lt;0 或 &gt;长度)按**末尾**处理 ——
    /// 输入框失去焦点后 CaretIndex 仍可读, 但某些时序下会是 0 或过期值, 不该把表情插到句首。</param>
    /// <param name="code">表情短代码(形如 <c>[doge]</c>)。空串直接返回原样。</param>
    /// <param name="maxLength">插入后的长度上限。超了就**不插**, 返回原文本与原光标。</param>
    public static (string Text, int Caret) Insert(
        string? text, int caret, string? code, int maxLength = DefaultMaxLength)
    {
        var src = text ?? "";
        if (string.IsNullOrEmpty(code)) return (src, Clamp(caret, src.Length));
        if (maxLength <= 0) maxLength = DefaultMaxLength;

        var at = Clamp(caret, src.Length);
        // 超上限就整条不插: 半截短代码发出去只会变成字面的 "[do", 比"点了没反应"更糟
        if (src.Length + code.Length > maxLength) return (src, at);

        return (src.Insert(at, code), at + code.Length);
    }

    private static int Clamp(int caret, int length)
        => caret < 0 || caret > length ? length : caret;
}
