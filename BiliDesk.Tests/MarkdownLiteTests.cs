using System.Linq;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// MarkdownLite 的块级 / 行内解析。输入取样自真实的 GitHub Release 正文
/// (标题 + 两级列表 + 加粗 + 行内代码 + 链接 + kbd + 引用 + 围栏代码)。
///
/// ★ 这些断言的目的是"别把标记漏到界面上": 用户看到的应该是 13.5px 的正文,
///   而不是 `**进度条**` 或 `### 新增`。
/// </summary>
public class MarkdownLiteTests
{
    [Fact]
    public void 空输入返回空列表()
    {
        Assert.Empty(MarkdownLite.Parse(null));
        Assert.Empty(MarkdownLite.Parse(""));
        Assert.Empty(MarkdownLite.Parse("   \n  \n"));
    }

    [Fact]
    public void 标题层级()
    {
        var blocks = MarkdownLite.Parse("## 1.8.1 更新内容\n\n### 新增\n\n一句话。");

        Assert.Equal(MdBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(2, blocks[0].Level);
        Assert.Equal("1.8.1 更新内容", blocks[0].Text);

        Assert.Equal(3, blocks[1].Level);
        Assert.Equal("新增", blocks[1].Text);

        Assert.Equal(MdBlockKind.Paragraph, blocks[2].Kind);
        Assert.Equal("一句话。", blocks[2].Text);
    }

    [Fact]
    public void 无空格的井号不当标题()
    {
        var blocks = MarkdownLite.Parse("#107 不是标题");
        Assert.Single(blocks);
        Assert.Equal(MdBlockKind.Paragraph, blocks[0].Kind);
    }

    [Fact]
    public void 两级列表与缩进深度()
    {
        var blocks = MarkdownLite.Parse(
            "- **历史与收藏支持搜索**：说明。\n" +
            "  - 历史按关键词筛选。\n" +
            "  - 收藏直接搜索。\n" +
            "- 第二条。");

        Assert.Equal(4, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(MdBlockKind.Bullet, b.Kind));
        Assert.Equal(0, blocks[0].Depth);
        Assert.Equal(1, blocks[1].Depth);
        Assert.Equal(1, blocks[2].Depth);
        Assert.Equal(0, blocks[3].Depth);
        Assert.Equal("历史按关键词筛选。", blocks[1].Text);
    }

    [Fact]
    public void 有序列表保留序号()
    {
        var blocks = MarkdownLite.Parse("1. 第一步\n2. 第二步");
        Assert.Equal(MdBlockKind.Ordered, blocks[0].Kind);
        Assert.Equal("1", blocks[0].Meta);
        Assert.Equal("第二步", blocks[1].Text);
    }

    [Fact]
    public void 分隔线与引用()
    {
        var blocks = MarkdownLite.Parse("正文。\n\n---\n\n> 已安装旧版本的用户覆盖安装即可。");
        Assert.Equal(MdBlockKind.Paragraph, blocks[0].Kind);
        Assert.Equal(MdBlockKind.Rule, blocks[1].Kind);
        Assert.Equal(MdBlockKind.Quote, blocks[2].Kind);
        Assert.Equal("已安装旧版本的用户覆盖安装即可。", blocks[2].Text);
    }

    [Fact]
    public void 围栏代码原样保留()
    {
        var blocks = MarkdownLite.Parse("**SHA256**\n\n```text\nABC  setup.exe\n```\n\n结尾。");
        var code = blocks.Single(b => b.Kind == MdBlockKind.Code);
        Assert.Equal("text", code.Meta);
        Assert.Equal("ABC  setup.exe", code.Text);
        Assert.Equal("结尾。", blocks[^1].Text);
    }

    [Fact]
    public void 未闭合围栏也不吞内容()
    {
        var blocks = MarkdownLite.Parse("```\n没有收尾的代码块");
        Assert.Single(blocks);
        Assert.Equal(MdBlockKind.Code, blocks[0].Kind);
        Assert.Equal("没有收尾的代码块", blocks[0].Text);
    }

    [Fact]
    public void 行内加粗代码链接与键帽()
    {
        var spans = MarkdownLite.ParseInlines("**粗体**、`代码`、[#98](https://github.com/x/y/issues/98)、<kbd>F5</kbd>");

        Assert.Contains(spans, s => s.Kind == MdSpanKind.Bold && s.Text == "粗体");
        Assert.Contains(spans, s => s.Kind == MdSpanKind.Code && s.Text == "代码");
        Assert.Contains(spans,
            s => s.Kind == MdSpanKind.Link && s.Text == "#98" && s.Url.EndsWith("/issues/98", System.StringComparison.Ordinal));
        Assert.Contains(spans, s => s.Kind == MdSpanKind.Key && s.Text == "F5");
        Assert.DoesNotContain(spans, s => s.Text.Contains("**", System.StringComparison.Ordinal));
    }

    [Fact]
    public void 非网页链接按文字显示()
    {
        var spans = MarkdownLite.ParseInlines("[点我](javascript:alert(1))");
        Assert.DoesNotContain(spans, s => s.Kind == MdSpanKind.Link);
    }

    [Fact]
    public void 未知HTML标签剥掉但保留内文()
    {
        var spans = MarkdownLite.ParseInlines("<details>折叠内容</details>");
        Assert.Equal("折叠内容", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void 转义星号不当加粗()
    {
        var spans = MarkdownLite.ParseInlines(@"不是 **粗体\*\*");
        Assert.DoesNotContain(spans, s => s.Kind == MdSpanKind.Bold);
    }

    [Fact]
    public void CRLF换行等价于LF()
    {
        var lf = MarkdownLite.Parse("### 新增\n\n- 一条。");
        var crlf = MarkdownLite.Parse("### 新增\r\n\r\n- 一条。");
        Assert.Equal(lf.Count, crlf.Count);
        Assert.Equal(lf[1].Text, crlf[1].Text);
    }

    [Fact]
    public void 纯文本化不留标记()
    {
        var text = MarkdownLite.ToPlainText(
            "### 新增\n\n- **滑条按压动画**：白点按下会放大。\n- 见 `PagerMath`。");

        Assert.DoesNotContain("###", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("`", text);
        Assert.Contains("滑条按压动画：白点按下会放大。", text);
    }
}
