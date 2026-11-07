using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 哔哩哔哩 API 客户端: 带浏览器 UA、Cookie、WBI 签名与风控降级逻辑。
/// ★ 2026-11-07 由单文件拆成 17 个 partial 分片(纯移动、零逻辑改动),
///   分片清单与动机见 ApiClient.Core.cs 头部说明。
/// </summary>
public partial class ApiClient
{
    // ---------------------------------------------------------------- 业务接口

    /// <summary>热门视频</summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetPopularAsync(int pn)
    {
        return await GetVideoListAsync(
            "https://api.bilibili.com/x/web-interface/popular",
            new Dictionary<string, string> { ["ps"] = "20", ["pn"] = pn.ToString() });
    }

    /// <summary>
    /// 分区内容(**B站现行分区页的数据源**)。原生分区页走这个。
    ///
    /// ★★★ 2026-10-17 整体重做(用户报"分区视频数据有问题, 重新做该功能")。
    ///
    ///   根因: 旧实现走 <c>ranking/v2?rid=旧tid</c>(美食=211)。实测那个组合返回的是
    ///   **约 564 天前**的陈旧榜单 —— 20 个分区里中位年龄普遍 560~580 天, 这就是
    ///   "数据有问题" 的直接来源。而 B站网页版现行分区页(/c/food/)用的是
    ///   <c>region/feed/rcmd</c> + **v2 tid**(美食=1020), 实测中位年龄只有 3~9 天,
    ///   与用户截图里的内容完全一致。
    ///
    ///   接口: <c>GET /x/web-interface/region/feed/rcmd?display_id={n}&amp;request_cnt={20}
    ///   &amp;from_region={v2tid}&amp;device=web&amp;plat=30</c>, 数据在 <c>data.archives[]</c>。
    ///
    ///   实测(2026-10-17, 带 buvid 设备指纹):
    ///     · <c>from_region</c> 只认 **v2 tid**(1001~1031); 传旧 tid(211/1/3…)一律 -400。
    ///     · <c>request_cnt</c> 上限 20: 21 起返回 62013「暂时没有更多内容了」。
    ///     · <c>display_id</c> 是**游标**, 从 1 递增即可持续翻页: 美食 25 页 = 500 条不重复,
    ///       页间重复率随深度上升(第 20 页仍有 2 条新的), 说明服务端在滚动推荐池。
    ///     · 条目字段(aid/bvid/cid/title/cover/duration/pubdate/stat{view,like,danmaku}/author{mid,name})
    ///       与 popular 的**语义**同构, 但**字段名有两处不同**: 封面是 <c>cover</c> 而不是 <c>pic</c>,
    ///       UP 主是 <c>author{}</c> 而不是 <c>owner{}</c>。
    ///       <see cref="ParseVideoItem"/> 两种都认 —— 只认前者的话分区页会"标题正常、封面全灰、
    ///       UP 主名空白"(探针 bd-probe-zone 抓着这个 bug)。
    ///
    ///   ★ 为什么要设备指纹(buvid): 不带 cookie 时, 鬼畜/vlog/小剧场 会返回
    ///     62013「暂时没有更多内容了」(空列表), 带上就正常 —— 又是一处"看起来像接口没数据、
    ///     其实是风控认不出设备"的坑。所以 <see cref="GetJsonAsync"/> 默认就带指纹。
    ///
    ///   ★ 为什么不做"热门+最新混合"了(旧实现的思路): 既然这个接口本身就是
    ///     "按分区 + 按质量 + 近期" 的推荐流, 混入就不需要了 —— 旧的混合是为了绕开
    ///     ranking 全是陈年榜单的问题, 换源之后那个问题的前提不存在了。
    /// </summary>
    /// <param name="tid">**v2 分区 id**(见 <see cref="PartitionCatalog"/>), 不是旧 tid。</param>
    /// <param name="page">游标, 从 1 开始。同一个分区连续翻页时才递增。</param>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetRegionFeedAsync(int tid, int page = 1)
    {
        try
        {
            await EnsureBuvidAsync();
            // ★ 这里**不用** GetVideoListAsync: 它把非 0 码压成一句 message, 拿不到**码**本身,
            //   而本接口恰恰需要区分 62013「暂时没有更多内容了」与真正的失败(见下)。
            //   字符串匹配 message 也能凑合, 但服务端改文案就会悄悄失效 —— 直接看码才可靠。
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/region/feed/rcmd",
                new Dictionary<string, string>
                {
                    // 接口硬上限: request_cnt >20 直接 62013。写死 20, 调用方不必关心。
                    ["display_id"] = Math.Max(1, page).ToString(),
                    ["request_cnt"] = "20",
                    ["from_region"] = tid.ToString(),
                    ["device"] = "web",
                    ["plat"] = "30"
                });

            // ★ 62013 = **正常的翻到底**, 不是错误: 翻译成"成功但空列表", 让页面能区分
            //   "到头了"与"请求失败", 而不是在列表底部弹一句吓人的报错(旧实现就是这么错的,
            //   探针 bd-probe-zone 抓着这个 bug)。
            if (code == 62013) return (true, null, new List<VideoItem>());
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            if (!data.Value.TryGetProperty("archives", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return (true, null, new List<VideoItem>());

            var items = new List<VideoItem>();
            foreach (var e in list.EnumerateArray())
            {
                var item = ParseVideoItem(e);
                if (item != null) items.Add(item);
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>分区流每页条数(接口硬上限 20, 见 <see cref="GetRegionFeedAsync"/>)。</summary>
    public const int RegionPageSize = 20;

    /// <summary>
    /// 全站排行榜(rid=0 = 全站, 与分区榜同一个接口)。
    /// </summary>
    public Task<(bool ok, string? err, List<VideoItem>? items)> GetRankingAsync() => GetRegionRankAsync(0);

    /// <summary>
    /// 排行榜。ranking/v2 的 rid 参数决定范围: 0 = 全站, 其余是**old tid**。
    ///
    /// ★ 首页「排行榜」tab 用它。注意 rid=0(全站)与分区是两套表现, 别拿分区 rid 来套:
    ///   实测(2026-10-17) 这个接口对**v2 tid**(1005 动画 / 1020 美食 / 1009 资讯) 一律 -400,
    ///   只有全站(0)与**旧 tid**(1/3/4/36/188…) 可用; 而旧 tid 那批返回的是约 564 天前的
    ///   陈年榜单 —— 分区页**不要**再走这里, 用 <see cref="GetRegionFeedAsync"/>。
    /// </summary>
    public Task<(bool ok, string? err, List<VideoItem>? items)> GetRegionRankAsync(int rid)
    {
        return GetVideoListAsync(
            "https://api.bilibili.com/x/web-interface/ranking/v2",
            new Dictionary<string, string> { ["rid"] = rid.ToString(), ["type"] = "all" },
            jsonProp: "list");
    }

    /// <summary>
    /// 首页个性化推荐(浏览器网页版「为你推荐」, wbi/index/top/feed/rcmd)。
    ///
    /// ★ 为什么只剩网页这一条路(2026-10-03 定):
    ///   以前还有个"B 站官方 App 算法"选项(app.bilibili.com/x/v2/feed/index)。
    ///   实测它对第三方客户端**不提供个性化** —— 即便拿到了有效的 access_key
    ///   (电视端扫码, myinfo 能返回真实昵称), 该接口返回的仍是全站通用热门池:
    ///   100 条里命中用户关注的 UP 只有 1~2 个、不重复 UP 达 90/100、平均播放量 66~103 万;
    ///   而网页 rcmd 的平均播放量只有 17 万、内容明显更垂直。参数层面穷举过 20 多种组合
    ///   (UA / buvid / session_id / recsys_mode / idx 游标接力 / fnval 变体)都无改善 ——
    ///   服务端按"手机端登录身份 + 手机端设备指纹"才给个性化, 第三方拿不到那个身份组合。
    ///   所以那个选项与 access_key 机制已整体删除, 只保留这条真正个性化的网页路。
    ///
    /// 失败时自动重试一次(刷新 WBI 密钥), 仍未成功则降级返回热门, 保证首页不会是空列表。
    /// </summary>
    /// <param name="reset">保留下拉刷新语义(此接口每次请求都返回新一批, 参数不影响)</param>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetRecommendAsync(bool reset = false)
    {
        _ = reset;
        try
        {
            await EnsureBuvidAsync();
            var (ok, err, items) = await GetWebRecommendCoreAsync();
            if (!ok || items == null || items.Count == 0)
            {
                // 重试一次。
                // ★ 这里**故意不**清 _buvid3/_buvid4: 设备指纹是推荐系统认人的凭据,
                //   清掉它们等于"失败一次就换一台新设备重来", 会让推荐模型重新冷启动
                //   —— 与"让推荐更准"正相反。真正需要刷新的是 WBI 密钥(会过期)。
                _wbiKeys = null;
                await EnsureBuvidAsync();
                (ok, err, items) = await GetWebRecommendCoreAsync();
            }
            if (ok && items != null && items.Count > 0) return (true, null, items);
            // 失败 -> 降级热门
            return await GetPopularAsync(1);
        }
        catch
        {
            return await GetPopularAsync(1);
        }
    }

    private async Task<(bool ok, string? err, List<VideoItem>? items)> GetWebRecommendCoreAsync()
    {
        try
        {
            // 现行 web 首页推荐接口(top feed), 需要 WBI 签名
            var ps = new Dictionary<string, string>
            {
                ["fresh_type"] = "4",
                ["ps"] = "30",
                ["fresh_idx"] = "1",
                ["fresh_idx_1h"] = "1",
                ["brush"] = "1",
                ["web_location"] = "1430650"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/wbi/index/top/feed/rcmd",
                ps, sign: true);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("item", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    // 过滤直播/边栏等非视频项
                    if (GetStr(e, "goto") != "av") continue;
                    var item = ParseRecommendItem(e);
                    if (item != null) items.Add(item);
                }
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>关注列表(关注的 UP 主)。vmid=自己 mid, pn=页码, ps=每页数量(<=50)。</summary>
    public async Task<(bool ok, string? err, List<FollowUser>? list, int total)> GetFollowingsAsync(
        long vmid, int pn = 1, int ps = 50)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps2 = new Dictionary<string, string>
            {
                ["vmid"] = vmid.ToString(),
                ["pn"] = pn.ToString(),
                ["ps"] = Math.Min(ps, 50).ToString(),
                ["order"] = "desc"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation/followings", ps2);
            if (code != 0 || data == null)
            {
                var errMsg = msg ?? $"请求失败 (code {code})";
                if (code == -101) errMsg = "请先登录";
                return (false, errMsg, null, 0);
            }

            var users = ParseFollowUserList(data.Value, out var total);
            return (true, null, users, total);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, 0);
        }
    }

    /// <summary>
    /// 在**已关注的 UP**里按名字搜索(2026-11-06 动态页搜索用)。
    ///
    /// 为什么用这条而不是全局 `search_type=bili_user`:
    ///   · 动态页左栏只需要"我关注的 UP", 全局搜索会返回没关注的人;
    ///   · 关注列表可能有几千人, 本地过滤要求先把所有页拉下来 —— 用服务端搜索
    ///     可以只返回匹配的已关注用户, 一条请求就能找到人。
    ///
    /// 接口: GET /x/relation/followings/search?vmid=&name=&ps=50&pn=1
    /// 返回的 list 与关注列表同一套字段(mid/uname/face/sign/official_verify)。
    /// </summary>
    public async Task<(bool ok, string? err, List<FollowUser>? list, int total)> SearchFollowingsAsync(
        long vmid, string keyword, int pn = 1, int ps = 50)
    {
        if (vmid <= 0) return (false, "缺少用户 mid", null, 0);
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return (true, null, new List<FollowUser>(), 0);

        try
        {
            await EnsureBuvidAsync();
            var ps2 = new Dictionary<string, string>
            {
                ["vmid"] = vmid.ToString(),
                ["name"] = keyword,
                ["pn"] = pn.ToString(),
                ["ps"] = Math.Min(ps, 50).ToString(),
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation/followings/search", ps2,
                referer: "https://www.bilibili.com/");
            if (code == 0 && data == null)
                return (true, null, new List<FollowUser>(), 0);   // 没搜到, 不是失败
            if (code != 0 || data == null)
            {
                var errMsg = msg ?? $"请求失败 (code {code})";
                if (code == -101) errMsg = "请先登录";
                return (false, errMsg, null, 0);
            }

            var users = ParseFollowUserList(data.Value, out var total);
            return (true, null, users, total);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, 0);
        }
    }

    /// <summary>把关注列表 / 关注搜索的 data.list 解析成 FollowUser 列表。</summary>
    private static List<FollowUser> ParseFollowUserList(JsonElement data, out int total)
    {
        total = 0;
        if (data.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
            total = t.GetInt32();

        var users = new List<FollowUser>();
        if (data.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var u = ParseFollowUser(e);
                if (u != null) users.Add(u);
            }
        }
        return users;
    }

    /// <summary>关注列表/关注搜索里的一行。字段与搜索页的 SearchUserItem 不同, 不复用。</summary>
    private static FollowUser? ParseFollowUser(JsonElement e)
    {
        var u = new FollowUser
        {
            Mid = GetLong(e, "mid"),
            Name = GetStr(e, "uname"),
            Face = UrlUtil.Normalize(GetStr(e, "face")),
            Sign = GetStr(e, "sign")
        };
        if (e.TryGetProperty("official_verify", out var ov) && ov.ValueKind == JsonValueKind.Object)
        {
            u.VerifyType = GetInt(ov, "type");
            u.VerifyDesc = GetStr(ov, "desc");
        }
        return u.Mid > 0 && !string.IsNullOrEmpty(u.Name) ? u : null;
    }

    private static VideoItem? ParseRecommendItem(JsonElement e)
    {
        try
        {
            var item = new VideoItem
            {
                Bvid = GetStr(e, "bvid"),
                Title = GetStr(e, "title").StripHtml(),
                Cover = UrlUtil.Normalize(GetStr(e, "pic")),
                Author = e.TryGetProperty("owner", out var o) ? GetStr(o, "name") : "",
                OwnerMid = e.TryGetProperty("owner", out var om) ? GetLong(om, "mid") : 0,
                Duration = VideoItem.FormatSeconds(GetInt(e, "duration")),
                Pubdate = GetLong(e, "pubdate"),
            };
            if (string.IsNullOrEmpty(item.Bvid)) return null;
            if (e.TryGetProperty("stat", out var st))
            {
                if (st.TryGetProperty("view", out var v) && v.ValueKind == JsonValueKind.Number)
                    item.ViewCount = v.GetInt64();
                if (st.TryGetProperty("danmaku", out var d) && d.ValueKind == JsonValueKind.Number)
                    item.DanmakuCount = d.GetInt64();
                if (st.TryGetProperty("like", out var lk) && lk.ValueKind == JsonValueKind.Number)
                    item.LikeCount = lk.GetInt64();
            }
            return item;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool ok, string? err, List<VideoItem>? items)> GetVideoListAsync(
        string url, Dictionary<string, string> ps, string jsonProp = "list")
    {
        try
        {
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(url, ps);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            if (!data.Value.TryGetProperty(jsonProp, out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return (true, null, new List<VideoItem>());

            var items = new List<VideoItem>();
            foreach (var e in list.EnumerateArray())
            {
                var item = ParseVideoItem(e);
                if (item != null) items.Add(item);
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    private static VideoItem? ParseVideoItem(JsonElement e)
    {
        try
        {
            var item = new VideoItem();
            item.Bvid = GetStr(e, "bvid");
            if (string.IsNullOrEmpty(item.Bvid)) return null;
            item.Title = GetStr(e, "title").StripHtml();
            // ★★ 封面字段名两个接口不一致(2026-10-17 修 "分区页卡片全灰、UP 主名空白"):
            //   popular/ranking/newlist/related 用 `pic` + `owner{mid,name}`,
            //   而 region/feed/rcmd(分区流) 用 `cover` + `author{mid,name}`。
            //   以前只认前者, 于是分区页拿到了标题和时间、封面却是空的 —— 卡片一片灰。
            //   两种都认, 谁在就用谁(实测没有哪个接口两个字段同时给且含义不同)。
            item.Cover = UrlUtil.Normalize(GetStr(e, "pic"));
            if (item.Cover.Length == 0) item.Cover = UrlUtil.Normalize(GetStr(e, "cover"));

            // UP 主名 + mid: 卡片下方的 UP 主名要能点进他的主页。
            // ★ 三种下发写法都要认(实测 2026-10-15):
            //   · 热门/排行/收藏: owner{ name, mid } 嵌一层;
            //   · 分区流(region/feed/rcmd): author{ name, mid } 嵌一层;
            //   · 空间投稿(arc/search 的 vlist): author + mid **平铺**, 没有 owner 这一层 ——
            //     只读 owner 的话这里的 Author 会一直是空串, 卡片上就只剩一个孤零零的"·"。
            // ★ mid 一律走 GetLongLoose: 动态接口实测把 mid 当字符串下发("1255510247"),
            //   这里用 GetLong 会静默读成 0(见 GetLongLoose 的说明)。
            var owner = e.TryGetProperty("owner", out var o) ? o
                      : e.TryGetProperty("author", out var a) ? a
                      : default;
            if (owner.ValueKind == JsonValueKind.Object)
            {
                item.Author = GetStr(owner, "name");
                item.OwnerMid = GetLongLoose(owner, "mid");
            }
            if (string.IsNullOrEmpty(item.Author)) item.Author = GetStr(e, "author");
            if (item.OwnerMid <= 0) item.OwnerMid = GetLongLoose(e, "mid");
            // 风控降级接口(recArchivesByKeywords)用的是 upMid, 两者都认
            if (item.OwnerMid <= 0) item.OwnerMid = GetLongLoose(e, "upMid");

            // 时长: `duration` 的单位是**秒**; 空间投稿给的是 "03:57" 这种成品文本(`length`)。
            // ★ 读不到时**留空串**而不是 FormatSeconds(0) 的 "--:--": 空串由界面隐藏角标,
            //   "--:--" 是一个看起来像数据的假值(空间页每张卡都挂这个)。
            item.Duration = GetInt(e, "duration") > 0
                ? VideoItem.FormatSeconds(GetInt(e, "duration"))
                : GetStr(e, "length");

            if (e.TryGetProperty("stat", out var st))
            {
                if (st.TryGetProperty("view", out var v) && v.ValueKind == JsonValueKind.Number)
                    item.ViewCount = v.GetInt64();
                // 弹幕数/点赞数: 封面左下角的角标与文字区的"N点赞"都靠它们
                if (st.TryGetProperty("danmaku", out var dm) && dm.ValueKind == JsonValueKind.Number)
                    item.DanmakuCount = dm.GetInt64();
                if (st.TryGetProperty("like", out var lk) && lk.ValueKind == JsonValueKind.Number)
                    item.LikeCount = lk.GetInt64();
            }
            // 空间投稿把计数平铺在条目上(play / video_review), 不套 stat 那一层
            if (item.ViewCount <= 0) item.ViewCount = GetLong(e, "play");
            if (item.DanmakuCount <= 0) item.DanmakuCount = GetLong(e, "video_review");

            // 发布时间: 热门/收藏用 pubdate, 空间投稿用 created
            item.Pubdate = GetLong(e, "pubdate");
            if (item.Pubdate <= 0) item.Pubdate = GetLong(e, "created");
            return item;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 视频详情。
    ///
    /// ★ 2026-10-01 由裸 <c>Task&lt;VideoDetail?&gt;</c> 改为 (ok, err, data):
    ///   原来只有"拿到"和"没拿到"两种结果, 调用方分不清"接口报错"与"这个 BV 不存在",
    ///   失败时只能默默给一个通用提示。现在把原因带出来。
    /// </summary>
    public async Task<(bool ok, string? err, VideoDetail? data)> GetVideoAsync(string bvid)
    {
        try
        {
            // 和其它业务接口一样先拿到设备指纹: 实测这个接口在**完全没有 cookie** 时会被
            // 风控拦下(返回非 0 码), 表现是"详情拿不到 → 播放器空转"。
            // 已登录时 cookie 里本来就有 buvid3, 这一步也是无副作用的空操作。
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/view",
                new Dictionary<string, string> { ["bvid"] = bvid });
            if (code != 0 || data == null)
                return (false, msg ?? $"获取视频信息失败 (code {code})", null);

            var d = data.Value;
            var detail = new VideoDetail
            {
                Bvid = bvid,
                Title = GetStr(d, "title"),
                Pic = UrlUtil.Normalize(GetStr(d, "pic")),
                Desc = GetStr(d, "desc"),
                DurationSec = GetInt(d, "duration")
            };
            if (d.TryGetProperty("owner", out var o))
            {
                detail.Owner = GetStr(o, "name");
                detail.OwnerMid = GetLong(o, "mid");
                detail.OwnerFace = UrlUtil.Normalize(GetStr(o, "face"));
            }
            if (d.TryGetProperty("stat", out var st))
            {
                detail.ViewCount = GetLong(st, "view");
                detail.LikeCount = GetLong(st, "like");
                detail.DanmakuCount = GetLong(st, "danmaku");
                // 操作栏要显示计数(2026-09-26 改成"图标 + 数量"那套)
                detail.CoinCount = GetLong(st, "coin");
                detail.FavoriteCount = GetLong(st, "favorite");
                detail.ShareCount = GetLong(st, "share");
            }
            // ★★ 这里以前写的是 `GetInt(d, "coin") == 1`, 而 view 接口**没有**这个字段
            //   (data.stat.coin 是全站投币总数, 不是本人的状态) ⇒ 恒为 false,
            //   于是"投过币、重开视频后投币键还是灰的"(2026-10-13)。
            //   真实状态改由 <see cref="GetCoinCountAsync"/> 单独查询, 调用方在拿到 aid 后拉一次。
            detail.Aid = GetLong(d, "aid");
            detail.Pubdate = GetLong(d, "pubdate");

            // 多分P: 全部解析, 不只取第一页。
            //
            // ★ 以前这里只取 pages[0].cid 用于播放, 于是"克拉克森的农场 1-5季"
            //   (BV1JMbw62E72, 单稿件 40 个 P) 只能看第 1 P, 界面上又没有合集入口可点,
            //   用户那边表现为"这个视频无法选集"(2026-10-11)。分P 列表跟合集一样
            //   是 view 接口**顺带**给的, 解析它零额外请求 —— 没有理由不收。
            //
            // ★ duration 的单位是**秒**(实测 P1 = 5395 ≈ 90 分钟正片)。这跟番剧分集的
            //   duration(毫秒)正好相反, 见 ParseBangumiDetail 的对照说明。
            if (d.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array
                && pages.GetArrayLength() > 0)
            {
                foreach (var p in pages.EnumerateArray())
                {
                    var cid = GetLong(p, "cid");
                    if (cid == 0) continue;   // 脏数据跳过, 别造出一个换不过去的空 P
                    detail.Pages.Add(new VideoPage
                    {
                        Cid = cid,
                        // page 字段可能缺失(极少), 这时用数组下标 +1 兜底 —— 序号从 1 起是硬约定
                        Page = GetInt(p, "page") > 0 ? GetInt(p, "page") : detail.Pages.Count + 1,
                        Part = p.TryGetProperty("part", out var pt) && pt.ValueKind == JsonValueKind.String
                            ? pt.GetString() ?? "" : "",
                        DurationSec = GetInt(p, "duration"),
                    });
                }
                detail.Cid = detail.Pages[0].Cid;
            }
            if (detail.Cid == 0) detail.Cid = GetLong(d, "cid");

            // 合集: view 接口顺带就给了 ugc_season, 解析它**零额外请求** ——
            // 播放器要在"简介"旁边常驻一个合集入口, 不能为它再挂一次 network round-trip。
            // ★ 不在合集里的视频**根本没有 ugc_season 这个键**(不是 null), 所以判存在性。
            if (d.TryGetProperty("ugc_season", out var ugc) &&
                ugc.ValueKind == JsonValueKind.Object)
            {
                detail.Season = ParseSeason(ugc);
            }

            return (true, null, detail);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取视频信息失败: " + ex.Message, null);
        }
    }

}
