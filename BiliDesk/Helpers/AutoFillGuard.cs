namespace BiliDesk.Helpers;

/// <summary>
/// "内容不足一屏就自动补下一页"的**连续补页**闸门。
///
/// ★★★ 2026-11-28 新增(用户报"搜索页再次点击搜索后会不断触发下拉刷新视频"):
///   各页面原来的写法是
///       <c>_autoFillCount++; try { await LoadMore(); } finally { _autoFillCount--; }</c>
///   —— <c>await</c> 一结束就减回去, 计数器实际上只等于"同时在飞的请求数",
///   **永远到不了上限 5**, 于是"补了一页还是不满一屏 ⇒ 立刻再补"这条链没有任何刹车。
///
///   搜索页尤其容易踩到: 再搜一次时 VerticalOffset 还停在上一次的位置, 新结果更短时
///   被 ScrollViewer 夹到底部, 产生的 ScrollChanged 反复命中"离底很近 / 内容不足一屏",
///   页面就自己不停往下加载(用户看到的"不断下拉刷新")。屏蔽词把结果筛得只剩几条时同理。
///
/// 语义:
///   · <see cref="OnFilled"/> 只累加, **不**在请求结束后回退 —— 这才是"连续补了几次";
///   · 只有用户**真的滚动**过, 或换了一次搜索 / 类别(调用 <see cref="Reset"/>), 额度才重新开始。
///
/// ★ 与 <see cref="AutoLoadThrottle"/> 分工不同, 两层都要:
///   那边管"缩放 / 布局变化引起的 ScrollChanged 不要立刻判", 这边管"连续补页要有上限"。
/// </summary>
public sealed class AutoFillGuard
{
    /// <summary>连续自动补页的次数上限。超过就停, 等用户滚动或重新搜索再放行。</summary>
    public const int MaxConsecutive = 5;

    private int _consecutive;

    /// <summary>当前已经连续补了几页(只增不减, 直到 <see cref="Reset"/>)。</summary>
    public int Consecutive => _consecutive;

    /// <summary>还能不能再自动补一页。</summary>
    public bool CanFill => _consecutive < MaxConsecutive;

    /// <summary>补了一页: 计数 +1。</summary>
    public void OnFilled() => _consecutive++;

    /// <summary>额度重开(用户真滚动过 / 换了一次搜索或类别 / 屏蔽条件刚落地)。</summary>
    public void Reset() => _consecutive = 0;
}
