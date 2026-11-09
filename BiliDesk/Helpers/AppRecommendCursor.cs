using System;
using System.Collections.Generic;

namespace BiliDesk.Helpers;

/// <summary>
/// 首页 App 端推荐流(app.bilibili.com/x/v2/feed/index)的**翻页游标**。
///
/// ★ 与网页版那套(wbi/index/top/feed/rcmd)是两套完全不同的游标:
///   · 网页版用 fresh_idx / brush / fetch_row / last_showlist(见 RecommendCursor);
///   · App 端用 **idx + pull**:
///       - 刷新: pull=true 且**不带** idx —— 从推荐池最前面取最新一批;
///       - 加载更多: pull=false 且带 idx —— 顺着推荐排序往下翻。
///     实测不带 idx 连拉时服务端窗口几乎不动; 带 idx 才每批稳定前进 10 条。
///
/// ★ idx 是"位置"不是"内容 id"。一批里 idx 从大到小排, 要带回**最小**的那个
///   (即这一批读到推荐池最深处的位置), 下一批才真的往下走。
///
/// ★ 本类是**纯逻辑**(不碰网络、不碰 UI), 失败时不要调用 <see cref="OnLoaded"/>,
///   游标原地不动, 重试复用同一组参数 —— 与 RecommendCursor 同一条不变量。
/// </summary>
public sealed class AppRecommendCursor
{
    /// <summary>App 首页推荐流单批条数(接口固定 10 条)。</summary>
    public const int PageSize = 10;

    /// <summary>翻页位置(0 = 还没有游标; 刷新后清零)。</summary>
    public long Idx { get; private set; }

    /// <summary>
    /// 本次请求是不是"下拉刷新"语义。true = pull=true 且不带 idx;
    /// false = pull=false 且带 idx(加载更多)。
    /// </summary>
    public bool Pull { get; private set; } = true;

    /// <summary>
    /// 刷新: 清掉游标, 下一次请求从推荐池最前面取(pull=true)。
    /// </summary>
    public void BeginRefresh()
    {
        Idx = 0;
        Pull = true;
    }

    /// <summary>
    /// 加载更多: 保留当前 idx, 下一次请求带 idx 往下翻(pull=false)。
    /// ★ 没有游标时不能假装翻页 —— 退回 pull=true(从池子最前面取),
    ///   否则服务端会收到 "pull=false 且无 idx" 这个实测窗口不动的组合。
    /// </summary>
    public void BeginLoadMore()
    {
        Pull = Idx <= 0;
    }

    /// <summary>
    /// 只把游标清零, 不动 pull 语义(登录/来源切换时用; 紧接着的请求会自己决定刷新还是翻页)。
    /// </summary>
    public void Rewind()
    {
        Idx = 0;
        Pull = true;
    }

    /// <summary>
    /// 一次**成功**请求之后推进游标。
    /// </summary>
    /// <param name="batchIndices">
    /// 本批**所有**条目的 idx(含直播/横幅等非视频项 —— 它们同样占推荐流里的位置,
    /// 跳过会让游标偏浅, 下一批又从中间重来)。取其中最小正数。
    /// </param>
    public void OnLoaded(IReadOnlyList<long>? batchIndices)
    {
        if (batchIndices == null || batchIndices.Count == 0) return;

        var min = long.MaxValue;
        foreach (var idx in batchIndices)
        {
            if (idx > 0 && idx < min) min = idx;
        }
        if (min != long.MaxValue) Idx = min;
    }

    /// <summary>把 pull / idx 写进请求参数(签名由 ApiClient 统一加 appkey/sign)。</summary>
    public void ApplyTo(Dictionary<string, string> ps)
    {
        ps["pull"] = Pull ? "true" : "false";
        if (Idx > 0) ps["idx"] = Idx.ToString();
    }
}
