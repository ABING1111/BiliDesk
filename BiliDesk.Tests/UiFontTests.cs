using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 界面字体存储语义的纯逻辑测试(2026-11-14)。
/// 只覆盖不碰 WPF/IO 的解析/拼接/清洗函数 —— 与测试工程"被测对象全是纯逻辑类"的约定一致。
/// </summary>
public class UiFontTests
{
    // ---------------- ParseStored: 存储值三形态 ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseStored_空值回落默认(string? stored)
    {
        var (kind, name) = UiFont.ParseStored(stored);
        Assert.Equal(UiFont.StoredKind.Default, kind);
        Assert.Equal("", name);
    }

    [Fact]
    public void ParseStored_系统字体名()
    {
        var (kind, name) = UiFont.ParseStored("Microsoft YaHei UI");
        Assert.Equal(UiFont.StoredKind.System, kind);
        Assert.Equal("Microsoft YaHei UI", name);
    }

    [Fact]
    public void ParseStored_系统字体名去首尾空白()
    {
        var (kind, name) = UiFont.ParseStored("  Arial  ");
        Assert.Equal(UiFont.StoredKind.System, kind);
        Assert.Equal("Arial", name);
    }

    [Fact]
    public void ParseStored_导入前缀()
    {
        var (kind, name) = UiFont.ParseStored("file:MyFont_20261114");
        Assert.Equal(UiFont.StoredKind.File, kind);
        Assert.Equal("MyFont_20261114", name);
    }

    [Fact]
    public void ParseStored_前缀大小写不敏感()
    {
        var (kind, name) = UiFont.ParseStored("FILE:abc");
        Assert.Equal(UiFont.StoredKind.File, kind);
        Assert.Equal("abc", name);
    }

    [Theory]
    [InlineData("file:")]
    [InlineData("file:   ")]
    public void ParseStored_前缀后为空回落默认(string stored)
    {
        var (kind, _) = UiFont.ParseStored(stored);
        Assert.Equal(UiFont.StoredKind.Default, kind);
    }

    // ---------------- BuildSystemSource: 中文回退栈 ----------------

    [Fact]
    public void BuildSystemSource_拼上中文回退()
    {
        // 纯西文字体下中文仍可读: 回退栈必须跟在用户所选字体之后
        Assert.Equal("Arial, Microsoft YaHei UI, Microsoft YaHei", UiFont.BuildSystemSource("Arial"));
    }

    [Fact]
    public void BuildSystemSource_默认栈常量未被改动()
    {
        // 默认字体栈是 FluentWindow 搬过来的单一来源, 改动它等于改全应用默认观感 —— 用测试钉住
        Assert.Equal("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI, Microsoft YaHei",
            UiFont.DefaultSource);
    }

    // ---------------- SanitizeBaseName: 导入目录名清洗 ----------------

    [Theory]
    [InlineData("MyFont.ttf", "MyFont")]
    [InlineData("思源黑体.otf", "思源黑体")]
    [InlineData("archive.tar.gz", "archive.tar")]   // 只去最后一个扩展名
    [InlineData("  spaced.ttf  ", "spaced")]
    public void SanitizeBaseName_常规文件名(string input, string expected)
    {
        Assert.Equal(expected, UiFont.SanitizeBaseName(input));
    }

    [Fact]
    public void SanitizeBaseName_非法字符替换为下划线()
    {
        Assert.Equal("a_b_c", UiFont.SanitizeBaseName("a<b>c.ttf"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".ttf")]
    public void SanitizeBaseName_空名回落font(string? input)
    {
        Assert.Equal("font", UiFont.SanitizeBaseName(input));
    }

    [Fact]
    public void SanitizeBaseName_超长截断到60()
    {
        var input = new string('x', 200) + ".ttf";
        Assert.Equal(60, UiFont.SanitizeBaseName(input).Length);
    }

    // ---------------- PickRegularWeight: 基准字重决策(2026-11-19) ----------------

    [Fact]
    public void PickRegularWeight_可变字体取fvar默认实例()
    {
        // MiSans VF 实测: 命名实例 150..700 没有 400, fvar 默认 = 330(Regular);
        // 事实来自 SfntReader, 同一文件的所有字面都带同一个 fvar 默认值
        var faces = new (int, string?, int?)[] { (150, "Thin", 330), (330, "Regular", 330), (380, "Medium", 330) };
        Assert.Equal(330, UiFont.PickRegularWeight(faces));
    }

    [Fact]
    public void PickRegularWeight_静态族取Regular命名的字面()
    {
        var faces = new (int, string?, int?)[] { (150, "Thin", null), (330, "Regular", null), (380, "Medium", null) };
        Assert.Equal(330, UiFont.PickRegularWeight(faces));
    }

    [Fact]
    public void PickRegularWeight_标准族维持400()
    {
        var faces = new (int, string?, int?)[] { (300, "Light", null), (400, "Regular", null), (700, "Bold", null) };
        Assert.Equal(400, UiFont.PickRegularWeight(faces));
    }

    [Fact]
    public void PickRegularWeight_判不出Regular回落400()
    {
        var faces = new (int, string?, int?)[] { (300, "Light", null), (500, "Medium", null) };
        Assert.Equal(400, UiFont.PickRegularWeight(faces));
    }

    [Fact]
    public void PickRegularWeight_中文常规命名()
    {
        var faces = new (int, string?, int?)[] { (330, "常规", null) };
        Assert.Equal(330, UiFont.PickRegularWeight(faces));
    }

    [Fact]
    public void PickRegularWeight_空列表回落400()
    {
        Assert.Equal(400, UiFont.PickRegularWeight(Array.Empty<(int, string?, int?)>()));
    }
}