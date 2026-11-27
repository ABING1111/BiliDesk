using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 直播间发送弹幕的纯逻辑测试(2026-11-28 新增)。
///
/// ★ 发送接口的真机验证必须登录 + 有可用直播间, 不适合放进单测;
///   这里只钉死"输入整理"和"错误码转人话"两块纯函数, 避免
///   "超长/换行/emoji 截断"这类边界在真机上靠手试。
/// </summary>
public class LiveDanmakuSendPolicyTests
{
    // ------------------------------------------------------------ 正文归一化

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void NormalizeText_空白输入返回空串(string? input)
    {
        Assert.Equal("", LiveDanmakuSendPolicy.NormalizeText(input));
    }

    [Fact]
    public void NormalizeText_去掉首尾空白()
    {
        Assert.Equal("前方高能", LiveDanmakuSendPolicy.NormalizeText("  前方高能  "));
    }

    [Theory]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\nb", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\r\n\r\nb", "a b")]
    public void NormalizeText_换行与制表符折成单个空格(string input, string expected)
    {
        Assert.Equal(expected, LiveDanmakuSendPolicy.NormalizeText(input));
    }

    [Fact]
    public void NormalizeText_超过20字按上限截断()
    {
        var input = new string('弹', 25);

        var result = LiveDanmakuSendPolicy.NormalizeText(input);

        Assert.Equal(20, result.Length);
        Assert.Equal(input[..20], result);
    }

    [Fact]
    public void NormalizeText_不会截出半个代理对()
    {
        // 1 个半角字符 + 10 个 emoji = 21 个 UTF-16 char。
        // 直接截前 20 个会留下半个 emoji(高位代理), 归一化必须再退一格。
        var input = "a" + string.Concat(System.Linq.Enumerable.Repeat("😀", 10));

        var result = LiveDanmakuSendPolicy.NormalizeText(input);

        Assert.Equal(19, result.Length);
        Assert.Equal("a" + string.Concat(System.Linq.Enumerable.Repeat("😀", 9)), result);
        Assert.False(char.IsHighSurrogate(result[^1]));
    }

    [Fact]
    public void NormalizeText_正好20字不截断()
    {
        var input = new string('弹', 20);

        Assert.Equal(input, LiveDanmakuSendPolicy.NormalizeText(input));
    }

    // ------------------------------------------------------------ 错误码

    [Theory]
    [InlineData(-101, "请先登录后再发送弹幕")]
    [InlineData(-111, "登录状态已失效, 请重新登录")]
    [InlineData(-400, "弹幕内容不合法, 请修改后再试")]
    [InlineData(-403, "当前账号无权在该直播间发送弹幕")]
    [InlineData(-412, "当前网络请求过于频繁, 请稍后再试")]
    [InlineData(10031, "发送频率过快, 请稍后再试")]
    [InlineData(10032, "弹幕功能已被冻结")]
    [InlineData(1003212, "弹幕内容超出长度限制")]
    [InlineData(10065, "你已被房间管理员禁言")]
    [InlineData(10066, "你已被系统管理员禁言")]
    public void DescribeError_已知错误码给中文提示(int code, string expected)
    {
        Assert.Equal(expected, LiveDanmakuSendPolicy.DescribeError(code, "server message"));
    }

    [Fact]
    public void DescribeError_未知错误码优先用服务端message()
    {
        Assert.Equal("服务端自定义错误",
            LiveDanmakuSendPolicy.DescribeError(12345, "  服务端自定义错误  "));
    }

    [Fact]
    public void DescribeError_message为空时用code兜底()
    {
        Assert.Equal("发送失败 (code 12345)",
            LiveDanmakuSendPolicy.DescribeError(12345, "   "));
    }
}
