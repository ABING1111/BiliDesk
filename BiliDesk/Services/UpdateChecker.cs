using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BiliDesk.Helpers;

namespace BiliDesk.Services;

/// <summary>
/// 一次检查更新的结果。没发现新版本时调用方拿到 null(见 <see cref="UpdateChecker.CheckAsync"/>)。
/// </summary>
public sealed class UpdateInfo
{
    /// <summary>最新版本号(tag 名去掉前缀 v/V 后解析, 解析失败的 tag 一律视为"没有新版本")</summary>
    public Version Version { get; init; } = new(0, 0, 0);

    /// <summary>Release 标题(没有就回落到 tag 名)</summary>
    public string Title { get; init; } = "";

    /// <summary>Release 说明(即发布说明正文, 弹窗里渲染给用户的那段 Markdown)</summary>
    public string Notes { get; init; } = "";

    /// <summary>该 Release 的网页地址(交给系统浏览器打开, 程序内不做大文件下载)</summary>
    public string ReleaseUrl { get; init; } = "";

    /// <summary>发布时间(Release 的 published_at; 拿不到时为 null)</summary>
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>检查失败的原因分类 —— 决定失败提示窗的文案与"值不值得重试"。</summary>
public enum UpdateCheckFailureKind
{
    /// <summary>连不上(GitHub 被墙 / 代理不通 / DNS 异常 / 网卡没网)</summary>
    Network,

    /// <summary>连上了但超时(线路慢、代理抖动)</summary>
    Timeout,

    /// <summary>GitHub 限流(403 + x-ratelimit-remaining: 0, 或 429) —— 共享 VPN 出口 IP 很常见</summary>
    RateLimited,

    /// <summary>GitHub 返回了 5xx 等异常状态码</summary>
    Http,

    /// <summary>响应拿到了但解析失败(接口格式变化、代理返回了 HTML 页面)</summary>
    Parse,

    /// <summary>非预期异常(兜底, 正常路径不应该出现)</summary>
    Unknown
}

/// <summary>
/// 检查更新失败。带"给用户看的短句"与失败分类 —— 关闭本程序前的旧实现把所有失败都压成
/// 一个静默 catch, 用户完全看不出是 VPN 不通、超时还是 GitHub 限流。
/// </summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(UpdateCheckFailureKind kind, string userMessage,
        int? statusCode = null, Exception? innerException = null)
        : base(userMessage, innerException)
    {
        Kind = kind;
        UserMessage = userMessage;
        StatusCode = statusCode;
    }

    /// <summary>失败分类(决定提示窗文案)</summary>
    public UpdateCheckFailureKind Kind { get; }

    /// <summary>给用户看的短句: 直接用于设置页状态行与 Toast</summary>
    public string UserMessage { get; }

    /// <summary>有 HTTP 状态码时带上(如 403), 否则 null</summary>
    public int? StatusCode { get; }
}

/// <summary>
/// 自动检查更新: 数据源 = GitHub Releases。
///
/// 为什么只"检查 + 跳浏览器", 不做程序内下载安装:
///   GitHub Release 页上同时挂着便携版与安装器, 下哪个、怎么装是用户自己的选择 ——
///   客户端替用户决定升级方式反而多出错面(自更新还要处理进程替换/权限, 与收益不成比例)。
///   所以这里的职责边界很窄: 比版本号 → 有新版就弹窗 → 按钮跳系统浏览器。
///
/// 网络上的独立性: 这不是 B 站接口, **不走 ApiClient**(那里带 WBI/appkey 签名语义和风控
/// 相关的默认值, 混进来只会互相牵连), 单独一个 HttpClient, 失败只影响"检查更新"这一件事。
///
/// ★ 2026-11-21 起失败不再全静默:
///   · 失败按 <see cref="UpdateCheckFailureKind"/> 分类, 自动检查失败时弹提示窗
///     (见 AutoCheckAsync / NotifyFailureAsync), 但 24 小时内最多一次、完全断网不弹;
///   · api.github.com 不通时再用 Releases Atom 备用源试一次 —— Atom 不占匿名 API 限额,
///     对"共享 VPN 出口 IP 被限流"这种最常见的失败尤其有效。
/// ★ 2026-11-28: 支持更新镜像源(设置 → 关于 → 更新镜像源), 默认仍直连 GitHub。
/// </summary>
public static class UpdateChecker
{
    /// <summary>Release 数据所在的仓库(owner/repo)。改名要连着 ReleasePageUrl 一起改。</summary>
    public const string RepoOwner = "ABING1111";
    public const string RepoName = "BiliDesk";

    private const string ReleasePageRawUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>releases/latest 网页地址(弹窗按钮落点); 配置镜像时走镜像。</summary>
    public static string ReleasePageUrl => ApplyMirror(ReleasePageRawUrl);

    /// <summary>仓库首页(设置 → 关于页「GitHub 仓库」的落点)。</summary>
    public const string RepoPageUrl =
        $"https://github.com/{RepoOwner}/{RepoName}";

    /// <summary>Issues 列表(设置 → 关于页「问题反馈」的落点)。</summary>
    public const string IssuesPageUrl =
        $"https://github.com/{RepoOwner}/{RepoName}/issues";

    // ★ 这里曾经有一个"微云高速下载"镜像按钮(WeiYunShareUrl / WeiYunSharePwd 两个常量)。
    //   2026-10-01 用户要求**下载全部走 GitHub**, 两个常量与那个按钮一起删掉了。
    //   现在只做 GitHub 代理转发, 不维护云端副本; 默认直连。

    private static readonly HttpClient Http = new()
    {
        // 检查更新不该拖住任何界面: 10 秒拿不到结果就当这次失败(启动后的自动检查会节流提示)。
        Timeout = TimeSpan.FromSeconds(10)
    };

    static UpdateChecker()
    {
        // GitHub API 强制要求 User-Agent 头(不带直接 403); GitHub 不是 B 站,
        // 沿用 HttpDefaults 的纯 Chrome UA 没有副作用(它本来也只是个普通浏览器 UA)。
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// 把 GitHub URL 套到更新镜像上。mirror 为空/不是 http(s) 地址时原样返回。
    /// 支持前缀("https://gh-proxy.com/")与模板("https://kkgithub.com/{url}")两种写法。
    /// 抽成纯函数: 单测要覆盖空值、无斜杠、模板、非法输入。
    /// </summary>
    public static string MirrorUrl(string? mirror, string url)
    {
        var m = (mirror ?? "").Trim();
        if (m.Length == 0) return url;
        if (!m.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !m.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        if (m.Contains("{url}", StringComparison.Ordinal))
            return m.Replace("{url}", url, StringComparison.Ordinal);
        return m.EndsWith("/", StringComparison.Ordinal) ? m + url : m + "/" + url;
    }

    private static string ApplyMirror(string url) => MirrorUrl(Svc.Settings.UpdateMirror, url);

    /// <summary>最新 Release 的 API 地址(每次现拼, 常量拼接没有循环依赖问题)</summary>
    private static string LatestApiUrl =>
        ApplyMirror($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

    /// <summary>
    /// Releases Atom 备用源。不消耗 GitHub API 的匿名限额(60 次/小时/IP),
    /// 共享出口 IP 的 VPN 用户靠它也能拿到最新 tag。
    /// </summary>
    private static string LatestAtomUrl =>
        ApplyMirror($"https://github.com/{RepoOwner}/{RepoName}/releases.atom");

    /// <summary>
    /// 查一次最新版本并与当前版本比较。
    /// 返回 null 的三种含义要分清: **没有 Release**(仓库还没发过版, 404)、**已是最新**、
    /// **tag 解析失败**(Tag 不是 vX.Y.Z 形状, 宁可不提示也不给用户一个装不上的"新版本")。
    /// 失败抛 <see cref="UpdateCheckException"/>; 网络类失败会先尝试 Atom 备用源。
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        UpdateCheckException primary;
        try
        {
            return await CheckViaApiAsync();
        }
        catch (UpdateCheckException ex)
        {
            primary = ex;
        }

        // 解析失败不换源: 那说明 GitHub API 的响应本身变了, Atom 也未必能修好,
        // 直接让调用方提示"请手动查看发布页"更诚实。
        if (primary.Kind is not (UpdateCheckFailureKind.Network
            or UpdateCheckFailureKind.Timeout
            or UpdateCheckFailureKind.RateLimited
            or UpdateCheckFailureKind.Http))
        {
            throw primary;
        }

        try
        {
            return await CheckViaAtomAsync();
        }
        catch (Exception atomEx)
        {
            // 保留主源的原因(它更接近用户实际遇到的问题), 备用源异常只留给 errors.txt。
            throw new UpdateCheckException(primary.Kind, primary.UserMessage,
                primary.StatusCode, atomEx);
        }
    }

    private static async Task<UpdateInfo?> CheckViaApiAsync()
    {
        using var resp = await GetAsync(LatestApiUrl);
        if (!resp.IsSuccessStatusCode)
        {
            // 404 = 还没发过任何 Release, 这是正常状态(新仓库/刚建仓), 不当错误抛
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;

            var rateLimited = resp.StatusCode == HttpStatusCode.TooManyRequests || IsRateLimitExhausted(resp);
            var kind = ClassifyHttpStatus(resp.StatusCode, rateLimited) ?? UpdateCheckFailureKind.Http;
            throw new UpdateCheckException(kind, MessageFor(kind, (int)resp.StatusCode),
                (int)resp.StatusCode);
        }

        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var root = doc.RootElement;
            var latest = ParseVersion(GetString(root, "tag_name"));
            if (latest == null || latest <= Version.Parse(App.AppVersion)) return null;

            return new UpdateInfo
            {
                Version = latest,
                Title = GetString(root, "name") ?? "",
                // body 是 Markdown 源文本, 弹窗里由 MarkdownView 渲染(先过 ReleaseNotes.Clean)
                Notes = GetString(root, "body") ?? "",
                ReleaseUrl = ApplyMirror(GetString(root, "html_url") ?? ReleasePageRawUrl),
                PublishedAt = ParseDate(GetString(root, "published_at"))
            };
        }
        catch (JsonException ex)
        {
            throw new UpdateCheckException(UpdateCheckFailureKind.Parse,
                "更新信息解析失败", innerException: ex);
        }
    }

    /// <summary>
    /// Atom 备用源: 只解析最新一条 entry。
    /// 版本号优先从 entry id(`tag:github.com,2008:Repository/123/v1.9.0`)取,
    /// 因为 entry title 可能是"BiliDesk v1.9.0"这类带前缀的 Release 名。
    /// </summary>
    private static async Task<UpdateInfo?> CheckViaAtomAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var resp = await GetAsync(LatestAtomUrl, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            throw new UpdateCheckException(UpdateCheckFailureKind.Http,
                MessageFor(UpdateCheckFailureKind.Http, (int)resp.StatusCode), (int)resp.StatusCode);
        }

        var xml = await resp.Content.ReadAsStringAsync(cts.Token);
        return ParseAtomFeed(xml, Svc.Settings.UpdateMirror);
    }

    /// <summary>
    /// 一次 GET, 把网络/超时异常翻译成带分类的 <see cref="UpdateCheckException"/>。
    /// HttpClient.Timeout 到点时抛的是 <see cref="TaskCanceledException"/>(内层 TimeoutException),
    /// 与调用方主动取消同型, 这里只可能是超时, 所以统一按 Timeout 处理。
    /// </summary>
    private static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken token = default)
    {
        try
        {
            return await Http.GetAsync(url, token);
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateCheckException(UpdateCheckFailureKind.Timeout,
                "连接 GitHub 超时", innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException(UpdateCheckFailureKind.Network,
                "网络不通, 无法连接 GitHub", innerException: ex);
        }
    }

    /// <summary>
    /// HTTP 状态码 → 失败分类。404 返回 null 表示"没有 Release, 不算失败"。
    /// 抽成纯函数是为了单测能穷举 403/429/5xx 这条最容易判错的分支。
    /// </summary>
    public static UpdateCheckFailureKind? ClassifyHttpStatus(HttpStatusCode code, bool rateLimitExhausted)
    {
        if (code == HttpStatusCode.NotFound) return null;
        if (code == HttpStatusCode.TooManyRequests) return UpdateCheckFailureKind.RateLimited;
        if (code == HttpStatusCode.Forbidden && rateLimitExhausted) return UpdateCheckFailureKind.RateLimited;
        return UpdateCheckFailureKind.Http;
    }

    private static bool IsRateLimitExhausted(HttpResponseMessage resp)
        => resp.Headers.TryGetValues("x-ratelimit-remaining", out var values)
           && string.Equals(values.FirstOrDefault()?.Trim(), "0", StringComparison.Ordinal);

    /// <summary>失败分类对应的短句(设置页状态行 / Toast / 异常 Message 共用一套文案)</summary>
    private static string MessageFor(UpdateCheckFailureKind kind, int? statusCode = null) => kind switch
    {
        UpdateCheckFailureKind.Network => "网络不通, 无法连接 GitHub",
        UpdateCheckFailureKind.Timeout => "连接 GitHub 超时",
        UpdateCheckFailureKind.RateLimited => "GitHub 请求过于频繁, 请稍后再试",
        UpdateCheckFailureKind.Http when statusCode is { } code => $"GitHub 返回错误 (HTTP {code})",
        UpdateCheckFailureKind.Http => "GitHub 返回错误",
        UpdateCheckFailureKind.Parse => "更新信息解析失败",
        _ => "检查更新时发生未知错误"
    };

    /// <summary>
    /// 解析 Releases Atom feed。只取第一条(最新); 没有 entry / 版本不比当前新时返回 null。
    /// 抽成纯函数: Atom 的结构比 JSON 更容易被改坏(代理返回 HTML 也是走这里),
    /// 单测要能覆盖"版本号来自 id/title 两种形状"。
    /// </summary>
    public static UpdateInfo? ParseAtomFeed(string xml, string? mirror = null)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://www.w3.org/2005/Atom";
        var entry = doc.Root?.Elements(ns + "entry").FirstOrDefault();
        if (entry == null) return null;

        var title = entry.Element(ns + "title")?.Value.Trim() ?? "";
        var id = entry.Element(ns + "id")?.Value.Trim() ?? "";
        var idTag = id.Length == 0 ? "" : id[(id.LastIndexOf('/') + 1)..];
        var latest = ParseVersion(idTag) ?? ParseVersion(title);
        if (latest == null || latest <= Version.Parse(App.AppVersion)) return null;

        var link = entry.Elements(ns + "link")
            .FirstOrDefault(l => string.Equals((string?)l.Attribute("rel"), "alternate",
                StringComparison.OrdinalIgnoreCase))
            ?.Attribute("href")?.Value;

        return new UpdateInfo
        {
            Version = latest,
            Title = title,
            // Atom 里只有 HTML 正文, 没有 Markdown 发布说明; 弹窗会显示"没有填写发布说明"占位。
            Notes = "",
            ReleaseUrl = MirrorUrl(mirror, string.IsNullOrWhiteSpace(link) ? ReleasePageRawUrl : link),
            PublishedAt = ParseDate(entry.Element(ns + "updated")?.Value)
        };
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>GitHub 的时间都是 ISO 8601(带 Z); 解析不出就当没有, 不抛。</summary>
    private static DateTimeOffset? ParseDate(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    /// <summary>tag → 版本号。容忍 "v"/"V" 前缀与 "-beta" 这类后缀(取 '-' 前的部分); 解析不出返回 null。</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>
    /// 启动后的自动检查: 设置里开着才查; 用户对**这个版本**点过「跳过此版本」就不再打扰
    /// (跳过只对当次发版有效, 下一个新版本照常弹)。
    ///
    /// ★ 失败不再静默(2026-11-21): VPN/代理、GitHub 被干扰、DNS 异常、共享出口 IP 被限流
    ///   时用户需要知道"自动检查没完成", 否则会以为功能坏了。提示本身有节流, 见 NotifyFailureAsync。
    /// </summary>
    public static async Task AutoCheckAsync()
    {
        if (!Svc.Settings.AutoCheckUpdate) return;

        UpdateInfo? info;
        try
        {
            info = await CheckAsync();
        }
        catch (UpdateCheckException ex)
        {
            App.ReportError(ex);
            await NotifyFailureAsync(ex);
            return;
        }
        catch (Exception ex)
        {
            // 兜底: 未预期异常同样要让用户知道, 但别把用户引到"解析失败"这种错误方向。
            App.ReportError(ex);
            await NotifyFailureAsync(new UpdateCheckException(UpdateCheckFailureKind.Unknown,
                "检查更新时发生未知错误", innerException: ex));
            return;
        }

        if (info == null) return;
        if (string.Equals(Svc.Settings.SkippedVersion, info.Version.ToString(),
                StringComparison.Ordinal)) return;

        // 从线程池回到 UI 线程再弹窗 —— 弹窗是 UI 对象
        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            var owner = App.Current.MainWindow;
            var win = new Views.Windows.UpdatePromptWindow(info)
            {
                Owner = owner,
                WindowStartupLocation =
                    owner is null
                        ? System.Windows.WindowStartupLocation.CenterScreen
                        : System.Windows.WindowStartupLocation.CenterOwner
            };
            win.Show();
        });
    }

    /// <summary>
    /// 自动检查失败后的提示流程。
    ///
    /// 三层过滤, 宁可少弹也不骚扰:
    ///   1. **完全断网不弹** —— 用户明显知道没网, 再弹"检查更新失败"只是噪音(有网但 GitHub 不通才提示);
    ///   2. **24 小时节流** —— 每次启动都失败的话, 一天最多提醒一次;
    ///   3. 先记账再弹窗 —— 窗口本身崩溃/用户强杀时也不会在下次启动继续纠缠。
    /// </summary>
    private static async Task NotifyFailureAsync(UpdateCheckException ex)
    {
        var networkAvailable = true;
        try { networkAvailable = NetworkInterface.GetIsNetworkAvailable(); }
        catch { /* 取不到就当有网: 宁可多提示一次, 也不要漏掉真实的失败 */ }

        var now = DateTimeOffset.UtcNow;
        if (!UpdateFailurePolicy.ShouldShowNotice(networkAvailable,
                Svc.Settings.LastUpdateFailureNoticeUtc, now))
        {
            return;
        }

        Svc.Settings.SetLastUpdateFailureNotice(now);

        try
        {
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                var owner = App.Current.MainWindow;
                var win = new Views.Windows.UpdateCheckFailedWindow(ex)
                {
                    Owner = owner,
                    WindowStartupLocation =
                        owner is null
                            ? System.Windows.WindowStartupLocation.CenterScreen
                            : System.Windows.WindowStartupLocation.CenterOwner
                };
                win.Show();
            });
        }
        catch (Exception uiEx)
        {
            // 提示窗都弹不出来只能是应用正在退出之类的场景, 记日志即可
            App.ReportError(uiEx);
        }
    }

    /// <summary>用系统浏览器打开链接(弹窗按钮的落点都是网页, 程序内不传大文件)</summary>
    public static void OpenInBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }
}
