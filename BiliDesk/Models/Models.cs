using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

/// <summary>视频卡片信息(跨页面通用)</summary>
public class VideoItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Bvid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Author { get; set; } = "";
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
    /// 点赞数。**当前卡片上不显示**(用户要求把"N点赞"从卡片撤掉), 但解析照旧保留 ——
    /// 将来要恢复显示或做按赞排序时, 不必再回头改一遍所有列表解析。
    /// </summary>
    public long LikeCount { get; set; }

    /// <summary>
    /// 稿件 avid。
    /// 收藏相关接口(/x/v3/fav/resource/deal)只认 avid, 不认 bvid, 所以列表解析时
    /// 顺手把它带上; 为 0 表示这条来源没有提供(例如搜索/推荐接口), 用到时再按 bvid 补查。
    /// </summary>
    public long Aid { get; set; }

    /// <summary>
    /// 直播间号。&gt;0 表示这条是**直播间**而不是视频稿件:
    /// 播放走直播流地址、没有弹幕/进度上报/三连, 卡片上的"N 播放"也要换成"N 人气"。
    /// 复用 VideoItem 而不是另起一个模型, 是为了直接拿到现成的卡片、网格与全局屏蔽。
    /// </summary>
    public long RoomId { get; set; }

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

/// <summary>登录用户信息</summary>
public class UserState
{
    public bool IsLogin { get; set; }
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public long Mid { get; set; }
    public int Level { get; set; }

    public double Coins { get; set; }

    /// <summary>当前经验值(nav 的 level_info.current_exp; 「我的」页显示"经验 x/y")</summary>
    public long ExperienceCurrent { get; set; }

    /// <summary>升级所需经验(nav 的 level_info.next_exp; Lv6 时为 0)</summary>
    public long ExperienceNext { get; set; }
}

/// <summary>UP 主空间信息(多接口聚合的结果)</summary>
public class SpaceInfo
{
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public string Sign { get; set; } = "";
    public long Mid { get; set; }
    public int Level { get; set; }
    public long Follower { get; set; }   // 粉丝数
    public long Following { get; set; }  // 关注数
    public long VideoCount { get; set; } // 投稿数
}

/// <summary>
/// 搜索 / 历史的"看哪一类"标签(2026-10-03 新增)。
///
/// 视频与直播间走的是**完全不同的接口和字段**, 但界面上是同一套卡片墙 ——
/// 所以用同一个枚举把"当前这一类"在页面/VM/接口之间传下去, 而不是各传一个 bool。
/// </summary>
public enum ContentKind
{
    /// <summary>视频稿件(搜索 search_type=video / 历史 type=archive)</summary>
    Video,

    /// <summary>直播间(搜索 search_type=live_room / 历史 type=live)</summary>
    Live,
}

/// <summary>搜索 API 返回结构</summary>
public class SearchData
{
    public bool Ok { get; set; }
    public bool Blocked { get; set; }    // 被风控拦截
    public string? Error { get; set; }
    public System.Collections.Generic.List<VideoItem> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// 热搜榜的一条(首页右上角搜索卡展开后的「热搜」区块)。
///
/// 接口的 key 与展示是两回事, 所以这里把两者都留着:
/// keyword 是"点下去要搜什么"(可能很短, 例如 "KC XLG"),
/// show_name 才是给人看的完整说法("KC战胜XLG VCT冠军赛") —— 网页端显示的也是后者。
/// </summary>
public class HotSearchItem
{
    /// <summary>1 起的排名, 直接当序号显示(左栏 1~5, 右栏 6~10)</summary>
    public int Rank { get; set; }

    /// <summary>真正的搜索词 —— 点这一条时用它去搜</summary>
    public string Keyword { get; set; } = "";

    /// <summary>展示名(接口的 show_name; 缺了就退回 keyword)</summary>
    public string ShowName { get; set; } = "";

    /// <summary>徽章文字(新 / 热 / 独家)。空 = 这一条没有徽章, 界面自己隐藏</summary>
    public string Badge { get; set; } = "";

    public bool HasBadge => Badge.Length > 0;

    /// <summary>前三名用强调色显示序号 —— 和 B 站一致, 让"最热的几条"一眼能挑出来</summary>
    public bool IsTopRank => Rank is >= 1 and <= 3;
}

/// <summary>关注列表项(关注的 UP 主)</summary>
public class FollowUser : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public long Mid { get; set; }
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public string Sign { get; set; } = "";
    public int VerifyType { get; set; } = -1;
    public string VerifyDesc { get; set; } = "";

    private bool _isSelected;

    /// <summary>
    /// 动态页左栏的选中高亮(UI 用, 不参与任何接口)。
    /// 必须带通知: 选中是**从 ViewModel 侧**改的, 新旧两项的高亮都要跟着变。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>
/// 评论里的一个表情。
/// 字段名跟着接口走: `url` 是图片地址, `meta.size` 是尺寸档位
/// (1 = 常规小表情, 2 = 偏大的那种, 比如游戏联动表情)。
/// </summary>
public class CommentEmote
{
    public string Url { get; set; } = "";

    /// <summary>接口 meta.size。1 = 小表情(约一行文字高), 2 = 大表情</summary>
    public int Size { get; set; } = 1;
}

/// <summary>评论条目(顶层评论与楼中楼回复共用同一个模型)</summary>
public class CommentItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>评论 id。取楼中楼回复时要拿它当 root 参数</summary>
    public long Rpid { get; set; }

    /// <summary>
    /// 评论者 mid。头像点一下跳他的个人主页要用。
    /// 顶层评论和楼中楼回复的 JSON 里都有 `mid` 字段(不必绕 member.mid)。
    /// </summary>
    public long Mid { get; set; }

    /// <summary>评论者等级(0~6)。0 = 接口没给, 不显示徽章</summary>
    public int Level { get; set; }

    public bool HasLevel => Level > 0;

    public string LevelText => "LV" + Level;

    /// <summary>
    /// 这条评论的作者是不是**本视频的 UP 主** → 昵称后挂 [UP] 标。
    /// 判据是评论者 mid == 顶层 data.upper.mid(接口按"当前稿件"给, 不是按评论本身),
    /// 所以只有取列表时能算, 解析单条时拿不到。
    /// </summary>
    public bool IsUp { get; set; }

    /// <summary>
    /// 这条是不是**置顶评论**(UP 主置顶 / 管理员置顶)。
    ///
    /// ★ 2026-10-03 修"置顶评论不可见": 置顶那条**不在** `data.replies` 里, 而在
    ///   `data.top.upper`(UP 主置顶)或 `data.top.admin`(管理员置顶) —— 只读 replies 的话
    ///   它永远不会出现在列表里。实测: 置顶 rpid=315658667873 在 replies 里**找不到**。
    ///   解析时由调用方置位, 界面据此在最前面挂一个"置顶"标。
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 被回复的那条评论 id。
    /// 直接回复楼主时它等于线程根(可用它判"这是给楼主的第一层回复"), 回复楼中楼里某个人时
    /// 指向那个人 —— 与 root 不同就说明这是"对话中的再回复"。
    /// </summary>
    public long ParentRpid { get; set; }

    public string UserName { get; set; } = "";
    public string UserFace { get; set; } = "";
    public string Content { get; set; } = "";

    private int _likeCount;
    public int LikeCount
    {
        get => _likeCount;
        set
        {
            if (_likeCount == value) return;
            _likeCount = value;
            Raise(nameof(LikeCount));
            Raise(nameof(LikeText));
        }
    }

    private bool _isLiked;

    /// <summary>
    /// 当前登录用户有没有给这条评论点过赞(接口的 `action` 字段: 1 = 已赞)。
    /// 带 INPC: 点赞是**从代码侧**改这个值的, 没有通知按钮的高亮不会跟着变。
    /// </summary>
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked == value) return;
            _isLiked = value;
            Raise(nameof(IsLiked));
        }
    }

    /// <summary>点赞请求进行中(防连点: 连点会造成 赞/取消 交叉, 服务端最终状态就不确定了)</summary>
    private bool _likeBusy;
    public bool LikeBusy
    {
        get => _likeBusy;
        set
        {
            if (_likeBusy == value) return;
            _likeBusy = value;
            Raise(nameof(LikeBusy));
            Raise(nameof(CanLike));
        }
    }

    /// <summary>
    /// 当前用户有没有给这条评论点过**踩**。
    ///
    /// ★ 注意接口层面的实情(2026-09-27 修正): 赞和踩是**两个不同的接口**
    /// (`/x/v2/reply/action` 与 `/x/v2/reply/hate`), 踩的 action 只在 1/0 之间取值。
    /// 评论列表里的 `action` 字段是不是真会下发 2(已踩) 并没有验证过, 所以这个值更准确的
    /// 含义是"**本地已知的踩状态**": 拿不到就当未踩, 由用户点一下来纠正。
    /// 踩**没有计数**: 接口不下发 dislike 总数(网页版也只是一个图标), 所以只有状态、没有数字。
    ///
    /// 赞与踩在服务端是互相覆盖的(点赞会消掉踩、点踩会消掉赞), 所以界面上的两个状态也必须互斥。
    /// </summary>
    private bool _isDisliked;
    public bool IsDisliked
    {
        get => _isDisliked;
        set
        {
            if (_isDisliked == value) return;
            _isDisliked = value;
            Raise(nameof(IsDisliked));
        }
    }

    public bool CanLike => !_likeBusy;

    public string RcTime { get; set; } = "";

    /// <summary>
    /// 正文里出现的表情: 短代码(`[微笑]`) → 图片地址。
    ///
    /// 为什么不能用 Unicode 直接替: B 站的评论区里绝大多数是**自定义表情**
    /// (大会员表情、直播表情、游戏联动表情), 根本没有对应的 Unicode 字符。
    /// 接口在 `content.emote` 里把"这条评论用到的表情"连图片地址一起给了,
    /// key 就是正文里原样出现的短代码, 所以按它替换即可。
    /// </summary>
    public Dictionary<string, CommentEmote> Emotes { get; } = new();

    /// <summary>
    /// IP 属地。来自接口的 `reply_control.location`, 是**已经带前缀的展示串**
    /// (形如 "IP属地：上海"), 所以直接显示、不再自己拼前缀。
    /// 可能为空(用户关了展示 / 接口没给), 空则不显示。
    /// </summary>
    public string Location { get; set; } = "";

    public bool HasLocation => Location.Length > 0;

    public string LikeText => LikeCount > 0 ? VideoItem.FormatCount(LikeCount) : "";

    // ------------------------------------------------------------ 楼中楼

    private int _replyCount;

    /// <summary>这条评论下的回复总数(服务端的 rcount)。
    /// 注意它可能**大于**已取到的条数 —— 首页接口只内联前几条。</summary>
    public int ReplyCount
    {
        get => _replyCount;
        set
        {
            if (_replyCount == value) return;
            _replyCount = value;
            Raise(nameof(ReplyCount));
            Raise(nameof(HasReplies));
            Raise(nameof(HasMoreReplies));
            Raise(nameof(ReplyMoreText));
            Raise(nameof(ReplyBoxText));
        }
    }

    /// <summary>已取到的回复(内联的前几条 + 后续展开补齐的)</summary>
    public ObservableCollection<CommentItem> Replies { get; } = new();

    // ------------------------------------------------------------ 楼中楼预览方框

    /// <summary>方框底部那行提示文字(点整框进详情页)</summary>
    public string ReplyBoxText => ReplyCount > 0 ? $"共 {ReplyCount} 条回复" : "";

    /// <summary>
    /// 方框/详情页里一条回复的**前缀**(用户名那一截)。
    ///
    /// 规则跟 B 站网页版一致:
    ///   · 正文以 "回复 @XXX：" 开头(接口对"回复楼中楼里的人"的正文**自带**这截前缀)
    ///     → 前缀只给"用户名 "一个空格, 免得出现"用户名 回复 @XX 回复 @XX"；
    ///   · 否则(直接回复楼主) → 前缀给"用户名：", 冒号由我们补。
    /// UP 主的评论在用户名后面挂 [UP]。
    /// </summary>
    public string ReplyPrefix
    {
        get
        {
            var name = UserName;
            if (IsUp) name += " [UP]";
            return Content.StartsWith("回复 @", StringComparison.Ordinal) ? name + " " : name + "：";
        }
    }

    /// <summary>
    /// 正文是否以 "回复 @XXX：" 开头 —— 接口对"回复楼中楼里某个人"的正文**自带**这截前缀。
    /// 详情页据此挂"查看对话"入口(直接回复楼主的那条没有对话可追)。
    /// </summary>
    public bool IsReplyToSomeone => Content.StartsWith("回复 @", StringComparison.Ordinal);

    /// <summary>
    /// 楼中楼"展开更多"下一次要请求的页码。
    /// 之前固定 pn=1: 超过 20 条的楼中楼第二页永远拉不到(去重后一条不增,
    /// 按钮却一直显示"展开 N 条回复"点不动)。首次展开前由加载方置为 1, 之后自增。
    /// </summary>
    public int RepliesNextPage { get; set; } = 1;

    /// <summary>还有没取到的回复 → 显示"展开 N 条回复"</summary>
    public bool HasMoreReplies => ReplyCount > Replies.Count;

    /// <summary>
    /// 有没有楼中楼 → 决定"查看详情"入口显不出来。
    /// 没有楼中楼的评论详情页就是它自己, 没有可看的内容, 入口不显示。
    /// </summary>
    public bool HasReplies => ReplyCount > 0;

    public string ReplyMoreText => ReplyCount > Replies.Count
        ? $"展开 {ReplyCount - Replies.Count} 条回复"
        : "";

    private bool _loadingReplies;

    public bool IsLoadingReplies
    {
        get => _loadingReplies;
        set
        {
            if (_loadingReplies == value) return;
            _loadingReplies = value;
            Raise(nameof(IsLoadingReplies));
        }
    }

    /// <summary>
    /// 补齐回复后手动通知一次。
    /// ObservableCollection 的数量变化只会通知集合本身, 不会带上 HasMoreReplies / ReplyMoreText
    /// 这两个依赖 Count 的计算属性, 所以必须显式补一刀。
    /// </summary>
    public void NotifyRepliesChanged()
    {
        Raise(nameof(HasMoreReplies));
        Raise(nameof(ReplyMoreText));
        // 展开后"共 N 条回复"可能跟着变(用实际条数收口时), 一并通知
        Raise(nameof(ReplyBoxText));
    }
}

/// <summary>云端收藏夹(账号下的一个收藏夹分组)</summary>
public class FavFolder : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; set; }
    public string Title { get; set; } = "";

    private int _mediaCount;

    /// <summary>
    /// 夹内条目数。
    /// 带变更通知是因为"批量移出"之后这个数要跟着减 —— 而**不能**靠"从集合里移除再插回来"
    /// 来触发刷新: 那会重建收藏夹 chip 的容器, 而 RadioButton 的分组选中态是挂在容器上的,
    /// 容器一重建, 高亮就没了(用户看到的是"删了几个之后选中的收藏夹标签突然不亮了")。
    /// </summary>
    public int MediaCount
    {
        get => _mediaCount;
        set
        {
            if (_mediaCount == value) return;
            _mediaCount = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MediaCount)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayText)));
        }
    }

    /// <summary>
    /// 某个**具体视频**是否在这个收藏夹里。
    /// 只有"按 rid 查收藏夹"接口(/x/v3/fav/folder/created/list-all?rid=)才会带上这个语义
    /// (响应里的 fav_state 字段); 普通的收藏夹列表接口不返回它, 此时保持 false。
    /// </summary>
    public bool HasVideo { get; set; }

    private bool _isCurrent;

    /// <summary>
    /// 是否是当前选中的收藏夹(收藏页 chip 的高亮)。
    /// 必须带通知: "从「我的」页点卡片进来"这类**程序切换**不经过用户点击,
    /// RadioButton 的选中态只能靠这里推过去 —— 否则数据换了、高亮还停在旧夹上,
    /// 看起来就像"点了 A 打开了 B"。
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    /// <summary>下拉里显示的 "标题 (数量)"</summary>
    public string DisplayText => MediaCount > 0 ? $"{Title} ({MediaCount})" : Title;

    /// <summary>
    /// 收藏夹封面(「我的」页的文件夹卡片用)。
    /// created/list 接口才下发; 个别夹(比如刚建的空夹)可能为空串, 界面自己放兜底底色。
    /// </summary>
    public string Cover { get; set; } = "";

    /// <summary>隐私状态: 0=公开 1=私密(接口不下发时按 0 处理)</summary>
    public int Privacy { get; set; }

    public string PrivacyText => Privacy == 1 ? "私密" : "公开";
}

/// <summary>
/// 私信会话(消息页左侧列表的一项)。
/// 需要 INotifyPropertyChanged 是因为: 打开会话要清未读角标、发完消息要更新摘要,
/// 这两件事都发生在对象已经绑到界面上之后。
/// </summary>
public class MsgSession : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>对话对象的 UID(群聊时是群 id)</summary>
    public long TalkerId { get; set; }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Raise(nameof(Name)); Raise(nameof(DisplayName)); }
    }

    private string _face = "";
    public string Face
    {
        get => _face;
        set { if (_face == value) return; _face = value; Raise(nameof(Face)); }
    }

    private string _preview = "";
    /// <summary>最后一条消息的摘要</summary>
    public string Preview
    {
        get => _preview;
        set { if (_preview == value) return; _preview = value; Raise(nameof(Preview)); }
    }

    private string _timeText = "";
    /// <summary>最后活跃时间</summary>
    public string TimeText
    {
        get => _timeText;
        set { if (_timeText == value) return; _timeText = value; Raise(nameof(TimeText)); }
    }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set { if (_unreadCount == value) return; _unreadCount = value; Raise(nameof(UnreadCount)); Raise(nameof(HasUnread)); }
    }

    /// <summary>已读到的序号 / 服务端最新序号(标记已读用)</summary>
    public long AckSeqno { get; set; }
    public long MaxSeqno { get; set; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>服务端名字拿不到时兜底显示</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? $"UID {TalkerId}" : Name;
}

/// <summary>一条私信</summary>
public class MsgItem
{
    public long SenderUid { get; set; }
    /// <summary>消息序号: 用它排序而不是靠接口返回顺序 —— 接口在新旧方向上没有稳定保证</summary>
    public long Seqno { get; set; }
    /// <summary>是不是自己发的(决定气泡靠左还是靠右)</summary>
    public bool IsMine { get; set; }
    public string Text { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string TimeText { get; set; } = "";

    public bool IsImage => ImageUrl.Length > 0;
    public bool IsText => ImageUrl.Length == 0;
}

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

/// <summary>会话(登录 Cookie)</summary>
public class Session
{
    public string? SessData { get; set; }
    public string? BiliJct { get; set; }
    public string? DedeUserID { get; set; }
    public string? DedeUserIDCkMd5 { get; set; }
    public string? Buvid3 { get; set; }

    /// <summary>
    /// 设备指纹的第二半(buvid4)。
    ///
    /// ★ 必须持久化, 不能只放内存。推荐系统靠 (buvid3 + buvid4) 这一对认"这是同一台设备",
    ///   而 `x/frontend/finger/spi` **每次调用都发一对全新的** —— 实测: 即使把已有的
    ///   buvid3/buvid4 带上去, 它照样回吐两个新的, 不做 round-trip。所以只要不在本地存下来,
    ///   每次启动都会变成一台"从未见过的设备", 推荐模型每次都要从零冷启动,
    ///   表现就是"推的东西东一榔头西一棒槌"。
    /// 老配置没有这个字段 → 反序列化保持 null → 下次启动补上并落盘。
    /// </summary>
    public string? Buvid4 { get; set; }
}

/// <summary>应用设置</summary>
public class AppSettings
{
    public AppTheme ThemeMode { get; set; } = AppTheme.System;

    /// <summary>弹幕开关(全局, 播放器不再提供弹幕按钮)</summary>
    public bool DanmakuEnabled { get; set; } = true;

    /// <summary>弹幕显示区域占比(25~100, 单位: 画面高度百分比)</summary>
    public int DanmakuAreaPercent { get; set; } = 25;

    /// <summary>弹幕智能屏蔽(过滤超长/刷屏弹幕, 降低卡顿)</summary>
    public bool DanmakuSmartFilter { get; set; } = true;

    /// <summary>
    /// 彩色弹幕。接口给的颜色非白时按原色渲染; 关掉之后所有弹幕统一白色。
    /// 默认开 —— 彩色弹幕是 B 站的既定玩法, 不看颜色等于丢信息。
    /// </summary>
    public bool DanmakuColorful { get; set; } = true;

    /// <summary>
    /// 弹幕关键词屏蔽规则。多个词用换行或逗号分隔;
    /// 以 "re:" 开头的行按正则处理(用于 "只要包含日期就屏蔽" 这类模式匹配)。
    /// </summary>
    public string DanmakuBlockKeywords { get; set; } = "";

    /// <summary>
    /// 首页「推荐」tab 的算法来源。★ 2026-10-03 起固定为网页版(字段仅为兼容老配置保留,
    /// 读取时一律强制 Web, 见 SettingsStore.Load)。
    /// </summary>
    public RecommendSource RecommendSource { get; set; } = RecommendSource.Web;

    /// <summary>
    /// 是否启用"跳过赞助片段"(SponsorBlock)。
    ///
    /// ★ **默认关**(用户 2026-09-27 拍板): 开启后每条视频的 BV 号都会被发到第三方服务器
    /// (www.bsbsb.top), 等于把"你在看什么"告诉别人。这是真实的隐私代价, 必须由用户
    /// 明确选择, 不能默认替他送出去。
    ///
    /// (属性初始化就是 false —— 老配置缺这个字段时会保持初始化值, 自动落到"关"。)
    /// </summary>
    public bool SponsorBlockEnabled { get; set; }

    /// <summary>
    /// 要跳过的片段类别(逗号分隔, 存服务端的 category 原值)。
    /// 默认只勾"赞助广告" —— 那是这个功能的立身之本; 片头片尾属于个人口味, 交给用户自己开。
    /// 存字符串而不是位标志: 类别以后可能增删, 位标志会像枚举重排那样错位。
    /// </summary>
    public string SponsorBlockCategories { get; set; } = SponsorCategories.Sponsor;

    /// <summary>
    /// 主题色(强调色)。空串 = 用默认的 B 站粉(见 <see cref="Helpers.ThemeService.DefaultAccent"/>),
    /// 非空时是 `#RRGGBB` 形式。
    ///
    /// 为什么默认空串而不是直接存 "#FB7299": 默认色以后可能会调, 空串表示"跟着默认走",
    /// 老配置(没这个字段 → 空串)自动落到默认值, 用户显式选过的颜色才被记住。
    /// </summary>
    public string AccentColor { get; set; } = "";

    /// <summary>
    /// 关闭主窗口时最小化到托盘(默认 true)。
    /// 关掉之后点右上角关闭键就是真的退出程序, 不再收到托盘里。
    /// 属性初始化就是 true —— 老配置缺这个字段时会保持初始化值, 于是"默认打开"自动成立。
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// 启动后自动检查更新(默认 true)。
    /// 检查是匿名的 GitHub Releases 请求, 不带任何账号信息; 关掉后仍可在设置页手动检查。
    /// 老配置缺这个字段时保持初始化值 ⇒ "默认打开"自动成立, 不需要版本号兼容。
    /// </summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>
    /// 用户点过「跳过此版本」的版本号(如 "1.2.0")。空 = 没跳过任何版本。
    /// 只对**自动**检查生效: 手动点「检查更新」说明用户就是想看, 一样会弹窗。
    /// </summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>
    /// 免登录 1080P(默认 true)。
    ///
    /// 未登录时在取流请求里补上 try_look + 四个 dm_img_* 设备指纹参数, 把匿名可见的
    /// 清晰度从 360P/480P 提到 720P/1080P —— 做法与参数依据见 ApiClient.ApplyTryLook。
    ///
    /// 默认 true 与 PiliPlus 一致: 只影响"未登录"这一种状态, 已登录用户按账号等级取流,
    /// 这个开关对它们没有任何作用(ApplyTryLook 会直接跳过)。
    /// 老配置缺这个字段时保持初始化值 ⇒ "默认打开"自动成立, 不需要版本号兼容。
    /// </summary>
    public bool NoLogin1080P { get; set; } = true;

    /// <summary>
    /// 视频线路(CDN)选择策略。默认自动测速 —— 实测各家 CDN 同一时刻吞吐能差 4 倍以上,
    /// 且排名随网络/地区/时段变化, 所以默认让程序自己挑。详见 Services/CdnService.cs。
    /// </summary>
    public CdnSelectMode CdnMode { get; set; } = CdnSelectMode.Auto;

    /// <summary>
    /// 手动模式下选定的 CDN Id(见 Models/CdnOption.All)。
    /// 存 Id 而不是 host: B 站换域名时 Id 不变, 老配置才不会失效。
    /// 读配置时用 CdnOption.ById 校验, 已下线的 Id 会被清成空串(退回自动)。
    /// </summary>
    public string CdnManualId { get; set; } = "";

    /// <summary>
    /// 屏蔽 PCDN(点对点分发)。默认开 —— PCDN 拿其他用户的带宽做节点, 速度不稳定;
    /// 实测同一档清晰度常规 CDN 吞吐高一倍以上。
    /// </summary>
    public bool BlockPcdn { get; set; } = true;

    /// <summary>
    /// 是否已同意首次启动的免责声明。
    /// 默认 false —— 老版本升级上来的用户也会被问一次, 这是有意的:
    /// 声明里有"账号风控/数据丢失风险自担"这类实质内容, 让所有人在同一份文本上确认一次更稳妥。
    /// </summary>
    public bool DisclaimerAccepted { get; set; }

    /// <summary>
    /// 用户同意的是**第几版**声明(对应 <see cref="Helpers.DisclaimerText.Version"/>)。
    ///
    /// 单独记版本号而不是只留一个 true/false: 声明正文以后还会改, 改完要再让用户确认一次 ——
    /// 否则"你事后能翻到的, 就是你当初点同意时看到的那一份"这句话就不成立了。
    /// 老配置里没有这个字段(值为 0), 兼容处理见 SettingsStore.Load。
    /// </summary>
    public int DisclaimerVersion { get; set; }
}

/// <summary>
/// 供页面往「视频卡片」右键菜单里追加的条目定义。
///
/// 为什么不用现成的 MenuItem:
/// 直接把 MenuItem 实例放进集合再让 DataTemplate 去渲染, 会丢失"命令参数应该是当前卡片的数据"
/// 这一层语义(MenuItem 自己的 DataContext 是它自身)。用一个纯数据的中转类型,
/// 模板里就能明确地把卡片的 DataContext 作为 CommandParameter 注入。
/// </summary>
public class CardMenuItem
{
    public string Header { get; set; } = "";
    public System.Windows.Input.ICommand? Command { get; set; }

    /// <summary>
    /// 菜单项左侧的图标字形(Segoe Fluent Icons 码位, 形如 "\uE896")。
    /// 留空则图标列收成 0 宽, 不会留下空档。
    /// </summary>
    public string Glyph { get; set; } = "";
}