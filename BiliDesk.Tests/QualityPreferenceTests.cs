using System.Linq;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「默认画质」档位表 + "播放器暂时播不了"的判据。
///
/// 这套规则值得单测: 它同时决定**设置页能预选什么**与**播放器清晰度菜单列什么**
/// (2026-11-14 起杜比视界/HDR/8K 三档从菜单里移除)。判据写漏一档, 用户就会点到一个
/// 画面发灰/卡死的选项; 而 Normalize 写漏, 手改坏的旧配置会把不支持的 qn 直接发进取流请求。
/// </summary>
public class QualityPreferenceTests
{
    [Theory]
    [InlineData(125)]   // HDR
    [InlineData(126)]   // 杜比视界
    [InlineData(127)]   // 8K
    public void IsUnsupportedTier_TrueForUnrenderableTiers(int qn) =>
        Assert.True(QualityPreference.IsUnsupportedTier(qn));

    [Theory]
    [InlineData(120)]   // 4K
    [InlineData(116)]   // 1080P 60帧
    [InlineData(112)]   // 1080P 高码率
    [InlineData(80)]    // 1080P
    [InlineData(74)]    // 720P 60帧
    [InlineData(64)]    // 720P
    [InlineData(32)]    // 480P
    [InlineData(16)]    // 360P
    public void IsUnsupportedTier_FalseForPlayableTiers(int qn) =>
        Assert.False(QualityPreference.IsUnsupportedTier(qn));

    [Fact]
    public void All_NeverOffersAnUnrenderableTier() =>
        Assert.DoesNotContain(QualityPreference.All, c => QualityPreference.IsUnsupportedTier(c.Qn));

    [Fact]
    public void All_HighestOfferedTierIs4K() =>
        Assert.Equal(120, QualityPreference.All
            .Where(c => c.Qn != QualityPreference.AutoQn)
            .Max(c => c.Qn));

    [Fact]
    public void HighestQn_IsItselfPlayable() =>
        Assert.False(QualityPreference.IsUnsupportedTier(QualityPreference.HighestQn));

    [Fact]
    public void RequestQn_AutoMapsToPlayableCeiling() =>
        Assert.Equal(120, QualityPreference.RequestQn(QualityPreference.AutoQn));

    [Fact]
    public void Normalize_UnrenderableTierFallsBackToDefault()
    {
        // 旧配置/手改配置里可能存着 126: 它既不在档位表里, 也不能被当成请求 qn 发出去
        Assert.Equal(QualityPreference.DefaultQn, QualityPreference.Normalize(126));
        Assert.Equal(QualityPreference.DefaultQn, QualityPreference.RequestQn(126));
    }

    [Fact]
    public void LabelOf_AutoHasShortLabelForControlBar() =>
        Assert.Equal("自动", QualityPreference.ShortLabelOf(QualityPreference.AutoQn));
}