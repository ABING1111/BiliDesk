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

// ★★★ 本类原有 4860 行单文件, 2026-11-07 拆成 17 个 partial 分片(**纯移动、零逻辑改动**)。
//   动机: 单文件已经大到"方法列表都要靠工具翻"的程度, 按业务域切片后:
//     · 改某个功能只需要打开对应分片 —— 评论在 Comment.cs, 弹幕在 Danmaku.cs, 取流在 Playurl.cs;
//     · partial 只是同一个类在编译期的拼接, 运行时行为与 private 互访能力和拆分前完全一致;
//     · 每个分片保留原文件的分区注释与全部历史说明, git blame 用 --follow 或逐分片查。
//   分片清单(按业务域):
//     Core        HttpClient / 基础请求(GetJsonAsync) / WBI 签名 / 设备指纹(buvid) / 风控预热
//     Feed        热门 / 分区 / 排行榜 / 推荐 / 关注列表 / 视频详情
//     Season      视频合集(ugc_season)与相关视频
//     Playurl     取流(DASH/durl) / 音轨候选 / CDN 测速样本 / 清晰度名
//     Space       UP 主空间信息与投稿列表(含风控降级)
//     Danmaku     弹幕(protobuf 段接口)
//     User        登录态(nav) / 关系数 / 空间统计
//     Favorite    收藏夹列表(完整字段版, 「我的」页用)
//     Search      搜索(风控 voucher 降级链)与热搜
//     Interact    写操作: 点赞 / 投币 / 收藏 / 关注(POST + CSRF)
//     Comment     评论区: 读取 / 点赞点踩 / 回复
//     Live        直播: 列表 / 关注直播 / 播放地址
//     Message     私信: 会话 / 历史 / 发送 / 已读
//     Utils       JSON 解析辅助(GetStr/GetInt/GetLong 及宽松变体)
//     History     云端观看历史(视频 + 直播游标分页)
//     FavAndLater 删除/清空历史 + 收藏夹操作 + 稍后再看(含列表缓存)
//     Download    视频下载地址 / 观看进度上报 / 番剧追番(PGC) / StripHtml 扩展

/// <summary>
/// 哔哩哔哩 API 客户端: 带浏览器 UA、Cookie、WBI 签名与风控降级逻辑。
/// </summary>
public partial class ApiClient
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

    /// <summary>上一次 GetJsonAsync 解析 JSON 失败时的原始响应体(见 -2 分支)。</summary>
    private string? _lastNonJsonBody;

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
        string? referer = null, bool fingerprint = true, string payloadKey = "data",
        int riskRetries = RiskRetryMax)
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

        // 在 try 外声明: JSON 解析失败时 catch 里要靠它判断是不是 HTML 风控页
        string text = "";
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
            text = await resp.Content.ReadAsStringAsync();
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
                //
                // ★ payloadKey: 绝大多数接口把载荷放在根级 "data" 下, 但 **PGC 那套不是** ——
                //   pgc/view/web/season 直接把内容放在根级 "result" 下, 压根没有 "data"。
                //   对它照常取 "data" 会拿到 null, 而接口明明返回 code=0 / message=success,
                //   表现就是"明明调通了却说什么都没拿到", 极难往接口本身头上想。
                //   调用方按接口的形状传 "result" 即可(见 GetBangumiDetailAsync)。
                data = root.TryGetProperty(payloadKey, out var d) &&
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
                    referer: referer, fingerprint: fingerprint, payloadKey: payloadKey);
            }

            // ★★★ -412「request was banned」= **IP 级限流**, 等一下重试即可(2026-10-17 修)
            //
            //   实测结论(探针 bd-probe-412 / cookie / cookie2 / conn, 四轮对照互相印证):
            //   ① 同一个 URL, 上一秒 code=0、这一秒 -412, **完全不带 cookie 也一样**;
            //   ② 换一组全新 buvid 仍被拦, 而同一时刻另一组又能过;
            //   ③ 完全相同的请求连打 12 次, **code=0 与 -412 交替出现**;
            //   ④ 等 45~180 秒后完全恢复(10 次连打全 code=0)。
            //   ⇒ 它既不是"免登录 1080P"那组参数的问题(playurl 根本还没走到,
            //     未登录取**详情**就已经被拦), 也不是本地指纹被标记 —— **是 IP 被限流**。
            //   ⇒ 唯一有效的处理是**退避后重试**; 换指纹只会白白多打一次请求。
            //
            //   重试策略: **退避重试两次**(1.5s → 3s)。实测(探针 bd-probe-conn)这个限流是
            //   "隔一阵就随机过、隔一阵又随机不过": 同一 IP、同一个 URL、完全不带 cookie,
            //   连打 12 次里出现过 code=0 与 -412 **交替**。⇒ 一次退避不一定够,
            //   而多试几次也不会把请求堆上去(总共只多 2 次, 且都带等待)。
            if (riskRetries > 0 && code == RiskRateLimitedCode)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Api] {url} 命中 -412(IP 限流), 退避 {RiskBackoffMs * riskRetries}ms 后重试");
                await Task.Delay(RiskBackoffMs * riskRetries);
                return await GetJsonAsync(url, ps, sign: sign, allowRetry: allowRetry,
                    referer: referer, fingerprint: fingerprint, payloadKey: payloadKey,
                    riskRetries: riskRetries - 1);
            }

            // 退避重试之后**仍然是** -412: 说明限流窗口比我们退避的那一下更长。
            // 给一句能看懂的话, 别把 "request was banned" 这种裸英文甩给用户 ——
            // 它看起来像"账号被封了", 而实际只是"这个 IP 暂时请求过密"。
            if (code == RiskRateLimitedCode)
                return (code, "当前网络请求过于频繁(B站限流), 请稍候再试", data);

            return (code, msg, data);
        }
        catch (HttpRequestException ex)
        {
            return (-1, "网络请求失败: " + ex.Message, null);
        }
        catch (JsonException)
        {
            // ★ 留下原始 body: 偶尔服务端回的不是 JSON 而是 HTML 风控页(HTTP 200),
            //   调用方需要看它长什么样才能区分"接口坏了"和"被风控了"(见 IsHtmlRiskPage)。
            _lastNonJsonBody = text;
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

    // ---------------------------------------------------------------- 写操作风控预热

    /// <summary>
    /// 从 Referer 推导 Origin。web 端每个 POST 都带它, 且必须与 Referer 同源。
    /// Referer 是页面地址(带路径), Origin 只要 origin 部分。
    /// </summary>
    private static string OriginFor(string? referer)
    {
        if (string.IsNullOrEmpty(referer)) return "https://www.bilibili.com";
        var i = referer.IndexOf("://", StringComparison.Ordinal);
        if (i < 0) return "https://www.bilibili.com";
        var rest = referer[(i + 3)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? referer : referer[..(i + 3 + slash)];
    }

    /// <summary>写操作预热是否已经做过(每进程一次, 失败不重试以免拖慢用户点击)</summary>
    private bool _writeReady;

    /// <summary>
    /// B 站的 IP 级限流码: <c>-412 request was banned</c>(2026-10-17 实测钉死)
    ///
    /// ★ 它**不是**账号/设备被封: 实测同一 IP 上"这一秒 -412、下一秒 code=0",
    ///   且**完全不带 cookie** 也照样被拦 ⇒ 与 buvid、登录态、参数都无关。
    ///   表现形式是短时间内请求过密, 退避一下就好(见 GetJsonAsync 里那处重试)。
    /// </summary>
    private const int RiskRateLimitedCode = -412;

    /// <summary>命中 -412 后的退避基数(毫秒)。第 n 次重试等待 n × 该值, 形成 1.5s / 3s。</summary>
    private const int RiskBackoffMs = 1500;

    /// <summary>-412 最多重试几次(见 GetJsonAsync 里那处的实测依据)</summary>
    private const int RiskRetryMax = 2;

    /// <summary>
    /// 清掉内存里的设备指纹缓存(下次 <see cref="EnsureBuvidAsync"/> 会重新领并落盘)。
    ///
    /// ★★ 为什么需要它(2026-10-12 16:33 实测, 用户报"退出登录再登进去风控又出现"):
    ///   <see cref="EnsureBuvidAsync"/> 第一句是"内存里这一对齐全就直接返回" ——
    ///   这是为了"一台设备的身份稳定"(见该方法注释)。但退出登录会删掉 session.json,
    ///   而这个单例的 `_buvid3/_buvid4` **不会**跟着清 ⇒ 重新登录后它直接短路,
    ///   既不重新领也不落盘 ⇒ 磁盘上 buvid 是空的, 写接口报风控。
    ///   由 <see cref="SessionManager.Clear"/> 调用。
    /// </summary>
    public void ResetBuvidCache()
    {
        _buvid3 = null;
        _buvid4 = null;
    }

    /// <summary>
    /// 供启动时调用的写操作预热(见 <see cref="EnsureWriteReadyAsync"/>)。
    ///
    /// ★ 为什么要单独暴露: 预热放在"用户第一次点赞"那一刻的话, 那一次点击要多等
    ///   一个网络往返, 体感就是"点了没反应"。启动 1.5 秒后静默做掉, 用户无感。
    /// </summary>
    public Task WarmUpWriteAsync() => EnsureWriteReadyAsync();

    /// <summary>
    /// 当前屏幕分辨率(上报设备指纹用)。取不到就给一个常见的桌面值 ——
    /// 这个字段服务端只校验存在性, 不影响任何判断, 所以失败不需要重试。
    /// </summary>
    private static string ScreenRes()
    {
        try
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
            return bounds is { } b ? $"{b.Width}x{b.Height}" : "1920x1080";
        }
        catch
        {
            return "1920x1080";
        }
    }

    /// <summary>
    /// 写操作(点赞/投币/收藏/三连)发出前的最后一道风控预热。
    ///
    /// ★★ 为什么需要它(2026-10-12 用户报"冷门视频点赞报 -352 风控现在依然存在"):
    ///   B 站的写接口有**三层**设备身份校验, 缺任何一层都可能 -352:
    ///     1) buvid3/buvid4 —— 光"有"不够, 还必须 POST 一次
    ///        `/x/internal/gaia-gateway/ExClimbWuzhi` 才算"激活";
    ///        未激活的指纹服务端视为无效, 读接口照过、写接口判风控。
    ///     2) b_nut / sid / buvid_fp / b_lsid —— 登录会话特征(2026-11-12 已补到 Cookie 头)。
    ///     3) Origin 头 —— web 端 POST 都带, 缺了会被判成非浏览器客户端。
    ///
    /// ★ 实测基线(探针 bd-probe-like2/3/4/5, 全程未改用户点赞状态):
    ///   · 对本人冷门投稿(播放 7762、赞 0)打 like=1 → **成功**(65006/0),
    ///     说明"当前账号 + 当前 Cookie + 当前代码路径"本身是通的;
    ///   · 补 b_nut/b_lsid/buvid_fp/dm_img/Origin 与不补, 返回码完全一样
    ///     ⇒ 这几项在**探针环境**下不是必要条件;
    ///   · 评论点赞 /x/v2/reply/action 同样全放行(不存在的 rpid 也返 0)。
    ///
    /// ★★ 当时据此推断"根因是冷启动未激活指纹"——**那个推断是错的**, 别沿用。
    ///   真根因(2026-10-12 用户对照实验定位): "退出登录 → 重新扫码登录"这条路上
    ///   `EnsureBuvidAsync` 的内存短路导致 buvid 从未落盘(session.json 里是空的),
    ///   见 <see cref="ResetBuvidCache"/> 与 SessionManager.Clear。
    ///   ⇒ 探针查不出问题**不等于**代码没问题: 探针的 ApiClient 是全新实例(内存为空),
    ///     而用户跑的是长驻进程(退出登录不清内存)。**"探针复现不了"本身就是一条线索** ——
    ///     要找"探针与真实进程的差异", 而不是回头怀疑用户或账号。
    ///   预热本身仍有价值(激活未激活的指纹是已知风控面), 保留。
    ///
    /// ★ 实测(2026-10-12, 探针 bd-probe-wuzhi, 只读):
    ///   这个接口返回 `{"code":130212,"message":"130212","data":null}` —— **不是错误**。
    ///   社区里同样如此(bilibili-api issue #691 有人专门问过这个码),
    ///   它是"已收到但不需要响应"的意思, 之后写接口仍可正常放行。
    ///   所以**不要**因为看到非 0 码就以为激活失败而反复重试。
    ///
    /// ★ 每进程只做一次且不阻塞失败: 预热失败就照常发写请求(退回预热前的行为),
    ///   绝不让"防风控"本身变成"不能点赞"。
    /// </summary>
    private async Task EnsureWriteReadyAsync()
    {
        if (_writeReady) return;
        _writeReady = true;   // 先置位: 失败也不重试(避免每次点赞都多打一次预热请求)

        // 第一层: 设备指纹必须存在(读接口已经在用, 通常已经是现成的)
        await EnsureBuvidAsync();

        // 第二层: 激活 buvid。**光拿到 buvid3/buvid4 不算激活** —— 还必须 POST 一次
        //   `/x/internal/gaia-gateway/ExClimbWuzhi` 把设备指纹上报, 服务端才认这个设备。
        //   未激活的指纹: 读接口照过、写接口判风控(这正是"能看不能赞"的根因之一)。
        //
        // ★ 请求形态按社区实测文档(SocialSisterYi/bilibili-API-collect issue #933):
        //     POST https://api.bilibili.com/x/internal/gaia-gateway/ExClimbWuzhi
        //     Content-Type: application/x-www-form-urlencoded
        //     表单字段 payload = 一串 JSON(里面是 WebGL/分辨率/时间戳等设备特征)
        //     Cookie 需要 buvid3 / buvid4 / b_nut
        //   payload 里的编号字段服务端只校验存在性, 用公开常量即可。
        // ★ 不需要登录态, 失败/无响应都不影响主流程。
        try
        {
            if (string.IsNullOrEmpty(_buvid3)) return;

            var cookie = "buvid3=" + _buvid3;
            if (!string.IsNullOrEmpty(_buvid4)) cookie += "; buvid4=" + _buvid4;
            var bNut = SessionManager.Instance.Current.BNut;
            if (!string.IsNullOrEmpty(bNut)) cookie += "; b_nut=" + bNut;

            // payload: 设备特征。分辨率用真实屏幕, 其余用公开常量(服务端只验存在性)。
            //   ★ 用 WinForms 的 Screen 而不是 WPF 的 SystemParameters: ApiClient 不依赖 WPF,
            //     而这是一个可能在非 UI 线程上跑的 Service(探针/后台任务都直接调它)。
            var res = ScreenRes();
            var payload = "{\"3064\":1,\"5062\":\"" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                + "\",\"03bf\":\"https%3A%2F%2Fwww.bilibili.com%2F\",\"39c8\":\"333.1007.fp.risk\""
                + ",\"34f1\":\"\",\"d402\":\"\",\"654a\":\"\",\"6e7c\":\"" + res
                + "\",\"3c43\":{\"2673\":1,\"5766\":24,\"6527\":0,\"7003\":1,\"807e\":1}}";

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.bilibili.com/x/internal/gaia-gateway/ExClimbWuzhi");
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["payload"] = payload
            });
            await _http.SendAsync(req);
        }
        catch
        {
            // 预热失败不阻塞: 见方法注释
        }
    }

}
