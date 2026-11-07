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
    /// <summary>
    /// 搜索(带风控 voucher 降级)。
    ///
    /// ★ <paramref name="kind"/> 决定 `search_type` 与解析方式:
    ///   · <see cref="ContentKind.Video"/>    → `search_type=video`, 走 <see cref="ParseSearchItem"/>
    ///   · <see cref="ContentKind.Live"/>     → `search_type=live_room`, 走 <see cref="ParseLiveSearchItem"/>
    ///   · <see cref="ContentKind.Bangumi"/>  → `media_bangumi` + `media_ft`, 走 <see cref="ParseBangumiItem"/>
    ///   · <see cref="ContentKind.User"/>     → `search_type=bili_user`, 走 <see cref="ParseUserSearchItem"/>
    ///   四类的**分页字段都是同一套**(numResults / pagesize), 但直播每页给 40 条、
    ///   其余三类给 20 条, 所以"还有没有下一页"的页大小必须跟着 kind 走(见下面 pageSize)。
    /// </summary>
    public async Task<SearchData> SearchAsync(string keyword, int page, ContentKind kind = ContentKind.Video)
    {
        var result = new SearchData();
        // ★★★ 番剧**不能**走 wbi/search/type?search_type=bangumi(2026-11 用户实测报"无法搜索番剧"):
        //   那个组合已被风控**降级**, WBI 签名照样返回 `code=-1200「被降级过滤的请求」`
        //   —— 探针实测裸请求与签名请求都是同一个码, 也就是说"补签名"救不了。
        //   改用 `x/web-interface/search/type?search_type=media_bangumi`(2026-11-11):
        //   不需要 WBI 签名。★ 但**必须带 buvid cookie** —— 2026-11-11 深夜实测:
        //   裸请求(无任何 cookie)这个端点返回的是 **HTML 风控页**(HTTP 200 但 body 是
        //   `<!DOCTYPE HTML ...>`), GetJsonAsync 解析 JSON 失败 ⇒ 报"响应解析失败",
        //   界面就是"搜不出番剧"。带 buvid3 cookie 后立即返回 JSON。上一轮注释里写的
        //   "不需要 buvid"是错的 —— 当时探针跑了HttpClient 复用上下文, 早已带上了指纹,
        //   探不出来。★ Referer 也要用 search.bilibili.com(www 域实测偶发 412)。
        //
        //   ★★ 纪录片/影视(《人生一串》等)在 `media_ft` 里, `media_bangumi` 搜不到
        //     (实测 num=0 vs 5)。番剧搜索一次打两个端点、按 season_id 去重合并,
        //     否则用户点开"纪录片"类内容时搜不到(2026-11-11 用户报"纪录片没有分P"
        //     的根因之一: 搜到的入口根本不含纪录片)。
        var isBangumi = kind == ContentKind.Bangumi;
        var searchType = kind switch
        {
            ContentKind.Live => "live_room",
            ContentKind.Bangumi => "media_bangumi",
            ContentKind.User => "bili_user",   // issue #47: 用户 / UP 主
            _ => "video"
        };
        // 视频/番剧/用户每页 20 条、直播每页 40 条(实测; 用户搜索 2026-11-06 实测 pagesize=20)。
        // 这个数只用来推"下一页还有没有", 不参与请求参数。
        var pageSize = kind == ContentKind.Live ? 40 : 20;
        try
        {
            // ★ 番剧也要 buvid: 不带 cookie 是 HTML 风控页(见上), 别省这次请求。
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["search_type"] = searchType,
                ["keyword"] = keyword,
                ["page"] = page.ToString()
            };

            var (code, msg, data) = await GetJsonAsync(
                isBangumi
                    ? "https://api.bilibili.com/x/web-interface/search/type"
                    : "https://api.bilibili.com/x/web-interface/wbi/search/type",
                ps, sign: !isBangumi,
                referer: isBangumi ? "https://search.bilibili.com/" : null);

            if (code == 0)
            {
                // ★★★ `data` 为 null 与 `result` 为空数组是**同一件事**: 这个关键词没搜到东西。
                //   实测(2026-11) media_bangumi 对不存在关键词返回 `code=0` 但 `data: null`;
                //   原来这里写的是 `if (code == 0 && data != null)`, 于是它一路掉到最下面的兜底
                //   分支, 把接口的 `message:"OK"` 当成错误文案弹出去 —— 用户看到的是一条
                //   红色 **"OK"**, 比老老实实说"没有找到相关番剧"糟糕得多。
                //   ★ 这个坑三类搜索都中招(同一段代码), 只是视频/直播的接口多半会回空数组,
                //     才一直没被发现。
                if (data != null && data.Value.TryGetProperty("result", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    AppendSearchItems(result, list, kind);
                    var numResults = data.Value.TryGetProperty("numResults", out var nr) &&
                                     nr.ValueKind == JsonValueKind.Number ? nr.GetInt32() : 0;
                    // ★ 一页的条数是**固定的**(视频 20 / 直播 40), 所以"下一页还有没有"必须拿
                    //   **页大小**去比, 不能拿**本页条数**去比 —— 本页不满时按本页条数乘会把
                    //   偏移算小(明明到底了还说有); 本页 0 条时更是退化成"永远有下一页",
                    //   于是「加载更多」永远不消失、每点一次都去要一个越界页(2026-10-01 修)。
                    // ★★★ 纪录片/影视合并(2026-11-12 修): media_bangumi 对纪录片关键词
                    //   返回的是 `data: null`(实测"人生一串"/"航拍中国" 全 0 条, 而 media_ft
                    //   分别给 5 / 4 条) —— 原代码在下面判"没找到"就 return, media_ft 根本没机会跑。
                    //   所以这一段必须摆在**所有** code==0 的出口之前, 而不是只塞在某个分支里。
                    if (isBangumi) await MergeFtResultsAsync(result, ps);

                    // ★ 番剧/用户结果不在 Items 里, 所以"本页取到几条"必须按 kind 分流,
                    //   否则那两类永远是 0 ⇒ HasMore 恒 false ⇒ 只给第一页就再也没了。
                    // ★ media_ft 合并后条数会变多, 所以这段必须放在它**之后**算(见上面那段注释)。
                    var got = SearchResultCount(result, kind);
                    result.HasMore = got > 0 &&
                                     (numResults <= 0 ? got >= pageSize
                                                      : page * pageSize < numResults);
                    if (page == 1 && numResults > 0)
                        result.TotalPages = Math.Max(1, (numResults + pageSize - 1) / pageSize);
                    // ★ 判空同样分流: 番剧/用户结果在自己的集合里, 用 Items.Count 判会永远是 0
                    //   ⇒ 明明搜到了却报"没有找到相关视频"。
                    if (got == 0)
                        result.Error = SearchEmptyText(kind);
                    result.Ok = true;
                    return result;
                }

                // 风控: 返回 v_voucher, 走 voucher 注册流程
                // ★ 这里的 `data != null` 是**必需**的空守卫: "没搜到"的查询会一路走到本行,
                //   而 `data.Value` 对 null 会抛。
                // ★★ 2026-10-31: v_voucher 是 B 站给游客的主要拦截形态(code=0 + 挑战字段,
                //   不是 -352 错误码)。注册失败后不能直接 Blocked —— 降级链(裸端点/换指纹)
                //   常能直接绕过挑战, 所以先走一遍 SearchRiskFallbackAsync, 全失败才拦。
                if (data != null && data.Value.TryGetProperty("v_voucher", out var vv) &&
                    vv.ValueKind == JsonValueKind.String)
                {
                    var token = await RegisterVoucherAsync(vv.GetString()!);
                    System.Diagnostics.Trace.WriteLine($"[Api] 搜索命中 v_voucher, 注册{(token == null ? "失败" : "成功")}");
                    if (!string.IsNullOrEmpty(token))
                    {
                        var ps2 = new Dictionary<string, string>(ps) { ["voucher"] = token };
                        var (c2, _, d2) = await GetJsonAsync(
                            "https://api.bilibili.com/x/web-interface/wbi/search/type", ps2, sign: true);
                        if (c2 == 0 && d2 != null && d2.Value.TryGetProperty("result", out var list2))
                        {
                            AppendSearchItems(result, list2, kind);
                            result.Ok = true;
                            result.HasMore = list2.GetArrayLength() >= pageSize;
                            if (SearchResultCount(result, kind) == 0)
                                result.Error = SearchEmptyText(kind);
                            return result;
                        }
                    }
                    System.Diagnostics.Trace.WriteLine("[Api] 搜索 voucher 流程未通过, 走降级链");
                    var psBare = new Dictionary<string, string>(ps);
                    psBare.Remove("wts");
                    psBare.Remove("w_rid");
                    psBare.Remove("voucher");
                    var (vfbCode, _, vfbData) = await SearchRiskFallbackAsync(ps, psBare, sign: !isBangumi);
                    if (vfbCode == 0 && vfbData != null &&
                        vfbData.Value.TryGetProperty("result", out var vfbList) &&
                        vfbList.ValueKind == JsonValueKind.Array)
                    {
                        AppendSearchItems(result, vfbList, kind);
                        var vgot = SearchResultCount(result, kind);
                        result.Ok = vgot > 0;
                        // 与主路径同规则: 页大小是固定的, 拿本页条数与它比才知道还有没有下一页
                        result.HasMore = vgot > 0 && vgot >= pageSize;
                        if (vgot == 0)
                            result.Error = SearchEmptyText(kind);
                        return result;
                    }
                    result.Blocked = true;
                    result.Error = "搜索被风控拦截";
                    return result;
                }

                // ★★★ 纪录片/影视合并(2026-11-12 修): 走到这里说明 media_bangumi 什么都没给
                //   (data 为 null 或没有 result 键), 而纪录片恰好就是这个形状 —— 它们全在
                //   media_ft 里(实测见 MergeFtResultsAsync 的注释)。所以兜底判"没找到"**之前**
                //   必须再问一次 media_ft, 否则纪录片永远是 0 条。
                if (isBangumi) await MergeFtResultsAsync(result, ps);

                // code == 0 但没有 result(或 data 为 null): 就是"没搜到", 不是失败
                result.Ok = true;
                // ★ 条数要按 media_ft 合并**之后**算 —— 上面那次合并可能刚补进来一堆纪录片,
                //   这时再说"没找到"就是错的。
                var gotFallback = SearchResultCount(result, kind);
                result.HasMore = gotFallback > 0 && gotFallback >= pageSize;
                result.Error = gotFallback == 0 ? SearchEmptyText(kind) : "";
                return result;
            }

            // ★ HTML 风控页(2026-10-31): 番剧注释里实测过"无 buvid 时该端点回 HTTP 200 +
            //   <!DOCTYPE HTML 风控页", GetJsonAsync 解析 JSON 失败回 -2「响应解析失败」。
            //   它本质就是被风控了, 和 -352 一样走降级链, 别把解析错误文案甩给用户。
            if (code == -2 && IsHtmlRiskPage())
                code = -352;

            if (code is -352 or -412 or -403 or -404)
            {
                // ★★★ 游客态风控降级链(2026-10-31 修"未登录搜索被风控"):
                //   探针(bd-probe-search)实测游客态第一次搜索就 Blocked、第二次又自己好了
                //   —— 同一条路径、同一对 buvid, 唯一稳定的解法是**换一条路再试**。
                //   依次降级, 哪一步通了就走哪一步:
                //   ① wbi 端点 → 裸端点(不签名; 探针实测游客可用, 番剧搜索本来就在用这条);
                //   ② 仍被拦 → 换一对全新 buvid(spi 重领+落盘)后回 wbi 端点重试一次。
                //      登录态有 SESSDATA 背书, 被标记的指纹常常也能过; 游客没有这层保护,
                //      一对被服务端盯上的 buvid 会**稳定**吃 -352, 只有换指纹才救得回来。
                //      只轮换一次: 每次失败都换身份反而更像脚本, 会加剧风控。
                //   全部走完仍被拦才真正置 Blocked, 保留"试试网页版搜索"的提示。
                // ★ ps 会被 SignParams 原地写入 wts/w_rid, 裸端点不能带这两个参数
                //   (签名对不上直接影响放行), 复制一份剥掉。
                var psBare = new Dictionary<string, string>(ps);
                psBare.Remove("wts");
                psBare.Remove("w_rid");
                var (fbCode, fbMsg, fbData) = await SearchRiskFallbackAsync(ps, psBare, sign: !isBangumi);
                if (fbCode == 0 && fbData != null &&
                    fbData.Value.TryGetProperty("result", out var fbList) &&
                    fbList.ValueKind == JsonValueKind.Array)
                {
                    AppendSearchItems(result, fbList, kind);
                    var got = SearchResultCount(result, kind);
                    result.Ok = got > 0;
                    // 与主路径同规则: 页大小固定, 拿本页条数与它比(裸端点也回 numResults)
                    var fbNum = fbData.Value.TryGetProperty("numResults", out var fbNr) &&
                                fbNr.ValueKind == JsonValueKind.Number ? fbNr.GetInt32() : 0;
                    result.HasMore = got > 0 &&
                                     (fbNum <= 0 ? got >= pageSize : page * pageSize < fbNum);
                    if (got == 0)
                        result.Error = SearchEmptyText(kind);
                    return result;
                }
                result.Blocked = true;
                result.Error = $"搜索被风控拦截 (code {code})";
            }
            else
            {
                result.Error = msg ?? $"请求失败 (code {code})";
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    /// <summary>
    /// 判断刚才那次 -2「响应解析失败」是不是 HTML 风控页。
    ///
    /// GetJsonAsync 失败时把原始 body 留在 _lastNonJsonBody, 这里只看它长什么样, 不重发请求。
    /// 实测(番剧搜索): 无 buvid 时该端点回 HTTP 200 + &lt;!DOCTYPE HTML 风控页, 本质就是被风控,
    /// 调用方应与 -352 同样处理(走降级链)。
    /// </summary>
    private bool IsHtmlRiskPage()
    {
        var head = _lastNonJsonBody?.TrimStart();
        return head != null &&
               (head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("<html", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 搜索被风控(-352/-412/-403/-404)后的降级链, 见 <see cref="SearchAsync"/> 里那段说明。
    ///
    ///   ① 换裸端点(不带 WBI 签名)。签名并不是风控放行证 —— 探针实测裸请求游客照样 code=0,
    ///     而 wbi 端点对游客的 -352 往往是"这对指纹+这条路径"被盯上了, 换路径常能直接过。
    ///   ② 还不行, 换一对全新 buvid(spi 重领并覆盖落盘)再回 wbi 端点试最后一次。
    ///
    /// 返回值与 <see cref="GetJsonAsync"/> 同形: 全链失败时返回**最后一次**的 code/msg。
    /// 注意只在此处轮换指纹; 常规路径必须保持"一对 buvid 用到底"(见 EnsureBuvidAsync 注释)。
    /// </summary>
    private async Task<(int code, string? msg, JsonElement? data)> SearchRiskFallbackAsync(
        Dictionary<string, string> ps, Dictionary<string, string> psBare, bool sign)
    {
        // ① 裸端点(番剧搜索已在用的那条路, Referer 同样用 search 域)
        var (code, msg, data) = await GetJsonAsync(
            "https://api.bilibili.com/x/web-interface/search/type", psBare,
            referer: "https://search.bilibili.com/");
        System.Diagnostics.Trace.WriteLine($"[Api] 搜索降级①裸端点 code={code} msg={msg}");
        if (code == 0) return (code, msg, data);
        if (code is not (-352 or -412 or -403 or -404)) return (code, msg, data);

        // ② 换指纹重试 wbi 端点。spi 每次都发全新的一对(见 EnsureBuvidAsync 注释),
        //    这里要的就是"全新身份", 所以直接覆盖内存与磁盘(ForceSet)。
        await RotateBuvidAsync();
        var (code3, msg3, data3) = await GetJsonAsync(
            "https://api.bilibili.com/x/web-interface/wbi/search/type", ps, sign: sign);
        System.Diagnostics.Trace.WriteLine($"[Api] 搜索降级②换buvid+wbi code={code3} msg={msg3}");
        return (code3, msg3, data3);
    }

    /// <summary>
    /// 风控专用: 从 spi 领一对**全新** buvid 并强制覆盖内存/磁盘。
    ///
    /// ★ 与 <see cref="EnsureBuvidAsync"/> 的区别是"覆盖"而不是"补缺" —— 那边的原则是
    ///   设备身份稳定, 这边的前提恰恰是"现有身份已被服务端盯上, 必须换人"。
    /// ★ 失败静默: 拿不到新指纹就带着旧指纹做最后一次重试, 行为退回修复前, 不多不少。
    /// </summary>
    private async Task RotateBuvidAsync()
    {
        try
        {
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/frontend/finger/spi", null);
            if (code != 0 || data == null) return;
            var b3 = data.Value.TryGetProperty("b_3", out var b3v) && b3v.ValueKind == JsonValueKind.String
                ? b3v.GetString() : null;
            var b4 = data.Value.TryGetProperty("b_4", out var b4v) && b4v.ValueKind == JsonValueKind.String
                ? b4v.GetString() : null;
            if (!string.IsNullOrEmpty(b3))
            {
                _buvid3 = b3;
                SessionManager.Instance.ForceSetBuvid3(b3);
            }
            if (!string.IsNullOrEmpty(b4))
            {
                _buvid4 = b4;
                SessionManager.Instance.ForceSetBuvid4(b4);
            }
        }
        catch
        {
            // 忽略: 拿不到新指纹就用旧的试最后一把
        }
    }

    /// <summary>
    /// 把 **media_ft(影视)** 的搜索结果合并进番剧结果集。
    ///
    /// ★★★ 为什么需要它(2026-11-12 用户报"纪录片搜不出来"):
    ///   纪录片在 B 站**不在** media_bangumi 里。实测裸端点:
    ///     · "人生一串"   media_bangumi=0 条, media_ft=5 条
    ///     · "航拍中国"   media_bangumi=0 条, media_ft=4 条
    ///     · "地球脉动"   media_bangumi=0 条, media_ft=6 条
    ///     · "蓝色星球"   media_bangumi=0 条, media_ft=8 条
    ///   更关键的是 media_bangumi 在关键词无结果时返回的是 **`data: null`**(连 result 键都没有),
    ///   于是主流程直接判"没找到"返回 —— 早年的实现在主结果分支**内部**补这一次请求,
    ///   那个分支压根进不去, media_ft 永远没机会跑。
    ///   ⇒ 它现在是一个独立方法, 由 <see cref="SearchAsync"/> 在**每一个** code==0 的出口前调用。
    ///
    /// ★ 两个端点的条目字段完全同构(season_id/cover/season_type/badge/index_show/ep_size 全有),
    ///   所以直接复用 ParseBangumiItem; 按 season_id 去重(番剧电影可能两边都出现)。
    /// ★ 失败必须静默: 它只是补充, 主端点已经给了结果时不该被它拖垮。
    /// </summary>
    private async Task MergeFtResultsAsync(SearchData result, Dictionary<string, string> ps)
    {
        try
        {
            var psFt = new Dictionary<string, string>(ps)
            {
                ["search_type"] = "media_ft"
            };
            var (cFt, _, dFt) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/search/type",
                psFt, referer: "https://search.bilibili.com/");
            if (cFt != 0 || dFt == null) return;
            if (!dFt.Value.TryGetProperty("result", out var listFt) ||
                listFt.ValueKind != JsonValueKind.Array) return;

            foreach (var e in listFt.EnumerateArray())
            {
                var b = ParseBangumiItem(e);
                if (b == null) continue;
                if (result.BangumiItems.Any(x => x.SeasonId == b.SeasonId)) continue;
                result.BangumiItems.Add(b);
            }
        }
        catch { /* media_ft 只是补充, 失败静默 */ }
    }

    /// <summary>
    /// 当前类别已经解析出多少条。
    ///
    /// ★★★ 收成一个方法而不是在主流程里反复写 `kind == Bangumi ? ... : ...`:
    ///   搜索结果有四类(视频/直播/番剧/用户), 这套判断在 SearchAsync 里重复了 5 次
    ///   (主路径 / voucher 重试 / voucher 降级 / data 为 null / 风控降级)。加一个新类别时
    ///   只要漏掉一处, 就会出现"明明搜到了结果却报没有找到"或"永远显示有下一页"这类隐蔽 bug。
    /// </summary>
    private static int SearchResultCount(SearchData r, ContentKind kind) => kind switch
    {
        ContentKind.Bangumi => r.BangumiItems.Count,
        ContentKind.User => r.UserItems.Count,
        _ => r.Items.Count
    };

    /// <summary>当前类别"没搜到"时的提示文案(同样收口, 避免 5 处 switch 各漏一个分支)</summary>
    private static string SearchEmptyText(ContentKind kind) => kind switch
    {
        ContentKind.Live => "没有找到相关直播间",
        ContentKind.Bangumi => "没有找到相关番剧",
        ContentKind.User => "没有找到相关 UP 主",
        _ => "没有找到相关视频"
    };

    /// <summary>把一段 result 数组按 kind 解析进结果集(视频/直播/番剧/用户四套字段)</summary>
    private static void AppendSearchItems(SearchData result, JsonElement list, ContentKind kind)
    {
        foreach (var e in list.EnumerateArray())
        {
            if (kind == ContentKind.Bangumi)
            {
                // ★ 番剧走**另一个集合**: 它的条目是 BangumiItem(season_id + 竖版海报),
                //   点开进选集页而不是播放 —— 混进 Items 会让点击走错路。详见 ContentKind.Bangumi。
                var b = ParseBangumiItem(e);
                if (b != null) result.BangumiItems.Add(b);
                continue;
            }
            if (kind == ContentKind.User)
            {
                // 用户同样单独一个集合: 条目没有 bvid, 点开是 UP 主主页而不是播放
                var u = ParseUserSearchItem(e);
                if (u != null) result.UserItems.Add(u);
                continue;
            }
            var item = kind == ContentKind.Live ? ParseLiveSearchItem(e) : ParseSearchItem(e);
            if (item != null) result.Items.Add(item);
        }
    }

    /// <summary>
    /// 搜索结果里的一条**用户 / UP 主**(search_type=bili_user, 2026-11-06 新增, issue #47)。
    ///
    /// ★ 字段按实测响应取(关键词「极客湾」第一条就是官方账号):
    ///     mid=25876945 / uname=极客湾Geekerwan / fans=5670832 / videos=643 /
    ///     upic=`//i1.hdslb.com/...` / official_verify.type=0
    /// ★ upic 带 `//` 前缀, 必须过 UrlUtil.Normalize(与直播封面同坑)。
    /// ★ uname/usign 实测(极客湾/老番茄)是纯文本, 没有视频标题那种 `<em class="keyword">`
    ///   高亮标签; 仍然照旧过一遍 StripHtml —— 服务端换一种渲染时直接把标签甩到界面上很难看,
    ///   而这个方法只去掉那对固定标签, 对正常名字无副作用。
    /// </summary>
    private static SearchUserItem? ParseUserSearchItem(JsonElement e)
    {
        var mid = GetLong(e, "mid");
        if (mid <= 0) return null;   // 没有 mid 就点不开主页, 直接丢掉

        // 签名里可能带换行(实测有用户写了多行简介)。卡片是一行截断的列表,
        // 原样塞进去会让那一行突然变高、整列参差不齐 —— 统一压成单行。
        var sign = GetStr(e, "usign").StripHtml().Replace("\r", " ").Replace("\n", " ").Trim();

        var u = new SearchUserItem
        {
            Mid = mid,
            Name = GetStr(e, "uname").StripHtml(),
            Face = UrlUtil.Normalize(GetStr(e, "upic")),
            Sign = sign,
            Fans = GetLong(e, "fans"),
            Videos = GetLong(e, "videos"),
            Level = GetInt(e, "level")
        };

        if (e.TryGetProperty("official_verify", out var ov) && ov.ValueKind == JsonValueKind.Object)
        {
            u.VerifyType = GetInt(ov, "type");
            u.VerifyDesc = GetStr(ov, "desc");
            // ★ 实测(2026-11-06): 未认证用户的 type 是 **127**, 不是 -1。
            //   只认 0(个人)/1(机构), 其余一律归一成"未认证", 否则每个普通用户都会挂一个认证勾。
            if (u.VerifyType is not (0 or 1)) u.VerifyType = -1;
        }

        return u.Name.Length > 0 ? u : null;
    }

    /// <summary>
    /// 搜索结果里的一条**直播间**。
    ///
    /// 与视频那条(ParseSearchItem)的差别是整片字段都不同, 实测(search_type=live_room):
    ///   roomid(直播间号) / uid(主播 mid) / uname(主播名) / title / cover / online(人气) / live_status。
    /// ★ 三个易错点:
    ///   ① 房间号字段是 **roomid**(没有下划线), 不是 room_id —— 后者是另一个接口(live 列表)的叫法;
    ///   ② 主播 mid 是 **uid**, 不是 mid(视频那条才是 mid) —— 抄错会让卡片下面的 UP 主名点不开;
    ///   ③ cover 可能带 `//` 前缀(实测就是), 必须过 UrlUtil.Normalize 补 https。
    /// </summary>
    private static VideoItem? ParseLiveSearchItem(JsonElement e)
    {
        var roomId = GetLong(e, "roomid");
        if (roomId <= 0) roomId = GetLong(e, "room_id");
        if (roomId <= 0) return null;   // 没有房间号就不是直播间, 直接丢掉(卡片也播不了)

        return new VideoItem
        {
            RoomId = roomId,
            Title = GetStr(e, "title").StripHtml(),
            Cover = UrlUtil.Normalize(GetStr(e, "cover")),
            Author = GetStr(e, "uname"),
            OwnerMid = GetLong(e, "uid"),
            // 直播间没有时长, 复用卡片那个时长位显示"直播中"(与首页直播 tab 同一约定)
            Duration = "直播中",
            ViewCount = Math.Max(0, GetLong(e, "online")),
        };
    }

    /// <summary>
    /// 搜索结果里的一条视频。
    ///
    /// ★★ 搜索接口**没有 owner 对象**: UP 主的 mid 是**顶层字段 mid**(昵称是顶层 author),
    ///   稿件 id 是顶层 aid。别的接口(热门/排行/推荐/历史)走的是 owner.mid, 所以这里要是照抄
    ///   那一套就会漏掉 OwnerMid —— 后果是卡片下面的 UP 主名点不开, 只弹"没拿到 UP 主的 UID"
    ///   (2026-10-01 用户报的"搜索页 up 主点不开")。改字段名之前先看一眼真实响应。
    /// </summary>
    private static VideoItem ParseSearchItem(JsonElement e)
    {
        var item = new VideoItem
        {
            Bvid = GetStr(e, "bvid"),
            Aid = GetLong(e, "aid"),
            Title = GetStr(e, "title").StripHtml(),
            Cover = UrlUtil.Normalize(GetStr(e, "pic")),
            Author = GetStr(e, "author"),
            OwnerMid = GetLong(e, "mid"),
            Duration = GetStr(e, "duration"),
            ViewCount = GetLong(e, "play"),
            DanmakuCount = GetLong(e, "video_review"),
            Pubdate = GetLong(e, "pubdate")
        };
        // 兜底: 少数情况下 mid 缺失/为 0, 别再让"点 UP 主名"落空
        if (item.OwnerMid <= 0) item.OwnerMid = GetLong(e, "uid");
        return item;
    }

    /// <summary>
    /// 热搜榜(首页右上角搜索卡展开后的「热搜」区块)。
    ///
    /// 接口: GET /x/web-interface/search/square —— 就是 B 站搜索框点开时用的那一个,
    /// 数据在 data.trending.list[]。三个字段各有分工:
    ///   · `keyword`   —— 真正的搜索词(可能很短, 例如 "KC XLG");
    ///   · `show_name` —— 给人看的完整说法("KC战胜XLG VCT冠军赛"), 网页端显示的就是它;
    ///   · `icon`      —— 徽章**图片地址**(不是文字), 见 BadgeOfIcon。
    /// 实测匿名(不带 Cookie)也能拿到 10 条, 无需 WBI 签名。
    /// </summary>
    public async Task<(bool ok, string? err, List<HotSearchItem>? items)> GetHotSearchAsync(int limit = 10)
    {
        try
        {
            var ps = new Dictionary<string, string>
            {
                ["limit"] = limit.ToString(),
                ["platform"] = "web"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/search/square", ps);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<HotSearchItem>();
            if (data.Value.TryGetProperty("trending", out var trending) &&
                trending.ValueKind == JsonValueKind.Object &&
                trending.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var kw = GetStr(e, "keyword");
                    if (string.IsNullOrWhiteSpace(kw)) continue;
                    var show = GetStr(e, "show_name");
                    list.Add(new HotSearchItem
                    {
                        Rank = list.Count + 1,
                        Keyword = kw,
                        ShowName = string.IsNullOrWhiteSpace(show) ? kw : show,
                        Badge = BadgeOfIcon(GetStr(e, "icon"))
                    });
                }
            }
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 把热搜项 `icon` 的图片地址翻成徽章文字。
    ///
    /// 为什么查表而不是直接显示那张图: 这几枚徽章是 activity-plat 下的**固定静态图**
    /// (新 = UF7B1wVKT2.png / 热 = lrx9rnKo24.png / 独家 = OBQYPHMTcv.png), 按文件名就能判定。
    /// 画成文字胶囊的好处是随主题走、任意 DPI 都不糊、不占位图缓存, 也省掉 10 次图片请求;
    /// 代价是 B 站换了新徽章我们不显示 —— 徽章纯装饰, 认不出来留空比认错强。
    /// (要补映射: 直接打开上面那个接口, 看 icon 的 URL 文件名。)
    /// </summary>
    private static string BadgeOfIcon(string iconUrl)
    {
        if (string.IsNullOrEmpty(iconUrl)) return "";
        var slash = iconUrl.LastIndexOf('/');
        var name = slash >= 0 ? iconUrl[(slash + 1)..] : iconUrl;
        return name switch
        {
            "UF7B1wVKT2.png" => "新",
            "lrx9rnKo24.png" => "热",
            "OBQYPHMTcv.png" => "独家",
            _ => ""
        };
    }

}
