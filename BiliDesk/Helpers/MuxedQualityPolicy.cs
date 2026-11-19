namespace BiliDesk.Helpers;

/// <summary>
/// durl(合流单流)取流结果的"是否真的给到请求档位"判据。
///
/// ★ 为什么需要它(2026-11-20 用户报"点 480P 实际变成 360P"):
///   durl 的单流档位并不总是全集。实测同一个视频:
///     qn=32 → data.quality=16, accept_quality=[64,16]
///   也就是说它**根本没有 480P 单流**(480P 往往只在 DASH 里)。服务端不会报错,
///   而是静默降成 360P。直接播这份地址, 用户看到的就是"点了 480 却在放 360"。
///   ⇒ 只有 durl 真的给到请求档位(或更高)才采用, 否则必须回落到 DASH。
/// ★ 判据放在这里而不是散在取流分支里: 这是一条产品规则(宁可多打一次 DASH,
///   也不能把用户显式选择的清晰度悄悄换掉), 值得单测。
/// </summary>
public static class MuxedQualityPolicy
{
    /// <summary>
    /// durl 是否满足请求。<paramref name="actualQn"/> &gt; 0 时以它为准
    /// (它就是服务端选中的实际档位); 缺失(0)时退回看 accept_quality 里有没有这一档。
    /// </summary>
    public static bool HonorsRequest(
        int requestedQn,
        int actualQn,
        IReadOnlyList<(int qn, string label)> acceptedQualities)
    {
        if (actualQn > 0) return actualQn >= requestedQn;
        return acceptedQualities.Any(q => q.qn == requestedQn);
    }
}
