using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

public class FileNameUtilTests
{
    // ------------------------------------------------------------ Sanitize

    [Fact]
    public void Sanitize_非法字符换成下划线()
    {
        // Windows 文件名非法字符: " < > | : * ? \ /(空格不在表内)
        Assert.Equal("a_b_c", FileNameUtil.Sanitize("a/b\\c", "fb"));
        Assert.Equal("test___1", FileNameUtil.Sanitize("test:*?1", "fb"));
    }

    [Fact]
    public void Sanitize_去掉首尾空白()
    {
        Assert.Equal("标题", FileNameUtil.Sanitize("  标题  ", "fb"));
    }

    [Fact]
    public void Sanitize_超长截断到80()
    {
        var longName = new string('x', 200);
        Assert.Equal(80, FileNameUtil.Sanitize(longName, "fb").Length);
    }

    [Theory]
    [InlineData("???")]
    [InlineData("|/\\")]
    public void Sanitize_全非法字符换为下划线不触发兜底(string name)
    {
        // ★ 现行为: 非法字符是"替换"而不是"删除", 清完是 "___" 这样的下划线串 —— 不是空串,
        //   所以 fallback 不会出现。把这条契约钉死, 防止以后有人把替换改成删除
        //   却没意识到 fallback 语义跟着变了。
        Assert.Equal("___", FileNameUtil.Sanitize(name, "兜底"));
    }

    [Fact]
    public void Sanitize_纯空白被trim成空后走兜底()
    {
        // 空格**不在**非法字符表里(GetInvalidFileNameChars 只含 0-31 控制符与符号),
        // 所以"   "是被 Trim 掉的 → 空串 → fallback。
        Assert.Equal("兜底", FileNameUtil.Sanitize("   ", "兜底"));
    }

    [Fact]
    public void Sanitize_中文与emoji保留()
    {
        Assert.Equal("【4K】混剪🎉", FileNameUtil.Sanitize("【4K】混剪🎉", "fb"));
    }

    // ------------------------------------------------------------ UniquePath

    [Fact]
    public void UniquePath_目录不存在时也返回路径()
    {
        // 调用方负责保证目录存在; 本方法只做字符串拼接, 不碰磁盘
        var p = FileNameUtil.UniquePath(@"Z:\不存在的目录", "视频", ".mp4", "fb");
        Assert.EndsWith("视频.mp4", p);
    }
}
