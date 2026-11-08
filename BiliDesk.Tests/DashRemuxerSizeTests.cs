using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 大文件(mdat / stco)的两个判据。
///
/// ★ 为什么单测它们: 这两条只在**输出超过 4GB** 时才生效(4K 长视频整段下载),
///   而手工造一个 4GB 文件来验证不现实。判错的表现是"文件能生成但播放器打不开/跳转错位",
///   而且只在少数超大视频上复现 —— 正是最需要单测钉住的那类逻辑。
/// </summary>
public class DashRemuxerSizeTests
{
    [Fact]
    public void NeedsLargeMdat_IsFalseAt32BitBoundary()
    {
        // payload + 8 恰好等于 uint.MaxValue: 32 位 size 放得下
        Assert.False(DashRemuxer.NeedsLargeMdat(uint.MaxValue - 8L));
    }

    [Fact]
    public void NeedsLargeMdat_IsTrueJustOverBoundary()
    {
        Assert.True(DashRemuxer.NeedsLargeMdat(uint.MaxValue - 7L));
    }

    [Fact]
    public void NeedsCo64_IsFalseWhenEndFitsInUint()
    {
        Assert.False(DashRemuxer.NeedsCo64(1024, uint.MaxValue - 1024L));
    }

    [Fact]
    public void NeedsCo64_IsTrueWhenEndExceedsUint()
    {
        Assert.True(DashRemuxer.NeedsCo64(1024, uint.MaxValue - 1023L));
    }

    [Fact]
    public void NeedsCo64_AccountsForMdatStart()
    {
        // 载荷本身不大, 但起点把它推过了 4GB 边界 ⇒ 偏移已经放不进 stco
        Assert.True(DashRemuxer.NeedsCo64(uint.MaxValue - 10, 100));
        Assert.False(DashRemuxer.NeedsCo64(0, 100));
    }
}
