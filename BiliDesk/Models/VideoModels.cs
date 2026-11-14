using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 视频: 卡片 / 详情 / 分P / 合集(季与集) / DASH 流描述

/// <summary>视频卡片信息(跨页面通用)</summary>
public class VideoItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>
    /// UP 主头像地址。**只有动态流需要**(动态卡片顶部要摆头像)——
    /// 首页/搜索那些列表接口不带头像, 那里保持空串即可, 界面按"没有就不显示"处理。
    /// </summary>
    public string AuthorFace { get; set; } = "";

    public string Duration { get; set; } = "";
    public long ViewCount { get; set; }
    public long DanmakuCount { get; set; }
    public long Pubdate { get; set; }

    /// <summary>
    /// UP 主 mid。点卡片下方的 UP 主名要跳他的主页, 所以列表解析时就顺手带上
    /// (热门/排行/搜索/推荐这些接口的 `owner.mid` 都有)。
    /// 0 = 该来源没给(少数字段残缺的接口), 点的时候给个明确提示而不是毫无反应。
    /// </summary>
    public long OwnerMid { get; set; }

    /// <summary>
    /// 当前登录用户**是否已关注**该 UP 主。
    ///
    /// ★ 首页推荐流: `wbi/index/top/feed/rcmd` 的每条 item 自带 `is_followed`
    ///   (0 未关注 / 1 已关注), 推荐理由 `rcmd_reason.reason_type == 1` 也是同一个含义。
    /// ★ 搜索页: 搜索接口不下发这个信息, 由 SearchViewModel 在结果展示后拿本页作者 mid
    ///   批量查 `/x/relation/relations`, 再回来置 true(见 MarkFollowedAuthorsAsync)。
    ///   所以这里必须带变更通知: 值是在条目已经绑到卡片上之后才改的, 没有 INPC 标签不会出现。
    ///   其它列表(热门/排行/收藏/动态/空间)不下发也不回填, 保持 false 自动隐藏。
    /// </summary>
    private bool _isAuthorFollowed;
    public bool IsAuthorFollowed
    {
        get => _isAuthorFollowed;
        set
        {
            if (_isAuthorFollowed == value) return;
            _isAuthorFollowed = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsAuthorFollowed)));
        }
    }

    /// <summary>
    /// 点赞数。**当前卡片上不显示**(用户要求把"N点赞"从卡片撤掉), 但解析照旧保留 ——
    /// 将来要恢复显示或做按赞排序时, 不必再回头改一遍所有列表解析。
    /// </summary>
    public long LikeCount { get; set; }

    /// <summary>
    /// 稿件 avid。
    /// 收藏相关接口(/x/v3/fav/resource/deal)只认 avid, 不认 bvid, 所以列表解析时
    /// 顺手把它带上; 为 0 表示这条来源没有提供(例如搜索接口), 用到时再按 bvid 补查。
    /// ★ 2026-11-08: 首页推荐流也有 aid —— 它放在 `id` 字段里(不是 `aid`)。
    ///   推荐流的翻页游标要用它拼 `last_showlist=av_{aid}`, 缺了就少一层服务端避重。
    /// ★ 2026-11-15: 热门/排行/分区/空间/相关这些列表接口都给 aid(或能按 bvid 换算),
    ///   解析侧已兜底(见 ApiClient.ParseVideoItem) —— 卡片右上角「稍后再看」的选中态
    ///   就是拿它比对全局集合的, 缺了按钮永远不亮。
    /// </summary>
    public long Aid { get; set; }

    /// <summary>
    /// 补齐 avid: 接口给了就用接口的, 没给就按 bvid 本地换算(见 UrlParser.BvToAv,
    /// 算法已用热门 + 排行榜真实数据双向校验), 换不出来(空/非法 bvid)返回 0。
    /// ★ 抽成纯函数是为了能单测 —— 换算错了「稍后再看」会把对勾加到别的视频头上。
    /// </summary>
    public static long ResolveAid(long aid, string? bvid)
        => aid > 0 ? aid : Utils.UrlParser.BvToAv(bvid);

    /// <summary>
    /// 直播间号。&gt;0 表示这条是**直播间**而不是视频稿件:
    /// 播放走直播流地址、没有弹幕/进度上报/三连, 卡片上的"N 播放"也要换成"N 人气"。
    /// 复用 VideoItem 而不是另起一个模型, 是为了直接拿到现成的卡片、网格与全局屏蔽。
    /// </summary>
    public long RoomId { get; set; }

    /// <summary>
    /// 直播间所属分区名(只有直播列表会填, 如「英雄联盟」「三角洲行动」)。
    /// 卡片底部显示成 "UP 名 · 分区名"; 视频稿件为空, 界面自动隐藏。
    /// </summary>
    public string AreaName { get; set; } = "";

    public bool IsLive => RoomId > 0;

    private bool _isSelected;

    /// <summary>
    /// 列表编辑模式下的选中标记(仅 UI 用, 不参与业务)。
    ///
    /// 这里必须带变更通知: "全选 / 取消全选"是**从代码侧**改这个值, 没有 INPC 的话
    /// 界面上已经绑好的复选框不会跟着变 —— 勾选框会保持原样, 表现就是"点了全选没反应"。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>
    /// 播放数展示串。0 时返回空串 —— 动态时间线等来源拿不到播放数,
    /// 显示"▶ 0"是错误信息(截图实测), 拿不到就整个胶囊隐藏(与弹幕数同款约定)。
    /// </summary>
    public string ViewCountText => ViewCount > 0 ? FormatCount(ViewCount) : "";
    /// <summary>弹幕数的展示串(封面左下角胶囊)。0 或未知时为空, 由界面隐藏。</summary>
    public string DanmakuCountText => DanmakuCount > 0 ? FormatCount(DanmakuCount) : "";

    /// <summary>
    /// 发布日期的短格式(卡片文字区右侧): 同年显示 "9-1", 跨年带年份。
    /// Pubdate 没解析到(部分接口)时为空, 由界面隐藏。
    /// </summary>
    public string PubDateShort => FormatPubDateShort(Pubdate);

    /// <summary>
    /// 接口**直接给的**发布时间展示串(动态接口的 `pub_time` 就是这种已经排好版的文本,
    /// 形如 "2小时前" / "09-25")。非空时优先用它 —— 服务端算好的相对时间比本地推算准。
    /// </summary>
    public string PubTimeLabel { get; set; } = "";

    /// <summary>
    /// <see cref="Pubdate"/> 是不是**从展示文本反推**出来的近似值(而不是接口给的真实时间戳)。
    ///
    /// ★ 只有动态接口的扁平路径会置 true: 它只给 "2小时前" / "昨天 18:45", 不给时间戳,
    ///   于是拿本机时钟反推 —— 误差 = 服务端渲染那一刻到现在的间隔(通常几分钟到几小时)。
    ///   modules 路径有真实 pub_ts, 空间投稿有真实 created, 都保持 false。
    /// ★ 用途只有一个: 悬停提示加"约"字(见 <see cref="PubFullText"/>)。
    ///   **不要**拿它去做排序/去重之类的判断 —— 近似值量级够用, 精确比较不够。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PubDateApprox { get; set; }

    /// <summary>
    /// 动态卡片头部**直接显示**的发布时间。
    ///
    /// ★ 定稿(用户 2026-10-15): 直接显示说"人话", 精确时间放悬停(<see cref="PubFullText"/>)。
    ///   · 动态流: 用**接口原文**("昨天 18:45" / "2小时前") —— 服务端已排好版, 比本地推算准;
    ///   · 单 UP 的空间投稿: 接口只给时间戳、没有原文, 就按本机时钟算相对时间
    ///     (见 <see cref="FormatPubRelative"/>)—— 两处观感必须一致, 否则同一页里
    ///     一个"3小时前"、一个"10-14 18:45", 是两种语言。
    /// ★ 原文是服务端渲染那一刻算的, 页面开着不动它就一直是"2小时前";
    ///   悬停看到的精确时间由解析出的 Pubdate 算, 不受这个影响。
    /// </summary>
    public string PubText
    {
        get
        {
            if (!string.IsNullOrEmpty(PubTimeLabel)) return PubTimeLabel;
            if (Pubdate > 0) return FormatPubRelative(Pubdate);
            return "";
        }
    }

    /// <summary>
    /// 本地算的相对时间(接口没给展示文本时用): "刚刚" / "27分钟前" / "2小时前" / "昨天 18:45",
    /// 更早的退化成日期("09-20", 跨年带年份)。
    /// 口径刻意与 B 站动态接口的原文措辞对齐(见 ApiClient.ParseDynamicTime)—— 两处显示同一套语言。
    /// </summary>
    public static string FormatPubRelative(long pubdate)
    {
        if (pubdate <= 0) return "";
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(pubdate).LocalDateTime;
            var span = DateTime.Now - t;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}小时前";
            if (span.TotalDays < 2) return $"昨天 {t:HH:mm}";
            return t.Year == DateTime.Now.Year ? t.ToString("MM-dd") : t.ToString("yyyy-MM-dd");
        }
        catch { return ""; }
    }

    /// <summary>
    /// 悬停提示用的完整时间("2026-09-20 14:30")。拿不到时回落到 PubText。
    ///
    /// ★ 近似值会带上"约": 动态接口只给 "2小时前" 这种原文, Pubdate 是拿本机时钟**反推**的
    ///   (误差 = 服务端渲染到现在的间隔), 详细见 <see cref="PubDateApprox"/>。
    ///   直接显示到分钟会让用户以为那是服务端的精确发布时间 —— 加一个字就不用为此解释。
    /// </summary>
    public string PubFullText
        => Pubdate > 0 ? (PubDateApprox ? "约 " : "") + FormatPubDateTimeFull(Pubdate)
         : !string.IsNullOrEmpty(PubTimeLabel) ? PubTimeLabel : "";

    /// <summary>完整时间(悬停提示用), 一律带年份</summary>
    public static string FormatPubDateTimeFull(long pubdate)
    {
        if (pubdate <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(pubdate).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }
        catch { return ""; }
    }

    /// <summary>
    /// 动态卡片右侧那行数据("1.2万 播放 · 345 弹幕")。两项都拿不到时为空串, 由界面隐藏整行。
    /// ★ 与封面上那两枚独立角标分开: 动态卡的封面只留时长, 播放/弹幕挪到标题下面,
    ///   所以要拼成一句话而不是两个独立字符串(见 DynamicCard)。
    ///
    /// ★★ 实测(2026-10-17, 6 页 104 条真实动态): 动态接口的**扁平条目不给任何计数** ——
    ///   字段只有 id_str/author/pub_time/type/rid/title/cover/jump_url/visible/fold,
    ///   没有 stat, 也没有平铺的 play / video_review。而扁平是主路径 ⇒ **这行在动态页
    ///   实际上永远是空的**(下面 modules 兼容分支给的 stat 才有值, 那条路实测不走到)。
    ///   保留它是因为 modules 形态确实带 play/danmaku(且注释里写着服务端对某些客户端
    ///   形态会返回 modules); 但别指望在动态页看到它 —— 想让它亮起来得换接口或额外请求。
    /// </summary>
    public string DynamicStatText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>(2);
            if (ViewCount > 0) parts.Add(FormatCount(ViewCount) + " 播放");
            if (DanmakuCount > 0) parts.Add(FormatCount(DanmakuCount) + " 弹幕");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 发布日期的短格式。**抽成静态的给别处复用** —— 合集列表也要显示同一个格式,
    /// 各写一份必然漂移(改了一处另一处忘)。见 SeasonEpisode.PubDateShort。
    /// </summary>
    public static string FormatPubDateShort(long pubdate)
    {
        if (pubdate <= 0) return "";
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(pubdate).LocalDateTime;
            var now = DateTime.Now;
            return t.Year == now.Year ? $"{t.Month}-{t.Day}" : $"{t.Year}-{t.Month}-{t.Day}";
        }
        catch { return ""; }
    }

    public static string FormatCount(long n)
    {
        if (n >= 100_000_000) return (n / 100_000_000.0).ToString("0.#") + "亿";
        if (n >= 10_000) return (n / 10_000.0).ToString("0.#") + "万";
        return n.ToString();
    }

    /// <summary>秒数 => "12:34" / "1:02:03"</summary>
    public static string FormatSeconds(int seconds)
    {
        if (seconds <= 0) return "--:--";
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    /// <summary>
    /// `Pubdate` 的本地时间文本(MM-dd HH:mm)。
    /// 各接口给这个字段的语义不一样 —— 历史里是"观看时间"(view_at), 推荐/搜索里是"发布时间" ——
    /// 所以名字保持中性, 由页面自己决定前缀文案(历史页用的是"观看于 {0}")。
    /// </summary>
    public string PubdateText
    {
        get
        {
            if (Pubdate <= 0) return "";
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(Pubdate).LocalDateTime.ToString("MM-dd HH:mm");
            }
            catch
            {
                return ""; // 越界时间戳(接口偶尔会给 0 或脏数据)不让它抛
            }
        }
    }
}

/// <summary>视频详情</summary>
public class VideoDetail
{
    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Pic { get; set; } = "";
    public string Owner { get; set; } = "";
    public long OwnerMid { get; set; }
    public string OwnerFace { get; set; } = "";
    public string Desc { get; set; } = "";
    public long ViewCount { get; set; }
    public long LikeCount { get; set; }
    public long DanmakuCount { get; set; }
    /// <summary>投币数 / 收藏数 / 分享数(操作栏"图标 + 数量"那套用; view 接口的 stat 里拿)</summary>
    public long CoinCount { get; set; }
    public long FavoriteCount { get; set; }
    public long ShareCount { get; set; }
    public long Pubdate { get; set; }
    public long Aid { get; set; }
    public long Cid { get; set; }
    public int DurationSec { get; set; }
    public string Duration => VideoItem.FormatSeconds(DurationSec);

    /// <summary>
    /// 该视频所属的合集(ugc_season); 不属于任何合集时为 null。
    /// ★ 跟着 view 接口一起解析, 而不是让播放器再单独请求一次 —— 见 ApiClient.GetVideoAsync。
    /// </summary>
    public SeasonInfo? Season { get; set; }

    /// <summary>
    /// 该视频的全部分P。**只有一个 P 时也会有一项**(即 pages[0]), 由界面自己判断要不要露入口。
    ///
    /// ★ 分P 与合集是两回事, 不能互相替代:
    ///   合集(Season) = 多个**独立稿件**, 每集有自己的 bvid/封面/播放量;
    ///   分P(Pages)   = **同一个稿件**被切成几段, bvid 完全一样, 换段只换 cid。
    ///   「克拉克森的农场 1-5季」(BV1JMbw62E72) 是 40 个 P 的**单个稿件**, 没有 ugc_season ——
    ///   这正是原先"合集"入口不出现、用户没法选集的原因(2026-10-11)。
    /// </summary>
    public List<VideoPage> Pages { get; set; } = new();
}

/// <summary>
/// 一个分P(view 接口的 pages[] 之一)。
///
/// ★ duration 的单位是**秒**(2026-10-11 实测 BV1JMbw62E72: P1 = 5395, 对应 90 分钟正片;
///   若按毫秒读就是 5.4 秒)。这与番剧分集(duration 毫秒)正好相反, 别串。
/// </summary>
public sealed class VideoPage
{
    /// <summary>P 序号(1 起, 接口原样给的)。</summary>
    public int Page { get; set; }

    /// <summary>这一段的 cid —— 换 P 就是换它, bvid 不用变。</summary>
    public long Cid { get; set; }

    /// <summary>分P 标题(如 "S01E01 拖拉机")。可能为空, 空时界面退回显示"P1"。</summary>
    public string Part { get; set; } = "";

    public int DurationSec { get; set; }
    public string Duration => VideoItem.FormatSeconds(DurationSec);

    /// <summary>
    /// 界面上这一行显示的标题: 有 part 用 part, 没有就退回 "P{序号}" ——
    /// 直接显示空的 part 会得到一条只有时长的空白行, 看着像加载坏了。
    /// </summary>
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Part) ? $"P{Page}" : Part;
}

/// <summary>
/// 合集(ugc_season)里的一集。
/// 字段挑的是"列表 UI 要用的那几个": 封面/标题/时长/播放量/发布日 + 换片必需的三件套。
/// </summary>
public sealed class SeasonEpisode
{
    /// <summary>稿件 avid。**必须 long** —— 新稿件 aid 已超过 int 上限(实测 117367564666453)</summary>
    public long Aid { get; set; }
    public string Bvid { get; set; } = "";
    /// <summary>该集的默认分 P cid(episodes[].cid, 与 page.cid 一致)</summary>
    public long Cid { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public int DurationSec { get; set; }
    public long ViewCount { get; set; }
    public long Pubdate { get; set; }

    /// <summary>所属分组(SeasonSection.Id); 0 = 没分组信息</summary>
    public long SectionId { get; set; }

    /// <summary>
    /// 该集在**整个合集**里的序号(1 起)。
    /// 跨分组连续编号, 因为界面上"第 N 集"是用户对合集的整体认知 ——
    /// 分组内重新从 1 数会让"第 3 集"在 10 个分组里各有一条, 定位不了。
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 这一集是不是**它所在分组的第一个**。用来决定要不要在它上面画分组标题 ——
    /// WPF 的 ListBox 用展平数据源 + 这个标记, 比为分组再套一层 ItemsControl 简单得多。
    /// </summary>
    public bool IsSectionStart { get; set; }

    /// <summary>分组标题(仅 IsSectionStart 时会被界面用到)</summary>
    public string SectionTitle { get; set; } = "";

    /// <summary>
    /// 界面上要不要在这条**上面**画分组标题。= IsSectionStart 且整个合集确实有多个分组。
    ///
    /// 为什么不只判 IsSectionStart: 单分组的合集(如"正片"那一组有 148 集)官方也不显示分组名,
    /// 多一行没信息量的标题反而让人以为还有别的分组。这个判断需要"整个合集的分组数",
    /// 单条数据自己算不出来, 所以由灌数据的一方在绑定前统一写好(条目是普通 POCO, 没有变更通知,
    /// 必须在赋给 ItemsSource **之前**定下来)。
    /// </summary>
    public bool ShowSectionHeader { get; set; }

    public string DurationText => VideoItem.FormatSeconds(DurationSec);
    public string ViewCountText => ViewCount > 0 ? VideoItem.FormatCount(ViewCount) : "";

    /// <summary>
    /// 发布日期的短格式(合集条目第二行右侧)。
    /// ★ 复用 <see cref="VideoItem.FormatPubDateShort"/> —— 这条以前**漏了**:
    ///   XAML 里绑着 `{Binding PubDateShort}` 而模型没有这个属性, WPF 对绑定到不存在的属性
    ///   是**静默空白、编译零警告**(项目笔记里记过这个坑), 表现就是每一条的日期都是空的。
    /// </summary>
    public string PubDateShort => VideoItem.FormatPubDateShort(Pubdate);
    public string IndexText => "第 " + Index + " 集";
}

/// <summary>合集里的一个分组(sections[])。官方单分组时该分组就是"正片"</summary>
public sealed class SeasonSection
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public List<SeasonEpisode> Episodes { get; set; } = new();
}

/// <summary>视频合集(ugc_season)</summary>
public sealed class SeasonInfo
{
    /// <summary>合集 id。同时是 seasons_archives_list 的 season_id 参数</summary>
    public long SeasonId { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    /// <summary>UP 主 mid(合集归属人)</summary>
    public long Mid { get; set; }
    public string Intro { get; set; } = "";

    /// <summary>
    /// 服务端自报的总集数。**只用来做"是不是被截断了"的交叉校验** ——
    /// 界面上一律以实际拿到的 Episodes.Count 为准(自报数偶尔与实际不符)。
    /// </summary>
    public int EpCount { get; set; }

    /// <summary>分组。官方网页把它当"选集"的页签(如「马刀西游」/「全新故事小剧场」)</summary>
    public List<SeasonSection> Sections { get; set; } = new();

    /// <summary>
    /// 全部集的**展平**视图, 顺序 = sections 依次拼接 = 官方 sort_reverse=false 的顺序
    /// (实测 118 集合集逐 aid 完全一致, 见 ApiClient.GetSeasonAsync 的说明)。
    /// 单位置取"当前播的是第几集"、也直接当列表数据源。
    /// </summary>
    public List<SeasonEpisode> Episodes { get; set; } = new();
}

/// <summary>
/// 一路 DASH 音视频流(短视频"本地合流"用)。
///
/// 为什么需要它: 短视频想同时拿到"1080P + 拖进度条不卡 + 结尾不丢", 实测只有
/// "单一输入"(单个本地文件)能做到 —— 见 Helpers/DashRemuxer 的实测对照表。
/// 于是短视频的策略是: 先用 DASH 立即开播(零等待), 同时在后台把这两路下到本地、
/// 合成一个 mp4, 好了再切过去; 下次直接播本地文件。
/// </summary>
public class DashStreams
{
    public string VideoUrl { get; set; } = "";
    public string AudioUrl { get; set; } = "";

    /// <summary>接口给的时长(毫秒), 用于两个判断: 大小估算(决定值不值得下)与 MPD/容器时长</summary>
    public long DurationMs { get; set; }

    public long VideoBandwidth { get; set; }
    public string VideoCodecs { get; set; } = "";
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }

    public long AudioBandwidth { get; set; }
    public string AudioCodecs { get; set; } = "";

    /// <summary>按码率估算的文件总大小(字节)。用来决定"这么大的片子不值得为它占本地缓存"</summary>
    public long EstimatedBytes =>
        DurationMs <= 0 ? 0 : (long)((VideoBandwidth + AudioBandwidth) / 8.0 * (DurationMs / 1000.0));
}

