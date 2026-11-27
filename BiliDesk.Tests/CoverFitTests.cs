using BiliDesk.Helpers;

namespace BiliDesk.Tests;

/// <summary>
/// 动态单图/详情图集的适配判断: Contain 下"几乎同比例"的图仍应铺满,
/// 只有比例差得多(竖图/长图/未知宽高)才切 Uniform 完整显示。
/// </summary>
public class CoverFitTests
{
    [Fact]
    public void NearlySameAspect_16x9_In16x9_IsFill()
    {
        Assert.True(Cover.IsNearlySameAspect(320, 180, 3003, 1689));
    }

    [Fact]
    public void DifferentAspect_16x9_In4x3_IsContain()
    {
        // 旧版单图固定 4:3 会把 16:9 封面左右各裁掉约 12.5%
        Assert.False(Cover.IsNearlySameAspect(320, 240, 3003, 1689));
    }

    [Fact]
    public void DifferentAspect_Portrait_IsContain()
    {
        Assert.False(Cover.IsNearlySameAspect(320, 180, 1549, 2048));
    }

    [Fact]
    public void MissingPixelSize_IsContain()
    {
        // 接口没给宽高时不能假设"同比例", 交给 Contain 保证完整显示
        Assert.False(Cover.IsNearlySameAspect(320, 180, 0, 0));
    }
}
