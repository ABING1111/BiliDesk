using BiliDesk.Models;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// "这条线路能不能 seek"的判据(2026-11-20)。
///
/// 背景: libvlc 的 HTTP seek 会拿首响应的 ETag 发条件 Range(If-Match); 华为云系线路
/// 对超过约 5MB 的条件 Range 一律 412, 于是拖动进度条 → 假 EOF → 从头播。
/// 这里只钉住"哪些 host 属于那一族", 真实网络行为见 .probes/bd-probe-hwseek。
/// </summary>
public class CdnSeekHostTests
{
    [Theory]
    [InlineData("https://upos-sz-mirrorhw.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-mirrorhwb.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-mirrorhwo1.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-mirror08c.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-tf-all-hw.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    public void 华为云系线路判定为不能seek(string url)
        => Assert.True(CdnService.IsSeekHostileUrl(url));

    [Theory]
    [InlineData("https://upos-sz-mirrorali.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-mirroraliov.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-mirrorcoso1.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-tf-all-tx.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-estgoss.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://cn-hnzz-cm-01-02.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    public void 其它线路判定为可以seek(string url)
        => Assert.False(CdnService.IsSeekHostileUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\Videos\\local.mp4")]
    public void 非远端地址不会被误判(string? url)
        => Assert.False(CdnService.IsSeekHostileUrl(url));

    [Fact]
    public async Task 服务端首选落在华为云时改选可seek的备用线路()
    {
        var old = SettingsStore.Instance.CdnMode;
        SettingsStore.Instance.CdnMode = CdnSelectMode.ServerDefault;
        try
        {
            var hostile = "https://upos-sz-mirrorhwo1.bilivideo.com/upgcxcode/66/44/1/1-1-30077.m4s?e=x";
            var safe = "https://cn-hnzz-cm-01-02.bilivideo.com/upgcxcode/66/44/1/1-1-30077.m4s?e=x";
            var picked = await CdnService.Instance.SelectUrlAsync(hostile, new[] { hostile, safe });
            Assert.Equal(safe, picked);
        }
        finally
        {
            SettingsStore.Instance.CdnMode = old;
        }
    }

    [Fact]
    public async Task 候选全是华为云时换到可seek的标准线路()
    {
        var old = SettingsStore.Instance.CdnMode;
        SettingsStore.Instance.CdnMode = CdnSelectMode.ServerDefault;
        try
        {
            var hostile = "https://upos-sz-mirrorhwo1.bilivideo.com/upgcxcode/66/44/1/1-1-30077.m4s?e=x";
            var picked = await CdnService.Instance.SelectUrlAsync(hostile, new[] { hostile });
            Assert.False(CdnService.IsSeekHostileUrl(picked));
            Assert.Equal("upgcxcode/66/44/1/1-1-30077.m4s", new Uri(picked).AbsolutePath.TrimStart('/'));
        }
        finally
        {
            SettingsStore.Instance.CdnMode = old;
        }
    }
}
