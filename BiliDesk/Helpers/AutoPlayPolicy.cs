using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 自动连播的**纯判定逻辑**(不依赖 WPF, 可直接单测)。
///
/// ★ 与 DescFoldPolicy / PopupToggle / PlayerWindowPlacement 同款拆法:
///   判定是纯函数, 真正的换片动作(停流 / 换 bvid / 重建队列)留在
///   PlayerWindow.Autoplay.cs 里 —— 那里才有 MediaPlayer 与真实输入时序。
///
/// ★ 为什么"会话是否继续"要单独判: 自动连播的播放队列是**打开视频时固定下来的**,
///   一旦用户手动点了别的视频, 那个队列就不再成立(继续按旧队列播会跳到跟用户操作
///   完全无关的东西上)。判据只有两个合法来源:
///     · 还在会话起点(刚打开、队列还没走第一步);
///     · 正处在"自动切过去"的那一条(切过去之后当前 bvid 就等于预期目标)。
///   其余一律视为用户接管 ⇒ 作废重开。
/// </summary>
public static class AutoPlayPolicy
{
    /// <summary>
    /// 播完之后等几秒再自动换片。
    ///
    /// ★ 必须有这段缓冲: 自动换片是"抢注意力"的行为, 用户可能正走开、也可能想看片尾。
    ///   5 秒是 B 站网页版"即将播放"卡片的量级 —— 够看清下一条标题, 又不至于让人等。
    /// </summary>
    public const int CountdownSeconds = 5;

    /// <summary>
    /// 这次换片是不是"自动连播自己接上的"(会话继续), 而不是用户手动换的。
    /// </summary>
    /// <param name="originBvid">会话起点视频(打开播放器时那一条)</param>
    /// <param name="expectedBvid">正在自动切过去的目标(空 = 还没自动切过)</param>
    /// <param name="currentBvid">当前正在加载/播放的 bvid</param>
    public static bool ContinuesSession(string originBvid, string expectedBvid, string currentBvid)
    {
        if (string.IsNullOrEmpty(currentBvid)) return false;
        if (string.Equals(currentBvid, originBvid, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrEmpty(expectedBvid) &&
            string.Equals(currentBvid, expectedBvid, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 两个队列项是不是**同一项**(bvid 相同 **且** cid 相同)。
    ///
    /// ★★ 2026-11-22 分P 连播: 多P 视频的各个 P **bvid 完全相同**, 只凭 bvid 判等会把
    ///   P2 当成 P1 —— 出队时删掉队头(结果播的还是 P1 的下一个, 看起来"原地打转"),
    ///   或者反过来删错位置。cid 才是分P 之间的区分依据(bvid 仍要判, 因为合集/相关视频
    ///   那两种项没有 cid, 全是 0, 只能靠 bvid 区分)。
    /// </summary>
    public static bool SameEntry(string bvidA, long cidA, string bvidB, long cidB)
        => cidA == cidB && string.Equals(bvidA, bvidB, StringComparison.Ordinal);

    /// <summary>
    /// "当前这一项之后还有哪些项"的下标(0 起, 按原顺序)。
    ///
    /// ★ 定位不上当前项(currentIndex &lt; 0)时返回**空**: 与其猜"下一集是哪一集",
    ///   不如不接 —— 接错比不接更让人恼火(合集与分P 共用这条语义)。
    /// </summary>
    public static int[] FollowingIndexes(int count, int currentIndex)
    {
        if (count <= 0 || currentIndex < 0 || currentIndex >= count - 1) return Array.Empty<int>();

        var result = new int[count - 1 - currentIndex];
        for (var i = 0; i < result.Length; i++) result[i] = currentIndex + 1 + i;
        return result;
    }

    /// <summary>倒计时递减一秒(不会变成负数 —— 归零后由调用方停表并换片)。</summary>
    public static int NextCountdown(int remaining) => remaining > 0 ? remaining - 1 : 0;

    /// <summary>倒计时是否已经走完(该切下一条了)。</summary>
    public static bool ShouldAdvance(int remaining) => remaining <= 0;
}
