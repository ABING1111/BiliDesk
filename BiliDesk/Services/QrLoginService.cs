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
