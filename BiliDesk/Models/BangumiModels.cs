using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 番剧 / 追番(PGC): 条目 / 剧集 / 详情

/// <summary>番剧卡片(追番列表 / 推荐列表共用)。</summary>
public sealed class BangumiItem
{
    public long SeasonId { get; set; }
    public long MediaId { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>
    /// 分类: 1 = 番剧(日漫), 2 = 电影, 3 = 纪录片, 4 = 国创, 5 = 电视剧, 7 = 综艺。
    /// 接口给的是 season_type。
    /// </summary>
    public int SeasonType { get; set; }

    /// <summary>接口自带的分类名("番剧" / "国创"), 解析不到时由 <see cref="SeasonTypeText"/> 兜底。</summary>
    public string SeasonTypeName { get; set; } = "";

    /// <summary>
    /// 分类的中文名。
    /// ★ 不直接用 <see cref="SeasonTypeName"/>: 推荐接口(index/result)那个字段是空的,
    ///   只有"我的追番 / 我的追剧"接口才给 —— 几个列表显示的却是同一批东西, 所以一律走这里。
    /// ★ 影视页要用到 2/3/5/7 这几个非番剧类型, 不能再只判"4 = 国创, 其余番剧"。
    /// </summary>
    public string SeasonTypeText => !string.IsNullOrEmpty(SeasonTypeName)
        ? SeasonTypeName
        : SeasonTypeNameOf(SeasonType);

    /// <summary>按 season_type 给分类中文名(首页追番 / 影视共用)。</summary>
    public static string SeasonTypeNameOf(int seasonType) => seasonType switch
    {
        2 => "电影",
        3 => "纪录片",
        4 => "国创",
        5 => "电视剧",
        7 => "综艺",
        _ => "番剧",
    };

    /// <summary>是否为国创(推荐列表按它把「番剧推荐」与「国创推荐」分成两栏)</summary>
    public bool IsGuochuang => SeasonType == 4;

    /// <summary>
    /// 是不是"追剧"类(电影 / 纪录片 / 电视剧 / 综艺)。
    /// ★ 影视页与详情页的追剧按钮都靠它决定文案与追番缓存类型: 对电影说"追番"会很怪,
    ///   而且「我的追剧」接口是 type=2, 用错类型永远查不到。
    /// </summary>
    public bool IsTeleplay => SeasonType is 2 or 3 or 5 or 7;

    /// <summary>角标: 大会员 / 独播 / 出品。空串表示没有角标, 界面上整个角标不显示。</summary>
    public string Badge { get; set; } = "";

    /// <summary>角标底色(B 站自带, 如 #FB7299 / #00C0FF)。取不到就由界面退回默认色。</summary>
    public string BadgeColor { get; set; } = "";

    /// <summary>总集数</summary>
    public int TotalCount { get; set; }

    /// <summary>是否已完结</summary>
    public bool IsFinish { get; set; }

    /// <summary>更新进度文案("更新至第 195 话" / "全 12 话")</summary>
    public string IndexShow { get; set; } = "";

    /// <summary>
    /// 观看进度文案("看到第 12 话")。
    /// ★ 只有真的看到一半的剧才会给这个值, 其余是空串 —— 所以界面按"有才显示"处理,
    ///   空的时候退回 <see cref="IndexShow"/>, 而不是留一个空行。
    /// </summary>
    public string Progress { get; set; } = "";

    public long FollowCount { get; set; }

    /// <summary>
    /// 评分("9.6")。影视索引接口会给, 追番列表没有; 空串表示没有评分。
    /// </summary>
    public string Score { get; set; } = "";

    /// <summary>卡片副标题: 优先显示观看进度(用户更关心"我看到哪了"), 没有就显示更新进度。</summary>
    public string CardSubText => !string.IsNullOrWhiteSpace(Progress) ? Progress : IndexShow;

    /// <summary>
    /// 海报右下角那块小角标。
    /// ★ 影视类优先显示评分(与 B 站影视页一致); 番剧保持原来的"看到第 N 话 / 更新至第 N 话",
    ///   因为追番用户更关心更新进度而不是分数。
    /// </summary>
    public string CardBadgeText => IsTeleplay && !string.IsNullOrWhiteSpace(Score)
        ? Score
        : CardSubText;

    /// <summary>番剧主页(B 站网页版)</summary>
    public string Url { get; set; } = "";
}

/// <summary>
/// 影视页的一个栏目(标题 + 一批 PGC 条目) —— 2026-11-28 新增。
///
/// ★ 为什么用一个栏目对象而不是在 ViewModel 里摆五份 ObservableCollection:
///   影视页的栏目结构一样(标题 + 条数 + 海报墙), 只有数据不同。用一份
///   <c>ItemsControl</c> 套栏目模板, 以后加/减栏目只改数据, 不用复制五段 XAML。
/// ★ 条目在写进栏目之前就已经填好, 所以这里不需要变更通知; 刷新时整份替换集合即可。
/// </summary>
public sealed class MediaSection
{
    public string Title { get; init; } = "";

    public IReadOnlyList<BangumiItem> Items { get; init; } = Array.Empty<BangumiItem>();

    public bool HasItems => Items.Count > 0;

    public string CountText => Items.Count > 0 ? $"({Items.Count})" : "";
}

/// <summary>番剧的一集(PGC 的分集, 不同于 UGC 合集)。</summary>
public sealed class BangumiEpisode
{
    public long EpId { get; set; }
    public long Aid { get; set; }

    /// <summary>默认分 P cid。播放走 bvid, 这个是给"定位到具体一集"用的。</summary>
    public long Cid { get; set; }

    /// <summary>这一集对应的普通稿件 bv 号 —— 播放器只认它, 点"播放"就是拿它去播。</summary>
    public string Bvid { get; set; } = "";

    /// <summary>集号("1" / "12")。部分条目是番外名而非数字。</summary>
    public string Title { get; set; } = "";

    /// <summary>本集标题("凡人风起天南1重制版")</summary>
    public string LongTitle { get; set; } = "";

    public string Cover { get; set; } = "";

    /// <summary>时长(秒)</summary>
    public int DurationSec { get; set; }
    public string Duration => VideoItem.FormatSeconds(DurationSec);

    public long PubTime { get; set; }

    /// <summary>是否已播(0 = 预告 / 未播)。没播的集不该给"播放"入口。</summary>
    public bool IsAired { get; set; }

    /// <summary>
    /// 是不是当前选中 / 正在播放的那一集(选集列表据此高亮)。
    ///
    /// ★ 这一集对象本身被 ItemsControl 绑定着, 所以**不能**靠"换引用"来改选中态 ——
    ///   换引用等于换一行, 高亮会跟着闪一下。这里让同一个对象自己报属性变更,
    ///   与 VideoItem.IsSelected 同一套路(Models 这一层刻意不依赖 WPF, 用的就是 INotifyPropertyChanged)。
    /// </summary>
    private bool _isCurrent;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 界面上这一行显示的文案。
    /// 集号 + 本集标题才是用户认得的东西; 只有标题没有集号会分不清"第几集"。
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            var num = string.IsNullOrWhiteSpace(Title) ? "" : "第" + Title + "话";
            if (string.IsNullOrWhiteSpace(LongTitle)) return num;
            return string.IsNullOrEmpty(num) ? LongTitle : num + " " + LongTitle;
        }
    }
}

/// <summary>番剧详情 + 选集列表(pgc/view/web/season 的 result)。</summary>
public sealed class BangumiDetail
{
    public long SeasonId { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>分类名("番剧" / "国创" / "电影"…)</summary>
    public string SeasonTypeName { get; set; } = "";

    /// <summary>分类数字(1 番剧 / 2 电影 / 3 纪录片 / 4 国创 / 5 电视剧 / 7 综艺)</summary>
    public int SeasonType { get; set; }

    /// <summary>是不是追剧类(电影 / 纪录片 / 电视剧 / 综艺)。详情页按钮文案与追剧缓存都用它。</summary>
    public bool IsTeleplay => SeasonType is 2 or 3 or 5 or 7;

    /// <summary>简介(pgc 的 evaluate)</summary>
    public string Evaluate { get; set; } = "";

    /// <summary>评分("9.7")。没评分(新番刚开播)时为空。</summary>
    public string Score { get; set; } = "";

    /// <summary>评分人数</summary>
    public long ScoreCount { get; set; }

    /// <summary>地区("中国大陆")</summary>
    public string Area { get; set; } = "";

    /// <summary>风格分类, 逗号连接("热血, 奇幻")</summary>
    public string Styles { get; set; } = "";

    /// <summary>更新时间说明("每日17:00更新一集")</summary>
    public string Renewal { get; set; } = "";

    /// <summary>首播日期("2022-01-01")</summary>
    public string PublishDate { get; set; } = "";

    /// <summary>追番人数</summary>
    public long FollowCount { get; set; }

    /// <summary>是否完结</summary>
    public bool IsFinish { get; set; }

    /// <summary>选集列表(正片, 按官方顺序展平)</summary>
    public List<BangumiEpisode> Episodes { get; set; } = new();

    public int TotalEp => Episodes.Count;

    /// <summary>评分那一行的文案; 没有评分就整段不显示。</summary>
    public string ScoreText => string.IsNullOrEmpty(Score) ? "" : Score + " 分";
}

/// <summary>
/// 进播放器时带进去的**番剧选集表**(2026-11-19)。
///
/// ★ 为什么播放器需要它: PGC 的分集表只存在于 <c>/pgc/view/web/season</c>, 而播放器加载
///   单个视频走的是 <c>/x/web-interface/view</c> —— 那条路只会给出 <c>ugc_season</c>(UGC
///   合集), 番剧根本不会带分集表。所以"在播放器里选集 / 播完自动下一集"必须由选集页
///   把整季的集表递进来。
/// ★ 播放器收到后把它**合成**成一份 <see cref="SeasonInfo"/> 喂给现有的合集面板
///   (见 PlayerWindow.BuildBangumiSeason), 于是高亮当前集 / 打开时滚到当前 / 点击换集
///   / 连播链全部复用, 不必再写第二套选集 UI。
/// ★ 这个对象**不进** Setter 语义: 它只描述"这一季有哪些集", 当前在第几集由播放器用
///   cid/bvid 自己定位(见 SeasonEpisode / FindCurrentEpisode)。
/// </summary>
public sealed class BangumiPlaylist
{
    /// <summary>番剧 season_id(ssid)。仅用于诊断与"重新拉详情"这类后备路径。</summary>
    public long SeasonId { get; init; }

    /// <summary>番剧名 —— 播放器合集面板的标题就用它。</summary>
    public string Title { get; init; } = "";

    /// <summary>整季正片, 按官方播放顺序。</summary>
    public List<BangumiEpisode> Episodes { get; init; } = new();
}
