using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BiliDesk.Helpers;

namespace BiliDesk.Services;

/// <summary>扫码登录的状态(取值来自接口里的 data.code)</summary>
public enum QrLoginState
{
    /// <summary>86101: 还没人扫</summary>
    Waiting,
    /// <summary>86090: 手机扫到了, 等确认</summary>
    Scanned,
    /// <summary>86038: 二维码已失效</summary>
    Expired,
    /// <summary>0: 登录成功</summary>
    Success,
    /// <summary>网络失败或未知响应</summary>
    Failed,
}

/// <summary>
/// 登录用的扫码接口(passport)。**故意不放在 ApiClient 里**, 三个理由:
///   1. 登录请求不能带现有会话的 Cookie —— 让 ApiClient 的管线(自动附加 Cookie/指纹)去做,
///      等于拿旧登录态去换新登录态, 语义混乱;
///   2. 成功时的凭据在**响应头** `Set-Cookie` 里, 而 ApiClient 的管线只解析响应体 JSON,
///      响应头被丢掉了(拿不到凭据就只能去解析 data.url 的 query, 少一条路);
///   3. 只有这两个接口, 且都不需要 WBI 签名。
///
/// ★ 为什么密码登录**不走这里**(2026-10-11 实测):
///   `POST /x/passport-login/web/login` 无条件要求极验(geetest)人机验证:
///   带/不带 buvid 指纹、带 captcha 接口换来的 token, 三种组合都只回 `-105 验证码错误`,
///   连用户名都不校验。极验必须在浏览器里由官方页面的 JS 完成, 所以账号密码这条路由
///   LoginWindow 交给官方登录页(见该窗口"密码登录"分支的说明), 这里不做半吊子实现。
/// </summary>
public sealed class PassportService
{
    public static PassportService Instance { get; } = new();

    private const string QrGenerateUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    private const string QrPollUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";

    private readonly HttpClient _http;

    private PassportService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", HttpDefaults.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
    }

    /// <summary>
    /// 申请一张新的二维码: 返回要画进二维码的 url 和后续轮询用的 key。
    /// 这一步不需要任何 Cookie。
    /// </summary>
    public async Task<(bool ok, string? err, string? url, string? key)> CreateQrAsync()
    {
        try
        {
            using var req = BuildRequest(QrGenerateUrl);
            using var resp = await _http.SendAsync(req);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
                return (false, Message(doc.RootElement) ?? "二维码获取失败", null, null);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return (false, "二维码获取失败", null, null);

            var url = data.TryGetProperty("url", out var u) ? u.GetString() : null;
            var key = data.TryGetProperty("qrcode_key", out var k) ? k.GetString() : null;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
                return (false, "二维码获取失败", null, null);

            return (true, null, url, key);
        }
        catch (Exception ex)
        {
            return (false, "网络异常: " + ex.Message, null, null);
        }
    }

    /// <summary>
    /// 轮询二维码状态。成功时把凭据 Cookie 一并带回来(SESSDATA / bili_jct / DedeUserID 等)。
    /// </summary>
    public async Task<(QrLoginState state, string? message, IReadOnlyDictionary<string, string>? cookies)>
        PollQrAsync(string key)
    {
        try
        {
            using var req = BuildRequest(QrPollUrl + "?qrcode_key=" + Uri.EscapeDataString(key));
            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("code", out var topCode) && topCode.GetInt32() != 0)
                return (QrLoginState.Failed, Message(doc.RootElement) ?? "登录失败", null);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return (QrLoginState.Failed, "登录失败", null);

            int inner = data.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            switch (inner)
            {
                case 0:
                {
                    // 凭据两条路都收: 响应头 Set-Cookie 为主, data.url 的 query 兜底。
                    // (官方页面靠 Set-Cookie 落地; 有些网络环境下响应头会被中间层吃掉, 那时还有 url。)
                    var cookies = new Dictionary<string, string>();
                    CollectSetCookies(resp, cookies);
                    if (data.TryGetProperty("url", out var signUrl))
                        CollectUrlQuery(signUrl.GetString(), cookies);
                    return (QrLoginState.Success, null, cookies);
                }
                case 86090: return (QrLoginState.Scanned, null, null);
                case 86038: return (QrLoginState.Expired, null, null);
                case 86101: return (QrLoginState.Waiting, null, null);
                default: return (QrLoginState.Failed, Message(data) ?? $"未知状态({inner})", null);
            }
        }
        catch (Exception ex)
        {
            // 轮询失败按"还在等"处理: 网络抖一下不该把已经掏出来的二维码判死, 下一轮继续。
            return (QrLoginState.Waiting, ex.Message, null);
        }
    }

    // ---------------------------------------------------------------- 内部

    private HttpRequestMessage BuildRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        // 带上设备指纹(buvid3/buvid4)但不带登录态: 官方页面此时也是这么发的。
        var cookie = new List<string>();
        var sess = SessionManager.Instance.Current;
        if (!string.IsNullOrEmpty(sess.Buvid3)) cookie.Add("buvid3=" + sess.Buvid3);
        if (!string.IsNullOrEmpty(sess.Buvid4)) cookie.Add("buvid4=" + sess.Buvid4);
        if (cookie.Count > 0)
            req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookie));
        req.Headers.TryAddWithoutValidation("Referer", "https://passport.bilibili.com/login");
        return req;
    }

    private static string? Message(JsonElement obj)
        => obj.TryGetProperty("message", out var m) ? m.GetString() : null;

    /// <summary>把 Set-Cookie 拆成 名字 → 值(只取第一个分号之前的键值对)</summary>
    private static void CollectSetCookies(HttpResponseMessage resp, Dictionary<string, string> into)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var raw in values)
        {
            int semi = raw.IndexOf(';');
            var pair = semi >= 0 ? raw[..semi] : raw;
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq].Trim();
            if (name.Length == 0) continue;
            into[name] = pair[(eq + 1)..].Trim();
        }
    }

    /// <summary>从 data.url 的 query 里捡凭据(登录成功时服务端会把它们拼在跳转链接上)</summary>
    private static void CollectUrlQuery(string? url, Dictionary<string, string> into)
    {
        if (string.IsNullOrEmpty(url)) return;
        int q = url.IndexOf('?');
        if (q < 0) return;

        foreach (var pair in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq];
            var value = pair[(eq + 1)..];
            try { value = Uri.UnescapeDataString(value); } catch { /* 原样保留 */ }
            if (string.IsNullOrEmpty(into.GetValueOrDefault(name))) into[name] = value;
        }
    }
}
