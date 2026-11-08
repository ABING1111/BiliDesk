using System.Collections.Generic;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 下载选择窗的默认档规则。
///
/// 这套规则值得单测: 它决定用户点开下载窗时**默认勾选哪一档**, 判错就是
/// "看着 1080P 点下载, 默认却勾了 360P" 或"默认勾了一个必降级的档"。
/// </summary>
public class DownloadSelectionTests
{
    private static DownloadQualityOption Q(int qn) => new()
    {
        Qn = qn,
        Label = qn.ToString(),
        VideoUrls = new List<string> { "https://example.invalid/" + qn }
    };

    [Fact]
    public void PickDefault_ExactMatchWins()
    {
        var list = new List<DownloadQualityOption> { Q(120), Q(80), Q(64) };
        Assert.Equal(80, DownloadSelection.PickDefault(list, 80)!.Qn);
    }

    [Fact]
    public void PickDefault_FallsBackToHighestBelowPreferred()
    {
        // 用户默认档是 112(1080P+), 这条视频只给到 1080P/720P ⇒ 应当选 80 而不是 64 或 112
        var list = new List<DownloadQualityOption> { Q(80), Q(64) };
        Assert.Equal(80, DownloadSelection.PickDefault(list, 112)!.Qn);
    }

    [Fact]
    public void PickDefault_FallsBackToHighestWhenNothingBelow()
    {
        // 当前播放 360P(16), 但接口只给 1080P 以上 ⇒ 只能选最高档, 不能返回 null
        var list = new List<DownloadQualityOption> { Q(80), Q(120) };
        Assert.Equal(120, DownloadSelection.PickDefault(list, 16)!.Qn);
    }

    [Fact]
    public void PickDefault_PrefersHighestWhenPreferredIsAuto()
    {
        // 「自动」的哨兵值是 0: 没有任何一档 ≤ 0, 所以落到最高档
        var list = new List<DownloadQualityOption> { Q(80), Q(120), Q(64) };
        Assert.Equal(120, DownloadSelection.PickDefault(list, QualityPreference.AutoQn)!.Qn);
    }

    [Fact]
    public void PickDefault_EmptyListReturnsNull()
    {
        Assert.Null(DownloadSelection.PickDefault(new List<DownloadQualityOption>(), 80));
        Assert.Null(DownloadSelection.PickDefault(null, 80));
    }

    [Fact]
    public void SizeText_ShowsUnknownWhenNoEstimate()
    {
        Assert.Equal("大小未知", Q(80).SizeText);
    }
}
