using BiliDesk.Models;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 线路选择的两条不变量(2026-11-21)。
///
/// ① **不参与"能不能 seek"的判断**(2026-11-20 勘误)。
///
/// ★ 这里原来叫 CdnSeekHostTests, 钉的是"华为云系线路不能 seek、要绕开"。
///   那个结论当晚被实测推翻, 详见 CdnService 里"seek 兼容性(2026-11-20, 已勘误)"那段:
///     · 同一份签名换到任何 host, 条件 Range 在 5M~350M 偏移上全部 206;
///     · 被排除的 mirrorhw 实测 :start-time 5/5 成功, 白名单里的 mirroraliov 是 0/5。
///   ⇒ 现在钉的是**反面**: 选路必须忠实返回服务端给的那条, 不许因为 host 名字做取舍。
///   真正的修复在播放器重建路径(见 PlayerWindow 的 _startTimeRebuildRetried)。
///
/// ② **只剩两种策略**(2026-11-21 删除"自动测速")。
///   跟随服务端 = 原样返回首选; 手动指定 = 换成那一家; 其余一切(含旧配置残留的 Auto)
///   都按跟随服务端处理。Auto 枚举成员保留只为反序列化不炸, 绝不能再驱动行为。
/// </summary>
public class CdnSelectionTests
{
    [Theory]
    [InlineData("https://upos-sz-mirrorhw.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-mirroraliov.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    [InlineData("https://upos-sz-estgoss.bilivideo.com/upgcxcode/1/2/3.m4s?e=x")]
    public async Task 跟随服务端时原样返回首选线路(string url)
    {
        var old = SettingsStore.Instance.CdnMode;
        var oldPcdn = SettingsStore.Instance.BlockPcdn;
        SettingsStore.Instance.CdnMode = CdnSelectMode.ServerDefault;
        SettingsStore.Instance.BlockPcdn = false;
        try
        {
            var picked = await CdnService.Instance.SelectUrlAsync(url, new[] { url });
            Assert.Equal(url, picked);
        }
        finally
        {
            SettingsStore.Instance.CdnMode = old;
            SettingsStore.Instance.BlockPcdn = oldPcdn;
        }
    }

    [Fact]
    public async Task 跟随服务端时不会把首选换成其它候选()
    {
        var old = SettingsStore.Instance.CdnMode;
        var oldPcdn = SettingsStore.Instance.BlockPcdn;
        SettingsStore.Instance.CdnMode = CdnSelectMode.ServerDefault;
        SettingsStore.Instance.BlockPcdn = false;
        try
        {
            var first = "https://upos-sz-mirrorhwo1.bilivideo.com/upgcxcode/66/44/1/1-1-30077.m4s?e=x";
            var second = "https://cn-hnzz-cm-01-02.bilivideo.com/upgcxcode/66/44/1/1-1-30077.m4s?e=x";
            var picked = await CdnService.Instance.SelectUrlAsync(first, new[] { first, second });
            Assert.Equal(first, picked);
        }
        finally
        {
            SettingsStore.Instance.CdnMode = old;
            SettingsStore.Instance.BlockPcdn = oldPcdn;
        }
    }

    /// <summary>
    /// 音轨那条路(SelectSecondaryUrl)同样不该按 host 挑三拣四 ——
    /// 实测"只换音轨 host"时 :start-time 是 12/12 成功, 音轨从来不是问题所在。
    /// </summary>
    [Fact]
    public void 音轨跟随服务端时原样返回首选()
    {
        var old = SettingsStore.Instance.CdnMode;
        var oldPcdn = SettingsStore.Instance.BlockPcdn;
        SettingsStore.Instance.CdnMode = CdnSelectMode.ServerDefault;
        SettingsStore.Instance.BlockPcdn = false;
        try
        {
            var first = "https://upos-sz-mirrorhw.bilivideo.com/upgcxcode/1/2/3-30280.m4s?e=x";
            var second = "https://upos-sz-mirrorali.bilivideo.com/upgcxcode/1/2/3-30280.m4s?e=x";
            Assert.Equal(first, CdnService.Instance.SelectSecondaryUrl(first, new[] { first, second }));
        }
        finally
        {
            SettingsStore.Instance.CdnMode = old;
            SettingsStore.Instance.BlockPcdn = oldPcdn;
        }
    }

    /// <summary>
    /// ★ 旧配置里写着 `"CdnMode": "Auto"` 时必须按**跟随服务端**处理。
    /// 枚举成员保留只是为了让 JsonStringEnumConverter 还能解析那份配置 ——
    /// 它一旦漏进选择逻辑, 用户就会看到"设置页没有这一项, 行为却按它走"。
    /// </summary>
    [Fact]
    public async Task 旧配置残留的Auto按跟随服务端处理()
    {
        var old = SettingsStore.Instance.CdnMode;
        var oldPcdn = SettingsStore.Instance.BlockPcdn;
        SettingsStore.Instance.CdnMode = CdnSelectMode.Auto;
        SettingsStore.Instance.BlockPcdn = false;
        try
        {
            var first = "https://upos-sz-mirrorhwo1.bilivideo.com/upgcxcode/66/44/1/1-1-30077.m4s?e=x";
            var second = "https://upos-sz-mirrorali.bilivideo.com/upgcxcode/66/44/1/1-1-30077.m4s?e=x";
            Assert.Equal(first, await CdnService.Instance.SelectUrlAsync(first, new[] { first, second }));
            Assert.Equal(first, CdnService.Instance.SelectSecondaryUrl(first, new[] { first, second }));
        }
        finally
        {
            SettingsStore.Instance.CdnMode = old;
            SettingsStore.Instance.BlockPcdn = oldPcdn;
        }
    }

    /// <summary>手动指定: 换成用户选定的那一家, path/query 原样保留(签名在里面)。</summary>
    [Fact]
    public async Task 手动指定时换成选定的那一家()
    {
        var old = SettingsStore.Instance.CdnMode;
        var oldId = SettingsStore.Instance.CdnManualId;
        var oldPcdn = SettingsStore.Instance.BlockPcdn;
        SettingsStore.Instance.CdnMode = CdnSelectMode.Manual;
        SettingsStore.Instance.CdnManualId = "cos";
        SettingsStore.Instance.BlockPcdn = false;
        try
        {
            const string tail = "upgcxcode/66/44/1/1-1-30077.m4s?e=x&deadline=1";
            var first = "https://upos-sz-mirrorali.bilivideo.com/" + tail;
            var picked = await CdnService.Instance.SelectUrlAsync(first, new[] { first });
            Assert.Equal("upos-sz-mirrorcos.bilivideo.com", new Uri(picked).Host);
            Assert.Equal(tail, new Uri(picked).PathAndQuery.TrimStart('/'));
        }
        finally
        {
            SettingsStore.Instance.CdnMode = old;
            SettingsStore.Instance.CdnManualId = oldId;
            SettingsStore.Instance.BlockPcdn = oldPcdn;
        }
    }

    /// <summary>
    /// 屏蔽 PCDN 与"选哪家"是两件独立的事: 开启时跳过 PCDN 候选;
    /// 但**过滤后一条不剩**时必须回退到不过滤 —— 宁可走 PCDN, 也不能让视频播不了。
    /// </summary>
    [Fact]
    public async Task 屏蔽PCDN时跳过它但不会让视频播不了()
    {
        var old = SettingsStore.Instance.CdnMode;
        var oldPcdn = SettingsStore.Instance.BlockPcdn;
        SettingsStore.Instance.CdnMode = CdnSelectMode.ServerDefault;
        SettingsStore.Instance.BlockPcdn = true;
        try
        {
            var pcdn = "https://x.mcdn.bilivideo.cn/upgcxcode/1/2/3.m4s?e=x";
            var normal = "https://upos-sz-mirrorali.bilivideo.com/upgcxcode/1/2/3.m4s?e=x";
            Assert.Equal(normal, await CdnService.Instance.SelectUrlAsync(pcdn, new[] { pcdn, normal }));

            // 全是 PCDN: 过滤会清空候选池, 此时必须退回原样(能播 > 洁癖)
            Assert.Equal(pcdn, await CdnService.Instance.SelectUrlAsync(pcdn, new[] { pcdn }));
        }
        finally
        {
            SettingsStore.Instance.CdnMode = old;
            SettingsStore.Instance.BlockPcdn = oldPcdn;
        }
    }
}
