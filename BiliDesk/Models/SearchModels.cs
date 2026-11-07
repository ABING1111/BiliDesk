using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 搜索: 内容种类 / 结果聚合 / 用户结果 / 动态导航条目 / 热搜

public enum ContentKind
{
    /// <summary>视频稿件(搜索 search_type=video / 历史 type=archive)</summary>
    Video,

    /// <summary>直播间(搜索 search_type=live_room / 历史 type=live)</summary>
    Live,

    /// <summary>
    /// 番剧(搜索 search_type=bangumi, 2026-10-11 新增)。
    ///
    /// ★ 它**不是**"视频的一个子类": 番剧按 season_id 寻址、封面竖版、点开是选集页,
    ///   既没有 bvid 也播不了 —— 塞进 VideoItem 会让点击走错路。
    ///   所以搜索结果单独走 <see cref="SearchData.BangumiItems"/>, 页面用海报卡渲染。
    /// </summary>
    Bangumi,

    /// <summary>
    /// 用户 / UP 主(搜索 search_type=bili_user, 2026-11-06 新增, issue #47)。
    ///
    /// ★ 只用于**搜索页**: 历史页只有视频/直播两种记录, 永远不会出现这个值。
    /// ★ 必须追加在枚举**末尾**: 用户配置里的 LastSearchKind 存的是 int,
    ///   插到 Bangumi 前面会把老配置的 2(番剧)读成 User。
    /// </summary>
    User,
}

/// <summary>搜索 API 返回结构</summary>
public class SearchData
{
    public bool Ok { get; set; }
    public bool Blocked { get; set; }    // 被风控拦截
    public string? Error { get; set; }
    public System.Collections.Generic.List<VideoItem> Items { get; set; } = new();

    /// <summary>
    /// 番剧结果(2026-10-11 新增)。与 <see cref="Items"/> **并存但互斥**:
    /// 搜番剧时 <see cref="Items"/> 为空、结果全在这里; 搜视频/直播时反之。
    /// 分开而不是塞进 Items, 是因为两者点开的去向完全不同(见 <see cref="ContentKind.Bangumi"/>)。
    /// </summary>
    public System.Collections.Generic.List<BangumiItem> BangumiItems { get; set; } = new();

    /// <summary>
    /// 用户 / UP 主结果(2026-11-06 新增, issue #47)。
    /// 与 <see cref="Items"/> / <see cref="BangumiItems"/> 互斥: 只有搜「UP主」分类时才有内容。
    /// </summary>
    public System.Collections.Generic.List<SearchUserItem> UserItems { get; set; } = new();

    public bool HasMore { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// 搜索接口(search_type=bili_user)返回的一条用户 / UP 主。
///
/// ★ 字段按实测响应取(2026-11-06, 关键词「极客湾」):
///   mid / uname / upic / usign / fans / videos / level / is_upuser / official_verify。
/// ★ upic 可能以 `//` 开头, 展示前必须过 UrlUtil.Normalize(与直播封面同款)。
/// ★ 与关注列表的 FollowUser 分开: 那个模型带动态页的选中/未读状态, 搜索行不需要,
///   而且这里多了粉丝数/投稿数; 硬复用会把两边的字段越绑越乱。
/// </summary>
public sealed class SearchUserItem
{
    public long Mid { get; set; }
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public string Sign { get; set; } = "";
    public long Fans { get; set; }
    public long Videos { get; set; }
    public int Level { get; set; }

    /// <summary>
    /// -1 = 未认证(统一值); 0 = 个人认证; 1 = 机构认证。
    /// ★ 接口对"没认证"的用户返回的 official_verify.type 是 **127**, 不是 -1;
    ///   解析时统一归一成 -1, 界面只认 0/1, 否则每个普通用户名字旁边都会多一个认证勾。
    /// </summary>
    public int VerifyType { get; set; } = -1;
    public string VerifyDesc { get; set; } = "";

    public bool HasVerify => VerifyType is 0 or 1;
    public string FansText => VideoItem.FormatCount(Fans) + " 粉丝";
    public string VideosText => VideoItem.FormatCount(Videos) + " 投稿";
    public string LevelText => Level > 0 ? $"Lv{Level}" : "";
    public bool HasSign => !string.IsNullOrWhiteSpace(Sign);

    /// <summary>
    /// 第二行的统计串("567万 粉丝 · 643 投稿 · Lv6")。
    /// ★ 在模型里拼而不是在 XAML 里摆三个 TextBlock + 三个分隔点:
    ///   粉丝/投稿为 0 时那一段要整段省略, 分开摆一定会留下"· ·"这种悬空分隔。
    /// </summary>
    public string MetaText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>(3);
            if (Fans > 0) parts.Add(FansText);
            if (Videos > 0) parts.Add(VideosText);
            if (Level > 0) parts.Add(LevelText);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// 动态导航接口(feed/nav)里的一条动态的**轻量元数据**。
///
/// 与 <see cref="VideoItem"/> 分开: 未读红点只需要"谁在什么时候发了哪条动态",
/// 不需要封面/标题/播放量; 单独解析可以复用同一个接口而不用把整套视频卡片数据搬一遍。
/// </summary>
public sealed class DynamicNavEntry
{
    /// <summary>动态 id(id_str)。用于判断新旧, 比 bvid 更可靠(转发/非视频动态也有 id)。</summary>
    public string Id { get; set; } = "";

    /// <summary>发布者 mid(动态的 author, 转发动态是转发者本人)。</summary>
    public long Mid { get; set; }

    /// <summary>发布者昵称(动态流 author.name / portal uname)。未读置顶分组要显示名字。</summary>
    public string AuthorName { get; set; } = "";

    /// <summary>
    /// 发布者头像地址(feed/nav 的 author.face, 2026-11-07)。
    ///
    /// ★ 为什么必须有(用户报"动态页刚更新的 UP 显示默认头像"):
    ///   左栏未读占位行的头像取自 <c>UpNotifyState.Face</c>; 之前 feed/nav 这条解析路径
    ///   只读 mid/name 不读 face, 于是"刚被动态流抓到的那个 UP"头像一直是空串,
    ///   那一格就露出了模板里的默认人形图标 —— 只有 portal 的 up_list 恰好也收录
    ///   它时才偶尔补上, 表现为"时好时坏"。
    /// </summary>
    public string AuthorFace { get; set; } = "";

    /// <summary>发布时间(Unix 秒; 接口只给 "2小时前" 时是近似值)。</summary>
    public long Pubdate { get; set; }

    /// <summary>视频 bvid(仅视频动态; 非视频为空)。</summary>
    public string Bvid { get; set; } = "";

    /// <summary>动态类型(8=视频; 转发=1, 等)。</summary>
    public int Type { get; set; }
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

