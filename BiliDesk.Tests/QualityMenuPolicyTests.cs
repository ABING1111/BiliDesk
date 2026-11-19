using System.Linq;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 清晰度菜单的合并规则。
///
/// ★ 这套规则是"切到 720P 后 1080P 从菜单消失"的修复核心: ≤720P 走 durl,
///   而 durl 的 accept_quality 只到 720P。合并保证同一视频里已经见过的更高档位
///   不会因为切档被覆盖掉。
/// </summary>
public class QualityMenuPolicyTests
{
    [Fact]
    public void Merge_KeepsHigherTiersReturnedByEarlierPlayback()
    {
        var merged = QualityMenuPolicy.Merge(
            new[] { (80, "1080P"), (64, "720P"), (32, "480P"), (16, "360P") },
            new[] { (64, "720P"), (16, "360P") });

        Assert.Equal(new[] { 80, 64, 32, 16 }, merged.Select(x => x.qn));
    }

    [Fact]
    public void Merge_AddsHigherTiersLearnedFromLaterPlayback()
    {
        var merged = QualityMenuPolicy.Merge(
            new[] { (64, "720P"), (16, "360P") },
            new[] { (120, "4K"), (80, "1080P"), (64, "720P") });

        Assert.Equal(new[] { 120, 80, 64, 16 }, merged.Select(x => x.qn));
    }

    [Fact]
    public void Merge_DedupesAndSortsDescending()
    {
        var merged = QualityMenuPolicy.Merge(
            new[] { (16, "360P"), (80, "1080P"), (64, "720P") },
            new[] { (32, "480P"), (80, "1080P") });

        Assert.Equal(new[] { 80, 64, 32, 16 }, merged.Select(x => x.qn));
    }

    [Fact]
    public void Merge_IncomingLabelWinsForSameQuality()
    {
        var merged = QualityMenuPolicy.Merge(
            new[] { (80, "旧名字"), (64, "720P") },
            new[] { (80, "1080P") });

        Assert.Equal("1080P", merged.Single(x => x.qn == 80).label);
    }

    [Fact]
    public void Merge_SkipsInvalidAndUnrenderableTiers()
    {
        var merged = QualityMenuPolicy.Merge(
            new[] { (0, "自动"), (-1, "坏值"), (126, "杜比视界") },
            new[] { (125, "HDR"), (127, "8K"), (80, "1080P") });

        Assert.Equal(new[] { 80 }, merged.Select(x => x.qn));
    }

    [Fact]
    public void Merge_HandlesEmptyInputs()
    {
        Assert.Empty(QualityMenuPolicy.Merge(
            System.Array.Empty<(int qn, string label)>(),
            System.Array.Empty<(int qn, string label)>()));

        var incomingOnly = QualityMenuPolicy.Merge(
            System.Array.Empty<(int qn, string label)>(),
            new[] { (64, "720P"), (16, "360P") });
        Assert.Equal(new[] { 64, 16 }, incomingOnly.Select(x => x.qn));

        var currentOnly = QualityMenuPolicy.Merge(
            new[] { (80, "1080P") },
            System.Array.Empty<(int qn, string label)>());
        Assert.Equal(new[] { 80 }, currentOnly.Select(x => x.qn));
    }
}
