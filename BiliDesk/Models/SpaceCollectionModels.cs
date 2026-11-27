using System;

namespace BiliDesk.Models;

/// <summary>UP 主空间「合集和系列」里的一项: 视频合集(ugc_season)或系列。</summary>
public enum SpaceCollectionKind
{
    Season,
    Series
}

/// <summary>
/// UP 主空间「合集和系列」列表卡片的一项。
///
/// 数据来自 <c>/x/polymer/web-space/seasons_series_list</c> 的
/// <c>items_lists.seasons_list[] / series_list[]</c>; 两个数组的 meta 字段名略有差异
/// (合集给 <c>season_id + ptime</c>, 系列给 <c>series_id + last_update_ts</c>),
/// 解析时统一收成这一份模型, 界面只认它。
/// </summary>
public sealed class SpaceCollectionItem
{
    public SpaceCollectionKind Kind { get; init; }

    /// <summary>合集 id(仅 <see cref="SpaceCollectionKind.Season"/> 有值)</summary>
    public long SeasonId { get; init; }

    /// <summary>系列 id(仅 <see cref="SpaceCollectionKind.Series"/> 有值)</summary>
    public long SeriesId { get; init; }

    /// <summary>归属 UP 的 mid</summary>
    public long Mid { get; init; }

    /// <summary>接口原样的名字。合集已带「合集·」前缀, 系列一般只有裸名字。</summary>
    public string Title { get; init; } = "";

    public string Cover { get; init; } = "";
    public string Intro { get; init; } = "";

    /// <summary>内容数(meta.total)</summary>
    public int Count { get; init; }

    /// <summary>更新时间(合集 ptime / 系列 last_update_ts, Unix 秒)</summary>
    public long UpdatedAt { get; init; }

    /// <summary>列表排序、详情请求共用的 id</summary>
    public long Id => Kind == SpaceCollectionKind.Season ? SeasonId : SeriesId;

    public string KindText => Kind == SpaceCollectionKind.Season ? "合集" : "系列";

    public string CountText => $"{Count}个内容";

    public string PubDateShort => VideoItem.FormatPubDateShort(UpdatedAt);

    /// <summary>
    /// 卡片标题要不要拼「合集· / 系列·」前缀。
    /// ★ 订阅的合集卡封面右下角已经有「合集」角标, 标题里再拼一次是重复信息;
    ///   调用方(订阅页)把它设为 false。UP 主空间的合集/系列列表保持默认 true。
    /// </summary>
    public bool ShowKindPrefix { get; init; } = true;

    /// <summary>
    /// 卡片标题: 统一成「合集·xxx / 系列·xxx」。
    /// ★ 不能无条件拼前缀 —— 合集接口的 meta.name 本身就带「合集·」,
    ///   直接拼会变成「合集·合集·xxx」。
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title)) return KindText;
            if (!ShowKindPrefix) return Title;
            if (Title.StartsWith(KindText, StringComparison.Ordinal)) return Title;
            return KindText + "·" + Title;
        }
    }
}
