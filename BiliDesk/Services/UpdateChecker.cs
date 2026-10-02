using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
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

    /// <summary>Release 说明(即发布说明正文, 弹窗里展示给用户的那段)</summary>
    public string Notes { get; init; } = "";

    /// <summary>该 Release 的网页地址(交给系统浏览器打开, 程序内不做大文件下载)</summary>
    public string ReleaseUrl { get; init; } = "";
}

/// <summary>
/// 自动检查更新: 数据源 = GitHub Releases(公开仓库的 releases/latest 接口, 匿名可访问)。
///
/// 为什么只"检查 + 跳浏览器", 不做程序内下载安装:
///   GitHub Release 页上同时挂着便携版与安装器, 下哪个、怎么装是用户自己的选择 ——
///   客户端替用户决定升级方式反而多出错面(自更新还要处理进程替换/权限, 与收益不成比例)。
///   所以这里的职责边界很窄: 比版本号 → 有新版就弹窗 → 按钮跳系统浏览器。
///
/// 网络上的独立性: 这不是 B 站接口, **不走 ApiClient**(那里带 WBI/appkey 签名语义和风控
/// 相关的默认值, 混进来只会互相牵连), 单独一个 HttpClient, 失败一律不影响主功能。
/// </summary>
public static class UpdateChecker
{
    /// <summary>Release 数据所在的仓库(owner/repo)。改名要连着 ReleasePageUrl 一起改。</summary>
    public const string RepoOwner = "ABING1111";
    public const string RepoName = "BiliDesk";

    /// <summary>releases/latest 网页地址(弹窗里「前往 GitHub 下载」按钮的落点)。</summary>
    public const string ReleasePageUrl =
        $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    // ★ 这里曾经有一个"微云高速下载"镜像按钮(WeiYunShareUrl / WeiYunSharePwd 两个常量)。
    //   2026-10-01 用户要求**下载全部走 GitHub**, 两个常量与那个按钮一起删掉了。
    //   别再按"国内下载慢"的直觉加回来: 镜像链接要人工维护, 忘了换新版就是给用户一个旧包;
    //   国内访问 Releases 页/附件本来也只需要浏览器能开 GitHub。

    private static readonly HttpClient Http = new()
    {
        // 检查更新不该拖住任何界面: 10 秒拿不到结果就当这次失败(启动后的静默检查会下次再试)。
        Timeout = TimeSpan.FromSeconds(10)
    };

    static UpdateChecker()
    {
        // GitHub API 强制要求 User-Agent 头(不带直接 403); GitHub 不是 B 站,
        // 沿用 HttpDefaults 的纯 Chrome UA 没有副作用(它本来也只是个普通浏览器 UA)。
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>最新 Release 的 API 地址(每次现拼, 常量拼接没有循环依赖问题)</summary>
    private static string LatestApiUrl =>
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>
    /// 查一次最新版本并与当前版本比较。
    /// 返回 null 的三种含义要分清: **没有 Release**(仓库还没发过版, 404)、**已是最新**、
    /// **tag 解析失败**(Tag 不是 vX.Y.Z 形状, 宁可不提示也不给用户一个装不上的"新版本")。
    /// 网络失败会抛异常, 由调用方决定"静默忽略"(自动检查)还是"提示用户"(手动检查)。
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var resp = await Http.GetAsync(LatestApiUrl);
        if (!resp.IsSuccessStatusCode)
        {
            // 404 = 还没发过任何 Release, 这是正常状态(新仓库/刚建仓), 不当错误抛
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            throw new HttpRequestException($"检查更新失败: HTTP {(int)resp.StatusCode}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        var latest = ParseVersion(root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null);
        if (latest == null || latest <= Version.Parse(App.AppVersion)) return null;

        return new UpdateInfo
        {
            Version = latest,
            Title = root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? ""
                : "",
            // body 是 Markdown 源文本; 弹窗里按纯文本展示, markdown 标记(# * -)读起来也不碍事,
            // 没必要为它引一个渲染库
            Notes = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
                ? bodyEl.GetString() ?? ""
                : "",
            ReleaseUrl = root.TryGetProperty("html_url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString() ?? ReleasePageUrl
                : ReleasePageUrl
        };
    }

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
    /// (跳过只对当次发版有效, 下一个新版本照常弹)。任何失败都静默 —— 自动检查不该在
    /// 断网时给用户报错。
    /// </summary>
    public static async Task AutoCheckAsync()
    {
        if (!Svc.Settings.AutoCheckUpdate) return;
        try
        {
            var info = await CheckAsync();
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
        catch
        {
            // 静默: 断网/GitHub 连不上是常态, 自动检查失败不该打扰用户
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
