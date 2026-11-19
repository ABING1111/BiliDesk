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

    /// <summary>倒计时递减一秒(不会变成负数 —— 归零后由调用方停表并换片)。</summary>
    public static int NextCountdown(int remaining) => remaining > 0 ? remaining - 1 : 0;

    /// <summary>倒计时是否已经走完(该切下一条了)。</summary>
    public static bool ShouldAdvance(int remaining) => remaining <= 0;
}
