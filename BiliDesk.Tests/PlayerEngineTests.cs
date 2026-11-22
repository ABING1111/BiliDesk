using System;
using BiliDesk.Playback;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>播放引擎抽象(feat/mpv-core)里与原生无关的那部分。</summary>
public class PlayerEngineTests
{
    [Fact]
    public void 位置事件按间隔节流()
    {
        var throttle = new TimeChangeThrottle(250);
        Assert.True(throttle.ShouldRaise(1000));
        Assert.False(throttle.ShouldRaise(1100));
        Assert.False(throttle.ShouldRaise(1249));
        Assert.True(throttle.ShouldRaise(1250));
    }

    [Fact]
    public void 节流复位后立刻可以再触发()
    {
        var throttle = new TimeChangeThrottle(250);
        Assert.True(throttle.ShouldRaise(1000));
        Assert.False(throttle.ShouldRaise(1010));
        throttle.Reset();
        Assert.True(throttle.ShouldRaise(1011));
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(500, 1.0)]
    [InlineData(1000, 1.0)]
    [InlineData(3000, 3.0)]
    [InlineData(20000, 10.0)]
    public void 缓冲毫秒换算成mpv预读秒数(int cacheMs, double expected)
    {
        Assert.Equal(expected, MpvEngine.MapCacheToReadAheadSeconds(cacheMs), 3);
    }

    [Fact]
    public void 播放请求默认值符合B站语义()
    {
        var media = new PlayerMedia { Url = "https://example.com/v.m4s" };
        Assert.Equal("https://www.bilibili.com/", media.Referer);
        Assert.Equal(1000, media.NetworkCacheMs);
        Assert.Null(media.AudioUrl);
        Assert.False(media.IsLive);
        Assert.Equal(0, media.StartMs);
    }

    [Fact]
    public void 引擎可用性只对mpv有意义()
    {
        // 不依赖原生库是否存在, 只验证枚举分支
        Assert.False(PlayerEngineFactory.IsAvailable(PlayerEngineKind.Vlc));
    }
}
