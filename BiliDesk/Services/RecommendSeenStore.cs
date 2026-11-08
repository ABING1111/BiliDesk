using System;
using System.Collections.Generic;

namespace BiliDesk.Services;

/// <summary>
/// 首页推荐流**已展示过**的稿件记录(仅本进程内存)。
///
/// ★★ 它挡的是"刷新一次, 刚看过的 30 条又原样回来": <see cref="Helpers.VideoList.AppendDistinct"/>
///   只管当前列表, 刷新清空之后就失效了。
///
/// ★★★ 但**实测(2026-11-08, 探针 bd-probe-rcmd)服务端根本不重复**: 旧参数连打 20 次
///   560 条全部唯一、相邻页零重复。所以这个类是**防御性**的 —— 服务端策略随时可能变
///   (滚动池、风控降级、A/B 分组都会改变去重行为), 有它在, 那一天不会变成"刷新等于重播"。
///   别把它写成"修复某个正在发生的重复 bug", 那是错的。
///
/// ★ 为什么只放内存、不落盘: 它是"这一轮浏览"的短期记忆, 重启之后推荐池本身也已经换了一轮;
///   落盘反而会让"昨天看过的东西"今天永远消失 —— 那不是去重, 是内容枯竭。
///   容量上限 800 条(约 27 页), 超出按 FIFO 淘汰最旧的。
///
/// ★ **只允许在 UI 线程调用**: 和 DynamicSeenStore 一样, 内部没有锁。
///   目前所有调用点都在 HomeViewModel 的 Dispatcher 回调里, 是安全的。
/// </summary>
public static class RecommendSeenStore
{
    /// <summary>最多记多少条(超出淘汰最早记的)。</summary>
    private const int MaxEntries = 800;

    private static readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>淘汰顺序(FIFO)。HashSet 本身不可枚举顺序稳定, 所以另存一份队列。</summary>
    private static readonly Queue<string> _order = new();

    /// <summary>是否已经展示过这个稿件。</summary>
    public static bool Contains(string bvid)
        => !string.IsNullOrEmpty(bvid) && _seen.Contains(bvid);

    /// <summary>记下一个已展示的稿件(重复调用无副作用)。</summary>
    public static void Mark(string bvid)
    {
        if (string.IsNullOrEmpty(bvid)) return;
        if (!_seen.Add(bvid)) return;

        _order.Enqueue(bvid);
        while (_order.Count > MaxEntries)
        {
            var old = _order.Dequeue();
            _seen.Remove(old);
        }
    }

    /// <summary>批量记下(顺序无要求)。</summary>
    public static void MarkRange(IEnumerable<string> bvids)
    {
        foreach (var b in bvids) Mark(b);
    }

    /// <summary>
    /// 清空。登录/退出登录时调用 —— 换了账号, "看过什么"这件事不该继承过来;
    /// 而且换账号后推荐池本身就是另一批内容, 留着旧记录只会误杀。
    /// </summary>
    public static void Clear()
    {
        _seen.Clear();
        _order.Clear();
    }

    /// <summary>当前记了多少条(诊断/测试用)。</summary>
    public static int Count => _seen.Count;
}
