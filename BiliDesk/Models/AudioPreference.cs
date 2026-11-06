using System.Linq;

namespace BiliDesk.Models;

/// <summary>
/// 「音质」可选项 —— 播放器控制栏那枚音质按钮 + 设置页共用。
///
/// ★ 与 <see cref="QualityPreference"/> 的分工:
///   · QualityPreference 管的是**画质**(视频流的 qn);
///   · 本类管的是**音轨**(DASH 里 dash.audio / dash.dolby.audio / dash.flac.audio)。
///   两者互不影响: 换音质不换视频流, 换画质不动音轨档位的选择。
///
/// ★ 枚举值刻意与 B 站的音轨 id 对齐(30251/30250/30280/30232/30216):
///   这样落盘的配置本身就是接口里的真实 id, 排查问题时不用再查一遍映射表。
///   Auto = 0 是哨兵值, 真实音轨 id 不可能是 0。
/// </summary>
public enum AudioTier
{
    /// <summary>自动: 有 Hi-Res 无损就用无损, 其次杜比, 再次 192K/132K/64K。</summary>
    Auto = 0,

    /// <summary>Hi-Res 无损(FLAC)。需要大会员且该视频本身提供了无损音轨。</summary>
    HiRes = 30251,

    /// <summary>杜比全景声(Dolby Audio)。</summary>
    Dolby = 30250,

    /// <summary>192K AAC(普通视频的默认最高音质)</summary>
    Aac192 = 30280,

    /// <summary>132K AAC</summary>
    Aac132 = 30232,

    /// <summary>64K AAC</summary>
    Aac64 = 30216,
}

/// <summary>音质档位的显示名与降级规则。</summary>
public static class AudioPreference
{
    /// <summary>下拉框/弹层里的顺序(从高到低, 「自动」在最前)。</summary>
    public static readonly AudioTier[] All =
    {
        AudioTier.Auto,
        AudioTier.HiRes,
        AudioTier.Dolby,
        AudioTier.Aac192,
        AudioTier.Aac132,
        AudioTier.Aac64,
    };

    /// <summary>落盘用的取值校验: 手改配置塞进别的数字时一律落回「自动」。</summary>
    public static AudioTier Normalize(AudioTier tier) =>
        All.Contains(tier) ? tier : AudioTier.Auto;

    /// <summary>弹层里的完整文案。</summary>
    public static string LabelOf(AudioTier tier) => tier switch
    {
        AudioTier.HiRes => "Hi-Res 无损",
        AudioTier.Dolby => "杜比全景声",
        AudioTier.Aac192 => "192K 高码率",
        AudioTier.Aac132 => "132K 标准",
        AudioTier.Aac64 => "64K 省流",
        _ => "自动 (最高音质)",
    };

    /// <summary>控制栏按钮上的短文案(位置很小, 不能放"自动 (最高音质)"这种解释性长句)。</summary>
    public static string ShortLabelOf(AudioTier tier) => tier switch
    {
        AudioTier.HiRes => "无损",
        AudioTier.Dolby => "杜比",
        AudioTier.Aac192 => "192K",
        AudioTier.Aac132 => "132K",
        AudioTier.Aac64 => "64K",
        _ => "自动",
    };

    /// <summary>
    /// 音轨 id → 档位。接口里 30251 在 flac 节点、30250 在 dolby 节点,
    /// 普通 AAC 在 dash.audio, id 与枚举值一致, 所以直接按 id 反查。
    /// </summary>
    public static AudioTier TierOfId(int id) => id switch
    {
        30251 => AudioTier.HiRes,
        30250 => AudioTier.Dolby,
        30280 => AudioTier.Aac192,
        30232 => AudioTier.Aac132,
        30216 => AudioTier.Aac64,
        _ => AudioTier.Auto,
    };

    /// <summary>
    /// 音质"高度"排序值, 只用于「用户指定档位不可用时取最接近的」这套降级规则。
    /// 数字越大音质越高。
    /// </summary>
    public static int LevelOf(int audioId) => audioId switch
    {
        30251 => 5,
        30250 => 4,
        30280 => 3,
        30232 => 2,
        30216 => 1,
        _ => 0,
    };

    /// <summary>这个音轨 id 对应的短文案(未知 id 返回"音质")。</summary>
    public static string ShortLabelOfId(int id) =>
        id > 0 ? ShortLabelOf(TierOfId(id)) : "音质";
}
