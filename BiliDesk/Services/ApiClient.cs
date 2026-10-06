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
/// 哔哩哔哩 API 客户端: 带浏览器 UA、Cookie、WBI 签名与风控降级逻辑
/// </summary>
public class ApiClient
{
    /// <summary>
    /// 请求头里的 UA。
    ///
    /// **不要**在结尾加上 "Edg/126.0.0.0" 这类浏览器品牌后缀 —— 实测(2026-09-24, 冷启动单请求)
    /// 同一个评论接口: 纯 Chrome UA 返回 20 条, 带 Edg 后缀直接 `-352`(风控)。
    /// 这类后缀会被 B 站的风控当成"非真实浏览器会话", 表现是接口时好时坏、返回被降级的短列表。
    /// 字符串本体收敛在 HttpDefaults 一份, 全项目所有出站请求共用同一份。
    /// </summary>
    private const string Ua = HttpDefaults.UserAgent;

    // bilibili 官方 JS 中的 mixinKey 置换表
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52
    };

    private readonly HttpClient _http;
    private (string img, string sub)? _wbiKeys;
    private string? _buvid3;
    private string? _buvid4;

    public ApiClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
    }

    // ---------------------------------------------------------------- 基础请求

    private async Task<(int code, string? msg, JsonElement? data)> GetJsonAsync(
        string url, Dictionary<string, string>? ps, bool sign = false, bool allowRetry = true,
        string? referer = null, bool fingerprint = true)
    {
        ps ??= new Dictionary<string, string>();
        if (sign)
        {
            var mixin = await GetMixinKeyAsync();
            if (mixin == null) return (-400, "WBI 密钥获取失败", null);
            SignParams(ps, mixin);
        }

        var fullUrl = url + "?" + string.Join("&",
            ps.Select(kv => $"{kv.Key}={Enc(kv.Value)}"));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            // fingerprint=false 时**故意不带**设备指纹 Cookie(见 GetCommentsAsync 的说明)
            if (fingerprint)
            {
                var cookie = AppendFingerprint(SessionManager.Instance.BuildCookieHeader());
                if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            // Referer 逐请求设置: 部分接口(如空间)校验 Referer 域名
            req.Headers.TryAddWithoutValidation("Referer", referer ?? "https://www.bilibili.com/");

            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            int code;
            string? msg;
            JsonElement? data;
            using (var doc = JsonDocument.Parse(text))
            {
                var root = doc.RootElement;
                code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
                msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                // Clone: 脱离 JsonDocument 生命周期, 否则返回的是悬空引用
                data = root.TryGetProperty("data", out var d) &&
                       d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
            }

            // WBI 签名过期(-403)时刷新密钥重试一次
            if (sign && allowRetry && code == -403)
            {
                _wbiKeys = null;
                // ★ fingerprint 必须原样透传(以前漏了, 会退回默认 true):
                //   故意不带指纹的调用方只有一个 —— 未登录评论(GetCommentsAsync, 匿名读评论
                //   不该留下设备痕迹)。它一旦撞上 -403, 重试就会突然**带上**指纹, 把"匿名访问"
                //   变成"带设备指纹访问", 正好与该调用点的用意相反。参数原样传下去才不会走样。
                return await GetJsonAsync(url, ps, sign: true, allowRetry: false,
                    referer: referer, fingerprint: fingerprint);
            }
            return (code, msg, data);
        }
        catch (HttpRequestException ex)
        {
            return (-1, "网络请求失败: " + ex.Message, null);
        }
        catch (JsonException)
        {
            return (-2, "响应解析失败", null);
        }
    }

    // ---------------------------------------------------------------- WBI 签名

    private async Task<string?> GetMixinKeyAsync()
    {
        if (_wbiKeys == null) await RefreshWbiKeysAsync();
        if (_wbiKeys == null) return null;

        var (img, sub) = _wbiKeys!.Value;
        var mixin = new StringBuilder(32);
        foreach (var i in MixinKeyEncTab) mixin.Append((img + sub)[i]);
        return mixin.ToString();
    }

    private async Task RefreshWbiKeysAsync()
    {
        try
        {
            // 注意: nav 接口未登录时返回 -101, 但 data 里仍带有 wbi 密钥, 不能判 code==0
            var (_, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/nav", null);
            if (data == null) return;

            if (data.Value.TryGetProperty("wbi_img", out var wi) &&
                wi.TryGetProperty("img_url", out var iu) &&
                wi.TryGetProperty("sub_url", out var su) &&
                iu.ValueKind == JsonValueKind.String &&
                su.ValueKind == JsonValueKind.String)
            {
                var img = NameOnly(iu.GetString()!);
                var sub = NameOnly(su.GetString()!);
                if (img.Length > 0 && sub.Length > 0) _wbiKeys = (img, sub);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static string NameOnly(string url)
    {
        var idx = url.LastIndexOf('/');
        var file = idx >= 0 ? url[(idx + 1)..] : url;
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    private static void SignParams(Dictionary<string, string> ps, string mixin)
    {
        ps["wts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        var query = string.Join("&",
            ps.OrderBy(kv => kv.Key, StringComparer.Ordinal)
              .Select(kv => $"{kv.Key}={Enc(kv.Value)}"));
        ps["w_rid"] = Hashing.Md5Hex(query + mixin);
    }

    /// <summary>与 bilibili 前端一致的参数编码(等价 urllib.urlencode + 过滤!'()*)</summary>
    private static string Enc(string v)
    {
        var s = Uri.EscapeDataString(v)
            .Replace("%21", "!").Replace("%27", "'").Replace("%28", "(")
            .Replace("%29", ")").Replace("%2A", "*");
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '!' or '\'' or '(' or ')' or '*') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 设备指纹

    /// <summary>
    /// 获取设备指纹 Cookie(buvid3/buvid4, 降低风控 -352 触发概率的关键)。
    ///
    /// ★★ 这里有一个**直接影响推荐准确度**的要点: 优先复用已有那一对, 别每次都要新的。
    ///
    /// 为什么: 推荐系统靠 (buvid3 + buvid4) 认"这是同一台设备"。而
    /// `x/frontend/finger/spi` **每次调用都发一对全新的** —— 实测把已有的 buvid3/buvid4
    /// 带上再去调它, 它照样回吐两个新的, 完全不做 round-trip。所以每次启动都换一对,
    /// 等于每次开机都变成一台"从未见过的设备": 推荐模型每次从零冷启动,
    /// 用户看到的就是"推的东西东一榔头西一棒槌"。
    ///
    /// 现在的顺序: 内存 → 本地已存(session.json) → 两边都没有才真去请求 spi,
    /// 并把拿到的那一对**落盘**(见 SessionManager.SetBuvid4)。
    /// 于是正常使用下, 一台设备的身份是稳定的。
    /// </summary>
    public async Task EnsureBuvidAsync()
    {
        // 内存里这一对齐全, 直接用
        if (!string.IsNullOrEmpty(_buvid3) && !string.IsNullOrEmpty(_buvid4)) return;

        // 不全就先把本地存的补进来(buvid3 / buvid4 都可能只缺一个)
        var sess = SessionManager.Instance.Current;
        _buvid3 ??= sess.Buvid3;
        _buvid4 ??= sess.Buvid4;

        // 补齐后完整了 —— 不需要再打网络请求
        if (!string.IsNullOrEmpty(_buvid3) && !string.IsNullOrEmpty(_buvid4)) return;

        try
        {
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/frontend/finger/spi", null);
            if (code == 0 && data != null)
            {
                // ★ 只补**缺的那一半**, 已有的绝不覆盖。
                //   spi 一次给两个, 但已有的那个往往比它给的这个更"旧"、更被服务端认过 ——
                //   假如本地只缺 buvid4(老版本升上来的用户就是这样), 无条件把 b_3 也收下,
                //   就会出现"内存用新 buvid3、磁盘留旧 buvid3"的分叉:
                //   下次启动读回来的是旧 buvid3 + 新 buvid4, 等于又换了一次设备身份。
                if (string.IsNullOrEmpty(_buvid3) &&
                    data.Value.TryGetProperty("b_3", out var b3) &&
                    b3.ValueKind == JsonValueKind.String)
                {
                    _buvid3 = b3.GetString();
                    if (!string.IsNullOrEmpty(_buvid3)) SessionManager.Instance.SetBuvid3(_buvid3);
                }
                if (string.IsNullOrEmpty(_buvid4) &&
                    data.Value.TryGetProperty("b_4", out var b4) &&
                    b4.ValueKind == JsonValueKind.String)
                {
                    _buvid4 = b4.GetString();
                    // ★ 必须落盘: 不存的话下次启动又变成一台新设备(见上面那段说明)
                    if (!string.IsNullOrEmpty(_buvid4)) SessionManager.Instance.SetBuvid4(_buvid4);
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>在 Cookie 基础上附加设备指纹(buvid3/buvid4)</summary>
    private string AppendFingerprint(string cookie)
    {
        if (!string.IsNullOrEmpty(_buvid3) && !cookie.Contains("buvid3", StringComparison.Ordinal))
            cookie = cookie.Length > 0 ? cookie + "; buvid3=" + _buvid3 : "buvid3=" + _buvid3;
        if (!string.IsNullOrEmpty(_buvid4) && !cookie.Contains("buvid4", StringComparison.Ordinal))
            cookie = cookie.Length > 0 ? cookie + "; buvid4=" + _buvid4 : "buvid4=" + _buvid4;
        return cookie;
    }

    // ---------------------------------------------------------------- 业务接口

    /// <summary>热门视频</summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetPopularAsync(int pn)
    {
        return await GetVideoListAsync(
            "https://api.bilibili.com/x/web-interface/popular",
            new Dictionary<string, string> { ["ps"] = "20", ["pn"] = pn.ToString() });
    }

    /// <summary>全站排行榜(rid=0 = 全站, 与分区榜同一个接口)</summary>
    public Task<(bool ok, string? err, List<VideoItem>? items)> GetRankingAsync() => GetRegionRankAsync(0);

    /// <summary>
    /// 排行榜。ranking/v2 的 rid 参数决定范围: 0 = 全站, 其余是分区 id。
    /// 返回该范围 Top 榜(60~95 条, 带 stat 全套计数)。
    /// ★ 不是所有 rid 都被接受: 实测 番剧(13)/国创(167)/VLOG(65559) 这类 PGC/虚拟分区会返回 -400,
    ///   所以分区面板里只放了实测可用的那些(见 PartitionCatalog 的生成说明)。
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

    /// <summary>关注列表(关注的 UP 主)。vmid=自己 mid, pn=页码, ps=每页数量(<=50)</summary>
    public async Task<(bool ok, string? err, List<FollowUser>? list)> GetFollowingsAsync(long vmid, int pn = 1, int ps = 30)
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
                return (false, errMsg, null);
            }

            var users = new List<FollowUser>();
            if (data.Value.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
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
                    if (u.Mid > 0 && !string.IsNullOrEmpty(u.Name)) users.Add(u);
                }
            }
            return (true, null, users);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
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
            item.Cover = UrlUtil.Normalize(GetStr(e, "pic"));
            // UP 主名 + mid: 卡片下方的 UP 主名要能点进他的主页
            if (e.TryGetProperty("owner", out var o))
            {
                item.Author = GetStr(o, "name");
                item.OwnerMid = GetLong(o, "mid");
            }
            item.Duration = VideoItem.FormatSeconds(GetInt(e, "duration"));
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
            if (e.TryGetProperty("pubdate", out var p) && p.ValueKind == JsonValueKind.Number)
                item.Pubdate = p.GetInt64();
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
            // 用户互动状态(登录后才有): 是否已投币
            detail.HasCoined = GetInt(d, "coin") == 1;
            detail.Aid = GetLong(d, "aid");
            detail.Pubdate = GetLong(d, "pubdate");

            // 多分集: 取第一页 cid 用于播放
            if (d.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array
                && pages.GetArrayLength() > 0)
            {
                var p0 = pages[0];
                detail.Cid = GetLong(p0, "cid");
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

    // ---------------------------------------------------------------- 视频合集

    /// <summary>
    /// 视频合集(ugc_season)。单独取一次合集信息时用它; 播放器的主路径是直接吃
    /// <see cref="GetVideoAsync"/> 顺带解析出来的 <see cref="VideoDetail.Season"/>, 不走这里。
    ///
    /// ★ 为什么要单独留一个方法: 超大合集可能被 view 接口截断(自报 ep_count 大于实际给的条数),
    ///   这时才需要走 seasons_archives_list 翻页补齐 —— 补页逻辑收在这里, 调用方只感知成败。
    /// </summary>
    public async Task<(bool ok, string? err, SeasonInfo? data)> GetSeasonAsync(string bvid)
    {
        try
        {
            var (detailOk, detailErr, detail) = await GetVideoAsync(bvid);
            if (!detailOk || detail == null) return (false, detailErr, null);
            if (detail.Season == null) return (false, "这个视频不属于任何合集", null);

            var season = detail.Season;
            // 截断才补页: 正常情况下(实测 118 集 / 254 集都是全量)这里不会触发。
            if (season.EpCount > 0 && season.EpCount > season.Episodes.Count)
            {
                await CompleteSeasonFromArchivesAsync(season);
                // 补进来的条目还没有序号/分组标题标记: 整个展平序重编一遍
                // (newly added 的落在尾部, 重编不会打乱已有条目的相对顺序)
                NumberEpisodes(season);
            }

            return (true, null, season);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取合集失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 把 ugc_season 解析成 <see cref="SeasonInfo"/>。
    ///
    /// ★ 顺序即官方顺序: sections 依次拼接展平后的序列, 与官方网页播放器默认的
    ///   `sort_reverse=false` **逐 aid 完全一致**(2026-10-02 用 118 集合集实测 118/118),
    ///   所以列表直接用这个展平序, 不需要再按发布时间排 —— 它本来也不按时间排。
    /// </summary>
    private static SeasonInfo ParseSeason(JsonElement ugc)
    {
        var season = new SeasonInfo
        {
            SeasonId = GetLong(ugc, "id"),
            Title = GetStr(ugc, "title").StripHtml(),
            // 合集封面本身就是 https, 但也过一道 Normalize —— 下一行 episodes 的 arc.pic
            // 实测是 http://, 统一在这里规范化省得 UI 层再各写一遍。
            Cover = UrlUtil.Normalize(GetStr(ugc, "cover")),
            Mid = GetLong(ugc, "mid"),
            Intro = GetStr(ugc, "intro"),
            EpCount = GetInt(ugc, "ep_count")
        };

        if (!ugc.TryGetProperty("sections", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
            return season;

        foreach (var sec in sections.EnumerateArray())
        {
            if (sec.ValueKind != JsonValueKind.Object) continue;
            var section = new SeasonSection
            {
                Id = GetLong(sec, "id"),
                Title = GetStr(sec, "title").StripHtml()
            };

            if (sec.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in eps.EnumerateArray())
                {
                    if (ep.ValueKind != JsonValueKind.Object) continue;
                    var parsed = ParseSeasonEpisode(ep, section.Id, section.Title);
                    if (parsed == null) continue;
                    section.Episodes.Add(parsed);
                    season.Episodes.Add(parsed);
                }
            }

            // 空分组不放进 Sections: 界面上一个只有标题、底下什么都没有的分组是纯噪音
            if (section.Episodes.Count > 0) season.Sections.Add(section);
        }

        NumberEpisodes(season);
        return season;
    }

    private static SeasonEpisode? ParseSeasonEpisode(JsonElement ep, long sectionId, string sectionTitle)
    {
        // episode 自己的 bvid 是权威播放键; 拿不到就整条丢掉(点不动的一行不如不显示)
        var bvid = GetStr(ep, "bvid");
        if (string.IsNullOrEmpty(bvid)) return null;

        var item = new SeasonEpisode
        {
            Bvid = bvid,
            // ★ aid 必须 long: 实测 117367564666453 已远超 int 上限
            Aid = GetLong(ep, "aid"),
            Cid = GetLong(ep, "cid"),
            Title = GetStr(ep, "title").StripHtml(),
            SectionId = sectionId,
            SectionTitle = sectionTitle
        };

        // arc 是"这条稿件"的摘要: 封面/时长/播放量/发布时间全在它里面
        if (ep.TryGetProperty("arc", out var arc) && arc.ValueKind == JsonValueKind.Object)
        {
            // 封面/时长以 arc 为准; title 优先用 episode 自己的(它是合集语境下的标题)
            item.Cover = UrlUtil.Normalize(GetStr(arc, "pic"));
            item.DurationSec = GetInt(arc, "duration");
            item.Pubdate = GetLong(arc, "pubdate");
            if (item.Title.Length == 0) item.Title = GetStr(arc, "title").StripHtml();
            if (item.Aid == 0) item.Aid = GetLong(arc, "aid");
            if (arc.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
                item.ViewCount = GetLong(st, "view");
        }
        // arc 缺了就从 page 兜底(实测两者 cid/duration 一致, 只是字段可能各自缺)
        if (ep.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
        {
            if (item.Cid == 0) item.Cid = GetLong(page, "cid");
            if (item.DurationSec == 0) item.DurationSec = GetInt(page, "duration");
        }

        return item;
    }

    /// <summary>
    /// 给展平后的每一集补上"第几集"与"是不是分组第一集"。
    /// 单独一步是因为这两件事依赖**跨分组的累计**, 逐条解析时算不出来。
    ///
    /// 按 <see cref="SeasonInfo.Episodes"/> 的展平顺序编号(而不是按 Sections 再遍历一遍):
    /// 兜底补页追加进来的条目只落在展平序列的尾部, 两个序列的顺序在那种情况下并不一致,
    /// 以展平序为准才不会出现"第 119 集排在第 100 集前面"。
    /// </summary>
    private static void NumberEpisodes(SeasonInfo season)
    {
        var started = new HashSet<long>();
        var index = 0;
        // 单分组时不画分组标题(官方在只有"正片"一组时也不显示组名)。
        // 在这里就定下初值: 界面灌数据之前必须先有确定值(条目是普通 POCO, 没有变更通知)。
        var showHeader = season.Sections.Count > 1;
        foreach (var ep in season.Episodes)
        {
            ep.Index = ++index;
            ep.IsSectionStart = started.Add(ep.SectionId);
            ep.ShowSectionHeader = showHeader && ep.IsSectionStart;
        }
    }

    /// <summary>
    /// 超大合集的兜底补齐: 走 <c>polymer/web-space/seasons_archives_list</c> 按页取。
    ///
    /// 只有 view 接口给不全(ep_count > 实际条数)时才会被调用, 正常路径永远不触发。
    /// 补进来的条目**排在原有条目之后**: 原有那批来自 view, 顺序已与官方一致, 不能打乱;
    /// 缺的那些按接口返回的页序追加即可。
    ///
    /// ★★ 终止条件必须是"这一页没有 archives", **不能靠错误码** ——
    ///   实测越界页照样回 code=0 + 空数组, 靠错误码判断会死循环。
    /// ★ mid 会被服务端忽略(给错的 mid 也照样返回全部), 所以只用它做参数, 不做校验。
    /// ★ page_size 上限: 30/50/100 可用, 200 回 -400。这里保守取 30。
    /// </summary>
    private async Task CompleteSeasonFromArchivesAsync(SeasonInfo season)
    {
        // 卫语句: 没有 season_id/mid 就没法翻页; 已经是全量也不用翻
        if (season.SeasonId <= 0 || season.Mid <= 0) return;

        var seen = new HashSet<string>(season.Episodes.Select(e => e.Bvid), StringComparer.Ordinal);
        // 上限只是防"服务端永远不返回空页"这种病态情况, 30 页 × 30 条 = 900 集, 远超实际合集规模
        const int maxPages = 30;
        const int pageSize = 30;

        for (var pageNum = 1; pageNum <= maxPages; pageNum++)
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-space/seasons_archives_list",
                new Dictionary<string, string>
                {
                    ["mid"] = season.Mid.ToString(),
                    ["season_id"] = season.SeasonId.ToString(),
                    // false = 官方网页播放器的默认顺序(与 view 的展平序一致)
                    ["sort_reverse"] = "false",
                    ["page_num"] = pageNum.ToString(),
                    ["page_size"] = pageSize.ToString()
                },
                referer: "https://space.bilibili.com/" + season.Mid);

            if (code != 0 || data == null)
                throw new InvalidOperationException(
                    $"合集分页补齐失败 (code {code}{(string.IsNullOrEmpty(msg) ? "" : ": " + msg)})");
            if (!data.Value.TryGetProperty("archives", out var archives) ||
                archives.ValueKind != JsonValueKind.Array)
                return;

            // ★ 越界页就是"空数组 + code 0", 靠它收口
            var count = archives.GetArrayLength();
            if (count == 0) return;

            foreach (var a in archives.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var bvid = GetStr(a, "bvid");
                if (string.IsNullOrEmpty(bvid)) continue;
                if (!seen.Add(bvid)) continue;   // 原有条目已经带过, 不重复追加

                var ep = new SeasonEpisode
                {
                    Bvid = bvid,
                    Aid = GetLong(a, "aid"),
                    Title = GetStr(a, "title").StripHtml(),
                    Cover = UrlUtil.Normalize(GetStr(a, "pic")),
                    DurationSec = GetInt(a, "duration"),
                    Pubdate = GetLong(a, "pubdate")
                };
                if (a.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
                    ep.ViewCount = GetLong(st, "view");

                // 补进来的条目没有分组归属(接口不给): 挂到第一个分组名下, 免得界面上
                // 它们落在所有分组标题之外、看起来像"无家可归"的一堆。
                var host = season.Sections.Count > 0 ? season.Sections[0] : null;
                if (host != null)
                {
                    ep.SectionId = host.Id;
                    ep.SectionTitle = host.Title;
                    host.Episodes.Add(ep);
                }
                season.Episodes.Add(ep);
            }
        }
    }

    /// <summary>
    /// 短视频的"本地合流"时长阈值(毫秒)。3 分钟是 B 站对"短视频"的口径。
    ///
    /// 为什么短视频要特殊处理: B 站的 DASH 把画面和声音分成两个文件, 播放器只能用
    /// LibVLC 的 `input-slave` 把音轨挂上去 —— 那是**两个各自独立的输入**(各自解复用、
    /// 各自时钟、各自 EOF)。实测(2026-09-25, 同一个 76.5 秒视频跳到 60s):
    ///   · 画面恢复要 1383ms(单个本地文件只要 470ms);
    ///   · 音轨先 EOF 会把**整个 input** 提前结束, 结尾固定少 1911ms(没声音 + 进度条提前满格);
    ///   · 而且这与"音轨在本地还是远端"无关(把音轨换成本地文件, 结尾照样少 1911ms),
    ///     是这个机制的固有缺陷。
    /// 短视频体积可控(3 分钟 1080P 约 20MB), 所以给它走"下到本地再合流成一个 mp4"的路:
    /// 单文件源同时解决跳转与结尾两个问题, 且能保留 1080P(见 ShortVideoCache)。
    /// 长视频体积不可控, 只能维持远端双流。
    /// </summary>
    private const long ShortVideoRemuxThresholdMs = 180_000;

    /// <summary>
    /// 关注 UP 的动态时间线(「动态」页的"全部动态")。
    ///
    /// 接口: <c>GET /x/polymer/web-dynamic/v1/feed/nav</c>(需 WBI 签名 + 登录态)。
    /// 动态的种类很多(视频/图文/纯文字/转发/直播开播...), 这个页面只关心**视频投稿**:
    ///   · type = DYNAMIC_TYPE_AV 且 major 里带 archive —— 直接映射成 VideoItem 走通用卡片;
    ///   · 转发(DYNAMIC_TYPE_FORWARD)里嵌的视频(orig)也算 —— 用户想看的是"UP 发了什么视频";
    ///   · 图文/纯文字/投票等没有可播对象, 跳过。
    /// 分页是游标式的: 响应里的 offset 原样带给下一页, has_more=false 就是到底了。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, string? nextOffset, bool hasMore)>
        GetDynamicFeedAsync(string? offset = null)
    {
        var items = new List<VideoItem>();
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(offset)) ps["offset"] = offset;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/nav", ps,
                sign: true, referer: "https://t.bilibili.com/");
            if (code != 0 || data == null)
                return (false, code == -101 ? "请先登录后查看动态" : (msg ?? "动态加载失败"), null, null, false);

            var d = data.Value;
            var hasMore = d.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.Number && hm.GetInt32() == 1;
            var next = d.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.String
                ? off.GetString() : null;

            if (d.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    var item = ParseDynamicVideo(e);
                    if (item != null) items.Add(item);
                }
            }
            return (true, null, items, next, hasMore);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, ex.Message, null, null, false);
        }
    }

    /// <summary>
    /// 把一条动态卡映射成 VideoItem(只取"能播的视频"), 不是视频动态就返回 null。
    ///
    /// **实测的返回是扁平结构**(2026-09-25, 带 WBI + 登录态):
    ///   { id_str, type, rid, title, cover, jump_url, pub_time,
    ///     author: { mid, name, face } }
    ///   · type=8 是视频投稿, rid 即 avid, bvid 要从 jump_url 里截出来;
    ///   · type=1 是转发, 内容嵌在 orig 里(递归取);
    ///   · 图文(2)/纯文字(4) 等没有可播对象, 跳过。
    /// 注: 服务端对某些客户端形态会返回 modules 嵌套结构, 这里两种都兼容 ——
    ///     扁平优先, 取不到再走 modules 路径。
    /// </summary>
    private static VideoItem? ParseDynamicVideo(JsonElement e)
    {
        try
        {
            // ---- 扁平结构(实测主路径) ----
            var type = GetStr(e, "type");
            var flatType = 0;
            int.TryParse(type, out flatType);
            if (flatType == 0 && type.Length == 0 &&
                e.TryGetProperty("type", out var tv) && tv.ValueKind == JsonValueKind.Number)
                flatType = tv.GetInt32();

            // 转发: 递归取被转发的原动态
            if (flatType == 1 && e.TryGetProperty("orig", out var orig) &&
                orig.ValueKind == JsonValueKind.Object)
                return ParseDynamicVideo(orig);

            var hasFlatVideo = flatType == 8 || (flatType == 0 &&
                e.TryGetProperty("cover", out var cv) && cv.ValueKind == JsonValueKind.String &&
                e.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String);
            if (hasFlatVideo)
            {
                var bvid = ExtractBvidFromJumpUrl(GetStr(e, "jump_url"));
                if (string.IsNullOrEmpty(bvid)) bvid = GetStr(e, "bvid");
                if (string.IsNullOrEmpty(bvid)) return null;

                var item = new VideoItem
                {
                    Bvid = bvid,
                    Title = GetStr(e, "title"),
                    Cover = GetStr(e, "cover"),
                };
                if (long.TryParse(GetStr(e, "rid"), out var rid)) item.Aid = rid;
                if (e.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
                {
                    item.Author = GetStr(au, "name");
                    item.OwnerMid = GetLong(au, "mid");
                }
                // 旧版扁平结构把作者 mid 放在顶层 uid
                if (item.OwnerMid <= 0) item.OwnerMid = GetLong(e, "uid");
                return item;
            }

            // ---- modules 嵌套结构(兼容路径) ----
            var arcJson = FindArchiveJson(e);
            if (arcJson == null) return null;
            using var doc = JsonDocument.Parse(arcJson);
            var a = doc.RootElement;
            var bvid2 = GetStr(a, "bvid");
            if (string.IsNullOrEmpty(bvid2)) return null;
            var item2 = new VideoItem
            {
                Bvid = bvid2,
                Aid = GetLong(a, "aid"),
                Title = GetStr(a, "title"),
                Cover = GetStr(a, "cover"),
                Duration = GetStr(a, "duration_text"),
            };
            if (a.TryGetProperty("stat", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                item2.ViewCount = st.TryGetProperty("view", out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt64() : 0;
                item2.DanmakuCount = st.TryGetProperty("danmaku", out var dm) && dm.ValueKind == JsonValueKind.Number
                    ? dm.GetInt64() : 0;
            }
            if (e.TryGetProperty("modules", out var mods) &&
                mods.TryGetProperty("module_author", out var au2))
            {
                item2.Author = GetStr(au2, "name");
                item2.OwnerMid = GetLong(au2, "mid");
            }
            return item2;
        }
        catch
        {
            return null;   // 单条动态结构异常只丢这一条, 不影响整页
        }
    }

    /// <summary>jump_url 形如 "//www.bilibili.com/video/BV1VfaT6ZELT" → 截出 bvid</summary>
    private static string ExtractBvidFromJumpUrl(string jumpUrl)
    {
        var i = jumpUrl.IndexOf("/video/", StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = jumpUrl[(i + "/video/".Length)..];
        var end = rest.IndexOf('?');
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>modules 嵌套结构里找 archive(兼容转发), 返回其原文 JSON</summary>
    private static string? FindArchiveJson(JsonElement el)
    {
        if (!el.TryGetProperty("modules", out var mods)) return null;
        if (!mods.TryGetProperty("module_dynamic", out var dyn)) return null;
        if (!dyn.TryGetProperty("major", out var major)) return null;
        var majorType = GetStr(major, "type");
        if (majorType == "MAJOR_TYPE_ARCHIVE" &&
            major.TryGetProperty("archive", out var arc) && arc.ValueKind == JsonValueKind.Object)
            return arc.ToString();
        if (majorType == "MAJOR_TYPE_FORWARD" &&
            major.TryGetProperty("orig", out var orig) && orig.ValueKind == JsonValueKind.Object)
            return FindArchiveJson(orig);
        return null;
    }

    /// <summary>获取视频播放地址。
    /// 优先 DASH 流(最高可用清晰度), 视频/音频分离——播放器用 input-slave 合并音轨;
    /// DASH 完全不可用时回退 durl 单流。
    /// 返回 (ok, err, 主 url、备选 urls、时长、可用清晰度列表、实际返回的清晰度、
    /// 音轨 url(仅 DASH)、以及**短视频的本地合流规格**(非 null 表示播放器可以去下它并
    /// 合成本地文件, 见 ShortVideoCache))。
    ///
    /// ★ 2026-10-01: 前面补上了 `ok`/`err`。以前这个元组里**一个能说明成败的字段都没有**,
    ///   调用方只能靠 `url == null` 猜, 于是播放器在失败时只能弹一句"该清晰度暂时不可用" ——
    ///   而真正的理由(网络失败 / 大会员专属 / 该清晰度无 DASH 也无 durl)在这一层就被丢掉了。
    ///   现在与本类其它方法统一为 `(bool ok, string? err, ...)` 开头, 失败原因能直接透给用户。
    /// </summary>
    public async Task<(bool ok, string? err, string? url, List<string> backups, long durationMs,
        List<(int qn, string label)> qualities, int actualQn, string? audioUrl, DashStreams? remux)>
        GetPlayUrlAsync(string bvid, long cid, int qn = 80)
    {
        var qualities = new List<(int qn, string label)>();
        try
        {
            await EnsureBuvidAsync();
            // ---- 1. DASH(fnval=16): 清晰度不受 html5 平台 720P 限制 ----
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "16",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            ApplyTryLook(ps);
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code == 0 && data != null &&
                data.Value.TryGetProperty("dash", out var dash) && dash.ValueKind == JsonValueKind.Object)
            {
                long dur = data.Value.TryGetProperty("timelength", out var tl) && tl.ValueKind == JsonValueKind.Number
                    ? tl.GetInt64() : 0;

                string? videoUrl = null, audioUrl = null;
                var backups = new List<string>();
                int chosenQn = 0;
                // 短视频做本地合流要用到这几项(码率用于估算体积, 其余写进容器)
                JsonElement? chosen = null;
                JsonElement? chosenAudio = null;

                if (dash.TryGetProperty("video", out var videos) && videos.ValueKind == JsonValueKind.Array)
                {
                    // 收集可用清晰度(按 id 去重, 从高到低)
                    var seen = new HashSet<int>();
                    foreach (var v in videos.EnumerateArray())
                    {
                        var id = GetInt(v, "id");
                        if (id > 0 && seen.Add(id))
                            qualities.Add((id, QnToLabel(id)));
                    }
                    qualities.Sort((a, b) => b.qn.CompareTo(a.qn));

                    // 选流: 不超过请求 qn 的最高清晰度; **同档优先 HEVC**。
                    //
                    // ★ 为什么同档优先 hevc 而不是 avc(2026-09-28 实测, 见下):
                    //   同一个视频(限免 4K 样片 BV1NGZtBwELa)同一档清晰度, 服务端同时给
                    //   avc 与 hevc 两条流, 声明码率差一倍:
                    //       id=120(4K)    avc 12.60Mbps   hevc 7.03Mbps   (hevc 省 44%)
                    //       id=112(1080P+) avc  4.17Mbps   hevc 2.63Mbps   (省 37%)
                    //       id=80 (1080P)  avc  2.67Mbps   hevc 1.34Mbps   (省 50%)
                    //   同画质(同一档 qn)下 hevc 只要一半左右的数据量。原来"同档优先 avc"等于
                    //   每次起播/跳转都要多搬 40%~100% 的字节 —— 4K 档最明显, 这正是
                    //   "缓冲特别久"的主因之一。硬解(VLC 默认 avcodec-hw=any)对 hevc 与 avc
                    //   同样支持, 所以这不是"拿兼容性换速度"。
                    //   av01(若有)排在最后: 体积更小但硬解支持面窄, 不主动选。
                    JsonElement best = default; int bestId = 0; int bestRank = -1;
                    foreach (var v in videos.EnumerateArray())
                    {
                        var id = GetInt(v, "id");
                        var codec = GetStr(v, "codecs");
                        if (id <= 0 || id > qn) continue;
                        var rank = CodecRank(codec);
                        if (bestId == 0 || id > bestId || (id == bestId && rank > bestRank))
                        {
                            best = v; bestId = id; bestRank = rank;
                        }
                    }
                    // 请求的 qn 不可用时取最高档(同档仍按上面的编码优先级挑)
                    if (bestId == 0 && videos.GetArrayLength() > 0)
                    {
                        foreach (var v in videos.EnumerateArray())
                        {
                            var id = GetInt(v, "id");
                            var rank = CodecRank(GetStr(v, "codecs"));
                            if (id > bestId || (id == bestId && rank > bestRank))
                            {
                                best = v; bestId = id; bestRank = rank;
                            }
                        }
                    }
                    if (bestId > 0)
                    {
                        // ★ 线路选择交给 CdnService(2026-10-03 新增, 设置页可配)。
                        //   它负责三件事: 按用户策略(自动测速/手动指定/跟随服务端)选线路、
                        //   剔除 PCDN、全部失败时回退。详细依据见 Services/CdnService.cs。
                        //
                        //   以前这里只有一条静态规则"优先非 mcdn", 问题是:
                        //     · 无法利用"各家 CDN 同一时刻吞吐差 4 倍"这件事(实测 2.5~11MB/s);
                        //     · 认不出**伪装域名** —— 实测 B 站给过
                        //       `mv0bz14m.edge.mountaintoys.cn`(query 里 os=mcdn), 域名看不出
                        //       PCDN 特征, 旧规则会把它当成正常线路用。
                        var cands = new List<string>();
                        var baseUrl = GetStr(best, "baseUrl");
                        if (baseUrl.Length > 0) cands.Add(baseUrl);
                        if (best.TryGetProperty("backupUrl", out var bu) && bu.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var u in bu.EnumerateArray())
                            {
                                var s = u.GetString();
                                if (!string.IsNullOrEmpty(s)) cands.Add(s);
                            }
                        }
                        if (cands.Count > 0)
                        {
                            var picked = await CdnService.Instance.SelectUrlAsync(cands[0], cands);
                            videoUrl = picked;
                            // 备用线路 = 其余候选(去重、且不包含已选中的那条)
                            foreach (var c in cands)
                                if (!string.Equals(c, picked, StringComparison.Ordinal) && !backups.Contains(c))
                                    backups.Add(c);
                            chosenQn = bestId;
                            chosen = best;
                        }
                    }
                }

                if (dash.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array)
                {
                    // 选码率最高的音轨(码率高的那条通常带更好的编码参数), 线路同样避开 mcdn。
                    // 音轨虽然只有 0.1~0.2Mbps, 但 DASH 是靠 input-slave 挂进来的: 它拉不动
                    // 会连累整个 input 的起播与跳转(见 DashRemuxer 里"音轨 EOF 拖垮 input"那条)。
                    long bestBw = -1;
                    foreach (var a in audios.EnumerateArray())
                    {
                        var bw = GetLong(a, "bandwidth");
                        if (bw <= bestBw) continue;
                        var acands = new List<string>();
                        var abase = GetStr(a, "baseUrl");
                        if (abase.Length > 0) acands.Add(abase);
                        if (a.TryGetProperty("backupUrl", out var abu) && abu.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var u in abu.EnumerateArray())
                            {
                                var s = u.GetString();
                                if (!string.IsNullOrEmpty(s)) acands.Add(s);
                            }
                        }
                        if (acands.Count == 0) continue;
                        bestBw = bw;
                        // 音轨复用视频那次测速的结果, 不单独测速(见 SelectSecondaryUrl 的说明)
                        audioUrl = CdnService.Instance.SelectSecondaryUrl(acands[0], acands);
                        chosenAudio = a;
                    }
                }

                if (!string.IsNullOrEmpty(videoUrl))
                {
                    // ---- 短视频: 顺带给出"本地合流"所需的规格(播放器后台下载并合成本地文件) ----
                    // 注意这里**不改播放地址**: 起播仍然用远端 DASH(零等待), 合流是后台在做,
                    // 就绪后播放器再切过去。见 ShortVideoRemuxThresholdMs 的说明。
                    DashStreams? remux = null;
                    if (dur > 0 && dur <= ShortVideoRemuxThresholdMs &&
                        !string.IsNullOrEmpty(audioUrl) && chosen != null && chosenAudio != null)
                    {
                        var v = chosen.Value;
                        var au = chosenAudio.Value;
                        remux = new DashStreams
                        {
                            VideoUrl = videoUrl!,
                            AudioUrl = audioUrl!,
                            DurationMs = dur,
                            VideoBandwidth = GetLong(v, "bandwidth"),
                            VideoCodecs = GetStr(v, "codecs"),
                            VideoWidth = GetInt(v, "width"),
                            VideoHeight = GetInt(v, "height"),
                            AudioBandwidth = GetLong(au, "bandwidth"),
                            AudioCodecs = GetStr(au, "codecs")
                        };
                    }
                    return (true, null, videoUrl, backups, dur, qualities, chosenQn, audioUrl, remux);
                }
            }

            // ---- 2. 回退 durl 单流 ----
            var (mUrl2, mBackups2, mQn2, mQualities2, dur2) = await FetchMuxedAsync(bvid, cid, qn);
            if (mUrl2 == null)
                return (false, "DASH 与 durl 都没能取到可播放的地址(可能是大会员/付费专属, 或该清晰度无源)",
                    null, new List<string>(), 0, qualities, 0, null, null);
            if (mQualities2.Count > 0) qualities = mQualities2;
            return (true, null, mUrl2, mBackups2, dur2, qualities, mQn2, null, null);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取播放地址失败: " + ex.Message,
                null, new List<string>(), 0, qualities, 0, null, null);
        }
    }

    /// <summary>
    /// 请求 durl(单流)并挑出可直接播放的那一条, 返回 (url, 备用线路, 实际清晰度, 清晰度列表, 时长)。
    ///
    /// 两个必须注意的地方:
    ///   1. **不要带 `platform=html5`**。带上它现在一律返回 `-404 啥都木有`(实测),
    ///      回退分支以前就是因为这个参数常年跑不通。去掉之后 qn=80 在 pc 平台能正常拿到 durl。
    ///   2. durl 可能是**多段**(长视频被切片, 每段带 order)。播放器只会播第一条,
    ///      所以这里要求"必须只有一段"才算可用 —— 宁可让上面回退到 DASH, 也不要给用户
    ///      一条"播到一半就没了"的地址。
    /// </summary>
    private async Task<(string? url, List<string> backups, int qn,
        List<(int qn, string label)> qualities, long durationMs)>
        FetchMuxedAsync(string bvid, long cid, int qn)
    {
        var qualities = new List<(int qn, string label)>();
        var backups = new List<string>();
        try
        {
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "1",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null) return (null, backups, 0, qualities, 0);

            long dur = data.Value.TryGetProperty("timelength", out var tl) && tl.ValueKind == JsonValueKind.Number
                ? tl.GetInt64() : 0;

            var segs = new List<string>();
            if (data.Value.TryGetProperty("durl", out var durl) && durl.ValueKind == JsonValueKind.Array)
            {
                foreach (var seg in durl.EnumerateArray())
                {
                    var u = GetStr(seg, "url");
                    if (!string.IsNullOrEmpty(u)) segs.Add(u);
                }
            }
            // 多段没法播(播放器不会自动续下一段), 当作拿不到
            if (segs.Count != 1) return (null, backups, 0, qualities, dur);

            var actualQn = GetInt(data.Value, "quality");
            if (data.Value.TryGetProperty("accept_quality", out var aq) && aq.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in aq.EnumerateArray())
                    if (q.ValueKind == JsonValueKind.Number)
                        qualities.Add((q.GetInt32(), QnToLabel(q.GetInt32())));
            }
            if (qualities.Count == 0 && actualQn > 0)
                qualities.Add((actualQn, QnToLabel(actualQn)));
            qualities.Sort((a, b) => b.qn.CompareTo(a.qn));

            return (segs[0], backups, actualQn, qualities, dur);
        }
        catch
        {
            // 解析失败按"没有可用地址"处理, 交给调用方走降级(报错/回落 durl) ——
            // 这里刻意不 ReportError: 拿到了 200 但结构不认识属于内容问题, 不该弹窗打扰用户。
            return (null, backups, 0, qualities, 0);
        }
    }

    /// <summary>
    /// 未登录时补上"免登录 1080P"参数(仿 PiliPlus 的做法)。
    ///
    /// ★★ 这里有一个**只加 try_look 会完全无效**的坑, 实测结论(2026-10-03, 见 .probes/bd-probe-trylook):
    ///
    ///   必要性矩阵(未登录, 同一视频, 每格重复 2 轮结果一致):
    ///     都不加                       -> dash.video id = [16,32]           只有 360P/480P
    ///     **只加 try_look=1**          -> dash.video id = [16,32]           依然只有 360P/480P  ← 无效!
    ///     **只加 dm_img_* 四个**        -> dash.video id = [16,32]           依然只有 360P/480P  ← 无效!
    ///     try_look=1 + dm_img_* 四个   -> dash.video id = [16,32,64,80]     ★ 720P + 1080P 解锁
    ///
    ///   也就是说这是**两要素同时成立**才放行: try_look 触发"试看"通道, 四个 dm_img_*
    ///   是被校验的设备指纹。少任何一个都退回 360P/480P —— 而且**四个一个都不能少**
    ///   (单独抽掉 dm_img_inter 就会立刻退回 [16,32])。
    ///
    ///   为什么别的项目里"只发 try_look"也能看到 1080P 的说法靠不住:
    ///   PiliPlus 是 2026-05-08 才补上 dm_img_* 的(此前只发 try_look), 那次补丁正是为了修
    ///   它自己 issue #2020「免登录1080p看不了」。照抄它的旧版参数就会复现那个 bug。
    ///
    /// 顺带实测确认**不需要**的东西(避免以后有人"顺手加上去"):
    ///   · 不需要 Cookie(完全无 session 也能解锁)
    ///   · 不需要改 UA / Referer / Origin / env / app-key / x-bili-aurora-zone
    ///   · 不需要把 fnval 从 16 改成 4048, 也不需要动 fourk / fnver
    ///   · qn 不决定返回哪些流(qn=16 照样返回 [16,32,64,80]), 它只决定哪一条带可播放地址
    ///
    /// 能力上限: 只到 1080P(id=80)。1080P+(112) / 60帧(116) / 4K(120) / HDR 匿名拿不到,
    /// 那是账号等级限制, 不是参数问题。番剧(PGC)也不适用 —— 那条接口匿名返回空 dash。
    ///
    /// ⚠️ data.quality 在 qn=80 时会返回 64(看起来像只给了 720P), 但 dash.video 里
    ///    **确实有** id=80 的 1920x1080 流。判断清晰度必须看 dash.video[].id, 不能看它。
    ///
    /// ⚠️ 这是一套设备指纹启发式校验, B 站随时可能改判据(历史上已失效过两次)。
    ///    所以失败要能静默回退 —— 这里只是"多加几个参数", 最坏情况就是退回现在的 360P/480P,
    ///    不会让播放本身出错。
    /// </summary>
    private static void ApplyTryLook(Dictionary<string, string> ps)
    {
        // 只在"未登录 + 用户开着该开关"时发。
        // 已登录时 B 站按账号等级给流, 发这个参数没有意义(还可能干扰), 所以直接不发。
        if (SessionManager.Instance.HasLogin) return;
        if (!SettingsStore.Instance.NoLogin1080P) return;

        ps["try_look"] = "1";
        // 四个设备指纹参数必须**成组**出现, 缺一不可(见上方必要性矩阵)。
        // 取值本身几乎不被校验: dm_img_str / dm_cover_img_str 用随机 base64 即可,
        // 但要保证是"非空的、形状像指纹"的串 —— 这是 PiliPlus 用随机 base64 的原因。
        ps["dm_img_list"] = "[]";
        ps["dm_img_str"] = RandomBase64(16, 64);
        ps["dm_cover_img_str"] = RandomBase64(32, 128);
        ps["dm_img_inter"] = "{\"ds\":[],\"wh\":[0,0,0],\"of\":[0,0,0]}";
    }

    /// <summary>生成指定长度范围内的随机 base64 串(用于 dm_img_str / dm_cover_img_str)</summary>
    private static string RandomBase64(int minLen, int maxLen)
    {
        var n = Random.Shared.Next(minLen, maxLen + 1);
        var bytes = new byte[n];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// 取一组"带签名的真实媒体 URL", 专门给 CDN 测速当样本用(设置页「重新测速」)。
    ///
    /// 为什么单独一个方法、而不是复用 GetPlayUrlAsync:
    ///   GetPlayUrlAsync 返回的是**已经按用户策略选好线路**的那一条, 拿它测速等于
    ///   "只测了当前用的这条", 测不出别的线路更快。测速需要的是**原始候选集**
    ///   (baseUrl + 所有 backupUrl), 所以这里返回全部候选。
    ///
    /// 返回空列表 = 没有可用样本(取流失败/付费视频), 调用方据此提示用户。
    /// </summary>
    public async Task<List<string>> GetCdnSampleUrlsAsync(string bvid, long cid, int qn = 80)
    {
        var urls = new List<string>();
        try
        {
            await EnsureBuvidAsync();
            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "16",
                ["fnver"] = "0",
                ["fourk"] = "1"
            };
            ApplyTryLook(ps);
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null) return urls;

            if (!data.Value.TryGetProperty("dash", out var dash) ||
                dash.ValueKind != JsonValueKind.Object ||
                !dash.TryGetProperty("video", out var videos) ||
                videos.ValueKind != JsonValueKind.Array ||
                videos.GetArrayLength() == 0)
                return urls;

            // 用最高那段视频当样本(它最吃带宽, 测出来的差距最有代表性)
            JsonElement best = default;
            var bestId = 0;
            foreach (var v in videos.EnumerateArray())
            {
                var id = GetInt(v, "id");
                if (id > bestId) { bestId = id; best = v; }
            }
            if (bestId <= 0) return urls;

            var baseUrl = GetStr(best, "baseUrl");
            if (baseUrl.Length > 0) urls.Add(baseUrl);
            if (best.TryGetProperty("backupUrl", out var bu) && bu.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in bu.EnumerateArray())
                {
                    var s = u.GetString();
                    if (!string.IsNullOrEmpty(s) && !urls.Contains(s)) urls.Add(s);
                }
            }
        }
        catch (Exception ex)
        {
            // 测速样本拿不到不该弹错(那是设置页的一个辅助功能), 但要在日志里留痕
            App.ReportError(ex);
        }
        return urls;
    }

    /// <summary>清晰度 id 转显示名</summary>
    public static string QnToLabel(int qn) => qn switch
    {
        127 => "杜比视界",
        126 => "杜比全景声",
        125 => "HDR",
        120 => "4K",
        116 => "1080P60",
        112 => "1080P+",
        100 => "智能修复",
        80 => "1080P",
        74 => "720P60",
        64 => "720P",
        32 => "480P",
        16 => "360P",
        _ => qn + "P"
    };

    /// <summary>获取 UP 主空间信息。
    /// acc/info 系列接口风控严格(未带完整设备指纹时返回 -403「访问权限不足」),
    /// 因此采用多接口降级策略:
    ///   1. WBI 签名的 /x/space/wbi/acc/info (信息最全)
    ///   2. 公开的 /x/web-interface/card (网页卡片悬浮预览接口, 风控宽松, 附带粉丝/投稿数)
    ///   3. 旧版 /x/space/acc/info
    /// 全部失败才返回 ok=false。★ 2026-10-01 由裸 <c>Task&lt;SpaceInfo?&gt;</c> 改为 (ok, err, data):
    /// 三个降级接口的 code 原本全被丢掉了, 调用方只知道"没取到", 排查风控时无从下手。</summary>
    public async Task<(bool ok, string? err, SpaceInfo? data)> GetSpaceInfoAsync(long mid)
    {
        try
        {
            await EnsureBuvidAsync();
            var referer = $"https://space.bilibili.com/{mid}/";

            // 1. WBI 签名接口
            var (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/wbi/acc/info",
                new Dictionary<string, string> { ["mid"] = mid.ToString() },
                sign: true, referer: referer);
            if (code == 0 && data != null)
            {
                var d = data.Value;
                return (true, null, new SpaceInfo
                {
                    Name = GetStr(d, "name"),
                    Face = UrlUtil.Normalize(GetStr(d, "face")),
                    Sign = GetStr(d, "sign"),
                    Mid = mid,
                    Level = GetInt(d, "level")
                });
            }

            // 2. 公开名片接口(风控宽松, 含粉丝/关注/投稿数)
            (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/card",
                new Dictionary<string, string> { ["mid"] = mid.ToString() },
                referer: referer);
            if (code == 0 && data != null)
            {
                var d = data.Value;
                if (d.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
                {
                    return (true, null, new SpaceInfo
                    {
                        Name = GetStr(card, "name"),
                        Face = UrlUtil.Normalize(GetStr(card, "face")),
                        Sign = GetStr(card, "sign"),
                        Mid = mid,
                        Level = card.TryGetProperty("level_info", out var li) ? GetInt(li, "current_level") : 0,
                        Follower = GetLong(d, "follower"),
                        Following = GetLong(d, "following"),
                        VideoCount = GetLong(d, "archive_count")
                    });
                }
            }

            // 3. 旧版接口兜底
            (code, _, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/acc/info",
                new Dictionary<string, string> { ["mid"] = mid.ToString() },
                referer: referer);
            if (code == 0 && data != null)
            {
                var d = data.Value;
                return (true, null, new SpaceInfo
                {
                    Name = GetStr(d, "name"),
                    Face = UrlUtil.Normalize(GetStr(d, "face")),
                    Sign = GetStr(d, "sign"),
                    Mid = mid,
                    Level = GetInt(d, "level")
                });
            }
            return (false, "三个空间接口都取不到该 UP 主的信息(可能是 mid 不存在或被风控)", null);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取 UP 主信息失败: " + ex.Message, null);
        }
    }

    /// <summary>获取 UP 主空间视频列表。
    /// 优先 arc/search(WBI 签名); 被风控(-403/-352)时降级到公开的 recArchivesByKeywords</summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)>
        GetSpaceVideosAsync(long mid, int pn = 1, int ps = 30)
    {
        try
        {
            await EnsureBuvidAsync();
            var ps2 = new Dictionary<string, string>
            {
                ["mid"] = mid.ToString(),
                ["pn"] = pn.ToString(),
                ["ps"] = Math.Min(ps, 50).ToString(),
                ["order"] = "pubdate",
                ["platform"] = "web",
                ["web_location"] = "1550101"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/space/wbi/arc/search", ps2, sign: true,
                referer: $"https://space.bilibili.com/{mid}/video");
            if (code != 0 || data == null)
            {
                // 风控降级: 公开的合集推荐接口, 不需要签名, 返回完整 archive 对象
                return await GetSpaceVideosFallbackAsync(mid, pn, ps);
            }

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Object
                && arr.TryGetProperty("vlist", out var vl) && vl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in vl.EnumerateArray())
                {
                    var item = ParseVideoItem(e);
                    if (item != null) items.Add(item);
                }
            }
            if (items.Count == 0) return await GetSpaceVideosFallbackAsync(mid, pn, ps);
            return (true, null, items);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    /// <summary>空间视频列表降级接口(series/recArchivesByKeywords, 公开无签名)。
    /// 返回 data.archives 数组, 字段与 arc/search 的 vlist 略有不同</summary>
    private async Task<(bool ok, string? err, List<VideoItem>? items)>
        GetSpaceVideosFallbackAsync(long mid, int pn, int ps)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/series/recArchivesByKeywords",
                new Dictionary<string, string>
                {
                    ["mid"] = mid.ToString(),
                    ["keywords"] = "",
                    ["pn"] = pn.ToString(),
                    ["ps"] = Math.Min(ps, 50).ToString()
                },
                referer: $"https://space.bilibili.com/{mid}/video");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("archives", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var bvid = GetStr(e, "bvid");
                    if (string.IsNullOrEmpty(bvid)) continue;
                    long view = 0, danmaku = 0, like = 0;
                    if (e.TryGetProperty("stat", out var stat))
                    {
                        view = GetLong(stat, "view");
                        danmaku = GetLong(stat, "danmaku");
                        like = GetLong(stat, "like");
                    }
                    string? author = null;
                    long ownerMid = 0;
                    if (e.TryGetProperty("owner", out var owner))
                    {
                        author = GetStr(owner, "name");
                        ownerMid = GetLong(owner, "mid");
                    }
                    // 空间投稿: owner 没给 mid 时就用当前空间主(这条列表本来就是他的)
                    if (ownerMid <= 0) ownerMid = mid;
                    items.Add(new VideoItem
                    {
                        Bvid = bvid,
                        Title = GetStr(e, "title"),
                        Cover = UrlUtil.Normalize(GetStr(e, "pic")),
                        Author = author ?? "",
                        OwnerMid = ownerMid,
                        Duration = VideoItem.FormatSeconds(GetInt(e, "duration")),
                        ViewCount = view,
                        DanmakuCount = danmaku,
                        LikeCount = like,
                        Pubdate = GetLong(e, "pubdate")
                    });
                }
            }
            return (true, null, items);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    /// <summary>
    /// 获取视频弹幕(protobuf 段接口)。
    ///
    /// 为什么换成这个接口: 旧实现走的 /x/v1/dm/list.so 早已被 B 站废弃,
    /// 多数视频返回 404 或空 XML, 属于"代码在跑但永远没数据"。
    /// 现行方案是 /x/v2/dm/web/seg.so —— 弹幕按 6 分钟一段(segment)存储,
    /// 每段独立返回一份 protobuf 二进制, 需要按段号依次拉取再合并。
    ///
    /// 拉取策略:
    ///   - 已知视频总时长时只拉需要的前 N 段(6 分钟一段), 避免长视频白拉几十个请求;
    ///   - 各段并发请求(默认 4 并发), 明显快于串行;
    ///   - 单段失败不影响其它段, 尽力而为。
    /// </summary>
    /// <param name="cid">视频 cid</param>
    /// <param name="durationSec">视频总时长(秒), &lt;=0 时按最多 20 段拉</param>
    /// <param name="max">条数上限</param>
    /// <returns>按时间排序的弹幕(时间秒 / 文本 / 模式 / 颜色)</returns>
    public async Task<List<RawDanmaku>>
        GetDanmakuAsync(long cid, int durationSec = 0, int max = 6000)
    {
        var result = new List<RawDanmaku>();

        // 启动硬超时: 单 SocketsHttpHandler 实例共用连接池, 超时设 15s 就够,
        // 但外层再兜一个, 防止某个段卡住时整页干等。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        try
        {
            const int segLen = 360; // B 站弹幕固定 6 分钟一段
            var segCount = durationSec > 0
                ? Math.Max(1, (int)Math.Ceiling(durationSec / (double)segLen))
                : 20;
            segCount = Math.Min(segCount, 120); // 上限保护(12 小时)

            var segments = new List<RawDanmaku>[segCount];
            var sem = new SemaphoreSlim(4); // 并发闸门: 同时最多 4 个段请求

            var tasks = new List<Task>(segCount);
            for (var i = 0; i < segCount; i++)
            {
                var index = i; // 闭包捕获
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        segments[index] = await FetchDanmakuSegmentAsync(cid, index + 1, cts.Token)
                            .ConfigureAwait(false) ?? new List<RawDanmaku>();
                    }
                    catch
                    {
                        segments[index] = new List<RawDanmaku>();
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, cts.Token));
            }

            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch { /* 个别段失败无所谓 */ }

            // 合并所有段并按时间排序
            var merged = new List<RawDanmaku>();
            foreach (var seg in segments)
                if (seg is { Count: > 0 }) merged.AddRange(seg);

            if (merged.Count == 0) return result;
            merged.Sort((a, b) => a.Time.CompareTo(b.Time));

            foreach (var d in merged)
            {
                if (result.Count >= max) break;
                if (string.IsNullOrWhiteSpace(d.Text)) continue;
                result.Add(d);
            }
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
        return result;
    }

    /// <summary>拉取单个弹幕段并解析为 RawDanmaku 列表(失败返回 null)</summary>
    private async Task<List<RawDanmaku>?> FetchDanmakuSegmentAsync(
        long cid, int segmentIndex, CancellationToken ct)
    {
        try
        {
            var url = "https://api.bilibili.com/x/v2/dm/web/seg.so" +
                      $"?type=1&oid={cid}&segment_index={segmentIndex}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var cookie = AppendFingerprint(SessionManager.Instance.BuildCookieHeader());
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            // 该时间段没有弹幕时, B 站返回 HTTP 304 + 头 "Bili-Status-Code: -304"
            // (不是 200 带空 body)。这属于正常情况, 返回空列表而不是当失败处理。
            if ((int)resp.StatusCode == 304) return new List<RawDanmaku>();
            if (resp.Headers.TryGetValues("Bili-Status-Code", out var codes) &&
                codes.Any(c => c.Contains("-304")))
                return new List<RawDanmaku>();
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return new List<RawDanmaku>();

            return DanmakuParser.ParseSegReply(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 登录用户信息(nav)。
    ///
    /// ★ 2026-10-01 由裸 <c>Task&lt;UserState?&gt;</c> 改为 (ok, err, data):
    ///   原来"未登录"和"请求失败"都塌成同一个 null, 而这个方法有 5 个调用点,
    ///   其中 MainViewModel.RefreshUserAsync 靠它判断登录态 —— 分不清两者时,
    ///   网络抖一下也会被当成"登录过期", 界面上直接显示"登录状态已过期"。
    /// </summary>
    public async Task<(bool ok, string? err, UserState? data)> GetUserStateAsync()
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/nav", null);
            if (code != 0 || data == null) return (false, msg ?? $"nav 请求失败 (code {code})", null);

            var d = data.Value;
            // 注意: 未登录时 nav 仍返回 code=0, 只是 isLogin=false —— 这是**正常状态**不是错误,
            // 所以 ok=true(查询本身成功), 由调用方看 data.IsLogin 决定怎么显示。
            if (!d.TryGetProperty("isLogin", out var l) || !l.GetBoolean())
                return (true, null, new UserState { IsLogin = false });

            var u = new UserState
            {
                IsLogin = true,
                Name = GetStr(d, "uname"),
                Face = UrlUtil.Normalize(GetStr(d, "face")),
                Mid = GetLong(d, "mid"),
                Level = d.TryGetProperty("level_info", out var li) ? GetInt(li, "current_level") : 0,
                Coins = d.TryGetProperty("money", out var mo) && mo.ValueKind == JsonValueKind.Number
                    ? mo.GetDouble() : 0
            };
            // 经验值(「我的」页显示"经验 x/y"): level_info.current_exp / next_exp
            if (d.TryGetProperty("level_info", out var lev))
            {
                u.ExperienceCurrent = GetLong(lev, "current_exp");
                u.ExperienceNext = GetLong(lev, "next_exp");
            }
            return (true, null, u);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取登录信息失败: " + ex.Message, null);
        }
    }

    /// <summary>
    /// 用户关系数(关注/粉丝)。接口 /x/relation/stat, 匿名即可用(2026-09-26 实测, 与登录态无关)。
    ///
    /// ★ 2026-10-01 去掉 (-1,-1) 哨兵, 改为 (ok, err, following, follower):
    ///   哨兵值的坏处是"真的 0 个粉丝"和"取不到"都是 -1, 调用方必须再自己判一次,
    ///   而且失败原因(B 站风控? 未登录? 网络?)一点都传不出来。现在 ok=false 时 err 说明原因,
    ///   界面再决定显示 "--"。
    /// </summary>
    public async Task<(bool ok, string? err, long following, long follower)> GetRelationStatAsync(long mid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation/stat",
                new Dictionary<string, string> { ["vmid"] = mid.ToString() },
                referer: $"https://space.bilibili.com/{mid}/");
            if (code != 0 || data == null) return (false, msg ?? $"relation/stat 请求失败 (code {code})", 0, 0);
            return (true, null, GetLong(data.Value, "following"), GetLong(data.Value, "follower"));
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取关注/粉丝数失败: " + ex.Message, 0, 0);
        }
    }

    /// <summary>
    /// 当前登录用户的计数三件套: 关注 / 粉丝 / 动态数。
    /// 接口: /x/web-interface/nav/stat(需要登录 Cookie, api.bilibili.com 域)。
    ///
    /// ★ 为什么不再用 api.vc.bilibili.com 的 dynamic_svr/num: 实测(2026-09-26)匿名请求
    /// 被风控页拦(返 HTML), 应用内带 Cookie 也拿不到 → 界面显示 "--"。
    /// nav/stat 一次给齐三个数, 动态数字段是 dynamic_count。
    /// ★ 2026-10-01 去掉 (-1,-1,-1) 哨兵, 改为 (ok, err, ...) —— 同 GetRelationStatAsync。
    /// </summary>
    public async Task<(bool ok, string? err, long following, long follower, long dynamicCount)> GetUserNavStatAsync()
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/nav/stat", null);
            if (code != 0 || data == null) return (false, msg ?? $"nav/stat 请求失败 (code {code})", 0, 0, 0);
            return (true, null,
                GetLong(data.Value, "following"),
                GetLong(data.Value, "follower"),
                GetLong(data.Value, "dynamic_count"));
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "获取用户计数失败: " + ex.Message, 0, 0, 0);
        }
    }

    /// <summary>
    /// 当前登录用户的收藏夹列表(**带封面/隐私字段**, 「我的」页的文件夹卡片用)。
    /// 接口: /x/v3/fav/folder/created/list(分页接口, pn=1/ps=50 一次拿够)。
    /// 与 GetFavFoldersAsync(list-all) 的区别: 那个只取 id/标题/数量(收藏页 chip 够用),
    /// 这个才下发 cover 和 privacy。失败语义与 GetFavFoldersAsync 一致。
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetFavFoldersFullAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["pn"] = "1",
                    ["ps"] = "50",
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<FavFolder>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    // ★ 公开/私密不是 privacy 字段 —— 列表接口里根本没有它, 藏在 attr 位掩码里:
                    //   attr bit0 = 是否默认收藏夹, attr bit1 = 1 表示私密(bilibili-API-collect 收藏夹文档)。
                    //   之前按 privacy 解析永远是 0, 私密夹全显示成"公开"。
                    var attr = GetLong(f, "attr");
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count"),
                        Cover = UrlUtil.Normalize(GetStr(f, "cover")),
                        Privacy = (attr & 2) != 0 ? 1 : 0
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
    /// 搜索(带风控 voucher 降级)。
    ///
    /// ★ 2026-10-03 起支持两类内容: <paramref name="kind"/> 决定 `search_type` 与解析方式。
    ///   · <see cref="ContentKind.Video"/>    → `search_type=video`, 走 <see cref="ParseSearchItem"/>
    ///   · <see cref="ContentKind.Live"/>     → `search_type=live_room`, 走 <see cref="ParseLiveSearchItem"/>
    ///   两者的**分页字段都是同一套**(numResults / pagesize=40), 但直播每页给 40 条而视频给 20 条,
    ///   所以"还有没有下一页"的页大小必须跟着 kind 走(见下面 SearchPageSize)。
    /// </summary>
    public async Task<SearchData> SearchAsync(string keyword, int page, ContentKind kind = ContentKind.Video)
    {
        var result = new SearchData();
        var searchType = kind == ContentKind.Live ? "live_room" : "video";
        // 视频每页 20 条、直播每页 40 条(实测)。这个数只用来推"下一页还有没有", 不参与请求参数。
        var pageSize = kind == ContentKind.Live ? 40 : 20;
        try
        {
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["search_type"] = searchType,
                ["keyword"] = keyword,
                ["page"] = page.ToString()
            };

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/wbi/search/type", ps, sign: true);

            if (code == 0 && data != null)
            {
                if (data.Value.TryGetProperty("result", out var list))
                {
                    AppendSearchItems(result, list, kind);
                    var numResults = data.Value.TryGetProperty("numResults", out var nr) &&
                                     nr.ValueKind == JsonValueKind.Number ? nr.GetInt32() : 0;
                    // ★ 一页的条数是**固定的**(视频 20 / 直播 40), 所以"下一页还有没有"必须拿
                    //   **页大小**去比, 不能拿**本页条数**去比 —— 本页不满时按本页条数乘会把
                    //   偏移算小(明明到底了还说有); 本页 0 条时更是退化成"永远有下一页",
                    //   于是「加载更多」永远不消失、每点一次都去要一个越界页(2026-10-01 修)。
                    result.HasMore = result.Items.Count > 0 &&
                                     (numResults <= 0 ? result.Items.Count >= pageSize
                                                      : page * pageSize < numResults);
                    if (page == 1 && numResults > 0)
                        result.TotalPages = Math.Max(1, (numResults + pageSize - 1) / pageSize);
                    if (result.Items.Count == 0)
                        result.Error = kind == ContentKind.Live ? "没有找到相关直播间" : "没有找到相关视频";
                    result.Ok = true;
                    return result;
                }

                // 风控: 返回 v_voucher, 走 voucher 注册流程
                if (data.Value.TryGetProperty("v_voucher", out var vv) &&
                    vv.ValueKind == JsonValueKind.String)
                {
                    var token = await RegisterVoucherAsync(vv.GetString()!);
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
                            if (result.Items.Count == 0)
                                result.Error = kind == ContentKind.Live ? "没有找到相关直播间" : "没有找到相关视频";
                            return result;
                        }
                    }
                    result.Blocked = true;
                    result.Error = "搜索被风控拦截";
                    return result;
                }
            }

            if (code is -352 or -412 or -403 or -404)
            {
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

    /// <summary>把一段 result 数组按 kind 解析进结果集(视频/直播两套字段)</summary>
    private static void AppendSearchItems(SearchData result, JsonElement list, ContentKind kind)
    {
        foreach (var e in list.EnumerateArray())
        {
            var item = kind == ContentKind.Live ? ParseLiveSearchItem(e) : ParseSearchItem(e);
            if (item != null) result.Items.Add(item);
        }
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

    /// <summary>POST 表单 + 自动加 CSRF / Cookie / Referer / 错误码解析</summary>
    private async Task<(bool ok, string? err)> PostFormAsync(string url, Dictionary<string, string> ps, string? referer = null)
    {
        var (code, msg) = await PostFormCodeAsync(url, ps, referer);
        return code == 0 ? (true, null) : (false, msg);
    }

    /// <summary>
    /// 与 PostFormAsync 相同, 但把业务码原样返回。
    /// 少数接口需要区分具体错误码才能判断"算不算成功" —— 例如收藏的
    /// 11201(已经收藏过了) / 11202(已经取消收藏了)。
    /// </summary>
    private async Task<(int code, string? msg)> PostFormCodeAsync(
        string url, Dictionary<string, string> ps, string? referer = null)
    {
        var csrf = SessionManager.Instance.Current.BiliJct;
        if (string.IsNullOrEmpty(csrf)) return (-101, "未登录");
        ps["csrf"] = csrf;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Cookie",
                AppendFingerprint(SessionManager.Instance.BuildCookieHeader()));
            if (!string.IsNullOrEmpty(referer))
                req.Headers.TryAddWithoutValidation("Referer", referer);
            req.Content = new FormUrlEncodedContent(ps);
            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
            if (code != 0)
            {
                var errMsg = "code " + code;
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    errMsg = m.GetString() ?? errMsg;
                return (code, errMsg);
            }
            return (0, null);
        }
        catch (Exception ex) { return (-999, ex.Message); }
    }

    /// <summary>
    /// 对**评论**点赞 / 取消点赞。
    ///
    /// 接口: `POST /x/v2/reply/action` —— `oid` 是视频 aid、`type=1`(稿件)、`rpid` 是评论 id、
    /// `action` 1 = 赞 / 0 = 取消赞。要带 csrf(bili_jct), 所以必须登录。
    /// 注意它和视频点赞是**两套接口**, 别混: 视频走 `/x/web-interface/archive/like`(like=1/2)。
    ///
    /// ★ **点踩是另一个接口**(`/x/v2/reply/hate`), 见 <see cref="CommentDislikeAsync"/>。
    ///   2026-09-27 修: 之前把踩也当成 action=2 发给这个接口, 服务端直接回
    ///   `12011 不合法的赞或踩`, 表现就是"点踩点了报错"。
    /// </summary>
    public Task<(bool ok, string? err)> CommentLikeAsync(long aid, long rpid, bool like)
        => CommentReactionAsync("https://api.bilibili.com/x/v2/reply/action", aid, rpid, like);

    /// <summary>
    /// 对**评论**点踩 / 取消点踩。
    ///
    /// 接口: `POST /x/v2/reply/hate`, 参数与点赞完全一样, 只是 `action` 的含义变成
    /// 1 = 踩 / 0 = 取消踩。同样要带 csrf, 同样必须登录。
    ///
    /// 赞与踩的服务端行为是**互相覆盖**: 点赞成功会自动消掉踩、点踩成功会自动消掉赞
    /// (这是接口文档写明的副作用, 不是我们本地凑出来的规矩) —— 所以界面上的两个按钮状态
    /// 也必须互斥, 否则会出现"两个都高亮但服务端只认后一个"的假象。
    /// 另外踩**没有计数**: 接口不下发 dislike 总数, 所以踩只有状态、没有数字。
    /// </summary>
    public Task<(bool ok, string? err)> CommentDislikeAsync(long aid, long rpid, bool dislike)
        => CommentReactionAsync("https://api.bilibili.com/x/v2/reply/hate", aid, rpid, dislike);

    /// <summary>赞与踩的公共实现(两个接口的参数、返回值、错误码完全一致, 只有地址不同)</summary>
    private async Task<(bool ok, string? err)> CommentReactionAsync(
        string url, long aid, long rpid, bool on)
    {
        if (aid <= 0 || rpid <= 0) return (false, "参数不完整");
        var (code, msg) = await PostFormCodeAsync(
            url,
            new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = "1",
                ["rpid"] = rpid.ToString(),
                ["action"] = on ? "1" : "0"
            },
            referer: "https://www.bilibili.com/");
        if (code == 0) return (true, null);

        // 这串错误码里有一半是"用户能看懂并且知道该怎么办"的, 直接说人话;
        // 剩下的原样透出服务端 message(带上 code 更好排查)。
        var friendly = code switch
        {
            -101 => "请先登录后再操作",
            -111 => "登录状态已失效, 请重新登录",
            -400 => "评论可能已被删除",
            -404 => "评论不存在或已被删除",
            -509 => "操作太频繁, 稍后再试",
            12002 => "该视频的评论区已关闭",
            12004 => "该评论禁止赞或踩(可能被折叠或已被删除)",
            12006 => "评论不存在或已被删除",
            12009 => "评论区类型不合法(这是客户端 bug, 请反馈)",
            12011 => "服务端不认这个操作(这是客户端 bug, 请反馈)",
            _ => msg
        };
        return (false, friendly);
    }

    /// <summary>
    /// 发表评论, 或**回复楼中楼**。
    ///
    /// 接口: `POST /x/v2/reply/add` —— `oid` 是**视频 aid**(不是 bvid)、`type=1`(稿件)、
    /// `message` 是正文(带换行也行, 表单编码会处理)。要带 csrf(bili_jct), 所以必须登录。
    /// `plat=1` 声明"来自 web 端", 少了它评论会以"未知来源"落库(网页版看不到来源标识)。
    ///
    /// 回复楼中楼时必须**同时**带 `root`(顶层评论 id) 与 `parent`(被回复的那条 id) ——
    /// 实测(2026-09-25 抓包): 只回复楼主时两者相等; 回复楼中楼里的某个人时 root 仍是顶层、
    /// parent 指向那个人。少了 root 会被服务端当成普通评论挂到根上(表现是"回错位置")。
    /// </summary>
    /// <param name="rootRpid">要回复的**顶层评论** id; 0 = 发普通评论(挂在新楼层上)</param>
    /// <param name="parentRpid">被回复的那条 id; 传 0 表示"回复楼主"(自动用 rootRpid)</param>
    public async Task<(bool ok, string? err)> PostCommentAsync(
        long aid, string message, long rootRpid = 0, long parentRpid = 0)
    {
        if (aid <= 0) return (false, "没拿到视频 aid, 暂时不能评论");
        if (string.IsNullOrWhiteSpace(message)) return (false, "评论内容不能为空");
        // B 站单条评论上限 1000 字。界面里也卡了 MaxLength, 这里再兜一次 ——
        // 超长会被服务端整条拒掉, 用户辛辛苦苦写的字就白费了。
        if (message.Length > 1000) message = message[..1000];

        var form = new Dictionary<string, string>
        {
            ["oid"] = aid.ToString(),
            ["type"] = "1",
            ["message"] = message,
            ["plat"] = "1",
            ["ordering"] = "heat"
        };
        if (rootRpid > 0)
        {
            form["root"] = rootRpid.ToString();
            form["parent"] = (parentRpid > 0 ? parentRpid : rootRpid).ToString();
        }

        var (code, msg) = await PostFormCodeAsync(
            "https://api.bilibili.com/x/v2/reply/add", form,
            referer: "https://www.bilibili.com/");
        if (code == 0) return (true, null);

        // 这几个码给用户看得懂的话, 其余原样透出服务端的 message
        var friendly = code switch
        {
            -101 => "请先登录后再发表评论",
            -111 => "登录状态已失效, 请重新登录",
            -400 => "评论内容不合规, 已拒绝发布",
            -403 => "当前账号没有评论权限",
            -412 => "操作太频繁, 请稍后再试",
            -509 => "操作太频繁, 请稍后再试",
            // 实测(空正文探针): 服务端用 12066 表示"内容为空" ——
            // 客户端已经 Trim 过, 正常不会走到这里, 留着是为了不把裸数字抛给用户
            12066 => "评论内容不能为空",
            _ => msg
        };
        return (false, friendly);
    }

    /// <summary>
    /// 查询当前用户是否已点赞该视频(aid)。
    ///
    /// ★ 2026-10-01 从 <c>Task&lt;bool?&gt;</c> 改为 (ok, err, value):
    ///   原来的三态约定是"null = 查询失败", 但**失败原因被丢掉了**, 调用方只能默默
    ///   保持原状, 用户看到的就是"点了没反应"。现在 ok=false 时 err 会说明原因。
    ///   value 仍然只在 ok=true 时有意义。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> HasLikedAsync(long aid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/archive/has/like",
                new Dictionary<string, string> { ["aid"] = aid.ToString() });
            if (code != 0 || data == null) return (false, msg ?? $"查询点赞状态失败 (code {code})", false);
            // data: 1 已赞 / 0 未赞
            return (true, null, data.Value.ValueKind == JsonValueKind.Number && data.Value.GetInt32() == 1);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询点赞状态失败: " + ex.Message, false);
        }
    }

    /// <summary>
    /// 把服务端的风控文案翻译成**用户能照着做**的提示。
    ///
    /// ★ 为什么需要它(2026-10-06 用户报"点赞投币依然报错"):
    ///   账号被风控时服务端回的是 `-403 账号异常,操作失败` / `-401 非法访问` ——
    ///   这两句对用户毫无信息量, 他只会反复点、然后以为程序坏了。
    ///   实测把完整响应打出来后, 真因写在 data.ga_data 里:
    ///       decisions = ["verify_captcha_level2"]   ← 要求二级人机验证
    ///   这是 B 站的**风控门**, 客户端绕不过(实测: 换 Referer/Origin/参数/端点/稿件,
    ///   以及补 b_nut/_uuid/bili_ticket 全部无效)。所以只能把话说清楚, 指引用户去官方
    ///   渠道完成一次验证 —— 而不是把服务端原文丢给用户。
    ///
    /// ★ 只替换**明确是风控**的这几种文案, 其它错误原样透出 —— 不吃掉真实报错。
    /// </summary>
    internal static string FriendlyRiskMessage(string? raw)
    {
        var msg = raw ?? "";
        // 这三句是同一道人机验证门的不同说法(见 v_voucher 文档的 -352 与实测的 -401/-403)
        if (msg.Contains("账号异常", StringComparison.Ordinal) ||
            msg.Contains("非法访问", StringComparison.Ordinal) ||
            msg.Contains("风控", StringComparison.Ordinal))
        {
            return "B 站风控拦截: 请在手机 App 或网页版用同一账号完成一次验证后重试";
        }
        return string.IsNullOrEmpty(msg) ? "未知错误" : msg;
    }

    /// <summary>点赞/取消点赞视频(现行 web 接口)。like=1 点赞, like=2 取消</summary>
    public async Task<(bool ok, string? err)> LikeVideoAsync(string bvid, bool liked)
    {
        var (ok, err) = await PostFormAsync(
            "https://api.bilibili.com/x/web-interface/archive/like",
            new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["like"] = liked ? "1" : "2"
            },
            referer: "https://www.bilibili.com/video/" + bvid);
        return (ok, ok ? err : FriendlyRiskMessage(err));
    }

    /// <summary>投币(现行 web 接口, 需要 aid)。count=1 或 2</summary>
    public async Task<(bool ok, string? err)> CoinVideoAsync(long aid, int count, string bvid)
    {
        var (ok, err) = await PostFormAsync(
            "https://api.bilibili.com/x/web-interface/coin/add",
            new Dictionary<string, string>
            {
                ["aid"] = aid.ToString(),
                ["multiply"] = count.ToString(),
                ["select_like"] = "0"
            },
            referer: "https://www.bilibili.com/video/" + bvid);
        return (ok, ok ? err : FriendlyRiskMessage(err));
    }

    /// <summary>
    /// 查询与指定用户的关系(是否已关注)。同 <see cref="HasLikedAsync"/>: 2026-10-01 由
    /// <c>Task&lt;bool?&gt;</c> 改为 (ok, err, value), 失败时给出原因而不是静默返回 null。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> GetRelationAsync(long mid)
    {
        try
        {
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/relation",
                new Dictionary<string, string> { ["fid"] = mid.ToString() },
                referer: "https://space.bilibili.com/" + mid);
            if (code != 0 || data == null) return (false, msg ?? $"查询关注状态失败 (code {code})", false);
            // data.attribute: 0 未关注, 2 已关注, 3 互关, 6 悄悄关注
            return (true, null,
                data.Value.TryGetProperty("attribute", out var a)
                && a.ValueKind == JsonValueKind.Number && a.GetInt32() > 0);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            return (false, "查询关注状态失败: " + ex.Message, false);
        }
    }

    /// <summary>关注/取消关注 UP 主。act=1 关注, act=2 取消关注</summary>
    public Task<(bool ok, string? err)> FollowUpAsync(long mid, bool follow)
    {
        return PostFormAsync(
            "https://api.bilibili.com/x/relation/modify",
            new Dictionary<string, string>
            {
                ["fid"] = mid.ToString(),
                ["act"] = follow ? "1" : "2",
                ["re_src"] = "11"
            },
            referer: "https://space.bilibili.com/" + mid);
    }

    /// <summary>
    /// 视频评论(返回前 N 条评论, 并带上每条的内联前几条回复)。
    ///
    /// ★ 2026-10-01 由裸 <c>Task&lt;List&lt;CommentItem&gt;&gt;</c> 改为 (ok, err, items):
    ///   原来"接口报错"与"这个视频一条评论都没有"都返回**空列表**, 调用方无法区分,
    ///   于是评论区会显示成"还没有人评论" —— 网络失败被伪装成了正常状态。
    ///
    /// ★ 2026-10-06 增加 <paramref name="mode"/>(评论区「热门 / 时间」切换)与返回的
    ///   <c>total</c>(根评论总数)。两个都来自同一个接口, 不需要额外请求:
    ///     · <c>mode=3</c> 热门 / <c>mode=2</c> 时间 —— 与 B 站网页版同一组参数;
    ///     · 总数在 <c>data.cursor.all_count</c>。
    ///   ★ 为什么要把 total 透出来: 以前调用方拿 <c>list.Count</c> 当总数显示"共 N 条评论",
    ///     而一页只拉 30 条 —— 视频有几千条评论时那句话是**错的**。排序入口就挂在这行文字右边,
    ///     数字不对会让整个入口看着像坏的。
    /// </summary>
    public async Task<(bool ok, string? err, List<CommentItem> items, long total)> GetCommentsAsync(
        long aid, int count = 20, int mode = 3)
    {
        var result = new List<CommentItem>();
        var total = 0L;
        try
        {
            var ps = new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = "1",
                ["mode"] = mode.ToString(),
                ["ps"] = count.ToString(),
                ["pn"] = "1"
            };
            // ★ fingerprint: 按**登录态**决定带不带 Cookie。
            //
            // 实测(2026-09-25, 冷启动单请求, 同一个 aid):
            //   未登录 + 不带任何 Cookie            -> 20 条, 但 reply_control 里**没有 location**
            //                                          (所以评论区一条 IP 属地都看不到)
            //   已登录 + 完整 Cookie(buvid3+buvid4) -> 20 条, 内联楼中楼 34 条,
            //                                          location = "IP属地：广东"
            // 即: IP 属地是**登录态才下发**的字段; 而 2026-09-24 那条"带设备指纹会被降级成
            // 3 条"的结论只对**未登录**成立 —— 登录态下带指纹一样是满 20 条。
            // 于是: 登录了就带(拿得到 IP 属地), 没登录就不带(保住 20 条不被降级)。
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/reply/main", ps,
                fingerprint: SessionManager.Instance.HasLogin);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取评论失败 (code {code})", result, 0);

            // 根评论总数(不受 ps 分页影响)。缺失时保持 0, 由调用方回落到"本页条数"。
            if (data.Value.TryGetProperty("cursor", out var cursor) &&
                cursor.ValueKind == JsonValueKind.Object)
                total = GetLong(cursor, "all_count");

            // 本稿件的 UP 主 mid: 用来给"UP 主自己的评论"打 [UP] 标
            var upperMid = data.Value.TryGetProperty("upper", out var up) &&
                           up.ValueKind == JsonValueKind.Object
                ? GetLong(up, "mid") : 0L;

            // ★★★ 置顶评论(2026-10-03 修"置顶评论不可见"): 置顶那条**不在 replies 里**,
            //   而是在 `data.top.upper`(UP 主置顶)或 `data.top.admin`(管理员置顶)。
            //   只读 replies 就会把它整条丢掉 —— 用户看到的就是"明明有置顶, 却一条都看不到"。
            //   实测(data.top.upper.rpid=315658667873 在 replies 里搜不到)。
            //   ★ 放在 replies **之前**: 置顶本来就该排在最前面。
            //   ★ 两个都可能有值, 但语义上只应展示一条置顶, 所以 upper 优先、admin 兜底。
            if (data.Value.TryGetProperty("top", out var top) && top.ValueKind == JsonValueKind.Object)
            {
                var topEl = TryObject(top, "upper") ?? TryObject(top, "admin");
                if (topEl != null)
                {
                    var pinned = ParseComment(topEl.Value, withReplies: true, upperMid: upperMid);
                    pinned.IsPinned = true;
                    result.Add(pinned);
                }
            }

            if (data.Value.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    result.Add(ParseComment(e, withReplies: true, upperMid: upperMid));
                }
            }
        }
        catch (Exception ex)
        {
            // 不能真的"静默": 解析里任何一处异常都会让后面的评论**整批丢掉**,
            // 表现是"评论只出来前几条", 而排查时一点线索都没有。记到日志里。
            App.ReportError(ex);
            return (false, "解析评论失败: " + ex.Message, result, 0);
        }
        return (true, null, result, total);
    }

    /// <summary>取对象里的某个子对象; 不存在或是 null 时返回 null(置顶评论可能只有 upper 或只有 admin)</summary>
    private static JsonElement? TryObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>
    /// 取某条评论下的**全部**回复(楼中楼)。
    /// 接口: /x/v2/reply/reply —— root 传顶层评论的 rpid, oid 是视频 aid, type=1。
    /// 首页接口 /x/v2/reply/main 只内联前 3 条回复, 要看全就只能走这个接口,
    /// 否则用户会觉得"别人回复的内容看不到"。
    /// </summary>
    public async Task<(bool ok, string? err, List<CommentItem> items)> GetReplyRepliesAsync(long aid, long rootRpid, int pn = 1, int ps = 20)
    {
        var result = new List<CommentItem>();
        try
        {
            if (aid <= 0 || rootRpid <= 0) return (false, "参数不完整", result);
            var ps2 = new Dictionary<string, string>
            {
                ["oid"] = aid.ToString(),
                ["type"] = "1",
                ["root"] = rootRpid.ToString(),
                ["ps"] = ps.ToString(),
                ["pn"] = pn.ToString()
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/reply/reply", ps2);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取楼中楼失败 (code {code})", result);

            // 楼中楼里 UP 主自己的回复同样要打 [UP] 标 —— reply/reply 的 data 里也带 upper
            var upperMid = data.Value.TryGetProperty("upper", out var up) &&
                           up.ValueKind == JsonValueKind.Object
                ? GetLong(up, "mid") : 0L;

            if (data.Value.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    result.Add(ParseComment(e, withReplies: false, upperMid: upperMid));
                }
            }
        }
        catch (Exception ex)
        {
            // 不能真的"静默": 解析里任何一处异常都会让后面的评论**整批丢掉**,
            // 表现是"评论只出来前几条", 而排查时一点线索都没有。记到日志里。
            App.ReportError(ex);
            return (false, "解析评论失败: " + ex.Message, result);
        }
        return (true, null, result);
    }

    /// <summary>
    /// 解析一条评论 JSON。顶层评论与楼中楼回复的字段结构完全一致, 所以共用同一份解析,
    /// 免得两边各写一遍之后字段理解跑偏。
    /// </summary>
    /// <param name="withReplies">是否解析 rcount 与内联的 replies(只有顶层评论才有)</param>
    /// <param name="upperMid">本稿件 UP 主的 mid; 评论者 mid 与它相同就打 [UP] 标。
    /// 0 = 调用方拿不到(或不是从"稿件维度"取的), 此时谁都不打标。</param>
    private static CommentItem ParseComment(JsonElement e, bool withReplies, long upperMid = 0)
    {
        // 当前登录用户对这条评论的操作: 1 = 已赞(0 = 无)。未登录时恒为 0。
        // 2 = 已踩: 只在服务端确实会下发这个值时才成立, 见 CommentItem.IsDisliked 的说明。
        var myAction = GetInt(e, "action");

        var item = new CommentItem
        {
            Rpid = GetLong(e, "rpid"),
            // 评论者 mid: 顶层字段就有, 取不到再从 member.mid 兜一次
            Mid = GetLong(e, "mid"),
            // 被回复的那条评论 id(详情页里"回复 @"指向谁)
            ParentRpid = GetLong(e, "parent"),
            LikeCount = e.TryGetProperty("like", out var l) && l.ValueKind == JsonValueKind.Number
                ? l.GetInt32() : 0,
            IsLiked = myAction == 1,
            IsDisliked = myAction == 2
        };

        if (e.TryGetProperty("member", out var m))
        {
            item.UserName = GetStr(m, "uname");
            item.UserFace = UrlUtil.Normalize(GetStr(m, "avatar"));
            if (item.Mid <= 0) item.Mid = GetLong(m, "mid");
            // 等级徽章。member.level_info.current_level(0~6), 缺了就不显示徽章
            if (m.TryGetProperty("level_info", out var lv) && lv.ValueKind == JsonValueKind.Object)
                item.Level = GetInt(lv, "current_level");
        }

        // UP 标: 评论者就是本稿件 UP 主本人
        item.IsUp = upperMid > 0 && item.Mid == upperMid;

        // IP 属地。接口给的是 `reply_control.location`, 形如 "IP属地：上海";
        // 它**本来就是带前缀的展示串**, 所以直接取用、不再自己拼"IP属地"。
        // 值可能缺失(老接口 / 用户设置里关了展示), 缺了就不显示。
        if (e.TryGetProperty("reply_control", out var rc) && rc.ValueKind == JsonValueKind.Object)
            item.Location = GetStr(rc, "location").Trim();

        // 评论内容在 content.message 嵌套里
        item.Content = e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object
            ? GetStr(c, "message")
            : "";

        // 表情: content.emote 是 { "[短代码]": { url, meta:{size} } } 的字典,
        // key 就是正文里原样出现的短代码(实测 "[打call]"、"鸣潮·共鸣与群星_逼近" 都在 message 里)。
        // 只收正文里真的用到的, 免得白存一堆用不上的地址。
        if (c.ValueKind == JsonValueKind.Object &&
            c.TryGetProperty("emote", out var emote) && emote.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in emote.EnumerateObject())
            {
                if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                if (!item.Content.Contains(kv.Name, StringComparison.Ordinal)) continue;
                var url = UrlUtil.Normalize(GetStr(kv.Value, "url"));
                if (url.Length == 0) url = UrlUtil.Normalize(GetStr(kv.Value, "webp_url"));
                if (url.Length == 0) continue;
                var size = 1;
                if (kv.Value.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    var s = GetInt(meta, "size");
                    if (s > 1) size = s;
                }
                item.Emotes[kv.Name] = new CommentEmote { Url = url, Size = size };
            }
        }

        item.RcTime = e.TryGetProperty("ctime", out var ct) && ct.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ct.GetInt64()).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : "";

        if (withReplies)
        {
            item.ReplyCount = GetInt(e, "rcount");
            // /x/v2/reply/main 会把前 3 条回复直接内联在 replies 里 —— 先渲染出来,
            // 用户不点任何东西就能看见别人的回复内容
            if (e.TryGetProperty("replies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    item.Replies.Add(ParseComment(r, withReplies: false, upperMid: upperMid));
                }
            }
            // 服务端有时返回的 rcount 比实际内联的还少, 兜一下, 否则"展开"按钮会一直挂着
            if (item.ReplyCount < item.Replies.Count) item.ReplyCount = item.Replies.Count;
        }

        return item;
    }
    // ---------------------------------------------------------------- 直播

    /// <summary>
    /// 首页「直播」tab 依次尝试的父分区。
    ///
    /// 0 = 全部(按人气排全站), 先问它; 拿不到再退回娱乐(1) 与游戏(2) 这两个大户。
    /// 只多一两次请求, 比"某个分区被风控/临时为空就整页空白"稳得多。
    /// </summary>
    private static readonly int[] LiveParentAreas = { 0, 1, 2 };

    /// <summary>
    /// 取直播列表(按人气排序)。
    ///
    /// 用 `room/v1/Area/getRoomList`。**不要**换成 `xlive/web-interface/v1/second/getList` ——
    /// 后者现在必返回 `-352`(风控), 同参数用 curl 直打也是 -352, 加 buvid3、加
    /// Origin/Referer、加 web_location 都一样, 属于接口本身不再对第三方开放;
    /// 这个老接口反而一直可用(实测 page_size=20 稳定给满 20 条, 且 online 严格降序)。
    ///
    /// 它的 `data` 是**裸数组**(不是 second/getList 那种 { list: [...] }), 所以解析走两条路。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetLiveListAsync(int page = 1)
    {
        try
        {
            await EnsureBuvidAsync();
            string? lastErr = null;

            foreach (var parentArea in LiveParentAreas)
            {
                var (code, msg, data) = await GetJsonAsync(
                    "https://api.live.bilibili.com/room/v1/Area/getRoomList",
                    new Dictionary<string, string>
                    {
                        ["platform"] = "web",
                        ["parent_area_id"] = parentArea.ToString(),
                        ["cate_id"] = "0",
                        ["area_id"] = "0",
                        ["sort_type"] = "online",
                        ["page"] = page.ToString(),
                        ["page_size"] = "20"
                    },
                    referer: "https://live.bilibili.com/");
                if (code != 0 || data == null)
                {
                    // 错误原文透出去(比吞掉有用): -352 是风控, -412 是被拦, 文字能说明情况
                    lastErr = msg ?? $"请求失败 (code {code})";
                    continue;
                }

                var items = new List<VideoItem>();
                foreach (var e in LiveItemsOf(data.Value))
                {
                    var roomId = GetLong(e, "roomid");
                    if (roomId <= 0) roomId = GetLong(e, "room_id");
                    if (roomId <= 0) continue;

                    // 封面字段各版本不一: cover / system_cover / user_cover / show_cover 都见过
                    var cover = GetStr(e, "cover");
                    if (cover.Length == 0) cover = GetStr(e, "system_cover");
                    if (cover.Length == 0) cover = GetStr(e, "user_cover");
                    if (cover.Length == 0) cover = GetStr(e, "show_cover");
                    if (cover.Length == 0) cover = GetStr(e, "keyframe");

                    var item = new VideoItem
                    {
                        RoomId = roomId,
                        Title = GetStr(e, "title").StripHtml(),
                        Author = GetStr(e, "uname"),
                        // 主播 mid(直播接口里叫 uid): 卡片下方点 UP 主名也能进他的主页
                        OwnerMid = GetLong(e, "uid"),
                        Cover = UrlUtil.Normalize(cover),
                        // 直播间没有时长, 这里放"直播中"当角标, 复用卡片上那个时长位
                        Duration = "直播中",
                        ViewCount = Math.Max(0, GetLong(e, "online"))
                    };
                    items.Add(item);
                }

                if (items.Count > 0) return (true, null, items);
                lastErr = "这个分区现在没有直播";
            }

            return (false, lastErr ?? "没有取到直播列表", null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 直播列表的条目枚举。兼容两种响应形态:
    ///   - 裸数组           (room/v1/Area/getRoomList)
    ///   - { list: [...] }  (xlive/.../second/getList 那一族)
    /// </summary>
    private static IEnumerable<JsonElement> LiveItemsOf(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in data.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) yield return e;
            yield break;
        }
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object) yield return e;
        }
    }

    /// <summary>
    /// 取某个直播间的播放地址(单条可直接喂给 LibVLC 的 URL)。
    ///
    /// 接口: /xlive/web-room/v2/index/getRoomPlayInfo —— 返回的是一棵树:
    ///   data.playurl_info.playurl.stream[]  (按协议分: http_stream / http_hls)
    ///     └ format[]  (按封装分: flv / ts / fmp4)
    ///         └ codec[]  (AVC / HEVC)
    ///             └ url_info[]  → host + base_url 拼出完整地址
    /// 优先选 http_stream + flv: 延迟最低, LibVLC 也最稳; 拿不到再退回列表里第一个。
    /// </summary>
    public async Task<(bool ok, string? err, string? url)> GetLivePlayUrlAsync(long roomId)
    {
        try
        {
            if (roomId <= 0) return (false, "缺少直播间号", null);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo",
                new Dictionary<string, string>
                {
                    ["room_id"] = roomId.ToString(),
                    ["no_playurl"] = "0",
                    ["mask"] = "1",
                    ["qn"] = "0",              // 0 = 让服务端给默认清晰度(原画由登录态决定)
                    ["platform"] = "web",
                    ["protocol"] = "0,1",
                    ["format"] = "0,1,2",
                    ["codec"] = "0,1",
                    ["dolby"] = "5",
                    ["panorama"] = "1"
                },
                referer: $"https://live.bilibili.com/{roomId}");
            if (code != 0 || data == null) return (false, msg ?? $"请求失败 (code {code})", null);

            // live_status: 0 未开播 / 1 直播中 / 2 轮播。没开播的话后面拿不到流, 直接说清楚。
            if (data.Value.TryGetProperty("live_status", out var ls) &&
                ls.ValueKind == JsonValueKind.Number && ls.GetInt32() == 0)
                return (false, "主播还没有开播", null);

            var url = PickLiveUrl(data.Value);
            return url == null ? (false, "没有取到可用的直播流地址", null) : (true, null, url);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>从 getRoomPlayInfo 的响应里挑一个播放地址</summary>
    private static string? PickLiveUrl(JsonElement data)
    {
        if (!data.TryGetProperty("playurl_info", out var pi) || pi.ValueKind != JsonValueKind.Object)
            return null;
        if (!pi.TryGetProperty("playurl", out var pu) || pu.ValueKind != JsonValueKind.Object)
            return null;
        if (!pu.TryGetProperty("stream", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return null;

        string? first = null;
        foreach (var st in streams.EnumerateArray())
        {
            var protocol = GetStr(st, "protocol_name");
            if (!st.TryGetProperty("format", out var formats) || formats.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var f in formats.EnumerateArray())
            {
                var format = GetStr(f, "format_name");
                if (!f.TryGetProperty("codec", out var codecs) || codecs.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var cd in codecs.EnumerateArray())
                {
                    if (!cd.TryGetProperty("url_info", out var infos) ||
                        infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0)
                        continue;

                    var host = GetStr(infos[0], "host");
                    // base_url 自带一串 query(过期时间/签名), 但**必须去掉**:
                    // 真正要保留的是 url_info[0].extra, 它才是配套的参数。直接拼 base_url 的
                    // 原始 query 在多数 CDN 上也能用, 但遇到需要 extra 的节点会 403 —— 统一只取路径。
                    var path = GetStr(cd, "base_url");
                    var q = path.IndexOf('?');
                    if (q >= 0) path = path[..q];
                    if (host.Length == 0 || path.Length == 0) continue;

                    var extra = GetStr(infos[0], "extra");
                    // extra 实测有不带 '?' 的(直接就是 "expires=..."), 直接拼会变成 ".flvexpires=...",
                    // 整条 CDN 全部 403/404 —— 表现就是"视频播放出错, 请检查网络或尝试其他清晰度"。
                    if (extra.Length > 0 && !extra.StartsWith('?')) extra = "?" + extra;
                    var full = host + path + extra;

                    // http_stream(原流) + flv 是最优解: 延迟低、LibVLC 支持最成熟
                    if (protocol == "http_stream" && format == "flv") return full;
                    first ??= full;
                }
            }
        }
        return first;
    }

    // ---------------------------------------------------------------- 评论


    /// <summary>
    /// 私信接口统一的来源页。
    /// 这几个接口在 message.bilibili.com 域名下被调用, 不带对应 Referer 很容易吃 -403,
    /// 所以每处都显式传它。
    /// </summary>
    private const string MsgReferer = "https://message.bilibili.com/";

    private static long ParseMid()
        => long.TryParse(SessionManager.Instance.Current.DedeUserID, out var v) ? v : 0;

    /// <summary>获取私信会话列表(含昵称/头像, 一次请求批量补齐)</summary>
    public async Task<(bool ok, string? err, List<MsgSession>? sessions)> GetMsgSessionsAsync(
        int sessionType = 1, int size = 50)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录后查看", null);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.vc.bilibili.com/session_svr/v1/session_svr/get_sessions",
                new Dictionary<string, string>
                {
                    ["session_type"] = sessionType.ToString(), // 1 = 私聊 + 系统消息
                    ["sort_rule"] = "2",                       // 按活跃时间倒序
                    ["size"] = size.ToString(),
                    ["build"] = "0",
                    ["mobi_app"] = "web"
                },
                referer: MsgReferer);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<MsgSession>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("session_list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in arr.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    var talker = GetLong(s, "talker_id");
                    if (talker <= 0) continue; // 系统会话可能没有 talker_id, 跳过

                    var atTime = GetLong(s, "at_time");
                    list.Add(new MsgSession
                    {
                        TalkerId = talker,
                        UnreadCount = GetInt(s, "unread_count"),
                        AckSeqno = GetLong(s, "ack_seqno"),
                        MaxSeqno = GetLong(s, "max_seqno"),
                        TimeText = atTime > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(atTime).LocalDateTime
                                .ToString("MM-dd HH:mm")
                            : "",
                        Preview = s.TryGetProperty("last_msg", out var lm) ? PreviewOf(lm) : ""
                    });
                }
            }

            await FillSessionProfilesAsync(list);
            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>
    /// 批量补会话的昵称与头像。
    /// 会话列表接口本身不返回用户资料, 得用 /account/v1/user/cards 单独查, 一次最多 50 个 UID。
    /// 失败只是没有名字(会退化成 "UID xxxx"), 不影响会话列表本身可用。
    /// </summary>
    private async Task FillSessionProfilesAsync(List<MsgSession> sessions)
    {
        var ids = sessions.Select(s => s.TalkerId).Distinct().ToList();
        for (var i = 0; i < ids.Count; i += 50)
        {
            var batch = ids.Skip(i).Take(50).ToList();
            try
            {
                var (code, _, data) = await GetJsonAsync(
                    "https://api.vc.bilibili.com/account/v1/user/cards",
                    new Dictionary<string, string> { ["uids"] = string.Join(",", batch) },
                    referer: MsgReferer);
                if (code != 0 || data == null) continue;
                if (data.Value.ValueKind != JsonValueKind.Array) continue;

                var cards = new Dictionary<long, (string name, string face)>();
                foreach (var c in data.Value.EnumerateArray())
                {
                    var mid = GetLong(c, "mid");
                    if (mid > 0)
                        cards[mid] = (GetStr(c, "name"), UrlUtil.Normalize(GetStr(c, "face")));
                }

                foreach (var s in sessions)
                    if (cards.TryGetValue(s.TalkerId, out var v))
                    {
                        s.Name = v.name;
                        s.Face = v.face;
                    }
            }
            catch { /* 单批失败不影响其它批次 */ }
        }
    }

    /// <summary>
    /// 拉取与某个会话的私信记录。
    /// 接口: /svr_sync/v1/svr_sync/fetch_session_msgs (GET, Cookie 认证)。
    /// 返回结果按 msg_seqno 升序排好(旧 → 新), 直接可以正序渲染。
    /// </summary>
    public async Task<(bool ok, string? err, List<MsgItem>? items, bool hasMore)> GetMsgHistoryAsync(
        long talkerId, int size = 30, long endSeqno = 0)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录后查看", null, false);
            if (talkerId <= 0) return (false, "会话无效", null, false);
            var me = ParseMid();
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["sender_device_id"] = "1",
                ["talker_id"] = talkerId.ToString(),
                ["session_type"] = "1",
                ["size"] = size.ToString(),
                ["build"] = "0",
                ["mobi_app"] = "web"
            };
            if (endSeqno > 0) ps["end_seqno"] = endSeqno.ToString();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.vc.bilibili.com/svr_sync/v1/svr_sync/fetch_session_msgs",
                ps,
                referer: MsgReferer);
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, false);

            var list = new List<MsgItem>();
            var hasMore = false;
            if (data.Value.ValueKind == JsonValueKind.Object)
            {
                hasMore = data.Value.TryGetProperty("has_more", out var hm) &&
                          hm.ValueKind == JsonValueKind.True;

                if (data.Value.TryGetProperty("messages", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        var uid = GetLong(m, "sender_uid");
                        var msgType = GetInt(m, "msg_type");
                        var ts = GetLong(m, "timestamp");
                        var content = GetStr(m, "content");

                        var item = new MsgItem
                        {
                            SenderUid = uid,
                            IsMine = uid == me,
                            Seqno = GetLong(m, "msg_seqno"),
                            TimeText = ts > 0
                                ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime
                                    .ToString("MM-dd HH:mm")
                                : ""
                        };

                        if (msgType == 2)
                        {
                            item.ImageUrl = ExtractImageUrl(content);
                            if (item.ImageUrl.Length == 0) item.Text = "[图片]";
                        }
                        else
                        {
                            item.Text = DecodeMsgContent(content);
                            if (item.Text.Length == 0) item.Text = "[暂不支持的消息类型]";
                        }
                        list.Add(item);
                    }
                }
            }

            // 按序号升序(旧 → 新)。不依赖接口返回顺序 —— 那个在新旧方向上没有稳定保证。
            // 序号缺失(0)的消息保持原有相对次序。
            list.Sort((a, b) => a.Seqno.CompareTo(b.Seqno));
            return (true, null, list, hasMore);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, false);
        }
    }

    /// <summary>发送文字私信</summary>
    public async Task<(bool ok, string? err)> SendMsgAsync(long receiverId, string text)
    {
        if (receiverId <= 0) return (false, "缺少接收者");
        if (string.IsNullOrWhiteSpace(text)) return (false, "消息不能为空");
        if (!SessionManager.Instance.HasLogin) return (false, "私信需要登录");

        var me = ParseMid();
        if (me <= 0) return (false, "无法获取自己的 UID");

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        return await PostFormAsync(
            "https://api.vc.bilibili.com/web_im/v1/web_im/send_msg",
            new Dictionary<string, string>
            {
                ["msg[sender_uid]"] = me.ToString(),
                ["msg[receiver_id]"] = receiverId.ToString(),
                ["msg[receiver_type]"] = "1",   // 1 = 用户
                ["msg[msg_type]"] = "1",        // 1 = 文字
                ["msg[msg_status]"] = "0",
                ["msg[content]"] = JsonSerializer.Serialize(new { content = text }),
                ["msg[timestamp]"] = ts,
                ["msg[dev_id]"] = Guid.NewGuid().ToString("N").ToUpperInvariant(),
                // 官方文档写的是 csrf_token, 但别的实现里也用 csrf; 两个都带上最稳
                ["csrf_token"] = SessionManager.Instance.Current.BiliJct ?? "",
                ["build"] = "0",
                ["mobi_app"] = "web"
            },
            referer: MsgReferer);
    }

    /// <summary>把某个会话标记为已读(失败无所谓, 只是未读数不准)</summary>
    public async Task MarkMsgReadAsync(long talkerId, long ackSeqno)
    {
        if (talkerId <= 0 || ackSeqno <= 0) return;
        try
        {
            await PostFormAsync(
                "https://api.vc.bilibili.com/session_svr/v1/session_svr/update_ack",
                new Dictionary<string, string>
                {
                    ["talker_id"] = talkerId.ToString(),
                    ["session_type"] = "1",
                    ["ack_seqno"] = ackSeqno.ToString(),
                    ["build"] = "0",
                    ["mobi_app"] = "web"
                },
                referer: MsgReferer);
        }
        catch { /* 已读回执失败不影响功能 */ }
    }

    /// <summary>会话列表里的 last_msg: 可能是对象, 也可能是被序列化成字符串的 JSON</summary>
    private static string PreviewOf(JsonElement lastMsg)
    {
        if (lastMsg.ValueKind == JsonValueKind.String)
        {
            var raw = lastMsg.GetString() ?? "";
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return PreviewOf(doc.RootElement);
            }
            catch { return ""; }
        }
        if (lastMsg.ValueKind != JsonValueKind.Object) return "";
        var text = DecodeMsgContent(GetStr(lastMsg, "content"));
        if (text.Length > 0) return text;
        return GetInt(lastMsg, "msg_type") == 2 ? "[图片]" : "";
    }

    /// <summary>
    /// 私信的 content 字段是一段 **JSON 字符串**(不是对象), 需要按消息类型再解一层。
    /// 文字是 {"content":"..."}; 分享卡片有 title/text; 图片是 url。
    /// 解不出来时原样返回, 至少不让消息凭空消失。
    /// </summary>
    private static string DecodeMsgContent(string contentJson)
    {
        if (string.IsNullOrEmpty(contentJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return contentJson;
            foreach (var key in new[] { "content", "title", "text" })
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) return s!;
                }
            if (root.TryGetProperty("url", out _)) return "[图片]";
            return "";
        }
        catch { return contentJson; }
    }

    private static string ExtractImageUrl(string contentJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            if (doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                return UrlUtil.Normalize(u.GetString());
        }
        catch { /* 解析失败当普通文本处理 */ }
        return "";
    }

    private async Task<string?> RegisterVoucherAsync(string voucherId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.bilibili.com/x/gaia-vgate/v1/register")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { voucher_id = voucherId }),
                    Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            var cookie = SessionManager.Instance.BuildCookieHeader();
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.GetInt32() == 0 &&
                root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "token", "v_token", "f_token" })
                {
                    if (d.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(t.GetString()))
                        return t.GetString();
                }
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>
    /// 同一档清晰度下, 各编码的优先级(数字越大越优先)。
    ///
    /// hevc 排最前是有实测依据的: 同一档 qn 下 hevc 的码率约为 avc 的一半
    /// (4K 档 7.03 vs 12.60 Mbps), 而 VLC 的硬解对两者一视同仁 —— 同样的画质少搬一半数据,
    /// 起播与跳转的缓冲自然短。
    /// av01 放最后: 体积更小, 但硬解支持面窄(软解 4K AV1 基本跑不动), 不主动选。
    /// </summary>
    private static int CodecRank(string codec)
    {
        if (codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase)) return 3;   // hev1 / hvc1
        if (codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)) return 2;   // avc1
        if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return 1;  // av01
        return 0;
    }

    /// <summary>
    /// 线路选择已迁到 <see cref="CdnService"/>(2026-10-03)。
    ///
    /// 以前这里是两个静态方法(IsPcdnHost / PickFastestHost): 只按"域名里有没有 mcdn"
    /// 换一下顺序。现在换成 CdnService, 因为它要额外负责:
    ///   · 自动测速挑最快(各家 CDN 实测吞吐能差 4 倍)、手动指定、跟随服务端三种策略;
    ///   · 认出**伪装域名**的 PCDN(实测 `mv0bz14m.edge.mountaintoys.cn` 带 os=mcdn,
    ///     只看域名会漏 —— 旧规则的漏洞);
    ///   · 失败回退与测速缓存。
    /// 逻辑集中在一处, 设置页改设置 -> 下次取流生效, 不需要动这里。
    /// </summary>

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.String || v.ValueKind == JsonValueKind.Number)
            ? v.ToString() : "";

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : 0;

    /// <summary>
    /// 获取登录用户的**直播**观看历史。
    ///
    /// ★★★ 接口选型(2026-10-03 实测定的, 别换回 /x/v2/history):
    ///   旧的那条 `/x/v2/history` **只给视频稿件**, 直播条目它根本不返回; 而且 `type=live`
    ///   作为参数传过去会直接 -400(实测)。
    ///   真正管用的是**新游标接口** `/x/web-interface/history/cursor`, 它自带一个 `tab` 数组:
    ///       [{type:"archive",name:"视频"}, {type:"live",name:"直播"}, {type:"article",name:"专栏"}]
    ///   —— 这正是官方历史页那三个标签, 说明官方自己就是靠 `type` 分流的。传 `type=live`
    ///   返回的就是真实直播记录(实测拿到 kid=22603245 / uri=https://live.bilibili.com/22603245)。
    ///   ★ 参数名必须是 **type**。`business=live` / `tab=live` / `business_type=live` 都会被忽略、
    ///     静默返回视频记录(实测), 那种"看起来成功但内容不对"的错最难查。
    ///
    /// 分页: 游标式 —— 用上一页返回的 `cursor.view_at` 与 `cursor.max` 当下一页的
    /// `view_at` / `max`。这里对外仍保持"页码"形状(pn), 内部用保存下来的游标推进,
    /// 免得把游标状态泄漏给页面/VM。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetLiveHistoryAsync(int pn = 1)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "直播历史需要登录", null);
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["type"] = "live",
                ["ps"] = "20"
            };
            // 第 1 页不带游标; 之后用上一次拿到的游标(见 _liveCursor)
            if (pn > 1 && _liveCursorViewAt > 0)
            {
                ps["view_at"] = _liveCursorViewAt.ToString();
                ps["max"] = _liveCursorMax.ToString();
            }

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/web-interface/history/cursor", ps,
                referer: "https://www.bilibili.com/account/history");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in list.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;

                    // 直播记录的"房间号"有两个来源, 都试一遍:
                    //   · 顶层 kid —— 实测就是房间号(22603245);
                    //   · history.oid —— 同为房间号, 顶层缺失时兜底。
                    var roomId = GetLong(h, "kid");
                    if (roomId <= 0 && h.TryGetProperty("history", out var hist))
                        roomId = GetLong(hist, "oid");
                    if (roomId <= 0) continue;

                    var cover = GetStr(h, "cover");
                    if (cover.Length == 0 && h.TryGetProperty("covers", out var covers) &&
                        covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0)
                        cover = covers[0].GetString() ?? "";

                    // 主播名 / mid: 顶层 author_name / author_mid(与视频那条同一组字段名)
                    var author = GetStr(h, "author_name");
                    var ownerMid = GetLong(h, "author_mid");
                    if (h.TryGetProperty("owner", out var owner))
                    {
                        if (author.Length == 0) author = GetStr(owner, "name");
                        if (ownerMid <= 0) ownerMid = GetLong(owner, "mid");
                    }

                    items.Add(new VideoItem
                    {
                        RoomId = roomId,
                        Title = GetStr(h, "title").StripHtml(),
                        Cover = UrlUtil.Normalize(cover),
                        Author = author,
                        OwnerMid = ownerMid,
                        // 时长位显示开播状态: live_status 1=直播中, 0=未开播(接口还给 badge 文案)
                        Duration = GetInt(h, "live_status") == 1 ? "直播中" : "未开播",
                        ViewCount = 0,   // 历史接口不给人气, 留 0 让卡片隐藏那个胶囊
                        Pubdate = GetLong(h, "view_at"),
                    });
                }
            }

            // 记下游标供下一页用。到底时 cursor 会归零(max/view_at 都是 0), 下次自然空手而归。
            if (data.Value.TryGetProperty("cursor", out var cur))
            {
                _liveCursorViewAt = GetLong(cur, "view_at");
                _liveCursorMax = GetLong(cur, "max");
            }
            else
            {
                _liveCursorViewAt = 0;
                _liveCursorMax = 0;
            }

            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    /// <summary>直播历史的游标(接口是游标式, 不是页码式)。由 <see cref="GetLiveHistoryAsync"/> 维护。</summary>
    private long _liveCursorViewAt;
    private long _liveCursorMax;

    /// <summary>重置直播历史游标(重新从头拉之前调, 否则第 2 页会接着上次的位置)</summary>
    public void ResetLiveHistoryCursor()
    {
        _liveCursorViewAt = 0;
        _liveCursorMax = 0;
    }

    /// <summary>获取登录用户的云端观看历史(x/v2/history, 分页)。
    /// 返回 (ok, err, items): items 为 VideoItem 列表, IsSelected 可复用</summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items)> GetCloudHistoryAsync(int pn = 1, int ps = 30)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录", null);
            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/history",
                new Dictionary<string, string>
                {
                    ["pn"] = pn.ToString(),
                    ["ps"] = ps.ToString(),
                    ["platform"] = "web"
                },
                sign: true,
                referer: "https://www.bilibili.com/account/history");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var items = new List<VideoItem>();
            if (data.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in data.Value.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;
                    var bvid = GetStr(h, "bvid");
                    if (string.IsNullOrEmpty(bvid)) continue;
                    // 新旧版字段兼容: pic/cover, author_name/owner.name
                    var cover = GetStr(h, "pic");
                    if (cover.Length == 0 && h.TryGetProperty("covers", out var covers) &&
                        covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0)
                        cover = covers[0].GetString() ?? "";
                    string author = GetStr(h, "author_name");
                    // 历史接口的 UP 主 mid 在顶层 author_mid; 老版结构在 owner.mid
                    long ownerMid = GetLong(h, "author_mid");
                    if (h.TryGetProperty("owner", out var owner))
                    {
                        if (author.Length == 0) author = GetStr(owner, "name");
                        if (ownerMid <= 0) ownerMid = GetLong(owner, "mid");
                    }
                    items.Add(new VideoItem
                    {
                        Bvid = bvid,
                        Aid = GetLong(h, "aid"),
                        Title = GetStr(h, "title").StripHtml(),
                        Cover = UrlUtil.Normalize(cover),
                        Author = author,
                        OwnerMid = ownerMid,
                        Duration = VideoItem.FormatSeconds(GetInt(h, "duration")),
                        // 播放量在 **stat.view** 里, 顶层没有 view 字段。
                        // 之前写成 GetLong(h, "view") 拿到的一直是 0, 于是云端历史每条都显示"0 播放"。
                        // 另外 stat.view 为 -1 表示"播放量被屏蔽", 当成 0 处理而不是显示负数。
                        ViewCount = Math.Max(0, GetNestedLong(h, "stat", "view")),
                        DanmakuCount = Math.Max(0, GetNestedLong(h, "stat", "danmaku")),
                        LikeCount = Math.Max(0, GetNestedLong(h, "stat", "like")),
                        Pubdate = GetLong(h, "view_at")
                    });
                }
            }
            return (true, null, items);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    // ---------------------------------------------------------------- 删除 / 清空

    /// <summary>
    /// 删除**单条**云端观看历史。
    /// 接口: /x/v2/history/delete (POST)。
    ///
    /// 参数 `kid` 不是裸 id, 而是 `{业务类型}_{目标id}`:
    ///   · 视频 → `archive_{avid}`(稿件 avid, 不是 bvid)
    ///   · 直播 → `live_{房间号}`
    /// ★ 前缀写错不会报错, 而是**静默不生效**(接口对未知 kid 也回 code=0, 实测),
    ///   所以视频和直播必须各传对(2026-10-03 加直播历史时踩到这条)。
    /// </summary>
    public async Task<(bool ok, string? err)> DeleteHistoryAsync(long aid, ContentKind kind = ContentKind.Video)
    {
        var prefix = kind == ContentKind.Live ? "live" : "archive";
        if (aid <= 0) return (false, kind == ContentKind.Live ? "缺少直播间号" : "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录");

        return await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/delete",
            new Dictionary<string, string> { ["kid"] = $"{prefix}_{aid}" },
            referer: "https://www.bilibili.com/account/history");
    }

    /// <summary>清空**全部**云端观看历史。接口: /x/v2/history/clear (POST), 只需要 csrf。</summary>
    public async Task<(bool ok, string? err)> ClearHistoryAsync()
    {
        if (!SessionManager.Instance.HasLogin) return (false, "云端历史需要登录");

        return await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/clear",
            new Dictionary<string, string>(),
            referer: "https://www.bilibili.com/account/history");
    }

    /// <summary>
    /// 批量把视频从**指定收藏夹**里移出。
    /// 接口: /x/v3/fav/resource/batch-del (POST, 注意是连字符不是斜杠)。
    ///
    /// `resources` 的格式是 `{内容id}:{内容类型}`, 成员之间用逗号分隔 ——
    /// 内容 id 是**稿件 avid**(不是 bvid, 也不需要 cid), 视频的 type 固定为 2。
    /// 一次请求可以塞多个, 所以"批量移出"不需要挨个调 deal 接口。
    /// </summary>
    public async Task<(bool ok, string? err)> BatchDelFavResourcesAsync(long mediaId, IEnumerable<long> aids)
    {
        if (mediaId <= 0) return (false, "缺少收藏夹 id");
        if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录");

        var res = string.Join(",", aids.Where(x => x > 0).Select(x => $"{x}:2"));
        if (res.Length == 0) return (false, "没有可移出的内容");

        var mid = SessionManager.Instance.Current.DedeUserID;
        return await PostFormAsync(
            "https://api.bilibili.com/x/v3/fav/resource/batch-del",
            new Dictionary<string, string>
            {
                ["resources"] = res,
                ["media_id"] = mediaId.ToString(),
                ["platform"] = "web"
            },
            referer: $"https://space.bilibili.com/{mid}/favlist?fid={mediaId}");
    }

    // ---------------------------------------------------------------- 云端收藏夹

    /// <summary>
    /// 获取当前登录用户的云端收藏夹列表。
    /// 接口: /x/v3/fav/folder/created/list-all —— 一次拿全(不分页), 返回 (ok, err, folders)
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetFavFoldersAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list-all",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<FavFolder>();
            // data.list 是收藏夹数组; 部分账号返回 null 表示没有自建收藏夹
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count")
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
    /// 获取某个云端收藏夹内的视频(分页)。
    /// 接口: /x/v3/fav/resource/list —— 需要 wbi 签名, 未登录时会被风控。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, bool hasMore)>
        GetFavResourcesAsync(long mediaId, int pn = 1, int ps = 20)
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "云端收藏需要登录", null, false);
            await EnsureBuvidAsync();
            var mid = SessionManager.Instance.Current.DedeUserID;

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/resource/list",
                new Dictionary<string, string>
                {
                    ["media_id"] = mediaId.ToString(),
                    ["pn"] = pn.ToString(),
                    ["ps"] = ps.ToString(),
                    ["keyword"] = "",
                    ["order"] = "mtime",   // 按收藏时间倒序
                    ["type"] = "0",
                    ["tid"] = "0",
                    ["platform"] = "web",
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://space.bilibili.com/{mid}/favlist?fid={mediaId}");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, false);

            var items = new List<VideoItem>();
            var hasMore = false;
            if (data.Value.ValueKind == JsonValueKind.Object)
            {
                hasMore = data.Value.TryGetProperty("has_more", out var hm) &&
                          hm.ValueKind == JsonValueKind.True;
                if (data.Value.TryGetProperty("medias", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        // 失效视频的 title 为 "已失效视频", bvid 为空 —— 跳过, 否则点开是空窗
                        var bvid = GetStr(m, "bvid");
                        if (string.IsNullOrEmpty(bvid)) continue;
                        // 播放量藏在 cnt_info.play 里(upper 也可能为空, 用 upper.name 兜底)
                        var author = GetStr(m, "upper");
                        if (author.Length == 0) author = GetUpperName(m);
                        items.Add(new VideoItem
                        {
                            Bvid = bvid,
                            // 收藏夹条目里 aid 字段名是 "id"
                            Aid = GetLong(m, "id"),
                            Title = GetStr(m, "title").StripHtml(),
                            Cover = UrlUtil.Normalize(GetStr(m, "cover")),
                            Author = author,
                            // upper 是对象时拿它的 mid(upper 为纯字符串的老格式则取不到, 保持 0)
                            OwnerMid = GetNestedLong(m, "upper", "mid"),
                            Duration = VideoItem.FormatSeconds(GetInt(m, "duration")),
                            ViewCount = GetNestedLong(m, "cnt_info", "play"),
                            DanmakuCount = GetNestedLong(m, "cnt_info", "danmaku"),
                            Pubdate = GetLong(m, "pubtime")
                        });
                    }
                }
            }
            return (true, null, items, hasMore);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, false);
        }
    }

    /// <summary>
    /// 查询"这个视频被收藏进了哪些收藏夹"。
    ///
    /// 接口必须用 **/x/v3/fav/folder/created/list-all**。
    /// 之前写成 /x/v3/fav/folder/created/list(没有 -all), 那个接口**不接受 rid 参数**,
    /// 带上它只会得到 -400「请求错误」—— 这就是"收藏状态获取失败: 请求错误"的根因。
    /// list-all 加上 rid 后, 每个收藏夹会多一个 fav_state 字段表示该视频是否在里面,
    /// 于是"是否已收藏"和"该往哪个夹放"一次拿到。
    /// </summary>
    public async Task<(bool ok, string? err, List<FavFolder>? folders)> GetVideoFavFoldersAsync(long aid)
    {
        try
        {
            if (aid <= 0) return (false, "缺少视频 aid", null);
            if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录", null);
            var mid = SessionManager.Instance.Current.DedeUserID;
            if (string.IsNullOrEmpty(mid)) return (false, "无法获取用户 mid", null);

            await EnsureBuvidAsync();
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v3/fav/folder/created/list-all",
                new Dictionary<string, string>
                {
                    ["up_mid"] = mid,
                    ["type"] = "2",        // 2 = 视频稿件
                    ["rid"] = aid.ToString(),
                    ["web_location"] = "333.1387"
                },
                sign: true,
                referer: $"https://www.bilibili.com/video/");
            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null);

            var list = new List<FavFolder>();
            if (data.Value.ValueKind == JsonValueKind.Object &&
                data.Value.TryGetProperty("list", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    list.Add(new FavFolder
                    {
                        Id = GetLong(f, "id"),
                        Title = GetStr(f, "title"),
                        MediaCount = GetInt(f, "media_count"),
                        // fav_state 为 1 表示该视频已在此收藏夹中
                        HasVideo = GetInt(f, "fav_state") == 1
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
    /// 收藏 / 取消收藏(现行 Web 端接口, 双端通用)。
    /// 接口: /x/v3/fav/resource/deal
    ///   rid          稿件 avid (**不是 bvid**)
    ///   type         固定 2(视频稿件)
    ///   add_media_ids 要加入的收藏夹 mlid, 多个用逗号分隔
    ///   del_media_ids 要移出的收藏夹 mlid, 多个用逗号分隔
    /// 已知业务码: 11201 已经收藏过了 / 11202 已经取消收藏了 —— 这两种对用户来说都是"目标已达成",
    /// 所以在上层当成成功处理, 不要弹红字报错。
    /// </summary>
    public async Task<(bool ok, string? err)> DealFavoriteAsync(
        long aid, IEnumerable<long>? addIds = null, IEnumerable<long>? delIds = null)
    {
        if (aid <= 0) return (false, "缺少视频 aid");
        if (!SessionManager.Instance.HasLogin) return (false, "收藏需要登录");

        var add = addIds == null ? "" : string.Join(",", addIds.Where(x => x > 0));
        var del = delIds == null ? "" : string.Join(",", delIds.Where(x => x > 0));
        if (add.Length == 0 && del.Length == 0) return (false, "未指定收藏夹");

        var mid = SessionManager.Instance.Current.DedeUserID;
        var ps = new Dictionary<string, string>
        {
            ["rid"] = aid.ToString(),
            ["type"] = "2",
            ["platform"] = "web"
        };
        if (add.Length > 0) ps["add_media_ids"] = add;
        if (del.Length > 0) ps["del_media_ids"] = del;

        var (code, msg) = await PostFormCodeAsync(
            "https://api.bilibili.com/x/v3/fav/resource/deal",
            ps,
            referer: $"https://space.bilibili.com/{mid}/favlist");

        // 11201/11202: "已经收藏过了 / 已经取消收藏了" —— 用户想要的状态本来就成立,
        // 属于幂等成功, 不该弹报错(重复点击时非常容易撞上这两个码)
        if (code is 0 or 11201 or 11202) return (true, null);
        return (false, msg);
    }

    private static string GetUpperName(JsonElement m)
        => m.TryGetProperty("upper", out var u) && u.ValueKind == JsonValueKind.Object
            ? GetStr(u, "name") : "";

    private static long GetNestedLong(JsonElement e, string objName, string propName)
        => e.TryGetProperty(objName, out var o) && o.ValueKind == JsonValueKind.Object
            ? GetLong(o, propName) : 0;

    // ---------------------------------------------------------------- 稍后再看

    /// <summary>
    /// 获取「稍后再看」列表。
    /// 接口: GET /x/v2/history/toview(需要登录 Cookie, 不需要 WBI 签名)。
    ///
    /// 返回形状实测有两种(服务端改过版): `data` 既可能是 `{count, list:[...]}`, 也可能是裸数组 ——
    /// 两种都接住, 免得某次灰度之后这里静默变成"空列表"。
    /// </summary>
    public async Task<(bool ok, string? err, List<VideoItem>? items, int count)> GetWatchLaterAsync()
    {
        try
        {
            if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录", null, 0);
            await EnsureBuvidAsync();

            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/v2/history/toview",
                null,
                sign: false,
                referer: "https://www.bilibili.com/watchlater/list");

            if (code != 0 || data == null)
                return (false, msg ?? $"请求失败 (code {code})", null, 0);

            var count = 0;
            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array)
            {
                arr = data.Value;
                count = arr.GetArrayLength();
            }
            else if (data.Value.TryGetProperty("list", out var lst) && lst.ValueKind == JsonValueKind.Array)
            {
                arr = lst;
                count = GetInt(data.Value, "count");
                if (count <= 0) count = arr.GetArrayLength();
            }
            else
            {
                return (false, "接口返回结构不认识", null, 0);
            }

            var items = new List<VideoItem>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var bvid = GetStr(e, "bvid");
                if (string.IsNullOrEmpty(bvid)) continue;

                var cover = GetStr(e, "pic");
                string author = "";
                long ownerMid = 0;
                if (e.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
                {
                    author = GetStr(owner, "name");
                    ownerMid = GetLong(owner, "mid");
                }
                items.Add(new VideoItem
                {
                    Bvid = bvid,
                    Aid = GetLong(e, "aid"),
                    Title = GetStr(e, "title").StripHtml(),
                    Cover = UrlUtil.Normalize(cover),
                    Author = author,
                    OwnerMid = ownerMid,
                    Duration = VideoItem.FormatSeconds(GetInt(e, "duration")),
                    ViewCount = Math.Max(0, GetNestedLong(e, "stat", "view")),
                    DanmakuCount = Math.Max(0, GetNestedLong(e, "stat", "danmaku")),
                    Pubdate = GetLong(e, "add_at")
                });
            }
            return (true, null, items, count > 0 ? count : items.Count);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, 0);
        }
    }

    /// <summary>
    /// 某条视频是否已在「稍后再看」里。
    ///
    /// 服务端没有"单条查询"的接口, 只能拉整个列表再找 —— 而列表可能有两百多条
    /// (实测某个账号 249 条), 每开一个视频都拉一遍太浪费。所以这里缓存一份 aid 集合,
    /// 60 秒内直接复用; 我们自己增删之后会立刻让缓存失效(见 InvalidateWatchLaterCache)。
    ///
    /// 返回 <c>ok=false</c> = **没查到**(未登录 / 请求失败) —— 调用方不要把 value 当成
    /// "不在列表里", 否则界面会显示一个错误的状态。2026-10-01 由 <c>Task&lt;bool?&gt;</c>
    /// 改为 (ok, err, value), 未登录/查询失败时 err 会说明是哪一种。
    /// </summary>
    public async Task<(bool ok, string? err, bool value)> IsInWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "参数不完整", false);
        if (!SessionManager.Instance.HasLogin) return (false, "未登录", false);

        var set = await GetWatchLaterAidSetAsync();
        if (set == null) return (false, "查询稍后再看失败", false);
        return (true, null, set.Contains(aid));
    }

    private HashSet<long>? _watchLaterAids;
    private DateTime _watchLaterFetchedAt = DateTime.MinValue;
    private readonly object _watchLaterGate = new();

    /// <summary>aid 集合的缓存有效期。列表本身刷新很慢(实测服务端写入后约 2s 才可见), 60 秒足够新。</summary>
    private static readonly TimeSpan WatchLaterCacheTtl = TimeSpan.FromSeconds(60);

    private async Task<HashSet<long>?> GetWatchLaterAidSetAsync()
    {
        lock (_watchLaterGate)
        {
            if (_watchLaterAids != null && DateTime.UtcNow - _watchLaterFetchedAt < WatchLaterCacheTtl)
                return _watchLaterAids;
        }

        var (ok, _, items, _) = await GetWatchLaterAsync();
        if (!ok || items == null) return null;   // 失败不缓存, 也不谎报状态

        var set = new HashSet<long>(items.Where(x => x.Aid > 0).Select(x => x.Aid));
        lock (_watchLaterGate)
        {
            _watchLaterAids = set;
            _watchLaterFetchedAt = DateTime.UtcNow;
        }
        return set;
    }

    /// <summary>本机刚改过稍后再看 —— 让 aid 缓存立刻失效, 否则按钮状态会停在旧值上。</summary>
    private void InvalidateWatchLaterCache()
    {
        lock (_watchLaterGate)
        {
            _watchLaterAids = null;
            _watchLaterFetchedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 加入「稍后再看」。
    /// 接口: POST /x/v2/history/toview/add, 参数只有 `aid`(稿件 avid)+ csrf。
    ///
    /// 幂等: 已经在列表里再调一次仍然返回 code 0(服务端不报错也不重复加),
    /// 所以调用方不需要先查一遍列表。
    /// </summary>
    public async Task<(bool ok, string? err)> AddWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/add",
            new Dictionary<string, string> { ["aid"] = aid.ToString() },
            referer: "https://www.bilibili.com/");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }

    /// <summary>
    /// 从「稍后再看」移除一条。
    /// 接口: POST /x/v2/history/toview/del, 参数 `aid` —— 注意同名的 `viewed=1` 是另一种语义
    /// ("清掉已经看完的"), 这里只做单条移除, 所以永远带 aid。
    /// </summary>
    public async Task<(bool ok, string? err)> RemoveWatchLaterAsync(long aid)
    {
        if (aid <= 0) return (false, "缺少视频 avid");
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/del",
            new Dictionary<string, string> { ["aid"] = aid.ToString() },
            referer: "https://www.bilibili.com/watchlater/list");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }

    /// <summary>
    /// 清空「稍后再看」。接口: POST /x/v2/history/toview/clear(只需要 csrf)。
    /// 服务端不可撤销, 调用方必须先确认。
    /// </summary>
    public async Task<(bool ok, string? err)> ClearWatchLaterAsync()
    {
        if (!SessionManager.Instance.HasLogin) return (false, "稍后再看需要登录");

        var r = await PostFormAsync(
            "https://api.bilibili.com/x/v2/history/toview/clear",
            new Dictionary<string, string>(),
            referer: "https://www.bilibili.com/watchlater/list");
        if (r.ok) InvalidateWatchLaterCache();
        return r;
    }

    // ---------------------------------------------------------------- 视频下载

    /// <summary>
    /// 取"可直接保存为单个 MP4 文件"的播放地址(durl 单流)。
    ///
    /// 为什么下载不用播放那套 DASH 地址:
    /// DASH 是音视频**分离**的两条流, 直接存下来会是"有画面没声音"的残缺文件,
    /// 要合并必须依赖 ffmpeg 做 remux —— 本项目不带 ffmpeg, 引入它会让安装包暴涨几十兆。
    /// durl 单流本身就是一个完整 MP4(音视频已封装在一起), 保存下来就能直接播,
    /// 代价是清晰度上限 720P。对"下载到本地看"这个场景, 能播 >> 高清晰度。
    /// </summary>
    public async Task<(bool ok, string? err, string? url, string? audioUrl)> GetDownloadUrlAsync(
        string bvid, long cid, int qn = 64)
    {
        try
        {
            if (string.IsNullOrEmpty(bvid) || cid <= 0) return (false, "参数不完整", null, null);
            await EnsureBuvidAsync();

            var ps = new Dictionary<string, string>
            {
                ["bvid"] = bvid,
                ["cid"] = cid.ToString(),
                ["qn"] = qn.ToString(),
                ["fnval"] = "1",      // 1 = durl 单流(音视频合一的 MP4)
                ["fnver"] = "0",
                ["fourk"] = "1",
                // ★ 不要带 platform=html5。FetchMuxedAsync 的实测结论(同接口、同 fnval=1):
                //   带上它一律返回 -404"啥都木有"。这里以前偏偏带了, 于是下载走的是一条
                //   已知跑不通的参数组合 —— 表现是"该视频没有可下载的单流地址"或 code -404,
                //   很容易被误判成"付费/大会员专属"。改接口参数前先看上面 FetchMuxedAsync 的说明。
                ["high_quality"] = "1"
            };
            var (code, msg, data) = await GetJsonAsync(
                "https://api.bilibili.com/x/player/wbi/playurl", ps, sign: true);
            if (code != 0 || data == null)
                return (false, msg ?? $"获取下载地址失败 (code {code})", null, null);

            if (data.Value.TryGetProperty("durl", out var durl) &&
                durl.ValueKind == JsonValueKind.Array && durl.GetArrayLength() > 0)
            {
                var url = GetStr(durl[0], "url");
                if (!string.IsNullOrEmpty(url)) return (true, null, url, null);
            }
            return (false, "该视频没有可下载的单流地址(可能是付费/大会员专属)", null, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, null);
        }
    }

    // ---------------------------------------------------------------- 观看进度上报

    /// <summary>
    /// 上报观看进度到 B 站云端历史。
    ///
    /// 接口: /x/v2/history/report (POST 表单, 需 csrf)
    /// 参数: aid / cid / progress(秒) / type=3(投稿视频)
    ///
    /// 调用时机由播放器控制: 起播后节流上报 + 暂停/关闭时补报一次,
    /// 这样"用 BiliDesk 看过的视频"会出现在 App 与网页端的观看记录里。
    /// 未登录直接返回 ok=false, 不产生任何网络请求。
    ///
    /// ★ 2026-10-01 由 <c>Task&lt;bool&gt;</c> 改为 (ok, err): 上报是 fire-and-forget,
    ///   失败**绝不能影响播放**, 但原因仍要带出来 —— 以前"没登录"和"网络失败"都是同一个
    ///   false, 排查"我的视频没进观看记录"时无从下手。
    /// </summary>
    public async Task<(bool ok, string? err)> ReportHistoryAsync(long aid, long cid, long progressSeconds)
    {
        if (!SessionManager.Instance.HasLogin) return (false, "未登录");
        if (aid <= 0 || cid <= 0) return (false, "参数不完整");

        try
        {
            await EnsureBuvidAsync();
            var (ok, err) = await PostFormAsync(
                "https://api.bilibili.com/x/v2/history/report",
                new Dictionary<string, string>
                {
                    ["aid"] = aid.ToString(),
                    ["cid"] = cid.ToString(),
                    ["progress"] = Math.Max(0, progressSeconds).ToString(),
                    ["type"] = "3",
                    ["csrf"] = SessionManager.Instance.Current.BiliJct ?? ""
                },
                referer: "https://www.bilibili.com/");
            return (ok, err);
        }
        catch (Exception ex)
        {
            // 上报失败绝不能影响播放, 但记一笔便于排查
            App.ReportError(ex);
            return (false, "上报失败: " + ex.Message);
        }
    }
}

/// <summary>字符串工具扩展</summary>
public static class StringExtensions
{
    /// <summary>去除搜索标题里的高亮标签</summary>
    public static string StripHtml(this string s)
        => string.IsNullOrEmpty(s)
            ? s
            : s.Replace("<em class=\"keyword\">", "").Replace("</em>", "");
}
