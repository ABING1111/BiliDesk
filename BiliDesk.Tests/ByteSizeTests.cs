using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(-100, "0 B")]          // 负数按 0
    [InlineData(1023, "1023 B")]
    public void Format_字节档(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Theory]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024 - 1024, "1023 KB")]
    public void Format_KB档(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Theory]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(1024L * 1024 * 5 / 2, "2.5 MB")]
    public void Format_MB档(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Theory]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(1536L * 1024 * 1024, "1.5 GB")]
    [InlineData(1024L * 1024 * 1024 * 3 + 512L * 1024 * 1024, "3.5 GB")]
    public void Format_GB档(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public void Format_GB只保留两位小数()
    {
        // 0.## : 1.234 GB 显示为 1.23
        Assert.Equal("1.23 GB", ByteSize.Format((long)(1.234 * 1024 * 1024 * 1024)));
    }
}
