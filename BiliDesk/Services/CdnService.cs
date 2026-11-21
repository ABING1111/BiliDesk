using System;
using System.Collections.Generic;
using System.Linq;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// CDN(线路)选择。★ 2026-10-03 新增, 2026-11-21 大幅收缩。
///
/// 做什么(现在只剩两件事):
///   · 跟随服务端(默认): 不换 host, 用接口给的线路;
///   · 手动指定: 用用户选定的那一家 CDN;
///   · 两者都能屏蔽 PCDN(点对点分发)。
///
/// ★★★ 2026-11-21 删除"自动测速"策略。
///   它曾按实测吞吐从候选线路里挑最快的一条, 但两个问题始终没解决:
///     ① 换 host 后部分线路的**持续吞吐/抖动**反而更差, 用户体感是"卡顿变多"
///        (这也是 2026-11-05 就把默认改成"跟随服务端"的原因);
///     ② 它连带引入了一整套缓存/后台预热/测速报告, 而这些的收益只体现在
///        "挑一条更快的", 与"能不能正常播、能不能拖进度条"都无关。
///   现在只留"跟随服务端"与"手动指定"。注意 <see cref="CdnSelectMode.Auto"/> 成员
///   **仍然存在**, 但那只是 settings.json 的反序列化兼容(见该枚举的注释)。
///
/// ★★ 换 host 的边界(2026-10-03 实测, 见 .probes/bd-probe-matrix):
///   同一份视频的 baseUrl/backupUrl **只有 host 不同**, path 与 query(含签名)一致,
///   所以"换 host"通常有效。但有**一个明确例外**:
///     · 模板 host 是 `*.mcdn.bilivideo.cn` → 换成任何标准 CDN **一律 403**
///       (实测 4 个视频 × 9 家 = 36 次全部 403, 签名与这个 host 绑死);
///     · 模板 host 是其它(`upos-sz-mirror*` / `estgoss` / `bcache` /
///       `*.edge.mountaintoys.cn`) → 换 9 家**全部成功**(实测 72/72)。
///   所以本类**永远不会拿 mcdn 主机当模板**去做替换(见 IsSwappableTemplate)。
///
/// ★★ 另一个实测发现(也见 CdnOption): PCDN 有两种写法
///   · 域名带 `mcdn.` / `pcdn.`;
///   · **伪装域名**: `*.edge.mountaintoys.cn`, 域名看不出特征, 靠 query 里的 `os=mcdn` 认。
///   旧实现只看域名, 会漏掉第二种 —— 这正是"屏蔽 PCDN"要客户端自己判断的原因。
///
/// ★★★ 2026-11-20 勘误: **不要**按 host 名给线路打"不能 seek"的黑名单。
///   曾经(055bd9a)按"华为云系线路对条件 Range 返回 412"的结论, 把
///   mirrorhw / mirror08c / tf-all-hw 排除在选路之外。当晚用户报"还是时好时坏",
///   复测(bd-probe-seek412 / bd-probe-vlcseek)发现那个结论**不成立**:
///     · 同一份签名换到任何 host, 条件 Range(闭区间/开放区间)在 5M~350M 偏移上
///       全部 206, ETag 连续 4 次连接也完全一致 —— 复现不出 412;
///     · 被排除的 mirrorhw 实测 :start-time 成功率 5/5, 反而是白名单里的
///       mirroraliov 是 0/5 —— 黑名单把好线路排掉、把坏线路留下。
///   真正的缺陷在**重建定位**上(见 PlayerWindow 的 :start-time 兜底), 与线路无关,
///   所以本类不再参与"能不能 seek"的判断。
/// </summary>
public sealed class CdnService
{
    public static CdnService Instance { get; } = new();

    private CdnService() { }

    /// <summary>
    /// 给一条媒体 URL 选线路。
    ///
    /// 返回的 URL 只换了 host(scheme/path/query 原样保留)。
    /// ★ 保留 Task 返回值以免改动调用点签名(它从来不会真的异步等待)。
    /// </summary>
    /// <param name="url">服务端给的一条 baseUrl(作为原样回退用)</param>
    /// <param name="allCandidates">该流的全部候选(含 baseUrl 与所有 backupUrl)</param>
    public Task<string> SelectUrlAsync(string url, IReadOnlyList<string> allCandidates)
    {
        var pool = BuildPool(url, allCandidates);

        var settings = SettingsStore.Instance;
        if (settings.CdnMode == CdnSelectMode.Manual &&
            CdnOption.ById(settings.CdnManualId) is { } target &&
            TryBuildSwapped(pool, target.Host) is { } manualUrl)
        {
            return Task.FromResult(manualUrl);
        }

        // 跟随服务端(默认), 以及"手动指定但拿不到目标"时的回落。
        return Task.FromResult(pool[0]);
    }

    /// <summary>
    /// 音轨等**次要流**用的线路选择: 与视频同一条路, 不单独测速。
    ///
    /// 为什么音轨不单独挑: 它只有 0.1~0.2Mbps, 单独挑一条纯属浪费; 而且它与视频
    /// 属于同一个 input(DASH 靠 input-slave 挂进来), 用同一家 CDN 反而更稳。
    /// ★ 2026-11-20 勘误: 音轨**不需要**绕开任何线路。实测"只换音轨 host"时
    ///   :start-time 成功率 12/12 —— 拖垮定位的是视频那条流的 host 替换, 不是音轨。
    /// </summary>
    public string SelectSecondaryUrl(string url, IReadOnlyList<string> allCandidates)
    {
        var pool = BuildPool(url, allCandidates);

        var settings = SettingsStore.Instance;
        if (settings.CdnMode == CdnSelectMode.Manual &&
            CdnOption.ById(settings.CdnManualId) is { } target &&
            TryBuildSwapped(pool, target.Host) is { } manualUrl)
        {
            return manualUrl;
        }

        return pool[0];
    }

    /// <summary>
    /// 组出可用的候选池: 先按「屏蔽 PCDN」过滤, 再兜底到不过滤。
    ///
    /// ★ 过滤后一条不剩时宁可不过滤 —— 换 CDN/PCDN 偏好是锦上添花,
    ///   绝不能因为它让本来能播的视频播不了。
    /// </summary>
    private static List<string> BuildPool(string url, IReadOnlyList<string> allCandidates)
    {
        var pool = SettingsStore.Instance.BlockPcdn
            ? allCandidates.Where(u => !IsPcdn(u)).ToList()
            : new List<string>(allCandidates);
        if (pool.Count == 0) pool = new List<string>(allCandidates);
        if (pool.Count == 0) pool = new List<string> { url };
        return pool;
    }

    /// <summary>
    /// 从池子里挑一个**可换模板**, 换成指定 host。
    /// 找不到可换模板(全是 mcdn)时返回 null —— 那种情况换 host 必 403(见类注释)。
    /// </summary>
    private static string? TryBuildSwapped(List<string> pool, string targetHost)
    {
        var template = pool.FirstOrDefault(IsSwappableTemplate);
        return template == null ? null : ReplaceHost(template, targetHost);
    }

    /// <summary>
    /// 这条线路能不能当"换 host 的模板"。
    ///
    /// ★ 实测: `*.mcdn.bilivideo.cn` 的签名与该 host 绑死, 换成任何标准 CDN 都 403,
    ///   所以它不能当模板。其它 host(含伪装成 edge.* 但 os=mcdn 的那些)都可以。
    /// </summary>
    private static bool IsSwappableTemplate(string url)
    {
        var host = SafeHost(url);
        return host.Length > 0 &&
               !host.Contains("mcdn.", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeHost(string url)
    {
        try { return new Uri(url).Host; } catch { return ""; }
    }

    /// <summary>把 URL 的 host 换成目标 CDN(路径/查询串全部保留; 统一走 https)</summary>
    public static string ReplaceHost(string url, string host)
    {
        try
        {
            return new UriBuilder(url) { Host = host, Port = -1, Scheme = "https" }.Uri.ToString();
        }
        catch
        {
            // URL 结构异常(理论上不会): 原样返回, 至少不破坏播放
            return url;
        }
    }

    /// <summary>
    /// 这条线路是不是 PCDN(点对点分发)。
    ///
    /// ★ 两个来源都要认(2026-10-03 实测):
    ///   1. 域名带 `mcdn.` / `pcdn.`;
    ///   2. **伪装域名**: 实测 B 站给过 `mv0bz14m.edge.mountaintoys.cn` /
    ///      `b-baacc8r65...edge.mountaintoys.cn`, 域名完全看不出 PCDN 特征,
    ///      但 query 里带 `os=mcdn`。只看域名会漏掉它们。
    /// </summary>
    public static bool IsPcdn(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host;

            if (host.Contains("mcdn.", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("pcdn.", StringComparison.OrdinalIgnoreCase))
                return true;

            var query = uri.Query;
            if (query.Length == 0) return false;

            foreach (var kv in query.TrimStart('?').Split('&'))
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                if (!kv[..eq].Equals("os", StringComparison.OrdinalIgnoreCase)) continue;
                if (kv[(eq + 1)..].StartsWith("mcdn", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch { return false; }
    }
}
