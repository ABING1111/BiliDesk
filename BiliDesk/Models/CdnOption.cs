namespace BiliDesk.Models;

/// <summary>
/// 视频 CDN(线路)选择策略。★ 2026-10-03 新增。
///
/// 背景(实测, 见 .probes/bd-probe-cdn):
///   B 站取流返回的 `dash.video[].baseUrl` 里带的是**某一家 CDN 的域名**,
///   同一份视频在 `backupUrl` 里还给若干备用域名。这些域名**只有 host 不同**,
///   path 与 query(含签名 upgcxcode 等)完全一致 —— 所以把 host 换掉就能换线路,
///   实测 8 个候选 host 全部返回 HTTP 206, 数据正常。
///
///   不同线路的速度差距很大: 同一时刻实测 2.5MB/s ~ 11MB/s(4 倍以上), 而且
///   不同网络/地区/时段排名会变 —— 所以"自动测速挑最快"比写死一条更有意义。
///
///   另外 B 站会把一部分流指向 PCDN(点对点分发, 拿其他用户的带宽做节点)。
///   它有两个来源要一起防:
///     1. 域名里带 `mcdn.` 的;
///     2. **伪装域名**: 实测拿到过 `mv0bz14m.edge.mountaintoys.cn`(query 里 os=mcdn),
///        域名看不出任何 PCDN 特征, 只能靠 `os=mcdn` 这个 query 参数认。
/// </summary>
public enum CdnSelectMode
{
    /// <summary>自动测速: 从候选线路里实测挑吞吐最高的(默认)</summary>
    Auto = 0,

    /// <summary>手动指定: 固定用用户选定的那一家 CDN</summary>
    Manual = 1,

    /// <summary>跟随服务端: 不替换 host, 完全用接口返回的线路</summary>
    ServerDefault = 2
}

/// <summary>
/// 一家可选 CDN。
///
/// host 只留域名 —— 换线路时把它替换到原 URL 的 host 上(path/query 原样保留,
/// 那是签名的一部分, 动不得)。
///
/// ★★ 重要边界(2026-10-03 实测, 见 .probes/bd-probe-matrix):
///   换 host 不是无条件可行 —— 它取决于**模板**是哪条线路:
///     · 模板是 `*.mcdn.bilivideo.cn`  → 换成下表任何一家都 **403**(实测 36/36 全失败,
///       签名与该 host 绑死);
///     · 模板是其它(upos-* / estgoss / bcache / `*.edge.mountaintoys.cn`)
///       → 换成下表 **全部成功**(实测 72/72)。
///   所以 CdnService 只在找到"非 mcdn 的模板"时才做替换, 并且**用测速请求同时验证可用性**
///   (403 的线路不会进排名)。
///
/// ★★ 也不要"顺手"往这个列表里加 host:
///   加之前先用 `.probes/bd-probe-matrix` 跑一遍, 把"实测可用"和"猜的"分开。
/// </summary>
public sealed record CdnOption(string Id, string Name, string Host)
{
    /// <summary>
    /// 全部候选(顺序即设置页里的显示顺序)。Id 是**持久化用**的稳定标识 ——
    /// 存 host 也行, 但 B 站换域名时 Id 不变、只改 Host, 老配置才不会失效。
    /// </summary>
    public static readonly CdnOption[] All =
    {
        new("ali",   "阿里云",       "upos-sz-mirrorali.bilivideo.com"),
        new("aliov", "阿里云(海外)", "upos-sz-mirroraliov.bilivideo.com"),
        new("cos",   "腾讯云",       "upos-sz-mirrorcos.bilivideo.com"),
        new("cosb",  "腾讯云 VOD",   "upos-sz-mirrorcosb.bilivideo.com"),
        new("hw",    "华为云",       "upos-sz-mirrorhw.bilivideo.com"),
        new("hwb",   "华为云 B",     "upos-sz-mirrorhwb.bilivideo.com"),
        new("08c",   "华为云 08c",   "upos-sz-mirror08c.bilivideo.com"),
        new("tf_hw", "华为云 TF",    "upos-tf-all-hw.bilivideo.com"),
        new("tf_tx", "腾讯云 TF",    "upos-tf-all-tx.bilivideo.com"),
    };

    /// <summary>按 Id 找一家(找不到返回 null —— 老配置里存了已下线的 Id 时用得上)</summary>
    public static CdnOption? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(o => o.Id == id);

    /// <summary>自动测速完全失败时的兜底: 用第一个候选(阿里云), 而不是放弃替换</summary>
    public static CdnOption Fallback => All[0];
}
