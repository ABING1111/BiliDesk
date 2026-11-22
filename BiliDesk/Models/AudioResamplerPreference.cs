using System.Linq;

namespace BiliDesk.Models;

/// <summary>
/// 音频重采样模块的选择(设置页「音频重采样」)。
///
/// ★ 背景: 1.8.3 为了修 44.1k→48k 的"电流音"强制 speex, 但 speex 会连 VLC 的时钟
///   漂移补偿一起接管 —— 蓝牙耳机(独立时钟 + 端点延迟大)因此"一卡一卡", 直连音响没事。
///   所以默认按端点自动: 非蓝牙用 speex(音质), 蓝牙回落到 libvlc 默认的 any(1.8.2 行为)。
///   完整推导见 Services/VlcCore.BuildCoreArguments 的注释。
/// ★ 枚举值落盘为 JSON 字符串(全局 JsonStringEnumConverter), 改名即失效, 别随手改。
/// </summary>
public enum AudioResamplerMode
{
    /// <summary>自动(推荐): 按当前默认输出端点决定 —— 蓝牙用 any, 其它用 speex。</summary>
    Auto = 0,

    /// <summary>强制 speex: 音质优先, 任何端点都做高质量重采样(蓝牙可能因此卡顿)。</summary>
    Speex = 1,

    /// <summary>兼容模式: 不强制模块, 完全交给 libvlc(= 1.8.2 行为)。</summary>
    Compat = 2,
}

/// <summary>音频重采样档位的显示名与说明。</summary>
public static class AudioResamplerPreference
{
    /// <summary>下拉框里的顺序(默认项在最前)。</summary>
    public static readonly AudioResamplerMode[] All =
    {
        AudioResamplerMode.Auto,
        AudioResamplerMode.Speex,
        AudioResamplerMode.Compat,
    };

    /// <summary>落盘用的取值校验: 手改配置塞进别的数字时一律落回「自动」。</summary>
    public static AudioResamplerMode Normalize(AudioResamplerMode mode) =>
        All.Contains(mode) ? mode : AudioResamplerMode.Auto;

    public static string LabelOf(AudioResamplerMode mode) => mode switch
    {
        AudioResamplerMode.Speex => "强制高质量 (Speex)",
        AudioResamplerMode.Compat => "兼容模式 (不干预)",
        _ => "自动 (推荐)",
    };

    /// <summary>副标题: 说清"哪种情况该改" —— 别让普通用户为了音质乱切。</summary>
    public static string HintOf(AudioResamplerMode mode) => mode switch
    {
        AudioResamplerMode.Speex =>
            "任何设备都做高质量重采样; 蓝牙耳机出现断断续续/一卡一卡时不要选它",
        AudioResamplerMode.Compat =>
            "完全交给 libvlc 默认策略; 44.1k 音轨在 48k 声卡上可能有轻微电流音",
        _ => "直连音响/声卡用高质量重采样(修电流音); 蓝牙耳机自动回落到兼容策略(防卡顿)",
    };
}
