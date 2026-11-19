using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 播放器清晰度菜单的列表规则(纯函数, 可单测)。
///
/// ★ 为什么需要"合并"而不是直接替换(2026-11-20 用户报"切到 720P 后 1080P 不见了"):
///   ≤720P 为了 seek 可靠走 durl 合流单流(fnval=1), 而 durl 封顶 720P, 它的
///   accept_quality 只有 [720P, 360P] 这类低档。切一次清晰度就把整份菜单换成这份
///   低档列表, 1080P/4K 会直接消失, 用户再也切不回去。
///   ⇒ 菜单只增不减: 同一视频里已经拿到过的档位一律保留, 新取流返回的档位补充进去。
/// ★ 换片时播放器会清空旧列表(见 PlayerWindow.ResetForNewMedia), 所以这里不需要
///   关心"上一条视频的档位串到下一条"。
/// </summary>
public static class QualityMenuPolicy
{
    /// <summary>
    /// 合并两份清晰度列表: 按 qn 去重, 保留旧档位, 补入新档位, 从高到低排序。
    ///
    /// <para><paramref name="incoming"/> 里同一个 qn 的显示名优先(它来自最新一次取流,
    /// 文案若跟随接口变化能及时更新)。</para>
    /// <para>非法 qn(&lt;= 0)与播放器渲染不了的档位(杜比视界/HDR/8K)在这里统一挡掉,
    /// 保证任何调用方都不会把"点了必然翻车"的选项塞进菜单。</para>
    /// </summary>
    public static List<(int qn, string label)> Merge(
        IReadOnlyList<(int qn, string label)> current,
        IReadOnlyList<(int qn, string label)> incoming)
    {
        var byQn = new Dictionary<int, string>();
        foreach (var (qn, label) in current)
            Add(byQn, qn, label);
        foreach (var (qn, label) in incoming)
            Add(byQn, qn, label);

        var merged = new List<(int qn, string label)>(byQn.Count);
        foreach (var (qn, label) in byQn)
            merged.Add((qn, label));
        merged.Sort((a, b) => b.qn.CompareTo(a.qn));
        return merged;
    }

    private static void Add(Dictionary<int, string> byQn, int qn, string label)
    {
        if (qn <= 0 || QualityPreference.IsUnsupportedTier(qn)) return;
        byQn[qn] = label;
    }
}
