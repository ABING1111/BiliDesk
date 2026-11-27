using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

public class SmartRatePolicyTests
{
    [Fact]
    public void 缺失数据回落三倍速()
        => Assert.Equal(3.0f, SmartRatePolicy.Resolve(null));

    [Theory]
    [InlineData(0.0, 2.0f)]
    [InlineData(2.07, 2.0f)]
    [InlineData(4.999, 2.0f)]
    [InlineData(5.0, 1.5f)]
    [InlineData(5.806, 1.5f)]
    public void 按阈值五分成两档(double pace, float expected)
        => Assert.Equal(expected, SmartRatePolicy.Resolve(pace));

    [Fact]
    public void 非数字回落一点五倍速()
        => Assert.Equal(1.5f, SmartRatePolicy.Resolve(double.NaN));
}
