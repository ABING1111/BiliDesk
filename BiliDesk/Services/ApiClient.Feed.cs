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
    /// 首页个性化推荐。按设置里的「推荐来源」走两套**不同的服务端模型**:
    ///   · Web —— 浏览器网页版「为你推荐」(wbi/index/top/feed/rcmd), 单次最多 30 条;
    ///   · App —— B 站手机客户端首页推荐流(app.bilibili.com/x/v2/feed/index), 每次 10 条。
    ///
    /// ★ 历史提醒(别丢掉): 2026-10-03 曾把 App 选项整体删除, 因为实测它对第三方客户端
    ///   可能返回全站通用热门池(100 条里命中关注的 UP 只有 1~2 个、平均播放量 66~103 万;
    ///   网页 rcmd 平均只有 17 万、内容明显更垂直)。现在按用户要求恢复, 默认仍是 Web,
    ///   设置页也如实写明 "App 端可能更通用" —— 不宣称 App 更准。
    ///
    /// 失败时自动重试一次; 仍不成功则降级返回热门, 保证首页不会是空列表。
    ///
    /// ★★★ 2026-11-08 补上网页版翻页游标(参数以前全是写死的 `fresh_idx=1 / brush=1`)。
    ///
    ///   ★★ 先说**实测事实**, 免得后来人按错误的前提改代码(探针 bd-probe-rcmd 有完整数据):
    ///     服务端**本身就在轮换推荐池** —— 用写死第 1 页的旧参数连打 20 次(560 条),
    ///     结果全部唯一、相邻页零重复。所以"加载更多等于没加载"这个现象**不是**参数写死造成的,
    ///     当初据此做的推断是错的。
    ///
    ///   那游标的意义在哪(仍然值得加):
    ///     · fresh_idx / fetch_row 让服务端知道"读到第几页了", 推荐会沿阅读轨迹往深处推,
    ///       而不是每次都给一轮"新鲜的随机池"(实测新游标组平均播放量更低、更垂直);
    ///     · last_showlist 是**唯一**能让服务端主动避开"刚给你看过的"的手段 ——
    ///       它依赖客户端如实上报上一屏, 这是本次新增的能力。
    ///
    ///   现在由 <see cref="RecommendCursor"/> 维护状态机: reset=true 换一批(brush+1),
    ///   reset=false 带着游标往下翻。**失败不推进游标**, 重试复用同一组参数。
    ///
    /// ★ 返回值 <c>fallback</c>: true 表示这次是**降级到热门**的结果。
    ///   以前降级是静默的 —— 界面挂着"推荐"标题却给热门内容, 用户根本看不出发生了什么。
    /// </summary>
    /// <param name="reset">true = 刷新(换一批); false = 带着游标继续翻下一页</param>
    public async Task<(bool ok, string? err, List<VideoItem>? items, bool fallback)> GetRecommendAsync(
        bool reset = false)
    {
        try
        {
            await EnsureBuvidAsync();

            // App 端是另一套完全不同的服务端模型(索引游标 + 每次 10 条), 单独走一条路。
            if (Svc.Settings.RecommendSource == RecommendSource.App)
                return await GetAppRecommendAsync(reset);

            // ★ 游标是**单例状态**: ApiClient 是全局单例, 翻页序列必须跨调用连续。
            //   放在这里而不是 ViewModel, 是因为"请求成功才推进"这件事只有请求方知道。
            var cursor = _recommendCursor;
            if (reset) cursor.Reset();

            var (ok, err, items) = await GetWebRecommendCoreAsync(cursor);
            if (!ok || items == null || items.Count == 0)
            {
                // 重试一次。
                // ★ 这里**故意不**清 _buvid3/_buvid4: 设备指纹是推荐系统认人的凭据,
                //   清掉它们等于"失败一次就换一台新设备重来", 会让推荐模型重新冷启动
                //   —— 与"让推荐更准"正相反。真正需要刷新的是 WBI 密钥(会过期)。
                _wbiKeys = null;
                await EnsureBuvidAsync();
                (ok, err, items) = await GetWebRecommendCoreAsync(cursor);
            }
            if (ok && items != null && items.Count > 0) return (true, null, items, false);

            // 失败 -> 降级热门(fallback=true, 界面要如实说明)
            var (pok, perr, pitems) = await GetPopularAsync(1);
            return (pok, perr ?? err, pitems, true);
        }
        catch
        {
            var (pok, perr, pitems) = await GetPopularAsync(1);
            return (pok, perr, pitems, true);
        }
    }

    /// <summary>
    /// App 端推荐的失败/重试/降级包装。
    /// 与网页版一样: 失败不推进游标; 带 idx 的翻页失败时先退回"刷新"再试一次
    /// (idx 过期是 App 接口最常见的失败原因), 仍失败才降级热门。
    /// </summary>
    private async Task<(bool ok, string? err, List<VideoItem>? items, bool fallback)> GetAppRecommendAsync(
        bool reset)
    {
        var cursor = _appRecommendCursor;
        if (reset) cursor.BeginRefresh();
        else cursor.BeginLoadMore();

        string? lastErr = null;
        try
        {
            var (ok, err, items) = await GetAppRecommendCoreAsync(cursor);
            lastErr = err;
            if (!ok || items == null || items.Count == 0)
            {
                // 翻页失败时 idx 可能已经过期: 退回刷新语义重来一次。
                if (cursor.Idx > 0) cursor.BeginRefresh();
                (ok, err, items) = await GetAppRecommendCoreAsync(cursor);
                lastErr = err;
            }
            if (ok && items != null && items.Count > 0) return (true, null, items, false);
        }
        catch
        {
            // 落到下面的热门兜底
        }

        var (pok, perr, pitems) = await GetPopularAsync(1);
        return (pok, perr ?? lastErr, pitems, true);
    }

    /// <summary>
    /// 推荐流的翻页游标(单例, 跨调用连续)。
    /// 退出登录/重新登录后由 <see cref="ResetRecommendCursor"/> 归零 —— 换了账号还用旧游标
    /// 等于让新账号接着上一个账号的阅读进度往下翻。
    /// </summary>
    private readonly RecommendCursor _recommendCursor = new();

    /// <summary>
    /// App 端推荐流的翻页游标(单例, 跨调用连续)。与网页游标互不影响 ——
    /// 两套接口的游标语义完全不同, 共用一份会让切换来源后从错误的位置开始。
    /// </summary>
    private readonly AppRecommendCursor _appRecommendCursor = new();

    /// <summary>
    /// 重置推荐游标(登录态变化 / 推荐来源切换时调用; 下一次请求重新从开头开始)。
    /// ★ Web 用 Rewind 而不是 Reset: 只归位、**不动刷子** —— "换一批"由紧接着那次
    ///   reset: true 的请求自己完成, 两处都推进刷子会让它一次跳 2。
    /// ★ App 同步归零 idx: 旧来源的游标对新来源没有意义。
    /// </summary>
    public void ResetRecommendCursor()
    {
        _recommendCursor.Rewind();
        _appRecommendCursor.Rewind();
    }

    /// <summary>
    /// 推荐游标的当前状态(只读快照, 供探针/诊断使用, 不参与任何业务判断)。
    ///
    /// ★ 为什么要把这个状态暴露出来: 游标是否**真的**跟着请求推进, 光看返回内容看不出来 ——
    ///   实测服务端本身就在轮换推荐池, 即便参数写死成第 1 页, 连续请求也拿不到重复内容。
    ///   也就是说"内容有变化"**不能**当作"游标生效"的证据。这里给探针一个直接观测点。
    /// </summary>
    public (int FreshIdx, int Brush, int FetchRow, string LastShowList) RecommendCursorState
        => (_recommendCursor.FreshIdx, _recommendCursor.Brush,
            _recommendCursor.FetchRow, _recommendCursor.LastShowList);

    private async Task<(bool ok, string? err, List<VideoItem>? items)> GetWebRecommendCoreAsync(
        RecommendCursor cursor)
    {
        try
        {
            // 现行 web 首页推荐接口(top feed), 需要 WBI 签名
            var ps = cursor.BuildQuery();
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

            // ★ 成功才推进游标(失败时不动, 重试复用同一组参数 —— 见 RecommendCursor 的说明)。
            var aids = new List<long>(items.Count);
            foreach (var it in items)
            {
                if (it.Aid > 0) aids.Add(it.Aid);
            }
            ParseUserFeature(data.Value, out var sIdx, out var sIdx1h);
            cursor.OnLoaded(aids, sIdx, sIdx1h);

            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    // ---------------------------------------------------------------- App 端推荐流

    /// <summary>App 端签名用的 appkey / appsec(粉版 android, "获取资源通用" 那一对)</summary>
    private const string AppKey = "1d8b6e7d45233436";
    private const string AppSec = "560c52ccd288fed045859ed18bffd973";

    /// <summary>
    /// App 端接口签名: 加 appkey → 按 key 升序 → 拼 query → 末尾接 appsec → MD5。
    /// 与 WBI 签名是两套独立机制(WBI 是网页端每日轮换密钥), 别混用。
    /// </summary>
    private static void AppSign(Dictionary<string, string> ps)
    {
        ps["appkey"] = AppKey;
        var query = string.Join("&",
            ps.OrderBy(kv => kv.Key, StringComparer.Ordinal)
              .Select(kv => $"{kv.Key}={Enc(kv.Value)}"));
        ps["sign"] = Hashing.Md5Hex(query + AppSec);
    }

    /// <summary>
    /// B 站官方 App 首页推荐流(app.bilibili.com/x/v2/feed/index)。
    ///
    /// ★ 与网页端那套完全不是一个模型:
    ///   1. **靠 idx 游标翻页**(见 <see cref="AppRecommendCursor"/>): 把上一批最小的 idx
    ///      带回去才拿得到"下一段"; 不带游标时服务端窗口几乎不动。
    ///      `pull` 要和 idx 配套(刷新 = pull=true 且不带 idx; 翻页 = pull=false 带 idx)。
    ///   2. **只给 avid 不给 bvid**(items[].param / player_args.aid), bvid 由
    ///      UrlParser.AvToBv 本地换算, 省掉"每条一次 view 接口"的开销。
    ///   3. 播放量/弹幕数给的是**已格式化好的文本**("149.1万"), 只能反解回近似数值 ——
    ///      够用来排序和显示, 但没有网页端那么精确。
    ///
    /// 参数坑(实测): 参数表里出现过的 `appver` 会让服务端直接 -400(整套参数被拒),
    /// 而 build/mobi_app/platform/fnval/fourk 这些是安全的。appkey+sign 必须带。
    /// </summary>
    private async Task<(bool ok, string? err, List<VideoItem>? items)> GetAppRecommendCoreAsync(
        AppRecommendCursor cursor)
    {
        try
        {
            var loggedIn = SessionManager.Instance.HasLogin;
            var ps = new Dictionary<string, string>
            {
                ["build"] = "8130300",
                ["mobi_app"] = "android",
                ["platform"] = "android",
                ["device"] = "phone",
                ["c_locale"] = "zh_CN",
                ["s_locale"] = "zh_CN",
                ["fnval"] = "272",
                ["fnver"] = "1",
                ["fourk"] = "1",
                ["video_mode"] = "1",
                ["network"] = "wifi",
                ["qn"] = "32",
                ["recsys_mode"] = "0",
                ["guidance"] = "0",
                ["https_url_req"] = "0",
                ["inline_danmu"] = "2",
                ["inline_sound"] = "1",
                ["interest_id"] = "0",
                // 刷新 = pull=true(从池子最前面取); 加载更多 = pull=false(配合 idx 往下翻)
                // 0 = 已登录, 1 = 未登录(接口自己的约定)。
                ["login_event"] = loggedIn ? "0" : "1"
            };
            cursor.ApplyTo(ps);
            AppSign(ps);

            var (code, msg, data) = await GetJsonAsync(
                "https://app.bilibili.com/x/v2/feed/index", ps);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            var indices = new List<long>();
            JsonElement arr = default;
            var hasArray = data.Value.ValueKind == JsonValueKind.Array;
            if (hasArray) arr = data.Value;
            else if (data.Value.TryGetProperty("items", out var list)) arr = list;

            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    // ★ 游标要取**最小**的 idx(= 这一批读到推荐池最深处的位置), 而不是最大的。
                    //   idx 在一批里是从大到小排的; 服务端的语义是"给我 idx 小于这个值的内容",
                    //   所以带回去的必须是**最深**的那个, 下一批才真的往下走 10 条。
                    //   (实测: 记录最大 idx 时, 每批只前进 1, 等于反复重读同一小段;
                    //    记录最小 idx 时, 每批稳定前进 10。)
                    //   注意要遍历**全部**条目: 直播/横幅这些非 av 项同样占着流里的位置,
                    //   跳过它们会让游标偏浅, 下一批又从那段中间重来。
                    var idx = GetLongLoose(e, "idx");
                    if (idx > 0) indices.Add(idx);

                    var item = ParseAppFeedItem(e);
                    if (item != null) items.Add(item);
                }
            }

            // ★ 成功才推进游标(失败时不动, 重试复用同一组参数 —— 见 AppRecommendCursor 的说明)。
            cursor.OnLoaded(indices);
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>解析 App 推荐流的一条。非视频卡片(直播/横幅/广告)返回 null, 由调用方跳过</summary>
    private static VideoItem? ParseAppFeedItem(JsonElement e)
    {
        try
        {
            // card_goto/goto: av = 普通视频, live = 直播, banner = 横幅。
            // 只收 av —— 与网页端推荐的处理保持一致(直播另有专门的 tab)。
            var kind = GetStr(e, "card_goto");
            if (kind.Length == 0) kind = GetStr(e, "goto");
            if (kind != "av") return null;

            // param 是**字符串**型的 avid(不是数字); 有些版本会带 "av" 前缀。
            var param = GetStr(e, "param");
            if (param.StartsWith("av", StringComparison.OrdinalIgnoreCase)) param = param[2..];
            long.TryParse(param, out var aid);
            if (aid <= 0) aid = GetNestedLong(e, "player_args", "aid");
            if (aid <= 0) aid = GetNestedLong(e, "args", "aid");
            if (aid <= 0) aid = GetLongLoose(e, "aid");
            if (aid <= 0) return null;

            // 接口有时直接给 bvid, 有时只给 aid。只给 aid 时本地换算(见 UrlParser.AvToBv)。
            var bvid = GetStr(e, "bvid");
            if (bvid.Length == 0) bvid = BiliDesk.Utils.UrlParser.AvToBv(aid);
            if (bvid.Length == 0) return null;

            // UP 主: 名字在 desc_button.text, mid 要从 desc_button.uri(bilibili://space/{mid}) 里抠
            var upName = "";
            long ownerMid = 0;
            if (e.TryGetProperty("desc_button", out var db) && db.ValueKind == JsonValueKind.Object)
            {
                upName = GetStr(db, "text");
                var upUri = GetStr(db, "uri");
                var slash = upUri.LastIndexOf('/');
                if (slash >= 0 && slash + 1 < upUri.Length)
                    long.TryParse(upUri[(slash + 1)..], out ownerMid);
            }
            if (upName.Length == 0 && e.TryGetProperty("args", out var args) &&
                args.ValueKind == JsonValueKind.Object)
            {
                upName = GetStr(args, "up_name");
                if (ownerMid <= 0) ownerMid = GetLongLoose(args, "up_id");
            }

            var cover = GetStr(e, "cover");
            if (cover.Length == 0) cover = GetStr(e, "pic");

            // 时长只在 player_args.duration 里是秒数; cover_right_text 是 "7:51" 这种给人看的串
            var durationSec = (int)GetNestedLong(e, "player_args", "duration");
            if (durationSec <= 0) durationSec = GetIntSafe(e, "duration");

            return new VideoItem
            {
                Bvid = bvid,
                Aid = aid,
                Title = GetStr(e, "title").StripHtml(),
                Cover = UrlUtil.Normalize(cover),
                Author = upName,
                OwnerMid = ownerMid,
                Duration = VideoItem.FormatSeconds(durationSec),
                ViewCount = ParseFormattedCount(GetStr(e, "cover_left_text_1")),
                DanmakuCount = ParseFormattedCount(GetStr(e, "cover_left_text_2"))
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把接口给的**已格式化**计数文本反解回数字("149.1万" → 1491000, "2.9万" → 29000)。
    /// App 端推荐流不给数值型 stat, 只有这种给人看的串; 解析失败一律返回 0,
    /// 界面按"0 = 未知"处理(不显示那个胶囊), 不会显示成错误的 0。
    /// </summary>
    private static long ParseFormattedCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var s = text.Trim();
        try
        {
            double mul = 1;
            if (s.EndsWith("亿", StringComparison.Ordinal)) { mul = 100_000_000; s = s[..^1]; }
            else if (s.EndsWith("万", StringComparison.Ordinal)) { mul = 10_000; s = s[..^1]; }
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? (long)(v * mul)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 从 `data.userfeature` 里取服务端回显的 fresh_idx / fresh_idx_1h。
    ///
    /// ★ 这个字段是**字符串里套 JSON**(实测形如
    ///   `"{\"enter_rank\":1500,\"fresh_idx\":3,\"fresh_idx_1h\":3,...}"`), 不是对象,
    ///   直接 TryGetProperty 会拿不到。解析失败就返回 0(游标退回本地自增), 不能抛 ——
    ///   它只是让翻页更准的补充信息, 拿不到不该影响整页。
    /// </summary>
    private static void ParseUserFeature(JsonElement data, out int freshIdx, out int freshIdx1h)
    {
        freshIdx = 0;
        freshIdx1h = 0;
        try
        {
            if (!data.TryGetProperty("userfeature", out var uf)) return;

            // 两种形态都认: 字符串(现行) / 对象(以后万一改成对象)
            JsonElement obj;
            if (uf.ValueKind == JsonValueKind.String)
            {
                var raw = uf.GetString();
                if (string.IsNullOrWhiteSpace(raw)) return;
                using var doc = JsonDocument.Parse(raw!);
                obj = doc.RootElement;
                ReadCounters(obj, ref freshIdx, ref freshIdx1h);
                return;
            }
            if (uf.ValueKind == JsonValueKind.Object)
            {
                obj = uf;
                ReadCounters(obj, ref freshIdx, ref freshIdx1h);
            }
        }
        catch
        {
            // 解析不出来就用本地自增
        }
    }

    private static void ReadCounters(JsonElement obj, ref int freshIdx, ref int freshIdx1h)
    {
        freshIdx = GetInt(obj, "fresh_idx");
        freshIdx1h = GetInt(obj, "fresh_idx_1h");
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
                // ★ aid: 推荐接口把它放在 `id` 里(不是 `aid`)。翻页游标的 last_showlist
                //   要的是 `av_{aid}`, 缺了它那一项就只能留空, 服务端避重效果打折。
                Aid = GetLong(e, "id"),
            };
            if (string.IsNullOrEmpty(item.Bvid)) return null;

            // 是否已关注该 UP 主(卡片 UP 名右边的「已关注」标签)。
            // ★ 主字段 is_followed(0/1); rcmd_reason.reason_type == 1 是同义的推荐理由,
            //   两处实测不一定同时给, 任一命中即算已关注 —— 少一个就漏标签, 多判成"未关注"则无害。
            item.IsAuthorFollowed = GetInt(e, "is_followed") == 1;
            if (!item.IsAuthorFollowed && e.TryGetProperty("rcmd_reason", out var reason)
                && reason.ValueKind == JsonValueKind.Object)
            {
                item.IsAuthorFollowed = GetInt(reason, "reason_type") == 1;
            }
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
            //   (data.stat.coin 是全站投币总数, 不是本人的状态) ? 恒为 false,
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
