using System;
using System.Collections.Generic;
using System.Windows;
using BiliDesk.Views;

namespace BiliDesk.Services;

/// <summary>
/// 正文里的链接 → 用**内置浏览器**(WebView2 的 <see cref="BrowserWindow"/>)打开。
///
/// ★ 为什么不用系统默认浏览器: 用户明确要求"点击即可用内置的浏览器打开";
///   内置浏览器共用同一个 WebView2 Profile, B 站页面里是**已登录**状态,
///   跳过去不用再登一次。
/// ★ 为什么单独抽一层: 评论区、简介、图文正文三处都要这个动作, 各自 new 一个
///   BrowserWindow 会让 Owner 处理不一致(尤其播放器全屏时)。
///
/// ★★ 去重(2026-11-14 用户报"点任意位置/关闭窗口会不断开新内置浏览器窗口"):
///   根因是 Hyperlink 在只读 RichTextBox 里留下了鼠标捕获(见 RichTextBuilder.CreateLink),
///   已在那侧修掉。这一层再加两道闸:
///     ① **同地址已有窗口开着就不再开** —— 语义上正确, 也是连环弹窗真正的拦路闸;
///     ② 同地址在极短时间内只开一次 —— 覆盖"窗口还没进 Application.Windows"的瞬间。
///   即使将来别的路径又漏了捕获, 用户也不会看到窗口成排弹出来。
/// </summary>
public static class LinkLauncher
{
    /// <summary>同一个地址在这个时间窗内重复请求只开一个窗口(ms)。</summary>
    private const long DedupMs = 800;

    /// <summary>最近一次打开记录: 地址 → 时刻(单调递增的 TickCount64)。</summary>
    private static readonly Dictionary<string, long> Recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 用内置浏览器打开一个链接。
    /// </summary>
    /// <param name="url">已归一化的 http/https 地址(见 <c>LinkParser</c>)。</param>
    /// <param name="owner">宿主窗口; 传 null 时退回主窗口, 再不行就不挂 Owner。</param>
    public static void OpenInBrowser(string? url, Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!IsWebUrl(url)) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            // ★ 两道去重(见类注释):
            //   ① 同一个地址已经有窗口开着 ⇒ 不再开第二个。这是**语义上正确**的那道闸:
            //      用户要的是"看这个链接", 已经有一个在看了就没必要再开;
            //      而且连环弹窗的每一步都会撞上它, 无论触发频率多低都挡得住。
            //   ② 时间窗去重: 覆盖"窗口还没进 Application.Windows 就被连点两次"的瞬间。
            if (HasOpenWindowFor(url) || IsDuplicate(url)) return;

            var host = owner ?? SvcWindow.Main;
            var win = new BrowserWindow(url, url);
            // Owner 只在不相等时挂: 给窗口自己设 Owner 会抛异常。
            if (host != null && !ReferenceEquals(host, win) && host.IsVisible)
                win.Owner = host;
            win.Show();
        });
    }

    /// <summary>这个地址是不是已经有一个内置浏览器窗口开着了。</summary>
    public static bool HasOpenWindowFor(string url)
    {
        // ★ Application.Current 可能为 null(单测宿主、启动极早期): 这时没有窗口, 如实返回 false。
        var app = Application.Current;
        if (app == null) return false;

        var urls = new List<(string Url, bool Closed)>();
        foreach (Window w in app.Windows)
        {
            if (w is not BrowserWindow browser) continue;
            urls.Add((browser.InitialUrl, browser.IsClosedForReuse));
        }
        return AnyOpenMatch(urls, url);
    }

    /// <summary>
    /// 纯判定: 给定"已开窗口的 (地址, 是否已关闭)"清单, 这个地址是不是已经开着了。
    ///
    /// ★ 抽成纯函数是为了能单测 —— 它才是"连环弹窗"真正的拦路闸(见类注释),
    ///   而真窗口在单测宿主里建不出来(WPF 需要 Application + STA + 资源字典)。
    /// </summary>
    public static bool AnyOpenMatch(IEnumerable<(string Url, bool Closed)> openWindows, string url)
    {
        foreach (var (openUrl, closed) in openWindows)
        {
            if (closed) continue;
            if (string.Equals(openUrl, url, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 这个地址是不是刚刚已经开过窗口了(同一次点击被触发多次时挡掉后续那几次)。
    /// ★ 只对**同一地址**去重: 用户确实想再开一个同页面的情况要等 DedupMs 之后。
    /// ★ 公开出来是给单测用的: 这条规则是"连环弹窗"的最后一道防线, 必须有测试钉住;
    ///   生产代码只经 <see cref="OpenInBrowser"/> 调用它。
    /// </summary>
    public static bool IsDuplicate(string url)    {
        var now = Environment.TickCount64;
        // 顺手清理过期记录: 条数天然很少(一次会话里点过的链接数), 但别让它无限涨。
        if (Recent.Count > 32)
        {
            var expired = new List<string>();
            foreach (var (key, at) in Recent)
                if (now - at >= DedupMs) expired.Add(key);
            foreach (var key in expired) Recent.Remove(key);
        }

        if (Recent.TryGetValue(url, out var last) && now - last < DedupMs) return true;
        Recent[url] = now;
        return false;
    }

    /// <summary>
    /// 只放行 http/https。链接是**外部数据**, 不能让它把 file:/javascript: 之类的地址塞进 WebView2。
    /// </summary>
    public static bool IsWebUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
