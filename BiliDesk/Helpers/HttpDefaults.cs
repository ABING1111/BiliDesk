namespace BiliDesk.Helpers;

/// <summary>
/// 所有出站 HTTP 请求共用的默认值。
///
/// ★ 为什么 UA 必须收敛成一份:
///   B 站按 UA 判定请求是不是"浏览器客户端"。分散在多处各写一遍时, 只要有人只改了
///   其中一处, 就会出现"封面能显示、评论却加载不出来"这种极难定位的现象。
/// </summary>
public static class HttpDefaults
{
    /// <summary>
    /// 纯 Chrome UA, **不要**追加 `Edg/` / `OPR/` 之类的品牌后缀。
    ///
    /// ★★★ 2026-10-17: 这里原来是 `Chrome/126.0.0.0`, 实测**只有这一个版本被 B 站拦**
    ///   (HTTP 412 / code `-412 request was banned`), 而同族其他版本全部正常:
    ///     <code>
    ///       Chrome/126.0.0.0   → 412  ❌
    ///       Chrome/120.0.0.0   → 200  ✅
    ///       Chrome/131.0.0.0   → 200  ✅
    ///       Chrome/140.0.0.0   → 200  ✅
    ///       ...Edg/126         → 200  ✅
    ///     </code>
    ///   ⇒ 表现是**游客态(未登录)完全打不开任何接口**: 取不到详情、取不到播放地址,
    ///     于是"免登录观看"整个功能不可用(登录态有 SESSDATA 背书才偶尔能过)。
    ///   ⇒ 是 B 站把这个旧版本 UA 拉黑了, 与我们代码无关; 升级版本号即可。
    ///   (探针 bd-probe-ua2: 逐个 UA 打 /x/web-interface/view。)
    ///
    ///   **版本号要跟着主流走**: 以后若再出现"游客态全量 412", 先拿 bd-probe-ua2 扫一遍 UA,
    ///   别去改别的代码 —— 这次就是被这条坑了很久。
    /// </summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
}
