using System;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>播放器诊断日志(feat/mpv-core): 时间戳格式是排障时的关键, 锁住它。</summary>
public class PlayerLogTests
{
    [Fact]
    public void 每行加时间戳且保留空行()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, 123);
        var text = PlayerLog.WithTimestamps("a\n\nb\n", now);
        Assert.Equal("[10:00:00.123] a\n\n[10:00:00.123] b\n", text);
    }

    [Fact]
    public void CRLF输入不会把回车带进日志()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, 0);
        var text = PlayerLog.WithTimestamps("a\r\nb", now);
        Assert.Equal("[10:00:00.000] a\n[10:00:00.000] b", text);
    }

    [Fact]
    public void 空内容不产生行()
    {
        Assert.Equal("", PlayerLog.WithTimestamps("", DateTime.Now));
    }
}
