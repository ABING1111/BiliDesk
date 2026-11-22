using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 评论表情插入(2026-11-22 评论表情发送)。
///
/// ★ 关键不变量:
///   ① 插到**光标处**, 不是末尾 —— 用户写一半点表情, 表情不该跑到句子外面;
///   ② 光标越界一律按末尾兜(失焦后 CaretIndex 可能读到 0 或过期值);
///   ③ 超 1000 字**整条不插** —— 半截短代码发出去只会变成字面文本。
/// </summary>
public class EmoteInsertTests
{
    [Fact]
    public void 插到中间并返回新光标位置()
    {
        var (text, caret) = EmoteInsert.Insert("你好世界", 2, "[doge]");

        Assert.Equal("你好[doge]世界", text);
        Assert.Equal(8, caret);
    }

    [Fact]
    public void 插到开头()
    {
        var (text, caret) = EmoteInsert.Insert("哈哈哈", 0, "[doge]");

        Assert.Equal("[doge]哈哈哈", text);
        Assert.Equal(6, caret);
    }

    [Fact]
    public void 插到末尾()
    {
        var (text, caret) = EmoteInsert.Insert("哈哈哈", 3, "[doge]");

        Assert.Equal("哈哈哈[doge]", text);
        Assert.Equal(9, caret);
    }

    [Fact]
    public void 空文本上插入()
    {
        var (text, caret) = EmoteInsert.Insert("", 0, "[doge]");

        Assert.Equal("[doge]", text);
        Assert.Equal(6, caret);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void 光标越界回落到末尾(int caret)
    {
        var (text, newCaret) = EmoteInsert.Insert("哈哈哈", caret, "[doge]");

        Assert.Equal("哈哈哈[doge]", text);
        Assert.Equal(9, newCaret);
    }

    [Fact]
    public void 空短代码不改变文本()
    {
        var (text, caret) = EmoteInsert.Insert("哈哈哈", 1, "");

        Assert.Equal("哈哈哈", text);
        Assert.Equal(1, caret);
    }

    [Fact]
    public void 超过字数上限时不插入且光标不动()
    {
        var src = new string('a', 997);

        var (text, caret) = EmoteInsert.Insert(src, 10, "[doge]");

        Assert.Equal(src, text);
        Assert.Equal(10, caret);
    }

    [Fact]
    public void 正好填满字数上限时可以插入()
    {
        var src = new string('a', 994);

        var (text, caret) = EmoteInsert.Insert(src, src.Length, "[doge]");

        Assert.Equal(1000, text.Length);
        Assert.Equal(1000, caret);
    }

    [Fact]
    public void null文本按空串处理()
    {
        var (text, caret) = EmoteInsert.Insert(null, 0, "[doge]");

        Assert.Equal("[doge]", text);
        Assert.Equal(6, caret);
    }
}
