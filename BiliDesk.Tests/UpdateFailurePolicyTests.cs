using System;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// "失败提示要不要弹"的边界: 断网静默、24 小时节流、系统时钟被改坏后的未来时间。
/// 这些在真机上很难稳定复现, 只能靠纯函数穷举。
/// </summary>
public class UpdateFailurePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 从未提示过_有网时提示()
        => Assert.True(UpdateFailurePolicy.ShouldShowNotice(true, null, Now));

    [Fact]
    public void 断网时不提示()
        => Assert.False(UpdateFailurePolicy.ShouldShowNotice(false, null, Now));

    [Fact]
    public void 二十三小时内不重复提示()
        => Assert.False(UpdateFailurePolicy.ShouldShowNotice(true, Now.AddHours(-23), Now));

    [Fact]
    public void 超过二十四小时再次提示()
        => Assert.True(UpdateFailurePolicy.ShouldShowNotice(true, Now.AddHours(-25), Now));

    [Fact]
    public void 未来时间按没记过处理()
        => Assert.True(UpdateFailurePolicy.ShouldShowNotice(true, Now.AddDays(1), Now));
}
