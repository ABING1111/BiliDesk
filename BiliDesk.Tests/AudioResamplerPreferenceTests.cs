using System.Linq;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 音频重采样档位的纯逻辑契约(2026-11-22)。
///
/// 设置页用**字符串**做下拉框绑定(与音质档位同一套写法), 靠 LabelOf 反查枚举。
/// 所以"显示名必须互不相同"是硬约束 —— 撞了就会一点下拉框就落到别的档位。
/// </summary>
public class AudioResamplerPreferenceTests
{
    [Fact]
    public void 显示名互不相同且可反查()
    {
        var labels = AudioResamplerPreference.All
            .Select(AudioResamplerPreference.LabelOf)
            .ToArray();

        Assert.Equal(labels.Length, labels.Distinct().Count());

        foreach (var mode in AudioResamplerPreference.All)
        {
            var label = AudioResamplerPreference.LabelOf(mode);
            var back = AudioResamplerPreference.All.FirstOrDefault(
                m => AudioResamplerPreference.LabelOf(m) == label);
            Assert.Equal(mode, back);
        }
    }

    [Fact]
    public void 非法值落回自动()
    {
        Assert.Equal(AudioResamplerMode.Auto, AudioResamplerPreference.Normalize((AudioResamplerMode)99));
        Assert.Equal(AudioResamplerMode.Speex, AudioResamplerPreference.Normalize(AudioResamplerMode.Speex));
        Assert.Equal(AudioResamplerMode.Compat, AudioResamplerPreference.Normalize(AudioResamplerMode.Compat));
    }

    [Fact]
    public void 默认是自动且排在最前()
    {
        Assert.Equal(AudioResamplerMode.Auto, AudioResamplerPreference.All[0]);
        Assert.Equal(AudioResamplerMode.Auto, new AppSettings().AudioResamplerMode);
    }
}
