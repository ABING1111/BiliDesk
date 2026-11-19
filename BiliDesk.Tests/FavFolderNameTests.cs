using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 收藏夹名称的前置校验(2026-11-19 "新建收藏夹")。
///
/// ★ 为什么单测这个: 校验结果同时驱动输入框的计数、提示文案与「创建」按钮的可用性,
///   而这三个地方都在 UI 里 —— 写错了不会崩, 只会表现成"看着合法却点不了"
///   或者"超长也发出去了, 服务端回一句看不懂的错"。把规则钉在纯函数上最省事。
/// ★ 长度口径必须与 TextBox 的 MaxLength 一致(UTF-16 码元数), 不按"字数/字素簇"数 ——
///   两套口径会打架(计数显示 20/20, 按钮却灰着)。
/// </summary>
public class FavFolderNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void 空或纯空白_不合法(string? raw)
    {
        Assert.False(FavFolderName.TryNormalize(raw, out var title, out var error));
        Assert.Equal("", title);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void 首尾空格会被Trim掉()
    {
        Assert.True(FavFolderName.TryNormalize("  化学  ", out var title, out var error));
        Assert.Equal("化学", title);
        Assert.Null(error);
    }

    [Fact]
    public void 正好上限长度_合法()
    {
        var raw = new string('a', FavFolderName.MaxLength);
        Assert.True(FavFolderName.TryNormalize(raw, out var title, out _));
        Assert.Equal(FavFolderName.MaxLength, title.Length);
    }

    [Fact]
    public void 超过上限_不合法()
    {
        var raw = new string('a', FavFolderName.MaxLength + 1);
        Assert.False(FavFolderName.TryNormalize(raw, out _, out var error));
        Assert.Contains(FavFolderName.MaxLength.ToString(), error);
    }

    /// <summary>
    /// 中文按字符数算(与输入框 MaxLength 一致): 20 个汉字合法, 21 个不合法。
    /// </summary>
    [Fact]
    public void 中文按字符计数()
    {
        Assert.True(FavFolderName.TryNormalize(new string('收', 20), out var ok20, out _));
        Assert.Equal(20, ok20.Length);

        Assert.False(FavFolderName.TryNormalize(new string('收', 21), out _, out _));
    }

    /// <summary>
    /// 首尾空格 Trim 之后长度也要跟着重新判定 —— 不能"按原串 21 字拒绝",
    /// 也不能"Trim 后 20 字却按原串放行"。
    /// </summary>
    [Fact]
    public void 先Trim再判长度()
    {
        var raw = " " + new string('a', FavFolderName.MaxLength) + " ";
        Assert.True(FavFolderName.TryNormalize(raw, out var title, out _));
        Assert.Equal(FavFolderName.MaxLength, title.Length);
    }
}
