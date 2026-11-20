using System.Collections.Generic;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 屏蔽关键词的纯逻辑(2026-11-20, issue #98)。
/// 覆盖: 分隔符拆分、忽略大小写去重、超长截断、总数封顶、单条删除与持久化拼接。
/// </summary>
public class FilterKeywordsTests
{
    [Fact]
    public void Split_按空格逗号顿号分号拆分并去空()
    {
        var terms = FilterKeywords.Split("  鬼畜, 剪辑，盘点、 混剪 ; 二创；\t");
        Assert.Equal(new[] { "鬼畜", "剪辑", "盘点", "混剪", "二创" }, terms);
    }

    [Fact]
    public void Split_空串返回空数组()
    {
        Assert.Empty(FilterKeywords.Split(""));
        Assert.Empty(FilterKeywords.Split("   "));
        Assert.Empty(FilterKeywords.Split(null));
    }

    [Fact]
    public void Merge_忽略大小写去重且不改入参()
    {
        var existing = new List<string> { "Reaction", "鬼畜" };
        var merged = FilterKeywords.Merge(existing, "reaction 剪辑");

        Assert.Equal(new[] { "Reaction", "鬼畜", "剪辑" }, merged);
        Assert.Equal(2, existing.Count);   // 入参保持不变
    }

    [Fact]
    public void Merge_超长词按上限截断()
    {
        var longTerm = new string('x', FilterKeywords.MaxLength + 10);
        var merged = FilterKeywords.Merge(new List<string>(), longTerm);

        Assert.Single(merged);
        Assert.Equal(FilterKeywords.MaxLength, merged[0].Length);
    }

    [Fact]
    public void Merge_总数不超过上限()
    {
        var existing = new List<string>();
        for (var i = 0; i < FilterKeywords.MaxTerms; i++) existing.Add("词" + i);

        var merged = FilterKeywords.Merge(existing, "多出来的词");
        Assert.Equal(FilterKeywords.MaxTerms, merged.Count);
        Assert.DoesNotContain("多出来的词", merged);
    }

    [Fact]
    public void Merge_重复词不会突破上限()
    {
        var existing = new List<string>();
        for (var i = 0; i < FilterKeywords.MaxTerms; i++) existing.Add("词" + i);

        // 全部是已存在的词: 条数不变, 不应该凭空多出来一条
        var merged = FilterKeywords.Merge(existing, "词1 词2 词3");
        Assert.Equal(FilterKeywords.MaxTerms, merged.Count);
    }

    [Fact]
    public void Remove_忽略大小写并返回新列表()
    {
        var existing = new List<string> { "Reaction", "鬼畜", "剪辑" };
        var next = FilterKeywords.Remove(existing, "reaction");

        Assert.Equal(new[] { "鬼畜", "剪辑" }, next);
        Assert.Equal(3, existing.Count);
    }

    [Fact]
    public void Remove_词不存在时原样返回()
    {
        var existing = new List<string> { "鬼畜" };
        Assert.Single(FilterKeywords.Remove(existing, "不存在"));
        Assert.Single(FilterKeywords.Remove(existing, "  "));
    }

    [Fact]
    public void Join_与Split往返一致()
    {
        var raw = "鬼畜 剪辑，盘点";
        var joined = FilterKeywords.Join(FilterKeywords.Merge(new List<string>(), raw));
        Assert.Equal(new[] { "鬼畜", "剪辑", "盘点" }, FilterKeywords.Split(joined));
    }
}
