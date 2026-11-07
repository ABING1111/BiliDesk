using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 杂项: 离线缓存条目 / 赞助片段 / 主题与材质枚举 / UrlUtil

/// <summary>
/// 离线缓存里的一条本地视频文件。
/// 它对应下载目录里的一个真实文件, 不是从接口拿到的数据。
/// </summary>
public class CacheEntry
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string TimeText { get; set; } = "";

    /// <summary>原始字节数(算总占用用, 不显示)</summary>
    public long Bytes { get; set; }
}

/// <summary>
/// SponsorBlock 的一条片段标注(B 站社区众筹的"空降"数据)。
/// 字段名对齐服务端返回, 见 Services/SponsorBlockService。
/// </summary>
public sealed class SponsorSegment
{
    /// <summary>
    /// 该片段所属**分 P** 的 cid。
    /// ★ 必须和当前正在播的 cid 相同才允许使用 —— 多 P 视频的片段是按分 P 标注的,
    /// 拿错分 P 的片段会把用户跳到完全不相干的位置。
    /// </summary>
    public long Cid { get; set; }

    /// <summary>类别 id(服务端原值, 如 "sponsor"; 中文名见 SponsorCategories)</summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// 动作类型: <c>skip</c>=跳过 / <c>full</c>=整条视频都是该类内容 / <c>mute</c>=静音 / <c>poi</c>=精彩时刻。
    /// ★ 只有 skip 能当"区间"处理: full 的 segment 是 [0, 0], 当成区间跳会直接回到开头。
    /// </summary>
    public string ActionType { get; set; } = "";

    public double StartSec { get; set; }
    public double EndSec { get; set; }

    /// <summary>标注 id。用于"这条已经处理过(跳过/撤回)"的去重</summary>
    public string Uuid { get; set; } = "";

    /// <summary>社区净投票。负数 = 被投下去的存疑标注, 不跳</summary>
    public int Votes { get; set; }

    public long StartMs => (long)(StartSec * 1000);
    public long EndMs => (long)(EndSec * 1000);

    /// <summary>给界面看的中文名</summary>
    public string Label => SponsorCategories.LabelOf(Category);
}

/// <summary>
/// SponsorBlock 的类别表。
///
/// id 是**服务端原值, 不能改**(改一个字母就查不到数据); 中文名只用于界面。
/// 只列 B 站语境下真会出现的几个 —— 服务端还有 music_offtopic(非音乐部分) 之类
/// 面向 YouTube 的类别, 放进设置页只会让人莫名其妙。
/// </summary>
public static class SponsorCategories
{
    public const string Sponsor = "sponsor";
    public const string SelfPromo = "selfpromo";
    public const string Intro = "intro";
    public const string Outro = "outro";
    public const string Preview = "preview";
    public const string PoiHighlight = "poi_highlight";

    // 注: 服务端还有 interaction(一键三连提醒) —— **本应用不做这个类别**(2026-09-27 用户拍板:
    // 用不到)。所以它既不出现在设置页, 也会在读设置时被 Sanitize 丢掉, 避免出现
    // "界面上一个都没勾, 后台却还在按 interaction 跳"这种自相矛盾的状态。

    /// <summary>(id, 界面名) —— 数组顺序 = 设置页里的显示顺序</summary>
    public static readonly (string Id, string Label)[] All =
    {
        (Sponsor, "赞助广告"),
        (SelfPromo, "自我推广"),
        (Intro, "开场动画"),
        (Outro, "结尾动画"),
        (Preview, "预告 / 回顾"),
        (PoiHighlight, "高能时刻")
    };

    /// <summary>id → 中文名; 不认识的 id 原样返回(界面上也不会因此变成空白)</summary>
    public static string LabelOf(string? id)
    {
        foreach (var (i, label) in All)
            if (i == id) return label;
        return id ?? "";
    }

    /// <summary>这个 id 是不是本应用支持的类别</summary>
    public static bool IsKnown(string? id)
    {
        foreach (var (i, _) in All)
            if (i == id) return true;
        return false;
    }

    /// <summary>
    /// 进度条上该类别色块的颜色。
    ///
    /// 和类别表放在一起是有意的: 以后加类别时会被迫在这里选一个颜色,
    /// 不会出现"新类别在进度条上没颜色"的漏网。
    /// 选的都是在深色控制栏上够扎眼的颜色(进度条压在视频上, 底色永远是暗的)。
    /// </summary>
    public static string MarkColorHex(string? id) => id switch
    {
        Sponsor => "#FB7299",        // 恰饭: 直接用 B 站品牌粉, 最醒目
        SelfPromo => "#FFB74D",      // 自我推广: 橙
        Intro => "#4FC3F7",          // 开场: 蓝
        Outro => "#9575CD",          // 结尾: 紫
        Preview => "#4DB6AC",        // 预告回顾: 青
        PoiHighlight => "#FFD54F",   // 高能: 亮黄
        _ => "#BDBDBD"               // 不认识的类别: 中性灰(照旧能看见, 不会消失)
    };

    /// <summary>
    /// 归一化一份"类别串": 只保留本应用认识的 id, 去重、去空白;
    /// 一个都不剩时退回默认的 sponsor(与"清空类别"的处理一致, 不能让功能静默失效)。
    /// 读设置时必须过一遍 —— 用户的配置里可能留着我们已经下掉的 interaction, 也可能被手改坏。
    /// </summary>
    public static string Sanitize(string? csv)
    {
        var kept = new List<string>();
        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var id = raw.Trim();
                if (id.Length == 0 || kept.Contains(id)) continue;
                if (!IsKnown(id)) continue;
                kept.Add(id);
            }
        }
        return kept.Count > 0 ? string.Join(",", kept) : Sponsor;
    }
}

/// <summary>外观模式</summary>
public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// 窗口背景材质变体(2026-10-06 新增)。
///
/// ★★★ 为什么做这个而不是"模糊度": 系统**没有**任何 API 能改模糊半径
///   (模糊半径是 DWM 写死的内部常量, 唯一能改的办法是注入 dwm.exe hook 私有符号, 普通应用做不到)。
///   而"用哪种材质"是**官方开放**的 —— <c>DWMWA_SYSTEMBACKDROP_TYPE</c> 就是这个选择器。
///   这是唯一既真实、又观感差异明显的可控项, 所以把这一层开放给用户。
///
/// ★ 枚举值**直接等于** DWMWA_SYSTEMBACKDROP_TYPE 的取值(DWMSBT_*), 下发时不做映射 ——
///   少一层转换就少一处能写错的地方。★ 别改这些数字。
/// </summary>
public enum BackdropMaterial
{
    /// <summary>无材质: 窗口保持全透明(不申请任何 DWM 材质, 桌面直接透出、不模糊)。</summary>
    None = 1,

    /// <summary>
    /// 云母(Mica, 2)。★ 它是**不透明**的: 只采样一次桌面壁纸、且**从不透出后面的窗口**。
    /// 所以选它等于放弃"透出桌面"这个效果, 换来的是更沉稳的壁纸色调。
    /// </summary>
    Mica = 2,

    /// <summary>亚克力(Acrylic, 3) —— 默认。真·半透明磨砂, 实时透出桌面内容。</summary>
    Acrylic = 3,

    /// <summary>云母 Alt / 标签页材质(Tabbed, 4)。同样是壁纸基调的不透明材质, 层次比云母更平。</summary>
    MicaAlt = 4
}

/// <summary>
/// 首页「推荐」tab 的算法来源。★ 2026-10-03 起这个选择**已取消**, 固定用网页版。
///
/// 保留枚举只是为了让老 settings.json 里存着的值仍能反序列化(删掉枚举会让老配置解析失败,
/// 那会连带把整个设置文件清空)。实际读取处一律强制 Web —— 原因见 ApiClient.GetRecommendAsync:
/// App 端接口对第三方客户端不提供个性化, 返回的是全站通用热门池。
///
/// 两个值的差别(历史记录, 供以后参考):
///   · App —— app.bilibili.com/x/v2/feed/index, 每次 10 条;
///   · Web —— wbi/index/top/feed/rcmd, 单次最多 30 条, **实测更贴口味**。
/// </summary>
public enum RecommendSource
{
    App = 0,
    Web = 1
}

/// <summary>URL 工具(纯函数, 不依赖 WPF)</summary>
public static class UrlUtil
{
    /// <summary>把 bilibili 返回的图片地址规范为 https 绝对地址</summary>
    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.StartsWith("//")) return "https:" + url;
        if (url.StartsWith("http://")) return "https://" + url[7..];
        return url;
    }
}

