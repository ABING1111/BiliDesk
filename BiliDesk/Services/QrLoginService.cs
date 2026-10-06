using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;

namespace BiliDesk.Services;

/// <summary>
/// B站扫码登录(passport 二维码接口), 不再走 WebView2 加载整页登录:
/// generate 拿二维码内容 → UI 渲染成真二维码 → 2 秒轮询 poll → 成功那一下的
/// Set-Cookie 就是登录凭据。流程与官方网页端一致, 窗口里只有一个二维码, 没有页面装饰。
/// </summary>
public sealed class QrLoginService : IDisposable
{
    private const string GenerateUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    private const string PollUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";

    /// <summary>generate 返回的二维码: Url 是要渲染成二维码的内容, Key 用来轮询</summary>
    public sealed record QrCode(string Url, string Key);

    /// <summary>
    /// ★★★ 登录前把**设备指纹**种进登录容器, 让这次发下来的 SESSDATA 与这台设备绑定。
    ///
    /// 为什么必须做(2026-10-06 查"点赞 -403 账号异常 / 投币 -401 非法访问"的回归):
    ///   二维码登录是**直接用空 CookieContainer** 打 passport 接口的, 从头到尾不带任何设备
    ///   指纹。而风控文档明确指出, 判断一个会话是否可信要看 Cookie 里的
    ///   `buvid3` / `buvid4` / `b_nut` / `bili_ticket`(见 bilibili-API-collect
    ///   `docs/misc/sign/v_voucher.md`); 服务端拒绝时回的
    ///   `data.ga_data.decisions = ["verify_captcha_level2"]` 也正是这套判定。
    ///
    ///   旧版登录走 WebView2 加载 `passport.bilibili.com/login`: 页面里 B 站自己的 JS 会先
    ///   下发 buvid3/buvid4/b_nut, 再从 www.bilibili.com 整体读 Cookie(见 WebView2Service.
    ///   ReadBiliCookiesAsync 取的是**全部** Cookie) —— 所以那时**登录请求自带指纹,
    ///   SESSDATA 天然与设备绑定**。合并把登录换成原生二维码(972fd13)时这一层丢了, 于是
    ///   出现"以前能点赞、现在不行"的回归。
    ///
    /// ★ 绑定时机才是关键: 指纹必须在**换 SESSDATA 的那次请求**上, 事后再往请求里补
    ///   buvid 是补不回来的 —— 实测(2026-10-06)在**已经发下来的**会话上把 buvid3/buvid4
    ///   补进 session.json 后再点赞, 仍然 -403。所以修复点必须在登录流程里, 不能靠
    ///   AppendFingerprint 那种"发送时拼接"。
    ///
    /// ★ 优先复用**本地已存**的那一对, 拿不到才去 spi 取新的。
    ///   理由与 ApiClient.EnsureBuvidAsync 完全一致(那边有详细说明): 推荐/风控都靠
    ///   buvid 认"同一台设备", 每次登录都换一对 = 每次登录都变成一台全新设备。
    ///
    /// ★ 为什么用同一个 CookieContainer 而不是另存再拼:
    ///   种进容器后这些 Cookie 会随登录请求一起发出去(请求就"带设备"了), 而且
    ///   <see cref="CollectLoginCookies"/> 会把容器里的东西**整体**交给 SessionManager,
    ///   buvid3/buvid4 自然跟着落盘 —— 不需要再补一条持久化链路。
    ///
    /// ★ 失败不阻断登录: 拿不到指纹顶多退回"没有指纹"的旧行为, 不该让用户登不进来。
    /// </summary>
    private async Task SeedDeviceFingerprintAsync(CancellationToken ct)
    {
        try
        {
            var sess = SessionManager.Instance.Current;

            // 先用本地已存的那一对(稳定设备身份); 缺了才去要新的
            var b3 = sess.Buvid3;
            var b4 = sess.Buvid4;
            if (string.IsNullOrEmpty(b3) || string.IsNullOrEmpty(b4))
            {
                var root = await GetJsonAsync("https://api.bilibili.com/x/frontend/finger/spi", ct);
                if (root != null && root.Value.TryGetProperty("data", out var data))
                {
                    if (string.IsNullOrEmpty(b3))
                        b3 = data.TryGetProperty("b_3", out var v3) && v3.ValueKind == JsonValueKind.String
                            ? v3.GetString() : null;
                    if (string.IsNullOrEmpty(b4))
                        b4 = data.TryGetProperty("b_4", out var v4) && v4.ValueKind == JsonValueKind.String
                            ? v4.GetString() : null;
                    // 新拿到的这一对记进会话, 保证"磁盘上那一份"也是这个设备 ——
                    // 否则下次登录又会去要一对新的, 设备身份永远不稳定。
                    if (!string.IsNullOrEmpty(b3)) SessionManager.Instance.SetBuvid3(b3);
                    if (!string.IsNullOrEmpty(b4)) SessionManager.Instance.SetBuvid4(b4);
                }
            }

            // Domain 必须写 .bilibili.com: 登录在 passport 主机、后续业务在 api 主机,
            // 只有父域 Cookie 才会被两边都带上(与浏览器行为一致)。
            var host = new Uri("https://www.bilibili.com/");
            void Add(string name, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                try
                {
                    _cookies.Add(host, new Cookie(name, value, "/", ".bilibili.com"));
                }
                catch { /* 值里有非法字符时跳过, 不影响登录 */ }
            }
            Add("buvid3", b3);
            Add("buvid4", b4);
            // b_nut 是"设备首次访问时间戳", 风控文档把它和 buvid 并列点名。浏览器由 B 站 JS 设。
            // 本地没有这一项的概念, 用当前秒(它表达的就是"这台设备第一次来的时间")。
            Add("b_nut", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        }
        catch
        {
            // 见上面的"失败不阻断登录"
        }
    }

    public enum PollStatus { Waiting, Scanned, Expired, Success, Error }

    /// <summary>轮询结果。Success 时 Cookies 非空(可直接交 SessionManager.UpdateFromCookies)</summary>
    public sealed record PollResult(PollStatus Status, IReadOnlyDictionary<string, string>? Cookies = null, string? Message = null);

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();

    public QrLoginService()
    {
        // ★ 独立的 HttpClient + CookieContainer, 不能复用 ApiClient 那个:
        //   登录凭据是 poll 成功响应的 Set-Cookie 下发的, 而 ApiClient 是"手动拼 Cookie 头"
        //   的用法(handler 没挂容器), 那条链路根本拿不到 Set-Cookie; 这里让容器自动收,
        //   登录成功后从容器里取。
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = _cookies,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", HttpDefaults.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://passport.bilibili.com/");
    }

    /// <summary>获取新二维码。失败返回 null(调用方展示重试入口)</summary>
    public async Task<QrCode?> GenerateAsync(CancellationToken ct)
    {
        // ★ 先把设备指纹种进容器(见 SeedDeviceFingerprintAsync 的说明) ——
        //   必须赶在轮询成功之前, 这样 Set-Cookie 下来的 SESSDATA 就是"这台设备"的。
        await SeedDeviceFingerprintAsync(ct);

        var root = await GetJsonAsync(GenerateUrl, ct);
        if (root == null || !root.Value.TryGetProperty("data", out var data)) return null;
        var url = data.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var key = data.TryGetProperty("qrcode_key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return null;
        return new QrCode(url!, key!);
    }

    /// <summary>轮询一次扫码状态。data.code: 86101 未扫 / 86090 已扫未确认 / 86038 过期 / 0 成功</summary>
    public async Task<PollResult> PollAsync(string key, CancellationToken ct)
    {
        try
        {
            var root = await GetJsonAsync($"{PollUrl}?qrcode_key={Uri.EscapeDataString(key)}", ct);
            if (root == null || !root.Value.TryGetProperty("data", out var data))
                return new PollResult(PollStatus.Error, Message: "响应解析失败");
            var code = data.TryGetProperty("code", out var c) ? c.GetInt32() : -999;
            return code switch
            {
                0 => new PollResult(PollStatus.Success, CollectLoginCookies()),
                86090 => new PollResult(PollStatus.Scanned),
                86038 => new PollResult(PollStatus.Expired),
                86101 => new PollResult(PollStatus.Waiting),
                _ => new PollResult(PollStatus.Error, Message: $"扫码状态码 {code}")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PollResult(PollStatus.Error, Message: ex.Message);
        }
    }

    /// <summary>成功响应的 Set-Cookie 已由容器收下, 从里面捞出会话关心的那几样</summary>
    private IReadOnlyDictionary<string, string> CollectLoginCookies()
    {
        var found = new Dictionary<string, string>();
        // 登录 Cookie(SESSDATA 等)是 Domain=.bilibili.com 的, 问 passport 主机即可全量取出
        foreach (Cookie c in _cookies.GetCookies(new Uri("https://passport.bilibili.com/")))
        {
            found[c.Name] = c.Value;
        }
        return found;
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        // Clone: 脱离 JsonDocument 生命周期, 否则返回的是悬空引用
        return doc.RootElement.Clone();
    }

    public void Dispose() => _http.Dispose();
}
