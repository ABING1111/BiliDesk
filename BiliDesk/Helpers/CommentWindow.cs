using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 评论区"内容窗口"的纯计算部分(不依赖 WPF, 可单测)。
///
/// ★ 背景(2026-11-09 用户反馈三连):
///   ① 往下滑时很多评论**一次性**刷新出来;
///   ② 热门视频里按住滚动条往上拖, 已经加载过的评论先是一片空白, 随后才刷出来, 还卡;
///   ③ 评论区内存占用偏高。
///
/// ★ 旧实现的问题:
///   · 释放/恢复区间是"视口上下各 4 屏", 一次扫描会把**几十条**离屏评论同时重建;
///   · 恢复只挂在 120ms 的 Background 定时器上, 而且**向上滚动根本不排这个定时器**
///     (OnInfoScrollChanged 里 `VerticalChange < 0` 直接 return) —— 拖回上面时
///     可见的卡片还是空模板, 要等下一次别的时机才会被补回来;
///   · 常驻内容最多 9 屏, 内存自然高。
///
/// ★ 新策略:
///   · 视口内(±一小截余量)同步恢复 —— 拖到哪一帧, 那一屏就得有内容;
///   · 视口外只预取 ±<see cref="PrefetchScreens"/> 屏, 且每轮限量;
///   · 超过 ±<see cref="ReleaseScreens"/> 屏才释放, 常驻窗口从 9 屏降到约 5 屏。
/// </summary>
public static class CommentWindow
{
    /// <summary>视口外预取多少屏(上下各一份)。</summary>
    public const double PrefetchScreens = 0.75;

    /// <summary>离视口多少屏以外才释放。必须明显大于预取区间, 否则会"刚补上又被放掉"。</summary>
    public const double ReleaseScreens = 2.0;

    /// <summary>同步恢复的余量(屏): 滚动时视口边缘的那半条评论也要提前恢复。</summary>
    public const double VisibleMarginScreens = 0.15;

    /// <summary>每轮后台扫描最多补多少张离屏卡片, 避免一次重建太多视觉树。</summary>
    public const int PrefetchBurst = 4;

    /// <summary>翻页时每次往列表里放几条(不是接口页大小), 让新评论分批出现。</summary>
    public const int RevealBatch = 6;

    /// <summary>
    /// 热门评论"异常少"的回退阈值: 非置顶根评论少于一页的一半(15 条)且总条数更多时,
    /// 认为 mode=3 返回不全, 改按时间重拉一页。
    ///
    /// ★ 2026-11-09 实测 BV1irxX6LE9R: mode=3 返回"置顶 + 1 条回复", all_count=3668,
    ///   用户只能看到两条评论。旧判据只覆盖"只剩置顶", 漏掉了"置顶 + 少量回复"。
    /// </summary>
    public const int HotFallbackBelow = 15;

    /// <summary>
    /// 热门页是否明显不完整、应当按时间再拉一页做对比。
    /// <paramref name="hotRootCount"/> 只算**非置顶**根评论(data.replies 的条数);
    /// 用总条数兜住"这个视频本来就只有几条评论"的情况, 避免无意义地多打一次接口。
    /// </summary>
    public static bool ShouldFallbackHot(int hotRootCount, long total)
        => total > 0 && hotRootCount < Math.Min(HotFallbackBelow, (int)Math.Min(total, int.MaxValue));

    // ------------------------------------------------------------ 评论限流冷却(2026-11-09)

    /// <summary>首次命中的限流冷却时长(秒)。</summary>
    public const int RateLimitFirstSeconds = 30;

    /// <summary>冷却时长上限(秒)。B 站实测恢复窗口约 45~180 秒。</summary>
    public const int RateLimitMaxSeconds = 180;

    /// <summary>
    /// 下一次限流冷却时长: 30 → 60 → 120 → 180 秒封顶。
    /// ★ 固定时长会在恢复窗口内连续撞墙, 每撞一次都多打一轮请求, 反而拖长限流。
    /// </summary>
    public static int NextRateLimitCooldown(int currentSeconds)
        => currentSeconds <= 0
            ? RateLimitFirstSeconds
            : Math.Min(RateLimitMaxSeconds, currentSeconds * 2);

    /// <summary>
    /// 接口错误文案是不是 B 站 -412(IP 限流)。
    /// ★ 只认 ApiClient 归一化后的中文文案: 界面层不应该依赖裸英文 "request was banned"。
    /// </summary>
    public static bool IsRateLimitMessage(string? err)
        => !string.IsNullOrEmpty(err) &&
           (err.Contains("频繁", StringComparison.Ordinal) ||
            err.Contains("限流", StringComparison.Ordinal));

    /// <summary>这个纵向区间是否落在同步恢复带内(视口 + 一小截余量)。</summary>
    public static bool ShouldRestoreNow(double top, double bottom, double viewport)
        => viewport > 0 &&
           bottom >= -viewport * VisibleMarginScreens &&
           top <= viewport * (1 + VisibleMarginScreens);

    /// <summary>这个纵向区间是否落在后台预取带内(视口 + 预取余量)。</summary>
    public static bool ShouldPrefetch(double top, double bottom, double viewport)
        => viewport > 0 &&
           bottom >= -viewport * PrefetchScreens &&
           top <= viewport * (1 + PrefetchScreens);

    /// <summary>这个纵向区间是否已经离视口足够远, 可以释放内容。</summary>
    public static bool ShouldRelease(double top, double bottom, double viewport)
        => viewport > 0 &&
           (bottom < -viewport * ReleaseScreens ||
            top > viewport * (1 + ReleaseScreens));

    /// <summary>后台预取带的上边界(视口坐标)。</summary>
    public static double PrefetchTop(double viewport) => -viewport * PrefetchScreens;

    /// <summary>后台预取带的下边界(视口坐标)。</summary>
    public static double PrefetchBottom(double viewport) => viewport * (1 + PrefetchScreens);

    /// <summary>释放带的上边界(视口坐标)。</summary>
    public static double ReleaseTop(double viewport) => -viewport * ReleaseScreens;

    /// <summary>释放带的下边界(视口坐标)。</summary>
    public static double ReleaseBottom(double viewport) => viewport * (1 + ReleaseScreens);

    /// <summary>同步恢复带的上边界(视口坐标)。</summary>
    public static double RestoreNowTop(double viewport) => -viewport * VisibleMarginScreens;

    /// <summary>同步恢复带的下边界(视口坐标)。</summary>
    public static double RestoreNowBottom(double viewport) => viewport * (1 + VisibleMarginScreens);
}
